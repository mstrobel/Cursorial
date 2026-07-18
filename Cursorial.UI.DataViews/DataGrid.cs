using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

using Cursorial.Input;
using Cursorial.Rendering.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Input;

namespace Cursorial.UI.DataViews;

/// <summary>
/// The DevExpress-style data grid (design doc §3; visual spec
/// <c>tokyo-night-terminal-datagrid.html</c>): a non-generic <see cref="Control"/> over the typed
/// shaping engine — columns (explicit + auto-generated), multi-level sorting and grouping with
/// summaries, criteria-tree filtering, direct-drawn virtualized rows, row-id-keyed selection, and
/// in-row editing via the element-hosting special case. The observable
/// <see cref="SortDescriptions"/>/<see cref="GroupDescriptions"/> collections are the ONE source of
/// truth — gestures edit them, glyphs render them, persistence reads them (panel decision).
/// </summary>
[RequiresDynamicCode("The shaping engine compiles expression trees specialized to the row type.")]
public class DataGrid : Control
{
    /// <summary>The row source (any <see cref="IEnumerable"/>; INCC observed; the row type is discovered).</summary>
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        UIProperty.Register<DataGrid, IEnumerable?>(nameof(ItemsSource),
            changed: static (sender, _, _) => ((DataGrid)sender).RebuildController());

    /// <summary>Whether columns auto-generate from the row type's public properties when <see cref="Columns"/> is empty (design doc §1).</summary>
    public static readonly StyledProperty<bool> AutoGenerateColumnsProperty =
        UIProperty.Register<DataGrid, bool>(nameof(AutoGenerateColumns), true);

    /// <summary>Whether per-row INPC live updates are tracked (opt-out for static snapshots — §2.1).</summary>
    public static readonly StyledProperty<bool> LiveUpdatesProperty =
        UIProperty.Register<DataGrid, bool>(nameof(LiveUpdates), true);

    /// <summary>Whether the auto-filter row renders (default hidden — opt-in band, §3.4).</summary>
    public static readonly StyledProperty<bool> ShowAutoFilterRowProperty =
        UIProperty.Register<DataGrid, bool>(nameof(ShowAutoFilterRow),
            changed: static (sender, _, _) => ((DataGrid)sender).AutoFilterRow?.InvalidateMeasure());

    /// <summary>
    /// Whether the group panel renders (default hidden — the flip drops the whole band from
    /// measure, so grids that never group spend no row on the drag prompt; opt-in like the
    /// auto-filter row).
    /// </summary>
    public static readonly StyledProperty<bool> ShowGroupPanelProperty =
        UIProperty.Register<DataGrid, bool>(nameof(ShowGroupPanel),
            changed: static (sender, _, _) => ((DataGrid)sender).GroupPanel?.InvalidateMeasure());

    /// <summary>Whether the summary footer renders (auto-hides when no summaries are defined).</summary>
    public static readonly StyledProperty<bool> ShowSummaryFooterProperty =
        UIProperty.Register<DataGrid, bool>(nameof(ShowSummaryFooter), true);

    /// <summary>
    /// Whether the trailing new-row template renders (design doc §3.2 deferred-suite item; the
    /// mockup's <c>newrow</c>). Effective only when the source is a non-readonly, non-fixed
    /// <see cref="IList"/> AND a new row instance is constructible (an <see cref="AddingNewRow"/>
    /// handler, or a public parameterless constructor on the row type) — see
    /// <see cref="HasNewRowPlaceholder"/>.
    /// </summary>
    public static readonly StyledProperty<bool> AllowAddNewProperty =
        UIProperty.Register<DataGrid, bool>(nameof(AllowAddNew),
            changed: static (sender, _, _) => ((DataGrid)sender).RowsPresenter?.InvalidateBand());

    static DataGrid()
    {
        FocusableProperty.OverrideDefaultValue<DataGrid>(true);
        AffectsMeasure<DataGrid>(ItemsSourceProperty);
        AffectsRender<DataGrid>(ShowAutoFilterRowProperty, ShowGroupPanelProperty, ShowSummaryFooterProperty);
    }

    private DataViewController? _controller;
    private Type? _rowType;
    private bool _rowTypeHasParameterlessCtor;
    private bool _columnsFromAutoGeneration;
    private bool _shapePushScheduled;

    public DataGrid()
    {
        Columns = [];
        Columns.CollectionChanged += (_, e) => OnColumnsChanged(e);
        SortDescriptions = [];
        SortDescriptions.CollectionChanged += (_, _) => ScheduleShapePush();
        GroupDescriptions = [];
        GroupDescriptions.CollectionChanged += (_, _) =>
        {
            ScheduleShapePush();
            // The chips re-ink even when no controller reshapes (a grouping edit before a source
            // attaches must still surface on the panel).
            GroupPanel?.InvalidateMeasure();
            GroupPanel?.InvalidateVisual();
        };
        SummaryDescriptions = [];
        SummaryDescriptions.CollectionChanged += (_, _) => ScheduleShapePush();
    }

    /// <inheritdoc cref="ItemsSourceProperty"/>
    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <inheritdoc cref="AutoGenerateColumnsProperty"/>
    public bool AutoGenerateColumns
    {
        get => GetValue(AutoGenerateColumnsProperty);
        set => SetValue(AutoGenerateColumnsProperty, value);
    }

    /// <inheritdoc cref="LiveUpdatesProperty"/>
    public bool LiveUpdates
    {
        get => GetValue(LiveUpdatesProperty);
        set => SetValue(LiveUpdatesProperty, value);
    }

    /// <inheritdoc cref="ShowAutoFilterRowProperty"/>
    public bool ShowAutoFilterRow
    {
        get => GetValue(ShowAutoFilterRowProperty);
        set => SetValue(ShowAutoFilterRowProperty, value);
    }

    /// <inheritdoc cref="ShowGroupPanelProperty"/>
    public bool ShowGroupPanel
    {
        get => GetValue(ShowGroupPanelProperty);
        set => SetValue(ShowGroupPanelProperty, value);
    }

    /// <inheritdoc cref="ShowSummaryFooterProperty"/>
    public bool ShowSummaryFooter
    {
        get => GetValue(ShowSummaryFooterProperty);
        set => SetValue(ShowSummaryFooterProperty, value);
    }

    /// <inheritdoc cref="AllowAddNewProperty"/>
    public bool AllowAddNew
    {
        get => GetValue(AllowAddNewProperty);
        set => SetValue(AllowAddNewProperty, value);
    }

    /// <summary>
    /// Raised when the new-row template needs a fresh row instance (§3.2): set
    /// <see cref="AddingNewRowEventArgs.Item"/> to supply one (rows with constructor arguments);
    /// left null, the grid falls back to <see cref="Activator.CreateInstance(Type)"/> when the row
    /// type has a public parameterless constructor. Neither available ⇒ the template never renders.
    /// </summary>
    public event EventHandler<AddingNewRowEventArgs>? AddingNewRow;

    /// <summary>The columns (get-only — the XAML loader fills it; empty + <see cref="AutoGenerateColumns"/> ⇒ generated).</summary>
    public ObservableCollection<DataGridColumn> Columns { get; }

    /// <summary>The active sort levels (outermost first; gestures and code both edit HERE).</summary>
    public ObservableCollection<SortDescription> SortDescriptions { get; }

