using System.Buffers;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;

namespace Cursorial.Tests.UI;

/// <summary>
/// T4 integration coverage beyond the §12 matrix rows: the byte-level "scrolling one row emits no
/// re-raster" pipeline proof, composite-content verification of the band slide, the band-straddle
/// drop rule for push-stack-uncovered draws, the zero-allocation in-band scroll frame, and the
/// <see cref="TerminalCaretService"/> contract (transform, scroll fold, registry semantics,
/// stale-owner drop, clip gating).
/// </summary>
public class ScrollCaretIntegrationTests
{
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

    // ───────────────────────────── byte-level scroll pipeline ─────────────────────────────

    [Fact]
    public void Scroll_OneRow_FullPipeline_EmitsBytesWithZeroReRaster()
    {
        var (_, tree, root, presenter, probes) = CreateScrolledTree(contentRows: 100);
        var renderer = new FrameRenderer();
        var writer = new ArrayBufferWriter<byte>();

        tree.Render();
        renderer.Render(tree.Composite(20, 10), writer); // frame 1 — the full redraw
        Assert.True(writer.WrittenCount > 0);

        var baseline = root.RenderCount + probes.Sum(p => p.RenderCount);

        presenter.ScrollOffsetRow = 1; // one row — in-band
        tree.Render();

        // Zero Render calls anywhere: the frame the terminal sees is produced purely by
        // re-compositing the cached band scene at a new offset.
        Assert.Equal(baseline, root.RenderCount + probes.Sum(p => p.RenderCount));

        var frame = tree.Composite(20, 10);
        var view = new CellBufferView(frame);
        for (var row = 0; row < 10; row++)
            Assert.Equal(((row + 1) % 10).ToString(), view[0, row].Grapheme); // content slid up one row

        writer.Clear();
        renderer.Render(frame, writer); // frame 2 — the delta the scroll produces
        Assert.True(writer.WrittenCount > 0);
    }

    [Fact]
    public void Scroll_ReAnchoredBand_CompositesTheCorrectContentRows()
    {
        var (_, tree, _, presenter, _) = CreateScrolledTree(contentRows: 100);
        tree.Render();

        presenter.ScrollOffsetRow = 60; // re-anchors: anchor 60, bandStart = 50
        tree.Render();

        Assert.Equal(50, presenter.BandStartRow);
        var view = new CellBufferView(tree.Composite(20, 10));
        for (var row = 0; row < 10; row++)
            Assert.Equal(((row + 60) % 10).ToString(), view[0, row].Grapheme); // viewport shows rows 60–69
    }

