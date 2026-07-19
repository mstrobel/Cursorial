using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Cursorial.UI.DataViews.Shaping;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews.Shaping;

/// <summary>
/// The §9.5 "order groups by summary" projection: sibling groups order by a per-group aggregate
/// (Sum/Average/Count, asc + desc) while rows WITHIN a group keep key order and the key-ordered
/// sorted view remains the repair substrate (the pinned two-array discipline). Nested levels order
/// their own siblings and a parent's reorder carries its whole subtree; ties keep key order; the
/// summary direction is independent of the level's key direction; live delta publishes re-project.
/// </summary>
public class GroupSummarySortTests
{
    private sealed class Order(string id, string region, string status, decimal amount) : INotifyPropertyChanged
    {
        private decimal _amount = amount;

        public string Id { get; } = id;
        public string Region { get; } = region;
        public string Status { get; } = status;
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
        new() { Key = "Status", FieldName = nameof(Order.Status) },
        new() { Key = "Amount", FieldName = nameof(Order.Amount) },
    ];

    private static DataViewController<Order> NewController(IEnumerable<Order> source)
    {
        var controller = new DataViewController<Order>();
        controller.SetColumns(Columns);
        controller.AttachSource(source is ObservableCollection<Order> oc ? oc : new List<Order>(source));
        return controller;
    }

    // Region sums: East = 300, South = 900, West = 150.
    private static Order[] SampleRows() =>
    [
        new("A", "East",  "Open",   100m),
        new("B", "East",  "Closed", 200m),
        new("C", "South", "Open",   900m),
        new("D", "West",  "Open",    50m),
        new("E", "West",  "Closed", 100m),
    ];

    /// <summary>The view rows as tokens: <c>"[key]"</c> for group headers, the row id for data rows.</summary>
    private static string[] ViewTokens(DataViewController<Order> controller)
    {
        var snapshot = controller.Snapshot;
        var tokens = new List<string>();
        for (int i = 0; i < snapshot.Count; i++)
        {
            var row = snapshot.GetRow(i);
            tokens.Add(row.IsGroup
                ? "[" + snapshot.Groups[row.GroupNodeIndex].FormattedKey + "]"
                : controller.RowAccessor(row.RowId).Id);
        }
        return tokens.ToArray();
    }

    private static string[] GroupKeysInDisplayOrder(DataViewController<Order> controller, int level = 0)
    {
        var snapshot = controller.Snapshot;
        var keys = new List<string>();
        for (int i = 0; i < snapshot.Count; i++)
        {
            var row = snapshot.GetRow(i);
            if (row.IsGroup && snapshot.Groups[row.GroupNodeIndex].Level == level)
                keys.Add(snapshot.Groups[row.GroupNodeIndex].FormattedKey);
        }
        return keys.ToArray();
    }

    [Fact]
    public void Sum_orders_siblings_ascending_and_descending()
    {
        using var controller = NewController(SampleRows());

        controller.SetShape([], [new GroupDescription("Region")
        {
            OrderBySummary = new SummaryDescription("Amount", AggregateKind.Sum),
        }], [], null);
        // Sums: West 150 < East 300 < South 900.
        Assert.Equal(new[] { "West", "East", "South" }, GroupKeysInDisplayOrder(controller));

        controller.SetShape([], [new GroupDescription("Region")
        {
            OrderBySummary = new SummaryDescription("Amount", AggregateKind.Sum),
            SummaryDirection = SortDirection.Descending,
        }], [], null);
        Assert.Equal(new[] { "South", "East", "West" }, GroupKeysInDisplayOrder(controller));
    }

    [Fact]
    public void Average_and_count_order_siblings()
    {
        using var controller = NewController(SampleRows());

        // Averages: West 75 < East 150 < South 900.
        controller.SetShape([], [new GroupDescription("Region")
        {
            OrderBySummary = new SummaryDescription("Amount", AggregateKind.Average),
        }], [], null);
        Assert.Equal(new[] { "West", "East", "South" }, GroupKeysInDisplayOrder(controller));

        // Counts: South 1 < East 2 = West 2 (tie → key order: East before West).
        controller.SetShape([], [new GroupDescription("Region")
        {
            OrderBySummary = new SummaryDescription("Amount", AggregateKind.Count),
        }], [], null);
        Assert.Equal(new[] { "South", "East", "West" }, GroupKeysInDisplayOrder(controller));
    }

