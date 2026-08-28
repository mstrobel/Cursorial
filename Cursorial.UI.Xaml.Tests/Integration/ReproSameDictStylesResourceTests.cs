// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Xaml;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Xaml.Integration;

/// <summary>
/// REPRO harness for the reported bug: a {StaticResource}/{DynamicResource} inside a Style that lives in a
/// ResourceDictionary's &lt;ResourceDictionary.Styles&gt; slot, referencing a keyed resource defined in the
/// SAME dictionary. Each test records the empirical outcome.
/// </summary>
public sealed class ReproSameDictStylesResourceTests
{
    private const string Ns = " xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    // ── DIAGNOSTIC: is the keyed entry even registered into the dict when a Styles slot is present? ──
    [Fact]
    public void Diagnostic_KeyedEntry_IsRegistered_WhenStylesPresent()
    {
        // (a) keyed entry ALONE (no Styles) — baseline.
        var alone = new XamlLoader().Load<ResourceDictionary>(
            "<ResourceDictionary" + Ns + ">" +
              "<SolidColorBrush x:Key=\"Accent\" Color=\"#FF0000\"/>" +
            "</ResourceDictionary>");

        // (b) keyed entry ALONGSIDE a Styles slot (the repro shape) — DynamicResource so load succeeds.
        var withStyles = new XamlLoader().Load<ResourceDictionary>(
            "<ResourceDictionary" + Ns + ">" +
              "<SolidColorBrush x:Key=\"Accent\" Color=\"#FF0000\"/>" +
              "<ResourceDictionary.Styles>" +
                "<Style TargetType=\"Button\">" +
                  "<Setter Property=\"Foreground\" Value=\"{DynamicResource Accent}\"/>" +
                "</Style>" +
              "</ResourceDictionary.Styles>" +
            "</ResourceDictionary>");

        // Report the observed state via asserts (xUnit shows the failing one).
        Assert.True(alone.ContainsKey("Accent"), $"[alone] keys=[{string.Join(",", alone.Keys)}] count={alone.Count}");
        Assert.True(withStyles.ContainsKey("Accent"),
            $"[withStyles] keys=[{string.Join(",", withStyles.Keys)}] count={withStyles.Count} stylesCount={withStyles.Styles?.Count}");
    }

    // ── STATIC, keyed brush BEFORE the Styles slot (the reporter's natural document order) ──
    [Fact]
    public void Static_KeyedBeforeStyles()
    {
        var xaml =
            "<ResourceDictionary" + Ns + ">" +
              "<SolidColorBrush x:Key=\"Accent\" Color=\"#FF0000\"/>" +
              "<ResourceDictionary.Styles>" +
                "<Style TargetType=\"Button\">" +
                  "<Setter Property=\"Foreground\" Value=\"{StaticResource Accent}\"/>" +
                "</Style>" +
              "</ResourceDictionary.Styles>" +
            "</ResourceDictionary>";

        var dict = new XamlLoader().Load<ResourceDictionary>(xaml);
        var setterValue = dict.Styles!.Single().Setters.Single().Value;
        // If StaticResource resolved, the value is the concrete brush; otherwise it would have thrown at load.
        var brush = Assert.IsType<SolidColorBrush>(setterValue);
        Assert.Equal(Color.FromRgb(255, 0, 0), brush.Color);
    }

    // ── STATIC, Styles slot BEFORE the keyed brush (adversarial document order) ──
    [Fact]
    public void Static_StylesBeforeKeyed()
    {
        var xaml =
            "<ResourceDictionary" + Ns + ">" +
              "<ResourceDictionary.Styles>" +
                "<Style TargetType=\"Button\">" +
                  "<Setter Property=\"Foreground\" Value=\"{StaticResource Accent}\"/>" +
                "</Style>" +
              "</ResourceDictionary.Styles>" +
              "<SolidColorBrush x:Key=\"Accent\" Color=\"#FF0000\"/>" +
            "</ResourceDictionary>";

        var dict = new XamlLoader().Load<ResourceDictionary>(xaml);
        var setterValue = dict.Styles!.Single().Setters.Single().Value;
        var brush = Assert.IsType<SolidColorBrush>(setterValue);
        Assert.Equal(Color.FromRgb(255, 0, 0), brush.Color);
    }

