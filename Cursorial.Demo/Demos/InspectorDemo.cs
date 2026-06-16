using Cursorial.Input;
using Cursorial.Rendering;            // Margins
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;
using Cursorial.UI.Xaml;

// ReSharper disable CheckNamespace

// P9.8 — the XAML style-inspector (design doc §3.9 / proposal §2.8 "style inspector overlay"): load a .xaml
// file, render it on the real frame loop, then inspect the element under the cursor — for it, the overlay
// lists every property with a live, non-default contribution (UIObject.GetSetProperties) and its full
// provenance (StyleDiagnostics.Explain: value ← layer/lane + selector + the packed sort key, winning vs
// shadowed). "Debuggability made concrete": the report is exact and cheap because the styling slots are
// objects, not a re-run of a matching algorithm.
//   'o'      open a XAML source (a bundled sample from the list, or a file path you type),
//   hover    inspect the element under the pointer (motion terminals),
//   Tab      move focus through the loaded tree — the focused element is inspected (keyboard fallback),
//   't'/'d'  cycle the color tier / flip dark-light (the loaded tree re-skins; the inspector re-reads),
//   q / Esc  exit.
internal sealed class InspectorDemo : IDemo
{
    public string Name => "inspect";
    public IReadOnlyList<string> Aliases => ["inspector", "xaml"];

    public string Description =>
        "XAML style-inspector (P9.8): open a .xaml (bundled sample or a file path), render it, then hover/Tab an " +
        "element to see every non-default property and its provenance — value ← slot + selector + sort key (StyleDiagnostics.Explain).";

    public async Task RunAsync(string argument)
    {
        Console.WriteLine("XAML inspector. Opening alt screen — press 'o' to open a XAML source (a bundled sample " +
                          "or a file path), then HOVER or Tab to an element to inspect its non-default properties and " +
                          "their provenance (value ← slot + selector). 't' cycles the color tier, 'd' flips dark/light; " +
                          "q / Esc exits.");

        var app = UIApplication.CreateBuilder().WithFrameRate(60).Build();
        var controller = new Controller(app);

        try
        {
            await app.RunAsync(controller.BuildDesktop);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    // Bundled samples — declarative trees with a mix of LOCAL values (Width, explicit Background/BorderPen),
    // THEMED contributions (the control themes' SurfaceBrush/well/etc.), and INHERITED foreground, so the
    // inspector has rich provenance to show. The default xmlns maps the UI/Controls/Drawing.Media types.
    private static readonly (string Label, string Xaml)[] Samples =
    [
        ("Settings panel", """
            <DockPanel xmlns="https://cursorial.dev/ui"
                       xmlns:x="https://cursorial.dev/xaml"
                       Background="{DynamicResource {x:Static ThemeKeys.WindowBackground}}"
                       TextElement.Foreground="{DynamicResource {x:Static ThemeKeys.TextBrush}}">
                <TextBlock DockPanel.Dock="Top" Text=" Settings " Margin="1,0"/>
                <StackPanel Margin="2,1" Spacing="1">
                    <CheckBox Content="_Airplane mode"/>
                    <CheckBox Content="_Wi-Fi" IsChecked="True"/>
                    <CheckBox Content="_Bluetooth"/>
                    <StackPanel Orientation="Horizontal" Spacing="2" Margin="0,1,0,0">
                        <Button Content="_OK" IsDefault="True" Width="10"/>
                        <Button Content="_Cancel"/>
                    </StackPanel>
                </StackPanel>
            </DockPanel>
            """),
        ("Login form", """
            <StackPanel xmlns="https://cursorial.dev/ui"
                        xmlns:x="https://cursorial.dev/xaml"
                        Background="{DynamicResource {x:Static ThemeKeys.WindowBackground}}"
                        TextElement.Foreground="{DynamicResource {x:Static ThemeKeys.TextBrush}}"
                        Margin="2,1" Spacing="1">
                <TextBlock Text="Sign in"/>
                <Label Content="User _name:"/>
                <TextBox Placeholder="username" Width="24"/>
                <Label Content="_Password:"/>
                <TextBox Placeholder="••••••••" Width="24"/>
                <CheckBox Content="_Remember me"/>
                <StackPanel Orientation="Horizontal" Spacing="2" Margin="0,1,0,0">
                    <Button Content="_Sign in" IsDefault="True"/>
                    <Button Content="_Cancel"/>
                </StackPanel>
            </StackPanel>
            """),
    ];

    private sealed class Controller(UIApplication app)
    {
        private TextBlock _status = null!;
        private Border _canvas = null!;            // hosts the loaded tree (or the placeholder / error)
        private StackPanel _inspectorContent = null!;
        private UIElement? _lastInspected;
        private string _loaded = "(nothing)";

        public UIElement BuildDesktop()
        {
            var root = new DockPanel();
            root.SetResourceReference(Panel.BackgroundProperty, ThemeKeys.WindowBackground);
            root.AddHandler(UIElement.KeyDownEvent, OnHotkey);

            var header = new Border { Child = new TextBlock(" Cursorial.UI — P9.8 XAML inspector   ·   o open   ·   hover / Tab to inspect   ·   t tier · d dark/light · q quit") };
            header.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SurfaceBrush);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var statusBar = new Border();
            statusBar.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SurfaceBrush);
            _status = new TextBlock();
            statusBar.Child = _status;
            DockPanel.SetDock(statusBar, Dock.Bottom);
            root.Children.Add(statusBar);

            // The inspector panel (docked right): a scrollable provenance report for the element under the cursor.
            _inspectorContent = new StackPanel { Margin = new Margins(1, 0) };
            var inspectorPanel = new Border
            {
                Width = 50,
                BorderPen = Cursorial.Drawing.Media.Pens.Light,
                Title = " inspector ",
                Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _inspectorContent },
            };
            inspectorPanel.SetResourceReference(Border.BorderPenProperty, ThemeKeys.BorderPen);
            DockPanel.SetDock(inspectorPanel, Dock.Right);
            root.Children.Add(inspectorPanel);

            // The canvas (fills the rest): the loaded tree. Hover / focus over it drives the inspector — the
            // handlers are scoped to the canvas, so hovering the inspector panel never inspects itself.
            _canvas = new Border { Child = Placeholder("Press 'o' to open a XAML source.") };
            _canvas.AddHandler(UIElement.MouseMoveEvent, (_, e) => Inspect(e.Source), handledEventsToo: true);
            _canvas.AddHandler(UIElement.GotFocusEvent, (_, e) => Inspect(e.Source));
            root.Children.Add(_canvas);

            Inspect(null);
            UpdateStatus();
            return root;
        }

