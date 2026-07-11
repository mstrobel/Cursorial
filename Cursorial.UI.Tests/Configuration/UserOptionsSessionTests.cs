// xUnit1031 disabled: UITestHost is single-thread-affine (see FrameLoopTests).
#pragma warning disable xUnit1031

using Cursorial.Output;
using Cursorial.UI;
using Cursorial.UI.Configuration;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Configuration;

/// <summary>
/// FB-17 Stage B — the shared editing session: live preview with absent-means-default semantics,
/// snapshot Reset/Cancel, staged dangerous keys (never live), the timed test scope, and Save.
/// </summary>
public sealed class UserOptionsSessionTests
{
    private const string AppId = "session-test-app";

    private static UIHeadlessHost CreateHost(TempConfigRoot root)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            Capabilities = HeadlessCapabilities.KittyTruecolor,
            ConfigureBuilder = builder => builder.WithUserConfiguration(new UserConfigurationOptions
            {
                ApplicationId = AppId,
                PathProvider = root
            })
        });

        host.ShowRoot(new StackPanel());
        Assert.True(host.RunUntilIdle());
        return host;
    }

    [Fact]
    public void SafeKey_LiveAppliesAndClearingRevertsToDefault()
    {
        using var root = new TempConfigRoot();
        using var host = CreateHost(root);
        var app = host.Application;

        var session = new UserOptionsSession(app);

        session.SetValue(UserOptionScope.Global, UserOptionKeys.ThemeBase, "light");
        Assert.Equal(ThemeBase.Light, app.RequestedThemeBase); // live preview, no Save needed

        // Clearing the key must live-revert to the DEFAULT (auto), not freeze the last value —
        // the startup applier never needs this direction; the session must.
        session.SetValue(UserOptionScope.Global, UserOptionKeys.ThemeBase, null);
        Assert.Null(app.RequestedThemeBase);
    }

    [Fact]
    public void PerAppOverlay_WinsLive_AndClearingReinherits()
    {
        using var root = new TempConfigRoot();
        using var host = CreateHost(root);
        var app = host.Application;

        var session = new UserOptionsSession(app);
        session.SetValue(UserOptionScope.Global, UserOptionKeys.ThemeBase, "dark");
        session.SetValue(UserOptionScope.Application, UserOptionKeys.ThemeBase, "light");
        Assert.Equal(ThemeBase.Light, app.RequestedThemeBase); // overlay wins

        session.SetValue(UserOptionScope.Application, UserOptionKeys.ThemeBase, null); // tri-state: unset → inherit
        Assert.Equal(ThemeBase.Dark, app.RequestedThemeBase);  // the global value re-exposes LIVE
    }

    [Fact]
    public void Reset_RestoresOpenTimeState_IncludingUnknownKeys_AndStaysUsable()
    {
        using var root = new TempConfigRoot();
        using var host = CreateHost(root);
        var app = host.Application;

        // Open-time state: a saved theme + an app-owned key the framework knows nothing about.
        app.UserOptions!.SetGlobal(UserOptionKeys.ThemeBase, "light");
        app.UserOptions.SetGlobal("myapp.customKey", "keep-me");
        var session = new UserOptionsSession(app);
        session.ApplyLive();
        Assert.Equal(ThemeBase.Light, app.RequestedThemeBase);

        session.SetValue(UserOptionScope.Global, UserOptionKeys.ThemeBase, "dark");
        session.SetValue(UserOptionScope.Global, "myapp.customKey", null); // even foreign keys revert
        Assert.Equal(ThemeBase.Dark, app.RequestedThemeBase);

        session.Reset();

        Assert.Equal(ThemeBase.Light, app.RequestedThemeBase); // live state back to open-time
        Assert.Equal("light", session.GetValue(UserOptionScope.Global, UserOptionKeys.ThemeBase));
        Assert.Equal("keep-me", session.GetValue(UserOptionScope.Global, "myapp.customKey"));

        // The Reset button contract: the session keeps working afterwards (no close required).
        session.SetValue(UserOptionScope.Global, UserOptionKeys.ThemeBase, "dark");
        Assert.Equal(ThemeBase.Dark, app.RequestedThemeBase);
    }

    [Fact]
    public void DangerousKeys_StageWithoutTouchingTheApp_UntilSave()
    {
        using var root = new TempConfigRoot();
        using var host = CreateHost(root);
        var app = host.Application;

        var session = new UserOptionsSession(app);
        Assert.False(session.HasStagedDangerousChanges);

        session.SetValue(UserOptionScope.Global, UserOptionKeys.ColorTier, "ansi16");
        session.SetValue(UserOptionScope.Global, UserOptionKeys.SixelOverride, "on");

        Assert.Null(app.RequestedColorTier);                                     // NOT live-applied …
        Assert.Equal(CapabilityOverride.Auto, app.CapabilityOverrides.Sixel);    // … on any axis
        Assert.True(session.HasStagedDangerousChanges);

        session.Save();

        Assert.Equal(ColorDepth.Ansi16, app.RequestedColorTier); // committed at Save
        Assert.Equal(CapabilityOverride.ForceOn, app.CapabilityOverrides.Sixel);
        Assert.False(session.HasStagedDangerousChanges);
        Assert.True(File.Exists(root.GlobalFilePath));           // and persisted
    }

    [Fact]
    public void TestScope_AppliesStagedValues_AndDisposeReverts()
    {
        using var root = new TempConfigRoot();
        using var host = CreateHost(root);
        var app = host.Application;

        var session = new UserOptionsSession(app);
        session.SetValue(UserOptionScope.Global, UserOptionKeys.ColorTier, "nocolor");
        Assert.Null(app.RequestedColorTier);

        using (session.BeginTest())
        {
            Assert.Equal(ColorDepth.NoColor, app.RequestedColorTier); // in force for the probe window
            Assert.True(session.IsTestRunning);
        }

        Assert.Null(app.RequestedColorTier); // reverted — the staged value survives in the store only
        Assert.False(session.IsTestRunning);
        Assert.Equal("nocolor", session.GetValue(UserOptionScope.Global, UserOptionKeys.ColorTier));
    }

    [Fact] // audit F3: a failed persist must leave the committed baseline untouched — Cancel must truly revert
    public void FailedSave_LeavesDangerousValuesUncommitted_SoResetTrulyReverts()
    {
        using var root = new TempConfigRoot();
        using var host = CreateHost(root);
        var app = host.Application;

        var session = new UserOptionsSession(app);
        session.SetValue(UserOptionScope.Global, UserOptionKeys.ColorTier, "ansi16");

        // Make the config root unwritable by occupying the global file path with a DIRECTORY.
        Directory.CreateDirectory(root.GlobalFilePath);

        try
        {
            Assert.ThrowsAny<Exception>(session.Save);

            Assert.Null(app.RequestedColorTier);              // never applied — persist-first ordering
            Assert.True(session.HasStagedDangerousChanges);   // still staged (the Test button still works)

            session.Reset();
            Assert.Null(app.RequestedColorTier);              // and Cancel/Reset genuinely reverts
        }
        finally
        {
            Directory.Delete(root.GlobalFilePath);
        }
    }

    [Fact] // audit F4: a safe edit mid-test must not clobber the staged overrides off the screen
    public void SafeEditDuringTest_DoesNotDisturbTheTestedValues()
    {
        using var root = new TempConfigRoot();
        using var host = CreateHost(root);
        var app = host.Application;

        var session = new UserOptionsSession(app);
        session.SetValue(UserOptionScope.Global, UserOptionKeys.SixelOverride, "on");

        using (session.BeginTest())
        {
            Assert.Equal(CapabilityOverride.ForceOn, app.CapabilityOverrides.Sixel);

            session.SetValue(UserOptionScope.Global, UserOptionKeys.ThemeBase, "light"); // safe edit mid-test

            Assert.Equal(ThemeBase.Light, app.RequestedThemeBase);                   // safe key applied …
            Assert.Equal(CapabilityOverride.ForceOn, app.CapabilityOverrides.Sixel); // … tested overrides intact
        }

        Assert.Equal(CapabilityOverride.Auto, app.CapabilityOverrides.Sixel); // reverted at test end
        Assert.Equal(ThemeBase.Light, app.RequestedThemeBase);               // the safe edit survives
    }

    [Fact]
    public void ImagesMasterToggle_IsSafe_AndFoldsLiveOverCommittedAxes()
    {
        using var root = new TempConfigRoot();
        using var host = CreateHost(root);
        var app = host.Application;

        var session = new UserOptionsSession(app);
        session.SetValue(UserOptionScope.Global, UserOptionKeys.Images, "false");

        // Live: every image axis withdrawn (the safe direction) without touching staged force-ons.
        Assert.Equal(CapabilityOverride.ForceOff, app.CapabilityOverrides.Sixel);
        Assert.Equal(CapabilityOverride.ForceOff, app.CapabilityOverrides.KittyGraphics);
        Assert.Equal(CapabilityOverride.ForceOff, app.CapabilityOverrides.ITerm2InlineImages);

        session.SetValue(UserOptionScope.Global, UserOptionKeys.Images, null);
        Assert.Equal(CapabilityOverride.Auto, app.CapabilityOverrides.Sixel); // clearing live-reverts
    }

    [Fact]
    public void ViewModel_TriStateProjections_TrackScopeAndInheritance()
    {
        using var root = new TempConfigRoot();
        using var host = CreateHost(root);

        using var model = new UserOptionsViewModel(host.Application);
        var theme = model.Categories
                         .SelectMany(static c => c.Options)
                         .OfType<ChoiceOptionViewModel>()
                         .Single(static o => o.Descriptor.Key == UserOptionKeys.ThemeBase);

        // Global scope, nothing set anywhere: inherit slot selected, sourced from the default.
        Assert.Equal(0, theme.SelectedIndex);
        Assert.False(theme.IsSetInCurrentScope);
        Assert.Equal("default", theme.InheritanceLabel);

        // Select "Dark" (inherit + auto + light precede it → index 3) in the GLOBAL layer.
        theme.SelectedIndex = 3;
        Assert.True(theme.IsSetInCurrentScope);
        Assert.Equal(ThemeBase.Dark, host.Application.RequestedThemeBase);

        // Switch the dialog to the APP layer: the same row now reads as inherited-from-global.
        model.EditScope = UserOptionScope.Application;
        Assert.False(theme.IsSetInCurrentScope);
        Assert.Equal("inherited from global", theme.InheritanceLabel);
        Assert.Equal(0, theme.SelectedIndex);
        Assert.Contains("Dark", theme.InheritItemLabel); // the inherit slot names what it yields

        // Override per-app, then clear back to inherited.
        theme.SelectedIndex = 2; // light
        Assert.Equal(ThemeBase.Light, host.Application.RequestedThemeBase);
        theme.ClearToInherited();
        Assert.Equal(ThemeBase.Dark, host.Application.RequestedThemeBase);
        Assert.False(theme.IsSetInCurrentScope);
    }

    [Fact]
    public void ViewModel_TimedTest_CountsDownOnTheFrameClock_AndAutoReverts()
    {
        using var root = new TempConfigRoot();
        using var host = CreateHost(root);
        var app = host.Application;

        using var model = new UserOptionsViewModel(app);
        var tier = model.Categories
                        .SelectMany(static c => c.Options)
                        .OfType<ChoiceOptionViewModel>()
                        .Single(static o => o.Descriptor.Key == UserOptionKeys.ColorTier);

        Assert.False(model.CanBeginTest); // nothing staged yet

        tier.SelectedIndex = 4; // ansi16 (inherit, auto, truecolor, ansi256, ansi16)
        Assert.True(model.CanBeginTest);
        Assert.Null(app.RequestedColorTier); // staged only

        model.BeginTest();
        Assert.True(model.IsTestRunning);
        Assert.Equal(ColorDepth.Ansi16, app.RequestedColorTier);
        Assert.Equal(5, model.TestSecondsRemaining);

        host.AdvanceTime(TimeSpan.FromSeconds(2));
        Assert.True(model.IsTestRunning);
        Assert.InRange(model.TestSecondsRemaining, 2, 4); // counting down on the fake clock

        host.AdvanceTime(TimeSpan.FromSeconds(4));
        Assert.False(model.IsTestRunning);            // auto-reverted at zero
        Assert.Null(app.RequestedColorTier);
        Assert.True(model.CanBeginTest);              // still staged — can test again
    }
}
