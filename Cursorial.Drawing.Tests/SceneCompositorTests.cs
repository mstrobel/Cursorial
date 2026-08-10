using System.Buffers;

using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;

namespace Cursorial.Tests.Drawing;

public class SceneCompositorTests
{
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color RedHalf = Color.FromRgba(255, 0, 0, 128);

    private static SceneCompositor OverBlueBase() => new(CellStyle.Default.WithBackground(Blue));

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

    [Fact] // regression: the base is what makes an uncovered cell BLENDABLE — see WindowManager.RenderFrameCore ⓪
    public void Base_FromTheTargetsDerivedBlank_KeepsATranslucentSourceBlended()
    {
        // A terminal that reported its default background: CellBuffer.DeriveDefaultStyle promotes it to RGB
        // "so we can take advantage of alpha blending", and that promoted blank is the buffer's DefaultStyle.
        var terminalDefault = Color.FromRgb(30, 30, 46);
        var buffer = new CellBuffer(4, 1, defaultStyle: CellStyle.Default.WithBackground(terminalDefault));
        var scene = Scene.Create(4, 1);
        Fill(scene, new SolidColorBrush(RedHalf));
        var layers = new[] { new SceneLayer(scene) };

        // Based on the target's own blank, the source's alpha survives: a genuine 50% blend.
        Assert.True(new SceneCompositor(buffer.DefaultStyle).Composite(layers, buffer.AsView()));
        Assert.Equal(Color.Composite(RedHalf, terminalDefault, BlendingModes.Default), buffer[0, 0].Style.Background);

        // The defect it pins: a CellStyle.Default base overwrites the derived blank with a NON-RGB color, and
        // Color.Composite reports a non-RGB backdrop's result at full opacity — the alpha is discarded outright.
        var unblendable = new CellBuffer(4, 1, defaultStyle: CellStyle.Default.WithBackground(terminalDefault));
        scene.Invalidate();
        Fill(scene, new SolidColorBrush(RedHalf));
        Assert.True(new SceneCompositor().Composite(layers, unblendable.AsView()));
        Assert.Equal(RedHalf.WithAlpha(255), unblendable[0, 0].Style.Background);
        Assert.NotEqual(buffer[0, 0].Style.Background, unblendable[0, 0].Style.Background);
    }

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
        backdrop[0, 0] = new Cell(null, CellKind.Single, CellStyle.Default.WithBackground(Red));
        backdrop[1, 0] = new Cell(null, CellKind.Single, CellStyle.Default.WithBackground(Blue));
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

    // ---- Wide-pair integrity at region seams ----
    // The target must NEVER hold half a wide glyph: a split pair makes the frame renderer emit
    // into a wide glyph's right half — terminal-side corruption the front buffer can't see (the
    // "artifacts when scrolling near wide glyphs" class).

    private static void AssertNoSplitPairs(CellBuffer buffer)
    {
        for (int r = 0; r < buffer.Rows; r++)
        {
            for (int c = 0; c < buffer.Columns; c++)
            {
                var cell = buffer[c, r];

                if (cell.Kind == CellKind.WideLeft)
                {
                    Assert.True(c + 1 < buffer.Columns && buffer[c + 1, r].Kind == CellKind.WideContinuation,
                                $"orphaned WideLeft at ({c},{r})");
                }
                else if (cell.Kind == CellKind.WideContinuation)
                {
                    Assert.True(c > 0 && buffer[c - 1, r].Kind == CellKind.WideLeft,
                                $"orphaned WideContinuation at ({c},{r})");
                }
            }
        }
    }

    [Fact]
    public void OverlappingLayer_StartingOnContinuationColumn_DoesNotOrphanLowerWideLeft()
    {
        // Layer A paints 中 at (4,5); layer B's GLYPH content starts at column 5 — B's first
        // glyph lands on A's continuation through the raw single-cell write. The composited
        // target must degrade A's left half, never keep an orphaned WideLeft beside B's glyph.
        // (A background-only B would instead tint the surviving pair — the intended overlay
        // semantics — so B carries glyphs here.)
        var buffer = new CellBuffer(12, 2);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        var a = Scene.Create(12, 2);
        a.Draw(ctx => ctx.DrawText(4, 0, "中", DrawHarness.Ink(Red)));

        var b = Scene.Create(4, 2);
        b.Draw(ctx =>
        {
            ctx.DrawText(0, 0, "xxxx", DrawHarness.Ink(Red));
            ctx.DrawText(0, 1, "xxxx", DrawHarness.Ink(Red));
        });

        Assert.True(compositor.Composite(
            [new SceneLayer(a), new SceneLayer(b, new CompositeParameters(offsetColumn: 5))], view));

        AssertNoSplitPairs(buffer);
        Assert.NotEqual(CellKind.WideLeft, buffer[4, 0].Kind); // the half-covered glyph degraded
        Assert.Equal("x", buffer[5, 0].Grapheme);              // B's glyph won the cell
    }

