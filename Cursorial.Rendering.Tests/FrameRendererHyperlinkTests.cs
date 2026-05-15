using System.Buffers;
using System.Text;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Rendering;

public class FrameRendererHyperlinkTests
{
    private static string Render(FrameRenderer renderer, CellBuffer back)
    {
        var w = new ArrayBufferWriter<byte>();
        renderer.Render(back, w);
        return Encoding.UTF8.GetString(w.WrittenSpan);
    }

    [Fact]
    public void HyperlinkCell_EmitsOsc8Open()
    {
        var r = new FrameRenderer();
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "a", Style.Default.WithHyperlink("https://example.com"));

        var output = Render(r, buf);

        Assert.Contains("]8;;https://example.com\\", output);
    }

    [Fact]
    public void HyperlinkCell_WithId_EmitsIdInParams()
    {
        var r = new FrameRenderer();
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "a", Style.Default.WithHyperlink("https://example.com", "anchor-1"));

        var output = Render(r, buf);

        Assert.Contains("id=anchor-1", output);
        Assert.Contains("https://example.com", output);
    }

    [Fact]
    public void HyperlinkRun_OpenedOnceAcrossAdjacentMatchingCells()
    {
        var r = new FrameRenderer();
        var buf = new CellBuffer(5, 1);
        var link = Style.Default.WithHyperlink("https://example.com");
        buf.Set(0, 0, "a", link);
        buf.Set(0, 1, "b", link);
        buf.Set(0, 2, "c", link);

        var output = Render(r, buf);

        // Exactly one OSC 8 open (";;<uri>") for the whole run. Counting "8;;" matches both
        // the open ("ESC]8;;<uri>") and the close ("ESC]8;;ST") so we look for the URI body
        // specifically.
        int openCount = 0;
        int idx = 0;
        while ((idx = output.IndexOf(";;https://example.com", idx, StringComparison.Ordinal)) >= 0)
        {
            openCount++;
            idx++;
        }
        Assert.Equal(1, openCount);
    }

    [Fact]
    public void HyperlinkBoundary_EmitsCloseThenOpen()
    {
        var r = new FrameRenderer();
        var buf = new CellBuffer(5, 1);
        buf.Set(0, 0, "a", Style.Default.WithHyperlink("https://a.example"));
        buf.Set(0, 1, "b", Style.Default.WithHyperlink("https://b.example"));

        var output = Render(r, buf);

        int firstOpen = output.IndexOf("https://a.example", StringComparison.Ordinal);
        int close = output.IndexOf("]8;;\x1b\\", StringComparison.Ordinal);
        int secondOpen = output.IndexOf("https://b.example", StringComparison.Ordinal);

        Assert.True(firstOpen >= 0);
        Assert.True(close > firstOpen, "Close must follow the first open.");
        Assert.True(secondOpen > close, "Second open must follow the close.");
    }

    [Fact]
    public void HyperlinkExit_EmitsCloseWhenLeavingLinkedRun()
    {
        var r = new FrameRenderer();
        var buf = new CellBuffer(5, 1);
        buf.Set(0, 0, "a", Style.Default.WithHyperlink("https://example.com"));
        buf.Set(0, 1, "b", Style.Default); // no hyperlink

        var output = Render(r, buf);

        int openIdx = output.IndexOf("https://example.com", StringComparison.Ordinal);
        int closeIdx = output.IndexOf("]8;;\x1b\\", StringComparison.Ordinal);
        Assert.True(openIdx >= 0);
        Assert.True(closeIdx > openIdx);
    }

    [Fact]
    public void EndOfFrame_ClosesOpenHyperlink()
    {
        // The renderer should not leave a hyperlink open across the frame boundary — otherwise
        // any subsequent prompt or interleaved output would inherit it.
        var r = new FrameRenderer();
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", Style.Default.WithHyperlink("https://example.com"));

        var output = Render(r, buf);

        Assert.Contains("]8;;\x1b\\", output);
    }
}
