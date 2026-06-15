using Cursorial.Animation;
using Cursorial.Rendering;

// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// Linear interpolation between two <see cref="Margins"/> — each side rounded to the nearest cell (ties
/// away from zero) and <b>signed</b> (no zero-clamp), since margins are signed per matrix LD19 and a track
/// may legitimately interpolate through negative side values (pull-up / overlap layouts). The signed
/// sibling of the clamped <see cref="SizeInterpolator"/>; lives in <c>Cursorial.Drawing</c> beside the
/// <c>Size</c>/<c>Rect</c> family so <c>Cursorial.Animation</c> needn't depend on Rendering. Stateless singleton.
/// </summary>
public sealed class MarginsInterpolator : IInterpolator<Margins>
{
    /// <summary>The shared instance.</summary>
    public static MarginsInterpolator Instance { get; } = new();

    private MarginsInterpolator() { }

    /// <inheritdoc/>
    public Margins Interpolate(Margins from, Margins to, double progress) =>
        new(RoundSigned(from.Left, to.Left, progress),
            RoundSigned(from.Top, to.Top, progress),
            RoundSigned(from.Right, to.Right, progress),
            RoundSigned(from.Bottom, to.Bottom, progress));

    // Rounded (ties away from zero), signed — unlike the size/geometry family, margins are NOT clamped ≥ 0.
    private static int RoundSigned(int from, int to, double progress) =>
        (int)System.Math.Round(from + (to - from) * progress, System.MidpointRounding.AwayFromZero);
}
