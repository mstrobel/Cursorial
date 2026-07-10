using Cursorial.Animation;
using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

using Style = Cursorial.UI.Style; // both Cursorial.Output and Cursorial.UI declare `Style`

// ReSharper disable CheckNamespace

// Cursorial.UI Phase-8 (S5) animation showcase on the REAL UIApplication frame loop. Four lanes, all
// composite-shaped (RenderOffset / Opacity) so they slide and fade WITHOUT re-rastering:
//   t / h  → a STORYBOARD (§9.3): a toast slides in from off-screen-right (Int32Track on
//            RenderOffsetColumn) AND fades in (DoubleTrack on Opacity) as one group; 'h' reverses it.
//            The slide track's EASING is selectable with 'e' (Linear → CubicOut → ElasticOut →
//            BounceOut) — watch the overshoot/bounce.
//   hover the FADE CARD → a TRANSITION (§9.5): its Opacity has a DoubleTransition, and a
//            `:pointerover` style rule drops the base Opacity; the change FADES instead of snapping
//            (and fades back on leave) — an implicit animation driven purely by the hover pseudo-class.
//   hover the PULSE BOX → an EDGE-ACTION storyboard (§9.3): the `:pointerover` rule carries a
//            BeginStoryboard in its Enter and a StopStoryboard in its Exit, so a perpetual AutoReverse
//            Opacity ping-pong runs while hovered and stops on leave.
// The loop idles when nothing is animating (HasActiveAnimations gates Phase 7) and wakes the instant
// a storyboard/transition begins. q / Esc / Ctrl+C exit.
internal sealed class MotionDemo : IDemo
{
    public string Name => "motion";
    public IReadOnlyList<string> Aliases => ["uianimate"];
    public string Description => "Cursorial.UI S5 animation showcase: t/h toast storyboard (slide+fade), e cycles the easing, hover the fade card (transition) and the pulse box (edge-action storyboard).";

