using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI;

/// <summary>
/// The opacity-<b>group</b> structure a render tree decides every pass, before any of it composites
/// differently: the contiguous <c>[LayerIndex, SubtreeEnd)</c> subtree run the zone rebuild assigns,
/// the group-root predicate (translucent <b>and</b> has a descendant boundary <b>and</b> group
/// compositing is enabled), its stickiness, and its de-materialisation when the feature is switched
/// off. <see cref="RenderTree.LayerCount"/> keeps counting <em>boundaries</em>;
/// <see cref="RenderTree.EmittedLayerCount"/> is the group-collapsed count the compositor sees.
/// </summary>
public class RenderGroupPredicateTests
{
    // ───────────────────────────── the subtree run ─────────────────────────────

    [Fact]
    public void SubtreeRun_IsTheContiguousPreOrderRunOfThisBoundarysDescendants()
    {
        //  root ─┬─ a ── b        boundaries in pre-order: root, a, b, c
        //        └─ c
        var root = new Host();
        var a = new Host { IsRenderBoundary = true };
        var b = new Host { IsRenderBoundary = true };
        var c = new Host { IsRenderBoundary = true };
        b.Add(new Probe(2, 1));
        a.Add(b);
        c.Add(new Probe(2, 1));
        root.Add(a);
        root.Add(c);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();

        Assert.Equal(4, tree.LayerCount);
        Assert.Equal((0, 4), tree.GetSubtreeRun(root));
        Assert.Equal((1, 3), tree.GetSubtreeRun(a)); // a and b — c is a sibling subtree, not a descendant
        Assert.Equal((2, 3), tree.GetSubtreeRun(b)); // a leaf boundary: SubtreeEnd == LayerIndex + 1
        Assert.Equal((3, 4), tree.GetSubtreeRun(c));
    }

    [Fact]
    public void SubtreeRun_TracksAMidLifePromotion()
    {
        var root = new Host();
        var a = new Host { IsRenderBoundary = true };
        var inner = new Host();
        inner.Add(new Probe(2, 1));
        a.Add(inner);
        root.Add(a);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        Assert.Equal((1, 2), tree.GetSubtreeRun(a)); // inner is not a boundary yet

        inner.IsRenderBoundary = true; // predicate ⑦ — promotes on the next pass
        tree.Render();

        Assert.Equal(3, tree.LayerCount);
        Assert.Equal((1, 3), tree.GetSubtreeRun(a));
        Assert.Equal((2, 3), tree.GetSubtreeRun(inner));
    }

    // ───────────────────────────── the group-root predicate ─────────────────────────────

    [Fact]
    public void TranslucentLeafBoundary_IsNotAGroupRoot()
    {
        var root = new Host();
        var x = new Host { Opacity = 0.5 };
        x.Add(new Probe(4, 2)); // a non-boundary child: nothing inside x to blend against
        root.Add(x);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();

        Assert.False(tree.IsGroupRoot(x));
        Assert.Equal(2, tree.LayerCount);
        Assert.Equal(2, tree.EmittedLayerCount); // nothing collapsed
        Assert.Equal((byte)128, tree.Parameters(x).Opacity); // its own opacity still rides its own layer
    }

    [Fact]
    public void TranslucentBoundary_WithADescendantBoundary_IsAGroupRoot()
    {
        var root = new Host();
        var x = new Host { Opacity = 0.5 };
        var inner = new Host { IsRenderBoundary = true };
        inner.Add(new Probe(4, 2));
        x.Add(inner);
        root.Add(x);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();

        Assert.True(tree.IsGroupRoot(x));
        Assert.False(tree.IsGroupRoot(inner));
        Assert.False(tree.IsGroupRoot(root));
    }

    [Fact]
    public void OpaqueBoundary_WithDescendantBoundaries_IsNotAGroupRoot()
    {
        var root = new Host();
        var x = new Host { IsRenderBoundary = true }; // Opacity == 1: nothing to group
        var inner = new Host { IsRenderBoundary = true };
        inner.Add(new Probe(4, 2));
        x.Add(inner);
        root.Add(x);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();

        Assert.False(tree.IsGroupRoot(x));
        Assert.Equal(3, tree.EmittedLayerCount);
    }

    [Fact]
    public void GroupRoot_PublishesItsOwnOpacity_NotTheAncestorProduct()
    {
        var root = new Host();
        var outer = new Host { Opacity = 0.5 };
        var inner = new Host { Opacity = 0.5 };
        inner.Add(new Probe(4, 2));
        outer.Add(inner);
        root.Add(outer);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();

        Assert.Equal(0.5, tree.GetEffectiveOpacity(outer));
        Assert.Equal(0.5, tree.GetEffectiveOpacity(inner)); // NOT 0.25 — outer fades the group, once
        Assert.True(tree.IsGroupRoot(outer));
    }

