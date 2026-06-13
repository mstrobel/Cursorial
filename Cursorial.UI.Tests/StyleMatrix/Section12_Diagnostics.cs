using Cursorial.UI;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.StyleMatrix;

/// <summary>Style matrix §12 — diagnostics (S164–S172; SD13 — the §3.9/§14 acceptance format).</summary>
public class Section12_Diagnostics
{
    private static string[] ExplainLines(UIElement element, UIProperty property)
        => StyleDiagnostics.Explain(element, property).Split('\n');

    [Fact]
    public void S164_Explain_WinningLine_TheExactAcceptanceLiteral()
    {
        using var tree = ShowTree(show: false);
        tree.PaneA.Styles.Add(R("Widget.primary:pointerover", (Widget.P, 9))); // S(paneA), owner depth 1
        tree.A.Classes.Add("primary");
        tree.Host.ShowRoot(tree.Root);
        tree.A.Flip(InteractionState.PointerOver, true);
        Assert.Equal(9, tree.A.GetValue(Widget.P));

        var lines = ExplainLines(tree.A, Widget.P);

        // The §3.9/§14 acceptance test — the full sort-key derivation in ONE line (SD13).
        Assert.Equal(
            "P = 9 <- Scoped(4) \"Widget.primary:pointerover\" names=0 classLike=2 types=1 depth=1 order=0 key=0x8000100808000000 -- winning",
            lines[0]);
        Assert.Single(lines);
    }

    [Fact]
    public void S165_Explain_ShadowedSetter_FullDerivation_StrongestFirst()
    {
        using var tree = ShowTree(show: false);
        tree.PaneA.Styles.Add(R("Widget.primary:pointerover", (Widget.P, 9)));
        tree.App.Styles.Add(R("Widget", (Widget.P, 3))); // weaker, active
        tree.A.Classes.Add("primary");
        tree.Host.ShowRoot(tree.Root);
        tree.A.Flip(InteractionState.PointerOver, true);

        var lines = ExplainLines(tree.A, Widget.P);

        Assert.Equal(2, lines.Length);
        Assert.Equal(
            "P = 9 <- Scoped(4) \"Widget.primary:pointerover\" names=0 classLike=2 types=1 depth=1 order=0 key=0x8000100808000000 -- winning",
            lines[0]);
        Assert.Equal(
            "P = 3 <- App(3) \"Widget\" names=0 classLike=0 types=1 depth=0 order=0 key=0x6000000800000000 -- shadowed",
            lines[1]);
    }

