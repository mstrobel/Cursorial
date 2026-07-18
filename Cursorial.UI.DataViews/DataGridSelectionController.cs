using Cursorial.UI.DataViews.Shaping;

namespace Cursorial.UI.DataViews;

/// <summary>
/// Row selection keyed on stable ROW IDS (design doc §3.3 — the panel decision): membership survives
/// every re-sort/filter/regroup by construction, so reshapes need zero selection fixup. Gestures
/// resolve view ranges to ids at gesture time against the current snapshot; paint asks
/// <see cref="IsSelected"/>. Large selections represent compactly: an explicit id set below the
/// flip threshold, an <b>all-except</b> inversion above it (Ctrl+A at 1M rows is O(1), never a
/// 1M-entry hash set). Designed control-agnostic for later lift to <c>Cursorial.UI</c>.
/// </summary>
public sealed class DataGridSelectionController
{
    private readonly HashSet<int> _set = [];   // selected ids, or the EXCEPTIONS when inverted
    private bool _inverted;                    // true ⇒ "everything except _set"

    /// <summary>The focus/lead row id (−1 none). The anchor for Shift ranges is view-space, grid-owned.</summary>
    public int LeadRowId { get; private set; } = -1;

    /// <summary>Raised after any selection change (the presenter re-inks).</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Whether a row id is selected.</summary>
    public bool IsSelected(int rowId) => _inverted ? !_set.Contains(rowId) : _set.Contains(rowId);

    /// <summary>Whether anything is selected (inverted mode is trivially non-empty for a non-empty view).</summary>
    public bool IsEmpty => !_inverted && _set.Count == 0;

    /// <summary>Replaces the selection with one row (the plain-click gesture).</summary>
    public void SelectOnly(int rowId)
    {
        _inverted = false;
        _set.Clear();
        _set.Add(rowId);
        LeadRowId = rowId;
        RaiseChanged();
    }

    /// <summary>Toggles one row (Ctrl+click / Ctrl+Space).</summary>
    public void Toggle(int rowId)
    {
        if (IsSelected(rowId))
        {
            if (_inverted) _set.Add(rowId);
            else _set.Remove(rowId);
        }
        else
        {
            if (_inverted) _set.Remove(rowId);
            else _set.Add(rowId);
        }
        LeadRowId = rowId;
        RaiseChanged();
    }

    /// <summary>Adds a resolved view range's ids (the Shift gesture — the grid resolves the range).</summary>
    public void AddRange(ReadOnlySpan<int> rowIds, int leadRowId)
    {
        foreach (int id in rowIds)
        {
            if (_inverted) _set.Remove(id);
            else _set.Add(id);
        }
        LeadRowId = leadRowId;
        RaiseChanged();
    }

    /// <summary>Replaces the selection with a resolved range (Shift+click without Ctrl).</summary>
    public void SelectRange(ReadOnlySpan<int> rowIds, int leadRowId)
    {
        _inverted = false;
        _set.Clear();
        AddRange(rowIds, leadRowId);
    }

    /// <summary>Select-all (Ctrl+A): O(1) — flips to the all-except representation.</summary>
    public void SelectAll()
    {
        _inverted = true;
        _set.Clear();
        RaiseChanged();
    }

    /// <summary>
    /// Forgets removed row ids BEFORE their slots recycle (final-audit fix — the slot-reuse bleed:
    /// a freed id left selected would render the slot's NEXT occupant selected). Normal mode drops
    /// the ids; inverted mode ADDS them to the exception set (the recycled id must start
    /// unselected). The lead resets when it dies.
    /// </summary>
    public void HandleRowsRemoved(IReadOnlyList<int> rowIds)
    {
        ArgumentNullException.ThrowIfNull(rowIds);
        bool changed = false;
        foreach (int id in rowIds)
        {
            changed |= _inverted ? _set.Add(id) : _set.Remove(id);
            if (LeadRowId == id)
            {
                LeadRowId = -1;
                changed = true;
            }
        }
        if (changed)
            RaiseChanged();
    }

    /// <summary>
    /// Resets state for rows entering the store: a recycled id behaves exactly like a fresh one.
    /// Normal mode: a new row is unselected (drop any stale membership). Inverted mode: new rows
    /// JOIN an all-except selection (Ctrl+A means "everything" — the documented semantics; slot
    /// luck must not decide, so a recycled id's stale exception drops too).
    /// </summary>
    public void HandleRowsAdded(IReadOnlyList<int> rowIds)
    {
        ArgumentNullException.ThrowIfNull(rowIds);
        bool changed = false;
        foreach (int id in rowIds)
            changed |= _set.Remove(id);
        if (changed)
            RaiseChanged();
    }

    /// <summary>Clears the selection.</summary>
    public void Clear()
    {
        _inverted = false;
        _set.Clear();
        LeadRowId = -1;
        RaiseChanged();
    }

    /// <summary>
    /// The selected row ids materialized against a snapshot (copy/SelectedItems surfaces — O(view)).
    /// Inverted mode enumerates the snapshot's visible data rows minus the exceptions.
    /// </summary>
    public List<int> MaterializeSelectedIds(DataViewSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var result = new List<int>();

        if (!_inverted)
        {
            // Walk the view so results come out in view order (and dead ids drop lazily).
            for (int i = 0; i < snapshot.Count; i++)
            {
                var row = snapshot.GetRow(i);
                if (!row.IsGroup && _set.Contains(row.RowId))
                    result.Add(row.RowId);
            }
        }
        else
        {
            for (int i = 0; i < snapshot.Count; i++)
            {
                var row = snapshot.GetRow(i);
                if (!row.IsGroup && !_set.Contains(row.RowId))
                    result.Add(row.RowId);
            }
        }

        return result;
    }

    private void RaiseChanged() => SelectionChanged?.Invoke(this, EventArgs.Empty);
}
