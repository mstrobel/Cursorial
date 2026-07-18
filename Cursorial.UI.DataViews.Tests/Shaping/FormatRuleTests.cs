using System.Linq.Expressions;

using Cursorial.Output;
using Cursorial.UI.DataViews.Shaping;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews.Shaping;

/// <summary>
/// The conditional-formatting engine (design doc §2.7): rule compilation against the typed key
/// vectors, the per-publish stats block anchoring DataBar/ColorScale, threshold first-match-wins,
/// predicate row verdicts, and the distinct-value surface the checklist popup consumes (§3.4).
/// All headless — the engine never sees a UI type (invariant 1).
/// </summary>
public class FormatRuleTests
{
    private sealed class Order(string id, string? region, decimal amount)
    {
        public string Id { get; } = id;
        public string? Region { get; set; } = region;
        public decimal Amount { get; set; } = amount;
    }

    private static readonly ShapingColumnDescription[] Columns =
    [
        new() { Key = "Id", FieldName = nameof(Order.Id) },
        new() { Key = "Region", FieldName = nameof(Order.Region) },
        new() { Key = "Amount", FieldName = nameof(Order.Amount) },
    ];

    private static DataViewController<Order> NewController(params Order[] rows)
    {
        var controller = new DataViewController<Order>();
        controller.SetColumns(Columns);
        controller.AttachSource(new List<Order>(rows), liveUpdates: false);
        return controller;
    }

    private static Order[] SampleRows() =>
    [
        new("A", "East", 100m),
        new("B", "West", 300m),
        new("C", "East", 200m),
        new("D", null, 500m),
    ];

    private static int RowId(DataViewController<Order> controller, string id)
    {
        var snapshot = controller.Snapshot;
        for (int i = 0; i < snapshot.Count; i++)
        {
            var row = snapshot.GetRow(i);
            if (!row.IsGroup && controller.RowAccessor(row.RowId).Id == id)
                return row.RowId;
        }
        throw new InvalidOperationException($"Row '{id}' not visible.");
    }

    // ── DataBar + the stats block ─────────────────────────────────────────────────────────────────

    [Fact]
    public void DataBar_fraction_anchors_to_the_view_min_max()
    {
        using var controller = NewController(SampleRows());
        controller.SetFormatRules([new DataBarRule { ColumnKey = "Amount" }]);

        Assert.True(controller.HasFormatRules);
        Assert.Equal(0.0, controller.GetDataBarFraction(RowId(controller, "A"), "Amount")); // min
        Assert.Equal(1.0, controller.GetDataBarFraction(RowId(controller, "D"), "Amount")); // max
        Assert.Equal(0.25, controller.GetDataBarFraction(RowId(controller, "C"), "Amount"), 3); // (200−100)/400
    }

    [Fact]
    public void DataBar_stats_recompute_over_the_FILTERED_view()
    {
        using var controller = NewController(SampleRows());
        controller.SetFormatRules([new DataBarRule { ColumnKey = "Amount" }]);
        controller.SetShape([], [], [],
                            FilterNode.Condition("Amount", FilterOperator.LessThanOrEqual, 300m));

        // D (500) filtered out ⇒ the range re-anchors to 100..300 (§2.7 — the stats block is the
        // footer's population: the visible set).
        Assert.Equal(1.0, controller.GetDataBarFraction(RowId(controller, "B"), "Amount"));
        Assert.Equal(0.5, controller.GetDataBarFraction(RowId(controller, "C"), "Amount"), 3);
    }

    [Fact]
    public void DataBar_on_a_non_numeric_column_is_inert()
    {
        using var controller = NewController(SampleRows());
        controller.SetFormatRules([new DataBarRule { ColumnKey = "Region" }]);
        Assert.True(double.IsNaN(controller.GetDataBarFraction(RowId(controller, "A"), "Region")));
    }

    // ── Threshold ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Threshold_entries_first_match_wins()
    {
        using var controller = NewController(SampleRows());
        var green = new CellFormat(Foreground: Color.FromRgb(0, 255, 0), Bold: true);
        var amber = new CellFormat(Foreground: Color.FromRgb(255, 191, 0));
        controller.SetFormatRules(
        [
            new ThresholdRule
            {
                ColumnKey = "Amount",
                Entries =
                [
                    (FilterOperator.GreaterThanOrEqual, 300m, green), // first match wins — 500 stops HERE
                    (FilterOperator.GreaterThanOrEqual, 150m, amber),
                ],
            },
        ]);

        Assert.Equal(green, controller.GetCellFormat(RowId(controller, "D"), "Amount"));
        Assert.Equal(amber, controller.GetCellFormat(RowId(controller, "C"), "Amount"));
        Assert.Equal(default, controller.GetCellFormat(RowId(controller, "A"), "Amount")); // below every entry
        Assert.Equal(default, controller.GetCellFormat(RowId(controller, "A"), "Region")); // untargeted column
    }

    [Fact]
    public void Threshold_literals_convert_at_compile_like_the_filter_lane()
    {
        using var controller = NewController(SampleRows());
        var red = new CellFormat(Foreground: Color.FromRgb(255, 0, 0));
        // A string literal against the decimal key — the same ConvertLiteral lane filters ride.
        controller.SetFormatRules(
        [
            new ThresholdRule { ColumnKey = "Amount", Entries = [(FilterOperator.GreaterThan, "250", red)] },
        ]);

        Assert.Equal(red, controller.GetCellFormat(RowId(controller, "B"), "Amount"));
        Assert.Equal(default, controller.GetCellFormat(RowId(controller, "C"), "Amount"));
    }

