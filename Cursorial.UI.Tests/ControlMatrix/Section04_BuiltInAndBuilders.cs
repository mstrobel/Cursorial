using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;
using Cursorial.UI.Themes;

using DrawingMedia = Cursorial.Drawing.Media;
using MediaBuilders = Cursorial.UI.Media;
using Style = Cursorial.UI.Style;

using static Cursorial.Tests.UI.ControlMatrix.ControlMatrixFixture;

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>§4 — Built-in theme + ThemeKeys + builders + ResourceBrushResolver (R2); rows C93–C112.</summary>
public sealed class Section04_BuiltInAndBuilders
{
    // ───────────────────────────── 4.1 CursorialTheme.BuiltIn structure ─────────────────────────────

    [Fact] // C93
    public void C93_BuiltIn_SealedProcessShared()
    {
        Assert.True(CursorialTheme.BuiltIn.IsSealed);
        Assert.Same(CursorialTheme.BuiltIn, CursorialTheme.BuiltIn); // process-shared (same instance)
    }

    [Fact] // C94
    public void C94_CreateDefault_UnsealedStructuralCopy()
    {
        var copy = CursorialTheme.CreateDefault();
        Assert.False(copy.IsSealed);
        Assert.NotSame(CursorialTheme.BuiltIn, copy);

        // Mutating the copy does not affect BuiltIn.
        copy[ThemeKeys.AccentBrush] = new DrawingMedia.SolidColorBrush(Color.FromRgb(1, 2, 3));
        Assert.True(CursorialTheme.BuiltIn.IsSealed); // BuiltIn untouched
    }

    [Fact] // C95
    public void C95_ThemeKeys_ResolveThroughBuiltIn()
    {
        var variant = new ThemeVariant(ThemeBase.Dark, ColorDepth.Truecolor);
        foreach (var key in new[]
                 {
                     ThemeKeys.SurfaceBrush, ThemeKeys.TextBrush, ThemeKeys.AccentBrush,
                     ThemeKeys.FocusPen, ThemeKeys.BorderPen, ThemeKeys.ObscuredOverlayBrush,
                     ThemeKeys.AccessKeyUnderlineBrush
                 })
        {
            Assert.True(CursorialTheme.BuiltIn.TryGetResource(key, variant, out var v), $"missing {key}");
            Assert.NotNull(v);
        }
    }

    [Fact] // C96
    public void C96_PaletteTierLayout_NoColorBearingValueInCatchAll()
    {
        // No (B,·) catch-all carries a color-bearing value; RGB lives at (B,Ansi256), palette at (B,Ansi16).
        Assert.False(CursorialTheme.BuiltIn.ThemeDictionaries.ContainsKey(new ThemeVariantKey(ThemeBase.Dark, null)));
        Assert.True(CursorialTheme.BuiltIn.ThemeDictionaries.ContainsKey(new ThemeVariantKey(ThemeBase.Dark, ColorDepth.Ansi256)));
        Assert.True(CursorialTheme.BuiltIn.ThemeDictionaries.ContainsKey(new ThemeVariantKey(ThemeBase.Dark, ColorDepth.Ansi16)));
        Assert.True(CursorialTheme.BuiltIn.ThemeDictionaries.ContainsKey(new ThemeVariantKey(null, ColorDepth.NoColor)));
    }

    [Theory] // C97 — tier coverage (the per-tier KIND is the pin: RGB high, Palette at 16, Default at NoColor)
    [InlineData(ColorDepth.Truecolor, ColorKind.Rgb)]
    [InlineData(ColorDepth.Ansi256, ColorKind.Rgb)]   // (B,Ansi256) via descent — the RGB brush
    [InlineData(ColorDepth.Ansi16, ColorKind.Palette)] // the hand-picked palette brush
    [InlineData(ColorDepth.NoColor, ColorKind.Default)] // attribute-only/safe value, never a stranded RGB
    public void C97_AccentBrush_ResolvesAtEveryTier(ColorDepth tier, ColorKind expectedKind)
    {
        var variant = new ThemeVariant(ThemeBase.Dark, tier);
        Assert.True(CursorialTheme.BuiltIn.TryGetResource(ThemeKeys.AccentBrush, variant, out var v));
        var brush = Assert.IsType<DrawingMedia.SolidColorBrush>(v);
        Assert.Equal(expectedKind, brush.Color.Kind); // the specific value pinned per tier
    }

    // ───────────────────────────── 4.3 Cursorial.UI.Media builders + ResourceBrushResolver ─────────────────────────────

