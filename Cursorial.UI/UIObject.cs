using System.Diagnostics;

// ReSharper disable RedundantTypeArgumentsOfMethod

namespace Cursorial.UI;

/// <summary>
/// The property-host base everything styleable derives from (<c>UIObject</c> → <c>UIElement</c> →
/// <c>Control</c> → …): thread affinity (invariant 6), the <c>Affects*</c> effects-registration
/// sugar (ledger A2), and the value-store surface — typed/untyped get/set, <c>SetCurrentValue</c>,
/// <c>ClearValue</c>/<c>CoerceValue</c>, binding producers (<c>Bind</c>/<c>BindInFrame</c>),
/// frames, animation handles, value inheritance (<c>SetInheritanceParent</c>), observers,
/// <c>DeferNotifications</c>, diagnostics (<c>GetValueSource</c>/<c>GetValueDiagnostics</c>), and
/// direct-property <c>SetAndRaise</c>.
/// </summary>
/// <remarks>
/// <b>Single UI thread is the v1 contract.</b> A <see cref="UIObject"/> constructed while a
/// thread-local <see cref="UIApplication.Current"/> exists captures that application's
/// <see cref="UIDispatcher"/> (ledger A25 / design doc §10.3) — affinity then follows the
/// dispatcher's owner thread, which survives the Build-thread → UI-thread ownership hand-off.
/// Without an ambient application (unit tests, standalone use) the constructing thread id is
/// captured instead. <see cref="VerifyAccess"/> asserts affinity in DEBUG builds and compiles
/// away in release (zero synchronization anywhere in the store — the whole stack below is
/// single-render-thread).
/// </remarks>
public abstract class UIObject : IInheritanceNode
{
    private readonly UIDispatcher? _dispatcher; // captured from UIApplication.Current (ledger A25)
    private readonly int _ownerManagedThreadId;
    private ValueStore? _store;
    private UIObject? _inheritanceParent;
    private List<UIObject>? _inheritanceChildren;
    private int _notificationDepth; // DEBUG-only fail-fast diagnostics (matrix M255)

    /// <summary>
    /// Opaque slot reserved for the binding engine's per-object expression registry (ledger A17) —
    /// S2 hangs its host state here so <c>BindingOperations</c> needs no side table.
    /// </summary>
    internal object? BindingHostState;