    [Fact]
    public void Ties_keep_key_order_regardless_of_summary_direction()
    {
        // East and West both sum to 100; South is distinct.
        using var controller = NewController(
        [
            new Order("A", "West", "Open", 100m),
            new Order("B", "East", "Open", 100m),
            new Order("C", "South", "Open", 500m),
        ]);

        controller.SetShape([], [new GroupDescription("Region")
        {
            OrderBySummary = new SummaryDescription("Amount", AggregateKind.Sum),
        }], [], null);
        // Tie (East/West at 100) keeps ascending KEY order, then South (500).
        Assert.Equal(new[] { "East", "West", "South" }, GroupKeysInDisplayOrder(controller));

        controller.SetShape([], [new GroupDescription("Region")
        {
            OrderBySummary = new SummaryDescription("Amount", AggregateKind.Sum),
            SummaryDirection = SortDirection.Descending,
        }], [], null);
        // Descending flips the SUMMARY order only — the tie still resolves by ascending key.
        Assert.Equal(new[] { "South", "East", "West" }, GroupKeysInDisplayOrder(controller));
    }

    [Fact]
    public void Rows_within_groups_keep_key_order_and_subtrees_carry_whole()
    {
        using var controller = NewController(SampleRows());
        controller.SetShape(
            [SortDescription.Ascending("Amount")],
            [new GroupDescription("Region")
            {
                OrderBySummary = new SummaryDescription("Amount", AggregateKind.Sum),
                SummaryDirection = SortDirection.Descending,
            }],
            [], null);

        // Regions by sum desc: South(900), East(300), West(150); rows inside each group keep the
        // Amount-ascending key order; every group's block is contiguous (the subtree carries whole).
        Assert.Equal(
            new[] { "[South]", "C", "[East]", "A", "B", "[West]", "D", "E" },
            ViewTokens(controller));
    }

    [Fact]
    public void Nested_levels_order_their_own_siblings_and_parents_carry_subtrees()
    {
        // Region sums: East 300, West 150 → desc: East first.
        // Status sums within East: Open 100 < Closed 200 → asc: Open first.
        // Status sums within West: Open 50 < Closed 100 → asc: Open first.
        using var controller = NewController(SampleRows().Where(o => o.Region != "South").ToArray());
        controller.SetShape(
            [],
            [
                new GroupDescription("Region")
                {
                    OrderBySummary = new SummaryDescription("Amount", AggregateKind.Sum),
                    SummaryDirection = SortDirection.Descending,
                },
                new GroupDescription("Status")
                {
                    OrderBySummary = new SummaryDescription("Amount", AggregateKind.Sum),
                },
            ],
            [], null);

        // Key order would put Closed before Open (alphabetical); the level-1 projection reorders
        // by sum asc within EACH parent, and the level-0 desc projection carries whole subtrees.
        Assert.Equal(
            new[] { "[East]", "[Open]", "A", "[Closed]", "B", "[West]", "[Open]", "D", "[Closed]", "E" },
            ViewTokens(controller));
    }

    [Fact]
    public void Nested_projection_on_inner_level_only_keeps_outer_key_order()
    {
        using var controller = NewController(SampleRows().Where(o => o.Region != "South").ToArray());
        controller.SetShape(
            [],
            [
                new GroupDescription("Region"), // key order: East, West
                new GroupDescription("Status")
                {
                    OrderBySummary = new SummaryDescription("Amount", AggregateKind.Sum),
                    SummaryDirection = SortDirection.Descending,
                },
            ],
            [], null);

        Assert.Equal(
            new[] { "[East]", "[Closed]", "B", "[Open]", "A", "[West]", "[Closed]", "E", "[Open]", "D" },
            ViewTokens(controller));
    }

    [Fact]
    public void Group_direction_is_independent_of_data_sorts()
    {
        // The pinned §9.5 structural row: a DESCENDING group level with an ASCENDING data sort.
        using var controller = NewController(SampleRows());
        controller.SetShape(
            [SortDescription.Ascending("Amount")],
            [new GroupDescription("Region", SortDirection.Descending)],
            [], null);

        Assert.Equal(new[] { "West", "South", "East" }, GroupKeysInDisplayOrder(controller));
        // Rows inside each group still ascend by Amount.
        Assert.Equal(
            new[] { "[West]", "D", "E", "[South]", "C", "[East]", "A", "B" },
            ViewTokens(controller));
    }

    [Fact]
    public void Summary_direction_differs_from_key_direction()
    {
        // Key direction DESCENDING derives boundaries (and the tie order); the summary projection
        // orders display ASCENDING by sum — the §9.5 "direction can differ" row.
        using var controller = NewController(SampleRows());
        controller.SetShape(
            [],
            [new GroupDescription("Region", SortDirection.Descending)
            {
                OrderBySummary = new SummaryDescription("Amount", AggregateKind.Sum),
                SummaryDirection = SortDirection.Ascending,
            }],
            [], null);

        // Sums asc: West 150, East 300, South 900 — NOT the descending key order (West, South, East).
        Assert.Equal(new[] { "West", "East", "South" }, GroupKeysInDisplayOrder(controller));

        // The SORTED view (the repair substrate) still carries the key-descending order — the
        // projection never fed back into it (the pinned two-array discipline).
        var snapshot = controller.Snapshot;
        var substrateRegions = new List<string>();
        for (int i = 0; i < snapshot.DataRowCount; i++)
        {
            string region = controller.RowAccessor(snapshot.SortedView[i]).Region;
            if (substrateRegions.Count == 0 || substrateRegions[^1] != region)
                substrateRegions.Add(region);
        }
        Assert.Equal(new[] { "West", "South", "East" }, substrateRegions);
    }

