namespace GlowTerm.Core.Input;

/// <summary>
/// Describes the fidelity of keyboard input a device reports.
/// </summary>
/// <param name="DistinguishesKeyUpDown">
/// True when the device reports key-down and key-up as separate events. Most VT terminals do
/// not; a key-up synthesizer decorator can fabricate releases based on idle timing.
/// </param>
/// <param name="ReportsRepeats">
/// True when key auto-repeat is delivered as distinct repeat events rather than collapsed presses.
/// </param>
/// <param name="DetailedModifiers">
/// True when modifier state is reported per-key (left vs right shift, super, hyper, meta) rather
/// than only as a coarse Shift/Control/Alt summary.
/// </param>
/// <param name="TextInput">
/// True when the device delivers IME-composed or printable text alongside key events.
/// </param>
/// <param name="AlternateLayouts">
/// True when the device can report both the layout-translated key and the underlying physical
/// key — e.g. the Kitty keyboard protocol's "alternate keys" feature.
/// </param>
public sealed record class KeyboardCapabilities(
    bool DistinguishesKeyUpDown,
    bool ReportsRepeats,
    bool DetailedModifiers,
    bool TextInput,
    bool AlternateLayouts)
{
    public static KeyboardCapabilities None { get; } = new(
        DistinguishesKeyUpDown: false,
        ReportsRepeats: false,
        DetailedModifiers: false,
        TextInput: false,
        AlternateLayouts: false);
}
