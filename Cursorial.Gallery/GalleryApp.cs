using Cursorial.Gallery.ViewModels;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
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

    /// <summary>Loads + binds the shell. The caller owns app-level wiring (e.g. the quit keys).</summary>
    public static UIElement BuildRoot()
    {
        EnsureRegistered();

        var root = (DockPanel)XamlLoader.Shared.Load(LoadEmbedded(ShellResource));
        var vm = new ShellViewModel();

        root.DataContext = vm;

        StyleDebugDiagnostics.DiagnosticEmitted += (c, m) => vm.Diagnostics.Add($"[Style] {c}: {m}");

        ControlDiagnostics.DiagnosticRaised += d => vm.Diagnostics.Add(
                                                   $"[Control  ] {d.Kind}: {d.Message} " +
                                                   $"({d.Element?.GetType().Name}" +
                                                   (d.Element is { Name: { Length: > 0 } n } ? $"#{n})" : ")"));

        BindingDiagnostics.TraceEmitted += d => vm.Diagnostics.Add(
                                               $"[Binding  ] {d.Level} - {d.Kind}: {d.Message} " +
                                               $"(Target={d.TargetDescription}; Path={d.Path})");

        LayoutDiagnostics.DiagnosticRaised += d => vm.Diagnostics.Add(
                                                  $"[Layout   ] {d.Kind}: {d.Message} ({FormatElement(d.Element)})");

        AnimationDiagnostics.TrackError += e => vm.Diagnostics.Add(
                                               $"[Animation] {FormatElement(e.Scope)}: {e.Message} " +
                                               $"({e.Track.TargetProperty?.Name})");

        UIDiagnostics.RejectedValue += (t, p, v) => vm.Diagnostics.Add(
                                           $"[Rejected ] {FormatElement(t)}.{p.Name} = {v}");

        return root;
    }

    private static string FormatElement(UIObject? element)
    {
        if (element is null) return "<null>";
        var name = (element as UIElement)?.Name;
        return $"{element.GetType().Name}" + (name is { Length: > 0 } ? $"#{name})" : "");
    }

    /// <summary>Registers the gallery assembly with the XAML schema context once, so <c>using:Cursorial.Gallery.ViewModels</c>
    /// resolves the page view-model types at load time.</summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
            XamlSchemaContext.Default.RegisterAssembly(typeof(ShellViewModel).Assembly);
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
