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

    // ── Relative-inline positioning (FrameRendererOptions.RelativeInline) ──────────────────────
    // Moves become a CUU/CUD row delta + column-absolute CHA rather than absolute CUP; the region
    // floats relative to the cursor's physical position (parked at the region bottom-left each frame).

    private static FrameRenderer RelativeInline(int rowOffset = 0) =>
        new(new FrameRendererOptions(Inline: true, RelativeInline: true)) { RowOffset = rowOffset };

    [Fact]
    public void RelativeInline_FullRedraw_ClimbsFromRegionBottomAndErasesRegion()
    {
        var r = RelativeInline(rowOffset: 5);
        var buffer = new CellBuffer(5, 2);
        FillRow(buffer, 0, "AAAAA");

        var output = Render(r, buffer);

        // Climb from the parked region bottom (row H-1 = 1) to the top: CUU(1) + CHA(col 0), then ED 0.
        // The absolute CUP an offset-5 renderer would emit (CSI 6;1H) never appears, and never ED 2.
        Assert.Contains("\x1b[1A\x1b[1G\x1b[0J", output);
        Assert.DoesNotContain("\x1b[6;1H", output);
        Assert.DoesNotContain("\x1b[2J", output);
    }

    [Fact]
    public void RelativeInline_EmissionIsIndependentOfRowOffset()
    {
        // The clear-survival proof at the renderer level: relative moves encode no absolute row, so
        // the SAME buffer emits byte-identical output at any RowOffset. Wherever an unobserved clear
        // has shifted the region, the next frame's relative moves land it at the region's new top.
        var buffer = new CellBuffer(6, 3);
        FillRow(buffer, 0, "AAAAA");
        FillRow(buffer, 2, "CCCCC");

        var atThree = Render(RelativeInline(rowOffset: 3), buffer);
        var atNine = Render(RelativeInline(rowOffset: 9), buffer);

        Assert.Equal(atThree, atNine);
        Assert.DoesNotContain("H", atThree); // no CUP at all (content is A/C — no literal 'H')
    }

    [Fact]
    public void RelativeInline_ParksCursorAtRegionBottomAtFrameEnd()
    {
        var r = RelativeInline();
        var buffer = new CellBuffer(5, 3) { CursorVisible = true, CursorRow = 0, CursorColumn = 0 };
        FillRow(buffer, 0, "AAAAA");

        var output = Render(r, buffer);

        // EmitCursor left the caret at row 0; the frame ends by parking at the region bottom
        // (row H-1 = 2, col 0): CUD(2) + CHA(col 0).
        Assert.Contains("\x1b[2B\x1b[1G", output);
    }

    [Fact]
    public void RelativeInline_IncrementalFrame_UsesRelativeMovesOnly()
    {
        var r = RelativeInline();
        var buffer = new CellBuffer(5, 3);
        FillRow(buffer, 0, "XXXXX");
        Render(r, buffer); // full frame; parks at the region bottom (row 2)

        // Change one cell; the incremental move to it is relative from the parked bottom — no CUP.
        buffer.Set(0, 2, "X", CellStyle.Default);
        var output = Render(r, buffer);

        Assert.False(r.NeedsFullRedraw);    // a steady incremental frame
        Assert.DoesNotContain("H", output); // no absolute CUP (content is 'X' — no literal 'H')
        Assert.Contains("X", output);
    }
}
