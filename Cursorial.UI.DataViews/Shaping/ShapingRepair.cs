namespace Cursorial.UI.DataViews.Shaping;

/// <summary>
/// The incremental sort repair (design doc §2.3): given a view permutation that WAS sorted before a
/// live tick dirtied K rows (values changed), inserted rows, and removed rows, produces the re-sorted
/// permutation with Θ(V) index traffic but only O(K log K + K·log(V/K)) comparer invocations — one
/// comparison-free compaction sweep, one small sort, one GALLOPING merge — instead of an O(N log N)
/// full re-sort. This is the "re-sort as few rows as possible" contract taken to its limit: clean
/// rows are never compared against each other again (their relative order is already correct), and
/// — the §2.3 panel-upheld amendment, replacing the naive per-element merge — clean rows are not
/// compared against dirty rows either except at gap boundaries: each sorted dirty element
/// exponential+binary-searches its insertion point in the clean run, and the intervening clean gap
/// travels by <see cref="Array.Copy(Array,int,Array,int,int)"/>. That turns the pass
/// memory-bandwidth-bound instead of compare-bound — each compare is 2+ random loads into multi-MB
/// key vectors, the dominant cost at 1M rows.
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
    /// The gap size below which the exponential probe stops paying for itself: doubling overshoots
    /// a tiny gap and the binary refinement re-derives what linear stepping would have found in the
    /// same (or fewer) comparisons. When the LAST gap was under this, the next search steps +1 for
    /// its first <see cref="GallopEntryGap"/> probes before doubling arms — so in the dense-K limit
    /// (gaps ~V/K → 1) the repair degrades to exactly the old per-element merge's comparison count
    /// instead of paying a log factor per element, while one large gap immediately re-arms full
    /// galloping. Mirrors TimSort's MinGallop hysteresis in spirit (constant chosen the same order).
    /// </summary>
    private const int GallopEntryGap = 8;

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

        // 2. Compact the LIVE clean lane. The old view interleaves live entries with DEAD ones
        //    (dirty rows' stale positions, removed rows) that must be skipped, not copied — which
        //    would defeat a naive Array.Copy of a clean gap. One Θ(V) comparison-free,
        //    branch-predictable sweep strips them out up front so the merge below never
        //    re-examines flags and its gaps are genuinely contiguous.
        //
        //    The compacted lane lives in RESULT itself, right-aligned at offset dirtyCount:
        //    left-compact, then one contiguous memmove right (Array.Copy is overlap-safe). No
        //    scratch buffer, no second working set. Right-alignment is what makes the in-place
        //    merge sound: the write cursor can never overtake the clean read cursor
        //    (write = dirtyEmitted + cleanEmitted ≤ dirtyCount + cleanEmitted = read), and the
        //    moment the dirty lane is exhausted the two cursors are EQUAL — the remaining clean
        //    tail is already sitting at its final position, so the tail costs zero copies.
        int cleanCount = 0;
        for (int i = 0; i < viewLength; i++)
        {
            int row = view[i];
            if (!dirty.Contains(row) && !removed.Contains(row))
                result[cleanCount++] = row;
        }

        if (dirtyCount == 0)
            return cleanCount; // pure compaction — zero comparer invocations

        if (cleanCount > 0)
            Array.Copy(result, 0, result, dirtyCount, cleanCount);

        // 3. Gallop-merge (sorted dirty, compacted clean) into the result prefix: for each dirty
        //    element, locate its insertion point in the remaining clean run (exponential probe +
        //    binary refinement — O(log gap) comparisons), bulk-copy the gap, emit the element.
        //    Comparer invocations total O(K·log(V/K)); every clean element moves at most once.
        int read = dirtyCount;                 // clean cursor (compacted lane)
        int cleanEnd = dirtyCount + cleanCount;
        int write = 0;
        bool linearFirst = false;              // first element assumes the scattered regime (K ≪ V)

        for (int d = 0; d < dirtyCount; d++)
        {
            int dirtyRow = dirtyRows[d];
            int insert = FindInsertionPoint(dirtyRow, result, read, cleanEnd, linearFirst, comparison);

            int gap = insert - read;
            if (gap > 0)
            {
                Array.Copy(result, read, result, write, gap);
                write += gap;
                read = insert;
            }

            // Arm the degradation guard off the OBSERVED gap (see GallopEntryGap): tiny gaps
            // predict tiny gaps (dense-K), large gaps re-arm immediate doubling (scattered-K).
            linearFirst = gap < GallopEntryGap;

            result[write++] = dirtyRow;
        }

        // Dirty lane exhausted ⇒ write == read (see the right-alignment argument above): the clean
        // tail [read, cleanEnd) is already in its final position.
        System.Diagnostics.Debug.Assert(write == read, "in-place merge invariant: cursors converge at dirty exhaustion");
        return write + (cleanEnd - read);
    }

    /// <summary>
    /// The first index in the sorted clean run <paramref name="clean"/>[<paramref name="lo"/>,
    /// <paramref name="hi"/>) whose element FOLLOWS <paramref name="dirtyRow"/> (the comparison is
    /// a total order, so no distinct pair compares equal; a 0 outcome — the row somehow meeting
    /// itself — keeps the old merge's clean-first arbitration). Exponential probe from
    /// <paramref name="lo"/> bracketing the boundary, then binary refinement of the bracketed
    /// window — the TimSort gallop shape, O(log gap) comparisons regardless of gap size.
    /// </summary>
    /// <remarks>
    /// Boundary discipline: the probe never reads past <paramref name="hi"/> (offsets clamp to the
    /// exclusive bound), an empty run returns <paramref name="lo"/> with zero comparisons (the
    /// clean-exhausted drain), and a dirty element preceding the whole run answers in ONE
    /// comparison (the clustered / adjacent-insertion fast path — successive dirty rows landing in
    /// the same gap each pay a single compare, exactly like the old merge). When
    /// <paramref name="linearFirst"/> is set (the previous gap was &lt; <see cref="GallopEntryGap"/>),
    /// the first probes step +1 instead of doubling — see <see cref="GallopEntryGap"/> for why that
    /// caps the dense-K regime at the old merge's comparison count.
    /// </remarks>
    private static int FindInsertionPoint(
        int dirtyRow, int[] clean, int lo, int hi, bool linearFirst, Comparison<int> comparison)
    {
        if (lo == hi)
            return lo;

        if (comparison(dirtyRow, clean[lo]) < 0)
            return lo; // clustered fast path: precedes the whole remaining run

        // Invariant: clean[lo + last] precedes dirtyRow; hunt an offset whose element follows it.
        int max = hi - lo; // exclusive offset bound
        int last = 0, offset = 1;
        while (offset < max && comparison(dirtyRow, clean[lo + offset]) >= 0)
        {
            last = offset;
            offset = linearFirst && offset < GallopEntryGap ? offset + 1 : (offset << 1) + 1;
            if (offset <= 0)
                offset = max; // overflow guard (mirrors ShapingSort's gallops)
        }
        if (offset > max)
            offset = max;

        // Binary-refine (last, offset]: clean[lo + last] precedes dirtyRow, clean[lo + offset]
        // follows it (or offset == max — the run may end inside the window). Linear stepping
        // leaves an empty window (last == offset - 1) — no comparisons spent here.
        int left = last + 1, right = offset;
        while (left < right)
        {
            int mid = (left + right) >>> 1;
            if (comparison(dirtyRow, clean[lo + mid]) < 0)
                right = mid;
            else
                left = mid + 1;
        }
        return lo + left;
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
