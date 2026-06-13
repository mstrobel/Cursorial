using Cursorial.Tests.UI.Conformance;
using Cursorial.UI;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.StyleMatrix;

/// <summary>Style matrix §6 — activation frames and store integration (S89–S107).</summary>
public class Section06_Frames
{
    [Fact]
    public void S089_OneValueFramePerRule_CarryingAllSetters()
    {
        using var tree = ShowTree(show: false);
        var probeP = StyleProbe<int>.Attach(tree.A, Widget.P);
        var probeQ = StyleProbe<int>.Attach(tree.A, Widget.Q);
        tree.App.Styles.Add(R("Widget", (Widget.P, 1), (Widget.Q, 2)));
        tree.Host.ShowRoot(tree.Root);

        var state = tree.A.StyleStateInternal!;
        var frame = Assert.Single(state.Frames); // ONE frame …
        Assert.Equal(2, frame.EntryCount);       // … two entries

        probeP.AssertSingleNotify(0, 1, BindingPriority.Style);
        probeQ.AssertSingleNotify(0, 2, BindingPriority.Style);
        Assert.Equal(new ValueSource(BindingPriority.Style, false), tree.A.GetValueSource(Widget.P));
        Assert.Equal(new ValueSource(BindingPriority.Style, false), tree.A.GetValueSource(Widget.Q));
    }

    [Fact]
    public void S090_ActivationNotification_OldNewStylePriority()
    {
        using var tree = ShowTree();
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        tree.App.Styles.Add(R("Widget", (Widget.P, 1)));

        probe.AssertSingleNotify(0, 1, BindingPriority.Style);
    }

    [Fact]
    public void S091_RunnerUpPromotion_SingleNotify_NoDefaultFlash()
    {
        using var tree = ShowTree(show: false);
        tree.A.Classes.Add("a");
        tree.App.Styles.Add(R(".a", (Widget.P, 1)));
        tree.App.Styles.Add(R(".a:pointerover", (Widget.P, 2)));
        tree.Host.ShowRoot(tree.Root);
        tree.A.Flip(InteractionState.PointerOver, true);
        Assert.Equal(2, tree.A.GetValue(Widget.P));
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        tree.A.Flip(InteractionState.PointerOver, false);

        probe.AssertSingleNotify(2, 1, BindingPriority.Style); // runner-up promotion in ONE notification
    }

    [Fact]
    public void S092_SoleRuleDeactivation_PromotionReportsTheNewLane()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget:pointerover", (Widget.P, 1)));
        tree.Host.ShowRoot(tree.Root);
        tree.A.Flip(InteractionState.PointerOver, true);
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        tree.A.Flip(InteractionState.PointerOver, false);

