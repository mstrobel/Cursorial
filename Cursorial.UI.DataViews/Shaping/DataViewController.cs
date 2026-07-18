using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Cursorial.UI.DataViews.Shaping;

/// <summary>
/// The non-generic shaping facade (design doc §2 — panel Q&amp;A: the named headless entry point).
/// <see cref="Create"/> closes <see cref="DataViewController{TRow}"/> over a runtime row type (the
/// grid's lane: <c>ItemsSource</c> is untyped); typed consumers construct the generic directly.
/// </summary>
public abstract class DataViewController : IDisposable
{
    private protected DataViewController() { }

    /// <summary>Closes the typed controller over <paramref name="rowType"/> (reference types only in v1).</summary>
    [RequiresDynamicCode("Closes the generic controller over a runtime row type.")]
    public static DataViewController Create(Type rowType, IShapingScheduler? scheduler = null)
    {
        ArgumentNullException.ThrowIfNull(rowType);
        if (rowType.IsValueType)
            throw new ArgumentException("Row types are reference types in v1 (design doc §2.1).", nameof(rowType));

        return (DataViewController)Activator.CreateInstance(
            typeof(DataViewController<>).MakeGenericType(rowType), [scheduler])!;
    }

    /// <summary>The published snapshot (never null; empty before the first shape).</summary>
    public DataViewSnapshot Snapshot { get; private protected set; } = DataViewSnapshot.Empty;

    /// <summary>Raised on the owner thread after a new snapshot publishes.</summary>
    public event EventHandler? SnapshotChanged;

    private protected void RaiseSnapshotChanged() => SnapshotChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>The active columns, sort, grouping, summaries, and filter (see the typed surface).</summary>
    public abstract void SetColumns(IReadOnlyList<ShapingColumnDescription> columns);

    /// <summary>Applies the whole shape atomically (one reshape regardless of how many facets changed).</summary>
    public abstract void SetShape(
        IReadOnlyList<SortDescription> sorts,
        IReadOnlyList<GroupDescription> groups,
        IReadOnlyList<SummaryDescription> summaries,
        FilterNode? filter);

    /// <summary>Attaches the row source (an <see cref="IEnumerable"/>; INCC observed; per-row INPC when <paramref name="liveUpdates"/>).</summary>
    public abstract void AttachSource(IEnumerable? source, bool liveUpdates = true);

    /// <summary>Toggles a group's collapse state by its path key; reshapes the flat view.</summary>
    public abstract void SetCollapsed(string groupPath, bool collapsed);

    /// <summary>Formats one cell (band-cache/cold callers; the presenter's span lane comes with the band cache).</summary>
    public abstract string FormatCell(int rowId, object columnKey);

    /// <summary>The row object for a row id (selection/copy surfaces).</summary>
    public abstract object GetRowObject(int rowId);

    /// <summary>Whether a column carries the compiled write-back setter (the editing lane — §3.2).</summary>
    public abstract bool IsColumnEditable(object columnKey);

    /// <summary>
    /// The edit-commit path (§3.2): parses <paramref name="text"/> to the column's key type and
    /// writes through the compiled setter. A non-INPC row ticks manually (INPC rows tick through
    /// the normal live pipeline). False when read-only or unparseable — the editor stays open.
    /// </summary>
    public abstract bool TrySetCellFromText(int rowId, object columnKey, string text);

    /// <summary>The grand-total formatted summaries, aligned with the summary descriptions.</summary>
    public IReadOnlyList<string> Totals { get; private protected set; } = [];

    /// <summary>Drains any pending coalesced ticks synchronously (tests + frame-boundary flushes).</summary>
    public abstract void Flush();

    /// <summary>Detaches the source (INCC + all INPC subscriptions) and drops state. Idempotent.</summary>
    public abstract void Dispose();
}

