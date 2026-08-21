using Cursorial.Gallery.ViewModels;
using Cursorial.Gallery.Views;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
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
    // The generator's embed target pins manifest names to the dotted relative path (the
    // cursorial:// scheme's lookup): Views/Shell.xaml → Views.Shell.xaml.
    private const string ShellResource = "Views.Shell.xaml";

    private static int _registered;

    /// <summary>Loads + binds the shell. The caller owns app-level wiring (e.g., the quit keys).</summary>
    public static UIElement BuildRoot(UIApplication app)
    {
        EnsureRegistered();

        // var root = (DockPanel)XamlLoader.Shared.Load(LoadEmbedded(ShellResource));
        var root = new Shell();
        var vm = new ShellViewModel(app);

        root.DataContext = vm;

        // The Ribbon's File tab raises BackstageRequested (bubbling) on activation — the gallery builds + hosts a real
        // Backstage (BackstageHost picks a full-screen Window or a File-anchored menu Popup per the page's DisplayMode
        // toggle). `opening` (a captured guard) drops a re-entrant File tap while a Backstage is already up.
        var opening = false;
        root.AddHandler(Ribbon.BackstageRequestedEvent, async (_, e) =>
        {
            if (opening || vm.SelectedPage is not RibbonViewModel ribbonVm || e.Source is not { } anchor)
                return;

            opening = true;
            try
            {
                var backstage = BuildBackstage(ribbonVm);
                ribbonVm.ReportBackstage("File → Backstage opened (◂ or Esc to return).");
                await BackstageHost.ShowAsync(backstage, anchor);
                ribbonVm.ReportBackstage("Backstage closed — back to the document.");
            }
            finally
            {
                opening = false;
            }
        });

        return root;
    }

    // Builds the gallery's Backstage: a rail of File destinations (each a titled detail pane) in the mode the page's
    // toggle chose. Selection reports into the page status so the live navigation is visible.
    private static Backstage BuildBackstage(RibbonViewModel vm)
    {
        var backstage = new Backstage { DisplayMode = vm.BackstageMode };
        backstage.Items.Add(Destination("_New", "Start a new, empty document.", vm));
        backstage.Items.Add(Destination("_Open", "Open an existing document from disk.", vm));
        backstage.Items.Add(Destination("_Save", "Save the current document.", vm));
        backstage.Items.Add(Destination("Save _As", "Save the document under a new name.", vm));
        backstage.Items.Add(Destination("_Export", "Export the document to another format.", vm));
        backstage.Items.Add(Destination("_Print", "Print the document.", vm));
        backstage.Items.Add(new BackstageItem { Header = "──────", IsSelectable = false }); // a non-selectable rule
        backstage.Items.Add(Destination("_Account", "Your account, sign-in, and connected services.", vm));
        backstage.Items.Add(Destination("_Options", "Application options and preferences.", vm));

        backstage.SelectionChanged += (_, _) =>
        {
            if (backstage.SelectedItem is BackstageItem { Header: string header })
                vm.ReportBackstage($"Backstage → {header.Replace("_", string.Empty)}");
        };
        return backstage;
    }

    // One rail destination: the Header is the rail label (access-key folded); the Content is the detail pane shown when
    // it is selected (a title over a description over an action button). Invoking the action button runs its command AND
    // closes the Backstage (the Backstage's detail-pane close-on-invoke — the Office "act and return" model).
    private static BackstageItem Destination(string header, string detail, RibbonViewModel vm)
    {
        var name = header.Replace("_", string.Empty);
        var title = new TextBlock { Text = name, Margin = new Margins(0, 0, 0, 1), TextWrapping = WrapMode.WordWrap };
        var body = new TextBlock { Text = detail, TextWrapping = WrapMode.WordWrap };
        var action = new BarButton
                     {
                         Content = $"◆ {name}",
                         Margin = new Margins(0, 1, 0, 0),
                         Command = new BarCommand(() => vm.ReportBackstage($"{name} invoked — returned to the document.")),
                     };
        var pane = new StackPanel { Orientation = Orientation.Vertical, Margin = new Margins(1, 0) };
        pane.Children.Add(title);
        pane.Children.Add(body);
        pane.Children.Add(action);
        return new BackstageItem { Header = header, Content = pane };
    }

    /// <summary>Registers the gallery assembly with the XAML schema context once, so <c>using:Cursorial.Gallery.ViewModels</c>
    /// resolves the page view-model types at load time.</summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
            XamlSchemaContext.Default.RegisterAssembly(typeof(ShellViewModel));
            XamlSchemaContext.Default.RegisterAssembly(typeof(Cursorial.UI.DataViews.DataGrid));
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
