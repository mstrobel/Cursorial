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
using Cursorial.UI.DataViews.Shaping.Expressions;
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

    /// <summary>
    /// The ONE horizontal scroll truth (§9.2 — the in-presenter horizontal axis): every band
    /// presenter draws shifted by this offset (the SCP scrolls vertically only; its horizontal
    /// extent is the viewport). Clamped to <c>[0, max(0, TotalWidth − viewportColumns)]</c> at set
    /// time AND re-clamped after each measure resolves the column layout (the SCP end-of-arrange
    /// re-coercion analog — a hide/resize while scrolled right snaps back the same frame).
    /// </summary>
    public static readonly StyledProperty<int> HorizontalOffsetProperty =
        UIProperty.Register<DataGrid, int>(nameof(HorizontalOffset),
            changed: static (sender, _, value) => ((DataGrid)sender).OnHorizontalOffsetChanged(value));

    /// <summary>
    /// The master-detail template (§9.3): non-null enables the 2-cell expander gutter and per-row
    /// detail panes. The engine stays 1-row-per-entry — detail geometry is presenter-side (the
    /// content-y map); detail elements are hosted children built fresh per expansion with
    /// <c>DataContext</c> = the row object.
    /// </summary>
    public static readonly StyledProperty<Controls.DataTemplate?> DetailTemplateProperty =
        UIProperty.Register<DataGrid, Controls.DataTemplate?>(nameof(DetailTemplate),
            changed: static (sender, _, _) =>
            {
                var grid = (DataGrid)sender;
                grid._expandedDetails.Clear(); // a template swap invalidates every built pane
                // The gutter is COLUMN GEOMETRY (every band presenter draws it) — the geometry
                // funnel re-inks all four bands, not just the rows presenter.
                grid.NotifyColumnGeometryChanged();
            });

    /// <summary>The selection granularity (§9.4): a mode switch clears BOTH selections and keeps
    /// the focus cell.</summary>
    public static readonly StyledProperty<DataGridSelectionUnit> SelectionUnitProperty =
        UIProperty.Register<DataGrid, DataGridSelectionUnit>(nameof(SelectionUnit),
            changed: static (sender, _, _) =>
            {
                var grid = (DataGrid)sender;
                grid.RowSelection.Clear();
                grid.ClearCellRange();
                grid.RowsPresenter?.InvalidateBand();
            });

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

    /// <summary>
    /// The liveUpdates value EVERY AttachSource site must pass (§9.6 — wave-2 audit F1): value-type
    /// rows have no INPC identity, so per-row live updates degrade off silently (the typed
    /// controller THROWS on liveUpdates:true for structs; the non-generic grid degrades instead —
    /// collection-level INCC still works, per-row edits ride SetRow). One property so the policy
    /// cannot drift between the initial attach and the new-row cold re-attach.
    /// </summary>
    private bool EffectiveLiveUpdates => LiveUpdates && _rowType is { IsValueType: false };

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

    /// <inheritdoc cref="HorizontalOffsetProperty"/>
    public int HorizontalOffset
    {
        get => GetValue(HorizontalOffsetProperty);
        set => SetValue(HorizontalOffsetProperty, value);
    }

    /// <inheritdoc cref="DetailTemplateProperty"/>
    public Controls.DataTemplate? DetailTemplate
    {
        get => GetValue(DetailTemplateProperty);
        set => SetValue(DetailTemplateProperty, value);
    }

    // ── Master-detail expansion state (§9.3 — grid-owned; the presenter owns realization) ────────

    private readonly HashSet<int> _expandedDetails = [];

    /// <summary>The expanded row ids (the presenter's realization input).</summary>
    internal IReadOnlySet<int> ExpandedDetails => _expandedDetails;

    /// <summary>Whether a row's detail pane is expanded.</summary>
    public bool IsDetailExpanded(int rowId) => _expandedDetails.Contains(rowId);

    /// <summary>Expands a row's detail pane (no-op without a <see cref="DetailTemplate"/>).</summary>
    public void ExpandDetail(int rowId)
    {
        if (DetailTemplate is null || !_expandedDetails.Add(rowId))
            return;
        RowsPresenter?.InvalidateBand();
    }

    /// <summary>Collapses a row's detail pane.</summary>
    public void CollapseDetail(int rowId)
    {
        if (!_expandedDetails.Remove(rowId))
            return;
        RowsPresenter?.InvalidateBand();
    }

    /// <summary>The expander gesture (gutter click / Ctrl+Right/Left).</summary>
    public void ToggleDetail(int rowId)
    {
        if (_expandedDetails.Contains(rowId))
            CollapseDetail(rowId);
        else
            ExpandDetail(rowId);
    }

    /// <summary>
    /// Prunes expansion state for rows that left the VIEW (refilter/removal — §9.3: a released id's
    /// pane is dropped, not parked). Runs per snapshot publish; collapsed-group hiding also counts
    /// as leaving (DevExpress collapses the pane with its row).
    /// </summary>
    private void PruneExpandedDetails()
    {
        if (_expandedDetails.Count == 0)
            return;
        _expandedDetails.RemoveWhere(rowId => ViewIndexOfRow(rowId) < 0);
    }

    // ── The grid context menu (the reachability surface — every dialog + summaries; DevExpress
    //    parity for "right-click does everything the ribbon would") ────────────────────────────────

    private ContextMenu? _gridMenu;

    /// <summary>The live grid context menu (tests reach its items; null when closed).</summary>
    internal ContextMenu? ActiveGridMenu => _gridMenu is { IsOpen: true } menu ? menu : null;

    /// <summary>
    /// Opens the grid's command menu anchored at a rows-area cell (right-click / the Menu key):
    /// per-column sort/group lanes, the filter dialogs, conditional formatting, the column chooser,
    /// the per-column summary picker (Sum/Average gated on a numeric key — the engine throws on
    /// non-numeric), and copy. Built FRESH per open — the items depend on the anchor column and the
    /// live shaping state.
    /// </summary>
    public void OpenGridContextMenu(int columnIndex, CellPosition? position = null)
    {
        var presenter = RowsPresenter;
        if (presenter is null)
            return;

        var entries = presenter.ColumnLayout.Entries;
        var column = columnIndex >= 0 && columnIndex < entries.Count ? entries[columnIndex].Column : null;

        _gridMenu?.Close();
        var menu = new ContextMenu();

        if (column is not null && column.AllowSort)
        {
            AddItem(menu, $"Sort \"{column.EffectiveHeader}\" ascending", () =>
            {
                SortDescriptions.Clear();
                SortDescriptions.Add(SortDescription.Ascending(column));
            });
            AddItem(menu, $"Sort \"{column.EffectiveHeader}\" descending", () =>
            {
                SortDescriptions.Clear();
                SortDescriptions.Add(SortDescription.Descending(column));
            });
            AddItem(menu, "Add as sort level (Ctrl+click)", () => AppendSort(column));
            if (SortDescriptions.Count > 0)
                AddItem(menu, "Clear sorting", SortDescriptions.Clear);
            menu.Items.Add(new Separator());
        }

        if (column is not null && column.AllowGroup)
        {
            bool grouped = GroupDescriptions.Any(g => ReferenceEquals(g.ColumnKey, column));
            if (!grouped)
            {
                AddItem(menu, $"Group by \"{column.EffectiveHeader}\"", () => GroupBy(column));
            }
            else
            {
                AddItem(menu, $"Ungroup \"{column.EffectiveHeader}\"", () =>
                {
                    for (int i = GroupDescriptions.Count - 1; i >= 0; i--)
                    {
                        if (ReferenceEquals(GroupDescriptions[i].ColumnKey, column))
                            GroupDescriptions.RemoveAt(i);
                    }
                });
            }
            menu.Items.Add(new Separator());
        }

        AddItem(menu, "Filter builder…", () => _ = OpenFilterBuilderAsync());
        AddItem(menu, "Filter editor (text)…", () => _ = OpenFilterEditorAsync());
        AddItem(menu, "Conditional formatting…", () => _ = OpenRulesManagerAsync());
        AddItem(menu, "Column chooser…", () => OpenColumnChooser(0));

        if (column is not null && _controller is not null)
        {
            menu.Items.Add(new Separator());
            var summaryMenu = new MenuItem { Header = $"Summary for \"{column.EffectiveHeader}\"" };

            var keyType = _controller.GetColumnKeyType(column);
            var underlying = keyType is null ? null : Nullable.GetUnderlyingType(keyType) ?? keyType;
            bool numeric = underlying == typeof(int) || underlying == typeof(long) ||
                           underlying == typeof(short) || underlying == typeof(byte) ||
                           underlying == typeof(double) || underlying == typeof(float) ||
                           underlying == typeof(decimal) || underlying == typeof(uint) ||
                           underlying == typeof(ulong) || underlying == typeof(ushort);

            void AddSummaryChoice(string caption, AggregateKind kind)
            {
                var item = new MenuItem
                {
                    Header = caption,
                    IsCheckable = true,
                    IsChecked = SummaryDescriptions.Any(s =>
                        ReferenceEquals(s.ColumnKey, column) && s.Aggregate == kind),
                };
                item.Click += (_, _) => ToggleSummary(column, kind);
                summaryMenu.Items.Add(item);
            }

            AddSummaryChoice("Count", AggregateKind.Count);
            if (numeric)
            {
                AddSummaryChoice("Sum", AggregateKind.Sum);
                AddSummaryChoice("Average", AggregateKind.Average);
            }
            AddSummaryChoice("Min", AggregateKind.Min);
            AddSummaryChoice("Max", AggregateKind.Max);
            if (SummaryDescriptions.Any(s => ReferenceEquals(s.ColumnKey, column)))
            {
                summaryMenu.Items.Add(new Separator());
                var clear = new MenuItem { Header = "None" };
                clear.Click += (_, _) =>
                {
                    for (int i = SummaryDescriptions.Count - 1; i >= 0; i--)
                    {
                        if (ReferenceEquals(SummaryDescriptions[i].ColumnKey, column))
                            SummaryDescriptions.RemoveAt(i);
                    }
                };
                summaryMenu.Items.Add(clear);
            }
            menu.Items.Add(summaryMenu);
        }

        menu.Items.Add(new Separator());
        AddItem(menu, "Copy", CopySelectionToClipboard);

        _gridMenu = menu;
        menu.Open(presenter, position);

        static void AddItem(ContextMenu menu, string caption, Action action)
        {
            var item = new MenuItem { Header = caption };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    /// <summary>Adds the (column, kind) summary, or removes it when already present (the menu's checkable toggle).</summary>
    public void ToggleSummary(DataGridColumn column, AggregateKind kind)
    {
        ArgumentNullException.ThrowIfNull(column);
        for (int i = 0; i < SummaryDescriptions.Count; i++)
        {
            if (ReferenceEquals(SummaryDescriptions[i].ColumnKey, column) && SummaryDescriptions[i].Aggregate == kind)
            {
                SummaryDescriptions.RemoveAt(i);
                return;
            }
        }
        SummaryDescriptions.Add(new SummaryDescription(column, kind));
    }

    // ── The §3.3 band cycle + virtual band focus (v1-pinned, landed with the wave-2 closeout) ────
    //
    // Drawn bands cannot hold framework focus (the header/group-panel/auto-filter cells are painted,
    // not elements), so the grid keeps the ONE framework focus and routes keys to a VIRTUAL band:
    // Ctrl+Up from row 0 (or F6 anywhere) walks rows → header → group panel → auto-filter → rows
    // (hidden bands skip); Esc (or Down from the header) returns to rows. Each band draws its own
    // focus cue from the grid-owned index.

    private DataGridFocusBand _focusBand = DataGridFocusBand.Rows;
    private int _headerFocusIndex;
    private int _chipFocusIndex;
    private int _filterCellFocusIndex;

    /// <summary>The §3.3 virtual band focus (rows unless a band walk moved it).</summary>
    public DataGridFocusBand FocusBand => _focusBand;

    /// <summary>The header band's virtually-focused entry index (−1 unless the header holds band focus).</summary>
    internal int HeaderFocusIndex => _focusBand == DataGridFocusBand.Header ? _headerFocusIndex : -1;

    /// <summary>The group panel's virtually-focused chip index (−1 unless the panel holds band focus).</summary>
    internal int GroupChipFocusIndex => _focusBand == DataGridFocusBand.GroupPanel ? _chipFocusIndex : -1;

    /// <summary>The auto-filter row's virtually-focused cell index (−1 unless the band holds band focus).</summary>
    internal int FilterCellFocusIndex => _focusBand == DataGridFocusBand.AutoFilter ? _filterCellFocusIndex : -1;

    /// <summary>Moves the virtual focus into the header band (the Ctrl+Up / F6 entry; public for tests/automation).</summary>
    public void FocusHeaderBand() => EnterBand(DataGridFocusBand.Header);

    private bool BandAvailable(DataGridFocusBand band) => band switch
    {
        DataGridFocusBand.Header => RowsPresenter is { ColumnLayout.Entries.Count: > 0 },
        DataGridFocusBand.GroupPanel => ShowGroupPanel && GroupDescriptions.Count > 0,
        DataGridFocusBand.AutoFilter => ShowAutoFilterRow && RowsPresenter is { ColumnLayout.Entries.Count: > 0 },
        _ => true,
    };

    private void EnterBand(DataGridFocusBand band)
    {
        if (band != DataGridFocusBand.Rows && !BandAvailable(band))
            return;
        _focusBand = band;
        switch (band)
        {
            case DataGridFocusBand.Header:
                _headerFocusIndex = Math.Clamp(Math.Max(0, FocusColumnIndex), 0,
                                               Math.Max(0, (RowsPresenter?.ColumnLayout.Entries.Count ?? 1) - 1));
                ScrollColumnIntoView(_headerFocusIndex);
                break;
            case DataGridFocusBand.GroupPanel:
                _chipFocusIndex = Math.Clamp(_chipFocusIndex, 0, Math.Max(0, GroupDescriptions.Count - 1));
                break;
            case DataGridFocusBand.AutoFilter:
                _filterCellFocusIndex = Math.Clamp(Math.Max(0, FocusColumnIndex), 0,
                                                   Math.Max(0, (RowsPresenter?.ColumnLayout.Entries.Count ?? 1) - 1));
                ScrollColumnIntoView(_filterCellFocusIndex);
                break;
        }
        InvalidateBandCues();
    }

    /// <summary>Returns the virtual focus to the rows (Esc, a rows press, or the cycle wrapping).</summary>
    internal void ExitBandFocus()
    {
        if (_focusBand == DataGridFocusBand.Rows)
            return;
        _focusBand = DataGridFocusBand.Rows;
        InvalidateBandCues();
    }

    private void AdvanceBandCycle()
    {
        ReadOnlySpan<DataGridFocusBand> cycle =
        [
            DataGridFocusBand.Rows, DataGridFocusBand.Header,
            DataGridFocusBand.GroupPanel, DataGridFocusBand.AutoFilter,
        ];
        int at = (int)_focusBand; // the enum IS the cycle order
        for (int step = 1; step <= cycle.Length; step++)
        {
            var next = cycle[(at + step) % cycle.Length];
            if (BandAvailable(next))
            {
                if (next == DataGridFocusBand.Rows)
                    ExitBandFocus();
                else
                    EnterBand(next);
                return;
            }
        }
    }

    private void InvalidateBandCues()
    {
        _header?.InvalidateVisual();
        GroupPanel?.InvalidateVisual();
        AutoFilterRow?.InvalidateVisual();
        RowsPresenter?.InvalidateVisual();
    }

    /// <summary>The §3.3 per-band key router (runs while a virtual band holds focus).</summary>
    private void HandleBandKey(KeyEventArgs e)
    {
        bool ctrl = (e.Modifiers & KeyModifiers.Control) != 0;
        bool alt = (e.Modifiers & KeyModifiers.Alt) != 0;

        // The cycle + exit gestures are band-agnostic.
        if (e.Key == Key.F6)
        {
            AdvanceBandCycle();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            ExitBandFocus();
            e.Handled = true;
            return;
        }

        var entries = RowsPresenter?.ColumnLayout.Entries;
        switch (_focusBand)
        {
            case DataGridFocusBand.Header when entries is { Count: > 0 }:
                switch (e.Key)
                {
                    case Key.LeftArrow when !ctrl:
                        _headerFocusIndex = Math.Max(0, _headerFocusIndex - 1);
                        ScrollColumnIntoView(_headerFocusIndex);
                        InvalidateBandCues();
                        e.Handled = true;
                        return;
                    case Key.RightArrow when !ctrl:
                        _headerFocusIndex = Math.Min(entries.Count - 1, _headerFocusIndex + 1);
                        ScrollColumnIntoView(_headerFocusIndex);
                        InvalidateBandCues();
                        e.Handled = true;
                        return;
                    case Key.Home:
                        _headerFocusIndex = 0;
                        ScrollColumnIntoView(0);
                        InvalidateBandCues();
                        e.Handled = true;
                        return;
                    case Key.End:
                        _headerFocusIndex = entries.Count - 1;
                        ScrollColumnIntoView(_headerFocusIndex);
                        InvalidateBandCues();
                        e.Handled = true;
                        return;
                    case Key.Enter:
                        // Enter cycles asc→desc→none as the REPLACING sort (§3.3).
                        CycleSort(entries[_headerFocusIndex].Column);
                        e.Handled = true;
                        return;
                    // Space appends/cycles this column as an additional sort level (§3.3 — no
                    // chord; the modifier-free wire is (Character, " "), ND10).
                    case Key.Character when !ctrl && !alt && e.Text.Length == 1 && e.Text.Span[0] == ' ':
                        AppendSort(entries[_headerFocusIndex].Column);
                        e.Handled = true;
                        return;
                    case Key.DownArrow when alt:
                        OpenFilterPopup(entries[_headerFocusIndex].Column); // §3.3 Alt+Down
                        e.Handled = true;
                        return;
                    case Key.DownArrow:
                        ExitBandFocus(); // back into the rows
                        e.Handled = true;
                        return;
                    case Key.Character when ctrl && e.Text.Length == 1 && (e.Text.Span[0] is 'g' or 'G'):
                        GroupBy(entries[_headerFocusIndex].Column); // §3.3 Ctrl+G
                        e.Handled = true;
                        return;
                }
                break;

            case DataGridFocusBand.GroupPanel when GroupDescriptions.Count > 0:
                _chipFocusIndex = Math.Clamp(_chipFocusIndex, 0, GroupDescriptions.Count - 1);
                switch (e.Key)
                {
                    case Key.LeftArrow when ctrl:
                    case Key.RightArrow when ctrl:
                    {
                        // Ctrl+Left/Right reorders the level (§3.3); the chip focus travels with it.
                        int target = e.Key == Key.LeftArrow ? _chipFocusIndex - 1 : _chipFocusIndex + 1;
                        if (target >= 0 && target < GroupDescriptions.Count)
                        {
                            (GroupDescriptions[_chipFocusIndex], GroupDescriptions[target]) =
                                (GroupDescriptions[target], GroupDescriptions[_chipFocusIndex]);
                            _chipFocusIndex = target;
                            InvalidateBandCues();
                        }
                        e.Handled = true;
                        return;
                    }
                    case Key.LeftArrow:
                        _chipFocusIndex = Math.Max(0, _chipFocusIndex - 1);
                        InvalidateBandCues();
                        e.Handled = true;
                        return;
                    case Key.RightArrow:
                        _chipFocusIndex = Math.Min(GroupDescriptions.Count - 1, _chipFocusIndex + 1);
                        InvalidateBandCues();
                        e.Handled = true;
                        return;
                    case Key.Enter:
                    {
                        // Enter toggles the chip's direction (§3.3) — the key order the level's
                        // boundaries derive from (OrderBySummary state rides the `with`).
                        var level = GroupDescriptions[_chipFocusIndex];
                        GroupDescriptions[_chipFocusIndex] = level with
                        {
                            Direction = level.Direction == SortDirection.Ascending
                                ? SortDirection.Descending
                                : SortDirection.Ascending,
                        };
                        e.Handled = true;
                        return;
                    }
                    case Key.Delete:
                        GroupDescriptions.RemoveAt(_chipFocusIndex);
                        if (GroupDescriptions.Count == 0)
                            ExitBandFocus();
                        else
                            _chipFocusIndex = Math.Min(_chipFocusIndex, GroupDescriptions.Count - 1);
                        InvalidateBandCues();
                        e.Handled = true;
                        return;
                }
                break;

            case DataGridFocusBand.AutoFilter when entries is { Count: > 0 }:
                switch (e.Key)
                {
                    case Key.LeftArrow:
                        _filterCellFocusIndex = Math.Max(0, _filterCellFocusIndex - 1);
                        ScrollColumnIntoView(_filterCellFocusIndex);
                        InvalidateBandCues();
                        e.Handled = true;
                        return;
                    case Key.RightArrow:
                        _filterCellFocusIndex = Math.Min(entries.Count - 1, _filterCellFocusIndex + 1);
                        ScrollColumnIntoView(_filterCellFocusIndex);
                        InvalidateBandCues();
                        e.Handled = true;
                        return;
                    case Key.Enter:
                    {
                        // Enter engages the cell's filter surface per its kind (§3.4).
                        var column = entries[_filterCellFocusIndex].Column;
                        if (!column.AllowFilter)
                            return;
                        if (column.FilterCellKind == FilterCellKind.DistinctPicker)
                            OpenFilterPopup(column);
                        else if (column.FilterCellKind == FilterCellKind.Text)
                            AutoFilterRow?.BeginEdit(_filterCellFocusIndex);
                        e.Handled = true;
                        return;
                    }
                }
                break;
        }
    }

    // ── Cell-range selection (§9.4 — corner truth; membership derives per snapshot) ──────────────

    /// <inheritdoc cref="SelectionUnitProperty"/>
    public DataGridSelectionUnit SelectionUnit
    {
        get => GetValue(SelectionUnitProperty);
        set => SetValue(SelectionUnitProperty, value);
    }

    // The range IS its corners (§9.4): row ids + COLUMN IDENTITIES (never visible indices — the
    // column UX reorders/hides at runtime). Membership derives per snapshot from the re-projected
    // corners; reshapes legitimately change membership (the Excel/DevExpress semantic).
    private int _cellAnchorRowId = -1;
    private int _cellLeadRowId = -1;
    private DataGridColumn? _cellAnchorColumn;
    private DataGridColumn? _cellLeadColumn;

    internal void ClearCellRange()
    {
        _cellAnchorRowId = _cellLeadRowId = -1;
        _cellAnchorColumn = _cellLeadColumn = null;
    }

    /// <summary>Both corners onto one cell (the plain click / unmodified focus move).</summary>
    private void SetCellRangeAnchor(int rowId, DataGridColumn? column)
    {
        _cellAnchorRowId = _cellLeadRowId = rowId;
        _cellAnchorColumn = _cellLeadColumn = column;
    }

    /// <summary>Moves the lead corner (Shift+click / Shift+arrow); the anchor stays.</summary>
    private void ExtendCellRangeTo(int rowId, DataGridColumn? column)
    {
        if (_cellAnchorRowId < 0)
        {
            SetCellRangeAnchor(rowId, column);
            return;
        }
        _cellLeadRowId = rowId;
        if (column is not null)
            _cellLeadColumn = column;
    }

    /// <summary>
    /// The derived rectangle in the CURRENT snapshot/layout (§9.4): view rows normalized min..max,
    /// column edges resolved by IDENTITY (a hidden endpoint clamps to its nearest visible neighbor
    /// in collection order). Null = no range (row mode, no corners, or nothing visible).
    /// </summary>
    internal (int FirstRow, int LastRow, int FirstColumn, int LastColumn)? CellRangeViewRect()
    {
        if (SelectionUnit != DataGridSelectionUnit.Cell || _cellAnchorRowId < 0 || _cellLeadRowId < 0)
            return null;

        int anchorRow = ViewIndexOfRow(_cellAnchorRowId);
        int leadRow = ViewIndexOfRow(_cellLeadRowId);
        int anchorColumn = EntryIndexOfColumnClamped(_cellAnchorColumn);
        int leadColumn = EntryIndexOfColumnClamped(_cellLeadColumn);
        if (anchorRow < 0 || leadRow < 0 || anchorColumn < 0 || leadColumn < 0)
            return null; // a lost corner renders nothing this frame; the publish prune collapses it

        return (Math.Min(anchorRow, leadRow), Math.Max(anchorRow, leadRow),
                Math.Min(anchorColumn, leadColumn), Math.Max(anchorColumn, leadColumn));
    }

    /// <summary>A column's layout-entry index; a hidden endpoint clamps to the nearest visible
    /// neighbor by collection position (§9.4).</summary>
    private int EntryIndexOfColumnClamped(DataGridColumn? column)
    {
        var layout = RowsPresenter?.ColumnLayout;
        if (column is null || layout is null)
            return -1;

        int EntryOf(DataGridColumn candidate)
        {
            var entries = layout.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                if (ReferenceEquals(entries[i].Column, candidate))
                    return i;
            }
            return -1;
        }

        int exact = EntryOf(column);
        if (exact >= 0)
            return exact;

        // A column REMOVED from the collection has no neighborhood to clamp into (audit W2-15:
        // position −1 scanned rightward from index 0 and silently ballooned the rectangle to the
        // FIRST column) — report no edge; the derivation returns null and the publish prune
        // collapses per the lost-corner rule.
        int position = Columns.IndexOf(column);
        if (position < 0)
            return -1;

        for (int distance = 1; distance < Columns.Count; distance++)
        {
            if (position - distance >= 0 && EntryOf(Columns[position - distance]) is >= 0 and var left)
                return left;
            if (position + distance < Columns.Count && EntryOf(Columns[position + distance]) is >= 0 and var right)
                return right;
        }
        return -1;
    }

    /// <summary>§9.4: a corner whose row id left the view — or whose column left the collection
    /// entirely (audit W2-15) — collapses the range to the focus cell.</summary>
    private void PruneCellRange()
    {
        if (SelectionUnit != DataGridSelectionUnit.Cell || _cellAnchorRowId < 0)
            return;
        if (ViewIndexOfRow(_cellAnchorRowId) >= 0 && ViewIndexOfRow(_cellLeadRowId) >= 0 &&
            EntryIndexOfColumnClamped(_cellAnchorColumn) >= 0 && EntryIndexOfColumnClamped(_cellLeadColumn) >= 0)
        {
            return;
        }
        CollapseCellRangeToFocusCell();
    }

    private void CollapseCellRangeToFocusCell()
    {
        var snapshot = Snapshot;
        if (FocusViewIndex >= 0 && FocusViewIndex < snapshot.Count &&
            snapshot.GetRow(FocusViewIndex) is { IsGroup: false, RowId: >= 0 } focusRow)
        {
            SetCellRangeAnchor(focusRow.RowId, ColumnAtEntry(FocusColumnIndex));
        }
        else
        {
            ClearCellRange();
        }
    }

    /// <summary>Audit W2-12: a corner id being REMOVED collapses immediately — before the freed
    /// slot can recycle onto an unrelated row and silently re-attach the range.</summary>
    private void HandleRowsRemovedForCellRange(IReadOnlyList<int> ids)
    {
        if (_cellAnchorRowId < 0)
            return;
        for (int i = 0; i < ids.Count; i++)
        {
            if (ids[i] == _cellAnchorRowId || ids[i] == _cellLeadRowId)
            {
                CollapseCellRangeToFocusCell();
                return;
            }
        }
    }

    /// <summary>The column at a layout-entry index, or null.</summary>
    private DataGridColumn? ColumnAtEntry(int entryIndex)
    {
        var entries = RowsPresenter?.ColumnLayout.Entries;
        return entries is not null && entryIndex >= 0 && entryIndex < entries.Count
            ? entries[entryIndex].Column
            : null;
    }

    // ── The per-snapshot id→viewIndex inverse map (§9.3/§9.4 shared substrate) ───────────────────

    private Dictionary<int, int>? _viewIndexByRowId;
    private int _viewIndexMapVersion = -1;

    /// <summary>
    /// A data row's view index in the CURRENT snapshot, or −1 (filtered out / collapsed away).
    /// One O(view) scan per publish, amortized over every consumer (detail placement, cell-range
    /// membership, focus re-anchoring) — <see cref="DataViewSnapshot.IndexOfRow"/> is the cold
    /// single-shot alternative.
    /// </summary>
    internal int ViewIndexOfRow(int rowId)
    {
        var snapshot = Snapshot;
        if (_viewIndexByRowId is null || _viewIndexMapVersion != snapshot.Version)
        {
            var map = _viewIndexByRowId ??= [];
            map.Clear();
            for (int i = 0; i < snapshot.Count; i++)
            {
                var row = snapshot.GetRow(i);
                if (!row.IsGroup)
                    map[row.RowId] = i;
            }
            _viewIndexMapVersion = snapshot.Version;
        }
        return _viewIndexByRowId.GetValueOrDefault(rowId, -1);
    }

    // ── Horizontal scrolling (§9.2 — grid-owned; the SCP's axis is vertical only) ────────────────

    /// <summary>
    /// The set-time reaction: clamp (re-entering once with the corrected value), commit a hosted
    /// editor the tick would slide under the frozen region or off-viewport (the §9.2 hosted-children
    /// policy; cancel on commit-failure so the editor never floats detached from its cell), then
    /// re-arrange/re-ink the presenters. The band cache is NEVER dirtied — offsets don't change
    /// per-row strings.
    /// </summary>
    private void OnHorizontalOffsetChanged(int value)
    {
        int clamped = ClampHorizontalOffset(value);
        if (clamped != value)
        {
            SetValue(HorizontalOffsetProperty, clamped);
            return; // the re-entrant change carries the work
        }

        if (RowsPresenter is { IsEditing: true } presenter)
        {
            var layout = presenter.ColumnLayout;
            int editColumn = presenter.EditCell.ColumnIndex;
            if (editColumn >= layout.FrozenCount && editColumn < layout.Entries.Count)
            {
                var entry = layout.Entries[editColumn];
                int drawX = entry.X - value;
                bool hidden = drawX + DataGridColumnLayout.CellPadding < layout.FrozenWidth ||
                              drawX >= Math.Max(1, presenter.ViewportColumns);
                if (hidden && !CommitEdit())
                    CancelEdit();
            }
        }

        // The auto-filter row's roving editor is a hosted child under the SAME policy (audit
        // W2-5): sliding under the frozen region (or off-viewport) commits it; an unparseable
        // draft cancels instead of floating detached from its cell.
        if (AutoFilterRow is { IsEditing: true } filterRow && RowsPresenter is { } rows)
        {
            var layout = rows.ColumnLayout;
            int editColumn = filterRow.EditColumnIndex;
            if (editColumn >= layout.FrozenCount && editColumn < layout.Entries.Count)
            {
                var entry = layout.Entries[editColumn];
                int drawX = entry.X - value;
                bool hidden = drawX + DataGridColumnLayout.CellPadding < layout.FrozenWidth ||
                              drawX >= Math.Max(1, rows.ViewportColumns);
                if (hidden && !filterRow.CommitEdit())
                    filterRow.EndEdit();
            }
        }

        RowsPresenter?.OnHorizontalOffsetChanged();
        UpdateHorizontalScrollBar();
    }

    /// <summary>The §9.2 clamp: pre-layout values pass through (the post-measure re-clamp settles them).</summary>
    private int ClampHorizontalOffset(int value)
    {
        if (value < 0)
            return 0;
        var rows = RowsPresenter;
        if (rows is null || rows.ViewportColumns <= 0)
            return value;
        return Math.Min(value, Math.Max(0, rows.ColumnLayout.TotalWidth - rows.ViewportColumns));
    }

    /// <summary>
    /// The presenter's post-measure callback (§9.2 — the end-of-arrange re-coercion analog): the
    /// resolved layout may have shrunk under the current offset (hide/resize/viewport change), so
    /// re-clamp and refresh the grid-owned bar's range. Runs inside the layout pass — a corrective
    /// set converges under the 16-pass fixpoint.
    /// </summary>
    internal void OnColumnGeometryResolved()
    {
        int current = HorizontalOffset;
        int clamped = ClampHorizontalOffset(current);
        if (clamped != current)
            SetValue(HorizontalOffsetProperty, clamped);
        UpdateHorizontalScrollBar();
    }

    /// <summary>Syncs the grid-owned horizontal bar (§9.2): range, viewport, silent value mirror,
    /// and overflow-gated visibility (no bar when everything fits).</summary>
    private void UpdateHorizontalScrollBar()
    {
        if (_hScrollBar is not { } bar || RowsPresenter is not { } rows)
            return;

        int viewport = Math.Max(1, rows.ViewportColumns);
        int total = rows.ColumnLayout.TotalWidth;
        bar.Maximum = Math.Max(0, total - viewport);
        bar.ViewportSize = viewport;
        bar.SetValueSilently(HorizontalOffset);
        bar.Visibility = total > viewport ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnHorizontalScroll(object? sender, ScrollEventArgs e)
        => HorizontalOffset = (int)Math.Round(e.NewValue);

    /// <summary>
    /// Scrolls a column entry into the visible window (§9.2): fixed entries are always visible
    /// (no-op); a scrolling entry lands minimally inside
    /// <c>[HorizontalOffset + frozenWidth, HorizontalOffset + viewport)</c>, leading-edge-aligned
    /// when wider than the window. Called from focus-cell moves, <see cref="SetFocusCell"/>, and
    /// <see cref="BeginEdit()"/>/Tab-advance (the hosted-editor clear-of-frozen guarantee).
    /// </summary>
    public void ScrollColumnIntoView(int columnIndex)
    {
        var rows = RowsPresenter;
        if (rows is null)
            return;
        var layout = rows.ColumnLayout;
        if (columnIndex < layout.FrozenCount || columnIndex >= layout.Entries.Count)
            return; // fixed (always visible) or out of range

        var entry = layout.Entries[columnIndex];
        int slot = entry.Width + 2 * DataGridColumnLayout.CellPadding;
        int viewport = Math.Max(1, rows.ViewportColumns);
        int offset = HorizontalOffset;

        if (slot > viewport - layout.FrozenWidth || entry.X - offset < layout.FrozenWidth)
            HorizontalOffset = entry.X - layout.FrozenWidth; // hidden left / wider than the window → leading edge
        else if (entry.X + slot - offset > viewport)
            HorizontalOffset = entry.X + slot - viewport;    // hidden right → minimal scroll
    }

    /// <summary>
    /// §9.2: the grid owns Shift+wheel and horizontal wheel deltas — routed into
    /// <see cref="HorizontalOffset"/> and handled EVEN at the extremes, so an outer scroller never
    /// captures the gesture mid-grid. (Vertical wheel is left for the ScrollViewer part below us in
    /// the route; it arrives here only when the SV couldn't consume it.)
    /// </summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled)
            return;

        bool horizontal = e.WheelDeltaX != 0 || (e.Modifiers & KeyModifiers.Shift) != 0;
        if (!horizontal)
            return;

        int delta = e.WheelDeltaX != 0 ? e.WheelDeltaX : -e.WheelDeltaY;
        HorizontalOffset += delta / 120 * e.LinesPerNotch;
        e.Handled = true;
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
            // A directly-assigned tree invalidates the expression surface's stored SOURCE TEXT
            // (§9.1 — the text belongs to the tree it lowered from); the editor re-derives via
            // ToText on its next open. TryApplyFilterExpression writes both sides together.
            _filterExpressionText = null;
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
    public const string PartHScrollBar = "PART_HScrollBar";

    private ScrollViewer? _scrollViewer;
    private DataGridHeaderPresenter? _header;
    private ScrollBar? _hScrollBar;

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
        // §9.2: the grid's HorizontalOffset is the one horizontal truth — every band presenter
        // re-binds to IT (the SCP's horizontal axis is disabled; its offset would coerce to 0).
        if (_header is not null)
        {
            _header.Owner = this;
            _header.SetBinding(DataGridHeaderPresenter.HorizontalOffsetProperty,
                               new Binding(nameof(HorizontalOffset)) { Source = this });
        }
        if (AutoFilterRow is not null)
        {
            AutoFilterRow.Owner = this;
            AutoFilterRow.SetBinding(DataGridAutoFilterRow.HorizontalOffsetProperty,
                                     new Binding(nameof(HorizontalOffset)) { Source = this });
        }
        if (footer is not null)
        {
            footer.Owner = this;
            footer.SetBinding(DataGridSummaryPresenter.HorizontalOffsetProperty,
                              new Binding(nameof(HorizontalOffset)) { Source = this });
        }

        // The grid-owned horizontal bar (§9.2 — the SV part cannot host it: its bar wiring pins to
        // the SCP offset, which coerces to 0 once CanScrollHorizontally is false).
        _hScrollBar = GetTemplatePart<ScrollBar>(PartHScrollBar);
        if (_hScrollBar is { } hBar)
        {
            hBar.Scroll += OnHorizontalScroll;
            UpdateHorizontalScrollBar();
        }
    }

    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        _filterPopup?.Close(); // the popup anchors to template parts about to detach
        _columnChooser?.Close(); // ditto — its placement target is the header part
        _gridMenu?.Close();      // ditto — the rows presenter anchors it
        if (_hScrollBar is { } hBar)
            hBar.Scroll -= OnHorizontalScroll;
        _hScrollBar = null;
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

        // An ItemsSource swap is a wholesale id-space change (audit W2-12): every id-keyed store
        // resets — recycled ids from the NEW controller must never inherit old state.
        ClearCellRange();
        _expandedDetails.Clear();
        _focusRowId = -1;

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
        _controller.AttachSource(source, EffectiveLiveUpdates);
        _controller.SnapshotChanged += (_, _) => RaiseSnapshotChanged();
        // Row-id hygiene (final-audit fix): removed ids leave the selection BEFORE their slots can
        // recycle onto new rows; a source reset clears id-keyed state wholesale.
        // Row-id hygiene fans out to EVERY id-keyed store (audit W2-12 — the cell-range corners
        // and detail expansions get the same treatment as the selection: a freed slot's id must
        // never re-attach state to the unrelated row that recycles it).
        _controller.RowsRemoved += ids =>
        {
            RowSelection.HandleRowsRemoved(ids);
            HandleRowsRemovedForCellRange(ids);
            if (_expandedDetails.Count > 0)
            {
                foreach (int id in ids)
                    _expandedDetails.Remove(id);
            }
            if (_focusRowId >= 0 && Contains(ids, _focusRowId))
                _focusRowId = -1; // the view-space fallback carries the focus (audit W2-13)
            if (RowsPresenter is { IsEditing: true } editing && editing.EditRowId >= 0 &&
                Contains(ids, editing.EditRowId))
            {
                CancelEdit(); // the edited row was removed — discard before its slot can recycle
            }
        };
        _controller.RowsAdded += ids => RowSelection.HandleRowsAdded(ids);
        _controller.RowsReset += (_, _) =>
        {
            RowSelection.Clear();
            ClearCellRange();
            _expandedDetails.Clear();
            _focusRowId = -1;
        };

        static bool Contains(IReadOnlyList<int> ids, int id)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                if (ids[i] == id)
                    return true;
            }
            return false;
        }
        PushShape();
    }

    private void RaiseSnapshotChanged()
    {
        ReanchorFocusRow();     // audit W2-13: focus follows its row id — the prunes read the RESULT
        ReanchorEditSession();  // the open editor rides its row id (live-canary fix)
        PruneExpandedDetails(); // §9.3: a row id that left the view drops its pane
        PruneCellRange();       // §9.4: a lost corner collapses the range to the focus cell
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
        InvalidateMeasure();
    }

    /// <summary>
    /// Re-anchors an open edit session against the NEW snapshot (live-canary fix — the gallery's
    /// feed): a data-row session's view slot re-resolves through the id map so the hosted editor
    /// rides its row; a row that left the view cancels the session (the DevExpress behavior — an
    /// edit on a vanished row is discarded, never written to whatever slid under it). The new-row
    /// placeholder session re-anchors to the ghost row (view == Count, which moves with membership).
    /// </summary>
    private void ReanchorEditSession()
    {
        if (RowsPresenter is not { IsEditing: true } presenter)
            return;

        if (_pendingNewRow is not null)
        {
            presenter.ReanchorEditRow(Snapshot.Count);
            return;
        }

        int view = presenter.EditRowId >= 0 ? ViewIndexOfRow(presenter.EditRowId) : -1;
        if (view < 0)
            CancelEdit();
        else
            presenter.ReanchorEditRow(view);
    }

    /// <summary>Row type discovery: the source's <c>IEnumerable&lt;T&gt;</c> (first closed interface), else the first item's runtime type.
    /// Value-type rows are first-class (§9.6 — the engine guards the INPC lane itself).</summary>
    private static Type? DiscoverRowType(IEnumerable source)
    {
        foreach (var candidate in source.GetType().GetInterfaces())
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                var arg = candidate.GetGenericArguments()[0];
                if (arg != typeof(object))
                    return arg;
            }
        }

        foreach (var item in source)
            return item?.GetType();
        return null;
    }

    // ── Columns ──────────────────────────────────────────────────────────────────────────────────

    // Audit W2-6: runtime writes to a column's layout-bearing properties (Width/Min/Max/Visible/
    // Fixed) must reach the geometry funnel without the author calling an internal method — each
    // member column's GeometryChanged event routes here. The set tracks membership so a removed
    // column stops notifying and a Reset re-syncs.
    private readonly HashSet<DataGridColumn> _geometrySubscribed = [];

    private void SyncColumnGeometrySubscriptions()
    {
        if (_geometrySubscribed.Count > 0)
        {
            List<DataGridColumn>? gone = null;
            foreach (var column in _geometrySubscribed)
            {
                if (!Columns.Contains(column))
                    (gone ??= []).Add(column);
            }
            if (gone is not null)
            {
                foreach (var column in gone)
                {
                    column.GeometryChanged -= OnColumnGeometryPropertyChanged;
                    _geometrySubscribed.Remove(column);
                }
            }
        }
        foreach (var column in Columns)
        {
            if (_geometrySubscribed.Add(column))
                column.GeometryChanged += OnColumnGeometryPropertyChanged;
        }
    }

    private void OnColumnGeometryPropertyChanged(object? sender, EventArgs e) => NotifyColumnGeometryChanged();

    private void OnColumnsChanged(NotifyCollectionChangedEventArgs e)
    {
        SyncColumnGeometrySubscriptions(); // before the generation early-out — generated columns notify too

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
        {
            foreach (var rule in column.FormatRules)
            {
                // The Enabled gate (the rules manager's On toggle) lives HERE, in the collection
                // funnel: a disabled rule never reaches the engine, so band-fill respects the flag
                // without the controller's compiled kit knowing it exists. (TopBottom rules ride
                // the same funnel now that the §9.5 TopK seam is live.)
                if (!rule.Enabled)
                    continue;
                rules.Add(rule);
            }
        }
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

    /// <summary>The focus row in VIEW space (−1 none; re-anchored per snapshot by row id when
    /// possible — audit W2-13 made the doc claim true: the setter captures the focused DATA row's
    /// id, and every publish re-projects it through the id→viewIndex map).</summary>
    public int FocusViewIndex
    {
        get => _focusViewIndex;
        private set
        {
            _focusViewIndex = value;
            var snapshot = Snapshot;
            _focusRowId = value >= 0 && value < snapshot.Count &&
                          snapshot.GetRow(value) is { IsGroup: false, RowId: >= 0 } row
                ? row.RowId
                : -1;
        }
    }

    private int _focusViewIndex = -1;
    private int _focusRowId = -1;

    /// <summary>Re-projects the focus row by id against the NEW snapshot (per publish, before the
    /// id-keyed prunes read it); a focus id that left the view falls back to the clamped view slot.</summary>
    private void ReanchorFocusRow()
    {
        var snapshot = Snapshot;
        if (_focusRowId >= 0 && ViewIndexOfRow(_focusRowId) is >= 0 and var view)
        {
            _focusViewIndex = view; // the id is unchanged — bypass the setter's re-capture
            return;
        }
        _focusRowId = -1;
        _focusViewIndex = Math.Min(_focusViewIndex, Math.Max(-1, snapshot.Count - 1));
    }

    /// <summary>The focus cell's visible-column index (−1 = whole-row focus). A cell focus move
    /// auto-scrolls the entry into the visible window (§9.2 — focus never lands hidden).</summary>
    public int FocusColumnIndex
    {
        get => _focusColumnIndex;
        private set
        {
            if (_focusColumnIndex == value)
                return;
            _focusColumnIndex = value;
            if (value >= 0)
                ScrollColumnIntoView(value);
        }
    }

    private int _focusColumnIndex = -1;

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
        ExitBandFocus(); // a rows press returns the §3.3 virtual focus to the rows

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

        // A data row's expander (the §9.3 gutter zone): toggle the detail pane; the press still
        // focuses the row below.
        if (onExpander)
        {
            ToggleDetail(row.RowId);
            FocusViewIndex = viewIndex;
            RowsPresenter?.InvalidateBand();
            return;
        }

        FocusViewIndex = viewIndex;
        FocusColumnIndex = columnIndex;

        if (SelectionUnit == DataGridSelectionUnit.Cell)
        {
            // §9.4: the cell-range lanes — Shift extends the lead corner, a plain press re-anchors.
            var column = ColumnAtEntry(columnIndex);
            if ((modifiers & KeyModifiers.Shift) != 0)
                ExtendCellRangeTo(row.RowId, column);
            else
                SetCellRangeAnchor(row.RowId, column);
        }
        else if ((modifiers & KeyModifiers.Shift) != 0 && _selectionAnchorViewIndex >= 0)
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
        ScrollColumnIntoView(columnIndex); // §9.2: the hosted editor must sit clear of the frozen region
        presenter.BeginEdit(FocusViewIndex, columnIndex, kind, _controller.FormatCell(row.RowId, column),
                            kind == DataGridEditorKind.Combo ? BuildComboItems(column) : null,
                            rowId: row.RowId); // the session's ONE identity (live publishes permute the view)
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
            // cold re-attach (rebuild + reshape; acceptable for the add-a-row cold path). The
            // §9.6 struct degrade rides EffectiveLiveUpdates here too (audit F1: passing the raw
            // LiveUpdates threw the engine's value-type guard mid-commit, AFTER the row was added).
            if (list is not INotifyCollectionChanged)
                _controller.AttachSource(list, EffectiveLiveUpdates);
            return true;
        }

        // The commit writes through the session's ROW ID (live-canary fix): under live updates the
        // view permutes beneath the open editor, and the old view-index lookup either aliased a
        // DIFFERENT row (silent wrong-row write) or fell off the shrunken view (the silently
        // discarded commit the gallery report hit). A row that left the VIEW entirely (refilter/
        // removal) discards the edit — the per-publish re-anchor cancels it before we ever get
        // here in practice; this is the same-frame backstop.
        int editRowId = presenter.EditRowId;
        if (editRowId < 0 || ViewIndexOfRow(editRowId) < 0)
        {
            presenter.EndEditVisual();
            return false;
        }

        if (!_controller.TrySetCellFromText(editRowId, column, text))
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

        // The §9.3 stand-down guard applies on the TUNNEL too (audit W2-10): keys typed inside a
        // hosted detail pane belong to the pane, never to an unrelated open cell editor.
        if (editing.FocusedDetailElement() is not null)
            return;

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

        // The §3.3 virtual band router: while a band holds the virtual focus, it owns the keys
        // (F6 advances the cycle, Esc returns to the rows).
        if (_focusBand != DataGridFocusBand.Rows)
        {
            HandleBandKey(e);
            return;
        }

        // The §9.3 detail stand-down guard (the popup/editor precedent): focus inside a hosted
        // detail pane means the pane owns the keyboard — the grid takes ONLY Esc (return to the
        // grid, focus intact on the anchor row).
        if (RowsPresenter?.FocusedDetailElement() is not null)
        {
            if (e.Key == Key.Escape)
            {
                Focus(FocusNavigationMethod.Programmatic);
                e.Handled = true;
            }
            return;
        }

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

        // The Menu key opens the grid command menu at the focus cell (the keyboard twin of the
        // rows right-click — the reachability surface for the dialogs + summaries).
        if (e.Key == Key.Menu)
        {
            OpenGridContextMenu(FocusColumnIndex);
            e.Handled = true;
            return;
        }

        // The §3.3 band-cycle entry gestures: F6 anywhere advances rows → header → group panel →
        // auto-filter → rows; Ctrl+Up from row 0 steps straight into the header (the pinned walk).
        if (e.Key == Key.F6)
        {
            AdvanceBandCycle();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.UpArrow && ctrl && FocusViewIndex <= 0)
        {
            FocusHeaderBand();
            e.Handled = true;
            return;
        }

        // The §9.3 detail keyboard cluster — BEFORE the row-nav switch (its plain-arrow arms would
        // otherwise swallow the Ctrl-modified gestures): Ctrl+Right expands the focused data row's
        // pane, Ctrl+Left collapses, Ctrl+Down enters it (Esc returns via the stand-down guard).
        if (ctrl && DetailTemplate is not null && FocusViewIndex >= 0 && FocusViewIndex < snapshot.Count &&
            snapshot.GetRow(FocusViewIndex) is { IsGroup: false, RowId: >= 0 } detailAnchor)
        {
            switch (e.Key)
            {
                case Key.RightArrow:
                    ExpandDetail(detailAnchor.RowId);
                    e.Handled = true;
                    return;
                case Key.LeftArrow:
                    CollapseDetail(detailAnchor.RowId);
                    e.Handled = true;
                    return;
                case Key.DownArrow when IsDetailExpanded(detailAnchor.RowId):
                {
                    // Audit W2-9: an expanded-but-unrealized pane must not fall through to the
                    // plain Down arm (a silent focus move). Bring the anchor in and retry after
                    // realization rides the next measure — the parked-work idiom.
                    if (RowsPresenter is { } rowsPresenter && !rowsPresenter.TryFocusDetail(detailAnchor.RowId))
                    {
                        int rowId = detailAnchor.RowId;
                        ScrollRowIntoView(FocusViewIndex);
                        UIApplication.Current?.Dispatcher.Post(() => RowsPresenter?.TryFocusDetail(rowId));
                    }
                    e.Handled = true;
                    return;
                }
            }
        }

        int? target = e.Key switch
        {
            Key.UpArrow => Math.Max(0, current - 1),
            Key.DownArrow => Math.Min(lastNavigable, current + 1),
            // §9.3 (audit W2-8): a page covers a VIEWPORT of CONTENT rows — with expanded panes,
            // view rows and content rows diverge, so the jump steps in content-y and maps back.
            Key.PageUp => Math.Max(0, PageTargetView(current, -viewport)),
            Key.PageDown => Math.Min(lastNavigable, PageTargetView(current, viewport)),
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

            // Data rows: Left/Right move the focus cell (§9.4 cell mode: Shift extends the lead
            // corner to the new column, a plain move re-anchors).
            case Key.LeftArrow or Key.RightArrow when !focused.IsGroup:
            {
                int visible = RowsPresenter?.ColumnLayout.Entries.Count ?? Columns.Count(c => c.Visible);
                int next = e.Key == Key.LeftArrow
                    ? Math.Max(0, (FocusColumnIndex < 0 ? 0 : FocusColumnIndex) - 1)
                    : Math.Min(Math.Max(0, visible - 1), FocusColumnIndex + 1);
                FocusColumnIndex = next;
                // Audit W2-11: `focused` is the DEFAULT carrier on the placeholder / with no focus
                // row (RowId 0 would alias a real slot) — the range writes need a REAL data row.
                bool focusedIsData = !onPlaceholder && FocusViewIndex >= 0 && FocusViewIndex < snapshot.Count &&
                                     !focused.IsGroup && focused.RowId >= 0;
                if (SelectionUnit == DataGridSelectionUnit.Cell && focusedIsData)
                {
                    if (shift)
                        ExtendCellRangeTo(focused.RowId, ColumnAtEntry(next));
                    else
                        SetCellRangeAnchor(focused.RowId, ColumnAtEntry(next));
                }
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

            // Ctrl+A — select all (the compact inversion). Row mode ONLY (§9.4: cell mode keeps
            // its one rectangle; select-all is left unhandled there).
            case Key.Character when ctrl && e.Text.Length == 1 && (e.Text.Span[0] is 'a' or 'A') &&
                                    SelectionUnit == DataGridSelectionUnit.Row:
                RowSelection.SelectAll();
                RowsPresenter?.InvalidateBand();
                e.Handled = true;
                return;

            // Space selects (the modifier-free wire is (Character, " ") — ND10). Never on the
            // placeholder: its default `focused` carries RowId 0, which would alias a real row.
            // ROW mode only (audit W2-14): cell mode keeps its one rectangle — a row-selection
            // write would re-create the mixed state the §9.4 mode switch exists to clear.
            case Key.Character or Key.Space when IsSpace(e) && !onPlaceholder && !focused.IsGroup && focused.RowId >= 0 &&
                                                 SelectionUnit == DataGridSelectionUnit.Row:
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
            if (SelectionUnit == DataGridSelectionUnit.Cell)
            {
                // §9.4: the lead follows the focus onto data rows (it passes THROUGH group rows
                // keeping its column — those never update it); a plain move re-anchors.
                var column = ColumnAtEntry(FocusColumnIndex);
                if (shift)
                    ExtendCellRangeTo(row.RowId, column);
                else if (!ctrl)
                    SetCellRangeAnchor(row.RowId, column);
            }
            else if (shift && _selectionAnchorViewIndex >= 0)
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
        var layout = RowsPresenter.ColumnLayout;
        for (int i = 0; i < layout.Entries.Count; i++)
        {
            var entry = layout.Entries[i];
            if (ReferenceEquals(entry.Column, column))
            {
                // §9.2 split: a frozen entry anchors unshifted; a scrolling one shifts.
                cellX = i < layout.FrozenCount ? entry.X : entry.X - HorizontalOffset;
                break;
            }
        }

        _filterPopup ??= new DataGridFilterPopup(this);
        _filterPopup.Open(column, _header, cellX);
    }

    /// <summary>
    /// Copies the selection to the terminal clipboard (OSC 52) as TSV — formatted values, view
    /// order. Row mode: selected rows across visible columns (falls back to the focus row); cell
    /// mode (§9.4): the derived rectangle, group rows skipped (never members).
    /// </summary>
    public void CopySelectionToClipboard()
    {
        if (_controller is null)
            return;

        var snapshot = Snapshot;

        if (SelectionUnit == DataGridSelectionUnit.Cell)
        {
            if (BuildCellRangeTsv() is { } rectangle)
                UIApplication.Current?.Clipboard.SetText(rectangle);
            return;
        }

        var ids = RowSelection.IsEmpty
            ? FocusRowIdOrEmpty(snapshot)
            : RowSelection.MaterializeSelectedIds(snapshot);
        if (ids.Count == 0)
            return;

        // Copy in DISPLAY order (§9.2: fixed columns lead the layout regardless of declaration order).
        var visible = RowsPresenter?.ColumnLayout.Entries.Select(entry => entry.Column).ToList()
                      ?? Columns.Where(c => c.Visible).ToList();
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

    /// <summary>The §9.4 rectangle as TSV (formatted values, display order, group rows skipped —
    /// they are never members), or null without a derivable range. The Ctrl+C payload.</summary>
    internal string? BuildCellRangeTsv()
    {
        if (_controller is null || CellRangeViewRect() is not { } range || RowsPresenter is null)
            return null;

        var snapshot = Snapshot;
        var entries = RowsPresenter.ColumnLayout.Entries;
        var rectangle = new System.Text.StringBuilder();
        for (int view = range.FirstRow; view <= range.LastRow && view < snapshot.Count; view++)
        {
            var viewRow = snapshot.GetRow(view);
            if (viewRow.IsGroup)
                continue;
            for (int c = range.FirstColumn; c <= range.LastColumn && c < entries.Count; c++)
            {
                if (c > range.FirstColumn)
                    rectangle.Append('\t');
                rectangle.Append(_controller.FormatCell(viewRow.RowId, entries[c].Column));
            }
            rectangle.Append('\n');
        }
        return rectangle.ToString();
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

    /// <summary>A page-jump's target view row (audit W2-8): the current view row's content-y, plus
    /// a viewport of content rows, mapped back to a view row (identity without expanded panes).</summary>
    private int PageTargetView(int currentView, int deltaContentRows)
    {
        if (RowsPresenter is not { } presenter)
            return currentView + deltaContentRows;
        int y = presenter.ContentYOf(Math.Max(0, currentView)) + deltaContentRows;
        return presenter.ViewIndexAtY(Math.Max(0, y)).ViewIndex;
    }

    /// <summary>Brings a view row into the viewport (the drawn-rows analog of bring-into-view —
    /// §3.1). The SCP offset is CONTENT-y, so the row maps through the §9.3 content-y map first.</summary>
    public void ScrollRowIntoView(int viewIndex)
    {
        if (RowsPresenter is not { ScrollOwner: { } scp } presenter)
            return;

        int y = presenter.ContentYOf(viewIndex);
        int offset = scp.ScrollOffsetRow;
        int viewportRows = Math.Max(1, presenter.PageStep(0, 1, vertical: true) + 1);
        if (y < offset)
            scp.ScrollOffsetRow = y;
        else if (y >= offset + viewportRows)
            scp.ScrollOffsetRow = y - viewportRows + 1;
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

    // ── The filter/formatting dialog suite (§9.1 — expression editor, Filter Builder, rules manager) ──

    private string? _filterExpressionText;
    private DataGridExpressionEditor? _expressionEditor;
    private DataGridFilterBuilder? _filterBuilder;
    private DataGridRulesManager? _rulesManager;

    /// <summary>
    /// The criteria SOURCE TEXT behind <see cref="Filter"/> when it was authored through the
    /// expression surface (§9.1 panel amendment: a Custom-lowered filter does not text-round-trip,
    /// so the ORIGINAL text is retained grid-side and re-seeds the editor). Setting it parses +
    /// applies (the <see cref="TryApplyFilterExpression"/> lane); an invalid assignment changes
    /// nothing — use the Try method to observe diagnostics. Cleared by a direct
    /// <see cref="Filter"/> write (a foreign tree invalidates the text) and by the Filter Builder's
    /// OK (the tree is then builder-authored; the editor re-derives text via
    /// <see cref="CriteriaExpression.ToText"/>).
    /// </summary>
    public string? FilterExpressionText
    {
        get => _filterExpressionText;
        set => TryApplyFilterExpression(value, out _);
    }

    /// <summary>
    /// Parses <paramref name="text"/> through the §9.1 pipeline and applies it as the programmatic
    /// <see cref="Filter"/>, storing the source text for round-trip. Null/whitespace clears both.
    /// Invalid text applies NOTHING (filter and text keep their values) and returns false with the
    /// positioned diagnostics.
    /// </summary>
    public bool TryApplyFilterExpression(string? text, out IReadOnlyList<CriteriaDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            diagnostics = [];
            _filter = null;
            _filterExpressionText = null;
            ScheduleShapePush();
            return true;
        }

        if (_rowType is not { } rowType)
        {
            diagnostics = [new CriteriaDiagnostic("No row source — attach an ItemsSource before applying a filter expression.", 0, 1)];
            return false;
        }

        var result = CriteriaExpression.ToFilterNode(text, rowType, BuildCriteriaFields());
        if (!result.IsValid)
        {
            diagnostics = result.Diagnostics;
            return false;
        }

        diagnostics = [];
        _filter = result.Filter;
        _filterExpressionText = text; // both sides together — the one write that keeps text and tree in step
        ScheduleShapePush();
        return true;
    }

    /// <summary>The row type the shaping engine discovered (null before a source attaches).</summary>
    internal Type? RowType => _rowType;

    /// <summary>
    /// The columns as criteria-language fields (§9.1 binding: canonical name = FieldName, display
    /// alias = the header, the selector from the authored KeySelector or a compiled property-path
    /// lambda; the column's SortMode is the string-comparison authority). Columns without a shaping
    /// identity (no field name or selector) are not bindable and are skipped.
    /// </summary>
    internal IReadOnlyList<CriteriaExpression.Field> BuildCriteriaFields()
    {
        var fields = new List<CriteriaExpression.Field>();
        if (_rowType is not { } rowType)
            return fields;

        foreach (var column in Columns)
        {
            var selector = column.KeySelector;
            if (selector is null && column.FieldName is { } fieldName)
            {
                try
                {
                    selector = ShapingCodegen.BuildPropertyPathLambda(rowType, fieldName);
                }
                catch (ArgumentException)
                {
                    continue; // an unresolvable path isn't a bindable field (the engine skips it too)
                }
            }
            if (selector is null)
                continue;

            string name = column.FieldName ?? column.EffectiveHeader;
            if (name.Length == 0)
                continue;
            fields.Add(new CriteriaExpression.Field(name, column.EffectiveHeader, selector, column, column.SortMode));
        }
        return fields;
    }

    /// <summary>The criteria field name for a filter node's column key (the ToText rendering seam).</summary>
    internal string CriteriaFieldName(object columnKey)
        => columnKey is DataGridColumn column
            ? column.FieldName ?? column.EffectiveHeader
            : columnKey as string ?? columnKey.ToString() ?? "?";

    /// <summary>The live expression-editor dialog (tests reach its content; null when closed).</summary>
    internal DataGridExpressionEditor? ActiveFilterEditor
        => _expressionEditor is { IsOpen: true } editor ? editor : null;

    /// <summary>The live Filter Builder dialog (tests reach its content; null when closed).</summary>
    internal DataGridFilterBuilder? ActiveFilterBuilder
        => _filterBuilder is { IsOpen: true } builder ? builder : null;

    /// <summary>The live rules-manager dialog (tests reach its content; null when closed).</summary>
    internal DataGridRulesManager? ActiveRulesManager
        => _rulesManager is { IsOpen: true } manager ? manager : null;

    /// <summary>
    /// Opens the criteria text editor (§9.1; the mockup's "Filter — text editor") seeded from
    /// <see cref="FilterExpressionText"/>, else from <see cref="Filter"/> via
    /// <see cref="CriteriaExpression.ToText"/>. Completes true when Apply landed a filter.
    /// The body runs via <c>Dispatcher.InvokeAsync</c> deliberately (all three dialog entry points
    /// do): the async core then executes UNDER the application's UI synchronization context, so
    /// every post-await continuation marshals back to the dispatcher queue — headless tests calling
    /// from a bare thread context stay deterministic (the queue keeps <c>RunUntilIdle</c> pumping),
    /// and no continuation can ever touch UI state from a thread-pool thread (invariant 6).
    /// </summary>
    public Task<bool> OpenFilterEditorAsync()
    {
        var application = UIApplication.Current;
        if (application?.WindowManager is null)
            return Task.FromResult(false); // no windowing surface (pure headless engine hosting)
        return application.Dispatcher.InvokeAsync(() => OpenFilterEditorCoreAsync(seedText: null));
    }

    private async Task<bool> OpenFilterEditorCoreAsync(string? seedText)
    {
        string seed = seedText ??
                      _filterExpressionText ??
                      (_filter is { } filter ? CriteriaExpression.ToText(filter, CriteriaFieldName) : string.Empty);
        var editor = new DataGridExpressionEditor(this, seed);
        _expressionEditor = editor;
        try
        {
            return await editor.ShowAsync();
        }
        finally
        {
            if (ReferenceEquals(_expressionEditor, editor))
                _expressionEditor = null;
        }
    }

    /// <summary>
    /// Opens the visual Filter Builder (§9.1; the mockup's condition tree) seeded from the current
    /// <see cref="Filter"/>. OK applies the rebuilt tree; "ƒ Edit as Text" chains into the
    /// expression editor pre-seeded with the model's criteria text (the one-way hop the mockup
    /// draws). Completes true when either surface applied a filter.
    /// </summary>
    public Task<bool> OpenFilterBuilderAsync()
    {
        var application = UIApplication.Current;
        if (application?.WindowManager is null)
            return Task.FromResult(false);
        return application.Dispatcher.InvokeAsync(OpenFilterBuilderCoreAsync);
    }

    private async Task<bool> OpenFilterBuilderCoreAsync()
    {
        var builder = new DataGridFilterBuilder(this);
        _filterBuilder = builder;
        FilterBuilderOutcome outcome;
        try
        {
            outcome = await builder.ShowAsync();
        }
        finally
        {
            if (ReferenceEquals(_filterBuilder, builder))
                _filterBuilder = null;
        }

        return outcome switch
        {
            FilterBuilderOutcome.Applied => true,
            FilterBuilderOutcome.EditAsText => await OpenFilterEditorCoreAsync(builder.EditAsTextSeed),
            _ => false,
        };
    }

    /// <summary>
    /// Opens the conditional-formatting rules manager (§2.7 UI; the mockup's <c>cfmgr</c>). Edits
    /// apply LIVE (toggle/reorder/delete re-collect into the engine immediately); the task
    /// completes when the window closes.
    /// </summary>
    public Task OpenRulesManagerAsync()
    {
        var application = UIApplication.Current;
        if (application?.WindowManager is null)
            return Task.CompletedTask;
        return application.Dispatcher.InvokeAsync(OpenRulesManagerCoreAsync);
    }

    private async Task OpenRulesManagerCoreAsync()
    {
        var manager = new DataGridRulesManager(this);
        _rulesManager = manager;
        try
        {
            await manager.ShowAsync();
        }
        finally
        {
            if (ReferenceEquals(_rulesManager, manager))
                _rulesManager = null;
        }
    }

    // ── Teardown ─────────────────────────────────────────────────────────────────────────────────

    protected override void OnTearDown()
    {
        _filterPopup?.Close(); // release the popup surface before the controller it reads goes away
        _columnChooser?.Close();
        _expressionEditor?.CloseWindow(); // the dialog windows read the grid/controller — close first
        _filterBuilder?.CloseWindow();
        _rulesManager?.CloseWindow();
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
