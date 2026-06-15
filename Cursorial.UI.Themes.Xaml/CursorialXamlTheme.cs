using System;
using System.IO;

using Cursorial.UI;
using Cursorial.UI.Xaml;

namespace Cursorial.UI.Themes.Xaml;

/// <summary>
/// The data-shipped XAML theme (ARCH-1; design doc §4.11): the BuiltIn control themes authored declaratively
/// in embedded <c>.xaml</c> and loaded at runtime through <see cref="XamlLoader"/>. Assign the result to
/// <c>UIApplication.Theme</c> — it layers over the code-first <see cref="CursorialTheme"/> <c>BuiltIn</c>
/// backstop (§2237), so a partial theme overrides only the controls it defines and the rest fall through.
/// Phase 1 ships the Button family; the remaining controls extend <c>Themes/*.xaml</c>.
/// </summary>
public static class CursorialXamlTheme
{
    private const string ControlsResource = "Cursorial.UI.Themes.Xaml.Themes.Controls.xaml";
    private const string PaletteResource = "Cursorial.UI.Themes.Xaml.Themes.Palette.xaml";
    private static readonly Uri ControlsSource = new("cursorial-themes://controls.xaml");
    private static readonly Uri PaletteSource = new("cursorial-themes://palette.xaml");

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
    /// The complete data-shipped theme: the <see cref="LoadPalette"/> spine merged under the
    /// <see cref="LoadControls"/> templates/glyphs (later-merged wins, so controls override nothing the palette
    /// owns). Assign to <c>UIApplication.Theme</c> for a XAML-authored theme that does not lean on
    /// <see cref="CursorialTheme"/> <c>BuiltIn</c> for its palette or controls (the Theme-layer caps-* style
    /// channel still falls through to BuiltIn until R2/B13 lands).
    /// </summary>
    public static ResourceDictionary LoadTheme()
    {
        var theme = new ResourceDictionary();
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
