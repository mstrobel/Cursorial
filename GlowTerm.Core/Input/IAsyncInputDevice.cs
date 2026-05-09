namespace GlowTerm.Core.Input;

/// <summary>
/// An input device whose events are consumed via an awaitable <see cref="IAsyncEnumerable{T}"/>.
/// Suitable for use inside <c>await foreach</c> loops driven by the consumer.
/// </summary>
public interface IAsyncInputDevice : IInputDevice
{
    /// <summary>
    /// Returns an asynchronous stream of input events. Iteration begins reading from the
    /// underlying source on first <c>MoveNextAsync</c>; the sequence completes when the source
    /// signals end-of-input or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <remarks>
    /// Whether multiple concurrent enumerations are supported is implementation-defined; most
    /// devices will deliver each event to a single consumer. Decorators forward iteration to
    /// the inner device, transforming or fabricating events as they pass through.
    /// </remarks>
    IAsyncEnumerable<InputEvent> ReadAllAsync(CancellationToken cancellationToken = default);
}
