namespace Cursorial.Drawing;

/// <summary>
/// The geometry of a soft shadow: how far it reaches, which edges cast it, its drop offset, and its peak
/// strength. A <c>readonly record struct</c> matching the <c>Pen</c> / <c>GradientStop</c> value-type idiom.
/// </summary>
public readonly record struct ShadowGeometry
{
    /// <summary>Cells the soft fringe fades across, beyond the offset-displaced silhouette. Clamped to ≥ 0
    /// (the offset is the crisp displacement; the radius is the soft edge).</summary>
    public int Radius { get; init; }

    /// <summary>Column offset of a drop shadow's cast direction (e.g. +1 = light from the upper-left). Ignored by inner shadows.</summary>
    public int OffsetColumn { get; init; }

    /// <summary>Row offset of a drop shadow's cast direction. Ignored by inner shadows.</summary>
    public int OffsetRow { get; init; }

    /// <summary>Peak opacity at the casting edge (0–1); alpha falls off linearly to 0 across <see cref="Radius"/>. Default 0.5.</summary>
    public double Strength { get; init; }

    /// <summary>Which element edges cast the shadow. Default <see cref="ShadowEdges.All"/>.</summary>
    public ShadowEdges Edges { get; init; }

    /// <summary>A conventional drop shadow: <paramref name="radius"/> cells, cast lower-right by (<paramref name="offset"/>, <paramref name="offset"/>), from <paramref name="edges"/>.</summary>
    public static ShadowGeometry Drop(int radius = 0, int offset = 1, double strength = 0.5, ShadowEdges edges = ShadowEdges.All) =>
        new() { Radius = radius, OffsetColumn = offset, OffsetRow = offset, Strength = strength, Edges = edges };

    /// <summary>A conventional inner shadow: <paramref name="radius"/> cells inward from <paramref name="edges"/>.</summary>
    public static ShadowGeometry Inner(int radius = 1, double strength = 0.5, ShadowEdges edges = ShadowEdges.All) =>
        new() { Radius = radius, OffsetColumn = 0, OffsetRow = 0, Strength = strength, Edges = edges };
}
