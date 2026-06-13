using System.Runtime.CompilerServices;

using Cursorial.UI;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.StyleMatrix;

/// <summary>
/// Style matrix §3 — the style object model and seal (S39–S58). Rows whose Expected cell reaches
/// the value store (activation / <c>eff</c> / notifications) assert their seal-time half here and
/// gain the store half at the matcher stage (Y3) — each is marked inline.
/// </summary>
public class Section03_StyleObject
{
    [Fact]
    public void S039_ThreeConstructionForms_Equivalent_InitOnlyIdentity()
    {
        var s1 = new Style(Selector.Parse("Widget.a", Resolver));
        var s2 = new Style("Widget.a", Resolver);
        var s3 = new Style { Selector = Selector.Parse("Widget.a", Resolver) };

        Assert.Equal(s1.Selector, s2.Selector);
        Assert.Equal(s2.Selector, s3.Selector);

        // Selector / BasedOn / Key are init-only (the C# init accessor carries the IsExternalInit modreq).
        foreach (var name in (string[])["Selector", "BasedOn", "Key"])
        {
            var setter = typeof(Style).GetProperty(name)!.SetMethod!;
            Assert.Contains(typeof(IsExternalInit), setter.ReturnParameter.GetRequiredCustomModifiers());
        }
    }

    [Fact]
    public void S040_Seal_Idempotent()
    {
        var style = R("Widget", (Widget.P, 1));

        Assert.False(style.IsSealed);
        style.Seal();
        Assert.True(style.IsSealed);
        style.Seal(); // idempotent — no throw, still sealed
        Assert.True(style.IsSealed);
    }

    [Fact]
    public void S041_AddToAttachedStylesCollection_AutoSeals()
    {
        var host = new Widget();
        var style = R("Widget", (Widget.P, 1));
        Assert.False(style.IsSealed);

        host.Styles.Add(style); // host.Styles is attached (owner-bound) on first touch

        Assert.True(style.IsSealed);

        // The store half: on a SHOWN tree the add seals AND arms in the same operation —
        // matching elements are styled immediately (seal-on-attach, doc §3.3).
        using var tree = ShowTree();
        var live = R("Widget", (Widget.P, 4));
        Assert.False(live.IsSealed);
        tree.PaneA.Styles.Add(live);
        Assert.True(live.IsSealed);
        Assert.Equal(4, tree.A.GetValue(Widget.P));
    }

    [Theory]
    [InlineData("Setters")]
    [InlineData("Children")]
    [InlineData("Enter")]
    [InlineData("Exit")]
    public void S042_SealedStyle_MutationThrows(string collection)
    {
        var style = R("Widget", (Widget.P, 1));
        style.Children.Add(R("^:focus"));
        style.Enter.Add(new RecordingEdgeAction());
        style.Exit.Add(new RecordingEdgeAction());
        style.Seal();

        Action mutate = collection switch
        {
            "Setters" => () => style.Setters.Add(new Setter(Widget.Q, 2)),
            "Children" => () => style.Children.Add(R("^:pointerover")),
            "Enter" => () => style.Enter.Add(new RecordingEdgeAction()),
            _ => () => style.Exit.Clear()
        };

        Assert.Throws<InvalidOperationException>(mutate);
    }

    [Fact]
    public void S043_BasedOn_FlattensDerivedAfterBase()
    {
        var baseStyle = R("Widget", (Widget.P, 1), (Widget.Q, 2));
        var derived = new Style("Widget", Resolver) { BasedOn = baseStyle };
        derived.Setters.Add(new Setter(Widget.P, 3));

        derived.Seal();

        var setters = Assert.Single(derived.CompiledRules).Setters;
        Assert.Equal(2, setters.Length);
        Assert.Equal(3, GetSetterValue(setters, Widget.P)); // later (derived) wins within the rule
        Assert.Equal(2, GetSetterValue(setters, Widget.Q)); // base survives

        // The store half: activate on a shown tree.
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(derived);
        tree.Host.ShowRoot(tree.Root);
        Assert.Equal(3, tree.A.GetValue(Widget.P));
        Assert.Equal(2, tree.A.GetValue(Widget.Q));
    }

