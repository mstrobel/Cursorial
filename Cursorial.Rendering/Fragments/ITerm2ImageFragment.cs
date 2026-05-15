using System.Buffers;
using System.Text;
using Cursorial.Output.Capabilities;

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
/// with <c>preserveAspectRatio=0</c> so the image stretches to fit exactly. <c>inline=1</c>
/// renders the image where the cursor sits; without it the terminal offers a download. The
/// <c>size=</c> field carries the byte count of the original payload (not the base64-expanded
/// version) — iTerm2 uses it for the progress indicator.
/// </para>
/// </remarks>
public sealed class ITerm2ImageFragment : IBufferFragment
{
    private readonly ImageData _data;

    /// <summary>Construct an iTerm2 inline image fragment.</summary>
    public ITerm2ImageFragment(ImageData data)
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
        return capabilities.Graphics.ITerm2InlineImages;
    }

    /// <inheritdoc/>
    public void Emit(int row, int column, IBufferWriter<byte> output, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (_data.Bytes.IsEmpty) return;

        // Build the header string up to the ':' separator. ASCII-only; we encode as raw bytes.
        var header = new StringBuilder();
        header.Append("\x1B]1337;File=");
        header.Append("size=").Append(_data.Bytes.Length).Append(';');
        header.Append("width=").Append(_data.CellSize.Columns).Append(';');
        header.Append("height=").Append(_data.CellSize.Rows).Append(';');
        header.Append("preserveAspectRatio=0;");
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
}
