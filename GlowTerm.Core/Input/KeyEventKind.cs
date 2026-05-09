namespace GlowTerm.Core.Input;

/// <summary>
/// Distinguishes a key press, release, or auto-repeat. Devices whose
/// <see cref="KeyboardCapabilities.DistinguishesKeyUpDown"/> is false report only
/// <see cref="Press"/>; <see cref="Up"/> and <see cref="Down"/> are produced by Kitty-protocol
/// devices, Win32-input-mode devices, and synthesizer decorators.
/// </summary>
public enum KeyEventKind
{
    /// <summary>An undifferentiated key activation — no separate up/down information available.</summary>
    Press,
    /// <summary>The key transitioned from up to down.</summary>
    Down,
    /// <summary>The key transitioned from down to up.</summary>
    Up,
    /// <summary>The key auto-repeated while held.</summary>
    Repeat,
}
