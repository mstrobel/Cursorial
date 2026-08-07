using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering.Fragments;
using Cursorial.Text;
using Cursorial.UI;

using Style = Cursorial.UI.Style;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Input;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>
/// §45 — the per-axis text-attribute properties (proposal-textattributes-decomposition; P2 rows).
/// The axes are NON-inheriting and flow like <c>Background</c>: element-level values, presenter
/// forwards onto framework-GENERATED leaves only (never DataTemplate content), the paint-time fold
/// as the single composition point. Rows named TA1–TA9.
/// </summary>
public sealed class Section45_TextAttributeAxes
{
    private static UIHeadlessHost Attach(UIElement root)
    {
        var host = UIHeadlessHost.Create();
        host.ShowRoot(root);
        host.RunFrame();
        return host;
    }

    [Fact] // TA1 — the fold maps every axis to its wire flag; TextWeight is exclusive by construction
    public void TA1_Fold_MapsAxes_WeightExclusive()
    {
        var e = new TextBlock("x");

        TextElement.SetTextWeight(e, TextWeight.Bold);
        Assert.Equal(TextAttributes.Bold, TextElement.ComposeAttributes(e).Flags);

        TextElement.SetTextWeight(e, TextWeight.Faint);
        Assert.Equal(TextAttributes.Faint, TextElement.ComposeAttributes(e).Flags); // one dial — Bold|Faint unrepresentable

        TextElement.SetTextWeight(e, TextWeight.Normal);
        TextElement.SetTextStyle(e, TextStyle.Italic);
        TextElement.SetStrikethrough(e, true);
        TextElement.SetOverline(e, true);
        TextElement.SetInverse(e, true);
        TextElement.SetBlink(e, true);
        TextElement.SetConcealed(e, true); // → TextAttributes.Hidden (SGR 8 "conceal")
        Assert.Equal(
            TextAttributes.Italic | TextAttributes.Strikethrough | TextAttributes.Overline |
            TextAttributes.Inverse | TextAttributes.Blink | TextAttributes.Hidden,
            TextElement.ComposeAttributes(e).Flags);
    }

    [Fact] // TA2 — Underline unifies presence + shape: null = absent; a value sets the flag AND the shape
    public void TA2_Underline_PresenceIsShape()
    {
        var e = new TextBlock("x");
        Assert.Equal(TextAttributes.None, TextElement.ComposeAttributes(e).Flags);

        TextElement.SetUnderline(e, UnderlineStyle.Curly);
        var resolved = TextElement.ComposeAttributes(e);
        Assert.Equal(TextAttributes.Underline, resolved.Flags);
        Assert.Equal(UnderlineStyle.Curly, resolved.UnderlineShape);

        TextElement.SetUnderline(e, null);
        Assert.Equal(TextAttributes.None, TextElement.ComposeAttributes(e).Flags);
    }


    [Fact] // TA4 — the motivating case: a conditional Inverse rule + a resting TextWeight rule COMPOSE (different axes)
    public void TA4_ConditionalInverse_ComposesWith_RestingWeight()
    {
        using var host = UIHeadlessHost.Create();
        var tb = new TextBlock("x");
        tb.Classes.Add("cta");

        host.Application.Styles.Add(new Style("TextBlock.cta") // class-gated ⇒ conditional (StyleTrigger)
            .Set(TextElement.InverseProperty, true));
        host.Application.Styles.Add(new Style("TextBlock")     // structural ⇒ resting (Style)
            .Set(TextElement.TextWeightProperty, TextWeight.Bold));
        host.ShowRoot(tb);
        host.RunFrame();

        // Different properties — no arbitration between them ever occurs (proposal §2.2).
        Assert.Equal(TextAttributes.Inverse | TextAttributes.Bold, TextElement.ComposeAttributes(tb).Flags);

        tb.Classes.Remove("cta"); // retraction touches ONLY the Inverse axis
        host.RunFrame();
        Assert.Equal(TextAttributes.Bold, TextElement.ComposeAttributes(tb).Flags);
    }

