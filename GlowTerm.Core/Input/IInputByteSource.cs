using System.IO.Pipelines;

namespace GlowTerm.Core.Input;

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
    PipeReader Reader { get; }
}
