using Cursorial.Drawing.Media;      // Pens (the opt-in GroupBox border)
using Cursorial.Input;
using Cursorial.Media;              // ColorDepth
using Cursorial.Rendering;          // Margins
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

// ReSharper disable CheckNamespace

// Phase-5 Cursorial.UI control showcase: the first S8 controls — Buttons, CheckBoxes, RadioButtons —
// laid out in a titled Border and scrolled by a ScrollViewer, driven by the REAL UIApplication frame
// loop against the live terminal. The tree carries NO explicit colors: every control draws from the
// cell-faithful default theme (§11.8a) — a Button's resting SurfaceBrush fill, its reverse-video focus
// and accent pressed, the hover fill, the check/radio in-box caret — so the demo shows the default look
// UNMASKED. The canary surface:
//   Tab / Shift+Tab  cycle focus through the controls (the S3 step-6 navigation tail),
//   Space / Enter    activate the focused button (Space-on-down, CD23) or toggle the check/radio,
//   click            focuses + activates (pointer modality),
//   wheel / arrows   scroll the ScrollViewer (the banded SCP composite slide),
//   't'              cycles the theme COLOR TIER live (Truecolor → Ansi256 → Ansi16 → NoColor) via
//                    UIApplication.RequestedColorTier — the controls re-quantize on the wire and the
//                    caps-* style classes re-stamp (inversion 6) with no tree rebuild,
//   'd'              flips the theme BASE (Dark ↔ Light) via RequestedThemeBase — the whole tree re-skins,
//   Ctrl+R           resets the toggles,
//   q / Esc / Ctrl+C exits.
// A status line reports the focused control, the last action, and the live ActualThemeVariant.
internal sealed class UIControlsDemo : IDemo
{
    public string Name => "uicontrols";
    public IReadOnlyList<string> Aliases => ["uic"];
    public string Description => "Cursorial.UI control showcase (default-themed Buttons/CheckBoxes/RadioButtons in a ScrollViewer; Tab focus, Space/Enter activate, 't' cycles color tier, 'd' flips base, Ctrl+R reset).";

    public async Task RunAsync(string argument)
    {
        Console.WriteLine("UI controls demo. Opening alt screen — Tab cycles the focusable controls, " +
                          "Space/Enter activates the focused one, click focuses + activates, the wheel " +
                          "and arrows scroll the panel, 't' cycles the theme color tier live, 'd' flips " +
                          "the theme base (dark/light), Ctrl+R resets the toggles; q / Esc / Ctrl+C exits.");

        var app = UIApplication.DefaultBuilder().Build();
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
        private TextBlock _status = null!;
        private string _lastAction = "(none)";

        private CheckBox _airplane = null!;
        private CheckBox _wifi = null!;
        private RadioButton _radioLow = null!;
        private RadioButton _radioMed = null!;
        private RadioButton _radioHigh = null!;

        public UIElement BuildTree()
        {
            // No app skin styles: the controls draw entirely from the cell-faithful default theme
            // (:pointerover / :focus / :pressed all come from the BuiltIn control themes now), so the demo
            // shows the default look unmasked — and 'd'/'t' re-skin it live.
            var root = new DockPanel();
            root.SetResourceReference(Panel.BackgroundProperty, ThemeKeys.WindowBackground);
            root.AddHandler(UIElement.KeyDownEvent, OnRootKeyDown);
            root.InputBindings.Add(new KeyBinding(KeyGesture.Parse("Ctrl+R"), new RelayCommand(ResetToggles)));
            root.AddHandler(UIElement.GotFocusEvent, (_, _) => UpdateStatus());

            // Header (docked top).
            var header = new StackPanel { Height = 1 };
            header.Children.Add(new TextBlock { Text = " Cursorial.UI — Phase 5 control showcase" });
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            // Status bar (docked bottom).
            var statusBar = new StackPanel { Height = 1 };
            _status = new TextBlock();
            statusBar.Children.Add(_status);
            DockPanel.SetDock(statusBar, Dock.Bottom);
            root.Children.Add(statusBar);

            // The control panel inside a scroller (LastChildFill).
            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = BuildControlPanel(),
            };
            root.Children.Add(scroller);

            UpdateStatus();
            return root;
        }

        private Border BuildControlPanel()
        {
            var panel = new StackPanel { Spacing = 1, Margin = new Margins(1) };

            panel.Children.Add(Caption("Buttons (Tab focus, Space/Enter or click activates):"));
            var ok = ThemedButton("_OK");
            ok.Click += (_, _) => Action("OK clicked");
            var cancel = ThemedButton("_Cancel");
            cancel.Click += (_, _) => Action("Cancel clicked");
            var apply = ThemedButton("_Apply");
            apply.Click += (_, _) => Action("Apply clicked");
            var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Margins(1, 0) };
            buttonRow.Children.Add(ok);
            buttonRow.Children.Add(cancel);
            buttonRow.Children.Add(apply);
            panel.Children.Add(buttonRow);

