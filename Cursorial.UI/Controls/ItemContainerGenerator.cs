using System.Collections.Specialized;

// ReSharper disable CheckNamespace
namespace Cursorial.UI.Controls;

/// <summary>
/// Maps an <see cref="ItemsControl"/>'s items to their realized containers (design doc §12.6). One per control,
/// control-lifetime. <b>Eager (v1):</b> every item is realized; the unrealize retraction sequence (ClearContainer
/// → visual detach → logical remove → DataContext clear) and the range-based <see cref="ContainersChanged"/>
/// event are authored as the seam a future recycling/
/// virtualizing host re-enters at. Containers are <b>logical children of the <see cref="ItemsControl"/></b> (so
/// DataContext/resource/style inheritance flows from the control) and <b>visual children of the panel</b> (the
/// <see cref="ItemsPresenter"/> adopts them via <c>AddVisualChildOnly</c> — punch 43).
/// </summary>
/// <remarks>
/// Each container is stamped with its source item via the internal
/// <see cref="ItemForItemContainerProperty"/> attached property (WPF parity) — the single source of truth for
/// "which item does this container represent". It powers <see cref="ItemFromContainer"/> (O(1), survives
/// reordering) and the unrealize own-container test: for an <b>own-container</b> (the item itself was a
/// <see cref="UIElement"/>) the stamp equals the container, so <c>container == item</c> identifies it — a
/// distinction the container's <see cref="UIElement.DataContext"/> cannot carry, since the generator never sets
/// DataContext on an own-container.
/// </remarks>
public sealed class ItemContainerGenerator
{
    /// <summary>Stamps each container with the item it was realized for (internal — the item↔container back-link).</summary>
    internal static readonly AttachedProperty<object?> ItemForItemContainerProperty =
        UIProperty.RegisterAttached<ItemContainerGenerator, UIElement, object?>("ItemForItemContainer");

    private readonly ItemsControl _owner;
    private readonly List<UIElement> _containers = []; // index-aligned with the current source (ordering; item is the stamp)
    private ItemsSourceView? _view;

    internal ItemContainerGenerator(ItemsControl owner) => _owner = owner;

    /// <summary>Raised (range-based) when containers are realized/unrealized/moved/reset — the host adopts/releases them.</summary>
    public event EventHandler<ContainersChangedEventArgs>? ContainersChanged;

    /// <summary>The realized container for <paramref name="index"/>, or null if out of range.</summary>
    public UIElement? ContainerFromIndex(int index) => index >= 0 && index < _containers.Count ? _containers[index] : null;

    /// <summary>The item index of <paramref name="container"/>, or −1.</summary>
    public int IndexFromContainer(UIElement container) => _containers.IndexOf(container);

    /// <summary>The source item <paramref name="container"/> represents (its <see cref="ItemForItemContainerProperty"/>
    /// stamp), or null if it is not one of this control's containers. For an own-container this is the container itself.</summary>
    public object? ItemFromContainer(UIElement container) => container.GetValue(ItemForItemContainerProperty);

    /// <summary>The number of realized containers (the host iterates <c>[0, ContainerCount)</c> via <see cref="ContainerFromIndex"/>).</summary>
    internal int ContainerCount => _containers.Count;