    [Fact]
    public void Scroll_InBandFrame_AllocatesNothing()
    {
        var (_, tree, _, presenter, _) = CreateScrolledTree(contentRows: 100);
        tree.Render();

        for (var i = 0; i < 64; i++)
        {
            presenter.ScrollOffsetRow = 1 + (i & 1); // alternate within the band
            tree.RunRenderPass();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            presenter.ScrollOffsetRow = 1 + (i & 1);
            tree.RunRenderPass(); // composite-parameter refresh only — the per-frame scroll path
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ───────────────────────────── band-edge behavior for uncovered draws ─────────────────────────────

    [Fact]
    public void BandRaster_CoveredPathsClipCorrectly_StraddlingUncoveredDrawIsDropped()
    {
        var (manager, tree, _, presenter, probes) = CreateScrolledTree(contentRows: 100);
        tree.Render();

        // probe[13] strokes a 3-row box from its own origin (content rows 13–15); probe[40] strokes
        // one fully inside the band. After re-anchoring to 25, bandStart = 15: the first box
        // straddles the band's top edge (rows −2…0 in scene coordinates) and is dropped — the doc
        // §5.7 pinned mechanism, with K sizing the edge outside the viewport clip; the second lands
        // at scene rows 25–27 and paints.
        probes[13].OnRender = (_, context) => context.DrawBox(new Rect(0, 0, 6, 3), Cursorial.Output.Color.FromRgb(9, 9, 9));
        probes[40].OnRender = (_, context) => context.DrawBox(new Rect(0, 0, 6, 3), Cursorial.Output.Color.FromRgb(9, 9, 9));

        var diagnostics = LayoutFixture.CaptureDiagnostics(manager, () =>
        {
            presenter.ScrollOffsetRow = 25; // re-anchor: bandStart = 15
            tree.Render();
        });

        Assert.Equal(15, presenter.BandStartRow);
#if DEBUG
        Assert.Single(diagnostics, d => d.Kind == LayoutDiagnosticKind.BandStraddlingDrawDropped);
#else
        Assert.Empty(diagnostics);
#endif

        // The covered paths (the probes' Set fills) clip per-cell at the band edge: the viewport
        // still composites the correct content rows.
        var view = new CellBufferView(tree.Composite(20, 10));
        for (var row = 0; row < 10; row++)
            Assert.Equal(((row + 25) % 10).ToString(), view[0, row].Grapheme);
    }

    // ───────────────────────────── terminal caret service (doc §5.9) ─────────────────────────────

    [Fact]
    public void Resize_WhileScrolledMidBand_RebandsAndCompositesCorrectRows()
    {
        // The RefreshBandGeometry path under a live viewport resize: anchor clamps into the new
        // offset range, the band start re-derives, the band-length change re-rents the scene, and
        // the composited viewport still shows the scrolled-to content rows.
        var (manager, tree, _, presenter, _) = CreateScrolledTree(contentRows: 100);
        tree.Render();

        presenter.ScrollOffsetRow = 60; // re-anchors: anchor 60, K = 10, bandStart = 50
        tree.Render();
        Assert.Equal(50, presenter.BandStartRow);

        // Shrink the viewport (10 → 6 rows): K = max(6, 8) = 8, bandLength = 22, bandStart = 52.
        manager.Layout(20, 6);
        tree.Render();
        Assert.Equal(60, presenter.ScrollOffsetRow); // still in range — no snap-back
        Assert.Equal(52, presenter.BandStartRow);

        var view = new CellBufferView(tree.Composite(20, 6));
        for (var row = 0; row < 6; row++)
            Assert.Equal(((row + 60) % 10).ToString(), view[0, row].Grapheme);

        // Grow it (6 → 12 rows): K = 12, bandLength = 36, bandStart = 48.
        manager.Layout(20, 12);
        tree.Render();
        Assert.Equal(48, presenter.BandStartRow);

        view = new CellBufferView(tree.Composite(20, 12));
        for (var row = 0; row < 12; row++)
            Assert.Equal(((row + 60) % 10).ToString(), view[0, row].Grapheme);

        // Bottom-scrolled + grow: the end-of-arrange re-coercion snaps the offset back same-frame.
        presenter.ScrollOffsetRow = 88; // max for viewport 12 (extent 100)
        tree.Render();
        manager.Layout(20, 16);        // max offset is now 84
        tree.Render();
        Assert.Equal(84, presenter.ScrollOffsetRow);

        view = new CellBufferView(tree.Composite(20, 16));
        for (var row = 0; row < 16; row++)
            Assert.Equal(((row + 84) % 10).ToString(), view[0, row].Grapheme);
    }

    [Fact]
    public void Caret_PublishTransformsToWindow_FoldsRenderAndScrollOffsets()
    {
        var (_, tree, _, presenter, probes) = CreateScrolledTree(contentRows: 100);
        presenter.ScrollOffsetRow = 5;
        tree.Render(); // frame assembly happens post-layout, post-boundary-walk

        var service = new TerminalCaretService();
        service.Publish(probes[7], column: 3, row: 0, CursorShape.BlinkingBar);

        // Content row 7, scrolled by 5 ⇒ window row 2 — TranslateToWindow folds the scroll.
        Assert.Equal(new TerminalCaretState(true, 3, 2, CursorShape.BlinkingBar), service.GetCaretState());

        // S4's surface-offset fold (P7) rides the parameters.
        Assert.Equal(new TerminalCaretState(true, 8, 4, CursorShape.BlinkingBar), service.GetCaretState(5, 2));
    }

    [Fact]
    public void Caret_ScrolledOutOfViewport_AssemblesHidden()
    {
        var (_, tree, _, presenter, probes) = CreateScrolledTree(contentRows: 100);
        var service = new TerminalCaretService();
        service.Publish(probes[7], column: 0, row: 0, CursorShape.SteadyBar);

        presenter.ScrollOffsetRow = 30; // content row 7 is above the viewport now
        tree.Render();

        var state = service.GetCaretState();
        Assert.False(state.Visible); // clipped-out ⇒ CursorVisible = false (doc §5.9)

        presenter.ScrollOffsetRow = 5; // back into view
        tree.Render();
        Assert.True(service.GetCaretState().Visible);
    }

    [Fact]
    public void Caret_MostRecentPublicationWins_ClearFallsBack()
    {
        var (_, tree, _, _, probes) = CreateScrolledTree(contentRows: 100);
        tree.Render();

        var service = new TerminalCaretService();
        service.Publish(probes[0], 1, 0, CursorShape.BlinkingBlock);
        service.Publish(probes[2], 4, 0, CursorShape.BlinkingBar);

        Assert.Equal(4, service.GetCaretState().Column); // most recent wins

        service.Clear(probes[2]);
        Assert.Equal(1, service.GetCaretState().Column); // earlier publication still registered

        service.Clear(probes[0]);
        Assert.Equal(TerminalCaretState.Hidden, service.GetCaretState());
        service.Clear(probes[0]); // clearing an absent owner is a no-op
    }

    [Fact]
    public void Caret_DetachedOwner_DroppedAtAssembly()
    {
        var root = new Host();
        var owner = new Probe(6, 1);
        root.Add(owner);
        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();

        var service = new TerminalCaretService();
        service.Publish(owner, 2, 0, CursorShape.BlinkingBar);
        Assert.True(service.GetCaretState().Visible);

        root.Remove(owner); // detach WITHOUT the owner calling Clear — the belt-and-braces leg

        Assert.Equal(TerminalCaretState.Hidden, service.GetCaretState());
        Assert.Equal(0, service.PublicationCount); // the stale publication was pruned
    }

    [Fact]
    public void Caret_HiddenAncestor_AssemblesHidden()
    {
        var root = new Host();
        var panel = new StackPanel();
        var owner = new Probe(6, 1);
        panel.Children.Add(owner);
        root.Add(panel);
        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();

        var service = new TerminalCaretService();
        service.Publish(owner, 0, 0, CursorShape.SteadyBlock);
        Assert.True(service.GetCaretState().Visible);

        panel.Visibility = Visibility.Hidden;
        tree.Render();

        Assert.False(service.GetCaretState().Visible);
    }

    [Fact]
    public void Caret_GetCaretState_AllocatesNothing()
    {
        var (_, tree, _, _, probes) = CreateScrolledTree(contentRows: 100);
        tree.Render();

        var service = new TerminalCaretService();
        service.Publish(probes[3], 1, 0, CursorShape.BlinkingBar);

        for (var i = 0; i < 64; i++)
            _ = service.GetCaretState();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
            _ = service.GetCaretState();

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void HorizontalScroll_FullWidthScene_PureCompositeSlide()
    {
        // v1 bands the VERTICAL axis only (LD11): a horizontally scrollable presenter's scene
        // spans the full content width, so every column offset is a pure composite slide.
        var root = new Host();
        var presenter = new ScrollContentPresenter { CanScrollHorizontally = true };
        var content = new Probe(50, 5)
        {
            OnRender = static (probe, context) =>
            {
                for (var row = 0; row < context.Size.Rows; row++)
                for (var column = 0; column < context.Size.Columns; column++)
                    context.Set(column, row, (column % 10).ToString(), default);
            },
        };
        presenter.Content = content;
        root.Add(presenter);
        var (_, tree) = LayoutFixture.CreateRenderRoot(root, 20, 10);
        tree.Render();

        var scene = tree.GetScene(presenter);
        Assert.Equal(50, scene!.Columns); // full content width — no horizontal banding
        var baseline = content.RenderCount;

        presenter.ScrollOffsetColumn = 35;
        Assert.Equal(30, presenter.ScrollOffsetColumn); // coerced into [0, 50 − 20]
        tree.Render();

        Assert.Equal(baseline, content.RenderCount); // composite-only
        Assert.Equal(-30, tree.Parameters(presenter).OffsetColumn);

        var view = new CellBufferView(tree.Composite(20, 10));
        for (var column = 0; column < 20; column++)
            Assert.Equal(((column + 30) % 10).ToString(), view[column, 0].Grapheme);
    }

    [Fact]
    public void TranslateToWindow_And_ToLocal_FoldScroll_Roundtrip()
    {
        var (_, tree, _, presenter, probes) = CreateScrolledTree(contentRows: 100);
        presenter.ScrollOffsetRow = 5;
        tree.Render();

        Assert.Equal((2, 2), probes[7].TranslateToWindow(2, 0));   // content row 7 − scroll 5
        Assert.Equal((2, 0), probes[7].TranslateToLocal(2, 2));    // the inverse

        // Round-trip through a nested point.
        var (column, row) = probes[42].TranslateToWindow(3, 0);
        Assert.Equal((3, 0), probes[42].TranslateToLocal(column, row));
    }
}
