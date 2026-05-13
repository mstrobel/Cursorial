using Cursorial.Input.Capabilities;

namespace Cursorial.Input.Events;

/// <summary>
/// A complete paste of text delivered as a single event. Only emitted by devices whose
/// <see cref="ProtocolCapabilities.BracketedPaste"/> is true; otherwise pasted text arrives
/// as individual <see cref="KeyEvent"/>s indistinguishable from typed input.
/// </summary>
public sealed record PasteEvent : InputEvent
{
    /// <summary>The pasted text. May contain embedded newlines.</summary>
    public required ReadOnlyMemory<char> Text { get; init; }
}