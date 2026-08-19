// xUnit1031 (no blocking task ops) does not apply here; the single async test awaits the
// renegotiation path the same way the P2 suites do (the synthetic host completes synchronously).

using Cursorial.Media;
using Cursorial.Terminal;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.StyleMatrix;

/// <summary>Style matrix §9 — capability classes (S140–S147; SD14, inversion 6: negotiated snapshot at P3).</summary>
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
    public void S141_Ansi16Host_ExactlyTierEmojiUnicodeAndLocal()
    {
        using var tree = ShowTree(new UIHeadlessHostOptions { Capabilities = HeadlessCapabilities.Ansi16Legacy }, show: false);
        tree.App.EnvironmentReader = new FakeEnvironmentReader(isSSH: false); // deterministic locality — see below
        tree.Host.ShowRoot(tree.Root);

        // Exact set: caps-emoji is default-present since the FB-15 opt-out flip (2026-07-04). caps-local is
        // present because the locality axis derives from the injected IEnvironmentReader, not the ambient
        // process environment — the seam reports IsSSH() == false and Local is stamped whenever it is false.
        // Injecting the reader is what makes this ENVIRONMENT-INDEPENDENT: the assertion holds even when the
        // suite runs over SSH (the old SSH-sensitivity gap). Local is the sole member of its axis — an SSH run
        // simply drops caps-local (there is no caps-remote class).
        // app-fullscreen closes the set: the §3.3 presentation pair rides the same stamp builder, and a
        // headless host presents fullscreen (ApplicationModel.FullScreen) unless built inline.
        Assert.Equal(["caps-ansi16", "caps-emoji", "caps-unicode", "caps-local", "app-fullscreen"], tree.Root.Classes.ToArray());
    }

    [Theory]
    [InlineData(ColorDepth.NoColor, "caps-nocolor")]
    [InlineData(ColorDepth.Ansi16, "caps-ansi16")]
    [InlineData(ColorDepth.Ansi256, "caps-ansi256")]
    [InlineData(ColorDepth.Truecolor, "caps-truecolor")]
    public void S142_ExactlyOneColorTierClass_PerDepth(ColorDepth depth, string expected)
    {
        var capabilities = HeadlessCapabilities.KittyTruecolor with
        {
            Output = HeadlessCapabilities.KittyTruecolor.Output with
            {
                Color = HeadlessCapabilities.KittyTruecolor.Output.Color with { Depth = depth }
            }
        };

        using var tree = ShowTree(new UIHeadlessHostOptions { Capabilities = capabilities });

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

        using (var legacy = ShowTree(new UIHeadlessHostOptions { Capabilities = HeadlessCapabilities.Ansi16Legacy }, show: false))
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

        tree.Host.Terminal.ScriptRenegotiatedCapabilities(HeadlessCapabilities.Ansi16Legacy);
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

    [Theory]
    [InlineData(true, false)]  // injected SSH reader → caps-local OMITTED (remote)
    [InlineData(false, true)]  // injected non-SSH reader → caps-local present (local)
    public void S147_LocalityAxis_TracksTheInjectedEnvironmentReader(bool isSSH, bool expectLocal)
    {
        // Both branches of the locality derivation, driven purely through UIApplication's injectable
        // IEnvironmentReader seam — the proof that StyleCapabilities.Local is sourced from the injected
        // reader's IsSSH() and nothing else. This is what discharges the environment-sensitivity of S141.
        using var tree = ShowTree(show: false);
        tree.App.EnvironmentReader = new FakeEnvironmentReader(isSSH);
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(expectLocal, tree.Root.Classes.Contains("caps-local"));
        Assert.DoesNotContain("caps-remote", tree.Root.Classes); // the axis has a single class; there is no remote
    }

    /// <summary>
    /// A deterministic <see cref="IEnvironmentReader"/> for the locality tests: only <see cref="IsSSH"/> is
    /// meaningful (it drives <see cref="StyleCapabilities.Local"/>). Everything else returns the empty/false
    /// default, so the stamped locality depends on nothing but the injected SSH flag.
    /// </summary>
    private sealed class FakeEnvironmentReader(bool isSSH) : IEnvironmentReader
    {
        public string? GetVariable(string name) => null;
        public bool IsSSH() => isSSH;
    }
}
