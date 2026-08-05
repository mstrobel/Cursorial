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

    /// <summary>Cells this cluster advances along its line, measured in isolation.</summary>
    public abstract int ClusterWidth(ReadOnlySpan<char> cluster);

    /// <summary>
    /// Cells this cluster ADDS when it follows <paramref name="previous"/> within one painted
    /// piece — the kerning-aware advance. Context-free sources (the identity, scaled text)
    /// return <see cref="ClusterWidth"/>; a FIGlet face returns the post-smush increment, so
    /// accumulated widths equal <c>Measure(text)</c> exactly instead of overestimating by one
    /// junction per glyph. An empty <paramref name="previous"/> means the piece's first cluster.
    /// </summary>
    public virtual int Advance(ReadOnlySpan<char> previous, ReadOnlySpan<char> cluster)
        => ClusterWidth(cluster);

    /// <summary>Rows one formatted line occupies.</summary>
    public abstract int LineRows { get; }

    /// <summary>
    /// Rows from the top of one formatted line down to and INCLUDING its baseline row — a COUNT
    /// in <c>[1, LineRows]</c>, never a 0-based index (see <see cref="IGlyphFont.Baseline"/>).
    /// The formatter uses it to line up runs of DIFFERENT metrics inside one band
    /// (<see cref="Text.VerticalTextAlignment.Baseline"/>).
    /// </summary>
    /// <remarks>
    /// The default — the line's bottom row — is right for every source whose glyphs have no
    /// descent below their box: the identity cell face (1 row, baseline 1) and OSC 66 scaled text
    /// (an <c>s×s</c> block of cells the TERMINAL fills; the protocol exposes no baseline, and the
    /// block's own bottom row is where neighbouring normal-size text meets it). Only a face with
    /// real vertical metrics — a FIGlet face, via <see cref="MeasuredGlyphMetrics"/> — overrides.
    /// </remarks>
    public virtual int Baseline => LineRows;

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
    private readonly int _baseline;

    // Pair-advance cache: FIGlet junction kerning depends only on the adjacent pair (Measure
    // accumulates max(0, width - overlap(prev, glyph)) per junction), so (prev, cluster) → cells
    // is exact and tiny. Concurrent — one metrics instance serves every formatter thread.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Previous, string Cluster), int> _advances = new();

    public MeasuredGlyphMetrics(IGlyphFont font)
    {
        _font = font ?? throw new ArgumentNullException(nameof(font));
        _lineRows = Math.Max(1, font.Measure("M").Rows);

        // The face's baseline is a COUNT of rows from the top; clamped into the line box we
        // actually measured, since a decorator can report a height its inner face's baseline
        // knows nothing about (and a hand-built face could disagree outright).
        _baseline = Math.Clamp(font.Baseline, 1, _lineRows);
    }

    public override int ClusterWidth(ReadOnlySpan<char> cluster) => _font.Measure(cluster).Columns;

    public override int Advance(ReadOnlySpan<char> previous, ReadOnlySpan<char> cluster)
    {
        if (previous.IsEmpty)
            return ClusterWidth(cluster);

        // WHITESPACE IS RIGID: a word gap advances its full width and breaks the kerning chain
        // on both sides (maintainer decision 2026-08-02). Faces like SmallSlant interlock
        // diagonally and kern 2-3 columns into each side of even a hardblank-carrying space —
        // font-legal, but a single-call paint then joins words while the piece-per-atom
        // formatter keeps them apart, and the mono truth says gaps hold. Words still kern
        // internally at the face's own rules; layout, caret math, and paint all share this
        // contract, so text renders identically however it is split into pieces.
        if (IsRigidGap(previous) || IsRigidGap(cluster))
            return ClusterWidth(cluster);

        return _advances.GetOrAdd((previous.ToString(), cluster.ToString()), static (key, font) =>
        {
            // The pair's kerned width minus the lead glyph alone = what the trailing cluster adds.
            int pair = font.Measure(key.Previous + key.Cluster).Columns;
            int lead = font.Measure(key.Previous).Columns;
            return Math.Max(0, pair - lead);
        }, _font);
    }

    private static bool IsRigidGap(ReadOnlySpan<char> cluster)
        => cluster.Length == 1 && char.IsWhiteSpace(cluster[0]);

    public override int LineRows => _lineRows;

    /// <inheritdoc/>
    public override int Baseline => _baseline;

    public override int StringWidth(ReadOnlySpan<char> text) => _font.Measure(text).Columns;
}
