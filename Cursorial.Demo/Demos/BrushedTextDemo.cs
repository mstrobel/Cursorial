using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;

// Phase-6 showcase: brush-aware rich text, two panels. (1) A BLOCK-scoped 2-D gradient sampled per cell against
// each block's rect — one sweep flows across and down a whole wrapped paragraph. (2) An INLINE-scoped 1-D gradient
// on a single wrapping run — sampled as one reading-order strip, so the color continues across the wrap instead of
// restarting per line (wrap-invariant). Event-driven (static): re-formats + repaints only on resize.
internal sealed class BrushedTextDemo : InteractiveDemo
{
    public override string Name => "brushtext";
    public override IReadOnlyList<string> Aliases => ["gradtext"];
    public override string Description =>
        "Brush-aware rich text — block-scoped 2-D + inline-scoped wrap-invariant gradients (Phase 6).";

    protected override string? IntroMessage =>
        "Brushed rich-text demo. Opening alt screen — press q or Ctrl+C to exit.";

    private Scene _scene = null!;
    private SceneCompositor _compositor = null!;
    private RichText _doc = null!;
    private RichText _inlineDoc = null!;
    private TextFormatter _formatter = null!;

    protected override void Initialize()
    {
        _doc = BuildDocument(Style);
        _inlineDoc = BuildInlineDocument();
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
        int width = Math.Max(20, Math.Min(78, ctx.Bounds.Columns - 4));

        // --- Panel 1: block-scoped 2-D — one gradient flows across AND down the whole document ---
        ctx.DrawText(2, 0, "Block-scoped 2-D — one gradient flows across and down the whole document:", label);
        var ft = _formatter.Format(_doc, width, maxRows: null, Capabilities.Output);

        // Diagonal gradient sampled against each block's rect: text and the rule are colored per cell, so the
        // sweep flows across and down each block; the explicitly-colored word survives (explicit fg wins).
        var diagonal = new LinearGradientBrush(
            Color.FromHex("#f92572"), Color.FromHex("#66d9ef"),
            startPoint: RelativePoint.TopLeft, endPoint: RelativePoint.BottomRight);

        // Reserve a band at the bottom for the inline (1-D) panel.
        const int inlineBand = 7;
        int docHeight = Math.Max(1, Math.Min(ft.Size.Rows, ctx.Bounds.Rows - 3 - inlineBand));
        ctx.DrawFormattedText(ft, new Rect(2, 2, width, docHeight), diagonal, Capabilities.Output);

        // --- Panel 2: inline-scoped 1-D — one gradient continues across the wrap, not restarting per line ---
        int inlineTop = 2 + docHeight + 1;
        if (inlineTop >= ctx.Bounds.Rows - 1) return;   // terminal too short for the second panel

        ctx.DrawText(2, inlineTop,
            "Inline-scoped 1-D — one gradient is a single reading-order strip; the color continues across the wrap:", label);
        int inlineWidth = Math.Min(width, 46);   // narrow so the run wraps several times
        var inlineFt = _formatter.Format(_inlineDoc, inlineWidth, maxRows: null, Capabilities.Output);
        int inlineHeight = Math.Max(1, Math.Min(inlineFt.Size.Rows, ctx.Bounds.Rows - inlineTop - 2));
        // Per-run overload (no document brush) — only the run's own inline BrushedStyle applies.
        ctx.DrawFormattedText(inlineFt, new Rect(2, inlineTop + 1, inlineWidth, inlineHeight), Capabilities.Output);
    }

    private static RichText BuildInlineDocument()
    {
        // One run, one Inline-scoped left→right gradient (green→orange). Because inline scope samples the run's
        // full reading-order strip, the color a glyph gets depends only on its position WITHIN the run — so the
        // sweep continues smoothly onto each wrapped line instead of restarting green at the left edge.
        var brush = new BrushedStyle(new LinearGradientBrush(
            Color.FromHex("#a6e22e"), Color.FromHex("#fd971f"),
            startPoint: RelativePoint.Left, endPoint: RelativePoint.Right));   // Inline is the default scope
        return new RichTextBuilder()
            .BrushedRun(
                "This single run carries one inline gradient — watch its color flow smoothly past each wrap " +
                "instead of restarting at green on every new line. That continuity is wrap-invariant 1-D sampling.",
                brush)
            .Build();
    }

    private static RichText BuildDocument(in Style defaultStyle)
    {
        var builder = new RichTextBuilder(defaultStyle);
        var opts = BrushMarkup.Options(defaultStyle);   // enables [brush=…] gradient markup (inline + registry)

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
            "the gradient too. A [brush=linear:#a6e22e,#fd971f]markup-authored gradient[/brush] overrides it for " +
            "its own run. Narrow the terminal and the sweep re-wraps with the prose.[/p]",
            builder, opts);

        return builder.Build();
    }

    protected override void RenderFrame(long frame) =>
        _compositor.Composite([new SceneLayer(_scene)], Buffer.AsView());
}
