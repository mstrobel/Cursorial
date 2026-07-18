using System.Linq.Expressions;

using Cursorial.UI.DataViews.Shaping;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews.Shaping;

/// <summary>
/// <see cref="ShapingGroups"/> contracts: group runs derive from the sorted view via compiled
/// comparisons (one format per NEW group), counts/parents/paths, collapse suppression across
/// levels, the ~nodeIndex flat encoding, and the ungrouped alias sentinel.
/// </summary>
public class ShapingGroupsTests
{
    private sealed record Order(string Region, string Status, decimal Amount);

    private static readonly Order[] Rows =
    [
        new("East",  "Shipped",    12450m),  // 0
        new("East",  "Shipped",    22750m),  // 1
        new("East",  "Processing", 31900m),  // 2
        new("South", "Shipped",    19800m),  // 3
        new("South", "Processing", 42100m),  // 4
        new("West",  "Processing", 27300m),  // 5
    ];

    private static ShapedColumn Column<TKey>(Expression<Func<Order, TKey>> selector)
    {
        var column = ShapingCodegen.CreateColumn<Order>(selector.ToString(), selector);
        column.EnsureCapacity(Rows.Length);
        for (int i = 0; i < Rows.Length; i++)
            column.ExtractKeyUntyped(Rows[i], i);
        return column;
    }

    private static readonly ShapedColumn Region = Column(o => o.Region);
    private static readonly ShapedColumn Status = Column(o => o.Status);

    // The rows are authored pre-sorted by (Region, Status desc-ish)? No — sort them for real:
    private static int[] SortedView(params ShapedColumn[] levels)
    {
        var view = Enumerable.Range(0, Rows.Length).ToArray();
        var comparison = ShapingCodegen.BuildSlotComparison(levels.Select(c => (c, false)).ToArray());
        ShapingSort.Sort(view, view.Length, comparison, new SortScratch());
        return view;
    }

    private static (List<GroupNode> Nodes, int[] Flat) Run(int[] sorted, IReadOnlySet<string> collapsed, params ShapedColumn[] levels)
    {
        var buffers = new ShapingGroups.Buffers();
        ShapingGroups.DeriveAndFlatten(sorted, sorted.Length, levels, collapsed, buffers);
        Assert.True(buffers.FlatLength >= 0, "grouped runs must not return the alias sentinel");
        return (buffers.Nodes, buffers.Flat.Take(buffers.FlatLength).ToArray());
    }

    [Fact]
    public void Ungrouped_returns_the_alias_sentinel()
    {
        var buffers = new ShapingGroups.Buffers();
        ShapingGroups.DeriveAndFlatten([2, 0, 1], 3, [], new HashSet<string>(), buffers);
        Assert.Equal(-1, buffers.FlatLength);
        Assert.Empty(buffers.Nodes);
    }

    [Fact]
    public void Two_level_grouping_derives_runs_counts_and_paths()
    {
        var sorted = SortedView(Region, Status);
        var (nodes, flat) = Run(sorted, new HashSet<string>(), Region, Status);

        // Regions: East(3), South(2), West(1); Statuses within: East{Processing(1),Shipped(2)},
        // South{Processing(1),Shipped(1)}, West{Processing(1)} — status sorts ascending.
        var regions = nodes.Where(n => n.Level == 0).Select(n => (n.FormattedKey, n.RowCount)).ToArray();
        Assert.Equal([("East", 3), ("South", 2), ("West", 1)], regions);

        var eastChildren = nodes.Where(n => n.Level == 1 && nodes[n.Parent].FormattedKey == "East")
                                .Select(n => (n.FormattedKey, n.RowCount)).ToArray();
        Assert.Equal([("Processing", 1), ("Shipped", 2)], eastChildren);

        // Paths chain with ¦.
        Assert.Contains(nodes, n => n.PathKey == "East¦Shipped");
        Assert.Contains(nodes, n => n.PathKey == "West¦Processing");

        // Flat: every group row precedes its rows; group entries are ~nodeIndex.
        Assert.Equal(6 + nodes.Count, flat.Length);
        Assert.True(flat[0] < 0);                       // Region: East
        Assert.Equal("East", nodes[~flat[0]].FormattedKey);
        Assert.True(flat[1] < 0);                       // Status: Processing (East's first child)
        Assert.Equal("Processing", nodes[~flat[1]].FormattedKey);
        Assert.True(flat[2] >= 0);                      // the East/Processing data row
        Assert.Equal(2, flat[2]);                       // row 2 is East+Processing
    }

