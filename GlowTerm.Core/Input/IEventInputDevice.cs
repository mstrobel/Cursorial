namespace GlowTerm.Core.Input;

/// <summary>
/// An input device that delivers events through classic .NET delegate events. Subscribers
/// attach to <see cref="Input"/> (and optionally <see cref="Error"/> and <see cref="Completed"/>),
/// then call <see cref="StartAsync"/> to begin pumping events.
/// </summary>
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
