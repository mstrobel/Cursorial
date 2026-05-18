using System.Buffers;
using System.Text;
using Cursorial.Output;
using Cursorial.Output.Capabilities;

namespace Cursorial.Rendering.Fragments;

/// <summary>
/// An image rendered via the Kitty graphics protocol — APC <c>G</c> escape with base64-encoded
/// payload, chunked to fit the per-APC size limit. Painted in the cell rectangle declared by
/// <see cref="ImageData.CellSize"/>; the protocol handles aspect-ratio adjustment from the
/// image's natural pixel size to the requested cell footprint.
/// </summary>
/// <remarks>
/// <para>
/// Wire format reference: <see href="https://sw.kovidgoyal.net/kitty/graphics-protocol/"/>.
/// We emit a transmit-and-display action (<c>a=T</c>) with format 100 (PNG) or 24/32 for raw
/// pixel formats; chunk continuation flag (<c>m=1</c>) is set on every chunk except the last
/// (<c>m=0</c>). Quietness 2 (<c>q=2</c>) suppresses both success and error responses from the
/// terminal, which we'd otherwise have to consume out of the input stream.
/// </para>
/// <para>
/// <b>Chunking.</b> Kitty's spec recommends each APC payload stay ≤ 4096 base64 bytes. We
/// emit headers on the first chunk and a continuation header (<c>m=…</c> only) on subsequent
/// chunks so the wire format matches what the spec calls out as the canonical layout.
/// </para>
/// </remarks>
public sealed class KittyImageFragment : IBufferFragment
{
    /// <summary>Maximum base64-encoded payload bytes per APC packet. Kitty's spec recommends ≤ 4096.</summary>
    private const int MaxChunkBytes = 4096;

    private readonly ImageData _data;

    /// <summary>Construct a Kitty image fragment for the supplied image data.</summary>
    public KittyImageFragment(ImageData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
    }

    /// <summary>The image being transmitted.</summary>
    public ImageData Image => _data;

    /// <inheritdoc/>
    public Size GetSize() => _data.CellSize;

    /// <inheritdoc/>
    public bool IsSupported(OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return capabilities.Graphics.KittyGraphics;
    }

    /// <inheritdoc/>
    public void Emit(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(output);

        // The cursor is already at the anchor (the FrameRenderer positions it before calling
        // us). Kitty places the image at the current cursor and advances by (c × r) cells.
        if (_data.Bytes.IsEmpty) return;

        // f=100 (PNG) is the only format-from-encoded-bytes Kitty advertises. JPEG/GIF callers
        // need to re-encode upstream; the Image content layer enforces protocol-format
        // compatibility, so by the time we get here we know the caller picked a Kitty-
        // compatible format.
        int format = 100;

        // Base64-encode the whole payload, then chunk the ASCII output. Image payloads are
        // measured in KB-to-MB ranges, and emission is one-shot per fragment registration —
        // a single allocation here is fine.
        string base64 = Convert.ToBase64String(_data.Bytes.Span);
        byte[] ascii = Encoding.ASCII.GetBytes(base64);
        EmitFromBytes(output, format, ascii, capabilities.Protocol.MultiplexerPassthrough);
    }

    private void EmitFromBytes(IBufferWriter<byte> output, int format, ReadOnlySpan<byte> base64, bool wrap)
    {
        int remaining = base64.Length;
        int offset = 0;
        bool firstChunk = true;

        while (remaining > 0)
        {
            int take = Math.Min(MaxChunkBytes, remaining);
            bool isLast = take == remaining;
            var chunk = base64.Slice(offset, take);
            EmitChunk(output, format, chunk, firstChunk, isLast, wrap);
            offset += take;
            remaining -= take;
            firstChunk = false;
        }
    }

    private void EmitChunk(IBufferWriter<byte> output, int format, ReadOnlySpan<byte> chunk,
                           bool firstChunk, bool isLast, bool wrap)
    {
        if (wrap)
        {
            // Inside a multiplexer that strips unknown escape sequences (tmux). Build the APC
            // payload into a scratch buffer, then wrap that in a DCS tmux passthrough envelope
            // so it reaches the outer terminal intact. Per-chunk wrapping keeps the APC
            // framing logic identical to the direct path.
            var scratch = new ArrayBufferWriter<byte>();
            WriteApcChunk(scratch, format, chunk, firstChunk, isLast);
            TmuxPassthrough.WriteWrapped(output, scratch.WrittenSpan);
        }
        else
        {
            WriteApcChunk(output, format, chunk, firstChunk, isLast);
        }
    }

    private void WriteApcChunk(IBufferWriter<byte> output, int format, ReadOnlySpan<byte> chunk,
                               bool firstChunk, bool isLast)
    {
        // Header bytes for the chunk. The first chunk carries full transmit params; subsequent
        // chunks carry only m=. Quietness q=2 always (we don't read responses).
        var rowQualifier = _data.CellSize.Rows >= 1 ? $"r={_data.CellSize.Rows}," : "";
        var header = firstChunk
                         ? Encoding.ASCII.GetBytes(
                             $"a=T,f={format},c={_data.CellSize.Columns},{rowQualifier}q=2,m={(isLast ? 0 : 1)};")
                         : Encoding.ASCII.GetBytes($"m={(isLast ? 0 : 1)};");

        // APC framing: ESC _ G <control-data> ; <payload> ESC \ — the "_G" prefix marks
        // every Kitty graphics APC, including continuation chunks. Earlier versions only
        // emitted 'G' on the first chunk, which made continuation chunks parse as unknown
        // APCs (silently discarded by kitty / Ghostty) and left the PNG buffer truncated.
        int needed = 3 + header.Length + chunk.Length + 2;
        var dest = output.GetSpan(needed);
        int written = 0;
        dest[written++] = 0x1B; // ESC
        dest[written++] = (byte) '_';
        dest[written++] = (byte) 'G';
        header.AsSpan().CopyTo(dest[written..]);
        written += header.Length;
        chunk.CopyTo(dest[written..]);
        written += chunk.Length;
        dest[written++] = 0x1B; // ESC
        dest[written++] = (byte) '\\';

        output.Advance(written);
    }
}
