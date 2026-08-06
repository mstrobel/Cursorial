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
/// The group-panel chip DRAG gestures (the gesture-sweep findings [4]+[5], ported from the header
/// presenter's Pending→threshold→drag pattern): press-and-drag a chip in-band reorders the
/// grouping levels (<c>GroupDescriptions.Move</c>), dragging it below the 1-row band ungroups the
/// dragged level, mid-drag Esc cancels via the root tunnel hook — and the press's CLICK actions
/// (body = direction toggle, ✕ = remove) moved from the down to the RELEASE, so a promoted drag
/// never also toggles and a dragged-away ✕ press never removes (the press-cancel rule).
/// Group panel = screen row 0 with <c>ShowGroupPanel=true</c>; header row 1, group rows from 2.
/// Chip geometry (Region + Amount grouped): Region X=1 W=12 (body 1–10, ✕ 11), Amount X=16 W=12
/// (midpoint 22, ✕ 26).
/// </summary>
public class GroupPanelGestureTests
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
    /// A 60-column host with the panel shown and TWO grouping levels (Region then Amount), so both
    /// chips render on row 0: <c>▲ Region ✕ ▸ ▲ Amount ✕</c> at the geometry the class doc pins.
    /// </summary>
    private static (UIHeadlessHost Host, DataGrid Grid) Show()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 14) });
        var grid = new DataGrid { AutoGenerateColumns = false };
        grid.Columns.Add(new DataGridColumn { FieldName = "Id", Width = DataGridLength.Cells(8) });
        grid.Columns.Add(new DataGridColumn { FieldName = "Region", Width = DataGridLength.Cells(10) });
        grid.Columns.Add(new DataGridColumn { FieldName = "Amount", Width = DataGridLength.Cells(10) });
        grid.ItemsSource = SampleOrders();
        grid.ShowGroupPanel = true;
        grid.GroupDescriptions.Add(new GroupDescription(grid.Columns[1])); // Region
        grid.GroupDescriptions.Add(new GroupDescription(grid.Columns[2])); // Amount
        host.ShowRoot(grid);
        host.RunUntilIdle();
        return (host, grid);
    }

    private static void Send(UIHeadlessHost host, MouseEventKind kind, int x, int y)
    {
        host.SendInput(new MouseEvent
        {
            Kind = kind,
            Position = new CellPosition(x, y),
            Button = MouseButton.Left,
            ButtonsHeld = kind == MouseEventKind.ButtonUp ? MouseButtons.None : MouseButtons.Left,
            Modifiers = KeyModifiers.None,
            Timestamp = DateTimeOffset.UnixEpoch,
        });
        host.RunUntilIdle();
    }

    [Fact]
    public void Chip_drag_right_reorders_the_grouping_levels()
    {
        var (host, grid) = Show();
        using var _ = host;
        var panel = grid.GroupPanel!;

        // Press the Region chip body, move 2 cells (promotes to a chip drag), then onto the
        // Amount chip's RIGHT half (x=24 ≥ midpoint 22 → slot 2 = after Amount) and release.
        host.SendMouseMove(5, 0);
        Send(host, MouseEventKind.ButtonDown, 5, 0);
        Send(host, MouseEventKind.Move, 7, 0);
        Assert.True(panel.IsDraggingChip); // the threshold promoted the pending press
        Send(host, MouseEventKind.Move, 24, 0);
        Assert.Equal(2, panel.DropSlot);
        Send(host, MouseEventKind.ButtonUp, 24, 0);

        // GroupDescriptions.Move(0, 1): Amount now leads, Region nests under it.
        Assert.Equal(2, grid.GroupDescriptions.Count);
        Assert.Same(grid.Columns[2], grid.GroupDescriptions[0].ColumnKey);
        Assert.Same(grid.Columns[1], grid.GroupDescriptions[1].ColumnKey);

        // Within-drag press-cancel: the promoted drag never ALSO toggled a direction.
        Assert.Equal(SortDirection.Ascending, grid.GroupDescriptions[0].Direction);
        Assert.Equal(SortDirection.Ascending, grid.GroupDescriptions[1].Direction);

        // The band re-inked in the new order.
        string row0 = host.GetRowText(0);
        int amountAt = row0.IndexOf("▲ Amount ✕", StringComparison.Ordinal);
        int regionAt = row0.IndexOf("▲ Region ✕", StringComparison.Ordinal);
        Assert.True(amountAt >= 0 && regionAt > amountAt, $"chip order wrong: [{row0}]");
        Assert.False(panel.IsDraggingChip); // the release funneled through the capture cleanup
    }

    [Fact]
    public void Chip_drag_below_the_band_ungroups_the_dragged_level()
    {
        var (host, grid) = Show();
        using var _ = host;
        var panel = grid.GroupPanel!;

        // Press the Region chip body and drag DOWN off the band (dy=2 promotes; row 2 = the rows
        // area). Off-band there is no drop slot — the ▾ cue never promises a reorder.
        host.SendMouseMove(5, 0);
        Send(host, MouseEventKind.ButtonDown, 5, 0);
        Send(host, MouseEventKind.Move, 5, 2);
        Assert.True(panel.IsDraggingChip);
        Assert.Equal(-1, panel.DropSlot);

        // Release on the HEADER row directly below: the zone decision is ANY local.Row > 0 (the
        // 1-row band has no jitter buffer — the next row down is the DevExpress return-to-header
        // ungroup target), so this ungroups the dragged level.
        Send(host, MouseEventKind.Move, 5, 1);
        Send(host, MouseEventKind.ButtonUp, 5, 1);

        var level = Assert.Single(grid.GroupDescriptions);
        Assert.Same(grid.Columns[2], level.ColumnKey);           // Amount survived
        Assert.Equal(SortDirection.Ascending, level.Direction);  // and never toggled
        Assert.True(grid.Columns[1].Visible);                    // ungroup, never the header's hide path
        Assert.DoesNotContain("Region ✕", host.GetRowText(0));
        Assert.Contains("▲ Amount ✕", host.GetRowText(0));
    }

    [Fact]
    public void Sub_threshold_click_still_toggles_direction_preserving_the_summary_ordering()
    {
        var (host, grid) = Show();
        using var _ = host;
        var panel = grid.GroupPanel!;

        // Give the Region level a summary ordering: the toggle must `with`-preserve it.
        grid.GroupDescriptions[0] = grid.GroupDescriptions[0] with
        {
            OrderBySummary = new SummaryDescription(grid.Columns[2], AggregateKind.Sum),
            SummaryDirection = SortDirection.Descending,
        };
        host.RunUntilIdle();

        // Press the chip body, jitter 1 cell (below the 2-cell threshold), release: the CLICK.
        host.SendMouseMove(5, 0);
        Send(host, MouseEventKind.ButtonDown, 5, 0);
        Send(host, MouseEventKind.Move, 6, 0);
        Assert.False(panel.IsDraggingChip); // sub-threshold — still a click-in-waiting
        Send(host, MouseEventKind.ButtonUp, 6, 0);

        Assert.Equal(2, grid.GroupDescriptions.Count);                       // nothing removed
        Assert.Same(grid.Columns[1], grid.GroupDescriptions[0].ColumnKey);   // order intact
        Assert.Equal(SortDirection.Descending, grid.GroupDescriptions[0].Direction); // toggled
        Assert.NotNull(grid.GroupDescriptions[0].OrderBySummary);            // summary ordering survived
        Assert.Equal(SortDirection.Descending, grid.GroupDescriptions[0].SummaryDirection);
        Assert.Contains("▼ Region ✕", host.GetRowText(0));
    }

    [Fact]
    public void Remove_zone_acts_on_release_and_a_dragged_away_press_never_removes()
    {
        var (host, grid) = Show();
        using var _ = host;
        var panel = grid.GroupPanel!;

        // Press Amount's ✕ (x=26): the DOWN alone no longer removes…
        host.SendMouseMove(26, 0);
        Send(host, MouseEventKind.ButtonDown, 26, 0);
        Assert.Equal(2, grid.GroupDescriptions.Count); // still both — the down is only a press
        // …the RELEASE is the click.
        Send(host, MouseEventKind.ButtonUp, 26, 0);
        var remaining = Assert.Single(grid.GroupDescriptions);
        Assert.Same(grid.Columns[1], remaining.ColumnKey); // Region remains

        // Press Region's ✕ (x=11) and DRAG away: the destructive press cancels — it never
        // promotes to a drag, and the release elsewhere neither removes nor ungroups.
        host.SendMouseMove(11, 0);
        Send(host, MouseEventKind.ButtonDown, 11, 0);
        Send(host, MouseEventKind.Move, 15, 3);
        Assert.False(panel.IsDraggingChip);
        Send(host, MouseEventKind.ButtonUp, 15, 3); // below the band — would ungroup were this a drag
        var level = Assert.Single(grid.GroupDescriptions);
        Assert.Same(grid.Columns[1], level.ColumnKey);
        Assert.Equal(SortDirection.Ascending, level.Direction); // and no toggle leaked through
        Assert.Contains("▲ Region ✕", host.GetRowText(0));
    }

    [Fact]
    public void Mid_drag_escape_cancels_and_the_release_is_inert()
    {
        var (host, grid) = Show();
        using var _ = host;
        var panel = grid.GroupPanel!;

        grid.Focus(Cursorial.UI.Input.FocusNavigationMethod.Programmatic);
        host.RunUntilIdle();

        // Promote a Region-chip drag onto a live slot (after Amount)…
        host.SendMouseMove(5, 0);
        Send(host, MouseEventKind.ButtonDown, 5, 0);
        Send(host, MouseEventKind.Move, 24, 0);
        Assert.True(panel.IsDraggingChip);
        Assert.Equal(2, panel.DropSlot);

        // …then Esc: the root tunnel hook cancels the drag (capture release funnels the cleanup).
        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.False(panel.IsDraggingChip);
        Assert.Equal(-1, panel.DropSlot);

        // The trailing release does nothing — no reorder, no toggle, no removal.
        Send(host, MouseEventKind.ButtonUp, 24, 0);
        Assert.Equal(2, grid.GroupDescriptions.Count);
        Assert.Same(grid.Columns[1], grid.GroupDescriptions[0].ColumnKey); // order unchanged
        Assert.Same(grid.Columns[2], grid.GroupDescriptions[1].ColumnKey);
        Assert.Equal(SortDirection.Ascending, grid.GroupDescriptions[0].Direction);
        Assert.Equal(SortDirection.Ascending, grid.GroupDescriptions[1].Direction);
        Assert.Contains("▲ Region ✕", host.GetRowText(0)); // chips re-inked in the original order
    }
}
