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

    /// <summary>Closes the typed controller over <paramref name="rowType"/>. Value-type rows are
    /// supported with runtime guards (design doc §9.6): they must attach with
    /// <c>liveUpdates: false</c>, and the row id is their only identity.</summary>
    [RequiresDynamicCode("Closes the generic controller over a runtime row type.")]
    public static DataViewController Create(Type rowType, IShapingScheduler? scheduler = null)
    {
        ArgumentNullException.ThrowIfNull(rowType);
        return (DataViewController)Activator.CreateInstance(
            typeof(DataViewController<>).MakeGenericType(rowType), [scheduler])!;
    }

    /// <summary>The published snapshot (never null; empty before the first shape).</summary>
    public DataViewSnapshot Snapshot { get; private protected set; } = DataViewSnapshot.Empty;

    /// <summary>Raised on the owner thread after a new snapshot publishes.</summary>
    public event EventHandler? SnapshotChanged;

    /// <summary>
    /// Raised (owner thread) when rows leave the store — their ids are about to become reusable, so
    /// id-keyed consumers (selection — the final-audit slot-reuse bleed) MUST forget them now.
    /// </summary>
    public event Action<IReadOnlyList<int>>? RowsRemoved;

    /// <summary>
    /// Raised (owner thread) when rows enter the store — id-keyed consumers reset any recycled-id
    /// state so a reused slot behaves exactly like a fresh one (consistency under the selection
    /// inversion — final-audit follow-through).
    /// </summary>
    public event Action<IReadOnlyList<int>>? RowsAdded;

    /// <summary>Raised when the whole source resets/detaches — id-keyed consumer state must clear.</summary>
    public event EventHandler? RowsReset;

    private protected void RaiseSnapshotChanged() => SnapshotChanged?.Invoke(this, EventArgs.Empty);
    private protected void RaiseRowsRemoved(IReadOnlyList<int> slots) => RowsRemoved?.Invoke(slots);
    private protected void RaiseRowsAdded(IReadOnlyList<int> slots) => RowsAdded?.Invoke(slots);
    private protected void RaiseRowsReset() => RowsReset?.Invoke(this, EventArgs.Empty);

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

    /// <summary>Formats one cell (cold callers — editors, copy, diagnostics; the band cache uses
    /// the <see cref="FormatCellTo"/> span lane).</summary>
    public abstract string FormatCell(int rowId, object columnKey);

    /// <summary>
    /// The §9.6 span-format lane: writes the cell's display text into
    /// <paramref name="destination"/>, returning chars written, −1 when it doesn't fit (grow and
    /// retry), or 0 for an unknown column. Text-identical to <see cref="FormatCell"/>; the band
    /// cache formats through this into pooled char buffers (no per-cell string per fill).
    /// </summary>
    public abstract int FormatCellTo(int rowId, object columnKey, Span<char> destination);

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

    /// <summary>
    /// The new-row lane of the edit-commit path (§3.2): parses and writes onto an UN-STORED row
    /// instance (no row id yet — the grid's new-row template edits the object BEFORE
    /// <c>source.Add</c> lands it). No tick is scheduled — the subsequent Add is what reshapes.
    /// False when the column is read-only/unknown or the text doesn't parse.
    /// </summary>
    internal abstract bool TrySetRowText(object row, object columnKey, string text);

    /// <summary>
    /// The active conditional-formatting rules (design doc §2.7): compiled against the typed key
    /// vectors on the next shape (the grid pushes rules with every shape push). Does not itself
    /// publish — evaluation surfaces (<see cref="GetCellFormat"/> et al.) lazily refresh the stats
    /// block against the current snapshot version.
    /// </summary>
    public abstract void SetFormatRules(IReadOnlyList<FormatRule> rules);

    /// <summary>Whether any compiled formatting rule is active (the band fill's fast-path gate).</summary>
    public abstract bool HasFormatRules { get; }

    /// <summary>The cell-level format verdict (Threshold first-match + ColorScale fill — §2.7). A struct; no allocation.</summary>
    public abstract CellFormat GetCellFormat(int rowId, object columnKey);

    /// <summary>The row-level format verdict (<see cref="PredicateRule"/> first-match).</summary>
    public abstract CellFormat GetRowFormat(int rowId);

    /// <summary>
    /// The data-bar fill fraction for a cell in [0,1] (value's position in the column's stats
    /// range), or NaN when the column carries no <see cref="DataBarRule"/> / the value is null.
    /// </summary>
    public abstract double GetDataBarFraction(int rowId, object columnKey);

    /// <summary>
    /// The exact top/bottom-K threshold for <paramref name="columnKey"/> over the CURRENT visible
    /// (filtered, pre-collapse) rows — the engine half of top/bottom conditional-format rules
    /// (design doc §9.5's TopK stats addition). Among the visible rows' NON-NULL key values:
    /// <list type="bullet">
    /// <item><paramref name="top"/> true: <paramref name="threshold"/> is the K-th largest value —
    /// a cell whose value compares &gt;= the threshold is in the top K. Ties INCLUDE (the
    /// DevExpress semantics: every value equal to the threshold qualifies, so more than K cells may
    /// highlight).</item>
    /// <item><paramref name="top"/> false: the K-th smallest value — a cell &lt;= the threshold is
    /// in the bottom K, ties included.</item>
    /// </list>
    /// K is <paramref name="count"/> directly, or — when <paramref name="percent"/> is true —
    /// <c>ceil(count% × visible non-null values)</c>. K clamps to the population (K ≥ N means every
    /// non-null value qualifies). Null keys never qualify and never count toward the population.
    /// Comparisons use the column's sort comparison (culture/collation-consistent), so consumers
    /// should evaluate cells against the threshold through the same column comparison (e.g. the
    /// filter lane's GreaterThanOrEqual).
    /// <para>
    /// Cost: one O(V)-expected quickselect over a scratch index array per distinct
    /// (column, count, percent, top) request, CACHED for the published snapshot's lifetime and
    /// invalidated on every publish.
    /// </para>
    /// </summary>
    /// <param name="columnKey">The shaped column's key.</param>
    /// <param name="count">The K (row count), or the percentage in (0, 100] when <paramref name="percent"/> is set.</param>
    /// <param name="percent">Whether <paramref name="count"/> is a percentage of the visible non-null population.</param>
    /// <param name="top">True for top-K (largest values), false for bottom-K (smallest).</param>
    /// <param name="threshold">The boxed threshold key value, or null when unavailable.</param>
    /// <returns>False when the column is unknown, <paramref name="count"/> ≤ 0, or no visible row
    /// has a non-null value for the column.</returns>
    public abstract bool TryGetTopKThreshold(object columnKey, int count, bool percent, bool top, out object? threshold);

    /// <summary>
    /// The number of CURRENT visible (filtered, pre-collapse) rows with a non-null key value for
    /// <paramref name="columnKey"/> — the population the percent form of
    /// <see cref="TryGetTopKThreshold"/> computes K against. 0 for an unknown column. Cached per
    /// published snapshot (invalidated on every publish).
    /// </summary>
    public abstract int VisibleNonNullCount(object columnKey);

    /// <summary>
    /// The column's distinct formatted values for the checklist popup (§3.4): typed dedupe over ALL
    /// stored rows (deliberately unfiltered — re-widening a filter must offer the excluded values),
    /// sorted by the column comparison, null first as <c>(Formatted: "", Raw: null)</c>, capped.
    /// </summary>
    public abstract IReadOnlyList<(string Formatted, object? Raw, int Count)> GetDistinctValues(
        object columnKey, int maxCount = 1000);

    /// <summary>The column's CLR key type (the auto-filter grammar's bare-text Contains-vs-Equals pick), or null.</summary>
    public abstract Type? GetColumnKeyType(object columnKey);

    /// <summary>
    /// Whether <paramref name="fragment"/> compiles against the current columns (literal conversion
    /// included). The filter surfaces validate BEFORE writing — the real compile runs inside a
    /// posted shape push where a bad literal would be an unhandled dispatcher exception.
    /// </summary>
    public abstract bool CanCompileFilter(FilterNode fragment);

    /// <summary>
    /// Visible-row count above which full reshapes route to the ThreadPool (§2.6 — the size gate;
    /// ticks share it via their post-repair view size). <see cref="int.MaxValue"/> forces the
    /// synchronous lane (deterministic tests; small-data apps).
    /// </summary>
    public int BackgroundThreshold { get; set; } = 32 * 1024;

    /// <summary>The grand-total formatted summaries, aligned with the summary descriptions.</summary>
    public IReadOnlyList<string> Totals { get; private protected set; } = [];

    /// <summary>Drains any pending coalesced ticks synchronously (tests + frame-boundary flushes).</summary>
    public abstract void Flush();

    /// <summary>Detaches the source (INCC + all INPC subscriptions) and drops state. Idempotent.</summary>
    public abstract void Dispose();
}