        private static TextBlock Placeholder(string text) => new($"\n   {text}");

        // ───────────────────────────── keys ─────────────────────────────

        private void OnHotkey(object? sender, KeyEventArgs e)
        {
            if ((e.Modifiers & KeyModifiers.Control) != 0)
                return;

            if (e.Key == Key.Escape)
            {
                app.Shutdown();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Character || e.Text.Length == 0 || (e.Modifiers & KeyModifiers.Alt) != 0)
                return;

            switch (char.ToLowerInvariant(e.Text.Span[0]))
            {
                case 'q':
                    app.Shutdown();
                    e.Handled = true;
                    break;
                case 'o':
                    OpenDialog();
                    e.Handled = true;
                    break;
                case 't':
                    app.RequestedColorTier = app.RequestedColorTier switch
                    {
                        null => Cursorial.Output.ColorDepth.Ansi256,
                        Cursorial.Output.ColorDepth.Truecolor => Cursorial.Output.ColorDepth.Ansi256,
                        Cursorial.Output.ColorDepth.Ansi256 => Cursorial.Output.ColorDepth.Ansi16,
                        Cursorial.Output.ColorDepth.Ansi16 => Cursorial.Output.ColorDepth.NoColor,
                        _ => Cursorial.Output.ColorDepth.Truecolor,
                    };
                    ReinspectLast();
                    e.Handled = true;
                    break;
                case 'd':
                    app.RequestedThemeBase = app.ActualThemeVariant.IsDark ? ThemeBase.Light : ThemeBase.Dark;
                    ReinspectLast();
                    e.Handled = true;
                    break;
            }
        }

        // ───────────────────────────── open + load ─────────────────────────────

