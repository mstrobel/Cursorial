using System.ComponentModel;
using System.Linq.Expressions;

using Cursorial.UI.DataViews.Shaping;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews.Shaping;

/// <summary>
/// The §2.2 collation-key panel amendment: culture-mode string columns sort through
/// <see cref="CollationKeyStore"/> sort-key bytes compared ordinally — culture order at memcmp
/// speed. The contract under test: the compiled sort AND <see cref="ShapedColumn.CompareSlots"/>
/// (the grouping/Min-Max leg — §2.5 group runs must match sort order) are byte-for-byte
/// indistinguishable from the direct null-first <c>string.Compare</c> reference, across mixed
/// case/accents/nulls, dirty re-extraction rewrites, and the blob's append+compaction lifecycle;
/// Ordinal columns never pay the blob.
/// </summary>
public class CollationKeyTests
{
    private sealed class Row
    {
        public string? Name { get; set; }
        public int Age { get; set; }
    }

    private static ShapedColumn<Row, string> CreateColumn(StringComparison comparison)
        => (ShapedColumn<Row, string>)ShapingCodegen.CreateColumn<Row>(
            identity: "name", (Expression<Func<Row, string?>>)(r => r.Name), comparison);

    private static List<Row> RowsFrom(params string?[] names)
        => names.Select(n => new Row { Name = n }).ToList();

    /// <summary>The mixed-case/accented/null/empty adversarial set (the §2.2 acceptance data).</summary>
    private static readonly string?[] MixedNames =
        ["apple", "Apple", "äpple", "zebra", null, "Zebra", "APPLE", "résumé", "resume", "", "réservé"];

    private static void ExtractAll(ShapedColumn<Row, string> column, IReadOnlyList<Row> rows)
    {
        column.EnsureCapacity(rows.Count);
        for (int i = 0; i < rows.Count; i++)
            column.ExtractKey(rows[i], i);
    }

