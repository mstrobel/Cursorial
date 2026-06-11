namespace Cursorial.UI.Input;

/// <summary>
/// The last input modality the dispatcher observed (design doc §7.4) — feeds the
/// <c>:focus-visible</c> policy. Starts as <see cref="Keyboard"/> (ND8: a keyboard-first device;
/// startup programmatic focus should show its visual before any input arrives).
/// </summary>
public enum InputModality : byte
{
    /// <summary>The last dispatched event was a key event (or none has arrived yet).</summary>
    Keyboard,

    /// <summary>The last dispatched event was a mouse event.</summary>
    Pointer,
}