            panel.Children.Add(Caption("Check boxes (Space / click toggles):"));
            _airplane = ThemedCheck(" Airplane mode");
            _airplane.Click += (_, _) => Action($"Airplane = {_airplane.IsChecked}");
            _wifi = ThemedCheck(" Wi-Fi", isChecked: true);
            _wifi.Click += (_, _) => Action($"Wi-Fi = {_wifi.IsChecked}");
            panel.Children.Add(Indent(_airplane));
            panel.Children.Add(Indent(_wifi));

            panel.Children.Add(Caption("Radio buttons (one group; arrows move + check):"));
            _radioLow = ThemedRadio(" Low", "quality");
            _radioMed = ThemedRadio(" Medium", "quality", isChecked: true);
            _radioHigh = ThemedRadio(" High", "quality");
            _radioLow.Click += (_, _) => Action("quality = Low");
            _radioMed.Click += (_, _) => Action("quality = Medium");
            _radioHigh.Click += (_, _) => Action("quality = High");
            panel.Children.Add(Indent(_radioLow));
            panel.Children.Add(Indent(_radioMed));
            panel.Children.Add(Indent(_radioHigh));

            panel.Children.Add(Caption("Keys: t = cycle color tier · d = flip dark/light · Ctrl+R = reset · q = quit"));

            // A few filler rows so the ScrollViewer actually has something to scroll.
            for (var i = 0; i < 12; i++)
                panel.Children.Add(new TextBlock { Text = $"   row {i + 1} — scroll with the wheel or arrows" });

            // The titled Border is the GroupBox story (opt-in line chrome the cell-faithful model keeps,
            // §11.8a) — a Pen, not a brush; no Background, so the panel is unfilled (theme/terminal default).
            var border = new Border
                                    {
                                        BorderPen = Pens.Light,
                                        Title = " Settings ",
                                        Padding = new Margins(1),
                                        Child = panel,
                                    };

            border.SetResourceReference(Border.BorderPenProperty, ThemeKeys.BorderPen);
            return border;
        }

        private static TextBlock Caption(string text) => new() { Text = text };

        private static StackPanel Indent(UIElement child)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Margins(2, 0) };
            row.Children.Add(child);
            return row;
        }

        // Default-themed controls — no explicit Background/Foreground, so the BuiltIn control theme drives
        // every state (resting fill, hover, reverse-video focus on buttons / in-box caret on check-radio,
        // accent pressed) and a 'd' / 't' flip re-skins them live.
        private static Button ThemedButton(string content) => new() { Content = content };

        private static CheckBox ThemedCheck(string content, bool isChecked = false)
            => new() { Content = content, IsChecked = isChecked };

        private static RadioButton ThemedRadio(string content, string group, bool isChecked = false)
            => new() { Content = content, GroupName = group, IsChecked = isChecked };

        // ───────────────────────────── routed key handling ─────────────────────────────

        private void OnRootKeyDown(object? sender, KeyEventArgs e)
        {
            if ((e.Modifiers & KeyModifiers.Control) != 0)
                return; // leave Ctrl-modified keys (Ctrl+R binding, Ctrl+C exit gesture) alone

            switch (e.Key)
            {
                case Key.Escape:
                    app.Shutdown();
                    e.Handled = true;
                    return;

                case Key.Character when e.Text.Length > 0:
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

        // Cycle the requested color tier — the controls re-quantize on the wire and the caps-* classes
        // re-stamp (inversion 6). Truecolor → Ansi256 → Ansi16 → NoColor → (clear, back to negotiated).
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

        private void ResetToggles()
        {
            _airplane.IsChecked = false;
            _wifi.IsChecked = true;
            _radioMed.IsChecked = true; // re-checking the group default unchecks the peers
            Action("reset");
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
                ContentControl cc when cc.Content is string s => s.Replace("_", string.Empty),
                { } other => other.GetType().Name,
                _ => "none",
            };
            _status.Text = $" focus: {focusName,-10} · last: {_lastAction,-18} · theme: {app.ActualThemeVariant}" +
                           "  —  Tab focus, Space/Enter activate, t tier, d dark/light, Ctrl+R reset, q quit";
        }
    }

    /// <summary>The minimal <c>ICommand</c> for the Ctrl+R <see cref="KeyBinding"/>.</summary>
    private sealed class RelayCommand(Action execute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
