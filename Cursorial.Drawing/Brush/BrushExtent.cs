using Cursorial.Rendering;

namespace Cursorial.Drawing;

/// <summary>
/// The logical, un-clipped region a <see cref="Brush"/> maps its color source onto — the
/// gradient's bounding box in cell coordinates. Unlike <see cref="Rect"/> (which is
/// <see langword="ushort"/>-backed and throws on a negative origin), a brush extent may have a
/// negative origin or extend past the buffer, so a gradient anchored partly off-screen, or larger
/// than its clip, samples correctly — the cell's position is taken relative to this extent, never
/// the clip. Coordinates are fractional cells.
/// </summary>
/// <param name="Column">Left edge, in cells. May be negative.</param>
/// <param name="Row">Top edge, in cells. May be negative.</param>
/// <param name="Columns">Width, in cells.</param>
/// <param name="Rows">Height, in cells.</param>
public readonly record struct BrushExtent(double Column, double Row, double Columns, double Rows)
{
    /// <summary>Horizontal center of the extent.</summary>
    public double CenterColumn => Column + Columns * 0.5;

    /// <summary>Vertical center of the extent.</summary>
    public double CenterRow => Row + Rows * 0.5;

    /// <summary>Build an extent from a (non-negative) cell rectangle.</summary>
    public static BrushExtent FromRect(in Rect rect) => new(rect.Column, rect.Row, rect.Columns, rect.Rows);
}
