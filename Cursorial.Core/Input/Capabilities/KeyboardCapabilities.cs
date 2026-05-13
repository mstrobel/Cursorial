using Cursorial.Input.Events;

namespace Cursorial.Input.Capabilities;

/// <summary>
/// Describes the fidelity of keyboard input a device reports.
/// </summary>
/// <param name="DistinguishesKeyUpDown">
/// True when the device reports key-down and key-up as separate events. Most VT terminals do
/// not; a key-up synthesizer decorator can fabricate releases based on idle timing.
/// </param>
/// <param name="ReportsRepeats">
/// True when key auto-repeat is reported (as a <see cref="KeyEvent"/> with
/// <see cref="KeyEvent.IsRepeat"/> set, and <see cref="KeyEvent.RepeatCount"/> populated where
/// the source coalesces multiple repeats into one record).
/// </param>
/// <param name="DetailedModifiers">
/// True when modifier state is reported per-key (left vs right shift, super, hyper, meta) rather
/// than only as a coarse Shift/Control/Alt summary.
/// </param>
/// <param name="TextInput">
/// True when the device delivers IME-composed or printable text alongside key events.
/// </param>
public sealed record class KeyboardCapabilities(
    bool DistinguishesKeyUpDown,
    bool ReportsRepeats,
    bool DetailedModifiers,
    bool TextInput)
{
    public static KeyboardCapabilities None { get; } = new(
        DistinguishesKeyUpDown: false,
        ReportsRepeats: false,
        DetailedModifiers: false,
        TextInput: false);
}
