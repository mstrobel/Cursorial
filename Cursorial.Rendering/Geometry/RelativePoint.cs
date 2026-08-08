// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// A point expressed as a fraction of an element's bounds: <c>(0, 0)</c> is the top-left corner and
/// <c>(1, 1)</c> the bottom-right. <see cref="X"/> runs left→right across the width, <see cref="Y"/>
/// top→bottom down the height. The typical range is <c>[0, 1]</c>, but values outside are perfectly
/// valid — the point simply lies outside the box — mirroring WPF / Avalonia's relative point. This is
/// resolution-independent geometry: it carries no cell coordinates (those are integer columns / rows),
/// only a proportion of whatever bounds it is later resolved against.
/// </summary>
public readonly record struct RelativePoint(double X, double Y)
{
    /// <summary>The top-left corner, <c>(0, 0)</c>.</summary>
    public static readonly RelativePoint TopLeft = new(0.0, 0.0);

    /// <summary>The top edge's midpoint, <c>(0.5, 0)</c>.</summary>
    public static readonly RelativePoint Top = new(0.5, 0.0);

    /// <summary>The top-right corner, <c>(1, 0)</c>.</summary>
    public static readonly RelativePoint TopRight = new(1.0, 0.0);

    /// <summary>The left edge's midpoint, <c>(0, 0.5)</c>.</summary>
    public static readonly RelativePoint Left = new(0.0, 0.5);

    /// <summary>The center, <c>(0.5, 0.5)</c>.</summary>
    public static readonly RelativePoint Center = new(0.5, 0.5);

    /// <summary>The right edge's midpoint, <c>(1, 0.5)</c>.</summary>
    public static readonly RelativePoint Right = new(1.0, 0.5);

    /// <summary>The bottom-left corner, <c>(0, 1)</c>.</summary>
    public static readonly RelativePoint BottomLeft = new(0.0, 1.0);

    /// <summary>The bottom edge's midpoint, <c>(0.5, 1)</c>.</summary>
    public static readonly RelativePoint Bottom = new(0.5, 1.0);

    /// <summary>The bottom-right corner, <c>(1, 1)</c>.</summary>
    public static readonly RelativePoint BottomRight = new(1.0, 1.0);

    /// <summary>Convenience conversion from an <c>(x, y)</c> tuple.</summary>
    public static implicit operator RelativePoint((double X, double Y) point) => new(point.X, point.Y);
}
