// ReSharper disable CheckNamespace

using System.Diagnostics.CodeAnalysis;

namespace Cursorial.UI;

/// <summary>
/// The per-instance value store (design doc §2.3): a sparse effective-value table, a frame list
/// sorted by <see cref="StyleSortKey"/>, copy-on-write observer arrays, and the defer-depth +
/// pending-change bookkeeping. Lazily allocated by <see cref="UIObject"/> on the first write /
/// observe / frame / defer — a default-valued element costs zero property-system bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Storage pick.</b> The effective and observer tables are sorted <c>int[]</c> property-id
/// arrays with parallel value arrays, searched binary — the doc's named shape ("sorted <c>int[]</c>
/// ids + parallel entries, binary search, n ≤ ~32") chosen over a packed-key hashtable per §2.6-5:
/// at terminal scale a ≤5-compare binary search beats hashing on time and memory, and iteration
/// order is deterministic, which the repo's oracle-pinned test style depends on.
/// </para>
/// <para>
/// <b>Arbitration</b> (§2.2): <c>Animation &gt; LocalValue &gt; Style (single slot, within-slot
/// StyleSortKey; equal keys: later-added wins) &gt; Inherited (lazy walk) &gt; Default</c>.
/// Entries carry the effective/base two-slot split; restoration is store-owned — retraction =
/// removal + promotion of the next source, never set-back (invariant 4). Inherited values are
/// <em>never stored</em>: entries whose lanes are all unset resolve through the owner's
/// inheritance-parent walk at read time (lazy-read v1; the push-down shared-box upgrade path is
/// documented on <see cref="UIObject.SetInheritanceParent"/>).
/// </para>
/// <para>
/// Single UI thread by contract (invariant 6) — no synchronization anywhere; thread affinity is
/// debug-asserted at the <see cref="UIObject"/> mouths.
/// </para>
/// </remarks>
internal sealed class ValueStore
{
    private int[] _entryIds = [];
    private EffectiveValueBase[] _entries = [];
    private int _entryCount;

    private ValueFrame[] _frames = [];
    private int _frameCount;

    private int[] _observerIds = [];
    private PropertyObservers[] _observers = [];
    private int _observerCount;

    private List<int>? _pendingIds;
    private int _deferDepth;

    public ValueStore(UIObject owner) => Owner = owner;

    /// <summary>The object this store belongs to.</summary>
    public UIObject Owner { get; }

    // ───────────────────────────── effective-value table ─────────────────────────────

    /// <summary>The entry for <paramref name="propertyId"/>, or <see langword="null"/>.</summary>
    public EffectiveValueBase? TryGetEntry(int propertyId)
    {
        var index = Array.BinarySearch(_entryIds, 0, _entryCount, propertyId);
        return index >= 0 ? _entries[index] : null;
    }

    /// <summary>Appends each property with a live (non-<see cref="BindingPriority.Unset"/>) store contribution — the diagnostics enumeration the style inspector walks.</summary>
    internal void CollectSetProperties(List<UIProperty> into)
    {
        for (var i = 0; i < _entryCount; i++)
            if (_entries[i].EffectivePriority != BindingPriority.Unset)
                into.Add(_entries[i].PropertyUntyped);
    }

    /// <summary>
    /// The local-lane write — plain <c>SetValue</c>, <c>SetCurrentValue</c>'s as-Local graft, and
    /// local binding-entry pushes (<paramref name="writer"/> non-null) all land here; within the
    /// lane, last writer wins (§2.2). The raw value was already validated at the mouth (PD7);
    /// coercion runs here, before any entry mutation, so a throwing coercer leaves the store
    /// untouched (M241). Under an active animation this is a <em>masked base write</em>: the base
    /// updates (and the winning-base channel fires, ledger A20) but the ordinary channels stay
    /// silent (§2.3 fast path, M61).
    /// </summary>
    public void SetLocalValue<T>(
        StyledProperty<T> property, PropertyMetadata<T> metadata, T rawValue,
        bool isCurrentValue, BindingEntryBase? writer = null)
    {
        var coerced = metadata.Coerce is {} coerce ? coerce(Owner, rawValue) : rawValue;
        var comparer = metadata.EffectiveComparer;

        var entry = (EffectiveValue<T>?)TryGetEntry(property.Id);
        if (entry is { HasLocal: true } && comparer.Equals(entry.BaseValue, coerced))
            return; // same winning lane, comparer-equal ⇒ fully gated: first-stored value survives (PD20), +cur untouched (M135)

        entry ??= CreateEntry(property);

        var wasCoerced = metadata.Coerce is not null && !comparer.Equals(rawValue, coerced);
        var oldBaseValue = entry.BasePriority != BindingPriority.Unset
            ? entry.BaseValue
            : GetUnsetFallback(property, metadata, out _); // inherited-or-default (M96: the old value is the inherited one)

        // The M118 graft marker (style matrix S100/A11): a SetCurrentValue that grafted as local
        // (no prior local) stays replaceable by a later-arriving style producer; a graft refresh
        // keeps the marker, a real SetValue (or SCV over a real local) clears it.
        entry.LocalIsCurrentValueOnly = isCurrentValue && (!entry.HasLocal || entry.LocalIsCurrentValueOnly);

        entry.RawLocalValue = rawValue;
        entry.HasLocal = true;
        entry.LocalValueFromEntry = writer is not null;
        entry.BasePriority = BindingPriority.LocalValue;
        entry.BaseValue = coerced;
        entry.BaseIsCoerced = wasCoerced;
        entry.BaseContributor = null;

        var baseChanged = !comparer.Equals(oldBaseValue, coerced);
        if (baseChanged)
            Owner.DispatchBaseValueChanged(property, oldBaseValue, coerced, entry.HasAnimatedValue);

        if (entry.HasAnimatedValue)
            return; // masked base write: ordinary channels silent (M61), the animated effective is untouched

        var oldValue = entry.EffectivePriority != BindingPriority.Unset ? entry.Value : oldBaseValue;
        entry.EffectivePriority = BindingPriority.LocalValue;
        entry.Value = coerced;
        entry.IsCoerced = wasCoerced;
        entry.IsCurrentValue = isCurrentValue;

        if (!baseChanged)
            return; // equal-value lane flip: silent, but the source updated (PD9, M101) — un-animated, old effective == old base

        entry.BoxedValue = null;
        NotifyOrDefer(entry, metadata, oldValue, coerced, BindingPriority.LocalValue);
    }