/// <summary>The typed shaping controller (design doc §2; the sync lane — the size-gated background lane rides §2.6).</summary>
[RequiresDynamicCode("The shaping engine compiles expression trees specialized to the row type.")]
public sealed class DataViewController<TRow> : DataViewController where TRow : class
{
    private readonly IShapingScheduler _scheduler;
    private readonly RowStore<TRow> _store = new();
    private readonly SortScratch _scratch = new();
    private readonly ShapingGroups.Buffers _groupBuffers = new();
    private readonly HashSet<string> _collapsedPaths = [];

    // Live-tick carriers (§2.3/§2.6).
    private readonly RowFlagSet _dirty = new();
    private readonly RowFlagSet _removed = new();
    private readonly List<int> _dirtyRows = [];
    private bool _tickScheduled;

    // The shape.
    private readonly List<(ShapingColumnDescription Description, ShapedColumn Column)> _columns = [];
    private IReadOnlyList<SortDescription> _sorts = [];
    private IReadOnlyList<GroupDescription> _groups = [];
    private IReadOnlyList<SummaryDescription> _summaries = [];
    private FilterNode? _filter;

    // Compiled per-shape kit.
    private Comparison<int>? _slotComparison;
    private ShapedColumn[] _groupColumns = [];
    private Func<int, bool>? _filterPredicate;
    private (SummaryDescription Description, ColumnAggregator Aggregator)[] _aggregators = [];

    // Source.
    private IEnumerable? _source;
    private bool _liveUpdates;
    private NotifyCollectionChangedEventHandler? _collectionHandler;
    private PropertyChangedEventHandler? _rowHandler;
    private HashSet<string>? _shapedPropertyNames;

    private int _version;
    private int[] _sortedView = [];
    private int _sortedLength;
    private bool _disposed;

    // The background lane (§2.6): one shape in flight at a time, generation-checked publishes,
    // reclamation gated while in flight, ticks replay after publish.
    private bool _inFlight;
    private int _shapeGeneration;

    public DataViewController(IShapingScheduler? scheduler = null)
        => _scheduler = scheduler ?? InlineShapingScheduler.Instance;

    /// <summary>
    /// Visible-row count above which full reshapes route to the ThreadPool (§2.6 — the size gate;
    /// ticks share it via their post-repair view size). <see cref="int.MaxValue"/> forces the
    /// synchronous lane (deterministic tests; small-data apps).
    /// </summary>
    public int BackgroundThreshold { get; set; } = 32 * 1024;

    /// <summary>The background executor seam (tests inject a manually-pumped runner for deterministic interleavings).</summary>
    internal Action<Action> BackgroundRunner { get; set; } = static work => Task.Run(work);

    // ── Configuration ────────────────────────────────────────────────────────────────────────────

    public override void SetColumns(IReadOnlyList<ShapingColumnDescription> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ThrowIfDisposed();

        _columns.Clear();
        foreach (var description in columns)
        {
            var selector = description.KeySelector
                ?? ShapingCodegen.BuildPropertyPathLambda(typeof(TRow), description.FieldName
                    ?? throw new ArgumentException($"Column '{description.Key}' has neither FieldName nor KeySelector."));

            var column = ShapingCodegen.CreateColumn<TRow>(description.Key, selector,
                                                           description.StringComparison, description.Format);
            _columns.Add((description, column));
        }

        _shapedPropertyNames = BuildShapedPropertyNames(columns);
        ExtractAllKeys();
        Reshape();
    }

    public override void SetShape(
        IReadOnlyList<SortDescription> sorts,
        IReadOnlyList<GroupDescription> groups,
        IReadOnlyList<SummaryDescription> summaries,
        FilterNode? filter)
    {
        ThrowIfDisposed();
        _sorts = sorts?.ToArray() ?? [];
        _groups = groups?.ToArray() ?? [];
        _summaries = summaries?.ToArray() ?? [];
        _filter = filter;
        CompileShape();
        Reshape();
    }

