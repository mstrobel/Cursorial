using Cursorial.UI;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.StyleMatrix;

/// <summary>Style matrix §8 — re-match dynamics: class, name, and style mutations (S126–S139).</summary>
public class Section08_Dynamics
{
    [Fact]
    public void S126_ClassAdd_SynchronousReMatchAtTheMutationSite()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R(".primary", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);
        Assert.Empty(StyleDiagnostics.MatchedRules(tree.A));

        tree.A.Classes.Add("primary"); // no frame pump — synchronous (SD12)

        Assert.True(Assert.Single(StyleDiagnostics.MatchedRules(tree.A)).IsActive);
        Assert.Equal(5, tree.A.GetValue(Widget.P));
    }

    [Fact]
    public void S127_ClassRemove_RetractsAndDisarms()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R(".primary", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);
        tree.A.Classes.Add("primary");
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        tree.A.Classes.Remove("primary");

        probe.AssertSingleNotify(5, 0, BindingPriority.Default);
        Assert.Empty(StyleDiagnostics.MatchedRules(tree.A));
    }

    [Fact]
    public void S128_UnrelatedClassAdd_RuleIdentityDiff_SurvivorsKeepFrames()
    {
        using var tree = ShowTree(show: false);
        tree.A.Classes.Add("keep");
        tree.App.Styles.Add(R(".keep", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);
        var frameBefore = Assert.Single(tree.A.StyleStateInternal!.Frames);
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        tree.A.Classes.Add("zzz"); // matches nothing

        Assert.Same(frameBefore, Assert.Single(tree.A.StyleStateInternal!.Frames)); // identity stable
        probe.AssertSilent();
    }

    [Fact]
    public void S129_Replace_OneRestylePass_SurvivorsNeverRetractAndReapply()
    {
        using var tree = ShowTree(show: false);
        tree.A.Classes.Add("keep");
        tree.A.Classes.Add("x");
        tree.App.Styles.Add(R(".keep", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        tree.A.Classes.Replace(["keep", "y"]); // swap the other classes; the matched rule survives

        probe.AssertSilent(); // no intermediate retract-then-reapply
        Assert.Equal(5, tree.A.GetValue(Widget.P));
        Assert.True(tree.A.Classes.Contains("y"));
        Assert.False(tree.A.Classes.Contains("x"));
    }

    [Fact]
    public void S130_NameChanges_ReMatchNameRules()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("#save", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);
        Assert.Empty(StyleDiagnostics.MatchedRules(tree.A));

        tree.A.Name = "save"; // post-attach rename
        Assert.Equal(5, tree.A.GetValue(Widget.P));

        tree.A.Name = null; // clear
        Assert.Equal(0, tree.A.GetValue(Widget.P));
        Assert.Empty(StyleDiagnostics.MatchedRules(tree.A));
    }

    [Fact]
    public void S131_AncestorInterestingClass_BoundedSubtreeReMatch()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Pane.fancy Widget", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);
        Assert.Empty(StyleDiagnostics.MatchedRules(tree.A));

        tree.PaneA.Classes.Add("fancy"); // ancestor-interesting ⇒ subtree re-match

        Assert.Equal(5, tree.A.GetValue(Widget.P));
        Assert.Equal(5, tree.B.GetValue(Widget.P));
        Assert.Equal(0, tree.C.GetValue(Widget.P)); // outside paneA's subtree
    }

    [Fact]
    public void S132_UninterestingClass_NoSubtreeReMatch_FrameIdentityStable()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Pane.fancy Widget", (Widget.P, 5)));
        tree.App.Styles.Add(R("Widget", (Widget.Q, 1))); // gives a an armed frame to watch
        tree.Host.ShowRoot(tree.Root);
        var stateBefore = tree.A.StyleStateInternal!;
        var framesBefore = stateBefore.Frames;
        var frameBefore = Assert.Single(framesBefore);

        tree.PaneA.Classes.Add("plain"); // not ancestor-interesting anywhere

        Assert.Same(framesBefore, tree.A.StyleStateInternal!.Frames); // a's arm state untouched
        Assert.Same(frameBefore, tree.A.StyleStateInternal!.Frames[0]);
    }

    [Fact]
    public void S133_ExplicitStyleSwap_SinglePromotionPerProperty_NoDefaultFlash()
    {
        using var tree = ShowTree();
        tree.A.Style = Explicit((Widget.P, 1));
        Assert.Equal(1, tree.A.GetValue(Widget.P));
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        tree.A.Style = Explicit((Widget.P, 2));

        probe.AssertSingleNotify(1, 2, BindingPriority.Style); // adds before removals — no Default flash
    }

    [Fact]
    public void S134_ExplicitStyleCleared_WeakerLayersPromote()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget", (Widget.P, 7)));
        tree.Host.ShowRoot(tree.Root);
        tree.A.Style = Explicit((Widget.P, 1));
        Assert.Equal(1, tree.A.GetValue(Widget.P));
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        tree.A.Style = null;

        probe.AssertSingleNotify(1, 7, BindingPriority.Style); // the App-layer rule promotes
    }

    [Fact]
    public void S135_AppStylesAdd_PostAttach_ScopeWideReMatch()
    {
        using var tree = ShowTree();
        Assert.Empty(StyleDiagnostics.MatchedRules(tree.A));

        tree.App.Styles.Add(R(":is(Widget)", (Widget.P, 5)));

        Assert.Equal(5, tree.A.GetValue(Widget.P));
        Assert.Equal(5, tree.B.GetValue(Widget.P));
        Assert.Equal(5, tree.C.GetValue(Widget.P)); // across the whole tree
    }

    [Fact]
    public void S136_AppStylesRemove_RetractsScopeWide()
    {
        using var tree = ShowTree();
        var style = R(":is(Widget)", (Widget.P, 5));
        tree.App.Styles.Add(style);
        Assert.Equal(5, tree.C.GetValue(Widget.P));

        tree.App.Styles.Remove(style);

        Assert.Equal(0, tree.A.GetValue(Widget.P)); // the store promotes per element
        Assert.Equal(0, tree.C.GetValue(Widget.P));
        Assert.Empty(StyleDiagnostics.MatchedRules(tree.A));
    }

    [Fact]
    public void S137_UnrelatedStyleAdd_SurvivorsKeepFramesAndCookies()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget", (Widget.P, 1)));
        tree.Host.ShowRoot(tree.Root);
        var frameBefore = Assert.Single(tree.A.StyleStateInternal!.Frames);
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        tree.App.Styles.Add(R(".zzz", (Widget.Q, 9))); // unrelated

        Assert.Same(frameBefore, Assert.Single(tree.A.StyleStateInternal!.Frames)); // identity diff (SD21)
        probe.AssertSilent();
    }

    [Fact]
    public void S138_StylesInstance_SingleOwnership()
    {
        using var tree = ShowTree();
        var styles = new Styles();
        tree.PaneA.Styles = styles;

        Assert.Throws<InvalidOperationException>(() => tree.PaneB.Styles = styles); // SD19
    }

    [Fact]
    public void S139_ScopedStylesAssignment_PostAttach_SubtreeReMatch()
    {
        using var tree = ShowTree();
        tree.A.Classes.Add("x");

        tree.PaneA.Styles = new Styles { R(".x", (Widget.P, 5)) };

        var armed = Assert.Single(StyleDiagnostics.MatchedRules(tree.A));
        Assert.Equal(StyleLayer.Scoped, armed.Layer);
        Assert.Equal(1, armed.Key.ScopeDepth); // paneA's styling depth (root = 0)
        Assert.Equal(5, tree.A.GetValue(Widget.P));
    }

    /// <summary>
    /// SD24: a structural mutation (here a class add) raised from a setter notification fired
    /// during the same element's own arm pass defers and drains to a structural fixpoint after the
    /// outer arm commits. The nested re-match's frame is tracked (not orphaned in the store), so it
    /// retracts cleanly — the invariant-4 re-entrancy hole the correctness review found.
    /// </summary>
    [Fact]
    public void S181_ReentrantStructuralMutation_DuringArm_DefersAndTracksTheNestedFrame()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R(".primary", (Widget.P, 9)));
        tree.App.Styles.Add(R(".x", (Widget.Q, 7)));

        // Observer on P: when .primary applies P=9, re-entrantly add class "x" (a structural change
        // on the element mid-arm). Without the SD24 fence, the .x frame arms in a nested re-match
        // that the outer ApplyMatchDiff then clobbers — applied-but-untracked, never retractable.
        var added = false;
        // ReSharper disable AccessToDisposedClosure
        var observer = new InlineObserver<int>((_, _, _, _) =>
        {
            if (added)
                return;
            added = true;
            tree.A.Classes.Add("x");
        });
        // ReSharper restore AccessToDisposedClosure
        tree.Host.ShowRoot(tree.Root);
        tree.A.AddObserver(Widget.P, observer);

        tree.A.Classes.Add("primary");

        // Both rules are tracked and active; the value held.
        Assert.Equal(7, tree.A.GetValue(Widget.Q));
        Assert.Equal(2, StyleDiagnostics.MatchedRules(tree.A).Count);

        // The nested frame is real — removing "x" retracts Q with no orphan.
        tree.A.Classes.Remove("x");
        Assert.Equal(0, tree.A.GetValue(Widget.Q));
        Assert.Single(StyleDiagnostics.MatchedRules(tree.A));
    }

    private sealed class InlineObserver<T>(Action<UIObject, UIProperty, T, T> onChanged) : IValueObserver<T>
    {
        public void OnPropertyChanged(UIObject source, UIProperty property, T oldValue, T newValue, BindingPriority priority)
            => onChanged(source, property, oldValue, newValue);
    }
}
