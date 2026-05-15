using System.Globalization;
using Cursorial.Output;
using Cursorial.Text;

namespace Cursorial.Rendering.Fonts;



/// <summary>
/// The identity <see cref="IGlyphFont"/>: each grapheme cluster of the input occupies one
/// terminal cell (or two for wide glyphs). This is the "plain text" rendering path and is
/// what almost every consumer wants by default.
/// </summary>
/// <remarks>
/// Grapheme segmentation uses <see cref="StringInfo.GetTextElementEnumerator(string)"/>, so
/// emoji clusters, CJK, accented Latin, and ZWJ sequences are all treated as single visual
/// units. Wide-cell handling is the buffer's responsibility — this font just calls
/// <see cref="CellBuffer.Set"/> per cluster and reads back the advance.
/// </remarks>
public sealed class MonospaceFont : IGlyphFont
{
    /// <summary>The default monospace instance — stateless, freely shareable.</summary>
    public static MonospaceFont Default { get; } = new();

    /// <inheritdoc/>
    public Size Measure(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return Size.Empty;

        int columns = 0;
        var remaining = text;
        while (!remaining.IsEmpty)
        {
            int len = StringInfo.GetNextTextElementLength(remaining);
            if (len <= 0) break;
            columns += GraphemeWidth.ClusterWidth(remaining[..len]);
            remaining = remaining[len..];
        }
        return new Size(columns, 1);
    }

    /// <inheritdoc/>
    public Size Paint(CellBuffer buffer, int row, int column, ReadOnlySpan<char> text, in Style style)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (text.IsEmpty) return Size.Empty;

        // Out-of-bounds anchor: nothing to paint. Don't throw; consumers should be free to ask
        // a font to "paint at a position that's now off-screen after a resize."
        if (row < 0 || row >= buffer.Rows || column >= buffer.Columns) return Size.Empty;

        int col = column;
        var remaining = text;

        while (!remaining.IsEmpty)
        {
            if (col >= buffer.Columns) break;

            int len = StringInfo.GetNextTextElementLength(remaining);
            if (len <= 0) break;
            var cluster = remaining[..len];
            remaining = remaining[len..];

            if (col < 0)
            {
                // Could happen if caller passed a negative anchor — advance until we're in
                // bounds. We still consume clusters to keep the painted-vs-asked accounting
                // honest.
                col += GraphemeWidth.ClusterWidth(cluster);
                continue;
            }

            // CellBuffer.Set takes a string; materialize the cluster once per call.
            // For most graphemes this is 1–4 chars — short enough that an alternative
            // span-based Set is a future micro-optimization.
            int written = buffer.Set(row, col, cluster.ToString(), style);
            col += written;
        }

        return new Size(Math.Max(0, col - column), 1);
    }
}
