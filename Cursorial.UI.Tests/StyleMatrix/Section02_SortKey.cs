using Cursorial.UI;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.StyleMatrix;

/// <summary>
/// Style matrix §2 — specificity and the packed <see cref="StyleSortKey"/> (S24–S34). S35–S38 are
/// effective-value rows over armed rules — they land with the matcher/store stage (Y3).
/// </summary>
public class Section02_SortKey
{
    private const int OrderMax = (1 << 27) - 1;

    [Fact]
    public void S024_WorkedExample_BitExactPacking()
    {
        var key = StyleSortKey.Create(StyleLayer.Scoped, names: 0, classLike: 2, types: 1, scopeDepth: 1, order: 0);

        Assert.Equal(0x8000100808000000UL, key.Packed);
        Assert.Equal(StyleLayer.Scoped, key.Layer);
        Assert.Equal(0, key.Names);
        Assert.Equal(2, key.ClassLike);
        Assert.Equal(1, key.Types);
        Assert.Equal(1, key.ScopeDepth);
        Assert.Equal(0, key.Order);
    }

    [Fact]
    public void S025_LayerBeatsAnySaturatedSpecificity()
    {
        var theme = StyleSortKey.Create(StyleLayer.Theme, 0, 0, 0, 0, 0);
        var template = StyleSortKey.Create(StyleLayer.Template, 255, 1023, 255, 255, OrderMax);

        Assert.True(theme > template);
    }

    [Theory]
    [InlineData("names")]
    [InlineData("classLike")]
    [InlineData("types")]
    [InlineData("scopeDepth")]
    public void S026_AdjacentFieldDominance_OneBeatsFullSaturationBelow(string field)
    {
        var (one, saturatedBelow) = field switch
        {
            "names" => (
                StyleSortKey.Create(StyleLayer.Scoped, 1, 0, 0, 0, 0),
                StyleSortKey.Create(StyleLayer.Scoped, 0, 1023, 255, 255, OrderMax)),
            "classLike" => (
                StyleSortKey.Create(StyleLayer.Scoped, 0, 1, 0, 0, 0),
                StyleSortKey.Create(StyleLayer.Scoped, 0, 0, 255, 255, OrderMax)),
            "types" => (
                StyleSortKey.Create(StyleLayer.Scoped, 0, 0, 1, 0, 0),
                StyleSortKey.Create(StyleLayer.Scoped, 0, 0, 0, 255, OrderMax)),
            _ => (
                StyleSortKey.Create(StyleLayer.Scoped, 0, 0, 0, 1, 0),
                StyleSortKey.Create(StyleLayer.Scoped, 0, 0, 0, 0, OrderMax))
        };

        Assert.True(one > saturatedBelow);
    }

    [Theory]
    [InlineData("names")]
    [InlineData("classLike")]
    [InlineData("types")]
    [InlineData("scopeDepth")]
    [InlineData("order")]
    public void S027_FieldSaturation_ClampsWithoutCarry(string field)
    {
        var (overflowing, atMax) = field switch
        {
            "names" => (
                StyleSortKey.Create(StyleLayer.ControlTheme, 300, 0, 0, 0, 0),
                StyleSortKey.Create(StyleLayer.ControlTheme, 255, 0, 0, 0, 0)),
            "classLike" => (
                StyleSortKey.Create(StyleLayer.ControlTheme, 0, 2000, 0, 0, 0),
                StyleSortKey.Create(StyleLayer.ControlTheme, 0, 1023, 0, 0, 0)),
            "types" => (
                StyleSortKey.Create(StyleLayer.ControlTheme, 0, 0, 300, 0, 0),
                StyleSortKey.Create(StyleLayer.ControlTheme, 0, 0, 255, 0, 0)),
            "scopeDepth" => (
                StyleSortKey.Create(StyleLayer.ControlTheme, 0, 0, 0, 300, 0),
                StyleSortKey.Create(StyleLayer.ControlTheme, 0, 0, 0, 255, 0)),
            _ => (
                StyleSortKey.Create(StyleLayer.ControlTheme, 0, 0, 0, 0, 1 << 28),
                StyleSortKey.Create(StyleLayer.ControlTheme, 0, 0, 0, 0, OrderMax))
        };

        // Saturation, not overflow: the packed bits equal the at-max key — nothing carried into a
        // neighboring field (the layer stays ControlTheme(0), so any carry would be visible).
        Assert.Equal(atMax.Packed, overflowing.Packed);
        Assert.Equal(StyleLayer.ControlTheme, overflowing.Layer);
    }

    [Fact]
    public void S028_CompoundComponents_TypePlusClassPlusPseudo()
    {
        var style = R("Widget.primary:pointerover");
        style.Seal();

        var rule = Assert.Single(style.CompiledRules);
        Assert.Equal(1, rule.Types);
        Assert.Equal(2, rule.ClassLike);
        Assert.Equal(0, rule.Names);
    }

    [Fact]
    public void S029_Components_CountedAcrossAllCompounds()
    {
        var style = R("Pane.toolbar > Widget.primary");
        style.Seal();

        var rule = Assert.Single(style.CompiledRules);
        Assert.Equal(2, rule.Types);
        Assert.Equal(2, rule.ClassLike);
        Assert.Equal(0, rule.Names);
    }

