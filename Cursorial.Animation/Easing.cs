namespace Cursorial.Animation;

/// <summary>
/// Shapes an animation's progress: maps the linear normalized progress <c>t ∈ [0, 1]</c> to an eased
/// value. The result is usually in <c>[0, 1]</c> too, but may briefly leave the range — e.g.
/// <see cref="Easings.BackOut"/> overshoots past 1 before settling. A plain delegate so custom curves
/// are first-class; see <see cref="Easings"/> for the built-in catalog.
/// </summary>
/// <param name="t">Linear progress, <c>0</c> at the start and <c>1</c> at the end.</param>
/// <returns>The eased progress fed to an <see cref="IInterpolator{T}"/>.</returns>
public delegate double Easing(double t);
