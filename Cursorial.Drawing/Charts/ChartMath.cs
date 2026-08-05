namespace Cursorial.Drawing.Charts;

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

    /// <summary>The hit-test tooltip text for a point: both coordinates, each formatted at a precision
    /// suited to its axis' rendered span.</summary>
    public static string FormatPoint(PointD p, AxisRange x, AxisRange y)
        => $"X={Format(p.X, Math.Abs(x.Span))}, Y={Format(p.Y, Math.Abs(y.Span))}";

    // ---- numeric formatting (tooltips / labels) ----

    private const char SuperscriptMinus = '⁻';
    private const char SuperscriptGroupSeparator = 'ʻ';

    private static readonly char[] SuperscriptDigits =
    [
        '⁰', '¹', '²', '³', '⁴', '⁵', '⁶', '⁷', '⁸', '⁹'
    ];

    /// <summary>Format <paramref name="value"/> for display against an axis whose rendered span is
    /// <paramref name="scale"/> — the wider the span, the fewer decimals matter; a span under a
    /// thousandth switches to superscript base-10 notation.</summary>
    internal static string Format(double value, double scale)
        => scale switch
           {
               >= 1     => value.ToString("#,0.#"),
               >= 0.1   => value.ToString("#,0.0#"),
               >= 0.01  => value.ToString("#,0.00#"),
               >= 0.001 => value.ToString("#,0.000#"),
               _        => FormatBase10Exponent(value)
           };

    internal static string FormatBase10Exponent(double value)
    {
        ExtractFractionAndExponent(value, out double f, out var e);

        Span<char> exponent = stackalloc char[14];

        if (e.TryFormat(exponent, out var expDigits) is false)
            return $"{value:E}";

        for (int i = 0; i < expDigits; i++)
        {
            exponent[i] = exponent[i] switch
                          {
                              char d and >= '0' and <= '9' => SuperscriptDigits[d - '0'],
                              ','                          => SuperscriptGroupSeparator,
                              '-'                          => SuperscriptMinus,
                              char c                       => c
                          };
        }

        Span<char> result = stackalloc char[32];

        if (f.TryFormat(result, out var fracDigits, "#,0.00##") is false)
            return $"{value:E}";

        var expBase = " ✕ 10";
        const int expBaseLength = 5;

        Span<char> part = result.Slice(fracDigits);

        expBase.CopyTo(part);
        part = part.Slice(expBaseLength);
        exponent.Slice(0, expDigits).CopyTo(part);

        var totalLength = fracDigits + expBaseLength + expDigits;

        return result.Slice(0, totalLength).ToString();
    }

    /// <summary>
    /// Decomposes <paramref name="value"/> into a base-10 fraction and exponent such that
    /// <c>value == fraction * 10^exponent</c>, where <c>0.1 &lt;= |fraction| &lt; 1</c>.
    /// </summary>
    /// <remarks>
    /// Zero, NaN, and the infinities are passed through unchanged with an exponent of zero.
    /// The sign of <paramref name="value"/> is carried by <paramref name="fraction"/>.
    /// </remarks>
    internal static void ExtractFractionAndExponent(double value, out double fraction, out int exponent)
    {
        if (value == 0.0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            fraction = value;
            exponent = 0;
            return;
        }

        // Log10 is only approximate near powers of ten, so treat this as a seed
        // and correct the off-by-one cases below.
        exponent = (int) Math.Floor(Math.Log10(Math.Abs(value))) + 1;
        fraction = ScaleByPowerOf10(value, -exponent);

        double magnitude = Math.Abs(fraction);

        if (magnitude >= 1.0)
            fraction = ScaleByPowerOf10(value, -(++exponent));
        else if (magnitude < 0.1)
            fraction = ScaleByPowerOf10(value, -(--exponent));
    }

    /// <summary>
    /// Computes <c>value * 10^power</c>, splitting the scale factor into steps so neither the
    /// intermediate power of ten nor the running product overflows or flushes to zero.
    /// </summary>
    private static double ScaleByPowerOf10(double value, int power)
    {
        // 10^300 sits comfortably inside the double range; apply larger magnitudes in steps.
        while (power > 300)
        {
            value *= 1e300;
            power -= 300;
        }

        while (power < -300)
        {
            value *= 1e-300;
            power += 300;
        }

        return value * Math.Pow(10.0, power);
    }
}
