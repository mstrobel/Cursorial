using System.Buffers;
using System.Globalization;

namespace Cursorial.UI.DataViews.Shaping;

/// <summary>
/// The per-column collation-key blob for culture-mode string sorting (design doc §2.2 panel
/// amendment): culture/case-insensitive comparison through ICU costs 100–300 ns per compare — a
/// 1M-row sort would take seconds. Instead, <see cref="ShapedColumn{TRow,TKey}.ExtractKey"/> also
/// runs the string through <see cref="CompareInfo.GetSortKey(ReadOnlySpan{char}, Span{byte}, CompareOptions)"/>
/// into ONE pooled byte blob with an (offset, length) pair per slot, and the compiled comparer
/// compares the spans ordinally (<see cref="System.MemoryExtensions.SequenceCompareTo{T}(ReadOnlySpan{T}, ReadOnlySpan{T})"/>
/// — SIMD memcmp): culture order at ordinal speed. Ordinal/OrdinalIgnoreCase columns never build a
/// store (<c>string.CompareOrdinal</c> is already memcmp-speed); memory cost is
/// string-length-order bytes per row, only for culture-mode string columns (documented §2.2).
///
/// Threading rides §2.6 invariant 1 automatically: the blob is written ONLY inside key extraction,
/// which the controller runs only on the owner thread and never while a shape is in flight — so
/// growth/compaction re-allocation can never tear an in-flight background sort.
/// </summary>
internal sealed class CollationKeyStore
{
    // Read via Expression.Field by the compiled comparer (the key-vector rule — see
    // ShapedColumn<TRow,TKey>.Keys): growth/compaction re-allocates these, so a compiled tree must
    // re-read them through the store constant per invocation, never capture the arrays.
    internal byte[] Blob = [];
    internal int[] Offsets = [];

    /// <summary>Per-slot sort-key byte count; <b>−1 flags a null string</b> (nulls sort FIRST — the
    /// column-comparison convention). 0 is both "empty string" and "never extracted"; harmless —
    /// views only ever compare live, extracted slots.</summary>
    internal int[] Lengths = [];

    private readonly CompareInfo _compareInfo;
    private readonly CompareOptions _options;

    private int _used;       // append cursor: bytes appended since the last compaction (live + dead)
    private int _deadBytes;  // bytes leaked by re-extraction rewrites (reclaimed by compaction)

    /// <summary>Compaction-pass count (test probe — the §2.2 "periodic compaction" contract).</summary>
    internal int CompactionCount;

    /// <summary>The append cursor (test probe — bounded-growth assertions).</summary>
    internal int UsedBytes => _used;

    /// <summary>Bytes currently leaked by rewrites (test probe).</summary>
    internal int DeadBytes => _deadBytes;

