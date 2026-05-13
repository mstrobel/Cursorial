namespace Cursorial.Input;

/// <summary>
/// Bitmask of modifier keys held during an input event. Toggle modifiers (<see cref="CapsLock"/>,
/// <see cref="NumLock"/>, <see cref="ScrollLock"/>) reflect the toggle state, not whether the
/// physical key is currently held.
/// </summary>
[Flags]
public enum KeyModifiers : uint
{
    None = 0,
    Shift = 1u << 0,
    Control = 1u << 1,
    Alt = 1u << 2,
    Super = 1u << 3, // Windows / Command
    Hyper = 1u << 4,
    Meta = 1u << 5,
    CapsLock = 1u << 6,
    NumLock = 1u << 7,
    ScrollLock = 1u << 8
}