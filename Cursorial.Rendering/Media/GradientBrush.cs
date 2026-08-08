using Cursorial.Drawing;
using Cursorial.Media;

namespace Cursorial.Rendering.Media;

/// <summary>
/// Base class for gradient brushes. Holds the (sorted) stops, spread, and opacity, and owns the
/// shared per-cell color resolution: each subclass maps a cell to a gradient parameter <c>t</c>
/// (<see cref="ComputeOffset"/>), then this class applies the spread and interpolates the stops in
/// <b>premultiplied sRGB</b> (un-premultiplied to a straight <see cref="Color"/>, so a fade to a
/// transparent stop keeps the neighbor's hue and composes correctly through the existing
/// straight-alpha compositing). Sampled directly per cell — no lookup table; terminal cell counts
/// are small and full-precision sampling avoids the banding a 256-entry ramp would introduce.
/// </summary>
[Cursorial.Markup.ContentProperty("Stops")]
public abstract class GradientBrush : IBrush
{
    private protected const double Epsilon = 1e-9;
    private double _opacity = 1.0;

    /// <summary>Parameterless constructor for XAML element authoring: <see cref="Stops"/> starts empty and is
    /// populated by the loader (the content property), <see cref="Spread"/>/<see cref="Opacity"/> via init.
    /// Stops added this way are NOT sorted — <see cref="SampleStops"/> is order-agnostic.</summary>
    protected GradientBrush()
    {
        Stops = new List<GradientStop>();
    }

    protected GradientBrush(IReadOnlyList<GradientStop> stops, GradientSpread spread, double opacity)
    {
        ArgumentNullException.ThrowIfNull(stops);
        if (stops.Count == 0)
            throw new ArgumentException("A gradient requires at least one stop.", nameof(stops));
        if (!double.IsFinite(opacity))
            throw new ArgumentOutOfRangeException(nameof(opacity), opacity, "Opacity must be a finite value.");

        // The code-first ctor keeps Stops sorted ascending (the documented contract + GradientBrushTests);
        // SampleStops no longer DEPENDS on it (it is order-agnostic), so element-authored stops need no sort.
        var list = new List<GradientStop>(stops);
        list.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
        Stops = list;
        Spread = spread;
        Opacity = opacity;
    }

    /// <summary>The gradient stops. Code-first construction sorts them ascending by offset; element-authored
    /// stops are in declaration order — either way <see cref="SampleStops"/> resolves them order-agnostically.</summary>
    public IList<GradientStop> Stops { get; init; }

    /// <summary>Behavior outside the [0, 1] parameter range.</summary>
    public GradientSpread Spread { get; init; }

    /// <summary>Whole-gradient opacity (0–1, clamped), folded into each sampled color's alpha.</summary>
    public double Opacity
    {
        get => _opacity;
        init
        {
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Opacity must be a finite value.");
            _opacity = Math.Clamp(value, 0.0, 1.0);
        }
    }

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

    /// <summary>
    /// Evaluate the stops at <paramref name="position"/> in [0, 1] (premultiplied lerp). Order-agnostic: it
    /// scans for the nearest stop at-or-below and the nearest above <paramref name="position"/> regardless of
    /// the stops' order, so element-authored (unsorted) stops resolve identically to code-first (sorted) ones.
    /// </summary>
    protected Color SampleStops(double position)
    {
        if (Stops.Count == 0)
            return Colors.Default; // a degenerate (stop-less) gradient paints the terminal default

        GradientStop? before = null;
        GradientStop? after = null;
        foreach (var stop in Stops)
        {
            if (stop.Offset <= position)
            {
                if (before is null || stop.Offset > before.Value.Offset)
                    before = stop;
            }
            else if (after is null || stop.Offset < after.Value.Offset)
            {
                after = stop;
            }
        }

        if (before is null) return ApplyOpacity(after!.Value.Color, Opacity);   // before the first stop
        if (after is null) return ApplyOpacity(before.Value.Color, Opacity);    // after the last stop

        var lo = before.Value;
        var hi = after.Value;
        double span = hi.Offset - lo.Offset;
        if (span <= Epsilon) return ApplyOpacity(lo.Color, Opacity);

        // Premultiplied-sRGB interpolation (with the non-RGB snap) lives in Core's Color.Lerp — the single
        // source of truth shared with the animation layer's ColorInterpolator. The whole-gradient Opacity
        // is the brush's own concern, applied here (same as the single-stop branches above).
        return ApplyOpacity(Color.Lerp(lo.Color, hi.Color, (position - lo.Offset) / span), Opacity);
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

    private static Color ApplyOpacity(Color color, double opacity)
    {
        if (opacity >= 1.0 || color.Kind != ColorKind.Rgb) return color;
        return color.WithAlpha(ToByte(color.Alpha * opacity));
    }

    private static byte ToByte(double value) => (byte) Math.Clamp(Math.Round(value), 0, 255);
}
