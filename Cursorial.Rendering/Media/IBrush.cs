using Cursorial.Media;
using Cursorial.Output;

namespace Cursorial.Rendering.Media;

/// <summary>
/// A color <em>source</em> the drawing layer samples per cell. Implementations resolve a solid color,
/// a gradient, or (in future) an image / tile / pattern. A brush never enters <see cref="CellStyle"/> or
/// a cell — it is resolved to a scalar <see cref="Color"/> at draw time (a terminal cell shows one
/// solid color).
/// </summary>
/// <remarks>
/// Brushes are immutable definitions; <see cref="ColorAt(int, int, Rect)"/> is pure and allocation-free, safe to call
/// once per cell from a fill loop and safe for repeated, potentially concurrent invocation. There is no
/// implicit <see cref="Color"/> → <see cref="IBrush"/> conversion (interfaces can't be conversion
/// targets); use <c>SolidColorBrush</c> (which has an implicit conversion from
/// <see cref="Color"/>) or the <see cref="Color"/> overloads on the drawing methods.
/// </remarks>
public interface IBrush
{
    /// <summary>
    /// Indicates whether this brush is <em>uniform</em>, i.e., it produces the same color for all sampling locations.
    /// </summary>
    bool IsUniform => false;

    /// <summary>
    /// Whole-brush opacity (0–1), folded into each sampled color's alpha at <see cref="ColorAt(int, int, Rect)"/> (RGB
    /// only). Clamped to [0, 1].
    /// </summary>
    double Opacity => 1.0;

    /// <summary>Whether the brush is opaque.</summary>
    /// <remarks>
    /// The DEFAULT speaks for the <see cref="Opacity"/> knob alone: <see cref="ColorAt(int, int, Rect)"/> is
    /// positional, so the interface cannot know its colours' alpha in general — a brush whose
    /// colours themselves carry alpha still reports opaque here (design doc defect 6). An
    /// implementation that does know its colours (<c>SolidColorBrush</c>) overrides this to fold
    /// them in. Per-cell decisions must test the SAMPLED colour (<see cref="Color.IsOpaque"/>),
    /// not this bit.
    /// </remarks>
    bool IsOpaque => Math.Abs(Opacity - 1.0) < double.Epsilon;
    
    /// <summary>
    /// The color for the cell at (<paramref name="column"/>, <paramref name="row"/>) — scene-local cell
    /// coordinates, with the origin at <paramref name="bounds"/>'s <see cref="Rect.Position"/> and units
    /// in cells. Position-dependent brushes (gradients) sample the <em>cell center</em>, i.e.
    /// <c>(column − bounds.Column + 0.5, row − bounds.Row + 0.5)</c>; solid brushes ignore all three
    /// arguments.
    /// </summary>
    /// <remarks>
    /// <paramref name="bounds"/> is the painted element's box (run / paragraph / shape / scene) and is
    /// the brush's coordinate space. It may be <see cref="Rect.IsEmpty">empty</see> (a zero-width text
    /// run yields <c>Columns == 0</c>), so an implementation that scales by the extent MUST guard
    /// against a zero <c>Columns</c>/<c>Rows</c> rather than dividing by it (the built-in gradients
    /// return a defined parameter via their degenerate-bounds checks).
    /// </remarks>
    Color ColorAt(int column, int row, Rect bounds);

    /// <summary>
    /// The phase-shifted sample: <see cref="ColorAt(int, int, Rect)"/> with the brush's parameter
    /// axis advanced by <paramref name="phase"/> periods before its out-of-range policy applies.
    /// The DEFAULT ignores the phase — a brush with no parameter axis (a solid color, an image
    /// sampled per cell) has nothing to shift, and <see cref="Rect"/>'s integer coordinates make a
    /// fractional shift inexpressible through the positional arguments — so only brushes that own
    /// a continuous parameterization (<c>GradientBrush</c>: <paramref name="phase"/> is added to
    /// the raw gradient parameter <c>t</c>, and the brush's <c>GradientSpread</c> then decides
    /// wrap/clamp/reflect) override this. Callers with no phase to apply should keep calling the
    /// three-argument form.
    /// </summary>
    Color ColorAt(int column, int row, Rect bounds, double phase) => ColorAt(column, row, bounds);
}
