using System.Collections.ObjectModel;

namespace Cursorial.UI.Controls;

/// <summary>
/// A pure, index-based selection model (design doc §12.6 / ledger CD-P9-8..11) shared by ListBox (P9.3) and
/// TabControl (P9.5). It holds <b>no element references</b> and does <b>not</b> track the total item count: the
/// non-structural operations (<see cref="Select"/>/<see cref="Toggle"/>/<see cref="SelectRangeFromAnchor"/>) drive
/// selection, and the structural hooks (<see cref="ItemsInserted"/>/<see cref="ItemsRemoved"/>/<see cref="Reset"/>)
/// keep the indices aligned as the source mutates.
/// </summary>
/// <remarks>
/// <see cref="SelectedIndex"/> is the <b>lead</b> (the most-recent primary index; always a member of the set when
/// non-empty, <c>−1</c> when empty). <see cref="AnchorIndex"/> is the range anchor that
/// <see cref="SelectRangeFromAnchor"/> reads. On a removal the model relocates a dropped lead to the nearest
/// surviving <i>selected</i> index but never auto-selects a previously-unselected item — re-targeting onto the
/// nearest surviving <i>item</i> (which needs the item count) is the ListBox's policy (CD-P9-9).
/// </remarks>
public sealed class SelectionModel
{
    private readonly HashSet<int> _selected = [];
    private ReadOnlyCollection<int>? _sortedCache;
    private SelectionMode _mode = SelectionMode.Single;
    private int _lead = -1;
    private int _anchor = -1;

    /// <summary>Raised when the set of selected items changes (membership only — a pure shift raises nothing).</summary>
    public event EventHandler<SelectionChangedEventArgs>? SelectionChanged;

    /// <summary>How many items may be selected at once. Narrowing to <see cref="SelectionMode.Single"/> collapses a
    /// multi-selection to its lead.</summary>
    public SelectionMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
                return;

