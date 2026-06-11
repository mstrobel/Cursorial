using Cursorial.Drawing;
using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.Text;
using Cursorial.UI;

// Phase-1 Cursorial.UI showcase: a panel tree (DockPanel chrome, star Grid, StackPanel sidebar,
// WrapPanel chips, Canvas stage with ZIndex overlap) driven by the REAL UIApplication frame loop
// against the live terminal. Three invalidation lanes are poked interactively:
//   arrows → RenderOffset on a promoted boundary  [AffectsComposite]  — slides WITHOUT re-raster
//            (watch the raster counters in the status bar hold still),
//   v      → Visibility toggle on a badge         [AffectsRender]     — zone re-raster,
//   o      → Opacity cycle on a glass panel       [AffectsComposite]  — translucent re-composite.
// Input lands through UIApplication.InputDispatchTarget — the internal P1 seam S3's InputDispatcher
// replaces at P2 (hence the InternalsVisibleTo from Cursorial.UI). Resize is handled by the loop's
// resize transaction; q / Esc / Ctrl+C exit through Shutdown / the default gesture.
internal sealed class UIPanelsDemo : IDemo
{
    public string Name => "uipanels";
    public IReadOnlyList<string> Aliases => ["uip"];
    public string Description => "Cursorial.UI panel-tree showcase on the real frame loop (arrows slide, v visibility, o opacity).";

    public async Task RunAsync(string argument)
    {
        Console.WriteLine("UI panels demo. Opening alt screen — arrows slide the floating panel, " +
                          "'v' toggles the badge, 'o' cycles the glass opacity; q / Esc / Ctrl+C exits.");

        var app = UIApplication.CreateBuilder()
            .WithFrameRate(60)
            .Build();

        var controller = new Controller(app);
        app.InputDispatchTarget = controller; // the P1 routing stopgap (S3's router lands at P2)
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
    private static readonly Color SidebarBg = Color.FromRgb(38, 42, 58);
    private static readonly Color StageBg = Color.FromRgb(18, 20, 28);
    private static readonly Color FloatBg = Color.FromRgb(212, 168, 66);
    private static readonly Color FloatText = Color.FromRgb(40, 32, 8);
    private static readonly Color GlassBg = Color.FromRgb(70, 130, 200);
    private static readonly Color BadgeBg = Color.FromRgb(200, 60, 120);

    // ───────────────────────────── the interactive controller ─────────────────────────────

    /// <summary>
    /// Owns the mutable showcase state. Everything here runs on the UI thread: the frame loop
    /// invokes <see cref="BuildTree"/> before the first frame and <see cref="Dispatch"/> during
    /// Phase 1, so plain fields need no synchronization.
    /// </summary>
    private sealed class Controller(UIApplication app) : IInputDispatchTarget
    {
        private static readonly double[] OpacitySteps = [1.0, 0.85, 0.65, 0.45, 0.25];

        private CountingPanel _floating = null!;
        private CountingPanel _glass = null!;
        private Label _glassReadout = null!;
        private Label _badge = null!;
        private Label _status = null!;
        private int _opacityStep = 1; // start translucent so the glass effect is visible immediately

        public UIElement BuildTree()
        {
            var root = new DockPanel { Background = new SolidColorBrush(Chrome) };

            // Header bar (docked top).
            var header = new StackPanel { Background = new SolidColorBrush(Accent), Height = 1 };
            header.Children.Add(new Label(" Cursorial.UI — Phase 1 panel showcase", Color.FromRgb(240, 244, 255), Accent));
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            // Status bar (docked bottom) — live readout of the three invalidation lanes.
            var statusBar = new StackPanel { Background = new SolidColorBrush(Chrome), Height = 1 };
            _status = new Label("", ChromeText, Chrome);
            statusBar.Children.Add(_status);
            DockPanel.SetDock(statusBar, Dock.Bottom);
            root.Children.Add(statusBar);

            // Body: a star grid — fixed sidebar, stage takes the rest.
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = 26 });
            body.ColumnDefinitions.Add(new ColumnDefinition()); // 1*
            body.Children.Add(BuildSidebar());
            var stage = BuildStage();
            Grid.SetColumn(stage, 1);
            body.Children.Add(stage);
            root.Children.Add(body); // LastChildFill

            UpdateStatus();
            return root;
        }

