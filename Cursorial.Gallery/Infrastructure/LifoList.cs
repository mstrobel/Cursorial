using System.Collections.Specialized;

namespace Cursorial.Gallery.Infrastructure;

public class ReversedList<T> : IList<T>, INotifyCollectionChanged
{
    public IList<T> Source { get; }

    public ReversedList(IList<T> source)
    {
        Source = source;
        
        if (Source is INotifyCollectionChanged notifySource)
        {
            notifySource.CollectionChanged += OnSourceCollectionChanged;
        }
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        NotifyCollectionChangedEventArgs reversedArgs;
        
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                reversedArgs = new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Add,
                    e.NewItems,
                    ReverseIndex(e.NewStartingIndex));
                break;
            
            case NotifyCollectionChangedAction.Remove:
                reversedArgs = new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Remove,
                    e.OldItems,
                    ReverseIndex(e.OldStartingIndex));
                break;
            
            case NotifyCollectionChangedAction.Replace:
                reversedArgs = new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Replace,
                    e.NewItems,
                    e.OldItems,
                    ReverseIndex(e.NewStartingIndex));
                break;
            
            case NotifyCollectionChangedAction.Move:
                reversedArgs = new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Move,
                    e.NewItems,
                    ReverseIndex(e.NewStartingIndex),
                    ReverseIndex(e.OldStartingIndex));
                break;
            
            case NotifyCollectionChangedAction.Reset:
                reversedArgs = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset);
                break;
            
            default:
                return;
        }
        
        CollectionChanged?.Invoke(this, reversedArgs);
    }

    private int ReverseIndex(int index) => Source.Count - 1 - index;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public T this[int index]
    {
        get => Source[ReverseIndex(index)];
        set => Source[ReverseIndex(index)] = value;
    }

    public int Count => Source.Count;
    public bool IsReadOnly => Source.IsReadOnly;

    public void Add(T item) => Insert(0, item);

    public void Clear() => Source.Clear();

    public bool Contains(T item) => Source.Contains(item);

    public void CopyTo(T[] array, int arrayIndex)
    {
        for (int i = 0; i < Count; i++)
        {
            array[arrayIndex + i] = this[i];
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = Source.Count - 1; i >= 0; i--)
        {
            yield return Source[i];
        }
    }

    public int IndexOf(T item)
    {
        int sourceIndex = Source.IndexOf(item);
        return sourceIndex == -1 ? -1 : ReverseIndex(sourceIndex);
    }

    public void Insert(int index, T item) => Source.Insert(ReverseIndex(index) + 1, item);

    public bool Remove(T item)
    {
        int index = IndexOf(item);
        if (index == -1) return false;
        RemoveAt(index);
        return true;
    }

    public void RemoveAt(int index) => Source.RemoveAt(ReverseIndex(index));

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}