    /// <summary>
    /// <c>SetCurrentValue</c> (§2.2, verbatim P3 graft): replaces the effective value in place
    /// without changing its source. No contribution ⇒ behaves as Local (M118); with one, the
    /// overwrite lands on the winning lane — the Animation lane's effective while animated (base
    /// untouched, M131), else the base (which the overwrite <em>becomes</em>, M121). The
    /// notification carries the replaced lane's priority (ledger A11).
    /// </summary>
    public void SetCurrentValue<T>(StyledProperty<T> property, PropertyMetadata<T> metadata, T rawValue)
    {
        var entry = (EffectiveValue<T>?)TryGetEntry(property.Id);

        if (entry is null || 
            entry.EffectivePriority == BindingPriority.Unset ||
            entry is { BasePriority: BindingPriority.LocalValue, HasAnimatedValue: false })
        {
            // No contribution (M118) — or the local lane already wins, where "overwrite in place"
            // IS a local write that also refreshes the raw slot (M119): both reduce to the local
            // mouth with the +cur bit.
            SetLocalValue(property, metadata, rawValue, isCurrentValue: true);
            return;
        }

        var coerced = metadata.Coerce is {} coerce ? coerce(Owner, rawValue) : rawValue;

        var comparer = metadata.EffectiveComparer;
        if (comparer.Equals(entry.Value, coerced))
            return; // gated: stored value survives (PD20), +cur NOT set (M135)

        var wasCoerced = metadata.Coerce is not null && !comparer.Equals(rawValue, coerced);
        var oldValue = entry.Value;
        var replacedLane = entry.HasAnimatedValue ? BindingPriority.Animation : entry.BasePriority;

        entry.Value = coerced;
        entry.IsCoerced = wasCoerced;
        entry.IsCurrentValue = true;

        if (!entry.HasAnimatedValue)
        {
            // Un-animated: the overwrite IS the base (M121) — and dies with its lane (M123) or on
            // the lane's next re-emit (M122). The winning-base channel sees it (A20 seam).
            var oldBaseValue = entry.BasePriority != BindingPriority.Unset ? entry.BaseValue : metadata.DefaultValue;
            entry.BaseValue = coerced;
            entry.BaseIsCoerced = wasCoerced;
            if (entry.BasePriority == BindingPriority.LocalValue)
                entry.RawLocalValue = rawValue;
            if (!comparer.Equals(oldBaseValue, coerced))
                Owner.DispatchBaseValueChanged(property, oldBaseValue, coerced, isAnimated: false);
        }

        entry.BoxedValue = null;
        NotifyOrDefer(entry, metadata, oldValue, coerced, replacedLane);
    }

    /// <summary>
    /// The Template-lane write (precedence matrix §20, PD24): a literal <c>SetValue</c> issued inside
    /// the template-instantiation scope, and the push of a Template-priority binding entry
    /// (<paramref name="writer"/> non-null — a <c>{TemplateBinding}</c>/<c>{Binding}</c> or
    /// <c>SetResourceReference</c> installed in a template). Stores the raw + coerced value in the
    /// per-entry Template slot (it can be masked by Style/Local/Animation, so it cannot win
    /// unconditionally the way the local lane does) and re-arbitrates. Within the lane, last writer
    /// wins; the raw value was validated at the mouth (PD7). A re-emit clobbers any
    /// <c>SetCurrentValue</c> overwrite riding the Template lane (M289).
    /// </summary>
    public void SetTemplateValue<T>(
        StyledProperty<T> property, PropertyMetadata<T> metadata, T rawValue, BindingEntryBase? writer = null)
    {
        var coerced = metadata.Coerce is {} coerce ? coerce(Owner, rawValue) : rawValue;
        var comparer = metadata.EffectiveComparer;

        var entry = (EffectiveValue<T>?)TryGetEntry(property.Id);

        // A re-emit of the Template contribution clobbers any SetCurrentValue overwrite riding the
        // Template lane — the Style analog (M122/M289): the template's source value (kept separately
        // in TemplateValue, never touched by the in-place overwrite) re-asserts, dropping the manual
        // overlay. This is unconditional on a producer re-emit, so it precedes the equal-value gate:
        // even when the template's own value is unchanged, the overwrite must yield (clearing +cur
        // makes Reevaluate restore the effective to the source). A masked-template update under a
        // stronger lane (BasePriority != Template) must not disturb that lane's own overwrite.
        var clobbering = entry is { BasePriority: BindingPriority.Template, IsCurrentValue: true };
        if (clobbering)
            entry!.IsCurrentValue = false;

        if (!clobbering && entry is { HasTemplate: true } && comparer.Equals(entry.TemplateValue, coerced))
            return; // same template value, no overwrite to clobber ⇒ fully gated (PD20 parity, M135 spirit)

        entry ??= CreateEntry(property);

        var wasCoerced = metadata.Coerce is not null && !comparer.Equals(rawValue, coerced);
        entry.RawTemplateValue = rawValue;
        entry.TemplateValue = coerced;
        entry.TemplateIsCoerced = wasCoerced;
        entry.HasTemplate = true;
        entry.TemplateValueFromEntry = writer is not null;

        Reevaluate(property, changedEntry: null);
    }

    /// <summary>
    /// The boxed effective value with interning (M267), the contributing ancestor's interned box
    /// for inherited reads, or the shared boxed default.
    /// </summary>
    public object? GetValueBoxed<T>(StyledProperty<T> property)
    {
        if (TryGetEntry(property.Id) is EffectiveValue<T> { EffectivePriority: not BindingPriority.Unset } entry)
            return entry.GetEffectiveBoxedValue();
        if (property.Inherits && Owner.FindInheritedEntry(property.Id, out _) is {} inherited)
            return inherited.GetEffectiveBoxedValue(); // interning rides the ancestor's entry
        return property.GetMetadata(Owner.GetType()).BoxedDefault;
    }

