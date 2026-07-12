// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The priority lane at which a value contribution arbitrates inside the value store. Lower numeric
/// values are <em>stronger</em>: <see cref="Animation"/> beats <see cref="LocalValue"/> beats
/// <see cref="StyleTrigger"/> beats <see cref="Template"/> beats <see cref="Style"/> beats
/// <see cref="Inherited"/> beats <see cref="Default"/> (design doc §2.2; precedence matrix §0.3,
/// amended 2026-07-12 — the Avalonia lattice, completed).
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
/// sole <see cref="StyleTrigger"/>/<see cref="Style"/> producer and <c>AnimatedValueHandle&lt;T&gt;</c>
/// the sole <see cref="Animation"/> producer (enforcement lands with the store). <see cref="Template"/>
/// is likewise not a <c>SetValue</c> parameter (PD24): it is reached only through the ambient
/// template-instantiation scope.
/// </para>
/// <para>
/// <b>Wire values are deliberately gapped</b> so rungs can be inserted without renumbering existing
/// members; ordering, not magnitude, is the contract — no consumer may depend on the numeric distance
/// between rungs (design doc §2.9). <see cref="Template"/> moved wire value (150 → 75) when
/// <see cref="StyleTrigger"/> landed, per that contract.
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
    /// The CONDITIONAL style slot (precedence matrix §0.3, 2026-07-12): frames whose rule carries an
    /// activation condition — any pseudo-class or <c>.class</c> simple on any compound, or a
    /// <c>When</c> data-condition. Sits ABOVE <see cref="Template"/> so state-driven looks
    /// (<c>:pointerover</c>, <c>:pressed</c>, <c>.obscured</c>, <c>.caps-nocolor</c>) pierce a
    /// template's authored part values while they are active, and retract cleanly to them. The
    /// trigger role of WPF's template/style triggers, in Avalonia's activator formulation: a rule is
    /// conditional by SHAPE (selector/When content), not by provenance — theme and app rules alike.
    /// Within the slot, frames order by the packed <c>StyleSortKey</c> exactly as in
    /// <see cref="Style"/>.
    /// </summary>
    StyleTrigger = 50,

    /// <summary>
    /// The template-instantiation lane (precedence matrix §20, PD24 as amended 2026-07-12): carries
    /// everything a control template <em>authors</em> on its parts — a literal <c>SetValue</c>, a
    /// <c>{TemplateBinding}</c>/<c>{Binding}</c>, a <c>SetResourceReference</c> — and is reached
    /// <b>only</b> through the ambient template-instantiation scope open while the template's content
    /// tree is built (never through a <c>SetValue</c> priority argument). Weaker than
    /// <see cref="StyleTrigger"/> so conditional rules re-ink parts while active; STRONGER than
    /// resting <see cref="Style"/> so a template author's literals and TemplateBinding plumbing are
    /// the part's resting truth — a broad structural rule cannot wreck a template's internal wiring.
    /// Re-skinning at rest flows through the control's own properties (which resting styles CAN set)
    /// via <c>{TemplateBinding}</c> forwarding. The completed Avalonia lattice
    /// (StyleTrigger &gt; Template &gt; Style); the 2026-06-16 half-adoption (all styles above
    /// Template) is recorded in the §20 history.
    /// </summary>
    Template = 75,

    /// <summary>
    /// The RESTING style slot: frames whose rule is purely structural (types, <c>#name</c>s,
    /// combinators, <c>/template/</c> hops — no pseudo-class, no <c>.class</c>, no <c>When</c>).
    /// Ordered within the slot by the styling engine's packed <c>StyleSortKey</c> (layer beats
    /// specificity); the store sorts frames by the key and arbitrates — it never evaluates selectors.
    /// </summary>
    Style = 100,

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
