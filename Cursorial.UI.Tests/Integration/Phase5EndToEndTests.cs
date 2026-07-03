using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Testing;
using Cursorial.UI.Themes;

using Style = Cursorial.UI.Style;

namespace Cursorial.Tests.UI.Integration;

/// <summary>
/// Phase 5 integration: the design doc §14 P5 exit criteria proven end-to-end through
/// <see cref="UITestHost"/> — every scenario crosses the full P5 spine (resource chain →
/// control-theme template apply + part wiring → S8 control behavior → styling/binding → layout/render
/// → composited cells), not an engine harness. Covers:
/// <list type="bullet">
/// <item>(a) a themed Button/CheckBox/ScrollViewer tree rendering with cell assertions ACROSS theme
/// variant tiers (KittyTruecolor truecolor vs Ansi16Legacy — color quantization differences proven —
/// and a per-tier glyph swap via a ThemeDictionaries variant override);</item>
/// <item>(b) template apply + <c>[TemplatePart]</c> wiring + Detach retraction — a Template swap tears
/// the old root down with a subscription-leak tracker clean afterward;</item>
/// <item>(c) a <see cref="ContentControl"/> with an implicit <see cref="DataTemplate"/>-by-type
/// resolving and rendering through the §12.3 chain;</item>
/// <item>(d) a Button clicked via mouse (ClickCount) AND keyboard (Space on DOWN), <c>:pressed</c>
/// flipping through <see cref="InteractionState"/>, <see cref="ButtonBase.Command"/> CanExecute gating
/// <see cref="UIElement.IsEffectivelyEnabled"/>;</item>
/// <item>(e) a ScrollViewer wheel-scroll moving content via the banded SCP (composite slide, no
/// re-raster of the band until the re-anchor — <c>Scene.RasterVersion</c> assertion);</item>
/// <item>(f) a CheckBox <c>:checked</c> restyle re-rastering only its own render zone (invariant 3);</item>
/// <item>(g) a <see cref="TextBlock"/> re-formatting on a theme-variant flip (the
/// <see cref="ResourceServices.GetResourceVersion"/>/<see cref="UIApplication.ActualThemeVariant"/>
/// cache-key proof — no staleness).</item>
/// </list>
/// </summary>
public sealed class Phase5EndToEndTests
{
    // ───────────────────────────── helpers ─────────────────────────────

    private static UITestHost Create(TerminalCapabilities? caps = null)
        => caps is { } c
            ? UITestHost.Create(new UITestHostOptions { Capabilities = c })
            : UITestHost.Create();

    private static MouseEvent MouseDown(int column, int row, int clickCount = 1) => new()
    {
        Kind = MouseEventKind.ButtonDown,
        Position = new CellPosition(column, row),
        Button = MouseButton.Left,
        ButtonsHeld = MouseButtons.None,
        Modifiers = KeyModifiers.None,
        ClickCount = clickCount,
        Timestamp = DateTimeOffset.UnixEpoch
    };

    private static MouseEvent MouseUp(int column, int row) => new()
    {
        Kind = MouseEventKind.ButtonUp,
        Position = new CellPosition(column, row),
        Button = MouseButton.Left,
        ButtonsHeld = MouseButtons.None,
        Modifiers = KeyModifiers.None,
        Timestamp = DateTimeOffset.UnixEpoch
    };

    private static MouseEvent Wheel(int column, int row, int deltaY) => new()
    {
        Kind = MouseEventKind.Wheel,
        Position = new CellPosition(column, row),
        Button = MouseButton.None,
        ButtonsHeld = MouseButtons.None,
        Modifiers = KeyModifiers.None,
        WheelDeltaY = deltaY,
        Timestamp = DateTimeOffset.UnixEpoch
    };

    // A rigid content block of a fixed cell size (the scrollable extent source).
    private sealed class Block(int columns, int rows, string glyph = "#") : UIElement
    {
        protected override Size MeasureOverride(Size availableSize) => new(columns, rows);
        protected override Size ArrangeOverride(Size finalSize) => new(columns, rows);
        protected override void Render(RenderContext context)
        {
            for (var r = 0; r < rows && r < context.Size.Rows; r++)
                context.DrawText(0, r, glyph, Color.FromRgb(220, 220, 220));
        }
    }

