namespace Cursorial.Drawing;

/// <summary>
/// The immutable definition of a gradient: its kind, stops, spread, opacity, and geometry. Geometry
/// values are <b>normalized [0, 1] relative to the paint extent</b> (relative-to-bounding-box) and
/// are <see langword="null"/> when unset, so a default never collides with a legitimate <c>0</c>
/// value. Stops are sorted by offset in the constructor (declaration order does not matter).
/// </summary>
/// <remarks>
/// Held behind a single reference by <see cref="Brush"/> so the brush value type stays small.
/// (Reference equality on the array means two distinct <see cref="GradientData"/> with equal stops
/// compare unequal — fine, since brushes aren't compared on the compositing hot path.)
/// </remarks>
public sealed record GradientData
{
    /// <summary>Construct a gradient definition. At least one stop is required.</summary>
    public GradientData(
        GradientKind kind,
        IReadOnlyList<GradientStop> stops,
        GradientSpread spread = GradientSpread.Pad,
        double opacity = 1.0,
        double? startColumn = null, double? startRow = null,     // linear (normalized)
        double? endColumn = null, double? endRow = null,
        double? centerColumn = null, double? centerRow = null,   // radial / conic
        double? radiusColumn = null, double? radiusRow = null,   // radial
        double? focusColumn = null, double? focusRow = null,     // radial (focal point; defaults to center)
        double? angleDegrees = null)                             // conic
    {
        ArgumentNullException.ThrowIfNull(stops);
        if (stops.Count == 0)
            throw new ArgumentException("A gradient requires at least one stop.", nameof(stops));

        var sorted = stops.ToArray();
        Array.Sort(sorted, static (a, b) => a.Offset.CompareTo(b.Offset));
        Stops = sorted;

        Kind = kind;
        Spread = spread;
        Opacity = Math.Clamp(opacity, 0.0, 1.0);
        StartColumn = startColumn; StartRow = startRow;
        EndColumn = endColumn; EndRow = endRow;
        CenterColumn = centerColumn; CenterRow = centerRow;
        RadiusColumn = radiusColumn; RadiusRow = radiusRow;
        FocusColumn = focusColumn; FocusRow = focusRow;
        AngleDegrees = angleDegrees;
    }

    /// <summary>Stops, sorted ascending by offset.</summary>
    public IReadOnlyList<GradientStop> Stops { get; }

    public GradientKind Kind { get; }
    public GradientSpread Spread { get; }

    /// <summary>Whole-gradient opacity, 0–1, folded into every sampled color's alpha.</summary>
    public double Opacity { get; }

    public double? StartColumn { get; }
    public double? StartRow { get; }
    public double? EndColumn { get; }
    public double? EndRow { get; }
    public double? CenterColumn { get; }
    public double? CenterRow { get; }
    public double? RadiusColumn { get; }
    public double? RadiusRow { get; }
    public double? FocusColumn { get; }
    public double? FocusRow { get; }
    public double? AngleDegrees { get; }
}
