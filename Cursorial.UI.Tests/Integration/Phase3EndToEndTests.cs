using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

using Style = Cursorial.UI.Style;

namespace Cursorial.Tests.UI.Integration;

/// <summary>
/// Phase 3 integration: the design doc §14 P3 exit criteria proven end-to-end through
/// <see cref="UIHeadlessHost"/> — every scenario crosses the full spine (input drain → hover/focus
/// interaction-state flips → the styling engine's reconcile → store arbitration → layout/render →
/// composited cells), not an engine harness. Covers: (a) a styled tree (app + scoped channels,
/// classes, <c>:pointerover</c>/<c>:focus</c>) rendering with cell assertions, (b) the invariant-3
/// proof — a hover restyle re-rasters only the hovered element's zone, (c) retraction promoting
/// the runner-up rule inside the store (one notification, never a set-back through default),
/// (d) the template barrier with a manually-stamped <c>TemplatedParent</c>, (e) capability classes
/// flipping styling across <see cref="HeadlessCapabilities"/> presets, (f) the
/// <see cref="StyleDiagnostics.Explain"/> acceptance literal for a three-layer contest, and
/// (g) <c>Styles</c> mutation hot-reload with cookie-stable survivors (SD21).
/// </summary>
public sealed class Phase3EndToEndTests
{
    // ───────────────────────────── local elements ─────────────────────────────
    // Self-contained on purpose (the Phase2EndToEnd convention): the StyleMatrix fixtures define
    // `Widget`/`Card`, so this file brings its own leaves instead of importing that namespace.

    /// <summary>
    /// A styleable card leaf: <see cref="FillProperty"/> paints the background (AffectsRender),
    /// <see cref="InkProperty"/>/<see cref="TagProperty"/> are value-level probes for precedence
    /// rows. Optionally focusable (scenario (a)'s tab stops).
    /// </summary>
    private class Card : UIElement
    {
        public static readonly StyledProperty<Color> FillProperty = UIProperty.Register<Card, Color>("Fill");
        public static readonly StyledProperty<int> InkProperty = UIProperty.Register<Card, int>("Ink");
        public static readonly StyledProperty<int> TagProperty = UIProperty.Register<Card, int>("Tag");

        static Card() => AffectsRender<Card>(FillProperty, InkProperty, TagProperty);

        public Card(bool focusable = false) => Focusable = focusable;

        public Color Fill => GetValue(FillProperty);

        protected override Size MeasureOverride(Size availableSize) => new(12, 1);

        protected override void Render(RenderContext context)
            => context.FillOpaque(context.Bounds, Fill);
    }

    /// <summary>The control stand-in that "stamps" template parts at P3 (template instantiation is P5).</summary>
    private sealed class Shell : StackPanel
    {
    }

    /// <summary>The file's selector type resolver (parser-driven selectors over the private leaves).</summary>
    private sealed class Resolver : ISelectorTypeResolver
    {
        public static readonly Resolver Instance = new();

        public Type? Resolve(string typeName, out IReadOnlyList<Type>? ambiguousCandidates)
        {
            ambiguousCandidates = null;
            return typeName switch
                   {
                       "Card" => typeof(Card),
                       "Shell" => typeof(Shell),
                       "Pane" => typeof(StackPanel),
                       _ => null
                   };
        }
    }

    /// <summary>An <see cref="IValueObserver{T}"/> recording <c>(old, new, priority)</c> deliveries in order.</summary>
    private sealed class Probe<T> : IValueObserver<T>
    {
        public readonly List<(T Old, T New, BindingPriority Priority)> Deliveries = [];

        public static Probe<T> Attach(UIObject element, StyledProperty<T> property)
        {
            var probe = new Probe<T>();
            element.AddObserver(property, probe);
            return probe;
        }

        void IValueObserver<T>.OnPropertyChanged(UIObject source, UIProperty property, T oldValue, T newValue, BindingPriority priority)
            => Deliveries.Add((oldValue, newValue, priority));
    }

    /// <summary>A style with a parsed selector and constant setters (the matrix's <c>R(sel){…}</c> shape).</summary>
    private static Style R(string selector, params (UIProperty Property, object? Value)[] setters)
    {
        var style = new Style(selector, Resolver.Instance);
        foreach (var (property, value) in setters)
            style.Setters.Add(new Setter(property, value));
        return style;
    }

    // ───────────────────────────── (a) styled tree → rendered cells ─────────────────────────────

