using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Drawing;

public class ScenePoolTests
{
    [Fact]
    public void Rent_AfterReturn_ReusesBuffer_ClearedToTransparent()
    {
        var pool = new ScenePool();

        // Rent, paint it red, return it to the pool.
        var first = pool.Rent(2, 1);
        // ReSharper disable once AccessToDisposedClosure
        first.Draw(ctx => ctx.FillRectangle(first.Bounds, new SolidColorBrush(Color.FromRgb(255, 0, 0))));
        first.Dispose();

        // Rent again at the same size → the recycled buffer must come back TRANSPARENT, not red.
        var second = pool.Rent(2, 1);

        var buffer = new CellBuffer(2, 1);
        var view = buffer.AsView();
        new SceneCompositor(Style.Default.WithBackground(Color.FromRgb(0, 0, 255)))
            .Composite(new[] { new SceneLayer(second) }, view);

        // The reused (undrawn) scene is transparent, so the blue base shows — the old red is gone.
        Assert.Equal(Color.FromRgb(0, 0, 255), buffer[0, 0].Style.Background);
    }

    [Fact]
    public void Rent_DifferentSize_AllocatesFresh_AndKeepsTheOtherSizePooled()
    {
        // Size-bucketed pool: a size miss never resizes a recycled buffer — it allocates fresh and
        // leaves the other size's buffer pooled for its own next rent.
        var pool = new ScenePool();

        var first = pool.Rent(2, 1);
        var firstBuffer = first.Buffer;
        first.Dispose();

        var second = pool.Rent(5, 3);
        Assert.Equal(5, second.Columns);
        Assert.Equal(3, second.Rows);
        Assert.NotSame(firstBuffer, second.Buffer);

        // The 2×1 buffer is still pooled under its own bucket.
        var third = pool.Rent(2, 1);
        Assert.Same(firstBuffer, third.Buffer);
    }

    [Fact]
    public void Rent_ExactSize_ReusesTheSameBufferInstance()
    {
        var pool = new ScenePool();

        var first = pool.Rent(10, 4);
        var buffer = first.Buffer;
        first.Dispose();

        var second = pool.Rent(10, 4);
        Assert.Same(buffer, second.Buffer);
    }

    [Fact]
    public void Buckets_PoolIndependently_PerExactSize()
    {
        var pool = new ScenePool();

        var a = pool.Rent(10, 5);
        var b = pool.Rent(20, 10);
        var bufferA = a.Buffer;
        var bufferB = b.Buffer;
        a.Dispose();
        b.Dispose();

        // Each size gets its own buffer back regardless of return order.
        Assert.Same(bufferB, pool.Rent(20, 10).Buffer);
        Assert.Same(bufferA, pool.Rent(10, 5).Buffer);
    }

    [Fact]
    public void RetentionCap_EvictsFromTheLeastRecentlyUsedBucket()
    {
        var pool = new ScenePool(maxRetainedBuffers: 2);

        // Return three distinct sizes; LRU order of last use: 2×1 (oldest), 3×1, 4×1.
        var a = pool.Rent(2, 1);
        var b = pool.Rent(3, 1);
        var c = pool.Rent(4, 1);
        var bufferB = b.Buffer;
        var bufferC = c.Buffer;
        a.Dispose();
        b.Dispose();
        c.Dispose(); // over cap → the 2×1 bucket (least recently used) is evicted

        Assert.Equal(2, pool.RetainedBufferCount);
        Assert.Same(bufferB, pool.Rent(3, 1).Buffer);
        Assert.Same(bufferC, pool.Rent(4, 1).Buffer);
    }

    [Fact]
    public void RetentionCap_RentRefreshesABucketsRecency()
    {
        var pool = new ScenePool(maxRetainedBuffers: 2);

        var a = pool.Rent(2, 1);
        var b = pool.Rent(3, 1);
        var bufferA = a.Buffer;
        a.Dispose();
        b.Dispose();

        // Touch the 2×1 bucket (rent + return) — it becomes the most recently used.
        pool.Rent(2, 1).Dispose();

        // The third size now evicts the 3×1 bucket, not the freshly-touched 2×1.
        pool.Rent(4, 1).Dispose();

        Assert.Equal(2, pool.RetainedBufferCount);
        Assert.Same(bufferA, pool.Rent(2, 1).Buffer);
    }

    [Fact]
    public void RetentionCap_SingleOverRetainedSize_DropsItsOwnExcess()
    {
        var pool = new ScenePool(maxRetainedBuffers: 2);

        var scenes = new[] { pool.Rent(6, 2), pool.Rent(6, 2), pool.Rent(6, 2), pool.Rent(6, 2) };
        foreach (var scene in scenes)
            scene.Dispose();

        Assert.Equal(2, pool.RetainedBufferCount);
    }

