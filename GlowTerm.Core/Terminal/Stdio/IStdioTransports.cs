using GlowTerm.Core.Input;
using GlowTerm.Core.Output;

namespace GlowTerm.Core.Terminal.Stdio;

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
}