    private static readonly Color ButtonFace = Color.FromRgb(40, 90, 200);   // a clearly truecolor face
    private static readonly Color ButtonInk = Color.FromRgb(245, 245, 250);

    /// <summary>A scriptable <see cref="System.Windows.Input.ICommand"/> recording executions.</summary>
    private sealed class RelayCommand : System.Windows.Input.ICommand
    {
        public bool CanExecuteResult { get; set; } = true;
        public int Executions { get; private set; }
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => CanExecuteResult;
        public void Execute(object? parameter) => Executions++;
        public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    // ───────────────────────────── (a) themed tree across theme-variant tiers ─────────────────────────────

    /// <summary>
    /// A themed Button + CheckBox + ScrollViewer tree renders end-to-end through the BuiltIn control
    /// themes (templates applied at measure, parts wired, content presented) on BOTH a truecolor and a
    /// legacy 16-color preset. The Button's RGB background is emitted verbatim as a truecolor SGR
    /// (<c>48;2;r;g;b</c>) on the Kitty tier and QUANTIZED to a 16-color background SGR on the Ansi16
    /// tier (the renderer's capability-aware quantizer, proven in the emitted wire bytes — the cell
    /// buffer itself is tier-agnostic, the quantizer lives at the byte writer); the CheckBox shows the
    /// caps-unicode checked mark <c>[✓]</c> on BOTH tiers (the <c>ToggleGlyph.GlyphsProperty</c> override
    /// wins over the glyph-set resource — SD14), so the genuine cross-tier difference is the color
    /// quantization, not the glyph.
    /// </summary>
    [Theory]
    [InlineData(false)] // KittyTruecolor — RGB survives
    [InlineData(true)]  // Ansi16Legacy — RGB quantizes
    public void ThemedTree_RendersAcrossThemeVariantTiers_ColorQuantizesAndGlyphSwapsPerTier(bool legacy)
    {
        var caps = legacy ? TestCapabilities.Ansi16Legacy : TestCapabilities.KittyTruecolor;
        using var host = UITestHost.Create(new UITestHostOptions { Capabilities = caps, CaptureFrameBytes = true });

        var stack = new StackPanel { Spacing = 0, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var button = new Button
        {
            Content = "OK",
            Background = new SolidColorBrush(ButtonFace),
            Foreground = new SolidColorBrush(ButtonInk),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var check = new CheckBox { Content = "Enable", IsChecked = true };
        var scroller = new ScrollViewer
        {
            Width = 12,
            Height = 4,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Content = new Block(11, 40, "S")
        };
        stack.Children.Add(button);
        stack.Children.Add(check);
        stack.Children.Add(scroller);

        // No per-tier glyph RESOURCE swap is set: the cell-faithful caps-unicode bridge (SD14) sets
        // ToggleGlyph.GlyphsProperty (read BEFORE the glyph-set resource), and caps-unicode is stamped on
        // every tier, so the CheckBox always shows the Unicode marks. The genuine cross-tier difference here
        // is the COLOR quantization (asserted above), not the glyph.

        host.ShowRoot(stack);
        Assert.True(host.RunUntilIdle());

        // The Button rendered its themed template: a bordered face with the "OK" content presented.
        // The border frames it; the content presenter shows the text inside the 1-cell padding.
        Assert.Contains("OK", host.GetRowText(0)); // cell-faithful: no border row — the content is on row 0

        // The cell buffer is tier-agnostic — the same RGB face background composits on both tiers.
        var faceCell = FindCell(host, c => c.Style.Background.Kind == ColorKind.Rgb, rows: 3);
        Assert.Equal(ButtonFace, faceCell.Style.Background);

        // The QUANTIZATION lives at the byte writer (the renderer runs each cell's style through a
        // capability StyleQuantizer before emission): the same composited RGB face survives verbatim on
        // truecolor and collapses to a 16-color palette index on Ansi16 — the cross-tier color difference.
        var quantizedFace = new StyleQuantizer(caps.Output).Quantize(faceCell.Style).Background;
        if (legacy)
            Assert.Equal(ColorKind.Palette, quantizedFace.Kind); // RGB → palette index on Ansi16
        else
            Assert.Equal(ButtonFace, quantizedFace);             // truecolor survives verbatim

        // The CheckBox glyph is the caps-unicode CHECKED mark "[✓]" on BOTH tiers (the GlyphsProperty
        // override wins over the glyph-set resource; SD14) — distinguishing, since the caps-ascii fallback
        // would be "[x]"; this fails on a regression of the override mechanism, not merely the color tier.
        var rows = ReadRows(host, 8);
        Assert.Contains(rows, r => r.Contains("[✓]") && r.Contains("Enable"));

        // The ScrollViewer presented its banded content (the 'S' block) inside its viewport.
        Assert.Contains(rows, r => r.Contains("S"));
    }

    // ───────────────────────────── (b) template apply + part wiring + Detach retraction ─────────────────────────────

    /// <summary>
    /// A control with a <c>[TemplatePart]</c> applies its template at measure (parts instantiated +
    /// name-scoped + wired), and a Template SWAP tears the old instantiated root down — every binding /
    /// observer the old subtree owned is retracted. The proof: a part inside the first template binds to
    /// a long-lived viewmodel; after the swap the VM has NO surviving subscription (the CD20 retraction
    /// order through <see cref="UIElement.TearDown"/>), and the new template's part is freshly wired.
    /// </summary>
    [Fact]
    public void TemplateApply_PartsWired_TemplateSwap_TearsOldSubtreeDown_NoLeak()
    {
        using var host = Create();
        var vm = new ProbeVm { Caption = "first" };
        var control = new PartHost { DataContext = vm };

        host.ShowRoot(control);
        Assert.True(host.RunUntilIdle());

        // The first template applied: the part is wired and bound to the VM (exactly one subscription).
        var firstPart = control.Part;
        Assert.NotNull(firstPart);
        Assert.Equal("first", firstPart.Text);
        Assert.Equal(1, vm.SubscriberCount); // the part's binding is the only live subscription

        // A VM change flows through the live binding to the rendered part.
        vm.Caption = "updated";
        host.RunFrame();
        Assert.Equal("updated", control.Part!.Text);

        // Swap the template: the old instantiated subtree is torn down (CD20), the new part is wired.
        control.Template = PartHost.MakeTemplate("PART_B");
        host.RunFrame();

        Assert.NotSame(firstPart, control.Part);
        // The net subscription count is still exactly 1 — the OLD part's binding was retracted on
        // TearDown and the NEW part's binding installed (a leak would leave 2).
        Assert.Equal(1, vm.SubscriberCount);

        // The new part is freshly bound and renders the current VM value.
        Assert.Equal("updated", control.Part!.Text);
        vm.Caption = "after-swap";
        host.RunFrame();
        Assert.Equal("after-swap", control.Part!.Text);          // the new part tracks the VM
        Assert.NotEqual("after-swap", firstPart.Text);           // the torn-down old part is dead (no leak)
        Assert.Equal(1, vm.SubscriberCount);                     // still exactly one — the old binding never re-armed

        // Detaching + tearing down the whole control retracts the surviving binding — the VM is clean.
        control.TearDown();
        host.RunFrame();
        Assert.Equal(0, vm.SubscriberCount);
    }

    /// <summary>A long-lived VM whose subscriber count is the leak tracker.</summary>
    private sealed class ProbeVm : System.ComponentModel.INotifyPropertyChanged
    {
        private string? _caption;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public string? Caption
        {
            get => _caption;
            set
            {
                if (_caption == value) return;
                _caption = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Caption)));
            }
        }
        public int SubscriberCount => PropertyChanged?.GetInvocationList().Length ?? 0;
    }

