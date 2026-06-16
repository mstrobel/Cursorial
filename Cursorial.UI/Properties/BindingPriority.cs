// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The priority lane at which a value contribution arbitrates inside the value store. Lower numeric
/// values are <em>stronger</em>: <see cref="Animation"/> beats <see cref="LocalValue"/> beats
/// <see cref="Style"/> beats <see cref="Template"/> beats <see cref="Inherited"/> beats
/// <see cref="Default"/> (design doc §2.2; precedence matrix §0.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Binding is not a priority.</b> A binding entry contributes at whatever priority it was installed
/// at; within one priority, last writer wins and a binding's push counts as a write.
/// </para>
/// <para>
/// <see cref="Inherited"/> and <see cref="Default"/> are <em>resolution-only</em> tiers — they are
/// reported by <c>GetValueSource</c> but are never assignable through <c>SetValue</c> or a frame.
/// Per the matrix's PD1 pin, <c>SetValue</c> accepts <see cref="LocalValue"/> only: frames are the
/// sole <see cref="Style"/> producer and <c>AnimatedValueHandle&lt;T&gt;</c> the sole
/// <see cref="Animation"/> producer (enforcement lands with the store). <see cref="Template"/> is
/// likewise not a <c>SetValue</c> parameter (PD24): it is reached only through the ambient
/// template-instantiation scope.
/// </para>
/// <para>
/// <b>Wire values are deliberately gapped</b> (multiples of 100) so the cut WPF ladder rungs recorded
/// as re-addable in design doc §2.9 can be inserted without renumbering existing members. The
/// <see cref="Template"/> rung (wire 150) is the first such addition. Ordering, not magnitude, is the
/// contract; no consumer may depend on the numeric distance between rungs.
/// </para>
/// </remarks>
public enum BindingPriority
{
    /// <summary>
    /// Storyboard / transition writes — above local, so trigger-driven pulses beat the value they
    /// animate and restoration falls out of handle disposal for free (the base value keeps living
    /// underneath).
    /// </summary>
    Animation = -100,

    /// <summary><c>SetValue</c> and local <c>{Binding}</c> contributions.</summary>
    LocalValue = 0,

    /// <summary>
    /// The single style slot. All style / trigger / template contributions are frames in this one
    /// slot, ordered within it by the styling engine's packed <c>StyleSortKey</c> (layer beats
    /// specificity); the store sorts frames by the key and arbitrates — it never evaluates selectors.
    /// </summary>
    Style = 100,

    /// <summary>
    /// The template-instantiation lane (precedence matrix §20, PD24): weaker than <see cref="Style"/>
    /// so a page/theme Style overrides a template default, stronger than <see cref="Inherited"/> so the
    /// template's authored value still beats an inherited/metadata one. Carries everything a control or
    /// data template <em>authors</em> on its parts — a literal <c>SetValue</c>, a
    /// <c>{TemplateBinding}</c>/<c>{Binding}</c>, a <c>SetResourceReference</c> — and is reached
    /// <b>only</b> through the ambient template-instantiation scope open while the template's content
    /// tree is built (never through a <c>SetValue</c> priority argument). The deliberate inverse of WPF,
    /// where template-property values outrank Style.
    /// </summary>
    Template = 150,

    /// <summary>
    /// Resolution-only: the walk-up result for an inheriting property. Never assignable.
    /// </summary>
    Inherited = 200,

    /// <summary>Resolution-only: the per-type metadata default. Never assignable.</summary>
    Default = 300,

    /// <summary>
    /// Internal sentinel ("no contribution"). Never assignable and never reported by
    /// <c>GetValueSource</c>.
    /// </summary>
    Unset = int.MaxValue
}
