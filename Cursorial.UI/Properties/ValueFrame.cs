// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// A removable group of property contributions in the single <see cref="BindingPriority.Style"/>
/// slot (design doc §2.4) — the styling/template contribution unit. Fork B subclasses it as the
/// thin per-element activation shim over a shared immutable setter list; the store sorts installed
/// frames by <see cref="SortKey"/> (larger = stronger; equal keys: later-added wins) and arbitrates.
/// It never evaluates selectors.
/// </summary>
/// <remarks>
/// <para>
/// <b>Retraction is store-owned</b> (invariant 4): <see cref="SetActive"/> and
/// <c>UIObject.RemoveFrame</c> withdraw the frame's contributions and the store <em>promotes the
/// next source</em> — nothing ever sets an old value back. Deactivation never evicts hosted binding
/// entries (matrix PD3); removal always does, firing <see cref="IValueEvictionListener.OnEvicted"/>
/// per hosted entry before the promotion notifications (PD2). Cookie-based batch retraction
/// (Fork B's <c>StyleValueCookie</c>, <c>TemplateInstance.Detach()</c>) reduces to
/// <c>RemoveFrame</c> at the engine level — the frame reference is the engine-level cookie.
/// </para>
/// <para>
/// A frame can be installed on at most one object at a time. Entries surfaced through
/// <see cref="GetEntry"/> are expected to be structurally stable after install: value changes are
/// re-emitted in place via <see cref="OnEntryChanged"/> (resource pulses — never remove/re-add);
/// adding or removing setters means a new frame.
/// </para>
/// </remarks>
public abstract class ValueFrame
{
    private List<BindingEntryBase>? _hostedEntries;

    /// <summary>Creates a frame at <paramref name="sortKey"/>, optionally starting inactive.</summary>
    protected ValueFrame(StyleSortKey sortKey, bool isActive = true)
    {
        SortKey = sortKey;
        IsActive = isActive;
    }

    /// <summary>The within-slot ordering key (opaque to the store; larger sorts stronger).</summary>
    public StyleSortKey SortKey { get; }

    /// <summary>Whether the frame's contributions currently participate in arbitration.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The number of declared entries (the subclass's setter list).</summary>
    public abstract int EntryCount { get; }

    /// <summary>The declared entry at <paramref name="index"/>.</summary>
    public abstract IValueEntry GetEntry(int index);

    /// <summary>The store this frame is installed on, or <see langword="null"/>.</summary>
    internal ValueStore? Store { get; set; }

    /// <summary>
    /// Whether this frame is a <em>conditional</em> style rule — a <c>When</c>-guarded rule (the
    /// data-condition "trigger" equivalent) rather than a plain setter (PD25). Drives the
    /// <see cref="ValueSourceKind.StyleWhen"/> vs <see cref="ValueSourceKind.StyleSetter"/> provenance
    /// reported by <c>GetValueSource</c>; default <see langword="false"/> (a plain frame is a setter).
    /// </summary>
    internal virtual bool IsConditionalStyleRule => false;

    /// <summary>The frame-hosted binding entries (ledger A5), or <see langword="null"/> when none.</summary>
    internal List<BindingEntryBase>? HostedEntries => _hostedEntries;

    /// <summary>
    /// Toggles participation. The store recomputes every property the frame contributes to and
    /// promotes/reclaims with one notification per changed property; hosted entries are never
    /// evicted by deactivation (matrix PD3) and re-apply their held values on reactivation.
    /// </summary>
    protected void SetActive(bool active)
    {
        if (active == IsActive)
            return;

        IsActive = active;
        Store?.OnFrameActivationChanged(this);
    }

    /// <summary>
    /// Re-emits <paramref name="entry"/>'s value in place (a resource pulse produced a new value —
    /// design doc §2.4: never remove/re-add). The store re-evaluates the entry's property; an
    /// unchanged or masked value produces no notification.
    /// </summary>
    protected void OnEntryChanged(IValueEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (IsActive)
            Store?.OnFrameEntryChanged(this, entry);
    }

    /// <summary>
    /// Called by the store right after the frame is installed (<see cref="Store"/> set; before the
    /// initial reevaluation). The B10 hook: a subclass with resource-backed entries opens its
    /// subscriptions and pushes the first resolved values here. No-op by default.
    /// </summary>
    internal virtual void OnInstalled()
    {
    }

    /// <summary>
    /// Called by the store right before the frame is removed (while <see cref="Store"/> is still set,
    /// before hosted-entry eviction). The B10 teardown hook: a subclass closes its resource
    /// subscriptions here. No-op by default.
    /// </summary>
    internal virtual void OnRemoving()
    {
    }

    /// <summary>Hosts <paramref name="entry"/> in this frame (ledger A5; install order is arbitration order — later wins).</summary>
    internal void AddHostedEntry(BindingEntryBase entry) => (_hostedEntries ??= []).Add(entry);

    /// <summary>Removes a self-disposed hosted entry (no eviction — matrix PD12).</summary>
    internal void RemoveHostedEntry(BindingEntryBase entry) => _hostedEntries?.Remove(entry);

    /// <summary>Detaches and returns the hosted-entry list (frame removal / teardown sweep).</summary>
    internal List<BindingEntryBase>? TakeHostedEntries()
    {
        var hosted = _hostedEntries;
        _hostedEntries = null;
        return hosted;
    }
}
