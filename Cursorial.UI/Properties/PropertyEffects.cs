// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// Invalidation and behavior flags carried by property metadata. The property engine itself never
/// references scenes, invalidation, or rendering (invariant 2) — these flags are <em>routed
/// exclusively by the element tree</em>: <see cref="AffectsComposite"/>-shaped changes refresh
/// <c>CompositeParameters</c> on a cached raster, while only content/brush-shaped
/// <see cref="AffectsRender"/> changes re-raster (invariant 3 — animated slides, fades, and reveals
/// must never re-raster).
/// </summary>
/// <remarks>
/// Effects live in <b>two lanes</b> (ledger A1): a per-type lane resolved through the runtime type's
/// inheritance chain, and a property-global lane (<c>UIProperty.GlobalEffects</c>).
/// <c>UIProperty.GetEffects(Type)</c> returns the OR of both. The global lane is what makes attached
/// properties work — a host type's frozen per-type table structurally never sees the declaring
/// panel's registration, so without it <c>Grid.SetRow(button, 2)</c> would invalidate nothing.
/// Both lanes are writable only during the property's registration window (closed at the first
/// metadata resolution or effects query).
/// </remarks>
[Flags]
public enum PropertyEffects
{
    /// <summary>No effects.</summary>
    None = 0,

    /// <summary>A change invalidates the element's measure.</summary>
    AffectsMeasure = 1 << 0,

    /// <summary>A change invalidates the element's arrange.</summary>
    AffectsArrange = 1 << 1,

    /// <summary>
    /// A change re-rasters the element's scene (<c>Scene.Invalidate()</c>). Content/brush-shaped
    /// changes only — offset/opacity/clip-shaped properties use <see cref="AffectsComposite"/>.
    /// </summary>
    AffectsRender = 1 << 2,

    /// <summary>
    /// A change refreshes <c>CompositeParameters</c> on the cached raster (offset / opacity / clip).
    /// Mandatory for animatable placement state: re-composite, never re-raster (invariant 3).
    /// </summary>
    AffectsComposite = 1 << 3,

    /// <summary>A change invalidates the parent's measure (e.g. <c>Grid.Row</c>-shaped properties).</summary>
    AffectsParentMeasure = 1 << 4,

    /// <summary>A change invalidates the parent's arrange.</summary>
    AffectsParentArrange = 1 << 5,

    /// <summary>
    /// The property inherits down the element tree. Mirrors <c>UIProperty.Inherits</c>, which is
    /// fixed at registration (never per-type-overridable, §2.6) — <c>GetEffects</c> reports this
    /// flag for inheriting properties on every type.
    /// </summary>
    Inherits = 1 << 6,

    /// <summary>Bindings on this property default to <c>TwoWay</c> mode (consumed by the binding engine).</summary>
    BindsTwoWayByDefault = 1 << 7,

    /// <summary>The property rejects data binding (consumed by the binding engine).</summary>
    NotDataBindable = 1 << 8,
}
