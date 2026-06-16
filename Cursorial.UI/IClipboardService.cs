using System.Buffers;
using Cursorial.Output;

namespace Cursorial.UI;

/// <summary>
/// The OSC 52 clipboard service (design doc §5.9 / spec-controls "IClipboardService"; punch 30): a
/// focused editor's Copy/Cut routes a write through it, gated on the negotiated
/// <see cref="Cursorial.Output.Capabilities.OutputProtocolCapabilities.ClipboardWrite"/>. Writes are
/// fire-and-forget — most terminals gate the clipboard behind a user prompt and the protocol gives no
/// acknowledgement (see <see cref="Cursorial.Output.ClipboardWriter"/>).
/// <para>
/// <b>Reads</b> (the OSC 52 query/response round-trip) are not negotiated by any terminal family yet, so
/// <see cref="CanRead"/> is <see langword="false"/> in v1 and <see cref="TryGetTextAsync"/> completes with
/// <see langword="null"/>. The supported inbound path is the terminal's own paste — a <c>PasteEvent</c>
/// surfaced by S3 as <c>TextInput{FromPaste = true}</c> — which the editor's text-input handler absorbs.
/// </para>
/// </summary>
public interface IClipboardService
{
    /// <summary>Whether the terminal honors OSC 52 writes (the negotiated capability).</summary>
    bool CanWrite { get; }

    /// <summary>Whether OSC 52 reads are available. Always <see langword="false"/> in v1 (no family negotiates it).</summary>
    bool CanRead { get; }

    /// <summary>Writes <paramref name="text"/> to the system clipboard (OSC 52). A no-op when <see cref="CanWrite"/> is false.</summary>
    void SetText(string text);

    /// <summary>Clears the system clipboard (an empty OSC 52 payload). A no-op when <see cref="CanWrite"/> is false.</summary>
    void Clear();

    /// <summary>
    /// Attempts an OSC 52 read with <paramref name="timeout"/>. Always completes with <see langword="null"/>
    /// in v1 (<see cref="CanRead"/> is false); a future implementation matches the terminal's OSC 52 response
    /// (it would add a <c>DeviceResponseKind.ClipboardResponse</c> + the interpreter leg, design doc §5.9).
    /// </summary>
    ValueTask<string?> TryGetTextAsync(TimeSpan timeout);
}

/// <summary>
/// The <see cref="UIApplication"/>-backed <see cref="IClipboardService"/>: writes are queued onto the
/// sanctioned out-of-band byte channel (<see cref="UIApplication.QueueControlSequence"/>, OSC-class only)
/// and emitted in the frame loop's Phase 6 after the delta. <see cref="CanWrite"/> reads the live
/// negotiated snapshot, so a re-negotiation is reflected without re-installing the service.
/// </summary>
internal sealed class TerminalClipboardService(UIApplication application) : IClipboardService
{
    /// <inheritdoc/>
    public bool CanWrite => application.Capabilities.Output.Protocol.ClipboardWrite;

    /// <inheritdoc/>
    public bool CanRead => false; // OSC 52 read is not parsed/negotiated yet (design doc §5.9 deferral).

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
    public ValueTask<string?> TryGetTextAsync(TimeSpan timeout) => new((string?)null);
}
