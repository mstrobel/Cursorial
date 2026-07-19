using Cursorial.UI.DataViews.Shaping;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews.Shaping;

/// <summary>
/// The galloping repair merge (design doc §2.3, panel-upheld amendment): beyond the
/// repair ≡ full-sort equivalence <see cref="ShapingRepairTests"/> pins, these rows pin the
/// COMPARER-INVOCATION contract — O(K·log(V/K)) instead of the old per-element merge's ~V — by
/// counting invocations through a wrapping <see cref="Comparison{T}"/>. Each compare is 2+ random
/// loads into multi-MB key vectors, so the invocation count IS the repair's dominant cost at 1M
/// rows; the gaps between insertion points must travel by <see cref="Array.Copy(Array,int,Array,int,int)"/>,
/// not by comparison.
/// </summary>
public class ShapingRepairGallopTests
{
    /// <summary>A mutable value table keyed by row id; comparison = value then row id (total order).</summary>
    private sealed class Table(int capacity)
    {
        public readonly int[] Values = new int[capacity];
        public Comparison<int> Comparison => (a, b) =>
        {
            int c = Values[a].CompareTo(Values[b]);
            return c != 0 ? c : a.CompareTo(b);
        };
    }

    /// <summary>Wraps <paramref name="inner"/> to count invocations into a one-element box.</summary>
    private static Comparison<int> Counting(Comparison<int> inner, int[] counter) =>
        (a, b) => { counter[0]++; return inner(a, b); };

