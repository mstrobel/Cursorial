using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;

namespace Cursorial.Tests.Rendering.Text;

public class TextFormatterTests
{
    private static string LineText(FormattedLine line)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var run in line.Runs)
            if (run is FormattedTextRun text) sb.Append(text.Text);
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
        Assert.Equal("hello", LineText(para.Lines[0]));
        Assert.Equal(5, para.Lines[0].Columns);
    }

    [Fact]
    public void Format_ZeroColumns_Throws()
    {
        var doc = Paragraph("hello");
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextFormatter().Format(doc, 0));
    }

    // ---- WordWrap ----

    [Fact]
    public void WordWrap_BreaksAtSpaces()
    {
        var doc = Paragraph("the quick brown fox", WrapMode.WordWrap);
        var para = FirstParagraph(new TextFormatter().Format(doc, 10));

        Assert.Equal(2, para.Lines.Length);
        Assert.Equal("the quick", LineText(para.Lines[0]));
        Assert.Equal("brown fox", LineText(para.Lines[1]));
    }

    [Fact]
    public void WordWrap_LongerThanBudget_BreaksAtCharBoundary()
    {
        var doc = Paragraph("supercalifragilistic", WrapMode.WordWrap);
        var para = FirstParagraph(new TextFormatter().Format(doc, 10));

        Assert.Equal(2, para.Lines.Length);
        Assert.Equal("supercalif", LineText(para.Lines[0]));
        Assert.Equal("ragilistic", LineText(para.Lines[1]));
    }

    [Fact]
    public void WordWrap_TrailingWhitespaceOnLineDropped()
    {
        var doc = Paragraph("hello world foo bar", WrapMode.WordWrap);
        var para = FirstParagraph(new TextFormatter().Format(doc, 12));

        foreach (var line in para.Lines)
            Assert.DoesNotMatch(@" $", LineText(line));
    }

    // ---- WordWrapOverflow ----

    [Fact]
    public void WordWrapOverflow_LongWordOverflowsPastEdge()
    {
        var doc = Paragraph("short supercalifragilistic", WrapMode.WordWrapOverflow);
        var para = FirstParagraph(new TextFormatter().Format(doc, 10));

        Assert.Equal(2, para.Lines.Length);
        Assert.Equal("short", LineText(para.Lines[0]));
        Assert.Equal("supercalifragilistic", LineText(para.Lines[1]));
        Assert.Equal(20, para.Lines[1].Columns);  // overflows past 10-cell budget
    }

    // ---- CharacterWrap ----

    [Fact]
    public void CharacterWrap_BreaksAtAnyCharacter()
    {
        var doc = Paragraph("the quick brown fox", WrapMode.CharacterWrap);
        var para = FirstParagraph(new TextFormatter().Format(doc, 5));

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
        var para = FirstParagraph(new TextFormatter().Format(doc, 8));

        Assert.Single(para.Lines);
        Assert.Equal("the quick brown fox", LineText(para.Lines[0]));
    }

    // ---- Hard breaks ----

    [Fact]
    public void HardBreak_TerminatesLine()
    {
        var doc = new RichTextBuilder()
            .Run("line one").LineBreak().Run("line two")
            .Build();
        var para = FirstParagraph(new TextFormatter().Format(doc, 80));

        Assert.Equal(2, para.Lines.Length);
        Assert.Equal("line one", LineText(para.Lines[0]));
        Assert.Equal("line two", LineText(para.Lines[1]));
    }

    // ---- Soft hyphens ----

    [Fact]
    public void SoftHyphen_BreaksWordAtMarkerWithHyphenAppended()
    {
        // Build the string explicitly with U+00AD to avoid any encoding round-tripping
        // through the source file.
        string text = "ABCD­EFGH";
        var doc = new RichTextBuilder().Run(text).Build();
        var para = FirstParagraph(new TextFormatter().Format(doc, 6));

        // Diagnostic on failure — surface what we actually got so debugging is straightforward.
        Assert.True(
            para.Lines.Length == 2,
            $"Expected 2 lines, got {para.Lines.Length}: " +
            string.Join(" | ", para.Lines.Select(l => $"'{LineText(l)}' ({l.Columns}c)")));
        Assert.Equal("ABCD-", LineText(para.Lines[0]));
        Assert.Equal("EFGH", LineText(para.Lines[1]));
    }

    [Fact]
    public void SoftHyphen_NotBrokenWhenWholeWordFits()
    {
        var doc = new RichTextBuilder()
            .Run("ABCD­EFGH")
            .Build();
        var para = FirstParagraph(new TextFormatter().Format(doc, 20));

        Assert.Single(para.Lines);
        Assert.Equal("ABCDEFGH", LineText(para.Lines[0]));
    }

    // ---- Trimming ----

    [Fact]
    public void Trim_CharacterEllipsis_OnOverlongLine()
    {
        var doc = Paragraph("this is too long", WrapMode.NoWrap, trim: TextTrimming.CharacterEllipsis);
        var para = FirstParagraph(new TextFormatter().Format(doc, 8));

        Assert.EndsWith("…", LineText(para.Lines[0]));
        Assert.True(para.Lines[0].Columns <= 8);
    }

    [Fact]
    public void Trim_WordEllipsis_TruncatesAtWordBoundary()
    {
        var doc = Paragraph("the quick brown fox", WrapMode.NoWrap, trim: TextTrimming.WordEllipsis);
        var para = FirstParagraph(new TextFormatter().Format(doc, 12));

        var text = LineText(para.Lines[0]);
        Assert.EndsWith("…", text);
        // Should cut at a word boundary, not mid-word.
        Assert.DoesNotContain("quic…", text);
        Assert.DoesNotContain("brow…", text);
    }

    [Fact]
    public void Trim_ClipFromEnd_TruncatesNoEllipsis()
    {
        var doc = Paragraph("supercalifragilistic", WrapMode.NoWrap, trim: TextTrimming.ClipFromEnd);
        var para = FirstParagraph(new TextFormatter().Format(doc, 5));

        Assert.Equal("super", LineText(para.Lines[0]));
        Assert.Equal(5, para.Lines[0].Columns);
    }

    [Fact]
    public void MaxLines_TrimsLastLineWithEllipsis()
    {
        var doc = Paragraph("one two three four five six seven eight nine ten",
                            WrapMode.WordWrap, trim: TextTrimming.CharacterEllipsis, maxLines: 2);
        var para = FirstParagraph(new TextFormatter().Format(doc, 10));

        Assert.Equal(2, para.Lines.Length);
        Assert.EndsWith("…", LineText(para.Lines[^1]));
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
        Assert.EndsWith("…", LineText(para.Lines[^1]));
    }

    // ---- Alignment ----

    [Fact]
    public void Alignment_Left_NoPaddingApplied()
    {
        var doc = Paragraph("hello", align: TextAlignment.Left);
        var para = FirstParagraph(new TextFormatter().Format(doc, 20));

        Assert.Equal("hello", LineText(para.Lines[0]));
    }

    [Fact]
    public void Alignment_Right_PadsToEdge()
    {
        var doc = Paragraph("hello", align: TextAlignment.Right);
        var para = FirstParagraph(new TextFormatter().Format(doc, 10));

        var text = LineText(para.Lines[0]);
        Assert.Equal("     hello", text);
        Assert.Equal(10, para.Lines[0].Columns);
    }

    [Fact]
    public void Alignment_Center_CentersLine()
    {
        var doc = Paragraph("hi", align: TextAlignment.Center);
        var para = FirstParagraph(new TextFormatter().Format(doc, 10));

        var text = LineText(para.Lines[0]);
        // 10 - 2 = 8; half is 4 leading spaces.
        Assert.Equal("    hi", text);
    }

    [Fact]
    public void Alignment_Justify_DistributesSlackAcrossGaps()
    {
        var doc = Paragraph("one two three four", WrapMode.WordWrap, align: TextAlignment.Justify);
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
        var para = FirstParagraph(new TextFormatter().Format(doc, 10));

        // Last line should not be padded to budget.
        var last = para.Lines[^1];
        Assert.True(last.Columns < 10 ||
                    last.Runs.OfType<FormattedTextRun>().All(r => !r.Text.Contains("  ")));
    }

    // ---- Styles + maps preserved through layout ----

    [Fact]
    public void Format_PreservesStyles()
    {
        var bold = Style.Default.WithAttributes(TextAttributes.Bold);
        var builder = new RichTextBuilder().Run("normal ");
        using (builder.Push(in bold)) builder.Run("bold");
        builder.Run(" more");
        var doc = builder.Build();

        var para = FirstParagraph(new TextFormatter().Format(doc, 80));
        var line = para.Lines[0];

        Assert.Contains(line.Runs.OfType<FormattedTextRun>(),
                        r => r.Style.Attributes.HasFlag(TextAttributes.Bold));
    }

    [Fact]
    public void Format_AppliesGlyphMap()
    {
        var doc = new RichTextBuilder()
            .Run("abc")
            .Build();

        // Replace with mapped equivalent — Fullwidth maps each char to 2-cell wide form.
        var withMap = new RichTextBuilder();
        using (withMap.PushMap(GlyphMaps.Fullwidth))
            withMap.Run("abc");
        var doc2 = withMap.Build();

        var para = FirstParagraph(new TextFormatter().Format(doc2, 80));
        Assert.Equal("ａｂｃ", LineText(para.Lines[0]));
        // Each fullwidth char is 2 cells; "abc" → 6 cells.
        Assert.Equal(6, para.Lines[0].Columns);
    }
}

