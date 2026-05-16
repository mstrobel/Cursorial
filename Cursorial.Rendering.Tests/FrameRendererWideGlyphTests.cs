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
        buf.Set(0, 2, "X", Style.Default); // first cell after the wide glyph's continuation

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
}
