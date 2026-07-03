using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.LayoutMatrix;

// ReSharper disable InconsistentNaming

/// <summary>
/// §11 — Render zones, composite order, hit testing (T3): rows L173–L200. Harness per §0.1: the
/// root element is a render boundary by construction (the P1 single-root stand-in for S4's window);
/// observability per LD12 (<c>Render</c>-call counting + collected <c>SceneLayer.Parameters</c>).
/// </summary>
public class Section11_RenderZones
{
    // ───────────────────────────── zones & paint order ─────────────────────────────

    [Fact]
    public void L173_RootZone_NestedNonBoundaries_OneLayer_PreOrderPaint()
    {
        var log = new List<string>();
        var root = new Host { Log = log, LogName = "root" };
        var mid = new Host { Log = log, LogName = "mid" };
        var a = new Probe(4, 2) { Log = log, LogName = "a" };
        var b = new Probe(4, 2) { Log = log, LogName = "b" };
        mid.Add(a);
        mid.Add(b);
        root.Add(mid);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        log.Clear();
        tree.Render();

        Assert.Equal(1, tree.LayerCount);
        Assert.Equal(["render:root", "render:mid", "render:a", "render:b"], log.Where(e => e.StartsWith("render:")));
    }

    [Fact]
    public void L174_ZIndexSiblings_StableSort_PaintOrder()
    {
        var log = new List<string>();
        var root = new Host { Log = log, LogName = "root" };
        var a = new Probe(4, 2) { Log = log, LogName = "a", ZIndex = 0 };
        var b = new Probe(4, 2) { Log = log, LogName = "b", ZIndex = 5 };
        var c = new Probe(4, 2) { Log = log, LogName = "c", ZIndex = 0 };
        root.Add(a);
        root.Add(b);
        root.Add(c);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        log.Clear();
        tree.Render();

        // index 0, index 2, then the ZIndex = 5 sibling — stable (ZIndex, index) sort.
        Assert.Equal(["render:root", "render:a", "render:c", "render:b"], log.Where(e => e.StartsWith("render:")));
    }

    [Fact]
    public void L175_ZIndexChange_RerastersWithNewOrder()
    {
        var log = new List<string>();
        var root = new Host { Log = log, LogName = "root" };
        var a = new Probe(4, 2) { Log = log, LogName = "a" };
        var b = new Probe(4, 2) { Log = log, LogName = "b" };
        root.Add(a);
        root.Add(b);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        var renderCount = a.RenderCount;

        log.Clear();
        a.ZIndex = 9; // [AffectsRender + z-order recollect]
        tree.Render();

        Assert.Equal(renderCount + 1, a.RenderCount); // the zone re-rastered
        Assert.Equal(["render:root", "render:b", "render:a"], log.Where(e => e.StartsWith("render:"))); // new paint order
    }

    // ───────────────────────────── boundary promotion ─────────────────────────────

    [Fact]
    public void L176_OpacityPromotion_FourStep_BothZonesRerasterOnce()
    {
        var root = new Host();
        var a = new Probe(4, 2);
        var x = new Host();
        var c = new Probe(4, 2);
        x.Add(c);
        root.Add(a);
        root.Add(x);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        Assert.Equal(1, tree.LayerCount);
        var (rcRoot, rcA, rcX, rcC) = (root.RenderCount, a.RenderCount, x.RenderCount, c.RenderCount);

        x.Opacity = 0.5; // predicate ② promotes
        tree.Render();

        Assert.Equal(2, tree.LayerCount); // +1 — the compositor's full-recomposite signal
        // The promotion frame re-rasters the old zone (now excluding X's subtree) and the new zone:
        // every member of each re-renders exactly once.
        Assert.Equal(rcRoot + 1, root.RenderCount);
        Assert.Equal(rcA + 1, a.RenderCount);
        Assert.Equal(rcX + 1, x.RenderCount);
        Assert.Equal(rcC + 1, c.RenderCount);
    }

    [Fact]
    public void L177_PromotionIsSticky_NoDemotion()
    {
        var root = new Host();
        var x = new Host();
        x.Add(new Probe(4, 2));
        root.Add(x);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        x.Opacity = 0.5;
        tree.Render();
        Assert.Equal(2, tree.LayerCount);
        var rcRoot = root.RenderCount;

        x.Opacity = 1.0; // does not demote — promotion is sticky until detach
        tree.Render();
        tree.Render();
        tree.Render();

        Assert.Equal(2, tree.LayerCount);
        Assert.Equal(rcRoot, root.RenderCount); // and the flips re-rastered nothing
    }

