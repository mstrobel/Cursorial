using System.Buffers;
using System.IO.Pipelines;

using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Terminal;

namespace Cursorial.UI.Testing;

/// <summary>
/// The headless <see cref="ITerminalHost"/> (design doc §10.10): scripted capabilities over an
/// in-memory output sink (byte capture for <c>UITestHost.LastFrameBytes</c>) and a <b>real</b>
/// <see cref="VtInputDevice"/> over an in-memory byte source constructed on the supplied
/// <see cref="TimeProvider"/> — interpreter event timestamps and the bare-ESC ambiguity timer live
/// on the same (fake) clock as gesture thresholds. Synchronous to create: no probes, no TTY, no
/// negotiation.
/// </summary>
public sealed class SyntheticTerminalHost : ITerminalHost
{
    private readonly Pipe _outputPipe;
    private readonly Pipe _inputPipe;
    private readonly TrackingInputSource _inputSource;
    private readonly VtInputDevice _inputDevice;
    private readonly (int Columns, int Rows) _initialSize;
    private long _inputBytesWritten;
    private byte[] _finalOutput = [];
    private TerminalCapabilities? _scriptedRenegotiation;
    private bool _disposed;

    /// <summary>
    /// Creates the host. <paramref name="timeProvider"/> is the single clock domain — pass the
    /// test's fake clock so parser timing is deterministic (a trailing lone ESC commits only when
    /// the clock crosses the ambiguity window, never on the wall clock).
    /// </summary>
    public SyntheticTerminalHost(
        TerminalCapabilities capabilities,
        Size initialSize,
        TimeProvider? timeProvider = null,
        TimeSpan? escapeAmbiguityTimeout = null)
    {
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _initialSize = (initialSize.Columns, initialSize.Rows);

        // pauseWriterThreshold 0 ⇒ FlushAsync always completes synchronously: the frame loop's
        // one blocking flush per frame never stalls on an unread in-memory pipe.
        var options = new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0, useSynchronizationContext: false);
        _outputPipe = new Pipe(options);
        _inputPipe = new Pipe(options);
        _inputSource = new TrackingInputSource(_inputPipe.Reader);
        _inputDevice = new VtInputDevice(
            _inputSource,
            capabilities.Input,
            timeProvider: timeProvider,
            escapeAmbiguityTimeout: escapeAmbiguityTimeout);
        Output = new PipeOutputSink(_outputPipe.Writer);
    }

    /// <inheritdoc/>
    public TerminalCapabilities Capabilities { get; private set; }

    /// <inheritdoc/>
    public IAsyncInputDevice Input => _inputDevice;

    /// <inheritdoc/>
    public IOutputByteSink Output { get; }

    /// <inheritdoc/>
    public bool OwnsSignalHandling => false;

    /// <summary>Total bytes injected through <see cref="WriteInputBytes"/>.</summary>
    public long InputBytesWritten => Volatile.Read(ref _inputBytesWritten);

    /// <summary>Total injected bytes the parser has consumed — <c>DrainParsedInputAsync</c>'s first leg.</summary>
    public long InputBytesConsumed => _inputSource.BytesConsumed;

    /// <inheritdoc/>
    public ValueTask<(int Columns, int Rows)?> QuerySizeAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<(int, int)?>(_initialSize);

    /// <summary>
    /// Scripts the snapshot the <b>next</b> <see cref="RenegotiateAsync"/> realizes — the headless
    /// stand-in for a terminal whose capabilities change across renegotiation (capability-gate
    /// flip tests). Single-shot: the script is consumed by one renegotiation; without a script,
    /// renegotiation keeps the current snapshot (scripted capabilities never change on their own).
    /// </summary>
    public void ScriptRenegotiatedCapabilities(TerminalCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        _scriptedRenegotiation = capabilities;
    }

    /// <inheritdoc/>
    public ValueTask RenegotiateAsync(CancellationToken cancellationToken = default)
    {
        if (_scriptedRenegotiation is {} scripted)
        {
            Capabilities = scripted;
            _scriptedRenegotiation = null;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The number of times <see cref="ReapplyScreenLocalOptInsAsync"/> has been called — lets a test
    /// assert the application re-applies screen-local opt-ins exactly once after entering the alt
    /// screen (and not at all when no alt screen was entered). Headless has no real Kitty stack, so
    /// the call itself is a no-op.
    /// </summary>
    public int ReapplyScreenLocalOptInsCount { get; private set; }

    /// <inheritdoc/>
    public ValueTask ReapplyScreenLocalOptInsAsync(CancellationToken cancellationToken = default)
    {
        ReapplyScreenLocalOptInsCount++;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Injects raw wire bytes into the parser path (the <c>UITestHost.SendBytes</c> substrate).
    /// Synchronous — the in-memory pipe never back-pressures.
    /// </summary>
    public void WriteInputBytes(ReadOnlySpan<byte> rawBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _inputPipe.Writer.Write(rawBytes);
        Interlocked.Add(ref _inputBytesWritten, rawBytes.Length);
        var flush = _inputPipe.Writer.FlushAsync();
        if (!flush.IsCompleted)
            flush.AsTask().GetAwaiter().GetResult(); // cannot happen with threshold 0; defensive
    }

    /// <summary>
    /// Completes the input byte stream — the parser observes EOF, the application's pump ends, and
    /// the frame loop initiates shutdown (the "terminal closed" path).
    /// </summary>
    public void CompleteInput() => _inputPipe.Writer.Complete();

    /// <summary>
    /// Drains everything the application has written to the output sink since the last call.
    /// Returns the captured bytes (empty when nothing was emitted).
    /// </summary>
    public byte[] DrainOutput()
    {
        if (_disposed)
            return [];

        if (!_outputPipe.Reader.TryRead(out var result))
            return [];

        var buffer = result.Buffer;
        var captured = buffer.ToArray();
        _outputPipe.Reader.AdvanceTo(buffer.End);
        return captured;
    }

    /// <summary>
    /// The bytes the application wrote between the last <see cref="DrainOutput"/> and disposal —
    /// the canonical-teardown sequence (renderer close, show cursor, SGR reset, leave alt screen),
    /// captured by <see cref="DisposeAsync"/> before the sink closes.
    /// </summary>
    public ReadOnlyMemory<byte> FinalOutput => _finalOutput;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _finalOutput = DrainOutput(); // capture the teardown bytes before the sink closes
        _disposed = true;
        await _inputPipe.Writer.CompleteAsync().ConfigureAwait(false); // EOF to the parser
        await _inputDevice.DisposeAsync().ConfigureAwait(false);
        await _outputPipe.Writer.CompleteAsync().ConfigureAwait(false);
        await _outputPipe.Reader.CompleteAsync().ConfigureAwait(false);
    }

    /// <summary>The in-memory output sink. Disposal completes the pipe writer (sink-owned completion).</summary>
    private sealed class PipeOutputSink(PipeWriter writer) : IOutputByteSink
    {
        public PipeWriter Writer => writer;

        public ValueTask DisposeAsync() => writer.CompleteAsync();
    }

    /// <summary>
    /// An <see cref="IInputByteSource"/> over the in-memory pipe that tracks the parser's consumed
    /// offset, making "all injected bytes have been parsed" deterministically observable.
    /// </summary>
    private sealed class TrackingInputSource(PipeReader inner) : IInputByteSource
    {
        private readonly TrackingReader _reader = new(inner);

        public PipeReader Reader => _reader;

        public long BytesConsumed => _reader.BytesConsumed;

        public ValueTask DisposeAsync() => inner.CompleteAsync();

        private sealed class TrackingReader(PipeReader inner) : PipeReader
        {
            private ReadOnlySequence<byte> _lastBuffer;
            private long _consumed;

            public long BytesConsumed => Volatile.Read(ref _consumed);

            public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
            {
                var result = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
                _lastBuffer = result.Buffer;
                return result;
            }

            public override bool TryRead(out ReadResult result)
            {
                var read = inner.TryRead(out result);
                if (read)
                    _lastBuffer = result.Buffer;
                return read;
            }

            public override void AdvanceTo(SequencePosition consumed)
            {
                Interlocked.Add(ref _consumed, _lastBuffer.GetOffset(consumed));
                inner.AdvanceTo(consumed);
            }

            public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
            {
                Interlocked.Add(ref _consumed, _lastBuffer.GetOffset(consumed));
                inner.AdvanceTo(consumed, examined);
            }

            public override void CancelPendingRead() => inner.CancelPendingRead();

            public override void Complete(Exception? exception = null) => inner.Complete(exception);
        }
    }
}
