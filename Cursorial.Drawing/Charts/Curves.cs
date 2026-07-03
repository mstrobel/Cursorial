namespace Cursorial.Drawing.Charts;

/// <summary>
/// Curve interpolation for line charts. Produces a dense polyline through the data; the caller draws
/// straight (braille) segments between consecutive samples, so the result is gap-free regardless of
/// sample density. Math is verified against a numerical oracle (centripetal Catmull-Rom via
/// Barry-Goldman, α=0.5; Fritsch-Carlson monotone cubic with the in-place left-to-right tangent clamp).
/// </summary>
internal static class Curves
{
    /// <summary>
    /// Densely sample the curve of <paramref name="kind"/> through <paramref name="points"/>, with
    /// <paramref name="samplesPerSegment"/> sub-samples per data interval. Linear returns the points
    /// verbatim. MonotoneCubic sorts by strictly-increasing X (it is a function-graph interpolant).
    /// </summary>
    public static List<PointD> Sample(CurveInterpolation kind, IReadOnlyList<PointD> points, int samplesPerSegment)
    {
        if (points.Count <= 1)
            return [.. points];

        int per = Math.Max(1, samplesPerSegment);
        return kind switch
        {
            CurveInterpolation.CatmullRom    => SampleCatmullRom(points, per),
            CurveInterpolation.MonotoneCubic => SampleMonotoneCubic(points, per),
            _                                => [.. points],   // Linear: straight segments between points
        };
    }

    // ---- centripetal Catmull-Rom (Barry-Goldman, α = 0.5) ----
    private static List<PointD> SampleCatmullRom(IReadOnlyList<PointD> points, int per)
    {
        int n = points.Count;
        var result = new List<PointD>(1 + (n - 1) * per) { points[0] };

        for (int i = 0; i < n - 1; i++)
        {
            PointD p0 = points[Math.Max(0, i - 1)];
            PointD p1 = points[i];
            PointD p2 = points[i + 1];
            PointD p3 = points[Math.Min(n - 1, i + 2)];

            for (int s = 1; s <= per; s++)
                result.Add(CatmullRomPoint(p0, p1, p2, p3, (double) s / per));
        }

        return result;
    }

    private static PointD CatmullRomPoint(PointD p0, PointD p1, PointD p2, PointD p3, double u)
    {
        const double alpha = 0.5;
        double t0 = 0.0;
        double t1 = t0 + Knot(p0, p1, alpha);
        double t2 = t1 + Knot(p1, p2, alpha);
        double t3 = t2 + Knot(p2, p3, alpha);
        double t = t1 + (t2 - t1) * u;

        PointD a1 = Lerp(p0, p1, t0, t1, t);
        PointD a2 = Lerp(p1, p2, t1, t2, t);
        PointD a3 = Lerp(p2, p3, t2, t3, t);
        PointD b1 = Lerp(a1, a2, t0, t2, t);
        PointD b2 = Lerp(a2, a3, t1, t3, t);
        return Lerp(b1, b2, t1, t2, t);

        static double Knot(PointD a, PointD b, double e) => Math.Pow(Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)), e);

        static PointD Lerp(PointD a, PointD b, double ta, double tb, double at)
        {
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (tb == ta) return a;
            double wa = (tb - at) / (tb - ta), wb = (at - ta) / (tb - ta);
            return new PointD(a.X * wa + b.X * wb, a.Y * wa + b.Y * wb);
        }
    }

    // ---- Fritsch-Carlson monotone cubic (strictly-increasing X) ----
    private static List<PointD> SampleMonotoneCubic(IReadOnlyList<PointD> points, int per)
    {
        // Sort by X and keep strictly-increasing X (a function graph; equal-X points would divide by zero).
        var sorted = points.OrderBy(p => p.X).ToList();
        var xs = new List<double>();
        var ys = new List<double>();
        foreach (var p in sorted)
            if (xs.Count == 0 || p.X > xs[^1])
            {
                xs.Add(p.X);
                ys.Add(p.Y);
            }

        int n = xs.Count;
        if (n <= 1)
            return [.. points];
        if (n == 2)
            return [new PointD(xs[0], ys[0]), new PointD(xs[1], ys[1])];   // a single segment is just linear

        double[] m = FritschCarlsonTangents(xs, ys);
        var result = new List<PointD>(1 + (n - 1) * per) { new PointD(xs[0], ys[0]) };
        for (int i = 0; i < n - 1; i++)
        {
            double h = xs[i + 1] - xs[i];
            for (int s = 1; s <= per; s++)
            {
                double t = (double) s / per;
                result.Add(new PointD(xs[i] + t * h, Hermite(t, h, ys[i], ys[i + 1], m[i], m[i + 1])));
            }
        }
        return result;
    }

    private static double[] FritschCarlsonTangents(List<double> xs, List<double> ys)
    {
        int n = xs.Count;
        var d = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
            d[i] = (ys[i + 1] - ys[i]) / (xs[i + 1] - xs[i]);

        var m = new double[n];
        m[0] = d[0];
        m[n - 1] = d[n - 2];
        for (int i = 1; i < n - 1; i++)
            m[i] = (d[i - 1] + d[i]) / 2.0;

        // In-place, left-to-right clamp (the order is load-bearing — it matches the oracle).
        for (int i = 0; i < n - 1; i++)
        {
            if (d[i] == 0.0)
            {
                m[i] = 0.0;
                m[i + 1] = 0.0;
            }
            else
            {
                double a = m[i] / d[i], b = m[i + 1] / d[i];
                if (a < 0.0) m[i] = 0.0;
                if (b < 0.0) m[i + 1] = 0.0;
                if (a * a + b * b > 9.0)
                {
                    double tau = 3.0 / Math.Sqrt(a * a + b * b);
                    m[i] = tau * a * d[i];
                    m[i + 1] = tau * b * d[i];
                }
            }
        }
        return m;
    }

    private static double Hermite(double t, double h, double y0, double y1, double m0, double m1)
    {
        double t2 = t * t, t3 = t2 * t;
        double h00 = 2 * t3 - 3 * t2 + 1, h10 = t3 - 2 * t2 + t, h01 = -2 * t3 + 3 * t2, h11 = t3 - t2;
        return h00 * y0 + h10 * h * m0 + h01 * y1 + h11 * h * m1;
    }
}
