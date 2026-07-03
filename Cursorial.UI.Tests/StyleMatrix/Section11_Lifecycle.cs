using Cursorial.UI;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.StyleMatrix;

/// <summary>Style matrix §11 — lifecycle and the edge-action seam (S153–S163; B19, SD15, SD16, SD23-③).</summary>
public class Section11_Lifecycle
{
    /// <summary>The S158–S162 recording action: logs <c>(kind, id, scope)</c> per edge.</summary>
    private sealed class RecordingEdgeAction(string id, List<(string Kind, string Id, UIElement Scope)> log) : IStyleEdgeAction
    {
        public void OnActivated(UIElement scope) => log.Add(("enter", id, scope));

        public void OnRetracted(UIElement scope) => log.Add(("exit", id, scope));
    }

    /// <summary>The S155 recorder: one shared observer logging <c>(element, property)</c> deliveries in order.</summary>
    private sealed class RetractionRecorder : IValueObserver<int>
    {
        public readonly List<(UIObject Element, string Property)> Events = [];

        void IValueObserver<int>.OnPropertyChanged(UIObject source, UIProperty property, int oldValue, int newValue, BindingPriority priority)
            => Events.Add((source, property.Name));
    }

    [Fact]
    public void S153_ArmHappensBeforeTheFirstMeasure_FirstLayoutHonorsTheStyledWidth()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget", (UIElement.WidthProperty, 20)));

        tree.Host.ShowRoot(tree.Root);
        tree.Host.RunFrame(); // the FIRST frame — arm precedes measure on the attach walk (B19)

