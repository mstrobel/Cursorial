using Cursorial.Gallery.ViewModels;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Xaml;

namespace Cursorial.Gallery;

/// <summary>
/// Builds the gallery's root view from the embedded <c>Shell.xaml</c> and binds it to a fresh
/// <see cref="ShellViewModel"/>. Shared by <c>Program</c> (the live terminal app) and the headless smoke test, so
/// both exercise the identical XAML-loaded, MVVM-bound tree.
/// </summary>
public static class GalleryApp
{
    // Default MSBuild logical name for Views/Shell.xaml under RootNamespace Cursorial.Gallery.
    private const string ShellResource = "Cursorial.Gallery.Views.Shell.xaml";

    private static int _registered;

    /// <summary>Loads + binds the shell. The caller owns app-level wiring (e.g., the quit keys).</summary>
    public static UIElement BuildRoot()
    {
        EnsureRegistered();

        var root = (DockPanel)XamlLoader.Shared.Load(LoadEmbedded(ShellResource));
        var vm = new ShellViewModel();

        root.DataContext = vm;

        // The Ribbon's File tab raises BackstageRequested (bubbling) on activation — Backstage is a later phase, so
        // the gallery just echoes it into the Ribbon page's status so File reads as a real, activatable command tab.
        root.AddHandler(Ribbon.BackstageRequestedEvent,
            (_, _) => (vm.SelectedPage as RibbonViewModel)?.NotifyBackstageRequested());

        return root;
    }

    /// <summary>Registers the gallery assembly with the XAML schema context once, so <c>using:Cursorial.Gallery.ViewModels</c>
    /// resolves the page view-model types at load time.</summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
            XamlSchemaContext.Default.RegisterAssembly(typeof(ShellViewModel));
    }

    private static string LoadEmbedded(string name)
    {
        var assembly = typeof(GalleryApp).Assembly;
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded XAML resource '{name}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
