namespace Cursorial.Input;

/// <summary>
/// Bitmask of modifier-state flags for an input event. The enum carries two distinct kinds of
/// state: input modifier keys (<see cref="Shift"/>..<see cref="Meta"/>) and lock-key toggle
/// state (<see cref="CapsLock"/>, <see cref="NumLock"/>, <see cref="ScrollLock"/>).
/// <see cref="Cursorial.Input.Events.KeyEvent"/> surfaces them through two properties —
/// <c>Modifiers</c> for just the input modifiers and <c>ExtendedModifiers</c> for the full set
/// including locks — so that
/// pattern-style matching like <c>k is KeyEvent { Modifiers: Control }</c> matches Ctrl+C
/// regardless of whether NumLock happens to be on.
/// </summary>
[Flags]
public enum KeyModifiers : uint
{
    None = 0,

    // Input modifier keys. Pattern matching against `Modifiers` only sees these bits.
    Shift   = 1u << 0,
    Control = 1u << 1,
    Alt     = 1u << 2,
    Super   = 1u << 3, // Windows / Command
    Hyper   = 1u << 4,
    Meta    = 1u << 5,

    // Lock-key toggle state. Surfaced in `ExtendedModifiers`, NEVER in `Modifiers`. Producers
    // that build a combined bitmask should split it via the masks below before assigning.
    CapsLock   = 1u << 6,
    NumLock    = 1u << 7,
    ScrollLock = 1u << 8,

    /// <summary>Mask covering only the input modifier keys (Shift..Meta). Use to project a
    /// combined bitmask down to the strict modifier set.</summary>
    ModifierMask = Shift | Control | Alt | Super | Hyper | Meta,

    /// <summary>Mask covering only the lock-key toggle bits.</summary>
    LockMask = CapsLock | NumLock | ScrollLock,
}
