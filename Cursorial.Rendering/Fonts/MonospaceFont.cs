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
    public Style EnsureCompatibleStyle(in Style style) => style;

    /// <inheritdoc/>
    public GlyphMetrics GetMetrics() => GlyphMetrics.Monospace;

    /// <summary>
    /// Metrics for text this font would render at the given OSC 66 <paramref name="sizing"/> —
    /// scaled text has no glyph font of its own (the terminal scales the cells), so the identity
    /// font is where layout comes to ask "how wide is a cluster at this sizing".
    /// See <see cref="ScaledGlyphMetrics"/> for the advance rules.
    /// </summary>
    public GlyphMetrics GetScaledMetrics(in TextSizing sizing)
        => sizing.IsNormal ? GlyphMetrics.Monospace : new ScaledGlyphMetrics(sizing);

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
    public Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, in Style style)
    {
        return PaintCore(buffer, column, row, text, style, null);
    }

    public Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text,
                      GlyphStyleProvider styleProvider)
    {
        return PaintCore(buffer, column, row, text, default, styleProvider);
    }

    private static Size PaintCore(CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, in Style style,
                                  GlyphStyleProvider? styleProvider = null)
    {
        if (buffer.IsEmpty || text.IsEmpty) return Size.Empty;

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
            var clusterStyle = styleProvider?.Invoke(col, row) ?? style;
            int written = buffer.Set(col, row, cluster.ToString(), clusterStyle);
            col += written;
        }

        return new Size(Math.Max(0, col - column), 1);
    }
}
