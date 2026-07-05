using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Cursorial.Input.Capabilities;
using Cursorial.Input.Events;
using Cursorial.Input.Parsing;

// ReSharper disable ChangeFieldTypeToSystemThreadingLock

namespace Cursorial.Input;

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
/// owns the resolution: if no further bytes arrive within <em>escapeAmbiguityTimeout</em>
/// (default 50&#xa0;ms — the xterm convention), it calls <see cref="VtSequenceClassifier.Flush"/>
/// to commit the bare ESC as an Escape event. The ambiguity timer is armed only while the
/// classifier actually holds a partial sequence (<see cref="VtSequenceClassifier.HasPendingSequence"/>);
/// an idle pump parks on the pipe read alone and never wakes on the quiet window.
/// </para>
/// <para>
/// <b>Single-shot.</b> The device is single-shot: <see cref="ReadAllAsync"/> may be called
/// once. A second call throws <see cref="InvalidOperationException"/>; calling after disposal
/// throws <see cref="ObjectDisposedException"/>.
/// </para>
/// </remarks>
public sealed class VtInputDevice : IAsyncInputDevice
{
    /// <summary>The xterm convention for resolving bare-ESC vs. CSI/SS3/Alt+key ambiguity.</summary>
    public static TimeSpan DefaultEscapeAmbiguityTimeout { get; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// How long <see cref="DisposeAsync"/> waits for the pump to exit before abandoning it.
    /// Only engages for a reader that honors neither the cancellation token nor
    /// <see cref="System.IO.Pipelines.PipeReader.CancelPendingRead"/>; matches the 2-second
    /// bound the <c>TerminalSession</c> signal path applies to full disposal.
    /// </summary>
    private static readonly TimeSpan PumpAbandonTimeout = TimeSpan.FromSeconds(2);

    private readonly IInputByteSource _source;
    private readonly VtInputMode _mode;
    private readonly TimeProvider _time;
    private readonly TimeSpan _escapeAmbiguityTimeout;

    // The classifier is constructed privately and not exposed. X10 mouse framing on the
    // classifier is synced from VtInputMode.MouseEncoding inside PumpAsync each batch.
    private readonly VtSequenceClassifier _classifier = new();
    private readonly VtInputInterpreter _interpreter;

    // SingleWriter is false because EnqueueExternalEvent allows other components (e.g., a SIGWINCH
    // resize monitor wired up by TerminalSession) to push events alongside the byte pump.
    private readonly Channel<InputEvent> _channel = Channel.CreateUnbounded<InputEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly object _startLock = new();
    private Task? _pumpTask;
    private CancellationTokenSource? _pumpCts;

    // Pump pause/resume plumbing. Used by TerminalSession.Renegotiate to clear the device's
    // reader of contention so a fresh negotiator can read probe responses without colliding
    // with the device pump's ReadAsync. The pump parks on _pumpResumeEvent in user space; no
    // libc syscall is pending while paused.
    private readonly ManualResetEventSlim _pumpResumeEvent = new(initialState: true);
    private TaskCompletionSource? _pumpParkedTcs;
    private int _pumpPaused;

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

        _interpreter = new VtInputInterpreter(_mode, new ChannelEventSink(_channel.Writer, this), _time);
    }

    /// <summary>
    /// Side-channel hook fired whenever a <see cref="DeviceResponseEvent"/> reaches the device.
    /// Subscribers see a *copy* of the event before consumers iterate the main event stream;
    /// the event is also written to the consumer-facing channel as usual. Used by the Windows
    /// non-console resize monitor to observe <c>CSI 18 t</c> responses without competing with
    /// the consumer for the input channel. Internal because it bypasses the public
    /// pull-based contract.
    /// </summary>
    internal event Action<DeviceResponseEvent>? DeviceResponseEmitted;

    private void RaiseDeviceResponse(DeviceResponseEvent inputEvent)
    {
        var handler = DeviceResponseEmitted;
        if (handler is null) return;
        try { handler(inputEvent); }
        catch { /* subscriber faults must not poison the input pump */ }
    }

    /// <inheritdoc/>
    public InputCapabilities Capabilities { get; }

    /// <summary>
    /// The mode bag the interpreter reads from. Shared with the negotiator so opt-in changes
    /// observed by the negotiator are visible to ongoing decoding.
    /// </summary>
    public VtInputMode Mode => _mode;

