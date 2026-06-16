using Cursorial.UI;

// ReSharper disable RedundantTypeArgumentsOfMethod

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>
/// The §0.1 fixture's <c>F(k)</c>: a minimal <see cref="ValueFrame"/> subclass with mutable typed
/// entries. Builder-style: <c>new TestValueFrame(k1).With(P, 5).WithUnset(Pc)</c>; values re-emit in
/// place via <see cref="SetEntryValue{T}"/> / <see cref="UnsetEntry{T}"/> (raising
/// <c>OnEntryChanged</c> even when the value is unchanged — the store's gate owns silence, M43);
/// activation toggles via <see cref="Activate"/> / <see cref="Deactivate"/>.
/// </summary>
public sealed class TestValueFrame : ValueFrame
{
    private readonly List<IValueEntry> _entries = [];
    private readonly bool _isConditional;

    public TestValueFrame(ulong sortKey, bool isActive = true, bool isConditional = false)
        : base(new StyleSortKey(sortKey), isActive)
    {
        _isConditional = isConditional;
    }

    /// <summary>The §20.6 <c>When</c>-guarded-rule flag (drives <see cref="ValueSourceKind.StyleWhen"/> provenance, M296).</summary>
    internal override bool IsConditionalStyleRule => _isConditional;

    public override int EntryCount => _entries.Count;

    public override IValueEntry GetEntry(int index) => _entries[index];

    /// <summary>Adds a value-bearing entry (pre-install only — frames are structurally stable).</summary>
    public TestValueFrame With<T>(StyledProperty<T> property, T value)
    {
        _entries.Add(new TestEntry<T>(property) { Value = value, HasValue = true });
        return this;
    }

    /// <summary>Adds a valueless entry (<c>F(k){P=∅}</c>).</summary>
    public TestValueFrame WithUnset<T>(StyledProperty<T> property)
    {
        _entries.Add(new TestEntry<T>(property));
        return this;
    }

    /// <summary>Adds an untyped entry (negative AddFrame-validation rows — M210/M219).</summary>
    public TestValueFrame WithUntyped(UIProperty property)
    {
        _entries.Add(new UntypedEntry(property));
        return this;
    }

    /// <summary>Mutates the first entry for <paramref name="property"/> in place and re-emits it.</summary>
    public void SetEntryValue<T>(StyledProperty<T> property, T value)
    {
        var entry = Find<T>(property);
        entry.Value = value;
        entry.HasValue = true;
        OnEntryChanged(entry);
    }

    /// <summary>Unsets the first entry for <paramref name="property"/> and re-emits it.</summary>
    public void UnsetEntry<T>(StyledProperty<T> property)
    {
        var entry = Find<T>(property);
        entry.Value = default!;
        entry.HasValue = false;
        OnEntryChanged(entry);
    }

    public void Activate() => SetActive(true);

    public void Deactivate() => SetActive(false);

    private TestEntry<T> Find<T>(StyledProperty<T> property)
        => _entries.OfType<TestEntry<T>>().First(e => ReferenceEquals(e.Property, property));

    private sealed class TestEntry<T>(StyledProperty<T> property) : IValueEntry<T>
    {
        public T Value = default!;

        public UIProperty Property { get; } = property;

        public bool HasValue { get; set; }

        public T GetValue() => Value;
    }

    private sealed class UntypedEntry(UIProperty property) : IValueEntry
    {
        public UIProperty Property { get; } = property;

        public bool HasValue => false;
    }
}

/// <summary>A recording winning-base observer (ledger A20 channel; §0.2 <c>baseNotify</c>).</summary>
public sealed class BaseRecorder<T> : IValueObserver<T>
{
    /// <summary>Base-channel deliveries in order.</summary>
    public readonly List<(T OldBase, T NewBase, bool IsAnimated)> BaseDeliveries = [];

    /// <summary>Ordinary-channel deliveries (should stay empty for an options-only subscription).</summary>
    public readonly List<(T Old, T New, BindingPriority Priority)> OrdinaryDeliveries = [];

    public void OnPropertyChanged(UIObject source, UIProperty property, T oldValue, T newValue, BindingPriority priority)
        => OrdinaryDeliveries.Add((oldValue, newValue, priority));

    public void OnBaseValueChanged(UIObject source, UIProperty property, T oldBaseValue, T newBaseValue, bool isAnimated)
        => BaseDeliveries.Add((oldBaseValue, newBaseValue, isAnimated));
}

/// <summary>A recording eviction listener (§0.2 <c>evict(X)</c>), with an optional reentrancy hook.</summary>
public sealed class EvictionRecorder : IValueEvictionListener
{
    /// <summary>Evicted entries in order.</summary>
    public readonly List<BindingEntryBase> Evictions = [];

    /// <summary>Optional reentrancy hook (e.g. disposing the entry from inside the callback — A7).</summary>
    public Action<BindingEntryBase>? Callback;

    /// <summary>Optional shared sequence log; appends "evict(Property)".</summary>
    public List<string>? SequenceLog;

    public void OnEvicted(BindingEntryBase entry)
    {
        Evictions.Add(entry);
        SequenceLog?.Add($"evict({entry.Property.Name})");
        Callback?.Invoke(entry);
    }
}