    public async Task RunAsync(string argument)
    {
        Console.WriteLine("Animation demo. Opening alt screen — 't' shows a toast (storyboard: slide + " +
                          "fade), 'h' hides it, 'e' cycles the slide easing (Linear/CubicOut/ElasticOut/" +
                          "BounceOut); hover the fade card (an Opacity TRANSITION) and the pulse box (an " +
                          "edge-action perpetual storyboard). q / Esc / Ctrl+C exits.");

        var app = UIApplication.DefaultBuilder().Build();
        var controller = new Controller(app);
        try
        {
            await app.RunAsync(controller.BuildTree);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private static readonly Color Chrome = Color.FromRgb(28, 31, 42);
    private static readonly Color ChromeText = Color.FromRgb(150, 160, 200);
    private static readonly Color Accent = Color.FromRgb(86, 120, 220);
    private static readonly Color StageBg = Color.FromRgb(18, 20, 28);
    private static readonly Color ToastBg = Color.FromRgb(212, 168, 66);
    private static readonly Color ToastText = Color.FromRgb(40, 32, 8);
    private static readonly Color FadeCardBg = Color.FromRgb(80, 150, 110);
    private static readonly Color PulseBg = Color.FromRgb(180, 80, 140);

    private sealed class Controller(UIApplication app)
    {
        private static readonly (string Name, Easing Easing)[] Easings =
        [
            ("Linear", Cursorial.Animation.Easings.Linear),
            ("CubicOut", Cursorial.Animation.Easings.CubicOut),
            ("ElasticOut", Cursorial.Animation.Easings.ElasticOut),
            ("BounceOut", Cursorial.Animation.Easings.BounceOut),
        ];

        private const int ToastHiddenOffset = 44; // off-screen to the right

        private FillBox _toast = null!;
        private Label _status = null!;
        private int _easingStep = 1;       // CubicOut
        private bool _toastShown;

        public UIElement BuildTree()
        {
            var root = new DockPanel { Background = new SolidColorBrush(Chrome) };
            root.AddHandler(UIElement.KeyDownEvent, OnKeyDown);

            var header = new StackPanel { Background = new SolidColorBrush(Accent), Height = 1 };
            header.Children.Add(new Label(" Cursorial.UI — Phase 8 animation showcase", Color.FromRgb(240, 244, 255), Accent));
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var statusBar = new StackPanel { Background = new SolidColorBrush(Chrome), Height = 1 };
            _status = new Label("", ChromeText, Chrome);
            statusBar.Children.Add(_status);
            DockPanel.SetDock(statusBar, Dock.Bottom);
            root.Children.Add(statusBar);

            root.Children.Add(BuildStage()); // LastChildFill
            UpdateStatus();
            return root;
        }

        private Canvas BuildStage()
        {
            var stage = new Canvas { Background = new SolidColorBrush(StageBg) };

            stage.Children.Add(Hint("storyboard:", 4, 2));
            stage.Children.Add(Hint("  t show · h hide · e easing", 4, 3));

            // The toast — an explicit render boundary; starts off-screen (offset) + invisible (opacity 0).
            // 't' begins a storyboard that animates BOTH RenderOffsetColumn (Int32Track) and Opacity
            // (DoubleTrack) as one group — composite-shaped, so it slides + fades with no re-raster.
            _toast = new FillBox(ToastBg, " ✦ toast (storyboard) ", ToastText)
            {
                Width = 24,
                Height = 3,
                IsRenderBoundary = true,
                RenderOffsetColumn = ToastHiddenOffset,
                Opacity = 0.0,
            };
            Canvas.SetLeft(_toast, 4);
            Canvas.SetTop(_toast, 5);
            stage.Children.Add(_toast);

            // The fade card — an Opacity TRANSITION (§9.5). A `:pointerover` style drops the base Opacity;
            // the DoubleTransition fades the change instead of snapping (and fades back on leave).
            stage.Children.Add(Hint("transition (hover):", 4, 10));
            var fadeCard = new FillBox(FadeCardBg, " hover me — Opacity fades ", Color.FromRgb(235, 245, 238))
            {
                Width = 30,
                Height = 3,
            };
            Transition.SetTransitions(fadeCard, new TransitionCollection
            {
                new DoubleTransition(UIElement.OpacityProperty) { Duration = TimeSpan.FromMilliseconds(220), Easing = Cursorial.Animation.Easings.CubicInOut },
            });
            var fadeStyle = new Style(Selectors.OfType<FillBox>().Class("fadecard"));
            var fadeHover = new Style(Selectors.Nesting().PseudoClass("pointerover"));
            fadeHover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
            fadeStyle.Children.Add(fadeHover);
            app.Styles.Add(fadeStyle);
            fadeCard.Classes.Add("fadecard");
            Canvas.SetLeft(fadeCard, 4);
            Canvas.SetTop(fadeCard, 11);
            stage.Children.Add(fadeCard);

            // The pulse box — an EDGE-ACTION storyboard (§9.3). The `:pointerover` rule carries a
            // BeginStoryboard in its Enter and a StopStoryboard in its Exit, so hovering begins a perpetual
            // AutoReverse Opacity ping-pong and leaving stops it (the base Opacity resurfaces).
            stage.Children.Add(Hint("edge-action storyboard (hover):", 4, 15));
            var pulse = new FillBox(PulseBg, " hover me — pulses ", Color.FromRgb(250, 235, 245))
            {
                Width = 24,
                Height = 3,
            };
            var pulseStoryboard = new Storyboard();
            pulseStoryboard.Children.Add(new DoubleTrack
            {
                TargetProperty = UIElement.OpacityProperty,
                From = 1.0,
                To = 0.35,
                Duration = TimeSpan.FromMilliseconds(550),
                AutoReverse = true,
                Repeat = RepeatBehavior.Forever,
            });
            var pulseStyle = new Style(Selectors.OfType<FillBox>().Class("pulsebox"));
            // The edge actions live on the :pointerover CHILD rule, so they fire on the HOVER activate/deactivate
            // edges (not the always-active parent): Enter begins the pulse, Exit stops it (the begin/stop pairing).
            var pulseHover = new Style(Selectors.Nesting().PseudoClass("pointerover"));
            pulseHover.Enter.Add(new BeginStoryboard { Storyboard = pulseStoryboard }); // hover-enter ⇒ begin
            pulseHover.Exit.Add(new StopStoryboard { Storyboard = pulseStoryboard });   // hover-leave ⇒ stop
            pulseStyle.Children.Add(pulseHover);
            app.Styles.Add(pulseStyle);
            pulse.Classes.Add("pulsebox");
            Canvas.SetLeft(pulse, 4);
            Canvas.SetTop(pulse, 16);
            stage.Children.Add(pulse);

            return stage;
        }

        private static Label Hint(string text, int left, int top)
        {
            var label = new Label(text, ChromeText, StageBg);
            Canvas.SetLeft(label, left);
            Canvas.SetTop(label, top);
            return label;
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if ((e.Modifiers & KeyModifiers.Control) != 0)
                return; // Ctrl+C falls through to the S6 default exit gesture

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
                            ShowToast();
                            e.Handled = true;
                            return;

                        case 'h':
                            HideToast();
                            e.Handled = true;
                            return;

                        case 'e':
                            _easingStep = (_easingStep + 1) % Easings.Length;
                            UpdateStatus();
                            e.Handled = true;
                            return;
                    }

                    break;
            }
        }

        private void ShowToast()
        {
            _toastShown = true;
            BuildToastStoryboard(toColumn: 0, toOpacity: 1.0).Begin(_toast);
            UpdateStatus();
        }

        private void HideToast()
        {
            _toastShown = false;
            BuildToastStoryboard(toColumn: ToastHiddenOffset, toOpacity: 0.0).Begin(_toast);
            UpdateStatus();
        }

        // A fresh two-track storyboard each time (the slide easing is selectable, so the track is rebuilt).
        private Storyboard BuildToastStoryboard(int toColumn, double toOpacity)
        {
            var storyboard = new Storyboard();
            storyboard.Children.Add(new Int32Track
            {
                TargetProperty = UIElement.RenderOffsetColumnProperty,
                To = toColumn,
                Duration = TimeSpan.FromMilliseconds(500),
                Easing = Easings[_easingStep].Easing, // From unset ⇒ snapshots the current offset (AD4)
            });
            storyboard.Children.Add(new DoubleTrack
            {
                TargetProperty = UIElement.OpacityProperty,
                To = toOpacity,
                Duration = TimeSpan.FromMilliseconds(400),
            });
            return storyboard;
        }

        private void UpdateStatus()
            => _status.Text = $" toast {(_toastShown ? "shown" : "hidden")}" +
                              $" · easing {Easings[_easingStep].Name}" +
                              " — t show · h hide · e easing · hover the cards · q quits";
    }

    /// <summary>A solid color block with a glyph label; sized by its Width/Height (the showcase's leaf element).</summary>
    private sealed class FillBox(Color fill, string label, Color labelColor) : UIElement
    {
        protected override void Render(RenderContext context)
        {
            context.FillOpaque(context.Bounds, fill);
            if (context.Size is { Columns: > 0, Rows: > 0 })
                context.DrawText(1, context.Size.Rows / 2, label, labelColor, fill);
        }
    }

    /// <summary>A one-line text leaf: AffectsRender Text, explicit colors, grapheme-measured (the UIPanelsDemo shape).</summary>
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

        protected override Size MeasureOverride(Size availableSize) => new(GraphemeWidth.StringWidth(Text), 1);

        protected override void Render(RenderContext context) => context.DrawText(0, 0, Text, _foreground, _background);
    }
}