    /// <summary>Swaps the items source (control's ItemsSource/Items change): unrealizes the old, realizes the new, fires Reset.</summary>
    internal void SetSource(ItemsSourceView? view)
    {
        if (_view is not null)
        {
            _view.CollectionChanged -= OnSourceCollectionChanged;
            _view.Dispose(); // unhook the old view's INotifyCollectionChanged subscription (no leak across re-source)
        }

        UnrealizeAllCore(); // staged teardown (ClearContainer → Unrealized event → FinishUnrealize) before the swap

        _view = view;
        if (_view is not null)
        {
            _view.CollectionChanged += OnSourceCollectionChanged;
            for (var i = 0; i < _view.Count; i++)
                _containers.Add(RealizeCore(i));
        }

        ContainersChanged?.Invoke(this, new ContainersChangedEventArgs(ContainersChangedAction.Reset, 0, _containers.Count));
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                InsertRange(e.NewStartingIndex, e.NewItems?.Count ?? 0);
                break;
            case NotifyCollectionChangedAction.Remove:
                RemoveRange(e.OldStartingIndex, e.OldItems?.Count ?? 0);
                break;
            case NotifyCollectionChangedAction.Replace:
                RemoveRange(e.OldStartingIndex, e.OldItems?.Count ?? 0);
                InsertRange(e.NewStartingIndex, e.NewItems?.Count ?? 0);
                break;
            case NotifyCollectionChangedAction.Move:
                MoveRange(e.OldStartingIndex, e.NewStartingIndex, e.NewItems?.Count ?? 1);
                break;
            case NotifyCollectionChangedAction.Reset:
            default:
                ResetFromSource();
                break;
        }
    }

    private void InsertRange(int start, int count)
    {
        for (var i = 0; i < count; i++)
            _containers.Insert(start + i, RealizeCore(start + i));

        ContainersChanged?.Invoke(this, new ContainersChangedEventArgs(ContainersChangedAction.Realized, start, count));
    }

    private void RemoveRange(int start, int count)
    {
        if (count <= 0)
            return;

        // Phase 1 — ClearContainer on each (unhook while bindings are still live), THEN fire Unrealized so the
        // host removes them visually (the subtree detach is the store-retraction trigger), THEN finish each
        // container's logical detach + DataContext clear. ClearContainer must precede the detach (step order §12.6).
        for (var i = 0; i < count; i++)
        {
            var container = _containers[start + i];
            _owner.ClearContainerForItem(container, ItemFromContainer(container));
        }

        ContainersChanged?.Invoke(this, new ContainersChangedEventArgs(ContainersChangedAction.Unrealized, start, count));

        for (var i = 0; i < count; i++)
        {
            var container = _containers[start + i];
            FinishUnrealize(container, ItemFromContainer(container)); // stamp still set — read the item before clearing it
        }

        _containers.RemoveRange(start, count);
    }

    private void MoveRange(int oldIndex, int newIndex, int count)
    {
        if (count <= 0)
            return;

        // Lift the block out and re-insert it at the new index — the SAME container instances, reordered (no
        // realize/unrealize). The host (ItemsPresenter) brings its panel children into the same order on the event.
        var block = new UIElement[count];
        for (var i = 0; i < count; i++)
            block[i] = _containers[oldIndex + i];

        _containers.RemoveRange(oldIndex, count);
        _containers.InsertRange(newIndex, block);
        ContainersChanged?.Invoke(this, new ContainersChangedEventArgs(ContainersChangedAction.Moved, newIndex, count));
    }

    private void ResetFromSource()
    {
        UnrealizeAllCore();
        if (_view is not null)
            for (var i = 0; i < _view.Count; i++)
                _containers.Add(RealizeCore(i));

        ContainersChanged?.Invoke(this, new ContainersChangedEventArgs(ContainersChangedAction.Reset, 0, _containers.Count));
    }

    // Creates + logical-parents + stamps + prepares one container (no visual adoption — the host does that on the event).
    private UIElement RealizeCore(int index)
    {
        var item = _view![index];
        var container = _owner.CreateContainer(item, out var isOwnContainer);
        container.SetValue(ItemForItemContainerProperty, item); // the item↔container back-link (own-container ⇒ stamp == container)
        _owner.AddContainerLogical(container); // logical child of the ItemsControl ⇒ inheritance flows from the control
        _owner.PrepareContainerForItem(container, item, isOwnContainer);
        return container;
    }

    // The tail of the 4-step Unrealize (after ClearContainer + the host's visual detach): logical detach + clear the stamp/DataContext.
    private void FinishUnrealize(UIElement container, object? item)
    {
        _owner.RemoveContainerLogical(container);
        // The generator only set DataContext on a GENERATED container (== item); an own-container's DataContext is
        // the user's — leave it (symmetric with PrepareContainerForItem / ClearContainerForItem).
        if (!ReferenceEquals(container, item))
            container.ClearValue(UIElement.DataContextProperty);
        container.ClearValue(ItemForItemContainerProperty);
    }

    /// <summary>Releases the source subscription on owner teardown — unhooks the view's
    /// <see cref="INotifyCollectionChanged"/> handler so a live external source no longer pins the control
    /// (the containers are torn down by the normal logical-child sweep). Idempotent.</summary>
    internal void ReleaseSource()
    {
        if (_view is null)
            return;

        _view.CollectionChanged -= OnSourceCollectionChanged;
        _view.Dispose();
        _view = null;
    }

    private void UnrealizeAllCore()
    {
        // Reuse the staged RemoveRange so the full-reset teardown honors CD-P9-3 ordering: ClearContainer (bindings
        // live) → Unrealized event (the host detaches them visually — the store-retraction trigger) → FinishUnrealize
        // (logical detach + stamp/DataContext clear). The caller fires Reset afterward to adopt the new generation.
        if (_containers.Count > 0)
            RemoveRange(0, _containers.Count);
    }
}
