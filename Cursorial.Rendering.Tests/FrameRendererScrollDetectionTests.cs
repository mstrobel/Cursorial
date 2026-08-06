using System.Buffers;
using System.Text;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fragments;

namespace Cursorial.Tests.Rendering;

public class FrameRendererScrollDetectionTests
{
    private static string Render(FrameRenderer renderer, CellBuffer back)
    {
        var w = new ArrayBufferWriter<byte>();
        renderer.Render(back, w);
        return Encoding.UTF8.GetString(w.WrittenSpan);
    }

    private static void FillRow(CellBuffer buffer, int row, string text)
    {
        for (int c = 0; c < buffer.Columns && c < text.Length; c++)
            buffer.Set(c, row, text[c].ToString(), CellStyle.Default);
    }

    [Fact]
    public void ScrollUp_DetectedAndEmitted()
    {
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 4);

        // First frame: rows A / B / C / D.
        FillRow(buffer, 0, "AAAAA");
        FillRow(buffer, 1, "BBBBB");
        FillRow(buffer, 2, "CCCCC");
        FillRow(buffer, 3, "DDDDD");
        Render(r, buffer);

        // Second frame: rows B / C / D / E (scrolled up by 1, new row at bottom).
        buffer.Clear();
        FillRow(buffer, 0, "BBBBB");
        FillRow(buffer, 1, "CCCCC");
        FillRow(buffer, 2, "DDDDD");
        FillRow(buffer, 3, "EEEEE");

        var output = Render(r, buffer);

        // The renderer must have detected the scroll and emitted SU 1 (CSI 1 S).
        Assert.Contains("\x1b[1S", output);
        // Only the newly-uncovered bottom row gets re-emitted; the SU itself moves the rest.
        Assert.Contains("E", output);
        // The old rows B / C / D don't need to re-paint cell-by-cell after the SU.
        // We can't easily assert their absence by character (the test data uses ASCII letters
        // that may appear elsewhere), but the output should be substantially smaller than a
        // full repaint. A repaint would include "BBBBB" worth of bytes (5 distinct cells).
        Assert.DoesNotContain("BBBBB", output);
    }

    [Fact]
    public void ScrollDown_DetectedAndEmitted()
    {
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 4);

        FillRow(buffer, 0, "AAAAA");
        FillRow(buffer, 1, "BBBBB");
        FillRow(buffer, 2, "CCCCC");
        FillRow(buffer, 3, "DDDDD");
        Render(r, buffer);

        // Scroll down by 1: new row Z at top, old D scrolled off bottom.
        buffer.Clear();
        FillRow(buffer, 0, "ZZZZZ");
        FillRow(buffer, 1, "AAAAA");
        FillRow(buffer, 2, "BBBBB");
        FillRow(buffer, 3, "CCCCC");

        var output = Render(r, buffer);

        Assert.Contains("\x1b[1T", output); // SD 1
        Assert.Contains("Z", output);
        Assert.DoesNotContain("AAAAA", output);
    }

    [Fact]
    public void ScrollByMultipleRows_DetectedAtOnce()
    {
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 6);

        for (int i = 0; i < 6; i++) FillRow(buffer, i, new string((char) ('A' + i), 5));
        Render(r, buffer);

        // Scroll up by 3.
        buffer.Clear();
        FillRow(buffer, 0, "DDDDD");
        FillRow(buffer, 1, "EEEEE");
        FillRow(buffer, 2, "FFFFF");
        FillRow(buffer, 3, "GGGGG");
        FillRow(buffer, 4, "HHHHH");
        FillRow(buffer, 5, "IIIII");

        var output = Render(r, buffer);

        Assert.Contains("\x1b[3S", output);
    }

    [Fact]
    public void NoScrollPattern_NoScrollCommandEmitted()
    {
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 3);

        FillRow(buffer, 0, "AAAAA");
        FillRow(buffer, 1, "BBBBB");
        FillRow(buffer, 2, "CCCCC");
        Render(r, buffer);

        // Change one cell — not a scroll.
        buffer.Set(2, 1, "X", CellStyle.Default);
        var output = Render(r, buffer);

        Assert.DoesNotContain("\x1b[1S", output);
        Assert.DoesNotContain("\x1b[1T", output);
        Assert.Contains("X", output);
    }

    [Fact]
    public void ScrollSkipped_WhenFragmentsRegistered()
    {
        // Fragments are anchored at fixed cells; scrolling cell content out from under them
        // would create visually inconsistent frames. Skip the optimization when any fragment
        // is registered.
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 4);

        FillRow(buffer, 0, "AAAAA");
        FillRow(buffer, 1, "BBBBB");
        FillRow(buffer, 2, "CCCCC");
        FillRow(buffer, 3, "DDDDD");
        buffer.AddFragment(0, 2, new StubFragment());
        Render(r, buffer);

        buffer.Clear();
        FillRow(buffer, 0, "BBBBB");
        FillRow(buffer, 1, "CCCCC");
        FillRow(buffer, 2, "DDDDD");
        FillRow(buffer, 3, "EEEEE");
        buffer.AddFragment(0, 2, new StubFragment());

        var output = Render(r, buffer);

        Assert.DoesNotContain("\x1b[1S", output);
    }

    [Fact]
    public void HorizontalSlideOverIdenticalRows_IsNotMisdetectedAsScroll()
    {
        // A scene sliding HORIZONTALLY over identical, horizontally-patterned rows is not a vertical scroll:
        // every row shifts left, so no row equals a front row shifted up/down. The detector must not fire a
        // false-positive SU/SD (which would emit a scroll command instead of the correct cell diff).
        var r = new FrameRenderer();
        var buffer = new CellBuffer(6, 4);
        for (int row = 0; row < 4; row++) FillRow(buffer, row, "ABABAB");
        Render(r, buffer);

        buffer.Clear();
        for (int row = 0; row < 4; row++) FillRow(buffer, row, "BABABA");   // slid left by 1 — rows still identical
        var output = Render(r, buffer);

        Assert.DoesNotContain("\x1b[1S", output);   // no false scroll-up
        Assert.DoesNotContain("\x1b[1T", output);   // no false scroll-down
    }

    private sealed class StubFragment : IBufferFragment
    {
        public Size GetSize() => new(1, 1);
        public bool IsSupported(OutputCapabilities capabilities) => true;
        public void Emit(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities) {}
    }
}