    [Fact]
    public void S044_BasedOnChain_TransitiveFlatten_NearestDerivedWins()
    {
        var styleA = R("Widget", (Widget.P, 1), (Widget.Q, 7));
        var styleB = new Style("Widget", Resolver) { BasedOn = styleA };
        styleB.Setters.Add(new Setter(Widget.Q, 8));
        var styleC = new Style("Widget", Resolver) { BasedOn = styleB };
        styleC.Setters.Add(new Setter(Widget.P, 9));

        styleC.Seal();

        var setters = Assert.Single(styleC.CompiledRules).Setters;
        Assert.Equal(2, setters.Length);
        Assert.Equal(9, GetSetterValue(setters, Widget.P)); // C over A
        Assert.Equal(8, GetSetterValue(setters, Widget.Q)); // B over A

        // The store half: nearest-derived wins per property through the activated frame.
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(styleC);
        tree.Host.ShowRoot(tree.Root);
        Assert.Equal(9, tree.A.GetValue(Widget.P));
        Assert.Equal(8, tree.A.GetValue(Widget.Q));
    }

    [Fact]
    public void S045_BasedOnCycle_SealThrows_NamingBothStyles()
    {
        var a = new Style("Widget", Resolver) { Key = "StyleA" };
        var b = new Style("Widget", Resolver) { Key = "StyleB" };

        // Selector/BasedOn/Key are init-only (S39), so a cycle is unconstructible in plain C# —
        // the check is defense-in-depth for reflection/loader object graphs (Fork C at P6).
        typeof(Style).GetProperty("BasedOn")!.SetValue(a, b);
        typeof(Style).GetProperty("BasedOn")!.SetValue(b, a);

        var ex = Assert.Throws<InvalidOperationException>(a.Seal);
        Assert.Contains("StyleA", ex.Message, StringComparison.Ordinal);
        Assert.Contains("StyleB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void S046_BasedOn_ContributesSettersNeverSpecificity()
    {
        var baseStyle = R("Widget", (Widget.P, 1)); // never attached
        var derived = new Style("Widget:focus", Resolver) { BasedOn = baseStyle };

        derived.Seal();

        var rule = Assert.Single(derived.CompiledRules);
        Assert.Equal(1, rule.Types);     // the derived selector's counts only
        Assert.Equal(1, rule.ClassLike);
        Assert.Equal(0, rule.Names);
        Assert.Equal(1, GetSetterValue(rule.Setters, Widget.P)); // the base's setter flowed in
    }

    [Fact]
    public void S047_NestingChild_CompilesToOneRule()
    {
        var style = R("Widget.primary");
        style.Children.Add(R("^:pointerover", (Widget.P, 5)));

        style.Seal();

        Assert.Equal(2, style.CompiledRules.Length);
        var childRule = style.CompiledRules[1];
        Assert.Equal("Widget.primary:pointerover", childRule.SelectorText); // ONE composed rule
        Assert.Equal(5, GetSetterValue(childRule.Setters, Widget.P));

        // The store half: arms on a classed widget; the flip activates the composed rule.
        using var tree = ShowTree(show: false);
        tree.A.Classes.Add("primary");
        tree.App.Styles.Add(style);
        tree.Host.ShowRoot(tree.Root);
        Assert.Equal(0, tree.A.GetValue(Widget.P));
        tree.A.Flip(InteractionState.PointerOver, true);
        Assert.Equal(5, tree.A.GetValue(Widget.P));
    }

    [Fact]
    public void S048_NestingComposition_Transitive()
    {
        var grandchild = R("^:focus");
        var child = R("^.primary");
        child.Children.Add(grandchild);
        var style = R("Widget");
        style.Children.Add(child);

        style.Seal();

        Assert.Equal(3, style.CompiledRules.Length);
        Assert.Equal("Widget.primary:focus", style.CompiledRules[2].SelectorText);
    }

    [Fact]
    public void S049_ChildWithoutNestingRoot_SealError_NamingStyleAndRuleIndex()
    {
        var style = R("Widget");
        style.Children.Add(R("Card.x"));

        var ex = Assert.Throws<InvalidOperationException>(style.Seal);

        Assert.Contains("Widget", ex.Message, StringComparison.Ordinal); // the style identity (Key ?? selector)
        Assert.Contains("rule 1", ex.Message, StringComparison.Ordinal); // the child's DFS rule index
        Assert.False(style.IsSealed);
    }

    [Fact]
    public void S050_NestingTemplateChild_FlattensAcrossTheHop()
    {
        var style = R("Widget");
        style.Children.Add(R("^ /template/ #chrome"));

        style.Seal();

        Assert.Equal("Widget /template/ #chrome", style.CompiledRules[1].SelectorText);
    }

    [Fact]
    public void S051_ConstantConversion_OnceAtSeal_InvariantCulture()
    {
        var style = R("Widget", (Widget.Pd, 5)); // int constant on a double property

        style.Seal();

        var setter = Assert.Single(Assert.Single(style.CompiledRules).Setters);
        var value = Assert.IsType<double>(setter.Value);
        Assert.Equal(5.0, value);

        // The store half: activation performs no conversion — the seal-converted constant applies.
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(style);
        tree.Host.ShowRoot(tree.Root);
        Assert.Equal(5.0, tree.A.GetValue(Widget.Pd));
    }

    [Fact]
    public void S052_UnconvertibleConstant_SealError_NamesTheTriple()
    {
        var style = R("Widget", (Widget.P, "nope"));

        var ex = Assert.Throws<InvalidOperationException>(style.Seal);

        Assert.Contains("Widget", ex.Message, StringComparison.Ordinal); // style identity (Key ?? selector text)
        Assert.Contains("rule 0", ex.Message, StringComparison.Ordinal); // rule index
        Assert.Contains("'P'", ex.Message, StringComparison.Ordinal);    // property name
    }

    [Fact]
    public void S053_NullOnNonNullableValueType_SealError_NamesTheTriple()
    {
        var style = R("Widget", (Widget.P, null));

        var ex = Assert.Throws<InvalidOperationException>(style.Seal);

        Assert.Contains("Widget", ex.Message, StringComparison.Ordinal);
        Assert.Contains("rule 0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'P'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void S054_ReadOnlyPropertyTarget_SealError_NamesTheTriple()
    {
        var style = R("Widget", (Widget.Pro, 5));

        var ex = Assert.Throws<InvalidOperationException>(style.Seal);

        Assert.Contains("Widget", ex.Message, StringComparison.Ordinal);
        Assert.Contains("rule 0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'Pro'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void S055_UnsetValueSetter_CompilesToValuelessEntry()
    {
        var style = R("Widget", (Widget.P, UIProperty.UnsetValue));

        style.Seal();

        var setter = Assert.Single(Assert.Single(style.CompiledRules).Setters);
        Assert.True(setter.IsUnset);
        Assert.Null(setter.Value);

        // The store half: while ACTIVE the valueless entry contributes nothing (A8).
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(style);
        tree.Host.ShowRoot(tree.Root);
        Assert.True(Assert.Single(StyleDiagnostics.MatchedRules(tree.A)).IsActive);
        Assert.False(tree.A.IsSet(Widget.P));
        Assert.Equal(new ValueSource(BindingPriority.Default, false), tree.A.GetValueSource(Widget.P));
    }

    [Fact]
    public void S056_SelectorLessKeyLessStyle_InStylesCollection_AttachTimeError()
    {
        var host = new Widget();
        var style = new Style();
        style.Setters.Add(new Setter(Widget.P, 1));

        var ex = Assert.Throws<InvalidOperationException>(() => host.Styles.Add(style));
        Assert.Contains("selector-less", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void S057_SelectorLessExplicitStyle_LegalOnStyleProperty()
    {
        var a = new Widget();
        var style = new Style();
        style.Setters.Add(new Setter(Widget.P, 9));

        a.Style = style; // legal: arms always-active at Explicit(5), zero specificity

        Assert.Same(style, a.Style);
        Assert.True(style.IsSealed);
        var rule = Assert.Single(style.CompiledRules);
        Assert.Null(rule.Branch);
        Assert.Equal((0, 0, 0), (rule.Names, rule.ClassLike, rule.Types));

        // The store half: arms always-active at Explicit(5) once attached.
        using var tree = ShowTree();
        var attached = new Style();
        attached.Setters.Add(new Setter(Widget.P, 9));
        tree.A.Style = attached;
        Assert.Equal(9, tree.A.GetValue(Widget.P));
        Assert.Equal(StyleLayer.Explicit, Assert.Single(StyleDiagnostics.MatchedRules(tree.A)).Layer);
    }

    [Fact]
    public void S058_SelectorBearingStyle_OnStyleProperty_Throws()
    {
        var a = new Widget();
        var style = R("Widget:focus", (Widget.P, 1));

        Assert.Throws<InvalidOperationException>(() => a.Style = style);
        Assert.Null(a.Style);
    }

    /// <summary>
    /// A <see cref="Style.Seal"/> that throws (here a non-<c>^</c>-rooted child) leaves the style
    /// unsealed and mutable. Fixing the error, adding a new setter, and re-sealing must recompile
    /// from current state — the failed-seal compile caches are dropped at re-seal entry, so the new
    /// setter is not silently lost (correctness review finding 5).
    /// </summary>
    [Fact]
    public void S182_FailedSeal_ThenFixAndAddSetter_Reseal_RecompilesFromCurrentState()
    {
        var style = R("Widget", (Widget.P, 5));
        style.Children.Add(R("Widget")); // not '^'-rooted ⇒ seal error after the parent's setters cached

        Assert.Throws<InvalidOperationException>(style.Seal);
        Assert.False(style.IsSealed);

        style.Children.Clear();
        style.Setters.Add(new Setter(Widget.Q, 9));
        style.Seal();

        var rule = Assert.Single(style.CompiledRules);
        Assert.Equal(2, rule.Setters.Length);
        Assert.Equal(5, GetSetterValue(rule.Setters, Widget.P));
        Assert.Equal(9, GetSetterValue(rule.Setters, Widget.Q));
    }

    /// <summary>
    /// A fluent selector with an open combinator (<c>Selectors.OfType&lt;Widget&gt;().Child()</c> —
    /// no following compound) is rejected at the <see cref="Style"/> boundary rather than compiled
    /// as its completed-so-far form (correctness review finding 6a): the ctor throws
    /// <see cref="ArgumentException"/>; the <c>init</c>-set path throws at <see cref="Style.Seal"/>.
    /// </summary>
    [Fact]
    public void S183_DanglingCombinatorSelector_RejectedAtTheStyleBoundary()
    {
        var dangling = Selectors.OfType<Widget>().Child();

        Assert.Throws<ArgumentException>(() => new Style(dangling));

        var viaInit = new Style { Selector = dangling };
        Assert.Throws<InvalidOperationException>(viaInit.Seal);
    }

    /// <summary>
    /// The fluent <see cref="Style.Set{T}"/> mutator is type-checked at compile time, chains,
    /// returns the same style, and feeds the same seal-time pipeline as adding to
    /// <see cref="Style.Setters"/> directly (API review findings 3/4 — code-first ergonomics without
    /// the untyped boxing footgun); it throws once the style is sealed (S42).
    /// </summary>
    [Fact]
    public void S184_FluentSet_TypeChecked_Chains_FeedsTheSamePipeline()
    {
        var style = new Style("Widget", Resolver)
            .Set(Widget.P, 5)
            .Set(Widget.Pd, 1.5);

        style.Seal();

        var rule = Assert.Single(style.CompiledRules);
        Assert.Equal(2, rule.Setters.Length);
        Assert.Equal(5, GetSetterValue(rule.Setters, Widget.P));
        Assert.Equal(1.5, GetSetterValue(rule.Setters, Widget.Pd));

        Assert.Throws<InvalidOperationException>(() => style.Set(Widget.Q, 1)); // sealed
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static object? GetSetterValue(CompiledSetter[] setters, UIProperty property)
    {
        foreach (var setter in setters)
        {
            if (ReferenceEquals(setter.Property, property))
                return setter.Value;
        }

        Assert.Fail($"No compiled setter for '{property.Name}'.");
        return null;
    }

    private sealed class RecordingEdgeAction : IStyleEdgeAction
    {
        public void OnActivated(UIElement scope)
        {
        }

        public void OnRetracted(UIElement scope)
        {
        }
    }
}