    public CollationKeyStore(StringComparison comparison)
    {
        // The culture is captured at column build time — parity with CreateKeyComparison's
        // StringComparer.FromComparison, which snapshots CurrentCulture at creation. A mid-run
        // culture change requires rebuilding the columns either way.
        (_compareInfo, _options) = comparison switch
        {
            StringComparison.CurrentCulture => (CultureInfo.CurrentCulture.CompareInfo, CompareOptions.None),
            StringComparison.CurrentCultureIgnoreCase => (CultureInfo.CurrentCulture.CompareInfo, CompareOptions.IgnoreCase),
            StringComparison.InvariantCulture => (CultureInfo.InvariantCulture.CompareInfo, CompareOptions.None),
            StringComparison.InvariantCultureIgnoreCase => (CultureInfo.InvariantCulture.CompareInfo, CompareOptions.IgnoreCase),
            _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison,
                     "Ordinal modes never build a collation store (§2.2 — ordinal compare is already memcmp-speed)."),
        };
    }

    /// <summary>Whether <paramref name="comparison"/> routes through ICU — the modes the store exists for.</summary>
    public static bool IsCultureBased(StringComparison comparison)
        => comparison is StringComparison.CurrentCulture or StringComparison.CurrentCultureIgnoreCase
                      or StringComparison.InvariantCulture or StringComparison.InvariantCultureIgnoreCase;

    /// <summary>Ensures the per-slot range arrays cover [0, <paramref name="capacity"/>) — the
    /// column's <c>EnsureCapacity</c> twin (slot-aligned with the key vector).</summary>
    public void EnsureCapacity(int capacity)
    {
        if (Offsets.Length < capacity)
        {
            int grown = Math.Max(capacity, Math.Max(16, Offsets.Length * 2));
            Array.Resize(ref Offsets, grown);
            Array.Resize(ref Lengths, grown);
        }
    }

    /// <summary>
    /// (Re-)extracts the collation key for <paramref name="slot"/>: a rewrite APPENDS the new sort
    /// key and leaks the old range until compaction (§2.2 "append + periodic compaction" — ranges
    /// are never edited in place, so a published view's compares stay coherent between extractions).
    /// The ONLY blob write site; called only from <see cref="ShapedColumn{TRow,TKey}.ExtractKey"/>.
    /// </summary>
    public void Extract(string? value, int slot)
    {
        int oldLength = Lengths[slot];
        if (oldLength > 0)
            _deadBytes += oldLength; // the rewrite orphans the old range (compaction reclaims)

        if (value is null)
        {
            Offsets[slot] = 0;
            Lengths[slot] = -1; // the null flag — nulls sort first in CompareSortKeys
            return;
        }

        // Lazy compaction inside extraction (the one site that is owner-thread + no-shape-in-flight
        // by the §2.6 invariant): rebuild over live ranges once dead bytes exceed half the appended
        // bytes. Correct-first; perf refinements recorded: a size floor (skip below a few KiB) to
        // avoid churn on tiny blobs, and in-place sliding compaction to avoid the fresh rent.
        if (_deadBytes * 2 > _used)
            Compact();

        int needed = _compareInfo.GetSortKeyLength(value, _options);
        EnsureBlob(_used + needed);
        int written = _compareInfo.GetSortKey(value, Blob.AsSpan(_used, needed), _options);
        Offsets[slot] = _used;
        Lengths[slot] = written;
        _used += written;
    }

    /// <summary>The instance compare over two slots' sort keys (the <c>CompareSlots</c> leg —
    /// grouping boundaries/Min-Max must match sort order, §2.5).</summary>
    public int CompareSlots(int a, int b) => CompareSortKeys(Blob, Offsets, Lengths, a, b);

    /// <summary>
    /// The static compare the compiled sort comparer calls (§2.2): expression trees cannot hold
    /// <see cref="Span{T}"/> locals, but they CAN call a static method that does. The arrays arrive
    /// as arguments read through <c>Expression.Field</c> on the store constant per invocation, so
    /// blob growth/compaction re-allocation is always observed. Null flags (length −1) sort first;
    /// otherwise an ordinal span compare — the ICU sort-key contract makes that exactly culture order.
    /// </summary>
    internal static int CompareSortKeys(byte[] blob, int[] offsets, int[] lengths, int a, int b)
    {
        int lengthA = lengths[a];
        int lengthB = lengths[b];
        if (lengthA < 0)
            return lengthB < 0 ? 0 : -1;
        if (lengthB < 0)
            return 1;
        return blob.AsSpan(offsets[a], lengthA).SequenceCompareTo(blob.AsSpan(offsets[b], lengthB));
    }

    private void EnsureBlob(int capacity)
    {
        if (Blob.Length >= capacity)
            return;

        // Grow-only pooled blob: rent the successor, copy the live prefix, return the old array.
        // Safe under §2.6 invariant 1 — nothing reads the old array once the field is swapped
        // (compiled comparers re-read the field per call; no shape is in flight during extraction).
        var fresh = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, Math.Max(256, Blob.Length * 2)));
        Blob.AsSpan(0, _used).CopyTo(fresh);
        if (Blob.Length > 0)
            ArrayPool<byte>.Shared.Return(Blob);
        Blob = fresh;
    }

    /// <summary>
    /// Repacks every live range (slot order) into a fresh pooled buffer and resets the dead count.
    /// Freed row-store slots still hold their last range — the store cannot see row liveness, so
    /// they repack too; their bytes reclaim when slot reuse re-extracts them (correct-first; a
    /// live-slot mask from the controller is a recorded refinement).
    /// </summary>
    private void Compact()
    {
        int live = _used - _deadBytes;
        var fresh = ArrayPool<byte>.Shared.Rent(Math.Max(live, 256));

        int write = 0;
        for (int slot = 0; slot < Lengths.Length; slot++)
        {
            int length = Lengths[slot];
            if (length <= 0)
            {
                // Null (−1), empty, or never-extracted: no bytes — pin the offset to 0 so a
                // zero-length AsSpan stays in-bounds against the (possibly smaller) fresh buffer.
                Offsets[slot] = 0;
                continue;
            }

            Blob.AsSpan(Offsets[slot], length).CopyTo(fresh.AsSpan(write));
            Offsets[slot] = write;
            write += length;
        }

        if (Blob.Length > 0)
            ArrayPool<byte>.Shared.Return(Blob);
        Blob = fresh;
        _used = write;
        _deadBytes = 0;
        CompactionCount++;
    }
}
