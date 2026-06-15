using Cursorial.Drawing.Media;

namespace Cursorial.UI.Controls;

/// <summary>
/// The base class for layout containers (design doc §5.4): <see cref="Children"/> is owner-wired
/// (visual <b>and</b> logical adoption, index = paint order); <see cref="Background"/> is the
/// surface brush, painted before children via <c>FillOpaque</c> — always glyph-occluding
/// (translucent brushes still frost).
/// </summary>
public abstract class Panel : UIElement
{
    /// <summary>
    /// The panel's surface brush (<c>[AffectsRender]</c>). Painted via <c>FillOpaque</c>, never
    /// <c>FillRectangle</c> — every zone composites over something (parent zone, lower layers,
    /// base), so a background that doesn't occlude lower layers' glyphs would show the text it
    /// floats over (doc §5.5 pinned surface rule — the glyph-grid hazard).
    /// </summary>
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        UIProperty.Register<Panel, IBrush?>(nameof(Background));

    static Panel()
    {
        AffectsRender<Panel>(BackgroundProperty);
    }

    /// <summary>Creates the panel and its owner-wired <see cref="Children"/> collection.</summary>
    protected Panel()
    {
        Children = new UIElementCollection(this);
    }

    /// <summary>The panel's children — owner-wired (visual + logical adoption; index = paint order).</summary>
    public UIElementCollection Children { get; }

    /// <inheritdoc cref="BackgroundProperty"/>
    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    /// <summary>
    /// Paints <see cref="Background"/> over the panel's full bounds via <c>FillOpaque</c> (the doc
    /// §5.5 pinned surface rule) before children paint on top. No-op when the brush is null.
    /// </summary>
    protected override void Render(RenderContext context)
    {
        if (context.Bounds.IsEmpty || Background is not {} background)
            return;

        if (background.IsOpaque)
            context.FillOpaque(context.Bounds, background);
        else
            context.FillRectangle(context.Bounds, background);
    }
}