    private static readonly Color BaseFill = Color.FromRgb(10, 10, 10);
    private static readonly Color AccentFill = Color.FromRgb(200, 120, 40);
    private static readonly Color HoverFill = Color.FromRgb(60, 60, 90);
    private static readonly Color FocusFill = Color.FromRgb(90, 40, 120);

    /// <summary>
    /// The styled-tree round trip: app-channel rules (type + class), a scoped channel on the
    /// sidebar carrying <c>:pointerover</c>/<c>:focus</c> children (<c>^</c>-composed), real input
    /// driving the flips (activation auto-focus, Tab, mouse moves), and every state proven in the
    /// composited cells. <c>:focus</c> is declared after <c>:pointerover</c>, so a hovered+focused
    /// card keeps the focus look (same specificity — declaration order breaks the tie).
    /// </summary>
    [Fact]
    public void StyledTree_AppAndScopedChannels_ClassesAndInteractionPseudoClasses_RenderToCells()
    {
        using var host = UIHeadlessHost.Create();
        var root = new StackPanel();
        var sidebar = new StackPanel();
        var card1 = new Card(focusable: true);
        var card2 = new Card(focusable: true);
        var card3 = new Card(focusable: true);
        card2.Classes.Add("accent");
        sidebar.Children.Add(card1);
        sidebar.Children.Add(card2);
        sidebar.Children.Add(card3);
        root.Children.Add(sidebar);

        // App channel: the base look + a class-targeted variant.
        host.Application.Styles.Add(R("Card", (Card.FillProperty, BaseFill)));
        host.Application.Styles.Add(R("Card.accent", (Card.FillProperty, AccentFill)));

        // Scoped channel on the sidebar: one style, interaction states as ^-composed children
        // (the hover/focus pair in one style also satisfies the SD23-③ hover-parity lint).
        var interaction = new Style("Card", Resolver.Instance);
        var hover = new Style("^:pointerover");
        hover.Setters.Add(new Setter(Card.FillProperty, HoverFill));
        var focus = new Style("^:focus");
        focus.Setters.Add(new Setter(Card.FillProperty, FocusFill));
        interaction.Children.Add(hover);
        interaction.Children.Add(focus);
        sidebar.Styles.Add(interaction);

        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        // Activation auto-focus landed on the first tab stop — card1 renders the focus look.
        Assert.Same(card1, host.Application.FocusManager.FocusedElement);
        Assert.Equal(FocusFill, host.GetCell(1, 0).Style.Background);
        Assert.Equal(AccentFill, host.GetCell(1, 1).Style.Background); // the class rule
        Assert.Equal(BaseFill, host.GetCell(1, 2).Style.Background);   // the type rule

        // Tab: focus moves card1 → card2; the SAME frame renders both restyles.
        host.SendKey(Cursorial.Input.Key.Tab);
        host.RunFrame();
        Assert.Equal(BaseFill, host.GetCell(1, 0).Style.Background);
        Assert.Equal(FocusFill, host.GetCell(1, 1).Style.Background); // focus (Scoped) beats accent (App) by layer

        // Hover card3: the :pointerover flip restyles and renders within one frame (S150).
        host.SendMouseMove(1, 2);
        host.RunFrame();
        Assert.Equal(HoverFill, card3.Fill);
        Assert.Equal(HoverFill, host.GetCell(1, 2).Style.Background);

        // Hover the FOCUSED card2: both pseudo-states active — :focus wins on declaration order.
        host.SendMouseMove(1, 1);
        host.RunFrame();
        Assert.Equal(FocusFill, host.GetCell(1, 1).Style.Background);
        Assert.Equal(BaseFill, host.GetCell(1, 2).Style.Background); // card3's hover retracted
    }

    // ───────────────────────────── (b) the invariant-3 proof ─────────────────────────────

