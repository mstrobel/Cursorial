using System.Buffers;

using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Drawing;

public class SceneCompositorTests
{
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color RedHalf = Color.FromRgba(255, 0, 0, 128);

    private static SceneCompositor OverBlueBase() => new(Style.Default.WithBackground(Blue));

    private static void Fill(Scene scene, IBrush brush) => scene.Draw(ctx => ctx.FillRectangle(scene.Bounds, brush));

    // ---- P0: the compositing invariant ----

    [Fact]
    public void Invariant_RecompositingTranslucentSceneEachFrame_IsStable_NotDrifting()
    {
        // The discriminating test. A translucent scene re-composited every frame must land on the
        // BASE each time (stable), never on its own prior output (which would saturate/drift).
        var expected = Color.Composite(RedHalf, Blue, BlendingModes.Default);

        var buffer = new CellBuffer(8, 2);
        var view = buffer.AsView();
        var compositor = OverBlueBase();
        var scene = Scene.Create(8, 2);
        var layers = new[] { new SceneLayer(scene) };

        for (int frame = 0; frame < 4; frame++)
        {
            scene.Invalidate();                 // force a re-raster + recomposite every frame
            Fill(scene, new SolidColorBrush(RedHalf));
            Assert.True(compositor.Composite(layers, view));

            Assert.Equal(expected, buffer[0, 0].Style.Background);   // identical every frame — no drift
            Assert.Equal(expected, buffer[7, 1].Style.Background);
        }
    }

    [Fact]
    public void Idle_NoInvalidation_SecondCompositeIsANoOp()
    {
        var expected = Color.Composite(RedHalf, Blue, BlendingModes.Default);

        var buffer = new CellBuffer(6, 2);
        var view = buffer.AsView();
        var compositor = OverBlueBase();
        var scene = Scene.Create(6, 2);
        var layers = new[] { new SceneLayer(scene) };

        Fill(scene, new SolidColorBrush(RedHalf));
        Assert.True(compositor.Composite(layers, view));   // first frame paints
        Assert.Equal(expected, buffer[0, 0].Style.Background);

        // No invalidation, no param change → nothing to do, target untouched.
        Assert.False(compositor.Composite(layers, view));
        Assert.Equal(expected, buffer[0, 0].Style.Background);
    }

    // ---- Base, transparency, coverage ----

    [Fact]
    public void UncoveredCells_ShowBase_PaintedCells_ShowComposite()
    {
        var buffer = new CellBuffer(6, 1);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        // A 2-wide scene covering only columns [2,4) of a 6-wide target.
        var scene = Scene.Create(2, 1);
        Fill(scene, new SolidColorBrush(Red));
        var layers = new[] { new SceneLayer(scene, new CompositeParameters(offsetColumn: 2)) };

        Assert.True(compositor.Composite(layers, view));

        Assert.Equal(Blue, buffer[0, 0].Style.Background);   // uncovered → base
        Assert.Equal(Color.Composite(Red, Blue, BlendingModes.Default), buffer[2, 0].Style.Background);  // covered
        Assert.Equal(Blue, buffer[5, 0].Style.Background);   // uncovered → base
    }

    [Fact] // regression: a layer's scene SHRINKING must reset the cells it vacated to base (no stale artifacts)
    public void SceneShrinks_VacatedCells_ResetToBase()
    {
        var buffer = new CellBuffer(10, 1);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        // Frame 1: an 8-wide Red scene covers columns [0,8) (the "large window").
        var large = Scene.Create(8, 1);
        Fill(large, new SolidColorBrush(Red));
        var layers = new[] { new SceneLayer(large) };
        Assert.True(compositor.Composite(layers, view));
        var redOverBlue = Color.Composite(Red, Blue, BlendingModes.Default);
        Assert.Equal(redOverBlue, buffer[7, 0].Style.Background);

        // Frame 2: the same layer slot now holds a SMALLER 4-wide scene (the window resized down — the pool
        // rents an exact-size scene, so this is a scene swap). Columns [4,8) were vacated and must show base.
        var small = Scene.Create(4, 1);
        Fill(small, new SolidColorBrush(Red));
        layers[0] = new SceneLayer(small);
        Assert.True(compositor.Composite(layers, view));

        Assert.Equal(redOverBlue, buffer[0, 0].Style.Background); // still covered
        Assert.Equal(redOverBlue, buffer[3, 0].Style.Background); // last covered column of the smaller scene
        Assert.Equal(Blue, buffer[4, 0].Style.Background);        // VACATED → base (was the artifact)
        Assert.Equal(Blue, buffer[7, 0].Style.Background);        // VACATED → base
    }

