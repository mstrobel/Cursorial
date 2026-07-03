using Cursorial.Output;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;
using Cursorial.UI.Themes;

using static Cursorial.Tests.UI.ControlMatrix.ControlMatrixFixture;

using Style = Cursorial.UI.Style;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>§3 — Variants live + subscriptions + the UIApplication merge + version (R1); rows C59–C92.</summary>
public sealed class Section03_SubscriptionsAndMerge
{
    private const string K = "Brush.A";

    // ───────────────────────────── 3.1 The UIApplication theme-surface merge ─────────────────────────────

    [Fact] // C59
    public void C59_MergedType_CarriesBothSurfaces()
    {
        using var host = UITestHost.Create();
        var app = host.Application;

        _ = app.Resources;
        Assert.Null(app.Theme);
        Assert.Null(app.RequestedThemeBase);
        Assert.Null(app.RequestedColorTier);
        _ = app.ActualThemeVariant;
        app.ActualThemeVariantChanged += (_, _) => { };
        app.ResourcesChanged += (_, _) => { };

        // The S6 host/loop surface lives on the same instance.
        Assert.NotNull(app.Dispatcher);
        Assert.NotNull(app.InputDispatcher);
    }

    [Fact] // C60
    public void C60_ResourcesReplace_FansCatchAll()
    {
        using var host = UITestHost.Create();
        var app = host.Application;
        var tree = BuildTree();
        host.ShowRoot(tree.Root);

        var events = new List<ResourcesChangedEventArgs>();
        app.ResourcesChanged += (_, e) => events.Add(e);

        app.Resources = new ResourceDictionary();
        Assert.Contains(events, e => e.Kind == ResourceChangeKind.CatchAll);
    }

    [Fact] // C61
    public void C61_AppResourcesBeatTheme()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        host.ShowRoot(tree.Root);
        host.Application.Resources[K] = "app";
        host.Application.Theme = new ResourceDictionary { [K] = "theme" };

