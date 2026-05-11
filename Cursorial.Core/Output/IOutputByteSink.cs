using System.IO.Pipelines;

namespace Cursorial.Core.Output;

/// <summary>
/// Abstraction over the destination of bytes written to the terminal — the output counterpart
/// to <see cref="Cursorial.Core.Input.IInputByteSource"/>. Used by anything that emits VT
/// sequences: capability negotiation, rendering, cursor manipulation, OSC requests.
/// </summary>
/// <remarks>
/// <para>
/// As with <see cref="Cursorial.Core.Input.IInputByteSource"/>, not every output channel goes
/// through a byte stream. Win32 console output can take structured paths (e.g.
/// <c>WriteConsoleOutput</c>) that bypass VT entirely. When a writer IS built on a byte sink,
/// using this abstraction lets the same writer target a real terminal, an in-memory buffer
/// for tests, or a captured trace.
/// </para>
/// <para>
/// Consumers MUST NOT call <see cref="PipeWriter.Complete(Exception?)"/> on
/// <see cref="Writer"/> directly; completion of the underlying transport is the sink's
/// responsibility and happens via <see cref="IAsyncDisposable.DisposeAsync"/>. Calling
/// <c>FlushAsync</c> is expected and required when bytes must reach the terminal before
/// further work proceeds (notably during capability negotiation, where the application waits
/// for a response before continuing).
/// </para>
/// </remarks>
public interface IOutputByteSink : IAsyncDisposable
{
    /// <summary>
    /// Writer over the underlying byte transport. Consumers use the standard
    /// <see cref="PipeWriter"/> protocol: get memory, advance, flush.
    /// </summary>
    PipeWriter Writer { get; }
}
