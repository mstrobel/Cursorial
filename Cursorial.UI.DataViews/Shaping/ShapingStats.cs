namespace Cursorial.UI.DataViews.Shaping;

/// <summary>
/// Order-statistic selection for the stats seams (design doc §9.5 — TopBottom conditional-format
/// thresholds): a median-of-three Hoare quickselect (<c>nth_element</c>) over a slot index array,
/// O(n) expected comparisons — never a full sort. The comparison is the column's compiled sort
/// comparison, so thresholds are exactly sort-consistent (collation included).
/// </summary>
internal static class ShapingStats
{
    /// <summary>Ranges at or below this length insertion-sort instead of partitioning (also the
    /// floor that keeps the Hoare partition's sentinel argument sound — it only ever runs on ranges
    /// long enough for a genuine median-of-three).</summary>
    private const int InsertionThreshold = 8;

    /// <summary>
    /// Partially reorders <paramref name="items"/>[0..<paramref name="length"/>) so that
    /// <paramref name="items"/>[<paramref name="k"/>] holds the element that would sit at (0-based)
    /// position <paramref name="k"/> in a full ascending sort by <paramref name="comparison"/>
    /// (ties resolve arbitrarily — the VALUE at the position is exact either way, which is all the
    /// ties-include threshold semantics need). Everything left of k compares ≤ it, everything right
    /// compares ≥ it. O(n) expected; the array is permuted in place.
    /// </summary>
    public static void SelectNth(int[] items, int length, int k, Comparison<int> comparison)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)length, (uint)items.Length, nameof(length));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)k, (uint)length, nameof(k));

        int lo = 0, hi = length - 1;
        while (hi - lo > InsertionThreshold)
        {
            int p = Partition(items, lo, hi, comparison);
            if (k <= p)
                hi = p;
            else
                lo = p + 1;
        }

        InsertionSort(items, lo, hi, comparison);
    }

    /// <summary>
    /// Hoare partition with a median-of-three pivot: returns <c>p</c> with every element of
    /// [lo..p] ≤ the pivot ≤ every element of [p+1..hi] (so the k-th order statistic lies wholly in
    /// one side). The median-of-three arrangement guarantees sentinels on both ends, and the return
    /// always satisfies lo ≤ p &lt; hi — both sides shrink, termination is structural.
    /// </summary>
    private static int Partition(int[] items, int lo, int hi, Comparison<int> comparison)
    {
        int mid = lo + ((hi - lo) >> 1);
        if (comparison(items[mid], items[lo]) < 0)
            (items[lo], items[mid]) = (items[mid], items[lo]);
        if (comparison(items[hi], items[lo]) < 0)
            (items[lo], items[hi]) = (items[hi], items[lo]);
        if (comparison(items[hi], items[mid]) < 0)
            (items[mid], items[hi]) = (items[hi], items[mid]);

        int pivot = items[mid];
        int i = lo - 1, j = hi + 1;
        while (true)
        {
            do { i++; } while (comparison(items[i], pivot) < 0);
            do { j--; } while (comparison(items[j], pivot) > 0);
            if (i >= j)
                return j;
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private static void InsertionSort(int[] items, int lo, int hi, Comparison<int> comparison)
    {
        for (int i = lo + 1; i <= hi; i++)
        {
            int value = items[i];
            int j = i - 1;
            while (j >= lo && comparison(items[j], value) > 0)
            {
                items[j + 1] = items[j];
                j--;
            }
            items[j + 1] = value;
        }
    }
}
