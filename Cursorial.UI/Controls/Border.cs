using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Rendering;

namespace Cursorial.UI.Controls;

/// <summary>
/// A decorated box (design doc §12.7): a <see cref="Decorator.Child"/> inside an optional
/// <see cref="BorderPen"/> stroke and <see cref="Padding"/>, over a <see cref="Background"/> fill.
/// <see cref="Title"/> is the GroupBox story (<c>DrawTitledBox</c>); <see cref="Occludes"/> selects
/// the floating-surface idiom (<c>FillOpaque</c> + overwrite box) over the tint fill. Adjacent borders
/// in a shared zone scene junction-merge (the default <c>JunctionMode.Merge</c>).
/// </summary>
public class Border : Decorator
{
    /// <summary>The surface brush (<c>AffectsRender</c>; <b>not</b> inherited).</summary>
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        UIProperty.Register<Border, IBrush?>(nameof(Background));

    /// <summary>The border stroke (<c>AffectsRender</c> + nullity escalation — doc §12.4).</summary>
    public static readonly StyledProperty<Pen?> BorderPenProperty =
        UIProperty.Register<Border, Pen?>(nameof(BorderPen), changed: OnBorderPenChanged);

    /// <summary>The inner padding (<c>AffectsMeasure</c>).</summary>
    public static readonly StyledProperty<Margins> PaddingProperty =
        UIProperty.Register<Border, Margins>(nameof(Padding));

    /// <summary>The title text in the top edge (<c>AffectsRender</c> + presence escalation — doc §12.4).</summary>
    public static readonly StyledProperty<string?> TitleProperty =
        UIProperty.Register<Border, string?>(nameof(Title), changed: OnTitleChanged);

    /// <summary>Placement of <see cref="Title"/> along the top edge (<c>AffectsRender</c>).</summary>
    public static readonly StyledProperty<TitlePosition> TitlePositionProperty =
        UIProperty.Register<Border, TitlePosition>(nameof(TitlePosition));

    /// <summary>Whether the background occludes (floating surface) rather than tints (<c>AffectsRender</c>).</summary>
    public static readonly StyledProperty<bool> OccludesProperty =
        UIProperty.Register<Border, bool>(nameof(Occludes));

    static Border()
    {
        AffectsRender<Border>(BackgroundProperty, BorderPenProperty, TitleProperty, TitlePositionProperty, OccludesProperty);
        AffectsMeasure<Border>(PaddingProperty);
    }

    /// <inheritdoc cref="BackgroundProperty"/>
    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    /// <inheritdoc cref="BorderPenProperty"/>
    public Pen? BorderPen { get => GetValue(BorderPenProperty); set => SetValue(BorderPenProperty, value); }

    /// <inheritdoc cref="PaddingProperty"/>
    public Margins Padding { get => GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }

    /// <inheritdoc cref="TitleProperty"/>
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <inheritdoc cref="TitlePositionProperty"/>
    public TitlePosition TitlePosition { get => GetValue(TitlePositionProperty); set => SetValue(TitlePositionProperty, value); }

    /// <inheritdoc cref="OccludesProperty"/>
    public bool Occludes { get => GetValue(OccludesProperty); set => SetValue(OccludesProperty, value); }

    /// <summary>
    /// The whole-edge inset the border + padding consumes: 1 cell per edge when a <see cref="BorderPen"/>
    /// is present; a <see cref="Title"/> with no pen still forces a 1-cell top edge (the GroupBox row,
    /// doc §12.4). Padding adds on top.
    /// </summary>
    private Margins Inset
    {
        get
        {
            var padding = Padding;
            var hasBorder = BorderPen.HasValue;
            var titled = Title is { Length: > 0 };
            var edge = hasBorder ? 1 : 0;
            var topEdge = hasBorder || titled ? 1 : 0;

            return new Margins(
                padding.Left + edge,
                padding.Top + topEdge,
                padding.Right + edge,
                padding.Bottom + edge);
        }
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var inset = Inset;
        var inner = LayoutMath.Sub(availableSize, inset);

        var content = Size.Empty;
        if (Child is { } child)
        {
            child.Measure(inner);
            content = child.DesiredSize;
        }

        return LayoutMath.Add(content, inset);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var inset = Inset;
        if (Child is { } child)
        {
            var x = inset.Left;
            var y = inset.Top;
            var w = Math.Max(0, finalSize.Columns - inset.Horizontal);
            var h = Math.Max(0, finalSize.Rows - inset.Vertical);
            child.Arrange(new Rect(x, y, w, h));
        }

        return finalSize;
    }

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        var bounds = context.Bounds;
        if (bounds.IsEmpty)
            return;

        var occludes = Occludes;

        // The surface: FillOpaque for a floating surface, the glyph-transparent FillRectangle tint otherwise.
        if (Background is { } background)
        {
            if (occludes)
                context.FillOpaque(bounds, background);
            else
                context.FillRectangle(bounds, background);
        }

        // The box: a titled frame is DrawTitledBox; a plain frame is DrawBox. An occluding surface
        // overwrites (the floating-surface recipe); a tint does not.
        if (BorderPen is { } pen)
        {
            if (Title is { Length: > 0 } title)
                context.DrawTitledBox(bounds, new PanelTitle(title) { Position = TitlePosition }, pen, overwrite: occludes);
            else
                context.DrawBox(bounds, pen, overwrite: occludes);
        }
        else if (Title is { Length: > 0 } titleNoPen)
        {
            // A title without an explicit pen still draws the framing edge (the GroupBox top row).
            context.DrawTitledBox(bounds, new PanelTitle(titleNoPen) { Position = TitlePosition }, Pens.Light, overwrite: occludes);
        }
    }

    // ───────────────────────────── nullity / presence escalation (doc §12.4) ─────────────────────────────

    private static void OnBorderPenChanged(UIObject sender, Pen? oldValue, Pen? newValue)
    {
        if (sender is Border border && oldValue.HasValue != newValue.HasValue)
            border.InvalidateMeasure(); // ±1 cell/edge geometry flip (C169)
    }

    private static void OnTitleChanged(UIObject sender, string? oldValue, string? newValue)
    {
        // A title presence flip forces/removes the top border row (C170); a text-only change is render-only.
        if (sender is Border border && (oldValue is { Length: > 0 }) != (newValue is { Length: > 0 }))
            border.InvalidateMeasure();
    }
}
