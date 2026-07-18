using System.Linq.Expressions;

using Cursorial.UI.DataViews.Shaping;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews.Shaping;

/// <summary>
/// <see cref="ShapingFilter"/> contracts: criteria-tree compilation over typed key vectors —
/// operator semantics (sort-consistent null ordering), string Contains/StartsWith, Between,
/// value sets incl. the null "(Blanks)" member, And/Or/Not composition, custom row predicates,
/// build-time literal conversion, and the evaluation no-boxing gate.
/// </summary>
public class ShapingFilterTests
{
    private sealed record Order(string Id, string? Region, decimal Amount, double? Margin, string Status);

    private static readonly Order[] Rows =
    [
        new("SO-1042", "East",  12450m, 0.38, "Shipped"),
        new("SO-1044", "East",  31900m, 0.41, "Processing"),
        new("SO-1046", "South", 19800m, 0.28, "Shipped"),
        new("SO-1047", "West",  27300m, null, "On Hold"),
        new("SO-1049", null,    19800m, 0.31, "Cancelled"),
    ];

    private sealed class Fixture
    {
        public readonly Dictionary<object, ShapedColumn> Columns = new();
        public readonly Func<int, Order> RowAccessor = slot => Rows[slot];

        public Fixture()
        {
            Add("Id", (Order o) => o.Id);
            Add("Region", (Order o) => o.Region);
            Add("Amount", (Order o) => o.Amount);
            Add("Margin", (Order o) => o.Margin);
            Add("Status", (Order o) => o.Status);
        }

        private void Add<TKey>(string key, Expression<Func<Order, TKey>> selector)
        {
            var column = ShapingCodegen.CreateColumn<Order>(key, selector);
            column.EnsureCapacity(Rows.Length);
            for (int i = 0; i < Rows.Length; i++)
                column.ExtractKeyUntyped(Rows[i], i);
            Columns[key] = column;
        }

        public Func<int, bool> Compile(FilterNode root)
            => ShapingFilter.Compile(root, key => Columns.GetValueOrDefault(key), RowAccessor);

        public int[] Matches(FilterNode root)
        {
            var predicate = Compile(root);
            return Enumerable.Range(0, Rows.Length).Where(predicate).ToArray();
        }
    }

    private static readonly Fixture F = new();

    [Fact]
    public void Comparison_operators_over_decimal()
    {
        Assert.Equal(new[] { 1, 3 }, F.Matches(FilterNode.Condition("Amount", FilterOperator.GreaterThan, 20000m)));
        Assert.Equal(new[] { 0, 2, 4 }, F.Matches(FilterNode.Condition("Amount", FilterOperator.LessThanOrEqual, 19800m)));
        Assert.Equal(new[] { 2, 4 }, F.Matches(FilterNode.Condition("Amount", FilterOperator.Equals, 19800m)));
        Assert.Equal(new[] { 0, 1, 3 }, F.Matches(FilterNode.Condition("Amount", FilterOperator.NotEquals, 19800m)));
    }

    [Fact]
    public void Literals_convert_at_build_time()
    {
        // A string literal against a decimal column (the auto-filter path) converts once at compile.
        Assert.Equal(new[] { 1, 3 }, F.Matches(FilterNode.Condition("Amount", FilterOperator.GreaterThan, "20000")));
        Assert.Equal(new[] { 1 }, F.Matches(FilterNode.Condition("Amount", FilterOperator.Equals, 31900)));  // int → decimal
    }

    [Fact]
    public void Between_is_inclusive()
        => Assert.Equal(new[] { 2, 3, 4 }, F.Matches(FilterNode.Condition("Amount", FilterOperator.Between, 19800m, 27300m)));