        probe.AssertSingleNotify(1, 0, BindingPriority.Default); // PD10 — the promoted lane
    }

    [Fact]
    public void S093_RetractionIsNeverASetBack_NoLocalValueDeliveryEver()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget:pointerover", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        tree.A.Flip(InteractionState.PointerOver, true);
        tree.A.Flip(InteractionState.PointerOver, false);
        tree.A.Flip(InteractionState.PointerOver, true);
        tree.A.Flip(InteractionState.PointerOver, false);

        Assert.Equal(4, probe.Deliveries.Count);
        Assert.DoesNotContain(probe.Deliveries, static delivery => delivery.Priority == BindingPriority.LocalValue);
        Assert.False(tree.A.IsSet(Widget.P)); // no local residue (invariant 4)
    }

    [Fact]
    public void S094_WeakerActivationUnderAStrongerWinner_MaskedSilent()
    {
        using var tree = ShowTree(show: false);
        tree.A.Classes.Add("x");
        tree.A.Classes.Add("y");
        tree.A.Classes.Add("z");
        tree.App.Styles.Add(R(".x.y.z", (Widget.P, 9)));           // classLike 3 — the winner
        tree.App.Styles.Add(R(".x:pointerover", (Widget.P, 5)));   // classLike 2 — weaker
        tree.Host.ShowRoot(tree.Root);
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        tree.A.Flip(InteractionState.PointerOver, true); // the weaker rule activates

        probe.AssertSilent(); // masked (M37)
        Assert.Equal(9, tree.A.GetValue(Widget.P));
        Assert.Equal(9, tree.A.GetValue(Widget.P, BindingPriority.Style)); // the lane probe resolves the winner
    }

    [Fact]
    public void S095_StrongerActivationOverAWeakerWinner_Notifies()
    {
        using var tree = ShowTree(show: false);
        tree.A.Classes.Add("x");
        tree.A.Classes.Add("y");
        tree.App.Styles.Add(R(".x", (Widget.P, 5)));
        tree.App.Styles.Add(R(".x.y:pointerover", (Widget.P, 9)));
        tree.Host.ShowRoot(tree.Root);
        Assert.Equal(5, tree.A.GetValue(Widget.P));
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        tree.A.Flip(InteractionState.PointerOver, true);

        probe.AssertSingleNotify(5, 9, BindingPriority.Style);
    }

    [Fact]
    public void S096_LayerLadder_PeelStrongestFirst()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget", (Widget.P, 1)));
        var scopedStyle = R("Widget", (Widget.P, 2));
        tree.PaneA.Styles.Add(scopedStyle);
        tree.A.Style = Explicit((Widget.P, 3));
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(3, tree.A.GetValue(Widget.P)); // Explicit(5)
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        tree.A.Style = null;
        probe.AssertSingleNotify(3, 2, BindingPriority.Style); // Scoped(4) promotes

        tree.PaneA.Styles.Remove(scopedStyle);
        probe.AssertSingleNotify(2, 1, BindingPriority.Style); // App(3) promotes
    }

    [Fact]
    public void S097_LocalMasks_ClearValuePromotesTheStyleBack()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);

        tree.A.SetValue(Widget.P, 9);
        Assert.Equal(9, tree.A.GetValue(Widget.P));
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), tree.A.GetValueSource(Widget.P));
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        tree.A.ClearValue(Widget.P);

        probe.AssertSingleNotify(9, 5, BindingPriority.Style);
    }

    [Fact]
    public void S098_StyleSitsAboveInherited()
    {
        using var tree = ShowTree(show: false);
        tree.PaneA.SetValue(Widget.Pi, 5); // ancestor local — the inherited fallback
        var style = R("Widget", (Widget.Pi, 9));
        tree.App.Styles.Add(style);
        tree.Host.ShowRoot(tree.Root);
        Assert.Equal(9, tree.A.GetValue(Widget.Pi));
        var probe = StyleProbe<int>.Attach(tree.A, Widget.Pi);

        tree.App.Styles.Remove(style);

        probe.AssertSingleNotify(9, 5, BindingPriority.Inherited);
    }

    [Fact]
    public void S099_AnimationMasksStyleEdges_DisposeSurfacesTheStyleValue()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget:pointerover", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);

        var handle = tree.A.BeginAnimation(Widget.P);
        handle.SetValue(50);
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        tree.A.Flip(InteractionState.PointerOver, true);
        tree.A.Flip(InteractionState.PointerOver, false);
        tree.A.Flip(InteractionState.PointerOver, true);
        probe.AssertSilent(); // both edges masked under Animation (wholesale)

        handle.Dispose();

        probe.AssertSingleNotify(50, 5, BindingPriority.Style);
        Assert.Equal(new ValueSource(BindingPriority.Style, false), tree.A.GetValueSource(Widget.P));
    }

    [Fact]
    public void S100_SetCurrentValueOverlay_ReplacedByTheArrivingStyleProducer()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget:pointerover", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);

        tree.A.SetCurrentValue(Widget.P, 7);
        Assert.Equal(7, tree.A.GetValue(Widget.P));

        tree.A.Flip(InteractionState.PointerOver, true); // the style producer arrives

        Assert.Equal(5, tree.A.GetValue(Widget.P));
        Assert.Equal(new ValueSource(BindingPriority.Style, false), tree.A.GetValueSource(Widget.P)); // A11
    }

    [Fact]
    public void S101_EqualValuePromotion_Silent_SourceStaysStyle()
    {
        using var tree = ShowTree(show: false);
        tree.A.Classes.Add("x");
        tree.App.Styles.Add(R(".x", (Widget.P, 5)));               // the runner-up
        tree.App.Styles.Add(R(".x:pointerover", (Widget.P, 5)));   // the (equal-valued) winner
        tree.Host.ShowRoot(tree.Root);
        tree.A.Flip(InteractionState.PointerOver, true);
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        tree.A.Flip(InteractionState.PointerOver, false); // deactivate the winner

        probe.AssertSilent(); // equal-value promotion (PD9)
        Assert.Equal(new ValueSource(BindingPriority.Style, false), tree.A.GetValueSource(Widget.P));
    }

    [Fact]
    public void S102_CookieBatchRetraction_BothPropertiesOneNotifyEach()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget:pointerover", (Widget.P, 1), (Widget.Q, 2)));
        tree.Host.ShowRoot(tree.Root);
        tree.A.Flip(InteractionState.PointerOver, true);
        var probeP = StyleProbe<int>.Attach(tree.A, Widget.P);
        var probeQ = StyleProbe<int>.Attach(tree.A, Widget.Q);

        tree.A.Flip(InteractionState.PointerOver, false);

        probeP.AssertSingleNotify(1, 0, BindingPriority.Default);
        probeQ.AssertSingleNotify(2, 0, BindingPriority.Default);
    }

    [Fact]
    public void S103_HundredFlips_FramesArrayIdentityStable_Phase2NeverReMatches()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget:pointerover", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);

        var framesBefore = tree.A.StyleStateInternal!.Frames;

        for (var i = 0; i < 100; i++)
        {
            tree.A.Flip(InteractionState.PointerOver, true);
            Assert.Equal(5, tree.A.GetValue(Widget.P));
            tree.A.Flip(InteractionState.PointerOver, false);
            Assert.Equal(0, tree.A.GetValue(Widget.P));
        }

        Assert.Same(framesBefore, tree.A.StyleStateInternal!.Frames);
    }

    [Fact]
    public void S104_ValuelessEntry_SkippedWithinTheSlot()
    {
        using var tree = ShowTree(show: false);
        tree.A.Classes.Add("x");
        tree.A.Classes.Add("y");
        tree.App.Styles.Add(R(".x", (Widget.P, 5)));
        tree.App.Styles.Add(R(".x.y", (Widget.P, UIProperty.UnsetValue))); // stronger, valueless
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(5, tree.A.GetValue(Widget.P)); // M41 end-to-end (A8)
    }

    [Fact]
    public void S106_NestingRootedExplicitStyle_ArmsInactive_HoverActivates()
    {
        using var tree = ShowTree();
        var style = new Style("^:pointerover", Resolver);
        style.Setters.Add(new Setter(Widget.P, 5));

        tree.A.Style = style;

        var armed = Assert.Single(StyleDiagnostics.MatchedRules(tree.A));
        Assert.Equal(StyleLayer.Explicit, armed.Layer);
        Assert.False(armed.IsActive);

        tree.A.Flip(InteractionState.PointerOver, true);

        Assert.Equal(5, tree.A.GetValue(Widget.P));
        Assert.True(Assert.Single(StyleDiagnostics.MatchedRules(tree.A)).IsActive);
    }

    [Fact]
    public void S107_OneSealedStyle_SharedAcrossTwoScopes()
    {
        using var tree = ShowTree(show: false);
        var shared = R("Widget", (Widget.P, 5));
        tree.App.Styles.Add(shared);
        tree.PaneA.Styles.Add(shared); // legal — sealed styles are shareable (SD19)
        tree.Host.ShowRoot(tree.Root);

        var armed = StyleDiagnostics.MatchedRules(tree.A);
        Assert.Equal(2, armed.Count); // two armed rules over the same CompiledRule
        Assert.Equal(StyleLayer.Scoped, armed[0].Layer); // Scoped wins
        Assert.Equal(StyleLayer.App, armed[1].Layer);
        Assert.Equal(5, tree.A.GetValue(Widget.P));
    }
}