    [Fact]
    public void Live_delta_reorders_groups_without_disturbing_row_order()
    {
        var rows = new ObservableCollection<Order>(SampleRows());
        using var controller = NewController(rows);
        controller.SetShape(
            [SortDescription.Ascending("Amount")],
            [new GroupDescription("Region")
            {
                OrderBySummary = new SummaryDescription("Amount", AggregateKind.Sum),
            }],
            [], null);

        Assert.Equal(new[] { "West", "East", "South" }, GroupKeysInDisplayOrder(controller));
        int versionBefore = controller.Snapshot.Version;

        // Boost West's sum (150 → 1150) through the INPC live lane: West must move last; the rows
        // inside every group keep their Amount-ascending order (E jumped above D inside West).
        rows.Single(o => o.Id == "D").Amount = 1050m;
        controller.Flush();

        Assert.True(controller.Snapshot.Version > versionBefore);
        Assert.Equal(new[] { "East", "South", "West" }, GroupKeysInDisplayOrder(controller));
        Assert.Equal(
            new[] { "[East]", "A", "B", "[South]", "C", "[West]", "E", "D" },
            ViewTokens(controller));
    }

    [Fact]
    public void Collapse_state_survives_a_summary_reorder()
    {
        var rows = new ObservableCollection<Order>(SampleRows());
        using var controller = NewController(rows);
        controller.SetShape(
            [],
            [new GroupDescription("Region")
            {
                OrderBySummary = new SummaryDescription("Amount", AggregateKind.Sum),
            }],
            [], null);

        controller.SetCollapsed("West", true);
        Assert.DoesNotContain("D", ViewTokens(controller));

        // Reorder the siblings via a live delta — the PathKey is order-independent, so West stays
        // collapsed in its new display position.
        rows.Single(o => o.Id == "D").Amount = 1050m;
        controller.Flush();

        Assert.Equal(new[] { "East", "South", "West" }, GroupKeysInDisplayOrder(controller));
        Assert.DoesNotContain("D", ViewTokens(controller));
        Assert.DoesNotContain("E", ViewTokens(controller));
    }

    [Fact]
    public void Displayed_summaries_align_with_reordered_groups()
    {
        using var controller = NewController(SampleRows());
        controller.SetShape(
            [],
            [new GroupDescription("Region")
            {
                OrderBySummary = new SummaryDescription("Amount", AggregateKind.Sum),
                SummaryDirection = SortDirection.Descending,
            }],
            [new SummaryDescription("Amount", AggregateKind.Sum)],
            null);

        var snapshot = controller.Snapshot;
        var summariesInDisplayOrder = new List<(string Key, string Sum)>();
        for (int i = 0; i < snapshot.Count; i++)
        {
            var row = snapshot.GetRow(i);
            if (row.IsGroup)
            {
                var node = snapshot.Groups[row.GroupNodeIndex];
                summariesInDisplayOrder.Add((node.FormattedKey, node.Summaries[0]));
            }
        }

        Assert.Equal(
        [
            ("South", "900"),
            ("East", "300"),
            ("West", "150"),
        ], summariesInDisplayOrder);
    }

    [Fact]
    public void Order_by_summary_on_a_column_dropped_by_a_column_rebuild_falls_back_to_key_order()
    {
        using var controller = NewController(SampleRows());
        controller.SetShape([], [new GroupDescription("Region")
        {
            OrderBySummary = new SummaryDescription("Amount", AggregateKind.Sum),
            SummaryDirection = SortDirection.Descending,
        }], [], null);
        Assert.Equal(new[] { "South", "East", "West" }, GroupKeysInDisplayOrder(controller));

        // Re-push columns WITHOUT Amount: the group survives (Region still resolves) but the
        // order-by summary is stripped — display falls back to key order instead of throwing.
        controller.SetColumns(
        [
            new ShapingColumnDescription { Key = "Id", FieldName = nameof(Order.Id) },
            new ShapingColumnDescription { Key = "Region", FieldName = nameof(Order.Region) },
        ]);
        Assert.Equal(new[] { "East", "South", "West" }, GroupKeysInDisplayOrder(controller));
    }
}