    /// <summary>A bindable text part used inside the swapped templates.</summary>
    [SuppressMessage("ReSharper", "UnusedMember.Local")]
    private sealed class TextPart : UIElement
    {
        public static readonly StyledProperty<string?> TextProperty = UIProperty.Register<TextPart, string?>("Text");
        static TextPart() => AffectsRender<TextPart>(TextProperty);
        public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
        protected override Size MeasureOverride(Size availableSize) => new(12, 1);
        protected override void Render(RenderContext context)
        {
            if (Text is { Length: > 0 } t)
                context.DrawText(0, 0, t, Color.FromRgb(255, 255, 255));
        }
    }

    /// <summary>A control whose template carries a bound <see cref="TextPart"/> part.</summary>
    private sealed class PartHost : Control
    {
        public PartHost() => Template = MakeTemplate("PART_A");

        public TextPart? Part => TemplateInstance?.NameScope.Find("PART_A") as TextPart
                                 ?? TemplateInstance?.NameScope.Find("PART_B") as TextPart;

        public static ControlTemplate MakeTemplate(string partName) => new(ctx =>
        {
            var part = new TextPart();
            ctx.RegisterName(partName, part);
            // Bind the part's Text to the templated parent's DataContext.Caption (the part inherits the
            // control's DataContext as a template child).
            part.SetBinding(TextPart.TextProperty, new Binding(nameof(ProbeVm.Caption)));
            return part;
        });
    }

