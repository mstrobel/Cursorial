using System.Buffers;
using System.Text;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;

namespace Cursorial.Tests.Rendering;

public class FrameRendererWideGlyphTests
{
    private static string Render(FrameRenderer renderer, CellBuffer back)
    {
        var w = new ArrayBufferWriter<byte>();
        renderer.Render(back, w);
        return Encoding.UTF8.GetString(w.WrittenSpan);
    }

    private static OutputCapabilities CapsWithWideGlyphs(bool wide)
        => OutputCapabilities.None with
           {
               TextSizing = new TextSizingCapabilities(Width: false, Scale: false, WideGlyphs: wide),
           };

    [Fact]
    public void WideLeft_WhenWideGlyphsTrusted_EmitsGlyphAlone()
    {
        // The trusted-terminal path is the v1 behavior — emit the wide glyph at the WideLeft
        // position, skip the continuation, trust the cursor to advance by 2.
        var r = new FrameRenderer(CapsWithWideGlyphs(true));
        var buf = new CellBuffer(4, 1);
        buf.Set(0, 0, "中", Style.Default);

        var output = Render(r, buf);

        // No pre-paint, no CUP-back-to-c. The glyph appears exactly once, with no flanking
        // spaces immediately before it.
        int idx = output.IndexOf('中');
        Assert.True(idx > 0);
        Assert.NotEqual(' ', output[idx - 1]);
    }

    [Fact]
    public void WideLeft_WhenWideGlyphsUntrusted_PrePaintsContinuationAndCupsBack()
    {
        // Untrusted terminals get the defense: cursor at (r, c), emit two spaces (painting c
        // and c+1 with the wide-left's SGR), CUP back to (r, c), then emit the wide glyph.
        // If the terminal honored the wide glyph, the glyph overpaints the spaces. If it
        // shrunk it, c+1 retains the wide-left's style instead of being abandoned.
        var r = new FrameRenderer(CapsWithWideGlyphs(false));
        var buf = new CellBuffer(4, 1);
        buf.Set(0, 0, "中", Style.Default);

        var output = Render(r, buf);

        // Find the wide glyph. Look back two characters — those should be the two pre-paint
        // spaces, and the cursor-position move-to that follows them returns the cursor to
        // column 0 (column 1 on the wire = 1-based) before the glyph is emitted.
        int glyphIdx = output.IndexOf('中');
        Assert.True(glyphIdx > 0);

        // The CSI move-back-to-c sequence (CSI 1;1 H) should appear between the spaces and
        // the glyph.
        string beforeGlyph = output[..glyphIdx];
        Assert.Contains("  ", beforeGlyph);              // two spaces
        Assert.Contains("\x1b[1;1H", beforeGlyph);       // CUP back to row 1 col 1 (0-based 0,0)
    }

    [Fact]
    public void WideLeft_WhenWideGlyphsUntrusted_ForcesCupForNextCell()
    {
        // After emitting a wide glyph on an untrusted terminal, the renderer can't trust the
        // cursor-advance count, so it forces an explicit CUP for the next cell rather than
        // relying on `_cursorCol += cell.Width`.
        var r = new FrameRenderer(CapsWithWideGlyphs(false));
        var buf = new CellBuffer(4, 1);
        buf.Set(0, 0, "中", Style.Default);
        buf.Set(2, 0, "X", Style.Default); // first cell after the wide glyph's continuation

        var output = Render(r, buf);

        int xIdx = output.IndexOf('X');
        int glyphIdx = output.IndexOf('中');
        Assert.True(xIdx > glyphIdx);

        // Between the wide glyph and the 'X', we must see a CUP that moves to (row 1, col 3)
        // — i.e., the renderer issued an explicit move rather than trusting the wide glyph's
        // advance.
        string between = output[(glyphIdx + 1)..xIdx];
        Assert.Contains("\x1b[1;3H", between);
    }

    [Fact]
    public void WideLeft_WithoutCapabilities_FallsBackToTrustedBehavior()
    {
        // When the renderer wasn't constructed with capabilities, the wide-glyph defense
        // doesn't engage — emit the glyph normally.
        var r = new FrameRenderer();
        var buf = new CellBuffer(4, 1);
        buf.Set(0, 0, "中", Style.Default);

        var output = Render(r, buf);
        int idx = output.IndexOf('中');
        Assert.True(idx > 0);
        Assert.NotEqual(' ', output[idx - 1]);
    }

    // ---- Ambiguous-width defense (box-drawing rules, geometric shapes, …) ----
    //
    // On a terminal flagged WideGlyphs=false, a single-width East-Asian-Ambiguous glyph is
    // treated like a wide glyph: its right neighbor is painted FIRST, the glyph is emitted at c,
    // then c+1 is SKIPPED so we never write into the glyph's (potential) second half — a write
    // there blanks the whole glyph on a terminal rendering ambiguous-as-wide.

