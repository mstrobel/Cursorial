using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Text;

using FT =  Cursorial.Rendering.Text.FormattedText;

namespace Cursorial.Tests.Rendering.Text;

public class TextFormatterTests
{
    private static string LineText(FormattedLine line, int providedColumns, TextAlignment alignment)
    {
        var sb = new System.Text.StringBuilder();

        for (var index = 0; index < line.Runs.Length; index++)
        {
            var run = line.Runs[index];
            if (run is FormattedTextRun text)
            {
                sb.Append(' ', FT.ComputeAnchorColumn(new(0, 0, providedColumns, 1), text.Text.Length, alignment))
                  .Append(text.Text);
            }
        }

        return sb.ToString();
    }

    private static FormattedParagraph FirstParagraph(FormattedText ft) =>
        (FormattedParagraph) ft.Blocks[0];

    private static RichText Paragraph(string text, WrapMode wrap = WrapMode.WordWrap, TextAlignment align = TextAlignment.Left,
                                       TextTrimming trim = TextTrimming.None, int? maxLines = null) =>
        new RichTextBuilder()
            .Paragraph(wrap: wrap, alignment: align, trim: trim, maxLines: maxLines)
            .Run(text)
            .Build();

    // ---- Empty / trivial cases ----

    [Fact]
    public void Format_EmptyDocument_ReturnsEmpty()
    {
        var formatter = new TextFormatter();
        var result = formatter.Format(RichText.Empty, 80);
        Assert.Empty(result.Blocks);
        Assert.Equal(Size.Empty, result.Size);
    }

    [Fact]
    public void Format_SingleShortRun_FitsOnOneLine()
    {
        var doc = Paragraph("hello");
        var ft = new TextFormatter().Format(doc, 80);
        var para = FirstParagraph(ft);

        Assert.Single(para.Lines);
        Assert.Equal("hello", LineText(para.Lines[0], ft.ProvidedColumns, para.Alignment));
        Assert.Equal(5, para.Lines[0].Columns);
    }

