// ReSharper disable CheckNamespace

using System.Runtime.CompilerServices;

namespace Cursorial.UI;

/// <summary>
/// Non-generic base for the per-property store entry. Carries the lane bookkeeping the untyped read
/// surfaces (<c>IsSet</c>, <c>GetValueSource</c>) need without knowing <c>T</c>, plus the virtuals
/// the untyped mouths (<c>ClearValue</c>, <c>CoerceValue</c>, defer flush, teardown) dispatch
/// through — the entry knows its typed property, so untyped operations recover the typed context
/// virtually with no reflection.
/// </summary>
internal abstract class EffectiveValueBase
{
    /// <summary>
    /// The packed lane/coercion bookkeeping bits. Each former <c>bool</c> field is now one flag here,
    /// surfaced through a property so the read/write surface is unchanged — the storage just folds ten
    /// one-byte fields into a single <see cref="EntryFlags"/> word.
    /// </summary>
    [Flags]
    protected enum EntryFlags
    {
        None = 0,

        /// <summary>Backs <see cref="HasLocal"/>.</summary>
        HasLocal = 1 << 0,

        /// <summary>Backs <see cref="LocalValueFromEntry"/>.</summary>
        LocalValueFromEntry = 1 << 1,

        /// <summary>Backs <see cref="HasTemplate"/>.</summary>
        HasTemplate = 1 << 2,

        /// <summary>Backs <see cref="TemplateValueFromEntry"/>.</summary>
        TemplateValueFromEntry = 1 << 3,

        /// <summary>Backs <see cref="TemplateIsCoerced"/>.</summary>
        TemplateIsCoerced = 1 << 4,

        /// <summary>Backs <see cref="LocalIsCurrentValueOnly"/>.</summary>
        LocalIsCurrentValueOnly = 1 << 5,

        /// <summary>Backs <see cref="IsCurrentValue"/>.</summary>
        IsCurrentValue = 1 << 6,

        /// <summary>Backs <see cref="HasAnimatedValue"/>.</summary>
        HasAnimatedValue = 1 << 7,

        /// <summary>Backs <see cref="IsCoerced"/>.</summary>
        IsCoerced = 1 << 8,

        /// <summary>Backs <see cref="BaseIsCoerced"/>.</summary>
        BaseIsCoerced = 1 << 9,
    }

    /// <summary>The packed backing store for every boolean lane bit.</summary>
    private EntryFlags _flags;

    /// <summary>Reads one flag bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Get(EntryFlags flag) => (_flags & flag) != 0;

    /// <summary>Sets or clears one flag bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Set(EntryFlags flag, bool value)
    {
        if (value)
            _flags |= flag;
        else
            _flags &= ~flag;
    }

    /// <summary>
    /// The lane the effective value resolved from; <see cref="BindingPriority.Unset"/> when the
    /// entry currently contributes nothing (reads fall through to the metadata default — the entry
    /// is kept so re-application holds no ghost state, matrix M115).
    /// </summary>
    public BindingPriority EffectivePriority = BindingPriority.Unset;

    /// <summary>
    /// The lane of the strongest sub-Animation contribution (the winning base, ledger A20). Equal
    /// to <see cref="EffectivePriority"/> while no animation holds the property.
    /// </summary>
    public BindingPriority BasePriority = BindingPriority.Unset;

    /// <summary>Whether a local-lane contribution is present (raw value stored, PD6).</summary>
    public bool HasLocal
    {
        get => Get(EntryFlags.HasLocal);
        set => Set(EntryFlags.HasLocal, value);
    }

    /// <summary>Whether the local slot's current value was pushed by <see cref="LocalEntry"/> (vs a plain <c>SetValue</c>) — eviction withdraws only the entry's own contribution.</summary>
    public bool LocalValueFromEntry
    {
        get => Get(EntryFlags.LocalValueFromEntry);
        set => Set(EntryFlags.LocalValueFromEntry, value);
    }

    /// <summary>
    /// Whether a Template-lane contribution is present (precedence matrix §20, PD24): a raw + coerced
    /// value stored, the structural twin of <see cref="HasLocal"/> one rung below Style. Set by
    /// <c>SetTemplateValue</c> — a literal <c>SetValue</c>, a <c>{TemplateBinding}</c>/<c>{Binding}</c>,
    /// or a <c>SetResourceReference</c> issued inside the template-instantiation scope.
    /// </summary>
    public bool HasTemplate
    {
        get => Get(EntryFlags.HasTemplate);
        set => Set(EntryFlags.HasTemplate, value);
    }