    /// <summary>The direct-ICU oracle: null-first + <c>string.Compare</c> — exactly what the
    /// sort-key path must reproduce (culture-agnostic tests: order is always RELATIVE to this).</summary>
    private static Comparison<string?> Reference(StringComparison comparison) => (a, b) =>
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        return string.Compare(a, b, comparison);
    };

    private static int[] ReferenceOrder(IReadOnlyList<Row> rows, StringComparison comparison, bool descending = false)
    {
        var reference = Reference(comparison);
        var order = Enumerable.Range(0, rows.Count).ToArray();
        Array.Sort(order, (a, b) =>
        {
            // Descending = operand swap (mirror of the fused comparer); ties fall to the ascending
            // slot-id tiebreak either way (BuildSlotComparison without a sequence owner).
            int c = descending ? reference(rows[b].Name, rows[a].Name) : reference(rows[a].Name, rows[b].Name);
            return c != 0 ? c : a - b;
        });
        return order;
    }

    private static int[] SortSlots(ShapedColumn<Row, string> column, int count, bool descending = false)
    {
        var comparison = ShapingCodegen.BuildSlotComparison([(column, descending)]);
        var slots = Enumerable.Range(0, count).ToArray();
        ShapingSort.Sort(slots, count, comparison, new SortScratch());
        return slots;
    }

    // ── Sort-order equivalence ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(StringComparison.CurrentCulture)]
    [InlineData(StringComparison.CurrentCultureIgnoreCase)]
    [InlineData(StringComparison.InvariantCulture)]
    [InlineData(StringComparison.InvariantCultureIgnoreCase)]
    public void Culture_mode_sort_order_matches_direct_string_compare(StringComparison comparison)
    {
        var rows = RowsFrom(MixedNames);
        var column = CreateColumn(comparison);
        ExtractAll(column, rows);

        Assert.Equal(ReferenceOrder(rows, comparison), SortSlots(column, rows.Count));
    }

    [Fact]
    public void Descending_swaps_operands_on_the_sort_key_path()
    {
        var rows = RowsFrom(MixedNames);
        var column = CreateColumn(StringComparison.InvariantCulture);
        ExtractAll(column, rows);

        Assert.Equal(ReferenceOrder(rows, StringComparison.InvariantCulture, descending: true),
                     SortSlots(column, rows.Count, descending: true));
    }

    // ── Group-boundary consistency (§2.5: CompareSlots must mirror the sort) ─────────────────────

    [Fact]
    public void CompareSlots_is_zero_iff_culture_equal_and_sign_matches_the_reference()
    {
        // IgnoreCase makes the case-variants genuinely EQUAL — the collation keys must collapse to
        // identical bytes ("apple" ≡ "APPLE") while accents stay distinct ("äpple" ≠ "apple").
        var rows = RowsFrom("apple", "Apple", "APPLE", "äpple", null, null, "", "zebra");
        var column = CreateColumn(StringComparison.InvariantCultureIgnoreCase);
        ExtractAll(column, rows);

        var reference = Reference(StringComparison.InvariantCultureIgnoreCase);
        for (int a = 0; a < rows.Count; a++)
        {
            for (int b = 0; b < rows.Count; b++)
            {
                Assert.Equal(Math.Sign(reference(rows[a].Name, rows[b].Name)),
                             Math.Sign(column.CompareSlots(a, b)));
            }
        }
    }

    // ── Dirty re-extraction (the append-rewrite lifecycle) ───────────────────────────────────────

    [Fact]
    public void Dirty_reextraction_rewrites_the_slot_range_and_reorders_correctly()
    {
        var rows = RowsFrom("delta", "alpha", "écho", "Bravo", null);
        var column = CreateColumn(StringComparison.CurrentCulture);
        ExtractAll(column, rows);
        Assert.Equal(ReferenceOrder(rows, StringComparison.CurrentCulture), SortSlots(column, rows.Count));

        rows[1].Name = "zulu";        // the front-runner must sink to the back
        column.ExtractKey(rows[1], 1);
        rows[3].Name = null;          // and a live→null rewrite (the −1 flag path)
        column.ExtractKey(rows[3], 3);

        Assert.Equal(ReferenceOrder(rows, StringComparison.CurrentCulture), SortSlots(column, rows.Count));
    }

    // ── Compaction ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Compaction_fires_past_the_waste_threshold_and_preserves_order()
    {
        var rows = RowsFrom("bravo", "alpha", "delta", "charlie", null, "echo");
        var column = CreateColumn(StringComparison.InvariantCulture);
        ExtractAll(column, rows);

        var store = column.Collation!;
        Assert.Equal(0, store.CompactionCount); // initial extraction leaks nothing

        // Each rewrite of one slot orphans its previous range — dead bytes cross the 50% threshold
        // within a handful of iterations, so 300 rewrites force MANY compaction passes.
        for (int i = 0; i < 300; i++)
        {
            rows[0].Name = $"mutant-{i:000}";
            column.ExtractKey(rows[0], 0);
        }

        Assert.True(store.CompactionCount > 0, "compaction never fired despite >50% waste");

        // Compaction actually reclaims: 300 rewrites appended kilobytes of sort keys, but the live
        // set is 6 short strings — the append cursor must stay near live size, not accumulate.
        Assert.True(store.UsedBytes < 4096, $"blob never shrank (append cursor at {store.UsedBytes} bytes)");

        // Order and equality survive the repacks (offsets moved; bytes must not have).
        Assert.Equal(ReferenceOrder(rows, StringComparison.InvariantCulture), SortSlots(column, rows.Count));
        var reference = Reference(StringComparison.InvariantCulture);
        for (int a = 0; a < rows.Count; a++)
        {
            for (int b = 0; b < rows.Count; b++)
            {
                Assert.Equal(Math.Sign(reference(rows[a].Name, rows[b].Name)),
                             Math.Sign(column.CompareSlots(a, b)));
            }
        }
    }

    // ── Path pinning ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Compiled_sort_and_CompareSlots_read_the_blob_not_the_string_vector()
    {
        // Collation keys are semantically ≡ direct culture compare BY DESIGN, so the equivalence
        // tests above cannot tell WHICH path executed. Corrupting the string vector without
        // re-extracting splits them: the sort-key path keeps the extracted order; the direct path
        // would follow the corruption. (Display reads the string vector — collation keys are
        // sort-order-only, §2.2 — so FormatSlot seeing the corruption is the same seam's proof.)
        var rows = RowsFrom("bravo", "alpha", "charlie");
        var column = CreateColumn(StringComparison.InvariantCulture);
        ExtractAll(column, rows);

        column.Keys[0] = "zzz-not-bravo";

        Assert.Equal(new[] { 1, 0, 2 }, SortSlots(column, rows.Count)); // alpha, bravo, charlie — extracted truth
        Assert.True(column.CompareSlots(0, 2) < 0);                     // bravo < charlie (not zzz > charlie)
        Assert.Equal("zzz-not-bravo", column.FormatSlot(0));            // display still reads the strings
    }

    // ── Blob opt-in (§2.2: ordinal columns skip it; non-string columns never see it) ─────────────

    [Fact]
    public void Ordinal_modes_and_non_string_columns_build_no_blob()
    {
        Assert.Null(CreateColumn(StringComparison.Ordinal).Collation);
        Assert.Null(CreateColumn(StringComparison.OrdinalIgnoreCase).Collation);

        // A non-string column with a culture mode requested: no blob either (strings only).
        var age = (ShapedColumn<Row, int>)ShapingCodegen.CreateColumn<Row>(
            identity: "age", (Expression<Func<Row, int>>)(r => r.Age), StringComparison.CurrentCulture);
        Assert.Null(age.Collation);
    }

    [Theory]
    [InlineData(StringComparison.CurrentCulture)]
    [InlineData(StringComparison.CurrentCultureIgnoreCase)]
    [InlineData(StringComparison.InvariantCulture)]
    [InlineData(StringComparison.InvariantCultureIgnoreCase)]
    public void Culture_modes_build_the_blob(StringComparison comparison)
        => Assert.NotNull(CreateColumn(comparison).Collation);

    [Fact]
    public void Ordinal_sort_still_orders_through_the_direct_path()
    {
        var rows = RowsFrom("B", "a", "A", null, "b");
        var column = CreateColumn(StringComparison.Ordinal);
        ExtractAll(column, rows);

        Assert.Equal(ReferenceOrder(rows, StringComparison.Ordinal), SortSlots(column, rows.Count));
    }

    // ── Controller end-to-end (extraction sites + the §2.6 tick lane feed the blob) ──────────────

    private sealed class ObservableRow : INotifyPropertyChanged
    {
        private string? _name;

        public string? Name
        {
            get => _name;
            set
            {
                _name = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static List<string?> ViewNames(DataViewController<ObservableRow> controller)
    {
        var snapshot = controller.Snapshot;
        var names = new List<string?>(snapshot.Count);
        for (int i = 0; i < snapshot.Count; i++)
            names.Add(((ObservableRow)controller.GetRowObject(snapshot.GetRow(i).RowId)).Name);
        return names;
    }

    [Fact]
    public void Controller_culture_sort_and_dirty_tick_ride_the_sort_key_path()
    {
        using var controller = new DataViewController<ObservableRow>();
        controller.SetColumns([new ShapingColumnDescription
        {
            Key = "name", FieldName = "Name", StringComparison = StringComparison.CurrentCulture,
        }]);

        var rows = new[] { "zebra", "Apple", null, "äpple", "apple", "Zebra" }
            .Select(n => new ObservableRow { Name = n }).ToList();
        controller.AttachSource(rows);
        controller.SetShape([SortDescription.Ascending("name")], [], [], null);

        var expected = rows.Select(r => r.Name).ToList();
        expected.Sort(Reference(StringComparison.CurrentCulture));
        Assert.Equal(expected, ViewNames(controller));

        // An INPC tick re-extracts at the drain (§2.6 invariant 1) — the blob rewrite must land
        // before the repair merge reads it, or the repaired order goes stale.
        rows[0].Name = "aardvark";
        controller.Flush();

        expected = rows.Select(r => r.Name).ToList();
        expected.Sort(Reference(StringComparison.CurrentCulture));
        Assert.Equal(expected, ViewNames(controller));
    }

    [Fact]
    public void Controller_groups_culture_equal_case_variants_into_one_run()
    {
        using var controller = new DataViewController<ObservableRow>();
        controller.SetColumns([new ShapingColumnDescription
        {
            Key = "name", FieldName = "Name", StringComparison = StringComparison.CurrentCultureIgnoreCase,
        }]);
        controller.AttachSource(new[] { "apple", "zebra", "Apple", "APPLE", "Zebra" }
            .Select(n => new ObservableRow { Name = n }).ToList());
        controller.SetShape([], [new GroupDescription("name")], [], null);

        // The group walk's boundary test is CompareSlots (§2.5): case-variants are culture-EQUAL
        // under IgnoreCase, so all three apples form ONE contiguous run — if CompareSlots diverged
        // from the sort-key order this would fracture into per-casing groups.
        var snapshot = controller.Snapshot;
        Assert.Equal(2, snapshot.Groups.Count);
        Assert.Equal(3, snapshot.Groups[0].RowCount); // apple / Apple / APPLE
        Assert.Equal(2, snapshot.Groups[1].RowCount); // zebra / Zebra
    }
}
