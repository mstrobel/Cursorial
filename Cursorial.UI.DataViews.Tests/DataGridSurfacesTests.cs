using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.DataViews;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews;

/// <summary>
/// The v1 shaping surfaces end-to-end (design doc §2.7/§3.4 + the mockup's group-panel /
/// filter-dropdown / flat-grid formatting sections): the group panel's live chips, the checklist
/// popup writing InSet fragments, the auto-filter row's operator grammar, and conditional
/// formatting (data bars / thresholds / row predicates) through the real frame loop — cell + style
/// assertions against the composited frame (the repo's integration idiom).
/// </summary>
public class DataGridSurfacesTests
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

    /// <summary>The first (column, row) of <paramref name="text"/> in the composited frame, or null.</summary>
    private static (int X, int Y)? FindText(UIHeadlessHost host, string text, int rows = 14)
    {
        for (int y = 0; y < rows; y++)
        {
            int x = Row(host, y).IndexOf(text, StringComparison.Ordinal);
            if (x >= 0)
                return (x, y);
        }
        return null;
    }

    // ── Group panel ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Group_panel_renders_chips_with_direction_glyphs()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.ShowGroupPanel = true;
        grid.GroupDescriptions.Add(new GroupDescription(grid.Columns[1])); // Region
        host.RunUntilIdle();

        // The panel bands above the header: chip = "▲ Region ✕"; the header slid to row 1.
        string panel = Row(host, 0);
        Assert.Contains("▲ Region ✕", panel);
        Assert.Contains("Id", Row(host, 1));
        Assert.Contains("Region: East", Row(host, 2)); // first group row under the header
    }

    [Fact]
    public void Group_panel_chip_click_toggles_the_level_direction_in_place()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.ShowGroupPanel = true;
        grid.GroupDescriptions.Add(new GroupDescription(grid.Columns[1]));
        host.RunUntilIdle();

        host.SendClick(4, 0); // the chip body (x=1..10; the ✕ zone starts at 11)
        host.RunUntilIdle();

        Assert.Equal(SortDirection.Descending, grid.GroupDescriptions[0].Direction);
        Assert.Contains("▼ Region ✕", Row(host, 0));
        Assert.Contains("Region: West", Row(host, 2)); // groups re-sorted descending
    }

    [Fact]
    public void Group_panel_remove_zone_ungroups_and_the_empty_prompt_returns()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.ShowGroupPanel = true;
        grid.GroupDescriptions.Add(new GroupDescription(grid.Columns[1]));
        host.RunUntilIdle();

        host.SendClick(11, 0); // the chip's ✕ zone (X + Width − 2 = 1 + 12 − 2)
        host.RunUntilIdle();

        Assert.Empty(grid.GroupDescriptions);
        Assert.Contains("— drag a column header here to add a grouping level —", Row(host, 0));
        Assert.Contains("SO-1042", Row(host, 2)); // flat rows again (panel, header, data…)
    }

    [Fact]
    public void Group_panel_spends_no_row_while_hidden()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // ShowGroupPanel defaults false: the header owns row 0 even while grouped.
        grid.GroupDescriptions.Add(new GroupDescription(grid.Columns[1]));
        host.RunUntilIdle();
        Assert.Contains("Id", Row(host, 0));
        Assert.Contains("Region: East", Row(host, 1));
    }

    // ── Checklist filter popup ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Filter_popup_keyboard_flow_unchecks_a_value_and_writes_the_InSet_fragment()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.OpenFilterPopup(grid.Columns[1]); // Region
        host.RunUntilIdle();

        var popup = grid.ActiveFilterPopup;
        Assert.NotNull(popup);
        Assert.NotNull(FindText(host, "Filter: Region"));

        // Distinct population: East(2), South(1), West(1) — typed dedupe, sorted, all pre-checked.
        Assert.Equal(3, popup.Rows.Count);
        Assert.True(popup.Rows.All(r => r.Check.IsChecked == true));

        // The search box holds focus (posted on open); Down leaves it via directional navigation:
        // (Select All) → East. Space toggles the row's check, Enter commits (the border's OK route).
        host.SendKey(Key.DownArrow);
        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        host.SendKey(Key.Character, text: " "); // the real spacebar wire (ND10)
        host.RunUntilIdle();
        Assert.False(popup.CheckBoxFor("East")!.IsChecked);
        Assert.Null(popup.SelectAllBox!.IsChecked); // partial ⇒ the tri-state master mark

        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.Null(grid.ActiveFilterPopup); // closed
        Assert.True(grid.HasColumnFilter(grid.Columns[1]));
        var fragment = Assert.IsType<FilterValueSetNode>(GetFragment(grid));
        Assert.Equal(2, fragment.Values.Count); // South + West stayed checked

        // The view filtered: only the South and West rows remain.
        Assert.Equal(2, grid.Snapshot.Count);
        Assert.Contains("SO-1046", Row(host, 1));
        Assert.Contains("SO-1047", Row(host, 2));

        // The header's filter affordance stays inked (the amber ▾ cue rides HasColumnFilter).
        Assert.Contains("▾", Row(host, 0));
    }

    private static FilterNode? GetFragment(DataGrid grid) => grid.GetColumnFilter(grid.Columns[1]);

    [Fact]
    public void Filter_popup_select_all_then_OK_clears_the_fragment()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        var region = grid.Columns[1];
        grid.SetColumnFilter(region, FilterNode.InSet(region, ["East"]));
        host.RunUntilIdle();
        Assert.Equal(2, grid.Snapshot.Count); // the two East rows

        grid.OpenFilterPopup(region);
        host.RunUntilIdle();
        var popup = grid.ActiveFilterPopup!;

        // Pre-selection re-checked only the fragment's members.
        Assert.True(popup.CheckBoxFor("East")!.IsChecked);
        Assert.False(popup.CheckBoxFor("South")!.IsChecked);
        Assert.False(popup.CheckBoxFor("West")!.IsChecked);

        foreach (var (_, _, check) in popup.Rows)
            check.IsChecked = true;
        popup.Apply();
        host.RunUntilIdle();

        Assert.False(grid.HasColumnFilter(region)); // all-checked ⇒ the fragment clears (§3.4)
        Assert.Equal(4, grid.Snapshot.Count);
    }

    [Fact]
    public void Filter_popup_search_hides_rows_without_unchecking_them()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.OpenFilterPopup(grid.Columns[1]);
        host.RunUntilIdle();
        var popup = grid.ActiveFilterPopup!;

        popup.SearchBox!.Text = "we";
        host.RunUntilIdle();

        Assert.Equal(Visibility.Collapsed, popup.CheckBoxFor("East")!.Visibility);
        Assert.Equal(Visibility.Visible, popup.CheckBoxFor("West")!.Visibility);
        Assert.True(popup.CheckBoxFor("East")!.IsChecked); // hidden ≠ unchecked

        popup.Apply();
        host.RunUntilIdle();
        Assert.False(grid.HasColumnFilter(grid.Columns[1])); // everything still checked ⇒ no fragment
    }

    [Fact]
    public void Filter_popup_escape_dismisses_without_writing()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.OpenFilterPopup(grid.Columns[1]);
        host.RunUntilIdle();
        var popup = grid.ActiveFilterPopup!;
        popup.CheckBoxFor("East")!.IsChecked = false; // a pending edit Escape must abandon

        host.SendKey(Key.Escape);
        host.RunUntilIdle();

        Assert.Null(grid.ActiveFilterPopup); // the Popup's CloseOnEscape light-dismiss
        Assert.False(grid.HasColumnFilter(grid.Columns[1]));
        Assert.Equal(4, grid.Snapshot.Count);
    }

    // ── Auto-filter row ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Auto_filter_row_parses_the_operator_grammar_into_a_working_filter()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.ShowAutoFilterRow = true;
        host.RunUntilIdle();

        // The band sits under the header (screen row 1) drawing the idle ⌕ affordances.
        Assert.Contains("⌕", Row(host, 1));

        // Click the Amount filter cell → the roving editor hosts there.
        var amount = grid.RowsPresenter!.ColumnLayout.Entries[2];
        host.SendClick(amount.X + 1, 1);
        host.RunUntilIdle();
        Assert.True(grid.AutoFilterRow!.IsEditing);

        host.SendText(">20000");
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.False(grid.AutoFilterRow.IsEditing);
        Assert.True(grid.HasColumnFilter(grid.Columns[2]));

        // 31900 and 27300 pass; the condition text re-inks in the cell's well.
        Assert.Equal(2, grid.Snapshot.Count);
        Assert.Contains("SO-1044", Row(host, 2));
        Assert.Contains("SO-1047", Row(host, 3));
        Assert.Contains(">20000", Row(host, 1));
    }

    [Theory] // the live-canary grammar extensions: %-wildcards + the ! not-equal aliases
    [InlineData("so-10%", FilterOperator.StartsWith, "so-10")]
    [InlineData("%42", FilterOperator.EndsWith, "42")]
    [InlineData("%o-10%", FilterOperator.Contains, "o-10")]
    [InlineData("!East", FilterOperator.NotEquals, "East")]
    [InlineData("!=East", FilterOperator.NotEquals, "East")]
    [InlineData("<>East", FilterOperator.NotEquals, "East")]
    [InlineData(">= 100", FilterOperator.GreaterThanOrEqual, "100")]
    [InlineData("%", null, "%")]           // a lone wildcard is bare text, not an empty needle
    [InlineData("50%", FilterOperator.StartsWith, "50")]
    public void Auto_filter_grammar_parses_wildcards_and_not_equal_aliases(
        string text, FilterOperator? op, string literal)
    {
        var (parsedOp, parsedLiteral) = DataGridAutoFilterRow.ParseCondition(text);
        Assert.Equal(op, parsedOp);
        Assert.Equal(literal, parsedLiteral);
    }

    [Fact]
    public void Auto_filter_wildcards_and_bang_filter_end_to_end()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.ShowAutoFilterRow = true;
        host.RunUntilIdle();

        // "%44" on Id → ends-with: only SO-1044 survives.
        var id = grid.RowsPresenter!.ColumnLayout.Entries[0];
        host.SendClick(id.X + 1, 1);
        host.RunUntilIdle();
        host.SendText("%44");
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Equal(1, grid.Snapshot.Count);
        Assert.Contains("SO-1044", Row(host, 2));

        // Clear, then "!East" on Region → not-equal: South + West remain.
        host.SendClick(id.X + 1, 1);
        host.RunUntilIdle();
        host.SendKey(Key.Character, text: ""); // Ctrl+A select-all is not wired in TextBox; retype instead
        grid.AutoFilterRow!.Editor!.Text = string.Empty;
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Equal(4, grid.Snapshot.Count);

        var region = grid.RowsPresenter!.ColumnLayout.Entries[1];
        host.SendClick(region.X + 1, 1);
        host.RunUntilIdle();
        host.SendText("!East");
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Equal(2, grid.Snapshot.Count);
        Assert.Contains("SO-1046", Row(host, 2)); // South
        Assert.Contains("SO-1047", Row(host, 3)); // West
    }

    [Fact]
    public void Auto_filter_unparseable_literal_keeps_the_editor_open_and_writes_nothing()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.ShowAutoFilterRow = true;
        host.RunUntilIdle();

        var amount = grid.RowsPresenter!.ColumnLayout.Entries[2];
        host.SendClick(amount.X + 1, 1);
        host.RunUntilIdle();

        host.SendText(">abc"); // no decimal in sight
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.True(grid.AutoFilterRow!.IsEditing); // stays open for correction (the commit contract)
        Assert.False(grid.HasColumnFilter(grid.Columns[2]));
        Assert.Equal(4, grid.Snapshot.Count);

        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.False(grid.AutoFilterRow.IsEditing); // Esc abandons
    }

    [Fact]
    public void Auto_filter_empty_commit_clears_the_column_fragment()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.ShowAutoFilterRow = true;
        var amount = grid.Columns[2];
        grid.SetColumnFilter(amount, FilterNode.Condition(amount, FilterOperator.GreaterThan, 20000m));
        host.RunUntilIdle();
        Assert.Equal(2, grid.Snapshot.Count);

        var entry = grid.RowsPresenter!.ColumnLayout.Entries[2];
        host.SendClick(entry.X + 1, 1);
        host.RunUntilIdle();
        Assert.True(grid.AutoFilterRow!.IsEditing);

        grid.AutoFilterRow.Editor!.Text = string.Empty; // wipe the seeded condition text
        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.False(grid.AutoFilterRow.IsEditing);
        Assert.False(grid.HasColumnFilter(amount)); // empty = clear (§1's grammar)
        Assert.Equal(4, grid.Snapshot.Count);
    }

    [Fact]
    public void Auto_filter_bare_text_contains_on_string_columns()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.ShowAutoFilterRow = true;
        host.RunUntilIdle();

        var region = grid.RowsPresenter!.ColumnLayout.Entries[1];
        host.SendClick(region.X + 1, 1);
        host.RunUntilIdle();

        host.SendText("as"); // Contains: E-as-t only
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.Equal(2, grid.Snapshot.Count); // the two East rows
        Assert.Contains("SO-1042", Row(host, 2));
        Assert.Contains("SO-1044", Row(host, 3));
    }

    [Fact]
    public void Auto_filter_editor_keys_never_leak_into_grid_row_gestures()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.ShowAutoFilterRow = true;
        host.RunUntilIdle();

        var region = grid.RowsPresenter!.ColumnLayout.Entries[1];
        host.SendClick(region.X + 1, 1);
        host.RunUntilIdle();

        // A typed space is TEXT for the editor — the grid's Space-selects gesture must not see it
        // (the roving editor owns the keyboard while hosted, like the cell editor).
        host.SendKey(Key.Character, text: " ");
        host.RunUntilIdle();

        Assert.True(grid.RowSelection.IsEmpty);
        Assert.True(grid.AutoFilterRow!.IsEditing);

        host.SendKey(Key.Escape);
        host.RunUntilIdle();
    }

    // ── Conditional formatting through the painter ────────────────────────────────────────────────

    [Fact]
    public void RefreshFormatRules_applies_late_rule_edits_without_a_shape_change()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // Rules added AFTER attach with NO shape edit: inert until the re-collect surface runs.
        grid.Columns[2].FormatRules.Add(new DataBarRule { ColumnKey = grid.Columns[2] });
        host.RunUntilIdle();
        Assert.DoesNotContain("█", Row(host, 2));

        grid.RefreshFormatRules();
        host.RunUntilIdle();
        Assert.Contains("█", Row(host, 2)); // 31900 = the max ⇒ a full fill run
    }

    [Fact]
    public void DataBar_rule_draws_fill_and_track_glyphs_in_the_amount_cells()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.Columns[2].FormatRules.Add(new DataBarRule { ColumnKey = grid.Columns[2] });
        grid.CycleSort(grid.Columns[2]); // any shape push re-collects the rules
        host.RunUntilIdle();

        // Ascending: row 1 = 12450 (fraction 0 ⇒ all track), row 4 = 31900 (fraction 1 ⇒ all fill).
        Assert.Contains("12,450 ░", Row(host, 1));
        Assert.Contains("31,900 █", Row(host, 4));
        Assert.DoesNotContain("█", Row(host, 1));
    }

    [Fact]
    public void Data_bar_track_is_uniform_across_value_widths()
    {
        var (host, grid, source) = Show();
        using var _ = host;

        // A 3-char value beside 5-char ones (the live-canary screenshot): the bar used to start
        // right after EACH row's text, so the origin — and therefore the SCALE — shifted per row.
        source[0].Amount = 999m;
        grid.Columns[2].FormatRules.Add(new DataBarRule { ColumnKey = grid.Columns[2] });
        grid.CycleSort(grid.Columns[2]); // ascending: 999 = all track, 31900 = all fill
        host.RunUntilIdle();

        static int BarStart(string row)
        {
            int fill = row.IndexOf('█');
            int track = row.IndexOf('░');
            return fill < 0 ? track : track < 0 ? fill : Math.Min(fill, track);
        }

        int[] starts = [BarStart(Row(host, 1)), BarStart(Row(host, 2)), BarStart(Row(host, 3)), BarStart(Row(host, 4))];
        Assert.All(starts, s => Assert.True(s >= 0, "every data row draws a bar"));
        // ONE origin per column — equal fractions must render equal bars regardless of the
        // value's character count.
        Assert.All(starts, s => Assert.Equal(starts[0], s));
    }

    [Fact]
    public void Between_threshold_colors_only_in_range_cells()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // §10.5: a Between entry carries a SecondValue upper bound through the controller compile.
        var green = Color.FromRgb(0x9E, 0xCE, 0x6A);
        var amount = grid.Columns[2];
        amount.FormatRules.Add(new ThresholdRule
        {
            ColumnKey = amount,
            Entries = [new ThresholdEntry(FilterOperator.Between, 15000m, new CellFormat(Foreground: green), 28000m)],
        });
        grid.CycleSort(amount);
        host.RunUntilIdle();

        // 19800 and 27300 are inside [15000, 28000] → green; 12450 and 31900 are outside → not.
        foreach (var inRange in new[] { "19,800", "27,300" })
        {
            var hit = FindText(host, inRange);
            Assert.NotNull(hit);
            Assert.Equal(green, host.GetCell(hit.Value.X, hit.Value.Y).Style.Foreground);
        }
        foreach (var outOfRange in new[] { "12,450", "31,900" })
        {
            var miss = FindText(host, outOfRange);
            Assert.NotNull(miss);
            Assert.NotEqual(green, host.GetCell(miss.Value.X, miss.Value.Y).Style.Foreground);
        }
    }

    [Fact]
    public void Icon_and_data_bar_coexist_on_one_cell()
    {
        var (host, grid, source) = Show();
        using var _ = host;

        // §10.7: a bar cell used to SKIP its verdict icon (to keep the track origin column-uniform).
        // Now a per-column icon reserve lets an icon + bar coexist AND stay uniform. Amount carries
        // BOTH a DataBarRule and an icon-bearing ThresholdRule.
        source[0].Amount = 999m;
        var amount = grid.Columns[2];
        // A generous explicit width so icon + grouped value ("31,900") + a bar run all fit (the
        // grouped default format widened the value by the thousands separator).
        amount.Width = DataGridLength.Cells(14);
        var green = Color.FromRgb(0x9E, 0xCE, 0x6A);
        var red = Color.FromRgb(0xF7, 0x76, 0x8E);
        amount.FormatRules.Add(new DataBarRule { ColumnKey = amount });
        amount.FormatRules.Add(new ThresholdRule
        {
            ColumnKey = amount,
            Entries =
            [
                (FilterOperator.GreaterThanOrEqual, 25000m, new CellFormat(Foreground: green, Icon: "▲")),
                (FilterOperator.LessThan, 25000m, new CellFormat(Foreground: red, Icon: "▼")),
            ],
        });
        grid.CycleSort(amount); // ascending
        host.RunUntilIdle();

        // The icon rides the cell's left content edge in the bucket color…
        var entry = grid.RowsPresenter!.ColumnLayout.Entries[2];
        int iconX = entry.X + 1;
        var big = FindText(host, "31,900");
        Assert.NotNull(big);
        Assert.Equal("▲", host.GetCell(iconX, big.Value.Y).Grapheme);
        Assert.Equal(green, host.GetCell(iconX, big.Value.Y).Style.Foreground);

        // …and the bar still renders (icons no longer suppress it), with ONE origin per column.
        static int BarStart(string row)
        {
            int fill = row.IndexOf('█');
            int track = row.IndexOf('░');
            return fill < 0 ? track : track < 0 ? fill : Math.Min(fill, track);
        }
        int[] starts = [BarStart(Row(host, 1)), BarStart(Row(host, 2)), BarStart(Row(host, 3)), BarStart(Row(host, 4))];
        Assert.All(starts, s => Assert.True(s >= 0, "icon-bearing cells still draw bars"));
        Assert.All(starts, s => Assert.Equal(starts[0], s));
    }

    [Fact]
    public void Threshold_rule_colors_the_matching_cells()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        var red = Color.FromRgb(247, 118, 142);
        var amountColumn = grid.Columns[2];
        amountColumn.FormatRules.Add(new ThresholdRule
        {
            ColumnKey = amountColumn,
            Entries = [(FilterOperator.GreaterThanOrEqual, 25000m, new CellFormat(Foreground: red, Bold: true))],
        });
        grid.CycleSort(amountColumn); // shape push re-collects rules
        host.RunUntilIdle();

        // 31900 (row 4 ascending) matched: its digits carry the rule's fg + Bold.
        var hit = FindText(host, "31,900");
        Assert.NotNull(hit);
        var cell = host.GetCell(hit.Value.X, hit.Value.Y);
        Assert.Equal(red, cell.Style.Foreground);
        Assert.True((cell.Style.Attributes & TextAttributes.Bold) != 0);

        // 12450 (row 1) below the threshold: the theme's resting fg, no bold.
        var miss = FindText(host, "12,450");
        Assert.NotNull(miss);
        var restingCell = host.GetCell(miss.Value.X, miss.Value.Y);
        Assert.NotEqual(red, restingCell.Style.Foreground);
        Assert.True((restingCell.Style.Attributes & TextAttributes.Bold) == 0);
    }

    [Fact]
    public void Format_background_fills_the_whole_cell()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // Live-canary fix: a format BACKGROUND used to vanish entirely — the glyph layer's
        // DrawText overwrites the base style's background with its transparent default, so only
        // the foreground ever changed. The background is now a WHOLE-CELL fill.
        var wine = Color.FromRgb(90, 30, 50);
        var amountColumn = grid.Columns[2];
        amountColumn.FormatRules.Add(new ThresholdRule
        {
            ColumnKey = amountColumn,
            Entries = [(FilterOperator.GreaterThanOrEqual, 25000m, new CellFormat(Background: wine))],
        });
        grid.CycleSort(amountColumn); // shape push re-collects rules
        host.RunUntilIdle();

        // The digits sit on the fill…
        var hit = FindText(host, "31,900");
        Assert.NotNull(hit);
        Assert.Equal(wine, host.GetCell(hit.Value.X, hit.Value.Y).Style.Background);
        // …and so does the EMPTY remainder of the cell (right-aligned numerics leave the left
        // side blank — a glyph-only tint would miss it).
        var entry = grid.RowsPresenter!.ColumnLayout.Entries[2];
        Assert.Equal(wine, host.GetCell(entry.X + 1, hit.Value.Y).Style.Background);

        // A below-threshold cell keeps the resting background.
        var miss = FindText(host, "12,450");
        Assert.NotNull(miss);
        Assert.NotEqual(wine, host.GetCell(miss.Value.X, miss.Value.Y).Style.Background);

        // A SELECTED row's tint outranks the format fill (the selection stays legible).
        host.SendClick(hit.Value.X, hit.Value.Y);
        host.RunUntilIdle();
        Assert.NotEqual(wine, host.GetCell(entry.X + 1, hit.Value.Y).Style.Background);
    }

    [Fact]
    public void Icon_set_rule_draws_its_glyphs_at_the_cell_edge()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // The editor's Icon Set lowering — glyph + color per bucket (live-canary fix: CellFormat
        // had no glyph lane, so icon-set rules tinted values but drew no icons at all).
        var green = Color.FromRgb(0x9E, 0xCE, 0x6A);
        var amber = Color.FromRgb(0xE0, 0xAF, 0x68);
        var red = Color.FromRgb(0xF7, 0x76, 0x8E);
        var amountColumn = grid.Columns[2];
        amountColumn.FormatRules.Add(new ThresholdRule
        {
            ColumnKey = amountColumn,
            Entries =
            [
                (FilterOperator.GreaterThanOrEqual, 25000m, new CellFormat(Foreground: green, Icon: "▲")),
                (FilterOperator.GreaterThanOrEqual, 15000m, new CellFormat(Foreground: amber, Icon: "●")),
                (FilterOperator.LessThan, 15000m, new CellFormat(Foreground: red, Icon: "▼")),
            ],
        });
        grid.RefreshFormatRules();
        host.RunUntilIdle();

        // Each bucket's glyph sits at the cell's LEFT content edge in the bucket color, with the
        // value still right-aligned (and tinted) in the remaining width.
        var entry = grid.RowsPresenter!.ColumnLayout.Entries[2];
        int iconX = entry.X + 1; // + CellPadding

        var high = FindText(host, "31,900");
        Assert.NotNull(high);
        Assert.Equal("▲", host.GetCell(iconX, high.Value.Y).Grapheme);
        Assert.Equal(green, host.GetCell(iconX, high.Value.Y).Style.Foreground);
        Assert.Equal(green, host.GetCell(high.Value.X, high.Value.Y).Style.Foreground);

        var mid = FindText(host, "19,800");
        Assert.NotNull(mid);
        Assert.Equal("●", host.GetCell(iconX, mid.Value.Y).Grapheme);
        Assert.Equal(amber, host.GetCell(iconX, mid.Value.Y).Style.Foreground);

        var low = FindText(host, "12,450");
        Assert.NotNull(low);
        Assert.Equal("▼", host.GetCell(iconX, low.Value.Y).Grapheme);
        Assert.Equal(red, host.GetCell(iconX, low.Value.Y).Style.Foreground);
    }

    [Fact]
    public void Predicate_rule_dims_the_whole_matching_row()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        var dim = Color.FromRgb(86, 95, 137);
        Expression<Func<Order, bool>> isWest = o => o.Region == "West";
        grid.Columns[0].FormatRules.Add(new PredicateRule
        {
            ColumnKey = grid.Columns[0],
            RowPredicate = isWest,
            Format = new CellFormat(Foreground: dim),
        });
        grid.CycleSort(grid.Columns[0]); // shape push re-collects rules
        host.RunUntilIdle();

        // SO-1047 (West): EVERY cell dims — the row verdict underlays each cell (§2.7).
        var id = FindText(host, "SO-1047");
        Assert.NotNull(id);
        Assert.Equal(dim, host.GetCell(id.Value.X, id.Value.Y).Style.Foreground);
        int westX = Row(host, id.Value.Y).IndexOf("West", StringComparison.Ordinal);
        Assert.Equal(dim, host.GetCell(westX, id.Value.Y).Style.Foreground);

        // A non-matching row keeps the resting fg.
        var other = FindText(host, "SO-1042");
        Assert.NotNull(other);
        Assert.NotEqual(dim, host.GetCell(other.Value.X, other.Value.Y).Style.Foreground);
    }

    [Fact]
    public void ColorScale_rule_tints_min_and_max_with_the_stop_colors()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        var low = Color.FromRgb(30, 30, 30);
        var high = Color.FromRgb(230, 130, 30);
        var amountColumn = grid.Columns[2];
        amountColumn.FormatRules.Add(new ColorScaleRule { ColumnKey = amountColumn, Stops = [low, high] });
        grid.CycleSort(amountColumn);
        host.RunUntilIdle();

        var min = FindText(host, "12,450");
        var max = FindText(host, "31,900");
        Assert.NotNull(min);
        Assert.NotNull(max);
        Assert.Equal(low, host.GetCell(min.Value.X, min.Value.Y).Style.Foreground);
        Assert.Equal(high, host.GetCell(max.Value.X, max.Value.Y).Style.Foreground);
    }
}
