using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

using Style = Cursorial.UI.Style; // both Cursorial.Output and Cursorial.UI declare `Style`

// ReSharper disable CheckNamespace

// Phase-5 Cursorial.UI control showcase: the first S8 controls — Buttons, CheckBoxes, RadioButtons —
// laid out in a themed Border and scrolled by a ScrollViewer, driven by the REAL UIApplication frame
// loop against the live terminal. Every control renders through its BuiltIn default theme (template
// applied at measure, parts wired, content presented). The canary surface:
//   Tab / Shift+Tab  cycle focus through the controls (the S3 step-6 navigation tail),
//   Space / Enter    activate the focused button (Space-on-down, CD23) or toggle the check/radio,
//   click            focuses + activates (pointer modality),
//   wheel / arrows   scroll the ScrollViewer (the banded SCP composite slide),
//   't'              cycles the theme COLOR TIER live (Truecolor → Ansi256 → Ansi16 → NoColor) via
//                    UIApplication.RequestedColorTier — the controls re-quantize on the wire and the
//                    caps-* style classes re-stamp (inversion 6) with no tree rebuild,
//   'd'              flips the theme BASE (Dark ↔ Light) via RequestedThemeBase — a base flip changes
//                    resources without re-stamping caps classes,
//   Ctrl+R           resets the toggles,
//   q / Esc / Ctrl+C exits.
// A status line reports the focused control, the last action, and the live ActualThemeVariant.
internal sealed class UIControlsDemo : IDemo
{
    public string Name => "uicontrols";
    public IReadOnlyList<string> Aliases => ["uic"];
    public string Description => "Cursorial.UI control showcase (themed Buttons/CheckBoxes/RadioButtons in a ScrollViewer; Tab focus, Space/Enter activate, 't' cycles color tier, 'd' flips base, Ctrl+R reset).";

    public async Task RunAsync(string argument)
    {
        Console.WriteLine("UI controls demo. Opening alt screen — Tab cycles the focusable controls, " +
                          "Space/Enter activates the focused one, click focuses + activates, the wheel " +
                          "and arrows scroll the panel, 't' cycles the theme color tier live, 'd' flips " +
                          "the theme base (dark/light), Ctrl+R resets the toggles; q / Esc / Ctrl+C exits.");

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

    // ───────────────────────────── palette ─────────────────────────────

    private static readonly Color Chrome = Color.FromRgb(30, 33, 44);
    private static readonly Color ChromeText = Color.FromRgb(150, 160, 200);
    private static readonly Color Accent = Color.FromRgb(86, 120, 220);
    private static readonly Color PanelBg = Color.FromRgb(38, 42, 58);
    private static readonly Color ButtonFace = Color.FromRgb(56, 92, 168);
    private static readonly Color ButtonHover = Color.FromRgb(80, 124, 210);

    private sealed class Controller(UIApplication app)
    {
        private Label _status = null!;
        private string _lastAction = "(none)";

        private CheckBox _airplane = null!;
        private CheckBox _wifi = null!;
        private RadioButton _radioLow = null!;
        private RadioButton _radioMed = null!;
        private RadioButton _radioHigh = null!;

        public UIElement BuildTree()
        {
            // The control look: a Button:pointerover background flip + a :focus accent border, a
            // :checked accent foreground on the toggles — all through the P3 styling engine over the
            // BuiltIn-themed controls (the default templates supply the structure, these app rules the
            // skin). Every setter is AffectsRender/AffectsComposite, so a flip restyles the control's
            // zone with no hand-rolled invalidation.
            var buttonHover = new Style(Selectors.OfType<Button>().PseudoClass("pointerover"));
            buttonHover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(ButtonHover)));
            app.Styles.Add(buttonHover);

            var checkedToggle = new Style(Selectors.Is<ToggleButton>().PseudoClass("checked"));
            checkedToggle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Accent)));
            app.Styles.Add(checkedToggle);

            var root = new DockPanel { Background = new SolidColorBrush(Chrome) };
            root.AddHandler(UIElement.KeyDownEvent, OnRootKeyDown);
            root.InputBindings.Add(new KeyBinding(KeyGesture.Parse("Ctrl+R"), new RelayCommand(ResetToggles)));
            root.AddHandler(UIElement.GotFocusEvent, (_, _) => UpdateStatus());

            // Header (docked top).
            var header = new StackPanel { Background = new SolidColorBrush(Accent), Height = 1 };
            header.Children.Add(new Label(" Cursorial.UI — Phase 5 control showcase", Color.FromRgb(240, 244, 255), Accent));
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            // Status bar (docked bottom).
            var statusBar = new StackPanel { Background = new SolidColorBrush(Chrome), Height = 1 };
            _status = new Label("", ChromeText, Chrome);
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
            var panel = new StackPanel { Background = new SolidColorBrush(PanelBg), Spacing = 1, Margin = new Margins(1) };

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

            panel.Children.Add(Caption("Check boxes (Space / click toggles; :checked accents):"));
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
                panel.Children.Add(new Label($"   row {i + 1} — scroll with the wheel or arrows", ChromeText, PanelBg));

            return new Border
            {
                BorderPen = Pens.Light,
                Title = " Settings ",
                Background = new SolidColorBrush(PanelBg),
                Padding = new Margins(1),
                Child = panel,
            };
        }

        private static Label Caption(string text) => new(text, Color.FromRgb(200, 206, 226), PanelBg);

        private static StackPanel Indent(UIElement child)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Margins(2, 0) };
            row.Children.Add(child);
            return row;
        }

        // The button keeps an explicit colored face (an intentional accent surface), but its content ink
        // and the check/radio foreground are LEFT TO THE THEME — no explicit Foreground — so the BuiltIn
        // control theme's per-variant TextBrush inks them. That makes 'd' (dark/light) a live canary: the
        // content re-colors on the flip (an explicit Foreground would pin it and win over the theme).
        private static Button ThemedButton(string content)
            => new() { Content = content, Background = new SolidColorBrush(ButtonFace) };

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
                null => ColorDepth.Ansi256,
                ColorDepth.Ansi256 => ColorDepth.Ansi16,
                ColorDepth.Ansi16 => ColorDepth.NoColor,
                ColorDepth.NoColor => ColorDepth.Truecolor,
                _ => null, // clear → back to the negotiated tier
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

    // ───────────────────────────── a tiny text leaf for captions / status ─────────────────────────────

    /// <summary>A one-line text leaf with explicit colors (the demo's caption/status surface).</summary>
    private sealed class Label : UIElement
    {
        public static readonly StyledProperty<string> TextProperty =
            UIProperty.Register<Label, string>(nameof(Text), defaultValue: "");

        static Label()
        {
            AffectsMeasure<Label>(TextProperty);
            AffectsRender<Label>(TextProperty);
        }

        private readonly Color _foreground;
        private readonly Color? _background;

        public Label(string text, Color foreground, Color? background = null)
        {
            _foreground = foreground;
            _background = background;
            Text = text;
        }

        public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

        protected override Size MeasureOverride(Size availableSize)
            => new(Cursorial.Text.GraphemeWidth.StringWidth(Text), 1);

        protected override void Render(RenderContext context)
            => context.DrawText(0, 0, Text, _foreground, _background);
    }

    /// <summary>The minimal <c>ICommand</c> for the Ctrl+R <see cref="KeyBinding"/>.</summary>
    private sealed class RelayCommand(Action execute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