    /// <summary>
    /// Invariant 3 end-to-end: a <c>:pointerover</c> flip driven by a REAL mouse move restyles the
    /// hovered element and re-rasters only its render zone — every other zone's
    /// <c>Scene.RasterVersion</c> (including the root's) is frozen, both on hover-on and on the
    /// retraction when the pointer leaves.
    /// </summary>
    [Fact]
    public void HoverRestyle_ReRastersOnlyTheHoveredZone_AllOtherSceneVersionsFrozen()
    {
        using var host = UIHeadlessHost.Create();
        var root = new StackPanel();
        var cardA = new Card { IsRenderBoundary = true };
        var cardB = new Card { IsRenderBoundary = true };
        var cardC = new Card { IsRenderBoundary = true };
        root.Children.Add(cardA);
        root.Children.Add(cardB);
        root.Children.Add(cardC);

        host.Application.Styles.Add(R("Card", (Card.FillProperty, BaseFill)));
        host.Application.Styles.Add(R("Card:pointerover", (Card.FillProperty, HoverFill)));

        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        var tree = host.Application.WindowManager!.Tree!;
        var surfaceRoot = host.Application.WindowManager!.RootSurface!.Root; // the RootElementHost owns the root zone
        var versionRoot = tree.GetScene(surfaceRoot)!.RasterVersion;
        var versionA = tree.GetScene(cardA)!.RasterVersion;
        var versionB = tree.GetScene(cardB)!.RasterVersion;
        var versionC = tree.GetScene(cardC)!.RasterVersion;

        // Hover ON: the pointer lands on card B (row 1) — only B's zone re-rasters.
        host.SendMouseMove(2, 1);
        host.RunFrame();

        Assert.True(cardB.IsPointerOver);
        Assert.Equal(HoverFill, host.GetCell(2, 1).Style.Background);
        Assert.Equal(versionB + 1, tree.GetScene(cardB)!.RasterVersion);
        Assert.Equal(versionA, tree.GetScene(cardA)!.RasterVersion);     // frozen
        Assert.Equal(versionC, tree.GetScene(cardC)!.RasterVersion);     // frozen
        Assert.Equal(versionRoot, tree.GetScene(surfaceRoot)!.RasterVersion);   // frozen

        // Hover OFF: the retraction restyles B back — again only B's zone re-rasters.
        host.SendMouseMove(50, 10);
        host.RunFrame();

        Assert.False(cardB.IsPointerOver);
        Assert.Equal(BaseFill, host.GetCell(2, 1).Style.Background);
        Assert.Equal(versionB + 2, tree.GetScene(cardB)!.RasterVersion);
        Assert.Equal(versionA, tree.GetScene(cardA)!.RasterVersion);
        Assert.Equal(versionC, tree.GetScene(cardC)!.RasterVersion);
        Assert.Equal(versionRoot, tree.GetScene(surfaceRoot)!.RasterVersion);
    }

    // ───────────────────────────── (c) retraction promotes the runner-up ─────────────────────────────

    /// <summary>
    /// Retraction is store-owned promotion (§0 invariant 4): hover-off retracts the
    /// <c>:pointerover</c> rule's entry and the store promotes the runner-up type rule in ONE
    /// observer delivery at <see cref="BindingPriority.Style"/> — never a set-back through
    /// default/unset (which would show as two deliveries or a non-Style lane).
    /// </summary>
    [Fact]
    public void HoverOff_RetractionPromotesTheRunnerUpRule_SingleStoreNotification_NeverASetBack()
    {
        using var host = UIHeadlessHost.Create();
        var root = new StackPanel();
        var card = new Card();
        root.Children.Add(card);

        host.Application.Styles.Add(R("Card", (Card.InkProperty, 3)));             // the runner-up
        host.Application.Styles.Add(R("Card:pointerover", (Card.InkProperty, 9))); // the hover winner

        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());
        Assert.Equal(3, card.GetValue(Card.InkProperty));

        var probe = Probe<int>.Attach(card, Card.InkProperty);

        // Hover ON: one activation delivery, runner-up → winner. The :pointerover rule is
        // conditional ⇒ it arbitrates (and reports) at StyleTrigger (§0.3, 2026-07-12).
        host.SendMouseMove(2, 0);
        host.RunFrame();
        Assert.Equal([(3, 9, BindingPriority.StyleTrigger)], probe.Deliveries);
        probe.Deliveries.Clear();