    [Fact] // C104
    public void C104_SolidColorBrushBuilder_BuildsImmutableDrawing()
    {
        var builder = new MediaBuilders.SolidColorBrush { Color = Color.FromRgb(0x10, 0x20, 0x30), Opacity = 0.5 };
        var built = builder.Build();
        var brush = Assert.IsType<DrawingMedia.SolidColorBrush>(built);
        Assert.Equal(ColorKind.Rgb, brush.Color.Kind);
    }

    [Fact] // C105
    public void C105_LinearGradientBuilder_BuildsDrawing()
    {
        var builder = new MediaBuilders.LinearGradientBrush
        {
            StartPoint = RelativePoint.TopLeft,
            EndPoint = RelativePoint.BottomRight,
            Spread = GradientSpread.Reflect,
        };
        builder.GradientStops.Add(new MediaBuilders.GradientStop { Offset = 0, Color = Color.FromRgb(0xF9, 0x26, 0x72) });
        builder.GradientStops.Add(new MediaBuilders.GradientStop { Offset = 1, Color = Color.FromRgb(0x66, 0xD9, 0xEF) });

        var built = Assert.IsType<DrawingMedia.LinearGradientBrush>(builder.Build());
        Assert.Equal(RelativePoint.BottomRight, built.EndPoint);
        Assert.Equal(GradientSpread.Reflect, built.Spread);
    }

    [Fact] // C106
    public void C106_PenBuilder_ColorXorBrush()
    {
        var both = new MediaBuilders.Pen { Color = Color.FromRgb(1, 2, 3), Brush = new DrawingMedia.SolidColorBrush(Color.FromRgb(4, 5, 6)) };
        Assert.Throws<InvalidOperationException>(() => both.Build());

        var single = new MediaBuilders.Pen { Color = Color.FromRgb(1, 2, 3), Weight = StrokeWeight.Heavy };
        var built = Assert.IsType<Cursorial.Drawing.Pen>(single.Build());
        Assert.Equal(StrokeWeight.Heavy, built.Weight);
    }

    [Fact] // C107
    public void C107_BuiltValue_ImmutableAndSealedShareable()
    {
        var brush = new MediaBuilders.SolidColorBrush { Color = Color.FromRgb(9, 9, 9) }.Build();
        var d = new ResourceDictionary { ["B"] = brush };
        d.Seal();
        // freely shared post-seal (folded-constant-friendly)
        var d2 = new ResourceDictionary();
        d2.MergedDictionaries.Add(d);
        Assert.True(d2.TryGetResource("B", new ThemeVariant(ThemeBase.Dark, ColorDepth.Truecolor), out var v));
        Assert.Same(brush, v);
    }

    [Fact] // C108
    public void C108_ResourceBrushResolver_ResourceAndInlineShareNamespace()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        tree.Root.Res[ThemeKeys.AccentBrush] = Vbrush;
        host.ShowRoot(tree.Root);

