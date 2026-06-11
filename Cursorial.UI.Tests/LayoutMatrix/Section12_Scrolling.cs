using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.LayoutMatrix;

// ReSharper disable InconsistentNaming

/// <summary>
/// §12 — Scrolling: banded scenes (T4): rows L201–L218. Harness per the matrix: <c>SCP</c> under
/// <c>Root</c>; <c>CanScrollVertically</c> default true, <c>CanScrollHorizontally</c> default
/// false; content = a tall <see cref="Probe"/>-instrumented stack. <c>K = max(viewportRows, 8)</c>;
/// band per LD11; the banded policy is the doc's (LD13) — no <c>SceneBudgetCells</c>, no degraded
/// mode. Observability per LD12 (<c>Render</c>-call counting + published parameters).
/// </summary>
public class Section12_Scrolling
{
    /// <summary>The standard render harness: root <c>Host</c> (20×10) → SCP → vertical stack of 20×1 probes.</summary>
    private static (LayoutManager Manager, RenderTree Tree, Host Root, ScrollContentPresenter Presenter, Probe[] Probes)
        CreateScrolledTree(int contentRows, int columns = 20, int rows = 10)
    {
        var root = new Host();
        var presenter = new ScrollContentPresenter();
        var stack = new StackPanel();
        var probes = new Probe[contentRows];
        for (var i = 0; i < contentRows; i++)
        {
            probes[i] = new Probe(columns, 1) { FillGlyph = (i % 10).ToString() };
            stack.Children.Add(probes[i]);
        }

        presenter.Content = stack;
        root.Add(presenter);

        var (manager, tree) = LayoutFixture.CreateRenderRoot(root, columns, rows);
        return (manager, tree, root, presenter, probes);
    }

    private static int TotalRenderCalls(Host root, Probe[] probes)
    {
        var total = root.RenderCount;
        foreach (var probe in probes)
            total += probe.RenderCount;
        return total;
    }

    [Fact]
    public void L201_SCP_IsBoundaryFromAttach_NoMidLifePromotion()
    {
        var (_, tree, _, presenter, _) = CreateScrolledTree(contentRows: 30);

        tree.Render(); // the FIRST render pass — no scroll has happened

        // Predicate ⑥: a boundary from attach, never via mid-life promotion — the layer exists on
        // the first pass and the count never changes afterwards (no promotion frame).
        Assert.Equal(2, tree.LayerCount);
        Assert.Same(presenter, tree.GetLayerBoundary(1));

        tree.Render();
        Assert.Equal(2, tree.LayerCount);
    }

    [Fact]
    public void L202_Measure_ScrollAxisConstraintIsMaxScrollExtent_NotUnbounded()
    {
        var presenter = new ScrollContentPresenter();
        var content = new Probe(10, 100);
        presenter.Content = content;

        presenter.Measure(new Size(20, 10));

        // The scrollable-axis constraint is LayoutLimits.MaxScrollExtent (doc §12 scrolling note —
        // supersedes the spec's Unbounded); the non-scrollable axis passes through.
        Assert.Equal(new Size(20, LayoutLimits.MaxScrollExtent), Assert.Single(content.MeasureConstraints));
        Assert.Equal(new Size(10, 100), presenter.Extent);
        Assert.Equal(new Size(10, 10), presenter.DesiredSize); // min(extent, constraint) per axis
    }

    [Fact]
    public void L203_CanScrollVerticallyFalse_ConstraintPassesThrough_OffsetsCoerceToZero()
    {
        var presenter = new ScrollContentPresenter { CanScrollVertically = false };
        var content = new Probe(10, 100);
        presenter.Content = content;

        presenter.Measure(new Size(20, 10));

        Assert.Equal(new Size(20, 10), Assert.Single(content.MeasureConstraints));

        presenter.ScrollOffsetRow = 5;
        Assert.Equal(0, presenter.ScrollOffsetRow); // no scroll range — coerced to 0
    }

