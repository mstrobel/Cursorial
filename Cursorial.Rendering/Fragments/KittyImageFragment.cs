using System.Buffers;
using System.Text;
using Cursorial.Output;
using Cursorial.Output.Capabilities;

namespace Cursorial.Rendering.Fragments;

/// <summary>
/// An image rendered via the Kitty graphics protocol — APC <c>G</c> escape with base64-encoded
/// payload, chunked to fit the per-APC size limit. Painted in the cell rectangle declared by
/// <see cref="ImageData.RequestedSize"/>; the protocol handles aspect-ratio adjustment from the
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

    /// <summary>
    /// Process-local image-id allocator. Kitty's protocol uses a 32-bit positive ID; the
    /// initial value is randomized at startup so two Cursorial processes sharing the same
    /// terminal don't collide on small consecutive IDs (which would otherwise overwrite each
    /// other's transmissions). Increments are monotonic per process — wrap-around at int.MaxValue
    /// is theoretical.
    /// </summary>
    private static int _nextImageId = SeedNextImageId();

    private static int SeedNextImageId()
    {
        // Random.Shared returns a value in [0, int.MaxValue). Bias to a midrange start so we
        // have room to grow without wrapping in any plausible session.
        return Random.Shared.Next(1, int.MaxValue / 2);
    }

    private readonly ImageData _data;
    private readonly Size _displaySize;
    private readonly (int Columns, int Rows)? _pixelSize;
    private readonly uint _imageId;

    /// <summary>Construct a Kitty image fragment for the supplied image data.</summary>
    public KittyImageFragment(ImageData data, Size? displaySize = null, (int Columns, int Rows)? pixelSize = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        _displaySize = displaySize ?? data.RequestedSize ?? throw new InvalidOperationException("ImageData.CellSize or displaySize must be provided.");
        _pixelSize = pixelSize;
        _imageId = (uint) Interlocked.Increment(ref _nextImageId);
    }

    /// <summary>The image being transmitted.</summary>
    public ImageData Image => _data;

    /// <summary>The Kitty graphics-protocol image ID assigned to this fragment, used as the deletion target by <see cref="EmitErase"/>.</summary>
    public uint ImageId => _imageId;

    /// <inheritdoc/>
    public FragmentLayer Layer => FragmentLayer.Overlay;

    /// <inheritdoc/>
    /// <remarks>
    /// Each fragment instance owns a unique image ID, so two fragments wrapping identical
    /// bytes still diff distinctly — the wire identity matters more than content identity for
    /// the renderer's erase / re-emit decision.
    /// </remarks>
    public object Key => _imageId;

    /// <inheritdoc/>
    public Size GetSize() => _displaySize;

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

    /// <inheritdoc/>
    /// <remarks>
    /// Emits Kitty's <c>a=d,d=I,i=&lt;id&gt;</c> delete-by-id command. Kitty proper removes the
    /// image immediately on a CSI 2 J / on placement removal, but other terminals implementing
    /// the protocol (Ghostty in some modes, foot, …) only honor the explicit delete sequence.
    /// Emitting unconditionally keeps behavior consistent across the Kitty-protocol family.
    /// </remarks>
    public void EmitErase(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(capabilities);

        var payload = Encoding.ASCII.GetBytes($"a=d,d=I,i={_imageId},q=2");

        if (capabilities.Protocol.MultiplexerPassthrough)
        {
            // Build the APC into scratch and wrap, matching Emit's behavior so the wire form is
            // symmetric with how the transmit was sent.
            var scratch = new ArrayBufferWriter<byte>();
            WriteApcEnvelope(scratch, payload);
            TmuxPassthrough.WriteWrapped(output, scratch.WrittenSpan);
        }
        else
        {
            WriteApcEnvelope(output, payload);
        }
    }

    private static void WriteApcEnvelope(IBufferWriter<byte> output, ReadOnlySpan<byte> payload)
    {
        // APC framing: ESC _ G <control-data> ESC \ — no payload-after-semicolon for the
        // delete command (the control data is the entire body).
        int needed = 3 + payload.Length + 2;
        var dest = output.GetSpan(needed);
        int written = 0;
        dest[written++] = 0x1B; // ESC
        dest[written++] = (byte) '_';
        dest[written++] = (byte) 'G';
        payload.CopyTo(dest[written..]);
        written += payload.Length;
        dest[written++] = 0x1B; // ESC
        dest[written++] = (byte) '\\';
        output.Advance(written);
    }

    private void WriteApcChunk(IBufferWriter<byte> output, int format, ReadOnlySpan<byte> chunk,
                               bool firstChunk, bool isLast)
    {
        // Header bytes for the chunk. The first chunk carries full transmit params plus the
        // image ID (i=) so a later EmitErase can target this exact image. Subsequent chunks
        // carry only m=. Quietness q=2 always (we don't read responses).
        var rowQualifier = _displaySize.Rows >= 1 ? $"r={_displaySize.Rows}," : "";
        var header = firstChunk
                         ? Encoding.ASCII.GetBytes(
                             $"a=T,f={format},i={_imageId},c={_displaySize.Columns},{rowQualifier}q=2,m={(isLast ? 0 : 1)};")
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
    
    /// <inheritdoc/>
    public override string ToString()
        => $"[{nameof(KittyImageFragment)} CellSize={_displaySize} PayloadLength={_data.Bytes.Length} " +
           $"SourceFileName={(_data.SourceFileName != null ? $"'{_data.SourceFileName}'" : "<unknown>")}]";
}
