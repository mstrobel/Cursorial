namespace Cursorial.Core.Input;

/// <summary>
/// A response from the terminal to a query previously issued by the application — for example
/// a Device Attributes (DA1/DA2) reply or a cursor position report (DSR). Surfaced through the
/// input pipeline so consumers can correlate replies with whatever issued the query.
/// </summary>
public sealed record class DeviceResponseEvent : InputEvent
{
    /// <summary>The kind of response, when recognized.</summary>
    public required DeviceResponseKind Kind { get; init; }

    /// <summary>Raw payload bytes of the response. Parsing into a typed value is left to the consumer.</summary>
    public required ReadOnlyMemory<byte> Payload { get; init; }
}
