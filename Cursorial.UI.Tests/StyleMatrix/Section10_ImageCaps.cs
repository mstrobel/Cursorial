using Cursorial.UI;
using Cursorial.UI.Testing;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.StyleMatrix;

/// <summary>
/// Control-matrix P9 §C22 — the image/glyph capability classes (CD-P2J-1): <c>caps-images</c> +
/// <c>caps-image-occlusion</c> from the negotiated <c>OutputCapabilities.Graphics</c>, and the no-probe
/// <c>caps-nerdfont</c> opt-in (<see cref="UIApplication.NerdFontAvailable"/>).
/// </summary>
public class Section10_ImageCaps
{
    [Fact] // C22.1: any graphics protocol stamps caps-images
    public void C22_1_AnyGraphicsStampsImages()
    {
        foreach (var caps in new[] { TestCapabilities.KittyGraphics, TestCapabilities.SixelGraphics, TestCapabilities.ITerm2Graphics })
        {
            using var tree = ShowTree(new UITestHostOptions { Capabilities = caps });
            Assert.Contains("caps-images", tree.Root.Classes);
        }
    }

    [Fact] // C22.2: no graphics protocol ⇒ no caps-images
    public void C22_2_NoGraphicsNoImages()
    {
        using var tree = ShowTree(new UITestHostOptions { Capabilities = TestCapabilities.Ansi16Legacy });
        Assert.DoesNotContain("caps-images", tree.Root.Classes);
    }

    [Fact] // C22.3: Kitty graphics ⇒ caps-image-occlusion
    public void C22_3_KittyGraphicsOcclusion()
    {
        using var tree = ShowTree(new UITestHostOptions { Capabilities = TestCapabilities.KittyGraphics });
        Assert.Contains("caps-images", tree.Root.Classes);
        Assert.Contains("caps-image-occlusion", tree.Root.Classes);
    }

    [Fact] // C22.4: Sixel only ⇒ caps-images but NOT caps-image-occlusion
    public void C22_4_SixelNoOcclusion()
    {
        using var tree = ShowTree(new UITestHostOptions { Capabilities = TestCapabilities.SixelGraphics });
        Assert.Contains("caps-images", tree.Root.Classes);
        Assert.DoesNotContain("caps-image-occlusion", tree.Root.Classes);
    }

    [Fact] // C22.5: iTerm2 inline images ⇒ caps-images but NOT caps-image-occlusion (excluded for now)
    public void C22_5_ITerm2NoOcclusion()
    {
        using var tree = ShowTree(new UITestHostOptions { Capabilities = TestCapabilities.ITerm2Graphics });
        Assert.Contains("caps-images", tree.Root.Classes);
        Assert.DoesNotContain("caps-image-occlusion", tree.Root.Classes);
    }

    [Fact] // C22.6: caps-nerdfont is never auto-stamped (no probe)
    public void C22_6_NerdFontNotAutoStamped()
    {
        using var tree = ShowTree(new UITestHostOptions { Capabilities = TestCapabilities.KittyGraphics });
        Assert.DoesNotContain("caps-nerdfont", tree.Root.Classes);
    }

    [Fact] // C22.7: NerdFontAvailable opts caps-nerdfont in (live), then back out
    public void C22_7_NerdFontOptInLive()
    {
        using var tree = ShowTree();
        Assert.DoesNotContain("caps-nerdfont", tree.Root.Classes);

        tree.App.NerdFontAvailable = true;
        Assert.Contains("caps-nerdfont", tree.Root.Classes);

        tree.App.NerdFontAvailable = false;
        Assert.DoesNotContain("caps-nerdfont", tree.Root.Classes);
    }

    [Fact] // C22.8: renegotiation drops the negotiated graphics classes but keeps the app-state Nerd-Font opt-in
    public async Task C22_8_RenegotiationKeepsNerdFontDropsGraphics()
    {
        using var tree = ShowTree(new UITestHostOptions { Capabilities = TestCapabilities.KittyGraphics }, show: false);
        tree.Host.ShowRoot(tree.Root);
        tree.App.NerdFontAvailable = true;
        Assert.Contains("caps-images", tree.Root.Classes);
        Assert.Contains("caps-image-occlusion", tree.Root.Classes);
        Assert.Contains("caps-nerdfont", tree.Root.Classes);

        tree.Host.Terminal.ScriptRenegotiatedCapabilities(TestCapabilities.Ansi16Legacy);
        await tree.App.RenegotiateAsync();

        Assert.DoesNotContain("caps-images", tree.Root.Classes);          // new snapshot has no graphics
        Assert.DoesNotContain("caps-image-occlusion", tree.Root.Classes);
        Assert.Contains("caps-nerdfont", tree.Root.Classes);              // app state survives renegotiation

        tree.Host.RunFrame();
    }

    [Fact] // C22.9: caps-image-occlusion is an ordinary matchable selector class
    public void C22_9_OcclusionClassIsMatchable()
    {
        using var tree = ShowTree(new UITestHostOptions { Capabilities = TestCapabilities.KittyGraphics }, show: false);
        tree.App.Styles.Add(R(".caps-image-occlusion Widget", (Widget.P, 7)));
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(7, tree.A.GetValue(Widget.P)); // armed under the Kitty-graphics root class
    }
}
