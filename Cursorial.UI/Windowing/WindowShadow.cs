using System;

using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering;

// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>
/// A window/popup drop shadow (design doc §8.2/§8.7): the soft-shadow <see cref="ShadowGeometry"/> plus its
/// color. The surface grows by <see cref="GetMargins"/> beyond its content rect so the shadow has cells to
/// paint into (drawn before content, as the lowest layer). RGB-only — a no-op on palette themes (W5 gates
/// emission on <c>caps-truecolor</c>). A <c>readonly record struct</c> matching the value-type idiom.
/// </summary>
public readonly record struct WindowShadow(ShadowGeometry Geometry, Color Color)
{
    /// <summary>No shadow (the default).</summary>
    public static WindowShadow None => default;

    /// <summary>The canonical window shadow — a 1-cell soft drop cast lower-right, half-strength opaque black.
    /// S8's chrome cites this rather than inlining geometry (design doc §8.2).</summary>
    public static WindowShadow Default { get; } =
        new(ShadowGeometry.Drop(radius: 1, offset: 1, strength: 0.5), Color.FromRgb(0, 0, 0));

    /// <summary>True when there is no shadow geometry to paint.</summary>
    public bool IsNone => Geometry == default;

    /// <summary>
    /// The per-edge cells the surface grows beyond its content so the shadow can paint (the soft fringe plus
    /// the drop displacement on the cast edges). W5 finalizes shadow rastering; this is the surface-growth
    /// estimate the placement/clip math uses.
    /// </summary>
    public Margins GetMargins()
    {
        if (IsNone)
            return default;

        var radius = Math.Max(0, Geometry.Radius);
        var left = Math.Max(0, radius - Geometry.OffsetColumn);
        var top = Math.Max(0, radius - Geometry.OffsetRow);
        var right = Math.Max(0, Geometry.OffsetColumn + radius);
        var bottom = Math.Max(0, Geometry.OffsetRow + radius);
        return new Margins(left, top, right, bottom);
    }
}
