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
