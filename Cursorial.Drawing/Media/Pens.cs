namespace Cursorial.Drawing.Media;

/// <summary>
/// Predefined <see cref="Pen"/> presets at the terminal's default foreground — the stroke-attribute
/// counterparts that compose with a color at the call site (<c>Pens.Heavy.WithColor(Colors.Red)</c>,
/// or a draw method's <see cref="Output.Color"/> overload). Deliberately keyed on attribute presets,
/// not color (color rides the per-call argument), so this is not a one-to-one mirror of
/// <see cref="Brushes"/>.
/// </summary>
public static class Pens
{
    /// <summary>Light, sharp, solid — equivalent to <c>default(Pen)</c>.</summary>
    public static Pen Light => default;

    /// <summary>A heavy-weight pen.</summary>
    public static Pen Heavy { get; } = default(Pen) with { Weight = StrokeWeight.Heavy };

    /// <summary>A double-line pen.</summary>
    public static Pen Double { get; } = default(Pen) with { Weight = StrokeWeight.Double };

    /// <summary>A light pen with rounded corners.</summary>
    public static Pen Rounded { get; } = default(Pen) with { Corners = CornerStyle.Rounded };

    /// <summary>A light pen with a triple-dash stroke.</summary>
    public static Pen Dashed { get; } = default(Pen) with { Dash = LineDash.Triple };

    /// <summary>A light pen that renders with the ASCII glyph set (<c>- | +</c>).</summary>
    public static Pen Ascii { get; } = default(Pen) with { GlyphSet = GlyphSet.Ascii };
}
