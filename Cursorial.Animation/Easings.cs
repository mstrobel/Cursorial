using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using static System.Math;

namespace Cursorial.Animation;

/// <summary>
/// The built-in <see cref="Easing"/> catalog (the standard Penner / easings.net curves). Each entry
/// is a cached, allocation-free delegate. <c>In</c> accelerates from rest, <c>Out</c> decelerates to
/// rest, <c>InOut</c> does both; <see cref="BackIn"/>/<see cref="BackOut"/>/<see cref="BackInOut"/>
/// deliberately overshoot for an anticipation / settle feel.
/// </summary>
public static class Easings
{
    // Back-easing overshoot constants (easings.net): c1 governs the In/Out overshoot, c2 the InOut.
    private const double C1 = 1.70158;
    private const double C2 = C1 * 1.525;
    private const double C3 = C1 + 1.0;

    /// <summary>No shaping — progress passes through unchanged.</summary>
    public static Easing Linear { get; } = t => t;

    /// <summary>Quadratic (<c>t²</c>) ease-in.</summary>
    public static Easing QuadIn { get; } = t => t * t;
    /// <summary>Quadratic ease-out.</summary>
    public static Easing QuadOut { get; } = t => 1.0 - (1.0 - t) * (1.0 - t);
    /// <summary>Quadratic ease-in-out.</summary>
    public static Easing QuadInOut { get; } = t => t < 0.5 ? 2.0 * t * t : 1.0 - Pow(-2.0 * t + 2.0, 2.0) / 2.0;

    /// <summary>Cubic (<c>t³</c>) ease-in.</summary>
    public static Easing CubicIn { get; } = t => t * t * t;
    /// <summary>Cubic ease-out.</summary>
    public static Easing CubicOut { get; } = t => 1.0 - Pow(1.0 - t, 3.0);
    /// <summary>Cubic ease-in-out.</summary>
    public static Easing CubicInOut { get; } = t => t < 0.5 ? 4.0 * t * t * t : 1.0 - Pow(-2.0 * t + 2.0, 3.0) / 2.0;

    /// <summary>Quartic (<c>t⁴</c>) ease-in.</summary>
    public static Easing QuartIn { get; } = t => t * t * t * t;
    /// <summary>Quartic ease-out.</summary>
    public static Easing QuartOut { get; } = t => 1.0 - Pow(1.0 - t, 4.0);
    /// <summary>Quartic ease-in-out.</summary>
    public static Easing QuartInOut { get; } = t => t < 0.5 ? 8.0 * t * t * t * t : 1.0 - Pow(-2.0 * t + 2.0, 4.0) / 2.0;

    /// <summary>Sinusoidal ease-in.</summary>
    public static Easing SineIn { get; } = t => 1.0 - Cos(t * PI / 2.0);
    /// <summary>Sinusoidal ease-out.</summary>
    public static Easing SineOut { get; } = t => Sin(t * PI / 2.0);
    /// <summary>Sinusoidal ease-in-out.</summary>
    public static Easing SineInOut { get; } = t => -(Cos(PI * t) - 1.0) / 2.0;

    /// <summary>Exponential (<c>2^(10(t-1))</c>) ease-in.</summary>
    public static Easing ExpoIn { get; } = t => t <= 0.0 ? 0.0 : Pow(2.0, 10.0 * t - 10.0);
    /// <summary>Exponential ease-out.</summary>
    public static Easing ExpoOut { get; } = t => t >= 1.0 ? 1.0 : 1.0 - Pow(2.0, -10.0 * t);
    /// <summary>Exponential ease-in-out.</summary>
    public static Easing ExpoInOut { get; } = t =>
        t <= 0.0 ? 0.0
        : t >= 1.0 ? 1.0
        : t < 0.5 ? Pow(2.0, 20.0 * t - 10.0) / 2.0
        : (2.0 - Pow(2.0, -20.0 * t + 10.0)) / 2.0;

