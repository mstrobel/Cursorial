using System.Buffers;
using System.Text;

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Text;

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
        buf.Set(0, 0, "a", CellStyle.Default);
        buf.Set(1, 0, "b", CellStyle.Default);

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
        buf.Set(0, 0, "a", CellStyle.Default);

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
        buf.Set(0, 0, "h", CellStyle.Default);
        buf.Set(1, 0, "i", CellStyle.Default);
        Render(r, buf);

        buf.Set(1, 0, "o", CellStyle.Default);
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
        buf.Set(0, 0, "a", CellStyle.Default);
        Render(r, buf);

        var buf2 = new CellBuffer(5, 2);
        buf2.Set(0, 0, "a", CellStyle.Default);
        var output = Render(r, buf2);

        Assert.Contains("\x1b[2J", output); // clear screen — full redraw
    }

    // ---- ForceFullRedraw ----

    [Fact]
    public void ForceFullRedraw_AlwaysClearsAndRedraws()
    {
        var r = new FrameRenderer(new FrameRendererOptions(ForceFullRedraw: true));
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "a", CellStyle.Default);
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
        var bold = CellStyle.Default.WithAttributes(TextAttributes.Bold);
        buf.Set(0, 0, "a", bold);
        buf.Set(1, 0, "b", CellStyle.Default);

        var output = Render(r, buf);

        // Renderer uses WriteDelta, so the first cell's style emerges as just the delta from
        // Style.Default (the post-reset state) to bold — SGR 1 alone, not the 0;1 absolute form.
        Assert.Contains("\x1b[1m", output);   // bold-on before 'a'
        Assert.Contains("\x1b[22m", output);  // bold-off (SGR 22) before 'b'
    }

    [Fact]
    public void StyleChange_BetweenCells_ReEmitsUnderlineShape()
    {
        var r = new FrameRenderer();
        var buf = new CellBuffer(3, 1);
        var boldSingle = CellStyle.Default
                                  .WithAttributes(TextAttributes.Bold | TextAttributes.Underline)
                                  .WithUnderlineStyle(UnderlineStyle.Single);
        var italicCurly = CellStyle.Default
                                   .WithAttributes(TextAttributes.Italic | TextAttributes.Underline)
                                   .WithUnderlineStyle(UnderlineStyle.Curly);

        buf.Set(0, 0, "a", boldSingle);
        buf.Set(1, 0, "b", italicCurly);

        var output = Render(r, buf);

        // The Underline flag survives the transition, so it appears in neither the added nor the
        // removed set — but the shape it carries changed, so SGR 4:3 still has to go out.
        // SyncStyle assigns `_currentStyle = target` unconditionally, so an under-emitted
        // parameter would leave the renderer's model permanently ahead of the terminal, with no
        // later frame ever correcting it.
        Assert.Contains("4:3", output);
    }

    // ---- Wide cells ----

    [Fact]
    public void WideCell_EmittedOnce_ContinuationSkipped()
    {
        var r = new FrameRenderer();
        var buf = new CellBuffer(4, 1);
        buf.Set(0, 0, "中", CellStyle.Default);

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
        buf.Set(0, 0, "中", CellStyle.Default);
        Render(r, buf);

        buf.Set(0, 0, "x", CellStyle.Default);
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
        buf.Set(0, 0, "a", CellStyle.Default);
        Render(r, buf);

        r.Reset();
        var output = Render(r, buf);
        Assert.Contains("\x1b[2J", output);
    }

    [Fact]
    public void NeedsFullRedraw_TracksResetAndRenderLifecycle()
    {
        // The frame-loop coupling: a loop that skips emission on clean frames consults this to
        // force one while a reset is pending (UIApplication.RequestFullRedraw on an idle app).
        var r = new FrameRenderer();
        Assert.True(r.NeedsFullRedraw); // construction: the first render is always full

        var buf = new CellBuffer(3, 1);
        Render(r, buf);
        Assert.False(r.NeedsFullRedraw); // satisfied by the render

        r.Reset();
        Assert.True(r.NeedsFullRedraw); // re-armed by the reset …

        Render(r, buf);
        Assert.False(r.NeedsFullRedraw); // … and consumed again
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
    public void CursorShape_NotEmittedWhenShapeControlUnsupported()
    {
        // DECSCUSR (CSI Ps SP q) must be suppressed when the negotiated capabilities report no
        // cursor ShapeControl — e.g. Apple Terminal, which mis-parses the space-intermediate
        // form and prints the literal 'q'. Verify across a first frame AND a subsequent shape
        // change that no DECSCUSR (no 'q' final, no ' q' tail) is emitted.
        var caps = OutputCapabilities.None with
                   {
                       Cursor = OutputCapabilities.None.Cursor with { ShapeControl = false },
                   };
        var r = new FrameRenderer(caps);
        var buf = new CellBuffer(3, 1);

        var first = Render(r, buf);                 // first frame would normally force DECSCUSR
        buf.CursorShape = CursorShape.SteadyBar;
        var second = Render(r, buf);                // explicit shape change

        Assert.DoesNotContain(" q", first);
        Assert.DoesNotContain(" q", second);
    }

    [Fact]
    public void CursorShape_EmittedWhenShapeControlSupported()
    {
        var caps = OutputCapabilities.None with
                   {
                       Cursor = OutputCapabilities.None.Cursor with { ShapeControl = true },
                   };
        var r = new FrameRenderer(caps);
        var buf = new CellBuffer(3, 1);
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
        buf.Set(0, 0, "x", CellStyle.Default.WithBackground(Color.FromRgb(255, 0, 0)));

        var output = Render(r, buf);
        Assert.EndsWith("\x1b[0m", output);
    }
}
