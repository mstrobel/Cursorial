using System.Diagnostics;
using GlowTerm.Core.Input;
using GlowTerm.Core.Output;

namespace GlowTerm.Core.Terminal.Stdio;

/// <summary>
/// POSIX implementation of <see cref="IStdioTransports"/>. Uses the <c>stty</c> subprocess to
/// save and apply terminal-mode state — this avoids the per-OS termios struct layout problem
/// (Linux glibc and macOS Darwin layouts differ enough that a single P/Invoke struct cannot
/// serve both) at the cost of two short-lived subprocess spawns at session start and one at
/// dispose. A direct termios P/Invoke implementation is a future optimization.
/// </summary>
internal sealed class PosixStdioTransports : IStdioTransports
{
    private readonly string _savedSttyState;
    private readonly StreamInputByteSource _source;
    private readonly StreamOutputByteSink _sink;
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
        // 1. Capture the current terminal state. Output of `stty -g` is an opaque
        //    platform-specific string that the same `stty` binary understands as input.
        string savedState;
        try
        {
            savedState = ExecuteStty("-g", captureOutput: true)!.Trim();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to read terminal state via `stty -g`. Standard input is likely not a terminal " +
                "(running under a pipe or in CI). Use the BYO TerminalSession.OpenAsync(source, sink) overload " +
                "for non-TTY scenarios.",
                ex);
        }

        // 2. Apply raw mode — disables canonical mode, echo, signal characters, and output
        //    post-processing so the application sees and writes raw bytes verbatim.
        try
        {
            ExecuteStty("raw -echo", captureOutput: false);
        }
        catch
        {
            // Best-effort revert if raw mode failed mid-application.
            try { ExecuteStty(savedState, captureOutput: false); } catch { }
            throw;
        }

        var source = new StreamInputByteSource(Console.OpenStandardInput());
        var sink = new StreamOutputByteSink(Console.OpenStandardOutput());

        return new PosixStdioTransports(savedState, source, sink);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Restore terminal state BEFORE closing transports. Order matters: completing the
        // PipeReader after restoration means the next consumer of stdin sees the original
        // mode, not raw mode.
        try { ExecuteStty(_savedSttyState, captureOutput: false); }
        catch { /* best-effort — terminal may have detached */ }

        try { await _sink.DisposeAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }

        try { await _source.DisposeAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Runs <c>stty</c> with the given arguments. The subprocess inherits this process's
    /// stdin so stty operates on the same controlling terminal we use.
    /// </summary>
    private static string? ExecuteStty(string arguments, bool captureOutput)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "stty",
            Arguments = arguments,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start `stty`.");

        string? output = null;
        if (captureOutput)
        {
            output = process.StandardOutput.ReadToEnd();
        }
        // Drain stderr so the subprocess doesn't block on a full pipe.
        _ = process.StandardError.ReadToEnd();

        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"`stty {arguments}` exited with code {process.ExitCode}.");
        }

        return output;
    }
}
