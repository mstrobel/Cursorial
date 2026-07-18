using System.Collections.ObjectModel;

using Cursorial.UI.DataViews.Shaping;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews.Shaping;

/// <summary>
/// The §9.6 struct-row contract: <see cref="DataViewController{TRow}"/> over a value-type row —
/// attach/sort/filter/group end-to-end, the pinned <c>liveUpdates: true</c> guard (no INPC identity
/// to observe), row id as the ONLY identity, and the edit write-back lane through
/// <c>RowStore.SetRow</c> (mutating a boxed copy is the silent-no-op trap the lane exists to avoid).
/// </summary>
public class StructRowTests
{
    private struct Trade
    {
        public string Symbol;
        public string Venue;
        public decimal Price;
        public int Quantity;

        public Trade(string symbol, string venue, decimal price, int quantity)
        {
            Symbol = symbol;
            Venue = venue;
            Price = price;
            Quantity = quantity;
        }
    }

    private static readonly ShapingColumnDescription[] Columns =
    [
        new() { Key = "Symbol", FieldName = nameof(Trade.Symbol) },
        new() { Key = "Venue", FieldName = nameof(Trade.Venue) },
        new() { Key = "Price", FieldName = nameof(Trade.Price) },
        new() { Key = "Quantity", FieldName = nameof(Trade.Quantity) },
    ];

    private static Trade[] SampleRows() =>
    [
        new("AAPL", "NYSE", 210m, 100),
        new("MSFT", "NASDAQ", 420m, 50),
        new("AAPL", "NASDAQ", 205m, 200),
        new("GOOG", "NYSE", 180m, 75),
    ];

    private static DataViewController<Trade> NewController(IEnumerable<Trade>? source = null)
    {
        var controller = new DataViewController<Trade>();
        controller.SetColumns(Columns);
        controller.AttachSource(new List<Trade>(source ?? SampleRows()), liveUpdates: false);
        return controller;
    }

    private static string[] DataRowSymbols(DataViewController<Trade> controller)
    {
        var snapshot = controller.Snapshot;
        var ids = new List<string>();
        for (int i = 0; i < snapshot.Count; i++)
        {
            var row = snapshot.GetRow(i);
            if (!row.IsGroup)
                ids.Add(controller.RowAccessor(row.RowId).Symbol);
        }
        return ids.ToArray();
    }

    [Fact]
    public void Live_updates_throw_for_value_type_rows()
    {
        using var controller = new DataViewController<Trade>();
        controller.SetColumns(Columns);

        var e = Assert.Throws<InvalidOperationException>(
            () => controller.AttachSource(new List<Trade>(SampleRows()), liveUpdates: true));
        Assert.Contains("liveUpdates: false", e.Message);

        // The DEFAULT is liveUpdates: true — it must throw too (the guard is the pinned contract).
        Assert.Throws<InvalidOperationException>(
            () => controller.AttachSource(new List<Trade>(SampleRows())));

        // Detaching (null source) never throws regardless of the flag.
        controller.AttachSource(null);
    }

    [Fact]
    public void Untyped_create_closes_over_a_value_type()
    {
        using var controller = DataViewController.Create(typeof(Trade));
        Assert.IsType<DataViewController<Trade>>(controller);
    }

    [Fact]
    public void Attach_sort_filter_group_end_to_end()
    {
        using var controller = NewController();
        controller.SetShape(
            [SortDescription.Ascending("Price")],
            [new GroupDescription("Venue")],
            [new SummaryDescription("Quantity", AggregateKind.Sum)],
            FilterNode.Condition("Price", FilterOperator.GreaterThan, 190m));

        // Filter: Price > 190 keeps AAPL(210), MSFT(420), AAPL(205). Groups (Venue asc):
        // NASDAQ { AAPL 205, MSFT 420 }, NYSE { AAPL 210 } — rows ascend by price within.
        var snapshot = controller.Snapshot;
        Assert.Equal(3, snapshot.DataRowCount);
        Assert.Equal(new[] { "AAPL", "MSFT", "AAPL" }, DataRowSymbols(controller));

        var firstGroup = snapshot.GetRow(0);
        Assert.True(firstGroup.IsGroup);
        Assert.Equal("NASDAQ", snapshot.Groups[firstGroup.GroupNodeIndex].FormattedKey);
        Assert.Equal("250", snapshot.Groups[firstGroup.GroupNodeIndex].Summaries[0]); // 200 + 50
    }

    [Fact]
    public void Edit_commit_writes_back_through_the_store_and_repairs()
    {
        using var controller = NewController();
        controller.SetShape([SortDescription.Ascending("Price")], [], [], null);
        Assert.Equal(new[] { "GOOG", "AAPL", "AAPL", "MSFT" }, DataRowSymbols(controller));

        // Find GOOG's row id (180) and raise its price above everything else.
        int googId = controller.Snapshot.GetRow(0).RowId;
        Assert.Equal("GOOG", controller.RowAccessor(googId).Symbol);

        Assert.True(controller.TrySetCellFromText(googId, "Price", "999.50"));
        controller.Flush();

        // The stored struct changed (rowId is the identity; the accessor reads the CURRENT value)…
        Assert.Equal(999.50m, controller.RowAccessor(googId).Price);
        Assert.Equal(999.50m, ((Trade)controller.GetRowObject(googId)).Price);
        Assert.Equal("999.50", controller.FormatCell(googId, "Price"));

        // …and the repair moved the row to its new sorted position.
        Assert.Equal(new[] { "AAPL", "AAPL", "MSFT", "GOOG" }, DataRowSymbols(controller));
    }

