using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Cursorial.UI.DataViews.Shaping;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews.Shaping;

/// <summary>
/// <see cref="DataViewController{TRow}"/> end-to-end: attach → shape → snapshot oracles (vs LINQ
/// references), INCC/INPC live ticks through the coalesced repair path, filter/group/summary
/// integration, collapse, the randomized tick-stream equivalence oracle, and teardown.
/// </summary>
public class DataViewControllerTests
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

    private static readonly ShapingColumnDescription[] Columns =
    [
        new() { Key = "Id", FieldName = nameof(Order.Id) },
        new() { Key = "Region", FieldName = nameof(Order.Region) },
        new() { Key = "Amount", FieldName = nameof(Order.Amount), Format = "$#,##0" },
    ];

    private static DataViewController<Order> NewController(IEnumerable<Order> source)
    {
        var controller = new DataViewController<Order>();
        controller.SetColumns(Columns);
        controller.AttachSource(source is ObservableCollection<Order> oc ? oc : new List<Order>(source));
        return controller;
    }

    private static Order[] SampleRows() =>
    [
        new("A", "East", 300m),
        new("B", "West", 100m),
        new("C", "East", 200m),
        new("D", "South", 400m),
    ];

    private static string[] DataRowIds(DataViewController<Order> controller)
    {
        var snapshot = controller.Snapshot;
        var ids = new List<string>();
        for (int i = 0; i < snapshot.Count; i++)
        {
            var row = snapshot.GetRow(i);
            if (!row.IsGroup)
                ids.Add(controller.RowAccessor(row.RowId).Id);
        }
        return ids.ToArray();
    }

    [Fact]
    public void Sort_orders_the_snapshot()
    {
        using var controller = NewController(SampleRows());
        controller.SetShape([SortDescription.Ascending("Amount")], [], [], null);
        Assert.Equal(new[] { "B", "C", "A", "D" }, DataRowIds(controller));

        controller.SetShape([SortDescription.Descending("Amount")], [], [], null);
        Assert.Equal(new[] { "D", "A", "C", "B" }, DataRowIds(controller));
    }

    [Fact]
    public void Unsorted_snapshot_keeps_insertion_order()
    {
        using var controller = NewController(SampleRows());
        controller.SetShape([], [], [], null);
        Assert.Equal(new[] { "A", "B", "C", "D" }, DataRowIds(controller)); // the sequence tiebreak
    }

    [Fact]
    public void Filter_and_sort_compose()
    {
        using var controller = NewController(SampleRows());
        controller.SetShape([SortDescription.Ascending("Amount")], [], [],
                            FilterNode.Condition("Region", FilterOperator.Equals, "East"));
        Assert.Equal(new[] { "C", "A" }, DataRowIds(controller));
        Assert.Equal(2, controller.Snapshot.DataRowCount);
    }

    [Fact]
    public void Grouping_produces_group_rows_and_summaries()
    {
        using var controller = NewController(SampleRows());
        controller.SetShape(
            [SortDescription.Ascending("Amount")],
            [new GroupDescription("Region")],
            [new SummaryDescription("Amount", AggregateKind.Sum, "$#,##0", "Σ {0}")],
            null);

        var snapshot = controller.Snapshot;
        // Groups ascend: East, South, West. East(2 rows), South(1), West(1) → 3 + 4 = 7 view rows.
        Assert.Equal(7, snapshot.Count);
        var firstGroup = snapshot.GetRow(0);
        Assert.True(firstGroup.IsGroup);
        var east = snapshot.Groups[firstGroup.GroupNodeIndex];
        Assert.Equal("East", east.FormattedKey);
        Assert.Equal(2, east.RowCount);
        Assert.Equal("Σ $500", east.Summaries[0]);

        Assert.Equal(new[] { "Σ $1,000" }, controller.Totals);
    }

    [Fact]
    public void Collapse_hides_rows_and_survives_reshape()
    {
        using var controller = NewController(SampleRows());
        controller.SetShape([], [new GroupDescription("Region")], [], null);

        controller.SetCollapsed("East", true);
        // Groups ascend (East, South, West); East's rows hidden ⇒ D (South) then B (West).
        Assert.Equal(new[] { "D", "B" }, DataRowIds(controller));

        // A reshape (new sort) keeps the collapse.
        controller.SetShape([SortDescription.Descending("Amount")], [new GroupDescription("Region")], [], null);
        Assert.Equal(new[] { "D", "B" }, DataRowIds(controller));

        controller.SetCollapsed("East", false);
        Assert.Contains("A", DataRowIds(controller));
    }

    [Fact]
    public void Incc_add_remove_replace_tick_through_repair()
    {
        var source = new ObservableCollection<Order>(SampleRows());
        using var controller = NewController(source);
        controller.SetShape([SortDescription.Ascending("Amount")], [], [], null);

        source.Add(new Order("E", "East", 250m));
        controller.Flush();
        Assert.Equal(new[] { "B", "C", "E", "A", "D" }, DataRowIds(controller));

        source.RemoveAt(0); // removes A (300)
        controller.Flush();
        Assert.Equal(new[] { "B", "C", "E", "D" }, DataRowIds(controller));

        source[0] = new Order("F", "West", 50m); // replaces B
        controller.Flush();
        Assert.Equal(new[] { "F", "C", "E", "D" }, DataRowIds(controller));
    }

    [Fact]
    public void Inpc_value_change_repositions_the_row()
    {
        var source = new ObservableCollection<Order>(SampleRows());
        using var controller = NewController(source);
        controller.SetShape([SortDescription.Ascending("Amount")], [], [], null);

        int publishes = 0;
        controller.SnapshotChanged += (_, _) => publishes++;

        source[1].Amount = 999m; // B: 100 → 999, moves last
        controller.Flush();
        Assert.Equal(new[] { "C", "A", "D", "B" }, DataRowIds(controller));
        Assert.Equal(1, publishes);

        // A tick on an UNshaped property does not publish.
        // (Id is shaped; Region is shaped; so mutate via a no-op value change instead.)
        source[1].Amount = 999m; // same value — still a tick (no value-diff suppression in v1)
        controller.Flush();
        Assert.Equal(2, publishes);
    }

    [Fact]
    public void Inpc_tick_respects_filter_membership()
    {
        var source = new ObservableCollection<Order>(SampleRows());
        using var controller = NewController(source);
        controller.SetShape([SortDescription.Ascending("Amount")], [], [],
                            FilterNode.Condition("Amount", FilterOperator.GreaterThan, 150m));
        Assert.Equal(new[] { "C", "A", "D" }, DataRowIds(controller));

        source[2].Amount = 120m;  // C drops below the filter
        controller.Flush();
        Assert.Equal(new[] { "A", "D" }, DataRowIds(controller));

        source[1].Amount = 500m;  // B rises into the filter
        controller.Flush();
        Assert.Equal(new[] { "A", "D", "B" }, DataRowIds(controller));
    }

    [Fact]
    public void Randomized_tick_stream_matches_linq_oracle()
    {
        var rng = new Random(99);
        var source = new ObservableCollection<Order>();
        for (int i = 0; i < 200; i++)
            source.Add(new Order($"R{i}", $"G{rng.Next(5)}", rng.Next(1000)));

        using var controller = NewController(source);
        controller.SetShape([SortDescription.Ascending("Amount"), SortDescription.Descending("Id")], [], [],
                            FilterNode.Condition("Amount", FilterOperator.LessThan, 800m));

        for (int round = 0; round < 60; round++)
        {
            switch (rng.Next(4))
            {
                case 0:
                    source.Add(new Order($"N{round}", $"G{rng.Next(5)}", rng.Next(1000)));
                    break;
                case 1 when source.Count > 10:
                    source.RemoveAt(rng.Next(source.Count));
                    break;
                default:
                    source[rng.Next(source.Count)].Amount = rng.Next(1000);
                    break;
            }

            if (rng.Next(3) == 0)
            {
                controller.Flush();

                var expected = source.Where(o => o.Amount < 800m)
                                     .OrderBy(o => o.Amount)
                                     .ThenByDescending(o => o.Id, StringComparer.CurrentCulture)
                                     .Select(o => o.Id)
                                     .ToArray();
                Assert.Equal(expected, DataRowIds(controller));
            }
        }
    }

    [Fact]
    public void Dispose_unsubscribes_inpc_and_incc()
    {
        var source = new ObservableCollection<Order>(SampleRows());
        var controller = NewController(source);
        controller.SetShape([SortDescription.Ascending("Amount")], [], [], null);
        controller.Dispose();

        // Post-dispose mutations must not throw or resurrect shaping.
        source.Add(new Order("Z", "East", 1m));
        source[0].Amount = 5m;
        Assert.Throws<ObjectDisposedException>(() => controller.Flush());
        controller.Dispose(); // idempotent
    }

    [Fact]
    public void Untyped_facade_closes_over_the_row_type()
    {
        using var controller = DataViewController.Create(typeof(Order));
        controller.SetColumns(Columns);
        controller.AttachSource(new List<Order>(SampleRows()));
        controller.SetShape([SortDescription.Ascending("Amount")], [], [], null);
        Assert.Equal(4, controller.Snapshot.Count);
        Assert.Equal("$100", controller.FormatCell(controller.Snapshot.GetRow(0).RowId, "Amount"));
    }
}
