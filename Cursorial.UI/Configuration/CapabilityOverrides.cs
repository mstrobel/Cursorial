using Cursorial.Terminal;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The tri-state of one capability-override axis (<see cref="CapabilityOverrides"/>): defer to the
/// negotiated snapshot, force the axis present, or force it absent.
/// </summary>
public enum CapabilityOverride
{
    /// <summary>Use the negotiated capability as-is (the default).</summary>
    Auto = 0,

    /// <summary>
    /// Treat the capability as present regardless of negotiation. <b>Styling-scoped</b>: the
    /// <c>caps-*</c> classes and capability-tiered visuals (e.g. <see cref="Controls.Icon"/>)
    /// see the axis as available, but the session cannot emit a wire protocol it never
    /// negotiated — the renderer and input pipeline stay on the negotiated truth.
    /// </summary>
    ForceOn,

    /// <summary>
    /// Treat the capability as absent regardless of negotiation. This is the <b>clean</b>
    /// direction: every consumer of <see cref="UIApplication.EffectiveCapabilities"/> sees the
    /// axis withdrawn, so forced-off features degrade exactly as they would on a terminal that
    /// never advertised them.
    /// </summary>
    ForceOff,
}

/// <summary>
/// Per-axis user overrides over the negotiated <see cref="TerminalCapabilities"/> — the first-class
/// override seam (FB-5): assign to <see cref="UIApplication.CapabilityOverrides"/> and the
/// <c>caps-*</c> classes restamp on every surface root, <see cref="UIApplication.EffectiveCapabilities"/>
/// re-folds coherently, and capability-tiered visuals re-resolve, all without a restart. Being
/// application state (like <see cref="UIApplication.NerdFontAvailable"/>), overrides survive
/// <see cref="UIApplication.RenegotiateAsync"/> by design — the post-renegotiation restamp folds
/// them over the fresh snapshot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Honest semantics.</b> Forcing an axis OFF is clean — the capability disappears from the
/// effective view and everything downstream degrades as if it were never negotiated. Forcing an
/// axis ON is <b>styling-scoped</b>: it can make styles match and capability-tiered content
/// resolve to a richer tier, but it cannot make the terminal session negotiate or speak a wire
/// protocol it did not advertise (a forced-on Kitty keyboard does not change the input wire; a
/// forced-on graphics protocol does not make the renderer emit images).
/// </para>
/// <para>
/// The color tier and the glyph opt-ins are deliberately <b>not</b> axes here: the color tier
/// already overrides through <see cref="UIApplication.RequestedColorTier"/>, and Nerd Font /
/// emoji availability are user-declared opt-ins (<see cref="UIApplication.NerdFontAvailable"/> /
/// <see cref="UIApplication.EmojiAvailable"/>) with no negotiated signal to override.
/// </para>
/// </remarks>
public sealed record CapabilityOverrides
{
    /// <summary>No overrides — every axis <see cref="CapabilityOverride.Auto"/> (the default).</summary>
    public static CapabilityOverrides None { get; } = new();

    /// <summary>Overrides <see cref="Output.Capabilities.GraphicsCapabilities.Sixel"/>.</summary>
    public CapabilityOverride Sixel { get; init; }

    /// <summary>Overrides <see cref="Output.Capabilities.GraphicsCapabilities.KittyGraphics"/>.</summary>
    public CapabilityOverride KittyGraphics { get; init; }

    /// <summary>Overrides <see cref="Output.Capabilities.GraphicsCapabilities.ITerm2InlineImages"/>.</summary>
    public CapabilityOverride ITerm2InlineImages { get; init; }

    /// <summary>Overrides <see cref="Cursorial.Input.Capabilities.MouseCapabilities.Motion"/> (the <c>caps-motion</c> hover axis).</summary>
    public CapabilityOverride MouseMotion { get; init; }

    /// <summary>Overrides <see cref="Cursorial.Input.Capabilities.ProtocolCapabilities.KittyKeyboardProtocol"/> (the <c>caps-kitty-keyboard</c> axis).</summary>
    public CapabilityOverride KittyKeyboardProtocol { get; init; }

    /// <summary>Whether every axis is <see cref="CapabilityOverride.Auto"/> (folding is a no-op).</summary>
    public bool IsNone => Sixel == CapabilityOverride.Auto
                          && KittyGraphics == CapabilityOverride.Auto
                          && ITerm2InlineImages == CapabilityOverride.Auto
                          && MouseMotion == CapabilityOverride.Auto
                          && KittyKeyboardProtocol == CapabilityOverride.Auto;

    /// <summary>This override set with every image-protocol axis forced off (the "disable image support" user option).</summary>
    public CapabilityOverrides WithImagesDisabled()
        => this with
        {
            Sixel = CapabilityOverride.ForceOff,
            KittyGraphics = CapabilityOverride.ForceOff,
            ITerm2InlineImages = CapabilityOverride.ForceOff,
        };

    /// <summary>
    /// Folds the overrides over the negotiated snapshot — the single fold both
    /// <see cref="UIApplication.EffectiveCapabilities"/> and capability-class stamping use, so the
    /// stamped <c>caps-*</c> set and the app-visible effective record can never desync.
    /// </summary>
    public TerminalCapabilities Apply(TerminalCapabilities negotiated)
    {
        ArgumentNullException.ThrowIfNull(negotiated);

        if (IsNone)
            return negotiated;

        var graphics = negotiated.Output.Graphics;
        var mouse = negotiated.Input.Mouse;
        var protocol = negotiated.Input.Protocol;

        var kittyKeyboard = Resolve(KittyKeyboardProtocol, protocol.KittyKeyboardProtocol);

        return negotiated with
        {
            Output = negotiated.Output with
            {
                Graphics = graphics with
                {
                    Sixel = Resolve(Sixel, graphics.Sixel),
                    KittyGraphics = Resolve(KittyGraphics, graphics.KittyGraphics),
                    ITerm2InlineImages = Resolve(ITerm2InlineImages, graphics.ITerm2InlineImages),
                },
            },
            Input = negotiated.Input with
            {
                Mouse = mouse with { Motion = Resolve(MouseMotion, mouse.Motion) },
                Protocol = protocol with
                {
                    KittyKeyboardProtocol = kittyKeyboard,
                    // The flag set travels with the protocol bit: a forced-off protocol reports no
                    // realized flags; a forced-on one keeps the negotiated (typically empty) set —
                    // forcing ON cannot invent wire-level enhancements (styling-scoped, see above).
                    KittyKeyboardFlags = kittyKeyboard ? protocol.KittyKeyboardFlags : Cursorial.Input.Parsing.KittyKeyboardFlags.None,
                },
            },
        };
    }

    private static bool Resolve(CapabilityOverride axis, bool negotiated)
        => axis switch
        {
            CapabilityOverride.ForceOn  => true,
            CapabilityOverride.ForceOff => false,
            _                           => negotiated,
        };
}
