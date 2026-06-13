using Cursorial.UI;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.StyleMatrix;

/// <summary>Style matrix §1 — selector grammar: parse, round-trip, the fence (S1–S23).</summary>
public class Section01_Grammar
{
    [Fact]
    public void S001_BareType_ParsesExact_AndRoundTrips()
    {
        var selector = Selector.Parse("Widget", Resolver);

        var branch = Assert.Single(selector.Branches);
        var compound = Assert.Single(branch.Compounds);
        Assert.Same(typeof(Widget), compound.Type);
        Assert.False(compound.IsAssignableType);
        Assert.False(compound.IsNesting);
        Assert.Empty(compound.Simples);
        Assert.Equal("Widget", selector.ToString());
    }

    [Fact]
    public void S002_IsType_ParsesAssignable_AndRoundTrips()
    {
        var selector = Selector.Parse(":is(Widget)", Resolver);

        var compound = Assert.Single(Assert.Single(selector.Branches).Compounds);
        Assert.Same(typeof(Widget), compound.Type);
        Assert.True(compound.IsAssignableType);
        Assert.Equal(":is(Widget)", selector.ToString());
    }

    [Fact]
    public void S003_ClassOnlyCompound_RoundTrips()
    {
        var selector = Selector.Parse(".primary", Resolver);

        var compound = Assert.Single(Assert.Single(selector.Branches).Compounds);
        Assert.Null(compound.Type);
        Assert.Equal([new SimpleSelector(SimpleSelectorKind.Class, "primary")], compound.Simples);
        Assert.Equal(".primary", selector.ToString());
    }

    [Fact]
    public void S004_NameOnlyCompound_RoundTrips()
    {
        var selector = Selector.Parse("#save", Resolver);

        var compound = Assert.Single(Assert.Single(selector.Branches).Compounds);
        Assert.Equal([new SimpleSelector(SimpleSelectorKind.Name, "save")], compound.Simples);
        Assert.Equal("#save", selector.ToString());
    }

    [Fact]
    public void S005_PseudoOnlyCompound_UniversalSubject_RoundTrips()
    {
        var selector = Selector.Parse(":focus", Resolver);

        var compound = Assert.Single(Assert.Single(selector.Branches).Compounds);
        Assert.Null(compound.Type);
        Assert.Equal([new SimpleSelector(SimpleSelectorKind.PseudoClass, "focus")], compound.Simples);
        Assert.Equal(":focus", selector.ToString());
    }

    [Fact]
    public void S006_CompoundSimples_PreserveDeclarationOrder()
    {
        const string text = "Widget.a.b#save:focus:pointerover";
        var selector = Selector.Parse(text, Resolver);

        var compound = Assert.Single(Assert.Single(selector.Branches).Compounds);
        Assert.Same(typeof(Widget), compound.Type);
        Assert.Equal(
        [
            new SimpleSelector(SimpleSelectorKind.Class, "a"),
            new SimpleSelector(SimpleSelectorKind.Class, "b"),
            new SimpleSelector(SimpleSelectorKind.Name, "save"),
            new SimpleSelector(SimpleSelectorKind.PseudoClass, "focus"),
            new SimpleSelector(SimpleSelectorKind.PseudoClass, "pointerover")
        ], compound.Simples);
        Assert.Equal(text, selector.ToString());
    }

    [Fact]
    public void S007_ChildCombinator_WhitespaceInsignificant_CanonicalForm()
    {
        var tight = Selector.Parse("Pane>Widget", Resolver);
        var loose = Selector.Parse("Pane  >  Widget", Resolver);
        var canonical = Selector.Parse("Pane > Widget", Resolver);

        Assert.Equal(canonical, tight);
        Assert.Equal(canonical, loose);
        Assert.Equal([SelectorCombinator.Child], Assert.Single(canonical.Branches).Combinators);
        Assert.Equal("Pane > Widget", tight.ToString());
        Assert.Equal("Pane > Widget", loose.ToString());
        Assert.Equal("Pane > Widget", canonical.ToString());
    }

