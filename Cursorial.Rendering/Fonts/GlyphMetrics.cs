using System.Globalization;

using Cursorial.Output;
using Cursorial.Text;

namespace Cursorial.Rendering.Fonts;

/// <summary>
/// Per-cluster advance metrics a glyph source exposes to text <b>layout</b> — the measuring half
/// of a font, decoupled from painting. <see cref="Text.TextFormatter"/> consults metrics to place
/// wrap and trim points in <b>cell units</b>, so any glyph source that can describe its advances —
/// the identity monospace font, OSC 66 scaled text (<see cref="MonospaceFont.GetScaledMetrics"/>),
/// a FIGlet face — participates in formatting on equal footing, instead of being special-cased
/// with a uniform "cell size" multiplier that could never describe variable-width glyphs.
/// </summary>
/// <remarks>
/// All widths are terminal cells. <see cref="LineRows"/> is the rows one formatted line of this
/// glyph source occupies (1 for plain text, <c>Scale</c> for sized text, the face height for
/// FIGlet); the formatter uses it to convert row budgets into line budgets. Implementations must
/// be immutable and cheap to query — metrics are consulted per grapheme cluster during
/// tokenization and splitting.
/// </remarks>
public abstract class GlyphMetrics
{
    /// <summary>The identity metrics: one cell per narrow cluster, two for wide, one row per line.</summary>
    public static GlyphMetrics Monospace { get; } = new MonospaceGlyphMetrics();

    /// <summary>Cells this cluster advances along its line.</summary>
    public abstract int ClusterWidth(ReadOnlySpan<char> cluster);

    /// <summary>Rows one formatted line occupies.</summary>
    public abstract int LineRows { get; }

    /// <summary>Sum of cluster advances over <paramref name="text"/> (which contains no line breaks).</summary>
    public virtual int StringWidth(ReadOnlySpan<char> text)
    {
        int columns = 0;
        var remaining = text;

        while (!remaining.IsEmpty)
        {
            int len = StringInfo.GetNextTextElementLength(remaining);
            if (len <= 0) break;
            columns += ClusterWidth(remaining[..len]);
            remaining = remaining[len..];
        }

        return columns;
    }

    private sealed class MonospaceGlyphMetrics : GlyphMetrics
    {
        public override int ClusterWidth(ReadOnlySpan<char> cluster) => GraphemeWidth.ClusterWidth(cluster);
        public override int LineRows => 1;
        public override int StringWidth(ReadOnlySpan<char> text) => GraphemeWidth.StringWidth(text);
    }
}

/// <summary>
/// Metrics for OSC 66 scaled text (<see cref="TextSizing"/>): per the protocol (w=0), text
/// splits into cells exactly as normal text would and each cell becomes an <c>s×s</c> block —
/// so a cluster advances its natural width × <c>Scale</c> cells, and a line stands
/// <c>Scale</c> rows tall. Mirrors <see cref="Fragments.SizedTextFragment"/>'s footprint math
/// so wrap points the formatter chooses agree with what the fragment will paint.
/// </summary>
/// <remarks>
/// <para>
/// <c>Scale = 0</c> is the record-struct default and means 1. The fractional scale
/// (<c>Numerator/Denominator</c>) deliberately does not participate: per the spec it "does not
/// affect the number of cells the text occupies", only the rendered glyph size within them —
/// half-size sizing at s=2 still advances 2 cells per narrow cluster.
/// </para>
/// <para>
/// <see cref="TextSizing.Width"/> ('w') does not participate either: it is the fixed width of
/// the ENTIRE sequence, not a per-cluster advance, and is unsupported by decision (see
/// <see cref="TextSizingWriter"/>).
/// </para>
/// </remarks>
public sealed class ScaledGlyphMetrics : GlyphMetrics
{
    private readonly int _unit;

    /// <summary>The sizing these metrics describe.</summary>
    public TextSizing Sizing { get; }

    public ScaledGlyphMetrics(in TextSizing sizing)
    {
        Sizing = sizing;
        _unit = sizing.Scale == 0 ? 1 : sizing.Scale;
    }

    public override int ClusterWidth(ReadOnlySpan<char> cluster)
        => GraphemeWidth.ClusterWidth(cluster) * _unit;

    public override int LineRows => _unit;
}

/// <summary>
/// Adapts any <see cref="IGlyphFont"/> into <see cref="GlyphMetrics"/> by measuring clusters
/// through <see cref="IGlyphFont.Measure"/> — the default for fonts without a bespoke metrics
/// implementation. Per-cluster measurement ignores inter-glyph kerning/smushing (a FIGlet face
/// may pack a word tighter than the sum of its glyphs), so word widths are conservative: wrap
/// points come a touch early, never late.
/// </summary>
public sealed class MeasuredGlyphMetrics : GlyphMetrics
{
    private readonly IGlyphFont _font;
    private readonly int _lineRows;

    public MeasuredGlyphMetrics(IGlyphFont font)
    {
        _font = font ?? throw new ArgumentNullException(nameof(font));
        _lineRows = Math.Max(1, font.Measure("M").Rows);
    }

    public override int ClusterWidth(ReadOnlySpan<char> cluster) => _font.Measure(cluster).Columns;

    public override int LineRows => _lineRows;

    public override int StringWidth(ReadOnlySpan<char> text) => _font.Measure(text).Columns;
}
