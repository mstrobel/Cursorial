// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The terminal-capability axes a <see cref="Style"/> can require (<see cref="Style.RequiresCapabilities"/>)
/// — the <c>@media</c>-model replacement for gating rules on the <c>caps-*</c> ancestor classes (which a
/// descendant selector can never match when the SUBJECT is itself a surface root: the top-level-element
/// gap). The flags mirror <see cref="CapabilityClasses"/> one-for-one and are satisfied against the same
/// effective fold the class stamp uses (negotiated snapshot + overrides + the effective color tier), so a
/// migrated rule can never disagree with its old class form. Semantics: a rule applies only while
/// <b>every</b> required flag is present in the effective set. Capabilities are terminal-global — the gate
/// is evaluated once per match pass, never per element, and never arms watchers (unlike
/// <see cref="Style.When"/>, which is per-element data state).
/// </summary>
[Flags]
public enum StyleCapabilities
{
    /// <summary>No requirement — the rule applies on every terminal (the default).</summary>
    None = 0,

    /// <summary>The effective color tier is truecolor (<see cref="CapabilityClasses.Truecolor"/>).</summary>
    Truecolor = 1 << 0,

    /// <summary>The effective color tier is 256-color (<see cref="CapabilityClasses.Ansi256"/>).</summary>
    Ansi256 = 1 << 1,

    /// <summary>The effective color tier is 16-color (<see cref="CapabilityClasses.Ansi16"/>).</summary>
    Ansi16 = 1 << 2,

    /// <summary>The effective color tier is monochrome (<see cref="CapabilityClasses.NoColor"/>).</summary>
    NoColor = 1 << 3,

    /// <summary>Pointer motion reporting (<see cref="CapabilityClasses.Motion"/>).</summary>
    Motion = 1 << 4,

    /// <summary>The Kitty keyboard protocol (<see cref="CapabilityClasses.KittyKeyboard"/>).</summary>
    KittyKeyboard = 1 << 5,

    /// <summary>Any inline-image protocol (<see cref="CapabilityClasses.Images"/>).</summary>
    Images = 1 << 6,

    /// <summary>Clippable inline images (<see cref="CapabilityClasses.ImageClipping"/>).</summary>
    ImageClipping = 1 << 7,

    /// <summary>Z-orderable image placements (<see cref="CapabilityClasses.ImageOcclusion"/>).</summary>
    ImageOcclusion = 1 << 8,

    /// <summary>Nerd Font glyph coverage — the app-state opt-in (<see cref="CapabilityClasses.NerdFont"/>).</summary>
    NerdFont = 1 << 9,

    /// <summary>Emoji coverage — the app-state opt-out (<see cref="CapabilityClasses.Emoji"/>).</summary>
    Emoji = 1 << 10,

    /// <summary>Unicode glyph coverage. Currently unconditional, exactly like the class (no negotiated
    /// glyph-capability source exists — the SD14 recorded deferral); requiring it documents intent and
    /// starts gating for real the moment the deferral is lifted.</summary>
    Unicode = 1 << 11,

    /// <summary>OSC 66 text sizing support (kitty text sizing protocol).</summary>
    TextSizing = 1 << 12,

    /// <summary>
    /// Indicates, as best as the framework can determine, that an application is running locally and not through
    /// an SSH tunnel. This is the sole member of the locality axis: present means the process is local, absent
    /// means it is remote (e.g., an SSH session).
    /// </summary>
    Local = 1 << 13,

    /// <summary>
    /// Frames currently present into the inline region (<see cref="PresentationClasses.Inline"/>).
    /// The presentation axis always carries exactly one of this pair; under
    /// <see cref="ApplicationModel.InlineWithSwitching"/> the pair flips at each window-count
    /// transition — a style requiring it detaches/reattaches with the switch, same tick as the
    /// <c>app-inline</c> class.
    /// </summary>
    Inline = 1 << 14,

    /// <summary>
    /// Frames currently present to the full (alternate) screen
    /// (<see cref="PresentationClasses.FullScreen"/>). The other half of the presentation axis;
    /// the top declared flag.
    /// </summary>
    FullScreen = 1 << 15
}
