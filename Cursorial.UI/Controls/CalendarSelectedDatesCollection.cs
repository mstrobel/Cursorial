using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Cursorial.UI.Controls;

/// <summary>
/// The set of dates selected in a <see cref="Calendar"/> when its <see cref="Calendar.SelectionMode"/> permits more
/// than one (the WPF <c>SelectedDatesCollection</c> analog). Every mutation is validated against the calendar's
/// selectability (its <see cref="Calendar.DisplayDateStart"/>/<see cref="Calendar.DisplayDateEnd"/> bounds and
/// <see cref="Calendar.BlackoutDates"/>) and its <see cref="Calendar.SelectionMode"/>; duplicate dates are ignored.
/// Changes drive the calendar's cell restamp and its <see cref="Calendar.SelectedDatesChanged"/> event and keep the
/// primary <see cref="Calendar.SelectedDate"/> in sync. Bulk edits (<see cref="AddRange"/>, the internal reconcile
/// paths) coalesce into a single <see cref="NotifyCollectionChangedAction.Reset"/> and a single owner notification.
/// </summary>
public sealed class CalendarSelectedDatesCollection : ObservableCollection<DateOnly>
{
    private readonly Calendar _owner;
    private bool _batching; // a bulk edit is in flight: swallow per-item notifications; one Reset + one diff fires at the end

    internal CalendarSelectedDatesCollection(Calendar owner) => _owner = owner;

    /// <summary>
    /// Adds every selectable date in the inclusive span [<paramref name="start"/>, <paramref name="end"/>]
    /// (order-agnostic) as a single batched change; blacked-out / out-of-range days within the span are skipped.
    /// Throws <see cref="InvalidOperationException"/> when <see cref="Calendar.SelectionMode"/> is
    /// <see cref="CalendarSelectionMode.None"/> or <see cref="CalendarSelectionMode.SingleDate"/> (use
    /// <see cref="Calendar.SelectedDate"/> there).
    /// </summary>
    public void AddRange(DateOnly start, DateOnly end)
    {
        _owner.VerifyMultiSelect();
        var lo = start <= end ? start : end;
        var hi = start <= end ? end : start;
        Edit(() =>
        {
            for (var n = lo.DayNumber; n <= hi.DayNumber; n++)
            {
                var d = DateOnly.FromDayNumber(n);
                if (_owner.IsSelectableDate(d))
                    Add(d);
            }
        });
    }

    /// <summary>Replace the whole selection with <paramref name="dates"/> as one batched change (owner reconcile paths).</summary>
    internal void ReplaceAll(IEnumerable<DateOnly> dates)
        => Edit(() =>
        {
            Clear();
            foreach (var d in dates)
                Add(d);
        });

    /// <summary>Run <paramref name="mutate"/> as one coalesced change: per-item notifications are suppressed, then a
    /// single <see cref="NotifyCollectionChangedAction.Reset"/> and a single owner diff (net added/removed) fire.</summary>
    internal void Edit(Action mutate)
    {
        if (_batching) { mutate(); return; } // already inside a batch — join it

        var before = new List<DateOnly>(this);
        _batching = true;
        try { mutate(); }
        finally { _batching = false; }

        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

        var beforeSet = new HashSet<DateOnly>(before);
        var afterSet = new HashSet<DateOnly>(this);
        var added = new List<DateOnly>();
        foreach (var d in this)
            if (!beforeSet.Contains(d))
                added.Add(d);
        var removed = new List<DateOnly>();
        foreach (var d in before)
            if (!afterSet.Contains(d))
                removed.Add(d);

        if (added.Count > 0 || removed.Count > 0)
            _owner.OnSelectedDatesCollectionChanged(added, removed);
    }

    /// <inheritdoc/>
    protected override void InsertItem(int index, DateOnly item)
    {
        _owner.ValidateAddSelectedDate(Count, item); // None ⇒ throw; SingleDate & already-populated ⇒ throw; out-of-range/blackout ⇒ throw
        if (Contains(item))
            return; // dedupe silently (WPF ignores duplicate selections)
        base.InsertItem(index, item);
        if (!_batching)
            _owner.OnSelectedDatesCollectionChanged([item], []);
    }

    /// <inheritdoc/>
    protected override void RemoveItem(int index)
    {
        var removed = this[index];
        base.RemoveItem(index);
        if (!_batching)
            _owner.OnSelectedDatesCollectionChanged([], [removed]);
    }

    /// <inheritdoc/>
    protected override void SetItem(int index, DateOnly item)
    {
        _owner.ValidateAddSelectedDate(Count - 1, item);
        var old = this[index];
        if (old == item)
            return;
        if (Contains(item)) { RemoveItem(index); return; } // setting to an existing date collapses to a remove
        base.SetItem(index, item);
        if (!_batching)
            _owner.OnSelectedDatesCollectionChanged([item], [old]);
    }

    /// <inheritdoc/>
    protected override void ClearItems()
    {
        if (Count == 0)
            return;
        var removed = new List<DateOnly>(this);
        base.ClearItems();
        if (!_batching)
            _owner.OnSelectedDatesCollectionChanged([], removed);
    }

    /// <inheritdoc/>
    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_batching)
            return; // coalesced into the single Reset raised at the end of Edit()
        base.OnCollectionChanged(e);
    }
}
