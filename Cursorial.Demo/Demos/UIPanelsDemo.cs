using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

using Cursorial.Input;
using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Input;

using Style = Cursorial.UI.Style; // both Cursorial.Output and Cursorial.UI declare `Style`

// ReSharper disable CheckNamespace

// Phase-1 Cursorial.UI showcase: a panel tree (DockPanel chrome, star Grid, StackPanel sidebar,
// WrapPanel chips, Canvas stage with ZIndex overlap) driven by the REAL UIApplication frame loop
// against the live terminal. Three invalidation lanes are poked interactively:
//   arrows → RenderOffset on a promoted boundary  [AffectsComposite]  — slides WITHOUT re-raster
//            (watch the raster counters in the status bar hold still),
//   v      → Visibility toggle on a badge         [AffectsRender]     — zone re-raster,
//   o      → Opacity cycle on a glass panel       [AffectsComposite]  — translucent re-composite.
// Input lands through S3's public routed events (P2): a KeyDown handler on the root tree sees
// every key the focused element leaves unhandled. The sidebar carries three FOCUSABLE cards — Tab /
// Shift+Tab cycles them through the step-6 navigation tail, clicking one focuses it (pointer
// modality), and each card's visual state is driven by REAL P3 style rules — one app-channel style
// (`FocusCard.card`) carries the rest look plus `^:pointerover` / `^:focus` children, so hover and
// focus restyle the cards through pseudo-class flips + AffectsRender invalidation (the styling
// engine's live canary; no hand-rolled IsFocused/IsPointerOver reads, no InvalidateVisual calls).
// Hovering any card highlights it live off the any-event-motion hover chain — and, on OSC 22
// terminals (Kitty/Ghostty/Foot), flips the HOST mouse pointer through UIElement.Cursor (§7.6):
// the cards request the hand, the glass panel the I-beam, everything else the default. Ctrl+R is a
// KeyBinding (KeyGesture → ICommand on the root's InputBindings) that resets the showcase.
// 'b' is the P4 DATA-BINDING canary: a sidebar card whose Label and Visibility are BOUND to an
// INPC viewmodel (Cursorial.UI.Data) — 'b' flips the VM's `Live` bool (and rewrites its `Banner`
// text), the bindings carry the change to the card with no manual element writes (the live MVVM
// round trip — INPC PropertyChanged → bound StyledProperty → AffectsRender repaint). Resize
// is handled by the loop's resize transaction; q / Esc exit through Shutdown, and an UNHANDLED
// Ctrl+C exits through the S6 default gesture (the handler deliberately leaves Ctrl-modified keys
// unhandled — Ctrl+R is consumed by the binding sweep, Ctrl+C falls through to the gesture).
internal sealed class UIPanelsDemo : IDemo
{
    public string Name => "uipanels";
    public IReadOnlyList<string> Aliases => ["uip"];
    public string Description => "Cursorial.UI panel-tree showcase on the real frame loop (arrows slide, v visibility, o opacity, Tab focus, style-rule cards, b toggles a BOUND MVVM card, Ctrl+R reset).";

