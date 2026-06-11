// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// A numeric data range [<see cref="Min"/>, <see cref="Max"/>] for a chart axis. Construct explicitly,
/// or derive one from data with <see cref="FromValues"/> (which handles the empty / all-equal / non-finite
/// edge cases so callers never divide by a zero span).
/// </summary>
public readonly record struct AxisRange(double Min, double Max)
{
    /// <summary>The width of the range (<see cref="Max"/> − <see cref="Min"/>).</summary>
    public double Span => Max - Min;

    /// <summary>True when the span is effectively zero (guard before normalizing against it).</summary>
    public bool IsDegenerate => Math.Abs(Span) < 1e-12;

    /// <summary>
    /// The tightest range covering the finite values in <paramref name="values"/>. An empty (or
    /// all-non-finite) input returns <c>[0, 1]</c>; an all-equal input pads to <c>[v−1, v+1]</c> so the
    /// range is never degenerate.
    /// </summary>
    public static AxisRange FromValues(ReadOnlySpan<double> values)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (double v in values)
        {
            if (!double.IsFinite(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        if (min > max) return new AxisRange(0.0, 1.0);              // no finite values
        if (Math.Abs(max - min) < 1e-12) return new AxisRange(min - 1.0, max + 1.0);   // all equal
        return new AxisRange(min, max);
    }

    /// <summary>The smallest range covering both this and <paramref name="other"/> (multi-series auto-range).</summary>
    public AxisRange Union(AxisRange other) => new(Math.Min(Min, other.Min), Math.Max(Max, other.Max));

    /// <summary>This range extended to include zero (bar charts anchor at the zero baseline).</summary>
    public AxisRange IncludingZero() => new(Math.Min(Min, 0.0), Math.Max(Max, 0.0));

    /// <summary>Map <paramref name="value"/> into [0, 1] across the range (0.5 for a degenerate range; unclamped).</summary>
    public double Normalize(double value) => IsDegenerate ? 0.5 : (value - Min) / Span;

    /// <summary>
    /// A "nice" version of this range plus its tick values, using Heckbert's nice-numbers (steps of
    /// 1/2/5·10ⁿ) for at most <paramref name="maxTicks"/> ticks. The returned range is rounded outward to
    /// the tick step so labels land on round numbers; pass it to the chart so axis and data align.
    /// </summary>
    public (AxisRange Range, double Step, IReadOnlyList<double> Ticks) Nice(int maxTicks = 5)
    {
        if (IsDegenerate || maxTicks < 2)
            return (this, Span, [Min, Max]);

        double step = NiceNum(NiceNum(Span, round: false) / (maxTicks - 1), round: true);
        double niceMin = Math.Floor(Min / step) * step;
        double niceMax = Math.Ceiling(Max / step) * step;

        var ticks = new List<double>();
        for (double t = niceMin; t <= niceMax + step * 0.5; t += step)
            ticks.Add(Math.Round(t, 10));   // tame fp drift so labels read cleanly

        return (new AxisRange(niceMin, niceMax), step, ticks);
    }

    // Heckbert "nice number": round (or take the ceiling of) a span to a 1/2/5·10ⁿ value.
    private static double NiceNum(double range, bool round)
    {
        double exponent = Math.Floor(Math.Log10(range));
        double fraction = range / Math.Pow(10, exponent);
        double nice = round
            ? (fraction < 1.5 ? 1.0 : fraction < 3.0 ? 2.0 : fraction < 7.0 ? 5.0 : 10.0)
            : (fraction <= 1.0 ? 1.0 : fraction <= 2.0 ? 2.0 : fraction <= 5.0 ? 5.0 : 10.0);
        return nice * Math.Pow(10, exponent);
    }
}
