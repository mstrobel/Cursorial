// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// Non-generic base for all property kinds (<see cref="StyledProperty{T}"/>,
/// <see cref="AttachedProperty{T}"/>, <see cref="DirectProperty{TOwner,T}"/>); also hosts the
/// registration statics and the <see cref="UnsetValue"/> sentinel. All public property-system types
/// live in the <c>Cursorial.UI</c> namespace (design doc §2).
/// </summary>
public abstract class UIProperty
{
    /// <summary>
    /// Singleton sentinel meaning "this source contributes nothing". Feeding it to a binding entry
    /// reports <c>HasValue == false</c> and promotes the next source (ledger A8); the untyped
    /// <c>SetValue(p, UnsetValue, LocalValue)</c> spelling is equivalent to <c>ClearValue</c> in
    /// full (matrix PD5). It is a write-side sentinel only — reads never return it.
    /// </summary>
    public static readonly object UnsetValue = UnsetValueSentinel.Instance;

    /// <summary>
    /// The unregistered sentinel property (ledger A14): <see cref="Id"/> is −1 and it is absent from
    /// the registry. Backs watch-only binding expressions that have no target property.
    /// </summary>
    internal static readonly UIProperty UnsetTargetProperty = new SentinelProperty("(unset)");

    private Dictionary<Type, PropertyEffects>? _perTypeEffects;
    private Dictionary<Type, PropertyEffects>? _resolvedEffects;
    private PropertyEffects _globalEffects;
    private bool _registrationWindowClosed;

    private protected UIProperty(
        string name, Type propertyType, Type ownerType,
        bool inherits, bool isAttached, bool isDirect, bool isReadOnly)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(propertyType);
        ArgumentNullException.ThrowIfNull(ownerType);

