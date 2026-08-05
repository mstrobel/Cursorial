using System.Runtime.InteropServices;

using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

using Style = Cursorial.Output.Style;

namespace Cursorial.Tests.UI;

/// <summary>
/// What a change <b>inside</b> an opacity group costs at the screen pass. A group collapses its whole
/// boundary subtree into one layer, and a layer that reports a new <see cref="Scene.RasterVersion"/>
/// costs its entire footprint — so without the damage hand-off, a caret blink in a background editor
/// recomposites the whole window every frame. <see cref="SceneLayer.Damage"/> is that hand-off: the
/// inner pass reports the cells it rewrote and the screen pass repaints exactly those.
/// </summary>
/// <remarks>
/// The reason this ships with the feature rather than after it: an inactive window is not an idle one
/// (it is simply not the <em>active</em> one), and <c>ScrollContentPresenter</c>, <c>TextPresenter</c>
/// and <c>DrawnContentPresenter</c> are all render boundaries from attach — so a translucent window's
/// group subtree is essentially its whole visual tree, and every one of its blinking carets, spinners
/// and progress bars would pay for the window.
/// </remarks>
public class RenderGroupDamageTests
{
    private static readonly Color Backdrop = Color.FromRgb(0, 255, 0);
    private static readonly Color ChromeColor = Color.FromRgb(200, 40, 40);
    private static readonly Color BodyColor = Color.FromRgb(40, 40, 200);
    private static readonly Color BlipColor = Color.FromRgb(240, 240, 40);

    // The one-cell member sits here in window coordinates in every fixture below, at every group size.
    private static readonly Rect BlipCell = new(5, 3, 1, 1);

    // ───────────────────────────── the damage a change actually reports ─────────────────────────────

    /// <summary>
    /// A one-cell change inside a group must reach the screen pass as one cell of damage, not as the
    /// group's footprint. The damage is in the group <b>surface's</b> own coordinates, which for a group
    /// rooted at the window origin coincide with the window's.
    /// </summary>
    [Fact]
    public void AOneCellChangeInsideAGroup_ReportsOneCellOfDamage_NotTheGroupFootprint()
    {
        var scene = new GroupScene(40, 12);
        var screen = new Screen(40, 12, Backdrop);

        scene.Tree.Render();
        Assert.True(scene.Tree.IsGroupRoot(scene.Group));
        screen.Present(scene.Tree);

        scene.BlinkBlip();
        scene.Tree.Render();
        screen.Present(scene.Tree);

        Assert.Equal(BlipCell, screen.GroupLayer.Damage);
    }

    /// <summary>
    /// And the screen pass believes it: only that cell is reset to base and recomposited. This is the
    /// whole point — the number below used to be the group's footprint, 480 cells for this 40×12 group.
    /// </summary>
    [Fact]
    public void AOneCellChangeInsideAGroup_RecompositesOneCell_NotTheGroupFootprint()
    {
        var scene = new GroupScene(40, 12);
        var screen = new Screen(40, 12, Backdrop);

        scene.Tree.Render();
        screen.Present(scene.Tree);

        scene.BlinkBlip();
        scene.Tree.Render();

        Assert.Equal(BlipCell, screen.Present(scene.Tree));
    }

    /// <summary>
    /// The property the fix is really about: the cost of a change inside a group is the size of the
    /// <b>change</b>, not the size of the group. A 160×48 full-screen translucent window and a 40×12 one
    /// pay the same for the same blinking cell.
    /// </summary>
    [Fact]
    public void TheCostOfAChangeInsideAGroup_DoesNotScaleWithTheGroup()
    {
        Assert.Equal(1, RecompositedCellsForOneBlink(40, 12));
        Assert.Equal(1, RecompositedCellsForOneBlink(160, 48));

        static int RecompositedCellsForOneBlink(int columns, int rows)
        {
            var scene = new GroupScene(columns, rows);
            var screen = new Screen(columns, rows, Backdrop);

            scene.Tree.Render();
            screen.Present(scene.Tree);

            scene.BlinkBlip();
            scene.Tree.Render();
            var rewritten = screen.Present(scene.Tree);
            return rewritten.Columns * rewritten.Rows;
        }
    }

