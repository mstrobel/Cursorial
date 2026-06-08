namespace Cursorial.Drawing;

/// <summary>Shared helpers for the point-series charts (scatter / line).</summary>
internal static class ChartMath
{
    public static bool Finite(PointD p) => double.IsFinite(p.X) && double.IsFinite(p.Y);

    /// <summary>Resolve X/Y ranges — explicit if given, else auto from the points (non-finite skipped).</summary>
    public static (AxisRange X, AxisRange Y) AutoRange(IReadOnlyList<PointD> points, AxisRange? x, AxisRange? y)
    {
        if (x is { } xr && y is { } yr)
            return (xr, yr);

        var xs = new double[points.Count];
        var ys = new double[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            xs[i] = points[i].X;
            ys[i] = points[i].Y;
        }
        return (x ?? AxisRange.FromValues(xs), y ?? AxisRange.FromValues(ys));
    }

    public static string MarkerGlyph(MarkerStyle marker) => marker switch
    {
        MarkerStyle.Circle   => "○",
        MarkerStyle.Square   => "■",
        MarkerStyle.Diamond  => "◆",
        MarkerStyle.Triangle => "▲",
        MarkerStyle.Cross    => "✕",
        _                    => "●",   // Dot
    };
}
