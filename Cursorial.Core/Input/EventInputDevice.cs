namespace Cursorial.Core.Input;

/// <summary>
/// Push-style <see cref="IEventInputDevice"/> built on top of a pull-style
/// <see cref="IAsyncInputDevice"/>. Iterates the inner device on a background pump and raises
/// <see cref="Input"/> events as <see cref="InputEvent"/>s arrive.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> The wrapper takes ownership of the supplied
/// <see cref="IAsyncInputDevice"/>; <see cref="DisposeAsync"/> disposes the inner device. If
/// you need to retain the inner device past the wrapper's lifetime, manage it yourself with
/// a different adapter.
/// </para>
/// <para>
/// <b>Restartability.</b> The wrapper is single-shot because <see cref="IAsyncInputDevice"/>
/// is single-shot: <see cref="StartAsync"/> may be called once. <see cref="StopAsync"/>
/// terminates the pump and cancels the inner enumeration; a subsequent <see cref="StartAsync"/>
/// throws <see cref="InvalidOperationException"/>.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Input"/> handlers are invoked on the pump thread (a
/// <see cref="Task.Run(Action)"/> worker) in the order events arrive from the inner device.
/// Long-running handler work should be marshaled off the pump so it does not stall delivery.
/// Capability-flow follows the inner device's <see cref="IInputDevice.Capabilities"/>;
/// decorators that change capabilities are expected to do so at the
/// <see cref="IAsyncInputDevice"/> layer beneath this wrapper.
/// </para>
/// </remarks>
public sealed class EventInputDevice : IEventInputDevice
{
    private readonly IAsyncInputDevice _inner;
    private readonly object _stateLock = new();

    private Task? _pumpTask;
    private CancellationTokenSource? _pumpCts;
    private int _disposed;
    private int _startAttempted;

    public EventInputDevice(IAsyncInputDevice inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc/>
    public InputCapabilities Capabilities => _inner.Capabilities;

    /// <inheritdoc/>
    public event EventHandler<InputEvent>? Input;

    /// <inheritdoc/>
    public event EventHandler<Exception>? Error;

    /// <inheritdoc/>
    public event EventHandler? Completed;

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (Interlocked.Exchange(ref _startAttempted, 1) != 0)
        {
            throw new InvalidOperationException(
                "EventInputDevice.StartAsync was already called on this instance. The underlying " +
                "IAsyncInputDevice is single-shot; create a new EventInputDevice wrapping a fresh " +
                "inner device to enumerate again.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        lock (_stateLock)
        {
            _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _pumpCts.Token;
            // Do NOT pass the token to Task.Run — a cancellation that races the scheduler can
            // abort the task before PumpAsync runs, skipping the Completed event entirely. We
            // want PumpAsync to always execute so its finally block raises Completed; the
            // token's role is to drive the inner await foreach out of its wait.
            _pumpTask = Task.Run(() => PumpAsync(token));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? pump;
        CancellationTokenSource? cts;
        lock (_stateLock)
        {
            pump = _pumpTask;
            cts = _pumpCts;
        }

        if (pump is null) return; // never started — nothing to stop.

        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* raced with disposal */ }

        try { await pump.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected */ }
        catch { /* pump errors are surfaced via the Error event */ }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Stop the pump if it ran. Use a private CT so a caller-cancelled token doesn't abort
        // the shutdown sequence partway through.
        await StopAsync(CancellationToken.None).ConfigureAwait(false);

        try { await _inner.DisposeAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }

        lock (_stateLock)
        {
            _pumpCts?.Dispose();
            _pumpCts = null;
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var inputEvent in _inner.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                RaiseInput(inputEvent);
            }
        }
        catch (OperationCanceledException)
        {
            // Stop/dispose requested — fall through to Completed.
        }
        catch (Exception ex)
        {
            RaiseError(ex);
        }
        finally
        {
            RaiseCompleted();
        }
    }

    private void RaiseInput(InputEvent inputEvent)
    {
        // Snapshot the delegate so a concurrent unsubscribe doesn't miss the in-flight raise.
        var handler = Input;
        if (handler is null) return;

        try { handler(this, inputEvent); }
        catch
        {
            // A faulty handler must not break the pump. Errors from handlers are swallowed;
            // applications wanting visibility should add their own try/catch inside the handler.
        }
    }

    private void RaiseError(Exception ex)
    {
        var handler = Error;
        if (handler is null) return;
        try { handler(this, ex); } catch { /* swallow */ }
    }

    private void RaiseCompleted()
    {
        var handler = Completed;
        if (handler is null) return;
        try { handler(this, EventArgs.Empty); } catch { /* swallow */ }
    }
}