    [Fact]
    public void MixedSizeChurn_NeverRetainsBeyondTheCap()
    {
        var pool = new ScenePool(maxRetainedBuffers: 3);

        // Interleaved rent/return across a rotating set of sizes — the retained count must never
        // exceed the cap at any observation point.
        for (var i = 0; i < 64; i++)
        {
            var x = pool.Rent(2 + i % 5, 1 + i % 3);
            var y = pool.Rent(4 + i % 2, 2);
            x.Dispose();
            Assert.InRange(pool.RetainedBufferCount, 0, 3);
            y.Dispose();
            Assert.InRange(pool.RetainedBufferCount, 0, 3);
        }
    }

    [Fact]
    public void ContinuousSizeChurn_SweepsEmptyBucketMetadata()
    {
        // Empty buckets are retained for the steady-state zero-alloc return, but a long-lived app
        // under continuous size churn (an animated resize sweeping one cell per frame) must not
        // accrete a bucket entry per distinct size forever — eviction sweeps the empties once the
        // table outgrows the working set (> 4 × cap).
        var pool = new ScenePool(maxRetainedBuffers: 2);

        for (var i = 0; i < 500; i++)
            pool.Rent(1 + i, 1).Dispose(); // every size distinct — each return evicts a cold buffer

        // Without the sweep this is 500; with it, the table stays within the sweep threshold plus
        // the (≤ cap) non-empty buckets surviving each sweep.
        Assert.InRange(pool.BucketCountInternal, 1, 4 * 2 + 2);
        Assert.InRange(pool.RetainedBufferCount, 0, 2);

        // The surviving (most recent) size still re-rents from the pool.
        var last = pool.Rent(500, 1);
        Assert.Equal(500, last.Columns);
    }

    [Fact]
    public void SteadyStateReRent_AllocatesOnlyTheSceneWrapper()
    {
        // The point of bucketing: a steady-state frame loop (rent the same per-zone sizes, return
        // them) must not allocate buffers. The per-rent Scene wrapper object is the only allocation;
        // a CellBuffer for even the smallest size here would blow the per-cycle budget by orders of
        // magnitude.
        var pool = new ScenePool();

        static void Cycle(ScenePool pool)
        {
            var a = pool.Rent(80, 24);
            var b = pool.Rent(40, 12);
            var c = pool.Rent(1, 1);
            a.Dispose();
            b.Dispose();
            c.Dispose();
        }

        for (var i = 0; i < 256; i++)
            Cycle(pool); // warm up: buckets created, stacks grown, JIT settled

        const int iterations = 1_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            Cycle(pool);
        var perCycle = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iterations;

        // 3 Scene instances per cycle ≈ 3 × ~56 bytes; 512 leaves headroom for object-header /
        // alignment drift while still failing loudly on any buffer (multi-KB) allocation.
        Assert.True(perCycle <= 512, $"Steady-state re-rent allocated {perCycle:F1} bytes/cycle (expected wrapper-only).");
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(ushort.MaxValue + 1, 1)]
    [InlineData(1, ushort.MaxValue + 1)]
    public void Rent_InvalidDimensions_Throws(int columns, int rows)
    {
        var pool = new ScenePool();
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(columns, rows));
    }

    [Fact]
    public void Constructor_NonPositiveCap_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScenePool(maxRetainedBuffers: 0));
    }

    [Fact]
    public void DoubleDispose_DoesNotAliasBufferAcrossRents()
    {
        // P1-3: a double dispose must not push the buffer twice, or two later rents would hand the
        // same backing buffer to two callers (silent aliasing).
        var pool = new ScenePool();

        var a = pool.Rent(1, 1);
        a.Dispose();
        a.Dispose();   // idempotent

        var b = pool.Rent(1, 1);
        b.Draw(ctx => ctx.FillRectangle(b.Bounds, new SolidColorBrush(Color.FromRgb(255, 0, 0))));
        var c = pool.Rent(1, 1);
        c.Draw(ctx => ctx.FillRectangle(c.Bounds, new SolidColorBrush(Color.FromRgb(0, 0, 255))));

        // If b and c aliased one buffer, c's blue would have overwritten b. Composite b → still red.
        var buffer = new CellBuffer(1, 1);
        new SceneCompositor(Style.Default.WithBackground(Color.FromRgb(0, 0, 0)))
            .Composite(new[] { new SceneLayer(b) }, buffer.AsView());
        Assert.Equal(Color.FromRgb(255, 0, 0), buffer[0, 0].Style.Background);
    }

    [Fact]
    public void Draw_AfterDispose_Throws()
    {
        var pool = new ScenePool();
        var scene = pool.Rent(1, 1);
        scene.Dispose();

        Assert.Throws<ObjectDisposedException>(() => scene.Draw(_ => { }));
    }
}