    [Fact]
    public void AmbiguousGlyph_WhenUntrusted_PaintsNeighborBeforeGlyphThenSkips()
    {
        // col 0 = ambiguous rule glyph (─), col 1 = a distinct narrow glyph (X). The neighbor X
        // must be emitted BEFORE the glyph ─, with a CUP back to col 0 between them, and X must
        // appear exactly once (col 1 is skipped on its own iteration, not repainted).
        var r = new FrameRenderer(CapsWithWideGlyphs(false));
        var buf = new CellBuffer(2, 1);
        buf.Set(0, 0, "─", Style.Default);
        buf.Set(1, 0, "X", Style.Default);

        var output = Render(r, buf);

        int xIdx = output.IndexOf('X');
        int ruleIdx = output.IndexOf('─');
        Assert.True(xIdx >= 0 && ruleIdx >= 0);
        Assert.True(xIdx < ruleIdx, "neighbor must be painted before the ambiguous glyph");
        Assert.Contains("\x1b[1;1H", output[xIdx..]);          // CUP back to col 0 before the glyph
        Assert.Equal(xIdx, output.LastIndexOf('X'));            // neighbor emitted exactly once
    }

    [Fact]
    public void AmbiguousGlyph_WhenTrusted_EmitsInNaturalOrder()
    {
        // Trusted terminal: no defense. The cells emit left-to-right in a contiguous run, so the
        // rule glyph comes before its neighbor and there's no CUP-back.
        var r = new FrameRenderer(CapsWithWideGlyphs(true));
        var buf = new CellBuffer(2, 1);
        buf.Set(0, 0, "─", Style.Default);
        buf.Set(1, 0, "X", Style.Default);

        var output = Render(r, buf);

        Assert.True(output.IndexOf('─') < output.IndexOf('X'));
    }

    [Fact]
    public void AmbiguousRule_WhenUntrusted_EmitsEveryRuleCell()
    {
        // A full run of rule glyphs must still paint a glyph for every column — the defense
        // pre-paints neighbors and skips them, but a neighbor that is itself a rule glyph is
        // painted as the pre-paint, so no column is left blank (the "rule has gaps" failure).
        var r = new FrameRenderer(CapsWithWideGlyphs(false));
        var buf = new CellBuffer(4, 1);
        for (int c = 0; c < 4; c++) buf.Set(c, 0, "─", Style.Default);

        var output = Render(r, buf);

        Assert.Equal(4, output.Count(ch => ch == '─'));
    }

    [Fact]
    public void AsciiRun_WhenUntrusted_StaysContiguous()
    {
        // The defense must NOT fire for plain ASCII — it's unambiguously narrow everywhere, so
        // prose keeps the fast contiguous-run path (no per-cell CUPs) even on an untrusted
        // terminal.
        var r = new FrameRenderer(CapsWithWideGlyphs(false));
        var buf = new CellBuffer(4, 1);
        foreach (var (c, ch) in new[] { (0, "a"), (1, "b"), (2, "c"), (3, "d") })
            buf.Set(c, 0, ch, Style.Default);

        var output = Render(r, buf);

        Assert.DoesNotContain("\x1b[1;2H", output);
        Assert.DoesNotContain("\x1b[1;3H", output);
    }

    [Fact]
    public void AmbiguousGlyph_NeighborRepaintedWithGlyph_AcrossDiffFrames()
    {
        // Cross-frame: when a later frame re-emits the ambiguous glyph (changed) but its neighbor
        // is model-unchanged, the neighbor must still be repainted as part of the pair — its
        // content may have been clobbered by the glyph's wide render on the prior frame. The
        // full-redraw frame would mask this, so the assertion is on the second (diff) frame.
        var r = new FrameRenderer(CapsWithWideGlyphs(false));
        var gutter = Style.Default.WithBackground(Color.FromRgb(10, 20, 30));

        var buf1 = new CellBuffer(3, 1);
        buf1.Set(0, 0, "─", Style.Default);
        buf1.Set(1, 0, " ", gutter);
        Render(r, buf1); // establishes the front buffer

        var buf2 = new CellBuffer(3, 1);
        buf2.Set(0, 0, "═", Style.Default); // col 0 changed (light → double rule)
        buf2.Set(1, 0, " ", gutter);        // col 1 model-unchanged
        var output = Render(r, buf2);

        // The changed glyph is re-emitted, and the pair-emit repositions to the neighbor (col 2,
        // 1-based) and back to the glyph (col 1) — so both CUPs appear in the diff frame.
        Assert.Contains("═", output);
        Assert.Contains("\x1b[1;2H", output); // neighbor pre-paint
        Assert.Contains("\x1b[1;1H", output); // CUP back to the glyph
    }

    [Fact]
    public void AmbiguousGlyph_InLastColumn_EmitsNormallyWithoutDefense()
    {
        // An ambiguous glyph in the final column has no in-bounds right neighbor, so the defense
        // doesn't engage — it emits as a plain cell. Must not throw or address a column past the
        // edge.
        var r = new FrameRenderer(CapsWithWideGlyphs(false));
        var buf = new CellBuffer(2, 1);
        buf.Set(0, 0, "a", Style.Default);
        buf.Set(1, 0, "─", Style.Default); // ambiguous glyph in the last column

        var output = Render(r, buf);
        Assert.Contains("─", output);
        Assert.DoesNotContain("\x1b[1;3H", output);
    }
}