    [Fact]
    public void S030_NameRule_AndIsTypeRule_Components()
    {
        var nameStyle = R("#save");
        nameStyle.Seal();
        var nameRule = Assert.Single(nameStyle.CompiledRules);
        Assert.Equal((1, 0, 0), (nameRule.Names, nameRule.ClassLike, nameRule.Types));

        // ':is' carries its argument's specificity (CSS).
        var isStyle = R(":is(Widget)");
        isStyle.Seal();
        var isRule = Assert.Single(isStyle.CompiledRules);
        Assert.Equal((0, 0, 1), (isRule.Names, isRule.ClassLike, isRule.Types));
    }

    [Fact]
    public void S031_NestingComposition_ComposesParentCounts()
    {
        var style = R("Widget.primary");
        style.Children.Add(R("^:pointerover"));
        style.Seal();

        var childRule = style.CompiledRules[1];
        Assert.Equal("Widget.primary:pointerover", childRule.SelectorText);
        Assert.Equal(1, childRule.Types);
        Assert.Equal(2, childRule.ClassLike);
        Assert.Equal(0, childRule.Names);
    }

    [Fact]
    public void S032_TemplateHop_BothSidesCount()
    {
        var style = R("Widget.primary /template/ #chrome");
        style.Seal();

        var rule = Assert.Single(style.CompiledRules);
        Assert.Equal(1, rule.Types);
        Assert.Equal(1, rule.ClassLike);
        Assert.Equal(1, rule.Names);
    }

    [Fact]
    public void S033_SelectorListMembers_OwnSpecificity_SharedSetters()
    {
        var style = R("#save, .primary", (Widget.P, 1));
        style.Seal();

        Assert.Equal(2, style.CompiledRules.Length);
        var member1 = style.CompiledRules[0];
        var member2 = style.CompiledRules[1];
        Assert.Equal((1, 0), (member1.Names, member1.ClassLike));
        Assert.Equal((0, 1), (member2.Names, member2.ClassLike));
        Assert.Same(member1.Setters, member2.Setters); // shared setters, per-member keys (doc §3.1)
    }

    [Fact]
    public void S034_ScopeOrderIndices_DepthFirstDeclarationOrder()
    {
        var styleA = R("Widget");
        var styleB = R(".b");
        styleB.Children.Add(R("^:focus"));
        styleB.Children.Add(R("^:pointerover"));
        var styleC = R("#save, .primary");

        var styles = new Styles { styleA, styleB, styleC };
        var rules = styles.BuildScopeRules(StyleLayer.Scoped, scopeDepth: 1);

        Assert.Equal(
            ["Widget", ".b", ".b:focus", ".b:pointerover", "#save", ".primary"],
            rules.Select(static r => r.Rule.SelectorText));
        Assert.Equal([0, 1, 2, 3, 4, 5], rules.Select(static r => r.Order));
        Assert.Equal([0, 1, 2, 3, 4, 5], rules.Select(static r => r.Key.Order));
        Assert.All(rules, static r => Assert.Equal(StyleLayer.Scoped, r.Key.Layer));
        Assert.All(rules, static r => Assert.Equal(1, r.Key.ScopeDepth));
    }

    [Fact]
    public void S035_EqualSpecificity_LaterDeclarationOrderWins()
    {
        using var tree = StyleMatrixFixture.ShowTree(show: false);
        tree.A.Classes.Add("a");
        tree.A.Classes.Add("b");
        tree.App.Styles.Add(StyleMatrixFixture.R(".a", (Widget.P, 1)));
        tree.App.Styles.Add(StyleMatrixFixture.R(".b", (Widget.P, 2)));
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(2, tree.A.GetValue(Widget.P)); // equal keys except order — later wins
    }

    [Fact]
    public void S036_IdenticalRule_DeeperScopeWins()
    {
        using var tree = StyleMatrixFixture.ShowTree(show: false);
        tree.A.Classes.Add("x");
        tree.PaneA.Styles.Add(StyleMatrixFixture.R(".x", (Widget.P, 1))); // owner depth 1
        tree.A.Styles.Add(StyleMatrixFixture.R(".x", (Widget.P, 2)));     // owner depth 2
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(2, tree.A.GetValue(Widget.P)); // scopeDepth (SD6)
    }

    [Fact]
    public void S037_ScopedBeatsApp_RegardlessOfSpecificity()
    {
        using var tree = StyleMatrixFixture.ShowTree(show: false);
        tree.A.Classes.Add("x");
        tree.A.Classes.Add("y");
        tree.A.Classes.Add("z");
        tree.App.Styles.Add(StyleMatrixFixture.R("Widget.x.y.z", (Widget.P, 1))); // specificity-boosted App rule
        tree.PaneA.Styles.Add(StyleMatrixFixture.R(".x", (Widget.P, 2)));         // bare Scoped rule
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(2, tree.A.GetValue(Widget.P)); // layer beats specificity (rule 3)
    }

    [Fact]
    public void S038_ExplicitBeatsScoped_AtAnySpecificity()
    {
        using var tree = StyleMatrixFixture.ShowTree(show: false);
        tree.A.Classes.Add("a");
        tree.A.Classes.Add("b");
        tree.A.Classes.Add("c");
        tree.A.Name = "save";
        tree.PaneA.Styles.Add(StyleMatrixFixture.R("Widget.a.b.c#save", (Widget.P, 1)));
        tree.A.Style = StyleMatrixFixture.Explicit((Widget.P, 9));
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(9, tree.A.GetValue(Widget.P)); // Explicit(5) outranks any Scoped specificity
    }
}
