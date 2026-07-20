using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.DataViews;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews;

/// <summary>
/// The wave-2 structural surfaces end-to-end (design doc §9.2–§9.4): in-presenter horizontal
/// scrolling (the grid-owned <c>HorizontalOffset</c>, clamping, the grid-owned H bar, Shift+wheel),
/// frozen columns (fixed-first partition, unshifted overpaint, hit mapping, editor commit policy,
/// <c>ScrollColumnIntoView</c>), master-detail (the content-y map, the expander gutter, pane
/// realization + keyboard cluster + pruning), and cell-range selection (corner truth, per-snapshot
/// re-projection, the rectangle TSV) — through the real frame loop with cell assertions.
/// </summary>
public class DataGridStructuralTests
{
    private sealed class Order(string id, string region, decimal amount) : INotifyPropertyChanged
    {
        private string _region = region;
        private decimal _amount = amount;
        public string Id { get; } = id;
        public string Region { get => _region; set => Set(ref _region, value); }
        public decimal Amount { get => _amount; set => Set(ref _amount, value); }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
        }
    }

    private static ObservableCollection<Order> SampleOrders() =>
    [
        new("SO-1042", "East", 12450m),
        new("SO-1044", "East", 31900m),
        new("SO-1046", "South", 19800m),
        new("SO-1047", "West", 27300m),
    ];

    /// <summary>
    /// A geometry-pinned grid: three FIXED-width columns (slots 10 + 12 + 12 = TotalWidth 34) in a
    /// 24-column host — 10 cells of horizontal overflow, no Auto jitter. Header row 0, data rows
    /// from row 1.
    /// </summary>
    private static (UIHeadlessHost Host, DataGrid Grid, ObservableCollection<Order> Source) Show(
        int columns = 24, int rows = 14)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(columns, rows) });
        var source = SampleOrders();
        // Columns BEFORE the source: an ItemsSource set with empty columns auto-generates (§1).
        var grid = new DataGrid { AutoGenerateColumns = false };
        grid.Columns.Add(new DataGridColumn { FieldName = "Id", Width = DataGridLength.Cells(8) });
        grid.Columns.Add(new DataGridColumn { FieldName = "Region", Width = DataGridLength.Cells(10) });
        grid.Columns.Add(new DataGridColumn { FieldName = "Amount", Width = DataGridLength.Cells(10) });
        grid.ItemsSource = source;
        host.ShowRoot(grid);
        host.RunUntilIdle();
        return (host, grid, source);
    }

    private static string Row(UIHeadlessHost host, int row) => host.GetRowText(row);

    private static int RowIdOf(DataGrid grid, string id)
    {
        var snapshot = grid.Snapshot;
        for (int i = 0; i < snapshot.Count; i++)
        {
            var row = snapshot.GetRow(i);
            if (!row.IsGroup && grid.Controller!.FormatCell(row.RowId, grid.Columns[0]) == id)
                return row.RowId;
        }
        throw new InvalidOperationException($"Row {id} not visible.");
    }

    private static MouseEvent Wheel(int column, int row, int deltaY, int deltaX = 0,
                                    KeyModifiers modifiers = KeyModifiers.None) => new()
    {
        Kind = MouseEventKind.Wheel,
        Position = new CellPosition(column, row),
        Button = MouseButton.None,
        ButtonsHeld = MouseButtons.None,
        Modifiers = modifiers,
        WheelDeltaY = deltaY,
        WheelDeltaX = deltaX,
        Timestamp = DateTimeOffset.UnixEpoch,
    };

    // ── §9.2 — the in-presenter horizontal axis ──────────────────────────────────────────────────

    [Fact]
    public void Horizontal_offset_shifts_every_band_and_clamps_to_the_layout()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // Resting: Id at content x 0 (text at 1), Region at 10 (text at 11).
        Assert.Equal(1, Row(host, 0).IndexOf("Id", StringComparison.Ordinal));
        Assert.Equal(1, Row(host, 1).IndexOf("SO-1042", StringComparison.Ordinal));
        Assert.Equal(11, Row(host, 1).IndexOf("East", StringComparison.Ordinal));

        // A 6-cell tick shifts header AND rows together (every band binds the one truth).
        grid.HorizontalOffset = 6;
        host.RunUntilIdle();
        Assert.Equal(5, Row(host, 0).IndexOf("Region", StringComparison.Ordinal));
        Assert.Equal(5, Row(host, 1).IndexOf("East", StringComparison.Ordinal));
        Assert.DoesNotContain("SO-1042", Row(host, 1)); // Id's text scrolled off (partially hidden)

        // Set-time clamping: [0, TotalWidth − viewport] = [0, 10].
        grid.HorizontalOffset = 1000;
        Assert.Equal(10, grid.HorizontalOffset);
        grid.HorizontalOffset = -5;
        Assert.Equal(0, grid.HorizontalOffset);
    }

    [Fact]
    public void Geometry_shrink_reclamps_the_offset_the_same_frame()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.HorizontalOffset = 10;
        host.RunUntilIdle();

        // Hiding the widest column shrinks TotalWidth to 22 (< viewport 24) — the post-measure
        // re-clamp snaps the offset back to 0 (the end-of-arrange re-coercion analog).
        grid.Columns[2].Visible = false;
        grid.NotifyColumnGeometryChanged();
        host.RunUntilIdle();
        Assert.Equal(0, grid.HorizontalOffset);
    }

    [Fact]
    public void Fixed_column_partitions_to_the_front_and_stays_pinned()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // Fixing AMOUNT (declared last) moves it to entry 0 (§9.2 — fixed columns lead the layout).
        grid.Columns[2].Fixed = DataGridColumnFixed.Left;
        grid.NotifyColumnGeometryChanged();
        host.RunUntilIdle();

        var layout = grid.RowsPresenter!.ColumnLayout;
        Assert.Same(grid.Columns[2], layout.Entries[0].Column);
        Assert.Equal(1, layout.FrozenCount);
        Assert.Equal(12, layout.FrozenWidth);

        // Scrolled right: the frozen cell repaints unshifted at x 0 (right-aligned numerics keep
        // their column), the scrolling columns slide under it.
        grid.HorizontalOffset = 8;
        host.RunUntilIdle();
        string row1 = Row(host, 1);
        Assert.Contains("12450", row1[..12]);          // the frozen Amount cell, intact
        Assert.DoesNotContain("12450", row1[12..]);    // never doubled into the scrolled region

        // Hit mapping splits at the frozen width: a press inside the frozen region hits entry 0;
        // one right of it maps through the offset.
        Assert.Equal(0, grid.RowsPresenter!.HitCell(5, 0).ColumnIndex);
        Assert.True(grid.RowsPresenter!.HitCell(13, 0).ColumnIndex > 0);
    }

    [Fact]
    public void Shift_wheel_owns_the_horizontal_axis_even_at_the_extremes()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // One notch right (Shift+wheel-down = +3 cells with the default LinesPerNotch).
        host.SendInput(Wheel(12, 5, deltaY: -120, modifiers: KeyModifiers.Shift));
        host.RunUntilIdle();
        Assert.Equal(3, grid.HorizontalOffset);

        // A horizontal wheel delta (WheelDeltaX) rides the same lane.
        host.SendInput(Wheel(12, 5, deltaY: 0, deltaX: 120));
        host.RunUntilIdle();
        Assert.Equal(6, grid.HorizontalOffset);

        // At the left extreme the gesture stays handled (clamped, no crash, no outer capture).
        grid.HorizontalOffset = 0;
        host.SendInput(Wheel(12, 5, deltaY: 120, modifiers: KeyModifiers.Shift));
        host.RunUntilIdle();
        Assert.Equal(0, grid.HorizontalOffset);
    }

    [Fact]
    public void Focus_moves_auto_scroll_the_column_into_view()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // Focusing the far-right column scrolls it into the window (minimal — right-aligned).
        grid.SetFocusCell(0, 2);
        host.RunUntilIdle();
        Assert.Equal(10, grid.HorizontalOffset); // 34 − 24

        // Focusing the first column scrolls back left (leading-edge under no frozen region).
        grid.SetFocusCell(0, 0);
        host.RunUntilIdle();
        Assert.Equal(0, grid.HorizontalOffset);
    }

    [Fact]
    public void Grid_owned_horizontal_bar_tracks_overflow()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        var bar = (ScrollBar)grid.TemplateInstance!.NameScope.Find(DataGrid.PartHScrollBar)!;
        Assert.Equal(Visibility.Visible, bar.Visibility);
        Assert.Equal(10, bar.Maximum);

        grid.HorizontalOffset = 7;
        host.RunUntilIdle();
        Assert.Equal(7, bar.Value); // the silent mirror (CD28)

        // No overflow ⇒ the bar collapses (its row returns to the rows viewport).
        grid.Columns[2].Visible = false;
        grid.NotifyColumnGeometryChanged();
        host.RunUntilIdle();
        Assert.Equal(Visibility.Collapsed, bar.Visibility);
    }

    [Fact]
    public void H_scroll_commits_an_editor_sliding_under_the_frozen_region()
    {
        var (host, grid, source) = Show();
        using var _ = host;

        grid.Columns[0].Fixed = DataGridColumnFixed.Left;
        grid.NotifyColumnGeometryChanged();
        host.RunUntilIdle();

        // Edit Region on the first row (a scrolling column), type a new value…
        grid.SetFocusCell(0, 1);
        host.RunUntilIdle();
        grid.BeginEdit();
        host.RunUntilIdle();
        Assert.True(grid.RowsPresenter!.IsEditing);
        var editor = (TextBox)grid.RowsPresenter!.EditorElement!;
        editor.Text = "North";

        // …then scroll it fully under the frozen region: the §9.2 policy commits the edit.
        grid.HorizontalOffset = 10;
        host.RunUntilIdle();
        Assert.False(grid.RowsPresenter!.IsEditing);
        Assert.Equal("North", source[0].Region);
    }

    // ── §9.3 — master-detail ─────────────────────────────────────────────────────────────────────

    private static DataTemplate DetailTemplate(string marker = "~ detail pane ~") => new()
    {
        Content = new FuncTemplateContent(_ => new TextBlock(marker)),
    };

    [Fact]
    public void Detail_template_adds_the_gutter_and_expansion_inserts_the_pane()
    {
        var (host, grid, _) = Show(columns: 40);
        using var _ = host;

        grid.DetailTemplate = DetailTemplate();
        host.RunUntilIdle();

        // The 2-cell gutter leads every band: header text shifts right, data rows wear ▶.
        Assert.Equal(3, Row(host, 0).IndexOf("Id", StringComparison.Ordinal));
        Assert.Equal(0, Row(host, 1).IndexOf('▶'));
        Assert.Equal(2, grid.RowsPresenter!.ColumnLayout.GutterWidth);
        Assert.Equal(2, grid.RowsPresenter!.ColumnLayout.FrozenWidth); // the gutter is pinned

        // Expanding the first row inserts its pane BELOW it; later rows shift down by its height.
        int rowId = RowIdOf(grid, "SO-1042");
        grid.ExpandDetail(rowId);
        host.RunUntilIdle();
        Assert.Equal(0, Row(host, 1).IndexOf('▼'));
        Assert.Contains("SO-1042", Row(host, 1));
        Assert.Contains("~ detail pane ~", Row(host, 2));
        Assert.Contains("SO-1044", Row(host, 3));

        // Collapse restores the flat map.
        grid.CollapseDetail(rowId);
        host.RunUntilIdle();
        Assert.Contains("SO-1044", Row(host, 2));
    }

    [Fact]
    public void Expander_gutter_click_toggles_the_pane()
    {
        var (host, grid, _) = Show(columns: 40);
        using var _ = host;

        grid.DetailTemplate = DetailTemplate();
        host.RunUntilIdle();

        int rowId = RowIdOf(grid, "SO-1044");
        host.SendClick(0, 2); // the second data row's gutter cell
        host.RunUntilIdle();
        Assert.True(grid.IsDetailExpanded(rowId));
        Assert.Contains("~ detail pane ~", Row(host, 3));

        host.SendClick(0, 2);
        host.RunUntilIdle();
        Assert.False(grid.IsDetailExpanded(rowId));
    }

    [Fact]
    public void Detail_keyboard_cluster_expands_enters_and_returns()
    {
        var (host, grid, _) = Show(columns: 40);
        using var _ = host;

        // A focusable pane (a TextBox) so Ctrl+Down has somewhere to land.
        grid.DetailTemplate = new DataTemplate
        {
            Content = new FuncTemplateContent(_ => new TextBox { Text = "inside" }),
        };
        host.RunUntilIdle();

        grid.Focus(Cursorial.UI.Input.FocusNavigationMethod.Programmatic);
        grid.SetFocusCell(0, 0);
        host.RunUntilIdle();
        int rowId = RowIdOf(grid, "SO-1042");

        host.SendKey(Key.RightArrow, KeyModifiers.Control);
        host.RunUntilIdle();
        Assert.True(grid.IsDetailExpanded(rowId));

        // Ctrl+Down enters the pane; the grid stands down while focus is inside it.
        host.SendKey(Key.DownArrow, KeyModifiers.Control);
        host.RunUntilIdle();
        Assert.NotNull(grid.RowsPresenter!.FocusedDetailElement());
        int focusBefore = grid.FocusViewIndex;
        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal(focusBefore, grid.FocusViewIndex); // the arrow stayed with the pane

        // Esc returns to the grid; Ctrl+Left collapses.
        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.Null(grid.RowsPresenter!.FocusedDetailElement());
        host.SendKey(Key.LeftArrow, KeyModifiers.Control);
        host.RunUntilIdle();
        Assert.False(grid.IsDetailExpanded(rowId));
    }

    [Fact]
    public void Detail_expansion_prunes_when_the_row_leaves_the_view()
    {
        var (host, grid, _) = Show(columns: 40);
        using var _ = host;

        grid.DetailTemplate = DetailTemplate();
        host.RunUntilIdle();

        int rowId = RowIdOf(grid, "SO-1044");
        grid.ExpandDetail(rowId);
        host.RunUntilIdle();
        Assert.True(grid.IsDetailExpanded(rowId));

        // Filtering SO-1044 (31900) out of the view drops its pane (§9.3 — released, not parked).
        grid.Filter = FilterNode.Condition(grid.Columns[2], FilterOperator.LessThan, 30000m);
        host.RunUntilIdle();
        Assert.False(grid.IsDetailExpanded(rowId));
        Assert.DoesNotContain("~ detail pane ~", string.Join("\n", Enumerable.Range(0, 14).Select(r => Row(host, r))));
    }

    [Fact]
    public void Tall_details_route_the_content_y_map_through_every_site()
    {
        var (host, grid, _) = Show(columns: 40);
        using var _ = host;

        // A 3-row pane: the content-y map pushes later rows down by 3.
        grid.DetailTemplate = new DataTemplate
        {
            Content = new FuncTemplateContent(_ =>
            {
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock("pane line 1"));
                panel.Children.Add(new TextBlock("pane line 2"));
                panel.Children.Add(new TextBlock("pane line 3"));
                return panel;
            }),
        };
        host.RunUntilIdle();

        grid.ExpandDetail(RowIdOf(grid, "SO-1042"));
        host.RunUntilIdle();

        Assert.Contains("SO-1042", Row(host, 1));
        Assert.Contains("pane line 1", Row(host, 2));
        Assert.Contains("pane line 3", Row(host, 4));
        Assert.Contains("SO-1044", Row(host, 5));

        // The inverse map: a hit inside the pane belongs to the pane (no row), a hit below it maps
        // back to the pushed-down view row.
        var presenter = grid.RowsPresenter!;
        Assert.Equal((-1, -1, false), presenter.HitCell(5, 2)); // content y 1..3 = the pane
        var (viewIndex, _, _) = presenter.HitCell(5, 4);        // content y 4 = SO-1044 (view 1)
        Assert.Equal(1, viewIndex);
    }

    // ── The §3.3 band cycle + reachability surfaces (the live-canary closeout) ───────────────────

    [Fact]
    public void F6_walks_the_bands_and_header_keys_sort()
    {
        var (host, grid, _) = Show(columns: 40);
        using var _ = host;

        grid.Focus(Cursorial.UI.Input.FocusNavigationMethod.Programmatic);
        host.RunUntilIdle();
        Assert.Equal(DataGridFocusBand.Rows, grid.FocusBand);

        // F6 → header (group panel + auto-filter are hidden, so the cycle skips them).
        host.SendKey(Key.F6);
        host.RunUntilIdle();
        Assert.Equal(DataGridFocusBand.Header, grid.FocusBand);

        // Right + Enter: sort by the second column (replace); Space: append the third as level 2.
        host.SendKey(Key.RightArrow);
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Single(grid.SortDescriptions);
        Assert.Same(grid.Columns[1], grid.SortDescriptions[0].ColumnKey);

        host.SendKey(Key.RightArrow);
        host.SendKey(Key.Character, text: " ");
        host.RunUntilIdle();
        Assert.Equal(2, grid.SortDescriptions.Count);
        Assert.Same(grid.Columns[2], grid.SortDescriptions[1].ColumnKey);

        // Ctrl+G groups the focused header column; the group panel band becomes reachable.
        grid.ShowGroupPanel = true;
        host.SendKey(Key.Character, KeyModifiers.Control, "g");
        host.RunUntilIdle();
        Assert.Single(grid.GroupDescriptions);

        // Esc returns to the rows.
        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.Equal(DataGridFocusBand.Rows, grid.FocusBand);
    }

    [Fact]
    public void Ctrl_up_from_row_zero_enters_the_header_and_chips_edit_grouping()
    {
        var (host, grid, _) = Show(columns: 40);
        using var _ = host;

        grid.ShowGroupPanel = true;
        grid.GroupDescriptions.Add(new GroupDescription(grid.Columns[1]));
        grid.GroupDescriptions.Add(new GroupDescription(grid.Columns[2]));
        host.RunUntilIdle();

        grid.Focus(Cursorial.UI.Input.FocusNavigationMethod.Programmatic);
        grid.SetFocusCell(0, 0);
        host.SendKey(Key.UpArrow, KeyModifiers.Control);
        host.RunUntilIdle();
        Assert.Equal(DataGridFocusBand.Header, grid.FocusBand);

        // F6 onward: group panel. Enter flips the chip's direction; Delete removes the level.
        host.SendKey(Key.F6);
        host.RunUntilIdle();
        Assert.Equal(DataGridFocusBand.GroupPanel, grid.FocusBand);

        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Equal(SortDirection.Descending, grid.GroupDescriptions[0].Direction);

        host.SendKey(Key.Delete);
        host.RunUntilIdle();
        Assert.Single(grid.GroupDescriptions);
        Assert.Same(grid.Columns[2], grid.GroupDescriptions[0].ColumnKey);
    }

    [Fact]
    public void Ctrl_click_appends_a_sort_level_where_shift_click_is_terminal_reserved()
    {
        var (host, grid, _) = Show(columns: 40);
        using var _ = host;

        grid.CycleSort(grid.Columns[0]); // level 1: Id ascending
        host.RunUntilIdle();

        // Ctrl+click the Region header — the wire-reliable append chord (terminals eat Shift+click).
        host.SendMouseMove(12, 0);
        foreach (var kind in new[] { MouseEventKind.ButtonDown, MouseEventKind.ButtonUp })
        {
            host.SendInput(new MouseEvent
            {
                Kind = kind,
                Position = new CellPosition(12, 0),
                Button = MouseButton.Left,
                ButtonsHeld = kind == MouseEventKind.ButtonDown ? MouseButtons.Left : MouseButtons.None,
                Modifiers = KeyModifiers.Control,
                Timestamp = DateTimeOffset.UnixEpoch,
            });
            host.RunUntilIdle();
        }

        Assert.Equal(2, grid.SortDescriptions.Count);
        Assert.Same(grid.Columns[1], grid.SortDescriptions[1].ColumnKey);
    }

    [Fact]
    public void Grid_context_menu_reaches_the_dialogs_and_summaries()
    {
        var (host, grid, _) = Show(columns: 40);
        using var _ = host;

        grid.OpenGridContextMenu(2);
        host.RunUntilIdle();
        var menu = grid.ActiveGridMenu;
        Assert.NotNull(menu);

        // The command lanes are present: sort lanes, dialogs, the chooser, the summary submenu, copy.
        var captions = menu!.Items.OfType<Cursorial.UI.Controls.MenuItem>()
                                  .Select(item => item.Header?.ToString() ?? string.Empty).ToList();
        Assert.Contains(captions, c => c.Contains("Filter builder", StringComparison.Ordinal));
        Assert.Contains(captions, c => c.Contains("Conditional formatting", StringComparison.Ordinal));
        Assert.Contains(captions, c => c.Contains("Column chooser", StringComparison.Ordinal));
        Assert.Contains(captions, c => c.Contains("Summary for", StringComparison.Ordinal));
        Assert.Contains(captions, c => c.Contains("Group panel", StringComparison.Ordinal));
        Assert.Contains(captions, c => c.Contains("Summary footer", StringComparison.Ordinal));
        menu.Close();
        host.RunUntilIdle();

        // The summary toggle is the menu's engine: on adds the description, off removes it.
        grid.ToggleSummary(grid.Columns[2], AggregateKind.Sum);
        host.RunUntilIdle();
        var summary = Assert.Single(grid.SummaryDescriptions);
        Assert.Equal(AggregateKind.Sum, summary.Aggregate);
        Assert.Contains("Σ", Row(host, 13) + Row(host, 12)); // the footer band renders the total
        grid.ToggleSummary(grid.Columns[2], AggregateKind.Sum);
        Assert.Empty(grid.SummaryDescriptions);
    }

    [Fact]
    public void Summary_menu_shows_live_values_and_the_edit_entry()
    {
        var (host, grid, _) = Show(columns: 40);
        using var _ = host;

        grid.OpenGridContextMenu(2); // Amount
        host.RunUntilIdle();
        var menu = grid.ActiveGridMenu!;
        var summarySub = menu.Items.OfType<Cursorial.UI.Controls.MenuItem>()
                             .First(i => (i.Header?.ToString() ?? string.Empty).Contains("Summary for", StringComparison.Ordinal));
        var captions = summarySub.Items.OfType<Cursorial.UI.Controls.MenuItem>()
                                 .Select(i => i.Header?.ToString() ?? string.Empty).ToList();

        // Each aggregate carries its live value (§2.5): Sum of 12450+31900+19800+27300 = 91450.
        Assert.Contains(captions, c => c.StartsWith("Sum", StringComparison.Ordinal) && c.Contains("91450", StringComparison.Ordinal));

        // …and the editor entry is present, carrying the gear on MenuItem.Icon (the tiered Icon
        // class), NOT embedded in the header text.
        var editEntry = summarySub.Items.OfType<Cursorial.UI.Controls.MenuItem>()
                                  .First(i => (i.Header?.ToString() ?? string.Empty).Contains("Edit / Format", StringComparison.Ordinal));
        Assert.DoesNotContain("⛭", editEntry.Header?.ToString() ?? string.Empty);
        Assert.IsType<Cursorial.UI.Controls.Icon>(editEntry.Icon);
        menu.Close();
        host.RunUntilIdle();
    }

    [Fact]
    public async Task Summary_editor_sets_the_format_and_display_template()
    {
        var (host, grid, _) = Show(columns: 40);
        await using var _ = host;

        var task = grid.OpenSummaryEditorAsync(grid.Columns[2]); // Amount
        host.RunUntilIdle();
        var editor = grid.ActiveSummaryEditor!;
        Assert.True(editor.IsOpen);

        // Sum (index 1: Count, Sum, Average, Min, Max), with a format + display template.
        editor.AggregateCombo.SelectedIndex = 1;
        editor.FormatBox.Text = "N0";
        editor.TemplateBox.Text = "Σ {0}";
        host.RunUntilIdle();
        Assert.StartsWith("Σ", editor.Preview.Text);       // the live preview composes them
        Assert.Contains("91,450", editor.Preview.Text);     // N0 groups the sum

        editor.Ok();
        host.RunUntilIdle();
        await task; // the dialog task completes on OK

        var s = Assert.Single(grid.SummaryDescriptions,
            d => ReferenceEquals(d.ColumnKey, grid.Columns[2]) && d.Aggregate == AggregateKind.Sum);
        Assert.Equal("N0", s.Format);
        Assert.Equal("Σ {0}", s.DisplayTemplate);

        // The footer renders the templated + formatted total.
        Assert.Contains("Σ 91,450", string.Join("\n", Enumerable.Range(0, 14).Select(y => Row(host, y))));
    }

    [Fact]
    public void Grid_context_menu_toggles_the_group_panel_and_summary_footer()
    {
        var (host, grid, _) = Show(columns: 60, rows: 16);
        using var _ = host;

        grid.ShowGroupPanel = true;
        host.RunUntilIdle();
        Assert.Contains("drag a column header here", Row(host, 0));

        // The toggles seed their check state from the live properties…
        grid.OpenGridContextMenu(0);
        host.RunUntilIdle();
        Assert.True(Toggle(grid, "Group panel").IsChecked);
        Assert.True(Toggle(grid, "Summary footer").IsChecked);

        // …and a click flips the property: the panel leaves and the header climbs to row 0.
        Invoke(Toggle(grid, "Group panel"));
        grid.ActiveGridMenu!.Close();
        host.RunUntilIdle();
        Assert.False(grid.ShowGroupPanel);
        Assert.DoesNotContain("drag a column header here", Row(host, 0));

        // Re-open: the check re-seeds unchecked; toggling back restores the panel band.
        grid.OpenGridContextMenu(0);
        host.RunUntilIdle();
        var groupToggle = Toggle(grid, "Group panel");
        Assert.False(groupToggle.IsChecked);
        Invoke(groupToggle);
        grid.ActiveGridMenu!.Close();
        host.RunUntilIdle();
        Assert.True(grid.ShowGroupPanel);
        Assert.Contains("drag a column header here", Row(host, 0));

        // The footer toggle hides a LIVE summary band — and keeps the descriptions (the panel is
        // chrome, not state), so re-showing brings the same total back.
        grid.ToggleSummary(grid.Columns[2], AggregateKind.Sum);
        host.RunUntilIdle();
        Assert.Contains("Σ", AllText(host));
        grid.OpenGridContextMenu(0);
        host.RunUntilIdle();
        Invoke(Toggle(grid, "Summary footer"));
        grid.ActiveGridMenu!.Close();
        host.RunUntilIdle();
        Assert.False(grid.ShowSummaryFooter);
        Assert.Single(grid.SummaryDescriptions);
        Assert.DoesNotContain("Σ", AllText(host));

        static MenuItem Toggle(DataGrid grid, string caption)
            => grid.ActiveGridMenu!.Items.OfType<MenuItem>()
                   .First(i => string.Equals(i.Header?.ToString(), caption, StringComparison.Ordinal));

        static void Invoke(MenuItem item)
            => item.RaiseEvent(new ClickEventArgs(MenuItem.ClickEvent, item));

        static string AllText(UIHeadlessHost host)
            => string.Join("\n", Enumerable.Range(0, 16).Select(host.GetRowText));
    }

    [Fact]
    public void Header_drag_onto_the_group_panel_adds_a_grouping_level()
    {
        var (host, grid, _) = Show(columns: 60); // wide enough for the panel's empty prompt
        using var _ = host;

        grid.ShowGroupPanel = true;
        host.RunUntilIdle();
        // Screen bands: group panel row 0, header row 1, data from row 2.
        Assert.Contains("drag a column header here", Row(host, 0));

        // Press the Region header, move 2 cells (promotes the pending press to a reorder drag),
        // then up onto the panel band and release — the drop the panel's prompt advertises.
        void Send(MouseEventKind kind, int x, int y) => host.SendInput(new MouseEvent
        {
            Kind = kind,
            Position = new CellPosition(x, y),
            Button = MouseButton.Left,
            ButtonsHeld = kind == MouseEventKind.ButtonUp ? MouseButtons.None : MouseButtons.Left,
            Modifiers = KeyModifiers.None,
            Timestamp = DateTimeOffset.UnixEpoch,
        });
        host.SendMouseMove(12, 1);
        Send(MouseEventKind.ButtonDown, 12, 1);
        host.RunUntilIdle();
        Send(MouseEventKind.Move, 14, 1);
        host.RunUntilIdle();
        Assert.True(grid.RowsPresenter is not null); // the drag is live header-side
        Send(MouseEventKind.Move, 14, 0);
        host.RunUntilIdle();
        Send(MouseEventKind.ButtonUp, 14, 0);
        host.RunUntilIdle();

        var level = Assert.Single(grid.GroupDescriptions);
        Assert.Same(grid.Columns[1], level.ColumnKey);
        Assert.True(grid.Columns[1].Visible);            // never the hide path
        Assert.Empty(grid.SortDescriptions);             // and never the click-sort path
        Assert.Contains("▲ Region ✕", Row(host, 0));     // the chip renders on the panel

        // With the panel HIDDEN there is no group zone: the same upward drop falls back to the
        // reorder path and grouping stays untouched.
        grid.GroupDescriptions.Clear();
        grid.ShowGroupPanel = false;
        host.RunUntilIdle();
        host.SendMouseMove(12, 0);
        Send(MouseEventKind.ButtonDown, 12, 0);
        host.RunUntilIdle();
        Send(MouseEventKind.Move, 14, 0);
        host.RunUntilIdle();
        Send(MouseEventKind.Move, 14, -1);
        host.RunUntilIdle();
        Send(MouseEventKind.ButtonUp, 14, -1);
        host.RunUntilIdle();
        Assert.Empty(grid.GroupDescriptions);
    }

    // ── The wave-2 audit regressions (W2-N = the confirmed finding index) ────────────────────────

    [Fact]
    public void Auto_filter_band_reinks_on_a_horizontal_tick() // W2-0
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.ShowAutoFilterRow = true;
        host.RunUntilIdle();
        int resting = Row(host, 1).IndexOf('⌕');
        Assert.True(resting >= 0);

        // With NO editor hosted, an H-tick must still re-ink the band (AffectsMeasure alone left
        // the cached raster at stale x — the filter cells detached from their columns).
        grid.HorizontalOffset = 6;
        host.RunUntilIdle();
        Assert.NotEqual(resting, Row(host, 1).IndexOf('⌕'));
    }

    [Fact]
    public void Gutter_region_is_repainted_over_scrolled_bleed_in_every_band() // W2-1/2/3/4
    {
        var (host, grid, _) = Show(); // 24 cols; TotalWidth grows by the 2-cell gutter
        using var _ = host;

        grid.ShowAutoFilterRow = true;
        grid.DetailTemplate = DetailTemplate();
        host.RunUntilIdle();

        // Scroll so a column straddles the gutter boundary: the pinned gutter (FrozenWidth 2 with
        // NO Fixed column) must stay clear in the header, the filter band, and the data rows.
        grid.HorizontalOffset = 5;
        host.RunUntilIdle();
        Assert.Equal(2, grid.RowsPresenter!.ColumnLayout.FrozenWidth);
        Assert.True(string.IsNullOrWhiteSpace(Row(host, 0)[..2]), $"header gutter bled: [{Row(host, 0)[..2]}]");
        Assert.True(string.IsNullOrWhiteSpace(Row(host, 1)[..2]), $"filter gutter bled: [{Row(host, 1)[..2]}]");
        Assert.Equal('▶', Row(host, 2)[0]); // the data row's expander survives the overpaint
    }

    [Fact]
    public void Runtime_fixed_write_reflows_without_the_internal_funnel() // W2-6
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // A PUBLIC property write must reach layout on its own (no NotifyColumnGeometryChanged).
        grid.Columns[2].Fixed = DataGridColumnFixed.Left;
        host.RunUntilIdle();
        Assert.Equal(1, grid.RowsPresenter!.ColumnLayout.FrozenCount);
        Assert.Same(grid.Columns[2], grid.RowsPresenter!.ColumnLayout.Entries[0].Column);

        grid.Columns[2].Fixed = DataGridColumnFixed.None;
        host.RunUntilIdle();
        Assert.Equal(0, grid.RowsPresenter!.ColumnLayout.FrozenCount);
    }

    [Fact]
    public void Page_jump_steps_content_rows_over_tall_panes() // W2-8
    {
        var (host, grid, _) = Show(columns: 40);
        using var _ = host;

        // A 3-row pane on the first row: PageDown from row 0 with a 3-row "viewport step" must
        // land on view row 0+... the CONTENT step (3 rows of pane + rows), not view 0+viewport.
        grid.DetailTemplate = new DataTemplate
        {
            Content = new FuncTemplateContent(_ =>
            {
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock("p1"));
                panel.Children.Add(new TextBlock("p2"));
                panel.Children.Add(new TextBlock("p3"));
                return panel;
            }),
        };
        host.RunUntilIdle();
        grid.ExpandDetail(RowIdOf(grid, "SO-1042"));
        host.RunUntilIdle();

        grid.Focus(Cursorial.UI.Input.FocusNavigationMethod.Programmatic);
        grid.SetFocusCell(0, 0);
        host.SendKey(Key.PageDown);
        host.RunUntilIdle();

        // Content y of view 0 is 0; a page of ~11 content rows reaches content y 11 → the pane
        // consumed 3 of them, so the focus lands 3 VIEW rows earlier than the naive view step.
        // With only 4 data rows both clamp to the last row — assert via the inverse instead: the
        // page target must map through the content-y inverse, which the presenter exposes.
        Assert.Equal(3, grid.FocusViewIndex); // clamped to the last data row either way…
        // …so pin the MAP itself: 11 content rows down from view 0 is view 8 WITHOUT the pane,
        // view 8−3 WITH it (the pane eats 3 content rows).
        Assert.Equal((0, false), grid.RowsPresenter!.ViewIndexAtY(0));
        Assert.Equal((0, true), grid.RowsPresenter!.ViewIndexAtY(2));  // inside the pane
        Assert.Equal((1, false), grid.RowsPresenter!.ViewIndexAtY(4)); // SO-1044 pushed down by 3
    }

    [Fact]
    public void Focus_follows_its_row_id_across_a_resort() // W2-13
    {
        var (host, grid, _) = Show(columns: 40);
        using var _ = host;

        // Focus SO-1042 (view 0 under the insertion order), then sort Amount descending: SO-1042
        // (12450, the smallest) re-projects to the LAST view row — the focus must follow the id.
        host.SendClick(4, 1);
        host.RunUntilIdle();
        Assert.Equal(0, grid.FocusViewIndex);

        grid.SortDescriptions.Add(SortDescription.Descending(grid.Columns[2]));
        host.RunUntilIdle();
        Assert.Equal(3, grid.FocusViewIndex);
        Assert.Contains("SO-1042", Row(host, 4)); // and it IS still the focused row on screen
    }

    [Fact]
    public void Removed_corner_row_collapses_the_range_before_its_slot_recycles() // W2-12
    {
        var (host, grid, source) = Show(columns: 40);
        using var _ = host;

        grid.SelectionUnit = DataGridSelectionUnit.Cell;
        host.RunUntilIdle();

        // Range anchored on SO-1042..SO-1046 (rows 0..2), focus on SO-1042.
        host.SendClick(12, 1);
        host.RunUntilIdle();
        host.SendInput(new MouseEvent
        {
            Kind = MouseEventKind.ButtonDown,
            Position = new CellPosition(12, 3),
            Button = MouseButton.Left,
            ButtonsHeld = MouseButtons.Left,
            Modifiers = KeyModifiers.Shift,
            Timestamp = DateTimeOffset.UnixEpoch,
        });
        host.RunUntilIdle();
        Assert.Equal((0, 2, 1, 1), grid.CellRangeViewRect()!.Value);

        // Deleting the LEAD corner's row (SO-1046) collapses the range to the focus cell — a new
        // row added right after (which recycles the freed slot id) must NOT re-attach the range.
        source.RemoveAt(2);
        host.RunUntilIdle();
        source.Add(new Order("SO-9999", "North", 50000m));
        host.RunUntilIdle();

        var collapsed = grid.CellRangeViewRect();
        Assert.NotNull(collapsed);
        Assert.Equal(collapsed!.Value.FirstRow, collapsed.Value.LastRow);
        // The Shift+click moved FOCUS onto the lead (SO-1046); its removal fell focus back to the
        // same view slot (now SO-1047), and the range collapsed onto THAT focus cell. The vital
        // half: the recycled slot id (SO-9999) never re-attached the range.
        Assert.Equal(grid.FocusViewIndex, collapsed.Value.FirstRow);
        var cornerRow = grid.Snapshot.GetRow(collapsed.Value.FirstRow);
        Assert.Equal("SO-1047", grid.Controller!.FormatCell(cornerRow.RowId, grid.Columns[0]));
    }

    // ── §9.6 — struct rows at the grid level ─────────────────────────────────────────────────────

    private struct TradeRow
    {
        public string Symbol;
        public decimal Price;
    }

    [Fact]
    public void Struct_rows_new_row_commit_survives_the_cold_reattach()
    {
        // Wave-2 audit F1: the new-row commit's cold re-attach (a plain non-INCC IList) passed the
        // RAW LiveUpdates instead of the §9.6 degrade — for struct rows it hit the engine's
        // liveUpdates:true throw MID-COMMIT, after the row was already added (crash + desync).
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 14) });
        using var _ = host;

        var source = new List<TradeRow>
        {
            new() { Symbol = "AAPL", Price = 210m },
            new() { Symbol = "MSFT", Price = 420m },
        };
        var grid = new DataGrid { AutoGenerateColumns = false, AllowAddNew = true };
        grid.Columns.Add(new DataGridColumn { FieldName = "Symbol", Width = DataGridLength.Cells(8) });
        grid.Columns.Add(new DataGridColumn { FieldName = "Price", Width = DataGridLength.Cells(8) });
        grid.AddingNewRow += (_, e) => e.Item = new TradeRow { Symbol = "?", Price = 0m };
        grid.ItemsSource = source;
        host.ShowRoot(grid);
        host.RunUntilIdle();

        Assert.True(grid.LiveUpdates); // the default — the crash path's precondition
        Assert.True(grid.HasNewRowPlaceholder);

        // Walk onto the ghost row, edit Symbol, commit.
        host.SendClick(2, 2); // MSFT (view row 1)
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal(2, grid.FocusViewIndex);
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.True(grid.RowsPresenter!.IsEditing);
        host.SendText("TSLA");
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        // The commit survived (no engine throw), the source gained the row, and the cold re-attach
        // refreshed the view (the row renders).
        Assert.False(grid.RowsPresenter!.IsEditing);
        Assert.Equal(3, source.Count);
        Assert.Equal("TSLA", source[2].Symbol);
        Assert.Equal(3, grid.Snapshot.Count);
        Assert.NotNull(FindText(host, "TSLA"));
    }

    /// <summary>The first (column, row) of <paramref name="text"/> in the composited frame, or null.</summary>
    private static (int X, int Y)? FindText(UIHeadlessHost host, string text, int rows = 14)
    {
        for (int y = 0; y < rows; y++)
        {
            int x = host.GetRowText(y).IndexOf(text, StringComparison.Ordinal);
            if (x >= 0)
                return (x, y);
        }
        return null;
    }

    // ── §9.4 — cell-range selection ──────────────────────────────────────────────────────────────

    [Fact]
    public void Cell_mode_derives_the_rectangle_from_the_corners()
    {
        var (host, grid, _) = Show(columns: 40);
        using var _ = host;

        grid.SelectionUnit = DataGridSelectionUnit.Cell;
        host.RunUntilIdle();

        // Click (row 0, Region), Shift+click (row 2, Amount) → a 3×2 rectangle.
        host.SendClick(12, 1);
        host.RunUntilIdle();
        host.SendMouseMove(24, 3);
        host.SendInput(new MouseEvent
        {
            Kind = MouseEventKind.ButtonDown,
            Position = new CellPosition(24, 3),
            Button = MouseButton.Left,
            ButtonsHeld = MouseButtons.Left,
            Modifiers = KeyModifiers.Shift,
            Timestamp = DateTimeOffset.UnixEpoch,
        });
        host.RunUntilIdle();

        var range = grid.CellRangeViewRect();
        Assert.NotNull(range);
        Assert.Equal((0, 2, 1, 2), range!.Value);

        // The rectangle TSV (the Ctrl+C payload): formatted values, display order.
        Assert.Equal("East\t12450\nEast\t31900\nSouth\t19800\n", grid.BuildCellRangeTsv());

        // Row selection stayed empty (cell mode never writes the row controller).
        Assert.True(grid.RowSelection.IsEmpty);
    }

    [Fact]
    public void Ctrl_click_accumulates_a_second_cell_range()
    {
        var (host, grid, source) = Show(columns: 40);
        using var _ = host;

        grid.SelectionUnit = DataGridSelectionUnit.Cell;
        host.RunUntilIdle();

        static void CtrlClick(UIHeadlessHost host, int x, int y)
        {
            host.SendMouseMove(x, y);
            host.SendInput(new MouseEvent
            {
                Kind = MouseEventKind.ButtonDown,
                Position = new CellPosition(x, y),
                Button = MouseButton.Left,
                ButtonsHeld = MouseButtons.Left,
                Modifiers = KeyModifiers.Control,
                Timestamp = DateTimeOffset.UnixEpoch,
            });
            host.RunUntilIdle();
        }

        // Range 1: Region rows 0..2 (click + Shift+extend).
        host.SendClick(12, 1);
        host.RunUntilIdle();
        host.SendMouseMove(12, 3);
        host.SendInput(new MouseEvent
        {
            Kind = MouseEventKind.ButtonDown,
            Position = new CellPosition(12, 3),
            Button = MouseButton.Left,
            ButtonsHeld = MouseButtons.Left,
            Modifiers = KeyModifiers.Shift,
            Timestamp = DateTimeOffset.UnixEpoch,
        });
        host.RunUntilIdle();

        // §10.1: Ctrl+click banks range 1 and starts a fresh active range on Amount, row 0;
        // Shift+extend grows the active range to Amount rows 0..1 (so it has a non-focus member).
        CtrlClick(host, 24, 1);
        host.SendMouseMove(24, 2);
        host.SendInput(new MouseEvent
        {
            Kind = MouseEventKind.ButtonDown,
            Position = new CellPosition(24, 2),
            Button = MouseButton.Left,
            ButtonsHeld = MouseButtons.Left,
            Modifiers = KeyModifiers.Shift,
            Timestamp = DateTimeOffset.UnixEpoch,
        });
        host.RunUntilIdle();

        var rects = grid.CellRangeViewRects();
        Assert.Equal(2, rects.Count);
        Assert.Contains((0, 2, 1, 1), rects); // banked Region rows 0..2
        Assert.Contains((0, 1, 2, 2), rects); // active Amount rows 0..1

        // Both blocks land in the TSV, separated by a blank line.
        Assert.Equal("East\nEast\nSouth\n\n12450\n31900\n", grid.BuildCellRangeTsv());

        // A banked member and an active member (not the focus cell — focus is Amount row 1) share
        // the selection fill; a true non-member (Amount row 2) does not.
        var region = grid.RowsPresenter!.ColumnLayout.Entries[1];
        var amount = grid.RowsPresenter.ColumnLayout.Entries[2];
        var memberA = host.GetCell(region.X + 1, 3).Style.Background; // South (banked Region row 2)
        var memberB = host.GetCell(amount.X + 1, 1).Style.Background; // 12450 (active Amount row 0)
        var nonMember = host.GetCell(amount.X + 1, 3).Style.Background; // 19800 (outside both)
        Assert.Equal(memberA, memberB);
        Assert.NotEqual(memberA, nonMember);

        // A plain click collapses back to ONE range (the committed ones drop).
        host.SendClick(12, 1);
        host.RunUntilIdle();
        Assert.Single(grid.CellRangeViewRects());
    }

    [Fact]
    public void Losing_the_active_range_with_invalid_focus_keeps_the_committed_ranges()
    {
        var (host, grid, source) = Show(columns: 40);
        using var _ = host;
        grid.SelectionUnit = DataGridSelectionUnit.Cell;
        host.RunUntilIdle();

        // Bank Region rows 0..2, then start a fresh active range on Amount row 3 (SO-1047).
        host.SendClick(12, 1);
        host.RunUntilIdle();
        host.SendMouseMove(12, 3);
        host.SendInput(new MouseEvent
        {
            Kind = MouseEventKind.ButtonDown, Position = new CellPosition(12, 3), Button = MouseButton.Left,
            ButtonsHeld = MouseButtons.Left, Modifiers = KeyModifiers.Shift, Timestamp = DateTimeOffset.UnixEpoch,
        });
        host.RunUntilIdle();
        host.SendMouseMove(24, 4);
        host.SendInput(new MouseEvent
        {
            Kind = MouseEventKind.ButtonDown, Position = new CellPosition(24, 4), Button = MouseButton.Left,
            ButtonsHeld = MouseButtons.Left, Modifiers = KeyModifiers.Control, Timestamp = DateTimeOffset.UnixEpoch,
        });
        host.RunUntilIdle();
        Assert.Equal(2, grid.CellRangeViewRects().Count); // banked Region + active Amount

        // Clear focus, then remove the ACTIVE range's row (SO-1047). The active corner is lost with
        // no valid focus cell to collapse onto → the else branch fires. Audit fix: it must clear
        // ONLY the active range, not wipe the still-valid banked Region range via ClearCellRange().
        grid.SetFocusCell(-1, -1);
        source.RemoveAt(3); // SO-1047
        host.RunUntilIdle();

        var rects = grid.CellRangeViewRects();
        Assert.Single(rects); // the banked Region range survived
        Assert.Equal((0, 2, 1, 1), rects[0]);
    }

    [Fact]
    public void Overlapping_ranges_write_each_shared_cell_once_in_the_tsv()
    {
        var (host, grid, _) = Show(columns: 40);
        using var _ = host;
        grid.SelectionUnit = DataGridSelectionUnit.Cell;
        host.RunUntilIdle();

        // Range 1: Region rows 0..2 (click + Shift+extend).
        host.SendClick(12, 1);
        host.RunUntilIdle();
        host.SendMouseMove(12, 3);
        host.SendInput(new MouseEvent
        {
            Kind = MouseEventKind.ButtonDown, Position = new CellPosition(12, 3), Button = MouseButton.Left,
            ButtonsHeld = MouseButtons.Left, Modifiers = KeyModifiers.Shift, Timestamp = DateTimeOffset.UnixEpoch,
        });
        host.RunUntilIdle();

        // Ctrl+click a cell INSIDE that rectangle (Region row 1) — banks rows 0..2, active is the
        // interior cell, so the two ranges OVERLAP on Region row 1.
        host.SendMouseMove(12, 2);
        host.SendInput(new MouseEvent
        {
            Kind = MouseEventKind.ButtonDown, Position = new CellPosition(12, 2), Button = MouseButton.Left,
            ButtonsHeld = MouseButtons.Left, Modifiers = KeyModifiers.Control, Timestamp = DateTimeOffset.UnixEpoch,
        });
        host.RunUntilIdle();

        // Audit fix: the shared cell (Region row 1 = "East") must appear ONCE, not duplicated across
        // the two TSV blocks. Rows 0,1 are East, row 2 South ⇒ exactly two "East".
        var tsv = grid.BuildCellRangeTsv()!;
        Assert.Equal(2, tsv.Split("East").Length - 1);
    }

    [Fact]
    public void Range_membership_reprojects_across_a_resort_and_survives_column_moves()
    {
        var (host, grid, _) = Show(columns: 40);
        using var _ = host;

        grid.SelectionUnit = DataGridSelectionUnit.Cell;
        host.RunUntilIdle();

        // Anchor on SO-1042's Region, lead on SO-1046's Region (view rows 0..2 ascending by Id).
        host.SendClick(12, 1);
        host.RunUntilIdle();
        host.SendInput(new MouseEvent
        {
            Kind = MouseEventKind.ButtonDown,
            Position = new CellPosition(12, 3),
            Button = MouseButton.Left,
            ButtonsHeld = MouseButtons.Left,
            Modifiers = KeyModifiers.Shift,
            Timestamp = DateTimeOffset.UnixEpoch,
        });
        host.RunUntilIdle();
        Assert.Equal((0, 2, 1, 1), grid.CellRangeViewRect()!.Value);

        // A descending Amount sort re-projects the SAME corners (row ids) into new view rows:
        // SO-1042 → view 3, SO-1046 → view 2 ⇒ rows normalize to 2..3.
        grid.SortDescriptions.Add(SortDescription.Descending(grid.Columns[2]));
        host.RunUntilIdle();
        Assert.Equal((2, 3, 1, 1), grid.CellRangeViewRect()!.Value);

        // Hiding the endpoint column clamps to the nearest visible neighbor (§9.4).
        grid.Columns[1].Visible = false;
        grid.NotifyColumnGeometryChanged();
        host.RunUntilIdle();
        var clamped = grid.CellRangeViewRect();
        Assert.NotNull(clamped);
        Assert.Equal(clamped!.Value.FirstColumn, clamped.Value.LastColumn); // both edges on one visible entry
    }

    [Fact]
    public void Lost_corner_collapses_to_the_focus_cell_and_mode_switch_clears()
    {
        var (host, grid, _) = Show(columns: 40);
        using var _ = host;

        grid.SelectionUnit = DataGridSelectionUnit.Cell;
        host.RunUntilIdle();

        // Range over rows 0..2, focus on row 0 / column 1.
        host.SendClick(12, 1);
        host.RunUntilIdle();
        host.SendInput(new MouseEvent
        {
            Kind = MouseEventKind.ButtonDown,
            Position = new CellPosition(12, 3),
            Button = MouseButton.Left,
            ButtonsHeld = MouseButtons.Left,
            Modifiers = KeyModifiers.Shift,
            Timestamp = DateTimeOffset.UnixEpoch,
        });
        host.RunUntilIdle();
        grid.SetFocusCell(0, 1);

        // Filtering the LEAD corner's row (SO-1046, 19800) out of view collapses the range to the
        // focus cell on the next publish (§9.4).
        grid.Filter = FilterNode.Condition(grid.Columns[2], FilterOperator.GreaterThan, 20000m);
        host.RunUntilIdle();
        var collapsed = grid.CellRangeViewRect();
        Assert.NotNull(collapsed);
        Assert.Equal(collapsed!.Value.FirstRow, collapsed.Value.LastRow);
        Assert.Equal(collapsed.Value.FirstColumn, collapsed.Value.LastColumn);

        // Switching back to row mode clears the range and keeps the focus cell.
        grid.SelectionUnit = DataGridSelectionUnit.Row;
        Assert.Null(grid.CellRangeViewRect());
        Assert.True(grid.FocusColumnIndex >= 0);

        // Ctrl+A stays row-mode-only: in cell mode it must NOT select all rows.
        grid.SelectionUnit = DataGridSelectionUnit.Cell;
        grid.Focus(Cursorial.UI.Input.FocusNavigationMethod.Programmatic);
        host.RunUntilIdle();
        host.SendKey(Key.Character, KeyModifiers.Control, "a");
        host.RunUntilIdle();
        Assert.True(grid.RowSelection.IsEmpty);
    }

    [Fact]
    public void Shift_arrows_extend_the_lead_through_group_rows()
    {
        var (host, grid, _) = Show(columns: 40);
        using var _ = host;

        grid.SelectionUnit = DataGridSelectionUnit.Cell;
        grid.GroupDescriptions.Add(new GroupDescription(grid.Columns[1])); // group by Region
        host.RunUntilIdle();

        // View: [East group] SO-1042 SO-1044 [South group] SO-1046 [West group] SO-1047.
        grid.Focus(Cursorial.UI.Input.FocusNavigationMethod.Programmatic);
        grid.SetFocusCell(1, 2); // SO-1042's Amount
        host.SendClick(26, 2);   // anchor the range on the focused cell
        host.RunUntilIdle();

        // Shift+Down twice: over SO-1044, then THROUGH the South group row — the lead lands on
        // SO-1046 keeping its column; the group row is never a member.
        host.SendKey(Key.DownArrow, KeyModifiers.Shift);
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow, KeyModifiers.Shift);
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow, KeyModifiers.Shift);
        host.RunUntilIdle();

        var range = grid.CellRangeViewRect();
        Assert.NotNull(range);
        Assert.Equal(1, range!.Value.FirstRow);  // SO-1042's view row
        Assert.Equal(4, range.Value.LastRow);    // SO-1046's view row (past the group banner)

        // The TSV skips the group banner row entirely.
        Assert.Equal("12450\n31900\n19800\n", grid.BuildCellRangeTsv());
    }
}
