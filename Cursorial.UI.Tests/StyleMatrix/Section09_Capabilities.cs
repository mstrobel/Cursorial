// xUnit1031 (no blocking task ops) does not apply here; the single async test awaits the
// renegotiation path the same way the P2 suites do (the synthetic host completes synchronously).

using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.UI;
using Cursorial.UI.Testing;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.StyleMatrix;

/// <summary>Style matrix §9 — capability classes (S140–S146; SD14, inversion 6: negotiated snapshot at P3).</summary>
public class Section09_Capabilities
{
    [Fact]
    public void S140_KittyHost_StampsTheFullCapsSet_OnTheRootOnly()
    {
        using var tree = ShowTree(); // KittyTruecolor default

        Assert.Contains("caps-truecolor", tree.Root.Classes);
        Assert.Contains("caps-motion", tree.Root.Classes);
        Assert.Contains("caps-kitty-keyboard", tree.Root.Classes);
        Assert.Contains("caps-unicode", tree.Root.Classes);
        Assert.DoesNotContain("caps-ascii", tree.Root.Classes); // reserved — never stamped at P3 (SD14)

        Assert.Single(tree.Root.Classes, static name => name.StartsWith("caps-", StringComparison.Ordinal)
                                                        && name is "caps-truecolor" or "caps-ansi256" or "caps-ansi16" or "caps-nocolor");

        Assert.Empty(tree.PaneA.Classes); // no caps-* on any child element
        Assert.Empty(tree.A.Classes);
    }

    [Fact]
    public void S141_Ansi16Host_ExactlyTierAndUnicode()
    {
        using var tree = ShowTree(new UITestHostOptions { Capabilities = TestCapabilities.Ansi16Legacy });

        Assert.Equal(["caps-ansi16", "caps-unicode"], tree.Root.Classes.ToArray()); // exact set
    }

    [Theory]
    [InlineData(ColorDepth.NoColor, "caps-nocolor")]
    [InlineData(ColorDepth.Ansi16, "caps-ansi16")]
    [InlineData(ColorDepth.Ansi256, "caps-ansi256")]
    [InlineData(ColorDepth.Truecolor, "caps-truecolor")]
    public void S142_ExactlyOneColorTierClass_PerDepth(ColorDepth depth, string expected)
    {
        var capabilities = TestCapabilities.KittyTruecolor with
        {
            Output = TestCapabilities.KittyTruecolor.Output with
            {
                Color = TestCapabilities.KittyTruecolor.Output.Color with { Depth = depth },
            },
        };

        using var tree = ShowTree(new UITestHostOptions { Capabilities = capabilities });

        var tiers = tree.Root.Classes
            .Where(static name => name is "caps-truecolor" or "caps-ansi256" or "caps-ansi16" or "caps-nocolor")
            .ToArray();

        Assert.Equal([expected], tiers);
    }

    [Fact]
    public void S143_CapsClasses_AreOrdinaryMatchableClasses()
    {
        using (var kitty = ShowTree(show: false))
        {
            kitty.App.Styles.Add(R(".caps-truecolor Widget", (Widget.P, 5)));
            kitty.Host.ShowRoot(kitty.Root);

            Assert.Equal(5, kitty.A.GetValue(Widget.P)); // active under the Kitty tier
        }

        using (var legacy = ShowTree(new UITestHostOptions { Capabilities = TestCapabilities.Ansi16Legacy }, show: false))
        {
            legacy.App.Styles.Add(R(".caps-truecolor Widget", (Widget.P, 5)));
            legacy.Host.ShowRoot(legacy.Root);

            Assert.Empty(StyleDiagnostics.MatchedRules(legacy.A)); // not armed — no matching ancestor class
        }
    }

    [Fact]
    public void S144_PreShowStartupCall_StampsNothing()
    {
        using var tree = ShowTree(show: false); // the host started — OnCapabilitiesChanged has run

        Assert.Empty(tree.Root.Classes); // records only (B2)
    }

    [Fact]
    public async Task S145_Renegotiation_ReplacesOnlyTheCapsSubset_SameTick()
    {
        using var tree = ShowTree(show: false);
        tree.Root.Classes.Add("brand");
        tree.App.Styles.Add(R(".caps-truecolor Widget", (Widget.P, 5)));
        tree.Host.ShowRoot(tree.Root);
        Assert.Equal(5, tree.A.GetValue(Widget.P));

        tree.Host.Terminal.ScriptRenegotiatedCapabilities(TestCapabilities.Ansi16Legacy);
        await tree.App.RenegotiateAsync();

        // Same tick: classes swapped + tier-gated rules re-matched, before any frame ran.
        Assert.Contains("brand", tree.Root.Classes); // app classes preserved
        Assert.Contains("caps-ansi16", tree.Root.Classes);
        Assert.DoesNotContain("caps-truecolor", tree.Root.Classes);
        Assert.DoesNotContain("caps-motion", tree.Root.Classes);
        Assert.DoesNotContain("caps-kitty-keyboard", tree.Root.Classes);
        Assert.Empty(StyleDiagnostics.MatchedRules(tree.A));
        Assert.Equal(0, tree.A.GetValue(Widget.P));

        tree.Host.RunFrame(); // the same renegotiation transaction's frame renders the change
    }

    [Fact]
    public void S146_NewRootShown_StampedFromTheCurrentSnapshot()
    {
        using var tree = ShowTree();
        Assert.Contains("caps-truecolor", tree.Root.Classes);

        var newRoot = new Cursorial.UI.Controls.StackPanel();
        tree.Host.ShowRoot(newRoot); // detaches the old root, attaches the new

        Assert.Contains("caps-truecolor", newRoot.Classes);
        Assert.Contains("caps-unicode", newRoot.Classes);
    }
}
