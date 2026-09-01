using Cursorial.Rendering;

// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// Recycles <see cref="Scene"/> backing buffers so transient (per-frame) scenes don't churn
/// allocations. <see cref="Rent"/> reuses a freed buffer of the <b>exact same dimensions</b> when one
/// is available; <see cref="Scene.Dispose"/> returns the buffer here. Persistent (cached) scenes are
/// owner-held and created via <see cref="Scene.Create"/> instead.
/// </summary>
/// <remarks>
/// <para>
/// <b>Size-bucketed reuse.</b> Freed buffers are held in exact-dimension buckets rather than one
/// free list, because the dominant consumer (a UI render tree) rents per-zone scenes whose sizes are
/// stable frame over frame — an exact-size hit costs nothing, where the former single free list
/// resized (reallocated) the recycled buffer on nearly every rent. A size miss allocates fresh and
/// leaves other sizes pooled. Total retention is capped at <see cref="MaxRetainedBuffers"/>; when a
/// return exceeds the cap, a buffer is dropped from the <b>least-recently-used size bucket</b>
/// (linear scan — bucket counts are tens at most), so cold sizes age out and a resize storm cannot
/// pin the old dimensions' memory forever.
/// </para>
/// <para>
/// Not thread-safe — rent/return from a single render loop.
/// </para>
/// </remarks>
public sealed class ScenePool
{
    /// <summary>The default <see cref="MaxRetainedBuffers"/> (comfortably above "tens of zone layers").</summary>
    public const int DefaultMaxRetainedBuffers = 32;

    // Exact-dimension buckets. Empty buckets are deliberately kept so the steady-state rent → return
    // cycle touches only an existing Stack (zero allocation) — until the table outgrows the working
    // set, at which point eviction sweeps the empties (see EvictOne) so unbounded size churn cannot
    // accrete bucket metadata forever.
    private readonly IGraphemeCache _graphemeCache;
    private readonly Dictionary<SizeKey, Bucket> _buckets = [];
    private readonly int _maxRetainedBuffers;
    private int _retainedCount;
    private long _useStamp;

    // ReSharper disable NotAccessedPositionalProperty.Local
    private readonly record struct SizeKey(int Columns, int Rows);
    // ReSharper restore NotAccessedPositionalProperty.Local

    private sealed class Bucket
    {
        public readonly Stack<CellBuffer> Free = new();
        public long LastUse;
    }

    /// <summary>Creates a pool retaining at most <paramref name="maxRetainedBuffers"/> freed buffers.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxRetainedBuffers"/> is less than 1.</exception>
    public ScenePool(int maxRetainedBuffers = DefaultMaxRetainedBuffers, IGraphemeCache? graphemeCache = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRetainedBuffers, 1);
        _maxRetainedBuffers = maxRetainedBuffers;
        _graphemeCache = graphemeCache ?? IGraphemeCache.None;
    }

    /// <summary>The retention cap: the maximum number of freed buffers held for reuse.</summary>
    public int MaxRetainedBuffers => _maxRetainedBuffers;

    /// <summary>
    /// The number of freed buffers currently held for reuse — pool-health observability for a
    /// consumer that configured a non-default <see cref="MaxRetainedBuffers"/>. Always at most
    /// the cap (a return that would exceed it evicts in the same call).
    /// </summary>
    public int RetainedBufferCount => _retainedCount;

    /// <summary>Distinct size-bucket entries currently held, empty ones included (test observability).</summary>
    internal int BucketCountInternal => _buckets.Count;

    /// <summary>
    /// Rent a transparent scene of the given dimensions, reusing a pooled buffer of the exact same
    /// size when one is available (a size miss allocates fresh — see the class remarks).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A dimension is less than 1 or exceeds <see cref="ushort.MaxValue"/> (the <see cref="Rect"/>
    /// coordinate cap — same validation as <see cref="Scene.Create"/>).
    /// </exception>
    public Scene Rent(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, ushort.MaxValue);

        if (_buckets.TryGetValue(new SizeKey(columns, rows), out var bucket) && bucket.Free.TryPop(out var buffer))
        {
            _retainedCount--;
            bucket.LastUse = ++_useStamp;
            return new Scene(buffer, this);   // ctor re-clears to transparent
        }

        return new Scene(Scene.CreateBuffer(columns, rows, _graphemeCache), this);
    }

    internal void Return(Scene scene)
    {
        var buffer = scene.Buffer;
        var key = new SizeKey(buffer.Columns, buffer.Rows);

        if (!_buckets.TryGetValue(key, out var bucket))
            _buckets.Add(key, bucket = new Bucket());

        bucket.Free.Push(buffer);
        bucket.LastUse = ++_useStamp;
        _retainedCount++;

        if (_retainedCount > _maxRetainedBuffers)
            EvictOne();
    }

    // Once the bucket table has clearly outgrown the retainable working set, the empties are swept
    // (see EvictOne). 4× the cap keeps the steady-state zero-alloc property for live sizes while
    // bounding the metadata a long-lived app under continuous size churn can accrete.
    private const int BucketSweepFactor = 4;

    // Drop one buffer from the least-recently-used non-empty bucket. Same-size buffers are fungible,
    // so "which buffer" within the bucket is immaterial; the just-returned bucket carries the newest
    // stamp and is chosen only when it is the sole non-empty bucket (i.e. one size over-retained —
    // dropping its excess is exactly right).
    private void EvictOne()
    {
        Bucket? coldest = null;

        foreach (var bucket in _buckets.Values)
        {
            if (bucket.Free.Count > 0 && (coldest is null || bucket.LastUse < coldest.LastUse))
                coldest = bucket;
        }

        if (coldest is null)
            return; // unreachable while _retainedCount > 0; defensive

        coldest.Free.Pop();
        _retainedCount--;

        // Metadata hygiene: empty buckets are normally retained (the steady-state return then
        // allocates nothing), but unbounded size churn — say an animated resize sweeping one cell
        // per frame for hours — would otherwise accrete a Bucket + Stack per distinct size forever.
        // Sweeping only past the threshold preserves zero-alloc returns for every live size.
        if (_buckets.Count > BucketSweepFactor * _maxRetainedBuffers)
        {
            foreach (var (key, bucket) in _buckets)
            {
                if (bucket.Free.Count == 0)
                    _buckets.Remove(key); // supported mid-enumeration on Dictionary since .NET Core 3.0
            }
        }
    }
}