        // Hover OFF: ONE delivery, winner → runner-up, at the promoted lane — the resting type
        // rule lives in the Style slot (PD10 reports the NEW winning lane).
        host.SendMouseMove(50, 10);
        host.RunFrame();
        Assert.Equal([(9, 3, BindingPriority.Style)], probe.Deliveries);
        Assert.Equal(3, card.GetValue(Card.InkProperty));
    }

    // ───────────────────────────── (d) the template barrier ─────────────────────────────

    /// <summary>
    /// The template barrier end-to-end (§0 invariant 5) with a manually-stamped
    /// <c>TemplatedParent</c> (template instantiation is P5): a plain type rule is skipped for the
    /// stamped part but applies to a free element; the <c>/template/</c> combinator crosses the
    /// barrier; an explicit <see cref="UIElement.Style"/> arms regardless (SD8 — explicit styles
    /// are element-addressed).
    /// </summary>
    [Fact]
    public void TemplateBarrier_StampedPart_SkipsPlainRules_TemplateCombinatorAndExplicitStyleApply()
    {
        using var host = UIHeadlessHost.Create();
        var root = new StackPanel();
        var shell = new Shell();
        var part = new Card();
        part.SetTemplatedParent(shell); // the manual P3 stamp — P5's template instantiation does this
        shell.Children.Add(part);
        var free = new Card();
        root.Children.Add(shell);
        root.Children.Add(free);

        host.Application.Styles.Add(R("Card", (Card.FillProperty, BaseFill)));        // barrier-skipped for the part
        host.Application.Styles.Add(R("Shell /template/ Card", (Card.InkProperty, 7))); // the sanctioned crossing
        var explicitStyle = new Style();
        explicitStyle.Setters.Add(new Setter(Card.TagProperty, 9));
        part.Style = explicitStyle;                                                   // exempt from the barrier

        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        // The free card: the plain type rule applies and renders.
        Assert.Equal(BaseFill, free.Fill);
        Assert.Equal(BaseFill, host.GetCell(1, 1).Style.Background);
        Assert.Contains(StyleDiagnostics.MatchedRules(free), static rule => rule.SelectorText == "Card");

        // The stamped part: the plain rule is SKIPPED (not armed-inactive — absent), the
        // /template/ rule and the explicit style both apply.
        Assert.Equal(default, part.Fill);                                     // "Card" never reached it
        Assert.NotEqual(BaseFill, host.GetCell(1, 0).Style.Background);       // rendered truth
        Assert.Equal(7, part.GetValue(Card.InkProperty));                     // /template/ crossed
        Assert.Equal(9, part.GetValue(Card.TagProperty));                     // explicit armed

        var partRules = StyleDiagnostics.MatchedRules(part);
        Assert.Equal(2, partRules.Count);
        Assert.Contains(partRules, static rule => rule.SelectorText == "Shell /template/ Card");
        Assert.Contains(partRules, static rule => rule.SelectorText == "(explicit)");
    }

    // ───────────────────────────── (e) capability classes ─────────────────────────────

    /// <summary>
    /// Capability classes flip styling end-to-end (SD14, inversion 6 — stamped from the negotiated
    /// snapshot at P3): a <c>.caps-truecolor</c>-gated rule wins on a Kitty-class preset and never
    /// arms on a legacy 16-color preset, proven in the rendered cells of both hosts.
    /// </summary>
    [Fact]
    public void CapabilityClasses_GateStyling_AcrossTestCapabilityPresets()
    {
        var richFill = Color.FromRgb(120, 200, 255);

        using (var kitty = UIHeadlessHost.Create()) // KittyTruecolor default
        {
            var root = new StackPanel();
            var card = new Card();
            root.Children.Add(card);
            kitty.Application.Styles.Add(R("Card", (Card.FillProperty, BaseFill)));
            kitty.Application.Styles.Add(R(".caps-truecolor Card", (Card.FillProperty, richFill)));
            kitty.ShowRoot(root);
            Assert.True(kitty.RunUntilIdle());

            Assert.Equal(richFill, card.Fill);
            Assert.Equal(richFill, kitty.GetCell(1, 0).Style.Background);
            Assert.Contains(StyleDiagnostics.MatchedRules(card), static rule => rule.SelectorText == ".caps-truecolor Card");
        }

        using (var legacy = UIHeadlessHost.Create(new UIHeadlessHostOptions { Capabilities = HeadlessCapabilities.Ansi16Legacy }))
        {
            var root = new StackPanel();
            var card = new Card();
            root.Children.Add(card);
            legacy.Application.Styles.Add(R("Card", (Card.FillProperty, BaseFill)));
            legacy.Application.Styles.Add(R(".caps-truecolor Card", (Card.FillProperty, richFill)));
            legacy.ShowRoot(root);
            Assert.True(legacy.RunUntilIdle());

            Assert.Equal(BaseFill, card.Fill); // the tier rule never armed
            Assert.Equal(BaseFill, legacy.GetCell(1, 0).Style.Background);
            Assert.DoesNotContain(StyleDiagnostics.MatchedRules(card), static rule => rule.SelectorText == ".caps-truecolor Card");
        }
    }

    // ───────────────────────────── (f) Explain acceptance ─────────────────────────────

    /// <summary>
    /// The §14 P3 acceptance surface: <see cref="StyleDiagnostics.Explain"/> renders the FULL
    /// sort-key derivation, one line per contributor, strongest first, for a three-layer contest.
    /// Amended for the activator split (§0.3, 2026-07-12): the class-gated Scoped rule is
    /// CONDITIONAL ⇒ the StyleTrigger slot, which beats every resting rule regardless of layer or
    /// sort key (slot beats key) — it renders first and wins over the resting Explicit; within the
    /// resting slot, Explicit still beats App (layer beats specificity). Literals pinned
    /// bit-for-bit (SD13).
    /// </summary>
    [Fact]
    public void Explain_ThreeLayerContest_ExactOneLinePerContributorDerivation()
    {
        using var host = UIHeadlessHost.Create();
        var root = new StackPanel();
        var pane = new StackPanel();
        var card = new Card();
        card.Classes.Add("accent");
        pane.Children.Add(card);
        root.Children.Add(pane);

        host.Application.Styles.Add(R("Card", (Card.InkProperty, 3)));   // App(3), order 0 — resting
        pane.Styles.Add(R("Card.accent", (Card.InkProperty, 5)));        // Scoped(4), owner depth 1 — CONDITIONAL
        var explicitStyle = new Style();
        explicitStyle.Setters.Add(new Setter(Card.InkProperty, 9));
        card.Style = explicitStyle;                                      // Explicit(5) — resting

        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());
        Assert.Equal(5, card.GetValue(Card.InkProperty)); // the conditional rule pierces both resting layers

        var lines = StyleDiagnostics.Explain(card, Card.InkProperty).Split('\n');

        Assert.Equal(3, lines.Length);
        Assert.Equal(
            "Ink = 5 <- Scoped(4) \"Card.accent\" names=0 classLike=1 types=1 depth=1 order=0 key=0x8000080808000000 -- winning",
            lines[0]);
        Assert.Equal(
            "Ink = 9 <- Explicit(5) \"(explicit)\" names=0 classLike=0 types=0 depth=0 order=0 key=0xA000000000000000 -- shadowed",
            lines[1]);
        Assert.Equal(
            "Ink = 3 <- App(3) \"Card\" names=0 classLike=0 types=1 depth=0 order=0 key=0x6000000800000000 -- shadowed",
            lines[2]);
    }

    // ───────────────────────────── (g) Styles mutation hot-reload ─────────────────────────────

    /// <summary>
    /// Hot-reload semantics for a live <c>Styles</c> collection (SD21 — coarse re-match with
    /// identity diff): an unrelated style add leaves the survivor's frame (and its retraction
    /// cookie) untouched and the store silent; a matching add promotes in one delivery while the
    /// survivor keeps the SAME frame instance; removing it again demotes in one delivery and the
    /// original frame has survived two re-matches.
    /// </summary>
    [Fact]
    public void StylesMutationHotReload_SurvivorFramesAndCookiesStable_AcrossReMatches()
    {
        using var host = UIHeadlessHost.Create();
        var root = new StackPanel();
        var card = new Card();
        card.Classes.Add("hot");
        root.Children.Add(card);

        host.Application.Styles.Add(R("Card", (Card.InkProperty, 3)));

        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());
        Assert.Equal(3, card.GetValue(Card.InkProperty));

        var frameBefore = Assert.Single(card.StyleStateInternal!.Frames);
        var probe = Probe<int>.Attach(card, Card.InkProperty);

        // Unrelated add: identity diff keeps the survivor's frame; the store never hears about it.
        host.Application.Styles.Add(R(".zzz", (Card.TagProperty, 1)));
        Assert.Same(frameBefore, Assert.Single(card.StyleStateInternal!.Frames));
        Assert.Empty(probe.Deliveries);
        Assert.Equal(3, card.GetValue(Card.InkProperty));

        // Matching add (stronger): one promotion; the survivor keeps the SAME frame instance.
        // `Card.hot` is class-gated ⇒ conditional ⇒ the StyleTrigger slot (§0.3, 2026-07-12).
        var hotStyle = R("Card.hot", (Card.InkProperty, 7));
        host.Application.Styles.Add(hotStyle);
        Assert.Equal([(3, 7, BindingPriority.StyleTrigger)], probe.Deliveries);
        probe.Deliveries.Clear();
        Assert.Equal(2, card.StyleStateInternal!.Frames.Length);
        Assert.Contains(frameBefore, card.StyleStateInternal!.Frames);

        // Remove it again: one demotion back to the survivor — whose cookie outlived both re-matches.
        host.Application.Styles.Remove(hotStyle);
        Assert.Equal([(7, 3, BindingPriority.Style)], probe.Deliveries);
        Assert.Same(frameBefore, Assert.Single(card.StyleStateInternal!.Frames));
        Assert.Equal(3, card.GetValue(Card.InkProperty));

        Assert.True(host.RunUntilIdle()); // the host settles cleanly after the churn
    }
}
