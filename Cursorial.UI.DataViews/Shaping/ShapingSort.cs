namespace Cursorial.UI.DataViews.Shaping;

/// <summary>
/// The engine's adaptive stable sort — a TimSort over an <see cref="int"/> permutation with an
/// arbitrary <see cref="Comparison{T}"/> (design doc §2.3). Chosen over <see cref="Array.Sort{T}"/>'s
/// introsort because the shaping profile is dominated by <em>mostly-sorted</em> input (a re-sort
/// after a burst of live-update repairs, a direction flip, a secondary-column add), where natural-run
/// detection is O(N) instead of O(N log N); pure-random input pays a small constant over introsort
/// (benchmarked in <c>SortBenchmark</c> — the recorded table drives any future hybrid).
/// </summary>
/// <remarks>
/// <para>
/// The comparison the engine feeds this is a <b>total order</b> (the compiled multi-column comparer
/// ends with the row-id tiebreak, doc §2.2), so stability is moot — but TimSort is stable anyway,
/// which keeps the algorithm honest under test oracles that assert stable semantics directly.
/// </para>
/// <para>
/// This is the classic algorithm (Peters' listsort; the CPython/OpenJDK shape): natural runs
/// (strictly-descending runs reversed in place), min-run binary-insertion extension, a run stack
/// with the (post-2015 corrected) invariants, merges with galloping mode entered after
/// <see cref="MinGallop"/> consecutive wins. Scratch memory comes from a caller-owned
/// <see cref="SortScratch"/> so steady-state shaping allocates nothing.
/// </para>
/// </remarks>
internal static class ShapingSort
{
    /// <summary>Runs shorter than this are extended to min-run via binary insertion (the TimSort constant).</summary>
    private const int MinMerge = 32;

    /// <summary>Initial/reset threshold of consecutive one-side wins before a merge enters galloping mode.</summary>
    private const int MinGallop = 7;

