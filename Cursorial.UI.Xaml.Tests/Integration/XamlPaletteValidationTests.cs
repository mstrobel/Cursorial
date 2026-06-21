// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Testing;
using Cursorial.UI.Themes;
using Cursorial.UI.Themes.Xaml;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Xaml.Integration;

/// <summary>
/// ARCH-1: the data-shipped XAML palette spine (<c>Themes/Palette.xaml</c>, loaded via
/// <see cref="CursorialXamlTheme.LoadPalette"/>) reproduces <see cref="CursorialTheme"/> <c>BuiltIn</c>'s
/// (ThemeBase × ColorDepth) <c>ThemeDictionaries</c> byte-for-byte. Every role-token brush + chrome pen is
/// resolved through <c>TryGetResource(key, variant)</c> at all 8 (base × tier) combinations — exercising the
/// CD8 descent (Truecolor → Ansi256, NoColor → the wildcard-base dict) — and compared to BuiltIn. A render
/// pass then proves the merged <see cref="CursorialXamlTheme.LoadTheme"/> drives a control identically.
/// </summary>
public sealed class XamlPaletteValidationTests
{
    // All 26 palette tokens: the 22-token cell-faithful role spine + the 4 opt-in chrome/infrastructure keys.
    private static readonly string[] PaletteTokens =
    {
        ThemeKeys.WindowBackground, ThemeKeys.SurfaceBrush, ThemeKeys.PanelBrush, ThemeKeys.WellBrush,
        ThemeKeys.SelectionBrush, ThemeKeys.HoverBrush, ThemeKeys.TextBrush, ThemeKeys.TextDimBrush,
        ThemeKeys.MutedBrush, ThemeKeys.FaintBrush, ThemeKeys.DisabledBackgroundBrush,
        ThemeKeys.DisabledForegroundBrush, ThemeKeys.AccentBrush, ThemeKeys.Accent2Brush,
        ThemeKeys.OnAccentBrush, ThemeKeys.GreenBrush, ThemeKeys.AmberBrush, ThemeKeys.RedBrush,
        ThemeKeys.PurpleBrush, ThemeKeys.StatusBarBackground, ThemeKeys.StatusBarAltBackground,
        ThemeKeys.StatusBarAltForeground, ThemeKeys.BorderPen, ThemeKeys.FocusPen,
        ThemeKeys.ObscuredOverlayBrush, ThemeKeys.AccessKeyUnderlineBrush,
    };

    [Theory]
    [InlineData(ThemeBase.Dark, ColorDepth.Truecolor)]
    [InlineData(ThemeBase.Dark, ColorDepth.Ansi256)]
    [InlineData(ThemeBase.Dark, ColorDepth.Ansi16)]
    [InlineData(ThemeBase.Dark, ColorDepth.NoColor)]
    [InlineData(ThemeBase.Light, ColorDepth.Truecolor)]
    [InlineData(ThemeBase.Light, ColorDepth.Ansi256)]
    [InlineData(ThemeBase.Light, ColorDepth.Ansi16)]
    [InlineData(ThemeBase.Light, ColorDepth.NoColor)]
    public void XamlPalette_ResolvesIdenticallyToBuiltIn(ThemeBase @base, ColorDepth tier)
    {
        var xaml = CursorialXamlTheme.LoadPalette();
        var builtIn = CursorialTheme.BuiltIn;
        var variant = new ThemeVariant(@base, tier);

        foreach (var key in PaletteTokens)
        {
            var gotXaml = xaml.TryGetResource(key, variant, out var xamlVal);
            var gotBuiltIn = builtIn.TryGetResource(key, variant, out var builtInVal);

            Assert.True(gotXaml, $"XAML palette missing '{key}' at {@base}+{tier}");
            Assert.True(gotBuiltIn, $"BuiltIn missing '{key}' at {@base}+{tier}");
            Assert.Equal(Normalize(builtInVal), Normalize(xamlVal));
        }
    }

    [Theory] // The merged data-shipped theme (palette + controls) drives a control byte-identically to BuiltIn.
    [InlineData(ThemeBase.Dark, ColorDepth.Truecolor)]
    [InlineData(ThemeBase.Dark, ColorDepth.Ansi256)]
    [InlineData(ThemeBase.Dark, ColorDepth.Ansi16)]
    [InlineData(ThemeBase.Dark, ColorDepth.NoColor)]
    [InlineData(ThemeBase.Light, ColorDepth.Truecolor)]
    [InlineData(ThemeBase.Light, ColorDepth.Ansi256)]
    [InlineData(ThemeBase.Light, ColorDepth.Ansi16)]
    [InlineData(ThemeBase.Light, ColorDepth.NoColor)]
    public void XamlTheme_RendersControlIdenticallyToBuiltIn(ThemeBase @base, ColorDepth tier)
    {
        Assert.Equal(
            RenderButton(xaml: false, @base, tier),
            RenderButton(xaml: true, @base, tier));
    }

