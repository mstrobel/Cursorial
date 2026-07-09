using System.Buffers;
using System.Buffers.Text;
using System.Text;

using Cursorial.Input.Events;
using Cursorial.Output;

// ReSharper disable RedundantLambdaParameterType

namespace Cursorial.UI;

/// <summary>
/// The OSC 52 clipboard service (design doc §5.9 / spec-controls "IClipboardService"; punch 30): a
/// focused editor's Copy/Cut routes a write through it, gated on the negotiated
/// <see cref="Cursorial.Output.Capabilities.OutputProtocolCapabilities.ClipboardWrite"/>. Writes are
/// fire-and-forget — most terminals gate the clipboard behind a user prompt and the protocol gives no
/// acknowledgement (see <see cref="Cursorial.Output.ClipboardWriter"/>).
/// <para>
/// <b>Reads</b> are the OSC 52 query/response round-trip: the query rides the sanctioned out-of-band
/// byte channel, the terminal's reply surfaces as a <c>DeviceResponseKind.Clipboard</c> device
/// response, and the pair is correlated with a timeout — a terminal that doesn't implement the query
/// (or whose user denies the permission prompt most readers gate it behind) simply never replies, so
/// <see cref="TryGetTextAsync"/> completes with <see langword="null"/> rather than hanging.
/// <see cref="CanRead"/> reflects the negotiated family gate ("the terminal implements the query",
/// not "a read will succeed"). The terminal's own paste — a <c>PasteEvent</c> surfaced by S3 as
/// <c>TextInput{FromPaste = true}</c> — remains the primary inbound path.
/// </para>
/// </summary>
public interface IClipboardService
{
    /// <summary>Whether the terminal honors OSC 52 writes (the negotiated capability).</summary>
    bool CanWrite { get; }

    /// <summary>Whether the terminal implements the OSC 52 read query (the negotiated capability).
    /// A <see langword="true"/> means a read is worth <em>attempting</em> — most terminals still gate
    /// the actual read behind a user prompt, and a denied read times out to <see langword="null"/>.</summary>
    bool CanRead { get; }

    /// <summary>Writes <paramref name="text"/> to the system clipboard (OSC 52). A no-op when <see cref="CanWrite"/> is false.</summary>
    void SetText(string text);

    /// <summary>Clears the system clipboard (an empty OSC 52 payload). A no-op when <see cref="CanWrite"/> is false.</summary>
    void Clear();

    /// <summary>
    /// Attempts an OSC 52 read: emits the query and completes with the decoded clipboard text when
    /// the terminal replies within <paramref name="timeout"/>, else <see langword="null"/> (never
    /// throws for an unsupported/denied read; completes immediately with <see langword="null"/> when
    /// <see cref="CanRead"/> is false). UI-thread affine — call from the dispatcher thread; the
    /// completion is delivered asynchronously.
    /// </summary>
    ValueTask<string?> TryGetTextAsync(TimeSpan timeout);
}

/// <summary>
/// The <see cref="UIApplication"/>-backed <see cref="IClipboardService"/>: writes are queued onto the
/// sanctioned out-of-band byte channel (<see cref="UIApplication.QueueControlSequence"/>, OSC-class only)
/// and emitted in the frame loop's Phase 6 after the delta. <see cref="CanWrite"/>/<see cref="CanRead"/>
/// read the live negotiated snapshot, so a re-negotiation is reflected without re-installing the service.
/// Reads register a one-shot device-response waiter (the S6 response router), arm a frame-aligned
/// <see cref="UITimer"/> for the timeout, and complete <see langword="null"/> on timeout or app shutdown —
/// a pending read can never outlive the loop that would deliver its answer.
/// </summary>
internal sealed class TerminalClipboardService(UIApplication application) : IClipboardService
{
    /// <inheritdoc/>
    public bool CanWrite => application.Capabilities.Output.Protocol.ClipboardWrite;

    /// <inheritdoc/>
    public bool CanRead => application.Capabilities.Output.Protocol.ClipboardRead;

