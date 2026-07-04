using Cursorial.Output;
using Cursorial.UI.Themes;

namespace Cursorial.UI.Configuration;

/// <summary>
/// Maps the loaded <see cref="UserOptionsStore"/> onto the <see cref="UIApplication"/> runtime
/// surfaces during startup — before the first capability-class stamp, so a themed/overridden app
/// never flashes its negotiated look first. Unrecognized values are ignored with a
/// <see cref="UserOptionsStore.LoadDiagnostics"/> entry; applying can never fail startup.
/// </summary>
internal static class UserConfigurationApplier
{
    internal static void Apply(UserOptionsStore store, UIApplication application)
    {
        ApplyThemeBase(store, application);
        ApplyColorTier(store, application);

        if (store.GetBoolean(UserOptionKeys.NerdFont) is { } nerdFont)
            application.NerdFontAvailable = nerdFont;

        if (store.GetBoolean(UserOptionKeys.Emoji) is { } emoji)
            application.EmojiAvailable = emoji;

        if (store.GetBoolean(UserOptionKeys.Animations) is { } animations)
            application.AnimationScheduler.AnimationsEnabled = animations;

        application.CapabilityOverrides = ReadCapabilityOverrides(store);

        // Reserved keys (Translucency, AlwaysShowAccessKeyCues, KeyboardProfile,
        // HorizontalScrollDeadZone) are defined in UserOptionKeys but have no framework consumer
        // yet — they round-trip through the store untouched.
    }

    private static void ApplyThemeBase(UserOptionsStore store, UIApplication application)
    {
        switch (Normalize(store.GetString(UserOptionKeys.ThemeBase)))
        {
            case null or "auto":
                break;
            case "light":
                application.RequestedThemeBase = ThemeBase.Light;
                break;
            case "dark":
                application.RequestedThemeBase = ThemeBase.Dark;
                break;
            case var other:
                store.AddDiagnostic($"'{UserOptionKeys.ThemeBase}': unrecognized value '{other}' (expected light/dark/auto); ignored.");
                break;
        }
    }

    private static void ApplyColorTier(UserOptionsStore store, UIApplication application)
    {
        switch (Normalize(store.GetString(UserOptionKeys.ColorTier)))
        {
            case null or "auto":
                break;
            case "truecolor":
                application.RequestedColorTier = ColorDepth.Truecolor;
                break;
            case "ansi256":
                application.RequestedColorTier = ColorDepth.Ansi256;
                break;
            case "ansi16":
                application.RequestedColorTier = ColorDepth.Ansi16;
                break;
            case "nocolor":
                application.RequestedColorTier = ColorDepth.NoColor;
                break;
            case var other:
                store.AddDiagnostic($"'{UserOptionKeys.ColorTier}': unrecognized value '{other}' (expected truecolor/ansi256/ansi16/nocolor/auto); ignored.");
                break;
        }
    }

    private static CapabilityOverrides ReadCapabilityOverrides(UserOptionsStore store)
    {
        var overrides = CapabilityOverrides.None with
        {
            Sixel = ReadAxis(store, UserOptionKeys.SixelOverride),
            KittyGraphics = ReadAxis(store, UserOptionKeys.KittyGraphicsOverride),
            ITerm2InlineImages = ReadAxis(store, UserOptionKeys.ITerm2ImagesOverride),
            MouseMotion = ReadAxis(store, UserOptionKeys.MouseMotionOverride),
            KittyKeyboardProtocol = ReadAxis(store, UserOptionKeys.KittyKeyboardOverride),
        };

        // The master image toggle wins over the per-protocol axes: "images off" is a safety/perf
        // choice that must hold even when an advanced per-protocol key says on.
        if (store.GetBoolean(UserOptionKeys.Images) is false)
            overrides = overrides.WithImagesDisabled();

        return overrides;
    }

    private static CapabilityOverride ReadAxis(UserOptionsStore store, string key)
    {
        switch (Normalize(store.GetString(key)))
        {
            case null or "auto":
                return CapabilityOverride.Auto;
            case "on":
                return CapabilityOverride.ForceOn;
            case "off":
                return CapabilityOverride.ForceOff;
            case var other:
                store.AddDiagnostic($"'{key}': unrecognized value '{other}' (expected on/off/auto); ignored.");
                return CapabilityOverride.Auto;
        }
    }

    private static string? Normalize(string? value) => value?.Trim().ToLowerInvariant();
}