    /// <summary>
    /// The caret-blink shape: one cell, alternating, forever. Every frame must cost the same one cell —
    /// a per-frame cost that drifted upwards (or that was the window's footprint to begin with) is what
    /// makes a background editor's caret expensive.
    /// </summary>
    [Fact]
    public void ACaretShapedBlinkInsideATranslucentWindow_CostsOneCellEveryFrame()
    {
        var scene = new GroupScene(160, 48);
        var screen = new Screen(160, 48, Backdrop);

        scene.Tree.Render();
        screen.Present(scene.Tree);

        for (var frame = 0; frame < 24; frame++)
        {
            scene.BlinkBlip();
            scene.Tree.Render();
            Assert.Equal(BlipCell, screen.Present(scene.Tree));
        }
    }

    /// <summary>
    /// The same shape end to end, through the real window manager and frame renderer: a caret-shaped
    /// blink inside a translucent window emits the same handful of wire bytes whatever the window's size,
    /// and the window's whole visual tree is inside the group while it does.
    /// </summary>
    /// <remarks>
    /// The wire diff was already cell-exact — the frame renderer compares back to front, and cells that a
    /// wider repaint recomposites to the values they already had emit nothing — so this number did not
    /// move with the damage hand-off and is not the measurement of it (that is the recomposited-cell count
    /// pinned above). It is here because it is the statement a regression would be reported as, and
    /// because it is the one that would catch damage narrowing that is too <em>narrow</em> in production
    /// coordinates: a stale cell left behind by an under-reported region shows up as bytes that never come.
    /// </remarks>
    [Fact]
    public void ACaretShapedBlinkInsideATranslucentWindow_EmitsTheSameFewBytesWhateverTheWindowSize()
    {
        var small = BlinkFrameBytes(80, 24);
        var large = BlinkFrameBytes(200, 50);

        // 49 and 52 bytes on alternating frames at the time of writing — one cursor address plus one
        // styled cell, the three bytes of difference being the two colours' decimal lengths — and the
        // SAME sequence at 1'920 cells and at 10'000. The bound is loose because the exact figure belongs
        // to the frame renderer's encoding, not to this feature; what this test owns is that it does not
        // grow with the window.
        Assert.Equal(small, large);
        Assert.All(large, count => Assert.InRange(count, 1, 64));

        static int[] BlinkFrameBytes(int columns, int rows)
        {
            using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
                                                   {
                                                       InitialSize = new Size(columns, rows),
                                                       CaptureFrameBytes = true
                                                   });

            var desktop = new Swatch(Backdrop);
            var canvas = new Canvas();
            var group = new Swatch(ChromeColor) { Opacity = 0.5, Width = columns, Height = rows };
            var inner = new Canvas { Width = columns, Height = rows };
            var body = new Swatch(BodyColor) { IsRenderBoundary = true, Width = columns, Height = rows };
            var blip = new Swatch(BlipColor) { IsRenderBoundary = true, Width = 1, Height = 1 };

            inner.Children.Add(body);
            inner.Children.Add(blip);
            Canvas.SetLeft(blip, BlipCell.Column);
            Canvas.SetTop(blip, BlipCell.Row);
            group.Add(inner);
            canvas.Children.Add(group);
            desktop.Add(canvas);

            host.ShowRoot(desktop);
            Assert.True(host.RunUntilIdle());
            Assert.True(host.Application.WindowManager!.Tree!.IsGroupRoot(group));

            var bytes = new int[8];
            for (var frame = 0; frame < bytes.Length; frame++)
            {
                blip.Color = frame % 2 == 0 ? Color.FromRgb(0, 0, 0) : BlipColor;
                blip.InvalidateVisual();
                host.RunFrame();
                bytes[frame] = host.LastFrameBytes.Length;
            }

            return bytes;
        }
    }

    /// <summary>
    /// An idle frame with a live group still costs nothing at all: the members do not re-raster, the
    /// inner composite early-outs without bumping the surface's <see cref="Scene.RasterVersion"/>, and
    /// the screen pass early-outs in turn. The chained early-out has to survive the damage hand-off —
    /// a group that reported damage every frame would defeat it just as thoroughly as a full footprint.
    /// </summary>
    [Fact]
    public void AnIdleFrameWithALiveGroup_RecompositesNothing()
    {
        var scene = new GroupScene(40, 12);
        var screen = new Screen(40, 12, Backdrop);

        scene.Tree.Render();
        screen.Present(scene.Tree);

        for (var frame = 0; frame < 8; frame++)
        {
            scene.Tree.Render();
            Assert.Equal(Rect.Empty, screen.Present(scene.Tree));
        }
    }

    // ───────────────────────────── where damage must NOT be believed ─────────────────────────────

    /// <summary>
    /// A change to the group's <b>own</b> parameters — a move, a fade, a re-clip — repaints the whole
    /// footprint however little the inner pass rewrote. The damage describes cells of the surface; it
    /// says nothing about where the surface lands or how strongly it blends, and those move every cell.
    /// </summary>
    [Fact]
    public void AChangeToTheGroupsOwnOpacity_RepaintsTheWholeFootprint()
    {
        var scene = new GroupScene(40, 12);
        var screen = new Screen(40, 12, Backdrop);

        scene.Tree.Render();
        screen.Present(scene.Tree);

        // Blink first, so a stale one-cell damage is sitting on the zone and would narrow the repaint
        // if the fade were allowed to take that path.
        scene.BlinkBlip();
        scene.Tree.Render();
        screen.Present(scene.Tree);

        scene.Group.Opacity = 0.25;
        scene.Tree.Render();

        Assert.Equal(new Rect(0, 0, 40, 12), screen.Present(scene.Tree));
    }

    /// <summary>A group that MOVES repaints what it vacated as well as what it now covers.</summary>
    [Fact]
    public void AGroupThatMoves_RepaintsTheVacatedFootprintToo()
    {
        var scene = new GroupScene(40, 12, groupColumns: 10, groupRows: 4);
        var screen = new Screen(40, 12, Backdrop);

        scene.Tree.Render();
        screen.Present(scene.Tree);
        scene.BlinkBlip();
        scene.Tree.Render();
        screen.Present(scene.Tree);

        scene.MoveGroupTo(6, 0);
        scene.Tree.Render();

        // Vacated (0,0,10,4) ∪ new (6,0,10,4).
        Assert.Equal(new Rect(0, 0, 16, 4), screen.Present(scene.Tree));
    }

    /// <summary>
    /// The first composite after a group materialises repaints everything: the emitted layer count moves,
    /// which is the compositor's own full-recomposite signal, and the surface it is being handed is one
    /// it has never seen.
    /// </summary>
    [Fact]
    public void TheFirstCompositeAfterMaterialisation_RepaintsTheWholeTarget()
    {
        var scene = new GroupScene(40, 12, opacity: 1.0);
        var screen = new Screen(40, 12, Backdrop);

        scene.Tree.Render();
        Assert.False(scene.Tree.IsGroupRoot(scene.Group));
        screen.Present(scene.Tree);

        scene.Group.Opacity = 0.5;
        scene.Tree.Render();
        Assert.True(scene.Tree.IsGroupRoot(scene.Group));

        Assert.Equal(new Rect(0, 0, 40, 12), screen.Present(scene.Tree));
    }

    /// <summary>
    /// A screen pass that skipped a frame must not believe a damage rect that only describes the last of
    /// the inner composites it missed. The surface's <see cref="Scene.RasterVersion"/> is the ledger:
    /// exactly one bump means the region is the whole story, more than one means it is not.
    /// </summary>
    [Fact]
    public void DamageIsIgnoredWhenTheScreenPassMissedAFrame()
    {
        var scene = new GroupScene(40, 12);
        var screen = new Screen(40, 12, Backdrop);

        scene.Tree.Render();
        screen.Present(scene.Tree);

        // Two render passes, each rewriting a different cell, with no composite in between.
        scene.BlinkBlip();
        scene.Tree.Render();
        scene.Body.Color = Color.FromRgb(10, 10, 10);
        scene.Body.InvalidateVisual();
        scene.Tree.Render();

        // The reported damage is only the body's footprint; the blip's cell is inside it here, but the
        // compositor cannot know that and must not gamble on it.
        Assert.Equal(new Rect(0, 0, 40, 12), screen.Present(scene.Tree));
    }

    // ───────────────────────────── correctness is not traded for it ─────────────────────────────

    /// <summary>
    /// The narrowed repaint must be pixel-for-pixel what a from-scratch composite of the same tree
    /// produces — checked after every step of a mixed sweep (one-cell blinks, whole-member repaints,
    /// a fade, a move), because a damage rect that is one cell too small is a stale cell that survives
    /// on the terminal until something unrelated happens to repaint it.
    /// </summary>
    [Fact]
    public void TheNarrowedRepaint_IsBitIdenticalToAFullComposite_AcrossAMixedSweep()
    {
        var scene = new GroupScene(40, 12, groupColumns: 20, groupRows: 8);
        var incremental = new Screen(40, 12, Backdrop);

        var steps = new Action[]
                    {
                        () => scene.BlinkBlip(),
                        () => scene.BlinkBlip(),
                        () => { scene.Body.Color = Color.FromRgb(1, 2, 3); scene.Body.InvalidateVisual(); },
                        () => scene.Group.Opacity = 0.25,
                        () => scene.BlinkBlip(),
                        () => scene.MoveGroupTo(7, 0),
                        () => scene.BlinkBlip(),
                        () => scene.MoveGroupTo(7, 2),
                        () => scene.Group.Opacity = 1.0,   // sticky: still a group, now an identity one
                        () => scene.BlinkBlip()
                    };

        scene.Tree.Render();
        incremental.Present(scene.Tree);

        foreach (var step in steps)
        {
            step();
            scene.Tree.Render();
            incremental.Present(scene.Tree);

            // A fresh compositor over a fresh target is the reference: it has no state to narrow with.
            var reference = new Screen(40, 12, Backdrop);
            reference.Present(scene.Tree);

            AssertBuffersEqual(reference.Target, incremental.Target);
        }
    }

    /// <summary>
    /// Damage carries through a nested group too. To the enclosing group an inner group is one member
    /// whose version bumps for anything beneath it, so without the inward hand-off a translucent panel
    /// inside a translucent window would re-blend the whole panel into the window's surface per tick.
    /// </summary>
    [Fact]
    public void ANestedGroupCarriesItsDamageInwards()
    {
        var root = new Canvas();
        var outer = new Swatch(ChromeColor) { Opacity = 0.5, Width = 40, Height = 12 };
        var canvas = new Canvas { Width = 40, Height = 12 };
        var inner = new Swatch(BodyColor) { Opacity = 0.5, Width = 40, Height = 12 };
        var innerCanvas = new Canvas { Width = 40, Height = 12 };
        var big = new Swatch(BodyColor) { IsRenderBoundary = true, Width = 40, Height = 12 };
        var blip = new Swatch(BlipColor) { IsRenderBoundary = true, Width = 1, Height = 1 };

        innerCanvas.Children.Add(big);
        innerCanvas.Children.Add(blip);
        Canvas.SetLeft(blip, BlipCell.Column);
        Canvas.SetTop(blip, BlipCell.Row);
        inner.Add(innerCanvas);
        canvas.Children.Add(inner);
        outer.Add(canvas);
        root.Children.Add(outer);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root, 40, 12);
        tree.Render();
        Assert.True(tree.IsGroupRoot(outer));
        Assert.True(tree.IsGroupRoot(inner));

        var screen = new Screen(40, 12, Backdrop);
        screen.Present(tree);

        blip.Color = Color.FromRgb(9, 9, 9);
        blip.InvalidateVisual();
        tree.Render();

        // One cell out of the inner group, one cell out of the outer, one cell on the screen.
        Assert.Equal(BlipCell, screen.Present(tree));
        Assert.Equal(BlipCell, screen.GroupLayer.Damage);
    }

    /// <summary>
    /// A plain (non-group) layer never claims damage. Its raster is whole-scene by construction
    /// (<see cref="Scene.Draw"/> wipes and redraws), so "unknown" is the only honest answer and the
    /// full-footprint path stays exactly what it was.
    /// </summary>
    [Fact]
    public void AnOrdinaryZoneLayerDeclaresNoDamage()
    {
        var scene = new GroupScene(40, 12);
        var screen = new Screen(40, 12, Backdrop);
        scene.Tree.Render();
        screen.Present(scene.Tree);

        Assert.All(screen.Layers.Where(layer => !ReferenceEquals(layer.Scene, screen.GroupLayer.Scene)),
                   layer => Assert.Null(layer.Damage));
    }

    // ───────────────────────────── fixtures ─────────────────────────────

    /// <summary>
    /// <c>root(Canvas) → group(Swatch, translucent) → Canvas → { body(boundary, group-sized),
    /// blip(boundary, 1×1) }</c>. The group root is translucent and has descendant boundaries, so it
    /// materialises; <see cref="BlinkBlip"/> re-rasters exactly one 1×1 zone inside it.
    /// </summary>
    private sealed class GroupScene
    {
        internal GroupScene(int columns, int rows, int? groupColumns = null, int? groupRows = null, double opacity = 0.5)
        {
            var width = groupColumns ?? columns;
            var height = groupRows ?? rows;

            var root = new Canvas();
            Group = new Swatch(ChromeColor) { IsRenderBoundary = true, Opacity = opacity, Width = width, Height = height };
            var canvas = new Canvas { Width = width, Height = height };
            Body = new Swatch(BodyColor) { IsRenderBoundary = true, Width = width, Height = height };
            Blip = new Swatch(BlipColor) { IsRenderBoundary = true, Width = 1, Height = 1 };

            canvas.Children.Add(Body);
            canvas.Children.Add(Blip);
            Canvas.SetLeft(Blip, BlipCell.Column);
            Canvas.SetTop(Blip, BlipCell.Row);
            Group.Add(canvas);
            root.Children.Add(Group);

            (_manager, Tree) = LayoutFixture.CreateRenderRoot(root, columns, rows);
            _columns = columns;
            _rows = rows;
        }

        private readonly LayoutManager _manager;
        private readonly int _columns;
        private readonly int _rows;

        internal RenderTree Tree { get; }

        internal Swatch Group { get; }

        internal Swatch Body { get; }

        internal Swatch Blip { get; }

        /// <summary>Moves the group by re-running layout, the way a real reposition reaches the tree.</summary>
        internal void MoveGroupTo(int column, int row)
        {
            Canvas.SetLeft(Group, column);
            Canvas.SetTop(Group, row);
            _manager.Layout(_columns, _rows);
        }

        /// <summary>Re-rasters the 1×1 zone and nothing else — the caret-blink shape.</summary>
        internal void BlinkBlip()
        {
            Blip.Color = Blip.Color == BlipColor ? Color.FromRgb(0, 0, 0) : BlipColor;
            Blip.InvalidateVisual();
        }
    }

    /// <summary>
    /// A retained screen: one compositor and one target across frames, so the incremental dirty-union
    /// machinery is actually exercised. <see cref="Present"/> reports the region the compositor reset and
    /// recomposited — the cost the damage hand-off is there to shrink.
    /// </summary>
    private sealed class Screen(int columns, int rows, Color backdrop)
    {
        private readonly SceneCompositor _compositor = new(Style.Default.WithBackground(backdrop));
        private readonly List<SceneLayer> _layers = [];

        internal CellBuffer Target { get; } = new(columns, rows);

        internal IReadOnlyList<SceneLayer> Layers => _layers;

        /// <summary>The group's emitted layer — the last one, in every fixture here.</summary>
        internal SceneLayer GroupLayer => _layers[^1];

        internal Rect Present(RenderTree tree)
        {
            Target.ClearDirty();
            _layers.Clear();
            tree.CollectLayers(_layers);
            _compositor.Composite(CollectionsMarshal.AsSpan(_layers), new CellBufferView(Target));

            var rewritten = Rect.Empty;
            foreach (var region in Target.DirtyRegions)
                rewritten = rewritten.IsEmpty ? region : Union(rewritten, region);

            return rewritten;
        }

        private static Rect Union(in Rect a, in Rect b)
        {
            var column = Math.Min(a.Column, b.Column);
            var row = Math.Min(a.Row, b.Row);
            return new Rect(column, row, Math.Max(a.ColumnEnd, b.ColumnEnd) - column, Math.Max(a.RowEnd, b.RowEnd) - row);
        }
    }

    /// <summary>A <see cref="Host"/> that fills its bounds with one opaque colour.</summary>
    private sealed class Swatch(Color color) : Host
    {
        internal Color Color { get; set; } = color;

        protected override void Render(RenderContext context)
        {
            base.Render(context);
            context.FillOpaque(context.Bounds, Color);
        }
    }

    private static void AssertBuffersEqual(CellBuffer expected, CellBuffer actual)
    {
        for (var row = 0; row < expected.Rows; row++)
        for (var column = 0; column < expected.Columns; column++)
        {
            if (expected[column, row] == actual[column, row])
                continue;

            Assert.Fail($"cell ({column}, {row}) differs: expected {expected[column, row]}, got {actual[column, row]}");
        }
    }
}
