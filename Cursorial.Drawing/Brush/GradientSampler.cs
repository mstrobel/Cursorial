using Cursorial.Output;

namespace Cursorial.Drawing;

/// <summary>
/// Resolves a <see cref="GradientData"/> to a solid <see cref="Color"/> per cell, against a fixed
/// <see cref="BrushExtent"/>. Build one per fill and reuse it across the fill's cells — the 256-entry
/// color ramp (the expensive part) is precomputed once in the constructor; per-cell sampling is then
/// a little geometry plus one array index.
/// </summary>
/// <remarks>
/// <para>
/// <b>Premultiplied interpolation.</b> The ramp interpolates stops in premultiplied-alpha sRGB and
/// un-premultiplies to a straight-alpha <see cref="Color"/>, so a fade to a transparent stop carries
/// the neighbor's hue (no dark/white fringe) and the result composes correctly through the existing
/// straight-alpha <see cref="Color.Composite"/>. Stops whose color isn't RGB (terminal-default /
/// palette) aren't channel-interpolated — the nearer stop wins. Gradient opacity folds into each
/// ramp entry's alpha.
/// </para>
/// <para>
/// Geometry is <b>relative to the extent</b> (normalized 0–1 → extent cells); a radial/conic gradient
/// therefore fills the extent as an ellipse/sweep. Cell-pixel aspect correction (true on-screen
/// circles) is a future refinement.
/// </para>
/// </remarks>
public sealed class GradientSampler
{
    private const int LutSize = 256;
    private const double Epsilon = 1e-9;

    private readonly Color[] _lut;
    private readonly GradientKind _kind;
    private readonly GradientSpread _spread;
    private readonly double _originColumn;
    private readonly double _originRow;

    // Resolved geometry, in extent-local cell coordinates.
    private readonly double _startX, _startY, _vecX, _vecY, _lenSq;             // linear
    private readonly double _centerX, _centerY, _radiusX, _radiusY, _focusX, _focusY;  // radial
    private readonly double _angle;                                            // conic (radians-free; degrees)

    public GradientSampler(GradientData data, in BrushExtent extent)
    {
        ArgumentNullException.ThrowIfNull(data);

        _kind = data.Kind;
        _spread = data.Spread;
        _originColumn = extent.Column;
        _originRow = extent.Row;
        _lut = BuildRamp(data);

        double w = extent.Columns;
        double h = extent.Rows;

        // Linear: start/end default to a horizontal left→right sweep.
        _startX = (data.StartColumn ?? 0.0) * w;
        _startY = (data.StartRow ?? 0.0) * h;
        double endX = (data.EndColumn ?? 1.0) * w;
        double endY = (data.EndRow ?? 0.0) * h;
        _vecX = endX - _startX;
        _vecY = endY - _startY;
        _lenSq = _vecX * _vecX + _vecY * _vecY;

        // Radial/conic: center defaults to the extent's middle; radii default to half-extent; the
        // focal point defaults to the center.
        double centerColN = data.CenterColumn ?? 0.5;
        double centerRowN = data.CenterRow ?? 0.5;
        _centerX = centerColN * w;
        _centerY = centerRowN * h;
        _radiusX = (data.RadiusColumn ?? 0.5) * w;
        _radiusY = (data.RadiusRow ?? 0.5) * h;
        _focusX = (data.FocusColumn ?? centerColN) * w;
        _focusY = (data.FocusRow ?? centerRowN) * h;

        _angle = data.AngleDegrees ?? 0.0;
    }

    /// <summary>Sample the cell at <paramref name="column"/>, <paramref name="row"/> (sampled at its center).</summary>
    public Color Sample(int column, int row) => Sample(column + 0.5, row + 0.5);

    /// <summary>Sample at a fractional position.</summary>
    public Color Sample(double column, double row)
    {
        double px = column - _originColumn;
        double py = row - _originRow;

        double t = _kind switch
                   {
                       GradientKind.Linear => LinearT(px, py),
                       GradientKind.Radial => RadialT(px, py),
                       GradientKind.Conic  => ConicT(px, py),
                       _                   => 0.0
                   };

        t = ApplySpread(t, _spread);
        int index = (int) Math.Clamp(Math.Round(t * (LutSize - 1)), 0, LutSize - 1);
        return _lut[index];
    }

