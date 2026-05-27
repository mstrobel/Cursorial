using Cursorial.Input.Capabilities;

namespace Cursorial.Input.Events;

/// <summary>
/// A keyboard event. <see cref="Kind"/> distinguishes <see cref="KeyEventKind.Down"/> from
/// <see cref="KeyEventKind.Up"/>; auto-repeat is conveyed as a <see cref="KeyEventKind.Down"/>
/// with <see cref="IsRepeat"/> set (and <see cref="RepeatCount"/> populated when the source
/// coalesces multiple physical repeats into a single record).
/// </summary>
public sealed record KeyEvent : InputEvent
{
    /// <summary>The named key. <see cref="Key.Character"/> indicates a printable key; see <see cref="Text"/>.</summary>
    public required Key Key { get; init; }

    /// <summary>
    /// Input modifier keys held when the event occurred — <see cref="KeyModifiers.Shift"/>,
    /// <see cref="KeyModifiers.Control"/>, <see cref="KeyModifiers.Alt"/>,
    /// <see cref="KeyModifiers.Super"/>, <see cref="KeyModifiers.Hyper"/>,
    /// <see cref="KeyModifiers.Meta"/>. CapsLock / NumLock / ScrollLock toggle state is NOT
    /// included here, so patterns like <c>KeyEvent { .., Modifiers: KeyModifiers.Control }</c>
    /// match <c>Ctrl+[Key]</c> regardless of the user's lock-key state. Lock state is surfaced
    /// through <see cref="ExtendedModifiers"/>.
    /// </summary>
    public required KeyModifiers Modifiers { get; init; }

    /// <summary>
    /// Full modifier state including CapsLock / NumLock / ScrollLock toggle bits. Equal to
    /// <see cref="Modifiers"/> OR'd with any active lock-state bits the producer supplied —
    /// always a superset of <see cref="Modifiers"/>. Consumers that need the user's lock-key
    /// state read from here; consumers matching on a specific modifier combination should use
    /// <see cref="Modifiers"/> instead so lock state doesn't break their pattern.
    /// </summary>
    public KeyModifiers ExtendedModifiers
    {
        // The OR with Modifiers ensures the returned value is always a superset of Modifiers
        // even when the producer set ExtendedModifiers before Modifiers (or didn't set
        // ExtendedModifiers at all). Storage holds just the additional lock bits.
        get => field | Modifiers;
        init;
    }

    /// <summary>Whether this event is a key-down or key-up.</summary>
    public required KeyEventKind Kind { get; init; }

    /// <summary>
    /// True when this <see cref="KeyEventKind.Down"/> event was produced by auto-repeat rather
    /// than an initial activation. Always false on <see cref="KeyEventKind.Up"/> events and on
    /// devices whose <see cref="KeyboardCapabilities.ReportsRepeats"/> is false.
    /// </summary>
    public bool IsRepeat { get; init; }

    /// <summary>
    /// Repeat count for this event — 1 for the initial press; greater than 1 either on sources
    /// that coalesce multiple physical repeats into one record (notably Win32 console input,
    /// which sets <c>KEY_EVENT_RECORD.wRepeatCount</c>), or on the <c>KeyReleaseSynthesizer</c>
    /// decorator's inferred repeats — there it's the Nth press since the initial activation
    /// (2 for the first auto-repeat, 3 for the next, …). Consumers that don't care about the
    /// distinction can ignore this field.
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