    [Fact]
    public void L204_Arrange_ContentRect_ViewportAndExtentReadbacks()
    {
        var root = new Host();
        var presenter = new ScrollContentPresenter();
        var content = new Probe(10, 100);
        presenter.Content = content;
        root.Add(presenter);

        var extentObserver = new LayoutFixture.RecordingObserver();
        var viewportObserver = new LayoutFixture.RecordingObserver();
        presenter.AddObserver(ScrollContentPresenter.ExtentProperty, extentObserver);
        presenter.AddObserver(ScrollContentPresenter.ViewportProperty, viewportObserver);

        var manager = LayoutFixture.CreateRoot(root);
        manager.Layout(20, 10);

        // Content arranged at (0, 0, viewportW × max(extentH, viewportH)) in content coordinates.
        Assert.Equal(new Rect(0, 0, 20, 100), content.Bounds);
        Assert.Equal(new Size(20, 10), presenter.Viewport);
        Assert.Equal(new Size(10, 100), presenter.Extent);

        // DirectProperty readbacks with change notifications.
        Assert.Contains(extentObserver.Changes, c => Equals(c.NewValue, new Size(10, 100)));
        Assert.Contains(viewportObserver.Changes, c => Equals(c.NewValue, new Size(20, 10)));
    }

    [Fact]
    public void L205_OffsetCoercion_AtSetTime_IntoExtentMinusViewport()
    {
        var (_, _, _, presenter, _) = CreateScrolledTree(contentRows: 100);
        Assert.Equal(100, presenter.Extent.Rows);
        Assert.Equal(10, presenter.Viewport.Rows);

        presenter.ScrollOffsetRow = -5;
        Assert.Equal(0, presenter.ScrollOffsetRow);

        presenter.ScrollOffsetRow = 95;
        Assert.Equal(90, presenter.ScrollOffsetRow); // [0, Extent − Viewport] (WPF ScrollViewer-shaped)
    }

    [Fact]
    public void L206_ContentShrink_OffsetReCoercedAtEndOfArrange_OneCompositeChange()
    {
        var root = new Host();
        var presenter = new ScrollContentPresenter();
        var content = new Probe(10, 100);
        presenter.Content = content;
        root.Add(presenter);
        var manager = LayoutFixture.CreateRoot(root);
        manager.Layout(20, 10);

        presenter.ScrollOffsetRow = 90;
        var observer = new LayoutFixture.RecordingObserver();
        presenter.AddObserver(ScrollContentPresenter.ScrollOffsetRowProperty, observer);

        content.Natural = new Size(10, 50); // the extent shrinks to 50
        content.InvalidateMeasure();
        manager.Layout(20, 10);

        // Re-coerced at the END of arrange, same frame: 90 → clamp into [0, 50 − 10] = 40; the
        // composite lane fired exactly once (only on actual movement) — the cached raster is never
        // left slid past the content.
        Assert.Equal(40, presenter.ScrollOffsetRow);
        var change = Assert.Single(observer.Changes);
        Assert.Equal((90, 40), ((int)change.OldValue!, (int)change.NewValue!));
    }

    [Fact]
    public void L207_InBandScroll_PureCompositeSlide_ZeroRenderCalls()
    {
        var (_, tree, root, presenter, probes) = CreateScrolledTree(contentRows: 100);
        tree.Render(); // anchor 0
        var baseline = TotalRenderCalls(root, probes);
        var before = tree.Parameters(presenter);

        presenter.ScrollOffsetRow = 5; // ≤ K — within the band
        tree.Render();

        Assert.Equal(baseline, TotalRenderCalls(root, probes)); // ZERO Render calls — invariant 3
        var after = tree.Parameters(presenter);
        Assert.Equal(before.OffsetRow - 5, after.OffsetRow); // the layer slides by −5
        Assert.Equal(before.Clip, after.Clip);               // the viewport clip is unchanged
    }

    [Fact]
    public void L208_BandedSceneSize_ViewportPlusTwoK_BandStartClamped()
    {
        var (_, tree, _, presenter, _) = CreateScrolledTree(contentRows: 100);
        tree.Render();

        // K = max(viewportRows, 8) = 10; scene rows = min(100, 10 + 2·10) = 30 — memory bounded by
        // construction (LD11; LD13: never extent-sized).
        var scene = tree.GetScene(presenter);
        Assert.NotNull(scene);
        Assert.Equal(30, scene.Rows);
        Assert.Equal(20, scene.Columns);
        Assert.Equal(0, presenter.BandStartRow); // clamp(0 − 10, 0, 100 − 30) = 0
    }