            _mode = value;
            if (value == SelectionMode.Single && _selected.Count > 1)
            {
                var keep = _lead >= 0 && _selected.Contains(_lead) ? _lead : Min(_selected);
                SetSelection([keep], keep, keep);
            }
        }
    }

    /// <summary>The lead (primary) selected index, or <c>−1</c> when nothing is selected. Setting it replaces the
    /// selection with that single index (or clears it when set to <c>−1</c>).</summary>
    public int SelectedIndex
    {
        get => _lead;
        set => Select(value);
    }

    /// <summary>The range anchor (settable). <see cref="Select"/>/<see cref="Toggle"/> set it; <see cref="SelectRangeFromAnchor"/> reads it.</summary>
    public int AnchorIndex
    {
        get => _anchor;
        set => _anchor = value;
    }

    /// <summary>The selected indices in ascending order (a read-only view — never the live backing array).</summary>
    public IReadOnlyList<int> SelectedIndexes
    {
        get
        {
            if (_sortedCache is null)
            {
                int[] sorted = [.. _selected];
                Array.Sort(sorted);
                _sortedCache = Array.AsReadOnly(sorted); // deny external mutation of internal state via a downcast
            }

            return _sortedCache;
        }
    }

    /// <summary>Whether <paramref name="index"/> is currently selected.</summary>
    public bool IsSelected(int index) => _selected.Contains(index);

    /// <summary>Replaces the selection with the single <paramref name="index"/> (a "plain click"); <c>−1</c> clears.</summary>
    public void Select(int index)
    {
        if (index < 0)
        {
            SetSelection([], -1, -1);
            return;
        }

        SetSelection([index], index, index);
    }

    /// <summary>Toggles <paramref name="index"/> in/out of the selection (Ctrl-click). In <see cref="SelectionMode.Single"/>
    /// this caps the result at one index.</summary>
    public void Toggle(int index)
    {
        if (index < 0)
            return;

        if (_mode == SelectionMode.Single)
        {
            if (_selected.Contains(index))
                SetSelection([], -1, index);   // deselect the lone selection
            else
                SetSelection([index], index, index); // replace whatever was selected
            return;
        }

        var next = new HashSet<int>(_selected);
        int lead;
        if (next.Remove(index))
            lead = next.Count == 0 ? -1 : (index == _lead ? Max(next) : _lead);
        else
        {
            next.Add(index);
            lead = index;
        }

        SetSelection(next, lead, index);
    }

    /// <summary>Selects the contiguous range from <see cref="AnchorIndex"/> to <paramref name="index"/> (Shift-click),
    /// replacing the current selection. In <see cref="SelectionMode.Single"/> — or with no anchor — it collapses to
    /// <see cref="Select"/>.</summary>
    public void SelectRangeFromAnchor(int index)
    {
        if (index < 0)
            return;

        if (_mode == SelectionMode.Single || _anchor < 0)
        {
            Select(index);
            return;
        }

        var lo = Math.Min(_anchor, index);
        var hi = Math.Max(_anchor, index);
        var range = new HashSet<int>();
        for (var i = lo; i <= hi; i++)
            range.Add(i);

        SetSelection(range, lead: index, anchor: _anchor); // the endpoint becomes the lead; the anchor is preserved
    }

    /// <summary>Index-fixup hook: <paramref name="count"/> items were inserted at <paramref name="index"/> — shift
    /// selected indices at or after it (a pure shift; no <see cref="SelectionChanged"/>).</summary>
    public void ItemsInserted(int index, int count)
    {
        if (index < 0 || count <= 0) // out-of-range is ignored (lenient, like Select(-1)); never leaks a bad index
            return;

        Reindex(x => x >= index ? x + count : x);
    }

    /// <summary>Index-fixup hook: <paramref name="count"/> items were removed at <paramref name="index"/> — drop
    /// selected indices in the removed range, shift those after it down, and relocate a dropped lead onto the nearest
    /// surviving selected index. Fires <see cref="SelectionChanged"/> only if a selected index was dropped.</summary>
    public void ItemsRemoved(int index, int count)
    {
        if (index < 0 || count <= 0) // out-of-range is ignored; valid input never produces a negative shift (x ≥ end ⇒ x−count ≥ index ≥ 0)
            return;

        var end = index + count;
        var survivors = new HashSet<int>();
        var dropped = new List<int>();
        foreach (var x in _selected)
        {
            if (x < index)
                survivors.Add(x);
            else if (x >= end)
                survivors.Add(x - count);
            else
                dropped.Add(x);
        }

        var newLead = ShiftOrRelocate(_lead, index, end, count, survivors);
        var newAnchor = _anchor < 0 ? -1
            : _anchor < index ? _anchor
            : _anchor >= end ? _anchor - count
            : newLead; // anchor fell in the removed range — re-anchor to the lead

        _selected.Clear();
        _selected.UnionWith(survivors);
        _sortedCache = null;
        _lead = newLead;
        _anchor = newAnchor;

        if (dropped.Count > 0)
        {
            dropped.Sort();
            Raise([], dropped);
        }
    }

    /// <summary>Index-fixup hook: a contiguous block of <paramref name="count"/> items moved from
    /// <paramref name="oldIndex"/> to <paramref name="newIndex"/> (post-removal index, matching
    /// <c>ObservableCollection.Move</c>) — remaps the selected indices so the same items stay selected (a pure
    /// permutation; no <see cref="SelectionChanged"/>).</summary>
    public void ItemsMoved(int oldIndex, int newIndex, int count)
    {
        if (oldIndex < 0 || newIndex < 0 || count <= 0 || oldIndex == newIndex)
            return;

        Reindex(x => MapMove(x, oldIndex, newIndex, count));
    }

    // The post-move position of index x under a block move [old, old+count) → newIndex (post-removal space).
    private static int MapMove(int x, int oldIndex, int newIndex, int count)
    {
        var hi = oldIndex + count;
        if (x >= oldIndex && x < hi)
            return newIndex + (x - oldIndex); // a moved element

        var shifted = x >= hi ? x - count : x;          // close the gap the block left
        return shifted >= newIndex ? shifted + count : shifted; // open the gap the block lands in
    }

    /// <summary>Index-fixup hook: the source was reset (cleared / wholesale-replaced) — clears the selection.</summary>
    public void Reset()
    {
        if (_selected.Count == 0)
        {
            _lead = -1;
            _anchor = -1;
            return;
        }

        SetSelection([], -1, -1);
    }

    // The lead's fate under a removal: survives (shifted) or, if dropped, relocates to the nearest survivor.
    private static int ShiftOrRelocate(int lead, int index, int end, int count, HashSet<int> survivors)
    {
        if (lead < 0)
            return -1;
        if (lead < index)
            return lead;
        if (lead >= end)
            return lead - count;
        return NearestTo(survivors, index); // lead was in the removed range
    }

    // The surviving index nearest to `target` (tie → smaller); −1 if none survive.
    private static int NearestTo(HashSet<int> set, int target)
    {
        var best = -1;
        var bestDistance = int.MaxValue;
        foreach (var x in set)
        {
            var distance = Math.Abs(x - target);
            if (distance < bestDistance || (distance == bestDistance && x < best))
            {
                best = x;
                bestDistance = distance;
            }
        }

        return best;
    }

    // Replaces the selection set + lead/anchor, firing SelectionChanged with the membership diff (if any).
    private void SetSelection(HashSet<int> target, int lead, int anchor)
    {
        List<int>? added = null;
        List<int>? removed = null;

        foreach (var x in target)
            if (!_selected.Contains(x))
                (added ??= []).Add(x);
        foreach (var x in _selected)
            if (!target.Contains(x))
                (removed ??= []).Add(x);

        _selected.Clear();
        _selected.UnionWith(target);
        _sortedCache = null;
        _lead = lead;
        _anchor = anchor;

        if (added is null && removed is null)
            return;

        added?.Sort();
        removed?.Sort();
        Raise(added ?? [], removed ?? []);
    }

    // A pure index remap (insert/remove shift) — same items stay selected, so no SelectionChanged.
    private void Reindex(Func<int, int> map)
    {
        if (_selected.Count > 0)
        {
            var remapped = new HashSet<int>(_selected.Count);
            foreach (var x in _selected)
                remapped.Add(map(x));
            _selected.Clear();
            _selected.UnionWith(remapped);
            _sortedCache = null;
        }

        if (_lead >= 0)
            _lead = map(_lead);
        if (_anchor >= 0)
            _anchor = map(_anchor);
    }

    private void Raise(IReadOnlyList<int> added, IReadOnlyList<int> removed)
        => SelectionChanged?.Invoke(this, new SelectionChangedEventArgs(added, removed));

    private static int Min(HashSet<int> set)
    {
        var min = int.MaxValue;
        foreach (var x in set)
            if (x < min)
                min = x;
        return min;
    }

    private static int Max(HashSet<int> set)
    {
        var max = int.MinValue;
        foreach (var x in set)
            if (x > max)
                max = x;
        return max;
    }
}