    [Fact]
    public void UnionEdge_CuttingUnderlyingPair_HealsSplitOnRecomposite()
    {
        // Frame 1 composites A (with 中 at (4,5)) and B side by side. Frame 2 changes ONLY B,
        // whose footprint starts at column 5 — the recomposite union's left edge lands on A's
        // continuation. The pass-1 reset overwrites the in-union half; the maintaining indexer
        // must blank the out-of-union WideLeft at column 4 rather than stranding it.
        var buffer = new CellBuffer(12, 2);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        var a = Scene.Create(12, 2);
        a.Draw(ctx => ctx.DrawText(4, 0, "中", DrawHarness.Ink(Red)));

        var b = Scene.Create(4, 2);
        Fill(b, new SolidColorBrush(Red));

        var layers = new[] { new SceneLayer(a), new SceneLayer(b, new CompositeParameters(offsetColumn: 5)) };
        Assert.True(compositor.Composite(layers, view));

        // Frame 2: B re-rasters (its own footprint is the union; A's pair straddles its left edge).
        b.Invalidate();
        Fill(b, new SolidColorBrush(Blue));
        Assert.True(compositor.Composite(layers, view));

        AssertNoSplitPairs(buffer);
    }

    [Fact]
    public void ClippedLayer_WideLeftAtClipEdge_DegradesInsteadOfBleedingPastClip()
    {
        // A horizontally-scrolled layer (negative offset) under a clip whose right edge lands
        // one column after a WideLeft, while a SECOND changed layer widens the union past the
        // clip: the continuation must NOT be written outside the clip (half a glyph bleeding
        // onto the neighbor's cells — the scrollbar/border column during scrolling).
        var buffer = new CellBuffer(12, 2);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        // Scene content: 中 at scene columns (7,8). Offset -2 puts it at target (5,6); the clip
        // [0,6) makes the WideLeft the last visible cell — its continuation column belongs to
        // the neighbor layer.
        var scrolled = Scene.Create(10, 2);
        scrolled.Draw(ctx => ctx.DrawText(7, 0, "中", DrawHarness.Ink(Red)));

        var neighbor = Scene.Create(3, 2);
        Fill(neighbor, new SolidColorBrush(Red));

        var layers = new[]
        {
            new SceneLayer(scrolled, new CompositeParameters(offsetColumn: -2, clip: new Rect(0, 0, 6, 2))),
            new SceneLayer(neighbor, new CompositeParameters(offsetColumn: 6))
        };

        Assert.True(compositor.Composite(layers, view));

        // The union spans both layers (0..9); the scrolled layer's clip ends at 6. Its WideLeft
        // at target column 5 must degrade — column 6 belongs to the neighbor.
        Assert.NotEqual(CellKind.WideContinuation, buffer[6, 0].Kind); // no bleed past the clip
        Assert.Equal(Red, buffer[6, 0].Style.Background);              // the neighbor's cell is intact
        AssertNoSplitPairs(buffer);
    }

    // ── emoji stomping under higher layers ──
    // Color emoji ignore the SGR foreground: a kept-and-tinted glyph renders as a full-color
    // bitmap straight through the covering layer — through opaque dialogs AND translucent menu
    // chrome (both observed live on macOS). A bitmap cannot be tinted, only removed: ANY cover
    // whose background contributes (not fully transparent) stomps emoji. Text glyphs keep the
    // tint contract (ghost-through/dim); same-scene selection highlights draw bg-under-glyphs at
    // raster time and never reach the cross-layer path at all.