    [Fact]
    public void S166_Explain_LocalValueWins_EveryStyleLineShadowed()
    {
        using var tree = ShowTree(show: false);
        tree.PaneA.Styles.Add(R("Widget.primary:pointerover", (Widget.P, 9)));
        tree.App.Styles.Add(R("Widget", (Widget.P, 3)));
        tree.A.Classes.Add("primary");
        tree.Host.ShowRoot(tree.Root);
        tree.A.Flip(InteractionState.PointerOver, true);
        tree.A.SetValue(Widget.P, 7); // L(7)

        var lines = ExplainLines(tree.A, Widget.P);

        Assert.Equal(3, lines.Length);
        Assert.Equal("P = 7 <- LocalValue", lines[0]);
        Assert.EndsWith("-- shadowed", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("-- shadowed", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void S167_Explain_NoStyleContribution_SingleLaneLine()
    {
        using var tree = ShowTree();

        // Fresh — the default lane.
        Assert.Equal("P = 0 <- Default", StyleDiagnostics.Explain(tree.A, Widget.P));

        // Inherited-only — the contributing lane is Inherited.
        tree.PaneA.SetValue(Widget.Pi, 4);
        Assert.Equal("Pi = 4 <- Inherited", StyleDiagnostics.Explain(tree.A, Widget.Pi));
    }

    [Fact]
    public void S168_Explain_UnsetValueSetter_RendersUnset()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget", (Widget.P, UIProperty.UnsetValue)));
        tree.Host.ShowRoot(tree.Root);

        var lines = ExplainLines(tree.A, Widget.P);

        Assert.Equal(2, lines.Length);
        Assert.Equal("P = 0 <- Default", lines[0]); // the valueless entry contributes nothing (A8)
        Assert.StartsWith("P = (unset) <- App(3) \"Widget\"", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("-- shadowed", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void S169_Explain_ChildrenComposedRule_RendersTheFlattenedCanonicalSelector()
    {
        using var tree = ShowTree(show: false);
        var parent = new Style("Widget.primary", Resolver);
        var child = new Style("^:pointerover");
        child.Setters.Add(new Setter(Widget.P, 9));
        parent.Children.Add(child);
        tree.App.Styles.Add(parent);
        tree.A.Classes.Add("primary");
        tree.Host.ShowRoot(tree.Root);
        tree.A.Flip(InteractionState.PointerOver, true);

        var line = Assert.Single(ExplainLines(tree.A, Widget.P));

        Assert.Contains("\"Widget.primary:pointerover\"", line, StringComparison.Ordinal); // flattened — no '^'
        Assert.DoesNotContain("^", line, StringComparison.Ordinal);
        Assert.EndsWith("-- winning", line, StringComparison.Ordinal);
    }

    [Fact]
    public void S170_Explain_SelectorLessExplicitStyle_RendersExplicit()
    {
        using var tree = ShowTree();
        tree.A.Style = Explicit((Widget.P, 9));

        var line = Assert.Single(ExplainLines(tree.A, Widget.P));

        Assert.Equal(
            "P = 9 <- Explicit(5) \"(explicit)\" names=0 classLike=0 types=0 depth=0 order=0 key=0xA000000000000000 -- winning",
            line);
    }

    [Fact]
    public void S171_MatchedRules_StrongestFirst_InactiveArmedRulesPresent()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget", (Widget.P, 1)));             // active
        tree.App.Styles.Add(R("Widget:pointerover", (Widget.Q, 2))); // armed-inactive, stronger
        tree.Host.ShowRoot(tree.Root);

        var rules = StyleDiagnostics.MatchedRules(tree.A);

        Assert.Equal(2, rules.Count);
        Assert.Equal("Widget:pointerover", rules[0].SelectorText);
        Assert.Equal(StyleLayer.App, rules[0].Layer);
        Assert.False(rules[0].IsActive); // armed-inactive rules are listed — activation is a flip, not a re-match
        Assert.Equal(StyleSortKey.Create(StyleLayer.App, names: 0, classLike: 1, types: 1, scopeDepth: 0, order: 1), rules[0].Key);
        Assert.Equal("Widget", rules[1].SelectorText);
        Assert.True(rules[1].IsActive);
        Assert.True(rules[0].Key > rules[1].Key); // strongest first
    }

    [Fact]
    public void S172_MatchedRules_NonMatchingAbsent_BarrierSkippedEmptyExceptTemplateAndExplicit()
    {
        using var tree = ShowTree(show: false);
        var card = new Card();
        var part = new Widget();
        part.SetTemplatedParent(card);
        card.Add(part);
        tree.PaneA.Children.Add(card);
        tree.App.Styles.Add(R("Card", (Widget.P, 1)));                   // structurally non-matching for a
        tree.App.Styles.Add(R("Widget", (Widget.P, 2)));                 // barrier-skipped for the part
        tree.App.Styles.Add(R("Card /template/ Widget", (Widget.Q, 3))); // the sanctioned crossing
        part.Style = Explicit((Widget.P, 9));
        tree.Host.ShowRoot(tree.Root);

        // Structurally non-matching rules are ABSENT (not armed-inactive).
        Assert.DoesNotContain(StyleDiagnostics.MatchedRules(tree.A), static r => r.SelectorText == "Card");

        // The barrier-skipped part lists only its /template/ and explicit entries.
        var partRules = StyleDiagnostics.MatchedRules(part);
        Assert.Equal(2, partRules.Count);
        Assert.Contains(partRules, static r => r.SelectorText == "(explicit)");
        Assert.Contains(partRules, static r => r.SelectorText == "Card /template/ Widget");
        Assert.DoesNotContain(partRules, static r => r.SelectorText == "Widget");
    }
}
