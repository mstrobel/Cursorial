using Cursorial.Drawing.Media;
using Cursorial.Output;

namespace Cursorial.UI.Themes;

/// <summary>
/// The built-in default theme (design doc §11.8): a sealed, process-shared, code-first
/// <see cref="ResourceDictionary"/> that is always the final lookup hop. S7 owns the
/// <b>infrastructure</b> (the dictionary, variant layout, tier-key rules, the <see cref="ThemeKeys"/>
/// naming + palette spine); S8 authors per-control templates/styles into this structure under
/// <c>Theme.*</c> names at C0+.
/// </summary>
/// <remarks>
/// The palette is populated per the §11.2 tier rules: no color-bearing value lives in a base-only
/// <c>(B,·)</c> key (so the catch-all stays NoColor-renderable); RGB brushes live at
/// <c>(B,Ansi256)</c> (served at Truecolor via descent), hand-picked palette brushes + ASCII-glyph
/// pens at <c>(B,Ansi16)</c>, and attribute-only values at <c>(·,NoColor)</c>. The quantizer makes
/// RGB safe; tier dictionaries make every tier good.
/// </remarks>
public static class CursorialTheme
{
    /// <summary>The 19 cell-faithful fill/foreground role tokens (design doc §11.8a) — the palette spine.</summary>
    /// <remarks>Declared BEFORE <see cref="BuiltInDictionary"/>: static fields initialize in textual order, and
    /// <see cref="CreateSealed"/> reads this array during <see cref="Populate"/>'s NoColor pass.</remarks>
    private static readonly string[] RoleTokenKeys =
    {
        ThemeKeys.WindowBackground, ThemeKeys.SurfaceBrush, ThemeKeys.PanelBrush, ThemeKeys.WellBrush,
        ThemeKeys.SelectionBrush, ThemeKeys.SelectionInactiveBrush, ThemeKeys.AlternateRowBrush,
        ThemeKeys.HoverBrush, ThemeKeys.TextBrush, ThemeKeys.TextDimBrush,
        ThemeKeys.MutedBrush, ThemeKeys.FaintBrush, ThemeKeys.DisabledBackgroundBrush,
        ThemeKeys.DisabledForegroundBrush, ThemeKeys.AccentBrush, ThemeKeys.Accent2Brush,ThemeKeys.AccentDarkBrush,
        ThemeKeys.OnAccentBrush, ThemeKeys.GreenBrush, ThemeKeys.AmberBrush, ThemeKeys.RedBrush,
        ThemeKeys.PurpleBrush, ThemeKeys.StatusBarBackground, ThemeKeys.StatusBarAltBackground,
        ThemeKeys.StatusBarAltForeground,
        ThemeKeys.CoolBrush, ThemeKeys.Cool2Brush, ThemeKeys.CoolDarkBrush, ThemeKeys.CoolInverseBrush,
        ThemeKeys.WarningBrush, ThemeKeys.Warning2Brush, ThemeKeys.WarningDarkBrush, ThemeKeys.WarningInverseBrush,
        ThemeKeys.SuccessBrush, ThemeKeys.Success2Brush, ThemeKeys.SuccessDarkBrush, ThemeKeys.SuccessInverseBrush,
        ThemeKeys.DangerBrush, ThemeKeys.Danger2Brush, ThemeKeys.DangerDarkBrush, ThemeKeys.DangerInverseBrush
    };

    private static readonly ResourceDictionary BuiltInDictionary = CreateSealed();

    /// <summary>The sealed, process-shared default dictionary — always the final lookup hop (design doc §11.8).</summary>
    public static ResourceDictionary BuiltIn => BuiltInDictionary;

    /// <summary>An unsealed structural copy for mutation (design doc §11.8): fresh shells, shared value instances; assignable to <c>UIApplication.Theme</c>.</summary>
    public static ResourceDictionary CreateDefault()
    {
        var dict = new ResourceDictionary();
        Populate(dict);
        return dict;
    }

    private static ResourceDictionary CreateSealed()
    {
        var dict = new ResourceDictionary();
        Populate(dict);
        dict.Seal();

        // Warm the process-shared theme-styles matcher index once, here in the static initializer,
        // before any UIApplication can gather against it. The BuiltIn dictionary is shared across every
        // host (and xUnit runs test classes in parallel); the styling engine only ever gathers it at
        // (Theme, 0), and a sealed dictionary's Styles never mutate — so this single build is the only
        // build, and every later GetOrBuildIndex returns the cached instance with no racing rebuild.
        dict.Styles?.GetOrBuildIndex(StyleLayer.Theme, 0);
        return dict;
    }

