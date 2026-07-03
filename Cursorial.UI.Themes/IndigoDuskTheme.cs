// ReSharper disable once CheckNamespace
namespace Cursorial.UI.Themes.IndigoDusk;

/// <summary>
/// The data-shipped XAML theme (ARCH-1; design doc §4.11): the BuiltIn control themes authored declaratively
/// in <c>.xaml</c>. As of WS-X5.4 the theme dictionaries are LOWERED at build time — each <c>Themes/*.xaml</c>
/// is a <c>&lt;CursorialXaml&gt;</c> item the X4/X5 generator compiles into a reflection-free
/// <c>GeneratedXamlLoaders.Build…()</c> builder (full lowering, <c>CursorialXamlLowering=full</c>) — so loading
/// a theme is straight-line C# with no runtime parse and no metadata provider (AOT-clean). Assign the result to
/// <c>UIApplication.Theme</c>; it layers over the code-first <see cref="CursorialTheme"/> <c>BuiltIn</c> backstop
/// (§2237), so a partial theme overrides only the controls 4it defines and the rest fall through. The control set
/// is at parity with the code-first themes (the button family plus every P9 control); only <c>Window</c> stays
/// code-first (its chrome's active/inactive look is code-driven, not declarable). The embedded <c>.xaml</c> is
/// retained as the dual-run drift oracle (<c>XamlThemeLoweringTests</c>), not loaded at runtime.
/// </summary>
public static class IndigoDuskTheme
{
    /// <summary>The XAML control themes (templates + glyphs) — the build-lowered <c>Themes/Controls.xaml</c>.</summary>
    public static ResourceDictionary LoadControls()
        => global::Cursorial.UI.Xaml.Generated.GeneratedXamlLoaders.BuildThemesIndigoDuskControls();

    /// <summary>The XAML palette spine — the (ThemeBase × ColorDepth) <c>ThemeDictionaries</c> of role-token brushes
    /// + chrome pens (the data twin of <see cref="CursorialTheme"/>'s tier palette).</summary>
    public static ResourceDictionary LoadPalette()
        => global::Cursorial.UI.Xaml.Generated.GeneratedXamlLoaders.BuildThemesIndigoDuskPalette();

    /// <summary>The theme-styles channel (design doc §11.8 #3) — the caps-* selector styles authored in
    /// <c>&lt;ResourceDictionary.Styles&gt;</c>, consumed from <c>UIApplication.Theme</c> at <c>Theme(2)</c>.</summary>
    public static ResourceDictionary LoadStyles()
        => global::Cursorial.UI.Xaml.Generated.GeneratedXamlLoaders.BuildThemesIndigoDuskStyles();

    /// <summary>
    /// The complete data-shipped theme: the <see cref="LoadStyles"/> theme-styles channel as the ROOT (so its
    /// <c>Styles</c> slot is the one the StyleEngine reads at <c>Theme(2)</c>), with the <see cref="LoadPalette"/>
    /// spine and <see cref="LoadControls"/> templates/glyphs merged under it. Assign to <c>UIApplication.Theme</c>.
    /// BuiltIn remains the final fallback (its framework rules — e.g. the access-key cue — still apply where the
    /// data theme is silent).
    /// </summary>
    public static ResourceDictionary LoadTheme()
    {
        var theme = LoadStyles();                    // ROOT: carries the top-level Styles slot the engine reads
        theme.MergedDictionaries.Add(LoadPalette());
        theme.MergedDictionaries.Add(LoadControls());
        return theme;
    }
}
