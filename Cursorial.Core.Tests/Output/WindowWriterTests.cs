using System.Buffers;
using System.Text;
using Cursorial.Output;

namespace Cursorial.Tests.Output;

public class WindowWriterTests
{
    private static string Render(Action<IBufferWriter<byte>> write)
    {
        var w = new ArrayBufferWriter<byte>();
        write(w);
        return Encoding.UTF8.GetString(w.WrittenSpan);
    }

    [Fact]
    public void WriteTitle_EmitsOsc2Sequence()
    {
        var text = Render(w => WindowWriter.WriteTitle(w, "hello"));
        Assert.Equal("]2;hello\\", text);
    }

    [Fact]
    public void WriteIconName_EmitsOsc1Sequence()
    {
        var text = Render(w => WindowWriter.WriteIconName(w, "icon"));
        Assert.Equal("]1;icon\\", text);
    }

    [Fact]
    public void WriteTitleAndIconName_EmitsOsc0Sequence()
    {
        var text = Render(w => WindowWriter.WriteTitleAndIconName(w, "combined"));
        Assert.Equal("]0;combined\\", text);
    }

    [Fact]
    public void WriteTitle_EmptyTitle_StillBracketsTheSequence()
    {
        var text = Render(w => WindowWriter.WriteTitle(w, ""));
        Assert.Equal("]2;\\", text);
    }

    [Fact]
    public void WriteTitle_Utf8Payload()
    {
        // Non-ASCII title — payload encodes as UTF-8 between the OSC and ST.
        var text = Render(w => WindowWriter.WriteTitle(w, "café"));
        Assert.Equal("]2;café\\", text);
    }
}
