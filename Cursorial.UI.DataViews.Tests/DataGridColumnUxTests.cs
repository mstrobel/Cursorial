using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.DataViews;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews;

/// <summary>
/// The §1-deferred column UX end-to-end (header-edge resize, drag-reorder with the mockup's
/// <c>draghdr</c> adorners, drag-off-to-hide, the column chooser, and the layout persistence
/// surface) through <c>UIHeadlessHost</c> + synthesized mouse gestures (ButtonDown → Move… →
/// ButtonUp, the real capture-routed path — <c>SendClick</c> can't hold a button across moves).
/// Geometry notes for the default 60×14 grid over Id/Region/Amount (all Auto):
/// Id X=0 W=7 (span 0–8, edge 8), Region X=9 W=9 (span 9–19, ▾ 18, edge 19),
/// Amount X=20 W=9 — captions draw at X+1.
/// </summary>
public class DataGridColumnUxTests
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
        int columns = 60, int rows = 14)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(columns, rows) });
        var source = SampleOrders();
        var grid = new DataGrid { ItemsSource = source };
        host.ShowRoot(grid);
        host.RunUntilIdle();
        return (host, grid, source);
    }

    private static string Row(UIHeadlessHost host, int row) => host.GetRowText(row);

    private static (int X, int Y)? FindText(UIHeadlessHost host, string text, int rows = 14, int fromRow = 0)
    {
        for (int y = fromRow; y < rows; y++)
        {
            int x = Row(host, y).IndexOf(text, StringComparison.Ordinal);
            if (x >= 0)
                return (x, y);
        }
        return null;
    }

    /// <summary>
    /// Hover-then-click: ButtonBase's release-click gates on <c>IsPointerOver</c>, which the hover
    /// pass establishes per FRAME — a same-frame down+up with no prior motion never sees it (a real
    /// pointer always moves before it presses, so the pre-move is fidelity, not a workaround).
    /// </summary>
    private static void HoverClick(UIHeadlessHost host, int x, int y)
    {
        host.SendMouseMove(x, y);
        host.RunUntilIdle();
        host.SendClick(x, y);
        host.RunUntilIdle();
    }

    // ── Gesture synthesis (the capture-routed drag path) ──────────────────────────────────────────

    private static void Press(UIHeadlessHost host, int x, int y, MouseButton button = MouseButton.Left)
    {
        host.SendInput(new MouseEvent
        {
            Kind = MouseEventKind.ButtonDown,
            Position = new CellPosition(x, y),
            Button = button,
            ButtonsHeld = MouseButtons.None,
            Modifiers = KeyModifiers.None,
            ClickCount = 1,
            Timestamp = default, // SendInput stamps the fake clock
        });
        host.RunUntilIdle();
    }

    private static void Move(UIHeadlessHost host, int x, int y)
    {
        host.SendInput(new MouseEvent
        {
            Kind = MouseEventKind.Move,
            Position = new CellPosition(x, y),
            Button = MouseButton.None,
            ButtonsHeld = MouseButtons.Left,
            Modifiers = KeyModifiers.None,
            Timestamp = default,
        });
        host.RunUntilIdle();
    }

    private static void Release(UIHeadlessHost host, int x, int y, MouseButton button = MouseButton.Left)
    {
        host.SendInput(new MouseEvent
        {
            Kind = MouseEventKind.ButtonUp,
            Position = new CellPosition(x, y),
            Button = button,
            ButtonsHeld = MouseButtons.Left,
            Modifiers = KeyModifiers.None,
            Timestamp = default,
        });
        host.RunUntilIdle();
    }

    // ── Header-edge resize ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resize_drag_widens_a_fixed_column_and_the_bands_follow()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.Columns[0].Width = DataGridLength.Cells(7); // pin Id (same as its Auto resolve)
        host.RunUntilIdle();
        Assert.Equal((10, 0), FindText(host, "Region")); // baseline: Region caption at X=9+1

        // Drag the Id edge (the right padding cell of its span, x=8) 4 cells right.
        Press(host, 8, 0);
        Move(host, 10, 0);
        Move(host, 12, 0);
        Release(host, 12, 0);

        Assert.Equal(DataGridLength.Cells(11), grid.Columns[0].Width);
        Assert.Equal((14, 0), FindText(host, "Region")); // the header band re-inked: 11+2 span → caption at 14
        Assert.Contains("East", Row(host, 1));           // the rows band re-resolved too (cells shifted, not lost)
    }

    [Fact]
    public void Resize_drag_clamps_at_MinWidth()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.Columns[0].Width = DataGridLength.Cells(7);
        grid.Columns[0].MinWidth = 4;
        host.RunUntilIdle();

        // Drag the edge far past the minimum: 7 + (0−8) = −1 → clamps at MinWidth 4.
        Press(host, 8, 0);
        Move(host, 0, 0);
        Release(host, 0, 0);

        Assert.Equal(DataGridLength.Cells(4), grid.Columns[0].Width);
    }

    [Fact]
    public void Double_click_on_the_edge_returns_to_Auto_best_fit()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.Columns[0].Width = DataGridLength.Cells(15); // manually widened…
        grid.NotifyColumnGeometryChanged(); // a direct property write has no notification — apps funnel here
        host.RunUntilIdle();
        Assert.Equal((18, 0), FindText(host, "Region")); // span 0–16, Region caption at 17+1

        // …then best-fit via edge double-click (edge = 15+2−1 = 16). The gesture must both return
        // the width to Auto AND drop the monotonic growth memory so the resolve is tight (7), not
        // the remembered 15.
        host.SendClick(16, 0, MouseButton.Left, clickCount: 2);
        host.RunUntilIdle();

        Assert.Equal(DataGridLength.Auto, grid.Columns[0].Width);
        Assert.Equal((10, 0), FindText(host, "Region")); // back at the tight Auto layout
    }

    // ── Header drag-reorder ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Drag_reorder_moves_the_column_without_sorting()
    {
        var (host, grid, _) = Show();
        using var _ = host;
        var region = grid.Columns[1];

        // Press the Region BODY (x=11) and drag left into Id's left half (x=2 → drop slot 0).
        Press(host, 11, 0);
        Move(host, 7, 0);  // ≥ 2 cells — promotes to a drag (no sort may ride this gesture)
        Move(host, 2, 0);
        Release(host, 2, 0);

        Assert.Same(region, grid.Columns[0]); // Columns order moved: [Region, Id, Amount]
        Assert.Equal("Id", grid.Columns[1].FieldName);
        Assert.Empty(grid.SortDescriptions); // the drag consumed the press — no click, no sort

        // The rendered header re-inked in the new order: Region caption now left of Id's.
        var regionAt = FindText(host, "Region");
        var idAt = FindText(host, "Id");
        Assert.NotNull(regionAt);
        Assert.NotNull(idAt);
        Assert.True(regionAt.Value.X < idAt.Value.X);
    }

    [Fact]
    public void Plain_header_click_still_sorts()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // A click (press + release, no movement) is the sort gesture — deferred to the release now,
        // but indistinguishable to the user.
        host.SendClick(11, 0); // Region body
        host.RunUntilIdle();

        var sort = Assert.Single(grid.SortDescriptions);
        Assert.Same(grid.Columns[1], sort.ColumnKey);
        Assert.Equal(SortDirection.Ascending, sort.Direction);
        Assert.Contains("▲", Row(host, 0));
    }

    [Fact]
    public void Mid_drag_escape_cancels_the_reorder()
    {
        var (host, grid, _) = Show();
        using var _ = host;
        var region = grid.Columns[1];

        Press(host, 11, 0);
        Move(host, 2, 0); // promoted — the drop slot is armed at 0
        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Release(host, 2, 0); // the release after a cancel must be inert (no drop, no sort)

        Assert.Same(region, grid.Columns[1]); // order unchanged
        Assert.Empty(grid.SortDescriptions);  // and the press never became a click
    }

    [Fact]
    public void Dragging_a_header_below_the_band_hides_the_column()
    {
        var (host, grid, _) = Show();
        using var _ = host;
        var region = grid.Columns[1];

        Press(host, 11, 0);
        Move(host, 11, 2);
        Move(host, 11, 4); // > 2 rows below the band — the hide zone
        Release(host, 11, 4);

        Assert.False(region.Visible);
        Assert.Equal(3, grid.Columns.Count); // hidden, not removed — shaping identity survives
        Assert.Null(FindText(host, "Region", rows: 1)); // gone from the header band
        Assert.Contains("Amount", Row(host, 0));        // the remaining columns closed the gap
    }

    // ── Column chooser ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Right_click_opens_the_chooser_listing_hidden_and_visible_columns()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.Columns[1].Visible = false; // hide Region programmatically first
        grid.NotifyColumnGeometryChanged();
        host.RunUntilIdle();

        host.SendClick(5, 0, MouseButton.Right);
        host.RunUntilIdle();

        var chooser = grid.ActiveColumnChooser;
        Assert.NotNull(chooser);
        Assert.NotNull(FindText(host, "Column Chooser"));

        var chip = Assert.Single(chooser.HiddenChips);
        Assert.Same(grid.Columns[1], chip.Column);
        Assert.NotNull(FindText(host, "⠿ Region")); // the mockup's grip chip
        Assert.Equal(2, chooser.VisibleChecks.Count); // Id + Amount, checked
        Assert.True(chooser.VisibleChecks.All(c => c.Check.IsChecked == true));
    }

    [Fact]
    public void Chooser_chip_click_shows_the_column_at_its_original_position()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.Columns[1].Visible = false;
        grid.NotifyColumnGeometryChanged();
        host.RunUntilIdle();
        grid.OpenColumnChooser();
        host.RunUntilIdle();

        // Click the ⠿ Region chip through the composited frame (the real popup surface).
        var chipAt = FindText(host, "⠿ Region");
        Assert.NotNull(chipAt);
        HoverClick(host, chipAt.Value.X + 3, chipAt.Value.Y);

        Assert.True(grid.Columns[1].Visible);
        Assert.Equal((10, 0), FindText(host, "Region")); // back between Id and Amount — order is the collection
        Assert.Empty(grid.ActiveColumnChooser!.HiddenChips); // the chooser re-partitioned in place
    }

    [Fact]
    public void Chooser_check_click_hides_and_ShowAll_restores()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.OpenColumnChooser();
        host.RunUntilIdle();
        var chooser = grid.ActiveColumnChooser!;
        Assert.Equal(3, chooser.VisibleChecks.Count);

        // Uncheck Region (click its check row on the POPUP surface — search below the header band,
        // whose own "Region" caption would match first) → hides in the grid behind.
        var checkAt = FindText(host, "Region", fromRow: 1);
        Assert.NotNull(checkAt);
        HoverClick(host, checkAt.Value.X, checkAt.Value.Y);

        Assert.False(grid.Columns[1].Visible);
        Assert.Single(grid.ActiveColumnChooser!.HiddenChips);

        // Show All brings everything back.
        var showAllAt = FindText(host, "Show All");
        Assert.NotNull(showAllAt);
        HoverClick(host, showAllAt.Value.X, showAllAt.Value.Y);

        Assert.True(grid.Columns.All(c => c.Visible));
        Assert.Empty(grid.ActiveColumnChooser!.HiddenChips);
    }

    [Fact]
    public void Chooser_escape_dismisses()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.OpenColumnChooser();
        host.RunUntilIdle();
        Assert.NotNull(grid.ActiveColumnChooser);

        host.SendKey(Key.Escape);
        host.RunUntilIdle();

        Assert.Null(grid.ActiveColumnChooser); // the Popup's CloseOnEscape light-dismiss
    }

    // ── Layout persistence ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Column_layout_round_trips_order_width_visibility_and_shape_state()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // Author a layout: Amount first at 12 cells, Region hidden, sorted by Amount desc,
        // grouped by Region.
        grid.Columns.Move(2, 0);
        grid.Columns[2].Visible = false; // Region (now last)
        grid.Columns[0].Width = DataGridLength.Cells(12);
        grid.SortDescriptions.Add(SortDescription.Descending(grid.Columns[0]));
        grid.GroupDescriptions.Add(new GroupDescription(grid.Columns[2]));
        host.RunUntilIdle();

        var state = grid.GetColumnLayout();

        // Wreck everything.
        grid.Columns.Move(0, 2);
        foreach (var column in grid.Columns)
        {
            column.Width = DataGridLength.Auto;
            column.Visible = true;
        }
        grid.SortDescriptions.Clear();
        grid.GroupDescriptions.Clear();
        host.RunUntilIdle();

        grid.ApplyColumnLayout(state);
        host.RunUntilIdle();

        Assert.Equal(["Amount", "Id", "Region"], grid.Columns.Select(c => c.FieldName!).ToArray());
        Assert.Equal(DataGridLength.Cells(12), grid.Columns[0].Width);
        Assert.False(grid.Columns[2].Visible);
        var sort = Assert.Single(grid.SortDescriptions);
        Assert.Same(grid.Columns[0], sort.ColumnKey);
        Assert.Equal(SortDirection.Descending, sort.Direction);
        var group = Assert.Single(grid.GroupDescriptions);
        Assert.Same(grid.Columns[2], group.ColumnKey);

        // And the restored layout actually renders: Amount's caption leads the header band.
        var amountAt = FindText(host, "Amount");
        var idAt = FindText(host, "Id");
        Assert.NotNull(amountAt);
        Assert.NotNull(idAt);
        Assert.True(amountAt.Value.X < idAt.Value.X);
        Assert.Null(FindText(host, "Region", rows: 1));
    }

    [Fact]
    public void Applying_a_layout_tolerates_unknown_and_missing_fields()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        var state = new DataGridLayoutState
        {
            Columns =
            [
                new DataGridColumnLayoutEntry("Ghost", "10", true),   // no such column — skipped
                new DataGridColumnLayoutEntry("Amount", "8", true),   // pulled to the front
            ],
            Sorts = [new DataGridSortLayoutEntry("Ghost", false)],    // unresolvable — skipped
            Groups = [],
        };

        grid.ApplyColumnLayout(state);
        host.RunUntilIdle();

        Assert.Equal("Amount", grid.Columns[0].FieldName);
        Assert.Equal(DataGridLength.Cells(8), grid.Columns[0].Width);
        Assert.Empty(grid.SortDescriptions);
        Assert.Equal(3, grid.Columns.Count); // unmatched grid columns kept, sunk after the matched
    }
}