        private async void OpenDialog()
        {
            var list = new ListBox { ItemsSource = Samples.Select(s => s.Label).ToArray(), Height = 4 };
            list.SelectedIndex = 0;
            var path = new TextBox { Placeholder = "…or a path to a .xaml file" };

            var open = new Button { Content = "_Open", IsDefault = true };
            var cancel = new Button { Content = "_Cancel", IsCancel = true, Margin = new Margins(1, 0, 0, 0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Margins(0, 1, 0, 0) };
            buttons.Children.Add(open);
            buttons.Children.Add(cancel);

            var body = new StackPanel { Margin = new Margins(1) };
            body.Children.Add(new Label { Content = "_Sample:" });
            body.Children.Add(list);
            body.Children.Add(new TextBlock("\nOr load a file:"));
            body.Children.Add(path);
            body.Children.Add(buttons);

            var dialog = new Window
            {
                Title = "Open XAML",
                Content = body,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Width = 50,
                Height = 15,
                CanResize = false,
            };
            dialog.SetResourceReference(Control.BackgroundProperty, ThemeKeys.PanelBrush);

            open.Click += (_, _) =>
            {
                var typed = path.Text.Trim();
                dialog.Close(typed.Length > 0 ? new OpenChoice(IsFile: true, typed) : new OpenChoice(IsFile: false, list.SelectedItem as string ?? Samples[0].Label));
            };
            cancel.Click += (_, _) => dialog.Close(null);

            if (await dialog.ShowDialogAsync() is OpenChoice choice)
                LoadAndShow(choice);
        }

        private void LoadAndShow(OpenChoice choice)
        {
            string label, xaml;
            try
            {
                if (choice.IsFile)
                {
                    label = Path.GetFileName(choice.Value);
                    if (Directory.Exists(choice.Value))
                    {
                        ShowError($"\"{choice.Value}\" is a directory, not a .xaml file.");
                        return;
                    }

                    xaml = File.ReadAllText(choice.Value);
                }
                else
                {
                    label = choice.Value;
                    xaml = Samples.First(s => s.Label == choice.Value).Xaml;
                }
            }
            catch (Exception ex) // a bad path / unreadable file
            {
                ShowError($"Could not read \"{choice.Value}\":\n   {ex.Message}");
                return;
            }

            try
            {
                var tree = (UIElement)XamlLoader.Shared.Load(xaml);
                _loaded = label;
                _lastInspected = null;
                _canvas.Child = tree;        // render the loaded tree
                Inspect(null);
            }
            catch (Exception ex) // XamlParseException (line+col in the message) / type-resolution / cast
            {
                ShowError($"Failed to load \"{label}\":\n\n   {ex.Message}");
            }

            UpdateStatus();
        }

        private void ShowError(string message)
        {
            _loaded = "(load failed)";
            _lastInspected = null;
            _canvas.Child = new StackPanel { Margin = new Margins(2, 1) };
            ((StackPanel)_canvas.Child!).Children.Add(new TextBlock($"\n⚠ {message}"));
            Inspect(null);
            UpdateStatus();
        }

        // ───────────────────────────── inspect ─────────────────────────────

        private void ReinspectLast()
        {
            var target = _lastInspected;
            _lastInspected = null; // force a rebuild (provenance changed after a theme flip)
            Inspect(target);
        }

        private void Inspect(UIElement? element)
        {
            if (ReferenceEquals(element, _lastInspected))
                return;
            _lastInspected = element;

            _inspectorContent.Children.Clear();

            if (element is null || ReferenceEquals(element, _canvas))
            {
                _inspectorContent.Children.Add(new TextBlock("\n  Hover or Tab to an element\n  in the loaded tree."));
                return;
            }

            var name = string.IsNullOrEmpty(element.Name) ? "" : $" #{element.Name}";
            _inspectorContent.Children.Add(new TextBlock($"‹{element.GetType().Name}›{name}"));

            var properties = element.GetSetProperties().OrderBy(p => p.Name).ToArray();
            _inspectorContent.Children.Add(new TextBlock($"{properties.Length} set propert{(properties.Length == 1 ? "y" : "ies")}:\n"));

            foreach (var property in properties)
            {
                // The winning derivation line (StyleDiagnostics.Explain is one line per contributor, strongest
                // first): "<prop> = <value> <- <Layer>(n) \"<selector>\" … -- winning" (or "<- LocalValue").
                // Guarded: a diagnostic must never crash the thing it inspects — a pathological value ToString()
                // in an arbitrarily-loaded tree degrades to an error line, not an unhandled hover-handler throw.
                string winning;
                var kind = ValueSourceKind.Default;
                try
                {
                    winning = StyleDiagnostics.Explain(element, property).Split('\n', 2)[0];
                    kind = element.GetValueSource(property).Kind; // PD25 within-lane provenance
                }
                catch (Exception ex)
                {
                    winning = $"{property.Name} = (error: {ex.GetType().Name})";
                }

                // Append the within-lane provenance (PD25): "template literal vs template binding vs
                // template resource", "style setter vs When-guarded rule" — the finer origin beyond the lane.
                _inspectorContent.Children.Add(new TextBlock($"{winning}   ·  {kind}"));
            }
        }

        private void UpdateStatus()
            => _status.Text = $" loaded: {_loaded}  ·  theme: {app.ActualThemeVariant}   —   o open · hover/Tab inspect · t tier · d dark/light · q quit";
    }

    private sealed record OpenChoice(bool IsFile, string Value);
}
