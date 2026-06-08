using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;

// Phase-6a showcase: brush-aware rich text. A RichText document is laid out by TextFormatter, then painted
// via DrawingContext.DrawFormattedText with a single gradient brush sampled per cell against each block's
// rect (block-scoped 2-D). Event-driven (static content): re-formats + repaints only on resize.
internal sealed class BrushedTextDemo : InteractiveDemo
{
    public override string Name => "brushtext";
    public override IReadOnlyList<string> Aliases => ["gradtext"];
    public override string Description =>
        "Brush-aware rich text — a gradient flows over a wrapped, formatted document (Phase 6a).";

    protected override string? IntroMessage =>
        "Brushed rich-text demo. Opening alt screen — press q or Ctrl+C to exit.";

    private Scene _scene = null!;
    private SceneCompositor _compositor = null!;
    private RichText _doc = null!;
    private TextFormatter _formatter = null!;

    protected override void Initialize()
    {
        _doc = BuildDocument(Style);
        _formatter = new TextFormatter();
        Build();
    }

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
        var label = Color.FromRgb(150, 160, 200);
        ctx.DrawText(2, 0, "Brush-aware rich text — one gradient over a formatted document (block-scoped 2-D):", label);

        int width = Math.Max(20, Math.Min(78, ctx.Bounds.Columns - 4));
        var ft = _formatter.Format(_doc, width, maxRows: null, Capabilities.Output);
        int height = Math.Min(ft.Size.Rows, Math.Max(1, ctx.Bounds.Rows - 3));

        // Diagonal gradient sampled against each block's rect: text and the rule are colored per cell, so the
        // sweep flows across and down each block; the explicitly-colored word survives (explicit fg wins).
        var gradient = new LinearGradientBrush(
            Color.FromHex("#f92572"), Color.FromHex("#66d9ef"),
            startPoint: RelativePoint.TopLeft, endPoint: RelativePoint.BottomRight);

        ctx.DrawFormattedText(ft, new Rect(2, 2, width, height), gradient, Capabilities.Output);
    }

    private static RichText BuildDocument(in Style defaultStyle)
    {
        var builder = new RichTextBuilder(defaultStyle);
        var opts = new TextMarkupOptions { DefaultStyle = defaultStyle };

        TextMarkup.Parse(
            "Cursorial's TextFormatter lays out wrapped, aligned rich text into cell-grid lines. In the " +
            "Drawing layer, DrawFormattedText colors that laid-out document with an IBrush sampled per cell " +
            "against each block's rectangle — so a single gradient flows across and down a whole paragraph, " +
            "wrapping along with the text rather than restarting on every line.",
            builder, opts);
        builder.EndParagraph();

        builder.HorizontalRule(HorizontalRule.Double);

        TextMarkup.Parse(
            "[p align=justify]The brush colors only cells whose foreground is unset, so an [fg=brightyellow]" +
            "explicitly colored word[/fg] keeps its own color while everything around it rides the gradient. " +
            "Horizontal rules are colored cell by cell, and an image or icon that degrades to a glyph inherits " +
            "the gradient too. Narrow the terminal and the sweep re-wraps with the prose.[/p]",
            builder, opts);

        return builder.Build();
    }

    protected override void RenderFrame(long frame) =>
        _compositor.Composite([new SceneLayer(_scene)], Buffer.AsView());
}
