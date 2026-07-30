using Cursorial.Output;
using Cursorial.UI;
using Cursorial.UI.Configuration;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Themes;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

namespace Cursorial.Tests.UI.Configuration;

// Spec for the builder wiring (FB-17 Stage A): UIApplicationBuilder.WithUserConfiguration loads the
// ~/.cursorial store (path-provider seam) at startup and applies the resolved options BEFORE the
// first capability-class stamp; the app-id override selects the per-app overlay; not opting in
// loads nothing; a corrupt store degrades to defaults with diagnostics, never a crash.
public sealed class UserConfigurationBuilderTests
{
    private const string AppId = "builder-test-app";

    private static UIHeadlessHostOptions Configured(TempConfigRoot root, string appId = AppId, Cursorial.Terminal.TerminalCapabilities? capabilities = null)
        => new()
        {
            Capabilities = capabilities ?? HeadlessCapabilities.KittyTruecolor,
            ConfigureBuilder = builder => builder.WithUserConfiguration(new UserConfigurationOptions
            {
                ApplicationId = appId,
                PathProvider = root,
            }),
        };

    [Fact]
    public void ResolvedOptions_AreInForceForTheFirstStamp()
    {
        using var root = new TempConfigRoot();
        root.WriteGlobalFile(
            """
            {
                "theme.base": "light",
                "theme.colorTier": "ansi16",
                "capabilities.nerdFont": "true",
                "capabilities.emoji": "false",
                "capabilities.kittyGraphics": "on",
                "appearance.animations": "false",
                "input.scrollDeadZone": "true"
            }
            """);

        using var tree = ShowTree(Configured(root)); // the FIRST stamp happens at ShowRoot

        // Theme preference + color tier land on the S7 surfaces…
        Assert.Equal(ThemeBase.Light, tree.App.RequestedThemeBase);
        Assert.Equal(ThemeBase.Light, tree.App.ActualThemeVariant.Base);
        Assert.Equal(ColorDepth.Ansi16, tree.App.RequestedColorTier);
        Assert.Equal(ColorDepth.Ansi16, tree.App.ActualThemeVariant.Tier);

        // …and the stamped classes reflect every configured axis, with no unconfigured flash:
        Assert.Contains("caps-ansi16", tree.Root.Classes);
        Assert.DoesNotContain("caps-truecolor", tree.Root.Classes);
        Assert.Contains("caps-nerdfont", tree.Root.Classes);
        Assert.DoesNotContain("caps-emoji", tree.Root.Classes); // disabled via the opt-OUT key (FB-15) — no default-present flash
        Assert.Contains("caps-images", tree.Root.Classes); // forced on through the FB-5 seam

        Assert.True(tree.App.NerdFontAvailable);
        Assert.False(tree.App.EmojiAvailable);
        Assert.False(tree.App.AnimationScheduler.AnimationsEnabled);
        Assert.True(tree.App.ScrollDeadZoneEnabled);
        Assert.NotNull(tree.App.UserOptions);
        Assert.Empty(tree.App.UserOptions!.LoadDiagnostics);
    }

    [Fact]
    public void PerAppOverlay_TriState_OverridesOnlyItsExplicitKeys()
    {
        using var root = new TempConfigRoot();

        // Seed through the store itself (dogfood): global dark/ansi16; the app overlays ONLY the tier.
        var seed = UserOptionsStore.Load(AppId, root);
        seed.SetGlobal(UserOptionKeys.ThemeBase, "dark");
        seed.SetGlobal(UserOptionKeys.ColorTier, "ansi16");
        seed.SetApplication(UserOptionKeys.ColorTier, "ansi256");
        seed.Save();

        using var tree = ShowTree(Configured(root));

        Assert.Equal(ColorDepth.Ansi256, tree.App.RequestedColorTier); // explicitly overlaid
        Assert.Equal(ThemeBase.Dark, tree.App.RequestedThemeBase);     // inherited from global
        Assert.Contains("caps-ansi256", tree.Root.Classes);
    }

