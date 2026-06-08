using Cursorial.Output;

namespace Cursorial.Animation;

/// <summary>
/// Premultiplied-sRGB interpolation between two <see cref="Color"/> values, delegating to
/// <see cref="Color.Lerp(Color, Color, double)"/> — the same blend gradients use, so an animated
/// color matches exactly how it would appear sampled across a gradient. Palette / default colors snap to
/// the nearer endpoint (they can't blend); RGB blends with alpha awareness. Stateless singleton.
/// </summary>
public sealed class ColorInterpolator : IInterpolator<Color>
{
    /// <summary>The shared instance.</summary>
    public static ColorInterpolator Instance { get; } = new();

    private ColorInterpolator() { }

    /// <inheritdoc/>
    public Color Interpolate(Color from, Color to, double progress) => Color.Lerp(from, to, progress);
}
