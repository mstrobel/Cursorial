using Cursorial.Drawing;
using Cursorial.Output;

namespace Cursorial.UI.Controls;

/// <summary>
/// A decorator that adds a drop shadow to its child element.
/// </summary>
public class ShadowChrome : Decorator
{
    /// <summary>
    /// Cells the soft fringe fades across, beyond the offset-displaced silhouette. Clamped to ≥ 0
    /// (the offset is the crisp displacement; the radius is the soft edge).
    /// </summary>
    public static readonly StyledProperty<int> RadiusProperty =
        UIProperty.Register<ShadowChrome, int>(nameof(Radius));

    /// <summary>
    /// Column offset of a drop shadow's cast direction (e.g., +1 = light from the upper-left).
    /// </summary>
    public static readonly StyledProperty<int> OffsetColumnProperty =
        UIProperty.Register<ShadowChrome, int>(nameof(OffsetColumn), 1);

    /// <summary>Row offset of a drop shadow's cast direction.</summary>
    public static readonly StyledProperty<int> OffsetRowProperty =
        UIProperty.Register<ShadowChrome, int>(nameof(OffsetRow), 1);

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
        AffectsRender<ShadowChrome>(RadiusProperty, OffsetColumnProperty, OffsetRowProperty, StrengthProperty, EdgesProperty, ShadowColorProperty);
    }

    /// <inheritdoc cref="RadiusProperty"/>
    public int Radius
    {
        get => GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    /// <inheritdoc cref="OffsetColumnProperty"/>
    public int OffsetColumn
    {
        get => GetValue(OffsetColumnProperty);
        set => SetValue(OffsetColumnProperty, value);
    }

    /// <inheritdoc cref="OffsetRowProperty"/>
    public int OffsetRow
    {
        get => GetValue(OffsetRowProperty);
        set => SetValue(OffsetRowProperty, value);
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

    protected override void Render(RenderContext context)
    {
        base.Render(context);

        var geometry = new ShadowGeometry
                       {
                           Edges = Edges,
                           Radius = Radius,
                           OffsetColumn = OffsetColumn,
                           OffsetRow = OffsetRow,
                           Strength = Strength
                       };

        context.DrawDropShadow(context.Bounds, in geometry, ShadowColor);
    }
}