    [Fact]
    public void OpaqueCover_StompsEmojiPair_ButKeepsTextGlyphs()
    {
        var buffer = new CellBuffer(12, 2);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        var content = Scene.Create(12, 2);
        content.Draw(ctx =>
        {
            ctx.DrawText(1, 0, "✅", DrawHarness.Ink(Red));   // emoji pair at (1,2)
            ctx.DrawText(5, 0, "中", DrawHarness.Ink(Red));   // CJK pair at (5,6)
            ctx.DrawText(8, 0, "x", DrawHarness.Ink(Red));    // plain text at 8
        });

        var cover = Scene.Create(12, 2);
        Fill(cover, new SolidColorBrush(Red)); // OPAQUE background-only layer over everything

        Assert.True(compositor.Composite([new SceneLayer(content), new SceneLayer(cover)], view));

        // The emoji is STOMPED (no bitmap can render through the cover)…
        Assert.True(string.IsNullOrEmpty(buffer[1, 0].Grapheme), $"emoji survived: '{buffer[1, 0].Grapheme}'");
        Assert.Equal(CellKind.Single, buffer[1, 0].Kind);
        Assert.Equal(CellKind.Single, buffer[2, 0].Kind);

        // …while text glyphs keep the tint contract (kept, foreground == background ⇒ invisible).
        Assert.Equal("中", buffer[5, 0].Grapheme);
        Assert.Equal(buffer[5, 0].Style.Background, buffer[5, 0].Style.Foreground);
        Assert.Equal("x", buffer[8, 0].Grapheme);
        AssertNoSplitPairs(buffer);
    }

    [Fact]
    public void TranslucentCover_StompsEmoji_TextGhostsThrough()
    {
        // Translucent chrome (fancy menus/popups) dims the text beneath it — but a bitmap cannot
        // be dimmed, only removed. Any contributing background stomps emoji; text keeps ghosting.
        var buffer = new CellBuffer(10, 1);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        var content = Scene.Create(10, 1);
        content.Draw(ctx =>
        {
            ctx.DrawText(2, 0, "✅", DrawHarness.Ink(Red));
            ctx.DrawText(6, 0, "ab", DrawHarness.Ink(Red));
        });

        var chrome = Scene.Create(10, 1);
        Fill(chrome, new SolidColorBrush(RedHalf)); // translucent

        Assert.True(compositor.Composite([new SceneLayer(content), new SceneLayer(chrome)], view));

        Assert.True(string.IsNullOrEmpty(buffer[2, 0].Grapheme), $"emoji survived: '{buffer[2, 0].Grapheme}'");
        Assert.Equal("a", buffer[6, 0].Grapheme); // text keeps the dimmed ghost-through look
        AssertNoSplitPairs(buffer);
    }

    [Fact]
    public void FullyTransparentCover_KeepsEmoji()
    {
        // A pass-through cell (bg alpha 0 — contributes nothing) leaves everything untouched,
        // emoji included. Layer-opacity fades scale toward transparent and land here too.
        var buffer = new CellBuffer(8, 1);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        var content = Scene.Create(8, 1);
        content.Draw(ctx => ctx.DrawText(2, 0, "✅", DrawHarness.Ink(Red)));

        var passThrough = Scene.Create(8, 1);
        Fill(passThrough, new SolidColorBrush(Color.FromRgba(255, 0, 0, 0))); // fully transparent

        compositor.Composite([new SceneLayer(content), new SceneLayer(passThrough)], view);

        Assert.Equal("✅", buffer[2, 0].Grapheme);
        Assert.Equal(CellKind.WideLeft, buffer[2, 0].Kind);
        AssertNoSplitPairs(buffer);
    }

    [Fact]
    public void TranslucentOccluderLayer_StompsEmoji_TextStillTintsThrough()
    {
        // The live repro shape (fancy translucent menu, an occluder surface): a menu row
        // reverting from its opaque selection highlight to the translucent normal background
        // resurfaced the emoji beneath it. Any contributing background stomps — the occluder
        // flag is not load-bearing, but this pins the exact scenario from the field.
        var buffer = new CellBuffer(12, 1);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        var content = Scene.Create(12, 1);
        content.Draw(ctx =>
        {
            ctx.DrawText(1, 0, "✅", DrawHarness.Ink(Red));
            ctx.DrawText(5, 0, "ab", DrawHarness.Ink(Red));
        });

        var menu = Scene.Create(12, 1);
        Fill(menu, new SolidColorBrush(RedHalf)); // translucent chrome

        Assert.True(compositor.Composite(
            [new SceneLayer(content), new SceneLayer(menu) { IsOccluder = true }], view));

        Assert.True(string.IsNullOrEmpty(buffer[1, 0].Grapheme), $"emoji survived the occluder: '{buffer[1, 0].Grapheme}'");
        Assert.Equal("a", buffer[5, 0].Grapheme); // text keeps the dimmed ghost-through look
        AssertNoSplitPairs(buffer);
    }