    /// <summary>
    /// The lane-probing read (PD16): <c>Animation</c> ⇒ the full effective; <c>LocalValue</c> ⇒ the
    /// winning base (so <c>GetBaseValue</c> sees a <c>SetCurrentValue</c> overwrite, M121);
    /// <c>Style</c> ⇒ the strongest style contribution (skipping Animation and Local);
    /// <c>Inherited</c> ⇒ the walk-up result. Every probe except <c>Default</c> falls through the
    /// rest of the ladder — inherited, then the metadata default (M114/M259–M261).
    /// </summary>
    public T GetValueAtMaxPriority<T>(StyledProperty<T> property, PropertyMetadata<T> metadata, BindingPriority maxPriority)
    {
        var entry = (EffectiveValue<T>?)TryGetEntry(property.Id);

        switch (maxPriority)
        {
            case BindingPriority.Animation:
                if (entry is { EffectivePriority: not BindingPriority.Unset })
                    return entry.Value;
                break;

            case BindingPriority.LocalValue:
                if (entry is { BasePriority: not BindingPriority.Unset })
                    return entry.BaseValue;
                break;

            case BindingPriority.Style:
                if (entry is { BasePriority: BindingPriority.Style })
                    return entry.BaseValue; // the maintained style base (a +cur overwrite included)
                if (TryResolveStyleValue(property, out var styleValue, out _))
                    return metadata.Coerce is {} coerce ? coerce(Owner, styleValue) : styleValue;
                break;

            case BindingPriority.Template:
                // Skips Animation/Local/Style (all stronger); the strongest considered is Template
                // and weaker. The maintained template base when it wins (a +cur overwrite included),
                // else the stored coerced template value when masked, else fall through.
                if (entry is { BasePriority: BindingPriority.Template })
                    return entry.BaseValue;
                if (entry is { HasTemplate: true })
                    return entry.TemplateValue;
                break;
        }

        if (maxPriority != BindingPriority.Default &&
            property.Inherits && Owner.FindInheritedEntry(property.Id, out _) is EffectiveValue<T> inherited)
        {
            return inherited.Value; // ancestor's effective — inherited reads skip re-coercion (M190)
        }

        return metadata.DefaultValue;
    }

    private EffectiveValue<T> GetOrCreateEntry<T>(StyledProperty<T> property)
        => (EffectiveValue<T>?)TryGetEntry(property.Id) ?? CreateEntry(property);

    private EffectiveValue<T> CreateEntry<T>(StyledProperty<T> property)
    {
        var entry = new EffectiveValue<T>(property);
        var index = Array.BinarySearch(_entryIds, 0, _entryCount, property.Id);
        index = ~index;

        if (_entryCount == _entryIds.Length)
        {
            var capacity = Math.Max(4, _entryIds.Length * 2);
            Array.Resize(ref _entryIds, capacity);
            Array.Resize(ref _entries, capacity);
        }

        if (index < _entryCount)
        {
            Array.Copy(_entryIds, index, _entryIds, index + 1, _entryCount - index);
            Array.Copy(_entries, index, _entries, index + 1, _entryCount - index);
        }

        _entryIds[index] = property.Id;
        _entries[index] = entry;
        _entryCount++;
        return entry;
    }

    // ───────────────────────────── re-evaluation (promotion engine) ─────────────────────────────

    /// <summary>
    /// Recomputes the winning base — local slot, else the strongest active style contribution
    /// (coerced at effective computation), else nothing — and folds the result into the entry:
    /// winning-base observers fire on base changes (ledger A20, detected under an active animation);
    /// the effective value follows the base only when un-animated (masked otherwise); equal-value
    /// promotions are silent with the source still updating (PD9); promotion notifications carry
    /// the new winning lane (PD10). A <c>SetCurrentValue</c> overwrite survives re-evaluations
    /// whose winner is the same contribution it overwrote, and is clobbered when that contribution
    /// re-emits (<paramref name="changedEntry"/>, M122) or loses (M123/M124).
    /// </summary>
    public void Reevaluate<T>(StyledProperty<T> property, IValueEntry? changedEntry)
    {
        var metadata = property.GetMetadata(Owner.GetType());
        var comparer = metadata.EffectiveComparer;
        var entry = (EffectiveValue<T>?)TryGetEntry(property.Id);

        BindingPriority newBasePriority;
        T newBaseValue;
        var newBaseIsCoerced = false;
        object? contributor = null;

        // The SetCurrentValue no-contribution graft (M118) is local-for-storage only: a style
        // producer arriving replaces the overlay (A11; style matrix S100), so a graft does not
        // short-circuit the style resolution the way a real local write does.
        var graftLocal = entry is { HasLocal: true, LocalIsCurrentValueOnly: true };

        if (entry is { HasLocal: true } && !graftLocal)
        {
            newBasePriority = BindingPriority.LocalValue;
            newBaseValue = entry.BaseValue; // the stored coerced local (never re-derived on reads)
            newBaseIsCoerced = entry.BaseIsCoerced;
        }
        else if (TryResolveStyleValue(property, out var styleRaw, out contributor))
        {
            newBasePriority = BindingPriority.Style;
            newBaseValue = metadata.Coerce is {} coerce ? coerce(Owner, styleRaw) : styleRaw;
            newBaseIsCoerced = metadata.Coerce is not null && !comparer.Equals(styleRaw, newBaseValue);

            if (graftLocal)
            {
                // The graft evaporates — the producer it stood in for has arrived.
                entry!.HasLocal = false;
                entry.LocalValueFromEntry = false;
                entry.LocalIsCurrentValueOnly = false;
                entry.RawLocalValue = default!;
            }
        }
        else if (entry is { HasTemplate: true })
        {
            // The Template lane (§20, PD24): one rung below Style, above Inherited. Stored coerced
            // (never re-derived on reads), the structural twin of the local branch.
            newBasePriority = BindingPriority.Template;
            newBaseValue = entry.TemplateValue;
            newBaseIsCoerced = entry.TemplateIsCoerced;

            if (graftLocal)
            {
                // The SCV graft yields to a Template producer too, not only a Style one (PD24, M287).
                entry.HasLocal = false;
                entry.LocalValueFromEntry = false;
                entry.LocalIsCurrentValueOnly = false;
                entry.RawLocalValue = default!;
            }
        }
        else if (graftLocal)
        {
            // No style/template producer: the graft keeps holding the local lane (M118 storage semantics).
            newBasePriority = BindingPriority.LocalValue;
            newBaseValue = entry!.BaseValue;
            newBaseIsCoerced = entry.BaseIsCoerced;
        }
        else
        {
            newBasePriority = BindingPriority.Unset;
            newBaseValue = default!;
        }

        if (entry is null)
        {
            if (newBasePriority == BindingPriority.Unset)
                return; // nothing before, nothing now
            entry = CreateEntry(property);
        }

        // The promotion fallback when no lane contributes: the inherited walk-up result, else the
        // metadata default (Inherited slots between Style and Default, §2.2). Computed once — the
        // upstream chain is stable during a node-local re-evaluation.
        var fallbackLane = BindingPriority.Default;
        var fallbackValue = default(T)!;
        if (entry.BasePriority == BindingPriority.Unset || newBasePriority == BindingPriority.Unset)
            fallbackValue = GetUnsetFallback(property, metadata, out fallbackLane);

        // The +cur overwrite holds while the same un-re-emitted contribution still wins (M120/M125
        // vs M122/M123); animated overwrites live on the Animation lane and are untouched by base
        // recomputation (M128/M131) — they fall through to the masked-write return below.
        if (entry is { IsCurrentValue: true, HasAnimatedValue: false } &&
            newBasePriority == entry.BasePriority &&
            ReferenceEquals(contributor, entry.BaseContributor) &&
            (changedEntry is null || !ReferenceEquals(contributor, changedEntry)))
        {
            return;
        }

        var oldBaseValue = entry.BasePriority != BindingPriority.Unset ? entry.BaseValue : fallbackValue;
        var newBaseForNotify = newBasePriority != BindingPriority.Unset ? newBaseValue : fallbackValue;

        entry.BasePriority = newBasePriority;
        entry.BaseValue = newBasePriority != BindingPriority.Unset ? newBaseValue : default!;
        entry.BaseIsCoerced = newBaseIsCoerced;
        entry.BaseContributor = contributor;

        if (!comparer.Equals(oldBaseValue, newBaseForNotify))
            Owner.DispatchBaseValueChanged(property, oldBaseValue, newBaseForNotify, entry.HasAnimatedValue);

        if (entry.HasAnimatedValue)
            return; // the animation masks the base: ordinary channels silent (M68/M117)

        var oldValue = entry.EffectivePriority != BindingPriority.Unset ? entry.Value : oldBaseValue;
        entry.EffectivePriority = newBasePriority;
        entry.Value = entry.BaseValue;
        entry.IsCoerced = newBasePriority != BindingPriority.Unset && newBaseIsCoerced;
        entry.IsCurrentValue = false; // the lane re-derived: any overwrite is clobbered (M122)

        if (comparer.Equals(oldValue, newBaseForNotify))
            return; // equal-value promotion is silent; the source still updates (PD9, M47)

        entry.BoxedValue = null;
        NotifyOrDefer(entry, metadata, oldValue, newBaseForNotify,
            newBasePriority != BindingPriority.Unset ? newBasePriority : fallbackLane); // PD10 (Inherited/Default)
    }

