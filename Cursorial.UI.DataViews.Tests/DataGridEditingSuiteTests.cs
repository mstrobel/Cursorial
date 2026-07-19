using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.DataViews;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Themes;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews;

/// <summary>
/// The editor suite end-to-end (design doc §3.2 — the deferred "full editor suite" landing on the
/// v1 edit host; the mockup's "Inline editing" section): per-kind editors (combo on enums, date
/// round-trip, spin stepping), the ed-err validation look with recovery, the new-row template
/// (eligibility, commit-into-sorted-position, Esc discard), and the edit action bar banding in and
/// out — all through <see cref="UIHeadlessHost"/> with cell assertions against the composited
/// frame (the repo's integration idiom).
/// </summary>
public class DataGridEditingSuiteTests
{
    private enum TicketStatus { Open, Active, Closed }

    /// <summary>A row type exercising every Auto editor kind (enum → Combo, DateOnly → Date, decimal → Spin).</summary>
    private sealed class Ticket(string id, TicketStatus status, DateOnly due, decimal hours) : INotifyPropertyChanged
    {
        private TicketStatus _status = status;
        private DateOnly _due = due;
        private decimal _hours = hours;

        public string Id { get; } = id;
        public TicketStatus Status { get => _status; set => Set(ref _status, value); }
        public DateOnly Due { get => _due; set => Set(ref _due, value); }
        public decimal Hours { get => _hours; set => Set(ref _hours, value); }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
        }
    }

    /// <summary>The new-row Activator lane needs a public parameterless ctor (Ticket has none).</summary>
    private sealed class Entry : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private decimal _amount;

        public string Name { get => _name; set => Set(ref _name, value); }
        public decimal Amount { get => _amount; set => Set(ref _amount, value); }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
        }
    }

    private static ObservableCollection<Ticket> SampleTickets() =>
    [
        new("TK-01", TicketStatus.Open, new DateOnly(2026, 5, 3), 12m),
        new("TK-02", TicketStatus.Active, new DateOnly(2026, 5, 6), 31m),
        new("TK-03", TicketStatus.Closed, new DateOnly(2026, 5, 9), 19m),
    ];

    private static (UIHeadlessHost Host, DataGrid Grid, ObservableCollection<Ticket> Source) Show(
        int columns = 64, int rows = 14)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(columns, rows) });
        var source = SampleTickets();
        var grid = new DataGrid { ItemsSource = source };
        host.ShowRoot(grid);
        host.RunUntilIdle();
        return (host, grid, source);
    }

    private static string Row(UIHeadlessHost host, int row) => host.GetRowText(row);

    /// <summary>Begin editing (row, column) via the real F2 path (focus first, like a user).</summary>
    private static void BeginEdit(UIHeadlessHost host, DataGrid grid, int viewIndex, int columnIndex)
    {
        host.SendClick(2, 1 + viewIndex); // focus the grid + the row (header is screen row 0)
        host.RunUntilIdle();
        grid.SetFocusCell(viewIndex, columnIndex);
        host.SendKey(Key.F2);
        host.RunUntilIdle();
    }

    // ── Editor-kind resolution ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Auto_kind_resolves_from_the_column_key_type()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // Id=string → Text, Status=enum → Combo, Due=DateOnly → Date, Hours=decimal → Spin.
        Assert.Equal(DataGridEditorKind.Text, grid.ResolveEditorKind(grid.Columns[0]));
        Assert.Equal(DataGridEditorKind.Combo, grid.ResolveEditorKind(grid.Columns[1]));
        Assert.Equal(DataGridEditorKind.Date, grid.ResolveEditorKind(grid.Columns[2]));
        Assert.Equal(DataGridEditorKind.Spin, grid.ResolveEditorKind(grid.Columns[3]));

        // An authored kind wins over Auto; None disables editing even though a setter exists.
        grid.Columns[3].EditorKind = DataGridEditorKind.Text;
        Assert.Equal(DataGridEditorKind.Text, grid.ResolveEditorKind(grid.Columns[3]));

        grid.Columns[1].EditorKind = DataGridEditorKind.None;
        grid.SetFocusCell(0, 1);
        host.SendKey(Key.F2);
        host.RunUntilIdle();
        Assert.False(grid.RowsPresenter!.IsEditing); // None: no editor despite the compiled setter
    }

    // ── Combo editor ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Combo_editor_on_an_enum_column_commits_the_picked_name_and_reshapes()
    {
        var (host, grid, source) = Show();
        using var _ = host;

        BeginEdit(host, grid, viewIndex: 0, columnIndex: 1); // TK-01.Status (Open)
        var combo = Assert.IsType<ComboBox>(grid.RowsPresenter!.EditorElement);

        // Items are the enum's declared names; the selection presets to the current cell value.
        Assert.Equal(new[] { "Open", "Active", "Closed" }, combo.ItemsSource!.Cast<string>().ToArray());
        Assert.Equal("Open", combo.SelectedItem);

        // Pick and commit: Enter on the CLOSED combo rides the grid's tunnel intercept (the combo's
        // own class handler would swallow it to open the list).
        combo.SelectedItem = "Closed";
        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.False(grid.RowsPresenter!.IsEditing);
        Assert.Equal(TicketStatus.Closed, source[0].Status);
        Assert.Contains("Closed", Row(host, 1)); // the INPC tick re-inked the cell
    }

    [Fact]
    public void Combo_keyboard_flow_open_pick_close_then_Enter_commits()
    {
        var (host, grid, source) = Show();
        using var _ = host;

        BeginEdit(host, grid, viewIndex: 0, columnIndex: 1);
        var combo = Assert.IsType<ComboBox>(grid.RowsPresenter!.EditorElement);

        host.SendKey(Key.DownArrow); // opens the drop-down (the combo's own gesture)
        host.RunUntilIdle();
        Assert.True(combo.IsDropDownOpen);

        host.SendKey(Key.DownArrow); // Open → Active
        host.RunUntilIdle();
        host.SendKey(Key.Enter);     // picks the highlighted item + closes (the combo owns it while open)
        host.RunUntilIdle();
        Assert.False(combo.IsDropDownOpen);
        Assert.True(grid.RowsPresenter!.IsEditing); // the pick is NOT the cell commit

        host.SendKey(Key.Enter);     // now closed: the tunnel intercept commits the cell
        host.RunUntilIdle();
        Assert.False(grid.RowsPresenter!.IsEditing);
        Assert.Equal(TicketStatus.Active, source[0].Status);
    }

    [Fact]
    public void Combo_editor_Escape_while_closed_cancels_without_writing()
    {
        var (host, grid, source) = Show();
        using var _ = host;

        BeginEdit(host, grid, viewIndex: 0, columnIndex: 1);
        var combo = Assert.IsType<ComboBox>(grid.RowsPresenter!.EditorElement);
        combo.SelectedItem = "Closed"; // a pending pick Esc must abandon

        host.SendKey(Key.Escape);
        host.RunUntilIdle();

        Assert.False(grid.RowsPresenter!.IsEditing);
        Assert.Equal(TicketStatus.Open, source[0].Status); // untouched
    }

    // ── Date editor ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Date_editor_round_trips_the_cell_value_through_SelectedDate()
    {
        var (host, grid, source) = Show();
        using var _ = host;

        BeginEdit(host, grid, viewIndex: 0, columnIndex: 2); // TK-01.Due (2026-05-03)
        var picker = Assert.IsType<DatePicker>(grid.RowsPresenter!.EditorElement);

        // The current cell text parsed into the preset (the mockup's ed-date seeding).
        Assert.Equal(new DateOnly(2026, 5, 3), picker.SelectedDate);

        // A calendar-style pick (programmatic here) pushes the canonical text into the editable
        // box; Enter on the CLOSED picker rides the tunnel intercept and commits that draft.
        picker.SelectedDate = new DateOnly(2026, 6, 15);
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.False(grid.RowsPresenter!.IsEditing);
        Assert.Equal(new DateOnly(2026, 6, 15), source[0].Due);
    }

    [Fact]
    public void Date_editor_Escape_cancels_the_whole_edit()
    {
        var (host, grid, source) = Show();
        using var _ = host;

        BeginEdit(host, grid, viewIndex: 0, columnIndex: 2);
        var picker = Assert.IsType<DatePicker>(grid.RowsPresenter!.EditorElement);
        picker.SelectedDate = new DateOnly(2027, 1, 1);

        // Esc (closed) tunnels to the grid's cancel — the editable picker's own Esc-revert branch
        // would otherwise swallow it forever and the edit could never be abandoned by keyboard.
        host.SendKey(Key.Escape);
        host.RunUntilIdle();

        Assert.False(grid.RowsPresenter!.IsEditing);
        Assert.Equal(new DateOnly(2026, 5, 3), source[0].Due);
    }

    // ── Spin editor ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Spin_editor_steps_with_Up_Down_and_Shift_steps_by_ten()
    {
        var (host, grid, source) = Show();
        using var _ = host;

        BeginEdit(host, grid, viewIndex: 0, columnIndex: 3); // TK-01.Hours (12)
        var box = Assert.IsType<TextBox>(grid.RowsPresenter!.EditorElement);
        Assert.Equal(DataGridEditorKind.Spin, grid.RowsPresenter!.EditorKind);
        Assert.Equal("12", box.Text);

        host.SendKey(Key.UpArrow); // +1 (single-line TextBox leaves Up/Down to the grid)
        host.RunUntilIdle();
        Assert.Equal("13", box.Text);

        host.SendKey(Key.UpArrow, KeyModifiers.Shift); // +10
        host.RunUntilIdle();
        Assert.Equal("23", box.Text);

        host.SendKey(Key.DownArrow); // −1
        host.RunUntilIdle();
        Assert.Equal("22", box.Text);

        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.False(grid.RowsPresenter!.IsEditing);
        Assert.Equal(22m, source[0].Hours);
    }

    [Fact]
    public void Spin_editor_draws_the_stepper_affordance_beside_the_cell()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // The ▲▼ suffix needs a ≥6-cell column (a cramped Auto "Hours" column elides the hint).
        grid.Columns[3].Width = DataGridLength.Cells(10);
        grid.RowsPresenter!.InvalidateBand();
        host.RunUntilIdle();

        BeginEdit(host, grid, viewIndex: 0, columnIndex: 3);
        Assert.Contains("▲▼", Row(host, 1)); // the drawn spinbtns suffix in the edit cell

        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.DoesNotContain("▲▼", Row(host, 1)); // gone with the editor
    }

    // ── Validation (the ed-err idiom) ─────────────────────────────────────────────────────────────

    [Fact]
    public void Unparseable_commit_keeps_the_editor_open_flips_the_danger_ink_and_recovers()
    {
        var (host, grid, source) = Show();
        using var _ = host;

        BeginEdit(host, grid, viewIndex: 0, columnIndex: 3); // Hours (decimal)
        var box = Assert.IsType<TextBox>(grid.RowsPresenter!.EditorElement);

        host.SendText("abc"); // select-all replace with garbage
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        // The editor stays open, wearing the theme's danger ink (a live resource reference).
        Assert.True(grid.RowsPresenter!.IsEditing);
        Assert.Equal(12m, source[0].Hours); // nothing written
        Assert.Same(box.FindResource(ThemeKeys.DangerBrush), box.Foreground);

        // The next text change clears the error look (the recovery contract) …
        box.SelectAll();
        host.SendText("45");
        host.RunUntilIdle();
        Assert.NotSame(box.FindResource(ThemeKeys.DangerBrush), box.Foreground);

        // … and the corrected value commits.
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.False(grid.RowsPresenter!.IsEditing);
        Assert.Equal(45m, source[0].Hours);
    }

    // ── The new-row template ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void New_row_template_renders_only_when_AllowAddNew_on_a_growable_IList()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // Default: no template (rows 1..3 are data; row 4 stays blank).
        Assert.DoesNotContain("(pick)", Row(host, 4));

        grid.AllowAddNew = true;
        host.RunUntilIdle();

        // Ticket has NO parameterless ctor and no AddingNewRow handler ⇒ still ineligible.
        Assert.False(grid.HasNewRowPlaceholder);
        Assert.DoesNotContain("(pick)", Row(host, 4));

        // A construction seam arrives (the event lane) ⇒ the ghost row appears after the last row:
        // the muted * indicator plus per-kind placeholders on the editable columns.
        grid.AddingNewRow += (_, e) => e.Item = new Ticket("TK-99", TicketStatus.Open, new DateOnly(2026, 1, 1), 0m);
        grid.RowsPresenter!.InvalidateBand();
        host.RunUntilIdle();

        Assert.True(grid.HasNewRowPlaceholder);
        string ghost = Row(host, 4);
        Assert.Contains("*", ghost);
        Assert.Contains("(pick)", ghost);   // Status → Combo hint
        Assert.Contains("yyyy-mm", ghost);  // Due → Date hint (grapheme-truncated to the 8-cell column)
        Assert.Contains("0", ghost);        // Hours → Spin hint
    }

    [Fact]
    public void New_row_template_absent_for_a_non_IList_source()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(64, 14) });
        using var _ = host;

        // ReadOnlyObservableCollection: IList.IsReadOnly == true — AllowAddNew must stay inert.
        var backing = SampleTickets();
        var grid = new DataGrid
        {
            ItemsSource = new ReadOnlyObservableCollection<Ticket>(backing),
            AllowAddNew = true,
        };
        grid.AddingNewRow += (_, e) => e.Item = new Ticket("TK-99", TicketStatus.Open, new DateOnly(2026, 1, 1), 0m);
        host.ShowRoot(grid);
        host.RunUntilIdle();

        Assert.False(grid.HasNewRowPlaceholder);
        Assert.DoesNotContain("(pick)", Row(host, 4));
    }

    [Fact]
    public void New_row_commit_adds_to_the_source_and_sorts_into_position()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 14) });
        using var _ = host;
        var source = new ObservableCollection<Entry>
        {
            new() { Name = "Alpha", Amount = 1m },
            new() { Name = "Delta", Amount = 2m },
        };
        var grid = new DataGrid { ItemsSource = source, AllowAddNew = true };
        host.ShowRoot(grid);
        host.RunUntilIdle();

        grid.CycleSort(grid.Columns[0]); // Name ascending — the add must re-sort into position
        host.RunUntilIdle();
        Assert.True(grid.HasNewRowPlaceholder); // Entry has a public parameterless ctor (the Activator lane)

        // Walk focus onto the template (last data row → one past) and enter it.
        host.SendClick(2, 2); // "Delta" (view row 1)
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow); // the placeholder at view index Count
        host.RunUntilIdle();
        Assert.Equal(2, grid.FocusViewIndex);

        host.SendKey(Key.Enter); // begins new-row entry on the FIRST editable column (Name)
        host.RunUntilIdle();
        Assert.True(grid.RowsPresenter!.IsEditing);
        Assert.Contains("editing (new)", Row(host, 13)); // the edit bar's new-row caption

        host.SendText("Charlie");
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        // Added to the source; INCC inserted it into the view where the sort placed it (between
        // Alpha and Delta), and the ghost row moved down one.
        Assert.False(grid.RowsPresenter!.IsEditing);
        Assert.Equal(3, source.Count);
        Assert.Equal("Charlie", source[2].Name);
        Assert.Contains("Alpha", Row(host, 1));
        Assert.Contains("Charlie", Row(host, 2));
        Assert.Contains("Delta", Row(host, 3));
        Assert.True(grid.HasNewRowPlaceholder); // the template survives for the next add
    }

    [Fact]
    public void New_row_Escape_discards_the_pending_instance()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 14) });
        using var _ = host;
        var source = new ObservableCollection<Entry> { new() { Name = "Alpha", Amount = 1m } };
        var grid = new DataGrid { ItemsSource = source, AllowAddNew = true };
        host.ShowRoot(grid);
        host.RunUntilIdle();

        host.SendClick(2, 1);
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow); // onto the placeholder
        host.SendKey(Key.Enter);     // begin the new-row edit
        host.RunUntilIdle();
        Assert.True(grid.RowsPresenter!.IsEditing);

        host.SendText("Ghost");
        host.RunUntilIdle();
        host.SendKey(Key.Escape);
        host.RunUntilIdle();

        Assert.False(grid.RowsPresenter!.IsEditing);
        Assert.Single(source); // the instance was discarded, never added
    }

    // ── The edit action bar ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Edit_bar_bands_in_while_editing_and_out_on_end()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // Idle: the bottom row spends nothing on the bar.
        Assert.DoesNotContain("✎", Row(host, 13));
        Assert.DoesNotContain("Enter commit", Row(host, 13));

        BeginEdit(host, grid, viewIndex: 1, columnIndex: 3); // TK-02.Hours
        string bar = Row(host, 13);
        Assert.Contains("✎", bar);
        Assert.Contains("editing TK-02", bar); // the caption: the edit row's first visible column
        Assert.Contains("Enter commit", bar);
        Assert.Contains("Esc cancel", bar);
        Assert.Contains("Tab next cell", bar);

        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.DoesNotContain("Enter commit", Row(host, 13)); // collapsed again
    }
}
