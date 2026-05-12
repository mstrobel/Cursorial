using System.IO.Pipelines;
using System.Runtime.InteropServices;
using Cursorial.Core.Input;

namespace Cursorial.Core.Terminal.Stdio;

/// <summary>
/// POSIX <see cref="IInputByteSource"/> that pumps bytes from a file descriptor into a
/// <see cref="Pipe"/> using <c>poll(2)</c> with a self-pipe wakeup. The self-pipe trick is the
/// canonical POSIX mechanism for cancelling a blocked <c>read(2)</c> — disposal writes one byte
/// to the pipe's write end, which causes the blocked <c>poll(2)</c> on the read end to return
/// immediately, the pump notices and exits cleanly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The natural alternative — wrapping the FD in a <see cref="FileStream"/>
/// and using <c>PipeReader.Create(stream)</c> — has a fatal flaw on POSIX: a pending
/// <c>read(2)</c> sits in the kernel and ignores .NET cancellation tokens. After the input
/// device is disposed, the pending read continues to wait. Whatever byte the user next types
/// gets consumed by that zombie <c>read(2)</c> and dropped on the floor — the symptom is
/// "the first keystroke after a TUI exits is swallowed." With the self-pipe pattern there is
/// no zombie read because the pump never has a pending <c>read</c> at disposal time; it has a
/// pending <c>poll</c>, which we can wake instantly.
/// </para>
/// <para>
/// <b>Ownership.</b> The supplied file descriptor is not closed at disposal — fd 0 (or whatever
/// the caller passes) is process-global and we mustn't close it. The self-pipe's two FDs ARE
/// owned and closed at disposal.
/// </para>
/// </remarks>
internal sealed partial class PosixPollInputByteSource : IInputByteSource
{
    private const short POLLIN = 0x0001;
    private const int EINTR = 4; // Same on Linux and macOS — a defensive check for syscall restart.
    private const int ReadBufferSize = 4096;

    private readonly int _fd;
    private readonly int _wakeupRead;
    private readonly int _wakeupWrite;
    private readonly Pipe _pipe;
    private readonly Task _pumpTask;
    private int _disposed;

    public PosixPollInputByteSource(int fd)
    {
        _fd = fd;

        // Create the self-pipe used to wake poll() on dispose.
        Span<int> ends = stackalloc int[2];
        int rc;
        unsafe
        {
            fixed (int* p = ends)
            {
                rc = pipe(p);
            }
        }
        if (rc != 0)
        {
            throw new InvalidOperationException(
                $"pipe() failed creating self-pipe (errno {Marshal.GetLastWin32Error()}).");
        }
        _wakeupRead = ends[0];
        _wakeupWrite = ends[1];

        _pipe = new Pipe();
        _pumpTask = Task.Run(PumpAsync);
    }

    /// <inheritdoc/>
    public PipeReader Reader => _pipe.Reader;

    private async Task PumpAsync()
    {
        Exception? completion = null;
        try
        {
            var pollFds = new PollFd[2];
            pollFds[0] = new PollFd { Fd = _fd, Events = POLLIN };
            pollFds[1] = new PollFd { Fd = _wakeupRead, Events = POLLIN };

            while (true)
            {
                pollFds[0].Revents = 0;
                pollFds[1].Revents = 0;

                int result;
                unsafe
                {
                    fixed (PollFd* p = pollFds)
                    {
                        result = poll(p, 2, -1);
                    }
                }

                if (result < 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == EINTR) continue;
                    completion = new IOException($"poll() failed (errno {err}).");
                    break;
                }

                // Wakeup pipe — disposal requested. Drain it (so a subsequent open of a new
                // source doesn't see stale wakeup bytes; defensive — we close it momentarily
                // anyway) and exit.
                if ((pollFds[1].Revents & POLLIN) != 0)
                {
                    DrainWakeup();
                    break;
                }

                if ((pollFds[0].Revents & POLLIN) != 0)
                {
                    var memory = _pipe.Writer.GetMemory(ReadBufferSize);
                    nint readResult;
                    unsafe
                    {
                        fixed (byte* buf = memory.Span)
                        {
                            readResult = read(_fd, buf, (nuint)memory.Length);
                        }
                    }

                    if (readResult < 0)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err == EINTR) continue;
                        completion = new IOException($"read() failed on fd {_fd} (errno {err}).");
                        break;
                    }
                    if (readResult == 0)
                    {
                        // EOF — terminal closed.
                        break;
                    }

                    _pipe.Writer.Advance((int)readResult);
                    var flush = await _pipe.Writer.FlushAsync().ConfigureAwait(false);
                    if (flush.IsCompleted || flush.IsCanceled) break;
                }
            }
        }
        catch (Exception ex)
        {
            completion = ex;
        }
        finally
        {
            try { await _pipe.Writer.CompleteAsync(completion).ConfigureAwait(false); }
            catch { /* best-effort */ }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Wake the pump out of its blocked poll() by writing one byte to the self-pipe.
        WriteWakeup();

        try { await _pumpTask.ConfigureAwait(false); }
        catch { /* best-effort */ }

        // Close the self-pipe FDs (we own these). Do NOT close _fd — it's process-global.
        close(_wakeupRead);
        close(_wakeupWrite);
    }

    private void WriteWakeup()
    {
        byte sentinel = 0;
        unsafe
        {
            // Single byte; on a brand-new pipe this never blocks. Ignore errno — if write
            // fails the pump is already gone or the FDs are closed, both fine.
            write(_wakeupWrite, &sentinel, 1);
        }
    }

    private void DrainWakeup()
    {
        Span<byte> buf = stackalloc byte[16];
        unsafe
        {
            fixed (byte* p = buf)
            {
                // Read until empty; we set O_NONBLOCK on the wakeup-read end so this returns
                // EAGAIN once drained. Actually we didn't — but disposal only writes one byte,
                // so a single 1-byte read is sufficient in practice. A loop is defensive in case
                // we ever choose to wake up more aggressively.
                read(_wakeupRead, p, (nuint)buf.Length);
            }
        }
    }

    // ---- P/Invokes ----

    /// <summary><c>pipe(int fds[2])</c> — create an anonymous pipe.</summary>
    [LibraryImport("libc", EntryPoint = "pipe", SetLastError = true)]
    private static unsafe partial int pipe(int* fds);

    /// <summary><c>poll(struct pollfd[], nfds_t, int timeout)</c> — wait for events on a set of FDs.</summary>
    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static unsafe partial int poll(PollFd* fds, uint nfds, int timeoutMs);

    /// <summary><c>read(int fd, void *buf, size_t count)</c> — read up to count bytes.</summary>
    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static unsafe partial nint read(int fd, byte* buf, nuint count);

    /// <summary><c>write(int fd, const void *buf, size_t count)</c> — write up to count bytes.</summary>
    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static unsafe partial nint write(int fd, byte* buf, nuint count);

    /// <summary><c>close(int fd)</c> — close a file descriptor.</summary>
    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int close(int fd);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }
}