    /// <summary>
    /// Sorts <paramref name="keys"/>[0..<paramref name="length"/>) in place by <paramref name="comparison"/>.
    /// </summary>
    public static void Sort(int[] keys, int length, Comparison<int> comparison, SortScratch scratch)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)length, (uint)keys.Length, nameof(length));

        if (length < 2)
            return;

        if (length < MinMerge)
        {
            // One binary-insertion pass over the whole (tiny) range, seeded by its leading run.
            int initial = CountRunAndMakeAscending(keys, 0, length, comparison);
            BinaryInsertionSort(keys, 0, length, initial, comparison);
            return;
        }

        var state = new MergeState(keys, comparison, scratch);
        int minRun = ComputeMinRun(length);
        int lo = 0, remaining = length;

        do
        {
            int runLength = CountRunAndMakeAscending(keys, lo, lo + remaining, comparison);

            if (runLength < minRun)
            {
                int force = Math.Min(remaining, minRun);
                BinaryInsertionSort(keys, lo, lo + force, lo + runLength, comparison);
                runLength = force;
            }

            state.PushRun(lo, runLength);
            state.MergeCollapse();

            lo += runLength;
            remaining -= runLength;
        }
        while (remaining > 0);

        state.MergeForceCollapse();
    }

    /// <summary>
    /// The TimSort min-run: length / 2^k in [16, 32], rounding up when any shifted-out bit was set.
    /// </summary>
    private static int ComputeMinRun(int length)
    {
        int r = 0;
        while (length >= MinMerge)
        {
            r |= length & 1;
            length >>= 1;
        }
        return length + r;
    }

    /// <summary>
    /// Measures the run starting at <paramref name="lo"/> (exclusive bound <paramref name="hi"/>),
    /// reversing an initially strictly-descending run in place. Returns the run length. Strictness
    /// on the descending arm preserves stability (equal elements never reverse).
    /// </summary>
    private static int CountRunAndMakeAscending(int[] keys, int lo, int hi, Comparison<int> comparison)
    {
        int runHi = lo + 1;
        if (runHi == hi)
            return 1;

        if (comparison(keys[runHi++], keys[lo]) < 0)
        {
            while (runHi < hi && comparison(keys[runHi], keys[runHi - 1]) < 0)
                runHi++;
            Array.Reverse(keys, lo, runHi - lo);
        }
        else
        {
            while (runHi < hi && comparison(keys[runHi], keys[runHi - 1]) >= 0)
                runHi++;
        }

        return runHi - lo;
    }

    /// <summary>
    /// Sorts [<paramref name="lo"/>, <paramref name="hi"/>) by binary insertion, with
    /// [<paramref name="lo"/>, <paramref name="start"/>) already sorted.
    /// </summary>
    private static void BinaryInsertionSort(int[] keys, int lo, int hi, int start, Comparison<int> comparison)
    {
        if (start == lo)
            start++;

        for (; start < hi; start++)
        {
            int pivot = keys[start];

            // Invariant: [left, right) is the insertion window; pivot lands after any equal elements
            // (right-most insertion point) for stability.
            int left = lo, right = start;
            while (left < right)
            {
                int mid = (left + right) >>> 1;
                if (comparison(pivot, keys[mid]) < 0)
                    right = mid;
                else
                    left = mid + 1;
            }

            int move = start - left;
            if (move > 0)
                Array.Copy(keys, left, keys, left + 1, move);
            keys[left] = pivot;
        }
    }

    // ── Galloping searches (shared by the merges) ────────────────────────────────────────────────

    /// <summary>
    /// The left-most position in sorted [<paramref name="baseIndex"/>, +<paramref name="length"/>) at which
    /// <paramref name="key"/> belongs (gallop-left: before any equal run), probing outward from
    /// <paramref name="hint"/>.
    /// </summary>
    private static int GallopLeft(int key, int[] keys, int baseIndex, int length, int hint, Comparison<int> comparison)
    {
        int lastOffset = 0, offset = 1;

        if (comparison(key, keys[baseIndex + hint]) > 0)
        {
            // Gallop right of the hint.
            int maxOffset = length - hint;
            while (offset < maxOffset && comparison(key, keys[baseIndex + hint + offset]) > 0)
            {
                lastOffset = offset;
                offset = (offset << 1) + 1;
                if (offset <= 0)
                    offset = maxOffset;
            }
            if (offset > maxOffset)
                offset = maxOffset;

            lastOffset += hint;
            offset += hint;
        }
        else
        {
            // Gallop left of the hint.
            int maxOffset = hint + 1;
            while (offset < maxOffset && comparison(key, keys[baseIndex + hint - offset]) <= 0)
            {
                lastOffset = offset;
                offset = (offset << 1) + 1;
                if (offset <= 0)
                    offset = maxOffset;
            }
            if (offset > maxOffset)
                offset = maxOffset;

            (lastOffset, offset) = (hint - offset, hint - lastOffset);
        }

        // Binary search the bracketed window: keys[lastOffset] < key <= keys[offset].
        lastOffset++;
        while (lastOffset < offset)
        {
            int mid = lastOffset + ((offset - lastOffset) >>> 1);
            if (comparison(key, keys[baseIndex + mid]) > 0)
                lastOffset = mid + 1;
            else
                offset = mid;
        }
        return offset;
    }

    /// <summary>
    /// The right-most position in sorted [<paramref name="baseIndex"/>, +<paramref name="length"/>) at which
    /// <paramref name="key"/> belongs (gallop-right: after any equal run), probing outward from
    /// <paramref name="hint"/>.
    /// </summary>
    private static int GallopRight(int key, int[] keys, int baseIndex, int length, int hint, Comparison<int> comparison)
    {
        int lastOffset = 0, offset = 1;

        if (comparison(key, keys[baseIndex + hint]) < 0)
        {
            // Gallop left of the hint.
            int maxOffset = hint + 1;
            while (offset < maxOffset && comparison(key, keys[baseIndex + hint - offset]) < 0)
            {
                lastOffset = offset;
                offset = (offset << 1) + 1;
                if (offset <= 0)
                    offset = maxOffset;
            }
            if (offset > maxOffset)
                offset = maxOffset;

            (lastOffset, offset) = (hint - offset, hint - lastOffset);
        }
        else
        {
            // Gallop right of the hint.
            int maxOffset = length - hint;
            while (offset < maxOffset && comparison(key, keys[baseIndex + hint + offset]) >= 0)
            {
                lastOffset = offset;
                offset = (offset << 1) + 1;
                if (offset <= 0)
                    offset = maxOffset;
            }
            if (offset > maxOffset)
                offset = maxOffset;

            lastOffset += hint;
            offset += hint;
        }

        lastOffset++;
        while (lastOffset < offset)
        {
            int mid = lastOffset + ((offset - lastOffset) >>> 1);
            if (comparison(key, keys[baseIndex + mid]) < 0)
                offset = mid;
            else
                lastOffset = mid + 1;
        }
        return offset;
    }

    // ── Merge state (run stack + merges) ─────────────────────────────────────────────────────────

    /// <summary>The run stack + merge machinery for one <see cref="Sort"/> call.</summary>
    private ref struct MergeState
    {
        private readonly int[] _keys;
        private readonly Comparison<int> _comparison;
        private readonly SortScratch _scratch;
        private int _minGallop;

        private int _stackSize;
        private readonly int[] _runBase;
        private readonly int[] _runLength;

        public MergeState(int[] keys, Comparison<int> comparison, SortScratch scratch)
        {
            _keys = keys;
            _comparison = comparison;
            _scratch = scratch;
            _minGallop = MinGallop;
            _stackSize = 0;
            // 49/85 covers int.MaxValue elements (the OpenJDK bound); 64 is comfortably past it.
            _runBase = scratch.RentRunStack(out _runLength);
        }

        public void PushRun(int runBase, int runLength)
        {
            _runBase[_stackSize] = runBase;
            _runLength[_stackSize] = runLength;
            _stackSize++;
        }

        /// <summary>
        /// Restores the run-stack invariants (the corrected pair — checking the top FOUR runs, the
        /// de Gouw fix): len[i-3] > len[i-2] + len[i-1], len[i-2] > len[i-1].
        /// </summary>
        public void MergeCollapse()
        {
            while (_stackSize > 1)
            {
                int n = _stackSize - 2;

                if ((n > 0 && _runLength[n - 1] <= _runLength[n] + _runLength[n + 1]) ||
                    (n > 1 && _runLength[n - 2] <= _runLength[n - 1] + _runLength[n]))
                {
                    if (_runLength[n - 1] < _runLength[n + 1])
                        n--;
                    MergeAt(n);
                }
                else if (_runLength[n] <= _runLength[n + 1])
                {
                    MergeAt(n);
                }
                else
                {
                    break;
                }
            }
        }

        /// <summary>Collapses the whole stack at end of input.</summary>
        public void MergeForceCollapse()
        {
            while (_stackSize > 1)
            {
                int n = _stackSize - 2;
                if (n > 0 && _runLength[n - 1] < _runLength[n + 1])
                    n--;
                MergeAt(n);
            }
        }

        /// <summary>Merges stack runs i and i+1 (i is the top or one below it).</summary>
        private void MergeAt(int i)
        {
            int base1 = _runBase[i], len1 = _runLength[i];
            int base2 = _runBase[i + 1], len2 = _runLength[i + 1];

            _runLength[i] = len1 + len2;
            if (i == _stackSize - 3)
            {
                _runBase[i + 1] = _runBase[i + 2];
                _runLength[i + 1] = _runLength[i + 2];
            }
            _stackSize--;

            // Skip run-1 elements already in place (run-2's first element positions after them).
            int k = GallopRight(_keys[base2], _keys, base1, len1, 0, _comparison);
            base1 += k;
            len1 -= k;
            if (len1 == 0)
                return;

            // Skip run-2 elements already in place (past run-1's last element).
            len2 = GallopLeft(_keys[base1 + len1 - 1], _keys, base2, len2, len2 - 1, _comparison);
            if (len2 == 0)
                return;

            if (len1 <= len2)
                MergeLo(base1, len1, base2, len2);
            else
                MergeHi(base1, len1, base2, len2);
        }

        /// <summary>Merge with run 1 copied aside (len1 &lt;= len2), filling left-to-right.</summary>
        private void MergeLo(int base1, int len1, int base2, int len2)
        {
            int[] keys = _keys;
            int[] tmp = _scratch.RentMergeBuffer(len1);
            Array.Copy(keys, base1, tmp, 0, len1);

            int cursor1 = 0, cursor2 = base2, dest = base1;

            // First element of run 2 always wins first (MergeAt guaranteed it precedes run 1's remainder).
            keys[dest++] = keys[cursor2++];
            if (--len2 == 0)
            {
                Array.Copy(tmp, cursor1, keys, dest, len1);
                return;
            }
            if (len1 == 1)
            {
                Array.Copy(keys, cursor2, keys, dest, len2);
                keys[dest + len2] = tmp[cursor1];
                return;
            }

            var comparison = _comparison;
            int minGallop = _minGallop;

            while (true)
            {
                int count1 = 0, count2 = 0;

                // One-pair-at-a-time mode until a side streaks.
                bool exhausted = false;
                do
                {
                    if (comparison(keys[cursor2], tmp[cursor1]) < 0)
                    {
                        keys[dest++] = keys[cursor2++];
                        count2++;
                        count1 = 0;
                        if (--len2 == 0) { exhausted = true; break; }
                    }
                    else
                    {
                        keys[dest++] = tmp[cursor1++];
                        count1++;
                        count2 = 0;
                        if (--len1 == 1) { exhausted = true; break; }
                    }
                }
                while ((count1 | count2) < minGallop);
                if (exhausted)
                    break;

                // Galloping mode: bulk-copy streaks until both fall under MinGallop.
                do
                {
                    count1 = GallopRight(keys[cursor2], tmp, cursor1, len1, 0, comparison);
                    if (count1 != 0)
                    {
                        Array.Copy(tmp, cursor1, keys, dest, count1);
                        dest += count1;
                        cursor1 += count1;
                        len1 -= count1;
                        if (len1 <= 1) { exhausted = true; break; }
                    }
                    keys[dest++] = keys[cursor2++];
                    if (--len2 == 0) { exhausted = true; break; }

                    count2 = GallopLeft(tmp[cursor1], keys, cursor2, len2, 0, comparison);
                    if (count2 != 0)
                    {
                        Array.Copy(keys, cursor2, keys, dest, count2);
                        dest += count2;
                        cursor2 += count2;
                        len2 -= count2;
                        if (len2 == 0) { exhausted = true; break; }
                    }
                    keys[dest++] = tmp[cursor1++];
                    if (--len1 == 1) { exhausted = true; break; }

                    minGallop--;
                }
                while (count1 >= MinGallop || count2 >= MinGallop);
                if (exhausted)
                    break;

                if (minGallop < 0)
                    minGallop = 0;
                minGallop += 2; // penalize leaving galloping mode
            }

            _minGallop = minGallop < 1 ? 1 : minGallop;

            if (len1 == 1)
            {
                Array.Copy(keys, cursor2, keys, dest, len2);
                keys[dest + len2] = tmp[cursor1]; // the last run-1 element caps the merge
            }
            else if (len1 > 0)
            {
                Array.Copy(tmp, cursor1, keys, dest, len1);
            }
        }

        /// <summary>Merge with run 2 copied aside (len1 &gt; len2), filling right-to-left.</summary>
        private void MergeHi(int base1, int len1, int base2, int len2)
        {
            int[] keys = _keys;
            int[] tmp = _scratch.RentMergeBuffer(len2);
            Array.Copy(keys, base2, tmp, 0, len2);

            int cursor1 = base1 + len1 - 1;   // descending through run 1 in place
            int cursor2 = len2 - 1;           // descending through tmp (run 2)
            int dest = base2 + len2 - 1;

            // Last element of run 1 always wins first (MergeAt guaranteed it follows run 2's remainder).
            keys[dest--] = keys[cursor1--];
            if (--len1 == 0)
            {
                Array.Copy(tmp, 0, keys, dest - (len2 - 1), len2);
                return;
            }
            if (len2 == 1)
            {
                dest -= len1;
                cursor1 -= len1;
                Array.Copy(keys, cursor1 + 1, keys, dest + 1, len1);
                keys[dest] = tmp[cursor2];
                return;
            }

            var comparison = _comparison;
            int minGallop = _minGallop;

            while (true)
            {
                int count1 = 0, count2 = 0;

                bool exhausted = false;
                do
                {
                    if (comparison(tmp[cursor2], keys[cursor1]) < 0)
                    {
                        keys[dest--] = keys[cursor1--];
                        count1++;
                        count2 = 0;
                        if (--len1 == 0) { exhausted = true; break; }
                    }
                    else
                    {
                        keys[dest--] = tmp[cursor2--];
                        count2++;
                        count1 = 0;
                        if (--len2 == 1) { exhausted = true; break; }
                    }
                }
                while ((count1 | count2) < minGallop);
                if (exhausted)
                    break;

                do
                {
                    count1 = len1 - GallopRight(tmp[cursor2], keys, base1, len1, len1 - 1, comparison);
                    if (count1 != 0)
                    {
                        dest -= count1;
                        cursor1 -= count1;
                        len1 -= count1;
                        Array.Copy(keys, cursor1 + 1, keys, dest + 1, count1);
                        if (len1 == 0) { exhausted = true; break; }
                    }
                    keys[dest--] = tmp[cursor2--];
                    if (--len2 == 1) { exhausted = true; break; }

                    count2 = len2 - GallopLeft(keys[cursor1], tmp, 0, len2, len2 - 1, comparison);
                    if (count2 != 0)
                    {
                        dest -= count2;
                        cursor2 -= count2;
                        len2 -= count2;
                        Array.Copy(tmp, cursor2 + 1, keys, dest + 1, count2);
                        if (len2 <= 1) { exhausted = true; break; }
                    }
                    keys[dest--] = keys[cursor1--];
                    if (--len1 == 0) { exhausted = true; break; }

                    minGallop--;
                }
                while (count1 >= MinGallop || count2 >= MinGallop);
                if (exhausted)
                    break;

                if (minGallop < 0)
                    minGallop = 0;
                minGallop += 2;
            }

            _minGallop = minGallop < 1 ? 1 : minGallop;

            if (len2 == 1)
            {
                dest -= len1;
                cursor1 -= len1;
                Array.Copy(keys, cursor1 + 1, keys, dest + 1, len1);
                keys[dest] = tmp[cursor2]; // the first run-2 element floors the merge
            }
            else if (len2 > 0)
            {
                Array.Copy(tmp, 0, keys, dest - (len2 - 1), len2);
            }
        }
    }
}

/// <summary>
/// Caller-owned scratch memory for <see cref="ShapingSort"/> (and the repair merge): the run stack
/// and a grow-only merge buffer, reused across sorts so steady-state shaping never allocates.
/// Not thread-safe — one instance per shaping context (the background shaper owns its own).
/// </summary>
internal sealed class SortScratch
{
    private int[]? _runBase;
    private int[]? _runLength;
    private int[] _merge = [];

    internal int[] RentRunStack(out int[] runLength)
    {
        _runBase ??= new int[64];
        _runLength ??= new int[64];
        runLength = _runLength;
        return _runBase;
    }

    /// <summary>A merge buffer of at least <paramref name="minimumLength"/> (grow-only, geometric).</summary>
    internal int[] RentMergeBuffer(int minimumLength)
    {
        if (_merge.Length < minimumLength)
        {
            int size = Math.Max(minimumLength, Math.Max(16, _merge.Length * 2));
            _merge = new int[size];
        }
        return _merge;
    }
}
