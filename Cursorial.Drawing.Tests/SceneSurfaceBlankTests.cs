using System.Buffers;

using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fragments;
using Cursorial.Rendering.Media;

namespace Cursorial.Tests.Drawing;

// FragmentDictionary exposes Count + a struct enumerator but does not implement IEnumerable, so xUnit's
// Assert.Single / Assert.Empty do not apply to it — Assert.Equal(N, …Count) is the only idiomatic form.
#pragma warning disable xUnit2013

/// <summary>
/// A scene is an intermediate surface: unpainted cells must contribute nothing when it composites.
/// That is a property of the buffer's <b>blank</b>, not of one fill performed at construction — every
/// piece of code that blanks a cell of its own accord (the buffer's wide-pair hygiene, a view clear,
/// the clear extensions) asks <see cref="CellBuffer.DefaultStyle"/> what blank means on this surface,
/// and a buffer that was merely <em>filled</em> transparent answers <see cref="CellStyle.Default"/> — the
/// opaque value, which punches a hole straight through the group.
/// </summary>
public class SceneSurfaceBlankTests
{
    [Fact]
    public void SceneBuffer_DefaultStyle_IsTransparent()
    {
        using var scene = Scene.Create(6, 3);

        Assert.Equal(CellStyle.Transparent, scene.Buffer.DefaultStyle);
    }

    [Fact]
    public void PooledSceneBuffer_DefaultStyle_IsTransparent_OnFirstRentAndOnReuse()
    {
        var pool = new ScenePool();

        var first = pool.Rent(6, 3);
        Assert.Equal(CellStyle.Transparent, first.Buffer.DefaultStyle);
        first.Dispose();

        var reused = pool.Rent(6, 3);
        Assert.Equal(CellStyle.Transparent, reused.Buffer.DefaultStyle);
        reused.Dispose();
    }

    [Fact]
    public void SceneBuffer_ContentsAreTransparent_FromConstruction()
    {
        using var scene = Scene.Create(4, 2);

        for (int row = 0; row < scene.Rows; row++)
        for (int col = 0; col < scene.Columns; col++)
            Assert.Equal(CellStyle.Transparent, scene.GetCell(col, row).Style);
    }

    [Fact]
    public void SceneRaster_LeavesUnpaintedCellsTransparent()
    {
        using var scene = Scene.Create(6, 2);
        scene.Draw(ctx => ctx.FillOpaque(new Rect(0, 0, 2, 1), new BrushedStyle { Background = new SolidColorBrush(Color.FromRgb(10, 20, 30)) }));

        Assert.Equal(CellStyle.Transparent, scene.GetCell(5, 1).Style);
    }

    [Fact]
    public void ViewClearOnASceneSurface_BlanksToTransparent_NotToTheOpaqueDefault()
    {
        // The exact bug DefaultStyle exists to prevent: CellBufferView.Clear routes through
        // CellBuffer.ClearCells, which writes the buffer's blank. With a scene buffer that only got
        // FILLED transparent, that blank was Style.Default and the clear left an opaque rectangle
        // sitting in the middle of the group.
        using var scene = Scene.Create(6, 2);
        scene.Draw(ctx => ctx.FillOpaque(scene.Bounds, new BrushedStyle { Background = new SolidColorBrush(Color.FromRgb(10, 20, 30)) }));

        scene.Buffer.View(1, 0, 2, 1).Clear();

        Assert.Equal(CellStyle.Transparent, scene.GetCell(1, 0).Style);
        Assert.Equal(CellStyle.Transparent, scene.GetCell(2, 0).Style);
    }

    [Fact]
    public void WideOrphanHygieneOnASceneSurface_LeavesATransparentBlank()
    {
        using var scene = Scene.Create(6, 1);
        scene.Draw(ctx =>
                   {
                       ctx.Set(0, 0, "漢", CellStyle.Transparent);
                       ctx.Set(1, 0, "x", CellStyle.Transparent);   // stomps the continuation
                   });

        Assert.Equal(CellStyle.Transparent, scene.GetCell(0, 0).Style);
    }

    // ---- ClearToTransparent collapsed into CellBuffer.Clear -------------------------------

    [Fact]
    public void ReRaster_WipesThePreviousRaster()
    {
        using var scene = Scene.Create(6, 1);
        scene.Draw(ctx => ctx.FillOpaque(new Rect(0, 0, 6, 1), new BrushedStyle { Background = new SolidColorBrush(Color.FromRgb(10, 20, 30)) }));

        scene.Invalidate();
        scene.Draw(_ => { });

        Assert.Equal(CellStyle.Transparent, scene.GetCell(0, 0).Style);
    }

    [Fact]
    public void ReRaster_DropsFragmentsTheNewRasterDidNotReRegister()
    {
        IBufferFragment fragment = new StubFragment(2, 1);

        using var scene = Scene.Create(6, 2);
        scene.Draw(_ => scene.Buffer.AddFragment(0, 0, fragment));
        Assert.Equal(1, scene.Buffer.Fragments.Count);

        scene.Invalidate();
        scene.Draw(_ => { });

        Assert.Equal(0, scene.Buffer.Fragments.Count);
    }

    [Fact]
    public void ReRaster_DoesNotAccumulateDirtyMarks()
    {
        // Nothing consumes a scene buffer's dirty regions — only a FrameRenderer clears them and a
        // scene never reaches one — so the footprints the fragment wipe used to mark grew a list
        // forever on a scene re-rastered every frame.
        IBufferFragment fragment = new StubFragment(2, 1);

        using var scene = Scene.Create(6, 2);

        for (int i = 0; i < 5; i++)
        {
            scene.Invalidate();
            scene.Draw(_ => scene.Buffer.AddFragment(0, 0, fragment));
        }

        Assert.Empty(scene.Buffer.DirtyRegions);
    }

    private sealed class StubFragment(int width, int height) : IBufferFragment
    {
        public Size GetSize() => new(width, height);
        public bool IsSupported(OutputCapabilities capabilities) => true;
        public void Emit(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities) { }
    }
}
