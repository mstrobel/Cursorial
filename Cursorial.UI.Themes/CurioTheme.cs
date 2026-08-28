namespace Cursorial.UI.Themes;

public static class CurioTheme
{
    /// <summary>The XAML control themes (templates + glyphs) — the build-lowered <c>Themes/Controls.xaml</c>.</summary>
    public static ResourceDictionary LoadControls()
        => global::Cursorial.UI.Xaml.Generated.GeneratedXamlLoaders.BuildThemesCurioControls();

    /// <summary>The XAML palette spine — the (ThemeBase × ColorDepth) <c>ThemeDictionaries</c> of role-token brushes
    /// + chrome pens (the data twin of <see cref="CursorialTheme"/>'s tier palette).</summary>
    public static ResourceDictionary LoadPalette()
        => global::Cursorial.UI.Xaml.Generated.GeneratedXamlLoaders.BuildThemesCurioPalette();

    /// <summary>The theme-styles channel (design doc §11.8 #3) — the caps-* selector styles authored in
    /// <c>&lt;ResourceDictionary.Styles&gt;</c>, consumed from <c>UIApplication.Theme</c> at <c>Theme(2)</c>.</summary>
    public static ResourceDictionary LoadStyles()
        => global::Cursorial.UI.Xaml.Generated.GeneratedXamlLoaders.BuildThemesCurioStyles();

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
        var palette = LoadPalette();
        var controls = LoadControls();
        theme.MergedDictionaries.Add(palette);
        theme.MergedDictionaries.Add(controls);
        controls.Seal();
        palette.Seal();
        controls.Seal();
        theme.Seal();
        return theme;
    }

    public static ResourceDictionary Snapshot
    {
        get
        {
            var theme = LoadTheme();
            theme.Seal();
            return theme;
        }
    }

    public static ResourceDictionary Controls
    {
        get
        {
            var controls = LoadControls();
            controls.Seal();
            return controls;
        }
    }

    public static ResourceDictionary Styles
    {
        get
        {
            var styles = LoadStyles();
            styles.Seal();
            return styles;
        }
    }
}