    [Fact]
    public void AppIdOverride_SelectsThatAppsOverlay()
    {
        using var root = new TempConfigRoot();

        var seed = UserOptionsStore.Load("other-app", root);
        seed.SetApplication(UserOptionKeys.ColorTier, "nocolor");
        seed.Save();

        using (var other = ShowTree(Configured(root, appId: "other-app")))
        {
            Assert.Equal(ColorDepth.NoColor, other.App.RequestedColorTier);
            Assert.Contains("caps-nocolor", other.Root.Classes);
        }

        using (var mine = ShowTree(Configured(root))) // different id → different overlay → untouched
        {
            Assert.Null(mine.App.RequestedColorTier);
            Assert.Contains("caps-truecolor", mine.Root.Classes);
        }
    }

    [Fact]
    public void WithoutOptIn_NothingLoads()
    {
        using var tree = ShowTree(); // no WithUserConfiguration

        Assert.Null(tree.App.UserOptions);
        Assert.Null(tree.App.RequestedThemeBase);
        Assert.Null(tree.App.RequestedColorTier);
        Assert.True(tree.App.CapabilityOverrides.IsNone);
        Assert.True(tree.App.EmojiAvailable); // FB-15 opt-out: present without any configuration
        Assert.Contains("caps-emoji", tree.Root.Classes);
    }

    [Fact]
    public void CorruptStore_StartupSucceedsOnDefaults_WithDiagnostics()
    {
        using var root = new TempConfigRoot();
        root.WriteGlobalFile("&&& not json at all");

        using var tree = ShowTree(Configured(root)); // must not throw

        Assert.NotNull(tree.App.UserOptions);
        Assert.NotEmpty(tree.App.UserOptions!.LoadDiagnostics);
        Assert.Contains("caps-truecolor", tree.Root.Classes); // negotiated defaults in force
        Assert.Null(tree.App.RequestedThemeBase);
    }

    [Fact]
    public void UnrecognizedValues_AreIgnoredWithDiagnostics_NotErrors()
    {
        using var root = new TempConfigRoot();
        root.WriteGlobalFile(
            """
            {
                "theme.base": "purple",
                "theme.colorTier": "million",
                "capabilities.mouseMotion": "sometimes"
            }
            """);

        using var tree = ShowTree(Configured(root));

        Assert.Null(tree.App.RequestedThemeBase);
        Assert.Null(tree.App.RequestedColorTier);
        Assert.True(tree.App.CapabilityOverrides.IsNone);
        Assert.Equal(3, tree.App.UserOptions!.LoadDiagnostics.Count);
    }

    [Fact]
    public void ImagesMasterToggle_ForcesEveryImageProtocolOff_EvenOverPerProtocolOn()
    {
        using var root = new TempConfigRoot();
        root.WriteGlobalFile(
            """
            {
                "capabilities.images": "false",
                "capabilities.sixel": "on"
            }
            """);

        using var tree = ShowTree(Configured(root, capabilities: HeadlessCapabilities.KittyGraphics));

        Assert.DoesNotContain("caps-images", tree.Root.Classes);
        Assert.False(tree.App.EffectiveCapabilities.Output.Graphics.KittyGraphics);
        Assert.False(tree.App.EffectiveCapabilities.Output.Graphics.Sixel);
        Assert.True(tree.App.Capabilities.Output.Graphics.KittyGraphics); // negotiated truth untouched
    }

    [Fact]
    public void StoreWrites_PersistForTheNextRun()
    {
        using var root = new TempConfigRoot();

        using (var first = ShowTree(Configured(root)))
        {
            first.App.UserOptions!.SetApplication(UserOptionKeys.NerdFont, true);
            first.App.UserOptions.Save(); // what Stage B's Options dialog will do
        }

        using var second = ShowTree(Configured(root));
        Assert.True(second.App.NerdFontAvailable);
        Assert.Contains("caps-nerdfont", second.Root.Classes);
    }
}
