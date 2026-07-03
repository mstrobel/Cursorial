using Cursorial.Input.Events;
using Cursorial.Input.Parsing;

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
public sealed record ProtocolCapabilities(bool BracketedPaste,
                                          bool FocusEvents,
                                          bool KittyKeyboardProtocol,
                                          bool Win32InputMode)
{
    /// <summary>
    /// The realized Kitty keyboard progressive-enhancement flag set (<see cref="KittyKeyboardFlags.None"/>
    /// when <see cref="KittyKeyboardProtocol"/> is false). Surfaced so a consumer that switches screen
    /// buffers can re-apply the negotiated flags — the Kitty flag stack is per-screen-buffer, so entering
    /// the alternate screen starts a fresh, empty stack. Init-only (the positional constructor and 4-arg
    /// deconstruction are unchanged); defaults to <see cref="KittyKeyboardFlags.None"/>.
    /// </summary>
    public KittyKeyboardFlags KittyKeyboardFlags { get; init; } = KittyKeyboardFlags.None;

    public static ProtocolCapabilities None { get; } = new(BracketedPaste: false,
                                                           FocusEvents: false,
                                                           KittyKeyboardProtocol: false,
                                                           Win32InputMode: false);
}