    [Fact] // TA5 — a same-axis contest arbitrates through the lattice (conditional Faint beats resting Bold while active)
    public void TA5_SameAxisContest_ConditionalBeatsResting_RetractsCleanly()
    {
        using var host = UIHeadlessHost.Create();
        var tb = new TextBlock("x");
        tb.Classes.Add("dim");

        host.Application.Styles.Add(new Style("TextBlock")
            .Set(TextElement.TextWeightProperty, TextWeight.Bold));       // resting
        host.Application.Styles.Add(new Style("TextBlock.dim")
            .Set(TextElement.TextWeightProperty, TextWeight.Faint));      // conditional — pierces while active
        host.ShowRoot(tb);
        host.RunFrame();

        Assert.Equal(TextWeight.Faint, TextElement.GetTextWeight(tb));
        Assert.Equal(BindingPriority.StyleTrigger, tb.GetValueSource(TextElement.TextWeightProperty).Priority);

        tb.Classes.Remove("dim");
        host.RunFrame();
        Assert.Equal(TextWeight.Bold, TextElement.GetTextWeight(tb)); // clean retraction to the resting rule
    }

    [Fact] // TA6 — the underline shape renders end-to-end through the widened formatted-text seam (Q2)
    public void TA6_UnderlineShape_RendersEndToEnd()
    {
        var tb = new TextBlock("Hi");
        TextElement.SetUnderline(tb, UnderlineStyle.Curly);
        using var host = Attach(tb);

        var style = host.GetCell(0, 0).Style;
        Assert.True((style.Attributes & TextAttributes.Underline) != 0);
        Assert.Equal(UnderlineStyle.Curly, style.UnderlineStyle); // not silently Single (proposal §3.1 "no silent drops")
    }

    [Fact] // TA7 — presenter forward: a control-level axis reaches the GENERATED string label (flows like Background)
    public void TA7_GeneratedLeaf_ReceivesForwardedAxes()
    {
        using var host = UIHeadlessHost.Create();
        var button = new Button { Content = "OK", Focusable = false };
        TextElement.SetInverse(button, true);
        TextElement.SetTextWeight(button, TextWeight.Bold);
        host.ShowRoot(button);
        Assert.True(host.RunUntilIdle());

        // The generated presentation leaf (the access-text label the presenter materialized) carries
        // the forwarded values — no inheritance, live per-axis forwards (proposal §2.1 path 3).
        var leaf = FindLeaf(button);
        Assert.NotNull(leaf);
        Assert.True(TextElement.GetInverse(leaf!));
        Assert.Equal(TextWeight.Bold, TextElement.GetTextWeight(leaf!));

        TextElement.SetInverse(button, false); // the forward is LIVE — a control change re-pushes
        host.RunFrame();
        Assert.False(TextElement.GetInverse(leaf!));
        Assert.Equal(TextWeight.Bold, TextElement.GetTextWeight(leaf!)); // the other axis never moved
    }

    [Fact] // TA8 REVISED — DataTemplate in a ContentPresenter ONLY: it receives ALL forwarded axes (§7.3 scoping rule)
           // Rationale: If text styling axes are non-inheriting, the only reason a user would set them on a
           // ContentControl or ContentPresenter would be for them to apply to the hosted content. The default theme
           // only sets Inverse for :caps-nocolor selection on stock item containers, so anything else would have come
           // from user code and should be assumed intentional.
    public void TA8_DataTemplateContent_ReceivesNothing()
    {
        using var host = UIHeadlessHost.Create();
        TextBlock? inner = null;
        var cc = new ContentControl
        {
            Content = new object(),
            ContentTemplate = new DataTemplate
            {
                Content = new FuncTemplateContent(_ => inner = new TextBlock("app")),
            },
        };
        TextElement.SetInverse(cc, true);
        host.ShowRoot(cc);
        Assert.True(host.RunUntilIdle());

        Assert.NotNull(inner);
        Assert.True(TextElement.GetInverse(inner!)); // app content styles itself — never clobbered by forwards
        Assert.Equal(BindingPriority.Template, inner!.GetValueSource(TextElement.InverseProperty).Priority);
    }

