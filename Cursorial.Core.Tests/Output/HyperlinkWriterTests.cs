using System.Buffers;
using System.Text;
using Cursorial.Core.Output;

namespace Cursorial.Core.Tests.Output;

public class HyperlinkWriterTests
{
    private static string Encode(Action<IBufferWriter<byte>> action)
    {
        var w = new ArrayBufferWriter<byte>();
        action(w);
        return Encoding.UTF8.GetString(w.WrittenSpan);
    }

    [Fact]
    public void WriteOpen_NoId_EmptyParamsSlot()
    {
        var s = Encode(w => HyperlinkWriter.WriteOpen(w, "https://example.com".AsSpan()));
        Assert.Equal("\x1b]8;;https://example.com\x1b\\", s);
    }

    [Fact]
    public void WriteOpen_WithId_EmitsIdParam()
    {
        var s = Encode(w => HyperlinkWriter.WriteOpen(w, "https://example.com".AsSpan(), "anchor1".AsSpan()));
        Assert.Equal("\x1b]8;id=anchor1;https://example.com\x1b\\", s);
    }

    [Fact]
    public void WriteClose_EmitsBareCloseSequence()
    {
        var s = Encode(HyperlinkWriter.WriteClose);
        Assert.Equal("\x1b]8;;\x1b\\", s);
    }

    [Fact]
    public void WriteHyperlink_OneShot_OpensTextCloses()
    {
        var s = Encode(w => HyperlinkWriter.WriteHyperlink(
            w, "https://cursorial.dev".AsSpan(), "click here".AsSpan()));
        Assert.Equal("\x1b]8;;https://cursorial.dev\x1b\\click here\x1b]8;;\x1b\\", s);
    }

    [Fact]
    public void Uri_WithMultibyteCharacters_EmittedAsUtf8()
    {
        // The URI contains a CJK character; OSC bodies are UTF-8 per the de facto convention.
        var uri = "https://例え.test";
        var s = Encode(w => HyperlinkWriter.WriteOpen(w, uri.AsSpan()));
        Assert.Equal($"\x1b]8;;{uri}\x1b\\", s);
    }
}
