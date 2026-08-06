using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.UI.DataViews;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews;

/// <summary>
/// The gesture-sweep regressions (the [N] indices reference the sweep's confirmed findings): the
/// empty-view reachability keys, plain Home/End, band-focus repair, the click-away displacement
/// policy (cell + filter editors), right-click focus/anchoring, AllowDelete, the frozen-boundary
/// drag honesty, the rejected group drop, spin-stepper clicks, and the chooser's drag-to-show.
/// </summary>
public class GestureSweepTests
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

    /// <summary>The structural tests' pinned geometry: Id(8) Region(10) Amount(10), header row 0.</summary>
    private static (UIHeadlessHost Host, DataGrid Grid, ObservableCollection<Order> Source) Show(
        int columns = 40, int rows = 16)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(columns, rows) });
        var source = SampleOrders();
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

    private static (int X, int Y)? FindText(UIHeadlessHost host, string text, int rows = 16)
    {
        for (int y = 0; y < rows; y++)
        {
            int x = host.GetRowText(y).IndexOf(text, StringComparison.Ordinal);
            if (x >= 0)
                return (x, y);
        }
        return null;
    }

    private static void SendMouse(UIHeadlessHost host, MouseEventKind kind, int x, int y,
                                  KeyModifiers modifiers = KeyModifiers.None)
        => host.SendInput(new MouseEvent
        {
            Kind = kind,
            Position = new CellPosition(x, y),
            Button = MouseButton.Left,
            ButtonsHeld = kind == MouseEventKind.ButtonUp ? MouseButtons.None : MouseButtons.Left,
            Modifiers = modifiers,
            Timestamp = DateTimeOffset.UnixEpoch,
        });

    // ── [9] empty-view reachability + [10] Home/End ──────────────────────────────────────────────

    [Fact]
    public void Empty_view_keeps_the_reachability_keys_alive()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // Filter everything out — the keyboard user's trap scenario.
        grid.Filter = FilterNode.Condition(grid.Columns[2], FilterOperator.GreaterThan, 1_000_000m);
        host.RunUntilIdle();
        Assert.Equal(0, grid.Snapshot.Count);

        grid.Focus(Cursorial.UI.Input.FocusNavigationMethod.Programmatic);
        host.SendKey(Key.F6);
        host.RunUntilIdle();
        Assert.Equal(DataGridFocusBand.Header, grid.FocusBand); // F6 still walks the bands

        host.SendKey(Key.Escape);
        host.SendKey(Key.Menu);
        host.RunUntilIdle();
        Assert.NotNull(grid.ActiveGridMenu); // the command menu still opens (the way out)
        grid.ActiveGridMenu!.Close();
        host.RunUntilIdle();
    }

    [Fact]
    public void Plain_home_and_end_jump_the_focus_row()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        host.SendClick(2, 2); // SO-1044 (view 1)
        host.RunUntilIdle();
        Assert.Equal(1, grid.FocusViewIndex);

        host.SendKey(Key.End);
        host.RunUntilIdle();
        Assert.Equal(3, grid.FocusViewIndex);

        host.SendKey(Key.Home);
        host.RunUntilIdle();
        Assert.Equal(0, grid.FocusViewIndex);
    }

    // ── [3] band-focus repair ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Band_focus_returns_to_rows_when_the_panel_loses_its_last_chip()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.ShowGroupPanel = true;
        grid.GroupDescriptions.Add(new GroupDescription(grid.Columns[1]));
        host.RunUntilIdle();

        grid.Focus(Cursorial.UI.Input.FocusNavigationMethod.Programmatic);
        host.SendKey(Key.F6);
        host.SendKey(Key.F6);
        host.RunUntilIdle();
        Assert.Equal(DataGridFocusBand.GroupPanel, grid.FocusBand);

        // The mouse ✕ / programmatic removal path (NOT the keyboard Delete arm, which repaired
        // explicitly): the collection funnel must hand the focus back.
        grid.GroupDescriptions.Clear();
        host.RunUntilIdle();
        Assert.Equal(DataGridFocusBand.Rows, grid.FocusBand);

        // And arrows work again immediately.
        host.SendClick(2, 1);
        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal(1, grid.FocusViewIndex);
    }

    // ── [17]/[18] click-away commits the cell editor ─────────────────────────────────────────────

    [Fact]
    public void Click_away_commits_the_open_editor_before_the_press_lands()
    {
        var (host, grid, source) = Show();
        using var _ = host;

        // Edit SO-1042's Amount, type a draft…
        host.SendClick(24, 1);
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.True(grid.RowsPresenter!.IsEditing);
        host.SendText("777");
        host.RunUntilIdle();

        // …then click another row's cell: the draft COMMITS (the §9.2 displacement idiom), the
        // press lands, and navigation is alive (the stranded-editor state is gone).
        host.SendClick(2, 2);
        host.RunUntilIdle();
        Assert.False(grid.RowsPresenter!.IsEditing);
        Assert.Equal(777m, source[0].Amount);
        Assert.Equal(1, grid.FocusViewIndex);

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal(2, grid.FocusViewIndex);

        // The unparseable-draft variant cancels instead of stranding (edit the AMOUNT cell — the
        // DownArrow left the focus on the read-only Id column).
        host.SendClick(24, 3); // SO-1046's Amount (view 2)
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.True(grid.RowsPresenter!.IsEditing);
        host.SendText("not a number");
        host.RunUntilIdle();
        host.SendClick(2, 1);
        host.RunUntilIdle();
        Assert.False(grid.RowsPresenter!.IsEditing);
        Assert.Equal(19800m, source[2].Amount); // untouched — the bad draft was discarded
    }

    // ── [12] a rows press commits the filter editor's draft ──────────────────────────────────────

    [Fact]
    public void Rows_press_closes_the_filter_editor_and_revives_the_grid_keys()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.ShowAutoFilterRow = true;
        host.RunUntilIdle();

        // Open the Region filter cell (filter band = screen row 1), type a draft…
        host.SendClick(12, 1);
        host.RunUntilIdle();
        Assert.True(grid.AutoFilterRow!.IsEditing);
        host.SendText("Ea");
        host.RunUntilIdle();

        // …then click a data row: the draft commits (the filter applies), the editor closes, and
        // the grid keyboard is ALIVE again (the stand-down no longer swallows every key).
        host.SendClick(2, 2);
        host.RunUntilIdle();
        Assert.False(grid.AutoFilterRow!.IsEditing);
        Assert.True(grid.HasColumnFilter(grid.Columns[1]));
        Assert.Equal(2, grid.Snapshot.Count); // "Ea" → contains → the two East rows

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal(1, grid.FocusViewIndex);
    }

    // ── [6]/[7]/[8] right-click focus + menu anchoring ───────────────────────────────────────────

    [Fact]
    public void Right_click_focuses_group_rows_and_the_placeholder()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.GroupDescriptions.Add(new GroupDescription(grid.Columns[1]));
        host.RunUntilIdle();

        // Focus a data row first, then right-click the FIRST GROUP ROW (view 0, screen row 1).
        host.SendClick(4, 2);
        host.RunUntilIdle();
        host.SendMouseMove(4, 1);
        host.SendInput(new MouseEvent
        {
            Kind = MouseEventKind.ButtonDown,
            Position = new CellPosition(4, 1),
            Button = MouseButton.Right,
            ButtonsHeld = MouseButtons.Right,
            Modifiers = KeyModifiers.None,
            Timestamp = DateTimeOffset.UnixEpoch,
        });
        host.RunUntilIdle();
        Assert.Equal(0, grid.FocusViewIndex);   // the group row took row focus
        Assert.Equal(-1, grid.FocusColumnIndex);
        Assert.NotNull(grid.ActiveGridMenu);
        grid.ActiveGridMenu!.Close();
        host.RunUntilIdle();

        // The placeholder keeps its past-the-end focus under a right-click.
        grid.GroupDescriptions.Clear();
        grid.AllowAddNew = true;
        grid.AddingNewRow += (_, e) => e.Item = new Order("SO-NEW", "North", 0m);
        host.RunUntilIdle();
        int placeholderY = grid.Snapshot.Count + 1; // header + data rows
        host.SendMouseMove(4, placeholderY);
        host.SendInput(new MouseEvent
        {
            Kind = MouseEventKind.ButtonDown,
            Position = new CellPosition(4, placeholderY),
            Button = MouseButton.Right,
            ButtonsHeld = MouseButtons.Right,
            Modifiers = KeyModifiers.None,
            Timestamp = DateTimeOffset.UnixEpoch,
        });
        host.RunUntilIdle();
        Assert.Equal(grid.Snapshot.Count, grid.FocusViewIndex); // past-the-end, not clamped
        grid.ActiveGridMenu?.Close();
        host.RunUntilIdle();
    }

    [Fact]
    public void Menu_key_anchors_the_menu_near_the_focus_cell()
    {
        var (host, grid, _) = Show(rows: 24);
        using var _ = host;

        host.SendClick(2, 2); // SO-1044 (view 1, screen row 2)
        host.RunUntilIdle();
        grid.Focus(Cursorial.UI.Input.FocusNavigationMethod.Programmatic);
        host.SendKey(Key.Menu);
        host.RunUntilIdle();
        Assert.NotNull(grid.ActiveGridMenu);

        // The menu opens exactly ONE ROW BELOW the focus cell (screen row 2 ⇒ menu top row 3) — it
        // must never cover the row its commands act on (the old bottom-edge placement pinned the
        // menu ~n rows away at the screen bottom regardless of the cell).
        var menu = grid.ActiveGridMenu!;
        Assert.Equal(3, menu.TranslateToScreen(menu.Bounds).Row);

        var hit = FindText(host, "Sort \"Id\" ascending", rows: 24);
        Assert.NotNull(hit);
        Assert.InRange(hit!.Value.Y, 3, 5);
        grid.ActiveGridMenu!.Close();
        host.RunUntilIdle();
    }

    // ── [11] AllowDelete ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Delete_removes_selected_rows_when_allowed()
    {
        var (host, grid, source) = Show();
        using var _ = host;

        // Off by default: Delete is inert.
        host.SendClick(2, 2);
        host.SendKey(Key.Delete);
        host.RunUntilIdle();
        Assert.Equal(4, source.Count);

        grid.AllowDelete = true;
        host.SendKey(Key.Delete);
        host.RunUntilIdle();
        Assert.Equal(3, source.Count);
        Assert.DoesNotContain(source, o => o.Id == "SO-1044"); // the focused row left
        Assert.Equal(3, grid.Snapshot.Count);
    }

    // ── [0]/[1] the frozen-boundary + rejected-group-drop honesty ────────────────────────────────

    [Fact]
    public void Header_drag_clamps_to_the_drag_columns_partition()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        var idCol = grid.Columns[0];
        var regionCol = grid.Columns[1];
        var amountCol = grid.Columns[2];
        amountCol.Fixed = DataGridColumnFixed.Left; // Amount leads: entries [Amount(F), Id, Region]
        host.RunUntilIdle();
        Assert.Same(amountCol, grid.RowsPresenter!.ColumnLayout.Entries[0].Column);

        // (a) Drag Region onto the FROZEN Amount's left half: the slot clamps to the first
        // scrolling position — Region lands immediately after the frozen run (before Id), never a
        // lying no-op and never inside the frozen run.
        int regionX = Row(host, 0).IndexOf("Region", StringComparison.Ordinal);
        host.SendMouseMove(regionX + 1, 0);
        SendMouse(host, MouseEventKind.ButtonDown, regionX + 1, 0);
        host.RunUntilIdle();
        SendMouse(host, MouseEventKind.Move, regionX + 4, 0); // promote
        host.RunUntilIdle();
        SendMouse(host, MouseEventKind.Move, 2, 0);           // over the frozen Amount
        host.RunUntilIdle();
        SendMouse(host, MouseEventKind.ButtonUp, 2, 0);
        host.RunUntilIdle();

        var entries = grid.RowsPresenter!.ColumnLayout.Entries;
        Assert.Same(amountCol, entries[0].Column); // Amount still leads (frozen)
        Assert.Same(regionCol, entries[1].Column); // Region landed FIRST among scrolling
        Assert.Same(idCol, entries[2].Column);

        // (b) Drag the FIXED Amount toward the scrolling region: the slot clamps to the frozen
        // partition — release changes NOTHING (no silent Columns mutation).
        var columnsBefore = grid.Columns.ToList();
        int amountX = Row(host, 0).IndexOf("Amount", StringComparison.Ordinal);
        host.SendMouseMove(amountX + 1, 0);
        SendMouse(host, MouseEventKind.ButtonDown, amountX + 1, 0);
        host.RunUntilIdle();
        SendMouse(host, MouseEventKind.Move, amountX + 4, 0);
        host.RunUntilIdle();
        SendMouse(host, MouseEventKind.Move, 30, 0); // deep in the scrolling region
        host.RunUntilIdle();
        SendMouse(host, MouseEventKind.ButtonUp, 30, 0);
        host.RunUntilIdle();
        Assert.Equal(columnsBefore, grid.Columns.ToList());
    }

    [Fact]
    public void Rejected_group_drop_cancels_instead_of_reordering()
    {
        var (host, grid, _) = Show(columns: 60);
        using var _ = host;

        grid.ShowGroupPanel = true;
        grid.Columns[1].AllowGroup = false; // Region cannot group — the drop must be a NO-OP
        host.RunUntilIdle();
        var columnsBefore = grid.Columns.ToList();

        int regionX = Row(host, 1).IndexOf("Region", StringComparison.Ordinal); // header row 1 under the panel
        host.SendMouseMove(regionX + 1, 1);
        SendMouse(host, MouseEventKind.ButtonDown, regionX + 1, 1);
        host.RunUntilIdle();
        SendMouse(host, MouseEventKind.Move, regionX + 4, 1); // promote
        host.RunUntilIdle();
        SendMouse(host, MouseEventKind.Move, 4, 0);           // onto the visible panel
        host.RunUntilIdle();
        SendMouse(host, MouseEventKind.ButtonUp, 4, 0);
        host.RunUntilIdle();

        Assert.Empty(grid.GroupDescriptions);                 // correctly refused…
        Assert.Equal(columnsBefore, grid.Columns.ToList());   // …and no accidental reorder either
    }

    // ── [19] spin-stepper clicks ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Spin_stepper_clicks_step_the_editor_value()
    {
        var (host, grid, source) = Show();
        using var _ = host;

        host.SendClick(24, 1); // SO-1042's Amount
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Equal(DataGridEditorKind.Spin, grid.RowsPresenter!.EditorKind);

        // The ▲▼ suffix occupies the edit cell's last two content cells.
        var entry = grid.RowsPresenter!.ColumnLayout.Entries[2];
        int zoneStart = entry.X + 1 + entry.Width - 2; // DrawXOf == entry.X at rest
        int editRowY = 1;

        host.SendMouseMove(zoneStart, editRowY);
        SendMouse(host, MouseEventKind.ButtonDown, zoneStart, editRowY);     // ▲ +1
        SendMouse(host, MouseEventKind.ButtonUp, zoneStart, editRowY);
        host.RunUntilIdle();
        SendMouse(host, MouseEventKind.ButtonDown, zoneStart + 1, editRowY, KeyModifiers.Shift); // ▼ −10
        SendMouse(host, MouseEventKind.ButtonUp, zoneStart + 1, editRowY, KeyModifiers.Shift);
        host.RunUntilIdle();

        Assert.True(grid.RowsPresenter!.IsEditing); // the presses never stranded the session
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Equal(12450m + 1m - 10m, source[0].Amount);
    }

    // ── [2]/[15] the chooser's drag-to-show ──────────────────────────────────────────────────────

    [Fact]
    public void Chooser_chip_drags_onto_the_header_to_show_at_the_slot()
    {
        var (host, grid, _) = Show(columns: 60);
        using var _ = host;

        var idCol = grid.Columns[0];
        var regionCol = grid.Columns[1];
        regionCol.Visible = false; // Region hides; the chooser lists it as a chip
        host.RunUntilIdle();
        grid.OpenColumnChooser(0);
        host.RunUntilIdle();

        var chip = FindText(host, "⠿ Region");
        Assert.NotNull(chip);

        // Press the chip, move past the threshold (the header adopts the drag; the chooser
        // closes), drop on the Id column's LEFT half → Region shows FIRST.
        host.SendMouseMove(chip!.Value.X + 1, chip.Value.Y);
        SendMouse(host, MouseEventKind.ButtonDown, chip.Value.X + 1, chip.Value.Y);
        host.RunUntilIdle();
        SendMouse(host, MouseEventKind.Move, chip.Value.X + 4, chip.Value.Y);
        host.RunUntilIdle();
        SendMouse(host, MouseEventKind.Move, 2, 0); // Id's left half on the header band
        host.RunUntilIdle();
        SendMouse(host, MouseEventKind.ButtonUp, 2, 0);
        host.RunUntilIdle();

        Assert.True(regionCol.Visible);
        var entries = grid.RowsPresenter!.ColumnLayout.Entries;
        Assert.Same(regionCol, entries[0].Column); // Region landed at the dropped slot
        Assert.Same(idCol, entries[1].Column);
    }

    // ── [20] the edit bar's honest Tab hint ──────────────────────────────────────────────────────

    [Fact]
    public void Edit_bar_tab_hint_says_commit_row_on_the_new_row_session()
    {
        var (host, grid, _) = Show(columns: 70, rows: 18); // wide enough for the whole hint line
        using var _ = host;

        grid.AllowAddNew = true;
        grid.AddingNewRow += (_, e) => e.Item = new Order("SO-NEW", "North", 0m);
        host.RunUntilIdle();

        // A data-row session advertises the cell advance… (the Amount cell — Id is read-only).
        host.SendClick(24, 1);
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.True(grid.RowsPresenter!.IsEditing);
        Assert.NotNull(FindText(host, "Tab next cell", rows: 18));
        host.SendKey(Key.Escape);
        host.RunUntilIdle();

        // …the new-row session says what Tab actually does there.
        host.SendClick(2, 4);
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal(grid.Snapshot.Count, grid.FocusViewIndex); // on the placeholder
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.True(grid.RowsPresenter!.IsEditing);
        Assert.NotNull(FindText(host, "Tab commit row", rows: 18));
        host.SendKey(Key.Escape);
        host.RunUntilIdle();
    }
}
