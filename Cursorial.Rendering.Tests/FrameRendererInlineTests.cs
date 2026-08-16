using System.Buffers;
using System.Text;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Rendering;

/// <summary>
/// Inline rendering (<see cref="FrameRendererOptions.Inline"/> + <see cref="FrameRenderer.RowOffset"/>):
/// the region-scoped full-redraw erase, the row offset on every emitted cursor address, the
/// offset-change reset, and the scroll-detection suppression.
/// </summary>
public class FrameRendererInlineTests
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
    public void InlineFullRedraw_ErasesRegionNotScreen()
    {
        var r = new FrameRenderer(new FrameRendererOptions(Inline: true)) { RowOffset = 5 };
        var buffer = new CellBuffer(5, 2);
        FillRow(buffer, 0, "AAAAA");

        var output = Render(r, buffer);

        // CUP to the region top (row 5 → 1-based 6) followed by ED 0 — never ED 2, never CUP 1;1.
        Assert.Contains("\x1b[6;1H\x1b[0J", output);
        Assert.DoesNotContain("\x1b[2J", output);
        Assert.DoesNotContain("\x1b[1;1H", output);
    }

    [Fact]
    public void RowOffset_OffsetsEveryCursorAddress()
    {
        var r = new FrameRenderer(new FrameRendererOptions(Inline: true)) { RowOffset = 3 };
        var buffer = new CellBuffer(5, 3);
        FillRow(buffer, 0, "AAAAA");
        FillRow(buffer, 2, "CCCCC");

        var output = Render(r, buffer);

        // Buffer rows 0 and 2 land on terminal rows 3 and 5 (1-based 4 and 6).
        Assert.Contains("\x1b[4;1H", output);
        Assert.Contains("\x1b[6;1H", output);
    }

    [Fact]
    public void RowOffset_OffsetsFinalCursorPosition()
    {
        var r = new FrameRenderer(new FrameRendererOptions(Inline: true)) { RowOffset = 4 };
        var buffer = new CellBuffer(10, 2) { CursorVisible = true, CursorColumn = 3, CursorRow = 1 };

        var output = Render(r, buffer);

        // The end-of-frame cursor lands at buffer (row 1, col 3) → terminal (row 5, col 3) → CSI 6;4 H.
        Assert.Contains("\x1b[6;4H", output);
    }

    [Fact]
    public void RowOffsetChange_ForcesFullRegionRepaint()
    {
        var r = new FrameRenderer(new FrameRendererOptions(Inline: true)) { RowOffset = 2 };
        var buffer = new CellBuffer(5, 1);
        FillRow(buffer, 0, "AAAAA");
        Render(r, buffer);

        // Steady frame at the same offset: nothing repaints.
        Assert.False(r.NeedsFullRedraw);

        // The region moved (e.g. growth at the terminal bottom scrolled it up): everything the
        // renderer believed about terminal rows is stale — the next render is a full repaint at
        // the new offset.
        r.RowOffset = 1;
        Assert.True(r.NeedsFullRedraw);

        var output = Render(r, buffer);
        Assert.Contains("\x1b[2;1H\x1b[0J", output);
        Assert.Contains("AAAAA", output);
    }

    [Fact]
    public void Inline_SuppressesScrollDetection()
    {
        var r = new FrameRenderer(new FrameRendererOptions(Inline: true));
        var buffer = new CellBuffer(5, 4);
        FillRow(buffer, 0, "AAAAA");
        FillRow(buffer, 1, "BBBBB");
        FillRow(buffer, 2, "CCCCC");
        FillRow(buffer, 3, "DDDDD");
        Render(r, buffer);

        buffer.Clear();
        FillRow(buffer, 0, "BBBBB");
        FillRow(buffer, 1, "CCCCC");
        FillRow(buffer, 2, "DDDDD");
        FillRow(buffer, 3, "EEEEE");

        var output = Render(r, buffer);

        // SU/SD scroll the WHOLE screen (shell history included) — inline must repaint instead.
        Assert.DoesNotContain("\x1b[1S", output);
        Assert.Contains("BBBBB", output);
    }

    [Fact]
    public void FullScreenDefault_IsUnchanged()
    {
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 2);
        FillRow(buffer, 0, "AAAAA");

        var output = Render(r, buffer);

        Assert.Contains("\x1b[2J", output);
        Assert.Contains("\x1b[1;1H", output);
        Assert.DoesNotContain("\x1b[0J", output);
    }
}
