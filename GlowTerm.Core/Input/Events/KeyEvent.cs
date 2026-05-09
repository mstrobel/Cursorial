namespace GlowTerm.Core.Input;

/// <summary>
/// A keyboard event. <see cref="Kind"/> distinguishes press, release, and repeat where the
/// device supports it; on devices that do not (most VT terminals), only
/// <see cref="KeyEventKind.Press"/> is observed unless a synthesizer decorator is in play.
/// </summary>
public sealed record class KeyEvent : InputEvent
{
    /// <summary>The named key. <see cref="Key.Character"/> indicates a printable key; see <see cref="Text"/>.</summary>
    public required Key Key { get; init; }

    /// <summary>Modifier keys held when the event occurred.</summary>
    public required KeyModifiers Modifiers { get; init; }

    /// <summary>Press, release, or repeat.</summary>
    public required KeyEventKind Kind { get; init; }

    /// <summary>
    /// The composed printable text produced by this key event, if any. Populated for
    /// character keys and IME composition results; empty for named/control keys.
    /// </summary>
    public ReadOnlyMemory<char> Text { get; init; }

    /// <summary>
    /// Platform raw key code (Win32 virtual-key, X11 keysym, etc.) when known. Useful for
    /// applications that need to distinguish physical keys regardless of layout.
    /// </summary>
    public uint? RawCode { get; init; }
}
