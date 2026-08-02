using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Text;

namespace Cursorial.Tests.Rendering.Text;

/// <summary>
/// The glyph-metrics layer (review round, 2026-08): layout consults <see cref="GlyphMetrics"/>
/// for every advance, so sized text and FIGlet faces participate in wrapping/trimming on the
/// same cell-unit arithmetic as plain text — no per-block "cell size" multiplier, no unit
/// mixing. Sizing semantics are pinned to the OSC 66 spec as maintainer-verified 2026-08-02:
/// scale multiplies the natural cell split; the fraction never changes the footprint; 'w' is
/// unsupported by decision.
/// </summary>
public class TextFormatterMetricsTests
{
    // ---- ScaledGlyphMetrics units ----

    [Fact]
    public void ScaledMetrics_ScaleMultipliesNaturalWidths()
    {
        var m = MonospaceFont.Default.GetScaledMetrics(new TextSizing(Scale: 2));

        Assert.Equal(2, m.ClusterWidth("A"));
        Assert.Equal(4, m.ClusterWidth("日")); // wide cluster: natural 2 × scale 2
        Assert.Equal(2, m.LineRows);
        Assert.Equal(10, m.StringWidth("HELLO"));
    }

    [Fact]
    public void ScaledMetrics_ZeroScale_IsIdentity()
    {
        // Scale = 0 is the record-struct default (TextSizing.Normal) and means 1.
        var m = MonospaceFont.Default.GetScaledMetrics(default);

        Assert.Equal(1, m.ClusterWidth("A"));
        Assert.Equal(1, m.LineRows);
    }

    [Fact]
    public void ScaledMetrics_FractionNeverChangesTheFootprint()
    {
        // Per the OSC 66 spec, n/d only shrinks the glyph WITHIN its cells — s=2:n=1:d=2 still
        // occupies 2×2 cells per narrow cluster, and s=1:n=1:d=2 occupies 1×1 (the old
        // integer-division reading collapsed this to a zero footprint, deleting every space).
        var half = MonospaceFont.Default.GetScaledMetrics(new TextSizing(Scale: 2, Numerator: 1, Denominator: 2));
        Assert.Equal(2, half.ClusterWidth("A"));
        Assert.Equal(2, half.LineRows);

        var subCell = MonospaceFont.Default.GetScaledMetrics(new TextSizing(Scale: 1, Numerator: 1, Denominator: 2));
        Assert.Equal(1, subCell.ClusterWidth("A"));
        Assert.Equal(1, subCell.LineRows);
    }

    [Fact]
    public void ScaledMetrics_WidthParameterIgnored_AndAgreesWithGetGlyphSize()
    {
        // 'w' is the whole-sequence width per the spec and unsupported by decision — the metrics
        // and the fragment's GetGlyphSize must tell the same story.
        var sizing = new TextSizing(Scale: 2, Width: 3);
        var m = MonospaceFont.Default.GetScaledMetrics(sizing);

        Assert.Equal(2, m.ClusterWidth("A"));
        Assert.Equal((2, 2), sizing.GetGlyphSize());
    }

    // ---- Sized-text blocks through the formatter ----
    // The block form is sugar over a one-run paragraph (proposal-glyph-runs); scaled geometry
    // requires a scale-capable terminal — on lesser tiers the source RESOLVES to the fallback
    // face at layout time, exactly as the old ScaledText.Measure-based path did.

    private static OutputCapabilities ScaleCaps()
        => OutputCapabilities.None with { TextSizing = new Cursorial.Output.Capabilities.TextSizingCapabilities(Width: true, Scale: true) };

    private static FormattedParagraph FormatSized(
        string text, TextSizing sizing, int columns, TextFormatter? formatter = null)
    {
        var rt = new RichTextBuilder().SizedText(text, sizing).Build();
        var ft = (formatter ?? new TextFormatter()).Format(rt, columns, capabilities: ScaleCaps());
        return Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
    }