    /// <summary>Whether the Template slot's value was pushed by <see cref="TemplateEntry"/> (vs a literal in-template <c>SetValue</c>) — eviction withdraws only the entry's own contribution.</summary>
    public bool TemplateValueFromEntry
    {
        get => Get(EntryFlags.TemplateValueFromEntry);
        set => Set(EntryFlags.TemplateValueFromEntry, value);
    }

    /// <summary>Whether the winning Template-base value was modified by the coercer when produced (resurfaces into <see cref="IsCoerced"/> when Template wins).</summary>
    public bool TemplateIsCoerced
    {
        get => Get(EntryFlags.TemplateIsCoerced);
        set => Set(EntryFlags.TemplateIsCoerced, value);
    }

    /// <summary>
    /// Whether the local contribution exists only as the <c>SetCurrentValue</c> no-contribution
    /// graft (matrix M118): the overlay rode Default/Inherited, so it grafted as local for storage —
    /// but a <em>style producer arriving later replaces it</em> (A11 "a producer change replaces the
    /// overlay"; style matrix S100). A real <c>SetValue</c> clears the flag; the graft refresh keeps it.
    /// </summary>
    public bool LocalIsCurrentValueOnly
    {
        get => Get(EntryFlags.LocalIsCurrentValueOnly);
        set => Set(EntryFlags.LocalIsCurrentValueOnly, value);
    }

    /// <summary>The <c>+cur</c> bit: the effective value was overwritten by <c>SetCurrentValue</c>.</summary>
    public bool IsCurrentValue
    {
        get => Get(EntryFlags.IsCurrentValue);
        set => Set(EntryFlags.IsCurrentValue, value);
    }

    /// <summary>Whether the Animation lane holds a value (an attached handle has pushed — PD4: a fresh handle is inert).</summary>
    public bool HasAnimatedValue
    {
        get => Get(EntryFlags.HasAnimatedValue);
        set => Set(EntryFlags.HasAnimatedValue, value);
    }

    /// <summary>Whether the effective value was modified by the coercer when produced (the <see cref="ValueSource.IsCoerced"/> annotation).</summary>
    public bool IsCoerced
    {
        get => Get(EntryFlags.IsCoerced);
        set => Set(EntryFlags.IsCoerced, value);
    }

    /// <summary>Whether the winning base value was modified by the coercer when produced (resurfaces into <see cref="IsCoerced"/> when an animation ends).</summary>
    public bool BaseIsCoerced
    {
        get => Get(EntryFlags.BaseIsCoerced);
        set => Set(EntryFlags.BaseIsCoerced, value);
    }

    /// <summary>
    /// The installed local-priority binding entry, or <see langword="null"/> (at most one — a newer
    /// install displaces the prior with eviction, matrix PD12).
    /// </summary>
    public BindingEntryBase? LocalEntry;

    /// <summary>
    /// The installed Template-priority binding entry, or <see langword="null"/> (at most one; the
    /// Template-lane twin of <see cref="LocalEntry"/>). A <c>{TemplateBinding}</c>/<c>{Binding}</c> or
    /// <c>SetResourceReference</c> installed inside the template-instantiation scope.
    /// </summary>
    public BindingEntryBase? TemplateEntry;

    /// <summary>
    /// The identity of the frame entry the winning base resolved from (<see langword="null"/> for
    /// local/default bases) — lets a <c>SetCurrentValue</c> overwrite survive re-evaluations whose
    /// winner is the same un-re-emitted contribution (matrix M120 vs M122).
    /// </summary>
    public object? BaseContributor;

    /// <summary>Whether a coalesced change is waiting for the defer-scope flush (ledger A23).</summary>
    public bool HasPendingNotification;

    /// <summary>The coalesced pending change's priority (last change wins, matrix M250).</summary>
    public BindingPriority PendingPriority;

    /// <summary>Removes the local contribution — value and entry alike (ledger A9) — and promotes the next source.</summary>
    public abstract void ClearLocal(ValueStore store);

    /// <summary>Re-runs the coercer against the stored raw local value (PD6; no-op without one).</summary>
    public abstract void RecoerceLocal(ValueStore store);

