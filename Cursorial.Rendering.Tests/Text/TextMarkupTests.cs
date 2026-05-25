using Cursorial.Output;
using Cursorial.Rendering.Text;

namespace Cursorial.Tests.Rendering.Text;

public class TextMarkupTests
{
    private static TextParagraph FirstParagraph(RichText rt) => (TextParagraph) rt.Blocks[0];

    private static TextRun FirstRun(RichText rt) => (TextRun) FirstParagraph(rt).Inlines[0];

    [Fact]
    public void Parse_PlainText_ProducesSingleRun()
    {
        var rt = TextMarkup.Parse("hello world");

        Assert.Equal("hello world", FirstRun(rt).Text);
    }

    [Fact]
    public void Parse_Empty_ProducesEmptyDocument()
    {
        Assert.True(TextMarkup.Parse("").IsEmpty);
    }

    [Fact]
    public void Parse_BoldTag_SetsBoldAttribute()
    {
        var rt = TextMarkup.Parse("[b]hello[/b]");
        var run = FirstRun(rt);

        Assert.Equal("hello", run.Text);
        Assert.True(run.Style.Attributes.HasFlag(TextAttributes.Bold));
    }

    [Fact]
    public void Parse_NestedStyles_Compose()
    {
        var rt = TextMarkup.Parse("[b][i]both[/i][/b]");
        var run = FirstRun(rt);

        Assert.True(run.Style.Attributes.HasFlag(TextAttributes.Bold));
        Assert.True(run.Style.Attributes.HasFlag(TextAttributes.Italic));
    }

    [Fact]
    public void Parse_ForegroundNamedColor()
    {
        var rt = TextMarkup.Parse("[fg=red]text[/fg]");
        var run = FirstRun(rt);

        Assert.Equal(ColorKind.Palette, run.Style.Foreground.Kind);
        Assert.Equal(1, run.Style.Foreground.PaletteIndex);
    }

    [Fact]
    public void Parse_ForegroundPaletteIndex()
    {
        var rt = TextMarkup.Parse("[fg=42]text[/fg]");
        var run = FirstRun(rt);

        Assert.Equal(42, run.Style.Foreground.PaletteIndex);
    }

    [Fact]
    public void Parse_ForegroundHexShort()
    {
        var rt = TextMarkup.Parse("[fg=#f00]text[/fg]");
        var run = FirstRun(rt);

        Assert.Equal(ColorKind.Rgb, run.Style.Foreground.Kind);
        Assert.Equal(255, run.Style.Foreground.Red);
        Assert.Equal(0, run.Style.Foreground.Green);
        Assert.Equal(0, run.Style.Foreground.Blue);
    }

    [Fact]
    public void Parse_ForegroundHexFull()
    {
        var rt = TextMarkup.Parse("[fg=#1a2b3c]text[/fg]");
        var run = FirstRun(rt);

        Assert.Equal(0x1A, run.Style.Foreground.Red);
        Assert.Equal(0x2B, run.Style.Foreground.Green);
        Assert.Equal(0x3C, run.Style.Foreground.Blue);
    }

    [Fact]
    public void Parse_Background()
    {
        var rt = TextMarkup.Parse("[bg=blue]text[/bg]");
        var run = FirstRun(rt);
        Assert.Equal(4, run.Style.Background.PaletteIndex);
    }

    [Fact]
    public void Parse_Hyperlink()
    {
        var rt = TextMarkup.Parse("[link=https://example.com]click[/link]");
        var run = FirstRun(rt);

        Assert.Equal("https://example.com", run.Hyperlink);
    }

    [Fact]
    public void Parse_FontFullwidth()
    {
        var rt = TextMarkup.Parse("[font=fullwidth]abc[/font]");
        var run = FirstRun(rt);

        Assert.Same(GlyphMaps.Fullwidth, run.Map);
    }

    [Fact]
    public void Parse_LineBreak()
    {
        var rt = TextMarkup.Parse("a[br/]b");
        var inlines = FirstParagraph(rt).Inlines;

        Assert.Equal(3, inlines.Length);
        Assert.Equal("a", ((TextRun) inlines[0]).Text);
        Assert.IsType<LineBreak>(inlines[1]);
        Assert.Equal("b", ((TextRun) inlines[2]).Text);
    }

    [Fact]
    public void Parse_HorizontalRule_Default()
    {
        var rt = TextMarkup.Parse("[hr/]");
        var hr = (HorizontalRule) rt.Blocks[0];

        Assert.Equal("─", hr.Glyph);
    }

    [Fact]
    public void Parse_HorizontalRule_NamedStyle()
    {
        var rt = TextMarkup.Parse("[hr=heavy/]");
        var hr = (HorizontalRule) rt.Blocks[0];

        Assert.Equal("━", hr.Glyph);
    }

    [Fact]
    public void Parse_HorizontalRule_CustomGlyph()
    {
        var rt = TextMarkup.Parse("[hr=*/]");
        var hr = (HorizontalRule) rt.Blocks[0];

        Assert.Equal("*", hr.Glyph);
    }

    [Fact]
    public void Parse_Paragraph_WithAttributes()
    {
        var rt = TextMarkup.Parse("[p wrap=character align=center trim=word]hello[/p]");
        var paragraph = FirstParagraph(rt);

        Assert.Equal(WrapMode.CharacterWrap, paragraph.Wrap);
        Assert.Equal(TextAlignment.Center, paragraph.Alignment);
        Assert.Equal(TextTrimming.WordEllipsis, paragraph.Trim);
    }