    [Fact]
    public void L178_RenderOffset_FirstWritePromotes_SubsequentWritesParamsOnly()
    {
        var root = new Host();
        var x = new Host();
        var c = new Probe(4, 2);
        x.Add(c);
        root.Add(x);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        Assert.Equal(1, tree.LayerCount);

        x.RenderOffsetColumn = 3; // predicate ③ promotes
        tree.Render();
        Assert.Equal(2, tree.LayerCount);
        Assert.Equal(3, tree.Parameters(x).OffsetColumn);
        var (rcRoot, rcX, rcC) = (root.RenderCount, x.RenderCount, c.RenderCount);

        x.RenderOffsetColumn = 4; // animated slides never re-raster (invariant 3)
        tree.Render();

        Assert.Equal(rcRoot, root.RenderCount);
        Assert.Equal(rcX, x.RenderCount);
        Assert.Equal(rcC, c.RenderCount);
        Assert.Equal(4, tree.Parameters(x).OffsetColumn);
    }

    [Fact]
    public void L179_ClipToBounds_Promotes_ClipIsAbsoluteBoundsIntersectAncestors()
    {
        var root = new Canvas();
        var x = new Host { ClipToBounds = true, Width = 10, Height = 3 };
        root.Children.Add(x);
        Canvas.SetLeft(x, 2);
        Canvas.SetTop(x, 1);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();

        Assert.Equal(2, tree.LayerCount); // predicate ④ promoted
        Assert.Equal(new Rect(2, 1, 10, 3), tree.Parameters(x).Clip);
    }

    [Fact]
    public void L180_CompositeClip_Promotes_NullRevertsToBoundsDerived_StillBoundary()
    {
        var root = new Canvas();
        var x = new Host { Width = 10, Height = 3 };
        root.Children.Add(x);
        Canvas.SetLeft(x, 2);
        Canvas.SetTop(x, 1);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        Assert.Equal(1, tree.LayerCount);

        x.CompositeClip = new Rect(1, 1, 3, 2); // predicate ⑤ promotes; element-local
        tree.Render();
        Assert.Equal(2, tree.LayerCount);
        Assert.Equal(new Rect(3, 2, 3, 2), tree.Parameters(x).Clip); // includes the composite clip

        x.CompositeClip = null;
        tree.Render();
        Assert.Equal(2, tree.LayerCount); // still a boundary (sticky)
        Assert.Equal(new Rect(2, 1, 10, 3), tree.Parameters(x).Clip); // reverts to the bounds-derived value
    }

    [Fact]
    public void L181_IsRenderBoundary_Promotes_FalseDemotesNothing()
    {
        var root = new Host();
        var x = new Host();
        x.Add(new Probe(4, 2));
        root.Add(x);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        Assert.Equal(1, tree.LayerCount);

        x.IsRenderBoundary = true; // predicate ⑦
        tree.Render();
        Assert.Equal(2, tree.LayerCount);

        x.IsRenderBoundary = false; // documented: demotes nothing
        tree.Render();
        Assert.Equal(2, tree.LayerCount);
    }

    // ───────────────────────────── composite parameters ─────────────────────────────

    [Fact]
    public void L182_OpacityHalf_ParameterByte128()
    {
        var root = new Host();
        var x = new Host { Opacity = 0.5 };
        x.Add(new Probe(4, 2));
        root.Add(x);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();

        Assert.Equal((byte)128, tree.Parameters(x).Opacity); // round(255·0.5)
    }

    [Fact]
    public void L183_NestedBoundaryOpacities_MultiplyDown()
    {
        var root = new Host();
        var x = new Host { Opacity = 0.5 };
        var y = new Host { Opacity = 0.5 };
        y.Add(new Probe(4, 2));
        x.Add(y);
        root.Add(x);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();

        Assert.Equal((byte)64, tree.Parameters(y).Opacity); // round(255·0.25) — the opacity-group approximation
    }

    [Fact]
    public void L184_AffectsRender_RerastersOwningZoneOnly()
    {
        var root = new Host();
        var p1 = new Probe(4, 2);
        var x = new StackPanel(); // non-boundary, [AffectsRender] Background
        var y = new Host { IsRenderBoundary = true };
        var c = new Probe(4, 2);
        y.Add(c);
        root.Add(p1);
        root.Add(x);
        root.Add(y);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        Assert.Equal(2, tree.LayerCount);
        var (rcP1, rcY, rcC) = (p1.RenderCount, y.RenderCount, c.RenderCount);

        x.Background = new SolidColorBrush(Color.FromRgb(40, 40, 40));
        tree.Render();

        Assert.Equal(rcP1 + 1, p1.RenderCount); // the owning (root) zone re-rastered
        Assert.Equal(rcY, y.RenderCount);       // descendant boundaries raster separately
        Assert.Equal(rcC, c.RenderCount);
    }

