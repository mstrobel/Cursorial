using System.Reflection;

using Cursorial.Output;
using Cursorial.Terminal;
using Cursorial.UI.Configuration;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The user-configuration surface (FB-17 Stage A) folded onto the merged <see cref="UIApplication"/>:
/// the loaded <see cref="UserOptions"/> store, the <see cref="CapabilityOverrides"/> seam (FB-5),
/// the <see cref="EmojiAvailable"/> opt-out (FB-15, the <c>caps-emoji</c> class), and the startup
/// application of the resolved options — before the first capability-class stamp.
/// </summary>
public sealed partial class UIApplication
{
    private CapabilityOverrides _capabilityOverrides = CapabilityOverrides.None;
    private UserOptionsStore? _userOptions;

    /// <summary>
    /// Whether the terminal font renders color emoji at the expected double-cell width (the
    /// <c>caps-emoji</c> root class — FB-15). Like <see cref="NerdFontAvailable"/> there is no
    /// probe (no terminal advertises emoji glyph coverage or width honesty), but unlike Nerd Font
    /// this is a user <b>opt-out</b> — default <see langword="true"/>, disabled through
    /// <see cref="Configuration.UserOptionKeys.Emoji"/>. The asymmetry is deliberate (maintainer
    /// decision, 2026-07-04): emoji coverage in modern terminals is near-universal — unlike Nerd
    /// Font PUA coverage, where the default-absent no-tofu posture rightly stays — and grid safety
    /// is owned by <see cref="Controls.Icon"/>'s 2-cell emoji measurement, not by hiding the tier.
    /// Setting it re-stamps the capability classes on every surface root live, and — being
    /// application state, not a negotiated capability — it survives renegotiation.
    /// </summary>
    public bool EmojiAvailable
    {
        get => field && ActualThemeVariant is { Tier: > ColorDepth.NoColor };
        set
        {
            Dispatcher.VerifyAccess();
            if (field == value)
                return;
            field = value;
            StyleEngineInternal.RestampCapabilityClasses();
            EmojiAvailableChanged?.Invoke(this, EventArgs.Empty);
        }
    } = true; // FB-15: opt-OUT — default present (maintainer decision, 2026-07-04)

    /// <summary>Raised on the UI thread when <see cref="EmojiAvailable"/> flips (so capability-tiered visuals
    /// such as <see cref="Controls.Icon"/> can re-resolve their rendered tier live).</summary>
    public event EventHandler? EmojiAvailableChanged;

    /// <summary>
    /// Whether translucency effects are honored (<see cref="Configuration.UserOptionKeys.Translucency"/>) —
    /// the switch behind <b>opacity groups</b>: a translucent render boundary composites its whole
    /// boundary subtree through a private surface so the subtree fades once, as a unit. Turning it off
    /// releases those surfaces and returns every boundary to flat compositing, where a translucent
    /// ancestor is re-blended wherever a descendant boundary overlaps it. Default
    /// <see langword="true"/>; opacity itself is unaffected either way, and below
    /// <see cref="Cursorial.Output.ColorDepth.Ansi256"/> groups never materialise regardless (those
    /// tiers collapse to palette indices, where the blend is discarded).
    /// </summary>
    /// <remarks>
    /// This is the framework-side seam only: nothing applies
    /// <see cref="Configuration.UserOptionKeys.Translucency"/> to it at startup yet, and no options-UI
    /// row surfaces it. Flipping it is a <em>structural</em> composition change (the emitted layer
    /// count moves), so the setter re-composites every surface from scratch rather than letting the
    /// flip land on whichever frame happens to be dirty next.
    /// </remarks>
    public bool TranslucencyEnabled
    {
        get;
        set
        {
            Dispatcher.VerifyAccess();
            if (field == value)
                return;
            field = value;
            _windowManager?.InvalidateComposition();
        }
    } = true;

