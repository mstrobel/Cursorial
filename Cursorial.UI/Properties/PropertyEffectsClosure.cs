// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The invalidation lattice for sub-object effect routing (design doc §5.5's implication chain,
/// made explicit): <see cref="PropertyEffects.AffectsMeasure"/> ⇒
/// <see cref="PropertyEffects.AffectsArrange"/> ⇒ <see cref="PropertyEffects.AffectsRender"/> (a
/// re-measure re-arranges and, when the arranged size changed, re-rasters), and
/// <see cref="PropertyEffects.AffectsParentMeasure"/> ⇒ <see cref="PropertyEffects.AffectsParentArrange"/>.
/// <see cref="PropertyEffects.AffectsComposite"/> is a parallel lane, deliberately outside both
/// chains: re-composite never re-rasters (invariant 3) and a re-raster needs no separate
/// composite-parameter refresh. A sub-object change routes at the sub-property's declared level
/// <em>clamped</em> by the host slot's ceiling — both sides <see cref="Expand"/>ed through the
/// implication closure, intersected, then <see cref="Reduce"/>d back to generator flags so the
/// dispatch invalidates exactly what the surviving level's ordinary change would.
/// </summary>
internal static class PropertyEffectsClosure
{
    /// <summary>The flags that route to invalidation — the behavior bits
    /// (<see cref="PropertyEffects.Inherits"/>, the binding-engine flags) never travel the
    /// sub-object path.</summary>
    internal const PropertyEffects InvalidationMask =
        PropertyEffects.AffectsMeasure | PropertyEffects.AffectsArrange | PropertyEffects.AffectsRender |
        PropertyEffects.AffectsComposite | PropertyEffects.AffectsParentMeasure | PropertyEffects.AffectsParentArrange;

    /// <summary>
    /// The upward closure of <paramref name="effects"/> under the implication chains (masked to the
    /// invalidation flags first). Closed sets intersect to closed sets, so a chained clamp — hop
    /// after hop of <c>expanded &amp; expanded</c> — stays closed without re-expansion.
    /// </summary>
    internal static PropertyEffects Expand(PropertyEffects effects)
    {
        effects &= InvalidationMask;

        if ((effects & PropertyEffects.AffectsMeasure) != 0)
            effects |= PropertyEffects.AffectsArrange | PropertyEffects.AffectsRender;
        if ((effects & PropertyEffects.AffectsArrange) != 0)
            effects |= PropertyEffects.AffectsRender;
        if ((effects & PropertyEffects.AffectsParentMeasure) != 0)
            effects |= PropertyEffects.AffectsParentArrange;

        return effects;
    }

    /// <summary>
    /// Strips the implied bits from a closed set, leaving the strongest generator per chain — what
    /// the dispatch actually fires. Dispatching the raw closure would over-invalidate: an ordinary
    /// <see cref="PropertyEffects.AffectsMeasure"/> change calls <c>InvalidateMeasure</c> alone and
    /// lets layout re-raster only when the arranged size actually changed, so the sub-object route
    /// must not hand that same level to <c>InvalidateVisual</c> directly.
    /// </summary>
    internal static PropertyEffects Reduce(PropertyEffects effects)
    {
        if ((effects & PropertyEffects.AffectsMeasure) != 0)
            effects &= ~(PropertyEffects.AffectsArrange | PropertyEffects.AffectsRender);
        else if ((effects & PropertyEffects.AffectsArrange) != 0)
            effects &= ~PropertyEffects.AffectsRender;
        if ((effects & PropertyEffects.AffectsParentMeasure) != 0)
            effects &= ~PropertyEffects.AffectsParentArrange;

        return effects;
    }
}
