using Cursorial.Output;

namespace Cursorial.Drawing;

/// <summary>
/// A color <em>source</em> for the drawing layer: a single solid <see cref="Color"/> (with optional
/// opacity folded into its alpha) or a linear / radial / conic gradient. A <see cref="Brush"/> is a
/// small <see langword="readonly"/> value type, mirroring <see cref="Color"/>; the implicit
/// <see cref="Color"/> → <see cref="Brush"/> conversion is allocation-free, so every existing
/// color-passing call site flows into a brush parameter unchanged. The gradient payload sits behind
/// one reference (<see cref="GradientData"/>), so the solid path stays inline / zero-allocation.
/// </summary>
/// <remarks>
/// A <see cref="Brush"/> never enters <see cref="Style"/> or a cell — it is resolved to a scalar
/// <see cref="Color"/> at draw time (a terminal cell shows one solid color). <c>default(Brush)</c>
/// is a solid brush over <see cref="Color.Default"/> (opaque), so the struct default is a sensible
/// no-op rather than an invisible one. To fill many cells with a gradient, build a
/// <see cref="GradientSampler"/> once and reuse it (the drawing layer does this); the gradient
/// overload of <see cref="Sample"/> is a convenience that builds a sampler per call.
/// </remarks>
public readonly record struct Brush
{
    private readonly Color _solid;             // meaningful when Kind == Solid; default(Color) == Color.Default
    private readonly GradientData? _gradient;  // non-null when Kind != Solid

    private Brush(BrushKind kind, Color solid, GradientData? gradient)
    {
        Kind = kind;
        _solid = solid;
        _gradient = gradient;
    }

    /// <summary>The color-source discriminator.</summary>
    public BrushKind Kind { get; }

    /// <summary>True for a solid-color brush.</summary>
    public bool IsSolid => Kind == BrushKind.Solid;

    /// <summary>The solid color (meaningful only when <see cref="IsSolid"/>).</summary>
    public Color SolidColor => _solid;

    /// <summary>The gradient definition (non-null only for gradient brushes).</summary>
    public GradientData? Gradient => _gradient;

    /// <summary>A solid brush over <see cref="Color.Default"/> — the no-op default.</summary>
    public static Brush Default => default;

    /// <summary>True when this is the default no-op brush (solid, default color, fully opaque).</summary>
    public bool IsDefault => Kind == BrushKind.Solid && _solid.IsDefault;

    /// <summary>A solid-color brush.</summary>
    public static Brush Solid(Color color) => new(BrushKind.Solid, color, null);

    /// <summary>
    /// A solid-color brush at reduced opacity. Opacity in [0, 1] scales the color's alpha (RGB only;
    /// the terminal-default / palette kinds carry no alpha, so opacity does not apply to them).
    /// </summary>
    public static Brush Solid(Color color, double opacity) => new(BrushKind.Solid, ApplyOpacity(color, opacity), null);

    /// <summary>A gradient brush from a full <see cref="GradientData"/> definition.</summary>
    public static Brush FromGradient(GradientData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new Brush(MapKind(data.Kind), default, data);
    }

    /// <summary>A linear gradient (left→right across the extent by default).</summary>
    public static Brush LinearGradient(IReadOnlyList<GradientStop> stops, GradientSpread spread = GradientSpread.Pad,
                                       double opacity = 1.0,
                                       double startColumn = 0.0, double startRow = 0.0,
                                       double endColumn = 1.0, double endRow = 0.0)
        => FromGradient(new GradientData(GradientKind.Linear, stops, spread, opacity,
                                         startColumn, startRow, endColumn, endRow));

    /// <summary>A radial gradient (centered, filling the extent as an ellipse by default).</summary>
    public static Brush RadialGradient(IReadOnlyList<GradientStop> stops, GradientSpread spread = GradientSpread.Pad,
                                       double opacity = 1.0)
        => FromGradient(new GradientData(GradientKind.Radial, stops, spread, opacity));

    /// <summary>
    /// A conic gradient (a full 360° sweep around the center). Conic gradients always wrap, so
    /// <see cref="GradientSpread"/> does not apply to them.
    /// </summary>
    public static Brush ConicGradient(IReadOnlyList<GradientStop> stops, double opacity = 1.0, double angleDegrees = 0.0)
        => FromGradient(new GradientData(GradientKind.Conic, stops, GradientSpread.Pad, opacity, angleDegrees: angleDegrees));

    /// <summary>Allocation-free implicit conversion — any <see cref="Color"/> is a solid brush.</summary>
    public static implicit operator Brush(Color color) => new(BrushKind.Solid, color, null);

    /// <summary>
    /// Resolve the brush to a solid color for the cell at <paramref name="column"/>,
    /// <paramref name="row"/> within <paramref name="extent"/>. Solid brushes ignore the extent;
    /// gradient brushes build a <see cref="GradientSampler"/> per call — to fill a region, build a
    /// sampler once and call it per cell instead.
    /// </summary>
    public Color Sample(int column, int row, in BrushExtent extent)
        => Kind == BrushKind.Solid
               ? _solid
               : new GradientSampler(_gradient!, extent).Sample(column, row);

    private static BrushKind MapKind(GradientKind kind) =>
        kind switch
        {
            GradientKind.Linear => BrushKind.Linear,
            GradientKind.Radial => BrushKind.Radial,
            GradientKind.Conic  => BrushKind.Conic,
            _                   => BrushKind.Solid
        };

    private static Color ApplyOpacity(Color color, double opacity)
    {
        if (opacity >= 1.0) return color;
        if (color.Kind != ColorKind.Rgb) return color;   // default / palette carry no alpha
        double scaled = opacity <= 0.0 ? 0.0 : color.Alpha * opacity;
        return color.WithAlpha((byte) Math.Clamp(Math.Round(scaled), 0, 255));
    }
}
