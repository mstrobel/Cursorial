using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Drawing.Media;

/// <summary>A brush that paints a single solid color (optionally at reduced opacity).</summary>
public sealed class SolidColorBrush : IBrush
{
    private readonly Color _color;

    /// <summary>
    /// Create a solid brush. <paramref name="opacity"/> (0–1) scales the color's alpha (RGB only —
    /// terminal-default / palette colors carry no alpha, so opacity doesn't apply to them).
    /// </summary>
    public SolidColorBrush(Color color, double opacity = 1.0)
    {
        if (!double.IsFinite(opacity))
            throw new ArgumentOutOfRangeException(nameof(opacity), opacity, "Opacity must be a finite value.");

        _color = ApplyOpacity(color, Math.Clamp(opacity, 0.0, 1.0));
    }

    /// <summary>The (opacity-folded) color this brush paints.</summary>
    public Color Color => _color;

    /// <inheritdoc/>
    public Color ColorAt(int column, int row, Rect bounds) => _color;

    /// <summary>Convenience conversion — any <see cref="Color"/> is a solid brush.</summary>
    public static implicit operator SolidColorBrush(Color color) => new(color);

    private static Color ApplyOpacity(Color color, double opacity)
    {
        if (opacity >= 1.0 || color.Kind != ColorKind.Rgb) return color;
        double scaled = opacity <= 0.0 ? 0.0 : color.Alpha * opacity;
        return color.WithAlpha((byte) Math.Clamp(Math.Round(scaled), 0, 255));
    }
}