/// <summary>
/// S105 — the mandated §3.9-② run: the Fork A <see cref="ValueFrameConformanceKit"/> executed
/// against the styling engine's own frame implementation (<c>StyleRuleFrame</c> over a compiled
/// selector-less rule). The inherited facts collectively discharge the row.
/// </summary>
public sealed class S105_StyleRuleFrameConformance : ValueFrameConformanceKit
{
    protected override ConformanceFrame CreateFrame(
        ulong sortKey, bool isActive, params (StyledProperty<int> Property, int? Value)[] entries)
    {
        var style = new Style();
        foreach (var (property, value) in entries)
            style.Setters.Add(new Setter(property, value is { } constant ? constant : UIProperty.UnsetValue));
        style.Seal();

        var rule = style.CompiledRules[0]; // the selector-less rule carrying the converted setters
        var frame = new StyleRuleFrame(owner: null, rule, new StyleSortKey(sortKey), StyleLayer.App, scopeOwner: null, isActive);
        return new Driver(frame);
    }

    private sealed class Driver(StyleRuleFrame frame) : ConformanceFrame
    {
        public override ValueFrame Frame => frame;

        public override void SetActive(bool active)
        {
            if (active)
                frame.Activate();
            else
                frame.Deactivate();
        }

        public override void SetEntryValue(StyledProperty<int> property, int value) => frame.SetEntryValue(property, value);

        public override void UnsetEntry(StyledProperty<int> property) => frame.UnsetEntryValue(property);
    }
}