    [Fact]
    public void Collapsed_outer_group_suppresses_inner_groups_and_rows()
    {
        var sorted = SortedView(Region, Status);
        var (nodes, flat) = Run(sorted, new HashSet<string> { "East" }, Region, Status);

        // East's group row is present; its child groups and rows are not.
        int eastNode = nodes.FindIndex(n => n.PathKey == "East");
        Assert.Contains(~eastNode, flat);
        Assert.True(nodes[eastNode].IsCollapsed);
        Assert.DoesNotContain(flat, e => e >= 0 && Rows[e].Region == "East");
        Assert.DoesNotContain(flat, e => e < 0 && nodes[~e].PathKey.StartsWith("East¦", StringComparison.Ordinal));

        // South/West unaffected.
        Assert.Contains(flat, e => e >= 0 && Rows[e].Region == "South");
    }

    [Fact]
    public void Collapsed_inner_group_suppresses_only_its_rows()
    {
        var sorted = SortedView(Region, Status);
        var (nodes, flat) = Run(sorted, new HashSet<string> { "East¦Shipped" }, Region, Status);

        // The East¦Shipped group row remains; its 2 data rows are hidden; East¦Processing's row shows.
        Assert.Contains(flat, e => e < 0 && nodes[~e].PathKey == "East¦Shipped");
        Assert.DoesNotContain(flat, e => e >= 0 && Rows[e] is { Region: "East", Status: "Shipped" });
        Assert.Contains(flat, e => e >= 0 && Rows[e] is { Region: "East", Status: "Processing" });
    }

    [Fact]
    public void Snapshot_reads_group_and_data_rows()
    {
        var sorted = SortedView(Region);
        var buffers = new ShapingGroups.Buffers();
        ShapingGroups.DeriveAndFlatten(sorted, sorted.Length, [Region], new HashSet<string>(), buffers);

        var snapshot = new DataViewSnapshot(1, sorted, sorted.Length,
                                            buffers.Nodes.ToArray(), buffers.Flat, buffers.FlatLength)
                       { DataRowLevel = 1 };

        Assert.Equal(9, snapshot.Count); // 3 groups + 6 rows
        var first = snapshot.GetRow(0);
        Assert.True(first.IsGroup);
        Assert.Equal(0, first.Level);
        Assert.Equal("East", snapshot.Groups[first.GroupNodeIndex].FormattedKey);

        var firstData = snapshot.GetRow(1);
        Assert.False(firstData.IsGroup);
        Assert.Equal(1, firstData.Level);
        Assert.True(firstData.RowId >= 0);

        Assert.Equal(1, snapshot.IndexOfRow(firstData.RowId));
        Assert.Equal(-1, snapshot.IndexOfRow(999));
    }

    [Fact]
    public void Group_summaries_ride_the_nodes()
    {
        var sorted = SortedView(Region);
        var (nodes, _) = Run(sorted, new HashSet<string>(), Region);

        var amount = Column(o => o.Amount);
        var sum = ColumnAggregator.Create(amount, AggregateKind.Sum, "$#,##0");
        var east = nodes.First(n => n.FormattedKey == "East");
        var value = sum.Aggregate(sorted, east.SortedStart, east.RowCount);
        Assert.Equal(12450m + 22750m + 31900m, value.AsDecimal);
    }
}
