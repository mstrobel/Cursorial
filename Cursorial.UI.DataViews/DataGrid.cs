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

    /// <summary>Whether the auto-filter row renders.</summary>
    public static readonly StyledProperty<bool> ShowAutoFilterRowProperty =
        UIProperty.Register<DataGrid, bool>(nameof(ShowAutoFilterRow));

    /// <summary>Whether the group panel renders.</summary>
    public static readonly StyledProperty<bool> ShowGroupPanelProperty =
        UIProperty.Register<DataGrid, bool>(nameof(ShowGroupPanel), true);

    /// <summary>Whether the summary footer renders (auto-hides when no summaries are defined).</summary>
    public static readonly StyledProperty<bool> ShowSummaryFooterProperty =
        UIProperty.Register<DataGrid, bool>(nameof(ShowSummaryFooter), true);

    static DataGrid()
    {
        FocusableProperty.OverrideDefaultValue<DataGrid>(true);
        AffectsMeasure<DataGrid>(ItemsSourceProperty);
        AffectsRender<DataGrid>(ShowAutoFilterRowProperty, ShowGroupPanelProperty, ShowSummaryFooterProperty);
    }

    private DataViewController? _controller;
    private Type? _rowType;
    private bool _columnsFromAutoGeneration;
    private bool _shapePushScheduled;

    public DataGrid()
    {
        Columns = [];
        Columns.CollectionChanged += (_, _) => OnColumnsChanged();
        SortDescriptions = [];
        SortDescriptions.CollectionChanged += (_, _) => ScheduleShapePush();
        GroupDescriptions = [];
        GroupDescriptions.CollectionChanged += (_, _) => ScheduleShapePush();
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

    public const string PartHeader = "PART_Header";
    public const string PartFooter = "PART_Footer";
    public const string PartScrollViewer = "PART_ScrollViewer";
    public const string PartRows = "PART_Rows";

    private ScrollViewer? _scrollViewer;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        var header = GetTemplatePart<DataGridHeaderPresenter>(PartHeader);
        var footer = GetTemplatePart<DataGridSummaryPresenter>(PartFooter);
        _scrollViewer = GetTemplatePart<ScrollViewer>(PartScrollViewer);
        RowsPresenter = GetTemplatePart<DataGridRowsPresenter>(PartRows);

        if (RowsPresenter is not null)
            RowsPresenter.Owner = this;
        if (header is not null)
        {
            header.Owner = this;
            if (_scrollViewer is not null)
                header.SetBinding(DataGridHeaderPresenter.HorizontalOffsetProperty,
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
        if (RowsPresenter is not null)
            RowsPresenter.Owner = null;
        _scrollViewer = null;
        RowsPresenter = null;
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
        if (_rowType is null)
        {
            // An empty untyped source: no rows to discover from — stay dormant until items arrive
            // (an INCC source re-triggers via the reset heuristic below) or a typed source is set.
            RaiseSnapshotChanged();
            return;
        }

        _controller = DataViewController.Create(_rowType);
        EnsureColumns();
        _controller.SetColumns(Columns.Where(c => c.FieldName is not null || c.KeySelector is not null)
                                      .Select(c => c.ToShapingDescription()).ToList());
        _controller.AttachSource(source, LiveUpdates);
        _controller.SnapshotChanged += (_, _) => RaiseSnapshotChanged();
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

    private void OnColumnsChanged()
    {
        if (_columnsFromAutoGeneration)
            return; // our own generation pass — not an authoring change

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

    /// <summary>Pushes sort/group/summaries/filter into the controller (one atomic reshape).</summary>
    private void PushShape()
    {
        if (_controller is null)
            return;

        FilterNode? effective = BuildEffectiveFilter();
        _controller.SetShape(SortDescriptions.ToList(), GroupDescriptions.ToList(),
                             SummaryDescriptions.ToList(), effective);
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
    {
        ArgumentNullException.ThrowIfNull(column);
        if (fragment is null)
            _columnFilters.Remove(column);
        else
            _columnFilters[column] = fragment;
        ScheduleShapePush();
    }

    /// <summary>Whether a column currently carries a filter fragment (the header's amber ▾ cue).</summary>
    public bool HasColumnFilter(DataGridColumn column) => _columnFilters.ContainsKey(column);

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
            return;

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

    /// <summary>The keyboard navigation surface (§3.3 — legacy-safe gestures).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        var snapshot = Snapshot;
        if (snapshot.Count == 0)
            return;

        int current = Math.Clamp(FocusViewIndex < 0 ? 0 : FocusViewIndex, 0, snapshot.Count - 1);
        int viewport = RowsPresenter is { } presenter ? Math.Max(1, presenter.PageStep(0, 1, vertical: true)) : 10;
        bool shift = (e.Modifiers & KeyModifiers.Shift) != 0;
        bool ctrl = (e.Modifiers & KeyModifiers.Control) != 0;

        int? target = e.Key switch
        {
            Key.UpArrow => Math.Max(0, current - 1),
            Key.DownArrow => Math.Min(snapshot.Count - 1, current + 1),
            Key.PageUp => Math.Max(0, current - viewport),
            Key.PageDown => Math.Min(snapshot.Count - 1, current + viewport),
            Key.Home when ctrl => 0,
            Key.End when ctrl => snapshot.Count - 1,
            _ => null,
        };

        if (target is { } t)
        {
            MoveFocusRow(t, shift, ctrl);
            e.Handled = true;
            return;
        }

        var focused = FocusViewIndex >= 0 && FocusViewIndex < snapshot.Count
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

            // Ctrl+A — select all (the compact inversion).
            case Key.Character when ctrl && e.Text.Length == 1 && (e.Text.Span[0] is 'a' or 'A'):
                RowSelection.SelectAll();
                RowsPresenter?.InvalidateBand();
                e.Handled = true;
                return;

            // Space selects (the modifier-free wire is (Character, " ") — ND10).
            case Key.Character or Key.Space when IsSpace(e) && !focused.IsGroup && focused.RowId >= 0:
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

    /// <summary>Opens the column's filter checklist popup (wired by the filter-surface stage).</summary>
    internal void OpenFilterPopup(DataGridColumn column)
    {
        // The checklist-popup stage (design doc §3.4) fills this in; the header's ▾ zone routes here.
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

    // ── Teardown ─────────────────────────────────────────────────────────────────────────────────

    protected override void OnTearDown()
    {
        _controller?.Dispose();
        _controller = null;
        base.OnTearDown();
    }
}