    [Fact]
    public void Edit_commit_visible_in_the_next_publish_snapshot()
    {
        using var controller = NewController();
        controller.SetShape([SortDescription.Ascending("Price")], [], [], null);
        int versionBefore = controller.Snapshot.Version;

        int firstId = controller.Snapshot.GetRow(0).RowId;
        Assert.True(controller.TrySetCellFromText(firstId, "Quantity", "7777"));
        controller.Flush();

        Assert.True(controller.Snapshot.Version > versionBefore);
        Assert.Equal(7777, controller.RowAccessor(firstId).Quantity);
        Assert.Equal("7777", controller.FormatCell(firstId, "Quantity"));
    }

    [Fact]
    public void Unparseable_or_readonly_edits_leave_the_row_untouched()
    {
        using var controller = NewController();
        controller.SetShape([], [], [], null);
        int id = controller.Snapshot.GetRow(0).RowId;
        var before = controller.RowAccessor(id);

        Assert.False(controller.TrySetCellFromText(id, "Price", "not-a-number"));
        Assert.False(controller.TrySetCellFromText(id, "Nope", "1"));
        Assert.Equal(before.Price, controller.RowAccessor(id).Price);
    }

    [Fact]
    public void Columns_report_editable_for_struct_rows()
    {
        using var controller = NewController();
        Assert.True(controller.IsColumnEditable("Price"));
        Assert.True(controller.IsColumnEditable("Symbol"));
        Assert.False(controller.IsColumnEditable("Missing"));
    }

    [Fact]
    public void New_row_lane_mutates_the_caller_box()
    {
        using var controller = NewController();

        // The grid's new-row template edits a BOXED un-stored struct before source.Add — the write
        // must land in the caller's box (unboxing a copy would silently no-op, §9.6).
        object box = new Trade("NEW", "NYSE", 0m, 0);
        Assert.True(controller.TrySetRowText(box, "Price", "123.45"));
        Assert.True(controller.TrySetRowText(box, "Symbol", "NVDA"));
        var edited = (Trade)box;
        Assert.Equal(123.45m, edited.Price);
        Assert.Equal("NVDA", edited.Symbol);
    }

    [Fact]
    public void Incc_collection_changes_apply_without_live_updates()
    {
        var rows = new ObservableCollection<Trade>(SampleRows());
        using var controller = new DataViewController<Trade>();
        controller.SetColumns(Columns);
        controller.AttachSource(rows, liveUpdates: false);
        controller.SetShape([SortDescription.Ascending("Price")], [], [], null);

        // INCC events are index-based — no row identity needed, so they work for structs.
        rows.Add(new Trade("TSLA", "NASDAQ", 500m, 10));
        controller.Flush();
        Assert.Equal(new[] { "GOOG", "AAPL", "AAPL", "MSFT", "TSLA" }, DataRowSymbols(controller));

        rows.RemoveAt(0); // AAPL @ 210
        controller.Flush();
        Assert.Equal(new[] { "GOOG", "AAPL", "MSFT", "TSLA" }, DataRowSymbols(controller));
    }

    [Fact]
    public void Group_summary_projection_works_over_struct_rows()
    {
        using var controller = NewController();
        controller.SetShape(
            [],
            [new GroupDescription("Venue")
            {
                OrderBySummary = new SummaryDescription("Quantity", AggregateKind.Sum),
                SummaryDirection = SortDirection.Descending,
            }],
            [], null);

        // Quantity sums: NASDAQ 250, NYSE 175 → desc: NASDAQ first (key order says NASDAQ anyway;
        // flip the data to make the projection observable).
        var snapshot = controller.Snapshot;
        Assert.Equal("NASDAQ", snapshot.Groups[snapshot.GetRow(0).GroupNodeIndex].FormattedKey);

        controller.SetShape(
            [],
            [new GroupDescription("Venue")
            {
                OrderBySummary = new SummaryDescription("Quantity", AggregateKind.Sum),
            }],
            [], null);
        snapshot = controller.Snapshot;
        Assert.Equal("NYSE", snapshot.Groups[snapshot.GetRow(0).GroupNodeIndex].FormattedKey); // 175 < 250
    }

    [Fact]
    public void Row_store_set_row_replaces_the_stored_struct()
    {
        var store = new RowStore<Trade>();
        int slot = store.Insert(0, new Trade("AAPL", "NYSE", 210m, 100));

        store.SetRow(slot, new Trade("AAPL", "NYSE", 999m, 100));
        Assert.Equal(999m, store.GetRow(slot).Price);

        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetRow(5, new Trade()));
    }

    [Fact]
    public void Distinct_values_and_filters_work_over_struct_rows()
    {
        using var controller = NewController();
        var distinct = controller.GetDistinctValues("Symbol");
        Assert.Equal(new[] { "AAPL", "GOOG", "MSFT" }, distinct.Select(d => d.Formatted).ToArray());
        Assert.Equal(2, distinct.Single(d => d.Formatted == "AAPL").Count);

        Assert.True(controller.CanCompileFilter(FilterNode.Condition("Quantity", FilterOperator.LessThan, 100)));
        controller.SetShape([], [], [], FilterNode.Condition("Quantity", FilterOperator.LessThan, 100));
        Assert.Equal(new[] { "MSFT", "GOOG" }, DataRowSymbols(controller)); // insertion order (sequence tiebreak)
    }
}
