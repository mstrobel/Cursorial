namespace Cursorial.UI.DataViews.Shaping;

/// <summary>
/// The incremental sort repair (design doc §2.3): given a view permutation that WAS sorted before a
/// live tick dirtied K rows (values changed), inserted rows, and removed rows, produces the re-sorted
/// permutation in O(N + K log K) — one partition sweep, one small sort, one merge — instead of an
/// O(N log N) full re-sort. This is the "re-sort as few rows as possible" contract: clean rows are
/// never compared against each other again (their relative order is already correct).
/// </summary>
internal static class ShapingRepair
{
    /// <summary>
    /// Above this dirty fraction (K/N) the repair degenerates and callers should full-sort instead
    /// (TimSort's run detection makes the fallback itself near-linear on the mostly-sorted result).
    /// Benchmark-tuned starting point; <c>SortBenchmark</c> records the measured crossover.
    /// </summary>
    public const double FullSortThreshold = 0.125;

    /// <summary>
    /// Repairs <paramref name="view"/> (length <paramref name="viewLength"/>, sorted except for rows
    /// flagged in <paramref name="dirty"/> / absent flags for removed rows) into
    /// <paramref name="result"/>: clean rows keep their order, dirty+inserted rows
    /// (<paramref name="dirtyRows"/>) are sorted and merged in, removed rows (present in the view but
    /// flagged in <paramref name="removed"/>) are dropped. Returns the new view length.
    /// </summary>
    /// <param name="view">The previously-sorted permutation (row ids).</param>
    /// <param name="viewLength">Live prefix length of <paramref name="view"/>.</param>
    /// <param name="dirty">Per-row-id flag: the row's key changed (it must be re-positioned).</param>
    /// <param name="removed">Per-row-id flag: the row left the view (deleted or filtered out).</param>
    /// <param name="dirtyRows">
    /// The rows to (re-)insert: every still-visible dirty row PLUS newly-inserted/unfiltered rows
    /// (which are not in <paramref name="view"/> at all). The buffer is sorted in place.
    /// </param>
    /// <param name="dirtyCount">Live prefix length of <paramref name="dirtyRows"/>.</param>
    /// <param name="comparison">The active total-order comparison (compiled multi-column + id tiebreak).</param>
    /// <param name="result">Receives the repaired permutation; must hold the final length.</param>
    /// <param name="scratch">Sort scratch for the K-row sort.</param>
    /// <returns>The repaired view length.</returns>
    public static int Repair(
        int[] view, int viewLength,
        RowFlagSet dirty, RowFlagSet removed,
        int[] dirtyRows, int dirtyCount,
        Comparison<int> comparison,
        int[] result,
        SortScratch scratch)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(dirtyRows);
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(result);

        // 1. Sort the K dirty/new rows (K log K).
        ShapingSort.Sort(dirtyRows, dirtyCount, comparison, scratch);

        // 2. Single merge sweep: walk the old view (skipping removed + dirty entries — dirty rows'
        //    old positions are stale) and the sorted dirty buffer, emitting the smaller head. The
        //    clean lane never compares clean-vs-clean: their order is inherited.
        int cleanIndex = 0, dirtyIndex = 0, write = 0;

        // Prime the clean cursor past leading dead entries.
        cleanIndex = SkipDead(view, viewLength, cleanIndex, dirty, removed);

        while (cleanIndex < viewLength && dirtyIndex < dirtyCount)
        {
            int cleanRow = view[cleanIndex];
            int dirtyRow = dirtyRows[dirtyIndex];

            if (comparison(dirtyRow, cleanRow) < 0)
            {
                result[write++] = dirtyRow;
                dirtyIndex++;
            }
            else
            {
                result[write++] = cleanRow;
                cleanIndex = SkipDead(view, viewLength, cleanIndex + 1, dirty, removed);
            }
        }

        while (cleanIndex < viewLength)
        {
            result[write++] = view[cleanIndex];
            cleanIndex = SkipDead(view, viewLength, cleanIndex + 1, dirty, removed);
        }

        for (; dirtyIndex < dirtyCount; dirtyIndex++)
            result[write++] = dirtyRows[dirtyIndex];

        return write;
    }

    private static int SkipDead(int[] view, int viewLength, int index, RowFlagSet dirty, RowFlagSet removed)
    {
        while (index < viewLength)
        {
            int row = view[index];
            if (!dirty.Contains(row) && !removed.Contains(row))
                break;
            index++;
        }
        return index;
    }
}

/// <summary>
/// A grow-only per-row-id flag set (bitset) with O(1) set/test and O(flagged) clear via a side list —
/// the dirty/removed carriers for <see cref="ShapingRepair"/> and the live-update coalescer.
/// Allocation-free in steady state (bits grow with the row-slot high-water mark; the side list is
/// reused).
/// </summary>
internal sealed class RowFlagSet
{
    private ulong[] _bits = [];
    private int[] _flagged = new int[16];
    private int _flaggedCount;

    /// <summary>The number of flagged rows.</summary>
    public int Count => _flaggedCount;

    /// <summary>The flagged row ids (unordered, live prefix of the backing buffer).</summary>
    public ReadOnlySpan<int> Flagged => _flagged.AsSpan(0, _flaggedCount);

    /// <summary>Flags <paramref name="row"/>; returns false when it was already flagged.</summary>
    public bool Add(int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);

        int word = row >> 6;
        if (word >= _bits.Length)
            Array.Resize(ref _bits, Math.Max(word + 1, _bits.Length * 2));

        ulong mask = 1UL << (row & 63);
        if ((_bits[word] & mask) != 0)
            return false;

        _bits[word] |= mask;
        if (_flaggedCount == _flagged.Length)
            Array.Resize(ref _flagged, _flagged.Length * 2);
        _flagged[_flaggedCount++] = row;
        return true;
    }

    /// <summary>Whether <paramref name="row"/> is flagged.</summary>
    public bool Contains(int row)
    {
        int word = row >> 6;
        return (uint)word < (uint)_bits.Length && (_bits[word] & (1UL << (row & 63))) != 0;
    }

    /// <summary>Clears all flags in O(flagged).</summary>
    public void Clear()
    {
        for (int i = 0; i < _flaggedCount; i++)
        {
            int row = _flagged[i];
            _bits[row >> 6] &= ~(1UL << (row & 63));
        }
        _flaggedCount = 0;
    }
}
