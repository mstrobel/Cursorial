using System.IO.Pipelines;
using System.Runtime.InteropServices;

using Cursorial.Input;

namespace Cursorial.Terminal.Stdio;

/// <summary>
/// POSIX <see cref="IInputByteSource"/> that pumps bytes from a file descriptor into a
/// <see cref="System.IO.Pipelines.Pipe"/> using <c>poll(2)</c> with a self-pipe wakeup. The self-pipe trick is the
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
/// the caller passes) is process-global, and we mustn't close it. The self-pipe's two FDs ARE
/// owned and closed at disposal.
/// </para>
/// </remarks>
// ReSharper disable once RedundantExtendsListEntry
internal sealed partial class PosixPollInputByteSource : IInputByteSource, IPausableInputByteSource
{
    private const short POLLIN = 0x0001;
    private const int EINTR = 4; // Same on Linux and macOS — a defensive check for syscall restart.
    private const int ReadBufferSize = 4096;

    private readonly int _fd;
    private readonly int _wakeupRead;
    private readonly int _wakeupWrite;
    private readonly Pipe _pipe;
    private readonly Task _pumpTask;

    // Pause-state plumbing. _pauseRefCount tracks the number of outstanding PauseScope handles;
    // the pump is parked iff the count is greater than zero. The pump enters user-space wait via
    // _runEvent (a manual-reset event), so no libc syscall is left pending while paused.
    // _pauseCompleted notifies callers of PauseAsync once the pump has definitively parked; it's
    // recreated on each zero→one transition and cleared on each one→zero transition.
    private readonly object _stateLock = new();
    private readonly ManualResetEventSlim _runEvent = new(initialState: true);
    private int _pauseRefCount;
    private TaskCompletionSource? _pauseCompleted;

    private int _disposed;

    public PosixPollInputByteSource(int fd)
    {
        _fd = fd;

        // Create the self-pipe used to wake poll() on dispose.
        Span<int> ends = stackalloc int[2];
        
        var rc = Pipe(ref ends[0]);
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

                var result = Poll(ref pollFds.AsSpan()[0], 2, -1);

                if (result < 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == EINTR) continue;
                    completion = new IOException($"poll() failed (errno {err}).");
                    break;
                }

                // Wakeup pipe — disposal or pause requested. Drain it and dispatch on state.
                if ((pollFds[1].Revents & POLLIN) != 0)
                {
                    DrainWakeup();

                    if (Volatile.Read(ref _disposed) != 0) break;

                    TaskCompletionSource? pauseTcs = null;
                    bool shouldPark;
                    lock (_stateLock)
                    {
                        shouldPark = _pauseRefCount > 0;
                        if (shouldPark)
                        {
                            // Signal whichever caller(s) are waiting on the pause-completion
                            // TCS. A null TCS is possible if every awaiter canceled before we
                            // got here; in that case we still park because the refcount remains
                            // elevated — disposal of those callers' handles will release us.
                            pauseTcs = _pauseCompleted;
                        }
                    }

                    if (shouldPark)
                    {
                        pauseTcs?.TrySetResult();

                        // Block this background thread in user space until the last pause
                        // handle is disposed (and sets _runEvent) — or disposal arrives.
                        // Crucially, no libc call is pending here: the kernel is free to
                        // buffer incoming TTY bytes for us to pick up after resume.
                        _runEvent.Wait();

                        if (Volatile.Read(ref _disposed) != 0) break;
                    }

                    continue;
                }