    [Fact]
    public void Format_ZeroColumns_Throws()
    {
        var doc = Paragraph("hello");
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextFormatter().Format(doc, 0));
    }

    /// <summary>A paragraph with no inlines still occupies a row.</summary>
    /// <remarks>Migrated from the characterisation corpus (empty-paragraph).</remarks>
    [Fact]
    public void Format_EmptyParagraph_StillOccupiesARow()
    {
        var doc = new RichTextBuilder().Paragraph().Build();
        var ft = new TextFormatter().Format(doc, 12);
        var para = FirstParagraph(ft);

        Assert.Single(para.Lines);
        Assert.Equal(1, para.Lines[0].Rows);
        Assert.Equal(0, para.Lines[0].Columns);
        Assert.Equal(new Size(0, 1), ft.Size);
    }

    /// <summary>
    /// Whitespace-only content: the packer drops leading whitespace, so this is the empty line.
    /// </summary>
    /// <remarks>Migrated from the characterisation corpus (whitespace-only).</remarks>
    [Fact]
    public void Format_WhitespaceOnly_PacksToTheEmptyLine()
    {
        var doc = new RichTextBuilder().Run("     ").Build();
        var ft = new TextFormatter().Format(doc, 12);
        var para = FirstParagraph(ft);

        Assert.Single(para.Lines);
        Assert.Equal(0, para.Lines[0].Columns);
        Assert.Equal(new Size(0, 1), ft.Size);
    }

    // ---- WordWrap ----

    [Fact]
    public void WordWrap_BreaksAtSpaces()
    {
        var doc = Paragraph("the quick brown fox", WrapMode.WordWrap);
        var ft = new TextFormatter().Format(doc, 10);
        var para = FirstParagraph(ft);

        Assert.Equal(2, para.Lines.Length);
        Assert.Equal("the quick", LineText(para.Lines[0], ft.ProvidedColumns, para.Alignment));
        Assert.Equal("brown fox", LineText(para.Lines[1], ft.ProvidedColumns, para.Alignment));
    }

    [Fact]
    public void WordWrap_LongerThanBudget_BreaksAtCharBoundary()
    {
        var doc = Paragraph("supercalifragilistic", WrapMode.WordWrap);
        var ft = new TextFormatter().Format(doc, 10);
        var para = FirstParagraph(ft);

        Assert.Equal(2, para.Lines.Length);
        Assert.Equal("supercalif", LineText(para.Lines[0], ft.ProvidedColumns, para.Alignment));
        Assert.Equal("ragilistic", LineText(para.Lines[1], ft.ProvidedColumns, para.Alignment));
    }

    [Fact]
    public void WordWrap_TrailingWhitespaceOnLineDropped()
    {
        var doc = Paragraph("hello world foo bar", WrapMode.WordWrap);
        var ft = new TextFormatter().Format(doc, 12);
        var para = FirstParagraph(ft);

        foreach (var line in para.Lines)
            Assert.DoesNotMatch(@" $", LineText(line, ft.ProvidedColumns, para.Alignment));
    }

    // ---- WordWrapOverflow ----

    [Fact]
    public void WordWrapOverflow_LongWordOverflowsPastEdge()
    {
        var doc = Paragraph("short supercalifragilistic", WrapMode.WordWrapOverflow);
        var ft = new TextFormatter().Format(doc, 10);
        var para = FirstParagraph(ft);

        Assert.Equal(2, para.Lines.Length);
        Assert.Equal("short", LineText(para.Lines[0], ft.ProvidedColumns, para.Alignment));
        Assert.Equal("supercalifragilistic", LineText(para.Lines[1], ft.ProvidedColumns, para.Alignment));
        Assert.Equal(20, para.Lines[1].Columns);  // overflows past 10-cell budget
    }

    // ---- CharacterWrap ----

    [Fact]
    public void CharacterWrap_BreaksAtAnyCharacter()
    {
        var doc = Paragraph("the quick brown fox", WrapMode.CharacterWrap);
        var ft = new TextFormatter().Format(doc, 5);
        var para = FirstParagraph(ft);

        // Each line is exactly 5 cells (except possibly the last).
        Assert.True(para.Lines.Length >= 4);
        for (int i = 0; i < para.Lines.Length - 1; i++)
            Assert.True(para.Lines[i].Columns <= 5);
    }

    // ---- NoWrap ----

    [Fact]
    public void NoWrap_KeepsEverythingOnOneLine_OverflowingWidth()
    {
        var doc = Paragraph("the quick brown fox", WrapMode.NoWrap);
        var ft = new TextFormatter().Format(doc, 8);
        var para = FirstParagraph(ft);

        Assert.Single(para.Lines);
        Assert.Equal("the quick brown fox", LineText(para.Lines[0], ft.ProvidedColumns, para.Alignment));
    }

    // ---- Hard breaks ----

    [Fact]
    public void HardBreak_TerminatesLine()
    {
        var doc = new RichTextBuilder()
            .Run("line one").LineBreak().Run("line two")
            .Build();
        var ft = new TextFormatter().Format(doc, 80);
        var para = FirstParagraph(ft);

        Assert.Equal(2, para.Lines.Length);
        Assert.Equal("line one", LineText(para.Lines[0], ft.ProvidedColumns, para.Alignment));
        Assert.Equal("line two", LineText(para.Lines[1], ft.ProvidedColumns, para.Alignment));
    }

    // ---- Soft hyphens ----

    [Fact]
    public void SoftHyphen_BreaksWordAtMarkerWithHyphenAppended()
    {
        // Build the string explicitly with U+00AD to avoid any encoding round-tripping
        // through the source file.
        string text = "ABCD­EFGH";
        var doc = new RichTextBuilder().Run(text).Build();
        var ft = new TextFormatter().Format(doc, 6);
        var para = FirstParagraph(ft);

        // Diagnostic on failure — surface what we actually got, so debugging is straightforward.
        Assert.True(
            para.Lines.Length == 2,
            $"Expected 2 lines, got {para.Lines.Length}: " +
            string.Join(" | ", para.Lines.Select(l => $"'{LineText(l, ft.ProvidedColumns, para.Alignment)}' ({l.Columns}c)")));
        Assert.Equal("ABCD-", LineText(para.Lines[0], ft.ProvidedColumns, para.Alignment));
        Assert.Equal("EFGH", LineText(para.Lines[1], ft.ProvidedColumns, para.Alignment));
    }

    [Fact]
    public void SoftHyphen_NotBrokenWhenWholeWordFits()
    {
        var doc = new RichTextBuilder()
            .Run("ABCD­EFGH")
            .Build();
        var ft = new TextFormatter().Format(doc, 20);
        var para = FirstParagraph(ft);

        Assert.Single(para.Lines);
        Assert.Equal("ABCDEFGH", LineText(para.Lines[0], ft.ProvidedColumns, para.Alignment));
    }

    // ---- Trimming ----

    [Fact]
    public void Trim_CharacterEllipsis_OnOverlongLine()
    {
        var doc = Paragraph("this is too long", WrapMode.NoWrap, trim: TextTrimming.CharacterEllipsis);
        var ft = new TextFormatter().Format(doc, 8);
        var para = FirstParagraph(ft);

        Assert.EndsWith("…", LineText(para.Lines[0], ft.ProvidedColumns, para.Alignment));
        Assert.True(para.Lines[0].Columns <= 8);
    }

    [Fact]
    public void Trim_WordEllipsis_TruncatesAtWordBoundary()
    {
        var doc = Paragraph("the quick brown fox", WrapMode.NoWrap, trim: TextTrimming.WordEllipsis);
        var ft = new TextFormatter().Format(doc, 12);
        var para = FirstParagraph(ft);

        var text = LineText(para.Lines[0], ft.ProvidedColumns, para.Alignment);
        Assert.EndsWith("…", text);
        // Should cut at a word boundary, not mid-word.
        Assert.DoesNotContain("quic…", text);
        Assert.DoesNotContain("brow…", text);
    }

    [Fact]
    public void Trim_ClipFromEnd_TruncatesNoEllipsis()
    {
        var doc = Paragraph("supercalifragilistic", WrapMode.NoWrap, trim: TextTrimming.ClipFromEnd);
        var ft = new TextFormatter().Format(doc, 5);
        var para = FirstParagraph(ft);

        Assert.Equal("super", LineText(para.Lines[0], ft.ProvidedColumns, para.Alignment));
        Assert.Equal(5, para.Lines[0].Columns);
    }

    [Fact]
    public void MaxLines_TrimsLastLineWithEllipsis()
    {
        var doc = Paragraph("one two three four five six seven eight nine ten",
                            WrapMode.WordWrap, trim: TextTrimming.CharacterEllipsis, maxLines: 2);
        var ft = new TextFormatter().Format(doc, 10);
        var para = FirstParagraph(ft);

        Assert.Equal(2, para.Lines.Length);
        Assert.EndsWith("…", LineText(para.Lines[^1], ft.ProvidedColumns, para.Alignment));
    }

    // ---- Document-level MaxRows ----

    [Fact]
    public void MaxRows_DocumentLevel_TruncatesAndAppliesFormatterTrim()
    {
        var doc = Paragraph("alpha beta gamma delta epsilon zeta eta theta", WrapMode.WordWrap);
        var formatter = new TextFormatter { Trim = TextTrimming.CharacterEllipsis };
        var ft = formatter.Format(doc, 10, maxRows: 2);

        var para = FirstParagraph(ft);
        Assert.Equal(2, para.Lines.Length);
        Assert.EndsWith("…", LineText(para.Lines[^1], ft.ProvidedColumns, para.Alignment));
    }

    // ---- Alignment ----

    [Fact]
    public void Alignment_Left_NoPaddingApplied()
    {
        var doc = Paragraph("hello", align: TextAlignment.Left);
        var ft = new TextFormatter().Format(doc, 20);
        var para = FirstParagraph(ft);

        Assert.Equal("hello", LineText(para.Lines[0], ft.ProvidedColumns, para.Alignment));
    }

    [Fact]
    public void Alignment_Right_PadsToEdge()
    {
        var doc = Paragraph("hello", align: TextAlignment.Right);
        var ft = new TextFormatter().Format(doc, 10);
        var para = FirstParagraph(ft);

        var text = LineText(para.Lines[0], ft.ProvidedColumns, para.Alignment);
        Assert.Equal("     hello", text);
        Assert.Equal(10, para.Lines[0].Columns + (ft.ProvidedColumns - para.Lines[0].Columns));
    }

    [Fact]
    public void Alignment_Center_CentersLine()
    {
        var doc = Paragraph("hi", align: TextAlignment.Center);
        var ft = new TextFormatter().Format(doc, 10);
        var para = FirstParagraph(ft);

        var text = LineText(para.Lines[0], ft.ProvidedColumns, para.Alignment);
        // 10 - 2 = 8; half is 4 leading spaces.
        Assert.Equal("    hi", text);
    }

    [Fact]
    public void Alignment_Justify_DistributesSlackAcrossGaps()
    {
        var doc = Paragraph("one two three four 123456789012345", WrapMode.WordWrap, align: TextAlignment.Justify);
        // Width = 14 ("one two three "+ "four"... wait, "one two three" = 13 chars, "four" = 4)
        // budget = 18 → "one two three" fits (13), with " four" we have 18 exactly.
        // Force wrap by using 13-cell budget: "one two three" wraps to one line (13 cells);
        // "four" goes to next line.
        // For justify test: use multi-word line that needs slack.
        var ft = new TextFormatter().Format(doc, 15);
        var para = FirstParagraph(ft);

        // Not the last line should be justified.
        if (para.Lines.Length >= 2)
        {
            Assert.Equal(15, para.Lines[0].Columns);
        }
    }

    [Fact]
    public void Alignment_Justify_LastLineStaysLeft()
    {
        var doc = Paragraph("alpha beta gamma delta", align: TextAlignment.Justify);
        var ft = new TextFormatter().Format(doc, 10);
        var para = FirstParagraph(ft);

        // Last line should not be padded to budget.
        var last = para.Lines[^1];
        Assert.True(last.Columns < 10 ||
                    last.Runs.OfType<FormattedTextRun>().All(r => !r.Text.Contains("  ")));
    }

    // ---- Styles + maps preserved through layout ----

    [Fact]
    public void Format_PreservesStyles()
    {
        var bold = CellStyle.Default.WithAttributes(TextAttributes.Bold);
        var builder = new RichTextBuilder().Run("normal ");
        using (builder.Push(in bold)) builder.Run("bold");
        builder.Run(" more");
        var doc = builder.Build();

        var ft = new TextFormatter().Format(doc, 80);
        var para = FirstParagraph(ft);
        var line = para.Lines[0];

        Assert.Contains(line.Runs.OfType<FormattedTextRun>(),
                        r => r.Style.ResolveFlat().Attributes.HasFlag(TextAttributes.Bold));
    }

    [Fact]
    public void Format_AppliesGlyphMap()
    {
        _ = new RichTextBuilder()
            .Run("abc")
            .Build();

        // Replace with mapped equivalent — Fullwidth maps each char to 2-cell wide form.
        var withMap = new RichTextBuilder();
        using (withMap.PushMap(GlyphMaps.Fullwidth))
            withMap.Run("abc");
        var doc2 = withMap.Build();

        var ft = new TextFormatter().Format(doc2, 80);
        var para = FirstParagraph(ft);
        Assert.Equal("ａｂｃ", LineText(para.Lines[0], ft.ProvidedColumns, para.Alignment));
        // Each fullwidth char is 2 cells; "abc" → 6 cells.
        Assert.Equal(6, para.Lines[0].Columns);
    }

    // ---- The trim ellipsis at an indicator run (maintainer ruling 2026-08-11 #3) ----

    /// <summary>
    /// A cut landing immediately after a surviving INDICATOR run (an access-key mnemonic) yields
    /// a PLAIN ellipsis: the style join skips indicator runs and takes the nearest preceding
    /// non-indicator run's style — "if the indicator glyph survived the trim, I see no reason why
    /// the ellipses should inherit its style." Non-indicator styled runs keep donating their style
    /// (the corpus' trim-ellipsis-in-a-styled-run contract is untouched).
    /// </summary>
    [Fact]
    public void Trim_CharacterEllipsisCutAfterAnIndicatorRun_TakesThePrecedingRunsStyle()
    {
        var pre = BrushedStyle.Identity.Applying(TextAttributes.Strikethrough);
        var cue = BrushedStyle.Identity.Underlining(UnderlineStyle.Single);

        var doc = new RichTextBuilder()
                  .Paragraph(wrap: WrapMode.NoWrap, trim: TextTrimming.CharacterEllipsis)
                  .Run("S", in pre)
                  .IndicatorRun("a", in cue)
                  .Run("ve")
                  .Build();

        var line = FirstParagraph(new TextFormatter { Trim = TextTrimming.CharacterEllipsis }
                                      .Format(doc, 3)).Lines[0];

        Assert.True(line.Trimmed);

        var runs = line.Runs.OfType<FormattedTextRun>().ToArray();
        Assert.Equal(new[] { "S", "a", "…" }, runs.Select(r => r.Text).ToArray());

        Assert.Equal(cue, runs[1].Style);   // the surviving mnemonic keeps its cue
        Assert.True(runs[1].Indicator);
        Assert.Equal(pre, runs[2].Style);   // the ellipsis skips it — the preceding run's style
        Assert.False(runs[2].Indicator);
    }

    /// <summary>The same ruling through <c>AppendEllipsisAtWordBoundary</c>: the word cut strips
    /// the trailing space, the draft ends in the indicator run, and the appended ellipsis still
    /// skips it for style.</summary>
    [Fact]
    public void Trim_WordEllipsisCutAfterAnIndicatorRun_TakesThePrecedingRunsStyle()
    {
        var pre = BrushedStyle.Identity.Applying(TextAttributes.Strikethrough);
        var cue = BrushedStyle.Identity.Underlining(UnderlineStyle.Single);

        var doc = new RichTextBuilder()
                  .Paragraph(wrap: WrapMode.NoWrap, trim: TextTrimming.WordEllipsis)
                  .Run("S", in pre)
                  .IndicatorRun("a", in cue)
                  .Run(" ve")
                  .Build();

        var line = FirstParagraph(new TextFormatter { Trim = TextTrimming.WordEllipsis }
                                      .Format(doc, 4)).Lines[0];

        Assert.True(line.Trimmed);

        var runs = line.Runs.OfType<FormattedTextRun>().ToArray();
        Assert.Equal(new[] { "S", "a", "…" }, runs.Select(r => r.Text).ToArray());

        Assert.Equal(cue, runs[1].Style);   // the surviving mnemonic keeps its cue
        Assert.Equal(pre, runs[2].Style);   // the ellipsis skips it — the preceding run's style
    }

    // ---- Corpus-retirement pins: format-time expansion and per-piece continuation ----

    /// <summary>Tab expansion at format time (TabWidth = 4) around real glyphs — the plain-monospace
    /// lane; the scaled-run budget rule is pinned separately in GlyphRunTests.</summary>
    [Fact]
    public void Format_ExpandsTabs_AroundRealGlyphs()
    {
        var doc = new RichTextBuilder().Run("a\tb\tc").Build();
        var ft = new TextFormatter().Format(doc, 16);
        var para = FirstParagraph(ft);

        Assert.Single(para.Lines);
        Assert.Equal(11, para.Lines[0].Columns);
        Assert.Equal("a    b    c", LineText(para.Lines[0], ft.ProvidedColumns, para.Alignment));
    }

    /// <summary>CJK at the formatter: layout advance is 2 cells per cluster, so a 7-column budget takes
    /// three 2-cell clusters per line and the packer's arithmetic agrees with the painter's cursor.</summary>
    [Fact]
    public void WordWrap_WideGlyphs_WrapAtCellAdvances()
    {
        var doc = new RichTextBuilder().Run("字字字 字字字 字字").Build();
        var ft = new TextFormatter().Format(doc, 7);
        var para = FirstParagraph(ft);

        Assert.Equal(3, para.Lines.Length);
        Assert.Equal(6, para.Lines[0].Columns);
        Assert.Equal(6, para.Lines[1].Columns);
        Assert.Equal(4, para.Lines[2].Columns);
        Assert.Equal("字字字", LineText(para.Lines[0], ft.ProvidedColumns, para.Alignment));
        Assert.Equal("字字", LineText(para.Lines[2], ft.ProvidedColumns, para.Alignment));
    }

    /// <summary>A document whose DefaultStyle states every channel — fg + bg + Bold|Underline + a curly
    /// coloured underline — paints that full set onto every inked cell, with no resolver installed.</summary>
    [Fact]
    public void Paint_DocumentDefaultStyle_EveryChannelReachesTheCells()
    {
        var style = CellStyle.Default
                             .WithForeground(Color.FromRgb(255, 176, 0))
                             .WithBackground(Color.FromRgb(16, 16, 32))
                             .WithAttributes(TextAttributes.Bold | TextAttributes.Underline)
                             .WithUnderlineStyle(UnderlineStyle.Curly)
                             .WithUnderlineColor(Color.FromRgb(0, 128, 128));
        var doc = new RichTextBuilder(style).Run("inherit me").Build();
        var ft = new TextFormatter().Format(doc, 16);

        var buffer = new CellBuffer(16, 2);
        ft.Paint(buffer, new Rect(0, 0, 16, 2), OutputCapabilities.None);

        const string text = "inherit me";
        for (int c = 0; c < text.Length; c++)
        {
            Assert.Equal(text[c].ToString(), buffer[c, 0].Grapheme);
            Assert.Equal(style, buffer[c, 0].Style);
        }
    }

    /// <summary>The OSC 8 target survives onto the continuation piece of a wrapped run — both painted
    /// pieces of one hyperlinked source run carry the same Uri.</summary>
    [Fact]
    public void Paint_HyperlinkRun_KeepsItsUriAcrossTheWrap()
    {
        var builder = new RichTextBuilder();
        using (builder.PushHyperlink("https://example.com/wrapped"))
            builder.Run("aaaa bbbb");
        var ft = new TextFormatter().Format(builder.Build(), 4);

        var buffer = new CellBuffer(4, 2);
        ft.Paint(buffer, new Rect(0, 0, 4, 2), OutputCapabilities.None);

        Assert.Equal("a", buffer[0, 0].Grapheme);
        Assert.Equal("b", buffer[0, 1].Grapheme);
        Assert.Equal("https://example.com/wrapped", buffer[0, 0].Style.Hyperlink.Uri);
        Assert.Equal("https://example.com/wrapped", buffer[0, 1].Style.Hyperlink.Uri);
    }

    /// <summary>A CellStyle-styled run that wraps keeps the run's style on BOTH pieces — the split
    /// machinery must not lose the carrier on the continuation.</summary>
    [Fact]
    public void Paint_StyledRun_BothWrapPiecesKeepTheRunsStyle()
    {
        var style = CellStyle.Default
                             .WithForeground(Color.FromRgb(255, 0, 0))
                             .WithAttributes(TextAttributes.Bold);
        var doc = new RichTextBuilder().Run("aaaa bbbb", style).Build();
        var ft = new TextFormatter().Format(doc, 4);

        var buffer = new CellBuffer(4, 2);
        ft.Paint(buffer, new Rect(0, 0, 4, 2), OutputCapabilities.None);

        Assert.Equal("a", buffer[0, 0].Grapheme);
        Assert.Equal("b", buffer[0, 1].Grapheme);
        Assert.Equal(style, buffer[0, 0].Style);
        Assert.Equal(style, buffer[0, 1].Style);   // the continuation piece, same carrier
    }
}