    /// <summary>Re-runs the coercer against the stored raw Template-lane value (PD6 parity, §20; no-op without one). Re-arbitrates: notifies when Template wins, silent when masked.</summary>
    public abstract void RecoerceTemplate(ValueStore store);

    /// <summary>Delivers the coalesced deferred change, equality-gated on first-old vs last-new (M245).</summary>
    public abstract void FlushPending(ValueStore store);

    /// <summary>Teardown sweep (ledger A13): evicts the local entry, silently detaches the animation handle.</summary>
    public abstract void TearDownContributions();

    /// <summary>The boxed effective value, interned per entry (the untyped/diagnostics read mouth).</summary>
    public abstract object? GetEffectiveBoxedValue();

    /// <summary>The boxed raw (pre-coercion) local value, or <see langword="null"/> when no local contribution exists.</summary>
    public abstract object? GetRawLocalBoxedValue();

    /// <summary>The boxed raw (pre-coercion) Template-lane value, or <see langword="null"/> when no template contribution exists (§20).</summary>
    public abstract object? GetRawTemplateBoxedValue();

    /// <summary>The property this entry stores values for — the untyped handle the diagnostics enumeration needs.</summary>
    public abstract UIProperty PropertyUntyped { get; }
}

/// <summary>
/// The per-<c>(instance, property)</c> entry — the effective/base two-slot split from design doc
/// §2.3. <see cref="Value"/> holds the post-coercion <em>effective</em> value (what <c>GetValue</c>
/// returns — the animated value while an animation holds the property);
/// <see cref="BaseValue"/> the post-coercion winning <em>base</em> (the strongest sub-Animation
/// contribution, which keeps living underneath an animation and absorbs masked writes);
/// <see cref="RawLocalValue"/> the pre-coercion local write (PD6 — <c>CoerceValue</c> re-runs the
/// coercer against it, the WPF Maximum/Value dance). Entries mutate in place: one allocation per
/// property that ever leaves its default, ever. <see cref="BoxedValue"/> is the untyped lane's
/// box-interning cache (invalidated on change — matrix M267).
/// </summary>
internal sealed class EffectiveValue<T> : EffectiveValueBase
{
    /// <summary>The typed property identity this entry stores values for.</summary>
    public readonly StyledProperty<T> Property;

    /// <inheritdoc/>
    public override UIProperty PropertyUntyped => Property;

    /// <summary>The post-coercion effective value; valid while <see cref="EffectiveValueBase.EffectivePriority"/> is not <c>Unset</c>.</summary>
    public T Value = default!;

    /// <summary>The post-coercion winning-base value; valid while <see cref="EffectiveValueBase.BasePriority"/> is not <c>Unset</c>. Holds the coerced local value whenever <see cref="EffectiveValueBase.HasLocal"/>.</summary>
    public T BaseValue = default!;

    /// <summary>The raw (pre-coercion) local value; valid while <see cref="EffectiveValueBase.HasLocal"/> (PD6).</summary>
    public T RawLocalValue = default!;

    /// <summary>The post-coercion Template-lane value; valid while <see cref="EffectiveValueBase.HasTemplate"/>. Held separately from <see cref="BaseValue"/> because Template can be masked by Style/Local and must resurface when they withdraw (precedence matrix §20).</summary>
    public T TemplateValue = default!;

    /// <summary>The raw (pre-coercion) Template-lane value; valid while <see cref="EffectiveValueBase.HasTemplate"/> (PD6 parity — <c>CoerceValue</c> re-runs against it when Template wins).</summary>
    public T RawTemplateValue = default!;

    /// <summary>The deferred change's first-old snapshot; valid while <see cref="EffectiveValueBase.HasPendingNotification"/>.</summary>
    public T PendingOldValue = default!;

    /// <summary>Box-interning cache for the untyped read lane; <see langword="null"/> after any value change.</summary>
    public object? BoxedValue;

    /// <summary>The attached animation handle, or <see langword="null"/> (last-started wins; PD4 inert until its first push).</summary>
    public AnimatedValueHandle<T>? AnimationHandle;

    public EffectiveValue(StyledProperty<T> property) => Property = property;