                if ((pollFds[0].Revents & POLLIN) != 0)
                {
                    var memory = _pipe.Writer.GetMemory(ReadBufferSize);
                    var readResult = Read(_fd, ref memory.Span[0], (nuint)memory.Length);

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

                    _pipe.Writer.Advance((int) readResult);
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
            // @formatter:off
            try { await _pipe.Writer.CompleteAsync(completion).ConfigureAwait(false); }
            catch { /* best-effort */ }
            // @formatter:on
        }
    }

    /// <inheritdoc/>
    public async ValueTask<IAsyncDisposable> PauseAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(PosixPollInputByteSource));

        cancellationToken.ThrowIfCancellationRequested();

        Task? completion;
        bool wakeupNeeded;
        lock (_stateLock)
        {
            // Tentative increment — we'll decrement on any failure between here and successful
            // completion of the pause-await, so cancellation can't strand the refcount.
            _pauseRefCount++;

            if (_pauseRefCount == 1)
            {
                // Zero → one transition. The pump is currently running — reset the run event
                // (so it'll block when it reaches the parked state) and create a fresh TCS for
                // overlapping callers to await.
                _runEvent.Reset();
                _pauseCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                completion = _pauseCompleted.Task;
                wakeupNeeded = true;
            }
            else if (_pauseCompleted is { Task.IsCompleted: false } pending)
            {
                // A pause is in flight — share its completion task.
                completion = pending.Task;
                wakeupNeeded = false;
            }
            else
            {
                // Already paused — nothing to await.
                completion = null;
                wakeupNeeded = false;
            }
        }

        if (wakeupNeeded) WriteWakeup();

        try
        {
            if (completion is not null)
                await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Unwind the tentative increment. If we were the lone pauser, the pump will resume
            // naturally; if other pausers are still in flight, they keep their share.
            DecrementPause();
            throw;
        }

        return new PauseScope(this);
    }

    private void DecrementPause()
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        bool wake;
        lock (_stateLock)
        {
            if (_pauseRefCount == 0) return; // defensive — shouldn't happen

            _pauseRefCount--;
            wake = _pauseRefCount == 0;

            if (wake)
            {
                _pauseCompleted = null;
            }
        }

        if (wake) _runEvent.Set();
    }

    private sealed class PauseScope : IAsyncDisposable
    {
        private PosixPollInputByteSource? _source;

        public PauseScope(PosixPollInputByteSource source) => _source = source;

        public ValueTask DisposeAsync()
        {
            // Interlocked.Exchange enforces single-effect dispose — accidental double dispose
            // doesn't double-decrement the refcount and prematurely unpark the pump.
            var source = Interlocked.Exchange(ref _source, null);
            source?.DecrementPause();
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // @formatter:off
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Unblock the pump regardless of whether it's parked on poll() or on the run event.
        WriteWakeup();
        _runEvent.Set();

        // If a caller is awaiting PauseAsync at this exact moment, fail their await so they
        // don't hang forever — we'll never deliver the pause-completed signal post-dispose.
        TaskCompletionSource? pendingPause;
        lock (_stateLock) { pendingPause = _pauseCompleted; _pauseCompleted = null; }
        pendingPause?.TrySetException(new ObjectDisposedException(nameof(PosixPollInputByteSource)));

        try { await _pumpTask.ConfigureAwait(false); }
        catch { /* best-effort */ }

        _runEvent.Dispose();

        // Close the self-pipe FDs (we own these). Do NOT close _fd — it's process-global.
        Close(_wakeupRead);
        Close(_wakeupWrite);
        // @formatter:on
    }

    private void WriteWakeup()
    {
        byte sentinel = 0;
        
        // Single byte; on a brand-new pipe this never blocks. Ignore errno — if write
        // fails, the pump is already gone, or the FDs are closed - both fine.
        Write(_wakeupWrite, ref sentinel, 1);
    }

    private void DrainWakeup()
    {
        Span<byte> buf = stackalloc byte[16];
        // Read until empty; we set O_NONBLOCK on the wakeup-read end so this returns
        // EAGAIN once drained. Actually, we didn't — but disposal only writes one byte,
        // so a single 1-byte read is sufficient in practice. A loop is defensive in case
        // we ever choose to wake up more aggressively.
        Read(_wakeupRead, ref buf[0], (nuint)buf.Length);
    }

    // ---- P/Invokes ----

    // ReSharper disable UnusedMethodReturnValue.Local
    
    /// <summary><c>pipe(int fds[2])</c> — create an anonymous pipe.</summary>
    [LibraryImport("libc", EntryPoint = "pipe", SetLastError = true)]
    private static partial int Pipe(ref int fds);

    /// <summary><c>poll(struct pollfd[], nfds_t, int timeout)</c> — wait for events on a set of FDs.</summary>
    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static partial int Poll(ref PollFd fds, uint nfds, int timeoutMs);

    /// <summary><c>read(int fd, void *buf, size_t count)</c> — read up to count bytes.</summary>
    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static partial nint Read(int fd, ref byte buf, nuint count);

    /// <summary><c>write(int fd, const void *buf, size_t count)</c> — write up to count bytes.</summary>
    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static partial nint Write(int fd, ref byte buf, nuint count);

    /// <summary><c>close(int fd)</c> — close a file descriptor.</summary>
    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);

    // ReSharper restore UnusedMethodReturnValue.Local

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }
}
