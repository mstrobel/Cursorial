using Cursorial.Rendering;
using Cursorial.Rendering.Imaging;
using Cursorial.Terminal;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Configuration;

// Spec for the Icon's fourth tier (FB-15, FB-17 Stage A): Glyph (caps-nerdfont) → Image
// (graphics protocols) → Emoji (caps-emoji, user opt-OUT, default present — maintainer decision
// 2026-07-04) → Text (the floor). The emoji tier measures at its resolved width — double-cell —
// so layouts budget correctly (grid safety lives in that measurement, not in hiding the tier),
// and the ladder honors the FB-5 capability overrides live.
public sealed class IconEmojiTierTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    private static UITestHost Host(TerminalCapabilities? caps = null) => UITestHost.Create(new UITestHostOptions
    {
        InitialSize = new Size(20, 4),
        Capabilities = caps ?? TestCapabilities.KittyTruecolor, // base Kitty: truecolor, NO graphics protocol
    });

    private static Icon Show(UITestHost host, Action<Icon> configure, bool nerdFont = false, bool? emoji = null)
    {
        host.Application.NerdFontAvailable = nerdFont;
        if (emoji is { } value)
            host.Application.EmojiAvailable = value; // set before attach so the first resolve sees it; null = framework default (present)
        var icon = new Icon { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        configure(icon);
        host.ShowRoot(icon);
        host.RunUntilIdle();
        return icon;
    }

    [Fact] // default present: emoji provided + no richer tier available → the emoji tier, no opt-in needed
    public void Emoji_ByDefault_AndNoRicherTier()
    {
        using var host = Host();
        var icon = Show(host, i => { i.Emoji = "📁"; i.Text = "T"; }); // framework default — nothing set

        Assert.Equal(IconTier.Emoji, icon.Tier);
        Assert.Contains("📁", host.GetRowText(0));
        Assert.DoesNotContain("T", host.GetRowText(0));
    }

    [Fact] // opt-OUT: disabling skips the emoji tier — the icon falls past it to the floor
    public void Emoji_SkippedWhenDisabled()
    {
        using var host = Host();
        var icon = Show(host, i => { i.Emoji = "📁"; i.Text = "T"; }, emoji: false);

        Assert.Equal(IconTier.Text, icon.Tier);
        Assert.Contains("T", host.GetRowText(0));
    }

    [Fact] // ladder order: a carriable image outranks the emoji tier
    public void Image_OutranksEmoji()
    {
        using var host = Host(TestCapabilities.KittyGraphics);
        var icon = Show(host, i => { i.Image = new ImageData(OnePixelPng, ImageFormat.Png); i.Emoji = "📁"; i.Text = "T"; });

        Assert.Equal(IconTier.Image, icon.Tier);
    }

    [Fact] // ladder order: a Nerd Font glyph outranks the emoji tier
    public void Glyph_OutranksEmoji()
    {
        using var host = Host();
        var icon = Show(host, i => { i.Glyph = "G"; i.Emoji = "📁"; i.Text = "T"; }, nerdFont: true);

        Assert.Equal(IconTier.Glyph, icon.Tier);
    }

    [Fact] // FB-15 width contract: the icon measures at its RESOLVED tier's width — emoji = 2 cells
    public void EmojiTier_MeasuresTwoCells()
    {
        using var host = Host();
        var icon = Show(host, i => { i.Emoji = "📁"; i.Text = "T"; });

        Assert.Equal(IconTier.Emoji, icon.Tier);
        Assert.Equal(2, icon.Bounds.Columns);
        Assert.Equal(1, icon.Bounds.Rows);
    }

    [Fact] // the width contract covers VS16 emoji-presentation sequences too (§18.4 ruler concern)
    public void EmojiTier_Vs16Sequence_MeasuresTwoCells()
    {
        using var host = Host();
        var icon = Show(host, i => { i.Emoji = "❤️"; i.Text = "T"; }); // ❤️ = U+2764 + VS16

        Assert.Equal(IconTier.Emoji, icon.Tier);
        Assert.Equal(2, icon.Bounds.Columns);
    }

    [Fact] // contrast: with emoji disabled the single-width Unicode floor measures 1 cell (the tier width really differs)
    public void TextFloor_MeasuresOneCell()
    {
        using var host = Host();
        var icon = Show(host, i => { i.Emoji = "📁"; i.Text = "T"; }, emoji: false);

        Assert.Equal(IconTier.Text, icon.Tier);
        Assert.Equal(1, icon.Bounds.Columns);
    }

    [Fact] // flipping availability live re-resolves the tier (mirrors the Nerd Font flip contract): opt-out, then re-enable
    public void ReResolves_OnEmojiAvailabilityFlip()
    {
        using var host = Host();
        var icon = Show(host, i => { i.Emoji = "📁"; i.Text = "T"; }); // default present
        Assert.Equal(IconTier.Emoji, icon.Tier);

        host.Application.EmojiAvailable = false; // live opt-out → falls past the emoji tier
        host.RunUntilIdle();

        Assert.Equal(IconTier.Text, icon.Tier);
        Assert.Contains("T", host.GetRowText(0));

        host.Application.EmojiAvailable = true; // live re-enable
        host.RunUntilIdle();

        Assert.Equal(IconTier.Emoji, icon.Tier);
        Assert.Contains("📁", host.GetRowText(0));
    }

    [Fact] // the ladder honors the FB-5 seam: forcing images off drops an image-tier icon to emoji, live
    public void ForcedOffImages_FallToEmojiTier_Live()
    {
        using var host = Host(TestCapabilities.KittyGraphics);
        var icon = Show(host, i => { i.Image = new ImageData(OnePixelPng, ImageFormat.Png); i.Emoji = "📁"; i.Text = "T"; });
        Assert.Equal(IconTier.Image, icon.Tier);

        host.Application.CapabilityOverrides = new CapabilityOverrides().WithImagesDisabled();
        host.RunUntilIdle();

        Assert.Equal(IconTier.Emoji, icon.Tier);

        host.Application.CapabilityOverrides = CapabilityOverrides.None;
        host.RunUntilIdle();

        Assert.Equal(IconTier.Image, icon.Tier);
    }

    [Fact] // conversely: forcing a graphics protocol ON promotes the icon to the image tier
    public void ForcedOnGraphics_PromoteToImageTier_Live()
    {
        using var host = Host(); // no graphics negotiated
        var icon = Show(host, i => { i.Image = new ImageData(OnePixelPng, ImageFormat.Png); i.Emoji = "📁"; i.Text = "T"; });
        Assert.Equal(IconTier.Emoji, icon.Tier);

        host.Application.CapabilityOverrides = new CapabilityOverrides { KittyGraphics = CapabilityOverride.ForceOn };
        host.RunUntilIdle();

        Assert.Equal(IconTier.Image, icon.Tier); // styling-scoped force-on: the tier gate opens…
        // (…whether the ImagePresenter can actually draw stays governed by the negotiated wire.)
    }
}