    [Fact]
    public void Transparent_UnpaintedSceneCells_LeaveBaseShowing()
    {
        var buffer = new CellBuffer(4, 1);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        // Scene paints only column 0; columns 1..3 stay transparent (cleared default).
        var scene = Scene.Create(4, 1);
        scene.Draw(ctx => ctx.FillRectangle(new Rect(0, 0, 1, 1), new SolidColorBrush(Red)));
        var layers = new[] { new SceneLayer(scene) };

        Assert.True(compositor.Composite(layers, view));

        Assert.Equal(Color.Composite(Red, Blue, BlendingModes.Default), buffer[0, 0].Style.Background);
        Assert.Equal(Blue, buffer[2, 0].Style.Background);   // transparent scene cell → base shows
    }

    // ---- Composite parameters ----

    [Fact]
    public void Opacity_ScalesSourceAlpha()
    {
        var buffer = new CellBuffer(2, 1);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        // Opaque red scene composited at 50% opacity == red@128 composited opaque.
        var scene = Scene.Create(2, 1);
        Fill(scene, new SolidColorBrush(Red));
        var layers = new[] { new SceneLayer(scene, new CompositeParameters(opacity: 128)) };

        Assert.True(compositor.Composite(layers, view));
        Assert.Equal(Color.Composite(RedHalf, Blue, BlendingModes.Default), buffer[0, 0].Style.Background);
    }

    [Fact]
    public void Offset_TranslatesSceneOntoTarget()
    {
        var buffer = new CellBuffer(6, 3);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        var scene = Scene.Create(1, 1);
        Fill(scene, new SolidColorBrush(Red));
        var layers = new[] { new SceneLayer(scene, new CompositeParameters(offsetColumn: 3, offsetRow: 1)) };

        Assert.True(compositor.Composite(layers, view));

        Assert.Equal(Color.Composite(Red, Blue, BlendingModes.Default), buffer[3, 1].Style.Background);
        Assert.Equal(Blue, buffer[0, 0].Style.Background);
    }

    [Fact]
    public void Clip_RestrictsCompositedCells()
    {
        var buffer = new CellBuffer(4, 1);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        var scene = Scene.Create(4, 1);
        Fill(scene, new SolidColorBrush(Red));
        // Clip to columns [1,3).
        var layers = new[] { new SceneLayer(scene, new CompositeParameters(clip: new Rect(1, 0, 2, 1))) };

        Assert.True(compositor.Composite(layers, view));

        Assert.Equal(Blue, buffer[0, 0].Style.Background);   // outside clip → base
        Assert.Equal(Color.Composite(Red, Blue, BlendingModes.Default), buffer[1, 0].Style.Background);
        Assert.Equal(Blue, buffer[3, 0].Style.Background);   // outside clip → base
    }

    [Fact]
    public void OffsetChange_VacatedRegionReturnsToBase()
    {
        var buffer = new CellBuffer(6, 1);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        var scene = Scene.Create(1, 1);
        Fill(scene, new SolidColorBrush(Red));
        var composite = Color.Composite(Red, Blue, BlendingModes.Default);

        // Frame 1 at column 1.
        compositor.Composite(new[] { new SceneLayer(scene, new CompositeParameters(offsetColumn: 1)) }, view);
        Assert.Equal(composite, buffer[1, 0].Style.Background);

        // Frame 2 at column 4 (param change, no re-raster): col 1 must reset to base, col 4 paints.
        Assert.True(compositor.Composite(new[] { new SceneLayer(scene, new CompositeParameters(offsetColumn: 4)) }, view));
        Assert.Equal(Blue, buffer[1, 0].Style.Background);     // vacated → base
        Assert.Equal(composite, buffer[4, 0].Style.Background);
    }