        private StackPanel BuildSidebar()
        {
            var sidebar = new StackPanel { Background = new SolidColorBrush(SidebarBg), Spacing = 1 };
            sidebar.Children.Add(new Label(" sidebar (StackPanel)", Color.FromRgb(220, 224, 240), SidebarBg));

            var caps = app.Capabilities.Output;
            sidebar.Children.Add(new Label($"   color: {caps.Color.Depth}", ChromeText, SidebarBg));
            sidebar.Children.Add(new Label($"   sync output: {(caps.Protocol.SynchronizedOutput ? "yes" : "no")}", ChromeText, SidebarBg));

            sidebar.Children.Add(new Label(" chips (WrapPanel):", ChromeText, SidebarBg));
            var chips = new WrapPanel { ItemWidth = 5, ItemHeight = 1, Margin = new Margins(2, 0) };
            for (var i = 0; i < 8; i++)
            {
                var hue = i / 8.0;
                chips.Children.Add(new FillBox(HsvToRgb(hue, 0.65, 0.85)) { Width = 4, Height = 1 });
            }

            sidebar.Children.Add(chips);

            sidebar.Children.Add(new Label(" keys:", ChromeText, SidebarBg));
            sidebar.Children.Add(new Label("   ← ↑ ↓ →  slide panel", ChromeText, SidebarBg));
            sidebar.Children.Add(new Label("   v        toggle badge", ChromeText, SidebarBg));
            sidebar.Children.Add(new Label("   o        cycle opacity", ChromeText, SidebarBg));
            sidebar.Children.Add(new Label("   q        quit", ChromeText, SidebarBg));
            return sidebar;
        }

        private Canvas BuildStage()
        {
            var stage = new Canvas { Background = new SolidColorBrush(StageBg) };

            // Backdrop art: overlapping boxes whose stacking is ZIndex-driven, not document-driven
            // ("B" is first in document order yet paints on top of both).
            var boxB = new FillBox(Color.FromRgb(60, 110, 90), "B") { Width = 12, Height = 4, ZIndex = 2 };
            Canvas.SetLeft(boxB, 10);
            Canvas.SetTop(boxB, 3);
            stage.Children.Add(boxB);

            var boxA = new FillBox(Color.FromRgb(96, 70, 130), "A") { Width = 12, Height = 4 };
            Canvas.SetLeft(boxA, 4);
            Canvas.SetTop(boxA, 1);
            stage.Children.Add(boxA);

            var boxC = new FillBox(Color.FromRgb(140, 90, 60), "C") { Width = 12, Height = 4, ZIndex = 1 };
            Canvas.SetLeft(boxC, 16);
            Canvas.SetTop(boxC, 1);
            stage.Children.Add(boxC);

            // The badge — 'v' flips Visible ↔ Hidden ([AffectsRender]-side custom routing: the
            // stage zone re-rasters; layout is untouched).
            _badge = new Label(" ● badge (v) ", Color.FromRgb(255, 235, 245), BadgeBg);
            Canvas.SetLeft(_badge, 30);
            Canvas.SetTop(_badge, 8);
            stage.Children.Add(_badge);

            // The glass panel — sub-1 Opacity promotes it to a render boundary; 'o' re-composites
            // the cached raster at a new opacity (no Render call — watch its counter).
            _glass = new CountingPanel
            {
                Background = new SolidColorBrush(GlassBg),
                Width = 26,
                Height = 5,
                Opacity = OpacitySteps[_opacityStep],
            };
            _glassReadout = new Label($"   opacity {OpacitySteps[_opacityStep]:0.00}", Color.FromRgb(235, 242, 252), GlassBg);
            _glass.Children.Add(new Label(" glass panel (o)", Color.FromRgb(235, 242, 252), GlassBg));
            _glass.Children.Add(_glassReadout);
            Canvas.SetLeft(_glass, 8);
            Canvas.SetTop(_glass, 3);
            stage.Children.Add(_glass);

            // The floating panel — an explicit render boundary (predicate ⑦); arrows write
            // RenderOffset* ([AffectsComposite]), so every slide is a pure composite move of the
            // cached scene. Its raster counter in the status bar stays frozen while it flies.
            _floating = new CountingPanel
            {
                Background = new SolidColorBrush(FloatBg),
                Width = 22,
                Height = 3,
                IsRenderBoundary = true,
            };
            _floating.Children.Add(new Label(" floating panel", FloatText, FloatBg));
            _floating.Children.Add(new Label("   ← ↑ ↓ → slides me", FloatText, FloatBg));
            Canvas.SetLeft(_floating, 14);
            Canvas.SetTop(_floating, 10);
            stage.Children.Add(_floating);

            return stage;
        }

        // ───────────────────────────── IInputDispatchTarget ─────────────────────────────

