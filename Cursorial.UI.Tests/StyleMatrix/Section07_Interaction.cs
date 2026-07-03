using Cursorial.UI;
using Cursorial.UI.Testing;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.StyleMatrix;

/// <summary>Style matrix §7 — interaction-state and pseudo-class plumbing (S108–S125).</summary>
public class Section07_Interaction
{
    [Fact]
    public void S108_StylingEngine_IsTheProductionObserver_SlotStaysAssignable()
    {
        using var host = UITestHost.Create();

        Assert.Same(host.Application.StyleEngineInternal, host.Application.InteractionStateObserver); // SD22

        var sink = new RecordingSink();
        host.Application.InteractionStateObserver = sink; // test-only disconnect — the P2 contract
        Assert.Same(sink, host.Application.InteractionStateObserver);
    }

    [Fact]
    public void S109_MoveDrivenFlip_RendersInTheSameFrame()
    {
        using var host = UITestHost.Create();
        var root = new Cursorial.UI.Controls.StackPanel();
        var a = new Widget(4, 1) { PaintP = true };
        root.Children.Add(a);
        host.Application.Styles.Add(R("Widget:pointerover", (Widget.P, 5)));
        host.ShowRoot(root);
        host.RunFrame();
        Assert.StartsWith("0", host.GetRowText(0)); // baseline: P's default digit

        host.SendMouseMove(1, 0);
        host.RunFrame(); // ONE frame: drain the Move + restyle + re-raster

        Assert.Equal(5, a.GetValue(Widget.P));
        Assert.StartsWith("5", host.GetRowText(0)); // invariant 1 — visible in the same frame
    }

    [Fact]
    public void S110_NetZeroBatch_NoDelivery_NoStylingWork()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget:pointerover", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        using (tree.A.Batch())
        {
            tree.A.Flip(InteractionState.PointerOver, true);
            tree.A.Flip(InteractionState.PointerOver, false);
        }

