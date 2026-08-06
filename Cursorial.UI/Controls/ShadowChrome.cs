using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Rendering;

namespace Cursorial.UI.Controls;

/// <summary>
/// A decorator that adds a drop shadow to its child element.
/// </summary>
public class ShadowChrome : Decorator
{
    /// <summary>
    /// Cells the soft shadow reaches across the casting edges, fading to nothing at the rim. Clamped to ≥ 0
    /// (0 = no shadow). The vertical reach is about half this, since terminal cells are ~2× tall.
    /// </summary>
    public static readonly StyledProperty<int> RadiusProperty =
        UIProperty.Register<ShadowChrome, int>(nameof(Radius), 1);

    /// <summary>Peak opacity at the casting edge (0–1); alpha falls off linearly to 0 across <see cref="Radius"/>. Default 0.5.</summary>
    public static readonly StyledProperty<double> StrengthProperty =
        UIProperty.Register<ShadowChrome, double>(nameof(Strength), 0.5,
                                                  coerce: (_, v) => Math.Clamp(v, 0.0, 1.0));

    /// <summary>
    /// Which element edges cast the shadow. Default <c><see cref="ShadowEdges.Left"/> | <see cref="ShadowEdges.Right"/></c>.
    /// </summary>
    public static readonly StyledProperty<ShadowEdges> EdgesProperty =
        UIProperty.Register<ShadowChrome, ShadowEdges>(nameof(Edges), ShadowEdges.Left | ShadowEdges.Right);

    /// <summary>Color of the shadow. Default <see cref="Colors.Black"/>.</summary>
    public static readonly StyledProperty<Color> ShadowColorProperty =
        UIProperty.Register<ShadowChrome, Color>(nameof(ShadowColor), Colors.TrueBlack);


    static ShadowChrome()
    {
        AffectsRender<ShadowChrome>(RadiusProperty, StrengthProperty, EdgesProperty, ShadowColorProperty);
        AffectsMeasure<ShadowChrome>(RadiusProperty, EdgesProperty);
    }

    /// <inheritdoc cref="RadiusProperty"/>
    public int Radius
    {
        get => GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    /// <inheritdoc cref="StrengthProperty"/>
    public double Strength
    {
        get => GetValue(StrengthProperty);
        set => SetValue(StrengthProperty, value);
    }

    /// <inheritdoc cref="EdgesProperty"/>
    public ShadowEdges Edges
    {
        get => GetValue(EdgesProperty);
        set => SetValue(EdgesProperty, value);
    }

    /// <inheritdoc cref="ShadowColorProperty"/>
    public Color ShadowColor
    {
        get => GetValue(ShadowColorProperty);
        set => SetValue(ShadowColorProperty, value);
    }

    // The space the shadow needs beyond the child on each casting edge: the full radius horizontally, and half
    // (rounded up) vertically — terminal cells are ~2× tall, so the shadow reaches fewer rows than columns.
    private Margins Inset
    {
        get
        {
            int rv = (Radius + 1) / 2;
            var left = Edges.HasFlag(ShadowEdges.Left) ? Radius : 0;
            var right = Edges.HasFlag(ShadowEdges.Right) ? Radius : 0;
            var top = Edges.HasFlag(ShadowEdges.Top) ? rv : 0;
            var bottom = Edges.HasFlag(ShadowEdges.Bottom) ? rv : 0;
            return new Margins(left, top, right, bottom);
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

    protected override void Render(RenderContext context)
    {
        base.Render(context);

        var geometry = new ShadowGeometry { Edges = Edges, Radius = Radius, Strength = Strength };

        context.DrawDropShadow(context.Bounds, in geometry, ShadowColor);
    }
}