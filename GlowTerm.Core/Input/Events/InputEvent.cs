namespace GlowTerm.Core.Input;

/// <summary>
/// Base type for all events produced by an <see cref="IInputDevice"/>. The concrete derived
/// type identifies the kind of input; consumers typically pattern-match on type with
/// <c>switch</c>.
/// </summary>
public abstract record class InputEvent
{
    /// <summary>
    /// When the event was observed by the device. For decorators that synthesize events
    /// (e.g. fabricated key-up), this is the synthesis time, not the time of the original
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
