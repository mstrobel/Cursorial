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
        Panel.BackgroundProperty.AddOwner<Border>();

    /// <summary>The title text brush (<c>AffectsRender</c>; <b>not</b> inherited).</summary>
    public static readonly StyledProperty<IBrush?> TitleForegroundProperty =
        UIProperty.Register<Border, IBrush?>(nameof(TitleForeground));

    /// <summary>The border stroke (<c>AffectsRender</c> + nullity escalation — doc §12.4).</summary>
    public static readonly StyledProperty<Pen?> BorderPenProperty =
        UIProperty.Register<Border, Pen?>(nameof(BorderPen), changed: OnBorderPenChanged, defaultValue: null);

    /// <summary>The inner padding (<c>AffectsMeasure</c>).</summary>
    public static readonly StyledProperty<Margins> PaddingProperty =
        UIProperty.Register<Border, Margins>(nameof(Padding));

    /// <summary>The title text in the top edge (<c>AffectsRender</c> + presence escalation — doc §12.4).</summary>
    public static readonly StyledProperty<string?> TitleProperty =
        UIProperty.Register<Border, string?>(nameof(Title), changed: OnTitleChanged);

    /// <summary>Placement of <see cref="Title"/> along the top edge (<c>AffectsRender</c>).</summary>
    public static readonly StyledProperty<TitlePosition> TitlePositionProperty =
        UIProperty.Register<Border, TitlePosition>(nameof(TitlePosition));

    /// <inheritdoc cref="Panel.OccludesProperty"/>
    public static readonly StyledProperty<bool> OccludesProperty =
        Panel.OccludesProperty.AddOwner<Border>();

    /// <summary>Whether a visible border is present.</summary>
    public static readonly DirectProperty<Border, bool> HasBorderProperty =
        UIProperty.RegisterDirect<Border, bool>(nameof(HasBorder), b => b.HasBorder);

    static Border()
    {
        AffectsRender<Border>(TitleForegroundProperty, BorderPenProperty, TitleProperty, TitlePositionProperty);
        AffectsMeasure<Border>(PaddingProperty, BorderPenProperty);
    }

    /// <inheritdoc cref="BackgroundProperty"/>
    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    /// <inheritdoc cref="TitleForegroundProperty"/>
    public IBrush? TitleForeground { get => GetValue(TitleForegroundProperty); set => SetValue(TitleForegroundProperty, value); }

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

    /// <inheritdoc cref="HasBorderProperty"/>
    public bool HasBorder => EvaluateHasBorder(BorderPen);

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
            var hasBorder = HasBorder;
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

        if (Child is {} child)
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

        if (Child is {} child)
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

        if (bounds.IsEffectivelyEmpty)
            return;

        var occludes = Occludes;
        var resolved = TextElement.ComposeAttributes(this);
        var attrs = resolved.Flags;

        // An effective TextElement Inverse (e.g., the caps-nocolor reverse-video cue) needs an OPAQUE
        // fill to composite the attribute onto the WHOLE face — the transparent FillRectangle tint
        // would drop it — so an attribute-bearing (or occluding) face uses FillOpaque; otherwise the
        // default transparent tint (None ⇒ the ordinary fill path, unchanged).
        var background = Background;

        // if (forceOpaque) background ??= Brushes.Default;

        // The surface: FillOpaque for a floating surface, the glyph-transparent PaintRectangle tint otherwise.
        if (background is not null)
        {
            if (occludes)
                context.FillOpaque(bounds, background, attrs, overwrite: false);
            else
                context.PaintRectangle(bounds, background, attrs, overwrite: true);
        }

        // The box: a titled frame is DrawTitledBox; a plain frame is DrawBox. An occluding surface
        // overwrites (the floating-surface recipe); a tint does not.
        PanelTitle? optionalTitle = null;

        if (Title is { Length: > 0 } title)
        {
            var panelTitle = new PanelTitle(title) { Position = TitlePosition, Attributes = attrs};
            var titleBrush = TitleForeground ?? TextElement.GetForeground(this);

            if (titleBrush is not null)
                panelTitle = panelTitle with { Brush = titleBrush };

            optionalTitle = panelTitle;
        }

        if (BorderPen is {} pen)
        {
            if (optionalTitle is {} panelTitle)
                context.DrawTitledBox(bounds, panelTitle, pen, overwrite: occludes);
            else
                context.DrawBox(bounds, pen, overwrite: occludes);
        }
        else if (optionalTitle is {} panelTitle)
        {
            // A title without an explicit pen still draws the framing edge (the GroupBox top row).
            context.DrawTitledBox(bounds, panelTitle, Pens.None, overwrite: occludes);
        }
    }

    // ───────────────────────────── nullity / presence escalation (doc §12.4) ─────────────────────────────

    private static bool EvaluateHasBorder(Pen? borderPen) => borderPen is not null;

    // ───────────────────────────── BorderPen nullity escalation (doc §12.4) ─────────────────────────────

    private static void OnBorderPenChanged(UIObject sender, Pen? oldValue, Pen? newValue)
    {
        // The hot path (a :focus pen-weight restyle, both non-null) is render-only (AffectsRender).
        // A nullity flip changes the ±1-cell border geometry, so it imperatively re-measures (C169).
        if (sender is UIElement element && oldValue.HasValue != newValue.HasValue)
        {
            element.InvalidateMeasure(); // ±1 cell/edge geometry flip (C169)

            if (element is Border border)
            {
                var hadBorder = EvaluateHasBorder(oldValue);
                var hasBorder = EvaluateHasBorder(newValue);

                if (hadBorder != hasBorder)
                    border.DispatchPropertyChanged(HasBorderProperty, null, hadBorder, hasBorder, BindingPriority.LocalValue);
            }
        }
    }

    private static void OnTitleChanged(UIObject sender, string? oldValue, string? newValue)
    {
        // A title presence flip forces/removes the top border row (C170); a text-only change is render-only.
        if (sender is Border border && (oldValue is { Length: > 0 }) != (newValue is { Length: > 0 }))
            border.InvalidateMeasure();
    }
}