    /// <summary>Anticipation ease-in: dips below 0 before accelerating.</summary>
    public static Easing BackIn { get; } = t => C3 * t * t * t - C1 * t * t;
    /// <summary>Settle ease-out: overshoots past 1 before resting.</summary>
    public static Easing BackOut { get; } = t => 1.0 + C3 * Pow(t - 1.0, 3.0) + C1 * Pow(t - 1.0, 2.0);
    /// <summary>Anticipation-and-settle ease-in-out: undershoots then overshoots.</summary>
    public static Easing BackInOut { get; } = t =>
        t < 0.5
            ? Pow(2.0 * t, 2.0) * ((C2 + 1.0) * 2.0 * t - C2) / 2.0
            : (Pow(2.0 * t - 2.0, 2.0) * ((C2 + 1.0) * (2.0 * t - 2.0) + C2) + 2.0) / 2.0;

    // Elastic angular frequencies (easings.net): c4 for In/Out, c5 for InOut.
    private const double C4 = 2.0 * PI / 3.0;
    private const double C5 = 2.0 * PI / 4.5;

    /// <summary>Elastic ease-in: an accelerating spring that overshoots below 0 before snapping to 1.</summary>
    public static Easing ElasticIn { get; } = t =>
        t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0 : -Pow(2.0, 10.0 * t - 10.0) * Sin((10.0 * t - 10.75) * C4);
    /// <summary>Elastic ease-out: a decaying spring that overshoots past 1 then settles.</summary>
    public static Easing ElasticOut { get; } = t =>
        t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0 : Pow(2.0, -10.0 * t) * Sin((10.0 * t - 0.75) * C4) + 1.0;
    /// <summary>Elastic ease-in-out: spring at both ends.</summary>
    public static Easing ElasticInOut { get; } = t =>
        t <= 0.0 ? 0.0
        : t >= 1.0 ? 1.0
        : t < 0.5 ? -(Pow(2.0, 20.0 * t - 10.0) * Sin((20.0 * t - 11.125) * C5)) / 2.0
        : Pow(2.0, -20.0 * t + 10.0) * Sin((20.0 * t - 11.125) * C5) / 2.0 + 1.0;

    /// <summary>Bounce ease-out: settles with a series of decaying bounces (easings.net).</summary>
    public static Easing BounceOut { get; } = BounceOutImpl;
    /// <summary>Bounce ease-in: the mirror of <see cref="BounceOut"/> — bounces up to the start.</summary>
    public static Easing BounceIn { get; } = t => 1.0 - BounceOutImpl(1.0 - t);
    /// <summary>Bounce ease-in-out.</summary>
    public static Easing BounceInOut { get; } = t =>
        t < 0.5 ? (1.0 - BounceOutImpl(1.0 - 2.0 * t)) / 2.0 : (1.0 + BounceOutImpl(2.0 * t - 1.0)) / 2.0;

    private static double BounceOutImpl(double t)
    {
        const double n1 = 7.5625, d1 = 2.75;
        if (t < 1.0 / d1)
            return n1 * t * t;
        if (t < 2.0 / d1)
        {
            t -= 1.5 / d1;
            return n1 * t * t + 0.75;
        }
        if (t < 2.5 / d1)
        {
            t -= 2.25 / d1;
            return n1 * t * t + 0.9375;
        }
        t -= 2.625 / d1;
        return n1 * t * t + 0.984375;
    }

