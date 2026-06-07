using Cursorial.Output;

namespace Cursorial.Drawing;

/// <summary>
/// A color <em>source</em> for the drawing layer: in Phase 1 a single solid <see cref="Color"/>
/// (with optional opacity folded into its alpha); the linear / radial / conic gradient kinds arrive
/// with gradient sampling in Phase 2. A <see cref="Brush"/> is a small <see langword="readonly"/>
/// value type, mirroring <see cref="Color"/>; the implicit <see cref="Color"/> → <see cref="Brush"/>
/// conversion is allocation-free, so every existing color-passing call site flows into a brush
/// parameter unchanged.
/// </summary>
/// <remarks>
/// A <see cref="Brush"/> never enters <see cref="Style"/> or a cell — it is resolved to a scalar
/// <see cref="Color"/> at draw time (a terminal cell shows one solid color). <c>default(Brush)</c>
/// is a solid brush over <see cref="Color.Default"/> (opaque), so the struct default is a sensible
/// no-op rather than an invisible one.
/// </remarks>
public readonly record struct Brush
{
    private readonly Color _solid;   // meaningful when Kind == Solid; default(Color) == Color.Default

    private Brush(BrushKind kind, Color solid)
    {
        Kind = kind;
        _solid = solid;
    }

    /// <summary>The color-source discriminator.</summary>
    public BrushKind Kind { get; }

    /// <summary>True for a solid-color brush.</summary>
    public bool IsSolid => Kind == BrushKind.Solid;

    /// <summary>The solid color (meaningful only when <see cref="IsSolid"/>).</summary>
    public Color SolidColor => _solid;

    /// <summary>A solid brush over <see cref="Color.Default"/> — the no-op default.</summary>
    public static Brush Default => default;

    /// <summary>True when this is the default no-op brush (solid, default color, fully opaque).</summary>
    public bool IsDefault => Kind == BrushKind.Solid && _solid.IsDefault;

    /// <summary>A solid-color brush.</summary>
    public static Brush Solid(Color color) => new(BrushKind.Solid, color);

    /// <summary>
    /// A solid-color brush at reduced opacity. Opacity in [0, 1] scales the color's alpha (RGB only;
    /// the terminal-default / palette kinds carry no alpha, so opacity does not apply to them).
    /// </summary>
    public static Brush Solid(Color color, double opacity) => new(BrushKind.Solid, ApplyOpacity(color, opacity));

    /// <summary>Allocation-free implicit conversion — any <see cref="Color"/> is a solid brush.</summary>
    public static implicit operator Brush(Color color) => new(BrushKind.Solid, color);

    /// <summary>
    /// Resolve the brush to the solid color for the cell at <paramref name="column"/>,
    /// <paramref name="row"/> within <paramref name="extent"/>. Solid brushes are
    /// position-independent (the extent is ignored); gradient sampling — which uses the extent and
    /// the cell center — arrives in Phase 2.
    /// </summary>
    public Color Sample(int column, int row, in BrushExtent extent)
        => Kind == BrushKind.Solid
               ? _solid
               : throw new NotSupportedException("Gradient brush sampling is not implemented until Phase 2.");

    private static Color ApplyOpacity(Color color, double opacity)
    {
        if (opacity >= 1.0) return color;
        if (color.Kind != ColorKind.Rgb) return color;   // default / palette carry no alpha
        double scaled = opacity <= 0.0 ? 0.0 : color.Alpha * opacity;
        return color.WithAlpha((byte) Math.Clamp(Math.Round(scaled), 0, 255));
    }
}