    public async Task RunAsync(string argument)
    {
        Console.WriteLine("UI panels demo. Opening alt screen — arrows slide the floating panel, " +
                          "'v' toggles the badge, 'o' cycles the glass opacity, Tab/click focuses the " +
                          "sidebar cards (hover/focus looks come from REAL :pointerover/:focus style " +
                          "rules — the P3 styling engine live; on Kitty-class terminals the mouse pointer " +
                          "turns to a hand over the cards and an I-beam over the glass), 'b' flips a " +
                          "BOUND MVVM card (the P4 data-binding canary), Ctrl+R resets; " +
                          "q / Esc / Ctrl+C exits.");

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
    private static readonly Color CardBg = Color.FromRgb(48, 52, 70);
    private static readonly Color CardHoverBg = Color.FromRgb(64, 70, 96);
    private static readonly Color CardText = Color.FromRgb(205, 212, 235);

    // ───────────────────────────── the interactive controller ─────────────────────────────

    /// <summary>
    /// Owns the mutable showcase state. Everything here runs on the UI thread: the frame loop
    /// invokes <see cref="BuildTree"/> before the first frame and the root's KeyDown handler runs
    /// inside Phase 1's input drain, so plain fields need no synchronization.
    /// </summary>
    private sealed class Controller(UIApplication app)
    {
        private static readonly double[] OpacitySteps = [1.0, 0.85, 0.65, 0.45, 0.25];

        private CountingPanel _floating = null!;
        private CountingPanel _glass = null!;
        private Label _glassReadout = null!;
        private Label _badge = null!;
        private Label _status = null!;
        private FocusCard[] _cards = [];
        private int _opacityStep = 1; // start translucent so the glass effect is visible immediately

        // The P4 MVVM canary state: a plain INPC viewmodel. The bound card's Label / Visibility track
        // these through Cursorial.UI.Data bindings — 'b' is the only thing that touches the VM.
        private readonly BannerVm _vm = new() { Banner = " ● live banner (b) ", Live = true };
        private int _bannerCount;

        public UIElement BuildTree()
        {
            // The P3 styling canary: the sidebar cards take their entire visual state from style
            // rules. One app-channel style targets `FocusCard.card` (type + class) with the rest
            // look; its `^`-composed children flip the look on :pointerover / :focus — declared in
            // that order, so a hovered+focused card keeps the focus look (declaration order breaks
            // the specificity tie). All three properties are AffectsRender: a pseudo-class flip
            // restyles and re-rasters only the card's zone — no hand-rolled InvalidateVisual.
            var cardStyle = new Style(Selectors.OfType<FocusCard>().Class("card"));
            cardStyle.Setters.Add(new Setter(FocusCard.CardBackgroundProperty, CardBg));
            cardStyle.Setters.Add(new Setter(FocusCard.CardForegroundProperty, CardText));
            cardStyle.Setters.Add(new Setter(FocusCard.MarkerProperty, " "));

            var hovered = new Style(Selectors.Nesting().PseudoClass("pointerover"));
            hovered.Setters.Add(new Setter(FocusCard.CardBackgroundProperty, CardHoverBg));
            hovered.Setters.Add(new Setter(FocusCard.MarkerProperty, "-"));
            cardStyle.Children.Add(hovered);

            var focused = new Style(Selectors.Nesting().PseudoClass("focus"));
            focused.Setters.Add(new Setter(FocusCard.CardBackgroundProperty, Accent));
            focused.Setters.Add(new Setter(FocusCard.CardForegroundProperty, Color.FromRgb(240, 244, 255)));
            focused.Setters.Add(new Setter(FocusCard.MarkerProperty, ">"));
            cardStyle.Children.Add(focused);

            app.Styles.Add(cardStyle);

            var root = new DockPanel { Background = new SolidColorBrush(Chrome) };

            // S3 routing (P2): keys route to the focused card and bubble; whatever the cards leave
            // unhandled reaches this root handler, then the root's InputBindings sweep (Ctrl+R),
            // then the step-6 navigation tail (Tab / Shift+Tab move focus between the cards).
            root.AddHandler(UIElement.KeyDownEvent, OnRootKeyDown);
            root.InputBindings.Add(new KeyBinding(KeyGesture.Parse("Ctrl+R"), new RelayCommand(ResetShowcase)));

            // Focus changes repaint the status readout (the cards repaint themselves).
            root.AddHandler(UIElement.GotFocusEvent, (_, _) => UpdateStatus());

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
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = 32 });
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
            for (var i = 0; i < 16; i++)
            {
                // var hue = i / 8.0;
                chips.Children.Add(new FillBox(/*Color.FromHsv(hue, 0.65, 0.85)*/Color.FromPalette((byte)i)) { Width = 4, Height = 1 });
            }

            sidebar.Children.Add(chips);

            // The focus cards — Tab cycles them (step-6 navigation), clicking focuses (pointer
            // modality), hover highlights; the looks come from the `FocusCard.card` style rules
            // installed in BuildTree (the "card" class is what the selector's `.card` part matches).
            sidebar.Children.Add(new Label(" cards (Tab / click):", ChromeText, SidebarBg));
            _cards =
            [
                new FocusCard("alpha card"),
                new FocusCard("bravo card"),
                new FocusCard("charlie card"),
            ];
            var cardStack = new StackPanel { Margin = new Margins(2, 0) };
            foreach (var card in _cards)
            {
                card.Classes.Add("card");
                cardStack.Children.Add(card);
            }
            sidebar.Children.Add(cardStack);

            // The P4 data-binding canary: a card whose Label and Visibility are BOUND to the INPC
            // `BannerVm` (DataContext-anchored). 'b' flips the VM — the bindings carry it to the card
            // with no manual element writes. Label ← Banner (OneWay), Visibility ← Live (OneWay through
            // a bool→Visibility converter). The DataContext set on the card is the binding anchor.
            sidebar.Children.Add(new Label(" bound card (b → MVVM):", ChromeText, SidebarBg));
            var boundCard = new Label("", Color.FromRgb(255, 235, 245), BadgeBg) { Margin = new Margins(2, 0) };
            boundCard.DataContext = _vm;
            boundCard.SetBinding(Label.TextProperty, new Binding(nameof(BannerVm.Banner)));
            boundCard.SetBinding(UIElement.VisibilityProperty,
                new Binding(nameof(BannerVm.Live)) { Converter = BoolToVisibility.Instance });
            sidebar.Children.Add(boundCard);

            sidebar.Children.Add(new Label(" keys:", ChromeText, SidebarBg));
            sidebar.Children.Add(new Label("   hover    cards=hand", ChromeText, SidebarBg));
            sidebar.Children.Add(new Label("            glass=I-beam", ChromeText, SidebarBg) { Margin = new(0, -1, 0, 0) });
            sidebar.Children.Add(new Label("   ← ↑ ↓ →  slide panel", ChromeText, SidebarBg));
            sidebar.Children.Add(new Label("   v        toggle badge", ChromeText, SidebarBg));
            sidebar.Children.Add(new Label("   o        cycle opacity", ChromeText, SidebarBg));
            sidebar.Children.Add(new Label("   b        flip bound VM", ChromeText, SidebarBg));
            sidebar.Children.Add(new Label("   Tab      cycle cards", ChromeText, SidebarBg));
            sidebar.Children.Add(new Label("   Ctrl+R   reset (binding)", ChromeText, SidebarBg));
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
                Cursor = MouseCursorShape.Text, // §7.6 — I-beam over the glass panel (OSC 22 terminals)
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

        // ───────────────────────────── routed key handling (S3, P2) ─────────────────────────────

        private void OnRootKeyDown(object? sender, KeyEventArgs e)
        {
            if ((e.Modifiers & KeyModifiers.Control) != 0)
                return; // leave Ctrl+C unhandled — the S6 default exit gesture claims it

            switch (e.Key)
            {
                case Key.LeftArrow:
                    Nudge(-2, 0);
                    e.Handled = true;
                    return;
                case Key.RightArrow:
                    Nudge(2, 0);
                    e.Handled = true;
                    return;
                case Key.UpArrow:
                    Nudge(0, -1);
                    e.Handled = true;
                    return;
                case Key.DownArrow:
                    Nudge(0, 1);
                    e.Handled = true;
                    return;

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

                        case 'v':
                            _badge.Visibility = _badge.Visibility == Visibility.Visible
                                ? Visibility.Hidden
                                : Visibility.Visible;
                            UpdateStatus();
                            e.Handled = true;
                            return;

                        case 'o':
                            _opacityStep = (_opacityStep + 1) % OpacitySteps.Length;
                            _glass.Opacity = OpacitySteps[_opacityStep];
                            _glassReadout.Text = $"   opacity {OpacitySteps[_opacityStep]:0.00}";
                            UpdateStatus();
                            e.Handled = true;
                            return;

                        case 'b':
                            // The P4 MVVM canary: touch ONLY the viewmodel. INPC PropertyChanged flows
                            // through the live bindings to the bound card's Label + Visibility — no
                            // element write, no InvalidateVisual.
                            _vm.Live = !_vm.Live;
                            _vm.Banner = _vm.Live
                                ? $" ● live banner #{++_bannerCount} (b) "
                                : " ○ hidden (b) ";
                            UpdateStatus();
                            e.Handled = true;
                            return;
                    }

                    break;
            }
        }

