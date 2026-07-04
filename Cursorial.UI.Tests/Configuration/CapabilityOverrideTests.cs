using Cursorial.Tests.UI.StyleMatrix;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Configuration;

// Spec for the FB-5 seam (FB-17 Stage A): UIApplication.CapabilityOverrides is a per-axis
// force-on / force-off / auto tri-state folded into capability-class stamping — assigning it
// restamps caps-* on every surface root and re-resolves dependent styles in the same tick (no
// restart), EffectiveCapabilities exposes the SAME fold (classes and snapshot can never desync,
// while Capabilities keeps reporting the negotiated truth), and overrides survive RenegotiateAsync
// by design. caps-emoji (FB-15) rides the same restamp as a user opt-OUT — default present
// (maintainer decision, 2026-07-04).
public sealed class CapabilityOverrideTests
{
    // ───────────────────────────── the per-axis override matrix ─────────────────────────────

    [Theory]
    // MouseMotion → caps-motion (detected on KittyTruecolor, absent on Ansi16Legacy)
    [InlineData("MouseMotion", CapabilityOverride.Auto,     true,  true)]
    [InlineData("MouseMotion", CapabilityOverride.Auto,     false, false)]
    [InlineData("MouseMotion", CapabilityOverride.ForceOn,  true,  true)]
    [InlineData("MouseMotion", CapabilityOverride.ForceOn,  false, true)]
    [InlineData("MouseMotion", CapabilityOverride.ForceOff, true,  false)]
    [InlineData("MouseMotion", CapabilityOverride.ForceOff, false, false)]
    // KittyKeyboardProtocol → caps-kitty-keyboard
    [InlineData("KittyKeyboardProtocol", CapabilityOverride.Auto,     true,  true)]
    [InlineData("KittyKeyboardProtocol", CapabilityOverride.Auto,     false, false)]
    [InlineData("KittyKeyboardProtocol", CapabilityOverride.ForceOn,  true,  true)]
    [InlineData("KittyKeyboardProtocol", CapabilityOverride.ForceOn,  false, true)]
    [InlineData("KittyKeyboardProtocol", CapabilityOverride.ForceOff, true,  false)]
    [InlineData("KittyKeyboardProtocol", CapabilityOverride.ForceOff, false, false)]
    public void OverrideMatrix_InputAxes_StampTheExpectedClass(string axis, CapabilityOverride value, bool detected, bool expectClass)
    {
        var (capabilities, expectedClass) = axis switch
        {
            "MouseMotion" => (detected ? TestCapabilities.KittyTruecolor : TestCapabilities.NoMotion, "caps-motion"),
            _             => (detected
                                  ? TestCapabilities.KittyTruecolor
                                  : TestCapabilities.KittyTruecolor with
                                    {
                                        Input = TestCapabilities.KittyTruecolor.Input with
                                        {
                                            Protocol = TestCapabilities.KittyTruecolor.Input.Protocol with { KittyKeyboardProtocol = false },
                                        },
                                    },
                              "caps-kitty-keyboard"),
        };

        using var tree = ShowTree(new UITestHostOptions { Capabilities = capabilities });

        tree.App.CapabilityOverrides = axis switch
        {
            "MouseMotion" => new CapabilityOverrides { MouseMotion = value },
            _             => new CapabilityOverrides { KittyKeyboardProtocol = value },
        };

        Assert.Equal(expectClass, tree.Root.Classes.Contains(expectedClass));
    }

    [Theory]
    // Force each graphics protocol ON from a no-graphics base: caps-images always appears; the
    // refinement classes follow the protocol's honest shape (clipping = sixel|kitty, occlusion = kitty).
    [InlineData("Sixel",  true,  true,  false)]
    [InlineData("Kitty",  true,  true,  true)]
    [InlineData("ITerm2", true,  false, false)]
    public void OverrideMatrix_ForcingAGraphicsProtocolOn_StampsTheImageClassFamily(string axis, bool images, bool clipping, bool occlusion)
    {
        using var tree = ShowTree(); // KittyTruecolor — NO graphics protocol negotiated

        Assert.DoesNotContain("caps-images", tree.Root.Classes);

        tree.App.CapabilityOverrides = axis switch
        {
            "Sixel"  => new CapabilityOverrides { Sixel = CapabilityOverride.ForceOn },
            "Kitty"  => new CapabilityOverrides { KittyGraphics = CapabilityOverride.ForceOn },
            _        => new CapabilityOverrides { ITerm2InlineImages = CapabilityOverride.ForceOn },
        };

        Assert.Equal(images, tree.Root.Classes.Contains("caps-images"));
        Assert.Equal(clipping, tree.Root.Classes.Contains("caps-image-clipping"));
        Assert.Equal(occlusion, tree.Root.Classes.Contains("caps-image-occlusion"));
    }