    /// <summary>
    /// The resolution fallback below the Style slot: the owner's inherited walk-up result with
    /// <paramref name="lane"/> = <see cref="BindingPriority.Inherited"/> when a contributing
    /// ancestor exists, else the metadata default at <see cref="BindingPriority.Default"/>.
    /// Inherited reads return the ancestor's effective value and skip re-coercion (M190/M242).
    /// </summary>
    internal T GetUnsetFallback<T>(StyledProperty<T> property, PropertyMetadata<T> metadata, out BindingPriority lane)
    {
        if (property.Inherits && Owner.FindInheritedEntry(property.Id, out _) is EffectiveValue<T> inherited)
        {
            lane = BindingPriority.Inherited;
            return inherited.Value;
        }

        lane = BindingPriority.Default;
        return metadata.DefaultValue;
    }

    // ───────────────────────────── animation lane ─────────────────────────────

    /// <summary>
    /// Attaches a fresh animation handle (the sole Animation producer, PD1). A prior handle is
    /// detached — last-started wins (M63) — but its pushed value persists until the new handle's
    /// first push (PD4 inertia).
    /// </summary>
    public AnimatedValueHandle<T> BeginAnimation<T>(StyledProperty<T> property)
    {
        var entry = GetOrCreateEntry(property);
        entry.AnimationHandle?.MarkDetached();

        var handle = new AnimatedValueHandle<T>(Owner, property);
        entry.AnimationHandle = handle;
        return handle;
    }

    /// <summary>
    /// The per-frame animated write: in-place mutate with the equality gate on the <em>coerced</em>
    /// value (M71/M72 — the §2.6-6 guardrail applies to animated values too). Returns whether the
    /// effective value actually changed (ledger A18). An equal-valued first push still flips the
    /// source to Animation, silently (PD9 lane flip, M59). Allocation-free steady-state (M265).
    /// </summary>
    public bool SetAnimatedValue<T>(StyledProperty<T> property, PropertyMetadata<T> metadata, T rawValue)
    {
        var entry = GetOrCreateEntry(property);
        var coerced = metadata.Coerce is {} coerce ? coerce(Owner, rawValue) : rawValue;

        var oldValue = entry.EffectivePriority != BindingPriority.Unset
            ? entry.Value
            : GetUnsetFallback(property, metadata, out _); // inherited-or-default (M84/M87)
        entry.HasAnimatedValue = true;
        entry.EffectivePriority = BindingPriority.Animation;

        if (metadata.EffectiveComparer.Equals(oldValue, coerced))
        {
            entry.Value = oldValue; // silent lane flip: the prior effective is the representative (PD20 spirit)
            return false;
        }

        entry.Value = coerced;
        entry.IsCoerced = metadata.Coerce is not null && !metadata.EffectiveComparer.Equals(rawValue, coerced);
        entry.IsCurrentValue = false; // the animation lane (re)asserts: any overwrite is clobbered (M129)
        entry.BoxedValue = null;
        NotifyOrDefer(entry, metadata, oldValue, coerced, BindingPriority.Animation);
        return true;
    }

