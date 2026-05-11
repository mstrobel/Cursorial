using System.IO.Pipelines;

namespace Cursorial.Core.Input;

/// <summary>
/// Abstraction over a stream of raw input bytes. Used by parser-based input devices —
/// typically VT/ANSI sequence parsers — as their underlying source.
/// </summary>
/// <remarks>
/// Not every input device is built on a byte source. Platforms such as the Win32 Console can
/// deliver structured input records that bypass the byte-stream layer entirely. When a device
/// IS built on a byte source, expressing it as <see cref="IInputByteSource"/> means the same
/// parser can run against live stdin, an in-memory buffer for tests, or a recorded trace for
/// offline analysis — directly supporting the goal of the same <see cref="IInputDevice"/>
/// implementations serving both interactive and offline scenarios.
/// </remarks>
public interface IInputByteSource : IAsyncDisposable
{
    /// <summary>
    /// Reader over the underlying byte stream. Consumers use the standard
    /// <see cref="PipeReader"/> protocol: read, examine consumed/examined positions, advance.
    /// </summary>
    /// <remarks>
    /// Consumers MUST NOT call <see cref="PipeReader.Complete(System.Exception?)"/> on this
    /// reader directly. Completion of the underlying transport (and any associated terminal
    /// state restoration on POSIX / Windows) is the source's responsibility and happens via
    /// <see cref="IAsyncDisposable.DisposeAsync"/>. Calling <c>Complete</c> on the reader will
    /// leave the source in an inconsistent state and may strand resources.
    /// </remarks>
    PipeReader Reader { get; }
}
