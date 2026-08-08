using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Imaging;
using Cursorial.Rendering.Media;
using Cursorial.Terminal;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Themes;

namespace Cursorial.Tests.UI.Controls;

// Spec for the tiered Icon (the command-bars prerequisite): it renders the highest-preference tier that is both
// PROVIDED and SUPPORTED — Nerd Font glyph (NerdFontAvailable) → graphics image (caps-images) → emoji/Unicode floor —
// and re-resolves live when those capabilities change. (Glyph/Text use "G"/"T" markers so the rendered tier is
// unambiguous in the row text; the semantics are nerd-font-vs-unicode.)
public sealed class IconTests
{
    // A minimal valid 1×1 transparent PNG, so the image tier's ImagePresenter measures without a decode failure.
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    private static UIHeadlessHost Host(TerminalCapabilities? caps = null) => UIHeadlessHost.Create(new UIHeadlessHostOptions
    {
        InitialSize = new Size(20, 4),
        Capabilities = caps ?? HeadlessCapabilities.KittyTruecolor, // base Kitty: truecolor, NO graphics protocol
    });

    private static Icon Show(UIHeadlessHost host, Action<Icon> configure, bool nerdFont = false)
    {
        host.Application.NerdFontAvailable = nerdFont; // set before attach so the first resolve sees it
        var icon = new Icon { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        configure(icon);
        host.ShowRoot(icon);
        host.RunUntilIdle();
        return icon;
    }

    [Fact] // Nerd Font available + a glyph provided → the glyph tier (glyph shows, the unicode fallback does not)
    public void Glyph_WhenNerdFontAvailable()
    {
        using var host = Host();
        var icon = Show(host, i => { i.Glyph = "G"; i.Text = "T"; }, nerdFont: true);

        Assert.Equal(IconTier.Glyph, icon.Tier);
        Assert.Contains("G", host.GetRowText(0));
        Assert.DoesNotContain("T", host.GetRowText(0));
    }

    [Fact] // no Nerd Font → the glyph tier is unsupported, so the Unicode floor shows instead
    public void Text_WhenNoNerdFont()
    {
        using var host = Host();
        var icon = Show(host, i => { i.Glyph = "G"; i.Text = "T"; }, nerdFont: false);

        Assert.Equal(IconTier.Text, icon.Tier);
        Assert.Contains("T", host.GetRowText(0));
        Assert.DoesNotContain("G", host.GetRowText(0));
    }

    [Fact] // an image + a graphics protocol → the image tier is selected (above the Unicode floor)
    public void Image_WhenGraphicsSupported()
    {
        using var host = Host(HeadlessCapabilities.KittyGraphics);
        var icon = Show(host, i => { i.Image = new ImageData(OnePixelPng, ImageFormat.Png); i.Text = "T"; });

        Assert.Equal(IconTier.Image, icon.Tier);
    }

    [Fact] // an image but NO graphics protocol → the image tier is unsupported, so it falls to the Unicode floor
    public void Image_FallsToText_WhenNoGraphics()
    {
        using var host = Host(); // base Kitty has no graphics protocol
        var icon = Show(host, i => { i.Image = new ImageData(OnePixelPng, ImageFormat.Png); i.Text = "T"; });

        Assert.Equal(IconTier.Text, icon.Tier);
        Assert.Contains("T", host.GetRowText(0));
    }

    [Fact] // preference order: Nerd Font outranks the image tier when a glyph is provided
    public void Glyph_OutranksImage()
    {
        using var host = Host(HeadlessCapabilities.KittyGraphics);
        var icon = Show(host, i => { i.Glyph = "G"; i.Image = new ImageData(OnePixelPng, ImageFormat.Png); i.Text = "T"; }, nerdFont: true);

        Assert.Equal(IconTier.Glyph, icon.Tier);
        Assert.Contains("G", host.GetRowText(0));
    }

    [Fact] // no glyph provided + Nerd Font on + graphics → skip the (empty) glyph tier, use the image
    public void NoGlyph_UsesImage_EvenWithNerdFont()
    {
        using var host = Host(HeadlessCapabilities.KittyGraphics);
        var icon = Show(host, i => { i.Image = new ImageData(OnePixelPng, ImageFormat.Png); i.Text = "T"; }, nerdFont: true);

        Assert.Equal(IconTier.Image, icon.Tier);
    }

    [Fact] // flipping NerdFontAvailable live re-resolves the tier (the glyph appears, the fallback is replaced)
    public void ReResolves_OnNerdFontFlip()
    {
        using var host = Host();
        var icon = Show(host, i => { i.Glyph = "G"; i.Text = "T"; }, nerdFont: false);
        Assert.Equal(IconTier.Text, icon.Tier);

        host.Application.NerdFontAvailable = true;
        host.RunUntilIdle();

        Assert.Equal(IconTier.Glyph, icon.Tier);
        Assert.Contains("G", host.GetRowText(0));
        Assert.DoesNotContain("T", host.GetRowText(0));
    }

    [Fact] // an Icon works as a control's content (the bar-button scenario)
    public void Icon_AsButtonContent()
    {
        using var host = Host();
        host.Application.NerdFontAvailable = false;
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Content = new Icon { Glyph = "G", Text = "T" },
        };
        host.ShowRoot(button);
        host.RunUntilIdle();

        Assert.Contains("T", host.GetRowText(0)); // the icon rendered its Unicode floor inside the button
    }

    /// <summary>
    /// An icon takes its host's ink by INHERITANCE (doc §12: "<c>Control.Foreground</c> (inherited) tints the
    /// glyph/emoji/text tiers"), so it follows the host through a reverse-video state. This is the constraint
    /// that rules a <c>Foreground</c> pin out of <c>Theme.Icon</c>: a pin sits at
    /// <see cref="BindingPriority.Style"/>, which beats <see cref="BindingPriority.Inherited"/>, and the glyph
    /// would hold resting TextBrush ink on the focus state's TextBrush fill — an invisible glyph.
    /// </summary>
    [Fact]
    public void Icon_InFocusedButton_TakesTheButtonsReverseVideoInk()
    {
        using var host = Host();
        host.Application.NerdFontAvailable = false;
        var icon = new Icon { Text = "T" };
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Content = icon,
        };
        host.ShowRoot(button);
        host.RunUntilIdle();

        button.Focus();
        host.RunUntilIdle();

        Assert.True(button.TryFindResource(ThemeKeys.ButtonForegroundFocus, out var focusInk));
        Assert.Equal(BindingPriority.Inherited, icon.GetValueSource(Control.ForegroundProperty).Priority);
        Assert.Equal(((SolidColorBrush)focusInk!).Color, GlyphInk(host, "T"));
    }

    private static Color GlyphInk(UIHeadlessHost host, string grapheme)
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
