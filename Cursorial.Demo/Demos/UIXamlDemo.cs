using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using Cursorial.Input;
using Cursorial.Output;
// Rect (the IBrush.ColorAt bounds type)
using Cursorial.UI;
using Cursorial.UI.Controls;
// INameScope
using Cursorial.UI.Input;
using Cursorial.UI.Xaml;

// ReSharper disable CheckNamespace

// Phase-6 (Fork C) showcase: the ENTIRE control tree is loaded at runtime from an embedded .xaml string
// by Cursorial.UI.Xaml — the live proof that declarative UI works. The XAML carries everything the loader
// learned to do:
//   • {StaticResource} brushes from a <DockPanel.Resources> chain,
//   • a <ControlTemplate> with a {TemplateBinding} part,
//   • themed Buttons / CheckBoxes / RadioButtons with access-key mnemonics (Content="_OK" → AccessText),
//   • {Binding}s onto a view-model (the status line + a live caption),
//   • x:Name parts wired back into the demo through the document name scope.
// It then runs on the REAL UIApplication frame loop against the live terminal — same loop the hand-built
// uicontrols demo uses; the only difference is the tree came from XAML, not C#.
//   Tab / Shift+Tab  cycle focus,  Space / Enter  activate the focused control,  Alt+<letter>  fires the
//   access key,  click  focuses + activates,  wheel / arrows  scroll,  't'  cycles the color tier,  'd'
//   flips dark/light,  q / Esc / Ctrl+C  exits.
internal sealed class UIXamlDemo : IDemo
{
    public string Name => "uixaml";
    public IReadOnlyList<string> Aliases => ["uix"];
    public string Description => "Declarative UI loaded from an embedded .xaml resource by Cursorial.UI.Xaml (resources + ControlTemplate + bindings + access keys), run on the real frame loop (Tab focus, Alt mnemonics, 't' tier, 'd' dark/light).";

    // The embedded resource name: <RootNamespace>.<dotted relative path>. The .csproj embeds Resources/*.xaml.
    private const string XamlResourceName = "Cursorial.Demo.Resources.uixaml-demo.xaml";

    public async Task RunAsync(string argument)
    {
        Console.WriteLine("UI XAML demo. The whole window is loaded from an embedded .xaml resource at " +
                          "runtime. Opening alt screen — Tab cycles focus, Space/Enter or Alt+<letter> " +
                          "activates, the wheel/arrows scroll, 't' cycles the color tier, 'd' flips " +
                          "dark/light; q / Esc / Ctrl+C exits.");

        // No app-assembly type registration needed: the document names only built-in controls plus
        // SolidColorBrush, which lives in Cursorial.Drawing.Media — already in the default xmlns map.

        var app = UIApplication.CreateBuilder()
            .WithFrameRate(60)
            .Build();

        var controller = new Controller(app);
        try
        {
            await app.RunAsync(controller.BuildTree);
        }
        finally
        {
            await app.DisposeAsync(); // clears the thread-local Current for the next demo run
        }
    }

    private sealed class Controller(UIApplication app)
    {
        private readonly DemoVm _vm = new() { Caption = "edit me via a binding", Status = "" };

        private Button _ok = null!;
        private Button _cancel = null!;
        private Button _apply = null!;
        private CheckBox _airplane = null!;
        private CheckBox _wifi = null!;
        private CheckBox _statusCheck = null!;
        private RadioButton _low = null!, _med = null!, _high = null!;
        private string _lastAction = "(none)";

