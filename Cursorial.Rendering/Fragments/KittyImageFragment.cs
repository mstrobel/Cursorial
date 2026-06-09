using System.Buffers;
using System.Text;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering.Imaging;

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
    // Source-rectangle crop in image pixels (Kitty x,y,w,h) for a clipped placement; null = full image.
    private readonly (int X, int Y, int W, int H)? _sourceRect;
    // Which placement dimension to leave to Kitty's aspect-ratio scaling (omit c= or r= on the wire).
    private readonly AspectFreeDimension _aspectFree;
    private readonly uint _imageId;
    private readonly object _contentKey;

    /// <summary>Construct a Kitty image fragment for the supplied image data.</summary>
    /// <param name="data">The encoded image and its metadata.</param>
    /// <param name="displaySize">The whole-cell footprint the placement reserves (reported by <see cref="GetSize"/>).</param>
    /// <param name="pixelSize">The image's native pixel dimensions, enabling source-rectangle cropping.</param>
    /// <param name="sourceRect">A pixel source rectangle for a clipped placement; null = full image.</param>
    /// <param name="aspectFree">Which dimension to omit on the wire so Kitty scales to native aspect (no cell-rounding stretch).</param>
    public KittyImageFragment(ImageData data, Size? displaySize = null, (int Columns, int Rows)? pixelSize = null,
                              (int X, int Y, int W, int H)? sourceRect = null,
                              AspectFreeDimension aspectFree = AspectFreeDimension.None)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        _displaySize = displaySize ?? data.RequestedSize ?? throw new InvalidOperationException("ImageData.CellSize or displaySize must be provided.");
        _pixelSize = pixelSize;
        _sourceRect = sourceRect;
        // A cropped placement is exact (the source rectangle defines what shows), so never omit a
        // dimension when cropping — aspect omission only applies to the full, uncropped image.
        _aspectFree = sourceRect is null ? aspectFree : AspectFreeDimension.None;
        _imageId = (uint) Interlocked.Increment(ref _nextImageId);

        // Content-derived diff key (see Key). Computed once; combines a hash of the encoded bytes
        // with the byte length, format, and target sizes so two distinct images are extremely
        // unlikely to collide. Boxed once to avoid per-access boxing during diffing.
        var hash = new HashCode();
        hash.AddBytes(data.Bytes.Span);
        hash.Add(data.Bytes.Length);
        hash.Add(data.Format);
        hash.Add(_displaySize);
        hash.Add(_pixelSize);
        hash.Add(_sourceRect);
        hash.Add(_aspectFree);
        _contentKey = (data.Bytes.Length, hash.ToHashCode(), _displaySize, _pixelSize, _sourceRect, _aspectFree);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Kitty <b>can</b> crop: it maps the <paramref name="visible"/> cell rectangle onto the source image's
    /// pixels (using the native pixel size) and re-places the same image with a source rectangle
    /// (<c>x,y,w,h</c>) over the visible cell footprint. Returns null only when the native pixel size is
    /// unknown — then the compositor suppresses the partial image rather than overdrawing.
    /// </remarks>
    public IBufferFragment? Clip(in Rect visible)
    {
        if (_pixelSize is not { } px || px.Columns <= 0 || px.Rows <= 0 ||
            _displaySize.Columns <= 0 || _displaySize.Rows <= 0)
            return null;

        double pixelsPerColumn = (double) px.Columns / _displaySize.Columns;
        double pixelsPerRow = (double) px.Rows / _displaySize.Rows;
        // Clamp the ORIGIN to the last valid pixel (px-1), then the EXTENT to whatever pixels remain, so
        // x+w <= px.Columns and y+h <= px.Rows for every regime — X and W are rounded independently, so
        // an unclamped extent can overshoot the image edge by a pixel (and at sub-1-px/cell ratios the
        // origin itself can land on the exclusive end). px.Columns/Rows are >= 1 (guarded above), so the
        // (1, px - origin) clamp range is always valid (no min>max).
        int sourceX = Math.Clamp((int) Math.Round(visible.Column * pixelsPerColumn), 0, px.Columns - 1);
        int sourceY = Math.Clamp((int) Math.Round(visible.Row * pixelsPerRow), 0, px.Rows - 1);
        var source = (
            X: sourceX,
            Y: sourceY,
            W: Math.Clamp((int) Math.Round(visible.Columns * pixelsPerColumn), 1, px.Columns - sourceX),
            H: Math.Clamp((int) Math.Round(visible.Rows * pixelsPerRow), 1, px.Rows - sourceY));

        return new KittyImageFragment(_data, new Size(visible.Columns, visible.Rows), _pixelSize, source);
    }

    /// <summary>The image being transmitted.</summary>
    public ImageData Image => _data;

    /// <summary>The Kitty graphics-protocol image ID assigned to this fragment, used as the deletion target by <see cref="EmitErase"/>.</summary>
    public uint ImageId => _imageId;

    /// <inheritdoc/>
    public FragmentLayer Layer => FragmentLayer.Overlay;

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Content-derived</b>, not the per-instance image ID. Two fragments wrapping the same
    /// image (same bytes, format, and target sizes) compare equal, so the renderer's fragment
    /// diff treats an identical per-frame reconstruction as unchanged and skips it — instead of
    /// deleting and re-transmitting a fresh image every frame. That churn would burn a new Kitty
    /// image ID per frame and, over a long session, exhaust the terminal's image-storage quota
    /// (Rio, Kitty, …), degrading to "one image at a time". The wire identity used for transmit
    /// and delete is still the unique <see cref="ImageId"/>; the renderer preserves the
    /// originally-transmitted id across a diff-skip (see <c>FrameRenderer.EmitFragments</c>), so
    /// erases target the image actually on screen.
    /// </remarks>
    public object Key => _contentKey;

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
        // Pin a cell dimension only when it isn't the aspect-free one — omitting c= (or r=) lets Kitty
        // scale the image to its native aspect in that axis rather than stretching into a rounded box.
        // Omit ONLY for the aspect-free axis (never for a degenerate 0): dropping BOTH qualifiers would
        // make Kitty fall back to the image's native pixel size and blow past the reserved footprint, so
        // the pinned dimension is clamped to >= 1, guaranteeing at least one sized axis on the wire.
        var colQualifier = _aspectFree == AspectFreeDimension.Columns
                               ? "" : $"c={Math.Max(1, _displaySize.Columns)},";
        var rowQualifier = _aspectFree == AspectFreeDimension.Rows
                               ? "" : $"r={Math.Max(1, _displaySize.Rows)},";
        // Source-rectangle crop (image pixels) for a clipped placement — Kitty displays only this region.
        var cropQualifier = _sourceRect is { } s ? $"x={s.X},y={s.Y},w={s.W},h={s.H}," : "";
        var header = firstChunk
                         ? Encoding.ASCII.GetBytes(
                             $"a=T,f={format},i={_imageId},{colQualifier}{rowQualifier}{cropQualifier}q=2,m={(isLast ? 0 : 1)};")
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
