using System.Buffers;
using System.Text;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Rendering;

public class FrameRendererDirtyRegionTests
{
    private static string Render(FrameRenderer renderer, CellBuffer back)
    {
        var w = new ArrayBufferWriter<byte>();
        renderer.Render(back, w);
        return Encoding.UTF8.GetString(w.WrittenSpan);
    }

    [Fact]
    public void EmptyDirtyRegions_FallBackToFullDiff()
    {
        // No MarkDirty calls — current behavior preserved: every cell diff'd against front.
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 2);
        buffer.Set(0, 0, "a", CellStyle.Default);
        buffer.Set(4, 1, "z", CellStyle.Default);

        var output = Render(r, buffer);

        Assert.Contains("a", output);
        Assert.Contains("z", output);
    }

    // Dirty-region-exclusive emission is opt-in via FrameRendererOptions.RestrictToDirtyRegions.
    private static FrameRenderer RestrictedRenderer()
        => new(new FrameRendererOptions(RestrictToDirtyRegions: true));

    [Fact]
    public void DirtyRegionsIgnored_ByDefault_FullDiff()
    {
        // Default renderer (RestrictToDirtyRegions off): dirty marks are ignored for emission, so
        // a changed cell OUTSIDE any marked region is still emitted. This is what keeps a stray
        // MarkDirty (e.g. from RemoveFragment) from silently dropping unrelated changed cells.
        var r = new FrameRenderer();
        var buffer = new CellBuffer(10, 3);
        Render(r, buffer);

        buffer.Set(1, 0, "I", CellStyle.Default);
        buffer.Set(8, 0, "O", CellStyle.Default);
        buffer.MarkDirty(new Rect(column: 0, row: 0, columns: 3, rows: 1));

        var output = Render(r, buffer);

        Assert.Contains("I", output);
        Assert.Contains("O", output); // outside the region, but full diff emits it anyway
    }

    [Fact]
    public void DirtyRegion_OnlyAffectsCellsInsideRegion()
    {
        var r = RestrictedRenderer();
        var buffer = new CellBuffer(10, 3);
        // Initial state — all blank.
        Render(r, buffer);

        // Paint a cell inside the dirty region and one outside; only the marked one should emit.
        buffer.Set(1, 0, "I", CellStyle.Default);
        buffer.Set(8, 0, "O", CellStyle.Default);
        buffer.MarkDirty(new Rect(column: 0, row: 0, columns: 3, rows: 1));

        var output = Render(r, buffer);

        Assert.Contains("I", output);
        // The 'O' cell is outside the dirty region — caller's responsibility to have marked
        // it; absent that mark, the renderer skips it.
        Assert.DoesNotContain("O", output);
    }

    [Fact]
    public void OverlappingRegions_HandledCorrectly()
    {
        var r = RestrictedRenderer();
        var buffer = new CellBuffer(10, 2);
        Render(r, buffer);

        buffer.Set(1, 0, "X", CellStyle.Default);
        buffer.Set(5, 0, "Y", CellStyle.Default);
        // Two overlapping regions covering both X (col 1) and Y (col 5).
        buffer.MarkDirty(new Rect(0, 0, 4, 1));
        buffer.MarkDirty(new Rect(3, 0, 4, 1));

        var output = Render(r, buffer);

        Assert.Contains("X", output);
        Assert.Contains("Y", output);
    }

    [Fact]
    public void DirtyRegions_AutoClearedAfterRender()
    {
        // Renderer takes ownership of consuming dirty regions — caller doesn't need to call
        // ClearDirty manually between frames.
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 1);
        buffer.MarkDirty(new Rect(0, 0, 3, 1));
        Assert.NotEmpty(buffer.DirtyRegions);

        Render(r, buffer);

        Assert.Empty(buffer.DirtyRegions);
    }

    [Fact]
    public void RegionOutsideBuffer_Clamped()
    {
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 2);
        Render(r, buffer);

        buffer.Set(0, 0, "X", CellStyle.Default);
        // Region anchored within bounds but extending past the right edge — clamped.
        buffer.MarkDirty(new Rect(0, 0, 100, 100));

        var output = Render(r, buffer);
        Assert.Contains("X", output);

        // Marking a region entirely outside the buffer is a no-op.
        buffer.MarkDirty(new Rect(50, 50, 5, 5));
        Assert.Empty(buffer.DirtyRegions); // auto-cleared from last render
    }

    [Fact]
    public void EmptyRect_NotRecorded()
    {
        var buffer = new CellBuffer(5, 2);
        buffer.MarkDirty(new Rect(0, 0, 0, 5)); // zero width
        buffer.MarkDirty(new Rect(0, 0, 5, 0)); // zero height
        Assert.Empty(buffer.DirtyRegions);
    }

    [Fact]
    public void MarkDirty_ConvenienceOverload()
    {
        var buffer = new CellBuffer(5, 2);
        buffer.MarkDirty(0, 0, 3, 1);
        Assert.Single(buffer.DirtyRegions);
        Assert.Equal(new Rect(0, 0, 3, 1), buffer.DirtyRegions[0]);
    }

    [Fact]
    public void CellInsideRegion_StillDiffAgainstFront()
    {
        // Inside a dirty region, the renderer still does back-vs.-front comparison — a cell
        // that's marked dirty but actually unchanged doesn't re-emit.
        var r = RestrictedRenderer();
        var buffer = new CellBuffer(5, 1);
        buffer.Set(0, 0, "S", CellStyle.Default);
        Render(r, buffer);

        // Second render with the same content — but the region is marked dirty.
        buffer.MarkDirty(new Rect(0, 0, 5, 1));
        var output = Render(r, buffer);

        // 'S' was unchanged — no re-emission, even though the region was marked dirty.
        Assert.DoesNotContain("S", output);
    }

    [Fact]
    public void Clear_ClearsDirtyRegions()
    {
        var buffer = new CellBuffer(5, 1);
        buffer.MarkDirty(new Rect(0, 0, 3, 1));
        buffer.Clear();
        Assert.Empty(buffer.DirtyRegions);
    }

    [Fact]
    public void Resize_ClearsDirtyRegions()
    {
        var buffer = new CellBuffer(5, 1);
        buffer.MarkDirty(new Rect(0, 0, 3, 1));
        buffer.Resize(10, 2);
        Assert.Empty(buffer.DirtyRegions);
    }
}
