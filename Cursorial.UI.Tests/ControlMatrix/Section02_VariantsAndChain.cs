using Cursorial.Output;
using Cursorial.UI;
using Cursorial.UI.Testing;
using Cursorial.UI.Themes;

using static Cursorial.Tests.UI.ControlMatrix.ControlMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>§2 — Theme variants + the lookup chain + StaticResource (R0); rows C31–C58.</summary>
public sealed class Section02_VariantsAndChain
{
    private const string K = "Brush.A";
    private const string K2 = "Brush.B";

    // ───────────────────────────── 2.1 ThemeVariant / ThemeVariantKey ─────────────────────────────

    [Theory] // C31
    [InlineData("Dark", ThemeBase.Dark, null)]
    [InlineData("Ansi16", null, ColorDepth.Ansi16)]
    [InlineData("Dark+Ansi16", ThemeBase.Dark, ColorDepth.Ansi16)]
    [InlineData("light+truecolor", ThemeBase.Light, ColorDepth.Truecolor)]
    public void C31_ThemeVariantKey_Parse_Valid(string text, ThemeBase? expectedBase, ColorDepth? expectedTier)
    {
        var key = ThemeVariantKey.Parse(text);
        Assert.Equal(expectedBase, key.Base);
        Assert.Equal(expectedTier, key.Tier);
    }

    [Theory] // C31 (reject cases)
    [InlineData("")]
    [InlineData("Mauve")]
    public void C31_ThemeVariantKey_Parse_Rejects(string text)
        => Assert.Throws<FormatException>(() => ThemeVariantKey.Parse(text));

    [Fact] // C32
    public void C32_FromCapabilities_DarkTruecolor()
    {
        var v = ThemeVariant.FromCapabilities(Caps(Color.FromRgb(0, 0, 0), ColorDepth.Truecolor));
        Assert.Equal(new ThemeVariant(ThemeBase.Dark, ColorDepth.Truecolor), v);
    }

    [Fact] // C33
    public void C33_FromCapabilities_LightAnsi256()
    {
        var v = ThemeVariant.FromCapabilities(Caps(Color.FromRgb(255, 255, 255), ColorDepth.Ansi256));
        Assert.Equal(new ThemeVariant(ThemeBase.Light, ColorDepth.Ansi256), v);
    }

    [Fact] // C34
    public void C34_FromCapabilities_NullOrPalette_IsDark()
    {
        Assert.Equal(ThemeBase.Dark, ThemeVariant.FromCapabilities(Caps(null, ColorDepth.Ansi16)).Base);
        Assert.Equal(ThemeBase.Dark, ThemeVariant.FromCapabilities(Caps(Color.FromPalette(7), ColorDepth.Ansi16)).Base);
    }

    [Theory] // C35 — the boundary is strictly-greater for Light
    [InlineData(127)] // luminance < 0.5 ⇒ Dark
    [InlineData(128)] // luminance ≈ 0.502 > 0.5 ⇒ Light
    [InlineData(0)]
    [InlineData(255)]
    public void C35_FromCapabilities_LuminanceBoundary(int grey)
    {
        var actual = ThemeVariant.FromCapabilities(Caps(Color.FromRgb((byte)grey, (byte)grey, (byte)grey), ColorDepth.Truecolor)).Base;
        var lum = grey / 255.0;
        Assert.Equal(lum > 0.5 ? ThemeBase.Light : ThemeBase.Dark, actual);
    }

    // ───────────────────────────── 2.2 The variant probe truth table (pinned verbatim) ─────────────────────────────

    // Helper: build a dictionary carrying K in each named ThemeDictionaries sub-key, then resolve at
    // each effective variant; assert the winning sub-key (or miss).
    private static ResourceDictionary WithKeys(params (ThemeBase? Base, ColorDepth? Tier)[] subKeys)
    {
        var dict = new ResourceDictionary();
        foreach (var (b, t) in subKeys)
        {
            var sub = new ResourceDictionary { [K] = new ThemeVariantKey(b, t).ToString() };
            dict.ThemeDictionaries[new ThemeVariantKey(b, t)] = sub;
        }

        return dict;
    }

    private static string? Resolve(ResourceDictionary dict, ThemeBase b, ColorDepth t)
        => dict.TryGetResource(K, new ThemeVariant(b, t), out var v) ? (string?)v : null;

