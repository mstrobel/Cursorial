using Cursorial.Drawing.Media;

namespace Cursorial.UI.Controls;

/// <summary>
/// The base class for layout containers (design doc §5.4): <see cref="Children"/> is owner-wired
/// (visual <b>and</b> logical adoption, index = paint order); <see cref="Background"/> is the
/// surface brush, painted before children via <c>FillOpaque</c> — always glyph-occluding
/// (translucent brushes still frost).
/// </summary>
[Cursorial.Markup.ContentProperty("Children")]
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

    /// <summary>
    /// Whether this panel hosts an <see cref="ItemsControl"/>'s generated containers (the WPF
    /// <c>Panel.IsItemsHost</c> contract): when true, <see cref="Children"/> adopts items <b>visually only</b>,
    /// leaving them logical children of the <c>ItemsControl</c> so inheritance flows from the control (punch 43).
    /// Set on the items panel inside an items-control template (the <see cref="ItemsPresenter"/> does this).
    /// </summary>
    public static readonly StyledProperty<bool> IsItemsHostProperty =
        UIProperty.Register<Panel, bool>(nameof(IsItemsHost), changed: OnIsItemsHostChanged);

    /// <summary>Whether the background occludes (floating surface) rather than tints (<c>AffectsRender</c>).</summary>
    public static readonly StyledProperty<bool> OccludesProperty =
        UIProperty.Register<Panel, bool>(nameof(Occludes));

    static Panel()
    {
        AddGlobalEffects(PropertyEffects.AffectsRender, OccludesProperty);
        AddGlobalEffects(PropertyEffects.AffectsRender, BackgroundProperty);
    }

    private static void OnIsItemsHostChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is Panel panel)
            panel.AdoptsChildrenVisualOnly = newValue;
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

    /// <inheritdoc cref="IsItemsHostProperty"/>
    public bool IsItemsHost { get => GetValue(IsItemsHostProperty); set => SetValue(IsItemsHostProperty, value); }

    /// <inheritdoc cref="Border.OccludesProperty"/>
    public bool Occludes { get => GetValue(OccludesProperty); set => SetValue(OccludesProperty, value); }

    /// <summary>
    /// Paints <see cref="Background"/> over the panel's full bounds via <c>FillOpaque</c> (the doc
    /// §5.5 pinned surface rule) before children paint on top. No-op when the brush is null.
    /// </summary>
    protected override void Render(RenderContext context)
    {
        var bounds = context.Bounds;

        if (bounds.IsEmpty || Background is not {} background)
            return;

        if (Occludes)
            context.FillOpaque(bounds, background, overwrite: true);
        else
            context.FillRectangle(bounds, background);
    }
}