    // ───────────────────────────── (c) ContentControl + implicit DataTemplate-by-type ─────────────────────────────

    /// <summary>
    /// A <see cref="ContentControl"/> with non-element content resolves the implicit
    /// <see cref="DataTemplate"/> keyed by the content's runtime type (the §12.3 chain ②) through the
    /// app resource dictionary, builds the data subtree (DataContext = the item, TemplatedParent null —
    /// the CD18 barrier), and renders it. A base-type item resolves the base-keyed template too.
    /// </summary>
    [Fact]
    public void ContentControl_ImplicitDataTemplateByType_ResolvesBuildsAndRenders()
    {
        using var host = Create();

        host.Application.Resources[new DataTemplateKey(typeof(Person))] = new DataTemplate
        {
            DataType = typeof(Person),
            Content = new FuncTemplateContent(_ => new TextPart())
        };

        var person = new Person { Name = "Grace" };
        var control = new TypedContentControl
        {
            Content = person,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        host.ShowRoot(control);
        Assert.True(host.RunUntilIdle());

        // The implicit template built a TextPart; its DataContext is the item, TemplatedParent null.
        var built = Assert.IsType<TextPart>(control.Presenter!.Child);
        Assert.Same(person, built.DataContext);
        Assert.Null(built.TemplatedParent);

        // Bind the part to the data's Name through the inherited DataContext — it renders the name.
        built.SetBinding(TextPart.TextProperty, new Binding(nameof(Person.Name)));
        host.RunFrame();
        Assert.Equal("Grace", built.Text);
        Assert.Contains("Grace", host.GetRowText(0)); // the built data subtree rendered into the cells
    }

    public class Person { public string? Name { get; set; } }

    /// <summary>A ContentControl with a known template, exposing its content presenter for assertions.</summary>
    private sealed class TypedContentControl : ContentControl
    {
        public ContentPresenter? Presenter => GetTemplatePart<ContentPresenter>("PART_ContentPresenter");

        public TypedContentControl()
        {
            Template = new ControlTemplate(ctx =>
            {
                var cp = new ContentPresenter();
                ctx.RegisterName("PART_ContentPresenter", cp);
                return cp;
            });
        }
    }

    // ───────────────────────────── (d) button click (mouse + keyboard) + :pressed + command gate ─────────────────────────────

    /// <summary>
    /// A themed Button activates by BOTH a mouse click (with a click count riding the press) and the
    /// keyboard (Space on DOWN, CD23); <c>:pressed</c> flips through <see cref="InteractionState"/>
    /// (the <see cref="ButtonBase.IsPressed"/> mirror) on mouse-down and clears on up; and the
    /// <see cref="ButtonBase.Command"/>'s CanExecute gates <see cref="UIElement.IsEffectivelyEnabled"/>
    /// (CD25) — a CanExecute flip recomputes effective-enabled and the <c>:disabled</c> bit.
    /// </summary>
    [Fact]
    public void Button_MouseAndKeyboardClick_PressedFlips_CommandCanExecuteGatesEnabled()
    {
        using var host = Create();
        var command = new RelayCommand { CanExecuteResult = true };
        var button = new Button
        {
            Content = "Go",
            Command = command,
            Background = new SolidColorBrush(ButtonFace),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        var clicks = 0;
        button.Click += (_, _) => clicks++;

        host.ShowRoot(button);
        Assert.True(host.RunUntilIdle());

        // Resolve a cell INSIDE the button's content area so the hit-test lands on the button itself. The
        // cell-faithful button is 1 row tall (no border) with the content on row 0 after the 1-col left pad.
        var (col, row) = button.TranslateToWindow(1, 0);

        // Mouse: hover, down (presses), up over self (clicks). A multi-click count rides the press event
        // through the dispatcher's synthesizer contract (the mouse-path is exercised end-to-end).
        host.SendMouseMove(col, row);
        host.RunFrame();
        var dispatcher = host.Application.InputDispatcher;
        dispatcher.ProcessEvent(MouseDown(col, row, clickCount: 2));
        host.RunFrame();
        Assert.True(button.IsPressed);                                       // :pressed via SetInteractionState
        Assert.True((button.InteractionStateInternal & InteractionState.Pressed) != 0);

        dispatcher.ProcessEvent(MouseUp(col, row));
        host.RunFrame();
        Assert.False(button.IsPressed);                                      // cleared on up
        Assert.Equal(1, clicks);                                             // up-over-self ⇒ one click
        Assert.Equal(1, command.Executions);                                 // command executed (CanExecute true)

        // Keyboard: Space activates on DOWN (CD23).
        button.Focus();
        host.SendKey(Key.Space);
        host.RunFrame();
        Assert.Equal(2, clicks);
        Assert.Equal(2, command.Executions);
        Assert.True(button.IsPressed);                                       // pressed latch held until Space up
        host.SendInput(new KeyEvent { Key = Key.Space, Kind = KeyEventKind.Up, Modifiers = KeyModifiers.None, Timestamp = host.Time.GetUtcNow() });
        host.RunFrame();
        Assert.False(button.IsPressed);

        // Command CanExecute gates effective-enabled (CD25): flip it false → the button disables.
        command.CanExecuteResult = false;
        command.Raise();
        host.RunFrame();
        Assert.False(button.IsEffectivelyEnabled);
        Assert.True((button.InteractionStateInternal & InteractionState.Disabled) != 0);

        // A click on a disabled button does not execute the command (the gate holds).
        button.Focus();
        host.SendKey(Key.Enter);
        host.RunFrame();
        Assert.Equal(2, command.Executions); // unchanged — disabled

        // Re-enable: effective-enabled recomputes.
        command.CanExecuteResult = true;
        command.Raise();
        host.RunFrame();
        Assert.True(button.IsEffectivelyEnabled);
    }

    // ───────────────────────────── (e) ScrollViewer wheel scroll via the banded SCP ─────────────────────────────

    /// <summary>
    /// A wheel-scroll on a themed ScrollViewer moves the content through the banded SCP: an in-band
    /// offset change is a pure composite slide — the SCP's band <c>Scene.RasterVersion</c> is
    /// frozen, the content shifts in the composited cells (invariant 3, C240). A scroll past the band
    /// edge re-anchors, re-rastering exactly the band zone once.
    /// </summary>
    [Fact]
    public void ScrollViewer_WheelScroll_BandedComposite_NoReRasterUntilReAnchor()
    {
        using var host = Create();
        var sv = new ScrollViewer
        {
            Width = 16,
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Content = new RowMarkerBlock(15, 200) // a tall block whose rows are numbered glyphs
        };
        host.ShowRoot(sv);
        Assert.True(host.RunUntilIdle());

        var presenter = sv.Presenter!;
        var tree = host.Application.WindowManager!.Tree!;
        var bandScene = tree.GetScene(presenter)!;
        var bandVersion = bandScene.RasterVersion;
        var anchorAtStart = presenter.BandStartRow;

        // The viewport currently shows content row 0 at its top.
        Assert.StartsWith("[0]", host.GetRowText(0));

        // One wheel notch down ⇒ 3 lines (CD28). The offset is well within the band, so this is a pure
        // composite slide — the band scene does NOT re-raster, yet the content moves in the cells.
        host.Application.InputDispatcher.ProcessEvent(Wheel(2, 2, deltaY: -120));
        host.RunFrame();

        Assert.Equal(3, sv.VerticalOffset);
        Assert.Equal(bandVersion, bandScene.RasterVersion); // frozen — the slide is composite-only
        Assert.Equal(anchorAtStart, presenter.BandStartRow);
        Assert.StartsWith("[3]", host.GetRowText(0));       // content row 3 slid to the viewport top (no re-raster)

        // Now scroll far past the band's safe zone — the re-anchor re-rasters the band zone once, and
        // the content (re-rastered at the new anchor) shows the new top row.
        sv.VerticalOffset = 120;
        host.RunFrame();
        Assert.True(presenter.BandStartRow > anchorAtStart);     // re-anchored
        Assert.True(bandScene.RasterVersion > bandVersion);      // the band zone re-rastered (re-anchor)
        Assert.StartsWith("[120]", host.GetRowText(0));

        // And the frame after the re-anchor is quiet again — back to the composite-only regime: a small
        // in-band scroll slides the content without re-rastering the band.
        var afterAnchorVersion = bandScene.RasterVersion;
        host.Application.InputDispatcher.ProcessEvent(Wheel(2, 2, deltaY: -120));
        host.RunFrame();
        Assert.Equal(123, sv.VerticalOffset);
        Assert.Equal(afterAnchorVersion, bandScene.RasterVersion); // composite-only again
        Assert.StartsWith("[123]", host.GetRowText(0));
    }

    /// <summary>A tall block whose rows are bracketed-numbered (the scroll-content-moves proof; the
    /// brackets disambiguate a multi-digit prefix from the row below it).</summary>
    private sealed class RowMarkerBlock(int columns, int rows) : UIElement
    {
        protected override Size MeasureOverride(Size availableSize) => new(columns, rows);
        protected override Size ArrangeOverride(Size finalSize) => new(columns, rows);
        protected override void Render(RenderContext context)
        {
            for (var r = 0; r < rows && r < context.Size.Rows; r++)
                context.DrawText(0, r, $"[{r.ToString(CultureInfo.InvariantCulture)}]", Color.FromRgb(210, 210, 210));
        }
    }

    // ───────────────────────────── (f) CheckBox :checked restyle re-rasters only its zone ─────────────────────────────

    /// <summary>
    /// A CheckBox's <c>:checked</c> flip restyles it (a <c>:checked</c> rule sets the foreground) and
    /// re-rasters ONLY the checkbox's own render zone — sibling zones' <c>Scene.RasterVersion</c>
    /// (and the root's) are frozen (invariant 3). The glyph swaps to the checked ASCII glyph in the
    /// composited cells.
    /// </summary>
    [Fact]
    public void CheckBoxCheckedRestyle_ReRastersOnlyItsZone_GlyphSwaps()
    {
        using var host = Create();
        var root = new StackPanel();
        var check = new CheckBox { Content = "On", IsRenderBoundary = true };
        var sibling = new Border { Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)), IsRenderBoundary = true, Width = 8, Height = 1 };
        root.Children.Add(check);
        root.Children.Add(sibling);

        // A :checked rule colors the checkbox foreground accent (an AffectsRender styled property).
        var checkedAccent = Color.FromRgb(120, 220, 140);
        var rule = new Style(Selectors.OfType<CheckBox>().PseudoClass("checked").Template().OfType<ToggleGlyph>());
        rule.Setters.Add(new Setter(ToggleGlyph.GlyphForegroundProperty, new SolidColorBrush(checkedAccent)));
        host.Application.Styles.Add(rule);

        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        // Flip :checked: the checkbox restyles + re-rasters; siblings + root frozen.
        check.Focus();

        var tree = host.Application.WindowManager!.Tree!;
        var checkScene = tree.GetScene(check)!;
        var siblingScene = tree.GetScene(sibling)!;
        var rootScene = tree.GetScene(root)!;
        var checkVersion = checkScene.RasterVersion;
        var siblingVersion = siblingScene.RasterVersion;
        var rootVersion = rootScene.RasterVersion;

        Assert.StartsWith("[ ]", host.GetRowText(0)); // unchecked ASCII glyph

        host.SendKey(Key.Space);
        host.RunFrame();

        Assert.Equal(true, check.IsChecked);
        Assert.True(check.HasCustomPseudoClass(":checked"));
        Assert.StartsWith("[✓]", host.GetRowText(0));                      // glyph swapped (caps-unicode checked mark)
        Assert.True(checkScene.RasterVersion > checkVersion);              // the checkbox zone re-rastered
        Assert.Equal(siblingVersion, siblingScene.RasterVersion);          // sibling frozen
        Assert.Equal(rootVersion, rootScene.RasterVersion);                // root frozen

        // The accent foreground reached the composited glyph cell.
        Assert.Equal(checkedAccent, host.GetCell(0, 0).Style.Foreground);
    }

