using System.Reflection;

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
    private bool _emojiAvailable = true; // FB-15: opt-OUT — default present (maintainer decision, 2026-07-04)
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
        get => _emojiAvailable;
        set
        {
            Dispatcher.VerifyAccess();
            if (_emojiAvailable == value)
                return;
            _emojiAvailable = value;
            StyleEngineInternal.RestampCapabilityClasses();
            EmojiAvailableChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised on the UI thread when <see cref="EmojiAvailable"/> flips (so capability-tiered visuals
    /// such as <see cref="Controls.Icon"/> can re-resolve their rendered tier live).</summary>
    public event EventHandler? EmojiAvailableChanged;

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

            if (_capabilityOverrides == value)
                return;

            _capabilityOverrides = value;
            StyleEngineInternal.RestampCapabilityClasses();
            CapabilityOverridesChanged?.Invoke(this, EventArgs.Empty);
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
    }
}
