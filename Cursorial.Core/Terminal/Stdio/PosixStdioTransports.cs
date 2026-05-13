using System.Diagnostics;
using System.Runtime.InteropServices;

using Cursorial.Input;
using Cursorial.Output;

using Microsoft.Win32.SafeHandles;

namespace Cursorial.Terminal.Stdio;

/// <summary>
/// POSIX implementation of <see cref="IStdioTransports"/>. Uses the <c>stty</c> subprocess to
/// save and apply terminal-mode state.
/// </summary>
/// <remarks>
/// <para>
/// <b>Critical: do not call <see cref="Console.OpenStandardInput()"/> /
/// <see cref="Console.OpenStandardOutput()"/>.</b> The .NET Console subsystem on Unix manages
/// its own termios state — it ensures Ctrl+C generates SIGINT, that the cursor is visible
/// at exit, etc. — and accessing those streams silently mutates termios. Our <c>stty raw -echo</c>
/// gets reverted. We wrap fd 0 / fd 1 as <see cref="FileStream"/> over a non-owning
/// <see cref="SafeFileHandle"/> instead, which goes through generic file I/O and doesn't
/// touch Console internals.
/// </para>
/// <para>
/// <b>Also critical: do not redirect any stream when applying stty mode changes.</b> Even
/// just redirecting stderr (which one might do defensively) prevents the change from taking
/// effect — stty exits 0 but the termios bits read back as the prior state. For the apply
/// path, redirect nothing; for the capture path (<c>stty -g</c>), only redirect what you
/// need to read.
/// </para>
/// </remarks>
internal sealed partial class PosixStdioTransports : IStdioTransports
{
    // tcflush(fd, queue_selector). The queue_selector value for "discard input queue" (TCIFLUSH)
    // is defined differently per platform: Linux uses 0, macOS uses 1. We need both because the
    // .NET runtime supports both at runtime.
    private const int LinuxTciFlush = 0;
    private const int MacosTciFlush = 1;

    private readonly string _savedSttyState;
    private readonly IInputByteSource _source;
    private readonly StreamOutputByteSink _sink;
    private int _terminalRestored;
    private int _disposed;

    private PosixStdioTransports(
        string savedSttyState,
        IInputByteSource source,
        StreamOutputByteSink sink)
    {
        _savedSttyState = savedSttyState;
        _source = source;
        _sink = sink;
    }

    public IInputByteSource Source => _source;
    public IOutputByteSink Sink => _sink;

    public static PosixStdioTransports Open()
    {
        // 1. Capture the current terminal state.
        string? savedState = CaptureSttyState();

        if (savedState is null)
        {
            throw new InvalidOperationException(
                "Failed to read terminal state via `stty -g`. Standard input is likely not a terminal " +
                "(running under a pipe or in CI). Use the BYO TerminalSession.OpenAsync(source, sink) overload " +
                "for non-TTY scenarios.");
        }

        // 2. Apply raw mode — NO redirection here (see remarks on the class).
        ApplySttyMode("-icanon -echo -isig -iexten -ixon -opost min 1 time 0");

        try
        {
            // Output: wrap fd 1 via FileStream(SafeFileHandle) — see remarks on the class for
            // why we do NOT use Console.OpenStandardOutput. ownsHandle: false because fd 1 is
            // process-global; we mustn't close it.
            var stdoutHandle = new SafeFileHandle(1, ownsHandle: false);
            var stdoutStream = new FileStream(stdoutHandle, FileAccess.Write);
            var sink = new StreamOutputByteSink(stdoutStream);

            // Input: poll(2)-based source with self-pipe wakeup. We deliberately do NOT wrap
            // fd 0 in a FileStream/PipeReader.Create chain for input — on POSIX that combo
            // leaves a zombie read(2) blocked in the kernel after disposal, which silently
            // eats the next byte the user types at any shell prompt that follows. See the
            // class remarks on PosixPollInputByteSource.
            var source = new PosixPollInputByteSource(fd: 0);

            return new PosixStdioTransports(savedState, source, sink);
        }
        catch
        {
            // @formatter:off
            // Best-effort revert if anything went wrong after raw mode was applied.
            try { ApplySttyMode(savedState); } catch { /* best-effort */ }
            throw;
            // @formatter:on
        }
    }

    public void RestoreTerminalState()
    {
        // Idempotent — guarded so signal-handler invocations don't run multiple times.
        if (Interlocked.Exchange(ref _terminalRestored, 1) != 0) return;

        // Discard any bytes still sitting in the TTY's input queue BEFORE restoring cooked
        // mode. Without this, trailing protocol reports the terminal emitted in response to
        // our opt-in-disable sequences (most visibly a Kitty key-release for whatever key
        // exited the application) can survive across the raw→cooked transition and get
        // delivered to the next cooked-mode `Console.ReadLine` as part of the user's next
        // typed line. tcflush is a no-op when the queue is already empty.
        FlushInputQueue();

        // @formatter:off
        try { ApplySttyMode(_savedSttyState); }
        catch { /* best-effort — terminal may have detached */ }
        // @formatter:on
    }

    private static void FlushInputQueue()
    {
        int selector = OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()
                           ? MacosTciFlush
                           : LinuxTciFlush;

        // @formatter:off
        try { TcFlush(0, selector); } catch { /* best-effort */ }
        // @formatter:on
    }

    // ReSharper disable UnusedMethodReturnValue.Local

    /// <summary><c>tcflush(int fd, int queue_selector)</c> — discard pending TTY data.</summary>
    [LibraryImport("libc", EntryPoint = "tcflush", SetLastError = true)]
    private static partial int TcFlush(int fd, int queueSelector);

    // ReSharper restore UnusedMethodReturnValue.Local

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Restore terminal state BEFORE closing transports. The sync method covers the
        // critical termios restore; safe to call here whether or not RestoreTerminalState
        // was already triggered by a signal handler.
        RestoreTerminalState();

        // @formatter:off
        try { await _sink.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        try { await _source.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        // @formatter:on
    }

    /// <summary>
    /// Capture stty state via <c>stty -g</c>. Stdout must be redirected (we need to read it);
    /// stderr can be inherited.
    /// </summary>
    private static string? CaptureSttyState()
    {
        try
        {
            var psi = new ProcessStartInfo
                      {
                          FileName = "stty",
                          Arguments = "-g",
                          UseShellExecute = false,
                          RedirectStandardOutput = true,
                          RedirectStandardError = false,
                          RedirectStandardInput = false,
                      };

            using var process = Process.Start(psi);

            if (process is null)
                return null;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Apply a stty mode change. NO redirection — see remarks on the class. We swallow errors
    /// here (the alternative on apply failure is to leave the user with a half-modified
    /// terminal, which is worse than carrying on with whatever state stty managed to set).
    /// </summary>
    private static void ApplySttyMode(string arguments)
    {
        var psi = new ProcessStartInfo
                  {
                      FileName = "stty",
                      Arguments = arguments,
                      UseShellExecute = false,
                      RedirectStandardOutput = false,
                      RedirectStandardError = false,
                      RedirectStandardInput = false,
                  };

        using var process = Process.Start(psi);
        process?.WaitForExit(3000);
    }
}