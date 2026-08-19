using System.Collections.Frozen;
using System.Runtime.CompilerServices;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// A typed styled property: full value-store citizenship — priority lanes, frames, animation,
/// inheritance, and per-type metadata. Registered via <see cref="UIProperty.Register{TOwner,T}(string,T,bool,Func{UIObject,T,T},Func{T,bool},PropertyChangedCallback{T})"/>
/// and friends; not externally subclassable (<see cref="AttachedProperty{T}"/> is the one derived kind).
/// </summary>
public class StyledProperty<T> : UIProperty
{
    private readonly PropertyMetadata<T> _registeredMetadata;
    private Dictionary<Type, PropertyMetadata<T>>? _overrides;

    /// <summary>
    /// The per-type RESOLVED metadata table: rebuilt as a <see cref="FrozenDictionary{TKey,TValue}"/>
    /// when a new type first resolves (cold, bounded by the number of concrete types), giving
    /// frozen-cost reads thereafter. A type's presence here is the "touched" marker that freezes
    /// metadata overrides for it and its ancestors. Values are the immutable
    /// <see cref="CachedResolution"/> pairs so a dictionary hit republishes the existing instance
    /// into the inline cache — polymorphic call sites (the render/hit walks alternate element
    /// types) stay allocation-free.
    /// </summary>
    private FrozenDictionary<Type, CachedResolution> _resolved = FrozenDictionary<Type, CachedResolution>.Empty;

    /// <summary>
    /// Monomorphic last-type inline cache, held as a single immutable pair so the publication is
    /// atomic — instances live on one UI thread, but the property statics are process-global and
    /// may be touched from several threads (e.g. parallel test hosts); a torn two-field cache could
    /// pair one type with another type's metadata.
    /// </summary>
    private CachedResolution? _lastResolution;

