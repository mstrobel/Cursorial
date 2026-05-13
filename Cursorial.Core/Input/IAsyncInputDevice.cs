using Cursorial.Input.Events;

namespace Cursorial.Input;

/// <summary>
/// An input device whose events are consumed via an awaitable <see cref="IAsyncEnumerable{T}"/>.
/// Suitable for use inside <c>await foreach</c> loops driven by the consumer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> No explicit start is required; iteration begins driving the underlying
/// source on first <c>MoveNextAsync</c>. To stop, cancel the token passed to
/// <see cref="ReadAllAsync"/> or break out of the loop. <see cref="IAsyncDisposable.DisposeAsync"/>
/// releases all resources and is idempotent; pending iteration observes a clean completion
/// (no exception) once disposal is in progress.
/// </para>
/// <para>
/// <b>Multiple iterations.</b> Whether the same device may be enumerated more than once
/// (sequentially or concurrently) is implementation-defined. Devices backed by a non-restartable
/// source — most notably <see cref="System.IO.Pipelines.PipeReader"/> over stdin — are
/// single-shot: once an enumeration completes or its enumerator is disposed, calling
/// <see cref="ReadAllAsync"/> again throws <see cref="InvalidOperationException"/>. Devices
/// over restartable sources (in-memory traces, recorded files) MAY support repeated enumeration.
/// </para>
/// </remarks>
public interface IAsyncInputDevice : IInputDevice
{
    /// <summary>
    /// Returns an asynchronous stream of input events. Iteration begins reading from the
    /// underlying source on first <c>MoveNextAsync</c>; the sequence completes when the source
    /// signals end-of-input or <paramref name="cancellationToken"/> is canceled.
    /// </summary>
    IAsyncEnumerable<InputEvent> ReadAllAsync(CancellationToken cancellationToken = default);
}