/// <summary>
/// The typed shaping controller (design doc §2; the sync lane — the size-gated background lane
/// rides §2.6). <typeparamref name="TRow"/> may be a value type (§9.6, runtime-guarded): struct
/// rows must attach with <c>liveUpdates: false</c> (no INPC identity to observe), the row id is
/// their ONLY identity (<see cref="GetRowObject"/> boxes a fresh copy per call), and edits write
/// back by id through the store (<see cref="TrySetCellFromText"/>).
/// </summary>
[RequiresDynamicCode("The shaping engine compiles expression trees specialized to the row type.")]
public sealed class DataViewController<TRow> : DataViewController
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
    private readonly List<int> _removedSlotsThisBatch = [];
    private readonly List<int> _addedSlotsThisBatch = [];
    private bool _tickScheduled;

    // The shape.
    private readonly List<(ShapingColumnDescription Description, ShapedColumn<TRow> Column)> _columns = [];
    private IReadOnlyList<SortDescription> _sorts = [];
    private IReadOnlyList<GroupDescription> _groups = [];
    private IReadOnlyList<SummaryDescription> _summaries = [];
    private FilterNode? _filter;

    // Compiled per-shape kit.
    private Comparison<int>? _slotComparison;
    private ShapedColumn[] _groupColumns = [];
    private Func<int, bool>? _filterPredicate;
    private (SummaryDescription Description, ColumnAggregator Aggregator)[] _aggregators = [];

    // The §9.5 order-groups-by-summary kit: one aggregator per group level with OrderBySummary
    // (compiled whether or not the same summary is displayed), consumed by the per-publish sibling
    // projection — never by the repair substrate.
    private (ColumnAggregator Aggregator, bool Descending)?[] _groupOrderKits = [];
    private bool _hasGroupOrder;
    private AggregateValue[] _groupOrderValues = [];
    private readonly GroupOrderComparer _groupOrderComparer = new();

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

    /// <summary>The background executor seam (tests inject a manually-pumped runner for deterministic interleavings).</summary>
    internal Action<Action> BackgroundRunner
    {
        get => _backgroundRunner;
        set
        {
            _backgroundRunner = value;
            _customRunner = true; // an injected runner pumps on the owner thread — inline Post is safe
        }
    }

    private Action<Action> _backgroundRunner = static work => Task.Run(work);
    private bool _customRunner;

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

            var column = (ShapedColumn<TRow>)ShapingCodegen.CreateColumn<TRow>(
                description.Key, selector, description.StringComparison, description.Format);
            _columns.Add((description, column));
        }

        _shapedPropertyNames = BuildShapedPropertyNames(columns);
        ExtractAllKeys();

        // Compiled format predicates capture ShapedColumn instances — a column rebuild must
        // recompile them or verdicts would read the ORPHANED key vectors (§2.7).
        if (_formatRules.Count > 0)
            CompileFormatRules();

        // The SORT/FILTER kit binds column objects the same way (the final-audit stale-kit bug: a
        // re-pushed column set left every later insert sorting through the orphaned columns'
        // never-extracted vectors). Drop shape entries whose key no longer resolves (the consumer's
        // next SetShape re-authorizes them), then recompile against the new columns.
        _sorts = _sorts.Where(d => FindColumn(d.ColumnKey) is not null).ToArray();
        _groups = _groups.Where(d => FindColumn(d.ColumnKey) is not null)
                         .Select(d => d.OrderBySummary is { } order && FindColumn(order.ColumnKey) is null
                             ? d with { OrderBySummary = null } // the summary column left; the level falls back to key order
                             : d)
                         .ToArray();
        _summaries = _summaries.Where(d => FindColumn(d.ColumnKey) is not null).ToArray();
        CompileShape();

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

        // §9.6 pinned guard: a struct row has no INPC identity to observe — the per-row live-update
        // lane cannot exist. Attach value-type rows with liveUpdates: false (INCC collection events
        // still work — they are index-based; edits write back by row id via TrySetCellFromText).
        if (typeof(TRow).IsValueType && liveUpdates && source is not null)
        {
            throw new InvalidOperationException(
                $"Live row updates require reference-typed rows; '{typeof(TRow).Name}' is a value type " +
                "with no INPC identity to observe. Attach with liveUpdates: false — collection changes " +
                "(INCC) still apply, and edits write back by row id (design doc §9.6).");
        }

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

    /// <summary>The shaped column for a key (the grid's paint/filter surfaces; row-typed so the
    /// §9.6 struct edit lane can dispatch without boxing the row).</summary>
    internal ShapedColumn<TRow>? FindColumn(object columnKey)
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

    public override int FormatCellTo(int rowId, object columnKey, Span<char> destination)
        => FindColumn(columnKey)?.FormatSlotTo(rowId, destination) ?? 0;

    public override object GetRowObject(int rowId) => _store.GetRow(rowId);

    public override bool IsColumnEditable(object columnKey) => FindColumn(columnKey)?.IsEditable == true;

    public override bool TrySetCellFromText(int rowId, object columnKey, string text)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(text);

        var column = FindColumn(columnKey);
        if (column is null)
            return false;

        // Wave-2 audit F2: a stale/removed rowId must never write into a freed slot — the old
        // reference-lane `row is null` sentinel cannot cover value-type rows (a freed slot holds a
        // default STRUCT, so the write "succeeded", the dirty mark re-inserted the dead slot, and a
        // ghost row entered the published view while the slot sat on the free list). IsLive is the
        // one liveness truth for both lanes.
        if (!_store.IsLive(rowId))
            return false;

        var row = _store.GetRow(rowId);

        if (!column.TrySetFromText(ref row, text))
            return false;

        if (typeof(TRow).IsValueType)
        {
            // §9.6: the compiled mutator wrote a modified COPY into `row` — store it back by id
            // (rowId is a struct row's ONLY identity) and tick so the edit reshapes.
            _store.SetRow(rowId, in row);
            MarkDirty(rowId);
            ScheduleTick();
        }
        else if (row is not INotifyPropertyChanged)
        {
            // An INPC row already ticked through the shared handler; a plain row ticks here so the
            // written value reshapes either way.
            MarkDirty(rowId);
            ScheduleTick();
        }
        return true;
    }

    internal override bool TrySetRowText(object row, object columnKey, string text)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return FindColumn(columnKey)?.TrySetFromText(row, text) == true;
    }

    /// <summary>The typed slot→row accessor (filter Custom leaves; the grid's typed surfaces).</summary>
    public Func<int, TRow> RowAccessor => _store.GetRow;

    public override Type? GetColumnKeyType(object columnKey) => FindColumn(columnKey)?.KeyType;

    public override bool CanCompileFilter(FilterNode fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        try
        {
            ShapingFilter.Compile(fragment, FindColumn, RowAccessor);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidCastException or
                                       OverflowException or InvalidOperationException)
        {
            return false; // unconvertible literal / unknown column — the surface keeps its editor open
        }
    }

    public override IReadOnlyList<(string Formatted, object? Raw, int Count)> GetDistinctValues(
        object columnKey, int maxCount = 1000)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        return FindColumn(columnKey)?.GetDistinctValues(_store.SourceOrder, maxCount) ?? [];
    }

    // ── Conditional formatting (§2.7 — compile per rule set, stats per publish, evaluate at band fill) ──

    private IReadOnlyList<FormatRule> _formatRules = [];
    private readonly List<CompiledColumnFormats> _columnFormats = [];
    private readonly List<(Func<int, bool> Predicate, CellFormat Format)> _rowFormatRules = [];
    private int _statsVersion = -1;

    /// <summary>One column's compiled formatting kit + its stats block (min/max over the filtered view).</summary>
    private sealed class CompiledColumnFormats
    {
        public required object ColumnKey { get; init; }
        public required ShapedColumn Column { get; init; }
        public Func<int, double>? DoubleReader;
        public bool HasDataBar;
        public ColorScaleRule? ColorScale;
        public readonly List<(Func<int, bool> Predicate, CellFormat Format)> Thresholds = [];
        public readonly List<CompiledTopBottom> TopBottom = [];
        public double StatsMin = double.NaN;
        public double StatsMax = double.NaN;
    }

    /// <summary>One TopBottom rule's compiled lane (§9.5): the rank predicate compiles ONCE; the
    /// boxed threshold re-derives per publish in <see cref="EnsureFormatStats"/> (the TopK seam).</summary>
    private sealed class CompiledTopBottom
    {
        public required TopBottomRule Rule { get; init; }
        public required Func<int, object?, bool> Qualifies { get; init; }
        public object? Threshold;
        public bool Valid;
    }

    public override bool HasFormatRules => _columnFormats.Count > 0 || _rowFormatRules.Count > 0;

    public override void SetFormatRules(IReadOnlyList<FormatRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ThrowIfDisposed();

        // Rules are immutable description objects — the same instance sequence IS the same rule
        // set, and the grid re-collects on EVERY shape push, so an unchanged set must not pay a
        // recompile (expression compiles per sort click otherwise).
        if (_formatRules.Count == rules.Count && _formatRules.SequenceEqual(rules))
            return;

        _formatRules = rules.ToArray();
        CompileFormatRules();
    }

    /// <summary>
    /// Compiles the rule set against the CURRENT shaped columns (re-run by <see cref="SetColumns"/>
    /// — compiled predicates capture <see cref="ShapedColumn"/> instances, so a column rebuild must
    /// recompile or verdicts would read orphaned key vectors). Rules referencing unshaped columns
    /// are skipped (the grid may push before a column's field resolves; a silent no-op beats a
    /// posted-dispatcher throw).
    /// </summary>
    private void CompileFormatRules()
    {
        _columnFormats.Clear();
        _rowFormatRules.Clear();
        _statsVersion = -1; // stats re-derive lazily against the next-read snapshot version

        foreach (var rule in _formatRules)
        {
            if (rule is PredicateRule predicate)
            {
                // The same compile lane as FilterNode.Custom — row-type mismatches throw here
                // (authoring-time, synchronous), never at evaluation.
                var compiled = ShapingFilter.Compile(
                    FilterNode.Custom(predicate.RowPredicate), FindColumn, RowAccessor);
                _rowFormatRules.Add((compiled, predicate.Format));
                continue;
            }

            var column = FindColumn(rule.ColumnKey);
            if (column is null)
                continue;

            var entry = _columnFormats.FirstOrDefault(e => ReferenceEquals(e.Column, column));
            if (entry is null)
            {
                entry = new CompiledColumnFormats { ColumnKey = rule.ColumnKey, Column = column };
                _columnFormats.Add(entry);
            }

            switch (rule)
            {
                case DataBarRule:
                    entry.DoubleReader ??= column.TryCreateDoubleReader();
                    entry.HasDataBar = entry.DoubleReader is not null; // non-numeric key ⇒ inert rule
                    break;

                case ColorScaleRule scale:
                    if (scale.Stops.Count is < 2 or > 3)
                        throw new ArgumentException($"A color scale needs 2 or 3 stops; got {scale.Stops.Count}.");
                    entry.DoubleReader ??= column.TryCreateDoubleReader();
                    if (entry.DoubleReader is not null)
                        entry.ColorScale = scale;
                    break;

                case ThresholdRule threshold:
                    foreach (var thresholdEntry in threshold.Entries)
                    {
                        // Each entry compiles through the column's typed condition builder — the
                        // FILTER lane, so literal conversion and null ordering can never drift. The
                        // SecondValue upper bound feeds Between (null for every other operator).
                        var slot = System.Linq.Expressions.Expression.Parameter(typeof(int), "slot");
                        var body = column.BuildConditionExpression(slot, thresholdEntry.Operator,
                                                                   thresholdEntry.Value, thresholdEntry.SecondValue);
                        entry.Thresholds.Add((
                            System.Linq.Expressions.Expression.Lambda<Func<int, bool>>(body, slot).Compile(),
                            thresholdEntry.Format));
                    }
                    break;

                case TopBottomRule topBottom:
                    // §9.5: the rank predicate binds the column's own sort comparison once; the
                    // threshold itself is per-publish state (EnsureFormatStats re-derives it
                    // through the TopK seam, so filter changes re-rank without a recompile).
                    entry.TopBottom.Add(new CompiledTopBottom
                    {
                        Rule = topBottom,
                        Qualifies = column.CreateRankPredicate(topBottom.Top),
                    });
                    break;
            }
        }
    }

    /// <summary>
    /// Refreshes the per-column stats blocks (min/max as double over the FILTERED sorted view —
    /// the same population the footer aggregates; §2.7) once per published snapshot version.
    /// </summary>
    private void EnsureFormatStats()
    {
        if (_statsVersion == _version)
            return;
        _statsVersion = _version;

        foreach (var entry in _columnFormats)
        {
            // §9.5: TopBottom thresholds re-derive per publish through the TopK seam (its own
            // per-version cache makes repeat reads cheap); the compiled rank predicates stand.
            foreach (var topBottom in entry.TopBottom)
            {
                topBottom.Valid = TryGetTopKThreshold(entry.ColumnKey, topBottom.Rule.Count,
                                                      topBottom.Rule.Percent, topBottom.Rule.Top,
                                                      out topBottom.Threshold);
            }

            if (entry.DoubleReader is not { } read || (!entry.HasDataBar && entry.ColorScale is null))
                continue;

            double min = double.NaN, max = double.NaN;
            for (int i = 0; i < _sortedLength; i++)
            {
                double v = read(_sortedView[i]);
                if (double.IsNaN(v))
                    continue; // null cells don't anchor the range (ignore-null, the aggregate convention)
                if (double.IsNaN(min) || v < min)
                    min = v;
                if (double.IsNaN(max) || v > max)
                    max = v;
            }
            entry.StatsMin = min;
            entry.StatsMax = max;
        }
    }

    private CompiledColumnFormats? FindFormats(object columnKey)
    {
        foreach (var entry in _columnFormats)
        {
            if (Equals(entry.ColumnKey, columnKey))
                return entry;
        }
        return null;
    }

    public override CellFormat GetCellFormat(int rowId, object columnKey)
    {
        var entry = FindFormats(columnKey);
        if (entry is null)
            return default;

        // Threshold entries: FIRST match wins (the authored priority order).
        CellFormat result = default;
        foreach (var (predicate, format) in entry.Thresholds)
        {
            if (predicate(rowId))
            {
                result = format;
                break;
            }
        }

        // §9.5: TopBottom qualifies when no explicit threshold claimed the cell (rule layering);
        // the rank predicate reads the per-publish threshold EnsureFormatStats derived.
        if (result.IsEmpty && entry.TopBottom.Count > 0)
        {
            EnsureFormatStats();
            foreach (var topBottom in entry.TopBottom)
            {
                if (topBottom.Valid && topBottom.Qualifies(rowId, topBottom.Threshold))
                {
                    result = topBottom.Rule.Format;
                    break;
                }
            }
        }

        // ColorScale fills the foreground only when no threshold claimed it (§2.7 layering).
        if (entry is { ColorScale: { } scale, DoubleReader: { } read } && result.Foreground is null)
        {
            EnsureFormatStats();
            double v = read(rowId);
            double range = entry.StatsMax - entry.StatsMin;
            if (!double.IsNaN(v) && !double.IsNaN(range))
            {
                double t = range <= 0 ? 1 : Math.Clamp((v - entry.StatsMin) / range, 0, 1);
                result = result with { Foreground = InterpolateStops(scale.Stops, t) };
            }
        }

        return result;
    }

    private static Cursorial.Output.Color InterpolateStops(IReadOnlyList<Cursorial.Output.Color> stops, double t)
    {
        // 2 stops: one segment; 3 stops: split at 0.5 (min→mid→max, the heat-scale convention).
        int segments = stops.Count - 1;
        double scaled = t * segments;
        int index = Math.Min(segments - 1, (int)scaled);
        double local = scaled - index;
        var (a, b) = (stops[index], stops[index + 1]);
        return Cursorial.Output.Color.FromRgb(
            (byte)Math.Round(a.Red + (b.Red - a.Red) * local),
            (byte)Math.Round(a.Green + (b.Green - a.Green) * local),
            (byte)Math.Round(a.Blue + (b.Blue - a.Blue) * local));
    }

    public override CellFormat GetRowFormat(int rowId)
    {
        foreach (var (predicate, format) in _rowFormatRules)
        {
            if (predicate(rowId))
                return format; // first match wins (authored priority)
        }
        return default;
    }

    public override double GetDataBarFraction(int rowId, object columnKey)
    {
        var entry = FindFormats(columnKey);
        if (entry is not { HasDataBar: true, DoubleReader: { } read })
            return double.NaN;

        EnsureFormatStats();
        double v = read(rowId);
        double range = entry.StatsMax - entry.StatsMin;
        if (double.IsNaN(v) || double.IsNaN(range))
            return double.NaN;
        return range <= 0 ? 1 : Math.Clamp((v - entry.StatsMin) / range, 0, 1);
    }

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
                    _addedSlotsThisBatch.Add(slot);
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
                RaiseRowsReset();
                return;
        }

        if (_removedSlotsThisBatch.Count > 0)
        {
            // Surface BEFORE the tick drains: the ids are dead the moment the event lands, so
            // id-keyed consumers prune before any slot-reusing insert can alias them.
            var removed = _removedSlotsThisBatch.ToArray();
            _removedSlotsThisBatch.Clear();
            RaiseRowsRemoved(removed);
        }

        if (_addedSlotsThisBatch.Count > 0)
        {
            var added = _addedSlotsThisBatch.ToArray();
            _addedSlotsThisBatch.Clear();
            RaiseRowsAdded(added);
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
        // A freed slot can recycle within one coalescing window (remove → insert): clear the stale
        // removed/dirty verdicts FIRST or the new row is suppressed by them and silently vanishes
        // from the view until an unrelated reshape (the final-audit critical — regression-tested).
        _removed.Remove(slot);
        _dirty.Remove(slot);
        _dirtyRows.Remove(slot);

        // Inserted rows enter through the dirty lane but are NOT in the current view — the repair
        // merge treats them as pure inserts (they're absent from the clean sweep by construction).
        if (_dirty.Add(slot))
            _dirtyRows.Add(slot);
    }

    private void MarkRemoved(int slot)
    {
        // Clear the dirty verdict WITH the queue entry — a stale dirty flag on a freed slot would
        // suppress the slot's next occupant (see MarkInserted).
        _dirty.Remove(slot);
        _dirtyRows.Remove(slot);
        _removed.Add(slot);
        _removedSlotsThisBatch.Add(slot);
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
        // The inline scheduler + the REAL ThreadPool runner is always a bug (the publish would run
        // on the pool thread — the final-audit critical): headless consumers without a scheduler
        // degrade to the synchronous lane instead; tests with an injected runner stay deterministic.
        if (_store.Count > BackgroundThreshold && (_scheduler is not InlineShapingScheduler || _customRunner))
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
        // §9.5 two-pass shape: DERIVE nodes/boundaries from the key-ordered view (the repair
        // substrate — never permuted), aggregate, project the sibling display order, then FLATTEN.
        ShapingGroups.Derive(_sortedView, _sortedLength, _groupColumns, _collapsedPaths, _groupBuffers);

        GroupNode[] nodes;
        int[] flat;
        int flatLength;
        bool grouped = _groupColumns.Length > 0;
        if (!grouped)
        {
            nodes = [];
            flat = _sortedView;
            flatLength = _sortedLength;
        }
        else
        {
            // Per-group summaries (v1: recompute per publish; membership-dirty tracking is a
            // recorded optimization — the walk is aggregate-loop bound, background-gated at scale).
            // Runs from the derive pass's key-ordered runs, BEFORE flatten (display order is
            // irrelevant to aggregation — the two-array discipline).
            if (_aggregators.Length > 0)
            {
                foreach (var node in _groupBuffers.Nodes)
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

            // The §9.5 projection: permute sibling segments by their summary values (per-publish;
            // the key-ordered view and the node array's document order are untouched).
            if (_hasGroupOrder)
                ApplyGroupOrderProjection();

            ShapingGroups.Flatten(_sortedView, _groupColumns.Length, _groupBuffers);

            nodes = _groupBuffers.Nodes.ToArray();
            flat = _groupBuffers.Flat;
            flatLength = _groupBuffers.FlatLength;
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
                                        grouped ? CopyFlat(flat, flatLength) : _sortedView,
                                        flatLength)
                   { DataRowLevel = _groupColumns.Length == 0 ? 0 : _groupColumns.Length };

        // §2.6 invariant 2, the PUBLISHED half (final-audit fix): freed slots release to the free
        // list only once a snapshot that excludes them has published — a published permutation can
        // never watch its slot's row swapped under it. The in-flight gate keeps them parked longer.
        if (!_inFlight)
            _store.ReleaseDeferredFrees();

        RaiseSnapshotChanged();
    }

    private static string[] _emptyStrings = [];

    private static int[] CopyFlat(int[] flat, int length)
    {
        var copy = new int[length];
        Array.Copy(flat, copy, length);
        return copy;
    }

    /// <summary>
    /// The §9.5 order-groups-by-summary projection (per publish, between derive and flatten):
    /// computes each node's ordering aggregate from its KEY-ORDERED run, then sorts every sibling
    /// segment of the derive pass's child-order substrate whose level carries an OrderBySummary kit.
    /// Ties keep key order (document order within a segment IS key order — the tiebreak is the node
    /// index, never direction-swapped); descending is an operand swap. The sorted view itself is
    /// never touched — it remains the incremental-repair substrate.
    /// </summary>
    private void ApplyGroupOrderProjection()
    {
        var buffers = _groupBuffers;
        var nodes = buffers.Nodes;
        if (nodes.Count == 0)
            return;

        if (_groupOrderValues.Length < nodes.Count)
            _groupOrderValues = new AggregateValue[Math.Max(nodes.Count, Math.Max(16, _groupOrderValues.Length * 2))];

        for (int n = 0; n < nodes.Count; n++)
        {
            var node = nodes[n];
            if (_groupOrderKits[node.Level] is { } kit)
                _groupOrderValues[n] = kit.Aggregator.Aggregate(_sortedView, node.SortedStart, node.RowCount);
        }

        _groupOrderComparer.Values = _groupOrderValues;

        if (_groupOrderKits[0] is { } rootKit && buffers.RootCount > 1)
        {
            _groupOrderComparer.Aggregator = rootKit.Aggregator;
            _groupOrderComparer.Descending = rootKit.Descending;
            Array.Sort(buffers.ChildOrder, 0, buffers.RootCount, _groupOrderComparer);
        }

        for (int n = 0; n < nodes.Count; n++)
        {
            int childLevel = nodes[n].Level + 1;
            if (childLevel >= _groupOrderKits.Length || _groupOrderKits[childLevel] is not { } kit ||
                buffers.ChildCount[n] <= 1)
            {
                continue;
            }

            _groupOrderComparer.Aggregator = kit.Aggregator;
            _groupOrderComparer.Descending = kit.Descending;
            Array.Sort(buffers.ChildOrder, buffers.ChildStart[n], buffers.ChildCount[n], _groupOrderComparer);
        }
    }

    /// <summary>The sibling-segment comparer (§9.5): summary value first (descending = operand swap),
    /// node index (document order == key order) as the never-swapped tie/total-order anchor — so the
    /// unstable Array.Sort is de-facto stable and ties keep key order.</summary>
    private sealed class GroupOrderComparer : IComparer<int>
    {
        public AggregateValue[] Values = [];
        public ColumnAggregator Aggregator = null!;
        public bool Descending;

        public int Compare(int x, int y)
        {
            int c = Descending
                ? Aggregator.CompareValues(Values[y], Values[x])
                : Aggregator.CompareValues(Values[x], Values[y]);
            return c != 0 ? c : x - y; // node indices are non-negative — no overflow
        }
    }

    // ── TopK stats (the §9.5 TopBottom conditional-format seam) ──────────────────────────────────

    private readonly Dictionary<(object ColumnKey, int Count, bool Percent, bool Top), (bool Found, object? Threshold)>
        _topKCache = new();
    private readonly Dictionary<object, int> _visibleNonNullCache = new();
    private int _topKVersion = -1;
    private int[] _topKScratch = [];

    /// <inheritdoc/>
    public override bool TryGetTopKThreshold(object columnKey, int count, bool percent, bool top, out object? threshold)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(columnKey);
        threshold = null;

        if (count <= 0)
            return false;
        var column = FindColumn(columnKey);
        if (column is null)
            return false;

        InvalidateStatsCachesIfStale();
        var cacheKey = (columnKey, count, percent, top);
        if (_topKCache.TryGetValue(cacheKey, out var cached))
        {
            threshold = cached.Threshold;
            return cached.Found;
        }

        int n = CollectVisibleNonNull(column);
        bool found = false;
        if (n > 0)
        {
            // K: direct, or ceil(percent% of the non-null population); clamps to the population
            // (K ≥ N ⇒ the threshold is the extreme value — every non-null cell qualifies).
            long k = percent ? (long)Math.Ceiling(count * (double)n / 100) : count;
            k = Math.Clamp(k, 1, n);

            // The K-th largest sits at sorted position n−K (ascending); the K-th smallest at K−1.
            int nth = top ? n - (int)k : (int)k - 1;
            ShapingStats.SelectNth(_topKScratch, n, nth, column.CompareSlots);
            threshold = column.GetKeyBoxed(_topKScratch[nth]);
            found = true;
        }

        _topKCache[cacheKey] = (found, threshold);
        return found;
    }

    /// <inheritdoc/>
    public override int VisibleNonNullCount(object columnKey)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(columnKey);

        var column = FindColumn(columnKey);
        if (column is null)
            return 0;

        InvalidateStatsCachesIfStale();
        if (_visibleNonNullCache.TryGetValue(columnKey, out int cached))
            return cached;

        int n = CollectVisibleNonNull(column);
        _visibleNonNullCache[columnKey] = n;
        return n;
    }

    /// <summary>Fills the TopK scratch with the visible (filtered) slots whose key is non-null for
    /// <paramref name="column"/>; returns the count.</summary>
    private int CollectVisibleNonNull(ShapedColumn column)
    {
        if (_topKScratch.Length < _sortedLength)
            _topKScratch = new int[Math.Max(_sortedLength, Math.Max(16, _topKScratch.Length * 2))];

        int n = 0;
        for (int i = 0; i < _sortedLength; i++)
        {
            int slot = _sortedView[i];
            if (!column.IsKeyNull(slot))
                _topKScratch[n++] = slot;
        }
        return n;
    }

    /// <summary>Per-publish cache invalidation (lazy, on read — the EnsureFormatStats pattern):
    /// TopK thresholds and non-null counts are valid for exactly one snapshot version.</summary>
    private void InvalidateStatsCachesIfStale()
    {
        if (_topKVersion == _version)
            return;
        _topKVersion = _version;
        _topKCache.Clear();
        _visibleNonNullCache.Clear();
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

        // §9.5: each grouped level's OrderBySummary aggregator compiles whether or not the same
        // summary is displayed (the projection reads it directly, not the display summaries).
        _groupOrderKits = new (ColumnAggregator, bool)?[_groups.Count];
        _hasGroupOrder = false;
        for (int i = 0; i < _groups.Count; i++)
        {
            if (_groups[i].OrderBySummary is not { } order)
                continue;
            _groupOrderKits[i] = (
                ColumnAggregator.Create(RequireColumn(order.ColumnKey), order.Aggregate, order.Format),
                _groups[i].SummaryDirection == SortDirection.Descending);
            _hasGroupOrder = true;
        }

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
                column.ExtractKey(row, slot); // typed — value-type rows never box on the extract path (§9.6)
        }
    }

    private void ExtractRowKeys(TRow row, int slot)
    {
        foreach (var (_, column) in _columns)
        {
            column.EnsureCapacity(_store.SlotCapacity);
            column.ExtractKey(row, slot);
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
        _removedSlotsThisBatch.Clear();
        _addedSlotsThisBatch.Clear();
        _sortedLength = 0;
        RaiseRowsReset();
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
