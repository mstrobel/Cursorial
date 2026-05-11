namespace GlowTerm.Core.Input;

/// <summary>
/// Identifies a key. For printable character keys, use <see cref="Character"/> and consult
/// <see cref="KeyEvent.Text"/> for the produced text. Named keys cover the standard terminal
/// keyboard surface; consumers needing keys outside this set can fall back on
/// <see cref="KeyEvent.RawCode"/>.
/// </summary>
public enum Key
{
    None = 0,

    /// <summary>
    /// A printable character key — letters, digits, punctuation, IME-composed characters.
    /// When <see cref="KeyEvent.Key"/> equals this value, the produced text is in
    /// <see cref="KeyEvent.Text"/>. All other <see cref="Key"/> values represent named keys
    /// whose <see cref="KeyEvent.Text"/> is normally empty (though some devices may populate
    /// it with the equivalent printable form for synthesized text input).
    /// </summary>
    Character,

    // Common control keys
    Backspace,
    Tab,
    Enter,
    Escape,
    Space,
    Delete,
    Insert,

    // Cursor + paging
    UpArrow,
    DownArrow,
    LeftArrow,
    RightArrow,
    Home,
    End,
    PageUp,
    PageDown,

    // Function keys
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    F13, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24,

    // Numpad
    Numpad0, Numpad1, Numpad2, Numpad3, Numpad4,
    Numpad5, Numpad6, Numpad7, Numpad8, Numpad9,
    NumpadAdd, NumpadSubtract, NumpadMultiply, NumpadDivide,
    NumpadEnter, NumpadDecimal, NumpadEquals,

    // Modifier keys, when the device reports them as standalone events
    LeftShift, RightShift,
    LeftControl, RightControl,
    LeftAlt, RightAlt,
    LeftSuper, RightSuper,
    LeftHyper, RightHyper,
    LeftMeta, RightMeta,
    CapsLock, NumLock, ScrollLock,

    // Misc
    PrintScreen,
    Pause,
    Menu,

    // Media keys (commonly seen on Win32 console / terminals that pass them through)
    MediaPlay,
    MediaPause,
    MediaPlayPause,
    MediaStop,
    MediaNext,
    MediaPrevious,
    VolumeUp,
    VolumeDown,
    VolumeMute,
}
