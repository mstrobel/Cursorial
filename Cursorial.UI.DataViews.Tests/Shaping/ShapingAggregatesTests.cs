using System.Globalization;
using System.Linq.Expressions;

using Cursorial.UI.DataViews.Shaping;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews.Shaping;

/// <summary>
/// <see cref="ColumnAggregator"/> contracts: typed Sum/Min/Max/Average/Count over slot ranges of a
/// view permutation, null skipping (ignore-null), the decimal-exact lane, group-range windows, and
/// the per-row no-boxing gate.
/// </summary>
public class ShapingAggregatesTests
{
    private sealed record Order(string Id, decimal Amount, double? Margin, int Quantity);

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static ShapedColumn<Order, TKey> Column<TKey>(Expression<Func<Order, TKey>> selector, string? format = null)
        => (ShapedColumn<Order, TKey>)ShapingCodegen.CreateColumn<Order>(selector.ToString(), selector,
                                                                         format: format, culture: Invariant);

    private static readonly Order[] Rows =
    [
        new("A", 12450m, 0.38, 3),
        new("B", 31900m, 0.41, 7),
        new("C", 19800m, null, 2),
        new("D", 27300m, 0.35, 5),
    ];

    private static void Extract<TKey>(ShapedColumn<Order, TKey> column)
    {
        column.EnsureCapacity(Rows.Length);
        for (int i = 0; i < Rows.Length; i++)
            column.ExtractKey(Rows[i], i);
    }

    private static readonly int[] View = [0, 1, 2, 3];

    [Fact]
    public void Decimal_sum_is_exact_and_formats_via_column()
    {
        var amount = Column(o => o.Amount, "$#,##0");
        Extract(amount);

        var sum = ColumnAggregator.Create(amount, AggregateKind.Sum, "$#,##0");
        var value = sum.Aggregate(View, 0, 4);
        Assert.Equal(91450m, value.AsDecimal);
        Assert.Equal("$91,450", sum.Format(value));
    }

    [Fact]
    public void Nullable_average_skips_nulls()
    {
        var margin = Column(o => o.Margin, "0.0%");
        Extract(margin);

        var avg = ColumnAggregator.Create(margin, AggregateKind.Average, "0.0%");
        var value = avg.Aggregate(View, 0, 4);
        Assert.Equal((0.38 + 0.41 + 0.35) / 3, value.AsDouble, precision: 12); // C's null skipped
    }

    [Fact]
    public void Min_max_use_the_column_comparison_and_format()
    {
        var amount = Column(o => o.Amount, "$#,##0");
        Extract(amount);

        var min = ColumnAggregator.Create(amount, AggregateKind.Min, "$#,##0");
        var max = ColumnAggregator.Create(amount, AggregateKind.Max, "$#,##0");
        Assert.Equal("$12,450", min.Format(min.Aggregate(View, 0, 4)));
        Assert.Equal("$31,900", max.Format(max.Aggregate(View, 0, 4)));
    }

    [Fact]
    public void Count_counts_the_range_not_the_values()
    {
        var margin = Column(o => o.Margin);
        Extract(margin);
        var count = ColumnAggregator.Create(margin, AggregateKind.Count);
        Assert.Equal(4, count.Aggregate(View, 0, 4).AsDouble);
        Assert.Equal("4", count.Format(count.Aggregate(View, 0, 4)));
    }

    [Fact]
    public void Group_range_windows_aggregate_partially()
    {
        var quantity = Column(o => o.Quantity);
        Extract(quantity);

        var sum = ColumnAggregator.Create(quantity, AggregateKind.Sum);
        Assert.Equal(10, sum.Aggregate(View, 0, 2).AsDouble);  // A+B
        Assert.Equal(7, sum.Aggregate(View, 2, 2).AsDouble);   // C+D
        Assert.Equal("10", sum.Format(sum.Aggregate(View, 0, 2))); // integral sum formats integrally
    }

    [Fact]
    public void Empty_range_yields_empty_average_and_minmax()
    {
        var amount = Column(o => o.Amount);
        Extract(amount);
        Assert.True(ColumnAggregator.Create(amount, AggregateKind.Average).Aggregate(View, 0, 0).IsEmpty);
        Assert.True(ColumnAggregator.Create(amount, AggregateKind.Min).Aggregate(View, 0, 0).IsEmpty);
        Assert.Equal(string.Empty, ColumnAggregator.Create(amount, AggregateKind.Min).Format(AggregateValue.Empty));
    }

    [Fact]
    public void Sum_over_non_numeric_throws_at_creation()
    {
        var id = Column(o => o.Id);
        Extract(id);
        Assert.Throws<ArgumentException>(() => ColumnAggregator.Create(id, AggregateKind.Sum));
        // …but Min/Max over strings is legal (comparison-based).
        var max = ColumnAggregator.Create(id, AggregateKind.Max);
        Assert.Equal("D", max.Format(max.Aggregate(View, 0, 4)));
    }

    [Fact]
    public void Numeric_aggregation_path_does_not_box_per_row()
    {
        var quantity = Column(o => o.Quantity);
        Extract(quantity);
        var sum = ColumnAggregator.Create(quantity, AggregateKind.Sum);

        for (int i = 0; i < 1000; i++)
            sum.Aggregate(View, 0, 4);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            sum.Aggregate(View, 0, 4);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