    [Fact]
    public void OverrideMatrix_ForcingANegotiatedProtocolOff_WithdrawsTheImageClasses()
    {
        using var tree = ShowTree(new UITestHostOptions { Capabilities = TestCapabilities.KittyGraphics });

        Assert.Contains("caps-images", tree.Root.Classes);
        Assert.Contains("caps-image-occlusion", tree.Root.Classes);

        tree.App.CapabilityOverrides = new CapabilityOverrides { KittyGraphics = CapabilityOverride.ForceOff };

        Assert.DoesNotContain("caps-images", tree.Root.Classes);
        Assert.DoesNotContain("caps-image-clipping", tree.Root.Classes);
        Assert.DoesNotContain("caps-image-occlusion", tree.Root.Classes);
    }

    [Fact]
    public void ResettingToNone_RestoresTheNegotiatedStamping()
    {
        using var tree = ShowTree(new UITestHostOptions { Capabilities = TestCapabilities.NoMotion });

        tree.App.CapabilityOverrides = new CapabilityOverrides { MouseMotion = CapabilityOverride.ForceOn };
        Assert.Contains("caps-motion", tree.Root.Classes);

        tree.App.CapabilityOverrides = CapabilityOverrides.None; // auto everywhere → negotiated truth
        Assert.DoesNotContain("caps-motion", tree.Root.Classes);
    }

    // ───────────────────────────── snapshot coherence ─────────────────────────────

    [Fact]
    public void EffectiveCapabilities_AgreesWithTheStampedClasses_WhileCapabilitiesKeepsNegotiatedTruth()
    {
        using var tree = ShowTree(new UITestHostOptions { Capabilities = TestCapabilities.KittyGraphics });

        tree.App.CapabilityOverrides = new CapabilityOverrides
        {
            KittyGraphics = CapabilityOverride.ForceOff,
            MouseMotion = CapabilityOverride.ForceOff,
        };

        // The effective view and the classes tell the same story…
        var effective = tree.App.EffectiveCapabilities;
        Assert.False(effective.Output.Graphics.KittyGraphics);
        Assert.False(effective.Input.Mouse.Motion);
        Assert.DoesNotContain("caps-images", tree.Root.Classes);
        Assert.DoesNotContain("caps-motion", tree.Root.Classes);

        // …while the undecorated negotiated snapshot is untouched (wire machinery reads this).
        Assert.True(tree.App.Capabilities.Output.Graphics.KittyGraphics);
        Assert.True(tree.App.Capabilities.Input.Mouse.Motion);
    }

    [Fact]
    public void ForcingKittyKeyboardOff_AlsoClearsTheRealizedFlagSet()
    {
        using var tree = ShowTree(); // KittyTruecolor

        tree.App.CapabilityOverrides = new CapabilityOverrides { KittyKeyboardProtocol = CapabilityOverride.ForceOff };

        var protocol = tree.App.EffectiveCapabilities.Input.Protocol;
        Assert.False(protocol.KittyKeyboardProtocol);
        Assert.Equal(Cursorial.Input.Parsing.KittyKeyboardFlags.None, protocol.KittyKeyboardFlags);
    }

    // ───────────────────────────── restamp + style re-resolution, no restart ─────────────────────────────

    [Fact]
    public void OverrideFlip_RestampsAndReResolvesDependentStyles_SameTick_NoRestart()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R(".caps-images Widget", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(0, tree.A.GetValue(Widget.P)); // no graphics negotiated → rule unmatched

        tree.App.CapabilityOverrides = new CapabilityOverrides { KittyGraphics = CapabilityOverride.ForceOn };
        Assert.Equal(5, tree.A.GetValue(Widget.P)); // same tick — no frame, no restart

        tree.App.CapabilityOverrides = CapabilityOverrides.None;
        Assert.Equal(0, tree.A.GetValue(Widget.P));
    }

    [Fact]
    public void OverrideFlip_PreservesAppClasses_AndRaisesChanged()
    {
        using var tree = ShowTree(show: false);
        tree.Root.Classes.Add("brand");
        tree.Host.ShowRoot(tree.Root);

        var raised = 0;
        tree.App.CapabilityOverridesChanged += (_, _) => raised++;

        tree.App.CapabilityOverrides = new CapabilityOverrides { Sixel = CapabilityOverride.ForceOn };
        Assert.Equal(1, raised);
        Assert.Contains("brand", tree.Root.Classes);

        tree.App.CapabilityOverrides = new CapabilityOverrides { Sixel = CapabilityOverride.ForceOn }; // value-equal → no-op
        Assert.Equal(1, raised);
    }