        probe.AssertSilent();
        Assert.False(Assert.Single(StyleDiagnostics.MatchedRules(tree.A)).IsActive);
    }

    [Fact]
    public void S111_BatchedFlips_OneDeliveryOnePass_OneNotify()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget:focus:focus-visible", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        using (tree.A.Batch())
        {
            tree.A.Flip(InteractionState.Focused, true);
            tree.A.Flip(InteractionState.FocusVisible, true);
        }

        probe.AssertSingleNotify(0, 5, BindingPriority.Style); // the combined delta, one pass
    }

    [Theory]
    [InlineData(InteractionState.PointerOver, ":pointerover")]
    [InlineData(InteractionState.Pressed, ":pressed")]
    [InlineData(InteractionState.Focused, ":focus")]
    [InlineData(InteractionState.FocusWithin, ":focus-within")]
    [InlineData(InteractionState.FocusVisible, ":focus-visible")]
    [InlineData(InteractionState.ActiveWindow, ":active-window")]
    [InlineData(InteractionState.AccessKeyCue, ":access-keys")]
    [InlineData(InteractionState.Disabled, ":disabled")]
    [InlineData(InteractionState.ModalAttention, ":modal-attention")]
    public void S112_EveryInteractionBit_ArmsExactlyItsPinnedPseudoClass(InteractionState bit, string pseudoClass)
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R($"Widget{pseudoClass}", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);

        Assert.False(Assert.Single(StyleDiagnostics.MatchedRules(tree.A)).IsActive);

        tree.A.Flip(bit, true);
        Assert.Equal(5, tree.A.GetValue(Widget.P));

        tree.A.Flip(bit, false);
        Assert.Equal(0, tree.A.GetValue(Widget.P));
    }

    [Fact]
    public void S113_EffectiveEnabledPlumbing_DisabledRulesActivateOnDescendants()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget:disabled", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);

        tree.PaneA.IsEnabled = false; // S1 recompute pushes Disabled into the subtree (B18)

        Assert.Equal(5, tree.A.GetValue(Widget.P));
        Assert.Equal(5, tree.B.GetValue(Widget.P));
        Assert.Equal(0, tree.C.GetValue(Widget.P)); // outside the disabled subtree
    }

    [Fact]
    public void S114_FocusMove_RetractsAndActivatesAcrossTheTransitionBatch()
    {
        // A non-Pane root: the §0.2 default tree's StackPanel root would itself match
        // 'Pane:focus-within' as the move's common ancestor (focus-within correctly persists
        // there), which is not the diverging-chain behavior this row pins.
        using var host = UITestHost.Create();
        var root = new Card();
        var paneA = new Cursorial.UI.Controls.StackPanel();
        var paneB = new Cursorial.UI.Controls.StackPanel();
        var a = new Widget { Focusable = true };
        var c = new Widget { Focusable = true };
        paneA.Children.Add(a);
        paneB.Children.Add(c);
        root.Add(paneA);
        root.Add(paneB);
        host.Application.Styles.Add(R("Widget:focus", (Widget.P, 5)));
        host.Application.Styles.Add(R("Pane:focus-within Widget", (Widget.Q, 5)));
        host.ShowRoot(root);
        var focus = host.Application.FocusManager;

        Assert.True(focus.SetFocus(a));
        Assert.Equal(5, a.GetValue(Widget.P)); // :focus on a
        Assert.Equal(5, a.GetValue(Widget.Q)); // paneA carries :focus-within

        Assert.True(focus.SetFocus(c));
        Assert.Equal(0, a.GetValue(Widget.P)); // a's chain retracted …
        Assert.Equal(0, a.GetValue(Widget.Q));
        Assert.Equal(5, c.GetValue(Widget.P)); // … c's activated, riding the one transition batch
        Assert.Equal(5, c.GetValue(Widget.Q));
    }

    [Fact]
    public void S115_PseudoClassMapping_BoolProperty_FlipsTheClass()
    {
        var mapped = UIProperty.Register<Widget, bool>(UniqueName("PbMap"));
        PseudoClassMapping.Register<Widget>(mapped, ":on");

        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget:on", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);

        tree.A.SetValue(mapped, true);
        Assert.True(tree.A.HasPseudo(":on"));
        Assert.Equal(5, tree.A.GetValue(Widget.P));

        tree.A.SetValue(mapped, false);
        Assert.False(tree.A.HasPseudo(":on"));
        Assert.Equal(0, tree.A.GetValue(Widget.P));
    }

    [Fact]
    public void S116_Mapping_AppliesToOwnerAssignableInstancesOnly()
    {
        var mapped = UIProperty.Register<Widget, bool>(UniqueName("PbFancy"));
        PseudoClassMapping.Register<FancyWidget>(mapped, ":fancy-on");

        using var tree = ShowTree(show: false);
        var fancy = new FancyWidget();
        tree.PaneA.Children.Add(fancy);
        tree.Host.ShowRoot(tree.Root);

        tree.A.SetValue(mapped, true); // a plain Widget — not TOwner-assignable
        Assert.False(tree.A.HasPseudo(":fancy-on"));

        fancy.SetValue(mapped, true);
        Assert.True(fancy.HasPseudo(":fancy-on"));
    }

    [Fact]
    public void S117_ClassifyMapping_TransitionsRetireOldAndSetNewInOnePass()
    {
        var mapped = UIProperty.Register<Widget, int>(UniqueName("PqMap"));
        PseudoClassMapping.Register<Widget, int>(
            mapped, static value => value switch { 1 => ":one", 2 => ":two", _ => null }, [":one", ":two"]);

        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget:one", (Widget.P, 1)));
        tree.App.Styles.Add(R("Widget:two", (Widget.P, 2)));
        tree.Host.ShowRoot(tree.Root);

        tree.A.SetValue(mapped, 1);
        Assert.True(tree.A.HasPseudo(":one"));
        Assert.False(tree.A.HasPseudo(":two"));
        Assert.Equal(1, tree.A.GetValue(Widget.P));

        tree.A.SetValue(mapped, 2);
        Assert.False(tree.A.HasPseudo(":one")); // retired …
        Assert.True(tree.A.HasPseudo(":two"));  // … and set, in one pass
        Assert.Equal(2, tree.A.GetValue(Widget.P));

        tree.A.SetValue(mapped, 0);
        Assert.False(tree.A.HasPseudo(":one"));
        Assert.False(tree.A.HasPseudo(":two"));
        Assert.Equal(0, tree.A.GetValue(Widget.P));
    }

    [Fact]
    public void S118_DuplicateMappingRegistration_Throws()
    {
        var mapped = UIProperty.Register<Widget, bool>(UniqueName("PbDup"));
        PseudoClassMapping.Register<Widget>(mapped, ":dup");

        Assert.Throws<InvalidOperationException>(() => PseudoClassMapping.Register<Widget>(mapped, ":dup-two"));
    }

    [Fact]
    public void S119_CustomPseudoClass_SetFlipsAndRestyles_RepeatIsRestyleFree()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget:open", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        Assert.True(tree.A.SetPseudo(":open", true));
        probe.AssertSingleNotify(0, 5, BindingPriority.Style);

        Assert.False(tree.A.SetPseudo(":open", true)); // no-change — restyle-free
        probe.AssertSilent();

        Assert.True(tree.A.SetPseudo(":open", false));
        probe.AssertSingleNotify(5, 0, BindingPriority.Default);
    }

    [Fact]
    public void S120_InteractionBackedName_RejectedFromPseudoClasses()
    {
        using var tree = ShowTree();
        Assert.Throws<InvalidOperationException>(() => tree.A.SetPseudo(":pressed", true)); // B9
    }

    [Fact]
    public void S121_ColonlessName_RejectedFromPseudoClasses()
    {
        using var tree = ShowTree();
        Assert.Throws<ArgumentException>(() => tree.A.SetPseudo("open", true));
    }

    [Fact]
    public void S122_ColonPrefixedName_RejectedFromClasses()
    {
        using var tree = ShowTree();
        Assert.Throws<ArgumentException>(() => tree.A.Classes.Add(":focus"));
    }

    [Fact]
    public void S123_ReEntrantFlip_DrainsToFixpoint_BeforeTheMutationReturns()
    {
        var mapped = UIProperty.Register<Widget, bool>(UniqueName("PbChain"));
        PseudoClassMapping.Register<Widget>(mapped, ":chain-on");

        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget.go", (mapped, true)));       // rule X sets the mapped property
        tree.App.Styles.Add(R("Widget:chain-on", (Widget.P, 5)));  // rule Y rides the mapped pseudo
        tree.Host.ShowRoot(tree.Root);

        tree.A.Classes.Add("go"); // X activates ⇒ mapped flips ⇒ :chain-on ⇒ Y activates — all before this returns

        Assert.True(tree.A.GetValue(mapped));
        Assert.True(tree.A.HasPseudo(":chain-on"));
        Assert.Equal(5, tree.A.GetValue(Widget.P));
        Assert.Equal(2, StyleDiagnostics.MatchedRules(tree.A).Count(static info => info.IsActive));
    }

    [Fact]
    public void S124_ConstructedOscillation_TripsTheDebugStyleLoopDiagnostic_DrainTerminates()
    {
        var diagnostics = new List<(string Category, string Message)>();
        Action<string, string> handler = (category, message) => diagnostics.Add((category, message));
        StyleDebugDiagnostics.DiagnosticEmitted += handler;

        try
        {
            using var tree = ShowTree(show: false);
            var oscillating = R("Widget:osc", (Widget.P, 5));
            oscillating.Enter.Add(new SelfRetractingAction()); // activation flips :osc back off (A→B→A)
            tree.App.Styles.Add(oscillating);
            tree.Host.ShowRoot(tree.Root);

            tree.A.SetPseudo(":osc", true); // the drain terminates — no throw

            Assert.False(tree.A.HasPseudo(":osc"));
            Assert.Equal(0, tree.A.GetValue(Widget.P));

#if DEBUG
            var loop = Assert.Single(diagnostics, static d => d.Category == StyleDebugDiagnostics.StyleLoopCategory);
            Assert.Contains("Widget:osc", loop.Message, StringComparison.Ordinal); // names the rule pair
#else
            Assert.Empty(diagnostics);
#endif
        }
        finally
        {
            StyleDebugDiagnostics.DiagnosticEmitted -= handler;
        }
    }

    [Fact]
    public void S125_UnboundedCascade_GenerationCap16_ThrowsWithTrace()
    {
        var mapped = UIProperty.Register<Widget, bool>(UniqueName("PbOsc"));
        PseudoClassMapping.Register<Widget>(mapped, ":osc2");

        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R(".go", (mapped, true)));            // the weaker driver
        tree.App.Styles.Add(R("Widget:osc2", (mapped, false)));   // the stronger inverter — a true oscillator
        tree.Host.ShowRoot(tree.Root);

        var ex = Assert.Throws<InvalidOperationException>(() => tree.A.Classes.Add("go"));

        Assert.Contains("16 generations", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Widget:osc2", ex.Message, StringComparison.Ordinal); // the cycle trace
    }

    private sealed class SelfRetractingAction : IStyleEdgeAction
    {
        public void OnActivated(UIElement scope) => ((Widget)scope).SetPseudo(":osc", false);

        public void OnRetracted(UIElement scope)
        {
        }
    }

    private sealed class RecordingSink : IInteractionStateObserver
    {
        public void OnInteractionStateChanged(UIElement element, InteractionState oldState, InteractionState newState)
        {
        }
    }
}