        private void Nudge(int columns, int rows)
        {
            _floating.RenderOffsetColumn = Math.Clamp(_floating.RenderOffsetColumn + columns, -60, 120);
            _floating.RenderOffsetRow = Math.Clamp(_floating.RenderOffsetRow + rows, -16, 40);
            UpdateStatus();
        }

        /// <summary>The Ctrl+R <see cref="KeyBinding"/> target: back to the opening state.</summary>
        private void ResetShowcase()
        {
            _floating.RenderOffsetColumn = 0;
            _floating.RenderOffsetRow = 0;
            _opacityStep = 1;
            _glass.Opacity = OpacitySteps[_opacityStep];
            _glassReadout.Text = $"   opacity {OpacitySteps[_opacityStep]:0.00}";
            _badge.Visibility = Visibility.Visible;
            _vm.Live = true;                          // the bound card follows through its bindings
            _vm.Banner = " ● live banner (b) ";
            _bannerCount = 0;
            UpdateStatus();
        }

        private void UpdateStatus()
            => _status.Text = $" offset ({_floating.RenderOffsetColumn,3},{_floating.RenderOffsetRow,3})" +
                              $" · opacity {OpacitySteps[_opacityStep]:0.00}" +
                              $" · badge {(_badge.Visibility == Visibility.Visible ? "on " : "off")}" +
                              $" · bound {(_vm.Live ? "live" : "off ")}" +
                              $" · focus {Array.Find(_cards, static card => card.IsFocused)?.Title ?? "none"}" +
                              $" · rasters: float {_floating.RasterCount}, glass {_glass.RasterCount}" +
                              " — arrows slide, v badge, o opacity, b bound VM, Tab cards, Ctrl+R reset, q quits";
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
            => context.DrawText(0, 0, Text,
                                new BrushedStyle
                                {
                                    Foreground = new SolidColorBrush(_foreground),
                                    Background = _background is {} bg ? new SolidColorBrush(bg) : Brushes.Transparent,
                                });
    }

    /// <summary>A solid color block with an optional centered glyph label.</summary>
    private sealed class FillBox(Color fill, string? label = null) : UIElement
    {
        protected override void Render(RenderContext context)
        {
            context.FillOpaque(context.Bounds, fill);
            if (label is not null && context.Size is { Columns: > 0, Rows: > 0 })
                context.DrawText(context.Size.Columns / 2, context.Size.Rows / 2, label,
                                 new BrushedStyle
                                 {
                                     Foreground = new SolidColorBrush(Color.FromRgb(235, 235, 235)),
                                     Background = new SolidColorBrush(fill),
                                 });
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

    /// <summary>
    /// A focusable, hover-aware card whose ENTIRE visual state arrives through the P3 styling
    /// engine: <see cref="CardBackgroundProperty"/> / <see cref="CardForegroundProperty"/> /
    /// <see cref="MarkerProperty"/> are written only by the <c>FocusCard.card</c> style rules
    /// (rest + <c>^:pointerover</c> + <c>^:focus</c>) installed in <c>BuildTree</c>. All three are
    /// <c>AffectsRender</c>, so a pseudo-class flip restyles and repaints with no hand-rolled
    /// <c>InvalidateVisual</c> — this card is the styling engine's live canary. Mouse-down focuses
    /// (pointer modality — no focus ring policy yet, the focus rule's accent serves as the
    /// indication).
    /// </summary>
    private sealed class FocusCard : UIElement
    {
        /// <summary>The card's background — styled (rest / hover / focus rules); terminal default until armed.</summary>
        public static readonly StyledProperty<Color> CardBackgroundProperty =
            UIProperty.Register<FocusCard, Color>(nameof(CardBackground));

        /// <summary>The card's text color — styled.</summary>
        public static readonly StyledProperty<Color> CardForegroundProperty =
            UIProperty.Register<FocusCard, Color>(nameof(CardForeground));

        /// <summary>The one-glyph state marker — styled (<c>" "</c> rest, <c>"-"</c> hover, <c>"&gt;"</c> focus).</summary>
        public static readonly StyledProperty<string> MarkerProperty =
            UIProperty.Register<FocusCard, string>(nameof(Marker), defaultValue: " ");

        static FocusCard() => AffectsRender<FocusCard>(CardBackgroundProperty, CardForegroundProperty, MarkerProperty);

        public FocusCard(string title)
        {
            Title = title;
            Focusable = true;
            Cursor = MouseCursorShape.Pointer; // §7.6 — clickable hand over the cards (OSC 22 terminals)
        }

        public string Title { get; }

        public Color CardBackground => GetValue(CardBackgroundProperty);

        public Color CardForeground => GetValue(CardForegroundProperty);

        public string Marker => GetValue(MarkerProperty);

        protected override Size MeasureOverride(Size availableSize)
            => new(GraphemeWidth.StringWidth(Title) + 4, 1);

        protected override void Render(RenderContext context)
        {
            var background = CardBackground;
            context.FillOpaque(context.Bounds, background);
            context.DrawText(0, 0, $" {Marker} {Title}",
                             new BrushedStyle
                             {
                                 Foreground = new SolidColorBrush(CardForeground),
                                 Background = new SolidColorBrush(background),
                             });
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            e.Handled = true;
        }
    }

    /// <summary>The minimal <c>ICommand</c> for the Ctrl+R <see cref="KeyBinding"/>.</summary>
    private sealed class RelayCommand(Action execute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }

    // ───────────────────────────── the P4 MVVM canary ─────────────────────────────

    /// <summary>
    /// The bound viewmodel for the data-binding canary: a plain <see cref="INotifyPropertyChanged"/>
    /// object (no UI knowledge). The sidebar's bound card binds <c>Label</c> ← <see cref="Banner"/> and
    /// <c>Visibility</c> ← <see cref="Live"/>; 'b' flips these on the VM and the live Cursorial.UI.Data
    /// bindings carry the change to the element with no manual element writes.
    /// </summary>
    private sealed class BannerVm : INotifyPropertyChanged
    {
        private string _banner = "";
        private bool _live;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Banner
        {
            get => _banner;
            set => Set(ref _banner, value);
        }

        public bool Live
        {
            get => _live;
            set => Set(ref _live, value);
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>A one-way bool → <see cref="Visibility"/> converter (true ⇒ Visible, false ⇒ Collapsed).</summary>
    private sealed class BoolToVisibility : IValueConverter
    {
        public static readonly BoolToVisibility Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
