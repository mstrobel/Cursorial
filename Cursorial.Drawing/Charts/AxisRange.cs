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
}