    [Fact]
    public void Composite_MarksDirtyUnion_ForBoundedRepaint()
    {
        var buffer = new CellBuffer(10, 4);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        var scene = Scene.Create(2, 2);
        Fill(scene, new SolidColorBrush(Red));
        var layers = new[] { new SceneLayer(scene, new CompositeParameters(offsetColumn: 5, offsetRow: 1)) };

        // Frame 1 establishes the stack (full-target union). The bounded union appears on a later
        // frame where only this one panel changed.
        compositor.Composite(layers, view);
        buffer.ClearDirty();

        scene.Invalidate();
        Fill(scene, new SolidColorBrush(Red));
        Assert.True(compositor.Composite(layers, view));

        Assert.Single(buffer.DirtyRegions);
        Assert.Equal(new Rect(5, 1, 2, 2), buffer.DirtyRegions[0]);
    }

    // ---- Two cache tiers: static frame → empty FrameRenderer delta ----

    [Fact]
    public void StaticFrame_ProducesEmptyFrameRendererDelta()
    {
        var buffer = new CellBuffer(8, 3);
        var view = buffer.AsView();
        var compositor = OverBlueBase();
        var renderer = new FrameRenderer();

        var scene = Scene.Create(8, 3);
        Fill(scene, new SolidColorBrush(Red));
        var layers = new[] { new SceneLayer(scene) };

        // Frame 1: composite + render (non-empty).
        Assert.True(compositor.Composite(layers, view));
        var w1 = new ArrayBufferWriter<byte>();
        renderer.Render(buffer, w1);
        Assert.True(w1.WrittenCount > 0);

        // Frame 2: nothing changed → compositor no-op → buffer unchanged → the renderer's cell diff
        // is empty, so frame 2 is just the per-frame preamble (autowrap-disable etc.) and is strictly
        // smaller than frame 1 (no cell content re-emitted).
        Assert.False(compositor.Composite(layers, view));
        var w2 = new ArrayBufferWriter<byte>();
        renderer.Render(buffer, w2);
        Assert.True(w2.WrittenCount < w1.WrittenCount, $"frame2={w2.WrittenCount} should be < frame1={w1.WrittenCount}");
    }

    // ---- Multi-layer z-order + opacity edges ----

    [Fact]
    public void MultipleLayers_CompositeBottomUp()
    {
        var buffer = new CellBuffer(3, 1);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        var bottom = Scene.Create(3, 1);
        Fill(bottom, new SolidColorBrush(Red));                 // opaque red, full width

        var top = Scene.Create(1, 1);
        Fill(top, new SolidColorBrush(Color.FromRgba(0, 255, 0, 128)));   // green@50%, column 0 only

        var layers = new[]
                     {
                         new SceneLayer(bottom),                                       // z=0 (lower)
                         new SceneLayer(top, new CompositeParameters(offsetColumn: 0)) // z=1 (upper)
                     };

        Assert.True(compositor.Composite(layers, view));

        var redOverBase = Color.Composite(Red, Blue, BlendingModes.Default);
        // Column 0: green@50% over (red over base). Columns 1-2: just red over base.
        Assert.Equal(Color.Composite(Color.FromRgba(0, 255, 0, 128), redOverBase, BlendingModes.Default),
                     buffer[0, 0].Style.Background);
        Assert.Equal(redOverBase, buffer[1, 0].Style.Background);
        Assert.Equal(redOverBase, buffer[2, 0].Style.Background);
    }

    [Fact]
    public void ZeroOpacity_LayerContributesNothing()
    {
        var buffer = new CellBuffer(2, 1);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        var scene = Scene.Create(2, 1);
        Fill(scene, new SolidColorBrush(Red));
        var layers = new[] { new SceneLayer(scene, new CompositeParameters(opacity: 0)) };

        Assert.True(compositor.Composite(layers, view));
        Assert.Equal(Blue, buffer[0, 0].Style.Background);   // opacity 0 → source alpha 0 → base shows
    }

