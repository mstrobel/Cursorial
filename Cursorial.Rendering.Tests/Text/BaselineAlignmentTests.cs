using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Text;

namespace Cursorial.Tests.Rendering.Text;

/// <summary>
/// Where a run sits inside a band that mixes glyph METRICS — the case the last-resort trim
/// indicator created (a one-cell "…" painted by the terminal's own font beside a multi-row FIGlet
/// face, see <see cref="TrimIndicatorTests"/>) and the case
/// <see cref="VerticalTextAlignment.Baseline"/> exists to place correctly.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> Run placement was bottom-of-band: <c>runRow = bandRow + (line.Rows -
/// run.LineRows)</c>. A face with a descender puts its glyph BODIES above the band's bottom row —
/// <c>standard.flf</c> is 6 rows with its baseline on row index 4 and one descender row beneath —
/// so the one-cell indicator landed UNDER the headline instead of beside it. It rendered correctly
/// on exactly one bundled face, <c>ansi-shadow</c> (7/7, zero descent), which is why it survived.
/// </para>
/// <para>
/// The two existing last-resort tests (<c>FaceWithoutAnyCandidate_FallsBackToTheMonospaceIdentity</c>
/// / <c>_StillPaintsVisibleInk</c>) assert ink and COLUMN, never the row — they pass with the bug
/// present. Everything here pins the ROW, on a face where <c>Height != Baseline</c>.
/// </para>
/// </remarks>
public class BaselineAlignmentTests
{
    /// <summary>
    /// A 6-row face with <c>standard.flf</c>'s exact geometry (<c>flf2a$ 6 5 …</c>): five body
    /// rows, one descender row. Neither U+2026 nor '.' is defined, so the formatter's LAST RESORT
    /// fires and the indicator is painted by the monospace identity beside the face's glyphs.
    /// Hand-built on purpose — every bundled face defines '.', so none of them can reach the last
    /// resort at all.
    /// </summary>
    private static FigletFont BuildDescenderFace() =>
        new("descender", '$', 6, FigletLayoutMode.None,
            new Dictionary<uint, FigletGlyph>
            {
                [' '] = new(' ', ["  ", "  ", "  ", "  ", "  ", "  "], '$'),
                ['A'] = new('A', ["AA", "AA", "AA", "AA", "AA", "  "], '$'),
            },
            baseline: 5);

    /// <summary>
    /// An 8-row face with <c>big.flf</c>'s geometry (<c>flf2a$ 8 6 …</c>): six body rows and TWO
    /// descender rows. Every other fixture here has a one-row descent, where "the baseline row"
    /// and "one above the bottom" are the same number — this face is the one that tells the
    /// implemented rule (drop by the baseline DIFFERENCE) apart from a hard-coded single
    /// descender row.
    /// </summary>
    private static FigletFont BuildDeepDescenderFace() =>
        new("deep", '$', 8, FigletLayoutMode.None,
            new Dictionary<uint, FigletGlyph>
            {
                [' '] = new(' ', ["  ", "  ", "  ", "  ", "  ", "  ", "  ", "  "], '$'),
                ['A'] = new('A', ["AA", "AA", "AA", "AA", "AA", "AA", "  ", "  "], '$'),
            },
            baseline: 6);

    /// <summary>The same face with NO descender (<c>ansi-shadow</c>'s geometry): baseline == height.</summary>
    private static FigletFont BuildFlatFace() =>
        new("flat", '$', 6, FigletLayoutMode.None,
            new Dictionary<uint, FigletGlyph>
            {
                [' '] = new(' ', ["  ", "  ", "  ", "  ", "  ", "  "], '$'),
                ['A'] = new('A', ["AA", "AA", "AA", "AA", "AA", "AA"], '$'),
            },
            baseline: 6);

    private static FormattedParagraph FormatFiglet(IGlyphFont face, string text, int columns)
    {
        var tf = new TextFormatter { Wrap = WrapMode.NoWrap, Trim = TextTrimming.CharacterEllipsis };
        var rt = new RichTextBuilder().Figlet(text, face).Build();
        var ft = tf.Format(rt, columns, capabilities: OutputCapabilities.None);
        return Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
    }

    // ---- The last-resort indicator ----

    [Fact]
    public void LastResortEllipsis_SitsOnTheBandsBaselineRow_NotTheDescenderRow()
    {
        var face = BuildDescenderFace();
        const int columns = 9;

        Assert.Equal(6, face.Height);
        Assert.Equal(5, face.Baseline);    // a COUNT — the baseline's row INDEX is 4
        Assert.Equal(1, face.Descender);

        var p = FormatFiglet(face, "AAAAAAAA", columns);
        var line = Assert.Single(p.Lines);
        var indicator = LastRun(line);

        Assert.True(line.Trimmed);
        Assert.Null(indicator.Source.Font);   // the last resort fired: the TERMINAL's font paints it
        Assert.Equal(1, indicator.CellWidth);
        Assert.Equal(6, line.Rows);

        var buffer = Paint(p, columns);
        int indicatorColumn = line.Columns - indicator.CellWidth;

        // The indicator owns its column outright (the face's glyphs stop to its left), so the rows
        // carrying ink there are exactly the rows the indicator painted.
        Assert.Equal(new[] { face.Baseline - 1 }, InkRows(buffer, indicatorColumn));
    }

