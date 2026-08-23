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
/// non-element host attaches immediately.</item>
/// <item><b>Direct</b> (<see cref="AttachAllTo"/> — a trigger's <c>Actions</c>): the OWNER is the lifecycle
/// authority (the trigger's own collection already deferred to the tree), so items attach/detach exactly
/// when told.</item>
/// </list>
/// </summary>
public abstract class AttachedObjectCollection<T> : Collection<T> where T : UIObject, IAttachedObject
{
    private object? _ownerHost;      // the host this collection is associated with (may be tree-detached)
    private object? _attachHost;     // non-null while items are live (attached)
    private UIElement? _tracked;     // the element whose tree lifecycle is followed (hosted mode)

    /// <summary>The host this collection is associated with (null while unhosted).</summary>
    public object? Host => _ownerHost;

    /// <summary>Associates the collection with <paramref name="host"/> and follows its tree lifecycle.</summary>
    internal void HostTo(object host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (ReferenceEquals(_ownerHost, host))
            return;

        Unhost();
        _ownerHost = host;

        if (host is UIElement element)
        {
            _tracked = element;
            element.AttachedToTree += OnHostTreeAttached;
            element.DetachedFromTree += OnHostTreeDetached;
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
            _tracked = null;
        }

        DetachAll();
        _ownerHost = null;
    }

    private void OnHostTreeAttached(object? sender, TreeAttachmentEventArgs e) => AttachAllTo(_ownerHost!);

    private void OnHostTreeDetached(object? sender, TreeAttachmentEventArgs e) => DetachAll();

    /// <summary>Goes live: attaches every item to <paramref name="host"/> (later adds attach immediately).</summary>
    internal void AttachAllTo(object host)
    {
        _attachHost = host;
        for (var i = 0; i < Count; i++)
            this[i].Attach(host);
    }

    /// <summary>Goes dark: detaches every item (later adds stay detached until the next attach).</summary>
    internal void DetachAll()
    {
        _attachHost = null;
        for (var i = 0; i < Count; i++)
            this[i].Detach();
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
        this[index].Detach();
        base.RemoveItem(index);
    }

    /// <inheritdoc/>
    protected override void SetItem(int index, T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        this[index].Detach();
        base.SetItem(index, item);
        if (_attachHost is { } host)
            item.Attach(host);
    }

    /// <inheritdoc/>
    protected override void ClearItems()
    {
        for (var i = 0; i < Count; i++)
            this[i].Detach();
        base.ClearItems();
    }
}

/// <summary>The behaviors attached to a host (<c>Interaction.Behaviors</c>).</summary>
public sealed class BehaviorCollection : AttachedObjectCollection<Behavior>;

/// <summary>The triggers attached to a host (<c>Interaction.Triggers</c>).</summary>
public sealed class TriggerCollection : AttachedObjectCollection<TriggerBase>;

/// <summary>A trigger's ordered actions (<see cref="TriggerBase.Actions"/> — direct lifecycle mode).</summary>
public sealed class TriggerActionCollection : AttachedObjectCollection<TriggerAction>;
