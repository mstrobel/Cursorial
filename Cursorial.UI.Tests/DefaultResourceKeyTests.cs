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
/// <remarks>
/// This file also holds precedence-matrix <b>§21 (M302–M307)</b>, the tier's normative rows — the one
/// exception to the matrix's file-per-section rule, because the §0.1 fixture's <c>Host</c> is a bare
/// <c>UIObject</c> and <c>DefaultResourceKey</c> resolution needs a <c>UIElement</c> with a resource
/// chain. The headless host is also what lets the rows assert the FRAME, which is the whole point: the
/// failure mode is a property and a frame that disagree.
/// </remarks>
public class DefaultResourceKeyTests
{
    private sealed class Probe : UIElement
    {
        public static readonly StyledProperty<string?> WithBogusKeyProperty =
            UIProperty.Register<Probe, string?>(
                "WithBogusKey",
                new PropertyMetadata<string?>("fallback") { DefaultResourceKey = "cursorial.tests.no-such-key" });
    }

    [Fact] // M302 (resolution at rest)
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

    [Fact] // M302 (the ladder above the tier is unchanged)
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

    [Fact] // M302 (unresolvable key ⇒ the registered default, PD8)
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

    /// <summary>The re-entrant overwrite — distinct from <see cref="Marker"/> and from the themed brush.</summary>
    private static readonly SolidColorBrush Overwrite = new(Color.FromRgb(11, 220, 140));

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
    [Fact] // M304
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
    [Fact] // M305
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
    [Fact] // M303
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

    /// <summary>
    /// <b>Both directions, end to end.</b> The M100 pair (<c>Default→Local→Default</c>) at the themed
    /// tier, run twice: once with an ordinary brush, once with the RAW <c>metadata.DefaultValue</c>
    /// (<see langword="null"/>) as the written value. The second round is the discriminator — a
    /// raw-answered baseline makes BOTH of its steps compare equal and stay silent while the effective
    /// value and the frame move underneath.
    /// </summary>
    [Fact] // M306
    public void SourceTransitions_AcrossTheThemedTier_BothDirections()
    {
        using var host = NewHost();
        var probe = ShowProbe(host, out var themed, out var themedInk);

        var recorder = new BrushRecorder();
        using var subscription = probe.AddObserver(BrushProbe.FillProperty, recorder);

        Assert.Equal(BindingPriority.Default, probe.GetValueSource(BrushProbe.FillProperty).Priority);

        probe.SetValue(BrushProbe.FillProperty, Marker);
        host.RunUntilIdle();
        Assert.Equal(BindingPriority.LocalValue, probe.GetValueSource(BrushProbe.FillProperty).Priority);
        Assert.Equal(1, recorder.Count);
        Assert.Same(themed, recorder.OldValue);
        Assert.Same(Marker, recorder.NewValue);
        Assert.Equal(Marker.Color, host.GetCell(0, 0).Style.Background);

        probe.ClearValue(BrushProbe.FillProperty);
        host.RunUntilIdle();
        Assert.Equal(BindingPriority.Default, probe.GetValueSource(BrushProbe.FillProperty).Priority);
        Assert.Equal(2, recorder.Count);
        Assert.Same(Marker, recorder.OldValue);
        Assert.Same(themed, recorder.NewValue);
        Assert.Equal(themedInk, host.GetCell(0, 0).Style.Background);

        probe.SetValue(BrushProbe.FillProperty, null); // the raw default, written as a VALUE (not CV)
        host.RunUntilIdle();
        Assert.Equal(BindingPriority.LocalValue, probe.GetValueSource(BrushProbe.FillProperty).Priority);
        Assert.Equal(3, recorder.Count);
        Assert.Same(themed, recorder.OldValue);
        Assert.Null(recorder.NewValue);
        Assert.Equal(BrushProbe.NullSentinel, host.GetCell(0, 0).Style.Background);

        probe.ClearValue(BrushProbe.FillProperty);
        host.RunUntilIdle();
        Assert.Equal(BindingPriority.Default, probe.GetValueSource(BrushProbe.FillProperty).Priority);
        Assert.Equal(4, recorder.Count);
        Assert.Null(recorder.OldValue);
        Assert.Same(themed, recorder.NewValue);
        Assert.Equal(themedInk, host.GetCell(0, 0).Style.Background);
    }

    /// <summary>
    /// A winning-base observer (A20) that issues exactly ONE re-entrant <c>SetCurrentValue</c> from its
    /// first delivery — the PD18 synchronous-recursion contract exercised at the one seam where the
    /// entry is mid-update.
    /// </summary>
    private sealed class ReentrantOverwriter(UIObject target) : IValueObserver<IBrush?>
    {
        private bool _fired;

        public List<(IBrush? Old, IBrush? New)> BaseDeliveries { get; } = [];

        void IValueObserver<IBrush?>.OnPropertyChanged(
            UIObject source, UIProperty property, IBrush? oldValue, IBrush? newValue, BindingPriority priority)
        {
        }

        void IValueObserver<IBrush?>.OnBaseValueChanged(
            UIObject source, UIProperty property, IBrush? oldBaseValue, IBrush? newBaseValue, bool isAnimated)
        {
            BaseDeliveries.Add((oldBaseValue, newBaseValue));

            if (_fired)
                return;

            _fired = true;
            target.SetCurrentValue(BrushProbe.FillProperty, Overwrite);
        }
    }

    /// <summary>
    /// <b>The re-entrant seam.</b> <c>SetCurrentValue</c>'s un-animated arm carries its own
    /// <c>oldBaseValue</c> baseline, and its <c>BasePriority == Unset</c> branch is unreachable from any
    /// RESTING state (the method's own guard routes an <c>Unset</c> effective lane to the local mouth, and
    /// for an un-animated entry the effective and base lanes are assigned together at every mutation site).
    /// It is reachable from inside <c>Reevaluate</c>'s retraction window: the base lane has already been
    /// set to <c>Unset</c> when the A20 winning-base channel fires, and the effective lane has not caught
    /// up — so a base observer that writes (PD18) lands in exactly that branch. The baseline it reports
    /// must be the same READ every other Default-tier exit answers: the themed brush, never the raw
    /// <c>metadata.DefaultValue</c> (<see langword="null"/> here).
    /// </summary>
    [Fact] // M307
    public void ReentrantSetCurrentValueDuringABaseRetraction_BaselinesOnTheThemedBrush()
    {
        using var host = NewHost();
        var probe = ShowProbe(host, out var themed, out _);

        probe.SetValue(BrushProbe.FillProperty, Marker);
        host.RunUntilIdle();

        var observer = new ReentrantOverwriter(probe);
        using var subscription = probe.AddObserver(
            BrushProbe.FillProperty, observer, new ObserverOptions { IncludeBaseChanges = true });

        probe.ClearValue(BrushProbe.FillProperty); // retracts the only contribution ⇒ base goes Unset
        host.RunUntilIdle();

        Assert.Equal(2, observer.BaseDeliveries.Count);
        Assert.Same(Marker, observer.BaseDeliveries[0].Old);  // the retraction: the local value …
        Assert.Same(themed, observer.BaseDeliveries[0].New);  // … yielding to the themed default
        Assert.Same(themed, observer.BaseDeliveries[1].Old);  // the re-entrant overwrite REPLACES the themed brush …
        Assert.Same(Overwrite, observer.BaseDeliveries[1].New);
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