    /// <summary>
    /// Ends an animation (handle disposal): the base resurfaces with one notification at its own
    /// lane (PD10), silent when comparer-equal (M60/M79 — the source still flips). Detaching an
    /// inert handle (PD4 — never pushed) is silent.
    /// </summary>
    public void EndAnimation<T>(StyledProperty<T> property, AnimatedValueHandle<T> handle)
    {
        if (TryGetEntry(property.Id) is not EffectiveValue<T> entry || !ReferenceEquals(entry.AnimationHandle, handle))
            return; // displaced or torn down since — nothing to do

        entry.AnimationHandle = null;
        if (!entry.HasAnimatedValue)
            return; // PD4: an inert handle holds nothing

        entry.HasAnimatedValue = false;

        var metadata = property.GetMetadata(Owner.GetType());
        var oldValue = entry.Value; // EffectivePriority is Animation here

        T newValue;
        BindingPriority newLane;
        if (entry.BasePriority != BindingPriority.Unset)
        {
            newValue = entry.BaseValue;
            newLane = entry.BasePriority;
        }
        else
        {
            newValue = GetUnsetFallback(property, metadata, out newLane); // inherited-or-default (M85/M117)
        }

        entry.EffectivePriority = entry.BasePriority;
        entry.Value = entry.BasePriority != BindingPriority.Unset ? entry.BaseValue : default!;
        entry.IsCoerced = entry.BasePriority != BindingPriority.Unset && entry.BaseIsCoerced;
        entry.IsCurrentValue = false; // an overwrite of the animated effective dies with its lane (M130)

        if (metadata.EffectiveComparer.Equals(oldValue, newValue))
            return; // equal promotion: silent, source flipped (PD9, M60)

        entry.BoxedValue = null;
        NotifyOrDefer(entry, metadata, oldValue, newValue, newLane);
    }

    // ───────────────────────────── local binding entries ─────────────────────────────

    /// <summary>
    /// Installs a free-standing local-priority entry (ledger A6/A8): valueless until its first
    /// push. A prior local entry is <em>displaced</em> — evicted with
    /// <see cref="IValueEvictionListener.OnEvicted"/>, its value withdrawn, the next source
    /// promoted (PD12/M147) — before the new entry installs.
    /// </summary>
    public BindingEntry<T> BindLocal<T>(StyledProperty<T> property, IValueEvictionListener? listener)
    {
        var entry = GetOrCreateEntry(property);

        if (entry.LocalEntry is {} prior)
        {
            entry.LocalEntry = null;
            prior.Evict(); // PD2: the eviction fires before the promotion's notification
            if (entry.LocalValueFromEntry)
            {
                entry.HasLocal = false;
                entry.LocalValueFromEntry = false;
                entry.LocalIsCurrentValueOnly = false;
                entry.RawLocalValue = default!;
                Reevaluate(property, changedEntry: null);
            }
        }

        var bindingEntry = new BindingEntry<T>(Owner, property, BindingPriority.LocalValue, hostFrame: null, listener);
        entry.LocalEntry = bindingEntry;
        return bindingEntry;
    }

    /// <summary>
    /// Withdraws the local slot's value (a binding entry pushed unset — an unset is a write, so it
    /// clears the lane regardless of last writer) and promotes the next source (A8/M139). Keeps the
    /// entry installed.
    /// </summary>
    public void UnsetLocalValue<T>(StyledProperty<T> property)
    {
        if (TryGetEntry(property.Id) is not EffectiveValue<T> { HasLocal: true } entry)
            return;

        entry.HasLocal = false;
        entry.LocalValueFromEntry = false;
        entry.LocalIsCurrentValueOnly = false;
        entry.RawLocalValue = default!;
        Reevaluate(property, changedEntry: null);
    }

    /// <summary>
    /// Detaches a self-disposed local entry (PD12: no <c>OnEvicted</c>) and withdraws its value —
    /// but only when the slot's current value is the entry's own contribution (a plain
    /// <c>SetValue</c> that out-wrote it survives).
    /// </summary>
    public void DetachLocalEntry<T>(StyledProperty<T> property, BindingEntryBase bindingEntry)
    {
        if (TryGetEntry(property.Id) is not EffectiveValue<T> entry)
            return;

        if (ReferenceEquals(entry.LocalEntry, bindingEntry))
            entry.LocalEntry = null;

        if (entry is { HasLocal: true, LocalValueFromEntry: true })
        {
            entry.HasLocal = false;
            entry.LocalValueFromEntry = false;
            entry.LocalIsCurrentValueOnly = false;
            entry.RawLocalValue = default!;
            Reevaluate(property, changedEntry: null);
        }
    }

    // ───────────────────────────── template binding entries (the Template lane) ─────────────────────────────

    /// <summary>
    /// Installs a Template-priority entry (precedence matrix §20, PD24) — the Template-lane twin of
    /// <see cref="BindLocal{T}"/>: a <c>{TemplateBinding}</c>/<c>{Binding}</c> or
    /// <c>SetResourceReference</c> installed inside the template-instantiation scope. Valueless until
    /// its first push; a prior template entry is displaced with eviction (PD12).
    /// </summary>
    public BindingEntry<T> BindTemplate<T>(StyledProperty<T> property, IValueEvictionListener? listener)
    {
        var entry = GetOrCreateEntry(property);

        if (entry.TemplateEntry is {} prior)
        {
            entry.TemplateEntry = null;
            prior.Evict(); // PD2: the eviction fires before the promotion's notification
            if (entry.TemplateValueFromEntry)
            {
                entry.HasTemplate = false;
                entry.TemplateValueFromEntry = false;
                entry.TemplateValue = default!;
                entry.RawTemplateValue = default!;
                Reevaluate(property, changedEntry: null);
            }
        }

        var bindingEntry = new BindingEntry<T>(Owner, property, BindingPriority.Template, hostFrame: null, listener);
        entry.TemplateEntry = bindingEntry;
        return bindingEntry;
    }

    /// <summary>Withdraws the Template slot's value (a template entry pushed unset) and promotes the next source; keeps the entry installed.</summary>
    public void UnsetTemplateValue<T>(StyledProperty<T> property)
    {
        if (TryGetEntry(property.Id) is not EffectiveValue<T> { HasTemplate: true } entry)
            return;

        entry.HasTemplate = false;
        entry.TemplateValueFromEntry = false;
        entry.TemplateValue = default!;
        entry.RawTemplateValue = default!;
        Reevaluate(property, changedEntry: null);
    }

    /// <summary>Detaches a self-disposed template entry (PD12: no <c>OnEvicted</c>) and withdraws its value when the slot still holds the entry's own contribution.</summary>
    public void DetachTemplateEntry<T>(StyledProperty<T> property, BindingEntryBase bindingEntry)
    {
        if (TryGetEntry(property.Id) is not EffectiveValue<T> entry)
            return;

        if (ReferenceEquals(entry.TemplateEntry, bindingEntry))
            entry.TemplateEntry = null;

        if (entry is { HasTemplate: true, TemplateValueFromEntry: true })
        {
            entry.HasTemplate = false;
            entry.TemplateValueFromEntry = false;
            entry.TemplateValue = default!;
            entry.RawTemplateValue = default!;
            Reevaluate(property, changedEntry: null);
        }
    }

