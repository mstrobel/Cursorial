namespace Cursorial.Input.Events;

/// <summary>
/// Base type for all events produced by an <see cref="IInputDevice"/>. The concrete derived
/// type identifies the kind of input; consumers typically pattern-match on type with
/// <c>switch</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Buffer-lifetime contract.</b> Events own their data. Any <see cref="ReadOnlyMemory{T}"/>
/// field exposed by an event (for example <see cref="KeyEvent.Text"/>,
/// <see cref="PasteEvent.Text"/>, <see cref="DeviceResponseEvent.Payload"/>,
/// <see cref="UnknownEvent.RawBytes"/>) is backed by memory whose lifetime extends for as long
/// as the consumer chooses to retain the event. Implementations MUST NOT alias buffers owned
/// by their underlying source — for example, a parser reading from <see cref="IInputByteSource"/>
/// must copy out of the <see cref="System.IO.Pipelines.PipeReader"/>'s segment before
/// constructing the event, since calling
/// <see cref="System.IO.Pipelines.PipeReader.AdvanceTo(System.SequencePosition)"/> invalidates
/// any retained view of that segment.
/// </para>
/// </remarks>
public abstract record InputEvent
{
    /// <summary>
    /// When the event was observed by the device. For decorators that synthesize events
    /// (e.g., fabricated key-up), this is the synthesis time, not the time of the original
    /// observation that triggered synthesis.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// True when the event was fabricated by a decorator rather than observed directly from
    /// the underlying source. Useful for consumers that want to distinguish best-effort
    /// synthesized signals (key-up timing, repeat heuristics) from device-reported truth.
    /// </summary>
    public bool Synthesized { get; init; }
}