    [Fact]
    public void Null_ordering_matches_sort_semantics()
    {
        // Nulls sort FIRST: null < any value, so "< 0.30" includes the null Margin row (3) — the
        // comparison-based lane is deliberately sort-consistent (doc §2.4). Checklist/Equals exclude it.
        Assert.Equal(new[] { 2, 3 }, F.Matches(FilterNode.Condition("Margin", FilterOperator.LessThan, 0.30)));
        Assert.Equal(new[] { 3 }, F.Matches(FilterNode.Condition("Margin", FilterOperator.Equals, null)));
    }

    [Fact]
    public void String_contains_and_startswith_ignore_case_and_reject_null()
    {
        Assert.Equal(new[] { 0, 2 }, F.Matches(FilterNode.Condition("Status", FilterOperator.Contains, "ship")));
        Assert.Equal(new[] { 4 }, F.Matches(FilterNode.Condition("Status", FilterOperator.StartsWith, "can")));
        // Region null row (4) never matches Contains — the null guard.
        Assert.Equal(new[] { 0, 1 }, F.Matches(FilterNode.Condition("Region", FilterOperator.Contains, "east")));
    }

    [Fact]
    public void Value_set_includes_null_blanks_member()
    {
        Assert.Equal(new[] { 0, 1, 4 }, F.Matches(FilterNode.InSet("Region", ["East", null])));
        Assert.Equal(new[] { 2 }, F.Matches(FilterNode.InSet("Region", ["South"])));
    }

    [Fact]
    public void And_or_not_compose()
    {
        var tree = FilterNode.And(
            FilterNode.Or(
                FilterNode.Condition("Region", FilterOperator.Equals, "East"),
                FilterNode.Condition("Region", FilterOperator.Equals, "West")),
            FilterNode.Condition("Amount", FilterOperator.GreaterThanOrEqual, 27300m));
        Assert.Equal(new[] { 1, 3 }, F.Matches(tree));

        Assert.Equal(new[] { 2, 4 }, F.Matches(FilterNode.Not(
            FilterNode.Or(
                FilterNode.Condition("Region", FilterOperator.Equals, "East"),
                FilterNode.Condition("Region", FilterOperator.Equals, "West")))));
    }

    [Fact]
    public void Custom_predicate_reads_the_row()
    {
        Expression<Func<Order, bool>> predicate = o => o.Id.EndsWith("7") || o.Status == "Cancelled";
        Assert.Equal(new[] { 3, 4 }, F.Matches(FilterNode.Custom(predicate)));
    }

    [Fact]
    public void Custom_predicate_type_mismatch_throws_at_compile()
    {
        Expression<Func<string, bool>> wrong = s => s.Length > 0;
        Assert.Throws<ArgumentException>(() => F.Compile(FilterNode.Custom(wrong)));
    }

    [Fact]
    public void Unknown_column_throws_at_compile()
        => Assert.Throws<ArgumentException>(() => F.Compile(FilterNode.Condition("Nope", FilterOperator.Equals, 1)));

    [Fact]
    public void Null_against_non_nullable_key_throws_at_compile()
        => Assert.Throws<ArgumentException>(() => F.Compile(FilterNode.Condition("Amount", FilterOperator.Equals, null)));

    [Fact]
    public void Group_factories_validate()
    {
        Assert.Throws<ArgumentException>(() => FilterNode.And());
        Assert.Throws<ArgumentException>(() => FilterNode.Condition("Amount", FilterOperator.Between, 1m));
        // Single-child groups collapse to the child.
        var single = FilterNode.And(FilterNode.Condition("Amount", FilterOperator.Equals, 19800m));
        Assert.IsType<FilterConditionNode>(single);
    }

    [Fact]
    public void Evaluation_path_allocates_nothing()
    {
        var predicate = F.Compile(FilterNode.And(
            FilterNode.Condition("Region", FilterOperator.Equals, "East"),
            FilterNode.Condition("Amount", FilterOperator.GreaterThan, 10000m),
            FilterNode.InSet("Status", ["Shipped", "Processing"])));

        for (int i = 0; i < 1000; i++)
            predicate(i % Rows.Length);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            predicate(i % Rows.Length);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
