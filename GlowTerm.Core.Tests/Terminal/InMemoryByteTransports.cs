using System.Buffers;
using System.IO.Pipelines;
using GlowTerm.Core.Input;
using GlowTerm.Core.Output;

namespace GlowTerm.Core.Tests.Terminal;

/// <summary>
/// Test-only <see cref="IInputByteSource"/> backed by a <see cref="Pipe"/>. Tests pre-populate
/// it with the bytes the simulated terminal would have sent; the negotiator reads them via
/// the standard <see cref="PipeReader"/> protocol.
/// </summary>
internal sealed class InMemoryInputByteSource : IInputByteSource
{
    private readonly Pipe _pipe = new();

    public PipeReader Reader => _pipe.Reader;

    /// <summary>Append <paramref name="bytes"/> to the source's read queue.</summary>
    public void Enqueue(ReadOnlySpan<byte> bytes)
    {
        var span = _pipe.Writer.GetSpan(bytes.Length);
        bytes.CopyTo(span);
        _pipe.Writer.Advance(bytes.Length);
        _pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
    }

    public void Enqueue(string utf8) => Enqueue(System.Text.Encoding.UTF8.GetBytes(utf8));

    /// <summary>Mark the source as fully drained — no more bytes will arrive.</summary>
    public void CompleteWriter() => _pipe.Writer.Complete();

    public ValueTask DisposeAsync()
    {
        _pipe.Writer.Complete();
        _pipe.Reader.Complete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Test-only <see cref="IOutputByteSink"/> backed by a <see cref="Pipe"/>. After exercising
/// code under test, call <see cref="ReadAllWrittenAsync"/> to inspect the bytes that were
/// emitted toward the simulated terminal.
/// </summary>
internal sealed class InMemoryOutputByteSink : IOutputByteSink
{
    private readonly Pipe _pipe = new();

    public PipeWriter Writer => _pipe.Writer;

    /// <summary>
    /// Returns every byte written so far and consumes them from the internal buffer. Safe to
    /// call multiple times during a test to interleave assertions with further activity.
    /// </summary>
    public async Task<byte[]> ReadAllWrittenAsync()
    {
        await Writer.FlushAsync();
        if (!_pipe.Reader.TryRead(out var result))
        {
            return [];
        }

        var buffer = result.Buffer;
        var bytes = BuffersExtensions.ToArray(buffer);
        _pipe.Reader.AdvanceTo(buffer.End);
        return bytes;
    }

    public ValueTask DisposeAsync()
    {
        _pipe.Writer.Complete();
        _pipe.Reader.Complete();
        return ValueTask.CompletedTask;
    }
}
