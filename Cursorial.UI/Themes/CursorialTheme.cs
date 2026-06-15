using Cursorial.Drawing;
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
        ThemeKeys.SelectionBrush, ThemeKeys.HoverBrush, ThemeKeys.TextBrush, ThemeKeys.TextDimBrush,
        ThemeKeys.MutedBrush, ThemeKeys.FaintBrush, ThemeKeys.DisabledBackgroundBrush,
        ThemeKeys.DisabledForegroundBrush, ThemeKeys.AccentBrush, ThemeKeys.Accent2Brush,
        ThemeKeys.OnAccentBrush, ThemeKeys.GreenBrush, ThemeKeys.AmberBrush, ThemeKeys.RedBrush,
        ThemeKeys.PurpleBrush, ThemeKeys.StatusBarBackground, ThemeKeys.StatusBarAltBackground,
        ThemeKeys.StatusBarAltForeground
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
        noColor[ThemeKeys.FocusPen] = Pens.Ascii.WithWeight(StrokeWeight.Heavy);
        noColor[ThemeKeys.ObscuredOverlayBrush] = defaultBrush;
        noColor[ThemeKeys.AccessKeyUnderlineBrush] = defaultBrush;
        dict.ThemeDictionaries[new ThemeVariantKey(null, ColorDepth.NoColor)] = noColor;
    }

    private static void AddTierPalette(ResourceDictionary dict, ThemeBase @base)
    {
        var dark = @base == ThemeBase.Dark;

        // (B,Ansi256): RGB role tokens — Tokyo-Night, verbatim from the default-theme gallery; served at
        // Truecolor too (descent never ascends; CD8). The cell-faithful spine (design doc §11.8a) — fill +
        // foreground tokens; the two pens are opt-in chrome, not spine members.
        var rgb = new ResourceDictionary();
        rgb[ThemeKeys.WindowBackground] = new SolidColorBrush(dark ? Color.FromHex("#0d0f18") : Color.FromHex("#e6e7ec"));
        rgb[ThemeKeys.SurfaceBrush] = new SolidColorBrush(dark ? Color.FromHex("#24283b") : Color.FromHex("#cbccd1"));
        rgb[ThemeKeys.PanelBrush] = new SolidColorBrush(dark ? Color.FromHex("#222639") : Color.FromHex("#e9e9ed"));
        rgb[ThemeKeys.WellBrush] = new SolidColorBrush(dark ? Color.FromHex("#16161e") : Color.FromHex("#f6f6f8"));
        rgb[ThemeKeys.SelectionBrush] = new SolidColorBrush(dark ? Color.FromHex("#33467c") : Color.FromHex("#a8aecb"));
        // Light --hover nudged off --surface (#cbccd1) so a hovered control reads as a fill (spec §1.1).
        rgb[ThemeKeys.HoverBrush] = new SolidColorBrush(dark ? Color.FromHex("#414868") : Color.FromHex("#bfc0c6"));
        rgb[ThemeKeys.TextBrush] = new SolidColorBrush(dark ? Color.FromHex("#c0caf5") : Color.FromHex("#343b58"));
        rgb[ThemeKeys.TextDimBrush] = new SolidColorBrush(dark ? Color.FromHex("#a9b1d6") : Color.FromHex("#565a6e"));
        rgb[ThemeKeys.MutedBrush] = new SolidColorBrush(dark ? Color.FromHex("#565f89") : Color.FromHex("#9699a3"));
        rgb[ThemeKeys.FaintBrush] = new SolidColorBrush(dark ? Color.FromHex("#414868") : Color.FromHex("#c4c5cc"));
        rgb[ThemeKeys.DisabledBackgroundBrush] = new SolidColorBrush(dark ? Color.FromHex("#1f2335") : Color.FromHex("#dcdde2"));
        rgb[ThemeKeys.DisabledForegroundBrush] = new SolidColorBrush(dark ? Color.FromHex("#565f89") : Color.FromHex("#9699a3"));
        rgb[ThemeKeys.AccentBrush] = new SolidColorBrush(dark ? Color.FromHex("#7aa2f7") : Color.FromHex("#34548a"));
        rgb[ThemeKeys.Accent2Brush] = new SolidColorBrush(dark ? Color.FromHex("#7dcfff") : Color.FromHex("#0f4b6e"));
        rgb[ThemeKeys.OnAccentBrush] = new SolidColorBrush(dark ? Color.FromHex("#0d0f18") : Color.FromHex("#e9e9ed"));
        rgb[ThemeKeys.GreenBrush] = new SolidColorBrush(dark ? Color.FromHex("#9ece6a") : Color.FromHex("#485e30"));
        rgb[ThemeKeys.AmberBrush] = new SolidColorBrush(dark ? Color.FromHex("#e0af68") : Color.FromHex("#8f5e15"));
        rgb[ThemeKeys.RedBrush] = new SolidColorBrush(dark ? Color.FromHex("#f7768e") : Color.FromHex("#8c4351"));
        rgb[ThemeKeys.PurpleBrush] = new SolidColorBrush(dark ? Color.FromHex("#bb9af7") : Color.FromHex("#5a3e8e"));
        rgb[ThemeKeys.StatusBarBackground] = new SolidColorBrush(dark ? Color.FromHex("#24283b") : Color.FromHex("#cbccd1"));
        rgb[ThemeKeys.StatusBarAltBackground] = new SolidColorBrush(dark ? Color.FromHex("#7aa2f7") : Color.FromHex("#34548a"));
        // Branch/alt status text reads on the accent fill → the on-accent ink (was hand-copied from --accent,
        // making light-tier text invisible; dark was the stale pre-fix on-accent #0d0f19). Review finding #3.
        rgb[ThemeKeys.StatusBarAltForeground] = new SolidColorBrush(dark ? Color.FromHex("#0d0f18") : Color.FromHex("#e9e9ed"));
        
        // Opt-in chrome (no shipped control reads these by default): border = faint ink, focus ring = accent heavy.
        rgb[ThemeKeys.BorderPen] = new Pen(dark ? Color.FromHex("#414868") : Color.FromHex("#c4c5cc")) { Corners = CornerStyle.Rounded };
        rgb[ThemeKeys.FocusPen] = new Pen(dark ? Color.FromHex("#7aa2f7") : Color.FromHex("#34548a")) { Weight = StrokeWeight.Heavy };
        rgb[ThemeKeys.ObscuredOverlayBrush] = new SolidColorBrush(Color.FromRgba(0, 0, 0, 0x60));
        rgb[ThemeKeys.AccessKeyUnderlineBrush] = new SolidColorBrush(dark ? Color.FromHex("#c0caf5") : Color.FromHex("#343b58"));
        dict.ThemeDictionaries[new ThemeVariantKey(@base, ColorDepth.Ansi256)] = rgb;

        // (B,Ansi16): hand-picked palette indices — beat the quantizer (spec §1). Pinned for role
        // distinguishability under reverse-video: --text/--bg at the extremes (15/0), --accent/--on-accent on
        // real blue, resting fills→0 vs interactive fills→8 (dark), status hues kept true.
        var ansi16 = new ResourceDictionary();
        ansi16[ThemeKeys.WindowBackground] = Palette(dark ? 0 : 15);
        ansi16[ThemeKeys.SurfaceBrush] = Palette(dark ? 0 : 7);
        ansi16[ThemeKeys.PanelBrush] = Palette(dark ? 0 : 7);
        ansi16[ThemeKeys.WellBrush] = Palette(dark ? 0 : 15);
        ansi16[ThemeKeys.SelectionBrush] = Palette(8);
        ansi16[ThemeKeys.HoverBrush] = Palette(8);
        ansi16[ThemeKeys.TextBrush] = Palette(dark ? 15 : 0);
        ansi16[ThemeKeys.TextDimBrush] = Palette(dark ? 7 : 8);
        ansi16[ThemeKeys.MutedBrush] = Palette(8);
        ansi16[ThemeKeys.FaintBrush] = Palette(dark ? 8 : 7);
        ansi16[ThemeKeys.DisabledBackgroundBrush] = Palette(dark ? 0 : 7);
        ansi16[ThemeKeys.DisabledForegroundBrush] = Palette(8);
        ansi16[ThemeKeys.AccentBrush] = Palette(dark ? 12 : 4);
        ansi16[ThemeKeys.Accent2Brush] = Palette(dark ? 14 : 6);
        ansi16[ThemeKeys.StatusBarBackground] = Palette(dark ? 7 : 15);
        ansi16[ThemeKeys.StatusBarAltBackground] = Palette(dark ? 8 : 7);
        ansi16[ThemeKeys.StatusBarAltForeground] = Palette(15);

        // on-accent dark = 15 (white): black-on-bright-blue is unreadable on pure-blue palettes (spec §1†).
        ansi16[ThemeKeys.OnAccentBrush] = Palette(15);
        ansi16[ThemeKeys.GreenBrush] = Palette(2);
        ansi16[ThemeKeys.AmberBrush] = Palette(3);
        ansi16[ThemeKeys.RedBrush] = Palette(dark ? 9 : 1);
        ansi16[ThemeKeys.PurpleBrush] = Palette(dark ? 13 : 5);
        ansi16[ThemeKeys.BorderPen] = Pens.Ascii.WithColor(Color.FromPalette(8));
        ansi16[ThemeKeys.FocusPen] = Pens.Ascii.WithColor(Color.FromPalette(dark ? (byte)12 : (byte)4)).WithWeight(StrokeWeight.Heavy);
        ansi16[ThemeKeys.ObscuredOverlayBrush] = Palette(8);
        ansi16[ThemeKeys.AccessKeyUnderlineBrush] = Palette(dark ? 15 : 0);
        dict.ThemeDictionaries[new ThemeVariantKey(@base, ColorDepth.Ansi16)] = ansi16;
    }

    private static SolidColorBrush Palette(int index) => new(Color.FromPalette((byte)index));
}