        public InputDispatchResult Dispatch(InputEvent inputEvent)
        {
            if (inputEvent is not KeyEvent { Kind: KeyEventKind.Down } key)
                return InputDispatchResult.NotUIInput;
            if ((key.Modifiers & KeyModifiers.Control) != 0)
                return InputDispatchResult.NotUIInput; // leave Ctrl+C to the default exit gesture

            switch (key.Key)
            {
                case Key.LeftArrow:
                    return Nudge(-2, 0);
                case Key.RightArrow:
                    return Nudge(2, 0);
                case Key.UpArrow:
                    return Nudge(0, -1);
                case Key.DownArrow:
                    return Nudge(0, 1);

                case Key.Escape:
                    app.Shutdown();
                    return InputDispatchResult.DispatchedHandled;

                case Key.Character when key.Text.Length > 0:
                    switch (char.ToLowerInvariant(key.Text.Span[0]))
                    {
                        case 'q':
                            app.Shutdown();
                            return InputDispatchResult.DispatchedHandled;

                        case 'v':
                            _badge.Visibility = _badge.Visibility == Visibility.Visible
                                ? Visibility.Hidden
                                : Visibility.Visible;
                            UpdateStatus();
                            return InputDispatchResult.DispatchedHandled;

                        case 'o':
                            _opacityStep = (_opacityStep + 1) % OpacitySteps.Length;
                            _glass.Opacity = OpacitySteps[_opacityStep];
                            _glassReadout.Text = $"   opacity {OpacitySteps[_opacityStep]:0.00}";
                            UpdateStatus();
                            return InputDispatchResult.DispatchedHandled;
                    }

                    break;
            }

            return InputDispatchResult.NotUIInput;
        }

        public void UpdateHover()
        {
        }

        public void OnCapabilitiesChanged(TerminalCapabilities capabilities)
        {
        }

        private InputDispatchResult Nudge(int columns, int rows)
        {
            _floating.RenderOffsetColumn = Math.Clamp(_floating.RenderOffsetColumn + columns, -60, 120);
            _floating.RenderOffsetRow = Math.Clamp(_floating.RenderOffsetRow + rows, -16, 40);
            UpdateStatus();
            return InputDispatchResult.DispatchedHandled;
        }

        private void UpdateStatus()
            => _status.Text = $" offset ({_floating.RenderOffsetColumn,3},{_floating.RenderOffsetRow,3})" +
                              $" · opacity {OpacitySteps[_opacityStep]:0.00}" +
                              $" · badge {(_badge.Visibility == Visibility.Visible ? "on " : "off")}" +
                              $" · rasters: float {_floating.RasterCount}, glass {_glass.RasterCount}" +
                              " — arrows slide (no re-raster!), v badge, o opacity, q quits";

        private static Color HsvToRgb(double hue, double saturation, double value)
        {
            var h = hue * 6.0 % 6.0;
            var c = value * saturation;
            var x = c * (1 - Math.Abs(h % 2 - 1));
            var (r, g, b) = (int)h switch
            {
                0 => (c, x, 0.0),
                1 => (x, c, 0.0),
                2 => (0.0, c, x),
                3 => (0.0, x, c),
                4 => (x, 0.0, c),
                _ => (c, 0.0, x),
            };
            var m = value - c;
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }
    }

    // ───────────────────────────── showcase elements ─────────────────────────────
    // P1 has panels but no content controls (TextBlock/Border land with the control set); the demo
    // brings its own two leaves, which doubles as a sample of the UIElement.Render surface.

    /// <summary>A one-line text leaf: explicit colors, measured by grapheme width, [AffectsRender] text.</summary>
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
            => new(GraphemeWidth.StringWidth(Text), 1);

        protected override void Render(RenderContext context)
            => context.DrawText(0, 0, Text, _foreground, _background);
    }

    /// <summary>A solid color block with an optional centered glyph label.</summary>
    private sealed class FillBox(Color fill, string? label = null) : UIElement
    {
        protected override void Render(RenderContext context)
        {
            context.FillOpaque(context.Bounds, fill);
            if (label is not null && context.Size is { Columns: > 0, Rows: > 0 })
                context.DrawText(context.Size.Columns / 2, context.Size.Rows / 2, label, Color.FromRgb(235, 235, 235), fill);
        }
    }

    /// <summary>A StackPanel that counts its rasters — the status bar's live no-re-raster proof.</summary>
    private sealed class CountingPanel : StackPanel
    {
        public int RasterCount { get; private set; }

        protected override void Render(RenderContext context)
        {
            RasterCount++;
            base.Render(context);
        }
    }
}