    [Fact]
    public void S008_DescendantCombinator_WhitespaceRunCollapses()
    {
        var selector = Selector.Parse("Pane \t  Widget", Resolver);

        Assert.Equal([SelectorCombinator.Descendant], Assert.Single(selector.Branches).Combinators);
        Assert.Equal("Pane Widget", selector.ToString());
    }

    [Fact]
    public void S009_TemplateCombinator_SurroundingWhitespaceOptional()
    {
        var spaced = Selector.Parse("Widget /template/ #chrome", Resolver);
        var tight = Selector.Parse("Widget/template/#chrome", Resolver);

        Assert.Equal(spaced, tight);
        Assert.Equal([SelectorCombinator.Template], Assert.Single(spaced.Branches).Combinators);
        Assert.Equal("Widget /template/ #chrome", spaced.ToString());
        Assert.Equal("Widget /template/ #chrome", tight.ToString());
    }

    [Fact]
    public void S010_SelectorList_TwoMembers_EachItsOwnSelector()
    {
        var selector = Selector.Parse("Widget, Card", Resolver);

        Assert.Equal(2, selector.Branches.Length);
        Assert.Same(typeof(Widget), selector.Branches[0].Subject.Type);
        Assert.Same(typeof(Card), selector.Branches[1].Subject.Type);
        Assert.Equal("Widget, Card", selector.ToString());
    }

    [Fact]
    public void S011_ListMembersWithCombinators_ParseIndependently_AndRoundTrip()
    {
        const string text = "Pane > .a, #b:focus";
        var selector = Selector.Parse(text, Resolver);

        Assert.Equal(2, selector.Branches.Length);
        Assert.Equal([SelectorCombinator.Child], selector.Branches[0].Combinators);
        Assert.Empty(selector.Branches[1].Combinators);
        Assert.Equal(text, selector.ToString());
    }

    [Fact]
    public void S012_NestingCompound_Leftmost_ParsesAndRoundTrips()
    {
        var selector = Selector.Parse("^:pointerover", Resolver);

        var compound = Assert.Single(Assert.Single(selector.Branches).Compounds);
        Assert.True(compound.IsNesting);
        Assert.Equal([new SimpleSelector(SimpleSelectorKind.PseudoClass, "pointerover")], compound.Simples);
        Assert.Equal("^:pointerover", selector.ToString());
        // Placement legality (Children / explicit style only) is seal/attach-time (SD17), not parse-time.
    }

    [Fact]
    public void S013_NestingAnchor_NonLeftmost_IsParseError_AtTheCaret()
    {
        var ex = Assert.Throws<SelectorParseException>(() => Selector.Parse("Widget ^", Resolver));
        Assert.Equal(7, ex.Position);
    }