    /// <inheritdoc/>
    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!CanWrite)
            return;

        // Capture the text (a string is immutable + safe to retain) and emit on the UI thread in Phase 6.
        application.QueueControlSequence(writer => ClipboardWriter.WriteSet(writer, text));
    }

    /// <inheritdoc/>
    public void Clear()
    {
        if (!CanWrite)
            return;

        application.QueueControlSequence(static (IBufferWriter<byte> writer) => ClipboardWriter.WriteClear(writer));
    }

    /// <inheritdoc/>
    public ValueTask<string?> TryGetTextAsync(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        application.Dispatcher.VerifyAccess(); // the timer rides the thread-ambient scheduler; the sink fires on this thread

        if (!CanRead)
            return new((string?) null);

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        IDisposable? sinkRegistration = null;
        UITimer? timer = null;
        EventHandler? onShutdown = null;

        // ReSharper disable AccessToModifiedClosure
        
        // Every completion path runs on the UI thread (response dispatch, timer tick, shutdown raise),
        // so TrySetResult's idempotence is the only guard needed; the first completer tears down all
        // three triggers so none dangles past the read.
        void Complete(string? result)
        {
            if (!tcs.TrySetResult(result))
                return;

            sinkRegistration?.Dispose();
            timer?.Dispose();
            application.BeginShutdown -= onShutdown;
        }

        // ReSharper restore AccessToModifiedClosure
        
        onShutdown = (_, _) => Complete(null); // teardown kills the timers that would otherwise fire the timeout
        application.BeginShutdown += onShutdown;

        sinkRegistration = application.RegisterDeviceResponseSink(response =>
        {
            if (response.Kind == DeviceResponseKind.Clipboard)
                Complete(DecodeClipboardPayload(response.Payload.Span));
        });

        timer = UITimer.Start(timeout, () => Complete(null));

        application.QueueControlSequence(static (IBufferWriter<byte> writer) => ClipboardWriter.WriteQuery(writer));

        return new ValueTask<string?>(tcs.Task);
    }

    /// <summary>
    /// Decodes a <c>DeviceResponseKind.Clipboard</c> payload — "<c>&lt;targets&gt;;&lt;base64&gt;</c>" —
    /// into text: <see langword="null"/> for a malformed body, a failed base64 decode, or a literal
    /// <c>?</c> data field (a terminal echoing the query rather than answering it); empty string for
    /// an empty selection.
    /// </summary>
    private static string? DecodeClipboardPayload(ReadOnlySpan<byte> payload)
    {
        var separator = payload.IndexOf((byte) ';');
        if (separator < 0)
            return null;

        var data = payload[(separator + 1)..];

        // Normalize into a padded, whitespace-free base64 buffer. Some terminals / tmux passthrough wrap the
        // payload across lines or omit the trailing '=' padding, and the strict Base64.DecodeFromUtf8 (final
        // block, no stray bytes) rejects both — filter ASCII whitespace and re-pad to a 4-byte multiple so a
        // real reply isn't misread as "no clipboard" (a false null indistinguishable from a denied read).
        var rented = ArrayPool<byte>.Shared.Rent(data.Length + 3);
        try
        {
            var n = 0;
            foreach (var b in data)
            {
                if (b is (byte) ' ' or (byte) '\t' or (byte) '\r' or (byte) '\n')
                    continue;
                rented[n++] = b;
            }

            if (n == 1 && rented[0] == (byte) '?')
                return null; // the terminal echoed the query rather than answering it (no read support / denied)
            if (n == 0)
                return string.Empty; // an empty selection

            switch (n % 4)
            {
                case 1:
                    ClipboardDecodeFailed(data); // a length ≡ 1 (mod 4) is not valid base64 at any padding
                    return null;
                case 2:
                    rented[n++] = (byte) '=';
                    rented[n++] = (byte) '=';
                    break;
                case 3:
                    rented[n++] = (byte) '=';
                    break;
            }

            var decoded = ArrayPool<byte>.Shared.Rent(Base64.GetMaxDecodedFromUtf8Length(n));
            try
            {
                if (Base64.DecodeFromUtf8(rented.AsSpan(0, n), decoded, out _, out var written) == OperationStatus.Done)
                    return Encoding.UTF8.GetString(decoded, 0, written);

                ClipboardDecodeFailed(data);
                return null;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(decoded);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>DEBUG-only breadcrumb separating "reply arrived but the base64 wouldn't decode" (a terminal-zoo
    /// quirk worth chasing) from an ordinary null (no reply / denied / unsupported). Compiled out of Release.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private static void ClipboardDecodeFailed(ReadOnlySpan<byte> data) =>
        System.Diagnostics.Debug.WriteLine(
            $"[Cursorial] OSC 52 clipboard reply failed to base64-decode ({data.Length} bytes): " +
            Encoding.ASCII.GetString(data));
}
