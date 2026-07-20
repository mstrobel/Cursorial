using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.UI;
using Cursorial.UI.DataViews;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews;

/// <summary>
/// NoColor-tier focus/selection cues (§4): on a monochrome terminal every selection/focus background
/// fill resolves to <c>Colors.Default</c> (invisible) and the framework's <c>.caps-nocolor</c> Inverse
/// STYLE rules never reach the DataGrid's direct-drawn cells — so the presenters must carry the cue as
/// reverse-video (selection / cell-range / focus) plus a bold weight on the focus cell. These pin the
/// draw-time degradation across the three direct-drawn bands (rows, header, auto-filter) and the
/// mutation guard that a color tier keeps the fill-based look (no reverse-video).
/// </summary>
public class DataGridNoColorCueTests
{
    private sealed class Order(string id, string region, decimal amount) : INotifyPropertyChanged
    {
        public string Id { get; } = id;
        public string Region { get; } = region;
        public decimal Amount { get; } = amount;
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static ObservableCollection<Order> SampleOrders() =>
    [
        new("SO-1042", "East", 12450m),
        new("SO-1044", "East", 31900m),
        new("SO-1046", "South", 19800m),
        new("SO-1047", "West", 27300m),
    ];

    /// <summary>Fixed-width columns (Id 8 / Region 10 / Amount 10) so cell positions are deterministic.</summary>
    private static (UIHeadlessHost Host, DataGrid Grid) ShowFixed(TerminalCapabilities caps)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(48, 14),
            Capabilities = caps,
        });
        var grid = new DataGrid { AutoGenerateColumns = false };
        grid.Columns.Add(new DataGridColumn { FieldName = "Id", Width = DataGridLength.Cells(8) });
        grid.Columns.Add(new DataGridColumn { FieldName = "Region", Width = DataGridLength.Cells(10) });
        grid.Columns.Add(new DataGridColumn { FieldName = "Amount", Width = DataGridLength.Cells(10) });
        grid.ItemsSource = SampleOrders();
        host.ShowRoot(grid);
        host.RunUntilIdle();
        return (host, grid);
    }

    private static bool Inverse(UIHeadlessHost host, int col, int row)
        => host.GetCell(col, row).Style.Attributes.HasFlag(TextAttributes.Inverse);

    private static bool Bold(UIHeadlessHost host, int col, int row)
        => host.GetCell(col, row).Style.Attributes.HasFlag(TextAttributes.Bold);

    private static int FindRow(UIHeadlessHost host, string needle)
    {
        for (int r = 0; r < host.FrameBuffer.Rows; r++)
        {
            if (host.GetRowText(r).Contains(needle, StringComparison.Ordinal))
                return r;
        }
        return -1;
    }

    [Fact]
    public void NoColor_selected_row_is_a_solid_reverse_video_bar_with_a_bold_focus_cell()
    {
        var (host, grid) = ShowFixed(HeadlessCapabilities.GenericVt);
        using var _ = host;

        host.SendClick(4, 1);      // select + focus the first data row (screen row 1)
        grid.SetFocusCell(0, 0);   // focus the Id cell
        host.RunUntilIdle();

        int row = FindRow(host, "SO-1042");
        Assert.Equal(1, row);      // header is row 0; the first data row is row 1

        // The whole first cell slot reads as a SOLID reverse-video bar — the fill covers the blank
        // padding AND the text glyphs redraw with inverse (columns 0..7 span the Id slot).
        for (int c = 0; c < 8; c++)
            Assert.True(Inverse(host, c, row), $"selected-row column {c} is not reverse-video");

        string text = host.GetRowText(row);
        int idCol = text.IndexOf("SO-1042", StringComparison.Ordinal);
        int amtCol = text.IndexOf("12450", StringComparison.Ordinal);

        // The focus cell's text is inverse + bold (the cursor emphasis); a non-focus cell in the
        // same selected row is inverse but NOT bold.
        Assert.True(Inverse(host, idCol, row) && Bold(host, idCol, row), "focus cell not inverse+bold");
        Assert.True(Inverse(host, amtCol, row) && !Bold(host, amtCol, row), "non-focus selected cell should be inverse, not bold");

        // An unselected row carries no cue.
        int other = FindRow(host, "SO-1046");
        Assert.False(Inverse(host, 1, other), "unselected row must not be reverse-video");
    }

    [Fact]
    public void Color_tier_selected_row_uses_no_reverse_video()
    {
        var (host, grid) = ShowFixed(HeadlessCapabilities.KittyTruecolor);
        using var _ = host;

        host.SendClick(4, 1);
        grid.SetFocusCell(0, 0);
        host.RunUntilIdle();

        int row = FindRow(host, "SO-1042");
        string text = host.GetRowText(row);
        int idCol = text.IndexOf("SO-1042", StringComparison.Ordinal);

        // On a color tier the cue is a background fill — never reverse-video / bold (the mutation guard
        // that the NoColor branch is gated on the capability, not applied unconditionally).
        Assert.False(Inverse(host, idCol, row));
        Assert.False(Bold(host, idCol, row));
    }

    [Fact]
    public void NoColor_focused_header_cell_is_reverse_video()
    {
        var (host, grid) = ShowFixed(HeadlessCapabilities.GenericVt);
        using var _ = host;

        grid.Focus(Cursorial.UI.Input.FocusNavigationMethod.Programmatic);
        grid.SetFocusCell(0, 0);
        grid.FocusHeaderBand();
        host.RunUntilIdle();
        Assert.Equal(DataGridFocusBand.Header, grid.FocusBand);

        string header = host.GetRowText(0);
        int idCol = header.IndexOf("Id", StringComparison.Ordinal);
        int regionCol = header.IndexOf("Region", StringComparison.Ordinal);
        Assert.True(idCol >= 0 && regionCol >= 0);

        // The focused header caption is reverse-video; an unfocused header column is not.
        Assert.True(Inverse(host, idCol, 0), "focused header caption is not reverse-video");
        Assert.False(Inverse(host, regionCol, 0), "unfocused header column must not be reverse-video");
    }

    [Fact]
    public void NoColor_focused_filter_cell_is_reverse_video()
    {
        var (host, grid) = ShowFixed(HeadlessCapabilities.GenericVt);
        using var _ = host;

        grid.ShowAutoFilterRow = true;
        grid.Focus(Cursorial.UI.Input.FocusNavigationMethod.Programmatic);
        grid.SetFocusCell(0, 0);
        host.RunUntilIdle();

        // F6 cycles Rows → Header → (GroupPanel hidden) → AutoFilter.
        for (int i = 0; i < 4 && grid.FocusBand != DataGridFocusBand.AutoFilter; i++)
        {
            host.SendKey(Key.F6);
            host.RunUntilIdle();
        }
        Assert.Equal(DataGridFocusBand.AutoFilter, grid.FocusBand);

        // The auto-filter band sits directly above the first data row.
        int firstData = FindRow(host, "SO-1042");
        int filterRow = firstData - 1;
        Assert.True(filterRow >= 1, "auto-filter row not below the header");

        // The focused filter cell (Id, col 0) is reverse-video (col 1 = the content origin past padding).
        Assert.True(Inverse(host, 1, filterRow), "focused filter cell is not reverse-video");
    }
}
