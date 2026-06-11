using Cursorial.Animation;

// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// Interpolates a scene's <see cref="CompositeParameters"/> — the per-frame target for sliding
/// (<see cref="CompositeParameters.OffsetColumn"/>/<see cref="CompositeParameters.OffsetRow"/>) and
/// fading (<see cref="CompositeParameters.Opacity"/>) a <b>cached</b> scene without re-rasterizing it.
/// </summary>
/// <remarks>
/// Offsets and opacity blend numerically (rounded to the nearest cell / alpha byte). The clip rectangle
/// and blend mode aren't continuously interpolable, so they snap at the midpoint (<c>from</c>'s
/// when <c>progress &lt; 0.5</c>, else <c>to</c>'s) — typically identical across both endpoints
/// of a slide/fade anyway. Stateless singleton.
/// </remarks>
public sealed class CompositeParametersInterpolator : IInterpolator<CompositeParameters>
{
    /// <summary>The shared instance.</summary>
    public static CompositeParametersInterpolator Instance { get; } = new();

    private CompositeParametersInterpolator() { }

    /// <inheritdoc/>
    public CompositeParameters Interpolate(CompositeParameters from, CompositeParameters to, double progress)
    {
        int offsetColumn = (int) Math.Round(from.OffsetColumn + (to.OffsetColumn - from.OffsetColumn) * progress);
        int offsetRow = (int) Math.Round(from.OffsetRow + (to.OffsetRow - from.OffsetRow) * progress);
        byte opacity = (byte) Math.Clamp(Math.Round(from.Opacity + (to.Opacity - from.Opacity) * progress), 0, 255);

        var discrete = progress < 0.5 ? from : to;   // clip + blend mode snap (not continuously interpolable)
        return new CompositeParameters(offsetColumn, offsetRow, opacity, discrete.Clip, discrete.Mode);
    }
}
