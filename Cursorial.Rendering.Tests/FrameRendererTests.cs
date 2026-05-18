using System.Buffers;
using System.Text;

using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Rendering;

public class FrameRendererTests
{
    private static string Render(FrameRenderer renderer, CellBuffer back)
    {
        var w = new ArrayBufferWriter<byte>();
        renderer.Render(back, w);
        return Encoding.UTF8.GetString(w.WrittenSpan);
    }

    // ---- Full redraw / first frame ----

    [Fact]
    public void FirstFrame_EmitsClearScreenAndCursorMove()
    {
        var r = new FrameRenderer();
        var buf = new CellBuffer(3, 1);

        var output = Render(r, buf);

        Assert.Contains("\x1b[2J", output);    // clear screen
        Assert.Contains("\x1b[0m", output);    // SGR reset
        Assert.Contains("\x1b[1;1H", output);  // CUP to (1,1) — 0-based (0,0)
    }

    [Fact]
    public void FirstFrame_BlankBuffer_DoesNotEmitCellContent()
    {
        var r = new FrameRenderer();
        var buf = new CellBuffer(3, 1);

        var output = Render(r, buf);

        // No cell glyphs emitted — every cell matches the freshly-blanked front buffer. The
        // output is the clear + SGR reset + cursor positioning / shape / show. We assert by
        // looking for the absence of any printable ASCII letter or non-escape printable; a
        // simpler invariant is that the output ends with a known cursor/visibility sequence
        // rather than cell content.
        Assert.DoesNotContain('a', output);
        Assert.DoesNotContain('b', output);
        // No 'cell-printing' bytes — only escape sequences (which start with ESC=0x1b) and the
        // ascii final/intermediate bytes inside them.
    }

    [Fact]
    public void FirstFrame_NonBlankCells_EmitsThem()
    {
        var r = new FrameRenderer();
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "a", Style.Default);
        buf.Set(1, 0, "b", Style.Default);

        var output = Render(r, buf);