    // ───────────────────────────── renegotiation survival ─────────────────────────────

    [Fact]
    public async Task Overrides_SurviveRenegotiation_FoldedOverTheFreshSnapshot()
    {
        using var tree = ShowTree(new UITestHostOptions { Capabilities = TestCapabilities.Ansi16Legacy });

        tree.App.CapabilityOverrides = new CapabilityOverrides
        {
            KittyGraphics = CapabilityOverride.ForceOn,
            MouseMotion = CapabilityOverride.ForceOff,
        };
        Assert.Contains("caps-images", tree.Root.Classes);

        tree.Host.Terminal.ScriptRenegotiatedCapabilities(TestCapabilities.KittyTruecolor);
        await tree.App.RenegotiateAsync();

        // The fresh fan-out restamped from the NEW snapshot — with the overrides still folded in:
        Assert.Contains("caps-truecolor", tree.Root.Classes);       // fresh negotiated tier
        Assert.Contains("caps-images", tree.Root.Classes);          // forced-on survives (Kitty negotiated no graphics)
        Assert.DoesNotContain("caps-motion", tree.Root.Classes);    // forced-off beats the fresh snapshot's motion=true
        Assert.Contains("caps-kitty-keyboard", tree.Root.Classes);  // auto axis follows the fresh snapshot

        Assert.False(tree.App.EffectiveCapabilities.Input.Mouse.Motion); // snapshot stays coherent post-renegotiation
        Assert.True(tree.App.Capabilities.Input.Mouse.Motion);
    }

    // ───────────────────────────── caps-emoji (FB-15) ─────────────────────────────

    [Fact]
    public void CapsEmoji_DefaultPresent_UnstampedWhenDisabled_LiveAndPersistent()
    {
        using var tree = ShowTree();

        Assert.Contains("caps-emoji", tree.Root.Classes); // default present — opt-OUT (FB-15, 2026-07-04)

        var raised = 0;
        tree.App.EmojiAvailableChanged += (_, _) => raised++;

        tree.App.EmojiAvailable = false;
        Assert.DoesNotContain("caps-emoji", tree.Root.Classes); // restamped live, same tick
        Assert.Equal(1, raised);

        tree.App.EmojiAvailable = true; // re-enable live
        Assert.Contains("caps-emoji", tree.Root.Classes);
        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task CapsEmoji_DisableSurvivesRenegotiation()
    {
        using var tree = ShowTree();
        tree.App.EmojiAvailable = false; // the non-default state is now the opt-out

        tree.Host.Terminal.ScriptRenegotiatedCapabilities(TestCapabilities.Ansi16Legacy);
        await tree.App.RenegotiateAsync();

        Assert.DoesNotContain("caps-emoji", tree.Root.Classes); // app state, not a negotiated capability
    }

    [Fact]
    public void CapsEmoji_IsAnOrdinaryMatchableClass()
    {
        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R(".caps-emoji Widget", (Widget.P, 7)));
        tree.Host.ShowRoot(tree.Root);
        Assert.Equal(7, tree.A.GetValue(Widget.P)); // matches out of the box — default present

        tree.App.EmojiAvailable = false;
        Assert.Equal(0, tree.A.GetValue(Widget.P));
    }

    // ───────────────────────────── downstream consumers honor the fold ─────────────────────────────

    [Fact]
    public void ImagePresenter_ForcedOffImages_CollapsesToPlaceholder_Live()
    {
        using var host = UITestHost.Create(new UITestHostOptions { Capabilities = TestCapabilities.ITerm2Graphics });
        var presenter = new ImagePresenter
        {
            Source = new Cursorial.Rendering.Imaging.ImageData(new byte[] { 1, 2, 3 }, Cursorial.Rendering.Imaging.ImageFormat.Jpeg),
            PlaceholderContent = "P",
        };
        host.ShowRoot(presenter);
        host.RunUntilIdle();

        Assert.True(presenter.IsImageVisible); // iTerm2 carries any format

        host.Application.CapabilityOverrides = new CapabilityOverrides().WithImagesDisabled();

        Assert.False(presenter.IsImageVisible); // degrades exactly like a no-graphics terminal
    }
}
