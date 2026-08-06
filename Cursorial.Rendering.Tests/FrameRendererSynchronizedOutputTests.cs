using System.Buffers;
using System.Text;

using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;

namespace Cursorial.Tests.Rendering;

public class FrameRendererSynchronizedOutputTests
{
    private static OutputCapabilities CapsWithSync(bool synchronizedOutput) =>
        OutputCapabilities.None with
        {
            Protocol = OutputCapabilities.None.Protocol with { SynchronizedOutput = synchronizedOutput },
        };

    [Fact]
    public void Render_WithSyncSupported_BracketsFrameWithBeginEnd()
    {
        var renderer = new FrameRenderer(CapsWithSync(synchronizedOutput: true));
        var buffer = new CellBuffer(4, 1);
        var writer = new ArrayBufferWriter<byte>();

        renderer.Render(buffer, writer);

        var output = Encoding.ASCII.GetString(writer.WrittenSpan);

        // First sequence emitted is the sync-begin; last is the sync-end.
        Assert.StartsWith("\x1b[?2026h", output);
        Assert.EndsWith("\x1b[?2026l", output);

        // Begin appears exactly once and before end.
        int beginIdx = output.IndexOf("\x1b[?2026h", StringComparison.Ordinal);
        int endIdx = output.IndexOf("\x1b[?2026l", StringComparison.Ordinal);
        Assert.True(beginIdx >= 0);
        Assert.True(endIdx > beginIdx);
    }

    [Fact]
    public void Render_WithoutSyncSupport_NoSyncBytesEmitted()
    {
        var renderer = new FrameRenderer(CapsWithSync(synchronizedOutput: false));
        var buffer = new CellBuffer(4, 1);
        var writer = new ArrayBufferWriter<byte>();

        renderer.Render(buffer, writer);

        var output = Encoding.ASCII.GetString(writer.WrittenSpan);
        Assert.DoesNotContain("\x1b[?2026h", output);
        Assert.DoesNotContain("\x1b[?2026l", output);
    }

    [Fact]
    public void Render_WithoutCapabilities_NoSyncBytesEmitted()
    {
        // Constructed without OutputCapabilities — caller didn't pass any, so the renderer can't
        // assume the terminal supports sync output. No bracketing.
        var renderer = new FrameRenderer();
        var buffer = new CellBuffer(4, 1);
        var writer = new ArrayBufferWriter<byte>();

        renderer.Render(buffer, writer);

        var output = Encoding.ASCII.GetString(writer.WrittenSpan);
        Assert.DoesNotContain("\x1b[?2026h", output);
        Assert.DoesNotContain("\x1b[?2026l", output);
    }

    [Fact]
    public void Render_SecondFrameWithSync_StillBrackets()
    {
        // Sync bracketing applies to every frame, not just the first / full-redraw.
        var renderer = new FrameRenderer(CapsWithSync(synchronizedOutput: true));
        var buffer = new CellBuffer(4, 1);

        var firstWriter = new ArrayBufferWriter<byte>();
        renderer.Render(buffer, firstWriter);

        // Mutate the buffer slightly so the second render has work to do.
        buffer.Set(0, 0, "X", CellStyle.Default);

        var secondWriter = new ArrayBufferWriter<byte>();
        renderer.Render(buffer, secondWriter);

        var second = Encoding.ASCII.GetString(secondWriter.WrittenSpan);
        Assert.StartsWith("\x1b[?2026h", second);
        Assert.EndsWith("\x1b[?2026l", second);
    }
}
