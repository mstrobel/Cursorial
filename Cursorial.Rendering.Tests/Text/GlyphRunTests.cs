using System.Buffers;
using System.Text;

using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Text;
using Cursorial.Text;

namespace Cursorial.Tests.Rendering.Text;

/// <summary>
/// Glyph runs (proposal-glyph-runs Phase 1): sized text and FIGlet as first-class styled runs.
/// A run's <see cref="GlyphSource"/> drives every advance through the shared pipeline — mixed
/// lines become bands (max run height), bands stack, runs align vertically within their band by
/// the paragraph rule, and non-cell runs paint per piece through the OSC-66-or-fallback tree.
/// </summary>
public class GlyphRunTests
{
    private static OutputCapabilities CapsWithScale()
        => OutputCapabilities.None with
           {
               TextSizing = new TextSizingCapabilities(Width: true, Scale: true),
           };

    private static readonly GlyphSource Big2 = new(null, new TextSizing(Scale: 2));

    private static FormattedParagraph FormatOne(RichText rt, int columns, TextFormatter? tf = null)
    {
        // Scaled geometry requires a scale-capable terminal: sources RESOLVE against the
        // capabilities at layout time, so under OutputCapabilities.None a scaled run would
        // honestly fall back to its (bundled) face and measure at FACE metrics.
        var ft = (tf ?? new TextFormatter()).Format(rt, columns, capabilities: CapsWithScale());
        return Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
    }

    // ---- Line geometry ----

    [Fact]
    public void MixedLine_IsOneBand_MaxOfRunHeights()
    {
        var rt = new RichTextBuilder().Paragraph().Run("tiny ").Run("BIG", Big2).Build();
        var p = FormatOne(rt, columns: 40);

        var line = Assert.Single(p.Lines);
        Assert.Equal(2, line.Rows);            // the band takes the tallest run
        Assert.Equal(2, p.Size.Rows);          // one band = two rows
        Assert.Equal(5 + 6, line.Columns);     // "tiny " at 1×, "BIG" at 2 cells/cluster
    }

    [Fact]
    public void WrappedMixedRuns_StackBands()
    {
        // "aaaa " (5 cells) + "XY" at scale 2 (4 cells) exceeds 8 columns — the sized word wraps
        // whole onto its own band. Line 1 is a 1-row band, line 2 a 2-row band; the paragraph is
        // their SUM, not the line count.
        var rt = new RichTextBuilder().Paragraph().Run("aaaa ").Run("XY", Big2).Build();
        var p = FormatOne(rt, columns: 8);

        Assert.Equal(2, p.Lines.Length);
        Assert.Equal(1, p.Lines[0].Rows);
        Assert.Equal(2, p.Lines[1].Rows);
        Assert.Equal(3, p.Size.Rows);
    }

    [Fact]
    public void SizedRun_WrapsAtItsOwnAdvances()
    {
        // A lone scaled word wider than the budget char-splits at SCALED cluster widths: 5
        // clusters × 2 cells = 10 > 6 → head takes 3 clusters (6 cells), tail wraps.
        var rt = new RichTextBuilder().Paragraph().Run("ABCDE", Big2).Build();
        var p = FormatOne(rt, columns: 6);

        Assert.Equal(2, p.Lines.Length);
        Assert.Equal(6, p.Lines[0].Columns);
        Assert.Equal(4, p.Lines[1].Columns);
        Assert.All(p.Lines, l => Assert.Equal(2, l.Rows));
    }

    [Fact]
    public void EllipsisJoiningASizedRun_MeasuresAtTheRunsScale()
    {
        // NoWrap + CharacterEllipsis: "ABCDE" at scale 2 is 10 cells in a 7-column budget. The
        // ellipsis joins the sized run, so it costs 2 cells (not 1): 7 − 2 = 5 → two whole
        // clusters (4 cells) survive, and the line lands at 6 cells, never 7+.
        var tf = new TextFormatter { Wrap = WrapMode.NoWrap, Trim = TextTrimming.CharacterEllipsis };
        var rt = new RichTextBuilder().Paragraph(wrap: WrapMode.NoWrap, trim: TextTrimming.CharacterEllipsis)
                                      .Run("ABCDE", Big2).Build();
        var p = FormatOne(rt, columns: 7, tf);

        var line = Assert.Single(p.Lines);
        Assert.True(line.Trimmed);
        Assert.Equal(6, line.Columns);

        var ellipsis = Assert.IsType<FormattedTextRun>(line.Runs[^1]);
        Assert.Equal("…", ellipsis.Text);
        Assert.Same(Big2, ellipsis.Source); // measures AND paints at the joined run's source
    }

    // ---- Vertical alignment within the band ----

