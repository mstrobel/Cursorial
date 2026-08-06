using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Rendering;
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
}