    // ───────────────────────────── frames (the Style slot) ─────────────────────────────

    /// <summary>
    /// Installs a frame in the single Style slot, sorted by <see cref="StyleSortKey"/> (equal keys:
    /// later-added wins, M38). Re-adding an installed frame throws (PD21); frames carrying entries
    /// for read-only properties are rejected (PD14/M210), for direct properties likewise (A24/M219).
    /// Active frames apply with one notification per changed property (M49).
    /// </summary>
    public void AddFrame(ValueFrame frame)
    {
        if (ReferenceEquals(frame.Store, this))
            throw new InvalidOperationException("The frame is already installed on this object (PD21).");
        if (frame.Store is not null)
            throw new InvalidOperationException("The frame is installed on another object; a ValueFrame is a per-element shim.");

        for (var i = 0; i < frame.EntryCount; i++)
        {
            var property = frame.GetEntry(i).Property;
            if (property.IsReadOnly)
                throw new InvalidOperationException($"Frame entry {i} targets read-only property '{property}'; read-only properties are not styleable (PD14).");
            if (property.IsDirect)
                throw new ArgumentException($"Frame entry {i} targets direct property '{property}'; direct properties have no store ladder (ledger A24).", nameof(frame));
        }

        var index = _frameCount;
        while (index > 0 && frame.SortKey < _frames[index - 1].SortKey)
            index--;

        if (_frameCount == _frames.Length)
            Array.Resize(ref _frames, Math.Max(4, _frames.Length * 2));
        if (index < _frameCount)
            Array.Copy(_frames, index, _frames, index + 1, _frameCount - index);

        _frames[index] = frame;
        _frameCount++;
        frame.Store = this;

        // The B10 install hook: a resource-backed frame opens its subscriptions and pushes the first
        // resolved values BEFORE the reevaluation, so the initial arbitration sees the resolved value.
        frame.OnInstalled();

        if (frame.IsActive)
            ReevaluateFrameProperties(frame, hosted: null);
    }

    /// <summary>
    /// Removes a frame — the engine-level form of cookie retraction. Pinned order (PD2/M157/M158):
    /// ① every hosted entry is evicted (<c>OnEvicted</c>, in index order) ② the store recomputes
    /// ③ one promotion notification per changed property. Eviction is lifecycle, not value-driven —
    /// it fires even for masked or inactive contributions (M164).
    /// </summary>
    public void RemoveFrame(ValueFrame frame)
    {
        if (!ReferenceEquals(frame.Store, this))
            throw new ArgumentException("The frame is not installed on this object (PD21).", nameof(frame));

        // The B10 teardown hook: close any resource subscriptions while the frame is still installed.
        frame.OnRemoving();

        var index = Array.IndexOf(_frames, frame, 0, _frameCount);
        _frameCount--;
        if (index < _frameCount)
            Array.Copy(_frames, index + 1, _frames, index, _frameCount - index);
        _frames[_frameCount] = null!;
        frame.Store = null;

        var hosted = frame.TakeHostedEntries();
        if (hosted is not null)
        {
            foreach (var entry in hosted)
                entry.Evict(); // ① lifecycle first — the listener tears down before any observer runs
        }

        ReevaluateFrameProperties(frame, hosted); // ② + ③
    }

    /// <summary>Activation toggled (PD3: never evicts): recompute every property the frame contributes to.</summary>
    public void OnFrameActivationChanged(ValueFrame frame)
        => ReevaluateFrameProperties(frame, frame.HostedEntries);

    /// <summary>An entry re-emitted in place (§2.4 resource pulse): recompute just that property.</summary>
    public void OnFrameEntryChanged(ValueFrame frame, IValueEntry entry)
        => entry.Property.Reevaluate(this, entry);

