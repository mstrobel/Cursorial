namespace GlowTerm.Core.Input;

/// <summary>
/// Aggregate description of which input categories a device can produce, broken down by
/// device class. Capabilities flow through a decorator chain — a synthesizer that wraps an
/// inner device may publish capabilities the inner did not (for example, fabricated key-up
/// events on terminals that do not natively report them).
/// </summary>
public sealed record class InputCapabilities(
    MouseCapabilities Mouse,
    KeyboardCapabilities Keyboard,
    PointerCapabilities Pointer,
    ProtocolCapabilities Protocol)
{
    /// <summary>A capability set reporting no supported input. Useful as a default or sentinel.</summary>
    public static InputCapabilities None { get; } = new(
        Mouse: MouseCapabilities.None,
        Keyboard: KeyboardCapabilities.None,
        Pointer: PointerCapabilities.None,
        Protocol: ProtocolCapabilities.None);
}