        Name = name;
        PropertyType = propertyType;
        OwnerType = ownerType;
        Inherits = inherits;
        IsAttached = isAttached;
        IsDirect = isDirect;
        IsReadOnly = isReadOnly;
        Id = UIPropertyRegistry.Register(this);
    }

    /// <summary>
    /// The internal sentinel constructor (ledger A14): creates an <em>unregistered</em> property with
    /// <see cref="Id"/> −1 that participates in no registry lookup. Used only by
    /// <see cref="UnsetTargetProperty"/>.
    /// </summary>
    private protected UIProperty(string name, Type propertyType)
    {
        Name = name;
        PropertyType = propertyType;
        OwnerType = typeof(UIObject);
        Id = -1;
        _registrationWindowClosed = true;
    }

    /// <summary>
    /// Dense, process-global identifier assigned at registration (array-index friendly). −1 marks the
    /// unregistered sentinel (A14).
    /// </summary>
    public int Id { get; }

    /// <summary>The property's name, unique per owner type. Contains no <c>'.'</c>.</summary>
    public string Name { get; }

    /// <summary>The CLR type of the property's values.</summary>
    public Type PropertyType { get; }

    /// <summary>The declaring (registering) type. Additional owners register via <c>AddOwner</c>.</summary>
    public Type OwnerType { get; }

    /// <summary>
    /// Whether the property's value inherits down the element tree. Fixed at registration —
    /// deliberately not per-type-overridable (§2.6): the inheriting set stays globally enumerable
    /// (reparent diff) and no inheritance-cache-invalidation protocol exists.
    /// </summary>
    public bool Inherits { get; }

    /// <summary>Whether this is an attached property (settable on host instances other than the owner).</summary>
    public bool IsAttached { get; }

    /// <summary>Whether this is a direct property (plain field storage, no value store participation).</summary>
    public bool IsDirect { get; }

    /// <summary>
    /// Whether writes are structurally restricted: for styled properties, registration returned a
    /// <see cref="UIPropertyKey{T}"/> and only key-holders may write (matrix PD14 — enforcement lives
    /// in the store); for direct properties, no setter delegate was registered.
    /// </summary>
    public bool IsReadOnly { get; }

    /// <summary>
    /// The global effects lane (ledger A1) — effects that apply on every host type, which is what
    /// makes attached properties invalidate correctly. Writable only during the registration window;
    /// assignment after the window closes throws <see cref="InvalidOperationException"/>.
    /// </summary>
    internal PropertyEffects GlobalEffects
    {
        get => _globalEffects;
        set
        {
            ThrowIfRegistrationWindowClosed();
            _globalEffects = value;
        }
    }

    /// <summary>Whether the registration window has closed (first metadata resolution or effects query).</summary>
    internal bool IsRegistrationWindowClosed => _registrationWindowClosed;

    /// <summary>
    /// ORs <paramref name="effects"/> into the per-type lane for <paramref name="forType"/> (ledger
    /// A1). Per-type effects are resolved through the runtime type's inheritance chain — effects
    /// registered for a base type apply to derived types, never the reverse. Throws once the
    /// registration window has closed.
    /// </summary>
    internal void AddPerTypeEffects(Type forType, PropertyEffects effects)
    {
        ArgumentNullException.ThrowIfNull(forType);
        ThrowIfRegistrationWindowClosed();

        var perType = _perTypeEffects ??= [];
        perType[forType] = perType.TryGetValue(forType, out var existing) ? existing | effects : effects;
    }

    /// <summary>
    /// The effective <see cref="PropertyEffects"/> for <paramref name="forType"/>: the per-type lane
    /// resolved through the inheritance chain, ORed with the global lane (A1:
    /// <c>perType | Global</c>), plus <see cref="PropertyEffects.Inherits"/> when the property
    /// inherits. The first query closes the registration window; results are cached per type.
    /// </summary>
    public PropertyEffects GetEffects(Type forType)
    {
        ArgumentNullException.ThrowIfNull(forType);
        _registrationWindowClosed = true;

        var cache = _resolvedEffects ??= [];
        if (cache.TryGetValue(forType, out var effects))
            return effects;

        effects = _globalEffects | (Inherits ? PropertyEffects.Inherits : PropertyEffects.None);
        if (_perTypeEffects is { Count: > 0 } perType)
        {
            for (var type = forType; type is not null; type = type.BaseType)
            {
                if (perType.TryGetValue(type, out var typeEffects))
                    effects |= typeEffects;
            }
        }

        cache[forType] = effects;
        return effects;
    }

    /// <summary>
    /// The resolved metadata default for <paramref name="forType"/>, boxed — the non-generic metadata
    /// access for the untyped lane and diagnostics. Direct properties return their registered
    /// fallback (<c>unsetValue</c>); the sentinel returns <see langword="null"/>.
    /// </summary>
    internal abstract object? GetDefaultValueUntyped(Type forType);

    // ─────────────────── untyped→typed bridge (A16 spirit; no reflection) ───────────────────
    //
    // The untyped UIObject mouths dispatch through these virtuals; StyledProperty<T> and
    // DirectProperty<TOwner,T> recover the typed context. The base (and therefore the A14
    // sentinel) does not support value access.

    /// <summary>The untyped read: the boxed effective value (box-interned for styled properties).</summary>
    internal virtual object? GetValueUntyped(UIObject host)
        => throw new NotSupportedException($"'{this}' does not support untyped value access.");

    /// <summary>The untyped lane-probing read (PD16 semantics; direct properties have no ladder).</summary>
    internal virtual object? GetValueUntyped(UIObject host, BindingPriority maxPriority)
        => throw new NotSupportedException($"'{this}' does not support untyped value access.");

    /// <summary>
    /// The untyped write. <paramref name="value"/> was already type-checked at the mouth and is
    /// never <see cref="UnsetValue"/> for styled properties (the mouth translates that spelling to
    /// <c>ClearValue</c>, PD5); direct properties receive the sentinel and substitute their
    /// registered fallback (matrix M218).
    /// </summary>
    internal virtual void SetValueUntyped(UIObject host, object? value)
        => throw new NotSupportedException($"'{this}' does not support untyped value access.");

    /// <summary>
    /// The untyped→typed binding-entry bridge (ledger A16): creates a typed
    /// <see cref="BindingEntry{T}"/> through the public mouths (all PD14/A6 checks apply), with no
    /// reflection. Only styled properties participate — direct properties and the sentinel have no
    /// store entries (A24).
    /// </summary>
    internal virtual BindingEntryBase CreateEntry(
        UIObject target, BindingPriority priority, ValueFrame? hostFrame, IValueEvictionListener? listener)
        => throw new NotSupportedException($"'{this}' does not support binding entries.");

    /// <summary>
    /// The untyped→typed re-evaluation bridge: frame operations know only <see cref="UIProperty"/>;
    /// styled properties route into the store's typed promotion engine.
    /// </summary>
    internal virtual void Reevaluate(ValueStore store, IValueEntry? changedEntry)
        => throw new NotSupportedException($"'{this}' does not participate in store re-evaluation.");

    /// <summary>
    /// The second A16 leg: the <c>TemplateBinding</c> fast-path transfer entry between a templated
    /// parent and a template part, with no reflection. The shape is pinned by the ledger; the
    /// implementation lands with Fork B's template machinery (<c>ITemplateContent</c> /
    /// <c>TemplateInstance</c>) — until then styled properties throw
    /// <see cref="NotImplementedException"/> and non-styled kinds <see cref="NotSupportedException"/>.
    /// </summary>
    internal virtual BindingEntryBase CreateTemplateTransfer(
        UIObject templatedParent, UIObject target, IValueEvictionListener? listener)
        => throw new NotSupportedException($"'{this}' does not support template transfer entries.");

    /// <summary>
    /// The typed leg of the reparent re-pull (ledger A3/A4; matrix M111/M185–M187):
    /// <see cref="UIObject.SetInheritanceParent"/> diffs every property in
    /// <c>UIPropertyRegistry.InheritingPropertyIds</c> through this bridge. Only styled properties
    /// can inherit, so only they override.
    /// </summary>
    internal virtual void NotifyInheritanceParentChanged(UIObject node, UIObject? oldParent)
        => throw new NotSupportedException($"'{this}' does not participate in inheritance.");

    /// <summary>
    /// The untyped value read for a frame entry (diagnostics enumeration, matrix M264):
    /// <see langword="null"/> for valueless entries, the boxed (interned) raw value otherwise.
    /// </summary>
    internal virtual object? GetEntryValueBoxed(IValueEntry entry)
        => throw new NotSupportedException($"'{this}' does not support frame entries.");

    /// <summary>
    /// The untyped→typed style-entry bridge (Fork B's frame construction, design doc §3.6): creates
    /// the typed <c>IValueEntry&lt;T&gt;</c> for one compiled setter with no reflection. The boxed
    /// value was seal-converted to the property type already (style matrix SD9);
    /// <paramref name="hasValue"/> <see langword="false"/> creates the valueless (A8) entry.
    /// </summary>
    internal virtual IValueEntry CreateStyleEntry(object? boxedValue, bool hasValue)
        => throw new NotSupportedException($"'{this}' does not support frame entries.");

    /// <summary>
    /// Closes the registration window: both effects lanes become read-only. Invoked on the first
    /// metadata resolution (a <c>GetValue</c> counts — it resolves the default) and the first
    /// <see cref="GetEffects"/> query.
    /// </summary>
    internal void CloseRegistrationWindow() => _registrationWindowClosed = true;

    /// <inheritdoc/>
    public override string ToString() => $"{OwnerType.Name}.{Name}";

    private void ThrowIfRegistrationWindowClosed()
    {
        if (_registrationWindowClosed)
        {
            throw new InvalidOperationException(
                $"The registration window for '{this}' has closed (the property's metadata or effects " +
                "have been resolved); its effects lanes are frozen. Register effects from the owner " +
                "type's static constructor, before any instance touches the property.");
        }
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('.'))
            throw new ArgumentException($"Property name '{name}' must not contain '.' (reserved for attached-property syntax).", nameof(name));
    }

    // ───────────────────────────── registration statics ─────────────────────────────

    /// <summary>
    /// Registers a styled property declared by <typeparamref name="TOwner"/>. There is deliberately
    /// no effects parameter (ledger A2) — effects are written through the <c>Affects*</c> sugar /
    /// the two effects lanes during the registration window.
    /// </summary>
    public static StyledProperty<T> Register<TOwner, T>(
        string name,
        T defaultValue = default!,
        bool inherits = false,
        Func<UIObject, T, T>? coerce = null,
        Func<T, bool>? validate = null,
        PropertyChangedCallback<T>? changed = null)
        where TOwner : UIObject
        => Register<TOwner, T>(name, new PropertyMetadata<T>(defaultValue, coerce, validate, changed), inherits);

    /// <summary>Registers a styled property with explicit registration metadata.</summary>
    public static StyledProperty<T> Register<TOwner, T>(string name, PropertyMetadata<T> metadata, bool inherits = false)
        where TOwner : UIObject
        => new(name, typeof(TOwner), metadata, inherits, isAttached: false, isReadOnly: false);

    /// <summary>
    /// Registers an attached property declared by <typeparamref name="TOwner"/> (which need not be a
    /// <see cref="UIObject"/>) and settable on <typeparamref name="THost"/> instances (DEBUG-asserted
    /// at the store's write mouth). Storage is instance-keyed by dense id, so attached values need
    /// nothing special; invalidation rides the global effects lane (A1).
    /// </summary>
    public static AttachedProperty<T> RegisterAttached<TOwner, THost, T>(
        string name,
        T defaultValue = default!,
        bool inherits = false,
        Func<UIObject, T, T>? coerce = null,
        Func<T, bool>? validate = null,
        PropertyChangedCallback<T>? changed = null)
        where THost : UIObject
        => RegisterAttached<TOwner, THost, T>(name, new PropertyMetadata<T>(defaultValue, coerce, validate, changed), inherits);

    /// <summary>Registers an attached property with explicit registration metadata.</summary>
    public static AttachedProperty<T> RegisterAttached<TOwner, THost, T>(string name, PropertyMetadata<T> metadata, bool inherits = false)
        where THost : UIObject
        => new(name, typeof(TOwner), typeof(THost), metadata, inherits, isReadOnly: false);

    /// <summary>
    /// Registers a structurally read-only styled property: the returned <see cref="UIPropertyKey{T}"/>
    /// is the only write surface; the companion <see cref="UIPropertyKey{T}.Property"/> (exposed as
    /// the public <c>static readonly</c> field) only reads. Key writes land in the
    /// <see cref="BindingPriority.LocalValue"/> lane (matrix PD14).
    /// </summary>
    public static UIPropertyKey<T> RegisterReadOnly<TOwner, T>(
        string name,
        T defaultValue = default!,
        bool inherits = false,
        Func<UIObject, T, T>? coerce = null,
        Func<T, bool>? validate = null,
        PropertyChangedCallback<T>? changed = null)
        where TOwner : UIObject
        => RegisterReadOnly<TOwner, T>(name, new PropertyMetadata<T>(defaultValue, coerce, validate, changed), inherits);

    /// <summary>Registers a structurally read-only styled property with explicit registration metadata.</summary>
    public static UIPropertyKey<T> RegisterReadOnly<TOwner, T>(string name, PropertyMetadata<T> metadata, bool inherits = false)
        where TOwner : UIObject
        => new(new StyledProperty<T>(name, typeof(TOwner), metadata, inherits, isAttached: false, isReadOnly: true));

    /// <summary>Registers a structurally read-only attached property; see <see cref="RegisterReadOnly{TOwner,T}(string,T,bool,Func{UIObject,T,T},Func{T,bool},PropertyChangedCallback{T})"/>.</summary>
    public static UIPropertyKey<T> RegisterAttachedReadOnly<TOwner, THost, T>(
        string name,
        T defaultValue = default!,
        bool inherits = false,
        Func<UIObject, T, T>? coerce = null,
        Func<T, bool>? validate = null,
        PropertyChangedCallback<T>? changed = null)
        where THost : UIObject
        => RegisterAttachedReadOnly<TOwner, THost, T>(name, new PropertyMetadata<T>(defaultValue, coerce, validate, changed), inherits);

    /// <summary>Registers a structurally read-only attached property with explicit registration metadata.</summary>
    public static UIPropertyKey<T> RegisterAttachedReadOnly<TOwner, THost, T>(string name, PropertyMetadata<T> metadata, bool inherits = false)
        where THost : UIObject
        => new(new AttachedProperty<T>(name, typeof(TOwner), typeof(THost), metadata, inherits, isReadOnly: true));

    /// <summary>
    /// Registers a direct property: plain field storage behind getter/setter delegates — the
    /// high-frequency internal-state lane. No store, no coercion, no styling, no inheritance, no
    /// effects routing, no animation lane (ledger A24). A <see langword="null"/>
    /// <paramref name="setter"/> makes the property read-only.
    /// </summary>
    /// <param name="name">The property name.</param>
    /// <param name="getter">Reads the backing field.</param>
    /// <param name="setter">Writes the backing field; <see langword="null"/> ⇒ read-only.</param>
    /// <param name="unsetValue">The value pushed through the setter when a producer yields
    /// <see cref="UnsetValue"/> (the descriptor's fallback contract).</param>
    public static DirectProperty<TOwner, T> RegisterDirect<TOwner, T>(
        string name,
        Func<TOwner, T> getter,
        Action<TOwner, T>? setter = null,
        T unsetValue = default!)
        where TOwner : UIObject
        => new(name, getter, setter, unsetValue);

    // ───────────────────────────── sentinels ─────────────────────────────

    private sealed class UnsetValueSentinel
    {
        internal static readonly UnsetValueSentinel Instance = new();

        private UnsetValueSentinel() { }

        public override string ToString() => "{UnsetValue}";
    }

    private sealed class SentinelProperty : UIProperty
    {
        internal SentinelProperty(string name)
            : base(name, typeof(object))
        {
        }

        internal override object? GetDefaultValueUntyped(Type forType) => null;
    }
}
