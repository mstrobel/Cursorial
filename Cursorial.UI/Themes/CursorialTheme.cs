using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.UI.Controls;

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
    [
        ThemeKeys.WindowBackground, ThemeKeys.WindowTitleBarBackground, ThemeKeys.WindowTitleBarActiveBackground,
        ThemeKeys.WindowTitleBarActiveForeground, ThemeKeys.WindowTitleBarForeground, ThemeKeys.SurfaceBrush,
        ThemeKeys.PanelBrush, ThemeKeys.WellBrush, ThemeKeys.ToolBarBrush, ThemeKeys.RibbonBrush,
        ThemeKeys.SelectionBrush, ThemeKeys.SelectionInactiveBrush, ThemeKeys.AlternateRowBrush, ThemeKeys.HoverBrush,
        ThemeKeys.TextBrush, ThemeKeys.TextDimBrush, ThemeKeys.MutedBrush, ThemeKeys.FaintBrush,
        ThemeKeys.DisabledBackgroundBrush, ThemeKeys.DisabledForegroundBrush, ThemeKeys.AccentBrush,
        ThemeKeys.Accent2Brush, ThemeKeys.AccentDarkBrush, ThemeKeys.AccentInverseBrush, ThemeKeys.CoolBrush,
        ThemeKeys.Cool2Brush, ThemeKeys.CoolDarkBrush, ThemeKeys.CoolInverseBrush, ThemeKeys.DangerBrush,
        ThemeKeys.Danger2Brush, ThemeKeys.DangerDarkBrush, ThemeKeys.DangerInverseBrush, ThemeKeys.OnAccentBrush,
        ThemeKeys.GreenBrush, ThemeKeys.AmberBrush, ThemeKeys.WarningBrush, ThemeKeys.Warning2Brush,
        ThemeKeys.WarningDarkBrush, ThemeKeys.WarningInverseBrush, ThemeKeys.InfoBrush, ThemeKeys.Info2Brush,
        ThemeKeys.InfoDarkBrush, ThemeKeys.InfoInverseBrush, ThemeKeys.SuccessBrush, ThemeKeys.Success2Brush,
        ThemeKeys.SuccessDarkBrush, ThemeKeys.SuccessInverseBrush, ThemeKeys.SpecialBrush, ThemeKeys.RedBrush,
        ThemeKeys.PurpleBrush, ThemeKeys.StatusBarBackground, ThemeKeys.StatusBarAltBackground,
        ThemeKeys.StatusBarAltForeground, ThemeKeys.ObscuredOverlayBrush, ThemeKeys.AccessKeyIndicatorBrush,
        ThemeKeys.AccentForegroundBrush, ThemeKeys.SelectionActiveBrush, ThemeKeys.PanelBackgroundBrush,
        ThemeKeys.ButtonForegroundNormal, ThemeKeys.ButtonBackgroundNormal, ThemeKeys.ButtonBackgroundHover,
        ThemeKeys.ButtonForegroundHover, ThemeKeys.ButtonForegroundFocus, ThemeKeys.ButtonBackgroundFocus,
        ThemeKeys.ButtonForegroundPressed, ThemeKeys.ButtonBackgroundPressed, ThemeKeys.ButtonForegroundDisabled,
        ThemeKeys.ButtonBackgroundDisabled, ThemeKeys.SplitButtonDropZoneBrush, ThemeKeys.ToggleForegroundNormal,
        ThemeKeys.ToggleForegroundDisabled, ThemeKeys.InputForegroundNormal, ThemeKeys.InputBackgroundNormal,
        ThemeKeys.InputBackgroundHover, ThemeKeys.InputForegroundHover, ThemeKeys.InputBackgroundFocus,
        ThemeKeys.InputForegroundDisabled, ThemeKeys.InputBackgroundDisabled, ThemeKeys.ListItemBackgroundNormal,
        ThemeKeys.ListItemForegroundNormal, ThemeKeys.ListItemBackgroundHover, ThemeKeys.ListItemForegroundHover,
        ThemeKeys.ListItemBackgroundSelected, ThemeKeys.ListItemForegroundFocus, ThemeKeys.ListItemBackgroundFocus,
        ThemeKeys.ListItemForegroundDisabled, ThemeKeys.TreeItemForegroundNormal, ThemeKeys.TreeItemBackgroundSelected,
        ThemeKeys.TreeItemForegroundFocus, ThemeKeys.TreeItemBackgroundFocus, ThemeKeys.TreeItemForegroundDisabled,
        ThemeKeys.MenuForegroundNormal, ThemeKeys.MenuBarBackground, ThemeKeys.MenuBackgroundHover,
        ThemeKeys.MenuBackgroundHighlighted, ThemeKeys.MenuAcceleratorForeground,
        ThemeKeys.MenuAcceleratorHoverForeground, ThemeKeys.MenuForegroundDisabled, ThemeKeys.TabForegroundNormal,
        ThemeKeys.TabForegroundSelected, ThemeKeys.TabForegroundHover, ThemeKeys.TabBackgroundHover,
        ThemeKeys.TabForegroundFocused, ThemeKeys.TabBackgroundFocused, ThemeKeys.TabBackgroundSelected,
        ThemeKeys.TabForegroundDisabled, ThemeKeys.RibbonTabStripBrush, ThemeKeys.RibbonTabActiveBrush,
        ThemeKeys.KeyTipBrush, ThemeKeys.KeyTipMatchedBrush, ThemeKeys.RibbonContextualFillBrush,
        ThemeKeys.ProgressTrackBrush, ThemeKeys.CalendarDayForeground, ThemeKeys.CalendarDayInactiveForeground,
        ThemeKeys.CalendarDayBackgroundHover, ThemeKeys.CalendarDayForegroundHover, ThemeKeys.CalendarDayTodayForeground,
        ThemeKeys.CalendarDayBackgroundSelected, ThemeKeys.CalendarDayForegroundSelected,
        ThemeKeys.CalendarDayForegroundFocus, ThemeKeys.CalendarDayBackgroundFocus,
        ThemeKeys.CalendarDayForegroundDisabled, ThemeKeys.ProgressFillNormal, ThemeKeys.ProgressFillIndeterminate,
        ThemeKeys.OnHoverBrush, ThemeKeys.ListItemForegroundSelected, ThemeKeys.AlternateRowInk, ThemeKeys.SelectionInk,
        ThemeKeys.MenuForegroundHover, ThemeKeys.MenuForegroundHighlighted, ThemeKeys.MenuIconCheckedForeground,
        ThemeKeys.MenuIconUncheckedForeground, ThemeKeys.MenuIconUncheckedHoverForeground,
        ThemeKeys.ScrollBarTrackBrush, ThemeKeys.ScrollBarThumbNormalBrush, ThemeKeys.ScrollBarThumbHoverBrush,
        ThemeKeys.ScrollBarThumbDragBrush
    ];

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
        dict[ThemeKeys.ListItemSelectionGlyph] = ">";

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
        dict.Styles =
        [
            CursorialThemeStyles.AccessKeyCue(),
            CursorialThemeStyles.AccessKeyCueIndicatorStyle(),
            CursorialThemeStyles.ActiveSelectionStyles(),
            CursorialThemeStyles.CapsUnicodeCheckBoxGlyphs(),
            CursorialThemeStyles.CapsUnicodeRadioGlyphs(),
            // The caps-nocolor interactive-state layer: colors collapse to Default under NoColor, so the
            // button family's focus/pressed/default flip Inverse (reverse-video, honored by the Border fill +
            // content text) and disabled dims to Faint.
            CursorialThemeStyles.CapsNoColorInteractiveInverse(),
            CursorialThemeStyles.CapsNoColorInputInteractiveInverse(),
            CursorialThemeStyles.CapsNoColorDisabledFaint(),
            CursorialThemeStyles.CapsNoColorSelectionInverse(),
            CursorialThemeStyles.CapsNoColorTreeSelectionInverse(),
            CursorialThemeStyles.CapsNoColorFocusBlink(),
            CursorialThemeStyles.CapsNoColorListFocusCue(),
            CursorialThemeStyles.CapsNoColorBorderStyle(),
            CursorialThemeStyles.CapsNoColorBorderPenStyle(),
            CursorialThemeStyles.CapsNoColorMenuIconToggleStyle(),
            CursorialThemeStyles.CapsNoColorObscuredOverlayStyle(),

            // All blending should be disabled for Ansi16, so make sure all borders and panels occlude.
            CursorialThemeStyles.CapsAnsi16BorderStyle(),
            CursorialThemeStyles.CapsAnsi16BorderPenStyle(),
            CursorialThemeStyles.CapsAnsi16ObscuredOverlayStyle(),
            CursorialThemeStyles.CapsAnsi16ThemeClassWindowBorders(),

            CursorialThemeStyles.MenuSeparatorStyle(),

            CursorialThemeStyles.AccentStyle(),
            CursorialThemeStyles.InfoStyle(),
            CursorialThemeStyles.CoolStyle(),
            CursorialThemeStyles.WarningStyle(),
            CursorialThemeStyles.DangerStyle(),
            CursorialThemeStyles.SuccessStyle()
        ];

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
        noColor[ThemeKeys.AccentBorderPen] = Pens.Ascii;
        noColor[ThemeKeys.CoolBorderPen] = Pens.Ascii;
        noColor[ThemeKeys.InfoBorderPen] = Pens.Ascii;
        noColor[ThemeKeys.SuccessBorderPen] = Pens.Ascii;
        noColor[ThemeKeys.WarningBorderPen] = Pens.Ascii;
        noColor[ThemeKeys.DangerBorderPen] = Pens.Ascii;
        noColor[ThemeKeys.MenuBorderPen] = Pens.Ascii;
        noColor[ThemeKeys.TabControlBorderPen] = Pens.Ascii;
        noColor[ThemeKeys.ToolTipBorderPen] = Pens.Ascii;
        noColor[ThemeKeys.FocusBorderPen] = Pens.Ascii;
        noColor[ThemeKeys.SeparatorPen] = Pens.Ascii;
        noColor[ThemeKeys.MenuSeparatorPen] = Pens.Ascii;
        noColor[ThemeKeys.FocusPen] = Pens.Ascii.WithWeight(StrokeWeight.Heavy);
        noColor[ThemeKeys.TabUnderlinePen] = Pens.Ascii.WithWeight(StrokeWeight.Heavy);
        noColor[ThemeKeys.TabFocusedUnderlinePen] = Pens.Ascii.WithWeight(StrokeWeight.Double);
        // No color to distinguish filled vs empty — both are an ASCII heavy rail; the thumb + reverse-video focus carry it.
        noColor[ThemeKeys.SliderFilledPen] = Pens.Ascii.WithWeight(StrokeWeight.Double);
        noColor[ThemeKeys.SliderTrackPen] = Pens.Ascii.WithWeight(StrokeWeight.Light);
        noColor[ThemeKeys.ObscuredOverlayBrush] = Brushes.Transparent;
        noColor[ThemeKeys.AccessKeyIndicatorBrush] = defaultBrush;
        // Ribbon: no fill under monochrome — the active tab is marked by the ASCII-heavy underline (TabUnderlinePen).
        noColor[ThemeKeys.RibbonTabStripBrush] = defaultBrush;
        noColor[ThemeKeys.RibbonTabActiveBrush] = defaultBrush;
        noColor[ThemeKeys.KeyTipBrush] = defaultBrush;
        noColor[ThemeKeys.KeyTipTextWeight] = TextWeight.Faint;
        noColor[ThemeKeys.KeyTipMatchedBrush] = defaultBrush;
        // A contextual tab has no purple to lean on under mono; its only cue is the underline — authored ASCII-heavy
        // (visually identical to the accent one here) since RibbonTabActiveBrush = defaultBrush leaves nothing else.
        noColor[ThemeKeys.RibbonContextualFillBrush] = defaultBrush;
        noColor[ThemeKeys.RibbonContextualUnderlinePen] = Pens.Ascii.WithWeight(StrokeWeight.Heavy);
        // Reverse-video is the monochrome interactive cue (the palette fill collapsed to Default). This lives ONLY
        // at the NoColor floor; the false/Normal counterpart at the Ansi16 wildcard floor (below) keeps it from
        // bleeding up. The pair splits along the per-axis properties (proposal §2.3).
        noColor[ThemeKeys.InteractiveCueInverse] = true;
        noColor[ThemeKeys.InteractiveCueWeight] = TextWeight.Normal;
        noColor[ThemeKeys.InteractiveCueUnderline] = null;

        dict.ThemeDictionaries[new ThemeVariantKey(null, ColorDepth.NoColor)] = noColor;

        // The color-tier floor for the cue pair. The CD8 probe DESCENDS tiers
        // (Truecolor→Ansi256→Ansi16→NoColor), so a bare NoColor=Inverse would resolve for every color tier too.
        // Ansi16 is the lowest color tier, so every color tier's descent reaches this false/Normal before the
        // NoColor pair below it, while a true NoColor variant (whose descent is NoColor-only) never sees it.
        var ansi16Floor = new ResourceDictionary
        {
            [ThemeKeys.InteractiveCueInverse] = false,
            [ThemeKeys.InteractiveCueWeight] = TextWeight.Bold,
            [ThemeKeys.InteractiveCueUnderline] = UnderlineStyle.Single,
            [ThemeKeys.ListItemSelectionGlyph] = "▍",
            [ThemeKeys.ObscuredOverlayBrush] = Brushes.Transparent
        };
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
            foreach (var themeDictionary in dict.ThemeDictionaries)
            {
                if (themeDictionary.Key.Tier is ColorDepth.Truecolor)
                    continue; // we only put a few overrides in Truecolor; most rgb values live in Ansi256.
                if (themeDictionary.Value.ContainsKey(key) is false)
                    themeDictionary.Value[key] = new ResourceReference(target);
            }
        }

        
        Alias(ThemeKeys.SelectionInk, ThemeKeys.OnAccentBrush);
        Alias(ThemeKeys.OnHoverBrush, ThemeKeys.OnAccentBrush);
        Alias(ThemeKeys.OnAccentInverseBrush, ThemeKeys.OnAccentBrush);

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
        Alias(ThemeKeys.ButtonBackgroundDisabled, ThemeKeys.DisabledBackgroundBrush);
        Alias(ThemeKeys.ButtonForegroundDisabled, ThemeKeys.DisabledForegroundBrush);
        Alias(ThemeKeys.ButtonForegroundDefault, ThemeKeys.AccentBrush);
        Alias(ThemeKeys.SplitButtonDropZoneBrush, ThemeKeys.AccentBrush);

        // ToggleSwitch / CheckBox / RadioButton.
        Alias(ThemeKeys.ToggleForegroundNormal, ThemeKeys.TextBrush);
        Alias(ThemeKeys.ToggleGlyphChecked, ThemeKeys.GreenBrush);
        Alias(ThemeKeys.ToggleGlyphIndeterminate, ThemeKeys.AmberBrush);
        Alias(ThemeKeys.RadioGlyphChecked, ThemeKeys.AccentBrush);
        Alias(ThemeKeys.ToggleForegroundDisabled, ThemeKeys.DisabledForegroundBrush);

        // Input (TextBox, editable ComboBox).
        Alias(ThemeKeys.InputForegroundNormal, ThemeKeys.TextBrush);
        Alias(ThemeKeys.InputBackgroundNormal, ThemeKeys.ElevationHighest);
        Alias(ThemeKeys.InputBackgroundHover, ThemeKeys.HoverBrush);
        Alias(ThemeKeys.InputForegroundHover, ThemeKeys.OnHoverBrush);
        Alias(ThemeKeys.InputBackgroundFocus, ThemeKeys.WellBrush);
        Alias(ThemeKeys.InputForegroundFocus, ThemeKeys.TextBrush);
        Alias(ThemeKeys.InputSelectionActive, ThemeKeys.SelectionBrush);
        Alias(ThemeKeys.InputSelectionInactive, ThemeKeys.SelectionInactiveBrush);
        Alias(ThemeKeys.InputForegroundDisabled, ThemeKeys.DisabledForegroundBrush);
        Alias(ThemeKeys.InputBackgroundDisabled, ThemeKeys.DisabledBackgroundBrush);

        // ListItem (ListBox / ComboBox drop-down item).
        Alias(ThemeKeys.ListItemForegroundNormal, ThemeKeys.TextBrush);
        Alias(ThemeKeys.ListItemBackgroundNormal, ThemeKeys.WellBrush);
        Alias(ThemeKeys.ListItemForegroundHover, ThemeKeys.OnHoverBrush);
        Alias(ThemeKeys.ListItemBackgroundHover, ThemeKeys.HoverBrush);
        Alias(ThemeKeys.ListItemBackgroundSelected, ThemeKeys.SelectionBrush);
        Alias(ThemeKeys.ListItemForegroundSelectedInactive, ThemeKeys.OnHoverBrush);
        Alias(ThemeKeys.ListItemBackgroundSelectedInactive, ThemeKeys.SelectionInactiveBrush);
        // Gallery `.item.rev { background: --text; color: --panel }` (final style guide): a list item sits on the
        // PanelBrush surface, so its reverse-video focus ink is --panel (not the universal --bg). #103.
        Alias(ThemeKeys.ListItemForegroundFocus, ThemeKeys.OnAccentBrush);
        Alias(ThemeKeys.ListItemForegroundSelected, ThemeKeys.SelectionInk);
        Alias(ThemeKeys.ListItemBackgroundFocus, ThemeKeys.TextBrush);
        Alias(ThemeKeys.ListItemForegroundDisabled, ThemeKeys.MutedBrush);

        // TreeViewItem (same item-on-panel reverse-video ink, #103).
        Alias(ThemeKeys.TreeItemForegroundNormal, ThemeKeys.TextBrush);
        Alias(ThemeKeys.TreeItemBackgroundSelected, ThemeKeys.SelectionBrush);
        Alias(ThemeKeys.TreeItemForegroundFocus, ThemeKeys.OnAccentBrush);
        Alias(ThemeKeys.TreeItemBackgroundFocus, ThemeKeys.TextBrush);
        Alias(ThemeKeys.TreeItemForegroundDisabled, ThemeKeys.MutedBrush);

        // Menu (MenuBar / MenuItem / ContextMenu).
        Alias(ThemeKeys.MenuForegroundNormal, ThemeKeys.TextBrush);
        Alias(ThemeKeys.MenuBarBackground, ThemeKeys.ElevationRaised);
        Alias(ThemeKeys.MenuBackgroundHover, ThemeKeys.SelectionBrush);
        Alias(ThemeKeys.MenuForegroundHover, ThemeKeys.SelectionInk);
        Alias(ThemeKeys.MenuBackgroundHighlighted, ThemeKeys.SelectionBrush);
        Alias(ThemeKeys.MenuForegroundHighlighted, ThemeKeys.SelectionInk);
        Alias(ThemeKeys.MenuAcceleratorForeground, ThemeKeys.MutedBrush);
        Alias(ThemeKeys.MenuAcceleratorHoverForeground, ThemeKeys.TextDimBrush);
        Alias(ThemeKeys.MenuForegroundDisabled, ThemeKeys.MutedBrush);
        Alias(ThemeKeys.MenuIconCheckedForeground, ThemeKeys.SuccessBrush);
        Alias(ThemeKeys.MenuIconUncheckedForeground, ThemeKeys.MenuAcceleratorForeground);
        Alias(ThemeKeys.MenuIconUncheckedHoverForeground, ThemeKeys.MenuAcceleratorHoverForeground);

        // TabItem — gallery: inactive ink = --text-dim, active = --surface fill + --text ink + an --accent
        // underline bar (#103, was a --sel fill with --text ink on every tab).
        Alias(ThemeKeys.TabForegroundNormal, ThemeKeys.TextDimBrush);
        Alias(ThemeKeys.TabForegroundSelected, ThemeKeys.TextBrush);
        Alias(ThemeKeys.TabForegroundHover, ThemeKeys.OnHoverBrush);
        Alias(ThemeKeys.TabBackgroundHover, ThemeKeys.HoverBrush);
        Alias(ThemeKeys.TabForegroundFocused, ThemeKeys.OnHoverBrush);
        Alias(ThemeKeys.TabBackgroundFocused, ThemeKeys.HoverBrush);
        Alias(ThemeKeys.TabBackgroundSelected, ThemeKeys.SurfaceBrush);
        Alias(ThemeKeys.TabForegroundDisabled, ThemeKeys.MutedBrush);
        Alias(ThemeKeys.TabControlBorderPen, ThemeKeys.BorderPen);
        Alias(ThemeKeys.TabFocusedUnderlinePen, ThemeKeys.TabUnderlinePen);
        // TabUnderlinePen is a Pen (not a brush alias) — defined per-variant in AddTierPalette.

        // ProgressBar — gallery: empty track = --faint (#103, was --well).
        Alias(ThemeKeys.ProgressFillNormal, ThemeKeys.GreenBrush);
        Alias(ThemeKeys.ProgressFillIndeterminate, ThemeKeys.AccentBrush);
        Alias(ThemeKeys.ProgressTrackBrush, ThemeKeys.FaintBrush);
        Alias(ThemeKeys.ScrollBarThumbNormalBrush, ThemeKeys.TextBrush);
        Alias(ThemeKeys.ScrollBarThumbHoverBrush, ThemeKeys.AccentBrush);
        Alias(ThemeKeys.ScrollBarThumbDragBrush, ThemeKeys.AccentInverseBrush);
        Alias(ThemeKeys.ScrollBarTrackBrush, ThemeKeys.WellBrush);

        // Calendar (day cells + Year/Decade cells; DatePicker).
        Alias(ThemeKeys.CalendarDayForeground, ThemeKeys.TextBrush);
        Alias(ThemeKeys.CalendarDayInactiveForeground, ThemeKeys.MutedBrush);
        Alias(ThemeKeys.CalendarDayForegroundHover, ThemeKeys.OnHoverBrush);
        Alias(ThemeKeys.CalendarDayBackgroundHover, ThemeKeys.HoverBrush);
        Alias(ThemeKeys.CalendarDayTodayForeground, ThemeKeys.AccentBrush);
        Alias(ThemeKeys.CalendarDayBackgroundSelected, ThemeKeys.SelectionBrush);
        Alias(ThemeKeys.CalendarDayForegroundSelected, ThemeKeys.SelectionInk);
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
        Alias(ThemeKeys.FocusBorderPen, ThemeKeys.BorderPen);
        
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

        // ReSharper disable once UseObjectOrCollectionInitializer
        var tc = new ResourceDictionary();

        tc[ThemeKeys.TextBrush] = new SolidColorBrush(dark ? Color.FromHex("#c0caf5") : Color.FromHex("#343b58"));
        tc[ThemeKeys.SelectionInk] = new SolidColorBrush(dark ? Color.FromHex("#c0caf5") : Color.FromHex("#343b58"));
        tc[ThemeKeys.OnHoverBrush] = new SolidColorBrush(dark ? Color.FromHex("#c0caf5") : Color.FromHex("#343b58"));
        
        tc[ThemeKeys.AnsiBlack] = SolidColorBrush.FromHex("#15161e");
        tc[ThemeKeys.AnsiRed] = SolidColorBrush.FromHex("#d9536a");
        tc[ThemeKeys.AnsiGreen] = SolidColorBrush.FromHex("#5aa457");
        tc[ThemeKeys.AnsiYellow] = SolidColorBrush.FromHex("#b0862f");
        tc[ThemeKeys.AnsiBlue] = SolidColorBrush.FromHex("#5a7ec9");
        tc[ThemeKeys.AnsiMagenta] = SolidColorBrush.FromHex("#a072cf");
        tc[ThemeKeys.AnsiCyan] = SolidColorBrush.FromHex("#3f9dbf");
        tc[ThemeKeys.AnsiWhite] = SolidColorBrush.FromHex("#a9b1d6");
        tc[ThemeKeys.AnsiLightBlack] = SolidColorBrush.FromHex("#565f89");
        tc[ThemeKeys.AnsiLightRed] = SolidColorBrush.FromHex("#f7768e");
        tc[ThemeKeys.AnsiLightGreen] = SolidColorBrush.FromHex("#9ece6a");
        tc[ThemeKeys.AnsiLightYellow] = SolidColorBrush.FromHex("#e0af68");
        tc[ThemeKeys.AnsiLightBlue] = SolidColorBrush.FromHex("#7aa2f7");
        tc[ThemeKeys.AnsiLightMagenta] = SolidColorBrush.FromHex("#bb9af7");
        tc[ThemeKeys.AnsiLightCyan] = SolidColorBrush.FromHex("#7dcfff");
        tc[ThemeKeys.AnsiLightWhite] = SolidColorBrush.FromHex("#e6e7ee");

        dict.ThemeDictionaries[new ThemeVariantKey(@base, ColorDepth.Truecolor)] = tc;

        // ReSharper disable once UseObjectOrCollectionInitializer

        // (B,Ansi256): RGB role tokens — Tokyo-Night, verbatim from the default-theme gallery; served at
        // Truecolor too (descent never ascends; CD8). The cell-faithful spine (design doc §11.8a) — fill +
        // foreground tokens; the two pens are opt-in chrome, not spine members.
        var rgb = new ResourceDictionary
                  {
                      [ThemeKeys.InteractiveCueInverse] = false,
                      [ThemeKeys.InteractiveCueWeight] = TextWeight.Normal,
                      [ThemeKeys.InteractiveCueUnderline] = UnderlineStyle.Single,
                      [ThemeKeys.ListItemSelectionGlyph] = "▍"
                  };
        
        rgb[ThemeKeys.ElevationWell] = new SolidColorBrush(dark ? Color.FromHex("#0d0f19") : Color.FromHex("#f6f6f8"));
        rgb[ThemeKeys.ElevationDesktop] = new SolidColorBrush(dark ? Color.FromHex("#080910") : Color.FromHex("#d2d3da"));
        rgb[ThemeKeys.ElevationWindow] = new SolidColorBrush(dark ? Color.FromHex("#11111C") : Color.FromHex("#f6f6f8"));
        rgb[ThemeKeys.ElevationPopup] = new SolidColorBrush(dark ? Color.FromHex("#11111C") : Color.FromHex("#f6f6f8")) { Opacity = 0.85 };
        rgb[ThemeKeys.ElevationDialog] = new SolidColorBrush(dark ? Color.FromHex("#11111C") : Color.FromHex("#f6f6f8")) /*{ Opacity = 0.95 }*/;
        rgb[ThemeKeys.ElevationRaised] = new SolidColorBrush(dark ? Color.FromHex("#1F2233") : Color.FromHex("#d8d8df"));
        rgb[ThemeKeys.ElevationHighest] = new SolidColorBrush(dark ? Color.FromHex("#24283b") : Color.FromHex("#cbccd1"));
        rgb[ThemeKeys.WindowBackground] = new SolidColorBrush(dark ? Color.FromHex("#0d0f19") : Color.FromHex("#e6e7ec"));
        rgb[ThemeKeys.SurfaceBrush] = new SolidColorBrush(dark ? Color.FromHex("#30364f") : Color.FromHex("#9ea0a8"));
        rgb[ThemeKeys.PanelBrush] = new SolidColorBrush(dark ? Color.FromHex("#171A26") : Color.FromHex("#e9e9ed"));
        rgb[ThemeKeys.WellBrush] = new SolidColorBrush(dark ? Color.FromHex("#0d0f19") : Color.FromHex("#f6f6f8"));
        rgb[ThemeKeys.TextBrush] = new SolidColorBrush(dark ? Color.FromHex("#e4e4e4") : Color.FromHex("#3a3a3a"));
        rgb[ThemeKeys.TextDimBrush] = new SolidColorBrush(dark ? Color.FromHex("#8d9fed") : Color.FromHex("#4a547d"));
        rgb[ThemeKeys.SelectionBrush] = new SolidColorBrush(dark ? Color.FromHex("#33467c") : Color.FromHex("#a8aecb"));
        rgb[ThemeKeys.SelectionInactiveBrush] = new SolidColorBrush(dark ? Color.FromHex("#454f6a") : Color.FromHex("#b1b4c2"));
        rgb[ThemeKeys.SelectionInk] = new SolidColorBrush(dark ? Color.FromHex("#e4e4e4") : Color.FromHex("#3a3a3a"));
        rgb[ThemeKeys.AlternateRowBrush] = new SolidColorBrush(dark ? Color.FromHex("#151828") : Color.FromHex("#f0f0f0"));
        rgb[ThemeKeys.AlternateRowInk] = null;
        // Light --hover nudged off --surface (#cbccd1) so a hovered control reads as a fill (spec §1.1).
        rgb[ThemeKeys.HoverBrush] = new SolidColorBrush(dark ? Color.FromHex("#414868") : Color.FromHex("#bfc0c6"));
        rgb[ThemeKeys.OnHoverBrush] = new SolidColorBrush(dark ? Color.FromHex("#e4e4e4") : Color.FromHex("#3a3a3a"));
        rgb[ThemeKeys.MutedBrush] = new SolidColorBrush(dark ? Color.FromHex("#565f89") : Color.FromHex("#9699a3"));
        rgb[ThemeKeys.FaintBrush] = new SolidColorBrush(dark ? Color.FromHex("#414868") : Color.FromHex("#818392"));
        rgb[ThemeKeys.DisabledBackgroundBrush] = new SolidColorBrush(dark ? Color.FromHex("#1f2335") : Color.FromHex("#dcdde2"));
        rgb[ThemeKeys.DisabledForegroundBrush] = new SolidColorBrush(dark ? Color.FromHex("#565f89") : Color.FromHex("#9699a3"));
        rgb[ThemeKeys.AccentBrush] = new SolidColorBrush(dark ? Color.FromHex("#6090f6") : Color.FromHex("#34548a"));
        rgb[ThemeKeys.AccentInverseBrush] = new SolidColorBrush(dark ? Color.FromHex("#34548a") : Color.FromHex("#7aa2f7"));
        rgb[ThemeKeys.Accent2Brush] = new SolidColorBrush(dark ? Color.FromHex("#9ab8f9") : Color.FromHex("#23385d"));
        rgb[ThemeKeys.AccentDarkBrush] = new SolidColorBrush(dark ? Color.FromHex("#2667f3") : Color.FromHex("#446EB6"));
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

        rgb[ThemeKeys.ScrollBarThumbNormalBrush] = new SolidColorBrush(/*dark ? Color.FromHex("#8d9fed3f") : Color.FromHex("#4a547d3f")*/dark ? Color.FromHex("#8d9fed") : Color.FromHex("#4a547d"));
        rgb[ThemeKeys.ScrollBarTrackBrush] = new SolidColorBrush(dark ? Color.FromHex("#8d9fed") : Color.FromHex("#4a547d")) { Opacity = 0.25d };

        // Opt-in chrome (no shipped control reads these by default): border = faint ink, focus ring = accent heavy.
        rgb[ThemeKeys.BorderPen] = new Pen(dark ? Color.FromHex("#414868") : Color.FromHex("#818392"));
        rgb[ThemeKeys.MenuBorderPen] = new Pen(dark ? Color.FromHex("#414868") : Color.FromHex("#818392")) /*{ Corners = CornerStyle.Rounded }*/;
        rgb[ThemeKeys.ToolTipBorderPen] = new Pen(dark ? Color.FromHex("#414868") : Color.FromHex("#818392")) /*{ Corners = CornerStyle.Rounded }*/;
        rgb[ThemeKeys.FocusBorderPen] = new Pen(dark ? Color.FromHex("#6090f6") : Color.FromHex("#34548a")) /*{ Corners = CornerStyle.Rounded }*/;
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
        // rgb[ThemeKeys.AccessKeyIndicatorBrush] = new ResourceReference(ThemeKeys.TextBrush);

        rgb[ThemeKeys.AccentBorderPen] = new Pen(dark ? Color.FromHex("#6090f6") : Color.FromHex("#34548a"));
        rgb[ThemeKeys.CoolBorderPen] = new Pen(dark ? Color.FromHex("#9663F3") : Color.FromHex("#5a3e8e"));
        rgb[ThemeKeys.InfoBorderPen] = new Pen(dark ? Color.FromHex("#8873ff") : Color.FromHex("#4a30d6"));
        rgb[ThemeKeys.SuccessBorderPen] = new Pen(dark ? Color.FromHex("#63C792") : Color.FromHex("#1e7d52"));
        rgb[ThemeKeys.WarningBorderPen] = new Pen(dark ? Color.FromHex("#e0af68") : Color.FromHex("#8f5e15"));
        rgb[ThemeKeys.DangerBorderPen] = new Pen(dark ? Color.FromHex("#f54e89") : Color.FromHex("#cf2f6e"));
        
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

        rgb[ThemeKeys.KeyTipTextWeight] = TextWeight.Normal;

        rgb[ThemeKeys.AnsiBlack] = SolidColorBrush.FromPalette(234);
        rgb[ThemeKeys.AnsiRed] = SolidColorBrush.FromPalette(167);
        rgb[ThemeKeys.AnsiGreen] = SolidColorBrush.FromPalette(71);
        rgb[ThemeKeys.AnsiYellow] = SolidColorBrush.FromPalette(136);
        rgb[ThemeKeys.AnsiBlue] = SolidColorBrush.FromPalette(68);
        rgb[ThemeKeys.AnsiMagenta] = SolidColorBrush.FromPalette(134);
        rgb[ThemeKeys.AnsiCyan] = SolidColorBrush.FromPalette(73);
        rgb[ThemeKeys.AnsiWhite] = SolidColorBrush.FromPalette(146);
        rgb[ThemeKeys.AnsiLightBlack] = SolidColorBrush.FromPalette(60);
        rgb[ThemeKeys.AnsiLightRed] = SolidColorBrush.FromPalette(210);
        rgb[ThemeKeys.AnsiLightGreen] = SolidColorBrush.FromPalette(149);
        rgb[ThemeKeys.AnsiLightYellow] = SolidColorBrush.FromPalette(179);
        rgb[ThemeKeys.AnsiLightBlue] = SolidColorBrush.FromPalette(111);
        rgb[ThemeKeys.AnsiLightMagenta] = SolidColorBrush.FromPalette(141);
        rgb[ThemeKeys.AnsiLightCyan] = SolidColorBrush.FromPalette(117);
        rgb[ThemeKeys.AnsiLightWhite] = SolidColorBrush.FromPalette(254);

        dict.ThemeDictionaries[new ThemeVariantKey(@base, ColorDepth.Ansi256)] = rgb;

        // ReSharper disable once UseObjectOrCollectionInitializer
        
        // (B,Ansi16): hand-picked palette indices — beat the quantizer (spec §1). Pinned for role
        // distinguishability under reverse-video: --text/--bg at the extremes (15/0), --accent/--on-accent on
        // real blue, resting fills→0 vs interactive fills→8 (dark), status hues kept true.
        var ansi16 = new ResourceDictionary();
        ansi16[ThemeKeys.ElevationWell] = Palette(dark ? Ansi.Black : Ansi.LightWhite);
        ansi16[ThemeKeys.ElevationDesktop] = Palette(dark ? Ansi.Black : Ansi.LightWhite);
        ansi16[ThemeKeys.ElevationWindow] = Palette(dark ? Ansi.Black : Ansi.LightWhite);
        ansi16[ThemeKeys.ElevationRaised] = Palette(dark ? Ansi.LightBlack : Ansi.White);
        ansi16[ThemeKeys.ElevationHighest] = Palette(dark ? Ansi.LightBlack : Ansi.White);
        ansi16[ThemeKeys.WindowBackground] = Palette(dark ? Ansi.Black : Ansi.LightWhite);
        ansi16[ThemeKeys.SurfaceBrush] = Palette(dark ? Ansi.LightBlack : Ansi.White);
        ansi16[ThemeKeys.PanelBrush] = Palette(dark ? Ansi.Black : Ansi.LightWhite);
        ansi16[ThemeKeys.ToolBarBrush] = Palette(dark ? Ansi.Black : Ansi.LightWhite);
        ansi16[ThemeKeys.RibbonBrush] = Palette(dark ? Ansi.Black : Ansi.LightWhite);
        ansi16[ThemeKeys.WellBrush] = Palette(dark ? Ansi.Black : Ansi.LightWhite);
        ansi16[ThemeKeys.SelectionBrush] = Palette(Ansi.LightBlue);
        ansi16[ThemeKeys.SelectionInk] = Palette(Ansi.Black);
        ansi16[ThemeKeys.SelectionInactiveBrush] = Palette(Ansi.LightBlack);
        ansi16[ThemeKeys.AlternateRowBrush] = Palette(dark ? Ansi.Black : Ansi.LightWhite);
        ansi16[ThemeKeys.AlternateRowInk] = null;//Palette(Ansi.Black);
        ansi16[ThemeKeys.HoverBrush] = Palette(dark ? Ansi.White : Ansi.LightBlack);
        ansi16[ThemeKeys.OnHoverBrush] = Palette(dark ? Ansi.Black : Ansi.LightWhite);
        ansi16[ThemeKeys.TextBrush] = Palette(dark ? Ansi.LightWhite : Ansi.Black);
        ansi16[ThemeKeys.TextDimBrush] = Palette(dark ? Ansi.LightWhite : Ansi.LightBlack);
        ansi16[ThemeKeys.MutedBrush] = Palette(dark ? Ansi.White :  Ansi.LightBlack);
        ansi16[ThemeKeys.FaintBrush] = Palette(dark ? Ansi.White : Ansi.LightBlack);
        ansi16[ThemeKeys.DisabledBackgroundBrush] = Palette(dark ? Ansi.Black : Ansi.White);
        ansi16[ThemeKeys.DisabledForegroundBrush] = Palette(Ansi.LightBlack);
        ansi16[ThemeKeys.AccentBrush] = Palette(dark ? Ansi.LightBlue : Ansi.Blue);
        ansi16[ThemeKeys.AccentDarkBrush] = Palette(dark ? Ansi.Blue : Ansi.LightBlue);
        ansi16[ThemeKeys.Accent2Brush] = Palette(dark ? Ansi.Blue : Ansi.LightBlue);
        ansi16[ThemeKeys.StatusBarBackground] = Palette(Ansi.White);
        ansi16[ThemeKeys.StatusBarAltBackground] = Palette(Ansi.LightBlack);
        ansi16[ThemeKeys.StatusBarAltForeground] = Palette(Ansi.LightWhite);
        ansi16[ThemeKeys.ButtonForegroundFocus] = Palette(dark ? Ansi.Black : Ansi.LightWhite);
        ansi16[ThemeKeys.ButtonBackgroundFocus] = Palette(dark ? Ansi.LightWhite : Ansi.Black);
        ansi16[ThemeKeys.ButtonForegroundNormal] = Palette(dark ? Ansi.White : Ansi.LightBlack);
        ansi16[ThemeKeys.ButtonBackgroundNormal] = Palette(dark ? Ansi.LightBlack : Ansi.White);
        ansi16[ThemeKeys.InputBackgroundNormal] = Palette(dark ? Ansi.LightBlack : Ansi.White);
        ansi16[ThemeKeys.InputForegroundNormal] = Palette(dark ? Ansi.White : Ansi.LightBlack);
        ansi16[ThemeKeys.InputBackgroundFocus] = Palette(dark ? Ansi.LightBlack : Ansi.White);
        ansi16[ThemeKeys.InputForegroundFocus] = Palette(dark ? Ansi.LightWhite : Ansi.Black);
        ansi16[ThemeKeys.InputBackgroundHover] = Palette(dark ? Ansi.LightBlack : Ansi.White);
        ansi16[ThemeKeys.InputForegroundHover] = Palette(dark ? Ansi.LightWhite : Ansi.Black);
        ansi16[ThemeKeys.MenuBarBackground] = Palette(dark ? Ansi.LightBlack : Ansi.White);
        ansi16[ThemeKeys.MenuAcceleratorHoverForeground] = Palette(Ansi.LightBlack);
        ansi16[ThemeKeys.MenuIconUncheckedHoverForeground] = Palette(Ansi.LightBlack);
        ansi16[ThemeKeys.ListItemForegroundFocus] = Palette(dark ? Ansi.Black : Ansi.LightWhite);
        ansi16[ThemeKeys.ListItemForegroundSelectedInactive] = Palette(Ansi.LightWhite);

        // on-accent dark = Ansi.LightWhite (white): black-on-bright-blue is unreadable on pure-blue palettes (spec §Ansi.Red†).
        ansi16[ThemeKeys.OnAccentBrush] = Palette(dark ? Ansi.Black : Ansi.LightWhite);
        ansi16[ThemeKeys.OnAccentInverseBrush] = Palette(Ansi.Black);
        ansi16[ThemeKeys.AccentInverseBrush] = Palette(dark ? Ansi.Blue : Ansi.LightBlue);
        ansi16[ThemeKeys.InfoBrush] = Palette(dark ? Ansi.LightCyan : Ansi.Cyan);
        ansi16[ThemeKeys.Info2Brush] = Palette(dark ? Ansi.Cyan : Ansi.LightCyan);
        ansi16[ThemeKeys.InfoInverseBrush] = Palette(dark ? Ansi.Cyan : Ansi.LightCyan);
        ansi16[ThemeKeys.CoolBrush] = Palette(dark ? Ansi.LightMagenta : Ansi.Magenta);
        ansi16[ThemeKeys.Cool2Brush] = Palette(dark ? Ansi.Magenta : Ansi.LightMagenta);
        ansi16[ThemeKeys.CoolInverseBrush] = Palette(dark ? Ansi.Magenta : Ansi.LightMagenta);
        ansi16[ThemeKeys.DangerBrush] = Palette(dark ? Ansi.LightRed : Ansi.Red);
        ansi16[ThemeKeys.Danger2Brush] = Palette(dark ? Ansi.Red : Ansi.LightRed);
        ansi16[ThemeKeys.DangerInverseBrush] = Palette(dark ? Ansi.Red : Ansi.LightRed);
        ansi16[ThemeKeys.SuccessBrush] = Palette(dark ? Ansi.LightGreen : Ansi.Green);
        ansi16[ThemeKeys.Success2Brush] = Palette(dark ? Ansi.Green : Ansi.LightGreen);
        ansi16[ThemeKeys.SuccessInverseBrush] = Palette(dark ? Ansi.Green : Ansi.LightGreen);
        ansi16[ThemeKeys.WarningBrush] = Palette(dark ? Ansi.LightYellow : Ansi.Yellow);
        ansi16[ThemeKeys.Warning2Brush] = Palette(dark ? Ansi.Yellow : Ansi.LightYellow);
        ansi16[ThemeKeys.WarningInverseBrush] = Palette(dark ? Ansi.Yellow : Ansi.LightYellow);
        ansi16[ThemeKeys.GreenBrush] = Palette(Ansi.Green);
        ansi16[ThemeKeys.AmberBrush] = Palette(Ansi.Yellow);
        ansi16[ThemeKeys.RedBrush] = Palette(dark ? Ansi.LightRed : Ansi.Red);
        ansi16[ThemeKeys.PurpleBrush] = Palette(dark ? Ansi.LightMagenta : Ansi.Magenta);
        ansi16[ThemeKeys.BorderPen] = Pens.Light.WithBrush(Palette(Ansi.LightBlack));
        ansi16[ThemeKeys.FocusBorderPen] = Pens.Light.WithBrush(Palette(dark ? Ansi.LightBlue : Ansi.Blue));
        ansi16[ThemeKeys.SeparatorPen] = Pens.Double.WithBrush(Palette(dark ? Ansi.LightBlack : Ansi.LightWhite));
        ansi16[ThemeKeys.MenuSeparatorPen] = Pens.Light.WithBrush(Palette(dark ? Ansi.LightWhite : Ansi.Black));
        ansi16[ThemeKeys.FocusPen] = Pens.Double.WithColor(Color.FromPalette(dark ? (byte)Ansi.LightBlue : (byte)Ansi.Blue));
        ansi16[ThemeKeys.TabUnderlinePen] = Pens.Heavy.WithColor(Color.FromPalette(dark ? (byte)Ansi.LightBlue : (byte)Ansi.Blue));
        ansi16[ThemeKeys.SliderFilledPen] = Pens.Heavy.WithColor(Color.FromPalette(dark ? (byte)Ansi.LightBlue : (byte)Ansi.Blue)); // accent
        ansi16[ThemeKeys.SliderTrackPen] = Pens.Heavy.WithColor(Color.FromPalette(Ansi.LightBlack));                                // faint/grey
        ansi16[ThemeKeys.ObscuredOverlayBrush] = Brushes.Transparent;                                                               //Palette(Ansi.LightBlack);
        // ansi16[ThemeKeys.AccessKeyIndicatorBrush] = Palette(dark ? 15 : 0);
        // Ribbon: the descent never ascends to Ansi256, so the strip recess and dropped active-tab fill need explicit
        // Ansi16 indices, or they collapse to the NoColor floor (no fill) on 16-color terminals.
        ansi16[ThemeKeys.RibbonTabStripBrush] = Palette(dark ? 0 : 7);                                                       // recess, tracks Surface/Panel
        ansi16[ThemeKeys.RibbonTabActiveBrush] = Palette(dark ? 0 : 15);                                                     // dropped active fill, tracks the band
        ansi16[ThemeKeys.KeyTipBrush] = Palette(3);                                                                          // amber → yellow
        ansi16[ThemeKeys.KeyTipMatchedBrush] = Palette(8);                                                                   // dimmed matched → bright-black
        ansi16[ThemeKeys.RibbonContextualFillBrush] = Palette(dark ? 0 : 7);                                                 // tinted well, tracks the recess
        ansi16[ThemeKeys.RibbonContextualUnderlinePen] = Pens.Heavy.WithColor(Color.FromPalette(dark ? (byte)13 : (byte)5)); // purple

        ansi16[ThemeKeys.ScrollBarThumbNormalBrush] = Palette(dark ? Ansi.White : Ansi.Black); // purple

        // The (Dark|Light, Ansi16) focus cue is BOLD (preserved verbatim from the whole-flags era —
        // now legible in the pair table for designers to revisit): weight speaks, inverse stays off.
        ansi16[ThemeKeys.InteractiveCueInverse] = false;
        ansi16[ThemeKeys.InteractiveCueWeight] = TextWeight.Bold;
        ansi16[ThemeKeys.InteractiveCueUnderline] = UnderlineStyle.Single;

        ansi16[ThemeKeys.AnsiBlack] = Palette(Ansi.Black);
        ansi16[ThemeKeys.AnsiRed] = Palette(Ansi.Red);
        ansi16[ThemeKeys.AnsiGreen] = Palette(Ansi.Green);
        ansi16[ThemeKeys.AnsiYellow] = Palette(Ansi.Yellow);
        ansi16[ThemeKeys.AnsiBlue] = Palette(Ansi.Blue);
        ansi16[ThemeKeys.AnsiMagenta] = Palette(Ansi.Magenta);
        ansi16[ThemeKeys.AnsiCyan] = Palette(Ansi.Cyan);
        ansi16[ThemeKeys.AnsiWhite] = Palette(Ansi.White);
        ansi16[ThemeKeys.AnsiLightBlack] = Palette(Ansi.LightBlack);
        ansi16[ThemeKeys.AnsiLightRed] = Palette(Ansi.LightRed);
        ansi16[ThemeKeys.AnsiLightGreen] = Palette(Ansi.LightGreen);
        ansi16[ThemeKeys.AnsiLightYellow] = Palette(Ansi.LightYellow);
        ansi16[ThemeKeys.AnsiLightBlue] = Palette(Ansi.LightBlue);
        ansi16[ThemeKeys.AnsiLightMagenta] = Palette(Ansi.LightMagenta);
        ansi16[ThemeKeys.AnsiLightCyan] = Palette(Ansi.LightCyan);
        ansi16[ThemeKeys.AnsiLightWhite] = Palette(Ansi.LightWhite);

        ansi16[ThemeKeys.AccentBorderPen] = Pens.Heavy.WithBrush(Palette(dark ? Ansi.LightBlue : Ansi.Blue));
        ansi16[ThemeKeys.CoolBorderPen] = Pens.Heavy.WithBrush(Palette(dark ? Ansi.LightMagenta : Ansi.Magenta));
        ansi16[ThemeKeys.InfoBorderPen] = Pens.Heavy.WithBrush(Palette(dark ? Ansi.LightCyan : Ansi.Cyan));
        ansi16[ThemeKeys.SuccessBorderPen] = Pens.Heavy.WithBrush(Palette(dark ? Ansi.LightGreen : Ansi.Green));
        ansi16[ThemeKeys.WarningBorderPen] = Pens.Heavy.WithBrush(Palette(dark ? Ansi.LightYellow : Ansi.Yellow));
        ansi16[ThemeKeys.DangerBorderPen] = Pens.Heavy.WithBrush(Palette(dark ? Ansi.LightRed : Ansi.Red));

        dict.ThemeDictionaries[new ThemeVariantKey(@base, ColorDepth.Ansi16)] = ansi16;
    }

    private static SolidColorBrush Palette(int index) => new(Color.FromPalette((byte)index));

    private static class Ansi
    {
        public const int Black = 0;
        public const int Red = 1;
        public const int Green = 2;
        public const int Yellow = 3;
        public const int Blue = 4;
        public const int Magenta = 5;
        public const int Cyan = 6;
        public const int White = 7;
        public const int LightBlack = 8;
        public const int LightRed = 9;
        public const int LightGreen = 10;
        public const int LightYellow = 11;
        public const int LightBlue = 12;
        public const int LightMagenta = 13;
        public const int LightCyan = 14;
        public const int LightWhite = 15;
    }
}