    private static void Populate(ResourceDictionary dict)
    {
        // Each base × tier sub-dictionary carries the color-bearing palette spine. No color-bearing
        // value goes into a base-only (B,·) key (the NoColor-safety contract, CD8/C96).
        AddTierPalette(dict, ThemeBase.Dark);
        AddTierPalette(dict, ThemeBase.Light);

        // Themeable glyph triples (design doc §12.7, spec line 660): the true-ASCII BASE — they render
        // identically on every terminal (zero ambiguous-width risk). CursorialThemeStyles opts the
        // check/radio marks UP to colored Unicode (✓ ▪ ●) under .caps-unicode via the ToggleGlyph.Glyphs
        // attached property (the genuinely capability-gated swap); a caps-ascii terminal keeps these. Keyed
        // at the dictionary top level (capability-driven, not color-tier-driven).
        dict[ThemeKeys.CheckBoxGlyphs] = new GlyphSetCarrier("[ ]", "[x]", "[-]");
        // The indeterminate "(-)" is distinct from unchecked "( )" so an explicitly-set IsChecked=null
        // radio is visually distinguishable (doc §12.7 — only an explicit set reaches indeterminate).
        dict[ThemeKeys.RadioGlyphs] = new GlyphSetCarrier("( )", "(*)", "(-)");
        dict[ThemeKeys.ScrollArrowGlyphs] = new GlyphSetCarrier("^", "v");

        // The per-control override keys (style-guide KEYS; design doc §11.4a): variant-agnostic LIVE
        // ALIASES of the palette role tokens, so a control template references its own key while one
        // role-token brush backs every consumer — and an app re-keys a single control's brush at a
        // nearer chain scope to re-skin just that control. Registered BEFORE the control themes (which
        // reference these keys) — order is irrelevant for resolution but keeps the read top-down.
        AddControlKeyAliases(dict);

        // The Type-keyed control themes (S8 content authored into S7's structure, CD30): selector-less
        // Styles rooted at '^', armed at ControlTheme(0). Their templates + pseudo-class child rules
        // ship the default look for every P5 control.
        ControlThemes.Populate(dict);

        // The theme-styles channel (design doc §11.8 #3): selector styles the theme ships, consumed
        // from this dictionary's Styles slot and armed at Theme(2) — below App, so app styles always
        // win. Requirement 6's access-key cue lives here as a single global rule (doc §7.8/§11.8): the
        // AccessKeyManager stamps InteractionState.AccessKeyCue (the :access-keys pseudo-class) on the
        // active scope/window root, and this descendant rule binds that ancestor bit to every
        // AccessTextPresenter underneath, flipping its ShowUnderline (AffectsRender). It works in both
        // cue modes with zero per-control wiring — permanent underscores in AlwaysVisible (the
        // non-capable terminal fallback) and Alt-toggled on a capable terminal.
        dict.Styles = new Styles
        {
            CursorialThemeStyles.AccessKeyCue(),
            CursorialThemeStyles.CapsUnicodeCheckBoxGlyphs(),
            CursorialThemeStyles.CapsUnicodeRadioGlyphs(),
            // The caps-nocolor interactive-state layer: colors collapse to Default under NoColor, so the
            // button family's focus/pressed/default flip Inverse (reverse-video, honored by the Border fill +
            // content text) and disabled dims to Faint.
            CursorialThemeStyles.CapsNoColorInteractiveInverse(),
            CursorialThemeStyles.CapsNoColorDisabledFaint(),
            CursorialThemeStyles.CapsNoColorSelectionInverse(),
            CursorialThemeStyles.CapsNoColorBorderStyle(),
            CursorialThemeStyles.CapsNoColorBorderPenStyle(),

            // All blending should be disabled for Ansi16, so make sure all borders and panels occlude.
            CursorialThemeStyles.CapsAnsi16BorderStyle(),
            
            CursorialThemeStyles.MenuSeparatorStyle(),

            CursorialThemeStyles.AccentStyle(),
            CursorialThemeStyles.InfoStyle(),
            CursorialThemeStyles.CoolStyle(),
            CursorialThemeStyles.WarningStyle(),
            CursorialThemeStyles.DangerStyle(),
            CursorialThemeStyles.SuccessStyle()
        };

        // (·,NoColor): every fill/foreground role token resolves to Colors.Default — no stranded RGB. State
        // distinction on monochrome rides a caps-nocolor TextAttributes layer (Inverse for focus/pressed/
        // default, Faint for disabled) — see CursorialThemeStyles; the inherited TextElement.TextAttributes is
        // honored by the Border fill + the content text (design doc §11.8a; spec §2/Q5), not by the palette.
        var noColor = new ResourceDictionary();
        var defaultBrush = new SolidColorBrush(Colors.Default);
        foreach (var key in RoleTokenKeys)
            noColor[key] = defaultBrush;
        // Opt-in chrome survives as ASCII pens; modal-dim + access-key underline stay Default (the attribute
        // — Underline — carries the cue).
        noColor[ThemeKeys.BorderPen] = Pens.Ascii;
        noColor[ThemeKeys.MenuBorderPen] = Pens.Ascii;
        noColor[ThemeKeys.TabControlBorderPen] = Pens.Ascii;
        noColor[ThemeKeys.ToolTipBorderPen] = Pens.Ascii;
        noColor[ThemeKeys.SeparatorPen] = Pens.Ascii;
        noColor[ThemeKeys.MenuSeparatorPen] = Pens.Ascii;
        noColor[ThemeKeys.FocusPen] = Pens.Ascii.WithWeight(StrokeWeight.Heavy);
        noColor[ThemeKeys.TabUnderlinePen] = Pens.Ascii.WithWeight(StrokeWeight.Heavy);
        // No color to distinguish filled vs empty — both are an ASCII heavy rail; the thumb + reverse-video focus carry it.
        noColor[ThemeKeys.SliderFilledPen] = Pens.Ascii.WithWeight(StrokeWeight.Heavy);
        noColor[ThemeKeys.SliderTrackPen] = Pens.Ascii.WithWeight(StrokeWeight.Heavy);
        noColor[ThemeKeys.ObscuredOverlayBrush] = defaultBrush;
        noColor[ThemeKeys.AccessKeyIndicatorBrush] = defaultBrush;
        // Ribbon: no fill under monochrome — the active tab is marked by the ASCII-heavy underline (TabUnderlinePen).
        noColor[ThemeKeys.RibbonTabStripBrush] = defaultBrush;
        noColor[ThemeKeys.RibbonTabActiveBrush] = defaultBrush;
        noColor[ThemeKeys.KeyTipBrush] = defaultBrush;
        noColor[ThemeKeys.KeyTipMatchedBrush] = defaultBrush;
        // A contextual tab has no purple to lean on under mono; its only cue is the underline — authored ASCII-heavy
        // (visually identical to the accent one here) since RibbonTabActiveBrush = defaultBrush leaves nothing else.
        noColor[ThemeKeys.RibbonContextualFillBrush] = defaultBrush;
        noColor[ThemeKeys.RibbonContextualUnderlinePen] = Pens.Ascii.WithWeight(StrokeWeight.Heavy);
        // Reverse-video is the monochrome interactive cue (the palette fill collapsed to Default). This lives ONLY
        // at the NoColor floor; the None counterpart at the Ansi16 wildcard floor (below) keeps it from bleeding up.
        noColor[ThemeKeys.InteractiveInverseAttributes] = TextAttributes.Inverse;
        dict.ThemeDictionaries[new ThemeVariantKey(null, ColorDepth.NoColor)] = noColor;

        // The color-tier None floor for InteractiveInverseAttributes. The CD8 probe DESCENDS tiers
        // (Truecolor→Ansi256→Ansi16→NoColor), so a bare NoColor=Inverse would resolve for every color tier too.
        // Ansi16 is the lowest color tier, so every color tier's descent reaches this None before the NoColor
        // Inverse below it, while a true NoColor variant (whose descent is NoColor-only) never sees it.
        var ansi16Floor = new ResourceDictionary { [ThemeKeys.InteractiveInverseAttributes] = TextAttributes.None };
        dict.ThemeDictionaries[new ThemeVariantKey(null, ColorDepth.Ansi16)] = ansi16Floor;
    }

