using Cursorial.UI.DataViews.Shaping;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews.Shaping;

/// <summary>
/// <see cref="ShapingRepair"/> oracles: repair(view, ticks) ≡ full sort of the post-tick data —
/// the equivalence that makes incremental repair safe to ship. Ticks mix value changes, inserts,
/// and removals; the comparison carries the engine's row-id tiebreak (total order).
/// </summary>
public class ShapingRepairTests
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

    [Fact]
    public void Value_changes_reposition_only_dirty_rows()
    {
        var table = new Table(10);
        for (int i = 0; i < 10; i++)
            table.Values[i] = i * 10;

        int[] view = Enumerable.Range(0, 10).ToArray(); // sorted by construction

        // Rows 3 and 7 change: 30→95 (moves near end), 70→5 (moves to front).
        table.Values[3] = 95;
        table.Values[7] = 5;

        var dirty = new RowFlagSet();
        dirty.Add(3);
        dirty.Add(7);

        int[] dirtyRows = [3, 7];
        int[] result = new int[10];
        int length = ShapingRepair.Repair(view, 10, dirty, new RowFlagSet(), dirtyRows, 2,
                                          table.Comparison, result, new SortScratch());

        // Values after the tick: r0=0, r7=5, r1=10, r2=20, r4=40, r5=50, r6=60, r8=80, r3=95, r9=90…
        // ascending: 0(0), 5(7), 10(1), 20(2), 40(4), 50(5), 60(6), 80(8), 90(9), 95(3).
        Assert.Equal(10, length);
        Assert.Equal(new[] { 0, 7, 1, 2, 4, 5, 6, 8, 9, 3 }, result);
    }

    [Fact]
    public void Inserts_merge_without_touching_clean_order()
    {
        var table = new Table(8);
        int[] initial = [0, 1, 2, 3];
        for (int i = 0; i < 4; i++)
            table.Values[i] = i * 20; // 0, 20, 40, 60

        table.Values[4] = 30; // between 1 and 2
        table.Values[5] = 70; // after 3

        int[] dirtyRows = [4, 5];
        int[] result = new int[6];
        int length = ShapingRepair.Repair(initial, 4, new RowFlagSet(), new RowFlagSet(), dirtyRows, 2,
                                          table.Comparison, result, new SortScratch());

        Assert.Equal(6, length);
        Assert.Equal(new[] { 0, 1, 4, 2, 3, 5 }, result);
    }

    [Fact]
    public void Removals_drop_in_the_sweep()
    {
        var table = new Table(6);
        int[] view = [0, 1, 2, 3, 4, 5];
        for (int i = 0; i < 6; i++)
            table.Values[i] = i;

        var removed = new RowFlagSet();
        removed.Add(0);
        removed.Add(3);
        removed.Add(5);

        int[] result = new int[6];
        int length = ShapingRepair.Repair(view, 6, new RowFlagSet(), removed, [], 0,
                                          table.Comparison, result, new SortScratch());

        Assert.Equal(3, length);
        Assert.Equal(new[] { 1, 2, 4 }, result.Take(3).ToArray());
    }

    [Fact]
    public void Mixed_tick_stream_fuzz_matches_full_sort()
    {
        // The load-bearing oracle: random sorted baseline → random tick batches (changes + inserts +
        // removes) → repair ≡ full TimSort of the surviving rows. 150 rounds, varying N and K.
        var rng = new Random(2026);

        for (int round = 0; round < 150; round++)
        {
            int capacity = rng.Next(1, 400);
            var table = new Table(capacity + 64);
            var live = new List<int>();
            for (int i = 0; i < capacity; i++)
            {
                table.Values[i] = rng.Next(50);
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

            int changes = rng.Next(0, Math.Max(1, capacity / 3));
            for (int c = 0; c < changes; c++)
            {
                int row = live[rng.Next(live.Count)];
                if (removed.Contains(row))
                    continue;
                table.Values[row] = rng.Next(50);
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

            int inserts = rng.Next(0, 32);
            int nextId = capacity;
            for (int a = 0; a < inserts; a++)
            {
                int row = nextId++;
                table.Values[row] = rng.Next(50);
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

    [Fact]
    public void Empty_view_with_inserts_is_a_plain_sort()
    {
        var table = new Table(4);
        table.Values[0] = 3; table.Values[1] = 1; table.Values[2] = 2; table.Values[3] = 0;

        int[] result = new int[4];
        int length = ShapingRepair.Repair([], 0, new RowFlagSet(), new RowFlagSet(),
                                          [0, 1, 2, 3], 4, table.Comparison, result, new SortScratch());

        Assert.Equal(4, length);
        Assert.Equal(new[] { 3, 1, 2, 0 }, result);
    }

    [Fact]
    public void RowFlagSet_add_contains_clear_roundtrip()
    {
        var set = new RowFlagSet();
        Assert.True(set.Add(5));
        Assert.False(set.Add(5));
        Assert.True(set.Add(1000)); // growth across words
        Assert.True(set.Contains(5));
        Assert.True(set.Contains(1000));
        Assert.False(set.Contains(64));
        Assert.Equal(2, set.Count);

        set.Clear();
        Assert.Equal(0, set.Count);
        Assert.False(set.Contains(5));
        Assert.False(set.Contains(1000));
        Assert.True(set.Add(5)); // reusable after clear
    }
}