    [Fact]
    public void NestedGroups_EachRootCollapsesOnlyItsOwnSubtree()
    {
        //  root ── outer(0.5) ── middle ── inner(0.5) ── leaf(boundary)
        var root = new Host();
        var outer = new Host { Opacity = 0.5 };
        var middle = new Host { IsRenderBoundary = true };
        var inner = new Host { Opacity = 0.5 };
        var leaf = new Host { IsRenderBoundary = true };
        leaf.Add(new Probe(2, 1));
        inner.Add(leaf);
        middle.Add(inner);
        outer.Add(middle);
        root.Add(outer);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();

        Assert.Equal(5, tree.LayerCount);
        Assert.True(tree.IsGroupRoot(outer));
        Assert.True(tree.IsGroupRoot(inner));
        Assert.False(tree.IsGroupRoot(middle));

        // root + outer's group. inner's group is a MEMBER of outer's, not a second emitted layer.
        Assert.Equal(2, tree.EmittedLayerCount);
    }

    [Fact]
    public void EmittedLayerCount_CollapsesAGroupSubtree_WhileLayerCountKeepsCountingBoundaries()
    {
        //  root ─┬─ x(0.5) ── inner(boundary)
        //        └─ sibling(boundary)
        var root = new Host();
        var x = new Host { Opacity = 0.5 };
        var inner = new Host { IsRenderBoundary = true };
        var sibling = new Host { IsRenderBoundary = true };
        inner.Add(new Probe(2, 1));
        sibling.Add(new Probe(2, 1));
        x.Add(inner);
        root.Add(x);
        root.Add(sibling);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();

        Assert.Equal(4, tree.LayerCount);        // root, x, inner, sibling
        Assert.Equal(3, tree.EmittedLayerCount); // root, x's group, sibling
    }

    // ───────────────────────────── stickiness ─────────────────────────────

    [Fact]
    public void GroupRoot_IsSticky_AcrossAnOpacityReturnToOne()
    {
        var root = new Host();
        var x = new Host { Opacity = 0.5 };
        var inner = new Host { IsRenderBoundary = true };
        inner.Add(new Probe(4, 2));
        x.Add(inner);
        root.Add(x);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        Assert.True(tree.IsGroupRoot(x));

        // A fade animation crossing 1.0 must not oscillate the emitted layer count — that is the
        // compositor's full-recomposite signal, and a group at opacity 1 costs a pass, not a pixel.
        x.Opacity = 1.0;
        tree.Render();

        Assert.True(tree.IsGroupRoot(x));
        Assert.Equal((byte)255, tree.Parameters(x).Opacity);
        Assert.Equal(2, tree.EmittedLayerCount);
    }

    [Fact]
    public void GroupRoot_IsSticky_AfterItsLastDescendantBoundaryDetaches()
    {
        var root = new Host();
        var x = new Host { Opacity = 0.5 };
        var inner = new Host { IsRenderBoundary = true };
        inner.Add(new Probe(4, 2));
        x.Add(inner);
        root.Add(x);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();
        Assert.True(tree.IsGroupRoot(x));

        x.Remove(inner);
        tree.Render();

        Assert.Equal((1, 2), tree.GetSubtreeRun(x)); // the run shrank to x alone
        Assert.True(tree.IsGroupRoot(x));            // …but group-ness is sticky, like promotion
        Assert.Equal(2, tree.EmittedLayerCount);     // and a one-member group still emits one layer
    }

    // ───────────────────────────── the group-eligibility gate ─────────────────────────────

