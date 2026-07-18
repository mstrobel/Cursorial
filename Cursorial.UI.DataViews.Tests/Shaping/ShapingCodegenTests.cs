using System.Globalization;
using System.Linq.Expressions;

using Cursorial.UI.DataViews.Shaping;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews.Shaping;

/// <summary>
/// <see cref="ShapingCodegen"/> contracts: typed compiled kits (getter/comparison/formatter), the
/// fused multi-column slot comparer (descending operand swap, null orderings, id tiebreak, growth
/// observation), the null-propagating property-path lambda, and the no-boxing allocation gate on
/// the compare path.
/// </summary>
public class ShapingCodegenTests
{
    private sealed record Order(string Id, string? Region, decimal Amount, double? Margin, DateOnly Date);

    private sealed class Node
    {
        public Node? Child { get; init; }
        public int Value { get; init; }
    }

    // ── Key comparisons ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Value_type_comparison_orders_and_never_boxes()
    {
        var compare = ShapingCodegen.CreateKeyComparison<decimal>();
        Assert.True(compare(1m, 2m) < 0);
        Assert.True(compare(2m, 1m) > 0);
        Assert.Equal(0, compare(5m, 5m));

        // Warm, then assert the compare path allocates nothing (the no-boxing contract).
        for (int i = 0; i < 1000; i++)
            compare(i, i + 1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            compare(i, i + 1);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Nullable_comparison_is_null_first_and_unboxed()
    {
        var compare = ShapingCodegen.CreateKeyComparison<double?>();
        Assert.True(compare(null, 1.0) < 0);
        Assert.True(compare(1.0, null) > 0);
        Assert.Equal(0, compare(null, null));
        Assert.True(compare(1.5, 2.5) < 0);

        for (int i = 0; i < 1000; i++)
            compare(i, i + 0.5);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            compare(i, i + 0.5);
            compare(null, i);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void String_comparison_defaults_to_current_culture_and_orders_null_first()
    {
        var compare = ShapingCodegen.CreateKeyComparison<string>();
        Assert.True(compare(null!, "a") < 0);
        Assert.True(compare("a", null!) > 0);
        Assert.Equal(0, compare(null!, null!));
        Assert.True(compare("apple", "banana") < 0);
    }

    [Fact]
    public void String_comparison_ordinal_optin_differs_from_culture()
    {
        var ordinal = ShapingCodegen.CreateKeyComparison<string>(StringComparison.Ordinal);
        var ignoreCase = ShapingCodegen.CreateKeyComparison<string>(StringComparison.OrdinalIgnoreCase);
        Assert.True(ordinal("B", "a") < 0);      // 'B'(66) < 'a'(97) ordinally
        Assert.Equal(0, ignoreCase("abc", "ABC"));
    }

    [Fact]
    public void DateOnly_comparison_works()
    {
        var compare = ShapingCodegen.CreateKeyComparison<DateOnly>();
        Assert.True(compare(new DateOnly(2026, 5, 3), new DateOnly(2026, 5, 9)) < 0);
    }

    // ── Property-path lambdas ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Property_path_lambda_reads_nested_members()
    {
        var lambda = ShapingCodegen.BuildPropertyPathLambda(typeof(Node), "Child.Value");
        var getter = (Func<Node, int>)lambda.Compile();
        Assert.Equal(42, getter(new Node { Child = new Node { Value = 42 } }));
    }

    [Fact]
    public void Property_path_lambda_null_propagates_reference_hops()
    {
        var lambda = ShapingCodegen.BuildPropertyPathLambda(typeof(Node), "Child.Child.Value");
        var getter = (Func<Node, int>)lambda.Compile();
        Assert.Equal(0, getter(new Node { Child = null })); // default(int), no NRE
    }

    [Fact]
    public void Property_path_lambda_rejects_unknown_members()
    {
        var ex = Assert.Throws<ArgumentException>(() => ShapingCodegen.BuildPropertyPathLambda(typeof(Node), "Nope"));
        Assert.Contains("Nope", ex.Message);
    }

    // ── Formatters ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Formatter_applies_format_and_culture_without_boxing()
    {
        var format = ShapingCodegen.CreateFormatter<decimal>("$#,##0", CultureInfo.InvariantCulture);
        Assert.Equal("$12,450", format(12450m));

        for (int i = 0; i < 500; i++)
            format(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        format(12450m);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        // The result STRING allocates (of course) — but nothing else should. One small string: < 64 B.
        Assert.InRange(delta, 1, 64);
    }

    [Fact]
    public void Formatter_handles_nullable_and_null_string()
    {
        var nullable = ShapingCodegen.CreateFormatter<double?>("0.0%", CultureInfo.InvariantCulture);
        Assert.Equal("38.0%", nullable(0.38 is { } v ? v : null));
        Assert.Equal(string.Empty, nullable(null));

        var str = ShapingCodegen.CreateFormatter<string>();
        Assert.Equal(string.Empty, str(null!));
        Assert.Equal("x", str("x"));
    }

    [Fact]
    public void Formatter_without_format_uses_plain_ToString()
    {
        var format = ShapingCodegen.CreateFormatter<int>(culture: CultureInfo.InvariantCulture);
        Assert.Equal("7", format(7));
    }

    // ── Columns + the fused slot comparer ────────────────────────────────────────────────────────

    private static ShapedColumn<Order, TKey> Column<TKey>(Expression<Func<Order, TKey>> selector,
                                                          string? format = null)
        => (ShapedColumn<Order, TKey>)ShapingCodegen.CreateColumn<Order>(
            identity: selector.ToString(), selector, format: format, culture: CultureInfo.InvariantCulture);

    private static readonly Order[] SampleRows =
    [
        new("SO-1042", "East",  12450m, 0.38, new DateOnly(2026, 5,  3)),
        new("SO-1044", "East",  31900m, 0.41, new DateOnly(2026, 5,  6)),
        new("SO-1046", "South", 19800m, 0.28, new DateOnly(2026, 5,  9)),
        new("SO-1047", "West",  27300m, null, new DateOnly(2026, 5, 11)),
        new("SO-1049", null,    19800m, 0.31, new DateOnly(2026, 5, 14)),
    ];

    private static void Extract<TKey>(ShapedColumn<Order, TKey> column)
    {
        column.EnsureCapacity(SampleRows.Length);
        for (int i = 0; i < SampleRows.Length; i++)
            column.ExtractKey(SampleRows[i], i);
    }

    [Fact]
    public void Single_column_slot_comparison_sorts_typed_keys()
    {
        var amount = Column(o => o.Amount);
        Extract(amount);

        var comparison = ShapingCodegen.BuildSlotComparison([(amount, false)]);
        int[] slots = [0, 1, 2, 3, 4];
        ShapingSort.Sort(slots, 5, comparison, new SortScratch());

        // 12450(0) < 19800(2 — id tiebreak beats 4) < 19800(4) < 27300(3) < 31900(1)
        Assert.Equal(new[] { 0, 2, 4, 3, 1 }, slots);
    }

    [Fact]
    public void Descending_swaps_operands_and_keeps_id_tiebreak_ascendingly()
    {
        var amount = Column(o => o.Amount);
        Extract(amount);

        var comparison = ShapingCodegen.BuildSlotComparison([(amount, true)]);
        int[] slots = [0, 1, 2, 3, 4];
        ShapingSort.Sort(slots, 5, comparison, new SortScratch());

        // 31900(1) > 27300(3) > 19800(2 vs 4: descending equal ⇒ id tiebreak a-b still ascending) > 12450(0)
        Assert.Equal(new[] { 1, 3, 2, 4, 0 }, slots);
    }

    [Fact]
    public void Multi_column_comparison_chains_levels_with_null_first()
    {
        var region = Column(o => o.Region);
        var amount = Column(o => o.Amount);
        Extract(region);
        Extract(amount);

        var comparison = ShapingCodegen.BuildSlotComparison([(region, false), (amount, true)]);
        int[] slots = [0, 1, 2, 3, 4];
        ShapingSort.Sort(slots, 5, comparison, new SortScratch());

        // Region null(4) < East(0,1 — amount desc: 1 then 0) < South(2) < West(3)
        Assert.Equal(new[] { 4, 1, 0, 2, 3 }, slots);
    }

    [Fact]
    public void Nullable_key_level_orders_null_first()
    {
        var margin = Column(o => o.Margin);
        Extract(margin);

        var comparison = ShapingCodegen.BuildSlotComparison([(margin, false)]);
        int[] slots = [0, 1, 2, 3, 4];
        ShapingSort.Sort(slots, 5, comparison, new SortScratch());

        // null(3) < 0.28(2) < 0.31(4) < 0.38(0) < 0.41(1)
        Assert.Equal(new[] { 3, 2, 4, 0, 1 }, slots);
    }

    [Fact]
    public void Compiled_comparer_observes_key_vector_growth()
    {
        var amount = Column(o => o.Amount);
        Extract(amount);
        var comparison = ShapingCodegen.BuildSlotComparison([(amount, false)]);

        // Grow the vector AFTER compiling (forces re-allocation), then extract new rows into it.
        amount.EnsureCapacity(1000);
        var big = new Order("SO-9999", "East", 999999m, 0.5, new DateOnly(2026, 6, 1));
        amount.ExtractKey(big, 900);

        Assert.True(comparison(0, 900) < 0);  // 12450 < 999999 — the comparer sees the NEW array
        Assert.True(comparison(900, 1) > 0);
    }

    [Fact]
    public void Fused_comparer_compare_path_allocates_nothing()
    {
        var region = Column(o => o.Region);
        var amount = Column(o => o.Amount);
        var margin = Column(o => o.Margin);
        Extract(region);
        Extract(amount);
        Extract(margin);

        var comparison = ShapingCodegen.BuildSlotComparison([(region, false), (amount, true), (margin, false)]);

        for (int i = 0; i < 2000; i++)
            comparison(i % 5, (i + 1) % 5);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20_000; i++)
            comparison(i % 5, (i + 1) % 5);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Column_formats_slots_and_exposes_boxed_keys_cold()
    {
        var amount = Column(o => o.Amount, format: "$#,##0");
        Extract(amount);
        Assert.Equal("$12,450", amount.FormatSlot(0));
        Assert.Equal(31900m, amount.GetKeyBoxed(1));
        Assert.Equal(typeof(decimal), amount.KeyType);
    }

    [Fact]
    public void Empty_sort_levels_reduce_to_the_id_tiebreak()
    {
        var comparison = ShapingCodegen.BuildSlotComparison([]);
        Assert.True(comparison(1, 2) < 0);
        Assert.True(comparison(5, 3) > 0);
        Assert.Equal(0, comparison(4, 4));
    }

    [Fact]
    public void Sequence_tiebreak_keeps_insertion_order_across_slot_reuse()
    {
        // Two equal-key rows where the LATER insertion reuses a LOWER slot: the slot-id tiebreak
        // would order the newcomer first; the sequence tiebreak keeps insertion order (the panel
        // finding — DevExpress-stable ties).
        var store = new RowStore<Order>();
        var region = Column(o => o.Region);

        var first = new Order("A", "East", 1m, null, default);
        var second = new Order("B", "East", 1m, null, default);
        var third = new Order("C", "East", 1m, null, default);

        store.Insert(0, first);              // slot 0
        int slotSecond = store.Insert(1, second); // slot 1
        store.RemoveAt(0);                   // frees slot 0
        int slotThird = store.Insert(1, third);   // REUSES slot 0, but sequence 2

        Assert.True(slotThird < slotSecond); // the trap: slot order inverts insertion order

        region.EnsureCapacity(store.SlotCapacity);
        region.ExtractKey(second, slotSecond);
        region.ExtractKey(third, slotThird);

        var comparison = ShapingCodegen.BuildSlotComparison([(region, false)], store);
        Assert.True(comparison(slotSecond, slotThird) < 0); // equal keys ⇒ insertion order, not slot order

        int[] slots = [slotThird, slotSecond];
        ShapingSort.Sort(slots, 2, comparison, new SortScratch());
        Assert.Equal(new[] { slotSecond, slotThird }, slots);
    }
}