    public override void AttachSource(IEnumerable? source, bool liveUpdates = true)
    {
        ThrowIfDisposed();
        DetachSource();

        _source = source;
        _liveUpdates = liveUpdates;

        if (source is not null)
        {
            int index = 0;
            foreach (var item in source)
            {
                var row = (TRow)item!;
                _store.Insert(index++, row);
                SubscribeRow(row);
            }

            if (source is INotifyCollectionChanged incc)
            {
                _collectionHandler = OnCollectionChanged;
                incc.CollectionChanged += _collectionHandler;
            }
        }

        ExtractAllKeys();
        Reshape();
    }

    public override void SetCollapsed(string groupPath, bool collapsed)
    {
        ArgumentNullException.ThrowIfNull(groupPath);
        ThrowIfDisposed();

        bool changed = collapsed ? _collapsedPaths.Add(groupPath) : _collapsedPaths.Remove(groupPath);
        if (!changed)
            return;

        // Collapse only re-flattens — sort/groups/aggregates are untouched.
        PublishFromSorted();
    }

    /// <summary>The shaped column for a key (the grid's paint/filter surfaces).</summary>
    internal ShapedColumn? FindColumn(object columnKey)
    {
        foreach (var (description, column) in _columns)
        {
            if (Equals(description.Key, columnKey))
                return column;
        }
        return null;
    }

    public override string FormatCell(int rowId, object columnKey)
        => FindColumn(columnKey)?.FormatSlot(rowId) ?? string.Empty;

    public override object GetRowObject(int rowId) => _store.GetRow(rowId);

    public override bool IsColumnEditable(object columnKey) => FindColumn(columnKey)?.IsEditable == true;

    public override bool TrySetCellFromText(int rowId, object columnKey, string text)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(text);

        var column = FindColumn(columnKey);
        if (column is null)
            return false;

        var row = _store.GetRow(rowId);
        if (row is null || !column.TrySetFromText(row, text))
            return false;