    /// <summary>The active grouping levels (chips render these; a group level is also a sort level).</summary>
    public ObservableCollection<GroupDescription> GroupDescriptions { get; }

    /// <summary>The summaries (footer + per-group).</summary>
    public ObservableCollection<SummaryDescription> SummaryDescriptions { get; }

    /// <summary>The programmatic filter tree (AND-composed with the filter surfaces' per-column fragments).</summary>
    public FilterNode? Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            ScheduleShapePush();
        }
    }

    private FilterNode? _filter;
    private readonly Dictionary<DataGridColumn, FilterNode> _columnFilters = [];

    /// <summary>The shaping controller (null before a source attaches). Exposed for headless/diagnostic use.</summary>
    public DataViewController? Controller => _controller;

    /// <summary>The published snapshot (empty before the first shape).</summary>
    public DataViewSnapshot Snapshot => _controller?.Snapshot ?? DataViewSnapshot.Empty;

    /// <summary>Raised after a new snapshot publishes (the presenters re-read).</summary>
    public event EventHandler? SnapshotChanged;

    /// <inheritdoc/>
    protected internal override bool HandlesScrolling => true;

    // ── Template parts (§3.1) ────────────────────────────────────────────────────────────────────

    public const string PartGroupPanel = "PART_GroupPanel";
    public const string PartHeader = "PART_Header";
    public const string PartAutoFilterRow = "PART_AutoFilterRow";
    public const string PartFooter = "PART_Footer";
    public const string PartScrollViewer = "PART_ScrollViewer";
    public const string PartRows = "PART_Rows";
    public const string PartEditBar = "PART_EditBar";

    private ScrollViewer? _scrollViewer;
    private DataGridHeaderPresenter? _header;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        GroupPanel = GetTemplatePart<DataGridGroupPanel>(PartGroupPanel);
        _header = GetTemplatePart<DataGridHeaderPresenter>(PartHeader);
        AutoFilterRow = GetTemplatePart<DataGridAutoFilterRow>(PartAutoFilterRow);
        var footer = GetTemplatePart<DataGridSummaryPresenter>(PartFooter);
        _scrollViewer = GetTemplatePart<ScrollViewer>(PartScrollViewer);
        RowsPresenter = GetTemplatePart<DataGridRowsPresenter>(PartRows);
        EditBar = GetTemplatePart<DataGridEditBar>(PartEditBar);

        if (RowsPresenter is not null)
            RowsPresenter.Owner = this;
        if (EditBar is not null)
            EditBar.Owner = this;
        if (GroupPanel is not null)
            GroupPanel.Owner = this;
        if (_header is not null)
        {
            _header.Owner = this;
            if (_scrollViewer is not null)
                _header.SetBinding(DataGridHeaderPresenter.HorizontalOffsetProperty,
                                   new Binding(nameof(ScrollViewer.HorizontalOffset)) { Source = _scrollViewer });
        }
        if (AutoFilterRow is not null)
        {
            AutoFilterRow.Owner = this;
            if (_scrollViewer is not null)
                AutoFilterRow.SetBinding(DataGridAutoFilterRow.HorizontalOffsetProperty,
                                         new Binding(nameof(ScrollViewer.HorizontalOffset)) { Source = _scrollViewer });
        }
        if (footer is not null)
        {
            footer.Owner = this;
            if (_scrollViewer is not null)
                footer.SetBinding(DataGridSummaryPresenter.HorizontalOffsetProperty,
                                  new Binding(nameof(ScrollViewer.HorizontalOffset)) { Source = _scrollViewer });
        }
    }

    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        _filterPopup?.Close(); // the popup anchors to template parts about to detach
        _columnChooser?.Close(); // ditto — its placement target is the header part
        if (RowsPresenter is not null)
            RowsPresenter.Owner = null;
        if (GroupPanel is not null)
            GroupPanel.Owner = null;
        if (AutoFilterRow is not null)
            AutoFilterRow.Owner = null;
        if (EditBar is not null)
            EditBar.Owner = null;
        _scrollViewer = null;
        _header = null;
        GroupPanel = null;
        AutoFilterRow = null;
        RowsPresenter = null;
        EditBar = null;
        base.OnTemplateDetaching(old);
    }

    // ── Source / controller lifecycle ────────────────────────────────────────────────────────────

    private void RebuildController()
    {
        _controller?.Dispose();
        _controller = null;
        _rowType = null;

        var source = ItemsSource;
        if (source is null)
        {
            RaiseSnapshotChanged();
            return;
        }

        _rowType = DiscoverRowType(source);
        _rowTypeHasParameterlessCtor = _rowType?.GetConstructor(Type.EmptyTypes) is not null;
        if (_rowType is null)
        {
            // An empty untyped source: no rows to discover from — stay dormant until items arrive
            // (an INCC source re-triggers via the reset heuristic below) or a typed source is set.
            RaiseSnapshotChanged();
            return;
        }

        // The owner-thread scheduler bridge (final-audit fix): without it the controller defaults
        // to the inline scheduler, and a background reshape past the threshold would complete ON the
        // ThreadPool thread (cross-thread mutation; the swallowed VerifyAccess froze the grid).
        // No ambient application (pure headless construction) falls back to inline — where the
        // controller also refuses the real-Task.Run background lane (see RunBackgroundReshape).
        var dispatcher = UIApplication.Current?.Dispatcher;
        _controller = DataViewController.Create(_rowType,
            dispatcher is null ? null : new DispatcherShapingScheduler(dispatcher));
        EnsureColumns();
        _controller.SetColumns(Columns.Where(c => c.FieldName is not null || c.KeySelector is not null)
                                      .Select(c => c.ToShapingDescription()).ToList());
        _controller.AttachSource(source, LiveUpdates);
        _controller.SnapshotChanged += (_, _) => RaiseSnapshotChanged();
        // Row-id hygiene (final-audit fix): removed ids leave the selection BEFORE their slots can
        // recycle onto new rows; a source reset clears id-keyed state wholesale.
        _controller.RowsRemoved += ids => RowSelection.HandleRowsRemoved(ids);
        _controller.RowsAdded += ids => RowSelection.HandleRowsAdded(ids);
        _controller.RowsReset += (_, _) => RowSelection.Clear();
        PushShape();
    }

    private void RaiseSnapshotChanged()
    {
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
        InvalidateMeasure();
    }

    /// <summary>Row type discovery: the source's <c>IEnumerable&lt;T&gt;</c> (first closed interface), else the first item's runtime type.</summary>
    private static Type? DiscoverRowType(IEnumerable source)
    {
        foreach (var candidate in source.GetType().GetInterfaces())
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                var arg = candidate.GetGenericArguments()[0];
                if (arg != typeof(object) && !arg.IsValueType)
                    return arg;
            }
        }

        foreach (var item in source)
            return item?.GetType();
        return null;
    }

    // ── Columns ──────────────────────────────────────────────────────────────────────────────────

    private void OnColumnsChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_columnsFromAutoGeneration)
            return; // our own generation pass — not an authoring change

        // A pure reorder (the header drag / ApplyColumnLayout) is LAYOUT-only: the shaping identity
        // is the column INSTANCE, so the engine's key vectors, sort/group/filter state, and collapse
        // paths are all order-blind — re-pushing SetColumns here would recompile every column and
        // run a full key re-extract + reshape for a change the snapshot cannot observe. Re-fill the
        // band caches (cell arrays index the Columns order) and re-ink instead.
        if (e.Action == NotifyCollectionChangedAction.Move)
        {
            NotifyColumnGeometryChanged();
            return;
        }

        if (_controller is not null)
        {
            _controller.SetColumns(Columns.Where(c => c.FieldName is not null || c.KeySelector is not null)
                                          .Select(c => c.ToShapingDescription()).ToList());
            PushShape();
        }

        InvalidateMeasure();
    }

    /// <summary>
    /// Auto-generation (design doc §1): when <see cref="Columns"/> is empty and
    /// <see cref="AutoGenerateColumns"/>, public instance properties of the row type generate
    /// columns in declaration order; <c>[Browsable(false)]</c> skips; numerics right-align.
    /// </summary>
    private void EnsureColumns()
    {
        if (Columns.Count > 0 || !AutoGenerateColumns || _rowType is null)
            return;

        _columnsFromAutoGeneration = true;
        try
        {
            foreach (var property in _rowType.GetProperties(System.Reflection.BindingFlags.Public |
                                                            System.Reflection.BindingFlags.Instance))
            {
                if (property.GetMethod is null || property.GetIndexParameters().Length > 0)
                    continue;
                if (property.GetCustomAttributes(typeof(BrowsableAttribute), inherit: true) is
                    [BrowsableAttribute { Browsable: false }, ..])
                    continue;

                var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                bool numeric = type == typeof(int) || type == typeof(long) || type == typeof(short) ||
                               type == typeof(byte) || type == typeof(double) || type == typeof(float) ||
                               type == typeof(decimal) || type == typeof(uint) || type == typeof(ulong) ||
                               type == typeof(ushort);

                Columns.Add(new DataGridColumn
                {
                    FieldName = property.Name,
                    TextAlignment = numeric ? TextAlignment.Right : TextAlignment.Left,
                });
            }
        }
        finally
        {
            _columnsFromAutoGeneration = false;
        }
    }

    // ── Shape push (the one funnel from the observable state into the engine) ────────────────────

    private void ScheduleShapePush()
    {
        if (_controller is null || _shapePushScheduled)
            return;

        // Coalesce burst edits (a gesture may rewrite several descriptions); the dispatcher job
        // runs before layout next frame. Without an application (headless engine tests), push inline.
        var dispatcher = UIApplication.Current?.Dispatcher;
        if (dispatcher is null)
        {
            PushShape();
            return;
        }

        _shapePushScheduled = true;
        dispatcher.Post(() =>
        {
            _shapePushScheduled = false;
            PushShape();
        });
    }

    /// <summary>Pushes sort/group/summaries/filter/format-rules into the controller (one atomic reshape).</summary>
    private void PushShape()
    {
        if (_controller is null)
            return;

        // Format rules ride every shape push (a plain IList carries no change notification — the
        // shape push is the one sanctioned re-read point; SetFormatRules itself never publishes,
        // the SetShape below does, and the engine short-circuits an unchanged rule sequence).
        _controller.SetFormatRules(CollectFormatRules());

        FilterNode? effective = BuildEffectiveFilter();
        _controller.SetShape(SortDescriptions.ToList(), GroupDescriptions.ToList(),
                             SummaryDescriptions.ToList(), effective);
    }

    private List<FormatRule> CollectFormatRules()
    {
        var rules = new List<FormatRule>();
        foreach (var column in Columns)
            rules.AddRange(column.FormatRules);
        return rules;
    }

    /// <summary>
    /// Re-collects the columns' <see cref="DataGridColumn.FormatRules"/> into the engine and
    /// re-inks the band — for rules edited AFTER the last shape push (the rule lists are plain
    /// collections with no change notification; every shape push re-collects automatically).
    /// </summary>
    public void RefreshFormatRules()
    {
        if (_controller is null)
            return;
        _controller.SetFormatRules(CollectFormatRules());
        RowsPresenter?.InvalidateBand();
    }

    /// <summary>AND-composes the programmatic tree with the per-column filter-surface fragments (§3.4).</summary>
    private FilterNode? BuildEffectiveFilter()
    {
        if (_columnFilters.Count == 0)
            return _filter;

        var parts = new List<FilterNode>();
        if (_filter is not null)
            parts.Add(_filter);
        parts.AddRange(_columnFilters.Values);
        return parts.Count == 1 ? parts[0] : FilterNode.And(parts.ToArray());
    }

    /// <summary>Sets/clears one column's filter fragment (the checklist popup + auto-filter row write here).</summary>
    public void SetColumnFilter(DataGridColumn column, FilterNode? fragment)
        => SetColumnFilter(column, fragment, summary: null);

    /// <summary>
    /// The surface-internal overload carrying the display SUMMARY the auto-filter row draws for the
    /// active condition (the typed grammar text / the checklist's selected-value digest) — pure
    /// presentation state beside the fragment, cleared with it.
    /// </summary>
    internal void SetColumnFilter(DataGridColumn column, FilterNode? fragment, string? summary)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (fragment is null)
        {
            _columnFilters.Remove(column);
            _columnFilterSummaries.Remove(column);
        }
        else
        {
            _columnFilters[column] = fragment;
            if (summary is not null)
                _columnFilterSummaries[column] = summary;
            else
                _columnFilterSummaries.Remove(column);
        }
        ScheduleShapePush();
        AutoFilterRow?.InvalidateVisual();
    }

    /// <summary>Whether a column currently carries a filter fragment (the header's amber ▾ cue).</summary>
    public bool HasColumnFilter(DataGridColumn column) => _columnFilters.ContainsKey(column);

    /// <summary>The column's active fragment (the popup pre-selects from an InSet), or null.</summary>
    internal FilterNode? GetColumnFilter(DataGridColumn column)
        => _columnFilters.GetValueOrDefault(column);

    /// <summary>The active condition's display text for the auto-filter row's well, or null.</summary>
    internal string? GetColumnFilterSummary(DataGridColumn column)
        => _columnFilterSummaries.GetValueOrDefault(column);

    private readonly Dictionary<DataGridColumn, string> _columnFilterSummaries = [];

    // ── Sort/group gesture surface (the keyboard/mouse handlers call these — §3.3) ───────────────

    /// <summary>
    /// The header-click / Enter gesture: cycles the column asc → desc → none as the REPLACING sort.
    /// </summary>
    public void CycleSort(DataGridColumn column)
    {
        if (!column.AllowSort)
            return;

        var existing = SortDescriptions.FirstOrDefault(s => ReferenceEquals(s.ColumnKey, column));
        var next = existing == default
            ? SortDescription.Ascending(column)
            : existing.Direction == SortDirection.Ascending
                ? SortDescription.Descending(column)
                : default;

        SortDescriptions.Clear();
        if (next != default)
            SortDescriptions.Add(next);
    }

    /// <summary>
    /// The Shift+click / Space gesture: appends the column as an additional sort level (or cycles
    /// its direction / removes it when already appended) without disturbing other levels.
    /// </summary>
    public void AppendSort(DataGridColumn column)
    {
        if (!column.AllowSort)
            return;

        for (int i = 0; i < SortDescriptions.Count; i++)
        {
            if (ReferenceEquals(SortDescriptions[i].ColumnKey, column))
            {
                if (SortDescriptions[i].Direction == SortDirection.Ascending)
                    SortDescriptions[i] = SortDescription.Descending(column);
                else
                    SortDescriptions.RemoveAt(i);
                return;
            }
        }

        SortDescriptions.Add(SortDescription.Ascending(column));
    }

    /// <summary>Adds a grouping level (Ctrl+G / API); no-op when already grouped by the column.</summary>
    public void GroupBy(DataGridColumn column)
    {
        if (!column.AllowGroup || GroupDescriptions.Any(g => ReferenceEquals(g.ColumnKey, column)))
            return;
        GroupDescriptions.Add(new GroupDescription(column));
    }

    /// <summary>Removes a grouping level (the chip ✕ / Delete).</summary>
    public void Ungroup(DataGridColumn column)
    {
        for (int i = 0; i < GroupDescriptions.Count; i++)
        {
            if (ReferenceEquals(GroupDescriptions[i].ColumnKey, column))
            {
                GroupDescriptions.RemoveAt(i);
                return;
            }
        }
    }

    /// <summary>The sort direction glyph state for a column header (▲/▼/none + the multi-sort ordinal).</summary>
    public (SortDirection? Direction, int Ordinal) GetSortState(DataGridColumn column)
    {
        // Group levels are sort levels too — the header shows their direction as well.
        for (int i = 0; i < GroupDescriptions.Count; i++)
        {
            if (ReferenceEquals(GroupDescriptions[i].ColumnKey, column))
                return (GroupDescriptions[i].Direction, i);
        }
        for (int i = 0; i < SortDescriptions.Count; i++)
        {
            if (ReferenceEquals(SortDescriptions[i].ColumnKey, column))
                return (SortDescriptions[i].Direction, GroupDescriptions.Count + i);
        }
        return (null, -1);
    }

    /// <summary>Toggles a group row's collapse state (the expander gesture).</summary>
    public void ToggleGroup(GroupNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _controller?.SetCollapsed(node.PathKey, !node.IsCollapsed);
    }

    // ── Selection + focus (row-id keyed — §3.3) ──────────────────────────────────────────────────

    /// <summary>The row-id-keyed selection (design doc §3.3 — survives every reshape untouched).</summary>
    public DataGridSelectionController RowSelection { get; } = new();

    /// <summary>The focus row in VIEW space (−1 none; re-anchored per snapshot by row id when possible).</summary>
    public int FocusViewIndex { get; private set; } = -1;

    /// <summary>The focus cell's visible-column index (−1 = whole-row focus).</summary>
    public int FocusColumnIndex { get; private set; } = -1;

    private int _selectionAnchorViewIndex = -1;

    /// <summary>The rows presenter (stamped when the template applies; hit/paint state flows through it).</summary>
    internal DataGridRowsPresenter? RowsPresenter { get; set; }

    /// <summary>The group panel band (stamped when the template applies).</summary>
    internal DataGridGroupPanel? GroupPanel { get; private set; }

    /// <summary>The auto-filter band (stamped when the template applies).</summary>
    internal DataGridAutoFilterRow? AutoFilterRow { get; private set; }

    /// <summary>The edit action bar band (stamped when the template applies; visible only while editing — §3.2).</summary>
    internal DataGridEditBar? EditBar { get; private set; }

    /// <summary>The rows presenter's editing-state fan-out (the edit bar bands in/out on it).</summary>
    internal void NotifyEditingChanged()
    {
        EditBar?.InvalidateMeasure();
        EditBar?.InvalidateVisual();
    }

    /// <summary>
    /// The presenter's press gesture (mouse): expander toggles; data rows select per modifiers
    /// (plain replace / Ctrl toggle / Shift range from the anchor — ranges resolve to row ids at
    /// gesture time against the current snapshot, then live as ids).
    /// </summary>
    internal void HandleRowPress(int viewIndex, int columnIndex, bool onExpander, KeyModifiers modifiers, int clickCount)
    {
        Focus(FocusNavigationMethod.Pointer);

        var snapshot = Snapshot;
        if (viewIndex < 0 || viewIndex >= snapshot.Count)
        {
            // The new-row template (view index == Count): focus like a data row, edit on
            // double-click; it never selects (not a snapshot row — §3.2).
            if (viewIndex == snapshot.Count && HasNewRowPlaceholder)
            {
                FocusViewIndex = viewIndex;
                FocusColumnIndex = columnIndex;
                RowsPresenter?.InvalidateBand();
                if (clickCount >= 2)
                    BeginEdit();
            }
            return;
        }

        var row = snapshot.GetRow(viewIndex);
        if (row.IsGroup)
        {
            if (onExpander || clickCount >= 2)
                ToggleGroup(snapshot.Groups[row.GroupNodeIndex]);
            FocusViewIndex = viewIndex;
            FocusColumnIndex = -1;
            RowsPresenter?.InvalidateBand();
            return;
        }

        FocusViewIndex = viewIndex;
        FocusColumnIndex = columnIndex;

        if ((modifiers & KeyModifiers.Shift) != 0 && _selectionAnchorViewIndex >= 0)
        {
            SelectViewRange(_selectionAnchorViewIndex, viewIndex, additive: (modifiers & KeyModifiers.Control) != 0);
        }
        else if ((modifiers & KeyModifiers.Control) != 0)
        {
            RowSelection.Toggle(row.RowId);
            _selectionAnchorViewIndex = viewIndex;
        }
        else
        {
            RowSelection.SelectOnly(row.RowId);
            _selectionAnchorViewIndex = viewIndex;
        }

        RowsPresenter?.InvalidateBand();

        // Double-click begins editing the pressed cell (the DevExpress gesture; §3.2).
        if (clickCount >= 2)
            BeginEdit();
    }

    /// <summary>Resolves a view range to row ids at gesture time (§3.3) and applies it.</summary>
    private void SelectViewRange(int from, int to, bool additive)
    {
        var snapshot = Snapshot;
        if (from > to)
            (from, to) = (to, from);
        from = Math.Clamp(from, 0, Math.Max(0, snapshot.Count - 1));
        to = Math.Clamp(to, 0, Math.Max(0, snapshot.Count - 1));

        var ids = new List<int>(to - from + 1);
        int lead = -1;
        for (int i = from; i <= to; i++)
        {
            var row = snapshot.GetRow(i);
            if (!row.IsGroup)
            {
                ids.Add(row.RowId);
                lead = row.RowId;
            }
        }

        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ids);
        if (additive)
            RowSelection.AddRange(span, lead);
        else
            RowSelection.SelectRange(span, lead);
    }

    /// <summary>Sets the focus cell programmatically (view-space row + visible-column index).</summary>
    public void SetFocusCell(int viewIndex, int columnIndex)
    {
        var snapshot = Snapshot;
        FocusViewIndex = Math.Clamp(viewIndex, -1, Math.Max(-1, snapshot.Count - 1));
        int visible = RowsPresenter?.ColumnLayout.Entries.Count ?? Columns.Count(c => c.Visible);
        FocusColumnIndex = Math.Clamp(columnIndex, -1, Math.Max(-1, visible - 1));
        RowsPresenter?.InvalidateBand();
    }

    // ── In-cell editing (§3.2 — the owner mandate; the per-kind editor suite over the v1 host) ───

    /// <summary>The new row instance under edit on the new-row template (null outside new-row entry — §3.2).</summary>
    private object? _pendingNewRow;

    /// <summary>
    /// Whether the trailing new-row template is live (§3.2): <see cref="AllowAddNew"/> is on, the
    /// source is a growable <see cref="IList"/>, the controller exists, AND a fresh instance is
    /// constructible (an <see cref="AddingNewRow"/> handler or a public parameterless ctor —
    /// neither ⇒ the template does not render). The template's view index == <c>Snapshot.Count</c>,
    /// one PAST the real rows — every snapshot read against it must stay guarded.
    /// </summary>
    internal bool HasNewRowPlaceholder =>
        AllowAddNew && _controller is not null &&
        ItemsSource is IList { IsReadOnly: false, IsFixedSize: false } &&
        (AddingNewRow is not null || _rowTypeHasParameterlessCtor);

    /// <summary>Whether the focus row is the new-row template (the placeholder guard the gestures share).</summary>
    private bool FocusOnNewRowPlaceholder => HasNewRowPlaceholder && FocusViewIndex == Snapshot.Count;

    /// <summary>
    /// Resolves a column's effective editor kind (§3.2): an authored kind wins;
    /// <see cref="DataGridEditorKind.Auto"/> maps the column's CLR key type — bool/enum →
    /// <see cref="DataGridEditorKind.Combo"/>, DateOnly/DateTime → <see cref="DataGridEditorKind.Date"/>,
    /// numeric → <see cref="DataGridEditorKind.Spin"/>, everything else (string included) →
    /// <see cref="DataGridEditorKind.Text"/>.
    /// </summary>
    internal DataGridEditorKind ResolveEditorKind(DataGridColumn column)
    {
        var kind = column.EditorKind;
        if (kind != DataGridEditorKind.Auto)
            return kind;

        var keyType = _controller?.GetColumnKeyType(column);
        if (keyType is null)
            return DataGridEditorKind.Text;

        var type = Nullable.GetUnderlyingType(keyType) ?? keyType;
        if (type == typeof(bool) || type.IsEnum)
            return DataGridEditorKind.Combo;
        if (type == typeof(DateOnly) || type == typeof(DateTime))
            return DataGridEditorKind.Date;
        if (IsNumericKeyType(type))
            return DataGridEditorKind.Spin;
        return DataGridEditorKind.Text;
    }

    private static bool IsNumericKeyType(Type type)
        => type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
           type == typeof(sbyte) || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) ||
           type == typeof(double) || type == typeof(float) || type == typeof(decimal);

    /// <summary>
    /// The Combo kind's item list, keyed by the column's type (§3.2): enum → the declared names
    /// (they round-trip through <c>Enum.Parse(ignoreCase)</c> in the engine's literal ladder);
    /// bool → True/False (the formatter's own spelling); anything else (string columns) → the
    /// column's distinct formatted values (the checklist popup's source, blanks elided — a combo
    /// pick must always be a committable value).
    /// </summary>
    private IReadOnlyList<string> BuildComboItems(DataGridColumn column)
    {
        var keyType = _controller?.GetColumnKeyType(column);
        var type = keyType is null ? null : Nullable.GetUnderlyingType(keyType) ?? keyType;
        if (type is { IsEnum: true })
            return Enum.GetNames(type);
        if (type == typeof(bool))
            return [bool.TrueString, bool.FalseString];
        return _controller?.GetDistinctValues(column)
                   .Where(v => v.Formatted.Length > 0)
                   .Select(v => v.Formatted)
                   .ToList()
               ?? (IReadOnlyList<string>)[];
    }

    /// <summary>
    /// Begins editing the focused cell (F2/Enter/double-click); no-op on read-only /
    /// <see cref="DataGridEditorKind.None"/> columns. On the new-row template it creates the
    /// pending instance and hosts the editor on the FIRST editable column (§3.2).
    /// </summary>
    public void BeginEdit()
    {
        var presenter = RowsPresenter;
        var snapshot = Snapshot;
        if (presenter is null || _controller is null || FocusViewIndex < 0)
            return;

        if (FocusOnNewRowPlaceholder)
        {
            BeginNewRowEdit(presenter);
            return;
        }

        if (FocusViewIndex >= snapshot.Count)
            return;

        var row = snapshot.GetRow(FocusViewIndex);
        if (row.IsGroup)
            return;

        int columnIndex = Math.Max(0, FocusColumnIndex);
        var entries = presenter.ColumnLayout.Entries;
        if (columnIndex >= entries.Count)
            return;

        var column = entries[columnIndex].Column;
        var kind = ResolveEditorKind(column);
        if (kind == DataGridEditorKind.None || !_controller.IsColumnEditable(column))
            return;

        FocusColumnIndex = columnIndex;
        presenter.BeginEdit(FocusViewIndex, columnIndex, kind, _controller.FormatCell(row.RowId, column),
                            kind == DataGridEditorKind.Combo ? BuildComboItems(column) : null);
    }

    /// <summary>
    /// The new-row entry path (§3.2, deliberately one-cell-at-a-time like the rest of the grid):
    /// creates the pending instance (<see cref="AddingNewRow"/> first, the parameterless-ctor
    /// fallback second) and hosts the editor on the first editable column with EMPTY initial text
    /// (the ghost row has no cell values yet).
    /// </summary>
    private void BeginNewRowEdit(DataGridRowsPresenter presenter)
    {
        var entries = presenter.ColumnLayout.Entries;
        int columnIndex = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            if (ResolveEditorKind(entries[i].Column) != DataGridEditorKind.None &&
                _controller!.IsColumnEditable(entries[i].Column))
            {
                columnIndex = i;
                break;
            }
        }
        if (columnIndex < 0)
            return; // no editable column — nothing the template could write

        var args = new AddingNewRowEventArgs();
        AddingNewRow?.Invoke(this, args);
        object? item = args.Item;
        if (item is null && _rowTypeHasParameterlessCtor && _rowType is { } rowType)
            item = Activator.CreateInstance(rowType);
        if (item is null)
            return;

        _pendingNewRow = item;
        FocusViewIndex = Snapshot.Count;
        FocusColumnIndex = columnIndex;

        var column = entries[columnIndex].Column;
        var kind = ResolveEditorKind(column);
        presenter.BeginEdit(FocusViewIndex, columnIndex, kind, string.Empty,
                            kind == DataGridEditorKind.Combo ? BuildComboItems(column) : null);
    }

    /// <summary>
    /// Commits the hosted editor's per-kind value through the compiled setter; keeps editing (with
    /// the error look) on parse failure. A pending new row writes the cell onto the un-stored
    /// instance THEN lands via <c>source.Add</c> — INCC inserts it into the view, where it re-sorts
    /// into position (§3.2).
    /// </summary>
    public bool CommitEdit()
    {
        var presenter = RowsPresenter;
        if (presenter is null || !presenter.IsEditing || _controller is null)
            return false;

        var (viewIndex, columnIndex) = presenter.EditCell;
        if (!presenter.TryGetEditorText(out string? text))
            return false; // nothing committable yet (a combo with no selection) — keep editing

        var column = presenter.ColumnLayout.Entries[columnIndex].Column;

        if (_pendingNewRow is { } newRow)
        {
            if (!_controller.TrySetRowText(newRow, column, text))
            {
                presenter.FlagEditorError(); // the ed-err look; the editor stays open for correction
                return false;
            }

            _pendingNewRow = null;
            presenter.EndEditVisual(); // tear down BEFORE the reshape moves the view under the host
            var list = (IList)ItemsSource!;
            list.Add(newRow);
            // A plain (non-INCC) IList carries no change wire — the one sanctioned refresh is a
            // cold re-attach (rebuild + reshape; acceptable for the add-a-row cold path).
            if (list is not INotifyCollectionChanged)
                _controller.AttachSource(list, LiveUpdates);
            return true;
        }

        var snapshot = Snapshot;
        if (viewIndex >= snapshot.Count)
        {
            presenter.EndEditVisual();
            return false;
        }

        var row = snapshot.GetRow(viewIndex);
        if (!_controller.TrySetCellFromText(row.RowId, column, text))
        {
            presenter.FlagEditorError(); // unparseable — the editor stays open for correction
            return false;
        }

        presenter.EndEditVisual();
        return true;
    }

    /// <summary>Cancels the hosted editor without writing (a pending new-row instance is discarded).</summary>
    public void CancelEdit()
    {
        _pendingNewRow = null;
        RowsPresenter?.EndEditVisual();
    }

    /// <summary>The edit bar's caption value: the edit row's first visible column, formatted ("(new)" on the template).</summary>
    internal string EditCaption()
    {
        var presenter = RowsPresenter;
        if (presenter is null || !presenter.IsEditing || _controller is null)
            return string.Empty;
        if (_pendingNewRow is not null)
            return "(new)";

        var snapshot = Snapshot;
        var (viewIndex, _) = presenter.EditCell;
        if (viewIndex < 0 || viewIndex >= snapshot.Count || presenter.ColumnLayout.Entries.Count == 0)
            return string.Empty;

        var row = snapshot.GetRow(viewIndex);
        return row.IsGroup ? string.Empty : _controller.FormatCell(row.RowId, presenter.ColumnLayout.Entries[0].Column);
    }

    /// <summary>
    /// The tunnel leg of the editing key contract (§3.2): drop-down editors consume Enter/Esc in
    /// their own class handlers while CLOSED (a closed ComboBox OPENS on Enter; an editable
    /// DatePicker parses its draft and swallows it), so the edit bar's Enter-commit / Esc-cancel
    /// can never ride the bubble for them — intercept on the way down instead. An OPEN drop-down
    /// owns its keys (Enter = pick the highlighted item, Esc = close the list), so the intercept
    /// applies only while closed; the pick's close then makes the NEXT Enter the commit.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || RowsPresenter is not { IsEditing: true } editing)
            return;
        if (editing.EditorKind is not (DataGridEditorKind.Combo or DataGridEditorKind.Date) ||
            editing.IsEditorDropDownOpen)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                CommitEdit();
                e.Handled = true;
                break;
            case Key.Escape:
                CancelEdit();
                e.Handled = true;
                break;
        }
    }

    /// <summary>The keyboard navigation surface (§3.3 — legacy-safe gestures).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        // While the checklist popup is open its content owns the keyboard: the popup's route
        // crosses back through its anchor into the grid, and a search-box Space / checklist arrow
        // must not select rows here — unhandled arrows fall through to the dispatcher's directional
        // navigation inside the popup instead (§3.4).
        if (ActiveFilterPopup is not null)
            return;

        // Same contract for the roving auto-filter editor: its row already took Enter/Esc in the
        // bubble; every other key (a typed space, Ctrl+C, arrows) belongs to the TextBox, not the
        // grid's row gestures.
        if (AutoFilterRow is { IsEditing: true })
            return;

        // Editing mode owns Enter/Esc/Tab (the edit bar's contract — commit/cancel/next-cell) plus
        // the Spin kind's Up/Down stepping; everything else stays with the hosted editor. (The
        // drop-down kinds' Enter/Esc arrive via the OnPreviewKeyDown tunnel intercept instead —
        // their class handlers would consume the bubble.)
        if (RowsPresenter is { IsEditing: true } editing)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    CommitEdit();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    CancelEdit();
                    e.Handled = true;
                    return;
                case Key.UpArrow or Key.DownArrow when editing.EditorKind == DataGridEditorKind.Spin:
                    // The mockup's spinbtns: ±1, Shift ±10 (single-line TextBox leaves Up/Down unhandled).
                    editing.SpinBy((e.Key == Key.UpArrow ? 1m : -1m) *
                                   ((e.Modifiers & KeyModifiers.Shift) != 0 ? 10m : 1m));
                    e.Handled = true;
                    return;
                case Key.Tab:
                {
                    // The new-row template is one-cell-at-a-time (§3.2): Tab = commit (the added
                    // row re-sorts away from the template — there is no stable "next cell" to
                    // advance into), never a cross-placeholder advance.
                    if (_pendingNewRow is not null)
                    {
                        CommitEdit();
                        e.Handled = true;
                        return;
                    }

                    if (CommitEdit())
                    {
                        int visible = editing.ColumnLayout.Entries.Count;
                        bool back = (e.Modifiers & KeyModifiers.Shift) != 0;
                        FocusColumnIndex = back
                            ? Math.Max(0, FocusColumnIndex - 1)
                            : Math.Min(Math.Max(0, visible - 1), FocusColumnIndex + 1);
                        BeginEdit();
                    }
                    e.Handled = true;
                    return;
                }
                default:
                    return; // the editor owns the rest
            }
        }

        // F2 (or Enter on a data row — handled below with the group-row Enter) begins editing.
        if (e.Key == Key.F2)
        {
            BeginEdit();
            e.Handled = true;
            return;
        }

        var snapshot = Snapshot;
        // The new-row template extends the navigable range one PAST the real rows (its ghost row
        // is focusable/clickable like a data row — §3.2), and an EMPTY AllowAddNew grid still
        // navigates to it (the only row there is).
        int lastNavigable = snapshot.Count - 1 + (HasNewRowPlaceholder ? 1 : 0);
        if (lastNavigable < 0)
            return;

        int current = Math.Clamp(FocusViewIndex < 0 ? 0 : FocusViewIndex, 0, lastNavigable);
        int viewport = RowsPresenter is { } presenter ? Math.Max(1, presenter.PageStep(0, 1, vertical: true)) : 10;
        bool shift = (e.Modifiers & KeyModifiers.Shift) != 0;
        bool ctrl = (e.Modifiers & KeyModifiers.Control) != 0;

        int? target = e.Key switch
        {
            Key.UpArrow => Math.Max(0, current - 1),
            Key.DownArrow => Math.Min(lastNavigable, current + 1),
            Key.PageUp => Math.Max(0, current - viewport),
            Key.PageDown => Math.Min(lastNavigable, current + viewport),
            Key.Home when ctrl => 0,
            Key.End when ctrl => lastNavigable,
            _ => null,
        };

        if (target is { } t)
        {
            MoveFocusRow(t, shift, ctrl);
            e.Handled = true;
            return;
        }

        // The placeholder is NOT a snapshot row: `focused` stays default there, and every gesture
        // that consumes a RowId must exclude it (default.RowId == 0 would alias a real row).
        bool onPlaceholder = FocusOnNewRowPlaceholder;
        var focused = !onPlaceholder && FocusViewIndex >= 0 && FocusViewIndex < snapshot.Count
            ? snapshot.GetRow(FocusViewIndex)
            : default;

        switch (e.Key)
        {
            // Group rows: Left collapses, Right expands, Enter toggles (§3.3).
            case Key.LeftArrow or Key.RightArrow or Key.Enter when focused.IsGroup:
            {
                var node = snapshot.Groups[focused.GroupNodeIndex];
                bool collapse = e.Key == Key.LeftArrow || (e.Key == Key.Enter && !node.IsCollapsed);
                if (node.IsCollapsed != collapse)
                    _controller?.SetCollapsed(node.PathKey, collapse);
                e.Handled = true;
                return;
            }

            // Data rows: Left/Right move the focus cell.
            case Key.LeftArrow when !focused.IsGroup:
                FocusColumnIndex = Math.Max(0, (FocusColumnIndex < 0 ? 0 : FocusColumnIndex) - 1);
                RowsPresenter?.InvalidateBand();
                e.Handled = true;
                return;

            case Key.RightArrow when !focused.IsGroup:
            {
                int visible = Columns.Count(c => c.Visible);
                FocusColumnIndex = Math.Min(Math.Max(0, visible - 1), FocusColumnIndex + 1);
                RowsPresenter?.InvalidateBand();
                e.Handled = true;
                return;
            }

            // Enter on a data row (or the new-row template) begins editing the focused cell (§3.2).
            case Key.Enter when onPlaceholder || (!focused.IsGroup && focused.RowId >= 0):
                BeginEdit();
                e.Handled = RowsPresenter is { IsEditing: true }; // read-only cells leave Enter unhandled
                return;

            // Ctrl+C — copy the selected rows as TSV (formatted values, visible columns; the
            // terminal's native selection is unavailable under mouse tracking, so the grid provides
            // extraction — §1 [panel]).
            case Key.Character when ctrl && e.Text.Length == 1 && (e.Text.Span[0] is 'c' or 'C'):
                CopySelectionToClipboard();
                e.Handled = true;
                return;

            // Ctrl+A — select all (the compact inversion).
            case Key.Character when ctrl && e.Text.Length == 1 && (e.Text.Span[0] is 'a' or 'A'):
                RowSelection.SelectAll();
                RowsPresenter?.InvalidateBand();
                e.Handled = true;
                return;

            // Space selects (the modifier-free wire is (Character, " ") — ND10). Never on the
            // placeholder: its default `focused` carries RowId 0, which would alias a real row.
            case Key.Character or Key.Space when IsSpace(e) && !onPlaceholder && !focused.IsGroup && focused.RowId >= 0:
                if (ctrl)
                    RowSelection.Toggle(focused.RowId);
                else
                    RowSelection.SelectOnly(focused.RowId);
                _selectionAnchorViewIndex = FocusViewIndex;
                RowsPresenter?.InvalidateBand();
                e.Handled = true;
                return;
        }
    }

    private static bool IsSpace(KeyEventArgs e)
        => e.Key == Key.Space || (e is { Key: Key.Character, Text.Length: 1 } && e.Text.Span[0] == ' ');

    private void MoveFocusRow(int target, bool shift, bool ctrl)
    {
        var snapshot = Snapshot;
        FocusViewIndex = target;

        // The new-row template: focus lands, nothing selects (it is not a snapshot row — §3.2).
        if (target >= snapshot.Count)
        {
            ScrollRowIntoView(target);
            RowsPresenter?.InvalidateBand();
            return;
        }

        var row = snapshot.GetRow(target);
        if (!row.IsGroup)
        {
            if (shift && _selectionAnchorViewIndex >= 0)
            {
                SelectViewRange(_selectionAnchorViewIndex, target, additive: false);
            }
            else if (!ctrl)
            {
                RowSelection.SelectOnly(row.RowId);
                _selectionAnchorViewIndex = target;
            }
            // Ctrl+move: focus travels without selecting (the DevExpress/ListBox idiom).
        }

        // Keep the focus row visible: drive the scroll offset through the ScrollViewer seam.
        ScrollRowIntoView(target);
        RowsPresenter?.InvalidateBand();
    }

    private DataGridFilterPopup? _filterPopup;

    /// <summary>The live checklist popup (tests reach its content; null when closed).</summary>
    internal DataGridFilterPopup? ActiveFilterPopup => _filterPopup is { IsOpen: true } popup ? popup : null;

    /// <summary>
    /// Opens the column's distinct-value checklist popup anchored below its header cell
    /// (design doc §3.4 — the header ▾ zone and DistinctPicker auto-filter cells route here).
    /// </summary>
    public void OpenFilterPopup(DataGridColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (_controller is null || _header is null || RowsPresenter is null || !column.AllowFilter)
            return;

        // Anchor: the popup places against the whole header band (Bottom edge) with the cell's x
        // as the horizontal offset — the WM clamps into the viewport, so an edge column's popup
        // slides left rather than clipping.
        int cellX = 0;
        foreach (var entry in RowsPresenter.ColumnLayout.Entries)
        {
            if (ReferenceEquals(entry.Column, column))
            {
                cellX = entry.X - _header.HorizontalOffset;
                break;
            }
        }

        _filterPopup ??= new DataGridFilterPopup(this);
        _filterPopup.Open(column, _header, cellX);
    }

    /// <summary>
    /// Copies the selected rows to the terminal clipboard (OSC 52) as TSV — formatted values,
    /// visible columns, view order; falls back to the focus row when nothing is selected.
    /// </summary>
    public void CopySelectionToClipboard()
    {
        if (_controller is null)
            return;

        var snapshot = Snapshot;
        var ids = RowSelection.IsEmpty
            ? FocusRowIdOrEmpty(snapshot)
            : RowSelection.MaterializeSelectedIds(snapshot);
        if (ids.Count == 0)
            return;

        var visible = Columns.Where(c => c.Visible).ToList();
        var builder = new System.Text.StringBuilder();
        foreach (int rowId in ids)
        {
            for (int c = 0; c < visible.Count; c++)
            {
                if (c > 0)
                    builder.Append('\t');
                builder.Append(_controller.FormatCell(rowId, visible[c]));
            }
            builder.Append('\n');
        }

        UIApplication.Current?.Clipboard.SetText(builder.ToString());
    }

    private List<int> FocusRowIdOrEmpty(DataViewSnapshot snapshot)
    {
        if (FocusViewIndex >= 0 && FocusViewIndex < snapshot.Count &&
            snapshot.GetRow(FocusViewIndex) is { IsGroup: false, RowId: >= 0 } row)
        {
            return [row.RowId];
        }
        return [];
    }

    /// <summary>Brings a view row into the viewport (the drawn-rows analog of bring-into-view — §3.1).</summary>
    public void ScrollRowIntoView(int viewIndex)
    {
        if (RowsPresenter?.ScrollOwner is not { } scp)
            return;

        int offset = scp.ScrollOffsetRow;
        int viewportRows = Math.Max(1, RowsPresenter.PageStep(0, 1, vertical: true) + 1);
        if (viewIndex < offset)
            scp.ScrollOffsetRow = viewIndex;
        else if (viewIndex >= offset + viewportRows)
            scp.ScrollOffsetRow = viewIndex - viewportRows + 1;
    }

    // ── Column UX: geometry funnel, drag-reorder drop, chooser, layout persistence (§1) ──────────

    /// <summary>
    /// Re-resolves column geometry across every band presenter after a width/order/visibility
    /// change (the header-edge resize, drag-reorder, and chooser surfaces write column properties
    /// that carry NO change notification into layout). Reuses the <see cref="SnapshotChanged"/>
    /// fan-out deliberately: every band presenter already subscribes it as its "re-read grid state
    /// and re-ink" signal, and that is exactly what a geometry change needs — the rows presenter's
    /// handler re-fills its band cache (cell arrays index the Columns order) and re-runs
    /// <c>ColumnLayout.Resolve</c>; header/filter/footer re-ink from the shared entries. No engine
    /// reshape rides this path (the snapshot is genuinely unchanged).
    /// </summary>
    internal void NotifyColumnGeometryChanged() => RaiseSnapshotChanged();

    /// <summary>
    /// Lands a header drag: moves <paramref name="column"/> so it renders immediately before the
    /// visible slot the pointer released over (<paramref name="slot"/> in visible-entry space;
    /// count = append). Hidden columns interleaved in <see cref="Columns"/> keep their positions —
    /// the move anchors on the slot's column INSTANCE, not on raw indices.
    /// </summary>
    internal void DropColumnAtSlot(DataGridColumn column, int slot)
    {
        var entries = RowsPresenter?.ColumnLayout.Entries;
        if (entries is null || slot < 0)
            return;

        int from = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            if (ReferenceEquals(entries[i].Column, column))
            {
                from = i;
                break;
            }
        }
        if (from < 0 || slot == from || slot == from + 1)
            return; // dropping beside itself — the no-op slots the adorner pass also suppresses

        int oldIndex = Columns.IndexOf(column);
        if (oldIndex < 0)
            return;

        if (slot >= entries.Count)
        {
            Columns.Move(oldIndex, Columns.Count - 1); // append after the last visible column
            return;
        }

        // Insert before the anchor: ObservableCollection.Move removes-then-inserts, so the target
        // index shifts down by one when the anchor currently sits past the dragged column.
        int anchorIndex = Columns.IndexOf(entries[slot].Column);
        if (anchorIndex < 0)
            return;
        Columns.Move(oldIndex, anchorIndex > oldIndex ? anchorIndex - 1 : anchorIndex);
    }

    private DataGridColumnChooser? _columnChooser;

    /// <summary>The live column chooser (tests reach its content; null when closed).</summary>
    internal DataGridColumnChooser? ActiveColumnChooser
        => _columnChooser is { IsOpen: true } chooser ? chooser : null;

    /// <summary>
    /// Opens the column chooser popup anchored below the header band (design doc §1 deferred item,
    /// now landed; the mockup's "Column chooser — hidden columns &amp; drag-to-show"): hidden
    /// columns as ⠿ chips (click = show, back at its original <see cref="Columns"/> position —
    /// order is the collection, <see cref="DataGridColumn.Visible"/> only filters layout), visible
    /// columns as checked entries (click = hide), Show All / Hide All footer. Also reachable by
    /// right-clicking the header band.
    /// </summary>
    public void OpenColumnChooser() => OpenColumnChooser(0);

    /// <summary>The header's right-click entry point — anchors at the pressed cell's x.</summary>
    internal void OpenColumnChooser(int anchorX)
    {
        if (_header is null)
            return;
        _columnChooser ??= new DataGridColumnChooser(this);
        _columnChooser.Open(_header, anchorX);
    }

    /// <summary>
    /// Snapshots the user-facing column layout for persistence (the DevExpress table-view
    /// expectation): per column — field name, width (in <see cref="DataGridLength"/> string form),
    /// visibility — in display order, plus the sort/group levels by field name. JSON-agnostic
    /// records; serialize with whatever the app already uses. Columns authored with only a
    /// <see cref="DataGridColumn.KeySelector"/> carry a null field name and cannot be re-matched by
    /// <see cref="ApplyColumnLayout"/> (documented limitation — name-keyed persistence needs names).
    /// </summary>
    public DataGridLayoutState GetColumnLayout() => new()
    {
        Columns = Columns
            .Select(c => new DataGridColumnLayoutEntry(c.FieldName, c.Width.ToString(), c.Visible))
            .ToList(),
        Sorts = SortDescriptions
            .Select(s => new DataGridSortLayoutEntry(FieldNameOf(s.ColumnKey), s.Direction == SortDirection.Descending))
            .Where(s => s.FieldName is not null)
            .ToList(),
        Groups = GroupDescriptions
            .Select(g => new DataGridGroupLayoutEntry(FieldNameOf(g.ColumnKey), g.Direction == SortDirection.Descending))
            .Where(g => g.FieldName is not null)
            .ToList(),
    };

    private static string? FieldNameOf(object columnKey)
        => columnKey as string ?? (columnKey as DataGridColumn)?.FieldName;

    /// <summary>
    /// Restores a layout captured by <see cref="GetColumnLayout"/> onto the CURRENT column set:
    /// columns are matched by field name (unmatched state entries are skipped; grid columns absent
    /// from the state keep their properties and sink to the end in relative order), then width /
    /// visibility apply and the sort/group descriptions rebuild. Deliberately tolerant — a saved
    /// layout must survive an app adding or removing columns between sessions.
    /// </summary>
    public void ApplyColumnLayout(DataGridLayoutState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // Order: walk the state's entries, pulling each matched column to the next slot (in-place
        // Moves — each one is the cheap geometry-only OnColumnsChanged path, never a reshape).
        int target = 0;
        foreach (var entry in state.Columns)
        {
            var column = entry.FieldName is null ? null : FindColumnByField(entry.FieldName);
            if (column is null)
                continue;

            int current = Columns.IndexOf(column);
            if (current != target)
                Columns.Move(current, target);
            target++;

            column.Width = DataGridLength.Parse(entry.Width);
            column.Visible = entry.Visible;
        }

        // Sort/group state: clear-and-rebuild from field names (the observable collections are the
        // one source of truth — §3; each edit schedules one coalesced shape push).
        SortDescriptions.Clear();
        foreach (var sort in state.Sorts)
        {
            if (sort.FieldName is not null && FindColumnByField(sort.FieldName) is { } column)
                SortDescriptions.Add(new SortDescription(column, sort.Descending ? SortDirection.Descending : SortDirection.Ascending));
        }

        GroupDescriptions.Clear();
        foreach (var group in state.Groups)
        {
            if (group.FieldName is not null && FindColumnByField(group.FieldName) is { } column)
                GroupDescriptions.Add(new GroupDescription(column, group.Descending ? SortDirection.Descending : SortDirection.Ascending));
        }

        NotifyColumnGeometryChanged();
    }

    private DataGridColumn? FindColumnByField(string fieldName)
        => Columns.FirstOrDefault(c => string.Equals(c.FieldName, fieldName, StringComparison.Ordinal));

    // ── Teardown ─────────────────────────────────────────────────────────────────────────────────

    protected override void OnTearDown()
    {
        _filterPopup?.Close(); // release the popup surface before the controller it reads goes away
        _columnChooser?.Close();
        _controller?.Dispose();
        _controller = null;
        base.OnTearDown();
    }
}

/// <summary>
/// Carries the new row instance for <see cref="DataGrid.AddingNewRow"/> (§3.2 — the new-row
/// template's construction seam): a handler sets <see cref="Item"/> (rows without a parameterless
/// constructor); left null, the grid falls back to <see cref="Activator.CreateInstance(Type)"/>.
/// </summary>
public sealed class AddingNewRowEventArgs : EventArgs
{
    /// <summary>The fresh row instance to edit and add, or null to use the Activator fallback.</summary>
    public object? Item { get; set; }
}
