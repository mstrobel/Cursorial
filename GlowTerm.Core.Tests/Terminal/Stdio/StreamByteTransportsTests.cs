using System.Buffers;
using System.IO.Pipelines;
using GlowTerm.Core.Terminal.Stdio;

namespace GlowTerm.Core.Tests.Terminal.Stdio;

/// <summary>
/// Tests for the generic <see cref="Stream"/> → <see cref="IInputByteSource"/> /
/// <see cref="IOutputByteSink"/> adapters that back the platform stdio transports. The
/// platform-specific code (POSIX <c>stty</c>, Windows <c>SetConsoleMode</c>) modifies
/// process-global terminal state and is verified via manual integration testing instead of
/// unit tests.
/// </summary>
public class StreamByteTransportsTests
{
    // Both StreamInputByteSource and StreamOutputByteSink are internal — InternalsVisibleTo
    // exposes them to the test assembly.

    [Fact]
    public async Task StreamInputByteSource_ReadsBytesFromUnderlyingStream()
    {
        var bytes = new byte[] { 0x68, 0x69 }; // "hi"
        var stream = new MemoryStream(bytes);

        await using var source = new StreamInputByteSource(stream);

        var result = await source.Reader.ReadAsync();
        var read = BuffersExtensions.ToArray(result.Buffer);
        Assert.Equal(bytes, read);
        source.Reader.AdvanceTo(result.Buffer.End);
    }

    [Fact]
    public async Task StreamInputByteSource_DisposalIsIdempotent()
    {
        await using var source = new StreamInputByteSource(new MemoryStream());
        await source.DisposeAsync();
        await source.DisposeAsync(); // should not throw
    }

    [Fact]
    public async Task StreamInputByteSource_LeavesUnderlyingStreamOpen()
    {
        // Process stdin shouldn't be closed when the source is disposed — verify the
        // adapter respects leaveOpen: true.
        var stream = new MemoryStream(new byte[] { 0x41 });
        var source = new StreamInputByteSource(stream);
        await source.DisposeAsync();

        // The stream should still be readable.
        Assert.True(stream.CanRead, "Underlying stream should remain open after source disposal.");
    }

    [Fact]
    public async Task StreamOutputByteSink_WritesBytesToUnderlyingStream()
    {
        var stream = new MemoryStream();
        await using var sink = new StreamOutputByteSink(stream);

        var memory = sink.Writer.GetMemory(2);
        memory.Span[0] = 0x68;
        memory.Span[1] = 0x69;
        sink.Writer.Advance(2);
        await sink.Writer.FlushAsync();

        Assert.Equal(new byte[] { 0x68, 0x69 }, stream.ToArray());
    }

    [Fact]
    public async Task StreamOutputByteSink_DisposalFlushesPendingWrites()
    {
        var stream = new MemoryStream();
        var sink = new StreamOutputByteSink(stream);

        var memory = sink.Writer.GetMemory(1);
        memory.Span[0] = 0x42;
        sink.Writer.Advance(1);
        // Don't manually flush — disposal should do it.

        await sink.DisposeAsync();
        Assert.Equal(new byte[] { 0x42 }, stream.ToArray());
    }

    [Fact]
    public async Task StreamOutputByteSink_DisposalIsIdempotent()
    {
        var sink = new StreamOutputByteSink(new MemoryStream());
        await sink.DisposeAsync();
        await sink.DisposeAsync(); // should not throw
    }

    [Fact]
    public async Task StreamOutputByteSink_LeavesUnderlyingStreamOpen()
    {
        var stream = new MemoryStream();
        var sink = new StreamOutputByteSink(stream);
        await sink.DisposeAsync();

        Assert.True(stream.CanWrite, "Underlying stream should remain open after sink disposal.");
    }
}