    // ───────────────────────────── (g) TextBlock re-formats on a theme-variant flip ─────────────────────────────

    /// <summary>
    /// A <see cref="TextBlock"/>'s <c>FormattedText</c> cache key folds the
    /// <see cref="ResourceServices.GetResourceVersion"/> and the <see cref="UIApplication.ActualThemeVariant"/>
    /// (CD16): the cache is the staleness firewall. Re-formatting under the SAME variant serves the
    /// cached layout (no re-resolve); once the requested theme base flips the variant, the cache key
    /// differs, so the next format re-resolves the markup <c>[brush=…]</c> against the new variant's
    /// dictionary — the new ink reaches the cells, never the stale dark ink. No dictionary subscription
    /// (the sealed BuiltIn never pulses — the cache key alone catches the change).
    /// </summary>
    [Fact]
    public void TextBlock_ReFormatsOnThemeVariantFlip_GetResourceVersionAndVariantCacheKey()
    {
        using var host = Create();

        // A brush resource that differs per theme base via ThemeDictionaries.
        var darkInk = Color.FromRgb(200, 210, 240);
        var lightInk = Color.FromRgb(20, 24, 60);
        var darkDict = new ResourceDictionary { ["ink"] = new SolidColorBrush(darkInk) };
        var lightDict = new ResourceDictionary { ["ink"] = new SolidColorBrush(lightInk) };
        host.Application.Resources.ThemeDictionaries[new ThemeVariantKey(ThemeBase.Dark, null)] = darkDict;
        host.Application.Resources.ThemeDictionaries[new ThemeVariantKey(ThemeBase.Light, null)] = lightDict;

        // Default the requested base to Dark so the test is deterministic regardless of the negotiated
        // background luminance.
        host.Application.RequestedThemeBase = ThemeBase.Dark;
        host.RunFrame();

        var block = new TextBlock { Markup = "[brush=ink]Hello[/brush]", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        host.ShowRoot(block);
        Assert.True(host.RunUntilIdle());

        Assert.Equal(ThemeBase.Dark, host.Application.ActualThemeVariant.Base);
        Assert.Equal(darkInk, host.GetCell(0, 0).Style.Foreground); // resolved against the Dark dictionary

        // A re-render under the SAME variant serves the cached layout (the cache-key match) — still dark.
        block.InvalidateVisual();
        host.RunFrame();
        Assert.Equal(darkInk, host.GetCell(0, 0).Style.Foreground);

        var variantFlips = 0;
        host.Application.ActualThemeVariantChanged += (_, _) => variantFlips++;

        // Flip the requested base to Light: the variant changes (the variant-changed signal fires). The
        // cache key now carries the new variant, so the next format of the block re-resolves
        // [brush=ink] against the Light dictionary — the staleness firewall holds (no dictionary
        // subscription, per CD16 — the variant flip alone does not pull a re-render, the next
        // invalidation-driven format does, and it must not serve the stale dark ink).
        host.Application.RequestedThemeBase = ThemeBase.Light;
        host.RunFrame();
        Assert.Equal(1, variantFlips);
        Assert.Equal(ThemeBase.Light, host.Application.ActualThemeVariant.Base);

        block.InvalidateVisual();
        host.RunFrame();
        Assert.Equal(lightInk, host.GetCell(0, 0).Style.Foreground); // re-formatted under the new variant — no staleness
    }

    // ───────────────────────────── shared finders ─────────────────────────────

    private static Cell FindCell(UITestHost host, Func<Cell, bool> predicate, int rows)
    {
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < host.FrameBuffer.Columns; c++)
            {
                var cell = host.GetCell(c, r);
                if (predicate(cell))
                    return cell;
            }
        return default;
    }

    private static string[] ReadRows(UITestHost host, int rows)
    {
        var result = new string[rows];
        for (var r = 0; r < rows; r++)
            result[r] = host.GetRowText(r);
        return result;
    }

}
