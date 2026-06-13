using Cursorial.UI;
using Cursorial.UI.Controls;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.StyleMatrix;

/// <summary>
/// Style matrix §4 — indexing, scope chain, structural matching (S59–S79). Trees are the §0.2
/// default fixture (<c>Root → paneA → a, b; paneB → c</c>) unless a row builds its own.
/// </summary>
public class Section04_Matching
{
    [Fact]
    public void S059_BareType_ExactClrTypeOnly()
    {
        using var tree = ShowTree(show: false);
        var fancy = new FancyWidget();
        var card = new Card();
        tree.PaneA.Children.Add(fancy);
        tree.PaneA.Children.Add(card);
        tree.App.Styles.Add(R("Widget", (Widget.P, 1)));
        tree.Host.ShowRoot(tree.Root);

        var armed = Assert.Single(StyleDiagnostics.MatchedRules(tree.A));
        Assert.True(armed.IsActive);
        Assert.Equal(1, tree.A.GetValue(Widget.P));

        Assert.Empty(StyleDiagnostics.MatchedRules(fancy)); // bare type = exact CLR type
        Assert.Equal(0, fancy.GetValue(Widget.P));
        Assert.Empty(StyleDiagnostics.MatchedRules(card));
    }

    [Fact]
    public void S060_IsType_AssignableMatching()
    {
        using var tree = ShowTree(show: false);
        var fancy = new FancyWidget();
        var card = new Card();
        tree.PaneA.Children.Add(fancy);
        tree.PaneA.Children.Add(card);
        tree.App.Styles.Add(R(":is(Widget)", (Widget.P, 1)));
        tree.Host.ShowRoot(tree.Root);

        Assert.Single(StyleDiagnostics.MatchedRules(tree.A));
        Assert.Single(StyleDiagnostics.MatchedRules(fancy)); // assignable
        Assert.Equal(1, fancy.GetValue(Widget.P));
        Assert.Empty(StyleDiagnostics.MatchedRules(card));
    }

    [Fact]
    public void S061_TypeKeyedCandidateExclusion_NeverEvaluatedAgainstOtherTypes()
    {
        using var tree = ShowTree(show: false);
        tree.A.Classes.Add("primary");
        tree.App.Styles.Add(R("Card.primary", (Widget.P, 1)));
        tree.Host.ShowRoot(tree.Root);

        // The rule is bucketed under exact-type Card — a classed Widget never gathers it.
        Assert.Empty(StyleDiagnostics.MatchedRules(tree.A));
        Assert.Equal(0, tree.A.GetValue(Widget.P));
    }

    [Fact]
    public void S062_ClassOnlyRule_MatchesAnyTypeCarryingTheClass()
    {
        using var tree = ShowTree(show: false);
        var card = new Card();
        tree.PaneA.Children.Add(card);
        tree.A.Classes.Add("primary");
        card.Classes.Add("primary");
        tree.PaneA.Classes.Add("primary");
        tree.App.Styles.Add(R(".primary", (Widget.P, 1)));
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(1, tree.A.GetValue(Widget.P));
        Assert.Equal(1, card.GetValue(Widget.P));
        Assert.Equal(1, tree.PaneA.GetValue(Widget.P));
        Assert.Empty(StyleDiagnostics.MatchedRules(tree.B)); // no class, no match
    }