    private static string PaintRow(FormattedParagraph p, int columns, int row)
    {
        var buffer = new CellBuffer(columns, p.Size.Rows);
        ((IContent)new FormattedText([p], p.Size, columns)).Paint(
            buffer.AsView(), new Rect(0, 0, columns, p.Size.Rows), default, CapsWithScale());

        var sb = new StringBuilder();
        for (int c = 0; c < columns; c++)
            sb.Append(buffer[c, row].Grapheme is { Length: > 0 } t ? t : " ");
        return sb.ToString();
    }

    private static FormattedParagraph FormatAligned(VerticalTextAlignment alignment)
    {
        var rt = new RichTextBuilder()
                .Paragraph()
                .Run("lo ").Run("HI", Big2)
                .Build();

        var paragraph = (TextParagraph)rt.Blocks[0] with { VerticalAlignment = alignment };
        var ft = new TextFormatter().Format(new RichText([paragraph]), 20, capabilities: CapsWithScale());
        return Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
    }

    [Fact]
    public void IdentityRuns_SitOnTheBottomRow_ByDefault()
    {
        var p = FormatAligned(default); // Bottom is the enum default by declaration order

        Assert.Equal(VerticalTextAlignment.Bottom, p.VerticalAlignment);
        Assert.StartsWith("lo ", PaintRow(p, 20, row: 1)); // bottom row of the 2-row band
        Assert.StartsWith("   ", PaintRow(p, 20, row: 0)); // top row empty under the identity run
    }

    [Fact]
    public void TopAlignment_PutsIdentityRunsOnTheTopRow()
    {
        var p = FormatAligned(VerticalTextAlignment.Top);

        Assert.StartsWith("lo ", PaintRow(p, 20, row: 0));
        Assert.StartsWith("   ", PaintRow(p, 20, row: 1));
    }

    // ---- Painting / emission ----

    [Fact]
    public void SizedRunPiece_EmitsItsOwnOsc66Fragment()
    {
        // On a scale-capable terminal, a sized run's painted piece attaches a SizedTextFragment
        // at the piece's own anchor — the renderer emits it after the cell pass.
        var rt = new RichTextBuilder().Paragraph().Run("at ").Run("BIG", Big2).Build();
        var ft = new TextFormatter().Format(rt, 20, capabilities: CapsWithScale());

        var buffer = new CellBuffer(20, ft.Size.Rows);
        ((IContent)ft).Paint(buffer.AsView(), new Rect(0, 0, 20, ft.Size.Rows), default, CapsWithScale());

        var w = new ArrayBufferWriter<byte>();
        new FrameRenderer(CapsWithScale()).Render(buffer, w);
        var output = Encoding.UTF8.GetString(w.WrittenSpan);

        Assert.Contains("at", output);              // the identity piece renders as cells
        Assert.Contains("\x1b]66;s=2;BIG", output); // the sized piece rides OSC 66
    }

    [Fact]
    public void ScaledSource_OnADumbTerminal_ResolvesToItsFallbackFace()
    {
        // Tier parity with the old block path: under capabilities without OSC 66, a scaled run
        // resolves AT LAYOUT TIME to its fallback face (bundled when none is given), so the
        // paragraph measures at face metrics — a figlet headline, not clipped monospace.
        var rt = new RichTextBuilder().Paragraph().Run("HI", Big2).Build();
        var ft = new TextFormatter().Format(rt, 40, capabilities: OutputCapabilities.None);

        var p = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
        var run = Assert.IsType<FormattedTextRun>(Assert.Single(Assert.Single(p.Lines).Runs));
        Assert.NotNull(run.Source.Font);              // resolved to a face
        Assert.True(run.Source.Sizing.IsNormal);      // the unsupported sizing is gone
        Assert.True(p.Lines[0].Rows >= 2);            // face-height band, not 1-row monospace
    }

    [Fact]
    public void DocumentRowCap_DropsWholeBands()
    {
        // maxRows 2 over [1-row band, 2-row band]: the second band cannot HALF-fit — it drops
        // whole, and the surviving line reports the trim.
        var rt = new RichTextBuilder().Paragraph().Run("top").LineBreak().Run("BIG", Big2).Build();
        var ft = new TextFormatter().Format(rt, 20, maxRows: 2, capabilities: CapsWithScale());

        var p = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
        Assert.Single(p.Lines);
        Assert.Equal(1, p.Size.Rows);
        Assert.True(ft.HasTrimmedLines);
    }

    // ---- Kerning pins (maintainer report 2026-08-02: figlets sized one glyph too large) ----

