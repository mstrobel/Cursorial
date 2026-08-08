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