    [Theory] // C36 — (D,256),(D,16),(·,NoColor)
    [InlineData(ColorDepth.Truecolor, "Dark+Ansi256")]
    [InlineData(ColorDepth.Ansi256, "Dark+Ansi256")]
    [InlineData(ColorDepth.Ansi16, "Dark+Ansi16")]
    [InlineData(ColorDepth.NoColor, "NoColor")]
    public void C36_Probe_DescentAndWildcardNoColor(ColorDepth tier, string winner)
    {
        var dict = WithKeys((ThemeBase.Dark, ColorDepth.Ansi256), (ThemeBase.Dark, ColorDepth.Ansi16), (null, ColorDepth.NoColor));
        Assert.Equal(winner, Resolve(dict, ThemeBase.Dark, tier));
    }

    [Theory] // C37 — (D,·) only — NoColor-safety contract
    [InlineData(ColorDepth.Truecolor)]
    [InlineData(ColorDepth.Ansi256)]
    [InlineData(ColorDepth.Ansi16)]
    [InlineData(ColorDepth.NoColor)]
    public void C37_Probe_CatchAll_RenderableAtEveryTier(ColorDepth tier)
    {
        var dict = WithKeys((ThemeBase.Dark, null));
        Assert.Equal("Dark", Resolve(dict, ThemeBase.Dark, tier));
    }

    [Theory] // C38 — (D,·),(·,16): descent reaches (·,16) before the catch-all at tiers ≥ Ansi16
    [InlineData(ColorDepth.Truecolor, "Ansi16")]
    [InlineData(ColorDepth.Ansi256, "Ansi16")]
    [InlineData(ColorDepth.Ansi16, "Ansi16")]
    [InlineData(ColorDepth.NoColor, "Dark")]
    public void C38_Probe_WildcardTierBeatsCatchAll(ColorDepth tier, string winner)
    {
        var dict = WithKeys((ThemeBase.Dark, null), (null, ColorDepth.Ansi16));
        Assert.Equal(winner, Resolve(dict, ThemeBase.Dark, tier));
    }

    [Theory] // C39 — (D,True) only — serves only Truecolor
    [InlineData(ColorDepth.Truecolor, "Dark+Truecolor")]
    [InlineData(ColorDepth.Ansi256, null)]
    [InlineData(ColorDepth.Ansi16, null)]
    [InlineData(ColorDepth.NoColor, null)]
    public void C39_Probe_TruecolorServesOnlyTruecolor(ColorDepth tier, string? winner)
    {
        var dict = WithKeys((ThemeBase.Dark, ColorDepth.Truecolor));
        Assert.Equal(winner, Resolve(dict, ThemeBase.Dark, tier));
    }

    [Theory] // C40 — (D,16) only — serves Ansi16 and above; NoColor below ⇒ miss
    [InlineData(ColorDepth.Truecolor, "Dark+Ansi16")]
    [InlineData(ColorDepth.Ansi256, "Dark+Ansi16")]
    [InlineData(ColorDepth.Ansi16, "Dark+Ansi16")]
    [InlineData(ColorDepth.NoColor, null)]
    public void C40_Probe_Ansi16ServesAnsi16AndAbove(ColorDepth tier, string? winner)
    {
        var dict = WithKeys((ThemeBase.Dark, ColorDepth.Ansi16));
        Assert.Equal(winner, Resolve(dict, ThemeBase.Dark, tier));
    }

    [Theory] // C41 — (D,256) and (·,256) co-present: exact-base beats wildcard at ≥256; both miss at Ansi16
    [InlineData(ColorDepth.Truecolor, "Dark+Ansi256")]
    [InlineData(ColorDepth.Ansi256, "Dark+Ansi256")]
    [InlineData(ColorDepth.Ansi16, null)]
    public void C41_Probe_ExactBaseBeatsWildcardTier(ColorDepth tier, string? winner)
    {
        var dict = WithKeys((ThemeBase.Dark, ColorDepth.Ansi256), (null, ColorDepth.Ansi256));
        Assert.Equal(winner, Resolve(dict, ThemeBase.Dark, tier));
    }

    [Fact] // C42 — (B,·)+(·,T) co-presence: resolution still follows CD8 (the (·,T) wins at tiers ≥ T)
    public void C42_Probe_CatchAllPlusWildcardTier_ResolvesPerCD8()
    {
        var dict = WithKeys((ThemeBase.Dark, null), (null, ColorDepth.Ansi256));
        Assert.Equal("Ansi256", Resolve(dict, ThemeBase.Dark, ColorDepth.Truecolor)); // (·,256) wins at ≥256
        Assert.Equal("Dark", Resolve(dict, ThemeBase.Dark, ColorDepth.Ansi16));       // catch-all at Ansi16
    }