    [Fact]
    public void FigletWord_MeasuresExactly_NotOneJunctionPerGlyphTooWide()
    {
        // MeasuredGlyphMetrics used to sum ISOLATED glyph widths, but FigletFont.Measure smushes
        // every junction — the excess grew with each added glyph. Advance(prev, cluster) is the
        // kerning-pair decomposition, and it must reproduce Measure exactly.
        var face = FigletFonts.Standard;
        var word = "HELLO";
        var kerned = face.Measure(word).Columns;
        int isolated = 0;
        foreach (var ch in word) isolated += face.Measure(ch.ToString()).Columns;
        Assert.True(kerned < isolated, "the face must kern for this pin to bite");

        var rt = new RichTextBuilder().Figlet(word, face).Build();
        var ft = new TextFormatter().Format(rt, 200, capabilities: OutputCapabilities.None);
        var p = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));

        Assert.Equal(kerned, Assert.Single(p.Lines).Columns);
        Assert.Equal(kerned, ((FormattedTextRun)p.Lines[0].Runs[0]).CellWidth); // StringWidth agrees
    }

    [Fact]
    public void GraphemeLayout_CaretColumns_FollowTheKernedAdvances()
    {
        // The editor-side twin (the TextBox caret drifted ahead of the glyphs): cumulative caret
        // columns must land on the kerned prefix widths, and the total must equal Measure.
        var face = FigletFonts.Standard;
        var metrics = face.GetMetrics();
        var text = "HELLO";

        var layout = Cursorial.Rendering.Text.GraphemeLayout.Build(text, metrics);

        Assert.Equal(face.Measure(text).Columns, layout.TotalColumns);
        Assert.Equal(face.Measure("HE").Columns, layout.ColumnOf(2)); // prefix, kerned
    }

    [Fact]
    public void WordGaps_SurviveKerning_AtEveryLayer()
    {
        // Maintainer report: spaces vanished unless a caret split happened to break the smush
        // chain. Two protections now hold: a PLAIN-BLANK space glyph is a word gap kerning never
        // crosses (pinned against a hand-built font in FigletFontTests — Standard's own space is
        // HARDBLANK-protected, the format's native mechanism, so its gap survives with normal
        // kerning tucked around the hardblank), and the formatted layer is piece-additive: word
        // atoms and space atoms paint independently, so the line width is exactly their sum.
        var face = FigletFonts.Standard;
        int a = face.Measure("A").Columns, sp = face.Measure(" ").Columns, b = face.Measure("B").Columns;
        Assert.True(sp > 0, "the space glyph must have width for this pin to bite");

        // The gap survives the face's own kerning: spaced words are wider than kerned letters.
        Assert.True(face.Measure("A B").Columns > face.Measure("AB").Columns);

        var rt = new RichTextBuilder().Figlet("A B", face).Build();
        var ft = new TextFormatter().Format(rt, 200, capabilities: OutputCapabilities.None);
        var p2 = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
        Assert.Equal(a + sp + b, Assert.Single(p2.Lines).Columns);
    }

    // ---- Review-round pins (wf_5257daa8) ----

    [Fact]
    public void CenterAlignment_RoundsTowardTheBottom()
    {
        var p = FormatAligned(VerticalTextAlignment.Center);

        // Band Rows=2, identity run slack=1: Center rounds toward the BOTTOM per the enum's
        // contract — the identity text sits on row 1, exactly like Bottom for a 1-cell slack.
        Assert.StartsWith("lo ", PaintRow(p, 20, row: 1));
    }

    [Fact]
    public void WordEllipsisTruncation_KeepsTheBandHeight()
    {
        // TruncateAt used to rebuild the draft with raw Runs.Add, leaving Rows=1 while keeping a
        // 2-row scaled run — the sized piece then painted one row ABOVE its band (or vanished at
        // row -1 under the default Bottom alignment at the top of a buffer).
        var tf = new TextFormatter { Wrap = WrapMode.NoWrap, Trim = TextTrimming.WordEllipsis };
        var rt = new RichTextBuilder().Paragraph(wrap: WrapMode.NoWrap, trim: TextTrimming.WordEllipsis)
                                      .Run("HI ", Big2).Run("ab cdefghijk").Build();
        var ft = tf.Format(rt, 14, capabilities: CapsWithScale());

        var p = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
        var line = Assert.Single(p.Lines);
        Assert.True(line.Trimmed);
        Assert.Equal(2, line.Rows);      // the kept scaled run still owns the band
        Assert.Equal(2, p.Size.Rows);
    }

    [Fact]
    public void GlyphSourceEquality_IgnoresTheMetricsCache()
    {
        var a = new GlyphSource(null, new TextSizing(Scale: 2));
        var b = new GlyphSource(null, new TextSizing(Scale: 2));

        int hashBefore = a.GetHashCode();
        _ = a.Metrics; // resolve the cache on one side only

        Assert.Equal(a, b);                          // synthesized field equality would say false
        Assert.Equal(hashBefore, a.GetHashCode());   // ...and the hash must not mutate when the cache fills
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void TabInsideAScaledRun_BudgetsAtTheSourcesWidth()
    {
        // A tab expands to TabWidth spaces IN THE RUN'S GLYPHS: at scale 2 with TabWidth 4 the
        // atom must budget 8 cells — matching what the emitted run will actually paint.
        var tf = new TextFormatter { Wrap = WrapMode.NoWrap, TabWidth = 4 };
        var rt = new RichTextBuilder().Paragraph(wrap: WrapMode.NoWrap).Run("A\tB", Big2).Build();
        var ft = tf.Format(rt, 40, capabilities: CapsWithScale());

        var p = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
        Assert.Equal(2 + 8 + 2, Assert.Single(p.Lines).Columns);
    }

    [Fact]
    public void Justify_StretchesOnlyIdentityGaps()
    {
        // A SCALED gap run must not receive plain ' ' chars — each would paint 2 cells while
        // justification's accounting assumes 1, blowing past the very budget it fills. With no
        // identity gap available, the line stays unjustified rather than lying about its width.
        // The line must WRAP naturally (a hard-break line is never justified, the last-line rule).
        var rt = new RichTextBuilder()
                .Paragraph(alignment: TextAlignment.Justify)
                .Run("aa").Run(" ", Big2).Run("bb cccccccc")
                .Build();

        var ft = new TextFormatter().Format(rt, 12, capabilities: CapsWithScale());
        var p = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));

        Assert.True(p.Lines.Length >= 2); // "cccccccc" wrapped away; line 0 is justified-eligible
        var scaledGap = p.Lines[0].Runs.OfType<FormattedTextRun>().Single(r => ReferenceEquals(r.Source, Big2));
        Assert.Equal(" ", scaledGap.Text);       // untouched — no injected identity spaces
        Assert.True(p.Lines[0].Columns <= 12);
    }

    [Fact]
    public void RowCap_FirstBandTallerThanBudget_KeepsNothingButFlagsTheTrim()
    {
        // Reporting more rows than maxRows breaks the cap's contract, and the painter clips a
        // too-tall band whole — an honest empty paragraph beats a blank block claiming space.
        var rt = new RichTextBuilder().Paragraph().Run("BIG", Big2).Build();
        var ft = new TextFormatter().Format(rt, 20, maxRows: 1, capabilities: CapsWithScale());

        var p = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
        Assert.Empty(p.Lines);
        Assert.Equal(0, p.Size.Rows);
        Assert.True(ft.HasTrimmedLines);
    }

    [Fact]
    public void EllipsisReclip_NeverOverflowsTheBudget()
    {
        // Budget is estimated with the line-END run's ellipsis width (identity, 1 cell); the
        // clip lands on the SCALED run, whose ellipsis is 2 cells — without the re-clip the line
        // came out 6 cells in a 5-column budget.
        var tf = new TextFormatter { Wrap = WrapMode.NoWrap, Trim = TextTrimming.CharacterEllipsis };
        var rt = new RichTextBuilder().Paragraph(wrap: WrapMode.NoWrap, trim: TextTrimming.CharacterEllipsis)
                                      .Run("AB", Big2).Run("xyz").Build();
        var ft = tf.Format(rt, 5, capabilities: CapsWithScale());

        var p = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
        var line = Assert.Single(p.Lines);
        Assert.True(line.Trimmed);
        Assert.True(line.Columns <= 5, $"line is {line.Columns} cells in a 5-column budget");
    }

    [Fact]
    public void ResolveFor_PrefersTheExplicitFallbackFace()
    {
        var face = FigletFonts.Mini;
        var source = new GlyphSource(face, new TextSizing(Scale: 2));

        var resolved = source.ResolveFor(OutputCapabilities.None);

        Assert.Same(face, resolved.Font);
        Assert.True(resolved.Sizing.IsNormal);
    }

    [Fact]
    public void HeterogeneousWord_SplitsAtEachPiecesOwnAdvances()
    {
        // One word spanning identity and scaled sources: the char-split walks each piece at its
        // OWN cluster widths. "ab" (2 cells) + "CDE" at scale 2 (6 cells) = 8 > 5 on an empty
        // line: head keeps "ab" + "C" (2+2=4 <= 5, "D" would be 6), tail carries "DE".
        var rt = new RichTextBuilder().Paragraph(wrap: WrapMode.CharacterWrap)
                                      .Run("ab").Run("CDE", Big2).Build();
        var ft = new TextFormatter().Format(rt, 5, capabilities: CapsWithScale());

        var p = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
        Assert.Equal(2, p.Lines.Length);
        Assert.Equal(4, p.Lines[0].Columns);
        Assert.Equal(4, p.Lines[1].Columns);
        Assert.Equal(2, p.Lines[0].Rows); // the scaled piece keeps the band on BOTH lines
        Assert.Equal(2, p.Lines[1].Rows);
    }
}