    [Fact]
    public void L185_AffectsComposite_ZeroRenderCalls_OnlyParametersDiffer()
    {
        var root = new Host();
        var p1 = new Probe(4, 2);
        var y = new Host { IsRenderBoundary = true };
        var c = new Probe(4, 2);
        y.Add(c);
        root.Add(p1);
        root.Add(y);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        var before = tree.Parameters(y);
        var (rcRoot, rcP1, rcY, rcC) = (root.RenderCount, p1.RenderCount, y.RenderCount, c.RenderCount);

        y.RenderOffsetRow = 2; // any [AffectsComposite] write — invariant 3's regression row
        tree.Render();

        Assert.Equal(rcRoot, root.RenderCount);
        Assert.Equal(rcP1, p1.RenderCount);
        Assert.Equal(rcY, y.RenderCount);
        Assert.Equal(rcC, c.RenderCount);
        Assert.NotEqual(before, tree.Parameters(y));
        Assert.Equal(before.OffsetRow + 2, tree.Parameters(y).OffsetRow);
    }

    [Fact]
    public void L186_IdleFrame_ZeroRenderCalls_ParametersUnchanged()
    {
        var root = new Host();
        var y = new Host { IsRenderBoundary = true };
        y.Add(new Probe(4, 2));
        root.Add(new Probe(4, 2));
        root.Add(y);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        var first = tree.Layers();
        var rcRoot = root.RenderCount;
        var rcY = y.RenderCount;

        tree.Render(); // the idle frame
        var second = tree.Layers();

        Assert.Equal(rcRoot, root.RenderCount);
        Assert.Equal(rcY, y.RenderCount);
        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Same(first[i].Scene, second[i].Scene);
            Assert.Equal(first[i].Parameters, second[i].Parameters); // equality is the change detector
        }
    }

    [Fact]
    public void L187_BoundarySizeChange_SceneRecreated_ZoneRerasters()
    {
        var root = new Canvas();
        var y = new Host { IsRenderBoundary = true, Width = 8, Height = 3 };
        var c = new Probe(4, 2);
        y.Add(c);
        root.Children.Add(y);

        var (manager, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        var scene = tree.GetScene(y);
        var rcC = c.RenderCount;

        y.Width = 12; // size change — scenes don't resize
        manager.Layout(40, 12);
        tree.Render();

        Assert.NotSame(scene, tree.GetScene(y));
        Assert.Equal(rcC + 1, c.RenderCount);
    }

    [Fact]
    public void L188_BoundaryMoved_PositionOnly_ParamsOnly()
    {
        var root = new Canvas();
        var y = new Host { IsRenderBoundary = true, Width = 6, Height = 2 };
        var c = new Probe(4, 2);
        y.Add(c);
        root.Children.Add(y);
        Canvas.SetLeft(y, 1);

        var (manager, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        var (rcY, rcC) = (y.RenderCount, c.RenderCount);

        Canvas.SetLeft(y, 5); // position-only bounds change on a boundary — the cheap layer move
        manager.Layout(40, 12);
        tree.Render();

        Assert.Equal(rcY, y.RenderCount);
        Assert.Equal(rcC, c.RenderCount);
        Assert.Equal(5, tree.Parameters(y).OffsetColumn);
    }

    [Fact]
    public void L189_NonBoundaryMoved_ZoneRerasters()
    {
        var root = new Canvas();
        var x = new Probe(6, 2);
        root.Children.Add(x);
        Canvas.SetLeft(x, 1);

        var (manager, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        var rcX = x.RenderCount;

        Canvas.SetLeft(x, 5); // cells physically move in the raster — why position animations use RenderOffset*
        manager.Layout(40, 12);
        tree.Render();

        Assert.Equal(rcX + 1, x.RenderCount);
    }

    // ───────────────────────────── z-order: zone-base rule, layer order ─────────────────────────────

    [Fact]
    public void L190_ZoneBaseRule_BoundaryCompositesAboveLaterSibling()
    {
        var root = new Canvas();
        var b = new Probe(10, 3) { FillGlyph = "B", IsRenderBoundary = true };
        var s = new Probe(10, 3) { FillGlyph = "S" };
        root.Children.Add(b); // index 0 — earlier in document order
        root.Children.Add(s); // index 1 — later, overlapping B's rect
        Canvas.SetLeft(s, 2);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        var view = new CellBufferView(tree.Composite(40, 12));

        // B's layer composites above ALL of the parent zone's raster — including S, which is later
        // in document order (DEV from strict document-order fidelity, recorded in the matrix).
        Assert.Equal("B", view[3, 1].Grapheme);  // overlap region
        Assert.Equal("S", view[10, 1].Grapheme); // S-only region
    }

    [Fact]
    public void L191_LayerOrder_BoundaryTreeDFS_ZIndexSorted_ZoneSceneLowest()
    {
        var root = new Canvas();
        var a = new Host { IsRenderBoundary = true, ZIndex = 5, Width = 8, Height = 3 };
        var a1 = new Host { IsRenderBoundary = true, Width = 4, Height = 2 };
        a.Add(a1);
        var b = new Host { IsRenderBoundary = true, ZIndex = 0, Width = 8, Height = 3 };
        root.Children.Add(a); // index 0, ZIndex 5
        root.Children.Add(b); // index 1, ZIndex 0

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();

        Assert.Equal(4, tree.LayerCount);
        Assert.Same(root, tree.GetLayerBoundary(0)); // a zone's own scene is the lowest layer of its subtree
        Assert.Same(b, tree.GetLayerBoundary(1));    // (ZIndex, index) sibling sort: B (0,1) before A (5,0)
        Assert.Same(a, tree.GetLayerBoundary(2));
        Assert.Same(a1, tree.GetLayerBoundary(3));   // pre-order DFS of the boundary tree
    }

    // ───────────────────────────── visibility ─────────────────────────────

    [Fact]
    public void L192_HiddenAncestor_BoundaryLayerPublishesEmptyClip_SameFrame()
    {
        var root = new Host();
        var sp = new StackPanel();
        var scp = new ScrollContentPresenter { Content = new Probe(8, 2) }; // the real predicate-⑥ element (T4)
        sp.Children.Add(scp);
        root.Add(sp);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        Assert.Equal(2, tree.LayerCount);
        Assert.NotEqual(Rect.Empty, tree.Parameters(scp).Clip);

        sp.Visibility = Visibility.Hidden;
        tree.Render(); // same frame

        Assert.Equal(Rect.Empty, tree.Parameters(scp).Clip);
        Assert.Equal(2, tree.LayerCount); // layer retained
    }

    [Fact]
    public void L193_BoundaryHiddenVisibleFlips_ParamsOnly()
    {
        var root = new Canvas();
        var y = new Host { IsRenderBoundary = true, Width = 8, Height = 3 };
        var c = new Probe(4, 2);
        y.Add(c);
        root.Children.Add(y);
        Canvas.SetLeft(y, 2);
        Canvas.SetTop(y, 1);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        var clip = tree.Parameters(y).Clip;
        var (rcY, rcC) = (y.RenderCount, c.RenderCount);

        y.Visibility = Visibility.Hidden;
        tree.Render();
        Assert.Equal(Rect.Empty, tree.Parameters(y).Clip);
        Assert.Equal(2, tree.LayerCount);

        y.Visibility = Visibility.Visible;
        tree.Render();
        Assert.Equal(clip, tree.Parameters(y).Clip); // restored
        Assert.Equal(2, tree.LayerCount);

        Assert.Equal(rcY, y.RenderCount); // both flips were parameters-only
        Assert.Equal(rcC, c.RenderCount);
    }

    [Fact]
    public void L194_NonBoundaryHidden_ZoneRerasters_HiddenPaintsNothing()
    {
        var root = new Host();
        var x = new Probe(6, 2) { FillGlyph = "X" };
        var a = new Probe(4, 2);
        root.Add(x);
        root.Add(a);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        var (rcX, rcA) = (x.RenderCount, a.RenderCount);

        x.Visibility = Visibility.Hidden;
        tree.Render();

        Assert.Equal(rcA + 1, a.RenderCount); // the zone re-rastered (cells must be erased)
        Assert.Equal(rcX, x.RenderCount);     // Hidden subtrees paint nothing
    }

    [Fact]
    public void L195_CollapsedBoundary_KeepsScene_EmptyClip_LayerStable_ReexpandRerastersOnce()
    {
        var root = new Canvas();
        var y = new Host { IsRenderBoundary = true, Width = 8, Height = 3 };
        var c = new Probe(4, 2);
        y.Add(c);
        root.Children.Add(y);
        Canvas.SetLeft(y, 2);
        Canvas.SetTop(y, 1);

        var (manager, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        var scene = tree.GetScene(y);
        var rcC = c.RenderCount;

        y.Visibility = Visibility.Collapsed;
        manager.Layout(40, 12);
        tree.Render();

        Assert.Equal(2, tree.LayerCount);                    // the layer slot survives
        Assert.Equal(Rect.Empty, tree.Parameters(y).Clip);
        Assert.Same(scene, tree.GetScene(y));                // zero-size boundary keeps its scene
        Assert.Equal(rcC, c.RenderCount);

        y.Visibility = Visibility.Visible;
        manager.Layout(40, 12);
        tree.Render();

        Assert.Equal(2, tree.LayerCount);
        Assert.Equal(rcC + 1, c.RenderCount);                // re-expand re-rasters once
        Assert.Equal(new Rect(2, 1, 8, 3), tree.Parameters(y).Clip);
    }

    [Fact]
    public void L196_InheritedAffectsRender_FanOutBoundedToAffectedZones()
    {
        var root = new Host();
        var h1 = new Host { IsRenderBoundary = true };
        var p1 = new InkProbe(4, 2);
        h1.Add(p1);
        var h2 = new Host { IsRenderBoundary = true };
        var p2 = new InkProbe(4, 2);
        h2.Add(p2);
        var h3 = new Host { IsRenderBoundary = true }; // no inheriting descendants
        var h3C = new Host();
        h3.Add(h3C);
        root.Add(h1);
        root.Add(h2);
        root.Add(h3);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        var (rc1, rc2, rc3) = (p1.RenderCount, p2.RenderCount, h3C.RenderCount);

        root.SetValue(InkProbe.InkProperty, 7); // one root write fans out to inheriting descendants
        tree.Render();

        Assert.Equal(rc1 + 1, p1.RenderCount); // the two affected zones re-raster
        Assert.Equal(rc2 + 1, p2.RenderCount);
        Assert.Equal(rc3, h3C.RenderCount);    // the uninvolved zone does not
    }

    // ───────────────────────────── hit testing ─────────────────────────────

    [Fact]
    public void L197_HitTest_MirrorsCompositeOrder_IncludingZoneBaseRule()
    {
        var root = new Canvas();
        var b = new Probe(10, 3) { IsRenderBoundary = true };
        var s = new Probe(10, 3);
        root.Children.Add(b);
        root.Children.Add(s);
        Canvas.SetLeft(s, 2);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();

        Assert.Same(b, tree.HitTest(3, 1));  // the overlap: B's layer is topmost (zone-base rule)
        Assert.Same(s, tree.HitTest(10, 1)); // S-only region falls to the root zone's descent
    }

    [Fact]
    public void L198_HitTest_LayerOffsetTransform_OldPositionFallsThrough()
    {
        var root = new Canvas();
        var y = new Probe(4, 2) { IsRenderBoundary = true, RenderOffsetColumn = 3 };
        root.Children.Add(y);
        Canvas.SetLeft(y, 1);
        Canvas.SetTop(y, 1);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();

        Assert.Same(y, tree.HitTest(5, 2));    // the offset position: layer offset (1+3, 1) transforms the point
        Assert.Same(root, tree.HitTest(1, 1)); // the old layout position: outside P(Y).Clip — the layer skips
    }

    [Theory]
    [InlineData("hit-test-invisible-container")]
    [InlineData("hidden-subtree")]
    [InlineData("hit-test-core-false")]
    public void L199_HitTest_Gating(string scenario)
    {
        var root = new Canvas();
        var (_, tree) = scenario switch
        {
            "hit-test-invisible-container" => BuildContainerCase(root),
            "hidden-subtree" => BuildHiddenCase(root),
            _ => BuildHitTestCoreCase(root)
        };
        tree.Render();

        switch (scenario)
        {
            case "hit-test-invisible-container":
                // ① the gate applies to the LEAF, not the subtree (DEV from WPF): the child is still
                // hit; the container itself never is.
                Assert.IsType<Probe>(tree.HitTest(1, 1), exactMatch: true);
                Assert.Same(root, tree.HitTest(8, 3));
                break;
            case "hidden-subtree":
                // ② Hidden excludes the whole subtree.
                Assert.Same(root, tree.HitTest(1, 1));
                break;
            default:
                // ③ HitTestCore false falls through to the element below.
                var hit = Assert.IsType<Probe>(tree.HitTest(1, 1), exactMatch: true);
                Assert.Equal("bottom", hit.LogName);
                break;
        }

        static (LayoutManager, RenderTree) BuildContainerCase(Canvas root)
        {
            var container = new Host { IsHitTestVisible = false, Width = 10, Height = 4 };
            var child = new Probe(4, 2)
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            container.Add(child);
            root.Children.Add(container);
            return LayoutFixture.CreateRenderRoot(root);
        }

        static (LayoutManager, RenderTree) BuildHiddenCase(Canvas root)
        {
            var container = new Host { Width = 10, Height = 4, Visibility = Visibility.Hidden };
            container.Add(new Probe(4, 2));
            root.Children.Add(container);
            return LayoutFixture.CreateRenderRoot(root);
        }

        static (LayoutManager, RenderTree) BuildHitTestCoreCase(Canvas root)
        {
            var bottom = new Probe(6, 2) { LogName = "bottom" };
            var top = new Probe(6, 2) { LogName = "top", HitTestCoreOverride = (_, _) => false };
            root.Children.Add(bottom);
            root.Children.Add(top);
            return LayoutFixture.CreateRenderRoot(root);
        }
    }

    [Fact]
    public void L200_HitTest_ZIndexDescent_OverflowHittable_ZeroAllocation()
    {
        var root = new Canvas();
        var p1 = new Probe(6, 2) { LogName = "p1" };
        var p2 = new Probe(6, 2) { LogName = "p2", ZIndex = 5 };
        root.Children.Add(p1);
        root.Children.Add(p2);
        var panel = new StackPanel { Width = 4 };
        var wide = new Probe(12, 2) { LogName = "wide" };
        panel.Children.Add(wide);
        root.Children.Add(panel);
        Canvas.SetLeft(panel, 10);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();

        // Intra-zone descent is descending (ZIndex, index).
        var hit = Assert.IsType<Probe>(tree.HitTest(1, 1), exactMatch: true);
        Assert.Equal("p2", hit.LogName);

        // The overflowing child is hittable outside its parent's rect (LD17 cross-slot overflow;
        // live Bounds, no parent-rect pre-clip — consistent with the painter).
        var overflow = Assert.IsType<Probe>(tree.HitTest(20, 1), exactMatch: true);
        Assert.Equal("wide", overflow.LogName);

        // 0 bytes per HitTest (warmed loop; repo norm — GetAllocatedBytesForCurrentThread deltas).
        for (var i = 0; i < 64; i++)
        {
            tree.HitTest(1, 1);
            tree.HitTest(20, 1);
            tree.HitTest(39, 11);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            tree.HitTest(1, 1);
            tree.HitTest(20, 1);
            tree.HitTest(39, 11);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ───────────────────────────── idle-guard regressions ─────────────────────────────

    [Fact]
    public void Regression_DeferredDirtyBoundary_DoesNotPinPendingRenderWork()
    {
        var root = new Canvas();
        var y = new Host { IsRenderBoundary = true, Width = 8, Height = 3 };
        var c = new Probe(4, 2);
        y.Add(c);
        root.Children.Add(y);

        var (manager, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        Assert.False(tree.HasPendingRenderWork);

        // Collapse: arrange publishes Rect.Empty → InvalidateVisual → the zone goes raster-dirty,
        // but the pass DEFERS the raster (zero-area). The deferred dirty bit must not pin the
        // Phase-7 idle guard true (the production loop would spin at frame rate forever).
        y.Visibility = Visibility.Collapsed;
        manager.Layout(40, 12);
        tree.Render();
        Assert.False(tree.HasPendingRenderWork);

        y.Visibility = Visibility.Visible;
        manager.Layout(40, 12);
        tree.Render();
        Assert.False(tree.HasPendingRenderWork);

        // Same for a Hidden boundary whose raster goes dirty while hidden.
        y.Visibility = Visibility.Hidden;
        tree.Render();
        c.InvalidateVisual();
        tree.Render(); // raster deferred — the boundary is Hidden
        Assert.False(tree.HasPendingRenderWork);

        // Re-expose: the deferred dirty bit was preserved (not lost), so the flip re-rasters once.
        var rc = c.RenderCount;
        y.Visibility = Visibility.Visible;
        Assert.True(tree.HasPendingRenderWork); // the composite-lane flip re-arms the pass
        tree.Render();
        Assert.Equal(rc + 1, c.RenderCount);
        Assert.False(tree.HasPendingRenderWork);
    }
}