    /// <summary>
    /// A CSS-style cubic-bezier timing function over control points <c>P0=(0,0)</c>, <c>P1=(x1,y1)</c>,
    /// <c>P2=(x2,y2)</c>, <c>P3=(1,1)</c>: maps progress <c>x</c> to <c>y</c> by solving <c>bezierX(u)=x</c>
    /// (Newton-Raphson with a bisection fallback) then evaluating <c>bezierY(u)</c>. <c>x1</c>/<c>x2</c> must
    /// be in <c>[0, 1]</c> (a monotonic x); <c>y</c> may overshoot.
    /// </summary>
    public static Easing CubicBezier(double x1, double y1, double x2, double y2)
    {
        if (x1 is < 0.0 or > 1.0 || x2 is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(x1), "cubic-bezier x control points must be in [0, 1] (a monotonic timing function).");

        // Polynomial coefficients (WebKit UnitBezier): sample(u) = ((a*u + b)*u + c)*u.
        var cx = 3.0 * x1;
        var bx = 3.0 * (x2 - x1) - cx;
        var ax = 1.0 - cx - bx;
        var cy = 3.0 * y1;
        var by = 3.0 * (y2 - y1) - cy;
        var ay = 1.0 - cy - by;

        double SampleX(double u) => ((ax * u + bx) * u + cx) * u;
        double SampleY(double u) => ((ay * u + by) * u + cy) * u;
        double SampleDerivX(double u) => (3.0 * ax * u + 2.0 * bx) * u + cx;

        return x =>
        {
            if (x <= 0.0)
                return 0.0;
            if (x >= 1.0)
                return 1.0;

            // Newton-Raphson from u = x (8 iters), falling back to bisection if the derivative is flat.
            var u = x;
            for (var i = 0; i < 8; i++)
            {
                var error = SampleX(u) - x;
                if (Abs(error) < 1e-7)
                    return SampleY(u);

                var derivative = SampleDerivX(u);
                if (Abs(derivative) < 1e-7)
                    break;

                u -= error / derivative;
            }

            double lo = 0.0, hi = 1.0;
            u = x;
            while (lo < hi)
            {
                var xAtU = SampleX(u);
                if (Abs(xAtU - x) < 1e-7)
                    break;
                if (x > xAtU)
                    lo = u;
                else
                    hi = u;
                u = (hi + lo) / 2.0;
            }

            return SampleY(u);
        };
    }

    // Catalog name → easing, case-insensitive (the Fork C converter target — §9.10).
    private static readonly Dictionary<string, Easing> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Linear"] = Linear,
        ["QuadIn"] = QuadIn, ["QuadOut"] = QuadOut, ["QuadInOut"] = QuadInOut,
        ["CubicIn"] = CubicIn, ["CubicOut"] = CubicOut, ["CubicInOut"] = CubicInOut,
        ["QuartIn"] = QuartIn, ["QuartOut"] = QuartOut, ["QuartInOut"] = QuartInOut,
        ["SineIn"] = SineIn, ["SineOut"] = SineOut, ["SineInOut"] = SineInOut,
        ["ExpoIn"] = ExpoIn, ["ExpoOut"] = ExpoOut, ["ExpoInOut"] = ExpoInOut,
        ["BackIn"] = BackIn, ["BackOut"] = BackOut, ["BackInOut"] = BackInOut,
        ["ElasticIn"] = ElasticIn, ["ElasticOut"] = ElasticOut, ["ElasticInOut"] = ElasticInOut,
        ["BounceIn"] = BounceIn, ["BounceOut"] = BounceOut, ["BounceInOut"] = BounceInOut,
    };

    /// <summary>
    /// Parses a catalog name (case-insensitive, e.g. <c>"CubicOut"</c>/<c>"ElasticInOut"</c>) or a
    /// <c>cubic-bezier(x1,y1,x2,y2)</c> functional form (design doc §9.10/§9.11). Returns false on an
    /// unknown name or malformed bezier.
    /// </summary>
    public static bool TryParse(string? text, [MaybeNullWhen(false)] out Easing easing)
    {
        easing = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var span = text.Trim();

        if (span.StartsWith("cubic-bezier", StringComparison.OrdinalIgnoreCase))
            return TryParseCubicBezier(span, out easing);

        return ByName.TryGetValue(span, out easing);
    }

    private static bool TryParseCubicBezier(string text, [MaybeNullWhen(false)] out Easing easing)
    {
        easing = null;

        var open = text.IndexOf('(');
        var close = text.IndexOf(')');
        if (open < 0 || close < open)
            return false;

        var inner = text.Substring(open + 1, close - open - 1);
        var parts = inner.Split(',');
        if (parts.Length != 4)
            return false;

        Span<double> values = stackalloc double[4];
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return false;
        }

        if (values[0] is < 0.0 or > 1.0 || values[2] is < 0.0 or > 1.0)
            return false; // x control points out of [0, 1]

        easing = CubicBezier(values[0], values[1], values[2], values[3]);
        return true;
    }
}