    [Fact]
    public void Parse_Paragraph_MaxLines()
    {
        var rt = TextMarkup.Parse("[p maxlines=3]content[/p]");
        var paragraph = FirstParagraph(rt);

        Assert.Equal(3, paragraph.MaxLines);
    }

    [Fact]
    public void Parse_MixedBlocks()
    {
        var rt = TextMarkup.Parse("first[hr/][p]second[/p]");

        Assert.Equal(3, rt.Blocks.Length);
        Assert.IsType<TextParagraph>(rt.Blocks[0]);
        Assert.IsType<HorizontalRule>(rt.Blocks[1]);
        Assert.IsType<TextParagraph>(rt.Blocks[2]);
    }

    [Fact]
    public void Parse_EscapeBracket_ProducesLiteralBracket()
    {
        var rt = TextMarkup.Parse(@"\[not a tag\]");

        Assert.Equal("[not a tag]", FirstRun(rt).Text);
    }

    [Fact]
    public void Parse_EscapeBackslash()
    {
        var rt = TextMarkup.Parse(@"a\\b");

        Assert.Equal(@"a\b", FirstRun(rt).Text);
    }

    [Fact]
    public void Parse_MismatchedClose_Throws()
    {
        var ex = Assert.Throws<FormatException>(() => TextMarkup.Parse("[b][i]text[/b][/i]"));
        Assert.Contains("Mismatched", ex.Message);
    }

    [Fact]
    public void Parse_UnknownTag_Throws()
    {
        Assert.Throws<FormatException>(() => TextMarkup.Parse("[unknown]text[/unknown]"));
    }

    [Fact]
    public void Parse_UnclosedTag_Throws()
    {
        Assert.Throws<FormatException>(() => TextMarkup.Parse("[b]oops"));
    }

    [Fact]
    public void Parse_StrayClose_Throws()
    {
        Assert.Throws<FormatException>(() => TextMarkup.Parse("hello[/b]"));
    }

    [Fact]
    public void Parse_InvalidHexColor_Throws()
    {
        Assert.Throws<FormatException>(() => TextMarkup.Parse("[fg=#xyz]text[/fg]"));
    }

    [Fact]
    public void Parse_PaletteIndexOutOfRange_Throws()
    {
        Assert.Throws<FormatException>(() => TextMarkup.Parse("[fg=256]text[/fg]"));
    }

    [Fact]
    public void Parse_ContentTag_LooksUpRegisteredContent()
    {
        var content = new StubContent(width: 3);
        var options = new TextMarkupOptions
        {
            Content = new Dictionary<string, Cursorial.Rendering.Content.IContent> { ["icon"] = content }
        };

        var rt = TextMarkup.Parse("hi [content=icon/] there", options);
        var paragraph = FirstParagraph(rt);

        Assert.Equal(3, paragraph.Inlines.Length);
        Assert.IsType<TextRun>(paragraph.Inlines[0]);
        Assert.IsType<InlineContent>(paragraph.Inlines[1]);
        Assert.Same(content, ((InlineContent) paragraph.Inlines[1]).Content);
        Assert.IsType<TextRun>(paragraph.Inlines[2]);
    }

    [Fact]
    public void Parse_ContentTag_CaseInsensitive()
    {
        var content = new StubContent(width: 2);
        var options = new TextMarkupOptions
        {
            Content = new Dictionary<string, Cursorial.Rendering.Content.IContent>(StringComparer.OrdinalIgnoreCase)
            {
                ["Settings"] = content
            }
        };

        var rt = TextMarkup.Parse("[content=settings/]", options);
        var ic = (InlineContent) FirstParagraph(rt).Inlines[0];
        Assert.Same(content, ic.Content);
    }

    [Fact]
    public void Parse_ContentTag_UnknownName_Throws()
    {
        var ex = Assert.Throws<FormatException>(() => TextMarkup.Parse("[content=missing/]"));
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public void Parse_ContentTag_MissingName_Throws()
    {
        Assert.Throws<FormatException>(() => TextMarkup.Parse("[content/]"));
    }

    private sealed class StubContent(int width) : Cursorial.Rendering.Content.IContent
    {
        public Cursorial.Rendering.Size Measure(Cursorial.Rendering.Size availableSpace,
                                                Output.Capabilities.OutputCapabilities capabilities)
            => new(width, 1);

        public Cursorial.Rendering.Rect Paint(
            in Cursorial.Rendering.CellBufferView buffer, in Cursorial.Rendering.Rect bounds,
            in Style style, Output.Capabilities.OutputCapabilities capabilities)
            => bounds;
    }

    [Fact]
    public void Parse_TextMarkupRoundTripsThroughFormatter()
    {
        var rt = TextMarkup.Parse("[b]Hello[/b] [fg=red]world[/fg]");
        var ft = new TextFormatter().Format(rt, 80);

        var para = (FormattedParagraph) ft.Blocks[0];
        var textRuns = para.Lines.SelectMany(l => l.Runs).OfType<FormattedTextRun>().ToArray();
        var allText = string.Concat(textRuns.Select(r => r.Text));

        Assert.Equal("Hello world", allText);
        Assert.Contains(textRuns, r => r.Style.Attributes.HasFlag(TextAttributes.Bold));
        Assert.Contains(textRuns, r => r.Style.Foreground.PaletteIndex == 1);
    }
}
