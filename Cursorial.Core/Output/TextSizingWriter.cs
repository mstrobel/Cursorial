using System.Buffers;
using System.Text;

using Cursorial.Output.Capabilities;
using Cursorial.Text;

namespace Cursorial.Output;

/// <summary>
/// Emits Kitty text-sizing OSC 66 sequences. Format on the wire is
/// <c>ESC ] 66 ; metadata ; text ST</c>, where metadata is a colon-separated list of
/// <c>key=value</c> pairs derived from a <see cref="TextSizing"/>. Default-valued fields are
/// omitted from the metadata block.
/// </summary>
/// <remarks>
/// <para>
/// Gate on <see cref="WriteSplit"/> at the call site — a non-supporting terminal
/// renders the text at normal size and silently ignores the OSC. The writer itself is pure
/// byte production; it doesn't consult capabilities.
/// </para>
/// <para>
/// The spec caps a single OSC 66 payload at 4096 UTF-8 bytes. Longer strings MUST be split
/// across multiple emissions to avoid truncation. <see cref="Write"/> handles that
/// automatically by chunking on grapheme boundaries; the bare <see cref="TextSizingCapabilities"/> overloads
/// emit a single sequence and assume the caller already respects the cap.
/// </para>
/// </remarks>
public static class TextSizingWriter
{
    /// <summary>
    /// Emit a single OSC 66 sequence carrying <paramref name="text"/> rendered per
    /// <paramref name="sizing"/>. Caller is responsible for keeping the UTF-8 byte length of
    /// <paramref name="text"/> ≤ <see cref="VtOutputSequences.KittyTextSizing.MaxTextBytes"/>;
    /// use <see cref="WriteSplit"/> if you can't guarantee that.
    /// </summary>
    public static void Write(IBufferWriter<byte> writer, in TextSizing sizing, ReadOnlySpan<char> text)
        => Write(writer, sizing, sizing.Width, text);

    // One sequence with an explicit w (0 = none) — the packed path supplies each chunk's computed width.
    private static void Write(IBufferWriter<byte> writer, in TextSizing sizing, byte width, ReadOnlySpan<char> text)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var prefix = VtOutputSequences.KittyTextSizing.Prefix;
        var st = VtOutputSequences.KittyTextSizing.StringTerminator;
        // metadata is at most ~30 bytes (each key fits in 4 chars; six keys). Text is at most
        // the spec cap.
        int budget = prefix.Length + 32 + 1 + Encoding.UTF8.GetMaxByteCount(text.Length) + st.Length;

        // Caller-respects-cap is the contract; allocate a heap buffer for the common case via
        // GetSpan, but if the writer can't give us that much, fall back to a rented buffer.
        Span<byte> buffer = budget <= 4096 ? writer.GetSpan(budget) : new byte[budget];

        int written = 0;
        prefix.CopyTo(buffer[written..]);
        written += prefix.Length;
        WriteMetadata(sizing, width, buffer, ref written);
        buffer[written++] = (byte) ';';
        written += Encoding.UTF8.GetBytes(text, buffer[written..]);
        st.CopyTo(buffer[written..]);
        written += st.Length;

        if (budget > 4096)
        {
            // We wrote into our rented buffer — now copy through to the actual writer.
            var dest = writer.GetSpan(written);
            buffer[..written].CopyTo(dest);
        }

        writer.Advance(written);
    }

    /// <summary>
    /// Emit one or more OSC 66 sequences carrying <paramref name="text"/>. Splits the UTF-8
    /// payload at grapheme-cluster boundaries (per <see cref="System.Globalization.StringInfo"/>)
    /// to respect the spec's 4096-byte payload cap without breaking a multi-codepoint glyph.
    /// </summary>
    public static void WriteSplit(IBufferWriter<byte> writer, in TextSizing sizing, ReadOnlySpan<char> text)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (text.Length == 0)
        {
            Write(writer, sizing, text);
            return;
        }

        // Packed: one sequence per packing chunk, each claiming its computed w (never above the spec's seven
        // cells) — the same chunks TextSizing.SpanColumns measured, so the footprint told to the terminal is
        // the footprint layout reserved.
        if (sizing.Packed && sizing.Width == 0)
        {
            foreach (var chunk in TextSizingPacking.Chunks(sizing, text))
                Write(writer, sizing, (byte) chunk.Cells, text.Slice(chunk.Start, chunk.Length));
            return;
        }

        // Quick path: whole string fits in one emission.
        int totalUtf8 = Encoding.UTF8.GetByteCount(text);

        if (totalUtf8 <= VtOutputSequences.KittyTextSizing.MaxTextBytes)
        {
            Write(writer, sizing, text);
            return;
        }

        var array = ArrayPool<char>.Shared.Rent(8192);

        Span<char> batch = array;

        try
        {
            // Split on grapheme cluster boundaries — splitting a UTF-8 sequence mid-glyph would
            // corrupt the rendered text.
            var enumerator = text.GetGraphemeEnumerator();
            int batchChars = 0;
            int batchBytes = 0;

            while (enumerator.MoveNext())
            {
                var cluster = enumerator.Current;
                int clusterBytes = Encoding.UTF8.GetByteCount(cluster);

                if (batchBytes + clusterBytes > VtOutputSequences.KittyTextSizing.MaxTextBytes && batchBytes > 0)
                {
                    Write(writer, sizing, batch[..batchChars]);
                    batchBytes = 0;
                    batchChars = 0;
                }

                cluster.CopyTo(batch.Slice(batchChars));
                batchBytes += clusterBytes;
                batchChars += cluster.Length;
            }

            if (batchChars > 0)
                Write(writer, sizing, batch[..batchChars]);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(array, clearArray: true);
        }
    }

    private static void WriteMetadata(in TextSizing sizing, byte width, Span<byte> buffer, ref int written)
    {
        bool first = true;

        if (sizing.Scale != 0 && sizing.Scale != 1)
            EmitKv((byte) 's', sizing.Scale, buffer, ref written, ref first);

        // 'w' — the width in cells of the WHOLE text of this sequence (spec: "all the text in that escape code
        // must be rendered in s·w cells"; maintainer-verified against kitty 2026-09-12). An explicit Width is
        // emitted as given; a Packed sizing has the writer compute it per chunk (WriteSplit → Write(…, width)).
        if (width != 0)
            EmitKv((byte) 'w', width, buffer, ref written, ref first);

        if (sizing.Numerator != 0)
            EmitKv((byte) 'n', sizing.Numerator, buffer, ref written, ref first);

        if (sizing.Denominator != 0)
            EmitKv((byte) 'd', sizing.Denominator, buffer, ref written, ref first);

        if (sizing.Vertical != TextSizingVerticalAlignment.Top)
            EmitKv((byte) 'v', (byte) sizing.Vertical, buffer, ref written, ref first);

        if (sizing.Horizontal != TextSizingHorizontalAlignment.Left)
            EmitKv((byte) 'h', (byte) sizing.Horizontal, buffer, ref written, ref first);
    }

    private static void EmitKv(byte key, byte value, Span<byte> buffer, ref int written, ref bool first)
    {
        if (!first) buffer[written++] = (byte) ':';
        buffer[written++] = key;
        buffer[written++] = (byte) '=';
        VtWriterUtilities.WriteAsciiInt(value, buffer, ref written);
        first = false;
    }
}