// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The within-lane provenance of an effective value (precedence matrix §20.6 / PD25) — refines the
/// <see cref="BindingPriority"/> lane with <em>how</em> the winning contribution was produced. The
/// lane already answers "template default vs style vs local"; <see cref="ValueSourceKind"/> answers
/// the finer question (a literal vs a binding vs a resource inside the Template lane; a plain setter
/// vs a <c>When</c>-guarded rule inside the Style lane). A non-equality annotation on
/// <see cref="ValueSource"/> (PD25); a contribution whose finer origin is unknown reports the lane's
/// generic kind.
/// </summary>
public enum ValueSourceKind
{
    /// <summary>No contribution — the metadata default (the <see cref="BindingPriority.Default"/> lane). The neutral zero default.</summary>
    Default,

    /// <summary>A value inherited from an ancestor (the <see cref="BindingPriority.Inherited"/> lane).</summary>
    Inherited,

    /// <summary>A local <c>SetValue</c>/<c>{Binding}</c>/<c>SetResourceReference</c> outside any template (the <see cref="BindingPriority.LocalValue"/> lane).</summary>
    Local,

    /// <summary>A literal value a template authored on a part (the <see cref="BindingPriority.Template"/> lane, not via a binding entry).</summary>
    TemplateLiteral,

    /// <summary>A <c>{TemplateBinding}</c>/<c>{Binding}</c> a template installed on a part (the <see cref="BindingPriority.Template"/> lane, via a binding entry).</summary>
    TemplateBinding,

    /// <summary>A <c>SetResourceReference</c>/<c>{DynamicResource}</c> a template installed on a part (the <see cref="BindingPriority.Template"/> lane, via a resource producer).</summary>
    TemplateResource,

    /// <summary>A plain style <c>Setter</c> (the <see cref="BindingPriority.Style"/> lane, an unconditional rule).</summary>
    StyleSetter,

    /// <summary>A <c>When</c>-guarded style rule — the data-condition "trigger" equivalent (the <see cref="BindingPriority.Style"/> lane, a conditional rule).</summary>
    StyleWhen,

    /// <summary>A base whole-style bundle authored on the element (the <see cref="BindingPriority.BaseTextStyle"/> lane) — below every style/trigger/template contribution, above inherited/default.</summary>
    BaseTextStyle,

    /// <summary>A storyboard/transition write (the <see cref="BindingPriority.Animation"/> lane).</summary>
    Animation,
}

/// <summary>
/// The diagnostic answer to "where is this property's effective value coming from?" — the winning
/// <see cref="BindingPriority"/> lane plus the <see cref="IsCurrentValue"/> bit a
/// <c>SetCurrentValue</c> overwrite leaves behind (notated <c>+cur</c> in the precedence matrix),
/// annotated with the §2.1 diagnostics grafts: the winning <em>base</em> lane
/// (<see cref="BasePriority"/>), <see cref="IsAnimated"/>, <see cref="IsCoerced"/>, and the
/// within-lane <see cref="Kind"/> (PD25).
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

    /// <summary>
    /// The within-lane provenance (PD25) — refines <see cref="Priority"/> (e.g. a Template-lane value
    /// as a literal vs a binding vs a resource; a Style-lane value as a setter vs a <c>When</c>-guarded
    /// rule). Diagnostics annotation; excluded from equality (PD23/PD25). Hand-constructed comparands
    /// leave it at <see cref="ValueSourceKind.Default"/>; <c>GetValueSource</c> always sets it.
    /// </summary>
    public ValueSourceKind Kind { get; init; }

    /// <summary>Whether an animation currently holds the property (≡ <see cref="Priority"/> is
    /// <see cref="BindingPriority.Animation"/>).</summary>
    public bool IsAnimated => Priority == BindingPriority.Animation;

    /// <summary>Equality over the matrix-pinned <c>src</c> pair only — <see cref="Priority"/> and
    /// <see cref="IsCurrentValue"/>; the diagnostics annotations do not participate (PD23).</summary>
    public bool Equals(ValueSource other) => Priority == other.Priority && IsCurrentValue == other.IsCurrentValue;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Priority, IsCurrentValue);
}