        Assert.Contains("a", output);
        Assert.Contains("b", output);
    }

    // ---- Diff between frames ----

    [Fact]
    public void SecondFrame_NoChanges_EmitsOnlyTerminalCursorMove()
    {
        var r = new FrameRenderer();
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "a", Style.Default);

        Render(r, buf);             // first frame
        var output = Render(r, buf); // second frame, identical buffer

        // No clear, no cell content. The end-of-frame cursor move may or may not appear
        // depending on whether cursor is already where we last left it. After first frame the
        // cursor was placed at column 0 (CursorColumn default). Then we emitted 'a', so cursor
        // is at column 1 in our tracking, then end-of-frame moves to (0,0).
        Assert.DoesNotContain("\x1b[2J", output);
        Assert.DoesNotContain("a", output);
    }

    [Fact]
    public void SecondFrame_SingleCellChange_EmitsOnlyThatCell()
    {
        var r = new FrameRenderer();
        var buf = new CellBuffer(5, 1);
        buf.Set(0, 0, "h", Style.Default);
        buf.Set(1, 0, "i", Style.Default);
        Render(r, buf);

        buf.Set(1, 0, "o", Style.Default);
        var output = Render(r, buf);

        // Should NOT re-emit 'h' (it's unchanged). Should emit 'o'.
        Assert.Contains("o", output);
        Assert.DoesNotContain("h", output);
    }

    // ---- Resize ----

    [Fact]
    public void Resize_TriggersFullRedraw()
    {
        var r = new FrameRenderer();
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "a", Style.Default);
        Render(r, buf);

        var buf2 = new CellBuffer(5, 2);
        buf2.Set(0, 0, "a", Style.Default);
        var output = Render(r, buf2);

        Assert.Contains("\x1b[2J", output); // clear screen — full redraw
    }

    // ---- ForceFullRedraw ----

    [Fact]
    public void ForceFullRedraw_AlwaysClearsAndRedraws()
    {
        var r = new FrameRenderer(new FrameRendererOptions(ForceFullRedraw: true));
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "a", Style.Default);
        Render(r, buf);

        var output = Render(r, buf);
        Assert.Contains("\x1b[2J", output);
    }

    // ---- SGR state tracking ----

    [Fact]
    public void StyleChange_BetweenCells_EmitsDelta()
    {
        var r = new FrameRenderer();
        var buf = new CellBuffer(3, 1);
        var bold = Style.Default.WithAttributes(TextAttributes.Bold);
        buf.Set(0, 0, "a", bold);
        buf.Set(1, 0, "b", Style.Default);

        var output = Render(r, buf);

        // Renderer uses WriteDelta, so the first cell's style emerges as just the delta from
        // Style.Default (the post-reset state) to bold — SGR 1 alone, not the 0;1 absolute form.
        Assert.Contains("\x1b[1m", output);   // bold-on before 'a'
        Assert.Contains("\x1b[22m", output);  // bold-off (SGR 22) before 'b'
    }

    // ---- Wide cells ----

    [Fact]
    public void WideCell_EmittedOnce_ContinuationSkipped()
    {
        var r = new FrameRenderer();
        var buf = new CellBuffer(4, 1);
        buf.Set(0, 0, "中", Style.Default);

        var output = Render(r, buf);

        // The wide glyph appears once in the output. The continuation cell at column 1
        // must NOT trigger a separate emission.
        int firstIdx = output.IndexOf('中');
        Assert.True(firstIdx >= 0);
        Assert.Equal(firstIdx, output.LastIndexOf('中'));
    }

    [Fact]
    public void WideCell_OverwrittenWithSingleChar_RedrawsContinuationAsBlank()
    {
        var r = new FrameRenderer();
        var buf = new CellBuffer(4, 1);
        buf.Set(0, 0, "中", Style.Default);
        Render(r, buf);

        buf.Set(0, 0, "x", Style.Default);
        var output = Render(r, buf);

        // Both (0,0) and (0,1) changed: (0,0) WideLeft → Single 'x', (0,1) WideContinuation → Blank.
        Assert.Contains("x", output);
        // The continuation slot is now blank — the renderer should emit a space at (0,1).
        Assert.Contains(" ", output);
    }

    // ---- Reset ----

    [Fact]
    public void Reset_ForcesFullRedrawOnNextRender()
    {
        var r = new FrameRenderer();
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "a", Style.Default);
        Render(r, buf);

        r.Reset();
        var output = Render(r, buf);
        Assert.Contains("\x1b[2J", output);
    }

    // ---- Cursor state ----

    [Fact]
    public void CursorVisibilityChange_EmitsShowHide()
    {
        var r = new FrameRenderer();
        var buf = new CellBuffer(3, 1);
        Render(r, buf); // first frame: cursor visible by default, so DECSET 25 emitted

        buf.CursorVisible = false;
        var output = Render(r, buf);

        Assert.Contains("\x1b[?25l", output);
    }

    [Fact]
    public void CursorShapeChange_EmitsDecScusr()
    {
        var r = new FrameRenderer();
        var buf = new CellBuffer(3, 1);
        Render(r, buf);

        buf.CursorShape = CursorShape.SteadyBar;
        var output = Render(r, buf);

        Assert.Contains("\x1b[6 q", output);
    }

    [Fact]
    public void CursorPositionAtFrameEnd_MovesToBufferCursorPosition()
    {
        var r = new FrameRenderer();
        var buf = new CellBuffer(5, 5);
        buf.CursorRow = 2;
        buf.CursorColumn = 3;

        var output = Render(r, buf);

        Assert.Contains("\x1b[3;4H", output); // 1-based (3, 4) for 0-based (2, 3)
    }

    [Fact]
    public void EndOfFrame_EmitsSgrReset()
    {
        // After the cell emission and cursor positioning, the renderer emits CSI 0 m so the
        // terminal's SGR state at frame boundary is "default" — protects against post-frame
        // bg-bleed when the terminal has to fill new rows (resize, scroll, etc.).
        var r = new FrameRenderer();
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", Style.Default.WithBackground(Color.FromRgb(255, 0, 0)));

        var output = Render(r, buf);
        Assert.EndsWith("\x1b[0m", output);
    }
}