    [Fact]
    public void L209_ReAnchorPastK_OneBandReRaster_ThenInBandIsCompositeOnly()
    {
        var (_, tree, root, presenter, probes) = CreateScrolledTree(contentRows: 100);
        tree.Render(); // anchor 0, K = 10

        // ReSharper disable once UnusedVariable
        var baseline = TotalRenderCalls(root, probes);

        presenter.ScrollOffsetRow = 11; // |11 − 0| > K — trips the re-anchor
        tree.Render();

        Assert.Equal(11, presenter.BandAnchorRow);
        Assert.Equal(1, presenter.BandStartRow); // clamp(11 − 10, 0, 70)
        foreach (var probe in probes)
            Assert.Equal(2, probe.RenderCount); // exactly ONE band re-raster
        var afterReAnchor = TotalRenderCalls(root, probes);

        presenter.ScrollOffsetRow = 15; // |15 − 11| ≤ K — in-band
        tree.Render();

        Assert.Equal(afterReAnchor, TotalRenderCalls(root, probes)); // composite-only
        Assert.Equal(11, presenter.BandAnchorRow);
    }

    [Fact]
    public void L210_BandClampsAtExtentEnd_MaxOffsetWritesAreNoOps()
    {
        var (_, tree, root, presenter, probes) = CreateScrolledTree(contentRows: 100);
        tree.Render();

        presenter.ScrollOffsetRow = 90; // Extent − Viewport
        tree.Render();

        Assert.Equal(70, presenter.BandStartRow); // bandStart = extent − bandLen = 100 − 30
        Assert.Equal(30, tree.GetScene(presenter)!.Rows); // no out-of-range raster
        var parameters = tree.Parameters(presenter);
        Assert.Equal(-20, parameters.OffsetRow); // origin 0 + bandStart 70 − offset 90

        var baseline = TotalRenderCalls(root, probes);
        presenter.ScrollOffsetRow = 90; // equality-gated — a further max-offset write is a no-op
        tree.Render();

        Assert.Equal(baseline, TotalRenderCalls(root, probes));
        Assert.Equal(parameters, tree.Parameters(presenter));
    }

    [Fact]
    public void L211_ReAnchorCheck_RunsInMetadataHandler_RasterHappensNextPass()
    {
        var (_, tree, root, presenter, probes) = CreateScrolledTree(contentRows: 100);
        tree.Render(); // anchor 0
        var baseline = TotalRenderCalls(root, probes);

        presenter.ScrollOffsetRow = 25; // past the re-anchor threshold

        // Synchronously: the metadata handler ran the CHECK only — the zone is marked dirty, no
        // raster work happened inside the property write.
        Assert.True(presenter.Zone!.RasterDirty);
        Assert.Equal(baseline, TotalRenderCalls(root, probes));

        tree.Render(); // the re-raster happens in the next RunRenderPass
        Assert.Equal(baseline + probes.Length, TotalRenderCalls(root, probes));
    }

    [Fact]
    public void L212_BandCoversWholeExtent_NoOffsetEverReAnchors()
    {
        // extent 25 ≤ viewport + 2K = 30 ⇒ the band is the whole extent.
        var (_, tree, root, presenter, probes) = CreateScrolledTree(contentRows: 25);
        tree.Render();
        Assert.Equal(25, tree.GetScene(presenter)!.Rows);
        var baseline = TotalRenderCalls(root, probes);

        foreach (var offset in new[] { 3, 11, 15, 0, 15, 7 }) // the whole range [0, 15]
        {
            presenter.ScrollOffsetRow = offset;
            tree.Render();

            Assert.Equal(baseline, TotalRenderCalls(root, probes)); // every frame composite-only
            Assert.Equal(0, presenter.BandAnchorRow);               // never re-anchors
            Assert.Equal(-offset, tree.Parameters(presenter).OffsetRow);
        }
    }

    [Fact]
    public void L213_AnimatedOffsets_StyledLane_InBandFramesRasterNothing_CoercionApplies()
    {
        var (_, tree, root, presenter, probes) = CreateScrolledTree(contentRows: 100);
        tree.Render();
        var baseline = TotalRenderCalls(root, probes);

        // Offsets are STYLED properties — the Animation lane drives them (smooth scrolling is
        // storyboard-able; the S5 A-gate).
        using var handle = presenter.BeginAnimation(ScrollContentPresenter.ScrollOffsetRowProperty);

        foreach (var offset in new[] { 2, 5, 8 }) // per-frame pushes within the band
        {
            handle.SetValue(offset);
            tree.Render();
            Assert.Equal(baseline, TotalRenderCalls(root, probes)); // in-band frames re-raster nothing
            Assert.Equal(-offset, tree.Parameters(presenter).OffsetRow);
        }

        handle.SetValue(10_000); // one value out of range
        Assert.Equal(90, presenter.ScrollOffsetRow); // coercion applies to animated writes too
    }