    [Fact]
    public void OpaqueCover_OverOnlyTheRightHalf_StompsTheWholeEmoji()
    {
        // A layer edge landing mid-pair: covering just the continuation must stomp the whole
        // glyph — the terminal cannot render half an emoji bitmap, and leaving the WideLeft
        // would paint the bitmap over the covering layer's first column.
        var buffer = new CellBuffer(12, 2);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        var content = Scene.Create(12, 2);
        content.Draw(ctx => ctx.DrawText(4, 0, "✅", DrawHarness.Ink(Red))); // pair at (4,5)

        var cover = Scene.Create(4, 2);
        Fill(cover, new SolidColorBrush(Red)); // opaque, footprint starts at the continuation column

        Assert.True(compositor.Composite(
            [new SceneLayer(content), new SceneLayer(cover, new CompositeParameters(offsetColumn: 5))], view));

        Assert.True(string.IsNullOrEmpty(buffer[4, 0].Grapheme), $"left half survived: '{buffer[4, 0].Grapheme}'");
        Assert.NotEqual(CellKind.WideLeft, buffer[4, 0].Kind);

        // Audit F1: the UNCOVERED half must keep a composited style — never the indexer
        // hygiene's default(Style), which would punch a terminal-default hole at the cover edge.
        Assert.NotEqual(default, buffer[4, 0].Style);
        AssertNoSplitPairs(buffer);
    }

    [Fact]
    public void OpaqueCover_OverOnlyTheLeftHalf_StompsWholePair_NoDefaultStyleHole()
    {
        // The mirror edge: the cover ENDS mid-pair (clipped to cover only the WideLeft). The
        // whole emoji stomps and the uncovered continuation column keeps a real style.
        var buffer = new CellBuffer(12, 2);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        var content = Scene.Create(12, 2);
        content.Draw(ctx => ctx.DrawText(4, 0, "✅", DrawHarness.Ink(Red))); // pair at (4,5)

        var cover = Scene.Create(5, 2);
        Fill(cover, new SolidColorBrush(Red)); // opaque, footprint columns 0..4 — covers ONLY the WideLeft

        Assert.True(compositor.Composite(
            [new SceneLayer(content), new SceneLayer(cover)], view));

        Assert.True(string.IsNullOrEmpty(buffer[4, 0].Grapheme));
        Assert.True(string.IsNullOrEmpty(buffer[5, 0].Grapheme), $"right half survived: '{buffer[5, 0].Grapheme}'");
        Assert.Equal(CellKind.Single, buffer[5, 0].Kind);
        Assert.NotEqual(default, buffer[5, 0].Style); // audit F1: no default-style hole outside the cover
        AssertNoSplitPairs(buffer);
    }

    [Fact]
    public void TransparentForegroundEmoji_UnderFullyTransparentCover_Keeps()
    {
        // An emoji authored with a transparent foreground ("fg doesn't matter for emoji") is
        // still an emoji: it keeps under a pure pass-through cover and stomps under any
        // contributing one — the gate reads the COVER's background, never the target's colors.
        var buffer = new CellBuffer(8, 1);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        var content = Scene.Create(8, 1);
        content.Draw(ctx => ctx.DrawText(2, 0, "✅", DrawHarness.Ink(Color.FromRgba(0, 0, 0, 0)))); // transparent fg

        var passThrough = Scene.Create(8, 1);
        Fill(passThrough, new SolidColorBrush(Color.FromRgba(255, 0, 0, 0)));

        compositor.Composite([new SceneLayer(content), new SceneLayer(passThrough)], view);

        Assert.Equal("✅", buffer[2, 0].Grapheme);
        AssertNoSplitPairs(buffer);
    }

    [Fact]
    public void VerticalSlide_WithWideGlyphs_KeepsPairsIntact()
    {
        // The scroll case proper: a taller-than-viewport scene slides vertically under a fixed
        // clip (the ScrollContentPresenter band pattern). Wide pairs are column-aligned, so every
        // slide must recomposite them whole.
        var buffer = new CellBuffer(10, 3);
        var view = buffer.AsView();
        var compositor = OverBlueBase();

        var band = Scene.Create(10, 6);
        band.Draw(ctx =>
        {
            for (int r = 0; r < 6; r++)
                ctx.DrawText(2, r, "中文", DrawHarness.Ink(Red));
        });

        var layers = new[] { new SceneLayer(band, new CompositeParameters(offsetRow: 0, clip: new Rect(0, 0, 10, 3))) };
        Assert.True(compositor.Composite(layers, view));
        AssertNoSplitPairs(buffer);

        for (int offset = 1; offset <= 3; offset++)
        {
            layers[0] = new SceneLayer(band, new CompositeParameters(offsetRow: -offset, clip: new Rect(0, 0, 10, 3)));
            Assert.True(compositor.Composite(layers, view));
            AssertNoSplitPairs(buffer);
            Assert.Equal("中", buffer[2, 0].Grapheme); // content actually slid, pairs whole
        }
    }
}