    // The per-control override keys (style-guide KEYS), each a live ResourceReference alias of a palette
    // role token (design doc §11.4a). Variant-agnostic top-level entries — the alias resolves the same on
    // every (base × tier) because it chases the role token, which IS per-variant. Behavior-preserving: each
    // key aliases exactly the role token its control theme already consumed, so wiring the themes to these
    // keys is a pure indirection (the render output is byte-identical). An app overrides a single control by
    // re-keying its per-control key at a nearer scope.
    private static void AddControlKeyAliases(ResourceDictionary dict)
    {
        void Alias(string key, string target)
        {
            if (dict.ContainsKey(key) is false)
                dict[key] = new ResourceReference(target);
        }

        // Base / shared — the 3 spec-named aliases whose guide name differs from our role token.
        Alias(ThemeKeys.AccentForegroundBrush, ThemeKeys.OnAccentBrush);
        Alias(ThemeKeys.SelectionActiveBrush, ThemeKeys.SelectionBrush);
        Alias(ThemeKeys.PanelBackgroundBrush, ThemeKeys.PanelBrush);
        Alias(ThemeKeys.ElevationPopup, ThemeKeys.ElevationWindow);
        Alias(ThemeKeys.ElevationDialog, ThemeKeys.ElevationWindow);

        // Button (Button / RepeatButton / ToggleButton).
        Alias(ThemeKeys.ButtonBackgroundNormal, ThemeKeys.SurfaceBrush);
        Alias(ThemeKeys.ButtonForegroundNormal, ThemeKeys.TextBrush);
        Alias(ThemeKeys.ButtonBackgroundHover, ThemeKeys.AccentBrush);
        Alias(ThemeKeys.ButtonForegroundHover, ThemeKeys.OnAccentBrush);
        Alias(ThemeKeys.ButtonBackgroundFocus, ThemeKeys.TextBrush);
        Alias(ThemeKeys.ButtonForegroundFocus, ThemeKeys.ElevationRaised);
        Alias(ThemeKeys.ButtonBackgroundPressed, ThemeKeys.AccentDarkBrush);
        Alias(ThemeKeys.ButtonForegroundPressed, ThemeKeys.OnAccentBrush);
        Alias(ThemeKeys.ButtonForegroundDisabled, ThemeKeys.DisabledForegroundBrush);
        Alias(ThemeKeys.SplitButtonDropZoneBrush, ThemeKeys.AccentBrush);

        // ToggleSwitch / CheckBox / RadioButton.
        Alias(ThemeKeys.ToggleForegroundNormal, ThemeKeys.TextBrush);
        Alias(ThemeKeys.ToggleGlyphChecked, ThemeKeys.GreenBrush);
        Alias(ThemeKeys.ToggleGlyphIndeterminate, ThemeKeys.AmberBrush);
        Alias(ThemeKeys.RadioGlyphChecked, ThemeKeys.AccentBrush);
        Alias(ThemeKeys.ToggleForegroundDisabled, ThemeKeys.DisabledForegroundBrush);

        // Input (TextBox, editable ComboBox).
        Alias(ThemeKeys.InputForegroundNormal, ThemeKeys.TextBrush);
        Alias(ThemeKeys.InputBackgroundNormal, ThemeKeys.SurfaceBrush);
        Alias(ThemeKeys.InputBackgroundHover, ThemeKeys.HoverBrush);
        Alias(ThemeKeys.InputForegroundHover, ThemeKeys.TextBrush);
        Alias(ThemeKeys.InputBackgroundFocus, ThemeKeys.WellBrush);
        Alias(ThemeKeys.InputSelectionActive, ThemeKeys.SelectionBrush);
        Alias(ThemeKeys.InputSelectionInactive, ThemeKeys.SelectionInactiveBrush);
        Alias(ThemeKeys.InputForegroundDisabled, ThemeKeys.DisabledForegroundBrush);
        Alias(ThemeKeys.InputBackgroundDisabled, ThemeKeys.DisabledBackgroundBrush);

        // ListItem (ListBox / ComboBox drop-down item).
        Alias(ThemeKeys.ListItemForegroundNormal, ThemeKeys.TextBrush);
        Alias(ThemeKeys.ListItemBackgroundNormal, ThemeKeys.PanelBrush);
        Alias(ThemeKeys.ListItemForegroundHover, ThemeKeys.TextBrush);
        Alias(ThemeKeys.ListItemBackgroundHover, ThemeKeys.HoverBrush);
        Alias(ThemeKeys.ListItemBackgroundSelected, ThemeKeys.SelectionBrush);
        // Gallery `.item.rev { background: --text; color: --panel }` (final style guide): a list item sits on the
        // PanelBrush surface, so its reverse-video focus ink is --panel (not the universal --bg). #103.
        Alias(ThemeKeys.ListItemForegroundFocus, ThemeKeys.PanelBrush);
        Alias(ThemeKeys.ListItemBackgroundFocus, ThemeKeys.TextBrush);
        Alias(ThemeKeys.ListItemForegroundDisabled, ThemeKeys.MutedBrush);

        // TreeViewItem (same item-on-panel reverse-video ink, #103).
        Alias(ThemeKeys.TreeItemForegroundNormal, ThemeKeys.TextBrush);
        Alias(ThemeKeys.TreeItemBackgroundSelected, ThemeKeys.SelectionBrush);
        Alias(ThemeKeys.TreeItemForegroundFocus, ThemeKeys.PanelBrush);
        Alias(ThemeKeys.TreeItemBackgroundFocus, ThemeKeys.TextBrush);
        Alias(ThemeKeys.TreeItemForegroundDisabled, ThemeKeys.MutedBrush);

        // Menu (MenuBar / MenuItem / ContextMenu).
        Alias(ThemeKeys.MenuForegroundNormal, ThemeKeys.TextBrush);
        Alias(ThemeKeys.MenuBarBackground, ThemeKeys.SurfaceBrush);
        Alias(ThemeKeys.MenuBackgroundHover, ThemeKeys.HoverBrush);
        Alias(ThemeKeys.MenuBackgroundHighlighted, ThemeKeys.SelectionBrush);
        Alias(ThemeKeys.MenuAcceleratorForeground, ThemeKeys.MutedBrush);
        Alias(ThemeKeys.MenuAcceleratorHoverForeground, ThemeKeys.TextDimBrush);
        Alias(ThemeKeys.MenuForegroundDisabled, ThemeKeys.MutedBrush);

        // TabItem — gallery: inactive ink = --text-dim, active = --surface fill + --text ink + an --accent
        // underline bar (#103, was a --sel fill with --text ink on every tab).
        Alias(ThemeKeys.TabForegroundNormal, ThemeKeys.TextDimBrush);
        Alias(ThemeKeys.TabForegroundSelected, ThemeKeys.TextBrush);
        Alias(ThemeKeys.TabForegroundHover, ThemeKeys.TextBrush);
        Alias(ThemeKeys.TabBackgroundHover, ThemeKeys.HoverBrush);
        Alias(ThemeKeys.TabForegroundFocused, ThemeKeys.TextBrush);
        Alias(ThemeKeys.TabBackgroundFocused, ThemeKeys.HoverBrush);
        Alias(ThemeKeys.TabBackgroundSelected, ThemeKeys.SurfaceBrush);
        Alias(ThemeKeys.TabForegroundDisabled, ThemeKeys.MutedBrush);
        Alias(ThemeKeys.TabControlBorderPen, ThemeKeys.BorderPen);
        // TabUnderlinePen is a Pen (not a brush alias) — defined per-variant in AddTierPalette.

        // ProgressBar — gallery: empty track = --faint (#103, was --well).
        Alias(ThemeKeys.ProgressFillNormal, ThemeKeys.GreenBrush);
        Alias(ThemeKeys.ProgressFillIndeterminate, ThemeKeys.AccentBrush);
        Alias(ThemeKeys.ProgressTrackBrush, ThemeKeys.FaintBrush);

        // Calendar (day cells + Year/Decade cells; DatePicker).
        Alias(ThemeKeys.CalendarDayForeground, ThemeKeys.TextBrush);
        Alias(ThemeKeys.CalendarDayInactiveForeground, ThemeKeys.MutedBrush);
        Alias(ThemeKeys.CalendarDayBackgroundHover, ThemeKeys.HoverBrush);
        Alias(ThemeKeys.CalendarDayTodayForeground, ThemeKeys.AccentBrush);
        Alias(ThemeKeys.CalendarDayBackgroundSelected, ThemeKeys.SelectionBrush);
        Alias(ThemeKeys.CalendarDayForegroundSelected, ThemeKeys.TextBrush);
        // Gallery `.cal .focus { background: --text; color: --panel }` (#103, was --bg).
        Alias(ThemeKeys.CalendarDayForegroundFocus, ThemeKeys.PanelBrush);
        Alias(ThemeKeys.CalendarDayBackgroundFocus, ThemeKeys.TextBrush);
        Alias(ThemeKeys.CalendarDayForegroundDisabled, ThemeKeys.MutedBrush);

        Alias(ThemeKeys.WindowTitleBarBackground, ThemeKeys.AccentBrush);
        Alias(ThemeKeys.WindowTitleBarActiveBackground, ThemeKeys.AccentBrush);
        Alias(ThemeKeys.WindowTitleBarForeground, ThemeKeys.OnAccentBrush);
        Alias(ThemeKeys.WindowTitleBarActiveForeground, ThemeKeys.OnAccentBrush);
        
        Alias(ThemeKeys.MenuBorderPen, ThemeKeys.BorderPen);
        Alias(ThemeKeys.ToolTipBorderPen, ThemeKeys.BorderPen);
        Alias(ThemeKeys.TabUnderlinePen, ThemeKeys.BorderPen);
        
        Alias(ThemeKeys.Success2Brush, ThemeKeys.SuccessBrush);
        Alias(ThemeKeys.SuccessDarkBrush, ThemeKeys.SuccessInverseBrush);
        Alias(ThemeKeys.Danger2Brush, ThemeKeys.DangerBrush);
        Alias(ThemeKeys.DangerDarkBrush, ThemeKeys.DangerInverseBrush);
        Alias(ThemeKeys.Cool2Brush, ThemeKeys.CoolBrush);
        Alias(ThemeKeys.CoolDarkBrush, ThemeKeys.CoolInverseBrush);
        Alias(ThemeKeys.Warning2Brush, ThemeKeys.WarningBrush);
        Alias(ThemeKeys.WarningDarkBrush, ThemeKeys.WarningInverseBrush);
        Alias(ThemeKeys.Info2Brush, ThemeKeys.InfoBrush);
        Alias(ThemeKeys.InfoDarkBrush, ThemeKeys.InfoInverseBrush);
    }

