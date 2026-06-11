using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;

// ReSharper disable CheckNamespace

// Phase-3 + 4b showcase: the Pen / box / junction engine and braille diagonal lines. Box weights
// (light/heavy/double/ascii), rounded + dashed strokes, a junction table grid (corners/tees/crosses
// resolve automatically across separate calls), a mixed-weight composed border, a gradient-stroked
// box, and diagonal lines rasterized to braille. Static content drawn once into a cached scene and
// composited each frame; repaints only on resize.
internal sealed class PensDemo : InteractiveDemo
{
    public override string Name => "pens";
    public override IReadOnlyList<string> Aliases => ["strokes"];
    public override string Description => "Pen box drawing: weights, corners, dashes, junctions, gradient strokes, braille diagonals.";

    protected override string IntroMessage =>
        "Pens demo. Opening alt screen — press q or Ctrl+C to exit.";

    private Scene? _scene;
    private SceneCompositor? _compositor;

    protected override void Initialize() => Build();

    protected override void OnResize(int columns, int rows)
    {
        base.OnResize(columns, rows);
        Build();
    }

    private void Build()
    {
        _compositor = new SceneCompositor(Style);
        _scene?.Dispose();
        _scene = Scene.Create(Buffer.Columns, Buffer.Rows);
        _scene.Draw(Paint);
    }

    private void Paint(DrawingContext ctx)
    {
        var heading = Color.FromRgb(200, 210, 255);
        var label = Color.FromRgb(150, 160, 200);
        var cyan = Color.FromRgb(120, 220, 232);
        var amber = Color.FromRgb(235, 195, 90);
        var green = Color.FromRgb(90, 210, 120);

        ctx.DrawText(1, 0, "Cursorial Drawing — pens · boxes · junctions · braille (Phase 3 + 4b)", heading);

        // --- Box weights (+ ASCII glyph set) ---
        ctx.DrawText(1, 2, "Box weights:", label);
        ctx.DrawBox(new Rect(1, 3, 8, 3), Pens.Light.WithColor(cyan));
        ctx.DrawBox(new Rect(11, 3, 8, 3), Pens.Heavy.WithColor(cyan));
        ctx.DrawBox(new Rect(21, 3, 8, 3), Pens.Double.WithColor(cyan));
        ctx.DrawBox(new Rect(31, 3, 8, 3), Pens.Ascii.WithColor(cyan));
        ctx.DrawText(2, 6, "light", label);
        ctx.DrawText(12, 6, "heavy", label);
        ctx.DrawText(22, 6, "double", label);
        ctx.DrawText(32, 6, "ascii", label);

        // --- Rounded + dashed ---
        ctx.DrawText(1, 8, "Rounded · dashed:", label);
        ctx.DrawBox(new Rect(1, 9, 8, 3), Pens.Rounded.WithColor(green));
        ctx.DrawBox(new Rect(11, 9, 14, 3), Pens.Light.WithColor(green).WithDash(LineDash.Triple));

        // --- Junction table grid: outer box + internal lines; corners/tees/crosses self-resolve. ---
        ctx.DrawText(1, 13, "Junction grid (┌┬┐ ├┼┤ └┴┘):", label);
        ctx.DrawBox(new Rect(1, 14, 22, 6), Pens.Light.WithColor(cyan));
        ctx.DrawLine(8, 14, 8, 19, Pens.Light.WithColor(cyan));    // internal vertical
        ctx.DrawLine(15, 14, 15, 19, Pens.Light.WithColor(cyan));  // internal vertical
        ctx.DrawLine(1, 16, 22, 16, Pens.Light.WithColor(cyan));   // internal horizontal

        // --- Mixed-weight composed border (heavy top, light sides → ┍ ┑ corners) ---
        ctx.DrawText(40, 2, "Mixed-weight border:", label);
        ctx.DrawLine(40, 3, 55, 3, Pens.Heavy.WithColor(amber));   // heavy top
        ctx.DrawLine(40, 5, 55, 5, Pens.Light.WithColor(amber));   // light bottom
        ctx.DrawLine(40, 3, 40, 5, Pens.Light.WithColor(amber));   // light left
        ctx.DrawLine(55, 3, 55, 5, Pens.Light.WithColor(amber));   // light right

        // --- Gradient-stroked box (the brush samples across the box bounds) ---
        ctx.DrawText(40, 7, "Gradient stroke:", label);
        var stroke = new LinearGradientBrush(Color.FromRgb(120, 220, 232), Color.FromRgb(196, 150, 255));
        ctx.DrawBox(new Rect(40, 8, 18, 3), new Pen(stroke));

        // --- Diagonal lines → braille (an X plus a fan, sub-cell resolution) ---
        ctx.DrawText(40, 12, "Diagonals → braille:", label);
        ctx.DrawLine(40, 13, 58, 20, cyan);   // ╲
        ctx.DrawLine(58, 13, 40, 20, cyan);   // ╱  (forms an X in braille)
        ctx.DrawLine(49, 13, 42, 20, green);  // fan spokes from the apex
        ctx.DrawLine(49, 13, 56, 20, green);
    }

    protected override void RenderFrame(long frame)
    {
        if (_scene is {} scene)
            _compositor?.Composite([new SceneLayer(scene)], Buffer.AsView());
    }
}