    [Fact]
    public void LastResortEllipsis_OnAZeroDescentFace_StaysOnTheBottomRow()
    {
        // Baseline alignment must not MOVE the case that was already right: with no descender the
        // baseline row IS the bottom row, and ansi-shadow-shaped faces render exactly as before.
        var face = BuildFlatFace();
        const int columns = 9;

        var p = FormatFiglet(face, "AAAAAAAA", columns);
        var line = Assert.Single(p.Lines);
        var indicator = LastRun(line);

        var buffer = Paint(p, columns);
        Assert.Equal(new[] { line.Rows - 1 }, InkRows(buffer, line.Columns - indicator.CellWidth));
    }

    [Fact]
    public void FacePaintedIndicator_CarriesNoOverride_AndKeepsTheParagraphsRule()
    {
        // The override is for MIXED metrics only. When the face can draw the indicator itself,
        // the run is the same source as its neighbours, there is nothing to reconcile, and the
        // author's paragraph rule stands untouched.
        var face = new FigletFont("ascii-only", '$', 3, FigletLayoutMode.None,
                                  new Dictionary<uint, FigletGlyph>
                                  {
                                      [' '] = new(' ', ["  ", "  ", "  "], '$'),
                                      ['.'] = new('.', ["  ", "  ", ".."], '$'),
                                      ['A'] = new('A', ["AA", "AA", "AA"], '$'),
                                  });

        var p = FormatFiglet(face, "AAAAAAAA", columns: 12);
        var indicator = LastRun(Assert.Single(p.Lines));

        Assert.Equal(TextFormatter.DerivedEllipsis, indicator.Text);
        Assert.Same(face, indicator.Source.Font);
        Assert.Null(indicator.VerticalAlignment);
    }

    [Fact]
    public void LastResortEllipsis_OnATwoRowDescent_DropsByTheBaselineDifference_NotOneRow()
    {
        // Every other placement fixture has a ONE-row descent, where "the baseline row" is also
        // "one row up from the bottom" — an implementation that hard-coded `slack - 1` would pass
        // all of them. big.flf's geometry (8/6) separates the three candidate answers: baseline
        // row 5, `slack - 1` row 6, bottom row 7.
        var face = BuildDeepDescenderFace();
        const int columns = 9;

        Assert.Equal(8, face.Height);
        Assert.Equal(6, face.Baseline);
        Assert.Equal(2, face.Descender);

        var p = FormatFiglet(face, "AAAAAAAA", columns);
        var line = Assert.Single(p.Lines);
        var indicator = LastRun(line);

        Assert.True(line.Trimmed);
        Assert.Null(indicator.Source.Font);
        Assert.Equal(8, line.Rows);
        Assert.Equal(new[] { 5 }, InkRows(Paint(p, columns), line.Columns - indicator.CellWidth));
    }

    [Fact]
    public void LastResortIndicator_OutranksEveryParagraphRule_NotJustTheDefaultBottom()
    {
        // The override's contract (TextFormatter.EllipsisChoice.ToRun) is that the SYNTHESIZED
        // indicator's placement holds "under every paragraph rule — Top or Center would float it
        // mid-glyph just as wrongly as Bottom buries it". Every other last-resort test runs at the
        // default Bottom, so a precedence flipped to "an explicit author rule beats a synthesized
        // one" would leave them all green while putting the indicator on row 0 of a Top-aligned
        // FIGlet paragraph.
        var face = BuildDescenderFace();

        foreach (var rule in new[]
                             {
                                 VerticalTextAlignment.Top,
                                 VerticalTextAlignment.Center,
                                 VerticalTextAlignment.Bottom,
                                 VerticalTextAlignment.Baseline
                             })
        {
            var p = FormatTrimmedFigletParagraph(face, rule, columns: 9);
            var line = Assert.Single(p.Lines);
            var indicator = LastRun(line);

            Assert.Equal(rule, p.VerticalAlignment);
            Assert.Equal(VerticalTextAlignment.Baseline, indicator.VerticalAlignment);
            Assert.Null(indicator.Source.Font);

            // face.Baseline - 1 == 4 under every rule; the paragraph's own runs still follow it.
            Assert.Equal(new[] { face.Baseline - 1 },
                         InkRows(Paint(p, 9), line.Columns - indicator.CellWidth));
        }
    }

