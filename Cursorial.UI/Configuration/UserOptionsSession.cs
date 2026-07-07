using Cursorial.Output;

namespace Cursorial.UI.Configuration;

/// <summary>The layer an options edit targets (the dialog's "Editing: Global | This application" switch).</summary>
public enum UserOptionScope : byte
{
    /// <summary>The global layer (<c>options.json</c>) — every Cursorial app.</summary>
    Global,

    /// <summary>The per-application overlay (<c>apps/&lt;appId&gt;.json</c>) — this app only.</summary>
    Application
}

/// <summary>
/// One live editing session over the application's <see cref="UserOptionsStore"/> — the model
/// BOTH the settings dialog and the first-run wizard drive (FB-17 Stage B). Owns the
/// live-preview / revert / persist lifecycle:
/// </summary>
/// <remarks>
/// <para>
/// <b>Live preview.</b> Every write through <see cref="SetValue"/> updates the store and
/// immediately re-applies the SAFE keys to the running application with explicit
/// absent-means-default semantics — clearing a key live-reverts the app to the documented
/// default, which the startup applier (only ever running against a fresh app) never needs to do.
/// </para>
/// <para>
/// <b>Staged danger.</b> <see cref="UserOptionDescriptor.RequiresTest"/> keys (capability
/// overrides, the color tier) never live-apply: a wrong force-on can corrupt the entire display.
/// They stage in the store, may be exercised through the timed <see cref="BeginTest"/> (dispose
/// reverts — the "did the screen survive?" probe), and reach the application only at
/// <see cref="Save"/>.
/// </para>
/// <para>
/// <b>Reset / Cancel.</b> <see cref="Reset"/> restores the open-time snapshot and re-applies —
/// the dialog stays open ("back to how it was when I opened this"). Cancel is Reset + close
/// (the view's concern). <see cref="Save"/> applies the staged dangerous values and persists
/// both files.
/// </para>
/// </remarks>
public sealed class UserOptionsSession
{
    private readonly UIApplication _application;
    private readonly UserOptionsSnapshot _openSnapshot;

    // The dangerous values in force on the live app — captured at open, advanced only by Save()
    // (and temporarily supplanted during a test run). Live preview never moves these.
    private ColorDepth? _committedColorTier;
    private CapabilityOverrides _committedOverrides;
    private bool _testRunning;

    /// <summary>
    /// Opens a session over <paramref name="application"/>'s loaded store. Requires
    /// <see cref="UIApplication.UserOptions"/> (the app opted into user configuration).
    /// </summary>
    public UserOptionsSession(UIApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.Dispatcher.VerifyAccess();

        Store = application.UserOptions
                ?? throw new InvalidOperationException(
                    "The application has no user-options store — opt in via UIApplicationBuilder.WithUserConfiguration.");

        _application = application;
        _openSnapshot = Store.CreateSnapshot();
        _committedColorTier = application.RequestedColorTier;
        _committedOverrides = application.CapabilityOverrides;
    }

    /// <summary>The store this session edits (the same instance the application loaded at startup).</summary>
    public UserOptionsStore Store { get; }

    /// <summary>The application whose live state previews the edits.</summary>
    public UIApplication Application => _application;

    /// <summary>Whether a timed capability test is currently in force (<see cref="BeginTest"/>).</summary>
    public bool IsTestRunning => _testRunning;

    /// <summary>
    /// Whether the staged dangerous keys (color tier, capability overrides) currently differ from
    /// what is in force on the live application — i.e. whether <see cref="Save"/> will change the
    /// display, and whether a test run has anything to demonstrate.
    /// </summary>
    public bool HasStagedDangerousChanges
        => StagedColorTier() != _committedColorTier || StagedOverrides() != _committedOverrides;

    // ───────────────────────────── reads / writes ─────────────────────────────

    /// <summary>The value the given layer explicitly sets, or <see langword="null"/> when that layer inherits.</summary>
    public string? GetValue(UserOptionScope scope, string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return scope == UserOptionScope.Application
                   ? Store.ApplicationValues.GetValueOrDefault(key)
                   : Store.GlobalValues.GetValueOrDefault(key);
    }

    /// <summary>The effective value: app overlay → global → the catalog default (when catalogued), else <see langword="null"/>.</summary>
    public string? GetEffectiveValue(string key)
        => Store.GetString(key) ?? FindDescriptor(key)?.DefaultValue;

    /// <summary>
    /// Writes (or, with <see langword="null"/>, clears — the inherit direction of the tri-state)
    /// a key in the given layer, then live-applies the safe subset. Dangerous keys
    /// (<see cref="UserOptionDescriptor.RequiresTest"/>) stage without touching the display.
    /// </summary>
    public void SetValue(UserOptionScope scope, string key, string? value)
    {
        _application.Dispatcher.VerifyAccess();

        if (scope == UserOptionScope.Application)
            Store.SetApplication(key, value);
        else
            Store.SetGlobal(key, value);

        ApplyLive();
    }

    // ───────────────────────────── lifecycle ─────────────────────────────

    /// <summary>
    /// Restores the open-time snapshot — both layers, including keys this UI doesn't know about —
    /// and re-applies live. The session stays usable: this is the dialog's Reset button, not a
    /// teardown. Any staged dangerous edits are discarded (the committed values were never moved).
    /// </summary>
    public void Reset()
    {
        _application.Dispatcher.VerifyAccess();
        Store.RestoreSnapshot(_openSnapshot);
        ApplyLive();
    }

