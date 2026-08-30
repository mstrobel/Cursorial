using System.Buffers;

using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering.Imaging;

namespace Cursorial.Rendering.Fragments;

/// <summary>
/// An image rendered via the Sixel graphics protocol — DEC's bitmap-graphics protocol revived
/// in modern terminals (xterm-with-sixel, foot, mlterm, mintty, WezTerm, modern Kitty / Konsole
/// / Alacritty builds). The fragment carries a pre-encoded Sixel byte payload (the full
/// <c>DCS … q … ST</c> envelope as produced by an upstream encoder) plus the cell footprint it
/// will occupy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Capability gating.</b> Sixel emission is gated on
/// <see cref="GraphicsCapabilities.Sixel"/>. The negotiator sets that via two signals: a family
/// allow-list (foot, mlterm, WezTerm, …) AND the terminal's DA1 advertisement of parameter 4.
/// The DA1 signal catches xterm-with-sixel and modern Kitty / Konsole / Alacritty builds
/// regardless of family identification.
/// </para>
/// <para>
/// <b>Layer.</b> Sixel images live in the cell stream — they paint at the cursor and scroll
/// with the cell grid on every terminal that implements the protocol. The renderer treats this
/// as a Cell-layer fragment: cells under the footprint skip their glyphs during emission, and
/// repainting the cell region (e.g., scrolling the buffer, removing the fragment) overwrites
/// the bitmap. No protocol-level <c>EmitErase</c> command exists; the default no-op
/// implementation is correct.
/// </para>
/// <para>
/// <b>Payload ownership.</b> The fragment retains the <see cref="ReadOnlyMemory{T}"/> reference
/// across frames. Don't pass buffers borrowed from a stack frame or a pool you intend to return
/// before the fragment is removed from the buffer.
/// </para>
/// <para>
/// <b>Multiplexer passthrough.</b> When the negotiator detects we're inside a multiplexer
/// (tmux today), <see cref="Emit"/> wraps the entire payload in a tmux DCS-passthrough envelope.
/// The inner DCS framing of the Sixel sequence is ESC-doubled by the wrapper so it survives
/// tmux's parser intact.
/// </para>
/// </remarks>
public sealed class SixelFragment : IBufferFragment
{
    private readonly ReadOnlyMemory<byte> _payload;
    private readonly Size _cellSize;
    private readonly string? _sourceFileName;

    // Retained from the RGBA constructor so the fragment can pixel-crop itself under a clip (re-crop the
    // pixels + re-encode). Null for pre-encoded payloads, which can't be cropped.
    private readonly byte[]? _rgba;
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;
    private readonly int _maxColors;

    // Diff identities (see Key). Both are PRE-BOXED so Key is allocation-free per access (the diff reads it
    // every frame).
    //
    //  _clipKey — for a fragment produced by Clip: the (source content key, visible rect) tuple, set in Clip.
    //    A clip re-crops + re-encodes into a brand-new instance every composite pass, so keying on reference
    //    identity would make the FrameRenderer's Key-equality diff (FragmentsMatch) read an unchanged-but-
    //    occluded image as a different fragment every frame and re-transmit the whole envelope — the "banner
    //    flickers under a clipping dialog" bug. Reusing the SOURCE's content key (not re-hashing the crop)
    //    keeps the clip path allocation-light.
    //  _contentKey — for a non-clipped (source) fragment: a content-derived key computed lazily on first
    //    access and cached, matching KittyImageFragment / ITerm2ImageFragment. The Image content layer builds
    //    a fresh SixelFragment every raster, so a content key lets an identical reconstruction diff-skip
    //    instead of re-transmitting; it also makes a re-rastered-but-identical source clip to a stable key.
    private object? _clipKey;
    private object? _contentKey;

