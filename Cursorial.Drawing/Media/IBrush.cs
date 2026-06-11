using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Drawing.Media;

/// <summary>
/// A color <em>source</em> the drawing layer samples per cell. Implementations resolve a solid color,
/// a gradient, or (in future) an image / tile / pattern. A brush never enters <see cref="Style"/> or
/// a cell — it is resolved to a scalar <see cref="Color"/> at draw time (a terminal cell shows one
/// solid color).
/// </summary>
/// <remarks>
/// Brushes are immutable definitions; <see cref="ColorAt"/> is pure and allocation-free, safe to call
/// once per cell from a fill loop and safe for repeated, potentially concurrent invocation. There is no
/// implicit <see cref="Color"/> → <see cref="IBrush"/> conversion (interfaces can't be conversion
/// targets); use <see cref="SolidColorBrush"/> (which has an implicit conversion from
/// <see cref="Color"/>) or the <see cref="Color"/> overloads on the drawing methods.
/// </remarks>
public interface IBrush
{
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
}
