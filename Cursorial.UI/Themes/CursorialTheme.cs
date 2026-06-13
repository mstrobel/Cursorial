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
        return dict;
    }

    private static void Populate(ResourceDictionary dict)
    {
        // Each base × tier sub-dictionary carries the color-bearing palette spine. No color-bearing
        // value goes into a base-only (B,·) key (the NoColor-safety contract, CD8/C96).
        AddTierPalette(dict, ThemeBase.Dark);
        AddTierPalette(dict, ThemeBase.Light);

        // Themeable glyph triples (design doc §12.7, spec line 660): the true-ASCII defaults — they
        // render identically on every terminal (zero ambiguous-width risk). A consumer (or a future
        // caps-unicode dictionary) may shadow these at a nearer scope to opt up to Unicode. Keyed at
        // the dictionary top level (capability-driven, not color-tier-driven).
        dict[ThemeKeys.CheckBoxGlyphs] = new GlyphSet("[ ]", "[x]", "[-]");
        // The indeterminate "(-)" is distinct from unchecked "( )" so an explicitly-set IsChecked=null
        // radio is visually distinguishable (doc §12.7 — only an explicit set reaches indeterminate).
        dict[ThemeKeys.RadioGlyphs] = new GlyphSet("( )", "(*)", "(-)");
        dict[ThemeKeys.ScrollArrowGlyphs] = new GlyphSet("^", "v");

        // The Type-keyed control themes (S8 content authored into S7's structure, CD30): selector-less
        // Styles rooted at '^', armed at ControlTheme(0). Their templates + pseudo-class child rules
        // ship the default look for every P5 control.
        ControlThemes.Populate(dict);

        // (·,NoColor): attribute-only fallbacks that win on monochrome — no color, no stranded RGB.
        var noColor = new ResourceDictionary();
        noColor[ThemeKeys.SurfaceBrush] = new SolidColorBrush(Colors.Default);
        noColor[ThemeKeys.TextBrush] = new SolidColorBrush(Colors.Default);
        noColor[ThemeKeys.AccentBrush] = new SolidColorBrush(Colors.Default);
        noColor[ThemeKeys.BorderPen] = Pens.Ascii;
        noColor[ThemeKeys.FocusPen] = Pens.Ascii.WithWeight(StrokeWeight.Heavy);
        noColor[ThemeKeys.ObscuredOverlayBrush] = new SolidColorBrush(Colors.Default);
        noColor[ThemeKeys.AccessKeyUnderlineBrush] = new SolidColorBrush(Colors.Default);
        dict.ThemeDictionaries[new ThemeVariantKey(null, ColorDepth.NoColor)] = noColor;
    }

    private static void AddTierPalette(ResourceDictionary dict, ThemeBase @base)
    {
        var dark = @base == ThemeBase.Dark;

        // (B,Ansi256): RGB brushes — served at Truecolor too (descent never ascends; CD8).
        var rgb = new ResourceDictionary();
        rgb[ThemeKeys.SurfaceBrush] = new SolidColorBrush(dark ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Color.FromRgb(0xF5, 0xF5, 0xF5));
        rgb[ThemeKeys.TextBrush] = new SolidColorBrush(dark ? Color.FromRgb(0xE0, 0xE0, 0xE0) : Color.FromRgb(0x20, 0x20, 0x20));
        rgb[ThemeKeys.AccentBrush] = new SolidColorBrush(dark ? Color.FromRgb(0x66, 0xD9, 0xEF) : Color.FromRgb(0x00, 0x66, 0xCC));
        rgb[ThemeKeys.BorderPen] = new Pen(dark ? Color.FromRgb(0x55, 0x55, 0x55) : Color.FromRgb(0xAA, 0xAA, 0xAA));
        rgb[ThemeKeys.FocusPen] = new Pen(dark ? Color.FromRgb(0x66, 0xD9, 0xEF) : Color.FromRgb(0x00, 0x66, 0xCC)) { Weight = StrokeWeight.Heavy };
        rgb[ThemeKeys.ObscuredOverlayBrush] = new SolidColorBrush(Color.FromRgba(0, 0, 0, 0x60));
        rgb[ThemeKeys.AccessKeyUnderlineBrush] = new SolidColorBrush(dark ? Color.FromRgb(0xE0, 0xE0, 0xE0) : Color.FromRgb(0x20, 0x20, 0x20));
        dict.ThemeDictionaries[new ThemeVariantKey(@base, ColorDepth.Ansi256)] = rgb;

        // (B,Ansi16): hand-picked palette colors + ASCII-glyph pens — beats the quantizer.
        var ansi16 = new ResourceDictionary();
        ansi16[ThemeKeys.SurfaceBrush] = new SolidColorBrush(Color.FromPalette(dark ? (byte)0 : (byte)15));
        ansi16[ThemeKeys.TextBrush] = new SolidColorBrush(Color.FromPalette(dark ? (byte)7 : (byte)0));
        ansi16[ThemeKeys.AccentBrush] = new SolidColorBrush(Color.FromPalette(dark ? (byte)14 : (byte)4)); // bright cyan / blue
        ansi16[ThemeKeys.BorderPen] = Pens.Ascii.WithColor(Color.FromPalette(8));
        ansi16[ThemeKeys.FocusPen] = Pens.Ascii.WithColor(Color.FromPalette(dark ? (byte)14 : (byte)4)).WithWeight(StrokeWeight.Heavy);
        ansi16[ThemeKeys.ObscuredOverlayBrush] = new SolidColorBrush(Color.FromPalette(8));
        ansi16[ThemeKeys.AccessKeyUnderlineBrush] = new SolidColorBrush(Color.FromPalette(dark ? (byte)7 : (byte)0));
        dict.ThemeDictionaries[new ThemeVariantKey(@base, ColorDepth.Ansi16)] = ansi16;
    }
}
