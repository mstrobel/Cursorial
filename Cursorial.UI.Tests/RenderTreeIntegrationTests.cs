using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Tests.UI;

/// <summary>
/// T3 integration coverage beyond the §11 matrix rows: zero-allocation clean frames (the stage
/// invariant), the DEBUG render-pass read-only guard, <see cref="RenderContext"/> coordinate
/// translation and figure scoping, the <c>Panel.Background</c> <c>FillOpaque</c> pin, window-offset
/// folding in <c>CollectLayers</c>, and render-tree lifecycle.
/// </summary>
public class RenderTreeIntegrationTests
{
    [Fact]
    public void CleanFrame_RunRenderPass_And_CollectLayers_AllocateNothing()
    {
        var root = new Host();
        var y = new Host { IsRenderBoundary = true, Opacity = 0.8 };
        y.Add(new Probe(6, 2));
        root.Add(new Probe(4, 2));
        root.Add(y);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();

        var layers = new List<SceneLayer>(16);
        for (var i = 0; i < 64; i++)
        {
            tree.RunRenderPass();
            layers.Clear();
            tree.CollectLayers(layers);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            tree.RunRenderPass(); // clean tree: no rebuild, no raster, parameter walk only
            layers.Clear();
            tree.CollectLayers(layers);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

#if DEBUG
    [Fact]
    public void RenderPass_IsReadOnly_WritesAndInvalidationsThrow()
    {
        var root = new Host();
        var probe = new Probe(4, 2);
        root.Add(probe);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        var verified = false;
        probe.OnRender = (p, _) =>
        {
            Assert.Throws<InvalidOperationException>(() => p.Width = 9);            // SetValue
            Assert.Throws<InvalidOperationException>(() => p.ClearValue(UIElement.WidthProperty));
            Assert.Throws<InvalidOperationException>(p.InvalidateMeasure);
            Assert.Throws<InvalidOperationException>(p.InvalidateArrange);
            Assert.Throws<InvalidOperationException>(p.InvalidateVisual);
            Assert.Throws<InvalidOperationException>(p.InvalidateComposite);
            Assert.Throws<InvalidOperationException>(() => root.Add(new Probe(1, 1))); // tree mutation
            verified = true;
        };

        tree.Render();
        Assert.True(verified);
    }
#endif

    [Fact]
    public void RenderContext_TranslatesAllCoordinates_ElementLocalToZone()
    {
        var root = new Canvas();
        var probe = new Probe(5, 2);
        root.Children.Add(probe);
        Canvas.SetLeft(probe, 3);
        Canvas.SetTop(probe, 2);
        probe.OnRender = (_, ctx) =>
        {
            Assert.Equal(new Size(5, 2), ctx.Size);
            Assert.Equal(new Rect(0, 0, 5, 2), ctx.Bounds);
            ctx.Set(0, 0, "X", default); // element-local origin
            ctx.DrawText(1, 1, "ab", Color.FromRgb(255, 255, 255));
        };

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        var view = new CellBufferView(tree.Composite(40, 12));

        Assert.Equal("X", view[3, 2].Grapheme);  // origin-add translation: (0,0) → (3,2)
        Assert.Equal("a", view[4, 3].Grapheme);  // DrawText translated too (the push-stack gap is bridged here)
        Assert.Equal("b", view[5, 3].Grapheme);
    }

    [Fact]
    public void PanelBackground_PaintsFillOpaque_OccludesLowerLayerGlyphs()
    {
        var root = new Host();
        var under = new Probe(10, 3) { FillGlyph = "G" }; // glyphs in the root zone
        var panel = new StackPanel
        {
            IsRenderBoundary = true, // own layer, composites above the root zone
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            Width = 6,
            Height = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        root.Add(under);
        root.Add(panel);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        var view = new CellBufferView(tree.Composite(40, 12));

        // FillOpaque writes space-bearing cells: the glyph beneath is hidden (doc §5.5 pinned
        // surface rule — FillRectangle would let "G" show through the panel).
        Assert.Equal(" ", view[1, 1].Grapheme);
        Assert.Equal("G", view[8, 1].Grapheme); // outside the panel the root zone shows through
    }

    [Fact]
    public void RenderContext_UserFigureScopes_DoNotNest_DoubleDisposeSafe()
    {
        var root = new Host();
        var probe = new Probe(8, 4);
        root.Add(probe);
        var verified = false;
        probe.OnRender = (_, ctx) =>
        {
            var scope = ctx.BeginFigure();
            Assert.Throws<InvalidOperationException>(() => ctx.BeginFigure()); // scopes do not nest
            scope.Dispose(); // closes the user figure, reopens the ambient figure
            scope.Dispose(); // double dispose is a safe no-op
            using (ctx.BeginFigure(new Rect(0, 0, 4, 2)))
            {
                ctx.DrawLine(0, 0, 3, 0, Color.FromRgb(200, 0, 0));
            }

            ctx.DrawLine(0, 2, 3, 2, Color.FromRgb(0, 200, 0)); // back in the ambient figure
            verified = true;
        };

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        Assert.True(verified);
    }

    [Fact]
    public void RenderContext_CapturedBeyondTheRenderCall_Throws()
    {
        var root = new Host();
        var probe = new Probe(4, 2);
        root.Add(probe);
        RenderContext? captured = null;
        probe.OnRender = (_, ctx) => captured = ctx;

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();

        Assert.NotNull(captured);
        Assert.Throws<InvalidOperationException>(() => captured.Set(0, 0, "X", default));
    }

    [Fact]
    public void CollectLayers_FoldsWindowOffsetAndOpacity()
    {
        var root = new Host();
        var y = new Host { IsRenderBoundary = true, Opacity = 0.5 };
        y.Add(new Probe(4, 2));
        root.Add(y);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();

        var layers = new List<SceneLayer>();
        tree.CollectLayers(layers, windowOffsetColumn: 5, windowOffsetRow: 3, windowOpacity: 0.5);

        Assert.Equal(2, layers.Count);
        var rootLayer = layers[0].Parameters;
        Assert.Equal(5, rootLayer.OffsetColumn);
        Assert.Equal(3, rootLayer.OffsetRow);
        Assert.Equal((byte)128, rootLayer.Opacity);                  // 1.0 × window 0.5
        Assert.Equal(new Rect(5, 3, 40, 12), rootLayer.Clip);        // clip translated by the window position

        var yLayer = layers[1].Parameters;
        Assert.Equal((byte)64, yLayer.Opacity);                      // 0.5 × window 0.5 → round(255·0.25)
    }

    [Fact]
    public void Detach_ReturnsScenes_TreeUnusable_RootCanHostANewTree()
    {
        var pool = new ScenePool();
        var root = new Host();
        root.Add(new Probe(4, 2));
        var manager = LayoutFixture.CreateRoot(root);
        manager.Layout(20, 6);
        var tree = new RenderTree(root, pool, OutputCapabilities.None);
        tree.Render();
        Assert.Equal(1, tree.LayerCount);

        tree.Detach();
        Assert.Throws<InvalidOperationException>(tree.RunRenderPass);
        Assert.Equal(0, tree.LayerCount);

        // The root is reusable: a fresh tree rebuilds all single-shot render state.
        var second = new RenderTree(root, pool, OutputCapabilities.None);
        second.Render();
        Assert.Equal(1, second.LayerCount);
    }

    [Fact]
    public void BoundaryHiddenAtFirstRaster_RasterDeferred_VisibleFlipShowsContent()
    {
        var root = new Host();
        var y = new Probe(6, 2) { IsRenderBoundary = true, FillGlyph = "Y", Visibility = Visibility.Hidden };
        root.Add(y);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        Assert.Equal(0, y.RenderCount); // raster deferred — an empty scene must not be cached
        Assert.Equal(Rect.Empty, tree.Parameters(y).Clip);

        y.Visibility = Visibility.Visible;
        tree.Render();

        Assert.Equal(1, y.RenderCount); // the surviving dirty bit rasters on the unhide frame
        var view = new CellBufferView(tree.Composite(40, 12));
        Assert.Equal("Y", view[1, 1].Grapheme);
    }

    [Fact]
    public void DetachedSubtree_Reattach_RebuildsZones_StickyPromotionCleared()
    {
        var root = new Host();
        var x = new Host();
        x.Add(new Probe(4, 2));
        root.Add(x);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        x.Opacity = 0.5;
        tree.Render();
        Assert.Equal(2, tree.LayerCount);

        x.Opacity = 1.0; // sticky while attached…
        tree.Render();
        Assert.Equal(2, tree.LayerCount);

        root.Remove(x); // …until detach clears the promotion
        tree.Render();
        Assert.Equal(1, tree.LayerCount);

        root.Add(x); // no live predicate holds anymore — rejoins the parent zone
        tree.Render();
        Assert.Equal(1, tree.LayerCount);
    }
}
