using Cursorial.UI;
using Cursorial.UI.Controls;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.StyleMatrix;

/// <summary>
/// Style matrix §5 — the template barrier (S80–S88, invariant 5). All rows stamp
/// <c>TemplatedParent</c> manually via <c>SetTemplatedParent</c> while detached (the P1 seam) —
/// template instantiation is P5.
/// </summary>
public class Section05_TemplateBarrier
{
    [Fact]
    public void S080_BarrierSkipsTheSubject_BeforeCandidateScan()
    {
        using var tree = ShowTree(show: false);
        var card = new Card();
        var part = new Widget();
        part.SetTemplatedParent(card);
        card.Add(part);
        tree.PaneA.Children.Add(card);
        tree.App.Styles.Add(R("Widget", (Widget.P, 1)));
        tree.Host.ShowRoot(tree.Root);

        Assert.Empty(StyleDiagnostics.MatchedRules(part));
        Assert.Equal(new ValueSource(BindingPriority.Default, false), part.GetValueSource(Widget.P));
        Assert.Equal(1, tree.A.GetValue(Widget.P)); // non-templated widgets still match
    }

    [Theory]
    [InlineData("Widget")]
    [InlineData(".chrome-part")]
    [InlineData("#chrome")]
    [InlineData(":focus")]
    public void S081_BarrierCoversEveryRuleShape(string selector)
    {
        using var tree = ShowTree(show: false);
        var card = new Card();
        var part = new Widget { Name = "chrome" };
        part.Classes.Add("chrome-part");
        part.SetTemplatedParent(card);
        card.Add(part);
        tree.PaneA.Children.Add(card);
        tree.App.Styles.Add(R(selector, (Widget.P, 1)));
        tree.Host.ShowRoot(tree.Root);

        Assert.Empty(StyleDiagnostics.MatchedRules(part));
    }

    [Fact]
    public void S082_TemplateCombinator_IsTheSanctionedCrossing()
    {
        using var tree = ShowTree(show: false);
        var card = new Card();
        var part = new Widget { Name = "chrome" };
        part.SetTemplatedParent(card);
        card.Add(part);
        tree.PaneA.Children.Add(card);
        tree.App.Styles.Add(R("Card /template/ #chrome", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);

        var armed = Assert.Single(StyleDiagnostics.MatchedRules(part));
        Assert.True(armed.IsActive);
        Assert.Equal(5, part.GetValue(Widget.P));
    }

    [Fact]
    public void S083_LeftCompoundMustMatchTheTemplatedParent()
    {
        using var tree = ShowTree(show: false);
        var part = new Widget { Name = "chrome" };
        part.SetTemplatedParent(tree.B); // a Widget, not a Card
        tree.PaneA.Children.Add(part);
        tree.App.Styles.Add(R("Card /template/ #chrome", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);

        Assert.Empty(StyleDiagnostics.MatchedRules(part));
    }

    [Fact]
    public void S084_TemplateHop_RequiresANonNullTemplatedParent()
    {
        using var tree = ShowTree(show: false);
        var free = new Widget { Name = "chrome" }; // TemplatedParent == null
        tree.PaneA.Children.Add(free);
        tree.App.Styles.Add(R("Card /template/ #chrome", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);

        Assert.Empty(StyleDiagnostics.MatchedRules(free));
    }

    [Fact]
    public void S085_EachHopCrossesExactlyOneStampEdge()
    {
        using var tree = ShowTree(show: false);
        var card = new Card();
        var pane = new StackPanel();
        var inner = new Widget();
        pane.SetTemplatedParent(card);
        inner.SetTemplatedParent(pane);
        pane.Children.Add(inner);
        card.Add(pane);
        tree.PaneA.Children.Add(card);
        tree.App.Styles.Add(R("Card /template/ Pane /template/ Widget", (Widget.P, 5)));
        tree.App.Styles.Add(R("Card /template/ Widget", (Widget.Q, 9)));
        tree.Host.ShowRoot(tree.Root);

        var armed = Assert.Single(StyleDiagnostics.MatchedRules(inner));
        Assert.Equal("Card /template/ Pane /template/ Widget", armed.SelectorText); // the two-hop rule
        Assert.Equal(5, inner.GetValue(Widget.P));
        Assert.Equal(0, inner.GetValue(Widget.Q)); // the single-hop rule does NOT match inner
    }

    [Fact]
    public void S086_CombinatorsRightOfTheHop_WalkInsideTheTemplateSubtree()
    {
        using var tree = ShowTree(show: false);
        var card = new Card();
        var pane = new StackPanel();
        var inner = new Widget();
        pane.SetTemplatedParent(card);
        inner.SetTemplatedParent(card); // both parts stamped to the same templated control
        pane.Children.Add(inner);       // inner is a visual child of pane
        card.Add(pane);
        tree.PaneA.Children.Add(card);
        tree.App.Styles.Add(R("Card /template/ Pane > Widget", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(5, inner.GetValue(Widget.P));
    }

    [Fact]
    public void S087_CompoundsLeftOfTheTemplatedControl_ContinueTheOrdinaryWalk()
    {
        using var tree = ShowTree(show: false);
        var card = new Card();
        var part = new Widget { Name = "chrome" };
        part.SetTemplatedParent(card);
        card.Add(part);
        tree.PaneA.Children.Add(card); // card sits under paneA
        tree.App.Styles.Add(R("Pane > Card /template/ #chrome", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(5, part.GetValue(Widget.P));
    }

    [Fact]
    public void S088_ExplicitStyle_ArmsRegardlessOfTemplatedParent()
    {
        using var tree = ShowTree(show: false);
        var card = new Card();
        var part = new Widget();
        part.SetTemplatedParent(card);
        card.Add(part);
        tree.PaneA.Children.Add(card);
        tree.Host.ShowRoot(tree.Root);

        part.Style = Explicit((Widget.P, 9)); // element-addressed — the barrier governs selector matching only (SD8)

        var armed = Assert.Single(StyleDiagnostics.MatchedRules(part));
        Assert.Equal(StyleLayer.Explicit, armed.Layer);
        Assert.Equal(9, part.GetValue(Widget.P));
    }
}
