using Cursorial.Rendering;

// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// Maps a chart's value space to sub-cell device coordinates within a plot <see cref="Rect"/>. The
/// Y-flip (value-space Y increases upward; screen rows increase downward) is isolated here — the one
/// place it happens — to kill the classic "everything is upside-down" bug. Sub-resolution is
/// <see cref="SubX"/> × <see cref="SubY"/> per cell (2 × 4 for braille, 1 × 1 for whole-cell markers).
/// </summary>
internal readonly record struct PlotProjector(Rect Plot, AxisRange X, AxisRange Y, int SubX, int SubY)
{
    /// <summary>Project a value point to absolute (scene) sub-cell coordinates, Y flipped.</summary>
    public (int SubColumn, int SubRow) ToSub(double vx, double vy)
    {
        int subWidth = Plot.Columns * SubX;
        int subHeight = Plot.Rows * SubY;

        int sx = (int) Math.Round(Math.Clamp(X.Normalize(vx), 0.0, 1.0) * (subWidth - 1));
        int sy = (int) Math.Round((1.0 - Math.Clamp(Y.Normalize(vy), 0.0, 1.0)) * (subHeight - 1));   // flip

        return (Plot.Column * SubX + sx, Plot.Row * SubY + sy);
    }

    /// <summary>Project a value point to an absolute cell (sub-cell truncated to whole cells).</summary>
    public (int Column, int Row) ToCell(double vx, double vy)
    {
        var (subColumn, subRow) = ToSub(vx, vy);
        return (subColumn / SubX, subRow / SubY);
    }
}