    [Fact]
    public void S014_UnknownPseudoClass_ParsesAsCustomName()
    {
        var selector = Selector.Parse(":frobnicate", Resolver);

        var compound = Assert.Single(Assert.Single(selector.Branches).Compounds);
        Assert.Equal([new SimpleSelector(SimpleSelectorKind.PseudoClass, "frobnicate")], compound.Simples);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 3)]
    [InlineData("Widget >", 8)]
    [InlineData("> Widget", 0)]
    [InlineData("A > > B", 4)]
    [InlineData("Widget.", 7)]
    [InlineData("#", 1)]
    [InlineData(".2col", 1)]
    [InlineData(":is(Widget", 10)]
    [InlineData("Widget,", 7)]
    [InlineData(null, -1)]
    public void S015_MalformedSelectors_ThrowWithPinnedPositions(string? text, int position)
    {
        if (text is null)
        {
            Assert.Throws<ArgumentNullException>(() => Selector.Parse(null!, Resolver));
            return;
        }

        var ex = Assert.Throws<SelectorParseException>(() => Selector.Parse(text, Resolver));
        Assert.Equal(position, ex.Position);
    }

    [Theory]
    [InlineData("A + B", 2, "+")]
    [InlineData("A ~ B", 2, "~")]
    [InlineData(":not(.a)", 0, ":not()")]
    [InlineData(":nth-child(2)", 0, ":nth-child")]
    [InlineData(":first-child", 0, ":first-child")]
    [InlineData("Widget[IsDefault=true]", 6, "[")]
    public void S016_TheNoFence_UnsupportedByDesign_NamedAndPositioned(string text, int position, string construct)
    {
        var ex = Assert.Throws<SelectorParseException>(() => Selector.Parse(text, Resolver));

        Assert.Equal(position, ex.Position);
        Assert.Contains("unsupported by design", ex.Message, StringComparison.Ordinal);
        if (construct is not "[")
            Assert.Contains(construct.TrimEnd('(', ')'), ex.Message, StringComparison.Ordinal);
        else
            Assert.Contains("Attribute", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void S017_ResolverReturnsNull_ParseError_NamesTheToken()
    {
        var ex = Assert.Throws<SelectorParseException>(() => Selector.Parse("Bogus", NullResolver));

        Assert.Equal(0, ex.Position);
        Assert.Contains("Bogus", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void S018_DefaultResolver_AmbiguousSimpleName_ListsCandidateFullNames()
    {
        EnsureRegistered();

        var ex = Assert.Throws<SelectorParseException>(() => Selector.Parse("Chip"));

        Assert.Equal(0, ex.Position);
        Assert.Contains(typeof(ChipVendorAlpha.Chip).FullName!, ex.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(ChipVendorBeta.Chip).FullName!, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void S019_DefaultResolver_ResolvesRegistryKnownSimpleNames()
    {
        EnsureRegistered();

        var selector = Selector.Parse("Widget");

        Assert.Same(typeof(Widget), Assert.Single(Assert.Single(selector.Branches).Compounds).Type);
    }

    [Fact]
    public void S020_OrdinalCaseSensitivity_ClassesAndPseudoClasses()
    {
        Assert.NotEqual(Selector.Parse(".primary", Resolver), Selector.Parse(".Primary", Resolver));

        // ":FOCUS" is a *custom* pseudo-class, never an alias of ":focus" (DEV from CSS — SD2).
        var upper = Selector.Parse(":FOCUS", Resolver);
        Assert.Equal(
            [new SimpleSelector(SimpleSelectorKind.PseudoClass, "FOCUS")],
            Assert.Single(Assert.Single(upper.Branches).Compounds).Simples);
        Assert.NotEqual(Selector.Parse(":focus", Resolver), upper);
    }

    [Theory]
    [InlineData(".col-2", true, 0)]
    [InlineData(".a_b", true, 0)]
    [InlineData("#x9", true, 0)]
    [InlineData(".-x", false, 1)]
    public void S021_IdentifierCharset_PerSD2(string text, bool parses, int position)
    {
        if (parses)
        {
            Assert.Equal(text, Selector.Parse(text, Resolver).ToString());
            return;
        }

        var ex = Assert.Throws<SelectorParseException>(() => Selector.Parse(text, Resolver));
        Assert.Equal(position, ex.Position);
    }

    /// <summary>The §3.9-③ round-trip corpus (input, canonical) — shared with Fork C's loader at P6.</summary>
    public static TheoryData<string, string> RoundTripCorpus => new()
    {
        { "Widget", "Widget" },
        { ":is(Widget)", ":is(Widget)" },
        { ".primary", ".primary" },
        { "#save", "#save" },
        { ":focus", ":focus" },
        { "Widget.a.b#save:focus:pointerover", "Widget.a.b#save:focus:pointerover" },
        { "Pane>Widget", "Pane > Widget" },
        { "Pane \t Widget", "Pane Widget" },
        { "Widget/template/#chrome", "Widget /template/ #chrome" },
        { "Widget, Card", "Widget, Card" },
        { "Widget ,Card ,  Pane", "Widget, Card, Pane" },
        { "Pane > .a, #b:focus", "Pane > .a, #b:focus" },
        { "^:pointerover", "^:pointerover" },
        { "^.selected#x", "^.selected#x" },
        { "^ /template/ #chrome", "^ /template/ #chrome" },
        { ":is(Pane) > Widget", ":is(Pane) > Widget" },
        { "Pane :is(Widget).a", "Pane :is(Widget).a" },
        { "#root Pane > Widget#leaf", "#root Pane > Widget#leaf" },
        { "Card /template/ Pane > Widget", "Card /template/ Pane > Widget" },
        { "Widget:focus:pointerover", "Widget:focus:pointerover" },
        { ".a.b.c", ".a.b.c" },
        { "Widget#x.y:focus", "Widget#x.y:focus" },
        { ":frobnicate.custom", ":frobnicate.custom" },
        { "Pane.toolbar > Widget.primary", "Pane.toolbar > Widget.primary" },
        { "Pane Widget Card", "Pane Widget Card" },
        { "Widget.a > #b .c:focus, :is(Card)", "Widget.a > #b .c:focus, :is(Card)" }
    };

    [Theory]
    [MemberData(nameof(RoundTripCorpus))]
    public void S022_RoundTripLaws_CanonicalToString_AndStructuralReparse(string input, string canonical)
    {
        var parsed = Selector.Parse(input, Resolver);

        Assert.Equal(canonical, parsed.ToString());
        var reparsed = Selector.Parse(parsed.ToString(), Resolver);
        Assert.Equal(parsed, reparsed);
    }

    private static readonly Dictionary<string, Func<Selector>> BuilderPairs = new(StringComparer.Ordinal)
    {
        ["Widget"] = static () => Selectors.OfType<Widget>(),
        [":is(Card)"] = static () => Selectors.Is<Card>(),
        [".primary"] = static () => ((Selector?)null).Class("primary"),
        ["#save"] = static () => ((Selector?)null).Name("save"),
        [":focus"] = static () => ((Selector?)null).PseudoClass("focus"),
        ["Widget.a.b#save:focus:pointerover"] = static () => Selectors.OfType<Widget>()
            .Class("a").Class("b").Name("save").PseudoClass("focus").PseudoClass("pointerover"),
        ["Card > Widget"] = static () => Selectors.OfType<Card>().Child().OfType<Widget>(),
        ["Card Widget"] = static () => Selectors.OfType<Card>().Descendant().OfType<Widget>(),
        ["Card /template/ #chrome"] = static () => Selectors.OfType<Card>().Template().Name("chrome"),
        ["^:pointerover"] = static () => Selectors.Nesting().PseudoClass(":pointerover"),
        ["Widget, Card"] = static () => Selectors.OfType<Widget>().Or(Selectors.OfType<Card>()),
        ["Card > :is(Widget)"] = static () => Selectors.OfType<Card>().Child().Is<Widget>(),
        ["Card .a:focus"] = static () => Selectors.OfType<Card>().Descendant().Class("a").PseudoClass("focus"),
        ["#root Card > Widget#leaf"] = static () => ((Selector?)null).Name("root")
            .Descendant().OfType<Card>().Child().OfType<Widget>().Name("leaf")
    };

    public static TheoryData<string> BuilderPairKeys
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var key in BuilderPairs.Keys)
                data.Add(key);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(BuilderPairKeys))]
    public void S023_FluentBuilders_StructurallyEqualParsedEquivalents_IdenticalToString(string canonical)
    {
        var built = BuilderPairs[canonical]();
        var parsed = Selector.Parse(canonical, Resolver);

        Assert.Equal(parsed, built);
        Assert.Equal(canonical, built.ToString());
    }
}