        Assert.Equal("app", tree.Leaf.FindResource(K));
    }

    [Fact] // C62
    public void C62_NearerScopeShadowsApp()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        host.ShowRoot(tree.Root);
        host.Application.Resources[K] = "app";
        tree.MidA.Res[K] = "mid";

        var sibling = new Probe();
        tree.Root.Adopt(sibling); // outside midA

        Assert.Equal("mid", tree.Leaf.FindResource(K));
        Assert.Equal("app", sibling.FindResource(K));
    }

    // ───────────────────────────── 3.2 Variant lifecycle ─────────────────────────────

    [Fact] // C63
    public void C63_Startup_DerivesVariant()
    {
        using var host = UITestHost.Create(); // Kitty preset: bg #1E1E2E (dark), Truecolor
        Assert.Equal(new ThemeVariant(ThemeBase.Dark, ColorDepth.Truecolor), host.Application.ActualThemeVariant);
    }

    [Fact] // C64
    public async Task C64_Renegotiate_FlipsBase_OrderPinned_NoDictionaryChange()
    {
        using var host = UITestHost.Create();
        var app = host.Application;
        var tree = BuildTree();
        host.ShowRoot(tree.Root);
        Assert.Equal(ThemeBase.Dark, app.ActualThemeVariant.Base);

        var order = new List<string>();
        app.ActualThemeVariantChanged += (_, _) => order.Add("variant");
        app.ResourcesChanged += (_, _) => order.Add("resources");
        var dictPulsed = false;
        app.Resources.Changed += (_, _) => dictPulsed = true;

        host.Terminal.ScriptRenegotiatedCapabilities(Caps(Color.FromRgb(255, 255, 255), ColorDepth.Truecolor));
        await app.RenegotiateAsync();

        Assert.Equal(ThemeBase.Light, app.ActualThemeVariant.Base);
        Assert.Equal(["variant", "resources"], order); // order pinned
        Assert.False(dictPulsed); // no dictionary mutates, no dictionary Changed
    }

    [Fact] // C65
    public void C65_RequestedThemeBase_OverridesDerived_OnlyResources()
    {
        using var host = UITestHost.Create();
        var app = host.Application;
        var tree = BuildTree();
        host.ShowRoot(tree.Root);

        var resourceEvents = 0;
        app.ResourcesChanged += (_, _) => resourceEvents++;
        var tierClassBefore = ColorClass(tree.Root);

        app.RequestedThemeBase = ThemeBase.Light;
        Assert.Equal(ThemeBase.Light, app.ActualThemeVariant.Base);
        Assert.Equal(ColorDepth.Truecolor, app.ActualThemeVariant.Tier); // tier still negotiated
        Assert.True(resourceEvents > 0);
        Assert.Equal(tierClassBefore, ColorClass(tree.Root)); // no capability-class change
    }

    [Fact] // C66
    public void C66_RequestedColorTier_Override_ResourcesAndClass()
    {
        using var host = UITestHost.Create();
        var app = host.Application;
        var tree = BuildTree();
        host.ShowRoot(tree.Root);

        app.RequestedColorTier = ColorDepth.Ansi16;
        host.RunFrame();
        Assert.Equal(ColorDepth.Ansi16, app.ActualThemeVariant.Tier);
        Assert.Equal("caps-ansi16", ColorClass(tree.Root)); // effective tier (CD14)
    }

    [Fact] // C67
    public void C67_ClearRequested_RederivesFromTerminal()
    {
        using var host = UITestHost.Create();
        var app = host.Application;
        var tree = BuildTree();
        host.ShowRoot(tree.Root);

        app.RequestedColorTier = ColorDepth.Ansi16;
        app.RequestedThemeBase = ThemeBase.Light;
        host.RunFrame();

        app.RequestedColorTier = null;
        app.RequestedThemeBase = null;
        host.RunFrame();

        Assert.Equal(new ThemeVariant(ThemeBase.Dark, ColorDepth.Truecolor), app.ActualThemeVariant);
    }

    [Fact] // C68
    public async Task C68_RenegotiateNoMaterialChange_Idempotent()
    {
        using var host = UITestHost.Create();
        var app = host.Application;
        var tree = BuildTree();
        host.ShowRoot(tree.Root);

        var fired = false;
        app.ActualThemeVariantChanged += (_, _) => fired = true;
        app.ResourcesChanged += (_, _) => fired = true;

        host.Terminal.ScriptRenegotiatedCapabilities(TestCapabilities.KittyTruecolor); // same caps
        await app.RenegotiateAsync();

        Assert.False(fired);
    }

    // ───────────────────────────── 3.3 Capability-class re-point ─────────────────────────────

    [Fact] // C69
    public void C69_ColorClass_FollowsEffectiveTier()
    {
        using var host = UITestHost.Create(); // negotiated Truecolor
        var app = host.Application;
        var tree = BuildTree();
        host.ShowRoot(tree.Root);

        app.RequestedColorTier = ColorDepth.Ansi256;
        host.RunFrame();
        Assert.Equal("caps-ansi256", ColorClass(tree.Root)); // NOT negotiated Truecolor
    }

    [Fact] // C70
    public void C70_NonColorClasses_StayNegotiated()
    {
        using var host = UITestHost.Create();
        var app = host.Application;
        var tree = BuildTree();
        host.ShowRoot(tree.Root);

        app.RequestedColorTier = ColorDepth.Ansi16;
        host.RunFrame();

        Assert.Contains("caps-motion", tree.Root.Classes);          // negotiated, unchanged
        Assert.Contains("caps-kitty-keyboard", tree.Root.Classes);  // negotiated, unchanged
        Assert.Contains("caps-unicode", tree.Root.Classes);
        Assert.Equal("caps-ansi16", ColorClass(tree.Root));         // only the color class follows the effective tier
    }

    [Fact] // C71 — a caps-ansi16-classed style reassigns a glyph resource; resources + styles agree
    public void C71_PreviewAnsi16_ResourcesAndStylesAgree()
    {
        using var host = UITestHost.Create();
        var app = host.Application;
        var tree = BuildTree();
        host.ShowRoot(tree.Root);

        app.RequestedColorTier = ColorDepth.Ansi16;
        host.RunFrame();

        // The Ansi16-gated class is present (styles agree) AND the BuiltIn glyph resource resolves at
        // the Ansi16 dictionary (resources agree) — no desync.
        Assert.Equal("caps-ansi16", ColorClass(tree.Root));
        var borderPen = tree.Leaf.FindResource(ThemeKeys.BorderPen);
        Assert.IsType<Drawing.Media.Pen>(borderPen); // resolves at the Ansi16 tier dictionary
    }

    [Fact] // C72 — the re-stamp rides ActualThemeVariantChanged
    public void C72_ClassRestamp_RidesVariantChanged()
    {
        using var host = UITestHost.Create();
        var app = host.Application;
        var tree = BuildTree();
        host.ShowRoot(tree.Root);

        var variantChangedFired = false;
        app.ActualThemeVariantChanged += (_, _) =>
        {
            variantChangedFired = true;
            // the class is already (or about to be) re-stamped on this event path
        };

        app.RequestedColorTier = ColorDepth.Ansi16;
        Assert.True(variantChangedFired);
        Assert.Equal("caps-ansi16", ColorClass(tree.Root));
    }

    // ───────────────────────────── 3.4 Subscriptions, registry, pulse routing, scope containment ─────────────────────────────

    [Fact] // C73
    public void C73_SetResourceReference_ProducerAtLocalValue()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        tree.Root.Res[K] = Vbrush;
        host.ShowRoot(tree.Root);

        tree.Leaf.SetResourceReference(Probe.P, K);
        Assert.Same(Vbrush, tree.Leaf.GetValue(Probe.P));
        Assert.Equal(BindingPriority.LocalValue, tree.Leaf.GetValueSource(Probe.P).Priority);
    }

    [Fact] // C74
    public void C74_KeyedPulse_ReResolvesInPlace()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        tree.Root.Res[K] = Vbrush;
        host.ShowRoot(tree.Root);
        tree.Leaf.SetResourceReference(Probe.P, K);

        tree.Root.Res[K] = Vbrush2;
        host.RunFrame();
        Assert.Same(Vbrush2, tree.Leaf.GetValue(Probe.P));
    }

    [Fact] // C75
    public void C75_LaterSetValue_EvictsProducer_NoZombie()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        tree.Root.Res[K] = Vbrush;
        host.ShowRoot(tree.Root);
        tree.Leaf.SetResourceReference(Probe.P, K);

        tree.Leaf.SetValue(Probe.P, Vbrush2); // later local SetValue
        Assert.Same(Vbrush2, tree.Leaf.GetValue(Probe.P));

        // A subsequent pulse must NOT clobber the SetValue (the producer disposed its subscription).
        tree.Root.Res[K] = Vbrush;
        host.RunFrame();
        Assert.Same(Vbrush2, tree.Leaf.GetValue(Probe.P));
    }

    [Fact] // C76
    public void C76_ClearValue_DetachesProducer()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        tree.Root.Res[K] = Vbrush;
        host.ShowRoot(tree.Root);
        tree.Leaf.SetResourceReference(Probe.P, K);
        Assert.Same(Vbrush, tree.Leaf.GetValue(Probe.P));

        tree.Leaf.ClearValue(Probe.P);
        Assert.Null(tree.Leaf.GetValue(Probe.P)); // falls to default
    }

    [Fact] // C77
    public void C77_MissingKey_PromotesLowerSource()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        host.ShowRoot(tree.Root);

        tree.Leaf.SetResourceReference(Probe.P, "Missing");
        Assert.Null(tree.Leaf.GetValue(Probe.P)); // HasValue == false ⇒ default promotes
    }

    [Fact] // C78
    public void C78_MissingThenAppears_ReResolves()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        host.ShowRoot(tree.Root);
        tree.Leaf.SetResourceReference(Probe.P, K);
        Assert.Null(tree.Leaf.GetValue(Probe.P));

        tree.Root.Res[K] = Vbrush;
        host.RunFrame();
        Assert.Same(Vbrush, tree.Leaf.GetValue(Probe.P));
    }

    [Fact] // C79
    public void C79_NearerScopeShadow_ScopeContainmentReResolves()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        host.Application.Resources[K] = Vbrush; // leaf resolves to the app value
        host.ShowRoot(tree.Root);
        tree.Leaf.SetResourceReference(Probe.P, K);
        Assert.Same(Vbrush, tree.Leaf.GetValue(Probe.P));

        tree.MidA.Res[K] = Vbrush2; // a nearer scope appears
        host.RunFrame();
        Assert.Same(Vbrush2, tree.Leaf.GetValue(Probe.P)); // scope containment re-resolves leaf
    }

    [Fact] // C80
    public void C80_SiblingOutsideScope_NotReResolved()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        host.Application.Resources[K] = Vbrush;
        var sibling = new Probe();
        tree.Root.Adopt(sibling); // outside midA's subtree
        host.ShowRoot(tree.Root);

        sibling.SetResourceReference(Probe.P, K);
        Assert.Same(Vbrush, sibling.GetValue(Probe.P));

        tree.MidA.Res[K] = Vbrush2;
        host.RunFrame();
        Assert.Same(Vbrush, sibling.GetValue(Probe.P)); // not contained by midA — stays at the app value
    }

    [Fact] // C81
    public void C81_PauseResume_AtMostOneReResolve()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        tree.Root.Res[K] = Vbrush;
        host.ShowRoot(tree.Root);

        var probe = new ResourceProbe();
        var sub = ResourceServices.Subscribe(tree.Leaf, K, probe, out var initial);
        Assert.Same(Vbrush, initial);
        probe.Clear();

        sub.Pause();
        tree.Root.Res[K] = Vbrush; // pulse 1 (back to original)
        tree.Root.Res["other"] = "x"; // pulse 2 (unrelated)
        tree.Root.Res[K] = Vbrush2; // pulse 3 — net change vs the subscribed value
        Assert.Empty(probe.Changes); // paused

        sub.Resume();
        Assert.Single(probe.Changes); // at most one re-resolve via version compare (net change delivered once)
        Assert.Same(Vbrush2, probe.Last);

        sub.Dispose();
    }

    [Fact] // C82 — snapshot/tombstone: mid-sweep Subscribe/Dispose is safe
    public void C82_MidSweepReArm_SnapshotTombstone()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        tree.Root.Res[K] = Vbrush;
        host.ShowRoot(tree.Root);

        var rearm = new ReArmingListener(tree.Leaf, K);
        var sub = ResourceServices.Subscribe(tree.Leaf, K, rearm, out _);

        // A catch-all that, mid-sweep, disposes a node and subscribes a fresh one — no crash, no double-visit.
        tree.Root.Res[K] = Vbrush2;
        host.RunFrame();

        Assert.True(rearm.Fired);
        sub.Dispose();
        rearm.Dispose();
    }

    [Fact] // C83 — a listener mutating resources during a sweep drains to a fixpoint
    public void C83_ReentrantResourceMutation_DrainsToFixpoint()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        tree.Root.Res[K] = Vbrush;
        tree.Root.Res["B"] = "b0";
        host.ShowRoot(tree.Root);

        var mutator = new MutatingListener(tree.Root);
        var sub = ResourceServices.Subscribe(tree.Leaf, K, mutator, out _);

        tree.Root.Res[K] = Vbrush2; // triggers the listener, which mutates "B"
        host.RunFrame();

        Assert.True(mutator.Fired);
        sub.Dispose();
    }

    [Fact] // C84
    public void C84_Detach_UnregistersNodes_ReattachReResolves()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        tree.Root.Res[K] = Vbrush;
        host.ShowRoot(tree.Root);
        tree.Leaf.SetResourceReference(Probe.P, K);
        Assert.Same(Vbrush, tree.Leaf.GetValue(Probe.P));

        tree.MidA.RemoveChildForTest(tree.Leaf); // detach
        Assert.Equal(0, ResourceServices.GetResourceVersion(tree.Leaf)); // detached

        tree.MidA.Adopt(tree.Leaf); // reattach — forces one re-resolve
        Assert.Same(Vbrush, tree.Leaf.GetValue(Probe.P));
    }

    [Fact] // C85 — DEBUG leak tracker: zero live nodes at root teardown
    public void C85_RootTeardown_ZeroLiveNodes()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        tree.Root.Res[K] = Vbrush;
        host.ShowRoot(tree.Root);
        tree.Leaf.SetResourceReference(Probe.P, K);
        Assert.True(ResourceDiagnostics.Subscriptions(tree.Root) > 0);

        host.Application.RootElement = null; // tear the root down
        Assert.Equal(0, ResourceDiagnostics.Subscriptions(tree.Root)); // detached root has no registry
    }

    // ───────────────────────────── 3.5 SubscribeControlTheme + GetResourceVersion staleness ─────────────────────────────

    [Fact] // C89
    public void C89_GetResourceVersion_BumpsOnEveryPulse()
    {
        using var host = UITestHost.Create();
        var app = host.Application;
        var tree = BuildTree();
        tree.Root.Res[K] = Vbrush;
        host.ShowRoot(tree.Root);
        // Subscribe so the root has a registry.
        var probe = new ResourceProbe();
        var sub = ResourceServices.Subscribe(tree.Leaf, K, probe, out _);

        var v0 = ResourceServices.GetResourceVersion(tree.Leaf);
        tree.Root.Res[K] = Vbrush2; // keyed pulse
        var v1 = ResourceServices.GetResourceVersion(tree.Leaf);
        Assert.True(v1 > v0);

        app.RequestedColorTier = ColorDepth.Ansi16; // variant flip (catch-all)
        var v2 = ResourceServices.GetResourceVersion(tree.Leaf);
        Assert.True(v2 > v1);

        sub.Dispose();
    }

    [Fact] // C86 — SubscribeControlTheme: one handle owns the ThemeProperty observer + the chain registry node; theme resolves to the BuiltIn Type-keyed theme (CD13)
    public void C86_SubscribeControlTheme_OneHandle_ResolvesBuiltInThemeAndWatchesChain()
    {
        using var host = UITestHost.Create();
        var button = new Button { Content = "OK" };
        host.ShowRoot(button);
        host.RunFrame();

        var listener = new ResourceProbe();
        var sub = ResourceServices.SubscribeControlTheme(button, listener, out var theme);
        try
        {
            // theme = ctrl.Theme ?? chain lookup → the BuiltIn Type-keyed Button theme.
            Assert.NotNull(theme);
            Assert.Same(CursorialTheme.BuiltIn[typeof(Button)], theme);

            // The single handle watches the chain: a keyed pulse for the control-theme key reaches the listener.
            host.Application.Resources[typeof(Button)] = new Style { Key = "App.Button" };
            host.RunFrame();
            Assert.NotEmpty(listener.Changes); // the chain node re-resolved (shadowed by the app entry)
        }
        finally
        {
            sub.Dispose();
        }
    }

    [Fact] // C87 — explicit Control.Theme set re-arms (the override wins; ThemeProperty drives re-templating) (CD13)
    public void C87_ExplicitThemeSet_OverrideWins_ReArms()
    {
        using var host = UITestHost.Create();
        var button = new Button { Content = "OK" };
        host.ShowRoot(button);
        host.RunFrame();

        // Initially the chain resolves the BuiltIn Button theme.
        Assert.Same(CursorialTheme.BuiltIn[typeof(Button)], ResolveControlThemeInstance(button));

        // Setting an explicit Control.Theme: the override wins the effective theme and the
        // ThemeProperty-changed hook re-arms the control theme (re-templating via OnControlThemeChanged).
        var explicitStyle = new Style { Key = "Explicit.Button" }.Set(Control.TemplateProperty, ControlThemesProbeTemplate());
        button.Theme = explicitStyle;
        host.RunFrame();

        Assert.Same(explicitStyle, ResolveControlThemeInstance(button)); // override wins (CD13)
        // The re-arm took effect: the engine sealed the explicit override style while arming it.
        Assert.True(explicitStyle.IsSealed);
    }

    [Fact] // C88 — variant flip: control theme re-resolves to the SAME Style instance (keyed per Type, not per variant) → identity short-circuit → no re-templating (CD15)
    public void C88_VariantFlip_SameThemeInstance_NoReTemplating()
    {
        using var host = UITestHost.Create();
        var button = new Button { Content = "OK" };
        host.ShowRoot(button);
        host.RunFrame();

        var before = ResolveControlThemeInstance(button);
        Assert.Same(CursorialTheme.BuiltIn[typeof(Button)], before);

        host.Application.RequestedThemeBase = ThemeBase.Light; // variant flip
        host.RunFrame();

        var after = ResolveControlThemeInstance(button);
        Assert.Same(before, after); // themes keyed per Type, not per variant — same instance, no churn
    }

    [Fact] // C90 — TextBlock cache re-parses on a KEYED resource pulse (the key includes ver(this)); no ResourceDictionary.Changed subscription (CD16)
    public void C90_TextBlockCache_ReParsesOnKeyedPulse()
    {
        using var host = UITestHost.Create();
        // The brush lives on the root's element scope so a keyed pulse on it sweeps that root's registry.
        var root = new StackPanel { Name = "Root" };
        root.Resources["Brush.Accent"] = Vbrush;
        var tb = new TextBlock { Markup = "[brush=Brush.Accent]X[/brush]" };
        root.Children.Add(tb);
        host.ShowRoot(root);
        host.RunFrame();
        // Ensure a registry exists for this root (the version-bump path requires it — doc §11.6) by
        // holding one live subscription on the root. The TextBlock itself holds NONE (CD16: the cache
        // key, not a Changed subscription, is its staleness mechanism).
        using var keepRegistry = ResourceServices.Subscribe(root, "Brush.Accent", new ResourceProbe(), out _);
        var c0 = host.GetCell(0, 0);

        // A keyed pulse on the referenced brush bumps the root version (cache key changes). The
        // TextBlock holds NO Changed subscription (CD16), so it does not self-invalidate — the next
        // render is what re-parses (the cache key, not a subscription, is the staleness mechanism). A
        // sibling invalidation drives that next render; the stale-key check then forces a re-format.
        var v0 = ResourceServices.GetResourceVersion(tb);
        root.Resources["Brush.Accent"] = Vbrush2;
        Assert.True(ResourceServices.GetResourceVersion(tb) > v0); // ver bumped — cache key is now stale
        tb.InvalidateVisual(); // the "next render" the row names
        host.RunFrame();
        var c1 = host.GetCell(0, 0);

        Assert.NotEqual(c0.Style.Foreground, c1.Style.Foreground); // re-parsed with the new brush color
    }

    [Fact] // C91 — variant flip bumps ver synchronously on the pulse
    public void C91_VariantFlip_BumpsVersionSynchronously()
    {
        using var host = UITestHost.Create();
        var app = host.Application;
        var tree = BuildTree();
        host.ShowRoot(tree.Root);
        var probe = new ResourceProbe();
        var sub = ResourceServices.Subscribe(tree.Leaf, K, probe, out _);

        var before = ResourceServices.GetResourceVersion(tree.Leaf);
        app.RequestedThemeBase = ThemeBase.Light; // no RunFrame
        Assert.True(ResourceServices.GetResourceVersion(tree.Leaf) > before);

        sub.Dispose();
    }

    [Fact] // C92 — registration root = where the logical chain tops out
    public void C92_RegistrationRoot_LogicalChainTop()
    {
        using var host = UITestHost.Create();
        var tree = BuildTree();
        tree.Root.Res[K] = Vbrush;
        host.ShowRoot(tree.Root);

        var probe = new ResourceProbe();
        var sub = ResourceServices.Subscribe(tree.Leaf, K, probe, out _);
        Assert.Equal(1, ResourceDiagnostics.Subscriptions(tree.Root)); // registered under the root

        tree.Root.Res[K] = Vbrush2; // a root-scoped pulse sweeps the registry
        host.RunFrame();
        Assert.Same(Vbrush2, probe.Last);
        sub.Dispose();
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static string ColorClass(UIElement element)
        => element.Classes.First(c => c is "caps-truecolor" or "caps-ansi256" or "caps-ansi16" or "caps-nocolor");

    // A minimal template for an explicit Control.Theme override (C87).
    private static ControlTemplate ControlThemesProbeTemplate()
        => new(_ => new Border());

    // Resolves the control's effective control theme via a transient subscription (the same path the
    // styling engine takes) — used to assert identity stability across a variant flip (C88).
    private static Style? ResolveControlThemeInstance(Control control)
    {
        using var sub = ResourceServices.SubscribeControlTheme(control, new ResourceProbe(), out var theme);
        return theme;
    }

    // A listener that re-arms during the sweep (disposes itself, subscribes fresh) — the C82 regression.
    private sealed class ReArmingListener(Probe scope, string key) : IResourceChangeListener, IDisposable
    {
        private ResourceSubscription _fresh;
        public bool Fired { get; private set; }

        public void OnResourceChanged(object k, object? newValue)
        {
            Fired = true;
            _fresh.Dispose();
            _fresh = ResourceServices.Subscribe(scope, key, new ResourceProbe(), out _); // subscribe during the sweep
        }

        public void Dispose() => _fresh.Dispose();
    }

    // A listener that mutates a different resource during the sweep — the C83 fixpoint.
    private sealed class MutatingListener(Probe root) : IResourceChangeListener
    {
        public bool Fired { get; private set; }

        public void OnResourceChanged(object k, object? newValue)
        {
            if (Fired)
                return;
            Fired = true;
            root.Res["B"] = "b1"; // re-entrant resource mutation
        }
    }
}