    /// <summary>
    /// Returns a string representation of the <see cref="VtInputDevice"/> instance,
    /// including the source type.
    /// </summary>
    /// <returns>
    /// A string that represents the current <see cref="VtInputDevice"/> object.
    /// </returns>
    public override string ToString() => $"{nameof(VtInputDevice)}({_source.GetType().Name})";

    /// <summary>
    /// Push an externally-observed <see cref="InputEvent"/> into the device's event stream.
    /// Intended for sources that aren't part of the byte pump — terminal resize via SIGWINCH,
    /// out-of-band IME composition, application-fabricated events for testing. After disposal
    /// the call is a no-op.
    /// </summary>
    /// <remarks>
    /// Safe to call concurrently with the byte pump. The device's channel is configured for
    /// multiple writers; <see cref="ChannelWriter{T}.TryWrite"/> on the unbounded channel
    /// never blocks and never fails for live channels.
    /// </remarks>
    public void EnqueueExternalEvent(InputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        if (Volatile.Read(ref _disposed) != 0) return;
        _channel.Writer.TryWrite(inputEvent);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<InputEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(VtInputDevice));

        if (Interlocked.Exchange(ref _enumerationStarted, 1) != 0)
        {
            throw new InvalidOperationException(
                "ReadAllAsync was already called on this device. " +
                "VtInputDevice is single-shot; create a new instance to re-enumerate.");
        }

        EnsurePumpStarted();

        await foreach (var inputEvent in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return inputEvent;
    }