    private static string LinePlainText(FormattedParagraph p, int index)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var run in p.Lines[index].Runs)
        {
            if (run is FormattedTextRun text)
                sb.Append(text.Text);
        }

        return sb.ToString().TrimEnd();
    }

    [Fact]
    public void SizedBlock_WrapsAtScaledWidths()
    {
        // "HELLO WORLD" at scale 2: each word is 10 cells, the space 2 — 22 > 12 wraps at the
        // word boundary into two 2-row bands.
        var block = FormatSized("HELLO WORLD", new TextSizing(Scale: 2), columns: 12);

        Assert.Equal(2, block.Lines.Length);
        Assert.Equal("HELLO", LinePlainText(block, 0));
        Assert.Equal("WORLD", LinePlainText(block, 1));
        Assert.All(block.Lines, l => Assert.Equal(2, l.Rows));
        Assert.Equal(4, block.Size.Rows);
        Assert.False(block.HasTrimmedLines);
    }

    [Fact]
    public void SizedBlock_AuthoredLineBreaks_AreHonored()
    {
        // '\n' (and normalized CRLF) in the source is a hard break — the old tokenizer treated
        // it as a zero-width word character and fused the words into one. Columns chosen so the
        // UNSPLIT fused word would char-wrap into different (dirtier) lines than a real break.
        var block = FormatSized("AAA\r\nBB", new TextSizing(Scale: 2), columns: 6);

        Assert.Equal(2, block.Lines.Length);
        Assert.Equal("AAA", LinePlainText(block, 0));
        Assert.Equal("BB", LinePlainText(block, 1));
    }

    [Fact]
    public void SizedBlock_TrimsWithScaledEllipsis()
    {
        // NoWrap keeps "ABCDE" one 10-cell line; CharacterEllipsis at 7 columns budgets
        // 7 − 2 (the ellipsis is ALSO a scaled cluster) = 5 → two clusters (4 cells) survive.
        var tf = new TextFormatter { Wrap = WrapMode.NoWrap, Trim = TextTrimming.CharacterEllipsis };
        var block = FormatSized("ABCDE", new TextSizing(Scale: 2), columns: 7, tf);

        Assert.Equal("AB…", LinePlainText(block, 0));
        Assert.True(block.Lines[0].Trimmed);
        Assert.True(block.HasTrimmedLines);
    }

    // ---- FIGlet participation ----

    [Fact]
    public void FigletBlock_WrapsAtFaceMetrics()
    {
        var face = FigletFonts.Mini;
        var oneWord = face.Measure("HI").Columns;

        // Two words that fit individually but not together: the block wraps at the face's
        // per-glyph widths into two face-height line bands (the old code clipped one line).
        var rt = new RichTextBuilder().Figlet("HI HI", face).Build();
        var ft = new TextFormatter().Format(rt, oneWord + 2, capabilities: OutputCapabilities.None);

        var block = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
        Assert.Equal(2, block.Lines.Length);
        Assert.Equal("HI", LinePlainText(block, 0));
        Assert.Equal("HI", LinePlainText(block, 1));
        Assert.All(block.Lines, l => Assert.Equal(face.Height, l.Rows));
        Assert.Equal(2 * face.Height, block.Size.Rows);
    }

    // ---- CharacterWrap split points (plain 1×1 — the arm changed for ordinary text too) ----

    [Fact]
    public void CharacterWrap_SplitsMidWordOnSparseLines()
    {
        // "abc " fills 4 of 9 columns (< 2/3): CharacterWrap splits the next word mid-word to
        // keep the line dense. (This width sits in the band where the pre-fix complement guard
        // word-wrapped instead, producing "abc" / "defghijkl" / "m".)
        var rt = new RichTextBuilder().Paragraph(wrap: WrapMode.CharacterWrap).Run("abc defghijklm").Build();
        var ft = new TextFormatter().Format(rt, 9, capabilities: OutputCapabilities.None);

        var p = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
        Assert.Equal("abc defgh", LineText(p, 0));
        Assert.Equal("ijklm", LineText(p, 1));
    }

    [Fact]
    public void CharacterWrap_PrefersWordWrapWhenLineIsTwoThirdsFull()
    {
        // "abcdef " fills 7 of 9 columns (≥ 2/3): splitting "ghi" mid-word would strand 2 chars —
        // the whole word wraps instead. (The pre-fix guard implemented the complement and
        // engaged word-wrap from 1/3 full, wrapping far too eagerly.)
        var rt = new RichTextBuilder().Paragraph(wrap: WrapMode.CharacterWrap).Run("abcdef ghi").Build();
        var ft = new TextFormatter().Format(rt, 9, capabilities: OutputCapabilities.None);

        var p = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
        Assert.Equal("abcdef", LineText(p, 0));
        Assert.Equal("ghi", LineText(p, 1));
    }

    // ---- FormatPlainText contract ----

    [Fact]
    public void FormatPlainText_JoinsWithBareLf_AndDropsNonTextBlocks()
    {
        var rt = new RichTextBuilder()
                .Paragraph().Run("one two three four")
                .HorizontalRule()
                .Paragraph().Run("five")
                .Build();

        var plain = new TextFormatter().FormatPlainText(rt, availableColumns: 10);

        Assert.Equal("one two\nthree four\nfive", plain);
        Assert.DoesNotContain('\r', plain);
    }

    // ---- Trimmed-flag propagation ----

    [Fact]
    public void WordEllipsisTrim_SetsTheTrimmedFlag()
    {
        // TruncateAt used to build an unflagged draft — WordEllipsis trims reported untrimmed
        // and the trimmed-content tooltip never offered the hidden text.
        var tf = new TextFormatter { Wrap = WrapMode.NoWrap, Trim = TextTrimming.WordEllipsis };
        var rt = new RichTextBuilder().Paragraph(wrap: WrapMode.NoWrap, trim: TextTrimming.WordEllipsis)
                                      .Run("alpha beta gamma delta").Build();
        var ft = tf.Format(rt, 12, capabilities: OutputCapabilities.None);

        Assert.True(ft.HasTrimmedLines);

        // The LINE-level flag matters independently (the sized lane and FormattedLine consumers
        // read it) — the paragraph flag alone would mask an unflagged TruncateAt draft.
        var p = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
        Assert.True(p.Lines[0].Trimmed);
    }

    [Fact]
    public void MaxLinesDrop_SetsTheTrimmedFlag_EvenWithTrimNone()
    {
        // Dropping lines IS trimming: with Trim=None the forced ellipsis is a no-op, but the
        // hidden lines must still raise the flag.
        var rt = new RichTextBuilder().Paragraph(maxLines: 1).Run("aa bb").Build();
        var ft = new TextFormatter().Format(rt, 2, capabilities: OutputCapabilities.None);

        var p = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
        Assert.Single(p.Lines);
        Assert.True(ft.HasTrimmedLines);
    }

    [Fact]
    public void DocumentRowCap_DroppingAWholeBlock_SetsTheTrimmedFlag()
    {
        var rt = new RichTextBuilder()
                .Paragraph().Run("one")
                .Paragraph().Run("two")
                .Build();

        var ft = new TextFormatter().Format(rt, 20, maxRows: 1, capabilities: OutputCapabilities.None);

        Assert.True(ft.HasTrimmedLines);
    }

    private static string LineText(FormattedParagraph p, int index)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var run in p.Lines[index].Runs)
        {
            if (run is FormattedTextRun text)
                sb.Append(text.Text);
        }

        return sb.ToString().TrimEnd();
    }
}
