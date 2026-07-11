// xUnit1031 disabled: UITestHost is single-thread-affine (see FrameLoopTests).
#pragma warning disable xUnit1031

using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Configuration;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Configuration;

/// <summary>
/// FB-17 Stage B — the two shells over the shared model: the settings dialog (chord open,
/// single-instance, Cancel reverts, Reset stays open, OK persists) and the first-run wizard
/// (opt-in, global marker gates it, skip counts as completion).
/// </summary>
public sealed class OptionsUiTests
{
    private const string AppId = "options-ui-app";

    private static UIHeadlessHost CreateHost(
        TempConfigRoot root,
        bool showWizard = false,
        bool forceWizard = false)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(90, 30),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
            ConfigureBuilder = builder => builder.WithUserConfiguration(new UserConfigurationOptions
            {
                ApplicationId = AppId,
                PathProvider = root,
                ShowFirstRunWizard = showWizard,
                ForceFirstRunWizard = forceWizard
            })
        });

        host.ShowRoot(new StackPanel());
        Assert.True(host.RunUntilIdle());
        return host;
    }

    private static UserOptionsDialog? OpenDialog(UIHeadlessHost host)
    {
        host.SendKey(Key.Character, KeyModifiers.Control | KeyModifiers.Alt, "O");
        Assert.True(host.RunUntilIdle());
        return host.Application.WindowManager!.Windows.OfType<UserOptionsDialog>().SingleOrDefault();
    }

    // ───────────────────────────── settings dialog ─────────────────────────────

    [Fact]
    public void Chord_OpensDialog_SecondChordIsNoOp_EscCloses()
    {
        using var root = new TempConfigRoot();
        using var host = CreateHost(root);

        var dialog = OpenDialog(host);
        Assert.NotNull(dialog);
        Assert.Equal("Options", dialog!.Title);

        // Single-instance: the chord again must not open a second dialog.
        host.SendKey(Key.Character, KeyModifiers.Control | KeyModifiers.Alt, "O");
        Assert.True(host.RunUntilIdle());
        Assert.Single(host.Application.WindowManager!.Windows.OfType<UserOptionsDialog>());

        host.SendKey(Key.Escape);
        Assert.True(host.RunUntilIdle());
        Assert.Empty(host.Application.WindowManager!.Windows.OfType<UserOptionsDialog>());

        // And it can open again after closing.
        Assert.NotNull(OpenDialog(host));
    }

    [Fact]
    public void DialogEdits_PreviewLive_CancelReverts_ResetStaysOpen()
    {
        using var root = new TempConfigRoot();
        using var host = CreateHost(root);
        var app = host.Application;

        var dialog = OpenDialog(host)!;
        var model = dialog.ModelInternal;
        var theme = model.Categories.SelectMany(static c => c.Options)
                         .OfType<ChoiceOptionViewModel>()
                         .Single(static o => o.Descriptor.Key == UserOptionKeys.ThemeBase);

        theme.SelectedIndex = 3; // dark — live preview
        host.RunFrame();
        Assert.Equal(ThemeBase.Dark, app.RequestedThemeBase);
        Assert.Contains("caps-", string.Join(' ', app.RootElement!.Classes)); // stamped classes exist (sanity)

        // Reset: back to the open-time state, dialog STAYS open (the user's requested button).
        model.Reset();
        host.RunFrame();
        Assert.Null(app.RequestedThemeBase);
        Assert.Single(app.WindowManager!.Windows.OfType<UserOptionsDialog>());

        // Edit again, then Cancel (Esc): the revert happens on close.
        theme.SelectedIndex = 3;
        Assert.Equal(ThemeBase.Dark, app.RequestedThemeBase);
        host.SendKey(Key.Escape);
        Assert.True(host.RunUntilIdle());
        Assert.Null(app.RequestedThemeBase);
        Assert.False(File.Exists(root.GlobalFilePath)); // nothing persisted
    }

    [Fact]
    public void DialogSave_PersistsBothLayers_AndAppliesStagedDangerousValues()
    {
        using var root = new TempConfigRoot();
        using var host = CreateHost(root);
        var app = host.Application;

        var dialog = OpenDialog(host)!;
        var model = dialog.ModelInternal;

        // Global theme + a per-app color tier (dangerous → staged until save).
        var theme = model.Categories.SelectMany(static c => c.Options)
                         .OfType<ChoiceOptionViewModel>()
                         .Single(static o => o.Descriptor.Key == UserOptionKeys.ThemeBase);
        theme.SelectedIndex = 3; // dark, global scope

        model.EditScope = UserOptionScope.Application;
        var tier = model.Categories.SelectMany(static c => c.Options)
                        .OfType<ChoiceOptionViewModel>()
                        .Single(static o => o.Descriptor.Key == UserOptionKeys.ColorTier);
        tier.SelectedIndex = 4; // ansi16, app scope
        Assert.Null(app.RequestedColorTier); // staged

        model.Save();
        host.RunFrame();

        Assert.Equal(ColorDepth.Ansi16, app.RequestedColorTier); // applied at save
        Assert.True(File.Exists(root.GlobalFilePath));

        // Round-trip proof: a fresh store sees the layered writes.
        var reloaded = UserOptionsStore.Load(AppId, root);
        Assert.Equal("dark", reloaded.GlobalValues[UserOptionKeys.ThemeBase]);
        Assert.Equal("ansi16", reloaded.ApplicationValues[UserOptionKeys.ColorTier]);
        Assert.False(reloaded.GlobalValues.ContainsKey(UserOptionKeys.ColorTier)); // never a global copy
    }

    // ───────────────────────────── first-run wizard ─────────────────────────────

    [Fact]
    public void FirstRun_WizardShows_SkipWritesMarker_SecondRunSkipsIt()
    {
        using var root = new TempConfigRoot();

        using (var host = CreateHost(root, showWizard: true))
        {
            var wizard = host.Application.WindowManager!.Windows.OfType<FirstRunWizard>().SingleOrDefault();
            Assert.NotNull(wizard); // fresh root, no marker → it shows

            host.SendKey(Key.Escape); // skip
            Assert.True(host.RunUntilIdle());
            Assert.Empty(host.Application.WindowManager!.Windows.OfType<FirstRunWizard>());
        }

        // Skipping IS completing: the marker persisted globally…
        var store = UserOptionsStore.Load(AppId, root);
        Assert.True(store.GetBoolean(UserOptionKeys.FirstRunCompleted));

        // …so the next app run (same system) shows no wizard.
        using (var host = CreateHost(root, showWizard: true))
            Assert.Empty(host.Application.WindowManager!.Windows.OfType<FirstRunWizard>());
    }

    [Fact]
    public void Wizard_FinishPersistsGlobalEdits()
    {
        using var root = new TempConfigRoot();
        using var host = CreateHost(root, showWizard: true);

        var wizard = host.Application.WindowManager!.Windows.OfType<FirstRunWizard>().Single();
        var model = (FirstRunWizardViewModel)wizard.DataContext!;

        // Edit an essential (the wizard is pinned to the GLOBAL layer)…
        var nerd = model.Options.Categories.SelectMany(static c => c.Options)
                        .OfType<BooleanOptionViewModel>()
                        .Single(static o => o.Descriptor.Key == UserOptionKeys.NerdFont);
        nerd.IsChecked = true;
        Assert.True(host.Application.NerdFontAvailable); // live preview inside the wizard too

        // …walk to the end and finish (the window-level path — the Finish button's exact code).
        while (!model.IsLastPage)
            model.Next();

        wizard.Finish();
        Assert.True(host.RunUntilIdle());

        var reloaded = UserOptionsStore.Load(AppId, root);
        Assert.Equal("true", reloaded.GlobalValues[UserOptionKeys.NerdFont]);
        Assert.True(reloaded.GetBoolean(UserOptionKeys.FirstRunCompleted)); // marker rides the close
        Assert.Empty(reloaded.ApplicationValues); // the wizard never writes the overlay
    }

    [Fact] // audit F1/F2: the chord must be dead while the modal wizard is up — two sessions corrupt the store
    public void OptionsChord_IsSuppressed_WhileWizardIsOpen()
    {
        using var root = new TempConfigRoot();
        using var host = CreateHost(root, showWizard: true);

        Assert.Single(host.Application.WindowManager!.Windows.OfType<FirstRunWizard>());

        host.SendKey(Key.Character, KeyModifiers.Control | KeyModifiers.Shift, "O");
        Assert.True(host.RunUntilIdle());

        Assert.Empty(host.Application.WindowManager!.Windows.OfType<UserOptionsDialog>()); // no dialog over the wizard

        host.SendKey(Key.Escape); // skip the wizard …
        Assert.True(host.RunUntilIdle());

        Assert.NotNull(OpenDialog(host)); // … and the chord works again
    }

    [Fact]
    public void Wizard_IsOptIn_AndForceReshowsDespiteMarker()
    {
        using var root = new TempConfigRoot();

        // Default (no opt-in): no wizard even on a virgin root.
        using (var host = CreateHost(root))
            Assert.Empty(host.Application.WindowManager!.Windows.OfType<FirstRunWizard>());

        // Seed the marker, then force: it shows anyway (the app-owned re-onboarding lever).
        var store = UserOptionsStore.Load(AppId, root);
        store.SetGlobal(UserOptionKeys.FirstRunCompleted, true);
        store.Save();

        using (var host = CreateHost(root, forceWizard: true))
            Assert.Single(host.Application.WindowManager!.Windows.OfType<FirstRunWizard>());
    }
}
