using System.Globalization;
using Cursorial.Output;
using Cursorial.Rendering.Media;
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
    public string DisplayName => "Monospace";

    /// <inheritdoc/>
    /// <remarks>Always true: this face owns no glyph table — it hands the cluster to the terminal
    /// and the terminal's own font draws it. Stated explicitly (rather than inherited from the
    /// interface) so callers holding the concrete type can ask.</remarks>
    public bool HasGlyph(uint codepoint) => true;

    /// <inheritdoc/>
    /// <remarks>A cell face's line box is one row tall and that row IS the baseline row — there is
    /// nowhere below it to hang a descender. Stated explicitly (rather than inherited from the
    /// interface's default) for the same reason as <see cref="HasGlyph"/>: a default interface
    /// member is not callable through the concrete type, and this face is very often held as
    /// <see cref="MonospaceFont"/>.</remarks>
    public int Baseline => 1;

    /// <inheritdoc cref="Baseline"/>
    public int Ascender => 1;

    /// <inheritdoc cref="Baseline"/>
    public int Descender => 0;

    /// <inheritdoc/>
    public CellStyle EnsureCompatibleStyle(in CellStyle style) => style;

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
    public Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, in CellStyle style)
    {
        return PaintCore(buffer, column, row, text, style, delta: null, bounds: default);
    }

    /// <inheritdoc/>
    public Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text,
                      in CellStyle baseStyle, in StyleDeltaTemplate delta, in Rect bounds)
    {
        // A uniform template resolves to the same value everywhere, so fold it once here and take the flat
        // path — one cluster loop with no per-cell resolve, identical to painting a plain style.
        return delta.IsUniform
                   ? PaintCore(buffer, column, row, text, delta.Resolve(column, row, bounds).ApplyTo(baseStyle),
                               delta: null, bounds: default)
                   : PaintCore(buffer, column, row, text, baseStyle, delta, bounds);
    }

    private static Size PaintCore(CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, in CellStyle style,
                                  in StyleDeltaTemplate? delta, in Rect bounds)
    {
        if (buffer.IsEmpty || text.IsEmpty) return Size.Empty;

        // Out-of-bounds anchor: nothing to paint. Don't throw; consumers should be free to ask
        // a font to "paint at a position that's now off-screen after a resize."
        //
        // The edges are the view's LOCAL addressable window, not [0, Columns) × [0, Rows): a view
        // re-based by WithOrigin (any negative push translate — a scrolled editor, a negatively
        // margined element painting inline in its parent's zone) moves that window in local space,
        // and the anchor this face is asked to paint at is documented as negative-capable.
        if (row < buffer.LocalRowStart || row >= buffer.LocalRowEnd || column >= buffer.LocalColumnEnd)
            return Size.Empty;

        int col = column;
        var remaining = text;

        while (!remaining.IsEmpty)
        {
            if (col >= buffer.LocalColumnEnd) break;

            int len = StringInfo.GetNextTextElementLength(remaining);
            if (len <= 0) break;
            var cluster = remaining[..len];
            remaining = remaining[len..];

            int width = GraphemeWidth.ClusterWidth(cluster);
            if (width < 1) width = 1;

            // Left of the window: consume the cluster and advance by its LAYOUT width to keep the
            // painted-vs-asked accounting honest — and, more importantly, to keep the run moving.
            // Advancing by Set's return instead would stall on the first rejected cell (Set returns
            // 0 there) and every remaining cluster would retry that cell, dropping the whole run
            // rather than painting its visible tail.
            if (col >= buffer.LocalColumnStart)
            {
                // CellBuffer.Set takes a string; materialize the cluster once per call.
                // For most graphemes this is 1–4 chars — short enough that an alternative
                // span-based Set is a future micro-optimization.
                // Resolved per cluster, not once up front: a non-uniform template is position-dependent, so
                // hoisting the resolve out of the loop would paint one cell's answer across the whole run.
                // (The uniform case never reaches here — the overload above folds it and passes null.)
                var clusterStyle = delta is null ? style : delta.Value.Resolve(col, row, bounds).ApplyTo(style);
                int written = buffer.Set(col, row, cluster.ToString(), clusterStyle);

                // The one case where the surface knows better than the layout: a wide glyph at the
                // window's right edge degrades to a blank single, freeing the column it did not take.
                if (written > 0) width = written;
            }

            col += width;
        }

        return new Size(Math.Max(0, col - column), 1);
    }
}
