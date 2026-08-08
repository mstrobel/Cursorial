using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;

namespace Cursorial.Tests.Drawing;

// FillOpaque writes space-bearing cells that hide (occlude) glyphs on lower layers — unlike FillRectangle,
// which is background-only and lets lower glyphs show through.
public class OccludeTests
{
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color White = Color.FromRgb(255, 255, 255);

    [Fact]
    public void FillOpaque_WritesSpaceBearingCells_WithBackground()
    {
        var b = DrawHarness.Render(4, 2, ctx => ctx.FillOpaque(new Rect(0, 0, 2, 2), Blue));
        Assert.Equal(CellBuffer.DurableEmptyGrapheme, b[0, 0].Grapheme);            // a real (space) glyph, not a blank null cell
        Assert.Equal(Blue, b[0, 0].Style.Background);
    }

    [Fact]
    public void FillOpaque_OccludesLowerLayerGlyph()
    {
        var b = DrawHarness.RenderLayers(4, 3, null,
            lower => lower.Set(1, 1, "X", CellStyle.Default.WithForeground(Red)),   // bottom
            upper => upper.FillOpaque(new Rect(0, 0, 3, 3), Blue));             // top
        Assert.Equal(CellBuffer.DurableEmptyGrapheme, b[1, 1].Grapheme);            // the lower 'X' is hidden
        Assert.Equal(Blue, b[1, 1].Style.Background);
    }

    [Fact]
    public void FillRectangle_DoesNotOcclude_LowerGlyphShowsThrough()
    {
        // Contrast: a background-only fill keeps the lower glyph (only the background merges).
        var b = DrawHarness.RenderLayers(4, 3, null,
            lower => lower.Set(1, 1, "X", CellStyle.Default.WithForeground(Red)),
            upper => upper.FillRectangle(new Rect(0, 0, 3, 3), Blue));
        Assert.Equal("X", b[1, 1].Grapheme);
        Assert.Equal(Blue, b[1, 1].Style.Background);
    }

    [Fact]
    public void BorderedOpaquePanel_BorderKeepsOpaqueBackground_AndOccludes()
    {
        // The recipe: fill opaque, then an overwriting border keeps the panel background under its glyph.
        var b = DrawHarness.RenderLayers(6, 4, null,
            lower => lower.Set(0, 0, "X", CellStyle.Default.WithForeground(Red)),
            upper =>
            {
                upper.FillOpaque(new Rect(0, 0, 4, 3), Blue);
                upper.DrawBox(new Rect(0, 0, 4, 3), White, overwrite: true);
            });
        Assert.Equal("┌", b[0, 0].Grapheme);            // border corner drawn over the panel
        Assert.Equal(Blue, b[0, 0].Style.Background);   // panel background kept (no transparent hole)
        Assert.NotEqual("X", b[0, 0].Grapheme);         // the lower 'X' does not bleed through
    }

    [Fact]
    public void FillOpaque_OverWideGlyph_LeavesCleanTarget()
    {
        // Filling over a wide glyph's right half must blank its orphaned left half (no stranded WideLeft).
        var b = DrawHarness.Render(4, 1, ctx =>
        {
            ctx.Set(0, 0, "中", CellStyle.Default.WithForeground(White));   // WideLeft @0, continuation @1
            ctx.FillOpaque(new Rect(1, 0, 2, 1), Blue);                 // overwrites the continuation
        });
        Assert.True(string.IsNullOrEmpty(b[0, 0].Grapheme));   // orphaned WideLeft cleaned up
        Assert.Equal(CellBuffer.DurableEmptyGrapheme, b[1, 0].Grapheme);
        Assert.Equal(Blue, b[1, 0].Style.Background);
    }

    [Fact]
    public void FillOpaque_Translucent_StillOccludesGlyph()
    {
        // Even a translucent (frosted) fill replaces the lower glyph — only the background blends.
        var frosted = new SolidColorBrush(Color.FromRgba(0, 0, 255, 128));
        var b = DrawHarness.RenderLayers(4, 3, Color.FromRgb(0, 0, 0),
            lower => lower.Set(1, 1, "X", CellStyle.Default.WithForeground(Red)),
            upper => upper.FillOpaque(new Rect(0, 0, 3, 3), frosted));
        Assert.Equal(CellBuffer.DurableEmptyGrapheme, b[1, 1].Grapheme);   // glyph occluded regardless of fill alpha
    }
}