    // ── ColorScale ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ColorScale_interpolates_between_stops_over_the_stats_range()
    {
        using var controller = NewController(new Order("A", "E", 0m), new Order("B", "E", 50m), new Order("C", "E", 100m));
        controller.SetFormatRules(
        [
            new ColorScaleRule { ColumnKey = "Amount", Stops = [Color.FromRgb(0, 0, 0), Color.FromRgb(200, 100, 0)] },
        ]);

        Assert.Equal(Color.FromRgb(0, 0, 0), controller.GetCellFormat(RowId(controller, "A"), "Amount").Foreground);
        Assert.Equal(Color.FromRgb(100, 50, 0), controller.GetCellFormat(RowId(controller, "B"), "Amount").Foreground);
        Assert.Equal(Color.FromRgb(200, 100, 0), controller.GetCellFormat(RowId(controller, "C"), "Amount").Foreground);
    }

    [Fact]
    public void Threshold_foreground_beats_a_co_present_ColorScale()
    {
        using var controller = NewController(new Order("A", "E", 0m), new Order("B", "E", 100m));
        var red = new CellFormat(Foreground: Color.FromRgb(255, 0, 0));
        controller.SetFormatRules(
        [
            new ThresholdRule { ColumnKey = "Amount", Entries = [(FilterOperator.GreaterThanOrEqual, 100m, red)] },
            new ColorScaleRule { ColumnKey = "Amount", Stops = [Color.FromRgb(0, 0, 0), Color.FromRgb(0, 0, 255)] },
        ]);

        Assert.Equal(Color.FromRgb(255, 0, 0), controller.GetCellFormat(RowId(controller, "B"), "Amount").Foreground);
        Assert.Equal(Color.FromRgb(0, 0, 0), controller.GetCellFormat(RowId(controller, "A"), "Amount").Foreground);
    }

    // ── Predicate (row-level) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Predicate_rule_formats_matching_rows()
    {
        using var controller = NewController(SampleRows());
        var dim = new CellFormat(Foreground: Color.FromRgb(90, 90, 90));
        Expression<Func<Order, bool>> cancelled = o => o.Region == null;
        controller.SetFormatRules([new PredicateRule { ColumnKey = "Id", RowPredicate = cancelled, Format = dim }]);

        Assert.Equal(dim, controller.GetRowFormat(RowId(controller, "D")));
        Assert.Equal(default, controller.GetRowFormat(RowId(controller, "A")));
    }

    [Fact]
    public void CellFormat_overlay_cell_wins_colors_and_flags_or()
    {
        var row = new CellFormat(Foreground: Color.FromRgb(1, 1, 1), Bold: true);
        var cell = new CellFormat(Foreground: Color.FromRgb(2, 2, 2), Inverse: true);
        var merged = cell.OverlayOn(row);
        Assert.Equal(Color.FromRgb(2, 2, 2), merged.Foreground);
        Assert.True(merged.Bold);
        Assert.True(merged.Inverse);

        // A cell with no fg falls through to the row's.
        Assert.Equal(Color.FromRgb(1, 1, 1), default(CellFormat).OverlayOn(row).Foreground);
    }

    [Fact]
    public void Rules_recompile_when_columns_rebuild()
    {
        using var controller = NewController(SampleRows());
        controller.SetFormatRules([new DataBarRule { ColumnKey = "Amount" }]);
        Assert.Equal(1.0, controller.GetDataBarFraction(RowId(controller, "D"), "Amount"));

        // A column rebuild re-creates every ShapedColumn — compiled rule predicates must re-bind to
        // the NEW vectors, not read the orphaned ones.
        controller.SetColumns(Columns);
        Assert.True(controller.HasFormatRules);
        Assert.Equal(1.0, controller.GetDataBarFraction(RowId(controller, "D"), "Amount"));
    }

    // ── Distinct values (§3.4 — the checklist popup's population) ─────────────────────────────────

    [Fact]
    public void Distinct_values_dedupe_count_sort_and_surface_blanks_first()
    {
        using var controller = NewController(SampleRows());
        var values = controller.GetDistinctValues("Region");

        Assert.Equal(3, values.Count);
        Assert.Null(values[0].Raw);                      // "(Blanks)" first
        Assert.Equal(string.Empty, values[0].Formatted);
        Assert.Equal(1, values[0].Count);
        Assert.Equal(("East", 2), (values[1].Formatted, values[1].Count)); // sorted by the column comparison
        Assert.Equal("East", (string?)values[1].Raw);
        Assert.Equal(("West", 1), (values[2].Formatted, values[2].Count));
    }

    [Fact]
    public void Distinct_values_cap_at_maxCount()
    {
        var rows = Enumerable.Range(0, 20).Select(i => new Order($"R{i}", $"Region{i:D2}", i)).ToArray();
        using var controller = NewController(rows);
        Assert.Equal(5, controller.GetDistinctValues("Region", maxCount: 5).Count);
    }

    // ── The filter-surface validation seam ────────────────────────────────────────────────────────

    [Fact]
    public void CanCompileFilter_rejects_unconvertible_literals_and_accepts_good_ones()
    {
        using var controller = NewController(SampleRows());
        Assert.True(controller.CanCompileFilter(FilterNode.Condition("Amount", FilterOperator.GreaterThan, "250")));
        Assert.False(controller.CanCompileFilter(FilterNode.Condition("Amount", FilterOperator.GreaterThan, "abc")));
        Assert.False(controller.CanCompileFilter(FilterNode.Condition("Nope", FilterOperator.Equals, 1)));
        Assert.Equal(typeof(decimal), controller.GetColumnKeyType("Amount"));
        Assert.Equal(typeof(string), controller.GetColumnKeyType("Region"));
    }
}