    [Fact]
    public void Scattered_dirty_over_large_view_gallops()
    {
        // The gallop's home regime: V = 100k clean rows, K = 10 dirty rows scattered across the
        // whole range — gaps of ~10k clean rows between insertion points. The old per-element
        // merge paid one comparison per emitted element (~100k); the gallop pays the K-sort plus
        // ~2·log2(gap) per dirty element (~300 total). The 1_000 bound leaves headroom for the
        // adaptive probe's constants while staying two orders of magnitude under the old cost.
        const int V = 100_000, K = 10;
        var table = new Table(V + K);
        for (int i = 0; i < V; i++)
            table.Values[i] = i * 10;

        int[] view = Enumerable.Range(0, V).ToArray(); // sorted by construction

        var dirty = new RowFlagSet();
        int[] dirtyRows = new int[K];
        for (int j = 0; j < K; j++)
        {
            int row = 5_000 + j * 10_000;                       // scattered old positions
            table.Values[row] = ((j * 7 + 3) % K) * 100_000 + 5; // scattered new positions
            dirty.Add(row);
            dirtyRows[j] = row;
        }

        int[] counter = new int[1];
        int[] result = new int[V];
        int length = ShapingRepair.Repair(view, V, dirty, new RowFlagSet(), dirtyRows, K,
                                          Counting(table.Comparison, counter), result, new SortScratch());

        Assert.True(counter[0] < 1_000,
                    $"comparer invocations: {counter[0]} (expected < 1_000; the old merge did ~{V:N0})");

        // Correctness ride-along: repair ≡ full sort of the post-tick data.
        int[] expected = Enumerable.Range(0, V).ToArray();
        ShapingSort.Sort(expected, V, table.Comparison, new SortScratch());
        Assert.Equal(V, length);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Clustered_dirty_insert_at_one_point()
    {
        // All K dirty rows land in ONE clean gap (equal values, id tiebreak). After the first
        // element's gallop locates the cluster point, every subsequent element is the one-compare
        // fast path (it precedes the clean element at the cursor), and the sorted-input K-sort is
        // a single natural-run detection — so the whole repair is ~K + 2·log2(V) comparisons.
        const int V = 10_000, K = 64;
        var table = new Table(V + K);
        for (int i = 0; i < V; i++)
            table.Values[i] = i * 100;

        int[] view = Enumerable.Range(0, V).ToArray();

        int[] dirtyRows = new int[K];
        for (int j = 0; j < K; j++)
        {
            int row = V + j;
            table.Values[row] = 499_950; // between clean values 499_900 (row 4999) and 500_000 (row 5000)
            dirtyRows[j] = row;
        }

        int[] counter = new int[1];
        int[] result = new int[V + K];
        int length = ShapingRepair.Repair(view, V, new RowFlagSet(), new RowFlagSet(), dirtyRows, K,
                                          Counting(table.Comparison, counter), result, new SortScratch());

        Assert.True(counter[0] < 400,
                    $"comparer invocations: {counter[0]} (expected < 400; the old merge did ~{V + K:N0})");

        // Clean prefix, the cluster in id order (the total order's tiebreak), clean suffix.
        int[] expected = new int[V + K];
        for (int i = 0; i < 5_000; i++) expected[i] = i;
        for (int j = 0; j < K; j++) expected[5_000 + j] = V + j;
        for (int i = 5_000; i < V; i++) expected[K + i] = i;

        Assert.Equal(V + K, length);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Dirty_all_smaller_than_every_clean_row()
    {
        // Left boundary: every dirty element takes the one-compare fast path (it precedes the
        // first clean element), the gap is always zero, and the whole clean lane rides the tail
        // (already in place after compaction — no copy at all).
        const int V = 5_000, K = 20;
        var table = new Table(V + K);
        for (int i = 0; i < V; i++)
            table.Values[i] = (i + 1_000) * 10;

        int[] view = Enumerable.Range(0, V).ToArray();

        int[] dirtyRows = new int[K];
        for (int j = 0; j < K; j++)
        {
            int row = V + j;
            table.Values[row] = j; // all below the clean minimum (10_000)
            dirtyRows[j] = row;
        }

        int[] counter = new int[1];
        int[] result = new int[V + K];
        int length = ShapingRepair.Repair(view, V, new RowFlagSet(), new RowFlagSet(), dirtyRows, K,
                                          Counting(table.Comparison, counter), result, new SortScratch());

        Assert.True(counter[0] < 100, $"comparer invocations: {counter[0]} (expected < 100)");

        int[] expected = Enumerable.Range(V, K).Concat(Enumerable.Range(0, V)).ToArray();
        Assert.Equal(V + K, length);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Dirty_all_larger_than_every_clean_row()
    {
        // Right boundary: the first dirty element's gallop runs off the end of the clean lane
        // (offset capped at the exclusive bound — the probe must not over-read), bulk-copying the
        // ENTIRE clean run in one Array.Copy; every subsequent element sees an exhausted clean
        // lane and costs zero comparisons.
        const int V = 5_000, K = 20;
        var table = new Table(V + K);
        for (int i = 0; i < V; i++)
            table.Values[i] = i * 10;

        int[] view = Enumerable.Range(0, V).ToArray();

        int[] dirtyRows = new int[K];
        for (int j = 0; j < K; j++)
        {
            int row = V + j;
            table.Values[row] = 1_000_000 + j; // all above the clean maximum (49_990)
            dirtyRows[j] = row;
        }

        int[] counter = new int[1];
        int[] result = new int[V + K];
        int length = ShapingRepair.Repair(view, V, new RowFlagSet(), new RowFlagSet(), dirtyRows, K,
                                          Counting(table.Comparison, counter), result, new SortScratch());

        Assert.True(counter[0] < 100, $"comparer invocations: {counter[0]} (expected < 100)");

        int[] expected = Enumerable.Range(0, V).Concat(Enumerable.Range(V, K)).ToArray();
        Assert.Equal(V + K, length);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void No_dirty_rows_is_pure_compaction_with_zero_comparisons()
    {
        // K = 0 fast path: removals (and dirty-then-removed rows) drop in the comparison-free
        // compaction sweep and the merge never runs — ZERO comparer invocations, clean rows keep
        // their inherited order.
        const int V = 50_000;
        var table = new Table(V);
        for (int i = 0; i < V; i++)
            table.Values[i] = i;

        int[] view = Enumerable.Range(0, V).ToArray();

        var dirty = new RowFlagSet();
        var removed = new RowFlagSet();
        for (int i = 0; i < V; i += 7)
            removed.Add(i);
        for (int i = 3; i < V; i += 1_000)
        {
            // A row that changed and THEN left the view: flagged in both sets, present in neither
            // dirtyRows nor the survivors (the existing oracle's dirtyRows.Remove convention).
            dirty.Add(i);
            removed.Add(i);
        }

        int[] counter = new int[1];
        int[] result = new int[V];
        int length = ShapingRepair.Repair(view, V, dirty, removed, [], 0,
                                          Counting(table.Comparison, counter), result, new SortScratch());

        Assert.Equal(0, counter[0]);

        int[] expected = Enumerable.Range(0, V).Where(r => !removed.Contains(r)).ToArray();
        Assert.Equal(expected.Length, length);
        Assert.Equal(expected, result.Take(length).ToArray());
    }

    [Fact]
    public void Gallop_fuzz_matches_full_sort_of_survivors()
    {
        // The gallop-path oracle: mirrors ShapingRepairTests' 150-round fuzz at 300 rounds, with
        // every fourth round in the gallop's home regime (large V, tiny K, wide value range —
        // deep exponential probes) and the rest dense/duplicate-heavy (tiny gaps → the linear
        // degradation guard; equal values → adjacent-insertion clustering via the id tiebreak).
        var rng = new Random(20726);

        for (int round = 0; round < 300; round++)
        {
            bool sparse = round % 4 == 0;
            int capacity = sparse ? rng.Next(500, 3_000) : rng.Next(1, 400);
            int valueRange = sparse ? 100_000 : 50;

            var table = new Table(capacity + 64);
            var live = new List<int>();
            for (int i = 0; i < capacity; i++)
            {
                table.Values[i] = rng.Next(valueRange);
                live.Add(i);
            }

            // Baseline: full sort.
            int[] view = live.ToArray();
            var scratch = new SortScratch();
            ShapingSort.Sort(view, view.Length, table.Comparison, scratch);

            // A tick batch.
            var dirty = new RowFlagSet();
            var removed = new RowFlagSet();
            var dirtyRows = new List<int>();

            int changes = sparse ? rng.Next(0, 8) : rng.Next(0, Math.Max(1, capacity / 3));
            for (int c = 0; c < changes; c++)
            {
                int row = live[rng.Next(live.Count)];
                if (removed.Contains(row))
                    continue;
                table.Values[row] = rng.Next(valueRange);
                if (dirty.Add(row))
                    dirtyRows.Add(row);
            }

            int removals = rng.Next(0, Math.Max(1, capacity / 4));
            for (int r = 0; r < removals; r++)
            {
                int row = live[rng.Next(live.Count)];
                if (!removed.Add(row))
                    continue;
                if (dirty.Contains(row))
                    dirtyRows.Remove(row); // a removed row never re-inserts
            }

            int inserts = sparse ? rng.Next(0, 4) : rng.Next(0, 32);
            int nextId = capacity;
            for (int a = 0; a < inserts; a++)
            {
                int row = nextId++;
                table.Values[row] = rng.Next(valueRange);
                dirtyRows.Add(row);
                live.Add(row);
            }

            // Repair.
            int[] dirtyBuffer = dirtyRows.ToArray();
            int[] result = new int[live.Count];
            int length = ShapingRepair.Repair(view, view.Length, dirty, removed, dirtyBuffer, dirtyBuffer.Length,
                                              table.Comparison, result, scratch);

            // Reference: full sort of the surviving rows.
            int[] expected = live.Where(r => !removed.Contains(r)).ToArray();
            ShapingSort.Sort(expected, expected.Length, table.Comparison, scratch);

            Assert.Equal(expected.Length, length);
            Assert.Equal(expected, result.Take(length).ToArray());
        }
    }
}
