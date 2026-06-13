using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Terminal;

namespace Cursorial.UI;

/// <summary>
/// The light/dark axis of the theme model (design doc §11.2). Numeric values are stable.
/// </summary>
public enum ThemeBase : byte
{
    /// <summary>A dark theme — the default when the terminal background is dark or unknown.</summary>
    Dark = 0,

    /// <summary>A light theme — derived from a light terminal background.</summary>
    Light = 1
}

/// <summary>
/// The effective theme variant: a 2-D, terminal-native <c>light/dark × negotiated <see cref="ColorDepth"/> tier</c>
/// axis (design doc §11.2). Unlike WPF's named variants, there are no tier-baked <c>Dark</c>/<c>Light</c>
/// statics — the tier is always the negotiated (or overridden) <see cref="ColorDepth"/>.
/// </summary>
/// <param name="Base">The light/dark axis.</param>
/// <param name="Tier">The negotiated (effective) color tier.</param>
public readonly record struct ThemeVariant(ThemeBase Base, ColorDepth Tier)
{
    /// <summary>Whether this is a dark variant.</summary>
    public bool IsDark => Base == ThemeBase.Dark;

    /// <summary>Whether this is a light variant.</summary>
    public bool IsLight => Base == ThemeBase.Light;

    /// <summary>
    /// Derives the variant from negotiated capabilities (CD9): <see cref="ThemeBase"/> from the
    /// relative luminance of <see cref="ColorCapabilities.DefaultBackground"/>
    /// (<c>0.2126R + 0.7152G + 0.0722B</c> over sRGB-normalized channels) — <c>&gt; 0.5</c> ⇒
    /// <see cref="ThemeBase.Light"/>, else <see cref="ThemeBase.Dark"/>; a <see langword="null"/> or
    /// non-RGB background ⇒ <see cref="ThemeBase.Dark"/>. <see cref="Tier"/> = the negotiated depth.
    /// </summary>
    public static ThemeVariant FromCapabilities(TerminalCapabilities caps)
        => new(DeriveBase(caps.Output.Color.DefaultBackground), caps.Output.Color.Depth);

    /// <summary>The light/dark derivation in isolation (the per-axis half consumed by the effective-variant computation).</summary>
    internal static ThemeBase DeriveBase(Color? background)
    {
        if (background is not { Kind: ColorKind.Rgb } rgb)
            return ThemeBase.Dark; // null/default/palette: dark by contract

        // Relative luminance over sRGB-normalized channels; the boundary is strictly-greater for Light (CD9/C35).
        var luminance = (0.2126 * rgb.Red + 0.7152 * rgb.Green + 0.0722 * rgb.Blue) / 255.0;
        return luminance > 0.5 ? ThemeBase.Light : ThemeBase.Dark;
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Base}+{Tier}";
}