    // ARCH-1b: the XAML overlay references the per-control OVERRIDE keys (§11.4a, the gallery KEYS), not the role
    // tokens directly — so an app re-keys one control under the loaded overlay theme. The render-identity theory
    // above can't catch a same-token mis-mapping (a wrong per-control key that aliases the same role token renders
    // identically); only overriding the specific key and asserting the control moves proves the overlay wires the
    // RIGHT key. Each case overrides one per-control key and asserts the target control's fill/ink re-skins.

    [Fact] // overriding ButtonBackgroundNormal under the overlay theme re-skins the Button's resting fill.
    public void XamlOverlay_ButtonBackgroundNormalOverride_ReSkinsButton()
        => AssertOverlayKeyDrivesControl(
            ThemeKeys.ButtonBackgroundNormal,
            new UIControls.Button { Content = "OK" },
            target: "OK", fill: true);

    [Fact] // overriding InputBackgroundNormal under the overlay theme re-skins a TextBox's resting fill.
    public void XamlOverlay_InputBackgroundNormalOverride_ReSkinsTextBox()
        => AssertOverlayKeyDrivesControl(
            ThemeKeys.InputBackgroundNormal,
            new UIControls.TextBox { Text = "hi" },
            target: "h", fill: true);

    [Fact] // overriding ToggleForegroundNormal under the overlay theme re-skins a CheckBox's label ink.
    public void XamlOverlay_ToggleForegroundNormalOverride_ReSkinsCheckBox()
        => AssertOverlayKeyDrivesControl(
            ThemeKeys.ToggleForegroundNormal,
            new UIControls.CheckBox { Content = "X" },
            target: "X", fill: false);

    // Loads the data-shipped overlay theme, renders the control, overrides one per-control key, and asserts the
    // target cell's fill (or ink) moved to the override — proving the overlay's setter referenced THAT key.
    private static void AssertOverlayKeyDrivesControl(string perControlKey, UIControls.Control control, string target, bool fill)
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(20, 4) });
        host.Application.Theme = CursorialXamlTheme.LoadTheme();
        host.Application.RequestedThemeBase = ThemeBase.Dark;
        host.ShowRoot(control);
        Assert.True(host.RunUntilIdle());

        var custom = Color.FromRgb(0xCC, 0x33, 0x99);
        Color Sample()
        {
            for (var r = 0; r < host.FrameBuffer.Rows; r++)
            for (var c = 0; c < host.FrameBuffer.Columns; c++)
            {
                var cell = host.GetCell(c, r);
                if (cell.Grapheme is { Length: > 0 } g && g[0] == target[0])
                    return fill ? cell.Style.Background : cell.Style.Foreground;
            }
            return Color.Default;
        }

        Assert.NotEqual(custom, Sample()); // not already the override
        host.Application.Resources[perControlKey] = new SolidColorBrush(custom);
        host.RunFrame();
        Assert.Equal(custom, Sample()); // the overlay's setter referenced perControlKey → the control re-skinned
    }

    private static (string Glyph, Color Fg, Color Bg)[] RenderButton(bool xaml, ThemeBase @base, ColorDepth tier)
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(14, 3) });
        if (xaml)
            host.Application.Theme = CursorialXamlTheme.LoadTheme();
        host.Application.RequestedThemeBase = @base;
        host.Application.RequestedColorTier = tier;
        // Prove the override actually forced the variant — otherwise both sides could render the host default
        // and the comparison would be vacuous.
        Assert.Equal(new ThemeVariant(@base, tier), host.Application.ActualThemeVariant);

        host.ShowRoot(new UIControls.Button { Content = "OK" });
        Assert.True(host.RunUntilIdle());

        var cells = new List<(string, Color, Color)>(14 * 3);
        for (var r = 0; r < 3; r++)
        for (var c = 0; c < 14; c++)
        {
            var cell = host.GetCell(c, r);
            cells.Add((cell.Grapheme ?? string.Empty, cell.Style.Foreground, cell.Style.Background));
        }
        return cells.ToArray();
    }

    // Normalizes a resolved palette value to a value-equal comparable: SolidColorBrush is a reference-equal
    // class (no value Equals), and Pen is a value-equal record struct whose Brush field is such a class — so a
    // direct Assert.Equal on the raw values would compare brush references. Compare the colors + Pen fields.
    private static object? Normalize(object? value) => value switch
    {
        SolidColorBrush b => ("brush", b.Color, b.Opacity),
        Pen p => ("pen", (p.Brush as SolidColorBrush)?.Color, p.Brush is null, p.Weight, p.GlyphSet,
                  p.Corners, p.Dash, p.EndCap, p.Junction, p.Attributes),
        _ => value,
    };
}
