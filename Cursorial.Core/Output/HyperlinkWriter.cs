using System.Buffers;
using System.Text;

namespace Cursorial.Core.Output;

/// <summary>
/// Emits OSC 8 hyperlink-anchor sequences. The terminal interprets the text emitted between an
/// open and a close as a clickable link to the supplied URI; non-supporting terminals render
/// the text without the link decoration.
/// </summary>
/// <remarks>
/// <para>
/// Gate on <see cref="TextStylingCapabilities.Hyperlinks"/>; sending OSC 8 to a non-supporting
/// terminal is benign in the sense that the URI is ignored, but some legacy terminals print
/// the metadata bytes literally — they're rare in 2026 but if you're targeting them, skip the
/// emission entirely rather than rely on graceful degradation.
/// </para>
/// <para>
/// Hyperlink nesting is not defined by the spec. Always close one anchor before opening
/// another; passing through anchor-decorated runs of styled text usually means
/// open/text/close as a unit. The optional <c>id</c> parameter lets multi-line links display
/// as a single hyperlink — give the same id to every anchor that should highlight together.
/// </para>
/// </remarks>
public static class HyperlinkWriter
{
    private const int StackBufferThreshold = 256;

    /// <summary>
    /// Open a hyperlink anchor pointing at <paramref name="uri"/>. Subsequent emitted text
    /// becomes the clickable surface; close with <see cref="WriteClose"/> when finished.
    /// </summary>
    public static void WriteOpen(IBufferWriter<byte> writer, ReadOnlySpan<char> uri, ReadOnlySpan<char> id = default)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // Worst case: prefix + "id=" + id + ";" + uri + terminator. Worst-case UTF-8 expansion
        // is 4 bytes/char.
        int uriMax = Encoding.UTF8.GetMaxByteCount(uri.Length);
        int idMax = id.IsEmpty ? 0 : 3 + Encoding.UTF8.GetMaxByteCount(id.Length);
        int budget = VtOutputSequences.Hyperlink.Prefix.Length + idMax + 1 + uriMax
            + VtOutputSequences.KittyTextSizing.StringTerminator.Length;

        // For long URIs spill onto the heap; the common case stays on the stack via GetSpan.
        byte[]? rented = budget > StackBufferThreshold ? new byte[budget] : null;
        Span<byte> buffer = rented is not null ? rented : writer.GetSpan(budget);

        int written = 0;
        VtOutputSequences.Hyperlink.Prefix.CopyTo(buffer[written..]);
        written += VtOutputSequences.Hyperlink.Prefix.Length;

        if (!id.IsEmpty)
        {
            buffer[written++] = (byte)'i';
            buffer[written++] = (byte)'d';
            buffer[written++] = (byte)'=';
            written += Encoding.UTF8.GetBytes(id, buffer[written..]);
        }
        buffer[written++] = (byte)';';
        written += Encoding.UTF8.GetBytes(uri, buffer[written..]);

        var st = VtOutputSequences.KittyTextSizing.StringTerminator;
        st.CopyTo(buffer[written..]);
        written += st.Length;

        if (rented is not null)
        {
            var dest = writer.GetSpan(written);
            buffer[..written].CopyTo(dest);
        }
        writer.Advance(written);
    }

    /// <summary>Close the current hyperlink anchor (<c>ESC ] 8 ; ; ST</c>).</summary>
    public static void WriteClose(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var close = VtOutputSequences.Hyperlink.Close;
        var dest = writer.GetSpan(close.Length);
        close.CopyTo(dest);
        writer.Advance(close.Length);
    }

    /// <summary>
    /// Convenience: open an anchor, emit <paramref name="text"/> as UTF-8 between the open and
    /// close, and close in a single call. Useful when the link text is known at the call site.
    /// </summary>
    public static void WriteHyperlink(
        IBufferWriter<byte> writer,
        ReadOnlySpan<char> uri,
        ReadOnlySpan<char> text,
        ReadOnlySpan<char> id = default)
    {
        WriteOpen(writer, uri, id);
        WriteUtf8(writer, text);
        WriteClose(writer);
    }

    private static void WriteUtf8(IBufferWriter<byte> writer, ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return;
        int max = Encoding.UTF8.GetMaxByteCount(text.Length);
        var dest = writer.GetSpan(max);
        int written = Encoding.UTF8.GetBytes(text, dest);
        writer.Advance(written);
    }
}