        public UIElement BuildTree()
        {
            // ── THE P6 PAYOFF: load the entire tree from the embedded XAML string. ──
            // No app skin styles: the controls draw entirely from the cell-faithful default theme
            // (:pointerover / :focus / :pressed all come from the BuiltIn control themes now), so the demo
            // shows the default look unmasked — and 'd'/'t' re-skin it live.
            var root = (DockPanel)XamlLoader.Shared.Load(LoadEmbeddedXaml());

            // The {Binding}s on the status line + the live caption resolve against this view-model.
            root.DataContext = _vm;

            // Reach the x:Name'd parts through the document name scope and wire behavior.
            var scope = NameScope.GetNameScope(root)
                ?? throw new InvalidOperationException("The loaded document carried no name scope.");

            _ok = Named<Button>(scope, "Ok");
            _cancel = Named<Button>(scope, "Cancel");
            _apply = Named<Button>(scope, "Apply");
            _ok.Click += (_, _) => Action("OK");
            _cancel.Click += (_, _) => Action("Cancel");
            _apply.Click += (_, _) => Action("Apply");

            _airplane = Named<CheckBox>(scope, "Airplane");
            _wifi = Named<CheckBox>(scope, "Wifi");
            _statusCheck = Named<CheckBox>(scope, "StatusCheck");
            _airplane.Click += (_, _) => Action($"Airplane = {_airplane.IsChecked}");
            _wifi.Click += (_, _) => Action($"Wi-Fi = {_wifi.IsChecked}");
            _statusCheck.Click += (_, _) => Action($"Status = {_statusCheck.IsChecked?.ToString() ?? "null"}");

            _low = Named<RadioButton>(scope, "Low");
            _med = Named<RadioButton>(scope, "Med");
            _high = Named<RadioButton>(scope, "High");
            _low.Click += (_, _) => Action("quality = Low");
            _med.Click += (_, _) => Action("quality = Medium");
            _high.Click += (_, _) => Action("quality = High");

            root.AddHandler(UIElement.KeyDownEvent, OnRootKeyDown);
            root.AddHandler(UIElement.GotFocusEvent, (_, _) => UpdateStatus());

            UpdateStatus();
            return root;
        }

        private static T Named<T>(INameScope scope, string name) where T : UIElement
            => (T)(scope.Find(name) ?? throw new InvalidOperationException($"x:Name '{name}' was not found in the loaded tree."));

        // ───────────────────────────── routed key handling ─────────────────────────────

        private void OnRootKeyDown(object? sender, KeyEventArgs e)
        {
            if ((e.Modifiers & KeyModifiers.Control) != 0)
                return;

            switch (e.Key)
            {
                case Key.Escape:
                    app.Shutdown();
                    e.Handled = true;
                    return;

                case Key.Character when e.Text.Length > 0 && (e.Modifiers & KeyModifiers.Alt) == 0:
                    switch (char.ToLowerInvariant(e.Text.Span[0]))
                    {
                        case 'q':
                            app.Shutdown();
                            e.Handled = true;
                            return;

                        case 't':
                            CycleColorTier();
                            e.Handled = true;
                            return;

                        case 'd':
                            app.RequestedThemeBase = app.ActualThemeVariant.IsDark ? ThemeBase.Light : ThemeBase.Dark;
                            Action($"theme base → {app.ActualThemeVariant.Base}");
                            e.Handled = true;
                            return;
                    }

                    break;
            }
        }

        private void CycleColorTier()
        {
            app.RequestedColorTier = app.RequestedColorTier switch
                                     {
                                         null                 => ColorDepth.Ansi256,
                                         ColorDepth.Truecolor => ColorDepth.Ansi256,
                                         ColorDepth.Ansi256   => ColorDepth.Ansi16,
                                         ColorDepth.Ansi16    => ColorDepth.NoColor,
                                         _                    => ColorDepth.Truecolor
                                     };
            Action($"color tier → {app.ActualThemeVariant.Tier}");
        }

        private void Action(string what)
        {
            _lastAction = what;
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            var focused = app.FocusManager.FocusedElement;

            var focusName = focused switch
                            {
                                ContentControl { Content: string s } => s.Replace("_", string.Empty),
                                {} other                             => other.GetType().Name,
                                _                                    => "none"
                            };

            // The status line is a {Binding} onto _vm.Status — writing the VM flows through the binding to
            // the bound TextBlock on the next frame (the live proof the loader produced a real binding).
            _vm.Status = $" focus: {focusName,-10} · last: {_lastAction,-16} · theme: {app.ActualThemeVariant}" +
                         "  —  Tab focus, Alt+<letter> mnemonics, t tier, d dark/light, q quit";
        }

        private static string LoadEmbeddedXaml()
        {
            var assembly = typeof(UIXamlDemo).Assembly;
            using var stream = assembly.GetManifestResourceStream(XamlResourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded XAML resource '{XamlResourceName}' was not found. Available: " +
                    string.Join(", ", assembly.GetManifestResourceNames()));
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    /// <summary>The {Binding} oracle for the XAML document (the status line + the live caption).</summary>
    [SuppressMessage("ReSharper", "UnusedMember.Local")]
    private sealed class DemoVm : INotifyPropertyChanged
    {
        private string? _caption;
        private string? _status;

        public string? Caption { get => _caption; set { _caption = value; Raise(); } }
        public string? Status { get => _status; set { _status = value; Raise(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