    internal StyledProperty(string name, Type ownerType, PropertyMetadata<T> metadata, bool inherits, bool isAttached, bool isReadOnly, bool targetsChildren = false)
        : base(name, typeof(T), ownerType, inherits, isAttached, isDirect: false, isReadOnly, targetsChildren)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _registeredMetadata = metadata;
        if (metadata.DefaultResourceKey is not null)
            UIPropertyRegistry.RegisterDefaultResourceKeyed(this);
    }

    /// <summary>Returns the metadata from the original property's owner's registration.</summary>
    public PropertyMetadata<T> DefaultMetadata => _registeredMetadata;

    /// <summary>
    /// The merged metadata for <paramref name="forType"/>, resolved through the type's inheritance
    /// chain (registration metadata as the root, overrides applied base-first — <c>Changed</c>
    /// callbacks chain base-first, defaults replace, other members fall through when
    /// <see langword="null"/>) and cached per concrete type with a monomorphic last-type inline
    /// cache. Caching a type's resolution seals further metadata <em>overrides</em> for it (the per-type
    /// <see cref="OverrideMetadata{TOwner}"/> gate); it does NOT freeze the effects lane (the M201 decouple).
    /// </summary>
    public PropertyMetadata<T> GetMetadata(Type forType)
    {
        ArgumentNullException.ThrowIfNull(forType);

        if (_lastResolution is { } last && ReferenceEquals(forType, last.Type))
            return last.Metadata;

        if (!_resolved.TryGetValue(forType, out var resolution))
        {
            resolution = new CachedResolution(forType, ResolveMetadata(forType));
            var grown = new Dictionary<Type, CachedResolution>(_resolved) { [forType] = resolution };
            _resolved = grown.ToFrozenDictionary();
            // NB: metadata resolution no longer freezes the EFFECTS lane (the M201 decouple) — it only seals
            // metadata-override for this type (the `_resolved.Keys` gate in OverrideMetadata). Effects freeze
            // per-type on their own resolution (GetEffects), so a sibling owner reading a value can't lock out
            // a later sibling's AffectsX registration (the TypeInitializationException cascade fix).
        }

        _lastResolution = resolution; // a reference republish — atomic, allocation-free on hits
        return resolution.Metadata;
    }

    /// <summary>
    /// Overrides metadata for <typeparamref name="TOwner"/> and the types below it. Throws
    /// <see cref="InvalidOperationException"/> once any <typeparamref name="TOwner"/> instance has
    /// touched the property (a <c>GetValue</c> counts — instances of derived types are
    /// <typeparamref name="TOwner"/> instances too), which removes the cache-invalidation problem
    /// class entirely. A second override for the same type throws <see cref="ArgumentException"/>.
    /// </summary>
    public void OverrideMetadata<TOwner>(PropertyMetadata<T> metadata) where TOwner : UIObject
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.DefaultResourceKey is not null)
            UIPropertyRegistry.RegisterDefaultResourceKeyed(this);
        var forType = typeof(TOwner);

        foreach (var touched in _resolved.Keys)
        {
            if (forType.IsAssignableFrom(touched))
            {
                throw new InvalidOperationException(
                    $"Cannot override metadata for '{this}' on '{forType.Name}': an instance of " +
                    $"'{touched.Name}' (a '{forType.Name}') has already resolved this property's metadata. " +
                    "Override metadata from the type's static constructor, before any instance touches the property.");
            }
        }

        var overrides = _overrides ??= [];
        if (!overrides.TryAdd(forType, metadata))
            throw new ArgumentException($"Metadata for '{this}' has already been overridden for type '{forType.Name}'.", nameof(metadata));
    }

    /// <summary>
    /// Sugar for the default-only override: replaces <see cref="PropertyMetadata{T}.DefaultValue"/>
    /// for <typeparamref name="TOwner"/> while every other member falls through to the base metadata.
    /// (Referred to as <c>OverrideDefault&lt;T&gt;</c> in older spec text — name drift only.)
    /// </summary>
    public void OverrideDefaultValue<TOwner>(T defaultValue) where TOwner : UIObject
        => OverrideMetadata<TOwner>(new PropertyMetadata<T>(defaultValue));

    /// <summary>
    /// Registers <typeparamref name="TOwner"/> as an additional owner for XAML / registry lookup and
    /// returns the <em>same</em> property instance (shared dense id — a value set through either
    /// alias reads back through the other). Per-owner metadata is layered afterwards via
    /// <see cref="OverrideMetadata{TOwner}"/> / <see cref="OverrideDefaultValue{TOwner}"/>.
    /// </summary>
    public StyledProperty<T> AddOwner<TOwner>() where TOwner : UIObject
    {
        UIPropertyRegistry.AddOwner(this, typeof(TOwner));
        return this;
    }

    internal override object? GetDefaultValueUntyped(Type forType) => GetMetadata(forType).DefaultValue;

    internal override object? GetDefaultResourceKeyUntyped(Type forType) => GetMetadata(forType).DefaultResourceKey;

    internal override bool AreValuesEqualUntyped(Type forType, object? a, object? b)
    {
        if (ReferenceEquals(a, b))
            return true;

        // The boxed values are this property's values (T). Honor the effective comparer when both
        // unbox cleanly; otherwise (a sentinel/mismatched box slipped in) fall back to object.Equals
        // so the comparison can't throw on the echo-suppression hot path.
        if (a is T ta && b is T tb)
            return GetMetadata(forType).EffectiveComparer.Equals(ta, tb);

        // A null on one side: for a reference T, null is a legitimate value, so route it through the
        // comparer with the typed default (= null) on the null side when the other side is T.
        if (typeof(T).IsNullableType())
        {
            if (a is null && b is T tbn)
                return GetMetadata(forType).EffectiveComparer.Equals(default!, tbn);
            if (b is null && a is T tan)
                return GetMetadata(forType).EffectiveComparer.Equals(tan, default!);
            if (a is null && b is null)
                return true;
        }

        return Equals(a, b);
    }

    internal override object? GetValueUntyped(UIObject host) => host.GetValueBoxed(this);

    internal override object? GetValueUntyped(UIObject host, BindingPriority maxPriority) => host.GetValue(this, maxPriority);

    internal override void SetValueUntyped(UIObject host, object? value) => host.SetValue(this, (T)value!);

    internal override BindingEntryBase CreateEntry(
        UIObject target, BindingPriority priority, ValueFrame? hostFrame, IValueEvictionListener? listener)
        => hostFrame is null ? target.Bind(this, priority, listener) : target.BindInFrame(this, hostFrame, listener);

    internal override BindingEntryBase CreateTemplateTransfer(
        UIObject templatedParent, UIObject target, IValueEvictionListener? listener)
        => throw new NotImplementedException(
            "TemplateBinding transfer entries land with the Fork B template engine " +
            "(ITemplateContent / TemplateInstance — ledger A16's second leg; the shape is pinned here).");

    internal override void Reevaluate(ValueStore store, IValueEntry? changedEntry) => store.Reevaluate(this, changedEntry);

    internal override void NotifyInheritanceParentChanged(UIObject node, UIObject? oldParent)
        => node.OnInheritanceParentChanged(this, oldParent);

    internal override object? GetEntryValueBoxed(IValueEntry entry)
        => entry.HasValue ? ValueBoxes.Box(((IValueEntry<T>)entry).GetValue()) : null;

    internal override IValueEntry CreateStyleEntry(object? boxedValue, bool hasValue)
        => new StyleSetterEntry<T>(this, hasValue ? (T)boxedValue! : default!, hasValue);

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2059",
        Justification = "forType is the runtime type of a live UIObject (or a registered owner walking its base chain): " +
                        "a type that exists in the running app keeps its static constructor — the trimmer removes " +
                        "cctors only together with their types.")]
    private PropertyMetadata<T> ResolveMetadata(Type forType)
    {
        // Force the inheritance chain's static ctors FIRST — a type registers its OverrideMetadata/OverrideDefaultValue
        // only in its own static ctor, so a cold by-Type resolution (style/diagnostics) before that ctor has run would
        // otherwise see an empty `_overrides` and return the registration default. This MUST precede the early return
        // below (the bug: if `_overrides` is empty because no chain ctor has run yet, the early return fires before the
        // force ever happens). Cache-miss only (off the hot path); same-thread re-entrant RunClassConstructor is a CLR
        // no-op. (object has no UIProperty registrations — skip it.)
        for (var type = forType; type is not null && type != typeof(object); type = type.BaseType)
            RuntimeHelpers.RunClassConstructor(type.TypeHandle);

        if (_overrides is not { Count: > 0 } overrides)
            return _registeredMetadata;

        // Collect applicable overrides derived-first by walking the chain, then merge base-first.
        List<PropertyMetadata<T>>? applicable = null;
        for (var type = forType; type is not null; type = type.BaseType)
        {
            if (overrides.TryGetValue(type, out var overrideMetadata))
                (applicable ??= []).Add(overrideMetadata);
        }

        if (applicable is null)
            return _registeredMetadata;

        var result = _registeredMetadata;
        for (var i = applicable.Count - 1; i >= 0; i--)
            result = Merge(result, applicable[i]);
        return result;
    }

    private sealed record CachedResolution(Type Type, PropertyMetadata<T> Metadata);

    private static PropertyMetadata<T> Merge(PropertyMetadata<T> baseMetadata, PropertyMetadata<T> overrideMetadata) => new(
        overrideMetadata.DefaultValue, // defaults replace (pinned, design doc §2)
        overrideMetadata.Coerce ?? baseMetadata.Coerce,
        overrideMetadata.Validate ?? baseMetadata.Validate,
        (PropertyChangedCallback<T>?)Delegate.Combine(baseMetadata.Changed, overrideMetadata.Changed), // chains base-first
        overrideMetadata.Comparer ?? baseMetadata.Comparer)
    {
        ParsesAccessKeyLiterals = overrideMetadata.ParsesAccessKeyLiterals ?? baseMetadata.ParsesAccessKeyLiterals,
        DefaultResourceKey = overrideMetadata.DefaultResourceKey ?? baseMetadata.DefaultResourceKey,
    };
}