    /// <inheritdoc/>
    public override void ClearLocal(ValueStore store)
    {
        // A9: ClearValue is the binding kill — evict the local entry first (PD2: evict →
        // recompute → notify), even when it is currently valueless.
        var evicted = LocalEntry;
        if (evicted is not null)
        {
            LocalEntry = null;
            evicted.Evict();
        }

        if (!HasLocal)
        {
            if (evicted is null)
                return; // no local contribution at all — no-op, silent (M21, M125, M162)
        }
        else
        {
            HasLocal = false;
            LocalValueFromEntry = false;
            LocalIsCurrentValueOnly = false;
            RawLocalValue = default!;
        }

        store.Reevaluate(Property, changedEntry: null);
    }

    /// <inheritdoc/>
    public override void RecoerceLocal(ValueStore store)
    {
        if (!HasLocal)
            return; // default lane is never coerced (PD8, M234)

        var metadata = Property.GetMetadata(store.Owner.GetType());
        if (metadata.Coerce is not { } coerce)
            return;

        var coerced = coerce(store.Owner, RawLocalValue); // against the RAW value (PD6, M232)
        var comparer = metadata.EffectiveComparer;
        BaseIsCoerced = !comparer.Equals(RawLocalValue, coerced);
        if (comparer.Equals(BaseValue, coerced))
            return;

        var oldBase = BaseValue;
        BaseValue = coerced;
        store.Owner.DispatchBaseValueChanged(Property, oldBase, coerced, HasAnimatedValue);

        if (HasAnimatedValue)
            return; // masked: the animated effective is untouched

        var oldValue = Value;
        Value = coerced;
        IsCoerced = BaseIsCoerced;
        BoxedValue = null;
        store.NotifyOrDefer(this, metadata, oldValue, coerced, EffectivePriority); // current effective lane (M233)
    }

    /// <inheritdoc/>
    public override void RecoerceTemplate(ValueStore store)
    {
        if (!HasTemplate)
            return; // no template contribution to re-coerce

        var metadata = Property.GetMetadata(store.Owner.GetType());
        if (metadata.Coerce is not { } coerce)
            return;

        var coerced = coerce(store.Owner, RawTemplateValue); // against the RAW template value (PD6, §20)
        var comparer = metadata.EffectiveComparer;
        var newIsCoerced = !comparer.Equals(RawTemplateValue, coerced);
        if (comparer.Equals(TemplateValue, coerced))
        {
            TemplateIsCoerced = newIsCoerced; // coerced result unchanged, but the flag may flip (raw differs)
            return;
        }

        TemplateValue = coerced;
        TemplateIsCoerced = newIsCoerced;
        // Re-arbitrate: the new template value flows to the effective when Template wins (one notify
        // at the Template lane, M300), or stays masked and silent under a stronger lane.
        store.Reevaluate(Property, changedEntry: null);
    }

    /// <inheritdoc/>
    public override void FlushPending(ValueStore store)
    {
        if (!HasPendingNotification)
            return;

        HasPendingNotification = false;
        var oldValue = PendingOldValue;
        PendingOldValue = default!;

        var metadata = Property.GetMetadata(store.Owner.GetType());
        var newValue = EffectivePriority != BindingPriority.Unset
            ? Value
            : store.GetUnsetFallback(Property, metadata, out _); // inherited-or-default (lazy walk)
        if (metadata.EffectiveComparer.Equals(oldValue, newValue))
            return; // first old == last new ⇒ the scope's changes canceled out (M245)

        store.Owner.DispatchPropertyChanged(Property, metadata.Changed, oldValue, newValue, PendingPriority);
    }

    /// <inheritdoc/>
    public override void TearDownContributions()
    {
        if (LocalEntry is { } local)
        {
            LocalEntry = null;
            local.Evict(); // fires OnEvicted; no change notifications (PD13)
        }

        if (TemplateEntry is { } template)
        {
            TemplateEntry = null;
            template.Evict(); // the Template-lane twin of the local entry (PD13)
        }

        if (AnimationHandle is { } handle)
        {
            AnimationHandle = null;
            handle.MarkDetached();
        }
    }

    /// <inheritdoc/>
    public override object? GetEffectiveBoxedValue() => BoxedValue ??= ValueBoxes.Box(Value);

    /// <inheritdoc/>
    public override object? GetRawLocalBoxedValue() => HasLocal ? ValueBoxes.Box(RawLocalValue) : null;

    /// <inheritdoc/>
    public override object? GetRawTemplateBoxedValue() => HasTemplate ? ValueBoxes.Box(RawTemplateValue) : null;
}
