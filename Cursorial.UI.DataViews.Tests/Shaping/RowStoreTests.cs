using Cursorial.UI.DataViews.Shaping;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews.Shaping;

/// <summary>
/// <see cref="RowStore{TRow}"/> contracts: stable slots (ids never shift under other rows' churn),
/// free-list recycling within the high-water mark, source-order maintenance across insert/remove/
/// move/replace, and the reference→slot INPC mapping rules.
/// </summary>
public class RowStoreTests
{
    private sealed class Row(string name)
    {
        public string Name { get; } = name;
        public override string ToString() => Name;
    }

    [Fact]
    public void Slots_are_stable_under_unrelated_churn()
    {
        var store = new RowStore<Row>();
        var a = new Row("a");
        var b = new Row("b");
        var c = new Row("c");

        int slotA = store.Insert(0, a);
        int slotB = store.Insert(1, b);
        int slotC = store.Insert(2, c);

        store.RemoveAt(0);           // remove a
        store.Insert(0, new Row("x"));

        // b and c keep their slots + rows.
        Assert.Equal(b, store.GetRow(slotB));
        Assert.Equal(c, store.GetRow(slotC));
        Assert.Equal(slotB, store.SlotOf(b));
        Assert.Equal(slotC, store.SlotOf(c));
        Assert.NotEqual(slotA, slotB);
    }

    [Fact]
    public void Freed_slots_recycle_within_high_water()
    {
        var store = new RowStore<Row>();
        store.Insert(0, new Row("a"));
        store.Insert(1, new Row("b"));
        store.Insert(2, new Row("c"));
        Assert.Equal(3, store.SlotCapacity);

        int freed = store.RemoveAt(1);
        store.ReleaseDeferredFrees();      // frees park until the owner releases (publish-gated — final-audit fix)
        int reused = store.Insert(2, new Row("d"));

        Assert.Equal(freed, reused);       // free-list reuse
        Assert.Equal(3, store.SlotCapacity); // high-water unchanged
        Assert.Equal(3, store.Count);
    }

    [Fact]
    public void Source_order_tracks_inserts_removes_and_moves()
    {
        var store = new RowStore<string>();
        int s0 = store.Insert(0, "a");
        int s1 = store.Insert(1, "b");
        int s2 = store.Insert(1, "c");   // a, c, b

        Assert.Equal(s0, store.SlotAt(0));
        Assert.Equal(s2, store.SlotAt(1));
        Assert.Equal(s1, store.SlotAt(2));

        store.Move(2, 0);                // b, a, c
        Assert.Equal(s1, store.SlotAt(0));
        Assert.Equal(s0, store.SlotAt(1));
        Assert.Equal(s2, store.SlotAt(2));

        store.Move(0, 2);                // a, c, b — the inverse move
        Assert.Equal(s0, store.SlotAt(0));
        Assert.Equal(s2, store.SlotAt(1));
        Assert.Equal(s1, store.SlotAt(2));

        store.RemoveAt(1);               // a, b
        Assert.Equal(2, store.Count);
        Assert.Equal(s0, store.SlotAt(0));
        Assert.Equal(s1, store.SlotAt(1));
    }

    [Fact]
    public void Replace_keeps_the_slot_and_remaps_the_instance()
    {
        var store = new RowStore<Row>();
        var old = new Row("old");
        var fresh = new Row("new");
        int slot = store.Insert(0, old);

        Assert.Equal(slot, store.Replace(0, fresh));
        Assert.Equal(fresh, store.GetRow(slot));
        Assert.Equal(slot, store.SlotOf(fresh));
        Assert.Equal(-1, store.SlotOf(old));   // the old instance unmapped
    }

    [Fact]
    public void Duplicate_reference_instances_map_to_the_last_insert()
    {
        var store = new RowStore<Row>();
        var dup = new Row("dup");
        int first = store.Insert(0, dup);
        int second = store.Insert(1, dup);

        Assert.Equal(second, store.SlotOf(dup)); // documented: last insert wins INPC mapping

        // Removing the position mapped to the CURRENT slot unmaps; removing the other does not.
        store.RemoveAt(1);
        Assert.Equal(-1, store.SlotOf(dup));
        Assert.Equal(dup, store.GetRow(first));  // the first position's row is untouched
    }

    [Fact]
    public void Clear_resets_everything()
    {
        var store = new RowStore<Row>();
        var a = new Row("a");
        store.Insert(0, a);
        store.Insert(1, new Row("b"));

        store.Clear();
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.SlotCapacity);
        Assert.Equal(-1, store.SlotOf(a));

        // Reusable after clear; slots restart at 0.
        Assert.Equal(0, store.Insert(0, new Row("c")));
    }

    [Fact]
    public void Value_type_rows_have_no_reference_mapping()
    {
        var store = new RowStore<int>();
        store.Insert(0, 42);
        Assert.Equal(-1, store.SlotOf(42));
        Assert.Equal(42, store.GetRow(store.SlotAt(0)));
    }

    [Fact]
    public void Bounds_are_validated()
    {
        var store = new RowStore<string>();
        store.Insert(0, "a");
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Insert(5, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.RemoveAt(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SlotAt(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Move(0, 1));
    }

    [Fact]
    public void Large_churn_fuzz_keeps_store_and_mirror_consistent()
    {
        var store = new RowStore<Row>();
        var mirror = new List<Row>();
        var rng = new Random(11);

        for (int op = 0; op < 5000; op++)
        {
            switch (rng.Next(4))
            {
                case 0 or 1: // bias toward inserts
                {
                    int index = rng.Next(mirror.Count + 1);
                    var row = new Row($"r{op}");
                    store.Insert(index, row);
                    mirror.Insert(index, row);
                    break;
                }
                case 2 when mirror.Count > 0:
                {
                    int index = rng.Next(mirror.Count);
                    store.RemoveAt(index);
                    mirror.RemoveAt(index);
                    break;
                }
                case 3 when mirror.Count > 1:
                {
                    int from = rng.Next(mirror.Count);
                    int to = rng.Next(mirror.Count);
                    store.Move(from, to);
                    var row = mirror[from];
                    mirror.RemoveAt(from);
                    mirror.Insert(to, row);
                    break;
                }
            }
        }

        Assert.Equal(mirror.Count, store.Count);
        for (int i = 0; i < mirror.Count; i++)
        {
            Assert.Equal(mirror[i], store.GetRow(store.SlotAt(i)));
            Assert.Equal(store.SlotAt(i), store.SlotOf(mirror[i]));
        }
        Assert.True(store.SlotCapacity <= 5000);
    }
}
