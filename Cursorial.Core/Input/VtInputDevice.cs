using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Cursorial.Core.Input.Parsing;

namespace Cursorial.Core.Input;

/// <summary>
/// Concrete <see cref="IAsyncInputDevice"/> built on top of an <see cref="IInputByteSource"/>
/// and the VT/ANSI parser pipeline. Reads bytes from the source on a background pump, drives
/// them through a <see cref="VtSequenceClassifier"/> + <see cref="VtInputInterpreter"/>, and
/// surfaces the produced <see cref="InputEvent"/>s to the consumer via an asynchronous
/// channel.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> The device does NOT take ownership of the supplied
/// <see cref="IInputByteSource"/>. Disposal stops the pump and completes the consumer's
/// <see cref="IAsyncEnumerable{T}"/> but leaves the byte source alone — the caller (or the
/// containing <c>TerminalSession</c>) is responsible for closing transports.
/// </para>
/// <para>
/// <b>Bare-ESC ambiguity.</b> The classifier holds a lone ESC pending until it knows whether
/// it's the start of a sequence (CSI / SS3 / Alt+key) or an Escape keypress. This device
/// owns the resolution: if no further bytes arrive within
/// <paramref name="escapeAmbiguityTimeout"/> (default 50&#xa0;ms — the xterm convention), it
/// calls <see cref="VtSequenceClassifier.Flush"/> to commit the bare ESC as an Escape event.
/// </para>
/// <para>
/// <b>Single-shot.</b> The device is single-shot: <see cref="ReadAllAsync"/> may be called
/// once. A second call throws <see cref="InvalidOperationException"/>; calling after disposal
/// throws <see cref="ObjectDisposedException"/>.
/// </para>
/// </remarks>
public sealed class VtInputDevice : IAsyncInputDevice
{
    /// <summary>The xterm convention for resolving bare-ESC vs CSI/SS3/Alt+key ambiguity.</summary>
    public static TimeSpan DefaultEscapeAmbiguityTimeout { get; } = TimeSpan.FromMilliseconds(50);

    private readonly IInputByteSource _source;
    private readonly VtInputMode _mode;
    private readonly TimeProvider _time;
    private readonly TimeSpan _escapeAmbiguityTimeout;

    // The classifier is constructed privately and not exposed. X10 mouse framing on the
    // classifier is therefore unreachable through this device — if a future negotiator path
    // grows an X10 opt-in, mirror VtInputMode.MouseEncoding == X10 onto
    // _classifier.X10MouseFramingEnabled here.
    private readonly VtSequenceClassifier _classifier = new();
    private readonly VtInputInterpreter _interpreter;

    private readonly Channel<InputEvent> _channel = Channel.CreateUnbounded<InputEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly object _startLock = new();
    private Task? _pumpTask;
    private CancellationTokenSource? _pumpCts;

    private int _enumerationStarted;
    private int _disposed;

    public VtInputDevice(
        IInputByteSource source,
        InputCapabilities capabilities,
        VtInputMode? mode = null,
        TimeProvider? timeProvider = null,
        TimeSpan? escapeAmbiguityTimeout = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _mode = mode ?? new VtInputMode();
        _time = timeProvider ?? TimeProvider.System;
        _escapeAmbiguityTimeout = escapeAmbiguityTimeout ?? DefaultEscapeAmbiguityTimeout;

        if (_escapeAmbiguityTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(escapeAmbiguityTimeout),
                _escapeAmbiguityTimeout,
                "Escape-ambiguity timeout must be positive.");
        }

        _interpreter = new VtInputInterpreter(_mode, new ChannelEventSink(_channel.Writer), _time);
    }

    /// <inheritdoc/>
    public InputCapabilities Capabilities { get; }

    /// <summary>
    /// The mode bag the interpreter reads from. Shared with the negotiator so opt-in changes
    /// observed by the negotiator are visible to ongoing decoding.
    /// </summary>
    public VtInputMode Mode => _mode;

    /// <inheritdoc/>
    public async IAsyncEnumerable<InputEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(VtInputDevice));
        }

        if (Interlocked.Exchange(ref _enumerationStarted, 1) != 0)
        {
            throw new InvalidOperationException(
                "ReadAllAsync was already called on this device. " +
                "VtInputDevice is single-shot; create a new instance to re-enumerate.");
        }

        EnsurePumpStarted();

        await foreach (var inputEvent in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return inputEvent;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Stop the pump if it was started.
        Task? pumpTask;
        CancellationTokenSource? pumpCts;
        lock (_startLock)
        {
            pumpTask = _pumpTask;
            pumpCts = _pumpCts;
        }

        try
        {
            pumpCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed via another path.
        }

        // Make sure consumer iterations terminate even if no pump ever ran.
        _channel.Writer.TryComplete();

        if (pumpTask is not null)
        {
            try
            {
                await pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
            catch
            {
                // Pump errors are surfaced through the channel; further suppression here.
            }
        }

        pumpCts?.Dispose();
    }

    private void EnsurePumpStarted()
    {
        lock (_startLock)
        {
            // Re-check disposed inside the lock. Without this, a DisposeAsync running between
            // the caller's outer check and this method's entry can leave us starting a pump
            // whose cancellation source nobody will trigger.
            if (Volatile.Read(ref _disposed) != 0) return;
            if (_pumpTask is not null) return;

            _pumpCts = new CancellationTokenSource();
            var token = _pumpCts.Token;
            _pumpTask = Task.Run(() => PumpAsync(token));
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        Task<System.IO.Pipelines.ReadResult>? pendingRead = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                pendingRead ??= _source.Reader.ReadAsync(cancellationToken).AsTask();

                var timeoutTask = Task.Delay(_escapeAmbiguityTimeout, _time, cancellationToken);
                var completed = await Task.WhenAny(pendingRead, timeoutTask).ConfigureAwait(false);

                if (completed != pendingRead)
                {
                    // Idle window elapsed — commit any pending bare-ESC. The pendingRead task
                    // remains in flight; we'll await it on the next iteration.
                    _classifier.Flush(_interpreter);
                    continue;
                }

                System.IO.Pipelines.ReadResult result;
                try
                {
                    result = await pendingRead.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                finally
                {
                    pendingRead = null;
                }

                var buffer = result.Buffer;
                foreach (var segment in buffer)
                {
                    _classifier.Process(segment.Span, _interpreter);
                }
                _source.Reader.AdvanceTo(buffer.End);

                if (result.IsCompleted) break;
            }
        }
        catch (OperationCanceledException)
        {
            // Outer cancellation — clean shutdown.
        }
        catch (Exception ex)
        {
            // Surface unexpected errors to the consumer via the channel before completing.
            _channel.Writer.TryComplete(ex);
            return;
        }
        finally
        {
            // Best-effort final flush — captures any lone-ESC that happened to be pending
            // when the source completed or shutdown began.
            try
            {
                _classifier.Flush(_interpreter);
            }
            catch
            {
                // Ignored — we're tearing down.
            }
        }

        _channel.Writer.TryComplete();
    }

    private sealed class ChannelEventSink : IInputEventSink
    {
        private readonly ChannelWriter<InputEvent> _writer;

        public ChannelEventSink(ChannelWriter<InputEvent> writer) => _writer = writer;

        public void OnInputEvent(InputEvent inputEvent)
        {
            // Unbounded channel — TryWrite never returns false except after Complete.
            _writer.TryWrite(inputEvent);
        }
    }
}
