using Cursorial.Terminal;

namespace Cursorial.Media;

/// <summary>
/// The terminal's effective default foreground / background colors in concrete <see cref="ColorKind.Rgb"/>
/// form — the values <c>SGR 39 / 49</c> select — handed to the frame compositor so a
/// <see cref="Color.Default"/> operand can composite against the <em>real</em> default instead of taking
/// <see cref="Color.Composite"/>'s opaque-by-kind short-circuit (a translucent scrim over a Default-bg cell
/// blends rather than painting a solid slab; a layer-opacity fade actually fades Default-styled cells).
/// </summary>
/// <remarks>
/// <para>
/// <b>This type is the seam.</b> The compositor never reaches into a global for the default: it receives the
/// effective default as this value and substitutes only inside its own arithmetic inputs. Three sources can
/// populate it, and every one flows through this single value so none of them touches the compositor:
/// </para>
/// <list type="bullet">
/// <item><b>REPORTED</b> (today): the terminal's captured OSC 10/11 defaults —
///       <see cref="FromCapabilities"/>, mirroring <c>FrameRenderer.CaptureTerminalDefaultColors</c>'s rule
///       and <c>CellBuffer.DeriveDefaultStyle</c>'s RGB gate. This is what the WindowManager screen path
///       hands the compositor.</item>
/// <item><b>NONE</b> (today, and the default): the terminal reported nothing (Windows Console, mute
///       terminals) — <see cref="None"/>. Substitution is a no-op, so the compositor's output is
///       byte-identical to its pre-substitution behaviour. "Unknown ⇒ status quo" holds <em>by
///       construction</em>: every <c>Resolve*</c> below returns its argument unchanged.</item>
/// <item><b>APP-DECLARED</b> (future, deferred): the application declares the terminal default RGB itself.
///       It plugs in here — a new factory producing this value — without touching the compositor arithmetic.
///       Not implemented; wiring the seam so it can be is the point.</item>
/// </list>
/// <para>
/// Only an <em>opaque RGB</em> default is usable: a palette default would still short-circuit
/// <see cref="Color.Composite"/> (palette→RGB probing is out of scope for task 20), and a transparent /
/// absent one is not a color the terminal paints. The underline channel is deliberately absent —
/// <c>SGR 59</c> means "follow the foreground", not a reported color, so it is never substitutable.
/// </para>
/// </remarks>
public readonly record struct TerminalColorDefaults
{
    /// <summary>The reported default foreground, or <see langword="null"/> when unknown / unusable.</summary>
    public Color? Foreground { get; private init; }

    /// <summary>The reported default background, or <see langword="null"/> when unknown / unusable.</summary>
    public Color? Background { get; private init; }

    /// <summary>No known defaults — the compositor performs no substitution (today's behaviour). Equals
    /// <c>default</c>.</summary>
    public static TerminalColorDefaults None => default;

    /// <summary>True when neither channel has a usable default (substitution is a no-op).</summary>
    public bool IsNone => Foreground is null && Background is null;

    /// <summary>
    /// Build from a terminal's reported capabilities. Keeps a default channel only when it is an opaque RGB
    /// color the terminal can actually paint; anything else (absent, transparent, palette, Default-kind)
    /// leaves that channel unknown, disabling substitution for it exactly as the emission-side round trip is
    /// disabled for it.
    /// </summary>
    public static TerminalColorDefaults FromCapabilities(TerminalCapabilities? capabilities)
    {
        var color = capabilities?.Output.Color;
        return new TerminalColorDefaults
               {
                   Foreground = Usable(color?.DefaultForeground),
                   Background = Usable(color?.DefaultBackground)
               };
    }

    /// <summary>
    /// Substitute a foreground-channel <see cref="Color.Default"/> with the reported default foreground.
    /// Returns <paramref name="color"/> unchanged when it is not Default or no foreground default is known —
    /// so a <see cref="None"/> value is a total no-op.
    /// </summary>
    public Color ResolveForeground(Color color) => color.IsDefault && Foreground is { } fg ? fg : color;

    /// <summary>
    /// Substitute a background-channel <see cref="Color.Default"/> with the reported default background.
    /// Returns <paramref name="color"/> unchanged when it is not Default or no background default is known.
    /// </summary>
    public Color ResolveBackground(Color color) => color.IsDefault && Background is { } bg ? bg : color;

    private static Color? Usable(Color? reported) =>
        reported is { Kind: ColorKind.Rgb, Alpha: 255 } value ? value : null;
}
