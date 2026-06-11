using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Text;

// ReSharper disable CheckNamespace

// Showcase of the post-Phase-6 UI drawing primitives: a gradient FIGlet headline (per-cell brushed), a
// titled panel with a drop + inner shadow, and an opaque modal that occludes the background text beneath it
// (FillOpaque) with its own drop shadow. Event-driven (static): repaints only on resize.
internal sealed class UIPrimitivesDemo : InteractiveDemo
{
    public override string Name => "ui";
    public override IReadOnlyList<string> Aliases => ["panels"];
    public override string Description =>
        "UI primitives — gradient FIGlet, titled panel + shadows, and an occluding opaque modal.";

    protected override string IntroMessage =>
        "UI primitives demo. Opening alt screen — press q or Ctrl+C to exit.";

    private Scene _scene = null!;
    private SceneCompositor _compositor = null!;
    private readonly TextFormatter _formatter = new();

    protected override void Initialize() => Build();

    protected override void OnResize(int columns, int rows)
    {
        base.OnResize(columns, rows);
        Build();
    }

    private void Build()
    {
        _compositor = new SceneCompositor(Style);
        _scene.Dispose();
        _scene = Scene.Create(Buffer.Columns, Buffer.Rows);
        _scene.Draw(Paint);
    }

    private void Paint(DrawingContext ctx)
    {
        int w = ctx.Bounds.Columns, h = ctx.Bounds.Rows;
        var dim = Color.FromRgb(72, 78, 104);
        int top = Math.Min(8, Math.Max(0, h - 8));   // headline above; content + widgets below

        // Background text only BELOW the headline — so the FIGlet sits on a clean backdrop, while the opaque
        // modal still has real content to occlude.
        const string phrase = "cursorial ui primitives  ";
        for (int r = top; r < h; r++)
            ctx.DrawText(0, r, Repeat(phrase, w), dim);

        // Gradient FIGlet headline — a block font (AnsiShadow), brushed per cell so the magenta→cyan sweep
        // flows vividly across the solid glyphs.
        if (w >= 30 && top >= 6)
        {
            var doc = new RichTextBuilder().Figlet("UI", FigletFonts.AnsiShadow).Build();
            var ft = _formatter.Format(doc, Math.Min(w - 4, 60), maxRows: null, Capabilities.Output);
            var grad = new LinearGradientBrush(Color.FromHex("#f92672"), Color.FromHex("#66d9ef"),
                                               startPoint: RelativePoint.Left, endPoint: RelativePoint.Right);
            ctx.DrawFormattedText(ft, new Rect(2, 0, Math.Min(w - 4, 60), Math.Min(ft.Size.Rows, top - 1)), grad, Capabilities.Output);
        }

        var edges = ShadowEdges.Bottom | ShadowEdges.Right;
        
        // Titled panel with a drop shadow and an inner shadow on its fill.

        if (w >= 24 && top + 7 <= h)
        {
            var panel = new Rect(2, top, 20, 6);

            ctx.DrawDropShadow(panel, ShadowGeometry.Drop(radius: 0, offset: 1, strength: 0.75, edges),
                               Color.FromRgb(0, 0, 0));
            ctx.DrawPanel(panel, Color.FromRgb(120, 200, 160), new SolidColorBrush(Color.FromRgb(30, 34, 46)),
                          new PanelTitle("Panel").WithColor(Color.FromRgb(200, 210, 255)));
            ctx.DrawInnerShadow(panel, ShadowGeometry.Inner(radius: 1, strength: 0.4), Color.FromRgb(0, 0, 0));
            ctx.DrawText(4, top + 2, "drop + inner", Color.FromRgb(190, 200, 220));
            ctx.DrawText(4, top + 3, "shadow, title", Color.FromRgb(190, 200, 220));
        }

        // Opaque modal: occludes the dotted background, with its own shadow and an overwriting border.
        if (w >= 48 && top + 7 <= h)
        {
            var modal = new Rect(26, top + 1, 18, 5);
            ctx.DrawDropShadow(modal, ShadowGeometry.Drop(radius: 0, offset: 1, strength: 0.75, edges), Color.FromRgb(0, 0, 0));
            ctx.FillOpaque(modal, Color.FromRgba(45, 40, 70, 63));
            ctx.DrawBox(modal, new Pen(Color.FromRgb(185, 120, 200)) { Weight = StrokeWeight.Light, Corners = CornerStyle.Rounded }, overwrite: true);
            ctx.DrawText(28, top + 2, "opaque modal", Color.FromRgb(235, 225, 245));
            ctx.DrawText(28, top + 3, "hides text", Color.FromRgb(200, 190, 220));
        }
    }

    private static string Repeat(string phrase, int width)
    {
        if (width <= 0) return string.Empty;
        var sb = new System.Text.StringBuilder(width);
        while (sb.Length < width) sb.Append(phrase);
        return sb.ToString(0, width);
    }

    protected override void RenderFrame(long frame) =>
        _compositor.Composite([new SceneLayer(_scene)], Buffer.AsView());
}
