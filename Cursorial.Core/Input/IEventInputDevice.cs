using Cursorial.Input.Events;

namespace Cursorial.Input;

/// <summary>
/// An input device that delivers events through classic .NET delegate events. Subscribers
/// attach to <see cref="Input"/> (and optionally <see cref="Error"/> and <see cref="Completed"/>),
/// then call <see cref="StartAsync"/> to begin pumping events.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> A device is created in the stopped state. <see cref="StartAsync"/> begins
/// pumping; <see cref="StopAsync"/> halts it without releasing the device. Whether
/// <see cref="StartAsync"/> may be called again after <see cref="StopAsync"/> is
/// implementation-defined: devices over a restartable source MAY support it; devices over a
/// non-restartable source (e.g. a <see cref="System.IO.Pipelines.PipeReader"/> that has been
/// completed) MUST throw <see cref="InvalidOperationException"/> from <see cref="StartAsync"/>
/// in that case.
/// </para>
/// <para>
/// <b>Disposal.</b> <see cref="IAsyncDisposable.DisposeAsync"/> implicitly stops the pump if
/// running and releases all resources. After disposal the device is unusable; further calls to
/// <see cref="StartAsync"/> throw <see cref="ObjectDisposedException"/>. Disposal is idempotent.
/// </para>
/// <para>
/// <b>Subscription thread-safety.</b> <c>+=</c>/<c>-=</c> on <see cref="Input"/>,
/// <see cref="Error"/>, and <see cref="Completed"/> are safe to call concurrently with the
/// pump (standard .NET event semantics). A handler attached after <see cref="StartAsync"/>
/// will see all events delivered after subscription; events that occurred earlier are not
/// replayed.
/// </para>
/// </remarks>
public interface IEventInputDevice : IInputDevice
{
    /// <summary>
    /// Raised for each input event produced by the device. Handlers run on the device's
    /// internal pump; long-running work should be marshalled off the handler so it does not
    /// stall further input delivery.
    /// </summary>
    event EventHandler<InputEvent>? Input;

    /// <summary>
    /// Raised when an unrecoverable error terminates the input pump. After this fires no
    /// further <see cref="Input"/> events will be raised; <see cref="Completed"/> will follow.
    /// </summary>
    event EventHandler<Exception>? Error;

    /// <summary>
    /// Raised once when the device has stopped producing events — because the underlying
    /// source ended, <see cref="StopAsync"/> was called, or an unrecoverable error occurred.
    /// </summary>
    event EventHandler? Completed;

    /// <summary>
    /// Begins pumping events to subscribers. Calling this on an already-running device is a no-op.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops pumping events to subscribers and releases any pump-specific resources. The device
    /// remains valid; <see cref="StartAsync"/> may be called again to resume.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
