using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Drawing;

// Intra-scene clip + translate push/pop state stack on DrawingContext. Honored by the per-cell write paths
// (Set / FillRectangle / DrawText). DrawHarness.Render composites over an opaque black base, so a cell that
// is clipped away reads back as the base color.
public class DrawingStateTests
{
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color White = Color.FromRgb(255, 255, 255);
    private static readonly Color Black = Color.FromRgb(0, 0, 0);

    [Fact]
    public void PushTranslate_ShiftsWrites()
    {
        var b = DrawHarness.Render(8, 4, ctx =>
        {
            using (ctx.PushTranslate(2, 1))
                ctx.Set(0, 0, "X", Style.Default.WithForeground(White));
        });
        Assert.Equal("X", b[2, 1].Grapheme);   // local (0,0) → scene (2,1)
        Assert.True(string.IsNullOrEmpty(b[0, 0].Grapheme));
    }

    [Fact]
    public void PushTranslate_Negative_DropsAboveLeft_NoThrow()
    {
        var b = DrawHarness.Render(8, 4, ctx =>
        {
            using (ctx.PushTranslate(-1, -1))
                ctx.FillRectangle(new Rect(0, 0, 4, 4), Red);   // local→scene shifted (−1,−1)
        });
        Assert.Equal(Red, b[0, 0].Style.Background);   // local (1,1) → scene (0,0)
        Assert.Equal(Red, b[2, 2].Style.Background);   // local (3,3) → scene (2,2)
        // local (0,*) / (*,0) map to a negative scene coordinate → silently dropped (no crash, no wrap).
    }

    [Fact]
    public void PushClip_BoundsWrites()
    {
        var b = DrawHarness.Render(8, 4, ctx =>
        {
            using (ctx.PushClip(new Rect(1, 1, 2, 2)))
                ctx.FillRectangle(new Rect(0, 0, 8, 4), Red);   // would fill all; clip restricts to (1,1,2,2)
        });
        Assert.Equal(Red, b[1, 1].Style.Background);
        Assert.Equal(Red, b[2, 2].Style.Background);
        Assert.Equal(Black, b[0, 0].Style.Background);   // outside clip → base
        Assert.Equal(Black, b[3, 3].Style.Background);
    }

    [Fact]
    public void Push_Viewport_TranslatesContentAndClipsOverflow()
    {
        var b = DrawHarness.Render(10, 6, ctx =>
        {
            using (ctx.Push(clip: new Rect(2, 1, 4, 2), translateColumns: 2, translateRows: 1))
                ctx.FillRectangle(new Rect(0, 0, 4, 4), Red);   // 4×4 content, viewport shows 4×2
        });
        Assert.Equal(Red, b[2, 1].Style.Background);    // local (0,0) → scene (2,1), in clip
        Assert.Equal(Red, b[5, 2].Style.Background);    // local (3,1) → scene (5,2), in clip
        Assert.Equal(Black, b[2, 3].Style.Background);  // scene row 3 ≥ clip end → clipped
        Assert.Equal(Black, b[6, 1].Style.Background);  // scene col 6 ≥ clip end → clipped
        Assert.Equal(Black, b[1, 1].Style.Background);  // scene col 1 < clip → clipped
    }

    [Fact]
    public void NestedClips_Intersect()
    {
        var b = DrawHarness.Render(10, 10, ctx =>
        {
            using (ctx.PushClip(new Rect(0, 0, 6, 6)))
            using (ctx.PushClip(new Rect(2, 2, 6, 6)))   // intersect → (2,2,4,4)
                ctx.FillRectangle(new Rect(0, 0, 10, 10), Red);
        });
        Assert.Equal(Red, b[2, 2].Style.Background);
        Assert.Equal(Red, b[5, 5].Style.Background);
        Assert.Equal(Black, b[1, 1].Style.Background);
        Assert.Equal(Black, b[6, 6].Style.Background);
    }

    [Fact]
    public void Scope_Dispose_RestoresParentState()
    {
        var b = DrawHarness.Render(8, 4, ctx =>
        {
            using (ctx.PushClip(new Rect(0, 0, 2, 2))) { }   // pushed then immediately popped
            ctx.FillRectangle(new Rect(0, 0, 8, 4), Red);     // unclipped again
        });
        Assert.Equal(Red, b[7, 3].Style.Background);
    }

    [Fact]
    public void Scope_DoubleDispose_IsNoOp()
    {
        DrawHarness.Render(8, 4, ctx =>
        {
            var scope = ctx.PushClip(new Rect(0, 0, 2, 2));
            scope.Dispose();
            scope.Dispose();   // no throw, no over-pop
            Assert.Equal(new Rect(0, 0, 8, 4), ctx.CurrentClip);
        });
    }

    [Fact]
    public void Current_ReportsActiveState()
    {
        DrawHarness.Render(8, 4, ctx =>
        {
            Assert.Equal((0, 0), ctx.CurrentTranslate);
            Assert.Equal(new Rect(0, 0, 8, 4), ctx.CurrentClip);
            using (ctx.Push(clip: new Rect(1, 1, 4, 2), translateColumns: 1, translateRows: 1))
            {
                Assert.Equal((1, 1), ctx.CurrentTranslate);
                Assert.Equal(new Rect(1, 1, 4, 2), ctx.CurrentClip);
            }
            Assert.Equal((0, 0), ctx.CurrentTranslate);
            Assert.Equal(new Rect(0, 0, 8, 4), ctx.CurrentClip);
        });
    }

    [Fact]
    public void WideGlyph_AtClipRightEdge_DegradesToBlank()
    {
        var b = DrawHarness.Render(8, 2, ctx =>
        {
            using (ctx.PushClip(new Rect(0, 0, 3, 2)))   // clip columns [0,3)
                ctx.DrawText(0, 0, "中中", White);        // two wide glyphs; the second straddles the clip edge
        });
        Assert.Equal("中", b[0, 0].Grapheme);
        Assert.Equal(CellKind.WideContinuation, b[1, 0].Kind);
        Assert.True(string.IsNullOrEmpty(b[2, 0].Grapheme));   // second glyph's right half is outside → blank
    }

    [Fact]
    public void LeakedScope_DoesNotThrow()
    {
        // A push never disposed is reconciled when the draw delegate returns (no crash, deferred pass runs).
        var b = DrawHarness.Render(6, 3, ctx =>
        {
            ctx.PushClip(new Rect(0, 0, 2, 2));   // intentionally leaked
            ctx.Set(0, 0, "Z", Style.Default.WithForeground(White));
        });
        Assert.Equal("Z", b[0, 0].Grapheme);
    }
}
