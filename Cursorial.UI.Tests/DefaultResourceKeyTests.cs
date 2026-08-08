using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Themes;

namespace Cursorial.Tests.UI;

/// <summary>
/// Theme-reactive metadata defaults (<see cref="PropertyMetadata{T}.DefaultResourceKey"/>): the
/// <see cref="BindingPriority.Default"/> tier resolves a resource key through the element's chain,
/// so a bare <see cref="TextBlock"/> is legible with no ambient setup while every real lane —
/// inheritance included — beats it. The lazy read keeps provenance honest (still Default) and the
/// theme-origin catch-all repaints default-tier consumers (they own no subscription to pulse).
/// </summary>
public class DefaultResourceKeyTests
{
    private sealed class Probe : UIElement
    {
        public static readonly StyledProperty<string?> WithBogusKeyProperty =
            UIProperty.Register<Probe, string?>(
                "WithBogusKey",
                new PropertyMetadata<string?>("fallback") { DefaultResourceKey = "cursorial.tests.no-such-key" });
    }

    [Fact]
    public void BareTextBlock_ResolvesThemeTextBrush_AtDefaultTier()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 4) });
        var text = new TextBlock { Text = "hi" };
        host.ShowRoot(text);
        host.RunUntilIdle();

        Assert.True(text.TryFindResource(ThemeKeys.TextBrush, out var themed));
        Assert.Same(themed, text.GetValue(TextBlock.ForegroundProperty));

        // No store entry, nothing set: provenance stays an honest Default.
        Assert.Equal(BindingPriority.Default, text.GetValueSource(TextBlock.ForegroundProperty).Priority);
    }

    [Fact]
    public void InheritedValue_BeatsTheThemeReactiveDefault()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 4) });
        var text = new TextBlock { Text = "hi" };
        var panel = new StackPanel();
        panel.Children.Add(text);
        var ancestral = new SolidColorBrush(Color.FromRgb(255, 128, 0));
        TextElement.SetForeground(panel, ancestral);
        host.ShowRoot(panel);
        host.RunUntilIdle();

        Assert.Same(ancestral, text.GetValue(TextBlock.ForegroundProperty));
        Assert.Equal(BindingPriority.Inherited, text.GetValueSource(TextBlock.ForegroundProperty).Priority);
    }

    [Fact]
    public void UnresolvableKey_FallsBackToTheMetadataDefault()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 4) });
        var probe = new Probe();
        host.ShowRoot(probe);
        host.RunUntilIdle();

        Assert.Equal("fallback", probe.GetValue(Probe.WithBogusKeyProperty));
    }

    [Fact]
    public void ThemeBaseFlip_RepaintsDefaultTierText()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 4) });
        var text = new TextBlock { Text = "hi", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        host.ShowRoot(text);
        host.Application.RequestedThemeBase = ThemeBase.Dark;
        host.RunUntilIdle();
        var dark = host.FrameBuffer[0, 0].Style.Foreground;

        // Default-tier consumers have no resource subscription; the catch-all walk must still
        // repaint them when the base flips (the value read is lazy and already current — this
        // asserts the CELLS caught up too).
        host.Application.RequestedThemeBase = ThemeBase.Light;
        host.RunUntilIdle();
        var light = host.FrameBuffer[0, 0].Style.Foreground;

        Assert.NotEqual(dark, light);
    }

    /// <summary>
    /// The template-part clone of <see cref="ThemeBaseFlip_RepaintsDefaultTierText"/>: an
    /// <see cref="Icon"/> with NO ambient <c>Foreground</c> (a bare root — under a <c>Window</c>
    /// every descendant gets a real INHERITED value and this lane never opens). The bare TextBlock
    /// above re-reads the themed default itself, so a repaint suffices; a control paints through a
    /// template, and <c>{TemplateBinding Foreground}</c> latches a COPY at
    /// <see cref="BindingPriority.Template"/>. The default-tier catch-all skips that copy (it only
    /// invalidates, and only elements still AT <see cref="BindingPriority.Default"/>), so repainting
    /// re-paints the stale value. The theme must PIN <c>Foreground</c> to a resource so the flip
    /// raises a real change the template plumbing can forward.
    /// </summary>
    [Fact]
    public void ThemeBaseFlip_RepaintsDefaultTierIcon()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 4) });
        var icon = new Icon { Text = "*", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        host.ShowRoot(icon);
        host.Application.RequestedThemeBase = ThemeBase.Dark;
        host.RunUntilIdle();

        Assert.Equal("*", host.GetCell(0, 0).Grapheme); // the Text tier landed where we sample
        var dark = host.GetCell(0, 0).Style.Foreground;

        host.Application.RequestedThemeBase = ThemeBase.Light;
        host.RunUntilIdle();
        var light = host.GetCell(0, 0).Style.Foreground;

        Assert.NotEqual(dark, light);
    }

    /// <summary>
    /// The same clone for <see cref="Expander"/>'s header twisty — a template part whose
    /// <c>Foreground</c> is a <c>{TemplateBinding}</c> copy. This one is GREEN today, and the reason is
    /// worth pinning: the templated parent of the header template is <c>PART_Header</c> (a
    /// <see cref="ToggleButton"/>), NOT the <see cref="Expander"/>, and <c>Theme.ToggleButton</c> pins
    /// <c>Foreground</c> to the palette spine — so the copy's source is never at
    /// <see cref="BindingPriority.Default"/>, whatever <c>Theme.Expander</c> does or does not set.
    /// Removing that pin makes this test fail with exactly the stuck-ink symptom the Icon clone shows,
    /// which is what earns it a place here as a regression guard.
    /// </summary>
    [Fact]
    public void ThemeBaseFlip_RepaintsDefaultTierExpanderHeader()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 4) });
        var expander = new Expander
                       {
                           Header = "Head",
                           Content = new TextBlock { Text = "body" },
                           HorizontalAlignment = HorizontalAlignment.Left,
                           VerticalAlignment = VerticalAlignment.Top
                       };
        host.ShowRoot(expander);
        host.Application.RequestedThemeBase = ThemeBase.Dark;
        host.RunUntilIdle();
        var dark = ForegroundOfGlyph(host, "⏵");

        host.Application.RequestedThemeBase = ThemeBase.Light;
        host.RunUntilIdle();
        var light = ForegroundOfGlyph(host, "⏵");

        Assert.NotEqual(dark, light);
    }

    // ───────────────────── transitions ACROSS the tier (the GetUnsetFallback seam) ─────────────────────

    /// <summary>
    /// The shape the thirty <c>IBrush?</c> registrations share: a themed default with NO positional
    /// default, so <c>metadata.DefaultValue</c> is <see langword="null"/> and the raw metadata default
    /// is nothing like the value a read returns. The element paints its <see cref="FillProperty"/>
    /// (or <see cref="NullSentinel"/> when it reads back null), so the FRAME reports which of the two
    /// was actually in effect.
    /// </summary>
    private sealed class BrushProbe : UIElement
    {
        /// <summary>The ink meaning "the themed brush is gone" — never a theme colour.</summary>
        public static readonly Color NullSentinel = Color.FromRgb(3, 5, 7);

        private static readonly SolidColorBrush NullBrush = new(NullSentinel);

        public static readonly StyledProperty<IBrush?> FillProperty =
            UIProperty.Register<BrushProbe, IBrush?>(
                "Fill", new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.AccentBrush });

        static BrushProbe() => AffectsRender<BrushProbe>(FillProperty);

        protected override Size MeasureOverride(Size availableSize) => availableSize;

        protected override void Render(RenderContext context)
            => context.FillOpaque(new Rect(0, 0, 2, 1),
                                  new StyleDeltaTemplate { Background = GetValue(FillProperty) ?? NullBrush });
    }

    private sealed class BrushRecorder : IValueObserver<IBrush?>
    {
        public int Count;
        public IBrush? OldValue;
        public IBrush? NewValue;
        public BindingPriority Priority;

        void IValueObserver<IBrush?>.OnPropertyChanged(
            UIObject source, UIProperty property, IBrush? oldValue, IBrush? newValue, BindingPriority priority)
        {
            Count++;
            OldValue = oldValue;
            NewValue = newValue;
            Priority = priority;
        }
    }

    /// <summary>A local write that is neither the themed default nor <see cref="BrushProbe.NullSentinel"/>.</summary>
    private static readonly SolidColorBrush Marker = new(Color.FromRgb(200, 30, 90));

    private static UIHeadlessHost NewHost()
        => UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 4) });

    private static BrushProbe ShowProbe(UIHeadlessHost host, out IBrush themed, out Color themedInk)
    {
        var probe = new BrushProbe();
        host.ShowRoot(probe);
        host.RunUntilIdle();

        Assert.True(probe.TryFindResource(ThemeKeys.AccentBrush, out var resolved));
        themed = Assert.IsAssignableFrom<IBrush>(resolved);
        Assert.Same(themed, probe.GetValue(BrushProbe.FillProperty));

        themedInk = host.GetCell(0, 0).Style.Background;
        Assert.NotEqual(BrushProbe.NullSentinel, themedInk); // the frame really is showing the themed default
        return probe;
    }

    /// <summary>
    /// <b>Into the tier.</b> An element resting at <see cref="BindingPriority.Default"/> renders the
    /// themed brush; a plain <c>SetValue(p, null)</c> — which is NOT a <c>ClearValue</c> (only
    /// <c>UnsetValue</c> is) — moves the effective value from that brush to <see langword="null"/>.
    /// The store's old-value baseline is <c>ValueStore.GetUnsetFallback</c>: if it answers the RAW
    /// <c>metadata.DefaultValue</c> (null) instead of the value a read returns, the write looks like a
    /// no-op, the notification and <c>AffectsRender</c> never fire — and the entry has ALREADY been
    /// written, so the element is silently null while the frame still shows the theme.
    /// </summary>
    [Fact]
    public void WritingNullOverAThemedDefault_NotifiesAndRepaints()
    {
        using var host = NewHost();
        var probe = ShowProbe(host, out var themed, out var themedInk);

        var recorder = new BrushRecorder();
        using var subscription = probe.AddObserver(BrushProbe.FillProperty, recorder);

        probe.SetValue(BrushProbe.FillProperty, null);
        host.RunUntilIdle();

        Assert.Null(probe.GetValue(BrushProbe.FillProperty));                   // the effective value DID move …
        Assert.NotEqual(themedInk, host.GetCell(0, 0).Style.Background);        // … so the frame must follow …
        Assert.Equal(BrushProbe.NullSentinel, host.GetCell(0, 0).Style.Background);
        Assert.Equal(1, recorder.Count);                                        // … and the change must be announced …
        Assert.Same(themed, recorder.OldValue);                                 // … carrying the brush that was in effect.
    }

    /// <summary>
    /// <b>Out of the tier.</b> The mirror: a local <see langword="null"/> is retracted, so the read
    /// goes back to the themed brush. <c>Reevaluate</c>'s promotion baseline is the same
    /// <c>GetUnsetFallback</c> — answered raw it equals the retracted local, the promotion looks
    /// equal-valued and returns silently, and the frame stays on the withdrawn value forever.
    /// </summary>
    [Fact]
    public void ClearingBackToAThemedDefault_NotifiesAndRepaints()
    {
        using var host = NewHost();
        var probe = ShowProbe(host, out var themed, out var themedInk);

        probe.SetValue(BrushProbe.FillProperty, Marker);
        host.RunUntilIdle();
        probe.SetValue(BrushProbe.FillProperty, null);
        host.RunUntilIdle();
        Assert.Equal(BrushProbe.NullSentinel, host.GetCell(0, 0).Style.Background);

        var recorder = new BrushRecorder();
        using var subscription = probe.AddObserver(BrushProbe.FillProperty, recorder);

        probe.ClearValue(BrushProbe.FillProperty);
        host.RunUntilIdle();

        Assert.Same(themed, probe.GetValue(BrushProbe.FillProperty));
        Assert.Equal(themedInk, host.GetCell(0, 0).Style.Background);
        Assert.Equal(1, recorder.Count);
        Assert.Same(themed, recorder.NewValue);
        Assert.Equal(BindingPriority.Default, recorder.Priority);
    }

    /// <summary>
    /// The always-on half: even when the transition is NOT swallowed, the notification's
    /// <c>oldValue</c> must be the themed brush that was in effect — not the raw
    /// <c>metadata.DefaultValue</c>. Anything that caches the payload (a <c>Changed</c> callback, an
    /// animation handoff snapshot) reads this, not the property.
    /// </summary>
    [Fact]
    public void LeavingAThemedDefault_CarriesTheThemedBrushAsTheOldValue()
    {
        using var host = NewHost();
        var probe = ShowProbe(host, out var themed, out _);

        var recorder = new BrushRecorder();
        using var subscription = probe.AddObserver(BrushProbe.FillProperty, recorder);

        probe.SetValue(BrushProbe.FillProperty, Marker);
        host.RunUntilIdle();

        Assert.Equal(1, recorder.Count);
        Assert.Same(themed, recorder.OldValue); // NOT null, the raw metadata default
        Assert.Same(Marker, recorder.NewValue);
        Assert.Equal(BindingPriority.LocalValue, recorder.Priority);
        Assert.Equal(Marker.Color, host.GetCell(0, 0).Style.Background);
    }

    private static Color ForegroundOfGlyph(UIHeadlessHost host, string grapheme)
    {
        for (var r = 0; r < host.FrameBuffer.Rows; r++)
        for (var c = 0; c < host.FrameBuffer.Columns; c++)
        {
            var cell = host.GetCell(c, r);
            if (cell.Grapheme == grapheme)
                return cell.Style.Foreground;
        }

        throw new Xunit.Sdk.XunitException($"No cell rendered the grapheme '{grapheme}'.");
    }
}
