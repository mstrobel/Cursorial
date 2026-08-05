using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering;

// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>
/// A window/popup drop shadow (design doc §8.2/§8.7): the soft-shadow <see cref="ShadowGeometry"/> plus its
/// color. The surface grows by <see cref="GetMargins"/> beyond its content rect so the shadow has cells to
/// paint into (drawn before content, as the lowest layer). RGB-only — a no-op on palette themes: emission is
/// gated in code (<c>TopLevelSurface.ShadowsEnabled</c>) on the RGB tiers, <see cref="ColorDepth.Ansi256"/>
/// and up, not by a theme capability class. A <c>readonly record struct</c> matching the value-type idiom.
/// </summary>
public readonly record struct WindowShadow(ShadowGeometry Geometry, Color Color)
{
    /// <summary>No shadow (the default).</summary>
    public static WindowShadow None => default;

    /// <summary>The canonical window shadow — a soft drop from the bottom and right edges at half strength. S8's
    /// chrome cites this rather than inlining geometry (design doc §8.2).</summary>
    public static WindowShadow Default { get; } =
        new(ShadowGeometry.Drop(
                radius: 2,
                strength: 0.5,
                edges: ShadowEdges.Bottom | ShadowEdges.Right),
            Color.FromRgb(0, 0, 0));

    /// <summary>True when there is no shadow geometry to paint.</summary>
    public bool IsNone => Geometry == default;

    /// <summary>
    /// The per-edge cells the surface grows beyond its content so the shadow has room to paint: the full
    /// <see cref="ShadowGeometry.Radius"/> on a casting left/right edge, and about half that (rounded up) on a
    /// casting top/bottom edge — terminal cells are ~2× tall, so the shadow reaches fewer rows than columns.
    /// </summary>
    public Margins GetMargins()
    {
        if (IsNone)
            return default;

        var radius = Math.Max(0, Geometry.Radius);
        var rv = (radius + 1) / 2;
        var edges = Geometry.Edges;
        var left = edges.HasFlag(ShadowEdges.Left) ? radius : 0;
        var right = edges.HasFlag(ShadowEdges.Right) ? radius : 0;
        var top = edges.HasFlag(ShadowEdges.Top) ? rv : 0;
        var bottom = edges.HasFlag(ShadowEdges.Bottom) ? rv : 0;
        return new Margins(left, top, right, bottom);
    }
}
