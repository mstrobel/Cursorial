using Cursorial.Input.Capabilities;
using Cursorial.Input.Events;

namespace Cursorial.Input;

/// <summary>
/// Distinguishes a key press from a key release. Devices whose
/// <see cref="KeyboardCapabilities.DistinguishesKeyUpDown"/> is false report only
/// <see cref="Down"/>; <see cref="Up"/> is produced by Kitty-protocol devices,
/// Win32-input-mode devices, and synthesizer decorators. Auto-repeat is reported as a
/// <see cref="Down"/> with <see cref="KeyEvent.IsRepeat"/> set.
/// </summary>
public enum KeyEventKind
{
    /// <summary>The key transitioned from up to down — or, on devices that don't report
    /// release, simply: the key was activated.</summary>
    Down,

    /// <summary>The key transitioned from down to up. Reported only when the device's
    /// <see cref="KeyboardCapabilities.DistinguishesKeyUpDown"/> is true.</summary>
    Up,
}