    /// <summary>
    /// The gate's UPPER half: Ansi256 keeps groups. That tier's theme spine is RGB (Palette.xaml's
    /// "Dark · Ansi256 (RGB, Tokyo Night)" dictionary, whose ElevationPopup/ElevationDialog brushes
    /// already ship <c>Opacity="0.95"</c>), so <see cref="Color.Composite"/> genuinely blends here —
    /// the surface a group buys is paid back in a visible single blend-down, exactly as at truecolor.
    /// Quantization to the 256-color cube happens later, at the <c>FrameRenderer</c>'s
    /// <c>StyleQuantizer</c>, and is not this predicate's business.
    /// </summary>
    [Fact]
    public void AtAnsi256_GroupsStillMaterialise()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 6) });
        host.Application.RequestedColorTier = ColorDepth.Ansi256;

        var (_, tree, x) = CreateGroupTree();
        tree.Render();

        Assert.True(tree.IsGroupRoot(x));
        Assert.Equal((byte)128, tree.Parameters(x).Opacity);
    }

    /// <summary>
    /// The gate's LOWER half, and the reason there is a gate at all: Ansi16 and NoColor collapse to
    /// palette indices, where <see cref="Color.Composite"/> returns the source verbatim. Materialising
    /// there would buy a surface and a second composite pass for a blend the tier discards, so the
    /// gate is on ELIGIBILITY, not just on the published value.
    /// </summary>
    [Theory]
    [InlineData(ColorDepth.Ansi16)]
    [InlineData(ColorDepth.NoColor)]
    public void BelowAnsi256_NoGroupMaterialises(ColorDepth tier)
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 6) });
        host.Application.RequestedColorTier = tier;

        var (_, tree, x) = CreateGroupTree();
        tree.Render();

        Assert.False(tree.IsGroupRoot(x));
        Assert.Equal((byte)128, tree.Parameters(x).Opacity); // the zone's own opacity is untouched by the tier
    }

    /// <summary>
    /// The two tier gates are one policy and must agree tier for tier: <c>GroupCompositingEnabled</c>
    /// decides whether a group root materialises, and <c>CollectLayers</c> decides whether a window's
    /// opacity survives the fold. A tier where they disagree is a tier where groups materialise but the
    /// window that owns them is forced opaque (or the reverse) — so both legs are read here, at every
    /// tier, from one tree.
    /// </summary>
    [Theory]
    [InlineData(ColorDepth.Truecolor, true)]
    [InlineData(ColorDepth.Ansi256, true)]
    [InlineData(ColorDepth.Ansi16, false)]
    [InlineData(ColorDepth.NoColor, false)]
    public void BothTierGates_AgreeAtEveryTier(ColorDepth tier, bool translucencySurvives)
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 6) });
        host.Application.RequestedColorTier = tier;

        var (_, tree, x) = CreateGroupTree();
        tree.Render();

        Assert.Equal(translucencySurvives, tree.IsGroupRoot(x));

        // The window leg, read where it lands: layers[0] is the ROOT zone (Opacity == 1, and by the
        // zone-base rule the lowest layer), so its published opacity is the window fold and nothing else.
        var layers = new List<SceneLayer>();
        tree.CollectLayers(layers, windowOpacity: 0.5);

        Assert.Equal(translucencySurvives ? (byte) 128 : (byte) 255, layers[0].Parameters.Opacity);
    }

    [Fact]
    public void TranslucencyDisabled_NoGroupMaterialises()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 6) });
        host.Application.TranslucencyEnabled = false;

        var (_, tree, x) = CreateGroupTree();
        tree.Render();

        Assert.False(tree.IsGroupRoot(x));
    }

    [Fact]
    public void WithGroupsGatedOff_NestedOpacitiesStillMultiplyDown()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 6) });
        host.Application.TranslucencyEnabled = false;

        var root = new Host();
        var outer = new Host { Opacity = 0.5 };
        var inner = new Host { Opacity = 0.5 };
        inner.Add(new Probe(4, 2));
        outer.Add(inner);
        root.Add(outer);

        var (_, tree) = LayoutFixture.CreateRenderRoot(root);
        tree.Render();

        // No group can carry outer's translucency onto inner here, so the pre-group multiplied-down
        // approximation stands in rather than outer's fade vanishing from inner outright. This is the
        // fallback's live path now that the tier gate reads ">= Ansi256": the translucency SWITCH gates
        // groups off on an RGB tier, where the multiplied-down value blends and is plainly visible.
        Assert.False(tree.IsGroupRoot(outer));
        Assert.Equal((byte)128, tree.Parameters(outer).Opacity);
        Assert.Equal((byte)64, tree.Parameters(inner).Opacity); // round(255·0.25)
    }

    [Fact]
    public void DisablingTranslucency_DeMaterialisesAnEstablishedGroup()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 6) });
        Assert.True(host.Application.TranslucencyEnabled); // default on

        var (_, tree, x) = CreateGroupTree();
        tree.Render();
        Assert.True(tree.IsGroupRoot(x));
        Assert.Equal(2, tree.EmittedLayerCount);

        // Group-ness is sticky against VALUE changes only. Switching the feature off is structural:
        // the group must release its surface and go back to compositing flat.
        host.Application.TranslucencyEnabled = false;
        tree.Render();

        Assert.False(tree.IsGroupRoot(x));
        Assert.Null(tree.GetGroupScene(x));
        Assert.Equal(3, tree.EmittedLayerCount);

        host.Application.TranslucencyEnabled = true;
        tree.Render();

        Assert.True(tree.IsGroupRoot(x)); // and it re-materialises when the feature comes back
        Assert.Equal(2, tree.EmittedLayerCount);
    }

    /// <summary>root ── x(0.5) ── inner(boundary) ── probe: the smallest tree with a group root.</summary>
    private static (LayoutManager Manager, RenderTree Tree, UIElement GroupRoot) CreateGroupTree()
    {
        var root = new Host();
        var x = new Host { Opacity = 0.5 };
        var inner = new Host { IsRenderBoundary = true };
        inner.Add(new Probe(4, 2));
        x.Add(inner);
        root.Add(x);

        var (manager, tree) = LayoutFixture.CreateRenderRoot(root);
        return (manager, tree, x);
    }
}
