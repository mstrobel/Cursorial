using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.DataViews;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews;

/// <summary>
/// The first end-to-end gate: a real <see cref="DataGrid"/> through <see cref="UIHeadlessHost"/> —
/// template application, SCP adoption of the rows presenter, band-cache fill, header glyphs, cell
/// rendering, sorting gestures, selection, live INPC ticks, and scrolling. Cell assertions against
/// the composited frame (the repo's integration idiom).
/// </summary>
public class DataGridRenderTests
{
    private sealed class Order(string id, string region, decimal amount) : INotifyPropertyChanged
    {
        private decimal _amount = amount;
        public string Id { get; } = id;
        public string Region { get; } = region;
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

    private static (UIHeadlessHost Host, DataGrid Grid, ObservableCollection<Order> Source) Show(
        int columns = 60, int rows = 14, bool autoColumns = true)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(columns, rows) });
        var source = SampleOrders();
        var grid = new DataGrid { ItemsSource = source, AutoGenerateColumns = autoColumns };
        host.ShowRoot(grid);
        host.RunUntilIdle();
        return (host, grid, source);
    }

    private static string Row(UIHeadlessHost host, int row) => host.GetRowText(row);

    [Fact]
    public void Grid_renders_header_and_cells()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // Auto-generated columns: Id, Region, Amount (declaration order).
        Assert.Equal(3, grid.Columns.Count);

        // Header row carries the captions.
        string header = Row(host, 0);
        Assert.Contains("Id", header);
        Assert.Contains("Region", header);
        Assert.Contains("Amount", header);

        // Data rows (insertion order — no sort yet).
        Assert.Contains("SO-1042", Row(host, 1));
        Assert.Contains("East", Row(host, 1));
        Assert.Contains("12,450", Row(host, 1));
        Assert.Contains("SO-1044", Row(host, 2));
        Assert.Contains("SO-1046", Row(host, 3));
        Assert.Contains("SO-1047", Row(host, 4));
    }

    [Fact]
    public void Sort_gesture_reorders_rows_and_draws_the_glyph()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.CycleSort(grid.Columns[2]); // Amount ascending
        host.RunUntilIdle();

        Assert.Contains("▲", Row(host, 0));
        Assert.Contains("12,450", Row(host, 1));
        Assert.Contains("19,800", Row(host, 2));
        Assert.Contains("27,300", Row(host, 3));
        Assert.Contains("31,900", Row(host, 4));

        grid.CycleSort(grid.Columns[2]); // descending
        host.RunUntilIdle();
        Assert.Contains("▼", Row(host, 0));
        Assert.Contains("31,900", Row(host, 1));
    }

    [Fact]
    public void Grouping_renders_group_rows_with_counts()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.GroupDescriptions.Add(new GroupDescription(grid.Columns[1])); // by Region
        host.RunUntilIdle();

        string firstGroup = Row(host, 1);
        Assert.Contains("▾", firstGroup);
        Assert.Contains("Region: East", firstGroup);
        Assert.Contains("(2)", firstGroup);
    }

    [Fact]
    public void Live_tick_repositions_a_sorted_row()
    {
        var (host, grid, source) = Show();
        using var _ = host;

        grid.CycleSort(grid.Columns[2]); // Amount ascending
        host.RunUntilIdle();
        Assert.Contains("SO-1042", Row(host, 1)); // 12450 is smallest

        source[0].Amount = 99999m;      // SO-1042 jumps to the end
        host.RunUntilIdle();

        Assert.Contains("SO-1046", Row(host, 1)); // 19800 now smallest
        Assert.Contains("SO-1042", Row(host, 4));
        Assert.Contains("99,999", Row(host, 4));
    }

    [Fact]
    public void Click_selects_a_row()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        host.SendClick(4, 2); // row index 2 on screen = the second data row (header is row 0)
        host.RunUntilIdle();

        var selected = grid.RowSelection.MaterializeSelectedIds(grid.Snapshot);
        int rowId = Assert.Single(selected);
        Assert.Equal("SO-1044", ((Order)grid.Controller!.GetRowObject(rowId)).Id);
    }

    [Fact]
    public void Keyboard_moves_focus_and_selection()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        host.SendClick(4, 1);           // select the first data row + focus the grid
        host.RunUntilIdle();
        host.SendKey(Cursorial.Input.Key.DownArrow);
        host.RunUntilIdle();

        var selected = grid.RowSelection.MaterializeSelectedIds(grid.Snapshot);
        int rowId = Assert.Single(selected);
        Assert.Equal("SO-1044", ((Order)grid.Controller!.GetRowObject(rowId)).Id);
        Assert.Equal(1, grid.FocusViewIndex);
    }

    [Fact]
    public void Summaries_render_in_the_footer()
    {
        var (host, grid, _) = Show(columns: 70);
        using var _ = host;

        grid.SummaryDescriptions.Add(new SummaryDescription(grid.Columns[2], AggregateKind.Sum, "#,##0"));
        host.RunUntilIdle();

        // The footer band is the last row: Σ 91,450.
        string footer = Row(host, 13);
        Assert.Contains("Σ", footer);
        Assert.Contains("91,450", footer);
    }

    [Fact]
    public void F2_edit_commit_writes_through_the_compiled_setter_and_reshapes()
    {
        var (host, grid, source) = Show();
        using var _ = host;

        grid.CycleSort(grid.Columns[2]); // Amount ascending
        host.RunUntilIdle();

        host.SendClick(4, 1);            // focus the smallest row (SO-1042, 12450), column 0-ish
        host.RunUntilIdle();

        // Move the focus cell to Amount (index 2) and begin editing.
        grid.SetFocusCell(grid.FocusViewIndex, 2);
        host.SendKey(Cursorial.Input.Key.F2);
        host.RunUntilIdle();
        Assert.True(grid.RowsPresenter!.IsEditing);

        // Type a new value: select-all is active, so typed text replaces.
        host.SendText("99999");
        host.RunUntilIdle();
        host.SendKey(Cursorial.Input.Key.Enter);
        host.RunUntilIdle();

        Assert.False(grid.RowsPresenter!.IsEditing);
        Assert.Equal(99999m, source.Single(o => o.Id == "SO-1042").Amount);

        // The write ticked the live pipeline: SO-1042 repositioned to the end.
        Assert.Contains("SO-1046", Row(host, 1));
        Assert.Contains("SO-1042", Row(host, 4));
    }

    [Fact]
    public void Escape_cancels_editing_without_writing()
    {
        var (host, grid, source) = Show();
        using var _ = host;

        host.SendClick(4, 1);
        host.RunUntilIdle();
        grid.SetFocusCell(grid.FocusViewIndex, 2);
        host.SendKey(Cursorial.Input.Key.F2);
        host.RunUntilIdle();
        Assert.True(grid.RowsPresenter!.IsEditing);

        host.SendText("777");
        host.RunUntilIdle();
        host.SendKey(Cursorial.Input.Key.Escape);
        host.RunUntilIdle();

        Assert.False(grid.RowsPresenter!.IsEditing);
        Assert.Equal(12450m, source[0].Amount); // untouched
    }

    [Fact]
    public void Scrolling_a_tall_source_shows_later_rows()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(50, 10) });
        using var _ = host;
        var source = new ObservableCollection<Order>(
            Enumerable.Range(0, 100).Select(i => new Order($"R{i:D3}", "X", i)));
        var grid = new DataGrid { ItemsSource = source };
        host.ShowRoot(grid);
        host.RunUntilIdle();

        Assert.Contains("R000", Row(host, 1));

        grid.ScrollRowIntoView(60);
        host.RunUntilIdle();

        // Row 60 landed inside the viewport; the top rows scrolled away.
        bool found = false;
        for (int r = 1; r < 10; r++)
            found |= Row(host, r).Contains("R060", StringComparison.Ordinal);
        Assert.True(found, "row 60 should be visible after ScrollRowIntoView");
        Assert.DoesNotContain("R000", Row(host, 1));
    }
}
