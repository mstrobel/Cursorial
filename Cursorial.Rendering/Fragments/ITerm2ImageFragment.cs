using System.Buffers;
using System.Text;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering.Imaging;

namespace Cursorial.Rendering.Fragments;

/// <summary>
/// An image rendered via the iTerm2 inline-image protocol (OSC 1337 <c>File=…</c>). Compatible
/// with iTerm2 itself and any terminal that implements the same extension (WezTerm at minimum).
/// </summary>
/// <remarks>
/// <para>
/// Wire format reference:
/// <see href="https://iterm2.com/documentation-images.html"/>. The OSC body is a
/// semicolon-separated list of <c>key=value</c> pairs after <c>File=</c>, followed by a colon
/// and the base64-encoded image bytes, terminated by <c>ESC \</c>.
/// </para>
/// <para>
/// We declare the cell footprint via <c>width=&lt;cells&gt;</c> and <c>height=&lt;cells&gt;</c>
/// with <c>preserveAspectRatio=0</c> so the image stretches to fit exactly. When one dimension is
/// aspect-free (the caller sized only the other), that axis is emitted as <c>auto</c> with
/// <c>preserveAspectRatio=1</c> so iTerm2 scales to native aspect — mirroring Kitty's <c>c=</c>/<c>r=</c>
/// omission. <c>inline=1</c> renders the image where the cursor sits; without it the terminal offers a download. The
/// <c>size=</c> field carries the byte count of the original payload (not the base64-expanded
/// version) — iTerm2 uses it for the progress indicator.
/// </para>
/// </remarks>
public sealed class ITerm2ImageFragment : IBufferFragment
{
    private readonly ImageData _data;
    // ReSharper disable once NotAccessedField.Local
    private readonly (int Columns, int Rows)? _pixelSize;
    private readonly Size _displaySize;
    // Which placement dimension to leave to iTerm2's aspect-ratio scaling (emit width=auto / height=auto).
    private readonly AspectFreeDimension _aspectFree;

    /// <summary>Construct an iTerm2 inline image fragment.</summary>
    /// <param name="data">The encoded image and its metadata.</param>
    /// <param name="displaySize">The whole-cell footprint reported by <see cref="GetSize"/>.</param>
    /// <param name="pixelSize">The image's native pixel dimensions (carried for parity; iTerm2 scales server-side).</param>
    /// <param name="aspectFree">Which dimension to emit as <c>auto</c> so iTerm2 scales to native aspect (no cell-rounding stretch).</param>
    public ITerm2ImageFragment(ImageData data, Size? displaySize = null, (int Columns, int Rows)? pixelSize = null,
                               AspectFreeDimension aspectFree = AspectFreeDimension.None)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        _pixelSize = pixelSize;
        _displaySize = displaySize ?? data.RequestedSize ?? throw new InvalidOperationException("ImageData.CellSize or displaySize must be provided.");
        _aspectFree = aspectFree;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// iTerm2 inline images live in the <b>cell stream</b>, not on a separate overlay plane:
    /// the image is placed at the cursor, occupies its footprint cells, scrolls with the buffer,
    /// and is erased by overwriting those cells — there is no protocol-level delete command (the
    /// inherited <see cref="IBufferFragment.EmitErase"/> no-op is correct). This MUST be <see cref="FragmentLayer.Cells"/>:
    /// marking it <see cref="FragmentLayer.Overlay"/> (as a refactor briefly did) breaks removal,
    /// because Overlay cells aren't repainted AND there's no delete command — so a moved/scrolled
    /// image leaves a duplicate behind. Kitty is Overlay only because it has a real <c>a=d</c>
    /// delete; iTerm2 and Sixel are both cell-stream images.
    /// </remarks>
    public FragmentLayer Layer => FragmentLayer.Cells;

    /// <summary>The image being transmitted.</summary>
    public ImageData Image => _data;

    /// <inheritdoc/>
    public Size GetSize() => _displaySize;

    /// <inheritdoc/>
    public bool IsSupported(OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return capabilities.Graphics.ITerm2InlineImages;
    }

    /// <inheritdoc/>
    public void Emit(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (_data.Bytes.IsEmpty) return;

        if (capabilities.Protocol.MultiplexerPassthrough)
        {
            // Inside a multiplexer (tmux) that would strip the OSC 1337 sequence — build the
            // payload into a scratch buffer first, then wrap it in DCS tmux passthrough so the
            // outer terminal receives the OSC intact.
            var scratch = new ArrayBufferWriter<byte>();
            WriteOscPayload(scratch);
            TmuxPassthrough.WriteWrapped(output, scratch.WrittenSpan);
        }
        else
        {
            WriteOscPayload(output);
        }
    }

    private void WriteOscPayload(IBufferWriter<byte> output)
    {
        var cellSize = _displaySize;

        // Build the header string up to the ':' separator. ASCII-only; we encode as raw bytes.
        var header = new StringBuilder();
        header.Append("\x1B]1337;File=");
        header.Append("size=").Append(_data.Bytes.Length).Append(';');

        // The aspect-free axis (if any) is emitted as 'auto' with preserveAspectRatio=1 so iTerm2 scales
        // to the image's native aspect in that axis; the pinned axis is fixed in cells (clamped >= 1).
        // No aspect-free axis → pin both and stretch to the exact box (preserveAspectRatio=0). Mirrors
        // KittyImageFragment's c=/r= omission so both protocols size one-dimension-specified images the same.
        switch (_aspectFree)
        {
            case AspectFreeDimension.Columns:
                header.Append("width=auto;");
                header.Append("height=").Append(Math.Max(1, cellSize.Rows)).Append(';');
                header.Append("preserveAspectRatio=1;");
                break;
            case AspectFreeDimension.Rows:
                header.Append("width=").Append(Math.Max(1, cellSize.Columns)).Append(';');
                header.Append("height=auto;");
                header.Append("preserveAspectRatio=1;");
                break;
            default:
                header.Append("width=").Append(Math.Max(1, cellSize.Columns)).Append(';');
                header.Append("height=").Append(Math.Max(1, cellSize.Rows)).Append(';');
                header.Append("preserveAspectRatio=0;");
                break;
        }

        header.Append("inline=1");
        header.Append(':');

        var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        string base64 = Convert.ToBase64String(_data.Bytes.Span);
        var base64Bytes = Encoding.ASCII.GetBytes(base64);
        var terminator = "\x1B\\"u8;

        var dest = output.GetSpan(headerBytes.Length + base64Bytes.Length + terminator.Length);
        int written = 0;
        headerBytes.AsSpan().CopyTo(dest[written..]);
        written += headerBytes.Length;
        base64Bytes.AsSpan().CopyTo(dest[written..]);
        written += base64Bytes.Length;
        terminator.CopyTo(dest[written..]);
        written += terminator.Length;
        output.Advance(written);
    }
    
    /// <inheritdoc/>
    public override string ToString()
        => $"[{nameof(ITerm2ImageFragment)} CellSize={_displaySize} PayloadLength={_data.Bytes.Length} " +
           $"SourceFileName={(_data.SourceFileName != null ? $"'{_data.SourceFileName}'" : "<unknown>")}]";
}
