using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI.DataViews;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews;

/// <summary>
/// The gallery's exact editing configuration, reproduced headlessly (live-canary report:
/// "committing edits didn't seem to work in the gallery demo"): AUTO-generated columns over the
/// gallery's nested INPC row type (read-only init props mixed with settable Amount/Status —
/// Amount resolves the SPIN editor by key type, Status the Text editor), no explicit column setup.
/// </summary>
public class GalleryEditReproTests
{
    /// <summary>Mirrors Cursorial.Gallery's DataGridViewModel.OrderRow member for member.</summary>
    public sealed class OrderRow : INotifyPropertyChanged
    {
        private decimal _amount;
        private string _status = "";

        public required string Order { get; init; }
        public required string Region { get; init; }
        public required string Rep { get; init; }

        public decimal Amount { get => _amount; set => Set(ref _amount, value); }
        public double Margin { get; init; }
        public string Status { get => _status; set => Set(ref _status, value); }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
        }
    }

    private static (UIHeadlessHost Host, DataGrid Grid, ObservableCollection<OrderRow> Rows) Show()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(80, 24) });
        var rows = new ObservableCollection<OrderRow>
        {
            new() { Order = "SO-1000", Region = "East", Rep = "A. Chen", Amount = 12450m, Margin = 0.21, Status = "Shipped" },
            new() { Order = "SO-1001", Region = "West", Rep = "K. Brooks", Amount = 31900m, Margin = 0.18, Status = "Processing" },
            new() { Order = "SO-1002", Region = "South", Rep = "M. Ortiz", Amount = 19800m, Margin = 0.33, Status = "On Hold" },
        };
        var grid = new DataGrid { ItemsSource = rows }; // the gallery declares NOTHING else
        host.ShowRoot(grid);
        host.RunUntilIdle();
        return (host, grid, rows);
    }

    private static (int X, int Y)? FindText(UIHeadlessHost host, string text)
    {
        for (int y = 0; y < 24; y++)
        {
            int x = host.GetRowText(y).IndexOf(text, StringComparison.Ordinal);
            if (x >= 0)
                return (x, y);
        }
        return null;
    }

    [Fact]
    public void Amount_edit_commits_and_rerenders_under_the_gallery_configuration()
    {
        var (host, grid, rows) = Show();
        using var _ = host;

        // Click row 0's Amount cell (auto columns: Order/Region/Rep/Amount/Margin/Status).
        var entries = grid.RowsPresenter!.ColumnLayout.Entries;
        int amountIndex = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Column.FieldName == "Amount")
                amountIndex = i;
        }
        Assert.True(amountIndex >= 0);
        host.SendClick(entries[amountIndex].X + 1, 1);
        host.RunUntilIdle();
        Assert.Equal(amountIndex, grid.FocusColumnIndex);

        // Enter begins the edit (Amount = decimal ⇒ the Spin editor's TextBox face).
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.True(grid.RowsPresenter!.IsEditing);

        // Type a replacement (the editor pre-selects, so typing replaces) and commit.
        host.SendText("999");
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.False(grid.RowsPresenter!.IsEditing);
        Assert.Equal(999m, rows[0].Amount);          // the source object took the write
        Assert.NotNull(FindText(host, "999"));       // and the cell re-rendered the new value
    }

    [Fact]
    public void Edit_session_rides_its_row_id_through_live_churn()
    {
        var (host, grid, rows) = Show();
        using var _ = host;

        // Begin editing SO-1001's Amount (view row 1) and type a replacement…
        var entries = grid.RowsPresenter!.ColumnLayout.Entries;
        int amountIndex = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Column.FieldName == "Amount")
                amountIndex = i;
        }
        host.SendClick(entries[amountIndex].X + 1, 2);
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.True(grid.RowsPresenter!.IsEditing);
        host.SendText("777");
        host.RunUntilIdle();

        // …then churn the source underneath (the gallery feed's profile): SO-1000 leaves, every
        // later view index shifts up. The session must RIDE ITS ROW ID — the editor re-anchors to
        // SO-1001's new slot, and the commit writes SO-1001, never whatever sits at the stale index.
        // (Before the fix: the commit fell off the shrunken view and was SILENTLY DISCARDED — the
        // gallery report's "committing edits didn't seem to work".)
        var edited = rows[1];
        rows.RemoveAt(0);
        host.RunUntilIdle();
        Assert.True(grid.RowsPresenter!.IsEditing);
        Assert.Equal(0, grid.RowsPresenter!.EditCell.ViewIndex); // the editor followed its row

        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.False(grid.RowsPresenter!.IsEditing);
        Assert.Equal(777m, edited.Amount);
        Assert.NotNull(FindText(host, "777"));
    }

    [Fact]
    public void Edit_session_cancels_when_its_row_is_removed()
    {
        var (host, grid, rows) = Show();
        using var _ = host;

        var entries = grid.RowsPresenter!.ColumnLayout.Entries;
        int amountIndex = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Column.FieldName == "Amount")
                amountIndex = i;
        }
        host.SendClick(entries[amountIndex].X + 1, 2); // SO-1001 (view 1)
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        host.SendText("555");
        host.RunUntilIdle();

        // The edited row ITSELF leaves: the session cancels — the draft is discarded, never
        // written to the row that slides into the vacated slot.
        var survivor = rows[2];
        rows.RemoveAt(1);
        host.RunUntilIdle();
        Assert.False(grid.RowsPresenter!.IsEditing);
        Assert.Equal(19800m, survivor.Amount); // untouched
    }

    [Fact]
    public void Status_edit_commits_and_rerenders_under_the_gallery_configuration()
    {
        var (host, grid, rows) = Show();
        using var _ = host;

        var entries = grid.RowsPresenter!.ColumnLayout.Entries;
        int statusIndex = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Column.FieldName == "Status")
                statusIndex = i;
        }
        Assert.True(statusIndex >= 0);
        host.SendClick(entries[statusIndex].X + 1, 1);
        host.RunUntilIdle();

        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.True(grid.RowsPresenter!.IsEditing);

        host.SendText("Delivered");
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.False(grid.RowsPresenter!.IsEditing);
        Assert.Equal("Delivered", rows[0].Status);
        Assert.NotNull(FindText(host, "Delivered"));
    }
}