    /// <summary>
    /// Captures the constructing thread's affinity (invariant 6): the ambient application's
    /// dispatcher when one exists on this thread, the managed thread id otherwise.
    /// </summary>
    protected UIObject()
    {
        _dispatcher = UIApplication.Current?.Dispatcher;
        _ownerManagedThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>Whether the calling thread is the thread this object has affinity with.</summary>
    public bool CheckAccess()
        => _dispatcher?.CheckAccess() ?? _ownerManagedThreadId == Environment.CurrentManagedThreadId;

    /// <summary>
    /// DEBUG-only thread-affinity assert: throws <see cref="InvalidOperationException"/> when called
    /// from a thread other than the constructing one. Call sites compile away entirely in release
    /// builds (cheap by construction).
    /// </summary>
    [Conditional("DEBUG")]
    public void VerifyAccess()
    {
        if (!CheckAccess())
        {
            throw new InvalidOperationException(
                $"Cross-thread access: this {GetType().Name} has affinity with managed thread " +
                $"{_ownerManagedThreadId} but was touched from thread {Environment.CurrentManagedThreadId}. " +
                "All property access must happen on the single UI thread (invariant 6).");
        }
    }

    /// <summary>The lazily allocated store, surfaced for tests/diagnostics (matrix M1 asserts laziness).</summary>
    internal ValueStore? DebugValueStore => _store;

    // ───────────────────────────── read surface ─────────────────────────────

    /// <summary>
    /// The effective value — the hot path: never boxes, never allocates, and skips the store
    /// entirely for default-valued properties (matrix M266). Inheriting properties with no own
    /// contribution walk the inheritance parents to the nearest contributing ancestor (lazy-read,
    /// design doc §2.3) — the ancestor's <em>effective</em> (animated included) value, with no
    /// re-coercion.
    /// </summary>
    public T GetValue<T>(StyledProperty<T> property)
    {
        ArgumentNullException.ThrowIfNull(property);
        VerifyAccess();

        if (_store?.TryGetEntry(property.Id) is EffectiveValue<T> { EffectivePriority: not BindingPriority.Unset } entry)
            return entry.Value;

        if (property.Inherits && FindInheritedEntry(property.Id, out _) is EffectiveValue<T> inherited)
            return inherited.Value;

        return property.GetMetadata(GetType()).DefaultValue;
    }

    /// <summary>
    /// The effective value considering only lanes at or below <paramref name="maxPriority"/>
    /// strength (PD16): <c>GetValue(p, Style)</c> skips Animation and Local.
    /// <see cref="BindingPriority.Unset"/> throws <see cref="ArgumentException"/>.
    /// </summary>
    public T GetValue<T>(StyledProperty<T> property, BindingPriority maxPriority)
    {
        ArgumentNullException.ThrowIfNull(property);
        ValidateMaxPriority(maxPriority);
        VerifyAccess();

        var metadata = property.GetMetadata(GetType());
        if (_store is { } store)
            return store.GetValueAtMaxPriority(property, metadata, maxPriority);

        if (maxPriority != BindingPriority.Default &&
            property.Inherits && FindInheritedEntry(property.Id, out _) is EffectiveValue<T> inherited)
        {
            return inherited.Value;
        }

        return metadata.DefaultValue;
    }

    /// <summary>
    /// The effective value ignoring the Animation lane — the storyboard handoff snapshot.
    /// Equivalent to <c>GetValue(property, BindingPriority.LocalValue)</c> (PD16).
    /// </summary>
    public T GetBaseValue<T>(StyledProperty<T> property) => GetValue(property, BindingPriority.LocalValue);

    /// <summary>
    /// The untyped read lane (XAML / tooling / diagnostics): the boxed effective value, never
    /// <see cref="UIProperty.UnsetValue"/> (M14). Boxes are interned per entry / per metadata
    /// default, so repeated reads of an unchanged value allocate nothing (M267).
    /// </summary>
    public object? GetValue(UIProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        VerifyAccess();
        return property.GetValueUntyped(this);
    }

    /// <summary>The untyped lane-probing read — parity overload for <see cref="GetValue{T}(StyledProperty{T}, BindingPriority)"/>.</summary>
    public object? GetValue(UIProperty property, BindingPriority maxPriority)
    {
        ArgumentNullException.ThrowIfNull(property);
        ValidateMaxPriority(maxPriority);
        VerifyAccess();
        return property.GetValueUntyped(this, maxPriority);
    }

    /// <summary>
    /// Whether a value-bearing local contribution — local value or local entry with a value — or a
    /// value-bearing entry in an <em>active</em> frame is present (PD11 — guards S8 auto-aliasing).
    /// Animation, inherited, and default contributions never count; valueless entries never count;
    /// direct properties always report <see langword="false"/>.
    /// </summary>
    public bool IsSet(UIProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        VerifyAccess();
        return _store is { } store &&
               (store.TryGetEntry(property.Id) is { HasLocal: true } || store.HasActiveStyleContribution(property.Id));
    }

    /// <summary>
    /// Diagnostics: the lane the effective value resolved from plus the <c>+cur</c> bit, annotated
    /// with the winning base lane and the <c>IsAnimated</c>/<c>IsCoerced</c> bits (annotations are
    /// excluded from <see cref="ValueSource"/> equality — PD23). Direct properties always report
    /// <see cref="BindingPriority.LocalValue"/> (field semantics, no ladder — matrix M220);
    /// <see cref="BindingPriority.Unset"/> is never reported; entry-less inheriting properties with
    /// a contributing ancestor report <see cref="BindingPriority.Inherited"/>.
    /// </summary>
    public ValueSource GetValueSource(UIProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        VerifyAccess();

        if (property.IsDirect)
            return new ValueSource(BindingPriority.LocalValue, IsCurrentValue: false);

        var entry = _store?.TryGetEntry(property.Id);
        if (entry is { EffectivePriority: not BindingPriority.Unset })
        {
            var basePriority = entry.BasePriority != BindingPriority.Unset
                ? entry.BasePriority
                : property.Inherits && FindInheritedEntry(property.Id, out _) is not null
                    ? BindingPriority.Inherited
                    : BindingPriority.Default;
            return new ValueSource(entry.EffectivePriority, entry.IsCurrentValue)
            {
                BasePriority = basePriority,
                IsCoerced = entry.IsCoerced,
            };
        }

        return property.Inherits && FindInheritedEntry(property.Id, out _) is not null
            ? new ValueSource(BindingPriority.Inherited, IsCurrentValue: false)
            : new ValueSource(BindingPriority.Default, IsCurrentValue: false);
    }

    /// <summary>
    /// The per-property value-stack enumeration for serialization / DevTools (design doc §2.1
    /// "frame/local enumeration for tooling"; matrix M264). Rows strongest-first: the animation
    /// contribution (current animated effective), the <em>raw</em> local value, every frame entry
    /// (sort-keyed, inactive frames included and flagged), and the inherited provenance with its
    /// contributing ancestor. Direct properties yield a single
    /// <see cref="BindingPriority.LocalValue"/> row (field semantics). Cold path — values box.
    /// </summary>
    public IReadOnlyList<PropertyValueDiagnostic> GetValueDiagnostics(UIProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        VerifyAccess();

        var results = new List<PropertyValueDiagnostic>();
        if (property.IsDirect)
        {
            results.Add(new PropertyValueDiagnostic(BindingPriority.LocalValue, property.GetValueUntyped(this), HasValue: true));
            return results;
        }

        var entry = _store?.TryGetEntry(property.Id);
        if (entry is { HasAnimatedValue: true })
            results.Add(new PropertyValueDiagnostic(BindingPriority.Animation, entry.GetEffectiveBoxedValue(), HasValue: true));
        if (entry is { HasLocal: true })
            results.Add(new PropertyValueDiagnostic(BindingPriority.LocalValue, entry.GetRawLocalBoxedValue(), HasValue: true));

        _store?.AppendStyleDiagnostics(property, results);

        if (property.Inherits && FindInheritedEntry(property.Id, out var source) is { } inherited)
            results.Add(new PropertyValueDiagnostic(
                BindingPriority.Inherited, inherited.GetEffectiveBoxedValue(), HasValue: true, InheritedFrom: source));

        return results;
    }

    internal object? GetValueBoxed<T>(StyledProperty<T> property)
    {
        if (_store is { } store)
            return store.GetValueBoxed(property);
        if (property.Inherits && FindInheritedEntry(property.Id, out _) is { } inherited)
            return inherited.GetEffectiveBoxedValue();
        return property.GetMetadata(GetType()).BoxedDefault;
    }

    // ───────────────────────────── write surface ─────────────────────────────

    /// <summary>
    /// Writes the local value. <paramref name="priority"/> accepts
    /// <see cref="BindingPriority.LocalValue"/> only (PD1 — one producer per lane: frames are the
    /// sole Style producer, animation handles the sole Animation producer; the parameter survives
    /// for the re-addable cut rungs, §2.9). Validation runs on the raw value and throws
    /// <see cref="ArgumentException"/> (PD7); coercion runs inside effective-value computation.
    /// </summary>
    public void SetValue<T>(StyledProperty<T> property, T value, BindingPriority priority = BindingPriority.LocalValue)
    {
        ArgumentNullException.ThrowIfNull(property);
        ValidateWritePriority(priority);
        ThrowIfReadOnly(property);
        SetValueCore(property, value, isCurrentValue: false);
    }

    /// <summary>
    /// The untyped write mouth (XAML lane): type-checked against
    /// <see cref="UIProperty.PropertyType"/> with no silent conversion (M222/M223);
    /// <see cref="UIProperty.UnsetValue"/> is the documented untyped spelling of
    /// <see cref="ClearValue"/> in full (PD5); direct properties route through their setter
    /// delegates (M216–M218). Same PD1 priority restriction as the typed mouth (M229).
    /// </summary>
    public void SetValue(UIProperty property, object? value, BindingPriority priority = BindingPriority.LocalValue)
    {
        ArgumentNullException.ThrowIfNull(property);
        ValidateWritePriority(priority);
        VerifyAccess();

        if (ReferenceEquals(value, UIProperty.UnsetValue))
        {
            if (property.IsDirect)
            {
                property.SetValueUntyped(this, value); // setter receives the registered fallback (M218)
                return;
            }

            ClearValue(property); // PD5: ≡ ClearValue in full, eviction included
            return;
        }

        ValidateValueType(property, value);
        property.SetValueUntyped(this, value); // routes through the typed public mouth — all checks apply
    }

    /// <summary>
    /// The key-holder write for a structurally read-only property: lands in the
    /// <see cref="BindingPriority.LocalValue"/> lane (PD14, matrix M205).
    /// </summary>
    public void SetValue<T>(UIPropertyKey<T> key, T value)
    {
        ArgumentNullException.ThrowIfNull(key);
        SetValueCore(key.Property, value, isCurrentValue: false);
    }

    /// <summary>
    /// Replaces the effective value in place without changing its source (design doc §2.2, verbatim
    /// P3 graft); with no entry it behaves as a Local write (M118). Observer args carry the
    /// <em>replaced</em> lane's priority (A11). It is a mouth, not a producer: a
    /// <c>Validate</c>-rejecting value throws like <c>SetValue</c> (PD17), and coercion applies
    /// identically (M133/M243).
    /// </summary>
    public void SetCurrentValue<T>(StyledProperty<T> property, T value)
    {
        ArgumentNullException.ThrowIfNull(property);
        ThrowIfReadOnly(property);
        SetValueCore(property, value, isCurrentValue: true);
    }

    /// <summary>
    /// Removes the local value and evicts local-priority binding entries (A9: <c>ClearValue</c> is
    /// the binding kill; <c>SetValue</c> never kills). Promotion reports the new winning lane
    /// (PD10) — Style, then Inherited, then Default; with no local contribution it is a silent
    /// no-op (M21).
    /// </summary>
    public void ClearValue(UIProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        VerifyAccess();
        RenderPassGuard.ThrowIfActive(); // the render pass is read-only (design doc §5.5; DEBUG only)

        if (property.IsDirect)
            throw new ArgumentException($"Direct property '{property}' cannot be cleared; push the registered unset value through its setter instead.", nameof(property));
        if (property.IsReadOnly)
            throw new InvalidOperationException($"Property '{property}' is read-only; clearing requires the key surface (PD14).");

        if (_store is { } store)
            store.TryGetEntry(property.Id)?.ClearLocal(store);
    }

    /// <summary>
    /// Re-runs the coercer against the stored <em>raw</em> local value (PD6 — the WPF
    /// Maximum/Value dance, matrix M232). No-op when no local contribution exists: the default lane
    /// is never coerced (PD8).
    /// </summary>
    public void CoerceValue(UIProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        VerifyAccess();

        if (property.IsDirect)
            throw new ArgumentException($"Direct property '{property}' has no coercion (ledger A24).", nameof(property));

        if (_store is { } store)
            store.TryGetEntry(property.Id)?.RecoerceLocal(store);
    }

    private void SetValueCore<T>(StyledProperty<T> property, T value, bool isCurrentValue)
    {
        VerifyAccess();
        RenderPassGuard.ThrowIfActive(); // the render pass is read-only (design doc §5.5; DEBUG only)
        DebugValidateAttachedHost(property);

        var metadata = property.GetMetadata(GetType());
        if (metadata.Validate is { } validate && !validate(value))
        {
            throw new ArgumentException(
                $"Value '{value}' was rejected by the validator registered for '{property}'.", nameof(value));
        }

        var store = _store ??= new ValueStore(this);
        if (isCurrentValue)
            store.SetCurrentValue(property, metadata, value);
        else
            store.SetLocalValue(property, metadata, value, isCurrentValue: false);
    }

    // ───────────────────────────── producers: bindings, frames, animation ─────────────────────────────

    /// <summary>
    /// Installs a free-standing binding producer entry (ledger A6/A7/A8). Free-standing entries are
    /// <see cref="BindingPriority.LocalValue"/>-only — Style-slot contributions must be frame-hosted
    /// (<see cref="BindInFrame{T}"/>) and Animation is <see cref="BeginAnimation{T}"/> territory.
    /// The entry installs <em>valueless</em>; a prior local entry is displaced with eviction
    /// (PD12). <c>ClearValue</c> evicts it (A9); plain <c>SetValue</c> coexists (last writer wins).
    /// </summary>
    public BindingEntry<T> Bind<T>(
        StyledProperty<T> property,
        BindingPriority priority = BindingPriority.LocalValue,
        IValueEvictionListener? listener = null)
    {
        ArgumentNullException.ThrowIfNull(property);
        ThrowIfReadOnly(property);
        VerifyAccess();

        if (priority != BindingPriority.LocalValue)
        {
            throw new ArgumentException(
                $"Free-standing Bind accepts BindingPriority.LocalValue only (ledger A6 — Style-slot " +
                $"contributions must be frame-hosted via BindInFrame); got {priority}.", nameof(priority));
        }

        return (_store ??= new ValueStore(this)).BindLocal(property, listener);
    }

    /// <summary>The untyped <see cref="Bind{T}"/> (ledger A16 bridge — no reflection, all checks apply).</summary>
    public BindingEntryBase BindUntyped(
        UIProperty property,
        BindingPriority priority = BindingPriority.LocalValue,
        IValueEvictionListener? listener = null)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.CreateEntry(this, priority, hostFrame: null, listener);
    }

