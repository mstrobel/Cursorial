// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// The geometry of a soft shadow: how far it reaches, which edges cast it, and its peak strength. A
/// <c>readonly record struct</c> matching the <c>Pen</c> / <c>GradientStop</c> value-type idiom.
/// </summary>
public readonly record struct ShadowGeometry
{
    /// <summary>Cells the soft shadow reaches across the casting edges, fading to nothing at the rim. Clamped
    /// to ≥ 0 (0 = no shadow). The vertical reach is about half this, since terminal cells are ~2× tall.</summary>
    public int Radius { get; init; }

    /// <summary>Peak opacity at the casting edge (0–1); alpha falls off linearly to 0 across <see cref="Radius"/>. Default 0.5.</summary>
    public double Strength { get; init; }

    /// <summary>Which element edges cast the shadow. Default <see cref="ShadowEdges.All"/>.</summary>
    public ShadowEdges Edges { get; init; }

    /// <summary>A conventional drop shadow: <paramref name="radius"/> cells soft, cast from <paramref name="edges"/>.</summary>
    public static ShadowGeometry Drop(int radius = 1, double strength = 0.5, ShadowEdges edges = ShadowEdges.All)
        => new() { Radius = radius, Strength = strength, Edges = edges };

    /// <summary>A conventional inner shadow: <paramref name="radius"/> cells inward from <paramref name="edges"/>.</summary>
    public static ShadowGeometry Inner(int radius = 1, double strength = 0.5, ShadowEdges edges = ShadowEdges.All)
        => new() { Radius = radius, Strength = strength, Edges = edges };
}
