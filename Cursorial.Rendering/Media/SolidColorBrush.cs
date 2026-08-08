using Cursorial.Media;

namespace Cursorial.Rendering.Media;

/// <summary>
/// A brush that paints a single solid color (optionally at reduced opacity). Immutable by contract;
/// construction-flexible so it is XAML-element-declarable: a parameterless constructor plus init-only
/// <see cref="Color"/> / <see cref="Opacity"/> (the loader sets them after activation), alongside the
/// positional constructor and the <see cref="Color"/> → brush implicit conversion.
/// </summary>
public sealed class SolidColorBrush : IBrush
{
    private readonly double _opacity = 1.0;

    private static readonly SolidColorBrush[] Ansi16Brushes =
        Enumerable.Range(0, 16).Select(i => new SolidColorBrush(Color.FromPalette((byte) i))).ToArray();

    /// <summary>Creates a brush over the terminal-default color — for XAML element declaration; set <see cref="Color"/> / <see cref="Opacity"/> via init.</summary>
    public SolidColorBrush() {}

    /// <summary>
    /// Create a solid brush. <paramref name="opacity"/> (0–1) scales the color's alpha (RGB only —
    /// terminal-default / palette colors carry no alpha, so opacity doesn't apply to them).
    /// </summary>
    public SolidColorBrush(Color color, double opacity = 1.0)
    {
        Color = color;
        Opacity = opacity; // validated + clamped by the init accessor
    }

    /// <summary>The brush's declared color. The opacity-folded value actually painted is <see cref="ColorAt"/>.</summary>
    public Color Color { get; init; }

    /// <inheritdoc/>
    public bool IsUniform => true;

    /// <summary>
    /// Whole-brush opacity (0–1), folded into each sampled color's alpha at <see cref="ColorAt"/> (RGB
    /// only). Throws <see cref="ArgumentOutOfRangeException"/> on a non-finite value; clamps to [0, 1].
    /// </summary>
    public double Opacity
    {
        get => _opacity;
        init => _opacity = ValidateOpacity(value);
    }

    /// <inheritdoc/>
    public Color ColorAt(int column, int row, Rect bounds) => ApplyOpacity(Color, _opacity);

    /// <summary>Convenience conversion — any <see cref="Color"/> is a solid brush.</summary>
    public static implicit operator SolidColorBrush(Color color) => new(color);

    private static double ValidateOpacity(double opacity)
    {
        if (!double.IsFinite(opacity))
            throw new ArgumentOutOfRangeException(nameof(opacity), opacity, "Opacity must be a finite value.");
        return Math.Clamp(opacity, 0.0, 1.0);
    }

    private static Color ApplyOpacity(Color color, double opacity)
    {
        if (opacity >= 1.0 || color.Kind != ColorKind.Rgb) return color;
        double scaled = opacity <= 0.0 ? 0.0 : color.Alpha * opacity;
        return color.WithAlpha((byte) Math.Clamp(Math.Round(scaled), 0, 255));
    }
    
    /// <summary>Construct a palette color from a 0–255 index. Indices 0–15 are the ANSI base colors.</summary>
    public static SolidColorBrush FromPalette(int index)
    {
        if (index < 0 || index > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be between 0 and 255.");
        if (index < 16) return Ansi16Brushes[index];
        return new(Color.FromPalette((byte) index));
    }

    /// <summary>Construct a fully opaque 24-bit truecolor value.</summary>
    public static SolidColorBrush FromRgb(byte red, byte green, byte blue)
        => new(Color.FromRgb(red, green, blue));

    /// <summary>Construct a 24-bit truecolor value from a hex code.</summary>
    public static SolidColorBrush FromHex(in ReadOnlySpan<char> hexCode)
        => new(Color.FromHex(hexCode));

    /// <summary>
    /// Construct a 24-bit truecolor value with an explicit alpha channel. <paramref name="alpha"/>
    /// of 255 is fully opaque (equivalent to <see cref="FromRgb"/>); 0 is fully transparent
    /// (compositing returns the backdrop unchanged). Intermediate values mix the blended source
    /// color with the backdrop linearly.
    /// </summary>
    public static SolidColorBrush FromRgba(byte red, byte green, byte blue, byte alpha)
        => new(Color.FromRgba(red, green, blue, alpha));

    /// <summary>
    /// Construct a 24-bit truecolor value from its HSV components with an explicit alpha channel.
    /// <paramref name="alpha"/> of 255 is fully opaque (equivalent to <see cref="FromRgb"/>); 0 is
    /// fully transparent (compositing returns the backdrop unchanged). Intermediate values mix the
    /// blended source color with the backdrop linearly.
    /// </summary>
    public static SolidColorBrush FromHsv(double hue, double saturation, double value, byte alpha = 255)
        => new(Color.FromHsv(hue, saturation, value, alpha));
}
