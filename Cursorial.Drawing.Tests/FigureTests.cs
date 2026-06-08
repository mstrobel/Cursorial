using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Drawing;

public class FigureTests
{
    // Black→white horizontal gradient; col red == round(t * 255).
    private static Pen GradientPen() => new(new LinearGradientBrush(Colors.TrueBlack, Colors.TrueWhite));

    [Fact]
    public void SameFigure_Root_FormsJunction()
    {
        var b = DrawHarness.Render(3, 3, ctx =>
        {
            ctx.DrawLine(0, 1, 2, 1, Pens.Light);   // horizontal
            ctx.DrawLine(1, 0, 1, 2, Pens.Light);   // vertical
        });
        Assert.Equal("┼", b[1, 1].Grapheme);
    }

    [Fact]
    public void SeparateFigures_DoNotJunction_LaterWinsTheCell()
    {
        var b = DrawHarness.Render(3, 3, ctx =>
        {
            using (ctx.BeginFigure()) ctx.DrawLine(0, 1, 2, 1, Pens.Light);   // figure 1: horizontal
            using (ctx.BeginFigure()) ctx.DrawLine(1, 0, 1, 2, Pens.Light);   // figure 2: vertical
        });
        Assert.Equal("│", b[1, 1].Grapheme);   // no ┼ — the later figure overwrites the shared cell
    }

    [Fact]
    public void RootBounds_ArePerCall()
    {
        // A 4-cell gradient line in the root samples against its own 4-wide bounds: col0 t=0.5/4 → 32.
        var b = DrawHarness.Render(8, 1, ctx => ctx.DrawLine(0, 0, 3, 0, GradientPen()));
        Assert.Equal(32, b[0, 0].Style.Foreground.Red);
    }

    [Fact]
    public void ExplicitFigureBounds_OverrideSampling()
    {
        // Same line, but the figure pins bounds to 8 wide: col0 t=0.5/8 → 16.
        var b = DrawHarness.Render(8, 1, ctx =>
        {
            using (ctx.BeginFigure(new Rect(0, 0, 8, 1)))
                ctx.DrawLine(0, 0, 3, 0, GradientPen());
        });
        Assert.Equal(16, b[0, 0].Style.Foreground.Red);
    }

    [Fact]
    public void AutoFigureBounds_AreTheUnionOfMemberStrokes()
    {
        // Two lines in one auto figure → union bounds (0,0,8,2); col0 t=0.5/8 → 16, not the per-call 32.
        var b = DrawHarness.Render(8, 2, ctx =>
        {
            using (ctx.BeginFigure())
            {
                ctx.DrawLine(0, 0, 3, 0, GradientPen());   // per-call would be 4 wide
                ctx.DrawLine(0, 1, 7, 1, GradientPen());   // widens the union to 8
            }
        });
        Assert.Equal(16, b[0, 0].Style.Foreground.Red);
    }

    [Fact]
    public void NestedFigure_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DrawHarness.Render(3, 3, ctx =>
            {
                using (ctx.BeginFigure())
                using (ctx.BeginFigure()) { }
            }));
    }

    [Fact]
    public void ManualEndThenScopeDispose_IsSafe_AndAllowsANewFigure()
    {
        // Manual EndFigure closes the figure; the stale scope.Dispose() must no-op (id mismatch),
        // and a fresh figure must be openable afterward.
        var b = DrawHarness.Render(3, 3, ctx =>
        {
            var scope = ctx.BeginFigure();
            ctx.DrawLine(0, 0, 2, 0, Pens.Light);
            ctx.EndFigure();
            scope.Dispose();                                  // no-op (already closed)
            using (ctx.BeginFigure()) ctx.DrawLine(0, 2, 2, 2, Pens.Light);
        });

        Assert.Equal("─", b[1, 0].Grapheme);
        Assert.Equal("─", b[1, 2].Grapheme);
    }
}
