using System.Runtime.InteropServices;

using System.Buffers;

using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fragments;
using Cursorial.Rendering.Media;
using Cursorial.Terminal;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI;

/// <summary>
/// What an opacity <b>group</b> actually composites to. The predicate and the layer bookkeeping live in
/// <see cref="RenderGroupPredicateTests"/>; this file is about pixels — a group root's subtree composites
/// into a private surface at full strength and the root's opacity is applied once to that surface, so a
/// translucent ancestor is blended exactly once no matter how many descendants overlap it.
/// </summary>
public class RenderGroupCompositingTests
{
    private static readonly Color Green = Color.FromRgb(0, 255, 0);
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);
    private static readonly Color White = Color.FromRgb(255, 255, 255);

    // ───────────────────────────── the maintainer's repro ─────────────────────────────

    /// <summary>
    /// The bug, in the smallest shape that shows it: an <b>opaque</b> boundary child inside an
    /// <c>Opacity = 0.5</c> ancestor, over a green backdrop. The ancestor's fade belongs to the composed
    /// subtree, so the child — which covers the ancestor completely — must reach the screen as pure blue
    /// faded once: <c>rgb(0, 127, 128)</c>.
    /// </summary>
    /// <remarks>
    /// The flat model gave the ancestor's opacity to every descendant layer as a pre-multiplied product and
    /// blended each independently onto the shared target, so the ancestor's own red was blended in first and
    /// the child's blue was then blended over that at half strength — the ancestor showing through an opaque
    /// child. That is <c>rgb(63, 63, 128)</c>, asserted below through the feature gate so the two numbers
    /// stay side by side.
    /// </remarks>
    [Fact]
    public void OpaqueChildInsideATranslucentAncestor_BlendsTheAncestorOnceNotTwice()
    {
        var (_, tree, x, _) = CreateAncestorOverChild();
        tree.Render();

        Assert.True(tree.IsGroupRoot(x));

        var composited = CompositeOver(tree, Green, 4, 2);

        Assert.Equal(Color.FromRgb(0, 127, 128), composited[0, 0].Style.Background);
    }

    [Fact]
    public void OpaqueChildInsideATranslucentAncestor_WithGroupsGatedOff_StillDoubleBlends()
    {
        // The "before" measurement, taken through the same tree and the same composite so the two numbers
        // are comparable: with no group to carry the ancestor's fade, the flat path is all that is left and
        // it blends the ancestor twice — visibly, as red bleeding through an opaque child.
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 6) });
        host.Application.TranslucencyEnabled = false;

        var (_, tree, x, _) = CreateAncestorOverChild();
        tree.Render();

        Assert.False(tree.IsGroupRoot(x));

        var composited = CompositeOver(tree, Green, 4, 2);

        Assert.Equal(Color.FromRgb(63, 63, 128), composited[0, 0].Style.Background);
    }

    /// <summary>
    /// The same repro at the <b>Ansi256</b> tier, which is where the gate used to cut: the theme spine there
    /// is RGB, so <see cref="Color.Composite"/> blends exactly as it does at truecolor and the group's
    /// single blend-down is a visible value, not a discarded one.
    /// </summary>
    /// <remarks>
    /// <b>The sampled layer is the composited <see cref="CellBuffer"/></b> — <see cref="SceneCompositor"/>'s
    /// output, the same surface the frame loop composites windows into. Quantization to the 256-color cube
    /// happens strictly later, at the <c>FrameRenderer</c>'s <c>StyleQuantizer</c>, on the way to the wire;
    /// asserting there would measure the cube's nearest-entry search, not the compositor's arithmetic.
    /// </remarks>
    [Fact]
    public void AtAnsi256_OpaqueChildInsideATranslucentAncestor_BlendsTheAncestorOnceNotTwice()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 6) });
        host.Application.RequestedColorTier = ColorDepth.Ansi256;

        var (_, tree, x, _) = CreateAncestorOverChild();
        tree.Render();

        Assert.True(tree.IsGroupRoot(x));

        var composited = CompositeOver(tree, Green, 4, 2);

        // The same number the truecolor case lands on — compositing is depth-agnostic. Before the gate was
        // widened this tier took the flat path and produced rgb(63, 63, 128), the double-blend.
        Assert.Equal(Color.FromRgb(0, 127, 128), composited[0, 0].Style.Background);
    }

    // ───────────────────────────── the identity group ─────────────────────────────

    /// <summary>
    /// A group at opacity 255 must be bit-identical to no group at all. This is the property that makes
    /// group-ness <b>sticky</b> safe: a fade animation that crosses 1.0 keeps its surface (materialising
    /// moves the emitted layer count, the compositor's full-recomposite signal), so the surface has to cost
    /// a pass and not a pixel.
    /// </summary>
    [Fact]
    public void IdentityGroup_IsBitIdenticalToTheSameTreeWithNoGroup()
    {
        // Sticky: x materialises a group at 0.5, then returns to 1.0 and keeps it.
        var (_, grouped, x, _) = CreateOverlappingTree(new Host { Opacity = 0.5 });
        grouped.Render();
        Assert.True(grouped.IsGroupRoot(x));

        x.Opacity = 1.0;
        grouped.Render();
        Assert.True(grouped.IsGroupRoot(x));
        Assert.Equal((byte)255, grouped.Parameters(x).Opacity);

        // The same tree that never had an opacity to group.
        var (_, flat, flatX, _) = CreateOverlappingTree(new Host { IsRenderBoundary = true });
        flat.Render();
        Assert.False(flat.IsGroupRoot(flatX));

        AssertBuffersEqual(CompositeOver(flat, Green, 8, 3), CompositeOver(grouped, Green, 8, 3));
    }

    // ───────────────────────────── nesting ─────────────────────────────

    /// <summary>
    /// A group inside a group: the inner one composites its own subtree, fades it by its own opacity on the
    /// way into the outer surface, and the outer fades the result once more. Each translucency is applied
    /// exactly once, at the level that owns it.
    /// </summary>
    [Fact]
    public void NestedGroups_ApplyEachOpacityExactlyOnceAtItsOwnLevel()
    {
        var root = new Host();
        var outer = new Fill(Red) { Opacity = 0.5 };
        var inner = new Fill(Blue) { Opacity = 0.5 };
        var leaf = new Fill(White) { IsRenderBoundary = true };
        inner.Add(leaf);
        outer.Add(inner);
        root.Add(outer);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root, 4, 2);
        tree.Render();

        Assert.True(tree.IsGroupRoot(outer));
        Assert.True(tree.IsGroupRoot(inner));

        var composited = CompositeOver(tree, Green, 4, 2);

        // inner's surface: blue covered by an opaque white leaf → white. Into outer's surface at 128 over
        // outer's own red → (255, 128, 128). Outer's layer at 128 over green → (128, 191, 64).
        Assert.Equal(Color.FromRgb(128, 191, 64), composited[0, 0].Style.Background);
    }

    // ───────────────────────────── coordinates: nothing rebases in a cache ─────────────────────────────

    /// <summary>
    /// Group-local coordinates exist only inside the per-pass inner composite. The published caches — the
    /// zone offsets, the effective clip, <see cref="RenderTree.GetPublishedParameters"/> — stay in window
    /// coordinates, which is why <see cref="RenderTree.HitTest"/> needs no notion of groups whatsoever.
    /// </summary>
    [Fact]
    public void GroupMembership_LeavesWindowCoordinatesAndHitTestingUntouched()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 8) });

        var (_, grouped, x, inner) = CreatePositionedTree();
        grouped.Render();
        Assert.True(grouped.IsGroupRoot(x));

        // Window coordinates throughout — the group origin is (3, 1) and nothing subtracted it from a
        // published cache. The member's own clip is tighter than the group's and still absolute.
        Assert.Equal(3, grouped.Parameters(x).OffsetColumn);
        Assert.Equal(1, grouped.Parameters(x).OffsetRow);
        Assert.Equal(new Rect(3, 1, 10, 4), grouped.Parameters(x).Clip);
        Assert.Equal(new Rect(3, 1, 4, 2), grouped.Parameters(inner).Clip);
        Assert.Equal(3, grouped.Parameters(inner).OffsetColumn);

        // And hit testing — which reads exactly those caches — answers identically over the whole window
        // whether or not a group materialised.
        host.Application.TranslucencyEnabled = false;
        var (_, flat, flatX, _) = CreatePositionedTree();
        flat.Render();
        Assert.False(flat.IsGroupRoot(flatX));

        for (var row = 0; row < 8; row++)
        for (var column = 0; column < 20; column++)
            Assert.Equal(flat.HitTest(column, row)?.Name, grouped.HitTest(column, row)?.Name);

        Assert.Equal("inner", grouped.HitTest(4, 2)?.Name);   // the generated matrix, spot-checked
        Assert.Equal("container", grouped.HitTest(9, 4)?.Name);
        Assert.Equal("root", grouped.HitTest(1, 0)?.Name);
    }

    // ───────────────────────────── the emitted layer ─────────────────────────────

    [Fact]
    public void CollectLayers_EmitsTheGroupSurfaceOnce_AndLabelsIt()
    {
        var root = new Host();
        var x = new Host { Opacity = 0.5, Name = "Fader" };
        var inner = new Host { IsRenderBoundary = true };
        inner.Add(new Probe(4, 2));
        x.Add(inner);
        root.Add(x);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root, 8, 3);
        tree.Render();

        var layers = new List<SceneLayer>();
        var descriptions = new List<string>();
        tree.CollectLayers(layers, boundaryDescriptions: descriptions);

        Assert.Equal(3, tree.LayerCount);
        Assert.Equal(2, tree.EmittedLayerCount);
        Assert.Equal(2, layers.Count);
        Assert.Equal(["Host", "Host#Fader (group)"], descriptions);

        // The emitted layer IS the group surface, carrying the group root's own parameters.
        Assert.Same(tree.GetGroupScene(x), layers[1].Scene);
        Assert.Equal(tree.Parameters(x), layers[1].Parameters);
        Assert.Equal((byte)128, layers[1].Parameters.Opacity);
    }

    [Fact]
    public void CollectLayers_FoldsTheWindowOffsetAndOpacityOntoAGroupLayerToo()
    {
        var root = new Host();
        var x = new Host { Opacity = 0.5 };
        var inner = new Host { IsRenderBoundary = true };
        inner.Add(new Probe(4, 2));
        x.Add(inner);
        root.Add(x);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root, 8, 3);
        tree.Render();

        var layers = new List<SceneLayer>();
        tree.CollectLayers(layers, windowOffsetColumn: 5, windowOffsetRow: 2, windowOpacity: 0.5);

        Assert.Equal(2, layers.Count);
        Assert.Equal(5, layers[1].Parameters.OffsetColumn);
        Assert.Equal(2, layers[1].Parameters.OffsetRow);
        Assert.Equal((byte)64, layers[1].Parameters.Opacity);          // 0.5 × window 0.5
        Assert.Equal(new Rect(5, 2, 8, 3), layers[1].Parameters.Clip);
    }

    // ───────────────────────────── fragments ─────────────────────────────

    /// <summary>
    /// A fragment (image / OSC 66 sized text) registered by a group member rides the two-stage relocation —
    /// member → group surface → target — and lands at exactly the anchor the flat path would have given it.
    /// Anchor translation and clip intersection are both idempotent under composition, so the extra hop is
    /// invisible.
    /// </summary>
    [Fact]
    public void FragmentInsideAGroup_LandsAtTheSameAnchorAsTheFlatPath()
    {
        IBufferFragment fragment = new FakeFragment(2, 1);
        var (_, tree, _, _) = CreateFragmentTree(fragment);
        tree.Render();

        var target = new CellBuffer(12, 4);
        CompositeInto(target, Green, tree, surfaceZ: 0, isOccluder: false);

        Assert.True(target.AsView().TryGetFragmentAnchor(fragment.Key, out var anchor));
        Assert.Equal((2, 1), anchor);
    }

    /// <summary>
    /// Cross-surface occlusion still works over a collapsed subtree. Occlusion is <b>surface</b>-granular —
    /// the compositor's occluder test needs a strictly higher <c>SurfaceZ</c> — so a group's members all
    /// entering the inner pass at <c>SurfaceZ = 0</c> costs nothing, and the group's emitted layer carries
    /// the surface's real stamps to the outer pass where the decision is actually made.
    /// </summary>
    [Fact]
    public void FragmentInsideAGroup_IsStillOccludedByAHigherSurface()
    {
        IBufferFragment fragment = new FakeFragment(2, 1);
        var (_, tree, _, _) = CreateFragmentTree(fragment);
        tree.Render();

        var cover = new Host();
        cover.Add(new Probe(12, 4));
        var (_, coverTree) = LayoutFixture.CreateRenderRoot(cover, 12, 4);
        coverTree.Render();

        var layers = new List<SceneLayer>();
        tree.CollectLayers(layers, surfaceZ: 0, isOccluder: false);
        coverTree.CollectLayers(layers, surfaceZ: 1, isOccluder: true);

        var target = new CellBuffer(12, 4);
        new SceneCompositor(CellStyle.Default.WithBackground(Green))
            .Composite(CollectionsMarshal.AsSpan(layers), new CellBufferView(target));

        // Fully covered by the higher opaque surface: the remainder is not a single rectangle, so the
        // fragment is suppressed rather than overdrawn — exactly as it would be without the group.
        Assert.False(target.AsView().TryGetFragmentAnchor(fragment.Key, out _));
    }

    // ───────────────────────────── scrolled group root ─────────────────────────────

    /// <summary>
    /// A translucent <see cref="ScrollContentPresenter"/> group root: its surface is its own <b>banded</b>
    /// scene, so a descendant boundary inside the scrolled content rebases into the band, not into the
    /// viewport. The band invariant (<c>BandStartRow &lt;= ScrollOffsetRow</c>) is what keeps that rebase
    /// non-negative and inside the surface; the DEBUG containment assert in the group pass is what would
    /// catch it if it ever stopped holding.
    /// </summary>
    [Fact]
    public void TranslucentScrollHost_GroupsItsBandedScene_AcrossAScroll()
    {
        var root = new Host();
        var presenter = new ScrollContentPresenter { Opacity = 0.5 };
        var stack = new StackPanel();
        for (var i = 0; i < 100; i++)
            stack.Children.Add(new Probe(20, 1) { FillGlyph = (i % 10).ToString() });
        presenter.Content = stack;
        root.Add(presenter);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root, 20, 6);
        var probe = (Probe)stack.Children[7];
        probe.IsRenderBoundary = true; // a boundary inside the scrolled content — presenter becomes a group
        tree.Render();

        Assert.True(tree.IsGroupRoot(presenter));
        Assert.Equal(2, tree.EmittedLayerCount);

        foreach (var offset in new[] { 0, 3, 12, 40, 0 })
        {
            presenter.ScrollOffsetRow = offset;
            tree.Render();

            // The scroll rides the group's emitted layer as a pure parameters change; the clip stays the
            // viewport footprint in window coordinates.
            Assert.Equal(new Rect(0, 0, 20, 6), tree.Parameters(presenter).Clip);
            CompositeOver(tree, Green, 20, 6); // exercises the inner pass + the DEBUG containment assert
        }
    }

    // ───────────────────────────── the fast path ─────────────────────────────

    [Fact]
    public void CleanFrame_WithALiveGroup_AllocatesNothing()
    {
        // The zero-allocation clean-frame contract has to survive a materialised group: the member list is
        // reused at settled capacity, the surface is rented once, and the inner compositor's early-out does
        // no work — which in turn leaves the group surface's RasterVersion alone, so the outer pass
        // early-outs too.
        var root = new Host();
        var x = new Fill(Red) { Opacity = 0.5 };
        var inner = new Fill(Blue) { IsRenderBoundary = true };
        inner.Add(new Probe(6, 2));
        x.Add(inner);
        root.Add(x);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root, 8, 3);
        tree.Render();
        Assert.True(tree.IsGroupRoot(x));

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
            tree.RunRenderPass();
            layers.Clear();
            tree.CollectLayers(layers);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void DeMaterialisingAGroup_ReturnsToTheFlatComposite()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 6) });

        var (_, tree, x, _) = CreateAncestorOverChild();
        tree.Render();
        Assert.Equal(Color.FromRgb(0, 127, 128), CompositeOver(tree, Green, 4, 2)[0, 0].Style.Background);

        host.Application.TranslucencyEnabled = false;
        tree.Render();

        Assert.Null(tree.GetGroupScene(x));
        Assert.Equal(Color.FromRgb(63, 63, 128), CompositeOver(tree, Green, 4, 2)[0, 0].Style.Background);

        host.Application.TranslucencyEnabled = true;
        tree.Render();

        Assert.Equal(Color.FromRgb(0, 127, 128), CompositeOver(tree, Green, 4, 2)[0, 0].Style.Background);
    }

    // ───────────────────────────── acceptance: the reported bug ─────────────────────────────

    /// <summary>
    /// <b>The bug as it was reported:</b> a File Open/Save dialog looks wrong because the opaque
    /// <see cref="ScrollViewer"/> content stands out instead of joining the owner window's blending. A
    /// translucent dialog is one translucent thing; the scroller region inside it must blend with the
    /// backdrop <b>identically</b> to the dialog chrome an inch to its left, because they are the same
    /// colour painted at the same strength inside the same fading subtree.
    /// </summary>
    /// <remarks>
    /// The scroller is a <c>ScrollContentPresenter</c>, a render boundary from attach, so it was its own
    /// sibling layer carrying a copy of the dialog's 0.95 — blended onto a target that already had the
    /// dialog's own 0.95 on it. Two fades where the user sees one, and the eye reads the difference as
    /// the scroller being <em>more solid</em> than the dialog around it: the chrome lands on
    /// <c>rgb(189, 0, 10)</c> and the scroller on <c>rgb(199, 0, 0)</c>, redder and with the desktop's
    /// blue squeezed out. Both numbers are pinned, here and in the gated-off companion below.
    /// </remarks>
    [Fact]
    public void ATranslucentDialogsScrollerRegion_BlendsWithTheBackdropIdenticallyToTheChromeBesideIt()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(80, 24) });

        var (root, dialog) = BuildTranslucentDialogOverABackdrop();
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        Assert.True(host.Application.WindowManager!.Tree!.IsGroupRoot(dialog));

        var chrome = host.GetCell(ChromeSample.Column, ChromeSample.Row).Style.Background;
        var scroller = host.GetCell(ScrollerSample.Column, ScrollerSample.Row).Style.Background;

        Assert.Equal(DialogOverBackdrop, chrome);
        Assert.Equal(chrome, scroller);
    }

    [Fact]
    public void ATranslucentDialogsScrollerRegion_WithGroupsGatedOff_StandsOutAgainstTheChrome()
    {
        // The "before", measured the same way: the chrome is unchanged and correct, and only the scroller
        // region moves — which is exactly why the report was about the scroller and not about the dialog.
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(80, 24) });
        host.Application.TranslucencyEnabled = false;

        var (root, dialog) = BuildTranslucentDialogOverABackdrop();
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        Assert.False(host.Application.WindowManager!.Tree!.IsGroupRoot(dialog));

        Assert.Equal(DialogOverBackdrop, host.GetCell(ChromeSample.Column, ChromeSample.Row).Style.Background);
        Assert.Equal(Color.FromRgb(199, 0, 0), host.GetCell(ScrollerSample.Column, ScrollerSample.Row).Style.Background);
    }

    /// <summary>
    /// The reported bug again on a <b>negotiated Ansi256 terminal</b> — no <c>RequestedColorTier</c>
    /// override, the capability snapshot itself reports the tier — because that is the population the gate
    /// used to exclude. The default theme's own Ansi256 dictionary ships translucent
    /// <c>ElevationDialog</c>/<c>ElevationPopup</c> brushes, so translucent dialogs are not hypothetical
    /// at this tier — this fixture just paints its own colours so the arithmetic is legible.
    /// </summary>
    /// <remarks>
    /// <b>Sampled layer:</b> <see cref="UIHeadlessHost.FrameBuffer"/> — the composited cell buffer the frame
    /// loop hands to the renderer, <em>before</em> <c>StyleQuantizer</c> maps it onto the 256-color cube at
    /// emit. That is the layer the group changes; the cube mapping downstream is the same function applied
    /// to whichever colour arrives.
    /// </remarks>
    [Fact]
    public void AtAnsi256_ATranslucentDialogsScrollerRegion_BlendsWithTheBackdropIdenticallyToTheChromeBesideIt()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
                                               {
                                                   InitialSize = new Size(80, 24),
                                                   Capabilities = Ansi256Terminal,
                                               });

        Assert.Equal(ColorDepth.Ansi256, host.Application.ActualThemeVariant.Tier); // negotiated, not requested

        var (root, dialog) = BuildTranslucentDialogOverABackdrop();
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        Assert.True(host.Application.WindowManager!.Tree!.IsGroupRoot(dialog));

        var chrome = host.GetCell(ChromeSample.Column, ChromeSample.Row).Style.Background;
        var scroller = host.GetCell(ScrollerSample.Column, ScrollerSample.Row).Style.Background;

        // Before the widening this tier produced the reported split: chrome rgb(189, 0, 10), scroller
        // rgb(199, 0, 0) — redder, with the desktop's blue squeezed out by the second blend.
        Assert.Equal(DialogOverBackdrop, chrome);
        Assert.Equal(chrome, scroller);
    }

    /// <summary>
    /// <b>The premise the tier gate rests on, measured on the far side of quantization.</b> Every other
    /// assertion here samples the composited <see cref="CellBuffer"/>, and
    /// <see cref="SceneCompositor"/> takes no color depth at all — so those numbers change with
    /// the gate by construction and cannot, on their own, show that a 256-color terminal <em>sees</em> the
    /// difference. The claim that admits Ansi256 is specifically that its theme spine is RGB and the blend
    /// survives; a blend that <see cref="StyleQuantizer"/> mapped back onto the un-blended cube entry would
    /// be arithmetic no user could observe. So run the real quantizer, at the real tier, over the exact
    /// before/after pairs the acceptance tests pin.
    /// </summary>
    [Fact]
    public void AtAnsi256_TheGroupsBlendSurvivesQuantizationToThe256ColorCube()
    {
        var quantizer = new StyleQuantizer(Ansi256Terminal.Output);

        Color Quantize(Color color) => quantizer.Quantize(CellStyle.Default.WithBackground(color)).Background;

        // The acceptance pair: dialog chrome (grouped, one blend) vs the scroller's old double blend.
        var chrome = Quantize(DialogOverBackdrop);              // rgb(189, 0, 10)
        var doubleBlendedScroller = Quantize(Color.FromRgb(199, 0, 0));

        // The repro pair: one blend vs two, over green.
        var groupedRepro = Quantize(Color.FromRgb(0, 127, 128));
        var doubleBlendedRepro = Quantize(Color.FromRgb(63, 63, 128));

        // Both land on the 256-color cube (RGB does not survive this tier verbatim — that is the point of
        // quantizing here rather than trusting the compositor's number).
        Assert.Equal(ColorKind.Palette, chrome.Kind);
        Assert.Equal(ColorKind.Palette, groupedRepro.Kind);

        // ...and land on DIFFERENT entries, so the group's single blend-down is visible on the wire at
        // Ansi256, not an arithmetic distinction the cube collapses. This is what makes the tier eligible.
        Assert.NotEqual(chrome, doubleBlendedScroller);
        Assert.NotEqual(groupedRepro, doubleBlendedRepro);
    }

    // ───────────────────────────── the tier flip as a live change ─────────────────────────────

    /// <summary>
    /// Crossing the gate at runtime is a <b>structural</b> composition change — a group materialises or
    /// releases, so the emitted layer count moves. <see cref="UIApplication.TranslucencyEnabled"/>, the
    /// predicate's other half, calls <c>WindowManager.InvalidateComposition</c> from its setter for
    /// exactly that reason; the tier half has no such call, so this pins that it does not need one.
    /// </summary>
    /// <remarks>
    /// It does not need one because a tier flip cannot leave the app idle: <c>UpdateActualThemeVariant</c>
    /// re-stamps the effective-tier capability classes on every root and pulses the application resource
    /// catch-all, both of which register styling work, so the frame loop always has a frame to run — and
    /// <c>RunRenderPass</c> re-reads <c>GroupCompositingEnabled</c> and recomputes the emitted layer count
    /// unconditionally on every pass, which is the compositor's own full-recomposite signal. This fixture
    /// paints literal colours through <c>FillOpaque</c> and reads no theme brush for the sampled cells, so
    /// none of that repair can be coming from a re-resolved resource. Both crossings are exercised: a
    /// one-way test would pass on a gate stuck in the state it happened to end in.
    /// </remarks>
    [Fact]
    public void ATierFlipAcrossTheGate_RecompositesWithoutWaitingForSomethingElseToDirty()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(80, 24) });

        var (root, dialog) = BuildTranslucentDialogOverABackdrop();
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        Assert.True(host.Application.WindowManager!.Tree!.IsGroupRoot(dialog));
        Assert.Equal(DialogOverBackdrop, host.GetCell(ScrollerSample.Column, ScrollerSample.Row).Style.Background);

        host.Application.RequestedColorTier = ColorDepth.Ansi16;
        Assert.True(host.RunUntilIdle());

        // Below the gate the group releases and the flat path double-blends the scroller again.
        Assert.False(host.Application.WindowManager!.Tree!.IsGroupRoot(dialog));
        Assert.Equal(Color.FromRgb(199, 0, 0), host.GetCell(ScrollerSample.Column, ScrollerSample.Row).Style.Background);

        // ...and back: the return crossing is structural in the same way.
        host.Application.RequestedColorTier = ColorDepth.Ansi256;
        Assert.True(host.RunUntilIdle());

        Assert.True(host.Application.WindowManager!.Tree!.IsGroupRoot(dialog));
        Assert.Equal(DialogOverBackdrop, host.GetCell(ScrollerSample.Column, ScrollerSample.Row).Style.Background);
    }

    // ───────────────────────────── fixtures ─────────────────────────────

    /// <summary>
    /// A terminal that negotiated 256 colors: the Kitty preset with its color depth stepped down, so the
    /// application derives an Ansi256 <c>ActualThemeVariant</c> on its own (<c>HeadlessCapabilities</c>'
    /// presets are truecolor / Ansi16 / NoColor — there is no 256-color one).
    /// </summary>
    private static readonly TerminalCapabilities Ansi256Terminal = HeadlessCapabilities.KittyTruecolor with
    {
        Output = HeadlessCapabilities.KittyTruecolor.Output with
        {
            Color = HeadlessCapabilities.KittyTruecolor.Output.Color with
            {
                Depth = ColorDepth.Ansi256,
                TruecolorVerified = false,
            },
        },
    };

    private static readonly Color Backdrop = Color.FromRgb(0, 0, 200);
    private static readonly Color DialogBg = Color.FromRgb(200, 0, 0);

    /// <summary>The dialog's own paint over the desktop, faded once: <c>Composite(DialogBg @ 242, Backdrop)</c>.</summary>
    private static readonly Color DialogOverBackdrop = Color.FromRgb(189, 0, 10);

    /// <summary>Dialog chrome, above and left of the scroller.</summary>
    private static readonly Rect ChromeSample = new(12, 5, 1, 1);

    /// <summary>Inside the scroller's viewport, clear of the vertical scroll bar at its right edge.</summary>
    private static readonly Rect ScrollerSample = new(20, 9, 1, 1);

    /// <summary>
    /// The closest headless equivalent of the reported shape: an opaque desktop backdrop, a dialog at
    /// <c>Opacity = 0.95</c> painting its own chrome, and a <see cref="ScrollViewer"/> inside it whose
    /// content paints the <b>same</b> colour opaquely and overflows (so it is a real scroller). Chrome and
    /// scroller therefore differ in exactly one respect — which layer painted them.
    /// </summary>
    private static (UIElement Root, UIElement Dialog) BuildTranslucentDialogOverABackdrop()
    {
        var desktop = new Fill(Backdrop);
        var surface = new Canvas();
        var dialog = new Fill(DialogBg) { Opacity = 0.95, Width = 40, Height = 12, Name = "dialog" };
        var content = new Canvas { Width = 40, Height = 12 };
        var scroller = new ScrollViewer { Width = 30, Height = 8, Content = new Fill(DialogBg) { Width = 28, Height = 60 } };

        content.Children.Add(scroller);
        Canvas.SetLeft(scroller, 5);
        Canvas.SetTop(scroller, 2);
        dialog.Add(content);
        surface.Children.Add(dialog);
        Canvas.SetLeft(dialog, 10);
        Canvas.SetTop(dialog, 4);
        desktop.Add(surface);

        return (desktop, dialog);
    }

    /// <summary>root → x(0.5, opaque red) → inner(boundary, opaque blue): the repro tree, 4×2.</summary>
    private static (LayoutManager Manager, RenderTree Tree, Host GroupRoot, Host Child) CreateAncestorOverChild()
    {
        var root = new Host();
        var x = new Fill(Red) { Opacity = 0.5 };
        var inner = new Fill(Blue) { IsRenderBoundary = true };
        x.Add(inner);
        root.Add(x);

        var (manager, tree) = LayoutFixture.CreateRenderRoot(root, 4, 2);
        return (manager, tree, x, inner);
    }

    /// <summary>
    /// root → x(configurable) painting red across 8×3 → inner(boundary) painting blue over the left half:
    /// members that genuinely overlap, so an identity group has something to be identical about.
    /// </summary>
    private static (LayoutManager Manager, RenderTree Tree, Host GroupRoot, Host Child) CreateOverlappingTree(Host x)
    {
        var root = new Host();
        var painted = new Fill(Red) { Region = new Rect(0, 0, 8, 3) };
        var inner = new Fill(Blue) { IsRenderBoundary = true, Region = new Rect(0, 0, 4, 3) };
        painted.Add(inner);
        x.Add(painted);
        root.Add(x);

        var (manager, tree) = LayoutFixture.CreateRenderRoot(root, 8, 3);
        return (manager, tree, x, inner);
    }

    /// <summary>
    /// A Canvas root with x(0.5) at (3, 1) sized 10×4, holding a Canvas container whose 4×2 boundary child
    /// keeps its own size: a group whose member's clip is strictly tighter than the group's own.
    /// </summary>
    private static (LayoutManager Manager, RenderTree Tree, Host GroupRoot, UIElement Child) CreatePositionedTree()
    {
        var root = new Canvas { Name = "root" };
        var x = new Host { Opacity = 0.5, Width = 10, Height = 4, Name = "x" };
        var container = new Canvas { Width = 10, Height = 4, Name = "container" };
        var inner = new Probe(4, 2) { IsRenderBoundary = true, Name = "inner" };
        container.Children.Add(inner);
        x.Add(container);
        root.Children.Add(x);
        Canvas.SetLeft(x, 3);
        Canvas.SetTop(x, 1);

        var (manager, tree) = LayoutFixture.CreateRenderRoot(root, 20, 8);
        return (manager, tree, x, inner);
    }

    /// <summary>root → x(0.5) → inner(boundary) drawing <paramref name="fragment"/> at window (2, 1).</summary>
    private static (LayoutManager Manager, RenderTree Tree, Host GroupRoot, UIElement Child)
        CreateFragmentTree(IBufferFragment fragment)
    {
        var root = new Canvas();
        var x = new Host { Opacity = 0.5, Width = 12, Height = 4 };
        var inner = new Probe(12, 4)
                    {
                        IsRenderBoundary = true,
                        OnRender = (_, ctx) => ctx.DrawContent(new Rect(2, 1, 2, 1), new FragmentContent(fragment))
                    };
        var container = new Host { Width = 12, Height = 4 };
        container.Add(inner);
        x.Add(container);
        root.Children.Add(x);

        var (manager, tree) = LayoutFixture.CreateRenderRoot(root, 12, 4);
        return (manager, tree, x, inner);
    }

    private static CellBuffer CompositeOver(RenderTree tree, Color backdrop, int columns, int rows)
    {
        var buffer = new CellBuffer(columns, rows);
        CompositeInto(buffer, backdrop, tree, surfaceZ: 0, isOccluder: false);
        return buffer;
    }

    private static void CompositeInto(CellBuffer target, Color backdrop, RenderTree tree, int surfaceZ, bool isOccluder)
    {
        var layers = new List<SceneLayer>();
        tree.CollectLayers(layers, surfaceZ: surfaceZ, isOccluder: isOccluder);
        new SceneCompositor(CellStyle.Default.WithBackground(backdrop))
            .Composite(CollectionsMarshal.AsSpan(layers), new CellBufferView(target));
    }

    private static void AssertBuffersEqual(CellBuffer expected, CellBuffer actual)
    {
        Assert.Equal(expected.Columns, actual.Columns);
        Assert.Equal(expected.Rows, actual.Rows);

        for (var row = 0; row < expected.Rows; row++)
        for (var column = 0; column < expected.Columns; column++)
        {
            if (expected[column, row] == actual[column, row])
                continue;

            Assert.Fail($"cell ({column}, {row}) differs: expected {expected[column, row]}, got {actual[column, row]}");
        }
    }

    /// <summary>A <see cref="Host"/> that paints an opaque colour over <see cref="Region"/> (default: its bounds).</summary>
    private sealed class Fill(Color color) : Host
    {
        /// <summary>The element-local region to fill; null fills the whole element.</summary>
        public Rect? Region { get; init; }

        protected override void Render(RenderContext context)
        {
            base.Render(context);
            context.FillOpaque(Region ?? context.Bounds, color);
        }
    }

    private sealed class FakeFragment(int width, int height) : IBufferFragment
    {
        public Size GetSize() => new(width, height);

        public bool IsSupported(OutputCapabilities capabilities) => true;

        public void Emit(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities)
        {
        }
    }

    /// <summary>An <see cref="IContent"/> that registers a fragment at its paint anchor (stands in for Image / ScaledText).</summary>
    private sealed class FragmentContent(IBufferFragment fragment) : IContent
    {
        public Size Measure(Size availableSpace, OutputCapabilities capabilities) => fragment.GetSize();

        public Rect Paint(in CellBufferView buffer, in Rect bounds, in BrushedStyle style, OutputCapabilities capabilities)
        {
            // The anchor style is a value seam — resolve the delta at the fragment's anchor.
            buffer.AddFragment(bounds.Column, bounds.Row, fragment,
                               style.Resolve(bounds.Column, bounds.Row, bounds).ApplyTo(CellStyle.Default));
            var size = fragment.GetSize();
            return new Rect(bounds.Column, bounds.Row, size.Columns, size.Rows);
        }
    }
}
