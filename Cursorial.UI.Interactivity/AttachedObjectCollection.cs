using System.Collections.ObjectModel;

using Cursorial.UI;

namespace Cursorial.UI.Interactivity;

/// <summary>
/// The lifecycle-aware collection base (design doc §2/§3): items attach when the collection goes live and
/// detach when it goes dark; adding/removing while live attaches/detaches the item immediately. Two entry
/// modes share the machinery:
/// <list type="bullet">
/// <item><b>Hosted</b> (<see cref="HostTo"/> — <c>Interaction.Behaviors</c>/<c>Triggers</c>): the collection
/// follows a <see cref="UIElement"/> host's TREE lifecycle — items attach when the host enters an attached
/// tree (<see cref="UIElement.AttachedToTree"/>), detach when it leaves, and re-attach on re-entry. A
/// non-element host attaches immediately. A collection has EXACTLY ONE host (Blend parity) — hosting an
/// already-hosted collection elsewhere throws.</item>
/// <item><b>Direct</b> (<see cref="AttachAllTo"/> — a trigger's <c>Actions</c>): the OWNER is the lifecycle
/// authority (the trigger's own collection already deferred to the tree), so items attach/detach exactly
/// when told.</item>
/// </list>
/// A hosted collection also registers as its element's <see cref="ITearDownParticipant"/>: when the element's
/// life ends, the items detach (releasing handlers/watches) and every item's INSTALLED BINDINGS are swept —
/// the InputBindings teardown leg, which no child sweep reaches (audit finding: an action's
/// Source-anchored binding otherwise pins the graph via the viewmodel's INPC list forever).
/// </summary>
public abstract class AttachedObjectCollection<T> : Collection<T>, ITearDownParticipant
    where T : UIObject, IAttachedObject
{
    private object? _ownerHost;      // the host this collection is associated with (may be tree-detached)
    private object? _attachHost;     // non-null while items are live (attached)
    private UIElement? _tracked;     // the element whose tree lifecycle is followed (hosted mode)

    /// <summary>The host this collection is associated with (null while unhosted).</summary>
    public object? Host => _ownerHost;

    /// <summary>Associates the collection with <paramref name="host"/> and follows its tree lifecycle.
    /// A collection has exactly one host — re-hosting an already-hosted collection throws (Blend parity;
    /// the audit's silent-steal scenario: the old element's property slot would keep a dead collection).</summary>
    internal void HostTo(object host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (ReferenceEquals(_ownerHost, host))
            return;

        if (_ownerHost is not null)
            throw new InvalidOperationException(
                $"This {GetType().Name} is already associated with another host. A collection has exactly one " +
                "host — clear the old association (SetBehaviors/SetTriggers(oldHost, null)) before re-hosting, " +
                "or use a fresh collection.");

        _ownerHost = host;

        if (host is UIElement element)
        {
            _tracked = element;
            element.AttachedToTree += OnHostTreeAttached;
            element.DetachedFromTree += OnHostTreeDetached;
            element.RegisterTearDownParticipant(this);
            if (element.IsAttachedToTree)
                AttachAllTo(element);
        }
        else
        {
            AttachAllTo(host); // a non-element host has no tree lifecycle — live immediately (§3)
        }
    }

    /// <summary>Detaches every item, stops following the host, and clears the association.</summary>
    internal void Unhost()
    {
        if (_tracked is { } element)
        {
            element.AttachedToTree -= OnHostTreeAttached;
            element.DetachedFromTree -= OnHostTreeDetached;
            element.UnregisterTearDownParticipant(this);
            _tracked = null;
        }

        DetachAll();
        _ownerHost = null;
    }

    /// <summary>
    /// The host element's end-of-life (<see cref="UIElement.TearDown"/>): detach every item (releasing
    /// event handlers/watches — the tree may be torn down without a prior detach walk) and sweep each
    /// item's installed bindings (incl. a trigger's actions), the InputBindings teardown leg — else a
    /// Source/ElementName-anchored binding on an action pins the whole graph via the source's INPC list.
    /// </summary>
    void ITearDownParticipant.OnTearDown(UIElement host)
    {
        DetachAll();
        for (var i = 0; i < Count; i++)
            TearDownItem(this[i]);
    }

    private static void TearDownItem(UIObject item)
    {
        Cursorial.UI.Data.BindingOperations.TearDown(item);
        if (item is TriggerBase { ActionsOrNull: { } actions })
            for (var i = 0; i < actions.Count; i++)
                Cursorial.UI.Data.BindingOperations.TearDown(actions[i]);
    }

    private void OnHostTreeAttached(object? sender, TreeAttachmentEventArgs e) => AttachAllTo(_ownerHost!);

    private void OnHostTreeDetached(object? sender, TreeAttachmentEventArgs e) => DetachAll();

    /// <summary>Goes live: attaches every item to <paramref name="host"/> (later adds attach immediately).
    /// Walks a SNAPSHOT (the <c>Fire</c> precedent) — an <c>OnAttached</c> that mutates the collection must
    /// not skip or double-visit siblings.</summary>
    internal void AttachAllTo(object host)
    {
        _attachHost = host;
        foreach (var item in Snapshot())
            item.Attach(host);
    }

    /// <summary>Goes dark: detaches every item (later adds stay detached until the next attach). Walks a
    /// SNAPSHOT — an <c>OnDetaching</c> that mutates the collection (the one-shot-behavior pattern) must not
    /// leave a shifted sibling silently attached (the audit's skipped-item subscription leak).</summary>
    internal void DetachAll()
    {
        _attachHost = null;
        foreach (var item in Snapshot())
            item.Detach();
    }

    private T[] Snapshot()
    {
        var snapshot = new T[Count];
        CopyTo(snapshot, 0);
        return snapshot;
    }

    /// <inheritdoc/>
    protected override void InsertItem(int index, T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        base.InsertItem(index, item);
        if (_attachHost is { } host)
            item.Attach(host);
    }

    /// <inheritdoc/>
    protected override void RemoveItem(int index)
    {
        var removed = this[index];
        base.RemoveItem(index); // remove FIRST: a re-entrant Detach (self-removal) must not see a stale index
        removed.Detach();
    }

    /// <inheritdoc/>
    protected override void SetItem(int index, T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var replaced = this[index];
        base.SetItem(index, item);
        replaced.Detach();
        if (_attachHost is { } host)
            item.Attach(host);
    }

    /// <inheritdoc/>
    protected override void ClearItems()
    {
        var snapshot = Snapshot();
        base.ClearItems();
        foreach (var item in snapshot)
            item.Detach();
    }
}

/// <summary>The behaviors attached to a host (<c>Interaction.Behaviors</c>).</summary>
public sealed class BehaviorCollection : AttachedObjectCollection<Behavior>;

/// <summary>The triggers attached to a host (<c>Interaction.Triggers</c>).</summary>
public sealed class TriggerCollection : AttachedObjectCollection<TriggerBase>;

/// <summary>A trigger's ordered actions (<see cref="TriggerBase.Actions"/> — direct lifecycle mode).</summary>
public sealed class TriggerActionCollection : AttachedObjectCollection<TriggerAction>;
