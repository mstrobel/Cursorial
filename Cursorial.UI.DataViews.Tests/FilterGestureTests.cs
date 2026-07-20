using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.DataViews;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews;

/// <summary>
/// The filter-surface gesture regressions from the 2026-07 gesture sweep (findings [13]/[14]/[16]):
/// the auto-filter Text cell seeds only grammar-round-trippable condition text (a checklist digest
/// like "(2)" seeds empty, and an UNTOUCHED Enter is a pure dismiss that keeps the InSet fragment);
/// the mouse BeginEdit path honors the §9.2 clear-of-frozen scroll like the keyboard path; and the
/// checklist's tri-state "(Select All)" clicked from the PARTIAL state checks everything (the
/// Excel/DevExpress/Explorer convention), not unchecks.
/// </summary>
public class FilterGestureTests
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

    /// <summary>The surfaces-tests idiom: a 60×14 host with auto-generated Id/Region/Amount columns.</summary>
    private static (UIHeadlessHost Host, DataGrid Grid, ObservableCollection<Order> Source) Show(
        int columns = 60, int rows = 14)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(columns, rows) });
        var source = SampleOrders();
        var grid = new DataGrid { ItemsSource = source };
        host.ShowRoot(grid);
        host.RunUntilIdle();
        return (host, grid, source);
    }

    /// <summary>
    /// The structural-tests geometry: three FIXED-width columns (slots 10 + 12 + 12 = 34) in a
    /// 24-column host, Id frozen Left (FrozenWidth 10) — the §9.2 straddle scenarios.
    /// </summary>
    private static (UIHeadlessHost Host, DataGrid Grid, ObservableCollection<Order> Source) ShowFrozen(
        int columns = 24, int rows = 14)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(columns, rows) });
        var source = SampleOrders();
        var grid = new DataGrid { AutoGenerateColumns = false };
        grid.Columns.Add(new DataGridColumn
        {
            FieldName = "Id", Width = DataGridLength.Cells(8), Fixed = DataGridColumnFixed.Left,
        });
        grid.Columns.Add(new DataGridColumn { FieldName = "Region", Width = DataGridLength.Cells(10) });
        grid.Columns.Add(new DataGridColumn { FieldName = "Amount", Width = DataGridLength.Cells(10) });
        grid.ItemsSource = source;
        host.ShowRoot(grid);
        host.RunUntilIdle();
        return (host, grid, source);
    }

    private static string Row(UIHeadlessHost host, int row) => host.GetRowText(row);

    // ── [13] — checklist digests never seed (nor re-commit through) the Text editor ───────────────

    [Fact]
    public void Checklist_fragment_seeds_empty_and_untouched_enter_preserves_it()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.ShowAutoFilterRow = true;
        host.RunUntilIdle();

        // Checklist-filter Region through the real popup: uncheck West, OK — an InSet fragment
        // whose stored summary is the "(2)" count digest, NOT grammar text.
        var region = grid.Columns[1];
        grid.OpenFilterPopup(region);
        host.RunUntilIdle();
        var popup = grid.ActiveFilterPopup!;
        popup.CheckBoxFor("West")!.IsChecked = false;
        popup.Apply();
        host.RunUntilIdle();

        var fragment = Assert.IsType<FilterValueSetNode>(grid.GetColumnFilter(region));
        Assert.Equal(3, grid.Snapshot.Count); // East ×2 + South

        // Click Region's Text filter cell: the editor seeds EMPTY — the digest must not leak in.
        var entry = grid.RowsPresenter!.ColumnLayout.Entries[1];
        host.SendClick(entry.X + 1, 1); // the band under the header
        host.RunUntilIdle();
        Assert.True(grid.AutoFilterRow!.IsEditing);
        Assert.Equal(string.Empty, grid.AutoFilterRow.Editor!.Text);

        // Enter immediately (untouched): a pure dismiss — the editor closes and the SAME InSet
        // fragment survives (before the fix this replaced it with Contains("(2)") = zero rows).
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.False(grid.AutoFilterRow.IsEditing);
        Assert.Same(fragment, grid.GetColumnFilter(region)); // byte-identical: never re-written
        Assert.Equal(3, grid.Snapshot.Count);

        // Actually-typed text still writes: the InSet is replaced by the grammar condition.
        host.SendClick(entry.X + 1, 1);
        host.RunUntilIdle();
        host.SendText("East");
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.False(grid.AutoFilterRow.IsEditing);
        Assert.IsType<FilterConditionNode>(grid.GetColumnFilter(region));
        Assert.Equal(2, grid.Snapshot.Count); // Contains("East") — the two East rows
    }

    [Fact]
    public void Grammar_fragment_still_seeds_its_condition_text_and_recommits()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.ShowAutoFilterRow = true;
        host.RunUntilIdle();

        // Type a grammar condition on Amount the normal way.
        var entry = grid.RowsPresenter!.ColumnLayout.Entries[2];
        host.SendClick(entry.X + 1, 1);
        host.RunUntilIdle();
        host.SendText(">20000");
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Equal(2, grid.Snapshot.Count); // 31900 + 27300

        // Re-click: a Condition fragment round-trips the grammar — its text seeds the editor.
        host.SendClick(entry.X + 1, 1);
        host.RunUntilIdle();
        Assert.True(grid.AutoFilterRow!.IsEditing);
        Assert.Equal(">20000", grid.AutoFilterRow.Editor!.Text);

        // An untouched Enter re-commits the seeded condition text — the filter is undisturbed
        // (the untouched-dismiss lane applies only to NON-grammar fragments, whose digests
        // seeded empty; a grammar seed IS the condition text, so re-committing is lossless).
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.False(grid.AutoFilterRow.IsEditing);
        Assert.True(grid.HasColumnFilter(grid.Columns[2]));
        Assert.Equal(2, grid.Snapshot.Count);

        // Edited text re-commits through the same cell (the touched lane writes).
        host.SendClick(entry.X + 1, 1);
        host.RunUntilIdle();
        grid.AutoFilterRow.Editor!.Text = string.Empty; // the wipe trips the touched flag
        host.SendText(">15000");
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.False(grid.AutoFilterRow.IsEditing);
        Assert.Equal(3, grid.Snapshot.Count); // 31900, 19800, 27300
        Assert.Contains(">15000", Row(host, 1)); // the new condition re-inks in the cell's well
    }

    // ── [14] — the mouse BeginEdit path honors the §9.2 clear-of-frozen scroll ────────────────────

    [Fact]
    public void Mouse_filter_edit_scrolls_the_straddling_cell_clear_of_the_frozen_region()
    {
        var (host, grid, _) = ShowFrozen();
        using var _ = host;

        grid.ShowAutoFilterRow = true;
        host.RunUntilIdle();

        var layout = grid.RowsPresenter!.ColumnLayout;
        Assert.Equal(1, layout.FrozenCount);
        Assert.Equal(10, layout.FrozenWidth);

        // Offset 5: Region (entry 1, X=10) draws at x 5 — straddling the frozen boundary, its
        // visible sliver starting at the frozen edge (x 10).
        grid.HorizontalOffset = 5;
        host.RunUntilIdle();

        // Click the sliver (x = frozen + 1). BeginEdit must first scroll the cell clear of the
        // frozen region (§9.2 hosted-children policy — the keyboard path already did).
        host.SendClick(layout.FrozenWidth + 1, 1);
        host.RunUntilIdle();

        Assert.True(grid.AutoFilterRow!.IsEditing);
        Assert.Equal(1, grid.AutoFilterRow.EditColumnIndex);
        Assert.Equal(0, grid.HorizontalOffset); // entry.X (10) − FrozenWidth (10): scrolled clear

        // The hosted editor arranges inside the scrolling window, never over the frozen Id cell.
        Assert.True(grid.AutoFilterRow.Editor!.Bounds.Column >= layout.FrozenWidth);

        // And the frame agrees: typed text renders clear of the frozen region.
        host.SendText("zz");
        host.RunUntilIdle();
        string band = Row(host, 1);
        int typedX = band.IndexOf("zz", StringComparison.Ordinal);
        Assert.True(typedX >= layout.FrozenWidth,
                    $"editor text at x={typedX} paints inside the frozen region: '{band}'");
    }

    // ── [16] — (Select All) clicked from the PARTIAL state checks everything ──────────────────────

    [Fact]
    public void Select_all_from_partial_checks_all_then_from_checked_unchecks_all()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.OpenFilterPopup(grid.Columns[1]); // Region: East / South / West, all pre-checked
        host.RunUntilIdle();
        var popup = grid.ActiveFilterPopup!;
        Assert.Equal(3, popup.Rows.Count);

        // Partial: the search box holds focus; Down → (Select All) → Down → East; Space unchecks.
        host.SendKey(Key.DownArrow);
        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        host.SendKey(Key.Character, text: " "); // the real spacebar wire (ND10)
        host.RunUntilIdle();
        Assert.False(popup.CheckBoxFor("East")!.IsChecked);
        Assert.Null(popup.SelectAllBox!.IsChecked); // the tri-state partial (▪) mark

        // Up to (Select All), Space: partial ⇒ CHECK ALL (Excel/DevExpress/Explorer), never
        // uncheck (ToggleButton's own cycle lands null→false before Click — the recorded
        // pre-toggle state must drive the target).
        host.SendKey(Key.UpArrow);
        host.RunUntilIdle();
        host.SendKey(Key.Character, text: " ");
        host.RunUntilIdle();
        Assert.True(popup.Rows.All(r => r.Check.IsChecked == true));
        Assert.True(popup.SelectAllBox.IsChecked);

        // Space again from the fully-checked master: uncheck all (the only uncheck direction).
        host.SendKey(Key.Character, text: " ");
        host.RunUntilIdle();
        Assert.True(popup.Rows.All(r => r.Check.IsChecked == false));
        Assert.False(popup.SelectAllBox.IsChecked);

        host.SendKey(Key.Escape); // dismiss without writing
        host.RunUntilIdle();
        Assert.Null(grid.ActiveFilterPopup);
    }

    // ── The checklist shows each distinct value's row count (the mockup's "value   N" rows) ────────

    [Fact]
    public void Checklist_rows_show_the_distinct_value_counts()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.OpenFilterPopup(grid.Columns[1]); // Region: East ×2, South ×1, West ×1
        host.RunUntilIdle();
        var popup = grid.ActiveFilterPopup!;

        // The checkbox carries just the display value; the COUNT rides a right-docked sibling (aligned
        // into one column). Search still matches on the display half.
        Assert.Equal("East", popup.CheckBoxFor("East")!.Content);
        Assert.Equal("2", popup.CountTextFor("East"));
        Assert.Equal("1", popup.CountTextFor("South"));
        Assert.Equal("1", popup.CountTextFor("West"));

        host.SendKey(Key.Escape);
        host.RunUntilIdle();
    }
}
