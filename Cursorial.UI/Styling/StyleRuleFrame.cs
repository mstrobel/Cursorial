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

    internal StyleRuleFrame(
        UIElement? owner, CompiledRule rule, StyleSortKey sortKey, StyleLayer layer, object? scopeOwner,
        bool isActive = false)
        : base(sortKey, isActive)
    {
        Owner = owner;
        Rule = rule;
        Layer = layer;
        ScopeOwner = scopeOwner;

        var setters = rule.Setters;
        var entries = new IValueEntry[setters.Length];
        for (var i = 0; i < setters.Length; i++)
            entries[i] = setters[i].Property.CreateStyleEntry(setters[i].Value, hasValue: !setters[i].IsUnset);

        _entries = entries;
    }

    /// <summary>The styled element (null only for conformance-kit frames hosted on bare <see cref="UIObject"/>s).</summary>
    internal UIElement? Owner { get; }

    /// <summary>The armed compiled rule (shared, immutable; the SD21 identity-diff key).</summary>
    internal CompiledRule Rule { get; }

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

    /// <inheritdoc/>
    public override string ToString() => $"frame[{Rule}] key={SortKey} active={IsActive}";
}