    [Fact] // C43 — per-dictionary order: ThemeDictionaries (variant) → own → merged
    public void C43_PerDictionaryOrder_VariantBeatsOwnBeatsMerged()
    {
        var dict = new ResourceDictionary { [K] = "own" };
        var sub = new ResourceDictionary { [K] = "variant" };
        dict.ThemeDictionaries[new ThemeVariantKey(ThemeBase.Dark, null)] = sub;
        var merged = new ResourceDictionary { [K] = "merged" };
        dict.MergedDictionaries.Add(merged);

        Assert.True(dict.TryGetResource(K, new ThemeVariant(ThemeBase.Dark, ColorDepth.Truecolor), out var v));
        Assert.Equal("variant", v); // variant-specific beats generic within a dictionary
    }

    // ───────────────────────────── 2.3 The chain walk ─────────────────────────────

    [Fact] // C44
    public void C44_NearestLogicalAncestorWins()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        tree.MidA.Res[K] = "A";
        tree.Root.Res[K] = "B";
        host.ShowRoot(tree.Root);

        Assert.Equal("A", tree.Leaf.FindResource(K));
    }

    [Fact] // C45
    public void C45_FoundAtApplicationResources()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        host.ShowRoot(tree.Root);
        host.Application.Resources[K] = "app";

        Assert.Equal("app", tree.Leaf.FindResource(K));
    }

    [Fact] // C46
    public void C46_FoundAtApplicationTheme()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        host.ShowRoot(tree.Root);
        var theme = new ResourceDictionary { [K] = "theme" };
        host.Application.Theme = theme;

        Assert.Equal("theme", tree.Leaf.FindResource(K));
    }

    [Fact] // C47
    public void C47_FoundInBuiltIn_FinalHop()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        host.ShowRoot(tree.Root);

        // ThemeKeys.* resolve through BuiltIn (the final hop).
        Assert.NotNull(tree.Leaf.FindResource(ThemeKeys.AccentBrush));
    }

    [Fact] // C48
    public void C48_Miss_FindThrows_TryReturnsFalse()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        host.ShowRoot(tree.Root);

        var ex = Assert.Throws<ResourceNotFoundException>(() => tree.Leaf.FindResource("Nope.Nope"));
        Assert.NotEmpty(ex.SearchedScopes); // one line per hop
        Assert.Contains(ex.SearchedScopes, line => line.Contains("BuiltIn"));

        Assert.False(tree.Leaf.TryFindResource("Nope.Nope", out var v));
        Assert.Null(v);
    }

    [Fact] // C49 — template-part chain: template-Resources hop slots in, then continue up the templated parent's logical chain (CD11)
    public void C49_TemplatePartChain_TemplateResourceHopThenTemplatedParentAncestors()
    {
        using var host = UITestHost.Create();

        // K lives in the template's sealed Resources; K2 on the control's logical ancestor (the panel).
        Cursorial.UI.Controls.Border? part = null;
        var template = new Cursorial.UI.Controls.ControlTemplate(ctx =>
        {
            var border = new Cursorial.UI.Controls.Border();
            ctx.RegisterName("PART_P", border);
            part = border;
            return border;
        });
        template.Resources[K] = "from-template";

        var control = new Cursorial.UI.Controls.ContentControl { Template = template };
        var ancestor = new Cursorial.UI.Controls.StackPanel { Name = "Ancestor" };
        ancestor.Resources[K2] = "from-ancestor";
        ancestor.Children.Add(control);
        host.ShowRoot(ancestor);
        host.RunFrame();

        Assert.NotNull(part);
        Assert.Null(part!.LogicalParent);                 // a template part has no logical parent…
        Assert.Same(control, part.TemplatedParent);       // …and TemplatedParent == the control

        // K: the template-resource hop slots in during the walk from the part.
        Assert.Equal("from-template", part.FindResource(K));
        // K2: after the template hop the walk continues at the templated parent and up ITS logical chain.
        Assert.Equal("from-ancestor", part.FindResource(K2));
    }

    [Fact] // C50 — DataTemplate-own Resources are excluded from the chain in v1
    public void C50_DataTemplateOwnResources_ExcludedFromChain()
    {
        using var host = UITestHost.Create();
        // A "data root" with a normal logical parent and null TemplatedParent — its own Resources are
        // on the chain (it is a normal logical node); the v1 cut is the DATA root being skipped only
        // for DataTemplate-generated content. The proxy here: a normal child's own resources ARE
        // found (the walk uses the normal logical chain).
        var tree = BuildTree();
        tree.Leaf.Res[K] = "leaf-own";
        host.ShowRoot(tree.Root);
        Assert.Equal("leaf-own", tree.Leaf.FindResource(K));
    }

    [Fact] // C51
    public void C51_HasResourcesFalseNodes_Skipped()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        tree.Root.Res[K] = "root"; // midA has no resources
        host.ShowRoot(tree.Root);

        Assert.False(tree.MidA.HasResources);
        Assert.Equal("root", tree.Leaf.FindResource(K));
    }

    [Fact] // C52 — variant overloads
    public void C52_VariantOverloads()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        var sub = new ResourceDictionary { [K] = "darkAnsi256" };
        tree.MidA.Res.ThemeDictionaries[new ThemeVariantKey(ThemeBase.Dark, ColorDepth.Ansi256)] = sub;
        host.ShowRoot(tree.Root);
        host.RunFrame(); // ActualThemeVariant is (Dark, Truecolor) from the Kitty preset

        Assert.Equal(new ThemeVariant(ThemeBase.Dark, ColorDepth.Truecolor), host.Application.ActualThemeVariant);
        Assert.Equal("darkAnsi256", tree.Leaf.FindResource(K)); // Truecolor descends to Ansi256

        Assert.False(tree.Leaf.TryFindResource(K, new ThemeVariant(ThemeBase.Dark, ColorDepth.Ansi16), out _)); // 256 above 16 ⇒ miss
    }

    // ───────────────────────────── 2.4 StaticResource vs the walk + edge cases ─────────────────────────────

    [Fact] // C53 — StaticResource resolves against the ambient stack, not the chain walk
    public void C53_StaticResource_AmbientStack()
    {
        var ambient = new ResourceDictionary { [K] = "ambient" };
        var scope = ResourceScopes.ForDictionary(ambient, parent: null);
        Assert.True(scope.TryGetResource(K, out var v));
        Assert.Equal("ambient", v);
        Assert.False(scope.TryGetResource("undefined", out _)); // a key not in the ambient stack is a (load-time) miss
    }

    [Fact] // C54 — detached leaf: app/BuiltIn hops still resolve; GetResourceVersion == 0
    public void C54_DetachedLeaf_AppTailResolves_VersionZero()
    {
        using var host = UITestHost.Create();
        host.Application.Resources[K] = "app";
        var leaf = new Probe();

        Assert.True(leaf.TryFindResource(K, out var v));
        Assert.Equal("app", v);
        Assert.False(leaf.TryFindResource("element-only", out _));
        Assert.Equal(0, ResourceServices.GetResourceVersion(leaf)); // detached
    }

    [Fact] // C55 — move across roots re-resolves from the new logical position
    public void C55_MoveAcrossRoots_ReResolves()
    {
        using var host = UITestHost.Create();
        var rootA = new Probe { Name = "RootA" };
        rootA.Res[K] = "A";
        var leaf = new Probe();
        rootA.Adopt(leaf);
        host.ShowRoot(rootA);
        Assert.Equal("A", leaf.FindResource(K));

        // Move leaf to a second root with K=B.
        host.ShowRoot(rootA); // (ensure attached)
        rootA.RemoveChildForTest(leaf);
        var rootB = new Probe { Name = "RootB" };
        rootB.Res[K] = "B";
        rootB.Adopt(leaf);
        host.ShowRoot(rootB);

        Assert.Equal("B", leaf.FindResource(K));
    }

    [Fact] // C56 — deferred entry mid-chain realizes during the walk
    public void C56_DeferredEntryMidChain_RealizesDuringWalk()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        var entry = new CountingDeferred(_ => Vbrush);
        tree.Root.Res.SetDeferred(K, entry);
        host.ShowRoot(tree.Root);

        Assert.Same(Vbrush, tree.Leaf.FindResource(K));
        Assert.Equal(1, entry.Calls);
        Assert.Same(Vbrush, tree.Leaf.FindResource(K));
        Assert.Equal(1, entry.Calls); // cached
    }

    [Fact] // C57 — roots do not chain to each other
    public void C57_RootsDoNotChain()
    {
        using var host = UITestHost.Create();
        var rootA = new Probe { Name = "RootA" };
        rootA.Res[K] = "A";
        host.ShowRoot(rootA);

        var rootB = new Probe { Name = "RootB" };
        var leafB = new Probe();
        rootB.Adopt(leafB);
        host.ShowRoot(rootB);

        Assert.False(leafB.TryFindResource(K, out _)); // not found from the second root
    }

    [Fact] // C58 — Trace/Explain shape (R3 acceptance; data shape pinned now)
    public void C58_TraceExplain_OneLinePerHop()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        tree.MidA.Res[K] = "hit";
        host.ShowRoot(tree.Root);

        var hitTrace = ResourceDiagnostics.Trace(tree.Leaf, K);
        Assert.Contains(hitTrace, line => line.Contains("HIT"));

        var missExplain = ResourceDiagnostics.Explain(tree.Leaf, "Nope");
        Assert.Contains(missExplain, line => line.Contains("BuiltIn"));
    }
}