    private static void AddTierPalette(ResourceDictionary dict, ThemeBase @base)
    {
        var dark = @base == ThemeBase.Dark;

        // (B,Ansi256): RGB role tokens — Tokyo-Night, verbatim from the default-theme gallery; served at
        // Truecolor too (descent never ascends; CD8). The cell-faithful spine (design doc §11.8a) — fill +
        // foreground tokens; the two pens are opt-in chrome, not spine members.
        var rgb = new ResourceDictionary { [ThemeKeys.InteractiveInverseAttributes] = TextAttributes.None };
        rgb[ThemeKeys.ElevationWell] = new SolidColorBrush(dark ? Color.FromHex("#0d0f19") : Color.FromHex("#ffffff"));
        rgb[ThemeKeys.ElevationDesktop] = new SolidColorBrush(dark ? Color.FromHex("#080910") : Color.FromHex("#d2d3da"));
        rgb[ThemeKeys.ElevationWindow] = new SolidColorBrush(dark ? Color.FromHex("#16161e") : Color.FromHex("#f6f6f8"));
        rgb[ThemeKeys.ElevationPopup] = new SolidColorBrush(dark ? Color.FromHex("#16161e") : Color.FromHex("#f6f6f8")) { Opacity = 0.85 };
        rgb[ThemeKeys.ElevationDialog] = new SolidColorBrush(dark ? Color.FromHex("#16161e") : Color.FromHex("#f6f6f8")) { Opacity = 0.95 };
        rgb[ThemeKeys.ElevationRaised] = new SolidColorBrush(dark ? Color.FromHex("#1f2335") : Color.FromHex("#e9e9ed"));
        rgb[ThemeKeys.ElevationHighest] = new SolidColorBrush(dark ? Color.FromHex("#24283b") : Color.FromHex("#cbccd2"));
        rgb[ThemeKeys.WindowBackground] = new SolidColorBrush(dark ? Color.FromHex("#0d0f19") : Color.FromHex("#e6e7ec"));
        rgb[ThemeKeys.SurfaceBrush] = new SolidColorBrush(dark ? Color.FromHex("#24283b") : Color.FromHex("#9EA0A8"));
        rgb[ThemeKeys.PanelBrush] = new SolidColorBrush(dark ? Color.FromHex("#171A26") : Color.FromHex("#e9e9ed"));
        rgb[ThemeKeys.WellBrush] = new SolidColorBrush(dark ? Color.FromHex("#16161e") : Color.FromHex("#f6f6f8"));
        rgb[ThemeKeys.SelectionBrush] = new SolidColorBrush(dark ? Color.FromHex("#33467c") : Color.FromHex("#a8aecb"));
        rgb[ThemeKeys.SelectionInactiveBrush] = new SolidColorBrush(dark ? Color.FromHex("#454f6a") : Color.FromHex("#b1b4c2"));
        rgb[ThemeKeys.AlternateRowBrush] = new SolidColorBrush(dark ? Color.FromHex("#272b41") : Color.FromHex("#e1e2e7"));
        // Light --hover nudged off --surface (#cbccd1) so a hovered control reads as a fill (spec §1.1).
        rgb[ThemeKeys.HoverBrush] = new SolidColorBrush(dark ? Color.FromHex("#414868") : Color.FromHex("#bfc0c6"));
        rgb[ThemeKeys.TextBrush] = new SolidColorBrush(dark ? Color.FromHex("#c0caf5") : Color.FromHex("#343b58"));
        rgb[ThemeKeys.TextDimBrush] = new SolidColorBrush(dark ? Color.FromHex("#a9b1d6") : Color.FromHex("#565a6e"));
        rgb[ThemeKeys.MutedBrush] = new SolidColorBrush(dark ? Color.FromHex("#565f89") : Color.FromHex("#9699a3"));
        rgb[ThemeKeys.FaintBrush] = new SolidColorBrush(dark ? Color.FromHex("#414868") : Color.FromHex("#818392"));
        rgb[ThemeKeys.DisabledBackgroundBrush] = new SolidColorBrush(dark ? Color.FromHex("#1f2335") : Color.FromHex("#dcdde2"));
        rgb[ThemeKeys.DisabledForegroundBrush] = new SolidColorBrush(dark ? Color.FromHex("#565f89") : Color.FromHex("#757985"));
        rgb[ThemeKeys.AccentBrush] = new SolidColorBrush(dark ? Color.FromHex("#7aa2f7") : Color.FromHex("#34548a"));
        rgb[ThemeKeys.AccentInverseBrush] = new SolidColorBrush(dark ? Color.FromHex("#34548a") : Color.FromHex("#7aa2f7"));
        rgb[ThemeKeys.Accent2Brush] = new SolidColorBrush(dark ? Color.FromHex("#7dcfff") : Color.FromHex("#0f4b6e"));
        rgb[ThemeKeys.AccentDarkBrush] = new SolidColorBrush(dark ? Color.FromHex("#3F78F3") : Color.FromHex("#446EB6"));
        rgb[ThemeKeys.OnAccentBrush] = new SolidColorBrush(dark ? Color.FromHex("#0d0f19") : Color.FromHex("#e9e9ed"));
        rgb[ThemeKeys.CoolBrush] = new SolidColorBrush(dark ? Color.FromHex("#9663F3") : Color.FromHex("#5a3e8e"));
        rgb[ThemeKeys.CoolInverseBrush] = new SolidColorBrush(dark ? Color.FromHex("#5a3e8e") : Color.FromHex("#9663F3"));
        rgb[ThemeKeys.Cool2Brush] = new SolidColorBrush(dark ? Color.FromHex("#bb9af7") : Color.FromHex("#3F2B63"));
        rgb[ThemeKeys.CoolDarkBrush] = new SolidColorBrush(dark ? Color.FromHex("#702AEF") : Color.FromHex("#7655B5"));
        rgb[ThemeKeys.DangerBrush] = new SolidColorBrush(dark ? Color.FromHex("#f54e89") : Color.FromHex("#cf2f6e"));
        rgb[ThemeKeys.DangerInverseBrush] = new SolidColorBrush(dark ? Color.FromHex("#cf2f6e") : Color.FromHex("#f54e89"));
        rgb[ThemeKeys.Danger2Brush] = new SolidColorBrush(dark ? Color.FromHex("#ff80ac") : Color.FromHex("#b3215a"));
        rgb[ThemeKeys.DangerDarkBrush] = new SolidColorBrush(dark ? Color.FromHex("#F21261") : Color.FromHex("#DB6191"));
        rgb[ThemeKeys.SuccessBrush] = new SolidColorBrush(dark ? Color.FromHex("#63C792") : Color.FromHex("#1e7d52"));
        rgb[ThemeKeys.SuccessInverseBrush] = new SolidColorBrush(dark ? Color.FromHex("#1e7d52") : Color.FromHex("#63C792"));
        rgb[ThemeKeys.Success2Brush] = new SolidColorBrush(dark ? Color.FromHex("#8ED7B0") : Color.FromHex("#124A31"));
        rgb[ThemeKeys.SuccessDarkBrush] = new SolidColorBrush(dark ? Color.FromHex("#3EAD72") : Color.FromHex("#29AD71"));
        rgb[ThemeKeys.WarningBrush] = new SolidColorBrush(dark ? Color.FromHex("#e0af68") : Color.FromHex("#8f5e15"));
        rgb[ThemeKeys.WarningInverseBrush] = new SolidColorBrush(dark ? Color.FromHex("#8f5e15") : Color.FromHex("#e0af68"));
        rgb[ThemeKeys.Warning2Brush] = new SolidColorBrush(dark ? Color.FromHex("#eac999") : Color.FromHex("#593a0d"));
        rgb[ThemeKeys.WarningDarkBrush] = new SolidColorBrush(dark ? Color.FromHex("#d59334") : Color.FromHex("#c4811d"));
        rgb[ThemeKeys.InfoBrush] = new SolidColorBrush(dark ? Color.FromHex("#8873ff") : Color.FromHex("#4a30d6"));
        rgb[ThemeKeys.InfoInverseBrush] = new SolidColorBrush(dark ? Color.FromHex("#4a30d6") : Color.FromHex("#8873ff"));
        rgb[ThemeKeys.Info2Brush] = new SolidColorBrush(dark ? Color.FromHex("#beb3ff") : Color.FromHex("#3621A6"));
        rgb[ThemeKeys.InfoDarkBrush] = new SolidColorBrush(dark ? Color.FromHex("#5638FF") : Color.FromHex("#7561e0"));
        rgb[ThemeKeys.AmberBrush] = new SolidColorBrush(dark ? Color.FromHex("#e0af68") : Color.FromHex("#8f5e15"));
        rgb[ThemeKeys.GreenBrush] = new SolidColorBrush(dark ? Color.FromHex("#63C792") : Color.FromHex("#1e7d52"));
        rgb[ThemeKeys.RedBrush] = new SolidColorBrush(dark ? Color.FromHex("#f7768e") : Color.FromHex("#8c4351"));
        rgb[ThemeKeys.PurpleBrush] = new SolidColorBrush(dark ? Color.FromHex("#bb9af7") : Color.FromHex("#5a3e8e"));
        rgb[ThemeKeys.StatusBarBackground] = new SolidColorBrush(dark ? Color.FromHex("#1f2335") : Color.FromHex("#e9e9ed"));
        rgb[ThemeKeys.StatusBarAltBackground] = new SolidColorBrush(dark ? Color.FromHex("#7aa2f7") : Color.FromHex("#34548a"));
        // Branch/alt status text reads on the accent fill → the on-accent ink (spec StatusBranchForeground =
        // --on-accent, so it tracks OnAccentBrush exactly: #0d0f19 dark / #e9e9ed light).
        rgb[ThemeKeys.StatusBarAltForeground] = new SolidColorBrush(dark ? Color.FromHex("#0d0f19") : Color.FromHex("#e9e9ed"));
        
        // Opt-in chrome (no shipped control reads these by default): border = faint ink, focus ring = accent heavy.
        rgb[ThemeKeys.BorderPen] = new Pen(dark ? Color.FromHex("#414868") : Color.FromHex("#818392"));
        rgb[ThemeKeys.MenuBorderPen] = new Pen(dark ? Color.FromHex("#414868") : Color.FromHex("#818392")) /*{ Corners = CornerStyle.Rounded }*/;
        rgb[ThemeKeys.ToolTipBorderPen] = new Pen(dark ? Color.FromHex("#414868") : Color.FromHex("#818392")) /*{ Corners = CornerStyle.Rounded }*/;
        rgb[ThemeKeys.TabControlBorderPen] = new Pen(dark ? Color.FromHex("#414868") : Color.FromHex("#818392")) { Corners = CornerStyle.Rounded };
        rgb[ThemeKeys.SeparatorPen] = new Pen(dark ? Color.FromHex("#414868") : Color.FromHex("#818392")) { Weight = StrokeWeight.Heavy };
        rgb[ThemeKeys.MenuSeparatorPen] = new Pen(dark ? Color.FromHex("#414868") : Color.FromHex("#818392")) { Weight = StrokeWeight.Light };
        // Slider rail (design guide): a Heavy ━ — the filled (value) side in --accent, the empty side in --faint.
        rgb[ThemeKeys.SliderFilledPen] = new Pen(dark ? Color.FromHex("#7aa2f7") : Color.FromHex("#34548a")) { Weight = StrokeWeight.Heavy };
        rgb[ThemeKeys.SliderTrackPen] = new Pen(dark ? Color.FromHex("#414868") : Color.FromHex("#818392")) { Weight = StrokeWeight.Heavy };
        rgb[ThemeKeys.FocusPen] = new Pen(dark ? Color.FromHex("#7aa2f7") : Color.FromHex("#34548a")) { Weight = StrokeWeight.Heavy };
        // The active-tab underline rule — a Heavy --accent pen (the gallery "━ cells" bar), themeable per variant.
        rgb[ThemeKeys.TabUnderlinePen] = new Pen(dark ? Color.FromHex("#7aa2f7") : Color.FromHex("#34548a")) { Weight = StrokeWeight.Heavy };
        rgb[ThemeKeys.ObscuredOverlayBrush] = new SolidColorBrush(Color.FromHex("#080910")) { Opacity = 0.55 };
        rgb[ThemeKeys.AccessKeyIndicatorBrush] = new SolidColorBrush(dark ? Color.FromHex("#c0caf5") : Color.FromHex("#343b58"));
        
        rgb[ThemeKeys.WindowTitleBarBackground] = new SolidColorBrush(dark ? Color.FromHex("#3F78F3") : Color.FromHex("#23385D"));
        rgb[ThemeKeys.WindowTitleBarActiveBackground] = new SolidColorBrush(dark ? Color.FromHex("#7aa2f7") : Color.FromHex("#34548a"));

        rgb[ThemeKeys.ToolBarBrush] = new SolidColorBrush(dark ? Color.FromHex("#1f2335") : Color.FromHex("#dedee3"));
        rgb[ThemeKeys.SplitButtonDropZoneBrush] = new SolidColorBrush(dark ? Color.FromHex("#343c5e") : Color.FromHex("#cdd0dd"));
        
        // Ribbon (Surface B): the strip recess (--tabstrip) and the dropped active-tab fill (--tab-active).
        rgb[ThemeKeys.RibbonBrush] = new SolidColorBrush(dark ? Color.FromHex("#24283b") : Color.FromHex("#dedee3"));
        rgb[ThemeKeys.RibbonTabStripBrush] = new SolidColorBrush(dark ? Color.FromHex("#1b1e2e") : Color.FromHex("#d2d3da"));
        rgb[ThemeKeys.RibbonTabActiveBrush] = new SolidColorBrush(dark ? Color.FromHex("#15161e") : Color.FromHex("#fdfdfe"));
        rgb[ThemeKeys.KeyTipBrush] = new SolidColorBrush(dark ? Color.FromHex("#e0af68") : Color.FromHex("#8f5e15")); // --keytip amber
        rgb[ThemeKeys.KeyTipMatchedBrush] = new SolidColorBrush(dark ? Color.FromHex("#9d7b3f") : Color.FromHex("#b8935a")); // dimmed matched ink
        rgb[ThemeKeys.RibbonContextualFillBrush] = new SolidColorBrush(dark ? Color.FromHex("#2a2440") : Color.FromHex("#e0d8ef"));
        rgb[ThemeKeys.RibbonContextualUnderlinePen] = new Pen(dark ? Color.FromHex("#bb9af7") : Color.FromHex("#5a3e8e")) { Weight = StrokeWeight.Heavy };

        dict.ThemeDictionaries[new ThemeVariantKey(@base, ColorDepth.Ansi256)] = rgb;

        // (B,Ansi16): hand-picked palette indices — beat the quantizer (spec §1). Pinned for role
        // distinguishability under reverse-video: --text/--bg at the extremes (15/0), --accent/--on-accent on
        // real blue, resting fills→0 vs interactive fills→8 (dark), status hues kept true.
        var ansi16 = new ResourceDictionary();
        ansi16[ThemeKeys.ElevationWell] = Palette(15);
        ansi16[ThemeKeys.ElevationDesktop] = Palette(dark ? 0 : 7);
        ansi16[ThemeKeys.ElevationWindow] = Palette(dark ? 0 : 15);
        ansi16[ThemeKeys.ElevationRaised] = Palette(dark ? 0 : 7);
        ansi16[ThemeKeys.ElevationHighest] = Palette(dark ? 0 : 7);
        ansi16[ThemeKeys.WindowBackground] = Palette(dark ? 0 : 15);
        ansi16[ThemeKeys.SurfaceBrush] = Palette(dark ? 0 : 7);
        ansi16[ThemeKeys.PanelBrush] = Palette(dark ? 0 : 7);
        ansi16[ThemeKeys.ToolBarBrush] = Palette(dark ? 0 : 7);
        ansi16[ThemeKeys.RibbonBrush] = Palette(dark ? 0 : 7);
        ansi16[ThemeKeys.WellBrush] = Palette(dark ? 0 : 15);
        ansi16[ThemeKeys.SelectionBrush] = Palette(8);
        ansi16[ThemeKeys.SelectionInactiveBrush] = Palette(8);    // 16-color: no distinct inactive shade — tracks active selection
        ansi16[ThemeKeys.AlternateRowBrush] = Palette(dark ? 0 : 7); // 16-color: tracks the panel fill (zebra needs RGB)
        ansi16[ThemeKeys.HoverBrush] = Palette(8);
        ansi16[ThemeKeys.TextBrush] = Palette(dark ? 15 : 0);
        ansi16[ThemeKeys.TextDimBrush] = Palette(dark ? 7 : 8);
        ansi16[ThemeKeys.MutedBrush] = Palette(8);
        ansi16[ThemeKeys.FaintBrush] = Palette(dark ? 8 : 7);
        ansi16[ThemeKeys.DisabledBackgroundBrush] = Palette(dark ? 0 : 7);
        ansi16[ThemeKeys.DisabledForegroundBrush] = Palette(8);
        ansi16[ThemeKeys.AccentBrush] = Palette(dark ? 12 : 4);
        ansi16[ThemeKeys.AccentDarkBrush] = Palette(dark ? 4 : 12);
        ansi16[ThemeKeys.Accent2Brush] = Palette(dark ? 14 : 6);
        ansi16[ThemeKeys.StatusBarBackground] = Palette(dark ? 7 : 15);
        ansi16[ThemeKeys.StatusBarAltBackground] = Palette(dark ? 8 : 7);
        ansi16[ThemeKeys.StatusBarAltForeground] = Palette(15);

        // on-accent dark = 15 (white): black-on-bright-blue is unreadable on pure-blue palettes (spec §1†).
        ansi16[ThemeKeys.OnAccentBrush] = Palette(0);
        ansi16[ThemeKeys.AccentInverseBrush] = Palette(dark ? 4 : 12);
        ansi16[ThemeKeys.InfoBrush] = Palette(dark ? 14 : 6);
        ansi16[ThemeKeys.InfoInverseBrush] = Palette(dark ? 6 : 14);
        ansi16[ThemeKeys.CoolBrush] = Palette(dark ? 13 : 5);
        ansi16[ThemeKeys.CoolInverseBrush] = Palette(dark ? 5 : 13);
        ansi16[ThemeKeys.DangerBrush] = Palette(dark ? 9 : 1);
        ansi16[ThemeKeys.DangerInverseBrush] = Palette(dark ? 1 : 9);
        ansi16[ThemeKeys.SuccessBrush] = Palette(dark ? 10 : 2);
        ansi16[ThemeKeys.SuccessInverseBrush] = Palette(dark ? 2 : 10);
        ansi16[ThemeKeys.WarningBrush] = Palette(dark ? 11 : 3);
        ansi16[ThemeKeys.WarningInverseBrush] = Palette(dark ? 3 : 11);
        ansi16[ThemeKeys.GreenBrush] = Palette(2);
        ansi16[ThemeKeys.AmberBrush] = Palette(3);
        ansi16[ThemeKeys.RedBrush] = Palette(dark ? 9 : 1);
        ansi16[ThemeKeys.PurpleBrush] = Palette(dark ? 13 : 5);
        ansi16[ThemeKeys.BorderPen] = Pens.Light.WithColor(Color.FromPalette(8));
        ansi16[ThemeKeys.SeparatorPen] = Pens.Double.WithColor(Color.FromPalette(8));
        ansi16[ThemeKeys.MenuSeparatorPen] = Pens.Light.WithBrush(Palette(dark ? 15 : 0));
        ansi16[ThemeKeys.FocusPen] = Pens.Double.WithColor(Color.FromPalette(dark ? (byte)12 : (byte)4));
        ansi16[ThemeKeys.TabUnderlinePen] = Pens.Heavy.WithColor(Color.FromPalette(dark ? (byte)12 : (byte)4));
        ansi16[ThemeKeys.SliderFilledPen] = Pens.Heavy.WithColor(Color.FromPalette(dark ? (byte)12 : (byte)4)); // accent
        ansi16[ThemeKeys.SliderTrackPen] = Pens.Heavy.WithColor(Color.FromPalette(8));                          // faint/grey
        ansi16[ThemeKeys.ObscuredOverlayBrush] = Palette(8);
        ansi16[ThemeKeys.AccessKeyIndicatorBrush] = Palette(dark ? 15 : 0);
        // Ribbon: the descent never ascends to Ansi256, so the strip recess and dropped active-tab fill need explicit
        // Ansi16 indices or they collapse to the NoColor floor (no fill) on 16-color terminals.
        ansi16[ThemeKeys.RibbonTabStripBrush] = Palette(dark ? 0 : 7);   // recess, tracks Surface/Panel
        ansi16[ThemeKeys.RibbonTabActiveBrush] = Palette(dark ? 0 : 15); // dropped active fill, tracks the band
        ansi16[ThemeKeys.KeyTipBrush] = Palette(3);                      // amber → yellow
        ansi16[ThemeKeys.KeyTipMatchedBrush] = Palette(8);              // dimmed matched → bright-black
        ansi16[ThemeKeys.RibbonContextualFillBrush] = Palette(dark ? 0 : 7);          // tinted well, tracks the recess
        ansi16[ThemeKeys.RibbonContextualUnderlinePen] = Pens.Heavy.WithColor(Color.FromPalette(dark ? (byte)13 : (byte)5)); // purple

        ansi16[ThemeKeys.InteractiveInverseAttributes] = TextAttributes.Faint;

        dict.ThemeDictionaries[new ThemeVariantKey(@base, ColorDepth.Ansi16)] = ansi16;
    }

    private static SolidColorBrush Palette(int index) => new(Color.FromPalette((byte)index));
}
