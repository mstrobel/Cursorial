using Cursorial.Input;
using Cursorial.Output;

namespace Cursorial.Terminal.Stdio;

/// <summary>
/// A platform-specific pair of <see cref="IInputByteSource"/> and <see cref="IOutputByteSink"/>
/// over the process's standard I/O streams. Owns whatever terminal-mode state was applied
/// during open (POSIX termios, Windows console-mode flags); disposal restores the prior state
/// before closing the underlying transports.
/// </summary>
/// <remarks>
/// Returned by <see cref="StdioTransports.Open"/>. Not thread-safe — must be opened and
/// disposed from a single thread, and only one instance per process should be alive at a time
/// (terminal state is process-global).
/// </remarks>
public interface IStdioTransports : IAsyncDisposable
{
    /// <summary>The input byte source — reads from process standard input.</summary>
    IInputByteSource Source { get; }

    /// <summary>The output byte sink — writes to process standard output.</summary>
    IOutputByteSink Sink { get; }

    /// <summary>
    /// Synchronously restore terminal state (POSIX termios or Windows console mode) to what
    /// it was before <see cref="StdioTransports.Open"/> was called. Idempotent — safe to call
    /// multiple times or after <see cref="IAsyncDisposable.DisposeAsync"/>.
    /// </summary>
    /// <remarks>
    /// Intended for signal handlers and other emergency-cleanup paths that need to undo our
    /// terminal modifications immediately, without waiting for the full async disposal of the
    /// underlying transports. The "critical" part of disposal (restoring termios / console
    /// mode) runs here synchronously; the async tail (closing the byte streams) still runs
    /// only via <see cref="IAsyncDisposable.DisposeAsync"/>.
    /// </remarks>
    void RestoreTerminalState();

    /// <summary>
    /// Synchronously emit raw bytes to the standard-output handle via a direct platform syscall
    /// (POSIX <c>write(2)</c>, Windows <c>WriteFile</c>), bypassing the async <see cref="Sink"/>
    /// chain. Intended for signal-handler / process-exit paths where the async pipeline may be
    /// stuck or where ordering against <see cref="RestoreTerminalState"/> matters (e.g., emitting
    /// VT opt-in disables before reverting to cooked mode, so the disable bytes don't echo).
    /// Best-effort: errors are swallowed.
    /// </summary>
    void WriteBytesSync(ReadOnlySpan<byte> bytes);
}