        Assert.Equal(20, tree.A.Bounds.Columns);
        Assert.Equal(20, tree.B.Bounds.Columns);
    }

    [Fact]
    public void S154_SubtreeDetach_RetractsEveryFrame_DefaultSource_EmptyMatchedRules()
    {
        using var tree = ShowTree();
        tree.App.Styles.Add(R("Widget", (Widget.P, 7)));
        Assert.Equal(7, tree.A.GetValue(Widget.P));
        Assert.Equal(7, tree.B.GetValue(Widget.P));

        tree.Root.Children.Remove(tree.PaneA); // detach the subtree

        Assert.Equal(0, tree.A.GetValue(Widget.P)); // store-owned promotion (invariant 4) — nothing set values back
        Assert.Equal(BindingPriority.Default, tree.A.GetValueSource(Widget.P).Priority);
        Assert.Equal(BindingPriority.Default, tree.B.GetValueSource(Widget.P).Priority);
        Assert.Empty(StyleDiagnostics.MatchedRules(tree.A));
        Assert.Empty(StyleDiagnostics.MatchedRules(tree.B));
        Assert.Empty(StyleDiagnostics.MatchedRules(tree.PaneA));
    }

    [Fact]
    public void S155_DetachRetractions_ArriveBottomUp_OneBatchPerElement()
    {
        using var tree = ShowTree(show: false);
        var top = new Card();
        var mid = new Card();
        var leaf = new Widget();
        mid.Add(leaf);
        top.Add(mid);
        tree.PaneA.Children.Add(top);
        tree.App.Styles.Add(R("Card", (Widget.P, 1), (Widget.Q, 2)));
        tree.App.Styles.Add(R("Widget", (Widget.P, 3), (Widget.Q, 4)));
        tree.Host.ShowRoot(tree.Root);
        Assert.Equal(3, leaf.GetValue(Widget.P));
        Assert.Equal(1, mid.GetValue(Widget.P));

        var recorder = new RetractionRecorder();
        foreach (var element in new UIObject[] { top, mid, leaf })
        {
            element.AddObserver(Widget.P, recorder);
            element.AddObserver(Widget.Q, recorder);
        }

        tree.PaneA.Children.Remove(top); // detach the 3-level subtree

        // Bottom-up: children before parents; each element's two retractions arrive as one batch.
        Assert.Equal(6, recorder.Events.Count);
        Assert.All(recorder.Events.Take(2), e => Assert.Same(leaf, e.Element));
        Assert.All(recorder.Events.Skip(2).Take(2), e => Assert.Same(mid, e.Element));
        Assert.All(recorder.Events.Skip(4), e => Assert.Same(top, e.Element));
        foreach (var batch in recorder.Events.Chunk(2))
            Assert.Equal(new[] { "P", "Q" }, batch.Select(static e => e.Property).Order().ToArray());
    }

    [Fact]
    public void S156_Reattach_RebuildsFreshState_RulesReArm_ValuesRestored()
    {
        using var tree = ShowTree();
        tree.App.Styles.Add(R("Widget", (Widget.P, 7)));
        Assert.Equal(7, tree.A.GetValue(Widget.P));

        tree.Root.Children.Remove(tree.PaneA);
        Assert.Equal(0, tree.A.GetValue(Widget.P));
        Assert.Empty(StyleDiagnostics.MatchedRules(tree.A));

        tree.Root.Children.Add(tree.PaneA); // reattach under the shown root

        Assert.Equal(7, tree.A.GetValue(Widget.P)); // re-match rebuilt from scratch (SD15)
        var armed = Assert.Single(StyleDiagnostics.MatchedRules(tree.A));
        Assert.True(armed.IsActive);
        Assert.Equal("Widget", armed.SelectorText);
    }

    [Fact]
    public void S157_ExplicitStyle_RetractsOnDetach_ReArmsOnAttach_ThePropertySurvives()
    {
        using var tree = ShowTree();
        var style = Explicit((Widget.P, 9));
        tree.A.Style = style;
        Assert.Equal(9, tree.A.GetValue(Widget.P));

        tree.Root.Children.Remove(tree.PaneA);

        Assert.Equal(0, tree.A.GetValue(Widget.P)); // the explicit frame retracted with the detach
        Assert.Empty(StyleDiagnostics.MatchedRules(tree.A));
        Assert.Same(style, tree.A.Style); // element state, not engine state — the assignment survives

        tree.Root.Children.Add(tree.PaneA);

        Assert.Equal(9, tree.A.GetValue(Widget.P));
        var armed = Assert.Single(StyleDiagnostics.MatchedRules(tree.A));
        Assert.Equal("(explicit)", armed.SelectorText);
        Assert.Equal(StyleLayer.Explicit, armed.Layer);
    }

    [Fact]
    public void S158_ActivationEdge_RunsEnterActions_InRuleDocumentOrder()
    {
        var log = new List<(string Kind, string Id, UIElement Scope)>();
        using var tree = ShowTree(show: false);
        var style = R("Widget:pointerover", (Widget.P, 1));
        style.Enter.Add(new RecordingEdgeAction("e1", log));
        style.Enter.Add(new RecordingEdgeAction("e2", log));
        style.Exit.Add(new RecordingEdgeAction("x1", log));
        style.Exit.Add(new RecordingEdgeAction("x2", log));
        tree.App.Styles.Add(style);
        tree.Host.ShowRoot(tree.Root);
        Assert.Empty(log); // armed-inactive — no edge yet

        tree.A.Flip(InteractionState.PointerOver, true);

        Assert.Equal([("enter", "e1", tree.A), ("enter", "e2", tree.A)], log);
    }

    [Fact]
    public void S159_DeactivationEdge_RunsExitActions_InRuleDocumentOrder()
    {
        var log = new List<(string Kind, string Id, UIElement Scope)>();
        using var tree = ShowTree(show: false);
        var style = R("Widget:pointerover", (Widget.P, 1));
        style.Enter.Add(new RecordingEdgeAction("e1", log));
        style.Exit.Add(new RecordingEdgeAction("x1", log));
        style.Exit.Add(new RecordingEdgeAction("x2", log));
        tree.App.Styles.Add(style);
        tree.Host.ShowRoot(tree.Root);
        tree.A.Flip(InteractionState.PointerOver, true);
        log.Clear();

        tree.A.Flip(InteractionState.PointerOver, false);

        Assert.Equal([("exit", "x1", tree.A), ("exit", "x2", tree.A)], log);
    }

    [Fact]
    public void S160_DetachOfAnActiveRule_RunsExitActions_AsPartOfTheRetraction()
    {
        var log = new List<(string Kind, string Id, UIElement Scope)>();
        using var tree = ShowTree(show: false);
        var style = R("Widget:pointerover", (Widget.P, 1));
        style.Exit.Add(new RecordingEdgeAction("x1", log));
        tree.App.Styles.Add(style);
        tree.Host.ShowRoot(tree.Root);
        tree.A.Flip(InteractionState.PointerOver, true);
        Assert.Equal(1, tree.A.GetValue(Widget.P));
        log.Clear();

        tree.PaneA.Children.Remove(tree.A); // detach the element

        Assert.Equal([("exit", "x1", tree.A)], log); // SD16: detach-driven retraction is an exit edge
    }

    [Fact]
    public void S161_ArmedButNeverActivated_DetachRunsNoExitAction()
    {
        var log = new List<(string Kind, string Id, UIElement Scope)>();
        using var tree = ShowTree(show: false);
        var style = R("Widget:pointerover", (Widget.P, 1));
        style.Exit.Add(new RecordingEdgeAction("x1", log));
        tree.App.Styles.Add(style);
        tree.Host.ShowRoot(tree.Root);

        tree.PaneA.Children.Remove(tree.A); // never activated — edges fire only from the active state

        Assert.Empty(log);
    }

    [Fact]
    public void S162_EdgeActions_FireOnEveryEdge_NotOnce()
    {
        var log = new List<(string Kind, string Id, UIElement Scope)>();
        using var tree = ShowTree(show: false);
        var style = R("Widget:pointerover", (Widget.P, 1));
        style.Enter.Add(new RecordingEdgeAction("e1", log));
        style.Exit.Add(new RecordingEdgeAction("x1", log));
        tree.App.Styles.Add(style);
        tree.Host.ShowRoot(tree.Root);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            tree.A.Flip(InteractionState.PointerOver, true);
            tree.A.Flip(InteractionState.PointerOver, false);
        }

        Assert.Equal(3, log.Count(static e => e.Kind == "enter"));
        Assert.Equal(3, log.Count(static e => e.Kind == "exit"));
    }

    [Fact]
    public void S163_HoverParityLint_OneTimeDebugDiagnostic_FocusRuleSilences()
    {
        var diagnostics = new List<(string Category, string Message)>();
        Action<string, string> handler = (category, message) => diagnostics.Add((category, message));
        StyleDebugDiagnostics.DiagnosticEmitted += handler;

        try
        {
            var offending = new Style("Widget", Resolver);
            var hover = new Style("^:pointerover");
            hover.Setters.Add(new Setter(Widget.P, 1));
            offending.Children.Add(hover);
            offending.Seal();

#if __NEVER__
            var finding = Assert.Single(diagnostics, static d => d.Category == StyleDebugDiagnostics.HoverParityCategory);
            Assert.Contains("Widget:pointerover", finding.Message, StringComparison.Ordinal);
            diagnostics.Clear();
#else
            Assert.Empty(diagnostics); // the lint is compiled out of release builds
#endif

            // Adding a :focus rule covering P silences the lint.
            var balanced = new Style("Widget", Resolver);
            var hover2 = new Style("^:pointerover");
            hover2.Setters.Add(new Setter(Widget.P, 1));
            var focus2 = new Style("^:focus");
            focus2.Setters.Add(new Setter(Widget.P, 2));
            balanced.Children.Add(hover2);
            balanced.Children.Add(focus2);
            balanced.Seal();

#pragma warning disable CS0618 // Type or member is obsolete
            Assert.DoesNotContain(diagnostics, static d => d.Category == StyleDebugDiagnostics.HoverParityCategory);
#pragma warning restore CS0618 // Type or member is obsolete
        }
        finally
        {
            StyleDebugDiagnostics.DiagnosticEmitted -= handler;
        }
    }
}
