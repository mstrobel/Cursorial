using Cursorial.Input.Events;

namespace Cursorial.Input.Capabilities;

/// <summary>
/// Describes terminal-protocol-level features a device can report or has negotiated.
/// </summary>
/// <param name="BracketedPaste">
/// True when the device emits a single <see cref="PasteEvent"/> for a paste operation
/// instead of a stream of individual key events.
/// </param>
/// <param name="FocusEvents">True when terminal focus-in / focus-out events are reported.</param>
/// <param name="KittyKeyboardProtocol">
/// True when the device participates in the Kitty keyboard protocol — full key-up/down,
/// disambiguated modifiers, alternate keys, and text payloads.
/// </param>
/// <param name="Win32InputMode">
/// True when the device participates in Microsoft's Win32 Input Mode for ConPTY
/// (xterm DECSET 9001), which carries lossless Win32 console key records over a VT channel.
/// </param>
public sealed record class ProtocolCapabilities(
    bool BracketedPaste,
    bool FocusEvents,
    bool KittyKeyboardProtocol,
    bool Win32InputMode)
{
    public static ProtocolCapabilities None { get; } = new(
        BracketedPaste: false,
        FocusEvents: false,
        KittyKeyboardProtocol: false,
        Win32InputMode: false);
}