    // ── DIAGNOSTIC: does the runtime chain resolve the key when the dict IS app.Theme? ──
    [Fact]
    public void Diagnostic_RuntimeChain_ResolvesKey_WhenDictIsAppTheme()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(8, 1) });

        var theme = new XamlLoader().Load<ResourceDictionary>(
            "<ResourceDictionary" + Ns + ">" +
              "<SolidColorBrush x:Key=\"Accent\" Color=\"#FF0000\"/>" +
              "<ResourceDictionary.Styles>" +
                "<Style TargetType=\"Button\">" +
                  "<Setter Property=\"Foreground\" Value=\"{DynamicResource Accent}\"/>" +
                "</Style>" +
              "</ResourceDictionary.Styles>" +
            "</ResourceDictionary>");
        host.Application.Theme = theme;

        var panel = new UIControls.StackPanel { Name = "Root" };
        var button = new UIControls.Button { Content = "OK" };
        panel.Children.Add(button);
        host.ShowRoot(panel);
        Assert.True(host.RunUntilIdle());

        // Does app.Theme (== the containing dict) expose its own keyed entry to the runtime chain?
        var themeHit = theme.TryGetResource("Accent", host.Application.ActualThemeVariant, out var themeVal);
        var chainHit = button.TryFindResource("Accent", out var chainVal);

        Assert.True(themeHit, "app.Theme.TryGetResource('Accent') MISSED (own keyed entry invisible to TryGetResource).");
        Assert.True(chainHit,
            $"button.TryFindResource('Accent') MISSED — the runtime chain cannot see app.Theme's own keyed entry. themeHit={themeHit} themeVal={(themeVal as SolidColorBrush)?.Color}");
    }

    // ── DYNAMIC, applied as UIApplication.Theme, read the effective Foreground ──
    //
    // Target a TextBlock, NOT a Button: a Button auto-focuses and the BuiltIn '^:focus' control-theme
    // Foreground setter (a conditional rule arbitrating at StyleTrigger) shadows the resting Theme(2)
    // 'Button' selector on precedence alone — the resource resolves fine, it just doesn't WIN. A TextBlock
    // is not a Control (no control theme, no ':focus'), so the Theme(2) selector is the sole producer and
    // its effective value is decisive proof the same-dict {DynamicResource} both resolved AND applied.
    [Fact]
    public void Dynamic_AppliedAsTheme_ResolvesForeground()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(8, 1) });

        host.Application.Theme = new XamlLoader().Load<ResourceDictionary>(
            "<ResourceDictionary" + Ns + ">" +
              "<SolidColorBrush x:Key=\"Accent\" Color=\"#FF0000\"/>" +
              "<ResourceDictionary.Styles>" +
                "<Style TargetType=\"TextBlock\">" +
                  "<Setter Property=\"Foreground\" Value=\"{DynamicResource Accent}\"/>" +
                "</Style>" +
              "</ResourceDictionary.Styles>" +
            "</ResourceDictionary>");

        var panel = new UIControls.StackPanel { Name = "Root" };
        var text = new UIControls.TextBlock { Text = "OK" };
        panel.Children.Add(text);
        host.ShowRoot(panel);
        Assert.True(host.RunUntilIdle());

        var fg = text.GetValue(UIControls.TextBlock.ForegroundProperty);
        var brush = Assert.IsType<SolidColorBrush>(fg);
        Assert.Equal(Color.FromRgb(255, 0, 0), brush.Color);
    }

    // ── STATIC, applied as UIApplication.Theme, read the effective Foreground (see the note above) ──
    [Fact]
    public void Static_AppliedAsTheme_ResolvesForeground()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(8, 1) });

        host.Application.Theme = new XamlLoader().Load<ResourceDictionary>(
            "<ResourceDictionary" + Ns + ">" +
              "<SolidColorBrush x:Key=\"Accent\" Color=\"#FF0000\"/>" +
              "<ResourceDictionary.Styles>" +
                "<Style TargetType=\"TextBlock\">" +
                  "<Setter Property=\"Foreground\" Value=\"{StaticResource Accent}\"/>" +
                "</Style>" +
              "</ResourceDictionary.Styles>" +
            "</ResourceDictionary>");

        var panel = new UIControls.StackPanel { Name = "Root" };
        var text = new UIControls.TextBlock { Text = "OK" };
        panel.Children.Add(text);
        host.ShowRoot(panel);
        Assert.True(host.RunUntilIdle());

        var fg = text.GetValue(UIControls.TextBlock.ForegroundProperty);
        var brush = Assert.IsType<SolidColorBrush>(fg);
        Assert.Equal(Color.FromRgb(255, 0, 0), brush.Color);
    }
}