    /// <summary>
    /// Installs a binding producer entry hosted by <paramref name="hostFrame"/> (ledger A5): the
    /// entry contributes at the frame's <see cref="StyleSortKey"/> inside the single Style slot —
    /// full within-slot citizenship — and is evicted (firing
    /// <see cref="IValueEvictionListener.OnEvicted"/>) when the frame is removed. The host frame
    /// must already be installed on this object.
    /// </summary>
    public BindingEntry<T> BindInFrame<T>(
        StyledProperty<T> property, ValueFrame hostFrame, IValueEvictionListener? listener = null)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(hostFrame);
        ThrowIfReadOnly(property);
        VerifyAccess();

        if (_store is null || !ReferenceEquals(hostFrame.Store, _store))
            throw new ArgumentException("The host frame is not installed on this object (matrix M166).", nameof(hostFrame));

        var entry = new BindingEntry<T>(this, property, BindingPriority.Style, hostFrame, listener);
        hostFrame.AddHostedEntry(entry);
        return entry; // installs valueless (A8) — no recompute until the first push
    }

    /// <summary>The untyped <see cref="BindInFrame{T}"/> (ledger A16 bridge).</summary>
    public BindingEntryBase BindInFrameUntyped(UIProperty property, ValueFrame hostFrame, IValueEvictionListener? listener = null)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(hostFrame);
        return property.CreateEntry(this, BindingPriority.Style, hostFrame, listener);
    }

    /// <summary>
    /// Attaches an animation handle — the sole <see cref="BindingPriority.Animation"/> producer
    /// (PD1). The handle is inert until its first <c>SetValue</c> (PD4); beginning a new animation
    /// detaches a prior handle (last-started wins), whose pushed value persists until the new
    /// handle's first push. Disposing the handle resurfaces the base with one notification.
    /// </summary>
    public AnimatedValueHandle<T> BeginAnimation<T>(StyledProperty<T> property)
    {
        ArgumentNullException.ThrowIfNull(property);
        ThrowIfReadOnly(property);
        VerifyAccess();
        return (_store ??= new ValueStore(this)).BeginAnimation(property);
    }

    /// <summary>
    /// Installs a <see cref="ValueFrame"/> in the single Style slot (design doc §2.2): within-slot
    /// order is the frame's <see cref="StyleSortKey"/> (larger wins; equal keys: later-added wins).
    /// Re-adding an installed frame throws <see cref="InvalidOperationException"/> (PD21); frames
    /// carrying entries for read-only or direct properties are rejected at install (PD14 / A24).
    /// </summary>
    public void AddFrame(ValueFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        VerifyAccess();
        (_store ??= new ValueStore(this)).AddFrame(frame);
    }

    /// <summary>
    /// Removes an installed frame — retraction is store-owned (invariant 4): hosted entries are
    /// evicted (PD2: evict → recompute → notify), the frame's contributions are withdrawn, and the
    /// next source promotes; nothing is ever "set back". Removing a never-added frame throws
    /// <see cref="ArgumentException"/> (PD21).
    /// </summary>
    public void RemoveFrame(ValueFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        VerifyAccess();

        if (_store is null)
            throw new ArgumentException("The frame is not installed on this object (PD21).", nameof(frame));

        _store.RemoveFrame(frame);
    }

    // ───────────────────────────── inheritance (ledger A3/A4) ─────────────────────────────

    /// <summary>
    /// Wires this object's inheritance parent — the element tree (S1) calls this on every attach /
    /// detach / reparent (<c>InheritanceParent = LogicalParent ?? VisualParent</c>, design doc
    /// §5.1). Reparenting re-pulls every inheriting property: the old and new chains are diffed
    /// over the registry's inheriting-property set and each actual change is delivered to this node
    /// and its non-shadowed descendants (matrix M111/M185–M187/M195); equal-value reparents are
    /// silent.
    /// </summary>
    /// <remarks>
    /// Inherited reads are <b>lazy</b> in v1: nothing is cached on descendants, every miss walks
    /// the parent chain (cheap on shallow, wide terminal trees — §2.6-4). The documented
    /// API-compatible upgrade is <b>push-down shared boxes</b> (§2.9): per-property value boxes
    /// pushed down the inheritance tree on change, turning reads O(1) without touching any public
    /// surface — benchmark-gated, deliberately not built until a profile asks for it.
    /// </remarks>
    /// <exception cref="ArgumentException">The new parent chain already contains this object (an
    /// inheritance cycle).</exception>
    public void SetInheritanceParent(UIObject? parent)
    {
        VerifyAccess();

        if (ReferenceEquals(_inheritanceParent, parent))
            return;

        for (var node = parent; node is not null; node = node._inheritanceParent)
        {
            if (ReferenceEquals(node, this))
                throw new ArgumentException("Setting this inheritance parent would create a cycle.", nameof(parent));
        }

        var oldParent = _inheritanceParent;
        oldParent?._inheritanceChildren?.Remove(this);
        _inheritanceParent = parent;
        if (parent is not null)
            (parent._inheritanceChildren ??= []).Add(this);

        // Reparent re-pull: diff every inheriting property between the old and new chains
        // (the registry's InheritingPropertyIds set — kept enumerable by Inherits being fixed at
        // registration, §2.6-3). Shadowed properties and equal-value diffs are silent.
        foreach (var property in UIPropertyRegistry.InheritingProperties)
            property.NotifyInheritanceParentChanged(this, oldParent);
    }

    /// <summary>The node this object inherits property values from, or <see langword="null"/> (the <see cref="IInheritanceNode"/> seam).</summary>
    public UIObject? GetInheritanceParent() => _inheritanceParent;

    /// <summary>
    /// The inherited-change virtual (ledger A3) — the second change carrier, delivered when an
    /// inheriting property's effective value changes on an ancestor and this node has no shadowing
    /// contribution. The element tree overrides it to run the same <c>PropertyEffects</c>
    /// invalidation dispatch it runs for ordinary changes (§5.5). Typed and untyped observers fire
    /// alongside it (ledger A4 — DataContext rebind rides this); the metadata <c>Changed</c>
    /// callback and the ordinary <see cref="OnPropertyChanged"/> virtual are origin-site channels
    /// and do not fire here (matrix PD22). Args are valid only for the duration of the call.
    /// </summary>
    protected internal virtual void OnInheritedPropertyChanged(in InheritedPropertyChangedEventArgs args)
    {
    }

    /// <summary>
    /// Walks the inheritance parents to the nearest node with a contributing store entry for
    /// <paramref name="propertyId"/> (the lazy-read walk; <paramref name="source"/> reports the
    /// contributing ancestor). The ancestor's entry is returned directly so callers read its
    /// <em>effective</em> — animated included — value with no re-coercion and share its box-intern
    /// cache.
    /// </summary>
    internal EffectiveValueBase? FindInheritedEntry(int propertyId, out UIObject? source)
        => FindInheritedEntryFrom(_inheritanceParent, propertyId, out source);

    private static EffectiveValueBase? FindInheritedEntryFrom(UIObject? start, int propertyId, out UIObject? source)
    {
        for (var node = start; node is not null; node = node._inheritanceParent)
        {
            if (node._store?.TryGetEntry(propertyId) is { EffectivePriority: not BindingPriority.Unset } entry)
            {
                source = node;
                return entry;
            }
        }

        source = null;
        return null;
    }

    /// <summary>
    /// The typed reparent re-pull for one inheriting property (called via the
    /// <c>NotifyInheritanceParentChanged</c> bridge): diffs the inherited value under the old
    /// versus the new chain and delivers the change to this node and its non-shadowed descendants.
    /// </summary>
    internal void OnInheritanceParentChanged<T>(StyledProperty<T> property, UIObject? oldParent)
    {
        var entry = _store?.TryGetEntry(property.Id);
        if (entry is { BasePriority: not BindingPriority.Unset })
            return; // an own sub-Animation contribution shadows — nothing changes here or below

        var oldSource = FindInheritedEntryFrom(oldParent, property.Id, out _) as EffectiveValue<T>;
        var newSource = FindInheritedEntry(property.Id, out _) as EffectiveValue<T>;
        if (oldSource is null && newSource is null)
            return; // neither chain contributes — both sides are the default (the common reparent case)

        var metadata = property.GetMetadata(GetType());
        var oldValue = oldSource is not null ? oldSource.Value : metadata.DefaultValue;
        var newLane = newSource is not null ? BindingPriority.Inherited : BindingPriority.Default;
        var newValue = newSource is not null ? newSource.Value : metadata.DefaultValue;

        if (metadata.EffectiveComparer.Equals(oldValue, newValue))
            return; // equal-value reparent diff is silent (M187) — and so is the whole subtree

        OnInheritedValueChanged(property, oldValue, newValue, newLane);
    }

    /// <summary>
    /// One eager-notify hop (design doc §2.3): an inheriting property's effective value changed on
    /// an ancestor. A node with its own winning base is a <em>shadow</em> — propagation stops
    /// (M183/M191); a node with only an animation is <em>masked</em> — the winning-base channel
    /// fires (A20's inherited seam, M174) but its effective (and therefore its subtree) is
    /// unchanged; an entry-less node delivers on the inherited channel set (PD22) and recurses.
    /// </summary>
    internal void OnInheritedValueChanged<T>(StyledProperty<T> property, T oldValue, T newValue, BindingPriority priority)
    {
        var entry = _store?.TryGetEntry(property.Id);
        if (entry is not null)
        {
            if (entry.BasePriority != BindingPriority.Unset)
                return; // shadowed: this subtree resolves against its own contribution

            if (entry.HasAnimatedValue)
            {
                // Masked: the inherited base changed under the animation — the sanctioned A20
                // exception (M86/M174). Descendants inherit THIS node's (unchanged) animated
                // effective, so recursion stops here.
                DispatchBaseValueChanged(property, oldValue, newValue, isAnimated: true);
                return;
            }
        }

        DispatchBaseValueChanged(property, oldValue, newValue, isAnimated: false); // A20 inherited seam (M175)
        DispatchInheritedChanged(property, oldValue, newValue, priority);
        NotifyInheritanceChildren(property, oldValue, newValue, priority);
    }

    /// <summary>
    /// Fans an inherited change out to the inheritance children, depth-first in child order. The
    /// equality gate ran at the origin; per-descendant re-gating with descendant-type comparers is
    /// deliberately not done (one gate per change, §0.3-3). Propagated deliveries dispatch
    /// immediately — a <em>descendant's</em> defer scope has no entry to coalesce them on; the
    /// origin's scope coalesces the whole fan-out (M194, PD22).
    /// </summary>
    private void NotifyInheritanceChildren<T>(StyledProperty<T> property, T oldValue, T newValue, BindingPriority priority)
    {
        if (_inheritanceChildren is not { } children)
            return;

        for (var i = 0; i < children.Count; i++)
            children[i].OnInheritedValueChanged(property, oldValue, newValue, priority);
    }

    /// <summary>
    /// The inherited-change delivery on one entry-less descendant, in pinned order: typed observers
    /// → untyped observers → the <see cref="OnInheritedPropertyChanged"/> virtual (PD22 — the
    /// metadata <c>Changed</c> callback and ordinary virtual are origin-site channels). Values are
    /// stack copies; the same pooled-carrier contract as the ordinary channel applies.
    /// </summary>
    private void DispatchInheritedChanged<T>(StyledProperty<T> property, T oldValue, T newValue, BindingPriority priority)
    {
        EnterNotification();
        try
        {
            if (_store?.GetObservers(property.Id) is { } observers)
            {
                var typed = observers.Typed; // snapshot — COW arrays keep it stable
                foreach (var observer in typed)
                    ((IValueObserver<T>)observer).OnPropertyChanged(this, property, oldValue, newValue, priority);

                var untyped = observers.Untyped;
                if (untyped.Length > 0)
                {
                    var oldBoxed = ValueBoxes.Box(oldValue);
                    var newBoxed = ValueBoxes.Box(newValue);
                    foreach (var observer in untyped)
                        observer.OnPropertyChanged(this, property, oldBoxed, newBoxed, priority);
                }
            }

            var carrier = ValueChangeCarrier<T>.Rent(oldValue, newValue);
            try
            {
                OnInheritedPropertyChanged(new InheritedPropertyChangedEventArgs(property, priority, carrier));
            }
            finally
            {
                ValueChangeCarrier<T>.Return(carrier);
            }
        }
        finally
        {
            ExitNotification();
        }
    }

    // ─────────────────── internal producer plumbing (binding entries / handles) ───────────────────

    /// <summary>A local entry's push (validated at the producer mouth) — last writer wins in the lane.</summary>
    internal void SetLocalValueFromEntry<T>(StyledProperty<T> property, PropertyMetadata<T> metadata, T value, BindingEntryBase writer)
        => _store?.SetLocalValue(property, metadata, value, isCurrentValue: false, writer);

    /// <summary>A local entry's unset push: withdraws the lane's value and promotes (ledger A8).</summary>
    internal void UnsetLocalValueFromEntry<T>(StyledProperty<T> property)
        => _store?.UnsetLocalValue(property);

    /// <summary>Self-disposed local entry: detach without eviction (PD12), withdrawing only its own value.</summary>
    internal void DetachLocalEntry<T>(StyledProperty<T> property, BindingEntryBase entry)
        => _store?.DetachLocalEntry(property, entry);

    /// <summary>A frame-hosted entry changed: re-arbitrate its property.</summary>
    internal void ReevaluateFromEntry<T>(StyledProperty<T> property, IValueEntry? changedEntry)
        => _store?.Reevaluate(property, changedEntry);

    /// <summary>An animation handle's per-frame push (ledger A18); allocation-free steady-state.</summary>
    internal bool SetAnimatedValue<T>(StyledProperty<T> property, PropertyMetadata<T> metadata, T value)
        => _store is { } store && store.SetAnimatedValue(property, metadata, value);

    /// <summary>An animation handle's disposal: the base resurfaces with one notification.</summary>
    internal void EndAnimation<T>(StyledProperty<T> property, AnimatedValueHandle<T> handle)
        => _store?.EndAnimation(property, handle);

    /// <summary>
    /// The teardown sweep (ledger A13, called by the element tree on permanent detach): evicts every
    /// producer entry — free-standing and frame-hosted alike — firing <c>OnEvicted</c> per entry and
    /// zero change notifications (PD13). Afterwards the store is inert; reads return defaults.
    /// </summary>
    internal void TearDownValueStore()
    {
        VerifyAccess();
        _store?.TearDown();
    }

    // ───────────────────────────── observers + defer ─────────────────────────────

    /// <summary>
    /// Subscribes a typed observer. Subscription does <b>not</b> replay the current value (PD19);
    /// arrays are copy-on-write, so an in-flight dispatch keeps its snapshot (M28/M29). Dispose the
    /// returned token to unsubscribe.
    /// </summary>
    public IDisposable AddObserver<T>(StyledProperty<T> property, IValueObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(observer);
        VerifyAccess();
        return (_store ??= new ValueStore(this)).AddObserver(property.Id, observer, ValueStore.ObserverChannel.Typed);
    }

    /// <summary>
    /// Subscribes a typed observer with options. With
    /// <see cref="ObserverOptions.IncludeBaseChanges"/> the subscription delivers on the
    /// winning-base channel only (ledger A20): <see cref="IValueObserver{T}.OnBaseValueChanged"/>
    /// fires when the effective base — the winner among sub-Animation priorities — changes,
    /// including under an active animation. Base deliveries are synchronous and immediate (they are
    /// a retargeting seam, not deferred by <see cref="DeferNotifications"/>); disposal is
    /// independent of any plain subscription of the same observer.
    /// </summary>
    public IDisposable AddObserver<T>(StyledProperty<T> property, IValueObserver<T> observer, ObserverOptions options)
    {
        if (!options.IncludeBaseChanges)
            return AddObserver(property, observer);

        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(observer);
        VerifyAccess();
        return (_store ??= new ValueStore(this)).AddObserver(property.Id, observer, ValueStore.ObserverChannel.Base);
    }

    /// <summary>Subscribes an untyped observer (ledger A10); same semantics as the typed overload.</summary>
    public IDisposable AddObserver(UIProperty property, IUntypedValueObserver observer)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(observer);
        VerifyAccess();
        return (_store ??= new ValueStore(this)).AddObserver(property.Id, observer, ValueStore.ObserverChannel.Untyped);
    }

    /// <summary>
    /// Opens a notification-defer scope (ledger A23 — template apply / container prepare /
    /// DataContext swap): writes apply immediately (reads stay live), notifications coalesce per
    /// property (first old, last new, last priority) and flush in first-change order (PD15) at the
    /// outermost dispose. Changes whose first-old equals last-new cancel out (M245).
    /// </summary>
    public IDisposable DeferNotifications()
    {
        VerifyAccess();
        return (_store ??= new ValueStore(this)).BeginDefer();
    }

    // ───────────────────────────── direct properties ─────────────────────────────

    /// <summary>
    /// The direct-property write helper: equality-gates on <see cref="EqualityComparer{T}.Default"/>
    /// (direct properties have no metadata), assigns the field, and raises the observer and virtual
    /// channels — there is no metadata-<c>Changed</c> channel for direct properties (matrix M213).
    /// Returns whether the value actually changed.
    /// </summary>
    protected bool SetAndRaise<TOwner, T>(DirectProperty<TOwner, T> property, ref T field, T value)
        where TOwner : UIObject
    {
        ArgumentNullException.ThrowIfNull(property);
        VerifyAccess();

        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        var oldValue = field;
        field = value;
        DispatchPropertyChanged<T>(property, changedCallback: null, oldValue, value, BindingPriority.LocalValue);
        return true;
    }

    // ───────────────────────────── notification dispatch ─────────────────────────────

    /// <summary>
    /// The single synchronous dispatch path, in pinned channel order: metadata <c>Changed</c> →
    /// typed observers → untyped observers → virtual <see cref="OnPropertyChanged"/> (design doc
    /// §2.3, matrix M16). Old/new are copies on this frame's stack, so reentrant writes from
    /// handlers dispatch nested-first (PD18) without corrupting in-flight args (M252); the equality
    /// gate is the cycle breaker, plus a DEBUG depth-64 fail-fast (M255). Inheriting properties
    /// fan the change out to non-shadowed inheritance descendants afterwards (eager-notify, ledger
    /// A3/A4) — riding this dispatch is what makes a defer-scope flush propagate exactly once
    /// (M194).
    /// </summary>
    internal void DispatchPropertyChanged<T>(
        UIProperty property, PropertyChangedCallback<T>? changedCallback, T oldValue, T newValue, BindingPriority priority)
    {
        EnterNotification();
        try
        {
            changedCallback?.Invoke(this, oldValue, newValue);

            if (_store?.GetObservers(property.Id) is { } observers)
            {
                var typed = observers.Typed; // snapshot — COW arrays keep it stable (M28/M29)
                foreach (var observer in typed)
                    ((IValueObserver<T>)observer).OnPropertyChanged(this, property, oldValue, newValue, priority);

                var untyped = observers.Untyped;
                if (untyped.Length > 0)
                {
                    var oldBoxed = ValueBoxes.Box(oldValue);
                    var newBoxed = ValueBoxes.Box(newValue);
                    foreach (var observer in untyped)
                        observer.OnPropertyChanged(this, property, oldBoxed, newBoxed, priority);
                }
            }

            var carrier = ValueChangeCarrier<T>.Rent(oldValue, newValue);
            try
            {
                OnPropertyChanged(new UIPropertyChangedEventArgs(property, priority, carrier));
            }
            finally
            {
                ValueChangeCarrier<T>.Return(carrier);
            }

            // Eager-notify (A3/A4): descendants' new lane is Inherited while this node (or one
            // above) still contributes, Default once nothing does — i.e. exactly when this
            // change's own lane is Default (M108/M109).
            if (property.Inherits && _inheritanceChildren is not null && property is StyledProperty<T> styled)
            {
                NotifyInheritanceChildren(styled, oldValue, newValue,
                    priority == BindingPriority.Default ? BindingPriority.Default : BindingPriority.Inherited);
            }
        }
        finally
        {
            ExitNotification();
        }
    }

    /// <summary>
    /// The winning-base channel dispatch (ledger A20): delivers <c>(oldBase, newBase, isAnimated)</c>
    /// to <see cref="ObserverOptions.IncludeBaseChanges"/> subscriptions. Fired at base-change
    /// detection — synchronously and immediately, before (and independently of) the ordinary
    /// channels; allocation-free.
    /// </summary>
    internal void DispatchBaseValueChanged<T>(UIProperty property, T oldBaseValue, T newBaseValue, bool isAnimated)
    {
        if (_store?.GetObservers(property.Id) is not { Base.Length: > 0 } observers)
            return;

        EnterNotification();
        try
        {
            var snapshot = observers.Base; // COW arrays keep the snapshot stable
            foreach (var observer in snapshot)
                ((IValueObserver<T>)observer).OnBaseValueChanged(this, property, oldBaseValue, newBaseValue, isAnimated);
        }
        finally
        {
            ExitNotification();
        }
    }

    /// <summary>
    /// The virtual change hook — last of the synchronous notification channels. The args carry
    /// copied values valid only for the duration of the call; copy them out to retain.
    /// </summary>
    protected virtual void OnPropertyChanged(in UIPropertyChangedEventArgs args)
    {
    }

    [Conditional("DEBUG")]
    private void EnterNotification()
    {
        if (++_notificationDepth >= 64)
        {
            _notificationDepth--;
            throw new InvalidOperationException(
                "Property-change notification depth reached 64 — a divergent reentrant write cycle " +
                "(DEBUG fail-fast diagnostics; release builds are unbounded by design).");
        }
    }

    [Conditional("DEBUG")]
    private void ExitNotification() => _notificationDepth--;

    // ───────────────────────────── argument validation ─────────────────────────────

    private static void ValidateWritePriority(BindingPriority priority)
    {
        if (priority != BindingPriority.LocalValue)
        {
            throw new ArgumentException(
                $"SetValue accepts BindingPriority.LocalValue only (PD1 — frames are the sole Style " +
                $"producer and animation handles the sole Animation producer); got {priority}.", nameof(priority));
        }
    }

    private static void ValidateMaxPriority(BindingPriority maxPriority)
    {
        switch (maxPriority)
        {
            case BindingPriority.Animation:
            case BindingPriority.LocalValue:
            case BindingPriority.Style:
            case BindingPriority.Inherited:
            case BindingPriority.Default:
                return;
            default:
                throw new ArgumentException(
                    $"maxPriority must be a resolvable lane (Animation, LocalValue, Style, Inherited, or Default — PD16); got {maxPriority}.",
                    nameof(maxPriority));
        }
    }

    internal static void ValidateValueType(UIProperty property, object? value)
    {
        if (value is null)
        {
            if (property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is null)
            {
                throw new ArgumentException(
                    $"null is not a valid value for '{property}' of non-nullable type {property.PropertyType.Name}.", nameof(value));
            }

            return;
        }

        var type = property.PropertyType;
        if (!type.IsInstanceOfType(value) &&
            (Nullable.GetUnderlyingType(type) is not { } underlying || !underlying.IsInstanceOfType(value)))
        {
            throw new ArgumentException(
                $"Value of type {value.GetType().Name} is not assignable to '{property}' of type {type.Name} (no silent conversion).",
                nameof(value));
        }
    }

    private static void ThrowIfReadOnly<T>(StyledProperty<T> property)
    {
        if (property.IsReadOnly)
        {
            throw new InvalidOperationException(
                $"Property '{property}' is read-only; writes require its UIPropertyKey (PD14).");
        }
    }

    [Conditional("DEBUG")]
    private void DebugValidateAttachedHost<T>(StyledProperty<T> property)
    {
        if (property is AttachedProperty<T> attached && !attached.HostType.IsInstanceOfType(this))
        {
            throw new InvalidOperationException(
                $"Attached property '{property}' may only be set on {attached.HostType.Name} instances; " +
                $"this is a {GetType().Name} (DEBUG host-type validation, matrix M204).");
        }
    }

    // ───────────────────── effects-registration sugar (ledger A2) ─────────────────────
    //
    // Called from owner-type static constructors during the registration window:
    //     static Button() { AffectsRender<Button>(BackgroundProperty, BorderPenProperty); }
    // Each writes the per-type lane for TOwner, plus the global lane for ATTACHED properties only
    // (doc §5.5: the global lane exists because a host type's per-type table can freeze before the
    // declaring panel's static ctor runs — without it Grid.SetRow(button, 2) would invalidate
    // nothing; A1 makes it mandatory for attached properties). Ordinary styled properties stay
    // per-type so effects dispatch is bounded to types that opted in — what keeps an inherited
    // AffectsRender fan-out scoped to zones actually containing affected elements (doc §5.5,
    // layout-matrix L196). The element tree (S1) layers the actual invalidation dispatch on the
    // metadata Changed channel; the engine itself never references scenes or rendering
    // (invariant 2).

    /// <summary>Marks <paramref name="properties"/> as <see cref="PropertyEffects.AffectsMeasure"/> for <typeparamref name="TOwner"/> (both lanes, pre-freeze).</summary>
    protected static void AffectsMeasure<TOwner>(params ReadOnlySpan<UIProperty> properties) where TOwner : UIObject
        => AddEffects<TOwner>(PropertyEffects.AffectsMeasure, properties);

    /// <summary>Marks <paramref name="properties"/> as <see cref="PropertyEffects.AffectsArrange"/> for <typeparamref name="TOwner"/> (both lanes, pre-freeze).</summary>
    protected static void AffectsArrange<TOwner>(params ReadOnlySpan<UIProperty> properties) where TOwner : UIObject
        => AddEffects<TOwner>(PropertyEffects.AffectsArrange, properties);

    /// <summary>Marks <paramref name="properties"/> as <see cref="PropertyEffects.AffectsRender"/> for <typeparamref name="TOwner"/> (both lanes, pre-freeze).</summary>
    protected static void AffectsRender<TOwner>(params ReadOnlySpan<UIProperty> properties) where TOwner : UIObject
        => AddEffects<TOwner>(PropertyEffects.AffectsRender, properties);

    /// <summary>Marks <paramref name="properties"/> as <see cref="PropertyEffects.AffectsComposite"/> for <typeparamref name="TOwner"/> (both lanes, pre-freeze).</summary>
    protected static void AffectsComposite<TOwner>(params ReadOnlySpan<UIProperty> properties) where TOwner : UIObject
        => AddEffects<TOwner>(PropertyEffects.AffectsComposite, properties);

    /// <summary>Marks <paramref name="properties"/> as <see cref="PropertyEffects.AffectsParentMeasure"/> for <typeparamref name="TOwner"/> (both lanes, pre-freeze).</summary>
    protected static void AffectsParentMeasure<TOwner>(params ReadOnlySpan<UIProperty> properties) where TOwner : UIObject
        => AddEffects<TOwner>(PropertyEffects.AffectsParentMeasure, properties);

    /// <summary>Marks <paramref name="properties"/> as <see cref="PropertyEffects.AffectsParentArrange"/> for <typeparamref name="TOwner"/> (both lanes, pre-freeze).</summary>
    protected static void AffectsParentArrange<TOwner>(params ReadOnlySpan<UIProperty> properties) where TOwner : UIObject
        => AddEffects<TOwner>(PropertyEffects.AffectsParentArrange, properties);

    /// <summary>
    /// Marks <paramref name="properties"/> as <see cref="PropertyEffects.BindsTwoWayByDefault"/> for
    /// <typeparamref name="TOwner"/> (the binding engine resolves <c>Mode.Default</c> to <c>TwoWay</c>
    /// from this, BD10). Registered pre-freeze from the owner type's static constructor.
    /// </summary>
    protected static void BindsTwoWayByDefault<TOwner>(params ReadOnlySpan<UIProperty> properties) where TOwner : UIObject
        => AddEffects<TOwner>(PropertyEffects.BindsTwoWayByDefault, properties);

    /// <summary>
    /// Marks <paramref name="properties"/> as <see cref="PropertyEffects.NotDataBindable"/> for
    /// <typeparamref name="TOwner"/> (the binding engine rejects installs, BD-B112). Registered
    /// pre-freeze from the owner type's static constructor.
    /// </summary>
    protected static void NotDataBindable<TOwner>(params ReadOnlySpan<UIProperty> properties) where TOwner : UIObject
        => AddEffects<TOwner>(PropertyEffects.NotDataBindable, properties);

    private static void AddEffects<TOwner>(PropertyEffects effects, ReadOnlySpan<UIProperty> properties) where TOwner : UIObject
    {
        foreach (var property in properties)
        {
            ArgumentNullException.ThrowIfNull(property, nameof(properties));
            property.AddPerTypeEffects(typeof(TOwner), effects);
            if (property.IsAttached)
                property.GlobalEffects |= effects;
        }
    }

    /// <summary>
    /// Registers <paramref name="effects"/> on the <b>global</b> effects lane (A1) of an attached
    /// property — the inherited fan-out reaches arbitrary descendant types, so an inherited attached
    /// property (e.g. <c>TextElement.Foreground</c>, declared by a non-<see cref="UIObject"/> holder)
    /// must route effects globally, not per owner type. Pre-freeze only.
    /// </summary>
    internal static void AddGlobalEffects(PropertyEffects effects, UIProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        property.GlobalEffects |= effects;
    }
}
