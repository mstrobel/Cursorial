namespace Cursorial.UI;

/// <summary>
/// The diagnostic answer to "where is this property's effective value coming from?" — the winning
/// <see cref="BindingPriority"/> lane plus the <see cref="IsCurrentValue"/> bit a
/// <c>SetCurrentValue</c> overwrite leaves behind (notated <c>+cur</c> in the precedence matrix),
/// annotated with the §2.1 diagnostics grafts: the winning <em>base</em> lane
/// (<see cref="BasePriority"/>), <see cref="IsAnimated"/>, and <see cref="IsCoerced"/>.
/// <see cref="BindingPriority.Unset"/> is never reported: a property with no contribution reports
/// <see cref="BindingPriority.Default"/>.
/// </summary>
/// <remarks>
/// <b>Equality compares exactly the matrix-pinned pair</b> (<see cref="Priority"/>,
/// <see cref="IsCurrentValue"/>) — the <c>src</c> notation (matrix PD23). The annotations are
/// informational: hand-constructed comparands carry defaults
/// (<see cref="BasePriority"/> = <paramref name="Priority"/>) without affecting comparisons.
/// Inherited-sourced reads report <see cref="IsAnimated"/> / <see cref="IsCoerced"/> as
/// <see langword="false"/> — those details live at the contributing ancestor.
/// </remarks>
/// <param name="Priority">The lane the effective value resolved from.</param>
/// <param name="IsCurrentValue">Whether the effective value was overwritten in place by
/// <c>SetCurrentValue</c> (provenance unchanged, value replaced — design doc §2.2). Cleared when the
/// lane re-asserts itself (a fresh write or re-emit from the replaced lane).</param>
public readonly record struct ValueSource(BindingPriority Priority, bool IsCurrentValue)
{
    /// <summary>
    /// The winning <em>base</em> lane — the strongest sub-Animation contribution (the lane the
    /// value falls back to when an animation ends). Equal to <see cref="Priority"/> while no
    /// animation holds the property. Diagnostics annotation; excluded from equality (PD23).
    /// </summary>
    public BindingPriority BasePriority { get; init; } = Priority;

    /// <summary>
    /// Whether the effective value was modified by the metadata coercer when it was produced.
    /// Diagnostics annotation; excluded from equality (PD23).
    /// </summary>
    public bool IsCoerced { get; init; }

    /// <summary>Whether an animation currently holds the property (≡ <see cref="Priority"/> is
    /// <see cref="BindingPriority.Animation"/>).</summary>
    public bool IsAnimated => Priority == BindingPriority.Animation;

    /// <summary>Equality over the matrix-pinned <c>src</c> pair only — <see cref="Priority"/> and
    /// <see cref="IsCurrentValue"/>; the diagnostics annotations do not participate (PD23).</summary>
    public bool Equals(ValueSource other) => Priority == other.Priority && IsCurrentValue == other.IsCurrentValue;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Priority, IsCurrentValue);
}