    /// <summary>
    /// A FIGlet-sourced run inside a real <see cref="TextParagraph"/> — the only way to pair a
    /// face with a NON-default paragraph rule, since the <c>FigletBlock</c> sugar
    /// (<see cref="FormatFiglet"/>) always formats at the default Bottom.
    /// </summary>
    private static FormattedParagraph FormatTrimmedFigletParagraph(IGlyphFont face, VerticalTextAlignment alignment,
                                                                   int columns)
    {
        var rt = new RichTextBuilder().Paragraph().Run("AAAAAAAA", new GlyphSource(face)).Build();
        var paragraph = (TextParagraph) rt.Blocks[0] with
                        {
                            VerticalAlignment = alignment,
                            Wrap = WrapMode.NoWrap,
                            Trim = TextTrimming.CharacterEllipsis
                        };
        var ft = new TextFormatter().Format(new RichText([paragraph]), columns, capabilities: OutputCapabilities.None);

        return Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
    }

    [Fact]
    public void LastResortIndicator_CarriesTheBaselineOverride()
    {
        // The formatter owns the placement of a run IT synthesized against the author's request —
        // the author asked for a face-painted indicator and got a foreign-metric one.
        var indicator = LastRun(Assert.Single(FormatFiglet(BuildDescenderFace(), "AAAAAAAA", 9).Lines));

        Assert.Equal(VerticalTextAlignment.Baseline, indicator.VerticalAlignment);
    }

    // ---- The paragraph-level rule ----

    private static FormattedParagraph FormatMixed(VerticalTextAlignment alignment)
    {
        var face = BuildDescenderFace();
        var rt = new RichTextBuilder().Paragraph().Run("x ").Run("A", new GlyphSource(face)).Build();
        var paragraph = (TextParagraph) rt.Blocks[0] with { VerticalAlignment = alignment };
        var ft = new TextFormatter().Format(new RichText([paragraph]), 20, capabilities: OutputCapabilities.None);

        return Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
    }

    [Fact]
    public void BaselineAlignment_LinesUpAShortRunWithTheFacesBaselineRow()
    {
        var p = FormatMixed(VerticalTextAlignment.Baseline);

        Assert.Equal(6, p.Size.Rows);
        Assert.Equal(new[] { 4 }, InkRows(Paint(p, 20), column: 0));   // 'x' on the baseline row, not row 5
    }

    [Fact]
    public void BottomAlignment_StillMeansBottom()
    {
        // The author's explicit rule is not second-guessed: Bottom puts the short run on the
        // band's last row, descender or no descender.
        var p = FormatMixed(VerticalTextAlignment.Bottom);

        Assert.Equal(new[] { 5 }, InkRows(Paint(p, 20), column: 0));
    }

    [Fact]
    public void TopAndCenterAlignment_AreUnaffected()
    {
        Assert.Equal(new[] { 0 }, InkRows(Paint(FormatMixed(VerticalTextAlignment.Top), 20), column: 0));
        Assert.Equal(new[] { 3 }, InkRows(Paint(FormatMixed(VerticalTextAlignment.Center), 20), column: 0));
    }

    [Fact]
    public void BaselineAlignment_NeverPushesARunOutOfItsBand()
    {
        // Two faces of the same height but different baselines: the band is 6 rows, the deeper
        // baseline is 6, and the shallow-baselined face would want to drop a row it does not have.
        // Clamping keeps its descender row inside the band — imperfect alignment beats ink
        // bleeding into the next line.
        var deep = BuildFlatFace();        // 6 rows, baseline 6
        var shallow = BuildDescenderFace(); // 6 rows, baseline 5

        var rt = new RichTextBuilder()
                .Paragraph()
                .Run("A", new GlyphSource(shallow))
                .Run("A", new GlyphSource(deep))
                .Build();

        var paragraph = (TextParagraph) rt.Blocks[0] with { VerticalAlignment = VerticalTextAlignment.Baseline };
        var ft = new TextFormatter().Format(new RichText([paragraph]), 20, capabilities: OutputCapabilities.None);
        var p = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));

        Assert.Equal(6, p.Size.Rows);

        var buffer = Paint(p, 20);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, InkRows(buffer, column: 0));       // shallow face, unmoved
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, InkRows(buffer, column: 2));    // deep face, unmoved
    }

    // ---- Helpers ----

    private static FormattedTextRun LastRun(FormattedLine line)
        => Assert.IsType<FormattedTextRun>(line.Runs[^1]);

    private static CellBuffer Paint(FormattedParagraph p, int columns)
    {
        int rows = Math.Max(1, p.Size.Rows);
        var buffer = new CellBuffer(columns, rows);

        ((IContent) new FormattedText([p], p.Size, columns)).Paint(
            buffer.AsView(), new Rect(0, 0, columns, rows), default, OutputCapabilities.None);

        return buffer;
    }

    /// <summary>Row indices carrying visible ink in <paramref name="column"/>.</summary>
    private static int[] InkRows(CellBuffer buffer, int column)
    {
        var rows = new List<int>();

        for (int r = 0; r < buffer.Rows; r++)
            if (buffer[column, r].Grapheme is { Length: > 0 } g && g.Trim().Length > 0)
                rows.Add(r);

        return rows.ToArray();
    }
}