        var resolver = ResourceBrushResolver.Create(tree.Leaf);
        Assert.NotNull(resolver(ThemeKeys.AccentBrush));   // chain resolution to the brush
        Assert.NotNull(resolver("linear:#f92672,#66d9ef")); // inline grammar via BrushMarkup
    }

    [Fact] // C109
    public void C109_ResourceBrushResolver_UnknownReturnsNull()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        host.ShowRoot(tree.Root);
        var resolver = ResourceBrushResolver.Create(tree.Leaf);
        Assert.Null(resolver("Mystery"));
    }

    // ───────────────────────────── 4.3 override paths (Type-key shadowing — no Button needed) ─────────────────────────────

    [Fact] // C110 — per-control override: shadow a Type key at app scope
    public void C110_PerControlOverride_AppScopeTypeKeyWins()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        host.ShowRoot(tree.Root);

        host.Application.Resources[typeof(Probe)] = "app-theme-for-probe";
        // The BuiltIn carries no Probe Type key; the app-scope key wins (the chain).
        Assert.Equal("app-theme-for-probe", tree.Leaf.FindResource(typeof(Probe)));
    }

    [Fact] // C111 — wholesale override: app.Theme participates before BuiltIn; BuiltIn backstops omitted keys
    public void C111_WholesaleOverride_ThemeHopBeforeBuiltIn()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        host.ShowRoot(tree.Root);

        var custom = CursorialTheme.CreateDefault();
        custom["Custom.Key"] = Vbrush; // a key BuiltIn does NOT carry
        host.Application.Theme = custom;

        // The custom theme participates at the Theme hop (before BuiltIn). Its own added key resolves;
        // and BuiltIn still backstops a control whose key the (partial) theme omits — here the theme is
        // a full CreateDefault copy, so every palette key still resolves.
        Assert.Same(Vbrush, tree.Leaf.FindResource("Custom.Key"));
        Assert.NotNull(tree.Leaf.FindResource(ThemeKeys.SurfaceBrush));
    }

    // ───────────────────────────── 4.4 the theme-channel layer ordering (C98–C103) ─────────────────────────────

    [Fact] // C98 — a Type-keyed control theme arms at ControlTheme(0)
    public void C98_ControlTheme_ArmsAtControlThemeLayer()
    {
        using var host = UITestHost.Create();
        var button = new Button { Content = "OK" };
        host.ShowRoot(button);
        host.RunFrame();

        var armed = StyleDiagnostics.MatchedRules(button);
        Assert.NotEmpty(armed);
        Assert.All(armed, r => Assert.Equal(StyleLayer.ControlTheme, r.Layer)); // the BuiltIn Button theme arms here
        // The theme's COLOR setters (Foreground / resting BorderPen) are ResourceReferences into the
        // ThemeKeys palette (the R2 spine — they still arm at ControlTheme). The :focus escalation is now
        // ALSO a ResourceReference (ThemeKeys.FocusPen — accent color + heavy weight, P2-1 fix); :default
        // stays the Pens.Double weight-only constant. C99 covers the live re-skin; C116 covers the variant
        // flip; Section04 C100b asserts the focus pen resolves the accent color, not terminal default.
    }

    [Fact] // C99 — shadowing a palette key re-skins a BuiltIn control with zero template work (R2 landed)
    public void C99_PaletteKeyShadow_ReSkin()
    {
        using var host = UITestHost.Create(new UITestHostOptions { Capabilities = TestCapabilities.KittyTruecolor });
        var button = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        host.ShowRoot(button);
        host.RunFrame();

        // The R2 contract: shadowing ThemeKeys.TextBrush at app scope re-points the control theme's
        // Foreground ResourceReference setter — the control re-inks with zero template work (the theme
        // Style instance is untouched; only the resolved resource changes).
        var beforeTheme = CursorialTheme.BuiltIn[typeof(Button)];
        var skin = Color.FromRgb(0x11, 0xAA, 0x55);
        host.Application.Resources[ThemeKeys.TextBrush] = new DrawingMedia.SolidColorBrush(skin);
        host.RunFrame();

        // The resolved Foreground tracks the override; the theme Style instance is the same (no re-template).
        var brush = Assert.IsType<DrawingMedia.SolidColorBrush>(button.Foreground);
        Assert.Equal(skin, brush.Color);
        Assert.Same(beforeTheme, CursorialTheme.BuiltIn[typeof(Button)]);
    }

    [Fact] // C100 — a populated app.Theme.Styles arms matched selector styles at Theme(2) (R2/B13)
    public void C100_AppThemeStyles_ArmAtThemeLayer()
    {
        using var host = UITestHost.Create();
        var button = new Button { Content = "OK" };
        host.ShowRoot(button);
        host.RunFrame();

        var theme = CursorialTheme.CreateDefault();
        theme.Styles = new Styles { new Style("Button").Set(Control.BackgroundProperty, Vbrush) };
        host.Application.Theme = theme;
        host.RunFrame();

        // The theme rule arms at Theme(2) (the R2/B13 consumption) and wins the Background over the BuiltIn
        // control theme's ControlTheme(0) setter (Theme(2) is the stronger layer).
        Assert.Contains(StyleDiagnostics.MatchedRules(button), r => r.Layer == StyleLayer.Theme && r.IsActive);
        Assert.Same(Vbrush, button.Background);
    }

    [Fact] // C100b — reassigning app.Theme retracts the old theme's rules and arms the new theme's (live re-match)
    public void C100b_ThemeReassignment_RetractsOldArmsNew()
    {
        var brush1 = new DrawingMedia.SolidColorBrush(Color.FromRgb(10, 20, 30));
        var brush2 = new DrawingMedia.SolidColorBrush(Color.FromRgb(40, 50, 60));

        using var host = UITestHost.Create();
        var button = new Button { Content = "OK" };
        host.ShowRoot(button);

        var theme1 = CursorialTheme.CreateDefault();
        theme1.Styles = new Styles { new Style("Button").Set(Control.BackgroundProperty, brush1) };
        host.Application.Theme = theme1;
        host.RunFrame();
        Assert.Same(brush1, button.Background);

        var theme2 = CursorialTheme.CreateDefault();
        theme2.Styles = new Styles { new Style("Button").Set(Control.BackgroundProperty, brush2) };
        host.Application.Theme = theme2;
        host.RunFrame();

        // The old theme's frame retracted (owner = theme1, no longer gathered) and the new one armed: exactly
        // one Theme-layer rule, the value is the new theme's, and NO dormant theme frame survived the swap.
        Assert.Same(brush2, button.Background);
        Assert.Single(StyleDiagnostics.MatchedRules(button), r => r.Layer == StyleLayer.Theme && r.IsActive);
        Assert.DoesNotContain(StyleDiagnostics.MatchedRules(button), r => r.Layer == StyleLayer.Theme && !r.IsActive);
    }

    [Fact] // C100c — mutating the live app.Theme.Styles re-reads on the theme-origin pulse (C100 "re-read on pulse")
    public void C100c_ThemeStylesMutation_ReReadsLive()
    {
        var brush = new DrawingMedia.SolidColorBrush(Color.FromRgb(7, 8, 9));

        using var host = UITestHost.Create();
        var button = new Button { Content = "OK" };
        host.ShowRoot(button);

        var theme = CursorialTheme.CreateDefault();
        theme.Styles = new Styles(); // empty at assignment
        host.Application.Theme = theme;
        host.RunFrame();
        Assert.DoesNotContain(StyleDiagnostics.MatchedRules(button), r => r.Layer == StyleLayer.Theme);

        // Mutate the live theme's Styles — the theme-origin pulse re-matches the Theme(2) leg.
        theme.Styles.Add(new Style("Button").Set(Control.BackgroundProperty, brush));
        host.RunFrame();

        Assert.Contains(StyleDiagnostics.MatchedRules(button), r => r.Layer == StyleLayer.Theme && r.IsActive);
        Assert.Same(brush, button.Background);
    }

    [Fact] // C100e — CD15: a variant flip stays resource-only — the theme-style frame survives (no retract / no double-arm)
    public void C100e_VariantFlip_DoesNotReMatchThemeStyles()
    {
        var brush = new DrawingMedia.SolidColorBrush(Color.FromRgb(1, 2, 3));

        using var host = UITestHost.Create();
        var button = new Button { Content = "OK" };
        host.ShowRoot(button);

        var theme = CursorialTheme.CreateDefault();
        theme.Styles = new Styles { new Style("Button").Set(Control.BackgroundProperty, brush) };
        host.Application.Theme = theme;
        host.RunFrame();
        Assert.Single(StyleDiagnostics.MatchedRules(button), r => r.Layer == StyleLayer.Theme && r.IsActive);

        // Flip the theme base (a genuine variant change — assert it took effect). Per CD15 the flip is
        // resource-only: the theme-style frame is neither retracted nor re-armed — exactly one Theme rule
        // remains, still active, value intact.
        var beforeBase = host.Application.ActualThemeVariant.Base;
        host.Application.RequestedThemeBase = beforeBase == ThemeBase.Dark ? ThemeBase.Light : ThemeBase.Dark;
        host.RunFrame();
        Assert.NotEqual(beforeBase, host.Application.ActualThemeVariant.Base); // the variant actually flipped

        Assert.Single(StyleDiagnostics.MatchedRules(button), r => r.Layer == StyleLayer.Theme && r.IsActive);
        Assert.Same(brush, button.Background);
    }

    [Fact] // C101 — a populated Styles on a non-theme (element) dictionary is ignored in v1 (only app.Theme.Styles is consumed)
    public void C101_ElementResourcesStyles_Ignored()
    {
        using var host = UITestHost.Create();
        var root = new StackPanel { Name = "Root" };
        var button = new Button { Content = "OK" };
        root.Children.Add(button);

        // A Styles on a plain element-scope Resources dictionary (not app.Theme): never consumed.
        root.Resources.Styles = new Styles { new Style("Button").Set(Control.BackgroundProperty, Vbrush) };
        host.ShowRoot(root);
        host.RunFrame();

        Assert.DoesNotContain(StyleDiagnostics.MatchedRules(button), r => r.Layer == StyleLayer.Theme);
        // The element-Styles is ignored. Post cell-faithful, the button (first tab stop in a panel root)
        // auto-focuses and shows the theme's :focus fill — so positively assert that legitimate fill IS
        // applied (a real SolidColorBrush, not null), and that it is NOT the ignored element-style's magenta.
        Assert.True(button.IsFocused);
        Assert.IsType<DrawingMedia.SolidColorBrush>(button.Background);
        Assert.NotSame(Vbrush, button.Background);
    }

    [Fact] // C102 — layer beats specificity: a LESS-specific App(3) rule beats a MORE-specific Theme(2) rule
    public void C102_LayerBeatsSpecificity_AppBeatsTheme()
    {
        var themeBrush = new DrawingMedia.SolidColorBrush(Color.FromRgb(99, 0, 0));

        using var host = UITestHost.Create();
        var button = new Button { Content = "OK" };
        button.Classes.Add("primary");
        host.ShowRoot(button);

        // A MORE-specific theme rule (Button.primary, classLike=1) at Theme(2) competes with a LESS-specific
        // app rule (Button, classLike=0) at App(3). Layer beats specificity (C102): the app rule wins.
        var theme = CursorialTheme.CreateDefault();
        theme.Styles = new Styles { new Style("Button.primary").Set(Control.BackgroundProperty, themeBrush) };
        host.Application.Theme = theme;
        host.Application.Styles.Add(new Style("Button").Set(Control.BackgroundProperty, Vbrush));
        host.RunFrame();

        // Both armed — but the App-layer (less specific) wins.
        Assert.Contains(StyleDiagnostics.MatchedRules(button), r => r.Layer == StyleLayer.Theme && r.IsActive);
        Assert.Contains(StyleDiagnostics.MatchedRules(button), r => r.Layer == StyleLayer.App && r.IsActive);
        Assert.Same(Vbrush, button.Background);
    }

    [Fact] // C100f — END-TO-END (the #19 shape): a theme rule's TextElement.TextAttributes setter reaches a focused button
    public void C100f_ThemeRule_TextAttributesReachesElement()
    {
        using var host = UITestHost.Create();

        // A panel root so its child Button is the first tab stop (auto-focused on activation).
        var root = new StackPanel { Name = "Root" };
        var button = new Button { Content = "OK" };
        root.Children.Add(button);

        // A PLAIN `Button:focus` rule at the default (color) tier. BuiltIn's only TextAttributes rules are
        // `.caps-nocolor`-gated, which never match here (no caps-nocolor class at a color tier) — so the
        // Inverse below is UNAMBIGUOUSLY this theme rule, not a BuiltIn duplicate (isolates the channel).
        var theme = CursorialTheme.CreateDefault();
        theme.Styles = new Styles
        {
            new Style("Button:focus").Set(TextElement.TextAttributesProperty, TextAttributes.Inverse),
        };
        host.Application.Theme = theme;
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        // The theme rule armed at Theme(2) and, with the button focused, flipped the inherited
        // TextElement.TextAttributes to Inverse — the exact mechanism #19 will author in XAML.
        Assert.True(button.IsFocused);
        Assert.Contains(StyleDiagnostics.MatchedRules(button), r => r.Layer == StyleLayer.Theme && r.IsActive);
        Assert.Equal(TextAttributes.Inverse, TextElement.GetTextAttributes(button));
        // Sanity: at a color tier the inherited attribute is the theme rule's doing — clearing the theme
        // removes it (proves it was not some always-on default).
        host.Application.Theme = null;
        host.RunFrame();
        Assert.Equal(default, TextElement.GetTextAttributes(button));
    }

    [Fact] // C103 — control-theme nearest-scope ordering: a nearer chain scope's Type-keyed theme wins (both at ControlTheme(0))
    public void C103_ControlTheme_NearestScopeWins()
    {
        using var host = UITestHost.Create();
        var root = new StackPanel { Name = "Root" };
        var button = new Button { Content = "OK" };
        root.Children.Add(button);

        // A nearer chain scope (the root's element Resources) supplies a Type-keyed Button theme; it must
        // win over the BuiltIn one (SubscribeControlTheme resolves the nearest scope).
        var nearerTheme = new Style { Key = "Nearer.Button" }.Set(Control.TemplateProperty, new ControlTemplate(ctx => new Border()));
        root.Resources[typeof(Button)] = nearerTheme;
        host.ShowRoot(root);
        host.RunFrame();

        using var sub = ResourceServices.SubscribeControlTheme(button, new ResourceProbe(), out var resolved);
        Assert.Same(nearerTheme, resolved); // the nearer scope's theme wins over BuiltIn
        // It arms at ControlTheme — the layer is the same wherever the theme is found.
        Assert.Contains(StyleDiagnostics.MatchedRules(button), r => r.Layer == StyleLayer.ControlTheme);
    }
}
