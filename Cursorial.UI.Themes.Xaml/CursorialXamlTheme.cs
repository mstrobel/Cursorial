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
    private static readonly Uri ControlsSource = new("cursorial-themes://controls.xaml");

    /// <summary>
    /// Loads the XAML control themes into a fresh <see cref="ResourceDictionary"/> (suitable for
    /// <c>UIApplication.Theme</c>). Throws if the embedded resource is missing or the XAML fails to parse.
    /// </summary>
    public static ResourceDictionary LoadControls()
        => (ResourceDictionary)XamlLoader.Shared.Load(ReadResource(ControlsResource), ControlsSource);

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
