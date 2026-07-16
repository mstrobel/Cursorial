// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The mutation surface of a style frame entry (Fork B internal): a resource pulse (P5) mutates the
/// entry <em>in place</em> and re-emits through <c>ValueFrame.OnEntryChanged</c> — never a
/// remove/re-add (design doc §3.6 / ledger B10). At P3 the only callers are the conformance kit and
/// future-phase plumbing; constants never change after seal.
/// </summary>
internal interface IStyleSetterEntry : IValueEntry
{
    /// <summary>Replaces the entry's value (boxed, already converted to the property type).</summary>
    void SetValueBoxed(object? boxedValue);

    /// <summary>Returns the entry to the valueless state (ledger A8 — the store promotes past it).</summary>
    void Unset();
}

/// <summary>
/// One typed setter entry inside a <see cref="StyleRuleFrame"/>: the seal-converted constant (or the
/// valueless A8 marker for an <see cref="UIProperty.UnsetValue"/> setter). Per-frame instances —
/// the entry is the future home of per-element resolved resource values (B10), so it is never
/// shared across elements.
/// </summary>
internal sealed class StyleSetterEntry<T>(StyledProperty<T> property, T value, bool hasValue)
    : IValueEntry<T>, IStyleSetterEntry
{
    private T _value = value;

    /// <inheritdoc/>
    public UIProperty Property { get; } = property;

    /// <inheritdoc/>
    public bool HasValue { get; private set; } = hasValue;

    /// <inheritdoc/>
    public T GetValue() => _value;

    /// <inheritdoc/>
    public void SetValueBoxed(object? boxedValue)
    {
        _value = (T)boxedValue!;
        HasValue = true;
    }

    /// <inheritdoc/>
    public void Unset()
    {
        _value = default!;
        HasValue = false;
    }
}

/// <summary>
/// The styling engine's <see cref="ValueFrame"/>: the per-(element, armed rule) activation shim over
/// the rule's shared compiled setters (design doc §3.6 — "one <c>ValueFrame</c> per active rule").
/// The frame reference is the engine-level retraction cookie (invariant 4: removal + store-owned
/// promotion, never set-back); <see cref="ValueFrame.SetActive"/> is the Phase-2 hot-path edge.
/// </summary>
internal sealed class StyleRuleFrame : ValueFrame
{
    private readonly IValueEntry[] _entries;

    // The R2 palette spine (design doc §11.5 / B10): the resource-backed entries — a setter whose
    // compiled value is a ResourceReference. Resolved per-element against the owner's resource chain
    // when the frame installs, re-resolved on every variant-flip / chain-shadowing pulse, torn down on
    // remove. Null when the rule carries no DynamicResource setters (the overwhelmingly common case —
    // zero per-frame overhead). The bound subscriptions parallel this array index-for-index.
    private readonly ResourceBackedEntry[]? _resourceEntries;
    private ResourceSubscription[]? _resourceSubscriptions;

    internal StyleRuleFrame(
        UIElement? owner, CompiledRule rule, StyleSortKey sortKey, StyleLayer layer, object? scopeOwner,
        bool isActive = false)
        // The activator split (§0.3, 2026-07-12): a conditional rule (any pseudo-class/.class/When)
        // arbitrates at StyleTrigger — above the Template lane, so state looks pierce template-authored
        // part values while active; a resting structural rule arbitrates at Style — below it, so a
        // template author's literals/TemplateBindings are the part's resting truth. Derived from the
        // rule's SHAPE, so re-matches classify identically (the ApplyMatchDiff survivor contract).
        : base(sortKey, isActive, rule.IsConditional ? BindingPriority.StyleTrigger : BindingPriority.Style)
    {
        Owner = owner;
        Rule = rule;
        Layer = layer;
        ScopeOwner = scopeOwner;

        var setters = rule.Setters;
        var entries = new IValueEntry[setters.Length];
        List<ResourceBackedEntry>? resourceEntries = null;

        for (var i = 0; i < setters.Length; i++)
        {
            var setter = setters[i];

            // A DynamicResource setter installs a VALUELESS entry (the store promotes the next source
            // until the first resolve); the resolved value flows in at OnInstalled / on each pulse.
            if (setter.Value is ResourceReference reference)
            {
                var entry = setter.Property.CreateStyleEntry(boxedValue: null, hasValue: false);
                entries[i] = entry;
                (resourceEntries ??= []).Add(new ResourceBackedEntry(setter.Property, (IStyleSetterEntry)entry, reference.Key));
                continue;
            }

            entries[i] = setter.Property.CreateStyleEntry(setter.Value, hasValue: !setter.IsUnset);
        }

        _entries = entries;
        _resourceEntries = resourceEntries?.ToArray();
    }

    /// <summary>The styled element (null only for conformance-kit frames hosted on bare <see cref="UIObject"/>s).</summary>
    internal UIElement? Owner { get; }

    /// <summary>The armed compiled rule (shared, immutable; the SD21 identity-diff key).</summary>
    internal CompiledRule Rule { get; }

    /// <summary>A <c>When</c>-guarded rule is a conditional rule (the data-condition "trigger") — drives the <see cref="ValueSourceKind.StyleWhen"/> provenance (PD25).</summary>
    internal override bool IsConditionalStyleRule => Rule.HasWhenConditions;

    /// <inheritdoc/>
    internal override bool TryGetResourceKey(UIProperty property, out object? key)
    {
        if (_resourceEntries is { } entries)
            foreach (var backed in entries)
                if (ReferenceEquals(backed.Property, property))
                {
                    key = backed.Key;
                    return true;
                }

        key = null;
        return false;
    }

    /// <summary>The channel layer the arming scope assigned (diagnostics).</summary>
    internal StyleLayer Layer { get; }

