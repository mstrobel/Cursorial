using System.Buffers;
using System.Buffers.Text;
using System.Text;

namespace Cursorial.Output;

/// <summary>
/// Target selection for clipboard operations. Maps to the single-character selection codes
/// defined by xterm's OSC 52 protocol.
/// </summary>
public enum ClipboardTarget
{
    /// <summary>The system clipboard (xterm <c>c</c> selection).</summary>
    System,

    /// <summary>The X11 primary selection (xterm <c>p</c> selection). On non-X11 platforms this typically aliases the system clipboard.</summary>
    Primary
}

/// <summary>
/// Provides extension methods for the <see cref="ClipboardTarget"/> enum to support clipboard-related
/// operations in terminal applications using xterm's OSC 52 protocol.
/// </summary>
public static class ClipboardTargetExtensions
{
    /// <summary>Extensions for the <see cref="ClipboardTarget"/> enumeration.</summary>
    extension(ClipboardTarget target)
    {
        /// <summary>
        /// Gets the byte representation of the clipboard target.
        /// This property provides the corresponding OSC 52 selection code based on the
        /// <see cref="ClipboardTarget"/> value.
        /// </summary>
        public byte Code => target is ClipboardTarget.Primary ? (byte) 'p' : (byte) 'c';
    }
}

/// <summary>
/// Emits xterm's OSC 52 clipboard sequences for writing to the user's clipboard from inside a
/// terminal application. The payload is base64-encoded text in UTF-8. Capability gating is the
/// caller's responsibility — check
/// <see cref="Output.Capabilities.OutputProtocolCapabilities.ClipboardWrite"/> before emitting;
/// terminals that don't honor OSC 52 silently drop the sequence (best case) or print the
/// envelope bytes (worst case, on older terminals that misparse OSCs).
/// </summary>
/// <remarks>
/// <para>
/// <b>Security model.</b> Most modern terminals gate clipboard writes behind a user
/// confirmation prompt or a per-terminal allow-list. The protocol itself is fire-and-forget;
/// applications can't tell whether the user actually accepted the write. Don't rely on the
/// clipboard reflecting the value immediately after emission.
/// </para>
/// <para>
/// <b>Payload size.</b> Some terminals cap the OSC 52 payload (xterm's default is 8 KB of
/// decoded text, configurable). Larger writes are silently truncated. The writer doesn't
/// enforce a limit — pass smaller chunks if the caller needs to support a wider range of
/// terminals.
/// </para>
/// </remarks>
public static class ClipboardWriter
{
    /// <summary>
    /// Write <paramref name="text"/> to the given <paramref name="target"/> selection.
    /// Encodes as UTF-8 and base64-wraps before emitting <c>ESC ] 52 ; &lt;target&gt; ; &lt;base64&gt; ST</c>.
    /// Empty text is permitted — most terminals interpret an empty base64 payload as a
    /// clipboard clear.
    /// </summary>
    public static void WriteSet(IBufferWriter<byte> writer,
                                ReadOnlySpan<char> text,
                                ClipboardTarget target = ClipboardTarget.System)
    {
        ArgumentNullException.ThrowIfNull(writer);

        int utf8ByteCount = Encoding.UTF8.GetByteCount(text);

        // Use a rented array for the intermediate UTF-8 buffer; the base64 encode reads from it
        // and writes into the destination span. Small string fast-path could use stackalloc, but
        // we keep it uniform.
        byte[]? rented = null;
        Span<byte> utf8 = utf8ByteCount <= 256
            ? stackalloc byte[256]
            : rented = ArrayPool<byte>.Shared.Rent(utf8ByteCount);

        try
        {
            int written = utf8ByteCount > 0
                ? Encoding.UTF8.GetBytes(text, utf8)
                : 0;

            int base64Length = Base64.GetMaxEncodedToUtf8Length(written);

            var prefix = VtOutputSequences.Clipboard.Prefix;
            var terminator = VtOutputSequences.Clipboard.StringTerminator;
            int total = prefix.Length + 1 /* target */ + 1 /* ';' */ + base64Length + terminator.Length;

            var dest = writer.GetSpan(total);
            int destWritten = 0;

            prefix.CopyTo(dest);
            destWritten += prefix.Length;

            dest[destWritten++] = target.Code;
            dest[destWritten++] = (byte) ';';

            // Base64-encode the UTF-8 payload directly into the destination span.
            Base64.EncodeToUtf8(
                utf8[..written],
                dest[destWritten..],
                out _,
                out int base64Written);

            destWritten += base64Written;

            terminator.CopyTo(dest[destWritten..]);
            destWritten += terminator.Length;

            writer.Advance(destWritten);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Clear the given <paramref name="target"/> selection. Emits an empty base64 payload, which
    /// most terminals interpret as a clipboard reset.
    /// </summary>
    public static void WriteClear(IBufferWriter<byte> writer,
                                  ClipboardTarget target = ClipboardTarget.System)
    {
        ArgumentNullException.ThrowIfNull(writer);
        WriteSet(writer, ReadOnlySpan<char>.Empty, target);
    }

    /// <summary>
    /// Query the given <paramref name="target"/> selection — <c>ESC ] 52 ; &lt;target&gt; ; ? ST</c>.
    /// A supporting terminal replies with the same envelope holding the selection's base64 payload
    /// (surfaced by the interpreter as <c>DeviceResponseKind.Clipboard</c>); an unsupporting one
    /// never replies, and many that do support it gate the read behind a user prompt — pair the
    /// query with a timeout, never an open-ended wait. Capability gating
    /// (<see cref="Output.Capabilities.OutputProtocolCapabilities.ClipboardRead"/>) is the
    /// caller's responsibility.
    /// </summary>
    public static void WriteQuery(IBufferWriter<byte> writer,
                                  ClipboardTarget target = ClipboardTarget.System)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var prefix = VtOutputSequences.Clipboard.Prefix;
        var terminator = VtOutputSequences.Clipboard.StringTerminator;
        int total = prefix.Length + 3 /* target ; ? */ + terminator.Length;

        var dest = writer.GetSpan(total);
        prefix.CopyTo(dest);
        int written = prefix.Length;

        dest[written++] = target.Code;
        dest[written++] = (byte) ';';
        dest[written++] = (byte) '?';

        terminator.CopyTo(dest[written..]);
        written += terminator.Length;

        writer.Advance(written);
    }
}
