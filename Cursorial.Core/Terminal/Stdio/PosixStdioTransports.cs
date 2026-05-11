using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Cursorial.Core.Input;
using Cursorial.Core.Output;

namespace Cursorial.Core.Terminal.Stdio;

/// <summary>
/// POSIX implementation of <see cref="IStdioTransports"/>. Uses the <c>stty</c> subprocess to
/// save and apply terminal-mode state.
/// </summary>
/// <remarks>
/// <para>
/// <b>Critical: do not call <see cref="Console.OpenStandardInput"/> /
/// <see cref="Console.OpenStandardOutput"/>.</b> The .NET Console subsystem on Unix manages
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
internal sealed class PosixStdioTransports : IStdioTransports
{
    private readonly string _savedSttyState;
    private readonly StreamInputByteSource _source;
    private readonly StreamOutputByteSink _sink;
    private int _terminalRestored;
    private int _disposed;

    private PosixStdioTransports(
        string savedSttyState,
        StreamInputByteSource source,
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
            // Wrap fd 0 / fd 1 via FileStream(SafeFileHandle) — see remarks on the class for
            // why we do NOT use Console.OpenStandardInput / Console.OpenStandardOutput.
            // ownsHandle: false because these descriptors are process-global; we mustn't close them.
            var stdinHandle = new SafeFileHandle((nint)0, ownsHandle: false);
            var stdoutHandle = new SafeFileHandle((nint)1, ownsHandle: false);

            var stdinStream = new FileStream(stdinHandle, FileAccess.Read);
            var stdoutStream = new FileStream(stdoutHandle, FileAccess.Write);

            var source = new StreamInputByteSource(stdinStream);
            var sink = new StreamOutputByteSink(stdoutStream);
            return new PosixStdioTransports(savedState, source, sink);
        }
        catch
        {
            // Best-effort revert if anything went wrong after raw mode was applied.
            try { ApplySttyMode(savedState); } catch { }
            throw;
        }
    }

    public void RestoreTerminalState()
    {
        // Idempotent — guarded so signal-handler invocations don't run multiple times.
        if (Interlocked.Exchange(ref _terminalRestored, 1) != 0) return;

        try { ApplySttyMode(_savedSttyState); }
        catch { /* best-effort — terminal may have detached */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Restore terminal state BEFORE closing transports. The sync method covers the
        // critical termios restore; safe to call here whether or not RestoreTerminalState
        // was already triggered by a signal handler.
        RestoreTerminalState();

        try { await _sink.DisposeAsync().ConfigureAwait(false); } catch { }
        try { await _source.DisposeAsync().ConfigureAwait(false); } catch { }
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
            if (process is null) return null;

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
    /// Apply an stty mode change. NO redirection — see remarks on the class. We swallow errors
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
