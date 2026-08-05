namespace Cursorial.UI.Configuration;

/// <summary>
/// The well-known option keys the framework reads from the <see cref="UserOptionsStore" /> at
/// startup (<see cref="UIApplicationBuilder.WithUserConfiguration" />). Values are stored as
/// strings; the accepted forms are documented per key. Unknown keys are preserved verbatim
/// through load/save round-trips, so apps may keep their own keys in the same store.
/// </summary>
public static class UserOptionKeys
{
    /// <summary>
    /// The light/dark preference: <c>"light"</c>, <c>"dark"</c>, or <c>"auto"</c> (derive from the
    /// terminal background — the default). Applied to <see cref="UIApplication.RequestedThemeBase" />.
    /// </summary>
    public const string ThemeBase = "theme.base";

    /// <summary>
    /// The color-tier override: <c>"truecolor"</c>, <c>"ansi256"</c>, <c>"ansi16"</c>,
    /// <c>"nocolor"</c>, or <c>"auto"</c> (the negotiated depth — the default). Applied to
    /// <see cref="UIApplication.RequestedColorTier" />.
    /// </summary>
    public const string ColorTier = "theme.colorTier";

    /// <summary>
    /// Whether the terminal renders with a Nerd Font: <c>"true"</c>/<c>"false"</c> (default absent
    /// = false — no probe exists). Applied to <see cref="UIApplication.NerdFontAvailable" />
    /// (the <c>caps-nerdfont</c> class).
    /// </summary>
    public const string NerdFont = "capabilities.nerdFont";

    /// <summary>
    /// Whether the terminal font renders color emoji at the expected double-cell width:
    /// <c>"true"</c>/<c>"false"</c> (default absent = <b>true</b> — an opt-OUT, set <c>"false"</c>
    /// to disable; no probe exists). Deliberately the opposite default from <see cref="NerdFont" />:
    /// emoji coverage in modern terminals is near-universal, Nerd Font PUA coverage is not
    /// (maintainer decision, 2026-07-04). Applied to <see cref="UIApplication.EmojiAvailable" />
    /// (the <c>caps-emoji</c> class).
    /// </summary>
    public const string Emoji = "capabilities.emoji";

    /// <summary>
    /// Master image toggle: <c>"false"</c> forces every image protocol off
    /// (<see cref="CapabilityOverrides.WithImagesDisabled" />) regardless of the per-protocol
    /// override keys; <c>"true"</c>/absent defers to them. The "disable image support" option.
    /// </summary>
    public const string Images = "capabilities.images";

    /// <summary>Sixel graphics override: <c>"auto"</c> (default), <c>"on"</c>, or <c>"off"</c>.</summary>
    public const string SixelOverride = "capabilities.sixel";

    /// <summary>Kitty-graphics override: <c>"auto"</c> (default), <c>"on"</c>, or <c>"off"</c>.</summary>
    public const string KittyGraphicsOverride = "capabilities.kittyGraphics";

    /// <summary>iTerm2 inline-image override: <c>"auto"</c> (default), <c>"on"</c>, or <c>"off"</c>.</summary>
    public const string ITerm2ImagesOverride = "capabilities.iterm2Images";

    /// <summary>Mouse any-event-motion override (<c>caps-motion</c>): <c>"auto"</c> (default), <c>"on"</c>, or <c>"off"</c>.</summary>
    public const string MouseMotionOverride = "capabilities.mouseMotion";

    /// <summary>
    /// Kitty keyboard-protocol override (<c>caps-kitty-keyboard</c>): <c>"auto"</c> (default), <c>"on"</c>, or
    /// <c>"off"</c>.
    /// </summary>
    public const string KittyKeyboardOverride = "capabilities.kittyKeyboard";

    /// <summary>
    /// Animated transitions: <c>"true"</c> (default) / <c>"false"</c> (reduced motion). Applied to
    /// <see cref="Cursorial.UI.AnimationScheduler.AnimationsEnabled" />.
    /// </summary>
    public const string Animations = "appearance.animations";

    /// <summary>
    /// Fancy translucent menus and popups: <c>"true"</c>/<c>"false"</c>.
    /// <b>Key reserved — not yet applied at startup.</b> The framework-side seam it is destined for
    /// is <see cref="UIApplication.TranslucencyEnabled"/> (the opacity-group switch); nothing reads
    /// this key into it yet.
    /// </summary>
    public const string Translucency = "appearance.translucency";

    /// <summary>
    /// Always show access-key cues (instead of Alt-gated): <c>"true"</c>/<c>"false"</c>.
    /// <b>Key reserved — not yet consumed by the framework.</b>
    /// </summary>
    public const string AlwaysShowAccessKeyCues = "input.alwaysShowAccessKeyCues";

    /// <summary>
    /// Keyboard chord profile: <c>"platform"</c> (platform-native modifiers) or <c>"pc"</c>
    /// (PC-standard, <c>Ctrl</c> instead of <c>Super</c>).
    /// <b>
    /// Key reserved — not yet consumed by
    /// the framework.
    /// </b>
    /// </summary>
    public const string KeyboardProfile = "input.keyboardProfile";

    /// <summary>
    /// Dead zone that rails wheel gestures onto their dominant axis, suppressing the accidental
    /// cross-axis drift trackpads shed during fast one-axis scrolling: <c>"true"</c>/<c>"false"</c>
    /// (default off). Applied to <see cref="UIApplication.ScrollDeadZoneEnabled"/>; the gesture
    /// model is <see cref="Cursorial.Input.WheelAxisLock"/>'s.
    /// </summary>
    public const string ScrollDeadZone = "input.scrollDeadZone";

    /// <summary>
    /// Whether ANY Cursorial application has completed (or skipped) the first-run wizard on this
    /// system: <c>"true"</c> once it has. Lives in the GLOBAL store only — first-run onboarding
    /// is a per-system experience (an app can re-show via
    /// <see cref="UserConfigurationOptions.ForceFirstRunWizard"/>).
    /// </summary>
    public const string FirstRunCompleted = "meta.firstRunCompleted";
}