    /// <summary>Whether any active frame carries a value-bearing entry for the property (the PD11 style half of <c>IsSet</c>).</summary>
    public bool HasActiveStyleContribution(int propertyId)
    {
        for (var i = _frameCount - 1; i >= 0; i--)
        {
            var frame = _frames[i];
            if (!frame.IsActive)
                continue;

            if (frame.HostedEntries is {} hosted)
            {
                foreach (var entry in hosted)
                {
                    if (entry.Property.Id == propertyId && entry.HasValue)
                        return true;
                }
            }

            for (var j = frame.EntryCount - 1; j >= 0; j--)
            {
                var entry = frame.GetEntry(j);
                if (entry.Property.Id == propertyId && entry.HasValue)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Appends one diagnostics row per frame entry for <paramref name="property"/> — declared and
    /// hosted, active frames or not — strongest-first in arbitration order (matrix M264; the
    /// DevTools/serialization surface). Cold path; values box through the untyped entry bridge.
    /// </summary>
    public void AppendStyleDiagnostics(UIProperty property, List<PropertyValueDiagnostic> results)
    {
        for (var i = _frameCount - 1; i >= 0; i--)
        {
            var frame = _frames[i];

            if (frame.HostedEntries is {} hosted)
            {
                for (var j = hosted.Count - 1; j >= 0; j--)
                {
                    if (hosted[j].Property.Id == property.Id)
                        results.Add(new PropertyValueDiagnostic(
                            BindingPriority.Style, property.GetEntryValueBoxed(hosted[j]), hosted[j].HasValue,
                            frame.SortKey, frame.IsActive));
                }
            }

            for (var j = frame.EntryCount - 1; j >= 0; j--)
            {
                var entry = frame.GetEntry(j);
                if (entry.Property.Id == property.Id)
                    results.Add(new PropertyValueDiagnostic(
                        BindingPriority.Style, property.GetEntryValueBoxed(entry), entry.HasValue,
                        frame.SortKey, frame.IsActive));
            }
        }
    }

    /// <summary>
    /// Appends the property's <b>active</b> style contributors in arbitration order — strongest
    /// frame first (largest sort key; equal keys later-added), within a frame hosted entries before
    /// declared ones, later indices first — the <c>StyleDiagnostics.Explain</c> surface (style
    /// matrix SD13: Explain renders active contributors only; armed-inactive frames belong to
    /// <c>MatchedRules</c>). Valueless (A8) entries are included — the renderer prints
    /// <c>(unset)</c>. Cold path.
    /// </summary>
    public void AppendActiveStyleContributors(UIProperty property, List<(ValueFrame Frame, IValueEntry Entry)> results)
    {
        for (var i = _frameCount - 1; i >= 0; i--)
        {
            var frame = _frames[i];
            if (!frame.IsActive)
                continue;

            if (frame.HostedEntries is {} hosted)
            {
                for (var j = hosted.Count - 1; j >= 0; j--)
                {
                    if (hosted[j].Property.Id == property.Id)
                        results.Add((frame, hosted[j]));
                }
            }

            for (var j = frame.EntryCount - 1; j >= 0; j--)
            {
                var entry = frame.GetEntry(j);
                if (entry.Property.Id == property.Id)
                    results.Add((frame, entry));
            }
        }
    }

    /// <summary>
    /// The within-lane provenance (PD25) of <paramref name="entry"/>'s effective value, keyed on the
    /// effective lane: Template refines to literal / binding / resource (via the template entry's
    /// <see cref="BindingEntryBase.SourceKind"/>); Style refines to setter / <c>When</c>-guarded rule
    /// (via the winning frame's conditional flag). Cold path (diagnostics).
    /// </summary>
    internal ValueSourceKind ComputeValueSourceKind(EffectiveValueBase entry) => entry.EffectivePriority switch
    {
        BindingPriority.Animation => ValueSourceKind.Animation,
        BindingPriority.LocalValue => ValueSourceKind.Local,
        BindingPriority.Template => entry.TemplateValueFromEntry
            ? entry.TemplateEntry?.SourceKind ?? ValueSourceKind.TemplateBinding
            : ValueSourceKind.TemplateLiteral,
        BindingPriority.Style => ResolveWinningStyleKind(entry.PropertyUntyped.Id),
        _ => ValueSourceKind.Default,
    };

    /// <summary>
    /// The kind of the winning active style contribution for <paramref name="propertyId"/> — the same
    /// strongest-first scan as <see cref="TryResolveStyleValue{T}"/>: <see cref="ValueSourceKind.StyleWhen"/>
    /// when the winning frame is a conditional rule, else <see cref="ValueSourceKind.StyleSetter"/>.
    /// </summary>
    private ValueSourceKind ResolveWinningStyleKind(int propertyId)
    {
        for (var i = _frameCount - 1; i >= 0; i--)
        {
            var frame = _frames[i];
            if (!frame.IsActive)
                continue;

            if (frame.HostedEntries is {} hosted)
            {
                for (var j = hosted.Count - 1; j >= 0; j--)
                {
                    if (hosted[j].Property.Id == propertyId && hosted[j].HasValue)
                        return frame.IsConditionalStyleRule ? ValueSourceKind.StyleWhen : ValueSourceKind.StyleSetter;
                }
            }

            for (var j = frame.EntryCount - 1; j >= 0; j--)
            {
                var entry = frame.GetEntry(j);
                if (entry.Property.Id == propertyId && entry.HasValue)
                    return frame.IsConditionalStyleRule ? ValueSourceKind.StyleWhen : ValueSourceKind.StyleSetter;
            }
        }

        return ValueSourceKind.StyleSetter; // effective lane is Style but no live frame found — defensive
    }

    /// <summary>
    /// Resolves the strongest active style contribution: frames strongest-first (largest sort key;
    /// equal keys later-added), within a frame hosted entries beat declared ones and later indices
    /// beat earlier (install/document order); valueless entries are skipped wholesale (A8 unset
    /// promotion, M41). Returns the <em>raw</em> value — coercion is the caller's.
    /// </summary>
    private bool TryResolveStyleValue<T>(StyledProperty<T> property, out T value, out object? contributor)
    {
        var propertyId = property.Id;
        for (var i = _frameCount - 1; i >= 0; i--)
        {
            var frame = _frames[i];
            if (!frame.IsActive)
                continue;

            if (frame.HostedEntries is {} hosted)
            {
                for (var j = hosted.Count - 1; j >= 0; j--)
                {
                    var entry = hosted[j];
                    if (entry.Property.Id == propertyId && entry.HasValue)
                    {
                        value = ((IValueEntry<T>)entry).GetValue();
                        contributor = entry;
                        return true;
                    }
                }
            }

            for (var j = frame.EntryCount - 1; j >= 0; j--)
            {
                var entry = frame.GetEntry(j);
                if (entry.Property.Id == propertyId && entry.HasValue)
                {
                    value = ((IValueEntry<T>)entry).GetValue();
                    contributor = entry;
                    return true;
                }
            }
        }

        value = default!;
        contributor = null;
        return false;
    }

    private void ReevaluateFrameProperties(ValueFrame frame, List<BindingEntryBase>? hosted)
    {
        // Allocation-free duplicate suppression (the style-flip hot path — matrix S173): instead of
        // a dedupe list, each property scans the entries before it. Frame entry counts are tiny
        // (style rules carry a handful of setters), so the O(n²) scan-back beats allocating.
        var count = frame.EntryCount;

        for (var i = 0; i < count; i++)
        {
            var property = frame.GetEntry(i).Property;
            if (!SeenInDeclaredEntries(frame, i, property))
                property.Reevaluate(this, changedEntry: null);
        }

        if (hosted is null)
            return;

        for (var i = 0; i < hosted.Count; i++)
        {
            var property = hosted[i].Property;
            if (SeenInDeclaredEntries(frame, count, property))
                continue;

            var duplicate = false;
            for (var j = 0; j < i; j++)
            {
                if (ReferenceEquals(hosted[j].Property, property))
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
                property.Reevaluate(this, changedEntry: null);
        }
    }

    private static bool SeenInDeclaredEntries(ValueFrame frame, int beforeIndex, UIProperty property)
    {
        for (var i = 0; i < beforeIndex; i++)
        {
            if (ReferenceEquals(frame.GetEntry(i).Property, property))
                return true;
        }

        return false;
    }

    // ───────────────────────────── teardown (ledger A13) ─────────────────────────────

    /// <summary>
    /// The teardown sweep: evicts every entry on the instance — free-standing and frame-hosted
    /// alike — firing <c>OnEvicted</c> per entry and <b>zero</b> change notifications (PD13);
    /// animation handles detach silently. Afterwards the store is inert: the effective table and
    /// frame list are empty, so reads return per-type defaults.
    /// </summary>
    public void TearDown()
    {
        for (var i = 0; i < _entryCount; i++)
            _entries[i].TearDownContributions();

        for (var i = 0; i < _frameCount; i++)
        {
            var frame = _frames[i];
            frame.Store = null;
            _frames[i] = null!;

            if (frame.TakeHostedEntries() is {} hosted)
            {
                foreach (var entry in hosted)
                    entry.Evict();
            }
        }

        _frameCount = 0;
        Array.Clear(_entries, 0, _entryCount);
        _entryCount = 0;
        _pendingIds?.Clear();
    }

    // ───────────────────────────── notification + defer ─────────────────────────────

    /// <summary>
    /// Dispatches a change immediately, or — inside a <c>DeferNotifications</c> scope — coalesces it
    /// per property (first old, last new, last priority; ledger A23 / PD15), flushing in
    /// first-change order at the outermost dispose.
    /// </summary>
    public void NotifyOrDefer<T>(EffectiveValue<T> entry, PropertyMetadata<T> metadata, T oldValue, T newValue, BindingPriority priority)
    {
        if (_deferDepth > 0)
        {
            if (!entry.HasPendingNotification)
            {
                entry.HasPendingNotification = true;
                entry.PendingOldValue = oldValue;
                (_pendingIds ??= []).Add(entry.Property.Id);
            }

            entry.PendingPriority = priority;
            return;
        }

        Owner.DispatchPropertyChanged(entry.Property, metadata.Changed, oldValue, newValue, priority);
    }

    /// <summary>Opens a defer scope; notifications coalesce until the outermost scope disposes.</summary>
    public IDisposable BeginDefer()
    {
        _deferDepth++;
        return new DeferScope(this);
    }

    private void EndDefer()
    {
        if (--_deferDepth > 0 || _pendingIds is not { Count: > 0 } pending)
            return;

        // Depth is already zero: anything a flush handler writes dispatches synchronously and
        // immediately (M251) — the scope does not capture writes made after its dispose began.
        for (var i = 0; i < pending.Count; i++)
            TryGetEntry(pending[i])?.FlushPending(this);

        pending.Clear();
    }

    private sealed class DeferScope(ValueStore store) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            store.EndDefer();
        }
    }

    // ───────────────────────────── observers ─────────────────────────────

    /// <summary>The channel an observer subscription delivers on.</summary>
    public enum ObserverChannel
    {
        /// <summary>Typed ordinary changes (<c>IValueObserver&lt;T&gt;.OnPropertyChanged</c>).</summary>
        Typed,

        /// <summary>Untyped ordinary changes (ledger A10).</summary>
        Untyped,

        /// <summary>Winning-base changes (<c>IValueObserver&lt;T&gt;.OnBaseValueChanged</c>, ledger A20).</summary>
        Base
    }

    /// <summary>The observer arrays for <paramref name="propertyId"/>, or <see langword="null"/>.</summary>
    public PropertyObservers? GetObservers(int propertyId)
    {
        var index = Array.BinarySearch(_observerIds, 0, _observerCount, propertyId);
        return index >= 0 ? _observers[index] : null;
    }

    /// <summary>
    /// Subscribes an observer on a channel. Arrays are copy-on-write: an in-flight dispatch keeps
    /// delivering to its snapshot (M28/M29). Observation creates no effective-value entry (the A22
    /// no-store-entry guarantee rides on this).
    /// </summary>
    public IDisposable AddObserver(int propertyId, object observer, ObserverChannel channel)
    {
        var index = Array.BinarySearch(_observerIds, 0, _observerCount, propertyId);
        PropertyObservers observers;
        if (index >= 0)
        {
            observers = _observers[index];
        }
        else
        {
            observers = new PropertyObservers();
            index = ~index;

            if (_observerCount == _observerIds.Length)
            {
                var capacity = Math.Max(4, _observerIds.Length * 2);
                Array.Resize(ref _observerIds, capacity);
                Array.Resize(ref _observers, capacity);
            }

            if (index < _observerCount)
            {
                Array.Copy(_observerIds, index, _observerIds, index + 1, _observerCount - index);
                Array.Copy(_observers, index, _observers, index + 1, _observerCount - index);
            }

            _observerIds[index] = propertyId;
            _observers[index] = observers;
            _observerCount++;
        }

        switch (channel)
        {
            case ObserverChannel.Typed:
                observers.Typed = Append(observers.Typed, observer);
                break;
            case ObserverChannel.Untyped:
                observers.Untyped = Append(observers.Untyped, (IUntypedValueObserver)observer);
                break;
            default:
                observers.Base = Append(observers.Base, observer);
                break;
        }

        return new ObserverSubscription(this, propertyId, observer, channel);
    }

    private void RemoveObserver(int propertyId, object observer, ObserverChannel channel)
    {
        if (GetObservers(propertyId) is not {} observers)
            return;

        switch (channel)
        {
            case ObserverChannel.Typed:
                observers.Typed = RemoveFirst(observers.Typed, observer);
                break;
            case ObserverChannel.Untyped:
                observers.Untyped = RemoveFirst(observers.Untyped, observer);
                break;
            default:
                observers.Base = RemoveFirst(observers.Base, observer);
                break;
        }
    }

    private static TArray[] Append<TArray>(TArray[] array, TArray item) where TArray : class
    {
        var grown = new TArray[array.Length + 1];
        Array.Copy(array, grown, array.Length);
        grown[^1] = item;
        return grown;
    }

    [SuppressMessage("ReSharper", "CoVariantArrayConversion")]
    private static TArray[] RemoveFirst<TArray>(TArray[] array, object observer) where TArray : class
    {
        var index = Array.IndexOf(array, observer);
        if (index < 0)
            return array;

        if (array.Length == 1)
            return [];

        var shrunk = new TArray[array.Length - 1];
        Array.Copy(array, shrunk, index);
        Array.Copy(array, index + 1, shrunk, index, array.Length - index - 1);
        return shrunk;
    }

    /// <summary>The copy-on-write observer arrays for one property id.</summary>
    internal sealed class PropertyObservers
    {
        /// <summary>Typed observers (<c>IValueObserver&lt;T&gt;</c>; the dispatch site knows <c>T</c>).</summary>
        public object[] Typed = [];

        /// <summary>Untyped observers (ledger A10).</summary>
        public IUntypedValueObserver[] Untyped = [];

        /// <summary>Winning-base observers (ledger A20; <c>IValueObserver&lt;T&gt;</c> instances).</summary>
        public object[] Base = [];
    }

    private sealed class ObserverSubscription(ValueStore store, int propertyId, object observer, ObserverChannel channel) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            store.RemoveObserver(propertyId, observer, channel);
        }
    }
}