    /// <summary>
    /// Applies the staged dangerous values to the live application (they were only staged until
    /// now) and persists both files. Throws on I/O failure, matching
    /// <see cref="UserOptionsStore.Save"/> — a failed save must be visible.
    /// </summary>
    public void Save()
    {
        _application.Dispatcher.VerifyAccess();

        if (_testRunning)
            throw new InvalidOperationException("End the capability test before saving.");

        // PERSIST FIRST: if the disk write throws, nothing has moved — the committed baseline
        // still holds the pre-save values, so the dialog's Cancel/✕ (Reset) genuinely reverts.
        // The reverse order left the staged dangerous values applied-but-unpersisted after a
        // failed save, exactly the torn state the staging contract exists to prevent.
        Store.Save();

        _committedColorTier = StagedColorTier();
        _committedOverrides = StagedOverrides();
        _application.RequestedColorTier = _committedColorTier;
        _application.CapabilityOverrides = _committedOverrides;
    }

    /// <summary>
    /// Applies the STAGED dangerous values (color tier + capability overrides) to the live
    /// application for a bounded look — the "will my screen survive this?" probe. Disposing the
    /// returned handle reverts to the committed values; the caller owns the countdown (a
    /// <see cref="UITimer"/> in the view-model) and simply disposes at zero. One test at a time.
    /// </summary>
    public IDisposable BeginTest()
    {
        _application.Dispatcher.VerifyAccess();

        if (_testRunning)
            throw new InvalidOperationException("A capability test is already running.");

        _testRunning = true;
        _application.RequestedColorTier = StagedColorTier();
        _application.CapabilityOverrides = StagedOverrides();
        return new TestScope(this);
    }

    private void EndTest()
    {
        if (!_testRunning)
            return;

        _testRunning = false;
        _application.RequestedColorTier = _committedColorTier;
        _application.CapabilityOverrides = _committedOverrides;
    }

    // ───────────────────────────── live application (safe subset) ─────────────────────────────

    /// <summary>
    /// Re-applies every SAFE key to the running application with explicit absent-means-default
    /// semantics. Idempotent (every target setter equality-gates), and deliberately never touches
    /// the dangerous keys — those move at <see cref="Save"/>/<see cref="BeginTest"/> only.
    /// </summary>
    public void ApplyLive()
    {
        _application.Dispatcher.VerifyAccess();

        _application.RequestedThemeBase = Normalize(Store.GetString(UserOptionKeys.ThemeBase)) switch
        {
            "light" => ThemeBase.Light,
            "dark" => ThemeBase.Dark,
            _ => null // absent / "auto" / unrecognized → derive from the terminal background
        };

        _application.NerdFontAvailable = Store.GetBoolean(UserOptionKeys.NerdFont) ?? false;
        _application.EmojiAvailable = Store.GetBoolean(UserOptionKeys.Emoji) ?? true;
        _application.AnimationScheduler.AnimationsEnabled = Store.GetBoolean(UserOptionKeys.Animations) ?? true;

        // While the timed test holds the STAGED overrides on screen, a safe edit must not clobber
        // them back to committed mid-countdown — the probe would silently show a mixed state and
        // the "screen survived" verdict would lie. EndTest restores the committed values itself.
        if (_testRunning)
            return;

        // The images master toggle is safe (it can only WITHDRAW capability) and lives on the
        // same record as the dangerous axes — fold it over the COMMITTED axes so flipping it
        // previews live without moving any staged force-on/off.
        var overrides = _committedOverrides;

        if (Store.GetBoolean(UserOptionKeys.Images) is false)
            overrides = overrides.WithImagesDisabled();

        _application.CapabilityOverrides = overrides;
    }

    // ───────────────────────────── staged dangerous values ─────────────────────────────

    private ColorDepth? StagedColorTier() => Normalize(Store.GetString(UserOptionKeys.ColorTier)) switch
    {
        "truecolor" => ColorDepth.Truecolor,
        "ansi256" => ColorDepth.Ansi256,
        "ansi16" => ColorDepth.Ansi16,
        "nocolor" => ColorDepth.NoColor,
        _ => null // absent / "auto" / unrecognized → the negotiated tier
    };

    private CapabilityOverrides StagedOverrides()
    {
        var overrides = CapabilityOverrides.None with
        {
            Sixel = StagedAxis(UserOptionKeys.SixelOverride),
            KittyGraphics = StagedAxis(UserOptionKeys.KittyGraphicsOverride),
            ITerm2InlineImages = StagedAxis(UserOptionKeys.ITerm2ImagesOverride),
            MouseMotion = StagedAxis(UserOptionKeys.MouseMotionOverride),
            KittyKeyboardProtocol = StagedAxis(UserOptionKeys.KittyKeyboardOverride)
        };

        // The master image toggle wins over the per-protocol axes (same rule as startup).
        if (Store.GetBoolean(UserOptionKeys.Images) is false)
            overrides = overrides.WithImagesDisabled();

        return overrides;
    }

    private CapabilityOverride StagedAxis(string key) => Normalize(Store.GetString(key)) switch
    {
        "on" => CapabilityOverride.ForceOn,
        "off" => CapabilityOverride.ForceOff,
        _ => CapabilityOverride.Auto
    };

    private static UserOptionDescriptor? FindDescriptor(string key)
    {
        foreach (var descriptor in UserOptionCatalog.All)
        {
            if (string.Equals(descriptor.Key, key, StringComparison.Ordinal))
                return descriptor;
        }

        return null;
    }

    private static string? Normalize(string? value) => value?.Trim().ToLowerInvariant();

    private sealed class TestScope(UserOptionsSession session) : IDisposable
    {
        private UserOptionsSession? _session = session;

        public void Dispose()
        {
            Interlocked.Exchange(ref _session, null)?.EndTest();
        }
    }
}
