using Cursorial.UI.Xaml;

namespace Cursorial.UI.Themes.Xaml;

/// <summary>
/// The data-shipped XAML theme (ARCH-1; design doc §4.11): the BuiltIn control themes authored declaratively
/// in embedded <c>.xaml</c> and loaded at runtime through <see cref="XamlLoader"/>. Assign the result to
/// <c>UIApplication.Theme</c> — it layers over the code-first <see cref="CursorialTheme"/> <c>BuiltIn</c>
/// backstop (§2237), so a partial theme overrides only the controls it defines and the rest fall through.
/// As of P9-W5 the control set is at parity with the code-first themes — the button family plus every P9
/// control (items/list/menu/tab/progress/textbox/tooltip/separator); only <c>Window</c> stays code-first (its
/// chrome's active/inactive look is code-driven, not declarable).
/// </summary>
public static class CursorialXamlTheme
{
    private const string ControlsResource = "Cursorial.UI.Themes.Xaml.Themes.Controls.xaml";
    private const string PaletteResource = "Cursorial.UI.Themes.Xaml.Themes.Palette.xaml";
    private const string StylesResource = "Cursorial.UI.Themes.Xaml.Themes.Styles.xaml";
    private static readonly Uri ControlsSource = new("cursorial-themes://controls.xaml");
    private static readonly Uri PaletteSource = new("cursorial-themes://palette.xaml");
    private static readonly Uri StylesSource = new("cursorial-themes://styles.xaml");

    /// <summary>
    /// Loads the XAML control themes into a fresh <see cref="ResourceDictionary"/> (suitable for
    /// <c>UIApplication.Theme</c>). Throws if the embedded resource is missing or the XAML fails to parse.
    /// </summary>
    public static ResourceDictionary LoadControls()
        => (ResourceDictionary)XamlLoader.Shared.Load(ReadResource(ControlsResource), ControlsSource);

    /// <summary>
    /// Loads the XAML palette spine — the (ThemeBase × ColorDepth) <c>ThemeDictionaries</c> of role-token
    /// brushes + chrome pens (the data twin of <see cref="CursorialTheme"/>'s tier palette). Throws if the
    /// embedded resource is missing or the XAML fails to parse.
    /// </summary>
    public static ResourceDictionary LoadPalette()
        => (ResourceDictionary)XamlLoader.Shared.Load(ReadResource(PaletteResource), PaletteSource);

    /// <summary>
    /// Loads the theme-styles channel (design doc §11.8 #3) — the caps-unicode / caps-nocolor selector styles
    /// authored in <c>&lt;ResourceDictionary.Styles&gt;</c>, consumed from <c>UIApplication.Theme</c> at
    /// <c>Theme(2)</c> (R2/B13). The returned dictionary's <c>Styles</c> slot is the channel;
    /// <see cref="LoadTheme"/> loads it as the theme root so the slot is top-level.
    /// </summary>
    public static ResourceDictionary LoadStyles()
        => (ResourceDictionary)XamlLoader.Shared.Load(ReadResource(StylesResource), StylesSource);

    /// <summary>
    /// The complete data-shipped theme: the <see cref="LoadStyles"/> theme-styles channel as the ROOT (so its
    /// <c>Styles</c> slot is the one the StyleEngine reads at <c>Theme(2)</c>), with the <see cref="LoadPalette"/>
    /// spine and <see cref="LoadControls"/> templates/glyphs merged under it. Assign to <c>UIApplication.Theme</c>
    /// for a XAML-authored theme that supplies its own palette, controls, AND caps-* styles. BuiltIn remains the
    /// final fallback (its framework rules — e.g. the access-key cue — still apply where the data theme is silent).
    /// </summary>
    public static ResourceDictionary LoadTheme()
    {
        var theme = LoadStyles();                    // ROOT: carries the top-level Styles slot the engine reads
        theme.MergedDictionaries.Add(LoadPalette());
        theme.MergedDictionaries.Add(LoadControls());
        return theme;
    }

    private static string ReadResource(string name)
    {
        var assembly = typeof(CursorialXamlTheme).Assembly;
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded theme resource '{name}' was not found. Available: " +
                string.Join(", ", assembly.GetManifestResourceNames()));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
