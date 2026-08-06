using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI;

/// <summary>
/// Regression: a control whose content is detached during a theme-base flip (an inactive TabControl tab) and
/// then re-attached must end up with the CORRECT (post-flip) inherited foreground — not a stale pre-flip color.
///
/// Root cause under test: re-attach re-expands the control's template (its <c>Control.Template</c> is a constant
/// control-theme Style setter, retracted on detach), and the freshly-built template's <c>{TemplateBinding
/// Foreground}</c> latches the stale (pre-flip) value instead of the templated parent's already-promoted current
/// value. The label then inherits the stale ink. (See the failed theme-flip investigation: the always-active tab
/// never detaches, so it never re-expands and tracks the flip normally.)
/// </summary>
public sealed class ThemeFlipDetachReattachTests
{
    // The R2 palette-spine TextBrush per variant (mirrors Section05_VariantFlipReSkin's pinned constants).
    private static readonly Color DarkText = Color.FromRgb(0xC0, 0xCA, 0xF5);  // (Dark, Ansi256) TextBrush
    private static readonly Color LightText = Color.FromRgb(0x34, 0x3B, 0x58); // (Light, Ansi256) TextBrush

    [Fact]
    public void BaseFlip_WhileTabContentDetached_ReattachRefreshesInheritedForeground()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            Capabilities = HeadlessCapabilities.KittyTruecolor,
            InitialSize = new Size(40, 12),
        });
        host.Application.RequestedThemeBase = ThemeBase.Dark;

        var tabs = new TabControl
        {
            Width = 40, Height = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        tabs.Items.Add(new TabItem { Header = "First", Content = new Label { Content = "Text Input" } });
        tabs.Items.Add(new TabItem { Header = "Second", Content = new Label { Content = "Other" } });
        host.ShowRoot(tabs);
        Assert.True(host.RunUntilIdle());

        // Baseline: First Tab realized in the dark variant — the label inherits the dark TextBrush.
        Assert.Equal(DarkText, ForegroundOf(host, "Text Input"));

        // Switch away → First Tab's content DETACHES from the visual tree (the shared content host rebuilds).
        tabs.SelectedIndex = 1;
        host.RunUntilIdle();

        // Flip dark→light WHILE First Tab is detached (the gallery repro: theme cycled on another tab).
        host.Application.RequestedThemeBase = ThemeBase.Light;
        host.RunUntilIdle();
        Assert.Equal(ThemeBase.Light, host.Application.ActualThemeVariant.Base);

        // Switch back → First Tab RE-ATTACHES; its template re-expands.
        tabs.SelectedIndex = 0;
        host.RunUntilIdle();

        // The label must show the LIGHT TextBrush, not the stale dark value latched by the re-expanded
        // template's {TemplateBinding Foreground}.
        Assert.Equal(LightText, ForegroundOf(host, "Text Input"));
    }

    [Fact] // Companion: the same scenario, but the control is realized via a DataTemplate (ContentPresenter
           // re-realizes fresh on switch-back) — the path the Control.ExpandTemplate reuse guard does NOT cover.
    public void BaseFlip_WhileDataTemplatedTabContentDetached_ReattachRefreshesForeground()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            Capabilities = HeadlessCapabilities.KittyTruecolor,
            InitialSize = new Size(40, 12),
        });
        host.Application.RequestedThemeBase = ThemeBase.Dark;

        // The tab content is a DATA item (a VM); an implicit DataTemplate (keyed by type) realizes it as a Label
        // whose foreground is theme-inherited. Switching tabs changes SelectedContent, so the shared content host
        // re-realizes the DataTemplate FRESH on switch-back — the path the Control.ExpandTemplate reuse guard does
        // NOT cover. "Payload" avoids the 'T'/header collision (no header starts with 'P').
        host.Application.Resources[new DataTemplateKey(typeof(PayloadVm))] =
            new DataTemplate { Content = new FuncTemplateContent(_ => new Label { Content = "Payload" }) };

        var tabs = new TabControl
        {
            Width = 40, Height = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        tabs.Items.Add(new TabItem { Header = "First", Content = new PayloadVm { Name = "Payload" } });
        tabs.Items.Add(new TabItem { Header = "Second", Content = new PayloadVm { Name = "Other" } });
        host.ShowRoot(tabs);
        Assert.True(host.RunUntilIdle());

        Assert.Equal(DarkText, ForegroundOf(host, "Payload")); // baseline: realized dark

        tabs.SelectedIndex = 1;
        host.RunUntilIdle();
        host.Application.RequestedThemeBase = ThemeBase.Light;
        host.RunUntilIdle();
        tabs.SelectedIndex = 0;
        host.RunUntilIdle();

        Assert.Equal(ThemeBase.Light, host.Application.ActualThemeVariant.Base);
        Assert.Equal(LightText, ForegroundOf(host, "Payload"));
    }

    private sealed class PayloadVm { public string Name { get; init; } = ""; }

    private static Color ForegroundOf(UIHeadlessHost host, string text)
    {
        var first = text[0];
        for (var r = 0; r < host.FrameBuffer.Rows; r++)
        for (var c = 0; c < host.FrameBuffer.Columns; c++)
        {
            var cell = host.GetCell(c, r);
            if (cell.Grapheme is { Length: > 0 } g && g[0] == first)
                return cell.Style.Foreground;
        }

        throw new Xunit.Sdk.XunitException($"'{text}' not found in the rendered frame.");
    }
}