    private double LinearT(double px, double py)
    {
        if (_lenSq <= Epsilon) return 0.0;
        return ((px - _startX) * _vecX + (py - _startY) * _vecY) / _lenSq;
    }

    private double RadialT(double px, double py)
    {
        if (_radiusX <= Epsilon || _radiusY <= Epsilon) return 0.0;

        // SVG two-point (focal) radial in unit-ellipse space: cast a ray from the focal point through
        // the sample point to the unit circle; the gradient value is 1/s.
        double fx = (_focusX - _centerX) / _radiusX;
        double fy = (_focusY - _centerY) / _radiusY;
        double pnx = (px - _centerX) / _radiusX;
        double pny = (py - _centerY) / _radiusY;
        double dx = pnx - fx;
        double dy = pny - fy;

        double a = dx * dx + dy * dy;
        if (a <= Epsilon) return 0.0;                 // sample sits on the focal point

        double b = 2.0 * (fx * dx + fy * dy);
        double c = fx * fx + fy * fy - 1.0;
        double disc = b * b - 4.0 * a * c;
        if (disc < 0.0) return 1.0;                   // outside the gradient → outer color

        double s = (-b + Math.Sqrt(disc)) / (2.0 * a);
        return s <= Epsilon ? 1.0 : 1.0 / s;
    }

    private double ConicT(double px, double py)
    {
        double dx = px - _centerX;
        double dy = py - _centerY;
        double angleDegrees = Math.Atan2(dx, -dy) * (180.0 / Math.PI);   // clockwise from 12 o'clock
        double t = (angleDegrees - _angle) / 360.0;
        return t - Math.Floor(t);                     // wrap into [0, 1)
    }

    private static double ApplySpread(double t, GradientSpread spread) =>
        spread switch
        {
            GradientSpread.Repeat  => t - Math.Floor(t),
            GradientSpread.Reflect => Reflect(t),
            _                      => Math.Clamp(t, 0.0, 1.0)   // Pad
        };

    private static double Reflect(double t)
    {
        double m = t - 2.0 * Math.Floor(t / 2.0);     // [0, 2)
        return m > 1.0 ? 2.0 - m : m;
    }

    private static Color[] BuildRamp(GradientData data)
    {
        var lut = new Color[LutSize];
        for (int i = 0; i < LutSize; i++)
            lut[i] = SampleStops(data, i / (double) (LutSize - 1));
        return lut;
    }

    /// <summary>Evaluate the stop list at <paramref name="position"/> in [0, 1] (premultiplied lerp).</summary>
    private static Color SampleStops(GradientData data, double position)
    {
        var stops = data.Stops;

        // Nearest-enclosing pair: `before` = greatest offset ≤ position, `after` = least offset > position.
        GradientStop? before = null;
        GradientStop? after = null;
        foreach (var stop in stops)
        {
            if (stop.Offset <= position)
                before = stop;
            else
            {
                after = stop;
                break;
            }
        }

        if (before is null) return ApplyOpacity(after!.Value.Color, data.Opacity);    // before the first stop
        if (after is null) return ApplyOpacity(before.Value.Color, data.Opacity);     // after the last stop

        var lo = before.Value;
        var hi = after.Value;
        double span = hi.Offset - lo.Offset;
        if (span <= Epsilon) return ApplyOpacity(lo.Color, data.Opacity);

        // Channel interpolation is only meaningful for RGB; otherwise snap to the nearer stop.
        if (lo.Color.Kind != ColorKind.Rgb || hi.Color.Kind != ColorKind.Rgb)
        {
            double mid = lo.Offset + span * 0.5;
            return ApplyOpacity(position < mid ? lo.Color : hi.Color, data.Opacity);
        }

        double ratio = (position - lo.Offset) / span;
        return LerpPremultiplied(lo.Color, hi.Color, ratio, data.Opacity);
    }

    private static Color LerpPremultiplied(Color from, Color to, double ratio, double opacity)
    {
        double a0 = from.Alpha / 255.0;
        double a1 = to.Alpha / 255.0;

        // Premultiply, lerp channels + alpha, then un-premultiply by the interpolated (pre-opacity) alpha.
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