    /// <summary>The arming scope's owner (a <see cref="UIElement"/>, the <see cref="UIApplication"/>, or the element itself for explicit styles).</summary>
    internal object? ScopeOwner { get; }

    /// <summary>
    /// The ancestor-state requirements bound at arm time (<c>Pane:focus-within Widget</c> — doc
    /// §3.3), or <see langword="null"/> when the rule has none. Rebound on re-match.
    /// </summary>
    internal AncestorStateRequirement[]? AncestorRequirements { get; set; }

    /// <summary>
    /// The <c>When</c> data-condition requirements armed at arm time (design doc §3.3 / §6.8) — one
    /// live <see cref="Data.IBindingWatch"/> per condition, or <see langword="null"/> when the rule
    /// carries no conditions. Disposed at disarm/detach; the watcher lifetime equals the armed rule's
    /// (ledger B16). The frame keeps its watches across re-match (a survivor keeps live watchers).
    /// </summary>
    internal WhenConditionRequirement[]? WhenRequirements { get; set; }

    /// <inheritdoc/>
    public override int EntryCount => _entries.Length;

    /// <inheritdoc/>
    public override IValueEntry GetEntry(int index) => _entries[index];

    /// <summary>Participation on (the Phase-2 activation edge; allocation-free through the store).</summary>
    internal void Activate() => SetActive(true);

    /// <summary>Participation off (the Phase-2 retraction edge; the store promotes the next source).</summary>
    internal void Deactivate() => SetActive(false);

    /// <summary>In-place entry mutation + re-emit (the B10 pulse shape; conformance-kit surface at P3).</summary>
    internal void SetEntryValue(UIProperty property, object? boxedValue)
    {
        var entry = FindEntry(property);
        ((IStyleSetterEntry)entry).SetValueBoxed(boxedValue);
        OnEntryChanged(entry);
    }

    /// <summary>In-place entry unset + re-emit (a pulse resolving to <c>UnsetValue</c> — entry-unset, never a value write).</summary>
    internal void UnsetEntryValue(UIProperty property)
    {
        var entry = FindEntry(property);
        ((IStyleSetterEntry)entry).Unset();
        OnEntryChanged(entry);
    }

    private IValueEntry FindEntry(UIProperty property)
    {
        foreach (var entry in _entries)
        {
            if (ReferenceEquals(entry.Property, property))
                return entry;
        }

        throw new ArgumentException($"The frame carries no entry for '{property}'.", nameof(property));
    }

    // ───────────────────────────── DynamicResource setters (design doc §11.5 / B10) ─────────────────────────────

    /// <inheritdoc/>
    internal override void OnInstalled()
    {
        if (_resourceEntries is not { Length: > 0 } resourceEntries || Owner is not { } owner)
            return;

        // One chain subscription per resource-backed setter, against the owner element's resource chain.
        // The initial value flows into the entry now (before the store's first reevaluation), so the
        // arm-time arbitration already sees the resolved palette value; later variant-flip / shadowing
        // pulses re-push in place via SetEntryValue/UnsetEntryValue (never remove/re-add — §2.4).
        var subscriptions = new ResourceSubscription[resourceEntries.Length];
        for (var i = 0; i < resourceEntries.Length; i++)
        {
            var backed = resourceEntries[i];
            subscriptions[i] = ResourceServices.Subscribe(owner, backed.Key, backed.Listener(this), out var initial);
            ApplyResource(backed, initial);
        }

        _resourceSubscriptions = subscriptions;
    }

    /// <inheritdoc/>
    internal override void OnRemoving()
    {
        if (_resourceSubscriptions is not { } subscriptions)
            return;

        foreach (var subscription in subscriptions)
            subscription.Dispose();

        _resourceSubscriptions = null;
    }

    // Pushes a resolved resource value into a backed entry and re-emits in place. A miss
    // (UnsetValue) or a type-incompatible resource leaves the entry valueless so the store promotes the
    // next source (CD12) — never a wrong-typed clobber.
    private void ApplyResource(ResourceBackedEntry backed, object? value)
    {
        if (ReferenceEquals(value, UIProperty.UnsetValue))
        {
            if (backed.Entry.HasValue)
                UnsetEntryValue(backed.Property);
            return;
        }

        if (!backed.Property.PropertyType.IsInstanceOfType(value) &&
            !(value is null && backed.Property.PropertyType.IsNullableType()))
        {
            ResourceDiagnostics.OnRejectedValue(
                backed.Key,
                $"resource '{backed.Key}' resolved to {value?.GetType().Name ?? "null"}, incompatible with " +
                $"style setter target '{backed.Property.Name}' ({backed.Property.PropertyType.Name})");

            if (backed.Entry.HasValue)
                UnsetEntryValue(backed.Property);
            return;
        }

        SetEntryValue(backed.Property, value);
    }

    /// <inheritdoc/>
    public override string ToString() => $"frame[{Rule}] key={SortKey} active={IsActive}";

    // One resource-backed setter: the target property, its style entry, and the resource key. The
    // listener is created lazily per (frame, entry) so a pulse routes back to ApplyResource on this frame.
    private sealed class ResourceBackedEntry(UIProperty property, IStyleSetterEntry entry, object key)
    {
        internal UIProperty Property { get; } = property;
        internal IStyleSetterEntry Entry { get; } = entry;
        internal object Key { get; } = key;

        internal IResourceChangeListener Listener(StyleRuleFrame frame) => new Change(frame, this);

        private sealed class Change(StyleRuleFrame frame, ResourceBackedEntry backed) : IResourceChangeListener
        {
            public void OnResourceChanged(object key, object? newValue) => frame.ApplyResource(backed, newValue);
        }
    }
}