    /// <summary>
    /// Park the device's background pump so a separate consumer (typically the renegotiation
    /// handshake) can take exclusive ownership of the underlying <see cref="IInputByteSource.Reader"/>
    /// without colliding with the pump's <c>ReadAsync</c>. The returned task completes once the
    /// pump has parked entirely in user space — no read is pending on the source's pipe reader.
    /// The classifier is reset to ground state at park time so post-resume byte stream is parsed
    /// from a clean context.
    /// </summary>
    /// <remarks>
    /// Internal — only callable from inside the assembly. The contract is paired with
    /// <see cref="ResumePump"/>; callers must always re-balance pause/resume on every code path
    /// or the channel consumer will starve indefinitely.
    /// </remarks>
    internal async Task PausePumpAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(VtInputDevice));

        cancellationToken.ThrowIfCancellationRequested();

        Task? parkedTask;
        lock (_startLock)
        {
            if (_pumpTask is null)
            {
                // Pump hasn't started yet (no consumer has called ReadAllAsync). Just record
                // the requested pause state — when the pump starts it will park immediately
                // on its first iteration.
                _pumpResumeEvent.Reset();
                Volatile.Write(ref _pumpPaused, 1);
                return;
            }

            if (Volatile.Read(ref _pumpPaused) != 0)
            {
                // Already paused or pausing — share the existing TCS.
                parkedTask = _pumpParkedTcs?.Task ?? Task.CompletedTask;
            }
            else
            {
                _pumpResumeEvent.Reset();
                _pumpParkedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _pumpPaused, 1);
                parkedTask = _pumpParkedTcs.Task;
            }
        }

        // Wake the pump if it's currently blocked in PipeReader.ReadAsync. CancelPendingRead is
        // safe even when no read is in flight — it stores the cancel for the next ReadAsync,
        // which we don't issue while paused. The pump's first read after resume returns canceled
        // with an empty buffer; the classifier no-ops on an empty span.
        _source.Reader.CancelPendingRead();

        await parkedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resume a previously paused pump. Safe to call when the pump isn't paused (no-op). The
    /// pump task is already running; resume just signals the manual-reset event it's blocked on.
    /// </summary>
    internal void ResumePump()
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        lock (_startLock)
        {
            Volatile.Write(ref _pumpPaused, 0);
            _pumpParkedTcs = null;
            _pumpResumeEvent.Set();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Stop the pump if it was started.
        Task? pumpTask;
        CancellationTokenSource? pumpCts;
        TaskCompletionSource? parkedTcs;

        lock (_startLock)
        {
            pumpTask = _pumpTask;
            pumpCts = _pumpCts;
            parkedTcs = _pumpParkedTcs;
        }

        // Release a parked pump so it can observe cancellation and exit. The MRE doubles as
        // wake-on-dispose since the pump's Wait(cancellationToken) honors the cancellation token
        // too, but setting the event also covers the race where Cancel arrives just before Wait.
        _pumpResumeEvent.Set();
        parkedTcs?.TrySetException(new ObjectDisposedException(nameof(VtInputDevice)));

        try
        {
            // ReSharper disable once MethodHasAsyncOverload
            pumpCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed via another path.
        }

        // An idle (Ground-state) pump parks directly on ReadAsync with no ambiguity timer
        // armed, so token cancellation is the primary wake and CancelPendingRead the second
        // lever for readers whose in-flight read observes it independently of the token.
        try
        {
            _source.Reader.CancelPendingRead();
        }
        catch
        {
            // The source may already be torn down by the owner; waking the pump is best-effort.
        }

        // Make sure consumer iterations terminate even if no pump ever ran.
        _channel.Writer.TryComplete();

        if (pumpTask is not null)
        {
            try
            {
                // Bounded: the previously always-armed ambiguity timer doubled as a liveness
                // backstop for readers that honor neither cancellation lever; with the timer
                // now conditional, such a reader would otherwise park this await until its
                // next byte. Mainline Pipe-backed sources exit promptly — on timeout the pump
                // is abandoned (the channel is already completed, so consumers terminate, and
                // the parked read dies when the owner closes the transport).
                await pumpTask.WaitAsync(PumpAbandonTimeout).ConfigureAwait(false);
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
        _pumpResumeEvent.Dispose();
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
            // ReSharper disable once MethodSupportsCancellation
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
                // Park at the top of each iteration when paused so we never hold an in-flight
                // ReadAsync on _source.Reader. A separate consumer (renegotiation handshake)
                // can then take exclusive ownership of the reader.
                if (Volatile.Read(ref _pumpPaused) != 0)
                {
                    // Reset the classifier before parking. A separate reader is about to
                    // consume bytes we won't see; if those bytes complete or invalidate a
                    // sequence the device's classifier had partially parsed, leaving the
                    // classifier in a non-Ground state would cause the post-resume byte
                    // stream to be parsed in the wrong context. Losing a partial in-flight
                    // sequence at this moment is acceptable (renegotiation is rare); producing
                    // garbage events is not.
                    _classifier.Reset();

                    var parked = _pumpParkedTcs;
                    parked?.TrySetResult();

                    try
                    {
                        _pumpResumeEvent.Wait(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (cancellationToken.IsCancellationRequested) break;

                    // The pending read (if any) was cancelled by the pauser via
                    // CancelPendingRead; drop our reference so the next iteration issues a
                    // fresh one.
                    pendingRead = null;
                    continue;
                }

                pendingRead ??= _source.Reader.ReadAsync(cancellationToken).AsTask();

                // Arm the ambiguity timer only while the classifier holds a partial sequence —
                // Flush on a ground-state classifier is a no-op, so an idle pump parks on the
                // pipe read alone instead of waking every quiet window for nothing.
                if (_classifier.HasPendingSequence)
                {
                    var timeoutTask = Task.Delay(_escapeAmbiguityTimeout, _time, cancellationToken);
                    var completed = await Task.WhenAny(pendingRead, timeoutTask).ConfigureAwait(false);

                    if (completed != pendingRead)
                    {
                        // Idle window elapsed — commit the pending sequence (bare ESC → Escape
                        // key, truncated sequence → recover-or-drop). The pendingRead task
                        // remains in flight; we'll await it on the next iteration.
                        _classifier.Flush(_interpreter);
                        continue;
                    }
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

                // Mirror VtInputMode.MouseEncoding onto the classifier's X10 framing flag.
                // The mode is mutable from outside (negotiator, app code), so we re-sync each
                // batch rather than only at startup; the comparison is a single field read.
                _classifier.X10MouseFramingEnabled = _mode.MouseEncoding == MouseEncoding.X10;

                // Likewise mirror the legacy 8-bit-meta input opt-in (off by default).
                _classifier.EightBitMetaEnabled = _mode.EightBitMetaInput;

                var buffer = result.Buffer;

                foreach (var segment in buffer)
                    _classifier.Process(segment.Span, _interpreter);

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
        private readonly VtInputDevice _owner;

        public ChannelEventSink(ChannelWriter<InputEvent> writer, VtInputDevice owner)
        {
            _writer = writer;
            _owner = owner;
        }

        public void OnInputEvent(InputEvent inputEvent)
        {
            // Unbounded channel — TryWrite never returns false except after Complete.
            _writer.TryWrite(inputEvent);

            if (inputEvent is DeviceResponseEvent r)
                _owner.RaiseDeviceResponse(r);
        }
    }
}