    [Fact]
    public void ClipFullyOutsideScene_LeavesBaseEverywhere()
    {
        var buffer = new CellBuffer(3, 1);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        var scene = Scene.Create(2, 1);
        Fill(scene, new SolidColorBrush(Red));
        // Clip lies entirely beyond the scene's footprint → empty footprint → no contribution.
        var layers = new[] { new SceneLayer(scene, new CompositeParameters(clip: new Rect(5, 0, 2, 1))) };

        Assert.True(compositor.Composite(layers, view));
        Assert.Equal(Blue, buffer[0, 0].Style.Background);
        Assert.Equal(Blue, buffer[1, 0].Style.Background);
    }

    // ---- Review regressions ----

    [Fact]
    public void SceneSwap_SameVersionAndParams_StillRecomposites()
    {
        // P0-1: a different Scene swapped into the same slot, with the same RasterVersion (both drawn
        // once → version 1) and same params, must recomposite — not silently keep the old pixels.
        var buffer = new CellBuffer(2, 1);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        var a = Scene.Create(2, 1);
        Fill(a, new SolidColorBrush(Red));
        var b = Scene.Create(2, 1);
        var green = Color.FromRgb(0, 255, 0);
        Fill(b, new SolidColorBrush(green));

        compositor.Composite(new[] { new SceneLayer(a) }, view);
        Assert.Equal(Color.Composite(Red, Blue, BlendingModes.Default), buffer[0, 0].Style.Background);

        Assert.True(compositor.Composite(new[] { new SceneLayer(b) }, view));
        Assert.Equal(Color.Composite(green, Blue, BlendingModes.Default), buffer[0, 0].Style.Background);
    }

    [Fact]
    public void BaseLayer_Backdrop_CompositesOverStoredCells()
    {
        var backdrop = new CellBuffer(2, 1);
        backdrop[0, 0] = new Cell(null, CellKind.Single, Style.Default.WithBackground(Red));
        backdrop[1, 0] = new Cell(null, CellKind.Single, Style.Default.WithBackground(Blue));
        var compositor = new SceneCompositor(backdrop);

        var buffer = new CellBuffer(2, 1);
        var scene = Scene.Create(2, 1);
        var greenHalf = Color.FromRgba(0, 255, 0, 128);
        Fill(scene, new SolidColorBrush(greenHalf));

        compositor.Composite(new[] { new SceneLayer(scene) }, buffer.AsView());

        Assert.Equal(Color.Composite(greenHalf, Red, BlendingModes.Default), buffer[0, 0].Style.Background);
        Assert.Equal(Color.Composite(greenHalf, Blue, BlendingModes.Default), buffer[1, 0].Style.Background);
    }

    [Fact]
    public void BaseLayer_SmallerThanTarget_DoesNotThrow()
    {
        // P0-2: a stored backdrop smaller than a (later-resized) larger target must not throw when
        // reset reaches cells beyond the backdrop — it falls back to the base style there.
        var compositor = new SceneCompositor(new CellBuffer(2, 1));   // small backdrop, default base style
        var buffer = new CellBuffer(4, 2);                            // larger target
        var scene = Scene.Create(1, 1);
        Fill(scene, new SolidColorBrush(Red));

        Assert.True(compositor.Composite(new[] { new SceneLayer(scene) }, buffer.AsView()));
        Assert.Equal(default(Cell), buffer[3, 1]);   // beyond the backdrop → base-style fallback
    }

    [Fact]
    public void GradientScene_CompositeOpacityZero_ContributesNothing()
    {
        // Exercises ScaleSourceAlpha on a gradient-sourced cell (all prior opacity tests used solids).
        var buffer = new CellBuffer(4, 1);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        var scene = Scene.Create(4, 1);
        scene.Draw(ctx => ctx.FillRectangle(scene.Bounds,
            new LinearGradientBrush([new(0.0, Color.FromRgb(0, 0, 0)), new(1.0, Color.FromRgb(255, 255, 255))])));

        Assert.True(compositor.Composite(new[] { new SceneLayer(scene, new CompositeParameters(opacity: 0)) }, view));
        Assert.Equal(Blue, buffer[0, 0].Style.Background);
        Assert.Equal(Blue, buffer[3, 0].Style.Background);
    }
}
