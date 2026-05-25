using System.Buffers;

using Cursorial.Output;
using Cursorial.Output.Capabilities;

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
