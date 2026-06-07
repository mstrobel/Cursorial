using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Drawing;

/// <summary>
/// Base class for gradient brushes. Holds the (sorted) stops, spread, and opacity, and owns the
/// shared per-cell color resolution: each subclass maps a cell to a gradient parameter <c>t</c>
/// (<see cref="ComputeOffset"/>), then this class applies the spread and interpolates the stops in
/// <b>premultiplied sRGB</b> (un-premultiplied to a straight <see cref="Color"/>, so a fade to a
/// transparent stop keeps the neighbor's hue and composes correctly through the existing
/// straight-alpha compositing). Sampled directly per cell — no lookup table; terminal cell counts
/// are small and full-precision sampling avoids the banding a 256-entry ramp would introduce.
/// </summary>
public abstract class GradientBrush : IBrush
{
    private protected const double Epsilon = 1e-9;

    protected GradientBrush(IReadOnlyList<GradientStop> stops, GradientSpread spread, double opacity)
    {
        ArgumentNullException.ThrowIfNull(stops);
        if (stops.Count == 0)
            throw new ArgumentException("A gradient requires at least one stop.", nameof(stops));
        if (!double.IsFinite(opacity))
            throw new ArgumentOutOfRangeException(nameof(opacity), opacity, "Opacity must be a finite value.");

        var sorted = stops.ToArray();
        Array.Sort(sorted, static (a, b) => a.Offset.CompareTo(b.Offset));
        Stops = sorted;
        Spread = spread;
        Opacity = Math.Clamp(opacity, 0.0, 1.0);
    }

    /// <summary>Stops, sorted ascending by offset.</summary>
    public IReadOnlyList<GradientStop> Stops { get; }

    /// <summary>Behavior outside the [0, 1] parameter range.</summary>
    public GradientSpread Spread { get; }

    /// <summary>Whole-gradient opacity (0–1), folded into each sampled color's alpha.</summary>
    public double Opacity { get; }

    /// <inheritdoc/>
    public Color ColorAt(int column, int row, Rect bounds)
    {
        // Sample the cell center, relative to the bounds origin, in cells.
        double px = column + 0.5 - bounds.Column;
        double py = row + 0.5 - bounds.Row;

        double t = ComputeOffset(px, py, bounds.Columns, bounds.Rows);
        return SampleStops(ApplySpread(t));
    }

    /// <summary>
    /// Map a sample point (<paramref name="px"/>, <paramref name="py"/>, in cells relative to the
    /// bounds origin) within a <paramref name="width"/> × <paramref name="height"/> box to a raw
    /// gradient parameter (may fall outside [0, 1]; the spread maps it back).
    /// </summary>
    protected abstract double ComputeOffset(double px, double py, double width, double height);

    /// <summary>Apply <see cref="Spread"/> to a raw parameter. Conic overrides this (it pre-wraps).</summary>
    protected virtual double ApplySpread(double t) =>
        Spread switch
        {
            GradientSpread.Repeat  => t - Math.Floor(t),
            GradientSpread.Reflect => Reflect(t),
            _                      => Math.Clamp(t, 0.0, 1.0)
        };

    /// <summary>Evaluate the stops at <paramref name="position"/> in [0, 1] (premultiplied lerp).</summary>
    protected Color SampleStops(double position)
    {
        GradientStop? before = null;
        GradientStop? after = null;
        foreach (var stop in Stops)
        {
            if (stop.Offset <= position)
                before = stop;
            else
            {
                after = stop;
                break;
            }
        }

        if (before is null) return ApplyOpacity(after!.Value.Color, Opacity);   // before the first stop
        if (after is null) return ApplyOpacity(before.Value.Color, Opacity);    // after the last stop

        var lo = before.Value;
        var hi = after.Value;
        double span = hi.Offset - lo.Offset;
        if (span <= Epsilon) return ApplyOpacity(lo.Color, Opacity);

        // Channel interpolation is only meaningful for RGB; otherwise snap to the nearer stop.
        if (lo.Color.Kind != ColorKind.Rgb || hi.Color.Kind != ColorKind.Rgb)
        {
            double mid = lo.Offset + span * 0.5;
            return ApplyOpacity(position < mid ? lo.Color : hi.Color, Opacity);
        }

        return LerpPremultiplied(lo.Color, hi.Color, (position - lo.Offset) / span, Opacity);
    }

    /// <summary>Validate a geometry scalar is finite (mirrors the opacity guard), returning it for chaining.</summary>
    private protected static double RequireFinite(double value, string paramName) =>
        double.IsFinite(value)
            ? value
            : throw new ArgumentOutOfRangeException(paramName, value, "Value must be a finite number.");

    /// <summary>Validate a relative point's coordinates are finite, returning it for chaining.</summary>
    private protected static RelativePoint RequireFinite(RelativePoint point, string paramName) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y)
            ? point
            : throw new ArgumentOutOfRangeException(paramName, point, "Point coordinates must be finite numbers.");

    private static double Reflect(double t)
    {
        double m = t - 2.0 * Math.Floor(t / 2.0);   // [0, 2)
        return m > 1.0 ? 2.0 - m : m;
    }

    private static Color LerpPremultiplied(Color from, Color to, double ratio, double opacity)
    {
        double a0 = from.Alpha / 255.0;
        double a1 = to.Alpha / 255.0;

        double pr = Lerp(from.Red * a0, to.Red * a1, ratio);
        double pg = Lerp(from.Green * a0, to.Green * a1, ratio);
        double pb = Lerp(from.Blue * a0, to.Blue * a1, ratio);
        double a = Lerp(a0, a1, ratio);
        double finalAlpha = a * opacity;

        if (a <= Epsilon)
            return Color.FromRgba(0, 0, 0, ToByte(finalAlpha * 255.0));

        return Color.FromRgba(ToByte(pr / a), ToByte(pg / a), ToByte(pb / a), ToByte(finalAlpha * 255.0));
    }

    private static Color ApplyOpacity(Color color, double opacity)
    {
        if (opacity >= 1.0 || color.Kind != ColorKind.Rgb) return color;
        return color.WithAlpha(ToByte(color.Alpha * opacity));
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static byte ToByte(double value) => (byte) Math.Clamp(Math.Round(value), 0, 255);
}
