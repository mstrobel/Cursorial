namespace Cursorial.Rendering.Fragments;

/// <summary>
/// An encoded image plus the cell footprint it should occupy on the terminal. Carries the raw
/// bytes (PNG / JPEG / GIF) directly to the image fragment that knows how to transmit them via
/// a particular protocol; the renderer / fragment combination handles base64 encoding, chunking,
/// and protocol framing.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CellSize"/> is the rectangle in cell coordinates the image will occupy. Both
/// Kitty's protocol and iTerm2's inline-image protocol accept cell-based sizing, so the same
/// value drives both wire formats. Callers that need precise pixel control (e.g., aligning to a
/// known display-pixels-per-cell ratio) can compute cells from the negotiated
/// <c>WindowCapabilities.SizeQueryInPixels</c> response and pass the result here.
/// </para>
/// <para>
/// <b>Buffer ownership.</b> The fragment retains a reference to <see cref="Bytes"/> across
/// frames — re-emitting the same image on each render isn't done (registration is one-shot per
/// <see cref="CellBuffer.AddFragment"/> call), but the bytes need to live at least until the
/// next render. Avoid passing buffers borrowed from a stack frame or a pool you intend to return.
/// </para>
/// </remarks>
public sealed class ImageData
{
    /// <summary>Construct an image carrier from a byte buffer.</summary>
    public ImageData(ReadOnlyMemory<byte> bytes, ImageFormat format, Size cellSize)
    {
        if (cellSize.Columns < 0 || cellSize.Rows < 0 || cellSize is { Columns: 0, Rows: 0 })
        {
            throw new ArgumentOutOfRangeException(
                nameof(cellSize),
                $"Cell size must be positive in at least one dimensions, and never negative; got {cellSize}.");
        }

        Bytes = bytes;
        Format = format;
        CellSize = cellSize;
    }

    /// <summary>Construct an image carrier from a byte array.</summary>
    public ImageData(byte[] bytes, ImageFormat format, Size cellSize)
        : this((ReadOnlyMemory<byte>) (bytes ?? throw new ArgumentNullException(nameof(bytes))), format, cellSize) {}

    /// <summary>The encoded image bytes (PNG / JPEG / GIF — see <see cref="Format"/>).</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>Encoding of <see cref="Bytes"/>.</summary>
    public ImageFormat Format { get; }

    /// <summary>The cell rectangle this image occupies.</summary>
    public Size CellSize { get; }
}
