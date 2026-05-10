namespace GlowTerm.Core.Input;

/// <summary>
/// A keyboard event. <see cref="Kind"/> distinguishes <see cref="KeyEventKind.Down"/> from
/// <see cref="KeyEventKind.Up"/>; auto-repeat is conveyed as a <see cref="KeyEventKind.Down"/>
/// with <see cref="IsRepeat"/> set (and <see cref="RepeatCount"/> populated when the source
/// coalesces multiple physical repeats into a single record).
/// </summary>
public sealed record class KeyEvent : InputEvent
{
    /// <summary>The named key. <see cref="Key.Character"/> indicates a printable key; see <see cref="Text"/>.</summary>
    public required Key Key { get; init; }

    /// <summary>Modifier keys held when the event occurred.</summary>
    public required KeyModifiers Modifiers { get; init; }

    /// <summary>Whether this event is a key-down or key-up.</summary>
    public required KeyEventKind Kind { get; init; }

    /// <summary>
    /// True when this <see cref="KeyEventKind.Down"/> event was produced by auto-repeat rather
    /// than an initial activation. Always false on <see cref="KeyEventKind.Up"/> events and on
    /// devices whose <see cref="KeyboardCapabilities.ReportsRepeats"/> is false.
    /// </summary>
    public bool IsRepeat { get; init; }

    /// <summary>
    /// Number of physical repeats this event represents — 1 for the initial press and for any
    /// repeat reported as a single event. Greater than 1 on sources that coalesce repeats into
    /// one record (notably Win32 console input, which sets <c>KEY_EVENT_RECORD.wRepeatCount</c>).
    /// Consumers that don't care about the distinction can ignore this field.
    /// </summary>
    public int RepeatCount { get; init; } = 1;

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
