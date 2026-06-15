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
        // A live IList source is used directly (a bound ObservableCollection<T> indexes live) and, if it also
        // change-notifies, its events forward. A non-IList enumerable is snapshotted and stays STATIC — we do not
        // subscribe even if it implements INotifyCollectionChanged, because its indexed change args can't be applied
        // against a snapshot whose indices it doesn't share (that would desync the generator).
        if (source is IList list)
        {
            _list = list;
            if (source is INotifyCollectionChanged incc)
            {
                _incc = incc;
                _incc.CollectionChanged += OnSourceCollectionChanged;
            }
        }
        else
        {
            _list = Snapshot(source);
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