    /// <summary>
    /// Whether the scroll dead zone is active (<see cref="Configuration.UserOptionKeys.ScrollDeadZone"/>):
    /// wheel gestures rail onto their dominant axis, suppressing the stray cross-axis ticks a
    /// trackpad sheds during fast one-axis scrolling (the gesture model is
    /// <see cref="Cursorial.Input.WheelAxisLock"/>'s). Default <see langword="false"/> — filtering
    /// raw input is an opt-in. Takes effect live: the toggle reaches into the already-running
    /// input chain (the filter is always assembled, §10.4) rather than rebuilding the single-shot
    /// device.
    /// </summary>
    public bool ScrollDeadZoneEnabled
    {
        get => field;
        set
        {
            Dispatcher.VerifyAccess();
            if (field == value)
                return;
            field = value;
            if (_wheelAxisLock is { } axisLock)
                axisLock.Enabled = value;
        }
    }

    /// <summary>
    /// The per-axis capability overrides (FB-5): assigning a new set re-stamps the <c>caps-*</c>
    /// classes on every surface root and re-folds <see cref="EffectiveCapabilities"/> in the same
    /// tick — no restart. Overrides are application state (the same posture as
    /// <see cref="NerdFontAvailable"/>): they survive <see cref="RenegotiateAsync"/> because every
    /// restamp folds them over the then-current negotiated snapshot. See
    /// <see cref="Cursorial.UI.CapabilityOverrides"/> for the honest force-on/force-off semantics.
    /// </summary>
    public CapabilityOverrides CapabilityOverrides
    {
        get => _capabilityOverrides;
        set
        {
            Dispatcher.VerifyAccess();
            ArgumentNullException.ThrowIfNull(value);

            if (_capabilityOverrides != value)
            {
                var oldEffective = _capabilityOverrides.Apply(_capabilities);

                _capabilityOverrides = value;

                CapabilityOverridesChanged?.Invoke(this, EventArgs.Empty);

                if (_host is {} host && _renderer is {} renderer)
                    ChangeCapabilities(host, renderer, oldEffective, _capabilities, Dispatcher.ShutdownToken);
            }
            
            if (ShowUserOptionsCommand is ShowUserOptionsCommandImpl command)
                command.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Raised on the UI thread when <see cref="CapabilityOverrides"/> is replaced (so capability-gated
    /// visuals such as <see cref="Controls.Icon"/>/<see cref="Controls.ImagePresenter"/> re-evaluate live).</summary>
    public event EventHandler? CapabilityOverridesChanged;

    /// <summary>
    /// The negotiated snapshot with <see cref="CapabilityOverrides"/> folded per axis — the same
    /// view capability-class stamping reads, so the stamped <c>caps-*</c> set and this record never
    /// desync. Capability-gated <b>consumers</b> (styles, tiered visuals) read this;
    /// wire-protocol machinery keeps reading the negotiated truth (<see cref="Capabilities"/>) —
    /// forcing an axis on is styling-scoped and cannot make the session speak an unnegotiated
    /// protocol. Distinct from <see cref="EffectiveInputCapabilities"/>, which reflects input
    /// pipeline decorations, not user overrides.
    /// </summary>
    public TerminalCapabilities EffectiveCapabilities => _capabilityOverrides.Apply(_capabilities);

    /// <summary>
    /// The user-options store loaded at startup, or <see langword="null"/> when the app did not
    /// opt in via <see cref="UIApplicationBuilder.WithUserConfiguration"/>. Stage B's Options UI
    /// (and any app-owned settings surface) reads and writes through it;
    /// <see cref="UserOptionsStore.Save"/> persists.
    /// </summary>
    public UserOptionsStore? UserOptions => _userOptions;

    /// <summary>
    /// Loads and applies the user configuration (both startup paths call this after
    /// <c>ComposeSystems</c>, before the capability fan-out — so the resolved options are in force
    /// before the first <c>caps-*</c> stamp). Tolerant by contract: a missing/corrupt store or a
    /// failing path provider degrades to defaults with diagnostics; only a programming error
    /// (not user data) may throw.
    /// </summary>
    private void ApplyUserConfiguration()
    {
        if (_options.UserConfiguration is not { } configuration)
            return;

        var applicationId = configuration.ApplicationId ?? Assembly.GetEntryAssembly()?.GetName().Name;
        if (string.IsNullOrWhiteSpace(applicationId))
            applicationId = "default"; // native hosts without an entry assembly still get a stable overlay

        _userOptions = UserOptionsStore.Load(applicationId, configuration.PathProvider);
        UserConfigurationApplier.Apply(_userOptions, this);

        // First-run wizard (Stage B): the GLOBAL marker gates it — first-run onboarding is a
        // per-system experience, not per-app (an app re-shows via ForceFirstRunWizard). Posted:
        // the wizard is a modal window and must open from a frame's dispatcher phase, after the
        // root has a chance to show — never synchronously mid-startup.
        if (configuration.ForceFirstRunWizard ||
            (configuration.ShowFirstRunWizard &&
             _userOptions.GetBoolean(UserOptionKeys.FirstRunCompleted) is not true))
        {
            Dispatcher.Post(() => _ = RunFirstRunWizardAsync());
        }
        
        if (ShowUserOptionsCommand is ShowUserOptionsCommandImpl command)
            command.RaiseCanExecuteChanged();
    }

    private async Task RunFirstRunWizardAsync()
    {
        try
        {
            await FirstRunWizard.ShowAsync(this); // resumes on the UI thread (frame-coherent sync context)
        }
        catch (Exception ex)
        {
            RaiseUnhandled(ex);
            return; // the wizard never (fully) showed — do NOT mark the first run completed
        }

        // Completing OR skipping counts — the wizard must never nag. A failed marker save is a
        // diagnostic, not a crash (it will re-offer next run, which is the honest behavior when
        // the config directory is unwritable anyway).
        try
        {
            _userOptions!.SetGlobal(UserOptionKeys.FirstRunCompleted, true);
            _userOptions.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _userOptions!.AddDiagnostic($"Could not persist the first-run marker: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the settings dialog (<see cref="Configuration.UserOptionsDialog"/>) modally —
    /// the programmatic twin of the configured chord (default Ctrl+Shift+O). At most one
    /// instance: while it is open, callers get the SAME task. UI thread only. Requires the app
    /// to have opted into user configuration
    /// (<see cref="UIApplicationBuilder.WithUserConfiguration"/>).
    /// </summary>
    public Task ShowUserOptionsDialogAsync()
    {
        Dispatcher.VerifyAccess();

        if (_userOptions is null)
        {
            throw new InvalidOperationException(
                "The application has no user-options store — opt in via UIApplicationBuilder.WithUserConfiguration.");
        }

        return _optionsDialogTask ??= ShowCoreAsync();

        async Task ShowCoreAsync()
        {
            try
            {
                await UserOptionsDialog.ShowAsync(this); // resumes on the UI thread
            }
            finally
            {
                _optionsDialogTask = null;
            }
        }
    }

    private Task? _optionsDialogTask;

    /// <summary>The user-configuration options the app was built with (the wizard reads the configured chord).</summary>
    internal UserConfigurationOptions? UserConfigurationInternal => _options.UserConfiguration;

    /// <summary>
    /// The framework binding behind the options-dialog chord: opens the dialog, single-instance
    /// (a second chord while open is a no-op — the existing task is returned, not a new dialog).
    /// </summary>
    private sealed class ShowUserOptionsCommandImpl(UIApplication application) : System.Windows.Input.ICommand
    {
        private readonly UIApplication _application =
            application ?? throw new ArgumentNullException(nameof(application));

        public bool CanExecute(object? parameter)
            => _application._userOptions is not null &&
               _application._shutdownRequested is false &&
               _application.Dispatcher.CheckAccess() &&
               // Never over the modal first-run wizard (two concurrent sessions corrupt the store;
               // the wizard root carries no chord, but a binding on an inner app element might).
               (parameter is "SkipFirstRunWizardCheck" ||
                _application._windowManager?.Windows.Any(static w => w is FirstRunWizard) is not true);

        public void Execute(object? parameter)
        {
            if (!CanExecute(parameter))
                return;

            // Fire-and-forget by design: the dialog's own faults surface through the exception
            // funnel; completion is "the user closed it".
            _ = _application.ShowUserOptionsDialogAsync();
        }

        public event EventHandler? CanExecuteChanged;
        
        public void RaiseCanExecuteChanged()
            => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
