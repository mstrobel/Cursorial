namespace Cursorial.UI.Input;

/// <summary>
/// How access-key cues behave on the current terminal (design doc §7.8) — derived by
/// <see cref="AccessKeyManager.OnCapabilitiesChanged"/> from the <b>negotiated, undecorated</b>
/// capability snapshot (ND23).
/// </summary>
public enum AccessKeyMode : byte
{
    /// <summary>
    /// Capability-gated cue window: the cue toggles with physical Alt bracketing (Kitty key-up +
    /// repeats, or Win32 input mode), with chord-flash self-correction for terminals that claim but
    /// never deliver the bracket.
    /// </summary>
    AltHeld,

    /// <summary>
    /// The legacy fallback (requirement 6's "permanently visible otherwise"): the cue is set on
    /// every surface root and never cleared.
    /// </summary>
    AlwaysVisible,
}