    /// <summary>
    /// Construct a Sixel fragment from a pre-encoded payload. <paramref name="sixelPayload"/>
    /// must be the full <c>DCS … q … ST</c> envelope (including framing) — the fragment writes
    /// it to the output sink verbatim aside from optional multiplexer wrapping.
    /// </summary>
    /// <param name="sixelPayload">Pre-encoded Sixel byte sequence including DCS framing.</param>
    /// <param name="cellSize">The cell footprint the rendered image will occupy on the terminal.</param>
    /// <param name="imageData">The optional source image data, used to provide the source file name for debugging.</param>
    public SixelFragment(ReadOnlyMemory<byte> sixelPayload, Size cellSize, ImageData? imageData = null)
    {
        if (sixelPayload.IsEmpty)
            throw new ArgumentException("Sixel payload must be non-empty.", nameof(sixelPayload));
        if (cellSize.Columns < 0 || cellSize.Rows < 0 || cellSize is { Columns: 0, Rows: 0 })
        {
            throw new ArgumentOutOfRangeException(
                nameof(cellSize),
                $"Cell size must be positive in at least one dimension; got {cellSize}.");
        }

        _payload = sixelPayload;
        _cellSize = cellSize;
        _sourceFileName = imageData?.SourceFileName;
    }

    /// <summary>
    /// Construct a Sixel fragment from raw RGBA pixels — quantizes to an indexed palette and
    /// encodes the Sixel envelope at construction time. Use this when feeding output from a PNG
    /// decoder or other rasterizer; for pre-encoded payloads (e.g., a cached image cooked once
    /// and rendered many times) use the <see cref="ReadOnlyMemory{T}"/> overload.
    /// </summary>
    /// <param name="rgbaPixels">Row-major RGBA bytes (4 bytes per pixel). Alpha is ignored — Sixel
    /// can't represent translucency; callers should pre-composite against a known background.</param>
    /// <param name="pixelWidth">Image width in pixels.</param>
    /// <param name="pixelHeight">Image height in pixels.</param>
    /// <param name="cellSize">Cell footprint the rendered image will occupy.</param>
    /// <param name="maxColors">Palette size cap, default 256 (Sixel's legacy ceiling).</param>
    /// <param name="imageData">The source image data for debugging display purposes (optional).</param>
    public SixelFragment(ReadOnlySpan<byte> rgbaPixels,
                         int pixelWidth,
                         int pixelHeight,
                         Size cellSize,
                         int maxColors = 256,
                         ImageData? imageData = null)
    {
        if (cellSize.Columns < 0 || cellSize.Rows < 0 || cellSize is { Columns: 0, Rows: 0 })
        {
            throw new ArgumentOutOfRangeException(
                nameof(cellSize),
                $"Cell size must be positive in at least one dimension; got {cellSize}.");
        }

        var quantized = MedianCutQuantizer.Quantize(rgbaPixels, pixelWidth, pixelHeight, maxColors);
        _payload = SixelEncoder.Encode(quantized.IndexedPixels, pixelWidth, pixelHeight, quantized.Palette);
        _cellSize = cellSize;
        _sourceFileName = imageData?.SourceFileName;

        // Retain the pixels so a clip can re-crop this image.
        _rgba = rgbaPixels.ToArray();
        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;
        _maxColors = maxColors;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Sixel <b>can</b> crop: it re-crops the retained RGBA to the <paramref name="visible"/> cell rectangle
    /// (mapping cells to pixels by this image's pixels-per-cell) and re-encodes a fresh Sixel envelope sized to
    /// those cells. Returns null only for the pre-encoded-payload variant, which has no pixels to crop.
    /// </remarks>
    public IBufferFragment? Clip(in Rect visible)
    {
        if (_rgba is null || _cellSize.Columns <= 0 || _cellSize.Rows <= 0 ||
            _pixelWidth <= 0 || _pixelHeight <= 0) return null;

        double pixelsPerColumn = (double) _pixelWidth / _cellSize.Columns;
        double pixelsPerRow = (double) _pixelHeight / _cellSize.Rows;

        // Clamp the origin to the last valid pixel (px-1) so the extent's (1, _pixelWidth - px) clamp
        // range is always valid: at sub-1-px/cell ratios a rightmost-cell origin could otherwise reach
        // _pixelWidth, making the extent clamp Math.Clamp(_, 1, 0) throw (min > max).
        int px = Math.Clamp((int) Math.Round(visible.Column * pixelsPerColumn), 0, _pixelWidth - 1);
        int py = Math.Clamp((int) Math.Round(visible.Row * pixelsPerRow), 0, _pixelHeight - 1);
        int pw = Math.Clamp((int) Math.Round(visible.Columns * pixelsPerColumn), 1, _pixelWidth - px);
        int ph = Math.Clamp((int) Math.Round(visible.Rows * pixelsPerRow), 1, _pixelHeight - py);

        var cropped = new byte[pw * ph * 4];
        for (int y = 0; y < ph; y++)
            Array.Copy(_rgba, ((py + y) * _pixelWidth + px) * 4, cropped, y * pw * 4, pw * 4);

        return new SixelFragment(cropped, pw, ph, new Size(visible.Columns, visible.Rows), _maxColors)
        {
            // Key off THIS fragment's (content-derived) Key plus the crop rect, so a static image under a
            // static clip diff-skips across composites — and a re-rastered-but-identical source clips to the
            // same key. Reuses the source's cached content key rather than re-hashing the cropped pixels.
            _clipKey = (Key, visible),
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Content-derived</b>, matching <see cref="KittyImageFragment.Key"/> / <see cref="ITerm2ImageFragment.Key"/>:
    /// the Image content layer builds a fresh <see cref="SixelFragment"/> every raster, so keying on reference
    /// identity would re-transmit the whole DCS envelope each identical reconstruction. A source fragment's key
    /// is a hash of its source (raw RGBA, or the pre-encoded payload) plus its sizes, computed once and cached;
    /// a clipped fragment keys off <c>(source content key, visible rect)</c> so an unchanged image under an
    /// unchanged clip diff-skips too.
    /// </remarks>
    public object Key => _clipKey ?? (_contentKey ??= ComputeContentKey());

    // The source-content key: hash the true source (raw RGBA when we hold it, else the pre-encoded payload)
    // rather than the encoded output, so it's deterministic regardless of any quantizer/encoder variation.
    // Only ever called for a non-clipped fragment (a clipped one carries _clipKey), so the crop path never
    // hashes pixels.
    private object ComputeContentKey()
    {
        var hash = new HashCode();
        ReadOnlySpan<byte> source = _rgba is not null ? _rgba : _payload.Span;
        hash.AddBytes(source);
        return (source.Length, hash.ToHashCode(), _cellSize, _pixelWidth, _pixelHeight, _maxColors);
    }

    /// <summary>The pre-encoded Sixel payload (DCS framing included).</summary>
    public ReadOnlyMemory<byte> Payload => _payload;

    /// <inheritdoc/>
    public Size GetSize() => _cellSize;

    /// <inheritdoc/>
    public bool IsSupported(OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return capabilities.Graphics.Sixel;
    }

    /// <inheritdoc/>
    public void Emit(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(capabilities);

        if (capabilities.Protocol.MultiplexerPassthrough)
        {
            // The Sixel payload's outer ESC bytes get doubled by the wrapper so the inner
            // DCS framing survives tmux's parser intact and reaches the outer terminal.
            TmuxPassthrough.WriteWrapped(output, _payload.Span);
            return;
        }

        var dest = output.GetSpan(_payload.Length);
        _payload.Span.CopyTo(dest);
        output.Advance(_payload.Length);
    }
    
    /// <inheritdoc/>
    public override string ToString()
        => $"[SixelFragment CellSize={_cellSize} PayloadLength={_payload.Length} " +
           $"SourceFileName={(_sourceFileName != null ? $"'{_sourceFileName}'" : "<unknown>")}]";
}