    [Fact] // TA9 — non-inheriting: an ancestor's axis value does NOT flow to arbitrary descendants
    public void TA9_NonInheriting_NoAmbientFlow()
    {
        using var host = UIHeadlessHost.Create();
        var panel = new StackPanel();
        var tb = new TextBlock("x");
        panel.Children.Add(tb);
        TextElement.SetInverse(panel, true);
        host.ShowRoot(panel);
        host.RunFrame();

        Assert.False(TextElement.GetInverse(tb)); // flows like Background: no ambient inheritance
        Assert.Equal(TextAttributes.None, TextElement.ComposeAttributes(tb).Flags);
    }

    [Fact] // TA10 — pair coherence: every tier dictionary carries BOTH cue keys (the judge's lint, at test-time cost)
    public void TA10_CuePair_PresentInEveryTierDictionary()
    {
        var theme = Cursorial.UI.Themes.CursorialTheme.CreateDefault();

        var carriers = theme.ThemeDictionaries
            .Where(kv => kv.Value.ContainsKey(Cursorial.UI.Themes.ThemeKeys.InteractiveCueInverse)
                      || kv.Value.ContainsKey(Cursorial.UI.Themes.ThemeKeys.InteractiveCueWeight))
            .ToList();

        Assert.NotEmpty(carriers);
        Assert.All(carriers, kv =>
        {
            Assert.True(kv.Value.ContainsKey(Cursorial.UI.Themes.ThemeKeys.InteractiveCueInverse),
                $"tier {kv.Key} carries CueWeight but not CueInverse");
            Assert.True(kv.Value.ContainsKey(Cursorial.UI.Themes.ThemeKeys.InteractiveCueWeight),
                $"tier {kv.Key} carries CueInverse but not CueWeight");
            Assert.IsType<bool>(kv.Value[Cursorial.UI.Themes.ThemeKeys.InteractiveCueInverse]);
            Assert.IsType<TextWeight>(kv.Value[Cursorial.UI.Themes.ThemeKeys.InteractiveCueWeight]);
        });

        // Pin the §2.3 four-row value table verbatim (audit fix — the presence lint above alone let a
        // deleted required pair or a flipped Faint pass): one cue vocabulary per tier, Inverse only
        // where brushes can't speak, Faint only at (Dark|Light, Ansi16).
        void AssertCue(ThemeVariantKey key, bool inverse, TextWeight weight)
        {
            var dict = theme.ThemeDictionaries[key];
            Assert.Equal(inverse, dict[Cursorial.UI.Themes.ThemeKeys.InteractiveCueInverse]);
            Assert.Equal(weight, dict[Cursorial.UI.Themes.ThemeKeys.InteractiveCueWeight]);
        }

        AssertCue(new ThemeVariantKey(null, ColorDepth.NoColor), inverse: true,  weight: TextWeight.Normal); // NoColor: faint
        AssertCue(new ThemeVariantKey(null, ColorDepth.Ansi16),  inverse: false, weight: TextWeight.Bold); // CD8 color floor
        AssertCue(new ThemeVariantKey(ThemeBase.Dark,  ColorDepth.Ansi16), inverse: false, weight: TextWeight.Bold);  // 16-color = Bold
        AssertCue(new ThemeVariantKey(ThemeBase.Light, ColorDepth.Ansi16), inverse: false, weight: TextWeight.Bold);
        AssertCue(new ThemeVariantKey(ThemeBase.Dark,  ColorDepth.Ansi256), inverse: false, weight: TextWeight.Normal); // RGB: brushes are the cue
    }

    [Fact] // TA11 — the landed P9.3b (the composability proof): NoColor focus-row = selection Inverse + focus Bold, composed
    public void TA11_NoColor_ListFocusRow_InverseAndBold_ComposeWithSelection()
    {
        using var host = UIHeadlessHost.Create();
        host.Application.RequestedThemeBase = ThemeBase.Dark;
        host.Application.RequestedColorTier = ColorDepth.NoColor;

        var list = new ListBox { ItemsSource = new[] { "alpha", "beta" } };
        var root = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        root.Children.Add(list);
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());
        Assert.Equal(ColorDepth.NoColor, host.Application.ActualThemeVariant.Tier);