    [Fact]
    public void L214_ScrollOffsets_GetEffects_ExactlyAffectsComposite()
    {
        // The lane that guarantees zero re-raster — no measure/arrange/render flags.
        Assert.Equal(
            PropertyEffects.AffectsComposite,
            ScrollContentPresenter.ScrollOffsetColumnProperty.GetEffects(typeof(ScrollContentPresenter)));
        Assert.Equal(
            PropertyEffects.AffectsComposite,
            ScrollContentPresenter.ScrollOffsetRowProperty.GetEffects(typeof(ScrollContentPresenter)));
    }

    [Fact]
    public void L215_HugeContent_ExtentClampedToMaxScrollExtent_OneTimeDiagnostic()
    {
        var root = new Host();
        var presenter = new ScrollContentPresenter();
        var content = new Probe(10, 50_000);
        presenter.Content = content;
        root.Add(presenter);
        var manager = LayoutFixture.CreateRoot(root);

        var diagnostics = LayoutFixture.CaptureDiagnostics(manager, () =>
        {
            manager.Layout(20, 10);

            // The diagnostic is one-time per presenter: a re-measure while still clamped stays quiet.
            content.InvalidateMeasure();
            manager.Layout(20, 10);
        });

        Assert.Equal(LayoutLimits.MaxScrollExtent, presenter.Extent.Rows);
#if DEBUG
        Assert.Single(diagnostics, d => d.Kind == LayoutDiagnosticKind.ScrollExtentClamped);
#else
        Assert.Empty(diagnostics);
#endif

        presenter.ScrollOffsetRow = 50_000;
        Assert.Equal(LayoutLimits.MaxScrollExtent - 10, presenter.ScrollOffsetRow); // range uses the capped extent
    }

    [Fact]
    public void L216_Clip_IsAbsoluteViewportRect_IntersectedWithAncestors()
    {
        var root = new Host();
        var presenter = new ScrollContentPresenter { Margin = new Margins(2, 1, 0, 0) };
        var stack = new StackPanel();
        for (var i = 0; i < 100; i++)
            stack.Children.Add(new Probe(38, 1) { FillGlyph = (i % 10).ToString() });
        presenter.Content = stack;
        root.Add(presenter);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root, 40, 12);
        presenter.ScrollOffsetRow = 5; // content is drawn past the viewport
        tree.Render();

        // Clip = the absolute viewport rect ∩ ancestor clips — band content outside the viewport
        // never reaches the screen (the scene offset slides; the clip does not).
        Assert.Equal(new Rect(2, 1, 38, 11), tree.Parameters(presenter).Clip);
    }

    [Fact]
    public void L217_NestedBoundaryInScrolledContent_OffsetSubtractsScroll_ClipIntersectsViewport()
    {
        var (_, tree, _, presenter, probes) = CreateScrolledTree(contentRows: 100);
        probes[7].IsRenderBoundary = true; // a boundary INSIDE the scrolled content
        tree.Render();
        Assert.Equal(3, tree.LayerCount);

        presenter.ScrollOffsetRow = 5;
        tree.Render();

        // The nested layer's effective offset subtracts the scroll (content row 7 → window row 2)
        // and its clip intersects the viewport.
        var nested = tree.Parameters(probes[7]);
        Assert.Equal(2, nested.OffsetRow);
        Assert.Equal(new Rect(0, 2, 20, 1), nested.Clip);

        presenter.ScrollOffsetRow = 50; // scrolled out of view
        tree.Render();

        Assert.Equal(Rect.Empty, tree.Parameters(probes[7]).Clip); // clipped away…
        Assert.Equal(3, tree.LayerCount);                          // …with the layer retained
    }

    [Fact]
    public void L218_HitTestInheritsScroll_IdleScrolledFrameDoesZeroWork()
    {
        var (_, tree, root, presenter, probes) = CreateScrolledTree(contentRows: 100);
        presenter.ScrollOffsetRow = 5;
        tree.Render();

        // ① the layer's effective offset folds −ScrollOffset: window row 2 = content row 7.
        Assert.Same(probes[7], tree.HitTest(1, 2));

        // ② an idle scrolled frame does zero work: no Render calls, parameters unchanged.
        var baseline = TotalRenderCalls(root, probes);
        var rootParameters = tree.Parameters(root);
        var presenterParameters = tree.Parameters(presenter);

        tree.Render();

        Assert.Equal(baseline, TotalRenderCalls(root, probes));
        Assert.Equal(rootParameters, tree.Parameters(root));
        Assert.Equal(presenterParameters, tree.Parameters(presenter));
    }
}