        // An INPC row already ticked through the shared handler; a plain row ticks here so the
        // written value reshapes either way.
        if (row is not INotifyPropertyChanged)
        {
            MarkDirty(rowId);
            ScheduleTick();
        }
        return true;
    }

    /// <summary>The typed slot→row accessor (filter Custom leaves; the grid's typed surfaces).</summary>
    public Func<int, TRow> RowAccessor => _store.GetRow;

    // ── The INCC / INPC live pipeline (§2.6) ─────────────────────────────────────────────────────

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
            {
                // Key extraction is DEFERRED to the drain (§2.6 invariant 1 — vectors are written
                // only on the owner thread and never while a shape is in flight; the drain is the
                // one site that satisfies both, and it coalesces multi-ticks per row for free).
                int index = e.NewStartingIndex;
                foreach (var item in e.NewItems!)
                {
                    var row = (TRow)item!;
                    int slot = _store.Insert(index++, row);
                    SubscribeRow(row);
                    MarkInserted(slot);
                }
                break;
            }

            case NotifyCollectionChangedAction.Remove:
            {
                for (int i = 0; i < e.OldItems!.Count; i++)
                {
                    var row = (TRow)_store.GetRow(_store.SlotAt(e.OldStartingIndex));
                    int slot = _store.RemoveAt(e.OldStartingIndex);
                    UnsubscribeRow(row);
                    MarkRemoved(slot);
                }
                break;
            }

            case NotifyCollectionChangedAction.Replace:
            {
                int index = e.NewStartingIndex;
                foreach (var item in e.NewItems!)
                {
                    var old = _store.GetRow(_store.SlotAt(index));
                    var row = (TRow)item!;
                    int slot = _store.Replace(index++, row);
                    UnsubscribeRow(old);
                    SubscribeRow(row);
                    MarkDirty(slot); // extraction deferred to the drain (invariant 1)
                }
                break;
            }

            case NotifyCollectionChangedAction.Move:
                // Source order is not a shaping input (the sequence tiebreak is insertion-stamped);
                // just re-map the index space.
                for (int i = 0; i < (e.OldItems?.Count ?? 1); i++)
                    _store.Move(e.OldStartingIndex + i, e.NewStartingIndex + i);
                return;

            default: // Reset
                RebuildFromSource();
                return;
        }

        ScheduleTick();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TRow row)
            return;

        // "" and "Item[]" always match (the binding-engine convention); otherwise only shaped columns tick.
        if (!string.IsNullOrEmpty(e.PropertyName) && e.PropertyName != "Item[]" &&
            _shapedPropertyNames is { } names && !names.Contains(e.PropertyName))
        {
            return;
        }

        int slot = _store.SlotOf(row);
        if (slot < 0)
            return;

        MarkDirty(slot); // extraction deferred to the drain (invariant 1)
        ScheduleTick();
    }

    private void MarkDirty(int slot)
    {
        if (!_removed.Contains(slot) && _dirty.Add(slot))
            _dirtyRows.Add(slot);
    }

    private void MarkInserted(int slot)
    {
        // Inserted rows enter through the dirty lane but are NOT in the current view — the repair
        // merge treats them as pure inserts (they're absent from the clean sweep by construction).
        if (_dirty.Add(slot))
            _dirtyRows.Add(slot);
    }

    private void MarkRemoved(int slot)
    {
        if (_dirty.Contains(slot))
            _dirtyRows.Remove(slot);
        _removed.Add(slot);
    }

    private void ScheduleTick()
    {
        if (_tickScheduled)
            return;
        _tickScheduled = true;
        _scheduler.Post(DrainTick);
    }

    public override void Flush()
    {
        ThrowIfDisposed();
        if (_tickScheduled)
            DrainTick();
    }

    private void DrainTick()
    {
        if (_disposed || !_tickScheduled)
            return;

        // A shape in flight owns the vectors — the tick replays after publish (§2.6).
        if (_inFlight)
            return;

        _tickScheduled = false;

        int k = _dirtyRows.Count + _removed.Count;
        if (k == 0)
            return;

        // Deferred key extraction (invariant 1): the one owner-thread, no-shape-in-flight site.
        foreach (int slot in _dirtyRows)
        {
            var row = _store.GetRow(slot);
            if (row is not null)
                ExtractRowKeys(row, slot);
        }

        // Visibility recheck for dirty rows: a row failing the filter leaves the view (mark removed
        // for the sweep); a passing row (re-)inserts via the dirty lane.
        int write = 0;
        for (int i = 0; i < _dirtyRows.Count; i++)
        {
            int slot = _dirtyRows[i];
            if (_filterPredicate is null || _filterPredicate(slot))
                _dirtyRows[write++] = slot;
            else
                _removed.Add(slot);
        }
        _dirtyRows.RemoveRange(write, _dirtyRows.Count - write);

        if (_dirtyRows.Count > ShapingRepair.FullSortThreshold * Math.Max(1, _sortedLength))
        {
            // Repair would degenerate — but a full Reshape would re-sort SOURCE order (random w.r.t.
            // the keys, ~18× slower at 1M; benchmark finding 2026-07-18). Re-sort the OLD VIEW
            // instead: [clean run (still sorted)] + [dirty rows with fresh keys] — TimSort's run
            // detection makes this near-linear, exactly the mostly-sorted profile. Into a FRESH
            // array: the published snapshot aliases _sortedView (immutable-by-contract, §2.6).
            var fresh = new int[Math.Max(16, _sortedLength + _dirtyRows.Count)];
            int count = 0;
            for (int i = 0; i < _sortedLength; i++)
            {
                int slot = _sortedView[i];
                if (!_removed.Contains(slot) && !_dirty.Contains(slot))
                    fresh[count++] = slot;
            }
            foreach (int slot in _dirtyRows)
                fresh[count++] = slot;

            ShapingSort.Sort(fresh, count, _slotComparison!, _scratch);
            _sortedView = fresh;
            _sortedLength = count;
            PublishFromSorted();
        }
        else
        {
            var dirtyBuffer = _dirtyRows.ToArray();
            ShapingSort.Sort(dirtyBuffer, dirtyBuffer.Length, _slotComparison!, _scratch);

            int capacity = _sortedLength + dirtyBuffer.Length;
            var result = new int[capacity]; // pooling arrives with the background lane
            _sortedLength = ShapingRepair.Repair(
                _sortedView, _sortedLength, _dirty, _removed, dirtyBuffer, dirtyBuffer.Length,
                _slotComparison!, result, _scratch);
            _sortedView = result;

            PublishFromSorted();
        }

        _dirty.Clear();
        _removed.Clear();
        _dirtyRows.Clear();
    }

    // ── The reshape pipeline (§2.5/§2.6 — filter → sort → groups → aggregates → publish) ─────────

    private void Reshape()
    {
        if (_slotComparison is null)
            CompileShape();

        int generation = ++_shapeGeneration;

        // One shape in flight at a time (§2.6): a request arriving mid-shape just bumps the
        // generation — the in-flight completion sees the mismatch, drops its stale result, and
        // re-runs the NEWEST shape. No double work, no torn publishes.
        if (_inFlight)
            return;

        // A full reshape ABSORBS any pending tick: extract the deferred dirty keys first (else the
        // sort reads stale values for ticked rows), then drop the tick — the reshape covers it.
        if (_dirtyRows.Count > 0)
        {
            foreach (int slot in _dirtyRows)
            {
                var row = _store.GetRow(slot);
                if (row is not null)
                    ExtractRowKeys(row, slot);
            }
        }
        _dirty.Clear();
        _removed.Clear();
        _dirtyRows.Clear();
        _tickScheduled = false;

        // The size gate (§2.6): big shapes leave the owner thread. The candidate size is the store
        // count (filtering may shrink it, but the filter pass itself is part of the gated cost).
        if (_store.Count > BackgroundThreshold)
        {
            RunBackgroundReshape(generation);
            return;
        }

        var (view, length) = FilterAndSort(_store.SourceOrder.ToArray(), _slotComparison!,
                                           _filterPredicate, _scratch,
                                           _sortedView.Length >= _store.Count ? _sortedView : null);
        _sortedView = view;
        _sortedLength = length;

        PublishFromSorted();
    }

    /// <summary>The filter+sort pass (both lanes; the background lane passes fresh buffers).</summary>
    private static (int[] View, int Length) FilterAndSort(
        int[] sourceOrder, Comparison<int> comparison, Func<int, bool>? filter,
        SortScratch scratch, int[]? reusable)
    {
        var view = reusable is not null && reusable.Length >= sourceOrder.Length
            ? reusable
            : new int[Math.Max(16, sourceOrder.Length * 2)];

        int length = 0;
        foreach (int slot in sourceOrder)
        {
            if (filter is null || filter(slot))
                view[length++] = slot;
        }

        ShapingSort.Sort(view, length, comparison, scratch);
        return (view, length);
    }

    private void RunBackgroundReshape(int generation)
    {
        _inFlight = true;
        _store.DeferReclamation = true;   // §2.6 invariant 2

        // Capture everything the pipeline reads: the compiled kit is immutable per shape; the source
        // order is copied (the store mutates on the owner thread while we run); vectors are sealed
        // by invariant 1 (extraction deferred while in flight).
        var sourceOrder = _store.SourceOrder.ToArray();
        var comparison = _slotComparison!;
        var filter = _filterPredicate;

        BackgroundRunner(() =>
        {
            try
            {
                var (view, length) = FilterAndSort(sourceOrder, comparison, filter, new SortScratch(), reusable: null);
                _scheduler.Post(() => CompleteBackgroundReshape(generation, view, length));
            }
            catch (Exception)
            {
                // A faulted shape must not strand the gates (a poisoned comparer/filter surfaces on
                // the next sync operation); publish nothing.
                _scheduler.Post(() => CompleteBackgroundReshape(generation, null, 0));
            }
        });
    }

    private void CompleteBackgroundReshape(int generation, int[]? view, int length)
    {
        if (_disposed)
            return;

        _inFlight = false;
        _store.DeferReclamation = false;

        if (generation == _shapeGeneration && view is not null)
        {
            _sortedView = view;
            _sortedLength = length;
            PublishFromSorted();
        }
        else if (generation != _shapeGeneration)
        {
            // Superseded: the newest shape request re-runs now (it found _inFlight and fell through
            // to nothing — the reshape below is its deferred execution).
            Reshape();
            return;
        }

        // Ticks that arrived mid-shape replay as ordinary repairs against the fresh view.
        if (_tickScheduled)
            _scheduler.Post(DrainTick);
    }

    private void PublishFromSorted()
    {
        ShapingGroups.DeriveAndFlatten(_sortedView, _sortedLength, _groupColumns, _collapsedPaths, _groupBuffers);

        GroupNode[] nodes;
        int[] flat;
        int flatLength;
        if (_groupBuffers.FlatLength < 0)
        {
            nodes = [];
            flat = _sortedView;
            flatLength = _sortedLength;
        }
        else
        {
            nodes = _groupBuffers.Nodes.ToArray();
            flat = _groupBuffers.Flat;
            flatLength = _groupBuffers.FlatLength;

            // Per-group summaries (v1: recompute per publish; membership-dirty tracking is a
            // recorded optimization — the walk is aggregate-loop bound, background-gated at scale).
            foreach (var node in nodes)
            {
                if (_aggregators.Length > 0)
                {
                    var cells = new string[_aggregators.Length];
                    for (int i = 0; i < _aggregators.Length; i++)
                    {
                        var (description, aggregator) = _aggregators[i];
                        var value = aggregator.Aggregate(_sortedView, node.SortedStart, node.RowCount);
                        cells[i] = ApplyTemplate(description.DisplayTemplate, aggregator.Format(value));
                    }
                    node.Summaries = cells;
                }
            }
        }

        // Grand totals.
        if (_aggregators.Length > 0)
        {
            var totals = new string[_aggregators.Length];
            for (int i = 0; i < _aggregators.Length; i++)
            {
                var (description, aggregator) = _aggregators[i];
                var value = aggregator.Aggregate(_sortedView, 0, _sortedLength);
                totals[i] = ApplyTemplate(description.DisplayTemplate, aggregator.Format(value));
            }
            Totals = totals;
        }
        else
        {
            Totals = [];
        }

        // The flat/sorted arrays are handed to the snapshot by reference; the tick pipeline swaps in
        // fresh arrays rather than mutating published ones (§2.6 invariant 4's sync-lane analog) —
        // EXCEPT the group buffers, which re-derive per publish into freshly-snapshot arrays above.
        Snapshot = new DataViewSnapshot(++_version, _sortedView, _sortedLength, nodes,
                                        _groupBuffers.FlatLength < 0 ? _sortedView : CopyFlat(flat, flatLength),
                                        flatLength)
                   { DataRowLevel = _groupColumns.Length == 0 ? 0 : _groupColumns.Length };

        RaiseSnapshotChanged();
    }

    private static string[] _emptyStrings = [];

    private static int[] CopyFlat(int[] flat, int length)
    {
        var copy = new int[length];
        Array.Copy(flat, copy, length);
        return copy;
    }

    private static string ApplyTemplate(string? template, string value)
        => template is null ? value : string.Format(System.Globalization.CultureInfo.CurrentCulture, template, value);

    private void CompileShape()
    {
        // Group levels prepend to the sort (a group level IS a sort level — §2.5).
        var levels = new List<(ShapedColumn, bool)>();
        var groupColumns = new List<ShapedColumn>();

        foreach (var group in _groups)
        {
            var column = RequireColumn(group.ColumnKey);
            levels.Add((column, group.Direction == SortDirection.Descending));
            groupColumns.Add(column);
        }
        foreach (var sort in _sorts)
            levels.Add((RequireColumn(sort.ColumnKey), sort.Direction == SortDirection.Descending));

        _slotComparison = ShapingCodegen.BuildSlotComparison(levels, _store);
        _groupColumns = groupColumns.ToArray();

        _filterPredicate = _filter is null
            ? null
            : ShapingFilter.Compile(_filter, FindColumn, RowAccessor);

        _aggregators = _summaries
            .Select(s => (s, ColumnAggregator.Create(RequireColumn(s.ColumnKey), s.Aggregate, s.Format)))
            .ToArray();
    }

    private ShapedColumn RequireColumn(object key)
        => FindColumn(key) ?? throw new ArgumentException($"Unknown column '{key}'.");

    // ── Key extraction + row subscription ────────────────────────────────────────────────────────

    private void ExtractAllKeys()
    {
        foreach (var (_, column) in _columns)
            column.EnsureCapacity(Math.Max(1, _store.SlotCapacity));

        foreach (int slot in _store.SourceOrder)
        {
            var row = _store.GetRow(slot);
            foreach (var (_, column) in _columns)
                column.ExtractKeyUntyped(row, slot);
        }
    }

    private void ExtractRowKeys(TRow row, int slot)
    {
        foreach (var (_, column) in _columns)
        {
            column.EnsureCapacity(_store.SlotCapacity);
            column.ExtractKeyUntyped(row, slot);
        }
    }

    private void SubscribeRow(TRow row)
    {
        if (!_liveUpdates || row is not INotifyPropertyChanged inpc)
            return;
        _rowHandler ??= OnRowPropertyChanged;
        inpc.PropertyChanged += _rowHandler;
    }

    private void UnsubscribeRow(TRow row)
    {
        if (_rowHandler is not null && row is INotifyPropertyChanged inpc)
            inpc.PropertyChanged -= _rowHandler;
    }

    private HashSet<string>? BuildShapedPropertyNames(IReadOnlyList<ShapingColumnDescription> columns)
    {
        // The FIRST path segment per column — a tick on any other property skips shaping entirely.
        // Nested-path invalidation over intermediate hops is a recorded limitation (a change to
        // row.Customer itself re-ticks; a change INSIDE a stale Customer instance does not).
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            if (column.FieldName is { Length: > 0 } field)
            {
                int dot = field.IndexOf('.');
                names.Add(dot < 0 ? field : field[..dot]);
            }
            else
            {
                return null; // lambda columns: unknown member set — every tick shapes (conservative)
            }
        }
        return names;
    }

    private void RebuildFromSource()
    {
        // Reset: unsubscribe everything, rebuild the store from the source, full reshape.
        if (_rowHandler is not null)
        {
            foreach (int slot in _store.SourceOrder)
                UnsubscribeRow(_store.GetRow(slot));
        }
        _store.Clear();
        _dirty.Clear();
        _removed.Clear();
        _dirtyRows.Clear();

        if (_source is not null)
        {
            int index = 0;
            foreach (var item in _source)
            {
                var row = (TRow)item!;
                _store.Insert(index++, row);
                SubscribeRow(row);
            }
        }

        ExtractAllKeys();
        Reshape();
    }

    private void DetachSource()
    {
        if (_source is INotifyCollectionChanged incc && _collectionHandler is not null)
            incc.CollectionChanged -= _collectionHandler;
        _collectionHandler = null;

        if (_rowHandler is not null)
        {
            foreach (int slot in _store.SourceOrder)
                UnsubscribeRow(_store.GetRow(slot));
        }

        _source = null;
        _store.Clear();
        _dirty.Clear();
        _removed.Clear();
        _dirtyRows.Clear();
        _sortedLength = 0;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────────────────────────

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public override void Dispose()
    {
        if (_disposed)
            return;
        DetachSource();
        _disposed = true;
    }
}
