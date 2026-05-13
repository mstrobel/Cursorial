namespace Cursorial.Input.Events;

/// <summary>
/// An input chunk the device could not classify. Emitted (rather than dropped) so consumers
/// can log, forward, or attempt their own parsing — useful when terminals emit sequences the
/// library does not yet recognize.
/// </summary>
public sealed record UnknownEvent : InputEvent
{
    /// <summary>The raw bytes that could not be classified.</summary>
    public required ReadOnlyMemory<byte> RawBytes { get; init; }
}