    [Fact]
    public void S063_NameMatching_OrdinalCaseSensitive()
    {
        using var tree = ShowTree(show: false);
        tree.A.Name = "save";
        tree.B.Name = "Save";
        tree.App.Styles.Add(R("#save", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(5, tree.A.GetValue(Widget.P));
        Assert.Empty(StyleDiagnostics.MatchedRules(tree.B)); // ordinal — "Save" is a different name
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void S064_CompoundSimples_AllMustHold(bool hasClass, bool hasName, bool hasFocus)
    {
        using var tree = ShowTree(show: false);
        if (hasClass)
            tree.A.Classes.Add("a");
        if (hasName)
            tree.A.Name = "x";
        tree.App.Styles.Add(R("Widget.a#x:focus", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);
        if (hasFocus)
            tree.A.Flip(InteractionState.Focused, true);

        if (hasClass && hasName)
        {
            // Structurally matched; the pseudo gates activation.
            var armed = Assert.Single(StyleDiagnostics.MatchedRules(tree.A));
            Assert.Equal(hasFocus, armed.IsActive);
            Assert.Equal(hasFocus ? 5 : 0, tree.A.GetValue(Widget.P));
        }
        else
        {
            Assert.Empty(StyleDiagnostics.MatchedRules(tree.A)); // a missing structural simple ⇒ no arm
        }
    }

    [Fact]
    public void S065_ChildCombinator_ImmediateStylingParentOnly()
    {
        using var tree = ShowTree(show: false);
        var card = new Card();
        var grandchild = new Widget();
        card.Add(grandchild);
        tree.PaneA.Children.Add(card); // paneA > card > grandchild
        tree.App.Styles.Add(R("Pane > Widget", (Widget.P, 1)));
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(1, tree.A.GetValue(Widget.P)); // immediate child of paneA
        Assert.Empty(StyleDiagnostics.MatchedRules(grandchild)); // grandchild — not immediate
    }

    [Fact]
    public void S066_DescendantCombinator_AnyAncestorOnTheChain()
    {
        using var tree = ShowTree(show: false);
        var card = new Card();
        var nested = new Widget();
        card.Add(nested);
        tree.PaneA.Children.Add(card); // paneA > card > nested — ≥2 levels under a Pane
        tree.App.Styles.Add(R("Pane Widget", (Widget.P, 1)));
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(1, nested.GetValue(Widget.P));
    }

    [Fact]
    public void S067_VisualOnlyAttachment_TraversalFallsBackToVisualParent()
    {
        using var tree = ShowTree();
        var leaf = new Widget();
        tree.PaneA.AddVisualChildOnly(leaf); // LogicalParent stays null
        Assert.Null(leaf.LogicalParent);

        tree.App.Styles.Add(R("Pane Widget", (Widget.P, 1)));

        Assert.Equal(1, leaf.GetValue(Widget.P)); // LogicalParent ?? VisualParent (SD7)
    }

    [Fact]
    public void S068_DescendantBacktracking_NoGreedyFirstCandidateFailure()
    {
        using var tree = ShowTree(show: false);
        var card1 = new Card();
        var card2 = new Card();
        var widget = new Widget();
        card2.Add(widget);
        card1.Add(card2);
        tree.PaneA.Children.Add(card1); // paneA > card1 > card2 > widget
        tree.App.Styles.Add(R("Pane > Card Widget", (Widget.P, 1)));
        tree.Host.ShowRoot(tree.Root);

        // Card₂'s parent fails the 'Pane >' test, but the walk backtracks to Card₁ and succeeds.
        Assert.Equal(1, widget.GetValue(Widget.P));
    }

    [Fact]
    public void S069_PseudoOnlyRule_UniversalBucket_ArmsEverywhere_DebugDiagnostic()
    {
        var diagnostics = new List<string>();
        Action<string, string> handler = (category, _) => diagnostics.Add(category);
        StyleDebugDiagnostics.DiagnosticEmitted += handler;

        try
        {
            using var tree = ShowTree(show: false);
            tree.App.Styles.Add(R(":focus", (Widget.P, 5)));
            tree.Host.ShowRoot(tree.Root);

            // Arms (inactive) on every element in scope — the universal bucket.
            Assert.Single(StyleDiagnostics.MatchedRules(tree.Root));
            Assert.Single(StyleDiagnostics.MatchedRules(tree.PaneA));
            Assert.Single(StyleDiagnostics.MatchedRules(tree.A));
            Assert.Single(StyleDiagnostics.MatchedRules(tree.C));

#if DEBUG
            Assert.Single(diagnostics, StyleDebugDiagnostics.UniversalBucketCategory); // one-time (SD23-①)
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
    public void S070_ScopedCollection_OwnerSelfInclusivePlusSubtree()
    {
        using var tree = ShowTree(show: false);
        tree.PaneA.Classes.Add("s");
        tree.A.Classes.Add("s");
        tree.C.Classes.Add("s");
        tree.PaneA.Styles.Add(R(".s", (Widget.P, 1)));
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(1, tree.PaneA.GetValue(Widget.P)); // the owner itself
        Assert.Equal(1, tree.A.GetValue(Widget.P));     // a descendant
        Assert.Empty(StyleDiagnostics.MatchedRules(tree.C)); // outside the scope
    }

    [Fact]
    public void S071_ArmedVersusActive_FlipActivatesWithoutReMatch()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget:pointerover", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);

        var armed = Assert.Single(StyleDiagnostics.MatchedRules(tree.A));
        Assert.False(armed.IsActive);
        Assert.Equal(0, tree.A.GetValue(Widget.P));
        Assert.Equal(new ValueSource(BindingPriority.Default, false), tree.A.GetValueSource(Widget.P));

        tree.A.Flip(InteractionState.PointerOver, true);

        var active = Assert.Single(StyleDiagnostics.MatchedRules(tree.A));
        Assert.True(active.IsActive);
        Assert.Equal(5, tree.A.GetValue(Widget.P));
        Assert.Equal(new ValueSource(BindingPriority.Style, false), tree.A.GetValueSource(Widget.P));
    }

    [Fact]
    public void S072_ArmTimeTruth_AlreadySatisfiedRuleActivatesInTheArmPass()
    {
        using var tree = ShowTree();
        tree.App.Styles.Add(R("Widget.primary:pointerover", (Widget.P, 5)));
        tree.A.Flip(InteractionState.PointerOver, true); // bit set BEFORE the rule arms
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        tree.A.Classes.Add("primary"); // the arm trigger

        probe.AssertSingleNotify(0, 5, BindingPriority.Style); // one notify, no inactive flicker (SD18)
        Assert.Equal(5, tree.A.GetValue(Widget.P));
    }

    [Fact]
    public void S073_MultiplePseudos_AllBitsRequired()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget:focus:pointerover", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);

        tree.A.Flip(InteractionState.Focused, true);
        Assert.Equal(0, tree.A.GetValue(Widget.P));
        tree.A.Flip(InteractionState.Focused, false);

        tree.A.Flip(InteractionState.PointerOver, true);
        Assert.Equal(0, tree.A.GetValue(Widget.P));

        tree.A.Flip(InteractionState.Focused, true);
        Assert.Equal(5, tree.A.GetValue(Widget.P)); // both bits ⇒ active
    }

    [Fact]
    public void S074_AncestorStateRequirement_AncestorFlipActivatesDependents()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Pane:focus-within Widget", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);

        Assert.False(Assert.Single(StyleDiagnostics.MatchedRules(tree.A)).IsActive);

        tree.PaneA.Flip(InteractionState.FocusWithin, true);

        Assert.Equal(5, tree.A.GetValue(Widget.P));
        Assert.Equal(5, tree.B.GetValue(Widget.P));
        Assert.Equal(0, tree.C.GetValue(Widget.P)); // its Pane ancestors carry no focus-within
    }

    [Fact]
    public void S075_AncestorFlipOff_RetractsDependents()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Pane:focus-within Widget", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);
        tree.PaneA.Flip(InteractionState.FocusWithin, true);
        Assert.Equal(5, tree.A.GetValue(Widget.P));
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        tree.PaneA.Flip(InteractionState.FocusWithin, false);

        probe.AssertSingleNotify(5, 0, BindingPriority.Default);
    }

    [Fact]
    public void S076_TwoAncestorStateCompounds_Functional_DebugDiagnostic()
    {
        var diagnostics = new List<string>();
        Action<string, string> handler = (category, _) => diagnostics.Add(category);
        StyleDebugDiagnostics.DiagnosticEmitted += handler;

        try
        {
            using var tree = ShowTree(show: false);
            tree.App.Styles.Add(R("Pane:focus-within Pane:pointerover Widget", (Widget.P, 5)));
            tree.Host.ShowRoot(tree.Root);

            // Chain for a: [a, paneA, Root] — compound₀ binds Root, compound₁ binds paneA.
            tree.Root.Flip(InteractionState.FocusWithin, true);
            Assert.Equal(0, tree.A.GetValue(Widget.P));

            tree.PaneA.Flip(InteractionState.PointerOver, true);
            Assert.Equal(5, tree.A.GetValue(Widget.P)); // functional

#if DEBUG
            Assert.Contains(StyleDebugDiagnostics.AncestorStateCategory, diagnostics); // SD23-②
            Assert.Single(diagnostics, StyleDebugDiagnostics.AncestorStateCategory);   // one-time
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
    public void S077_DetachedElementsMatchNothing_AttachEngages()
    {
        using var tree = ShowTree();
        tree.App.Styles.Add(R(".x", (Widget.P, 5)));

        var loose = new Widget();
        loose.Classes.Add("x");
        Assert.Empty(StyleDiagnostics.MatchedRules(loose)); // pre-attach: nothing

        tree.PaneA.Children.Add(loose);

        Assert.True(Assert.Single(StyleDiagnostics.MatchedRules(loose)).IsActive);
        Assert.Equal(5, loose.GetValue(Widget.P));
    }

    [Fact]
    public void S078_SubtreeAttach_OneWalkArmsEveryDescendant()
    {
        using var tree = ShowTree();
        tree.App.Styles.Add(R(":is(StackPanel)", (Widget.P, 1)));
        tree.App.Styles.Add(R(":is(Widget)", (Widget.P, 2)));

        var mid = new StackPanel();
        var inner = new StackPanel();
        var leaf = new Widget();
        inner.Children.Add(leaf);
        mid.Children.Add(inner);

        tree.Root.Children.Add(mid); // ONE attach operation for the 3-level subtree

        Assert.Equal(1, mid.GetValue(Widget.P));
        Assert.Equal(1, inner.GetValue(Widget.P));
        Assert.Equal(2, leaf.GetValue(Widget.P));
    }

    [Fact]
    public void S079_SelectorList_BothMembersArm_OneNotify()
    {
        using var tree = ShowTree();
        tree.A.Name = "a";
        tree.A.Classes.Add("b");
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        tree.App.Styles.Add(R("#a, .b", (Widget.P, 7)));

        var armed = StyleDiagnostics.MatchedRules(tree.A);
        Assert.Equal(2, armed.Count); // each member is its own rule with its own key
        Assert.All(armed, static info => Assert.True(info.IsActive));
        Assert.Equal(1, armed[0].Key.Names);     // strongest first: the #a member
        Assert.Equal(0, armed[0].Key.ClassLike);
        Assert.Equal(0, armed[1].Key.Names);
        Assert.Equal(1, armed[1].Key.ClassLike);

        Assert.Equal(7, tree.A.GetValue(Widget.P));
        probe.AssertSingleNotify(0, 7, BindingPriority.Style); // the weaker member's activation is masked-silent (M37)
    }
}