        // Select the first row and give it the keyboard focus-visible cue: :selected (Inverse via the
        // selection rule) AND :focus-visible (Inverse + Bold via the focus-cue rule) — the two rules
        // COMPOSE per axis: Inverse from either, Bold from the focus rule's independent weight axis.
        // Pre-decomposition this was impossible — the combined-flags rule fought the selection rule
        // over one property (the deferred P9.3b).
        var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0)!;
        container.IsSelected = true;
        Assert.True(container.Focus(FocusNavigationMethod.Tab)); // Tab modality ⇒ :focus-visible
        Assert.True(host.RunUntilIdle());

        Assert.True(container.IsSelected);
        Assert.True(TextElement.GetInverse(container));
        Assert.Equal(TextWeight.Bold, TextElement.GetTextWeight(container));

        // And the cells prove delivery end-to-end: the row's text carries Inverse|Bold.
        var (col, row) = container.TranslateToWindow(1, 0); // past the 1-cell row padding
        var style = host.GetCell(col, row).Style;
        Assert.True((style.Attributes & TextAttributes.Inverse) != 0);
        Assert.True((style.Attributes & TextAttributes.Bold) != 0);
    }

    [Fact] // TA12 — a conditional rule pierces a Template-lane forward on a generated leaf (audit fix: forwards install at Template, not LocalValue)
    public void TA12_ConditionalRule_PiercesForward_OnGeneratedLeaf()
    {
        using var host = UIHeadlessHost.Create();
        var button = new Button { Content = "OK", Focusable = false };
        button.Classes.Add("host");
        TextElement.SetTextWeight(button, TextWeight.Bold); // the control value the presenter forwards down
        host.ShowRoot(button);
        Assert.True(host.RunUntilIdle());

        var leaf = FindLeaf(button)!;
        Assert.Equal(TextWeight.Bold, TextElement.GetTextWeight(leaf)); // the forward delivers, at the Template lane
        Assert.Equal(BindingPriority.Template, leaf.GetValueSource(TextElement.TextWeightProperty).Priority);

        // A CONDITIONAL rule targeting the generated leaf pierces the Template-lane forward
        // (StyleTrigger 50 > Template 75, PD26) — the composability the LocalValue install occluded.
        // Class-only selector: the button's generated leaf is an AccessTextPresenter (RecognizesAccessKey).
        host.Application.Styles.Add(new Style(".tweak")
            .Set(TextElement.TextWeightProperty, TextWeight.Faint));
        leaf.Classes.Add("tweak");
        host.RunFrame();

        Assert.Equal(TextWeight.Faint, TextElement.GetTextWeight(leaf));
        Assert.Equal(BindingPriority.StyleTrigger, leaf.GetValueSource(TextElement.TextWeightProperty).Priority);

        leaf.Classes.Remove("tweak"); // retract → the forward resurfaces (clean)
        host.RunFrame();
        Assert.Equal(TextWeight.Bold, TextElement.GetTextWeight(leaf));
    }

    [Fact] // TA13 — the borrowed-Icon Inverse forward is disposed on content swap (final-audit leak fix)
    public void TA13_BorrowedIconForward_TornDownOnUnhost()
    {
        using var host = UIHeadlessHost.Create();
        var button = new Button { Content = "OK", Focusable = false };
        TextElement.SetInverse(button, true);
        var iconA = new Icon { Text = "A" };
        button.Content = iconA;
        host.ShowRoot(button);
        Assert.True(host.RunUntilIdle());

        // Hosted: the presenter installed the Inverse forward on iconA (Template lane), so it tracks the control.
        Assert.True(TextElement.GetInverse(iconA));
        Assert.Equal(BindingPriority.Template, iconA.GetValueSource(TextElement.InverseProperty).Priority);

        // Swap content → the presenter disposes iconA's forward (else its source-anchored observer leaks it).
        button.Content = new Icon { Text = "B" };
        Assert.True(host.RunUntilIdle());

        // iconA's forward is gone: its Inverse falls to Default and no longer tracks the control.
        Assert.Equal(BindingPriority.Default, iconA.GetValueSource(TextElement.InverseProperty).Priority);
        TextElement.SetInverse(button, false);
        button.Content = new Icon { Text = "B2" }; // re-swap; iconA must stay untracked (no stale push)
        Assert.True(host.RunUntilIdle());
        Assert.False(TextElement.GetInverse(iconA));
    }

    // ─────────────── paint-time composition: the caps-nocolor Inverse must ADD, never REPLACE ───────────────

    /// <summary>
    /// The Kitty preset with color negotiated AWAY. The TextPresenter reads its <c>noColor</c> flag from
    /// the RENDER capabilities (<c>context.Capabilities.Color.Depth</c>), so a <c>RequestedColorTier</c>
    /// override alone would arm the theme rule without reaching the painter — the capability snapshot is
    /// the only lever that moves both.
    /// </summary>
    private static Cursorial.Terminal.TerminalCapabilities NoColorTerminal { get; } =
        HeadlessCapabilities.KittyTruecolor with
        {
            Output = HeadlessCapabilities.KittyTruecolor.Output with
            {
                Color = ColorCapabilities.None with { Depth = ColorDepth.NoColor },
            },
        };

    /// <summary>The same, plus OSC 66 text sizing — the lane where <c>EditingSource.PaintsAsCells</c> is false.</summary>
    private static Cursorial.Terminal.TerminalCapabilities NoColorSizedTerminal { get; } =
        NoColorTerminal with
        {
            Output = NoColorTerminal.Output with
            {
                TextSizing = new TextSizingCapabilities(Width: true, Scale: true),
            },
        };

    private static (UIHeadlessHost Host, TextBox Box) NoColorBoldBox(
        Cursorial.Terminal.TerminalCapabilities capabilities, TextSizing? sizing = null, bool focus = false)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Cursorial.Rendering.Size(30, 6),
            Capabilities = capabilities,
        });

        var box = new TextBox
        {
            Text = "hello",
            Width = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        TextElement.SetTextWeight(box, TextWeight.Bold); // the OTHER axis — the one the fold must keep

        if (sizing is {} value)
            TextElement.SetSizing(box, value);

        host.ShowRoot(box);
        host.RunUntilIdle();

        if (focus)
        {
            box.Focus();
            host.RunUntilIdle();
        }

        return (host, box);
    }

    // The presenter is where the paint-time fold happens; the theme forwards both axes onto it.
    private static TextPresenter Presenter(TextBox box)
    {
        var presenter = FindDescendant<TextPresenter>(box);
        Assert.NotNull(presenter);
        return presenter!;
    }

    [Fact] // TA14 — plain-text lane: the caps-nocolor Inverse composes with a resting weight axis
    public void TA14_NoColorInverse_KeepsOtherAxes_PlainText()
    {
        var (host, box) = NoColorBoldBox(NoColorTerminal);
        using var _ = host;

        // Reachability first (the suite renders through the real theme — "we set no X" proves nothing):
        // the theme's :is(TextBox) NoColor rule ARMED, and both axes reached the presenter's fold.
        Assert.Equal(ColorDepth.NoColor, host.Application.ActualThemeVariant.Tier);
        var presenter = Presenter(box);
        Assert.True(TextElement.GetInverse(presenter));
        Assert.Equal(TextAttributes.Bold | TextAttributes.Inverse, TextElement.ComposeAttributes(presenter).Flags);

        var origin = box.TranslateToWindow(0, 0);
        var cell = host.GetCell(origin.Column + 1, origin.Row); // first text cell (past the 1-col padding)

        Assert.Equal("h", cell.Grapheme); // the cell is INKED — not an untouched blank
        Assert.True((cell.Style.Attributes & TextAttributes.Inverse) != 0);
        Assert.True((cell.Style.Attributes & TextAttributes.Bold) != 0); // the fold's other axis SURVIVES the paint
    }

    [Fact] // TA15 — the clobber's tell: selected and unselected runs must agree on every non-Inverse bit
    public void TA15_NoColorInverse_SelectedAndUnselectedRuns_AgreeOnNonInverseBits()
    {
        var (host, box) = NoColorBoldBox(NoColorTerminal, focus: true);
        using var _ = host;

        box.SelectionStart = 0;
        box.SelectionLength = 2; // "he" selected, "llo" not — two runs, one frame
        Assert.True(host.RunUntilIdle());

        var origin = box.TranslateToWindow(0, 0);
        var selected = host.GetCell(origin.Column + 1, origin.Row);
        var unselected = host.GetCell(origin.Column + 3, origin.Row);

        Assert.Equal("h", selected.Grapheme);   // both cells are inked, from the two different runs
        Assert.Equal("l", unselected.Grapheme);

        var nonInverse = ~TextAttributes.Inverse;
        Assert.Equal(unselected.Style.Attributes & nonInverse, selected.Style.Attributes & nonInverse);
        Assert.True((selected.Style.Attributes & TextAttributes.Bold) != 0); // and it is the WEIGHT they agree on
        Assert.NotEqual(selected.Style.Attributes & TextAttributes.Inverse,
                        unselected.Style.Attributes & TextAttributes.Inverse); // selection still reads distinctly
    }

    [Fact] // TA16 — sized lane (PaintsAsCells == false): the fragment's SGR backdrop keeps the other axes
    public void TA16_NoColorInverse_KeepsOtherAxes_SizedText()
    {
        var (host, box) = NoColorBoldBox(NoColorSizedTerminal, sizing: new TextSizing(Scale: 2));
        using var _ = host;

        Assert.Equal(ColorDepth.NoColor, host.Application.ActualThemeVariant.Tier);
        var presenter = Presenter(box);
        Assert.True(TextElement.GetInverse(presenter));
        Assert.False(presenter.EditingSource.PaintsAsCells); // the sized lane, not the cell walk
        Assert.Equal(TextAttributes.Bold | TextAttributes.Inverse, TextElement.ComposeAttributes(presenter).Flags);

        // A sized piece paints as an OSC 66 fragment: the run's style IS its SGR backdrop (the cell
        // grid under a fragment is untouched by construction), so that is where the paint is observed.
        CellStyle? backdrop = null;
        foreach (var entry in host.FrameBuffer.Fragments)
        {
            if (entry.Value.Fragment is SizedTextFragment sized)
                backdrop = sized.Style;
        }

        Assert.NotNull(backdrop);
        Assert.True((backdrop!.Value.Attributes & TextAttributes.Inverse) != 0);
        Assert.True((backdrop.Value.Attributes & TextAttributes.Bold) != 0);
    }

    [Fact] // TA17 — ResolvedTextAttributes.Apply: adding the underline SHAPE must not drop the flags
    public void TA17_Apply_UnderlineShape_KeepsComposedAttributes()
    {
        var glyph = new GlyphPresenter
        {
            Glyph = "*",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        TextElement.SetUnderline(glyph, UnderlineStyle.Curly);
        TextElement.SetTextWeight(glyph, TextWeight.Bold);

        using var host = UIHeadlessHost.Create();
        host.ShowRoot(glyph);
        Assert.True(host.RunUntilIdle());

        var resolved = TextElement.ComposeAttributes(glyph);
        Assert.Equal(TextAttributes.Bold | TextAttributes.Underline, resolved.Flags); // the fold is fine —

        var applied = resolved.Apply(CellStyle.Default);                              // — Apply is where it went
        Assert.Equal(TextAttributes.Bold | TextAttributes.Underline, applied.Attributes);
        Assert.Equal(UnderlineStyle.Curly, applied.UnderlineStyle);

        // And end to end: GlyphPresenter's ONLY style source is that Apply call.
        var cell = host.GetCell(0, 0);
        Assert.Equal("*", cell.Grapheme);
        Assert.Equal(TextAttributes.Bold | TextAttributes.Underline, cell.Style.Attributes);
        Assert.Equal(UnderlineStyle.Curly, cell.Style.UnderlineStyle);
    }

    private static T? FindDescendant<T>(UIElement root) where T : UIElement
    {
        for (var i = 0; i < root.VisualChildrenCount; i++)
        {
            var child = root.GetVisualChild(i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is {} found)
                return found;
        }

        return null;
    }

    private static UIElement? FindLeaf(UIElement root)
    {
        // Depth-first search for the generated presentation leaf (AccessTextPresenter or TextBlock).
        for (var i = 0; i < root.VisualChildrenCount; i++)
        {
            var child = root.GetVisualChild(i);
            if (child is AccessTextPresenter or TextBlock)
                return child;
            if (FindLeaf(child) is { } found)
                return found;
        }

        return null;
    }
}
