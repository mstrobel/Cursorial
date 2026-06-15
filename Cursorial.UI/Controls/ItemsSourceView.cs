using System.Collections;
using System.Collections.Specialized;

// ReSharper disable CheckNamespace
namespace Cursorial.UI.Controls;

/// <summary>
/// An indexable, change-notifying view over an <see cref="ItemsControl"/>'s items (design doc §12.6) — the
/// single internal driver behind both the direct <see cref="ItemsControl.Items"/> lane and a bound
/// <see cref="ItemsControl.ItemsSource"/>, so the generator has exactly one realize/unrealize source. When the
/// underlying source implements <see cref="INotifyCollectionChanged"/> its events forward through
/// <see cref="CollectionChanged"/> (normalized to Add/Remove/Move/Replace/Reset with indices); a non-notifying
/// source is a static snapshot.
/// </summary>
internal sealed class ItemsSourceView : IDisposable
{
    private readonly IList _list;
    private readonly INotifyCollectionChanged? _incc;

    internal ItemsSourceView(IEnumerable source)
    {
        // IList is used directly (so a bound ObservableCollection<T> indexes live); a non-list enumerable is
        // snapshotted (it can't change-notify meaningfully without IList indices anyway).
        _list = source as IList ?? Snapshot(source);

        if (source is INotifyCollectionChanged incc)
        {
            _incc = incc;
            _incc.CollectionChanged += OnSourceCollectionChanged;
        }
    }

    /// <summary>Forwards the source's collection changes (or the direct-Items lane's).</summary>
    internal event NotifyCollectionChangedEventHandler? CollectionChanged;

    internal int Count => _list.Count;

    internal object? this[int index] => _list[index];

    internal int IndexOf(object? item) => _list.IndexOf(item);

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => CollectionChanged?.Invoke(this, e);

    private static IList Snapshot(IEnumerable source)
    {
        var snapshot = new List<object?>();
        foreach (var item in source)
            snapshot.Add(item);
        return snapshot;
    }

    public void Dispose()
    {
        if (_incc is not null)
            _incc.CollectionChanged -= OnSourceCollectionChanged;
    }
}
