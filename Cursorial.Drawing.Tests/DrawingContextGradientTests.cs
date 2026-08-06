using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Drawing;

public class DrawingContextGradientTests
{
    private static readonly Color Black = Color.FromRgb(0, 0, 0);
    private static readonly Color White = Color.FromRgb(255, 255, 255);
    private static GradientStop[] BlackToWhite => [new(0.0, Black), new(1.0, White)];

    // Inspect drawing output by compositing the scene onto a black base buffer and reading cells
    // (Scene.Buffer is internal — the public path is to composite).
    private static CellBuffer Composite(Scene scene, int cols, int rows)
    {
        var buffer = new CellBuffer(cols, rows);
        new SceneCompositor(CellStyle.Default.WithBackground(Black)).Composite(new[] { new SceneLayer(scene) }, buffer.AsView());
        return buffer;
    }

    [Fact]
    public void FillRectangle_Gradient_SamplesPerCellBackground()
    {
        var scene = Scene.Create(4, 1);
        scene.Draw(ctx => ctx.FillRectangle(scene.Bounds, new LinearGradientBrush(BlackToWhite)));

        var buffer = Composite(scene, 4, 1);

        // Opaque gradient over a black base composites to the gradient colors verbatim.
        Assert.Equal(32, buffer[0, 0].Style.Background.Red);
        Assert.Equal(96, buffer[1, 0].Style.Background.Red);
        Assert.Equal(159, buffer[2, 0].Style.Background.Red);
        Assert.Equal(223, buffer[3, 0].Style.Background.Red);
    }

    [Fact]
    public void FillRectangle_Solid_StillWorks()
    {
        var scene = Scene.Create(2, 1);
        scene.Draw(ctx => ctx.FillRectangle(scene.Bounds, Color.FromRgb(10, 20, 30)));   // implicit Color → Brush

        var buffer = Composite(scene, 2, 1);
        Assert.Equal(Color.FromRgb(10, 20, 30), buffer[0, 0].Style.Background);
    }

    [Fact]
    public void DrawText_SamplesForegroundGradientPerGlyph()
    {
        var scene = Scene.Create(2, 1);
        scene.Draw(ctx => ctx.DrawText(0, 0, "AB", new LinearGradientBrush(BlackToWhite)));

        var buffer = Composite(scene, 2, 1);

        Assert.Equal("A", buffer[0, 0].Grapheme);
        Assert.Equal("B", buffer[1, 0].Grapheme);
        // Foreground ramps across the run (extent = the two run cells): 0.25 → 64, 0.75 → 191.
        Assert.Equal(64, buffer[0, 0].Style.Foreground.Red);
        Assert.Equal(191, buffer[1, 0].Style.Foreground.Red);
    }

    [Fact]
    public void DrawText_DefaultBackground_IsTransparent_BaseShowsThrough()
    {
        var scene = Scene.Create(2, 1);
        scene.Draw(ctx => ctx.DrawText(0, 0, "x", Color.FromRgb(255, 255, 0)));   // solid yellow fg, default bg

        var buffer = Composite(scene, 2, 1);

        // Glyph cell's background is the base (transparent text bg let it through); empty cell is base.
        Assert.Equal(Black, buffer[0, 0].Style.Background);
        Assert.Equal(Black, buffer[1, 0].Style.Background);
        Assert.Equal("x", buffer[0, 0].Grapheme);
    }

    [Fact]
    public void DrawText_OverFill_PreservesFillBackground()
    {
        // Intra-scene compose: DrawText's default transparent background lets a prior FillRectangle's
        // background show under the glyph (Set blends the transparent text-bg over the fill).
        var fill = Color.FromRgb(50, 60, 70);
        var scene = Scene.Create(2, 1);
        scene.Draw(ctx =>
        {
            ctx.FillRectangle(scene.Bounds, fill);
            ctx.DrawText(0, 0, "A", Color.FromRgb(255, 255, 255));
        });

        var buffer = Composite(scene, 2, 1);

        Assert.Equal("A", buffer[0, 0].Grapheme);
        Assert.Equal(fill, buffer[0, 0].Style.Background);   // fill preserved under the glyph
        Assert.Equal(fill, buffer[1, 0].Style.Background);   // and where there's no glyph
    }

    [Fact]
    public void DrawText_AdvancesPastWideClusters()
    {
        var scene = Scene.Create(6, 1);
        var advanced = Size.Empty;
        scene.Draw(ctx => advanced = ctx.DrawText(0, 0, "中a", Color.FromRgb(255, 255, 255)));

        Assert.Equal(new Size(3, 1), advanced);   // wide CJK (2) + 'a' (1), one line

        var buffer = Composite(scene, 6, 1);
        Assert.Equal("中", buffer[0, 0].Grapheme);
        Assert.Equal(CellKind.WideLeft, buffer[0, 0].Kind);
        Assert.Equal("a", buffer[2, 0].Grapheme);
    }
}
