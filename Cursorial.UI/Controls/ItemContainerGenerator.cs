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
public sealed class ItemContainerGenerator
{
    private readonly ItemsControl _owner;
    private readonly List<UIElement> _containers = []; // index-aligned with the current source
    private ItemsSourceView? _view;

    internal ItemContainerGenerator(ItemsControl owner) => _owner = owner;

    /// <summary>Raised (range-based) when containers are realized/unrealized/moved/reset — the host adopts/releases them.</summary>
    public event EventHandler<ContainersChangedEventArgs>? ContainersChanged;

    /// <summary>The realized container for <paramref name="index"/>, or null if out of range.</summary>
    public UIElement? ContainerFromIndex(int index) => index >= 0 && index < _containers.Count ? _containers[index] : null;

    /// <summary>The item index of <paramref name="container"/>, or −1.</summary>
    public int IndexFromContainer(UIElement container) => _containers.IndexOf(container);

    /// <summary>The current realized containers in index order (the host's adopt list).</summary>
    internal IReadOnlyList<UIElement> Containers => _containers;

    /// <summary>Swaps the items source (control's ItemsSource/Items change): unrealizes the old, realizes the new, fires Reset.</summary>
    internal void SetSource(ItemsSourceView? view)
    {
        if (_view is not null)
            _view.CollectionChanged -= OnSourceCollectionChanged;

        UnrealizeAllCore();

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
                MoveOne(e.OldStartingIndex, e.NewStartingIndex);
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
        // Phase 1 — ClearContainer on each (unhook while bindings are still live), THEN fire Unrealized so the
        // host removes them visually (the subtree detach is the store-retraction trigger), THEN finish each
        // container's logical detach + DataContext clear. ClearContainer must precede the detach (step order §12.6).
        for (var i = 0; i < count; i++)
            _owner.ClearContainerForItem(_containers[start + i]);

        ContainersChanged?.Invoke(this, new ContainersChangedEventArgs(ContainersChangedAction.Unrealized, start, count));

        for (var i = 0; i < count; i++)
            FinishUnrealize(_containers[start + i]);

        _containers.RemoveRange(start, count);
    }

    private void MoveOne(int oldIndex, int newIndex)
    {
        var container = _containers[oldIndex];
        _containers.RemoveAt(oldIndex);
        _containers.Insert(newIndex, container);
        ContainersChanged?.Invoke(this, new ContainersChangedEventArgs(ContainersChangedAction.Moved, newIndex, 1));
    }

    private void ResetFromSource()
    {
        UnrealizeAllCore();
        if (_view is not null)
            for (var i = 0; i < _view.Count; i++)
                _containers.Add(RealizeCore(i));

        ContainersChanged?.Invoke(this, new ContainersChangedEventArgs(ContainersChangedAction.Reset, 0, _containers.Count));
    }

    // Creates + logical-parents + prepares one container (no visual adoption — the host does that on the event).
    private UIElement RealizeCore(int index)
    {
        var item = _view![index];
        var container = _owner.CreateContainer(item, out var isOwnContainer);
        _owner.AddContainerLogical(container); // logical child of the ItemsControl ⇒ inheritance flows from the control
        _owner.PrepareContainerForItem(container, item, isOwnContainer);
        return container;
    }

    // The tail of the 4-step Unrealize (after ClearContainer + the host's visual detach): logical detach + clear DataContext.
    private void FinishUnrealize(UIElement container)
    {
        _owner.RemoveContainerLogical(container);
        container.ClearValue(UIElement.DataContextProperty);
    }

    private void UnrealizeAllCore()
    {
        // Reset path: clear + finish each (the host clears its panel wholesale on the Reset event it gets next).
        for (var i = 0; i < _containers.Count; i++)
        {
            _owner.ClearContainerForItem(_containers[i]);
            FinishUnrealize(_containers[i]);
        }
        _containers.Clear();
    }
}
