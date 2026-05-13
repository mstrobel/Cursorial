using Cursorial.Text;

namespace Cursorial.Tests.Text;

public class AnsiTextWrapTests
{
    // ---- MeasureEscapeSequence (unit-test the inner state machine directly) ----

    [Theory]
    [InlineData("\x1b[31m", 5)]                // CSI SGR
    [InlineData("\x1b[1;5H", 6)]               // CSI CUP
    [InlineData("\x1b[?25l", 6)]               // CSI DEC private
    [InlineData("\x1b]8;;https://x\x1b\\", 16)]// OSC 8 with ST terminator
    [InlineData("\x1b]0;title\x07", 10)]       // OSC with BEL terminator
    [InlineData("\x1bOA", 3)]                  // SS3
    [InlineData("\x1b(B", 3)]                  // ESC charset designator
    [InlineData("7", 2)]                 // ESC 7 (DECSC) — note \u not \x: greedy \x would swallow the '7'.
    public void MeasureEscapeSequence_RecognizesCommonShapes(string seq, int expected)
    {
        Assert.Equal(expected, AnsiTextWrap.MeasureEscapeSequence(seq, 0));
    }

    [Fact]
    public void MeasureEscapeSequence_ReturnsZeroForNonEscape()
    {
        Assert.Equal(0, AnsiTextWrap.MeasureEscapeSequence("a", 0));
        Assert.Equal(0, AnsiTextWrap.MeasureEscapeSequence("abc", 1));
    }

    // ---- Wrap: plain text ----

    [Fact]
    public void Wrap_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", AnsiTextWrap.Wrap("", 10));
    }

    [Fact]
    public void Wrap_TextThatFits_ReturnsUnchanged()
    {
        Assert.Equal("hello", AnsiTextWrap.Wrap("hello", 10));
    }

    [Fact]
    public void Wrap_BreaksAtLastWhitespace()
    {
        Assert.Equal("hello\nworld", AnsiTextWrap.Wrap("hello world", 7));
    }

    [Fact]
    public void Wrap_MultipleWraps()
    {
        var input = "the quick brown fox jumps over the lazy dog";
        var wrapped = AnsiTextWrap.Wrap(input, 12);
        // Each line should be at most 12 columns, and the rejoin should equal the original
        // (modulo line-break replacements).
        foreach (var line in wrapped.Split('\n'))
        {
            Assert.True(GraphemeWidth.StringWidth(line) <= 12, $"line too long: \"{line}\"");
        }
        Assert.Equal(input, wrapped.Replace("\n", " "));
    }

    [Fact]
    public void Wrap_BreaksLongWord_WhenAllowed()
    {
        var s = AnsiTextWrap.Wrap("supercalifragilistic", 5);
        foreach (var line in s.Split('\n'))
        {
            Assert.True(GraphemeWidth.StringWidth(line) <= 5);
        }
    }

    [Fact]
    public void Wrap_PreservesLongWord_WhenBreakingDisabled()
    {
        var opts = new WrapOptions(BreakLongWords: false);
        var s = AnsiTextWrap.Wrap("supercalifragilistic", 5, opts);
        Assert.Equal("supercalifragilistic", s);
    }

    [Fact]
    public void Wrap_RespectsHardLineBreaks()
    {
        Assert.Equal("ab\ncd", AnsiTextWrap.Wrap("ab\ncd", 10));
    }

    [Fact]
    public void Wrap_TreatsCrLfAsSingleBreak()
    {
        Assert.Equal("ab\ncd", AnsiTextWrap.Wrap("ab\r\ncd", 10));
    }

    [Fact]
    public void Wrap_TrimsTrailingSpacesByDefault()
    {
        // "hello " hits 6 cols (with trailing space included); "world" overflows so we break.
        // Trailing space should be stripped.
        var s = AnsiTextWrap.Wrap("hello world", 6);
        Assert.Equal("hello\nworld", s);
    }

    [Fact]
    public void Wrap_PreservesTrailingSpaces_WhenOptionDisabled()
    {
        var opts = new WrapOptions(TrimTrailingSpaces: false);
        var s = AnsiTextWrap.Wrap("hello world", 6, opts);
        Assert.Equal("hello \nworld", s);
    }

    [Fact]
    public void Wrap_CustomNewLine()
    {
        var opts = new WrapOptions(NewLine: "\r\n");
        Assert.Equal("ab\r\ncd", AnsiTextWrap.Wrap("ab cd", 3, opts));
    }

    // ---- Wrap: ANSI escape preservation ----

    [Fact]
    public void Wrap_PassesEscapeSequencesThroughWithoutCountingWidth()
    {
        // The SGR sequence is 5 chars but contributes 0 columns.
        var input = "hi \x1b[31mworld\x1b[0m";
        var s = AnsiTextWrap.Wrap(input, 10);
        Assert.Equal(input, s); // total displayed width = 8, fits.
    }

    [Fact]
    public void Wrap_BreaksAroundEscapeSequencesByDisplayWidth()
    {
        // Display width: 5 + 5 = 10. Wrap at 6 → "hello\n" + escape + "world".
        var input = "hello \x1b[31mworld\x1b[0m";
        var s = AnsiTextWrap.Wrap(input, 6);
        Assert.Equal("hello\n\x1b[31mworld\x1b[0m", s);
    }

    [Fact]
    public void Wrap_OscHyperlinkPassesThrough()
    {
        var input = "click \x1b]8;;https://example.com\x1b\\here\x1b]8;;\x1b\\";
        var s = AnsiTextWrap.Wrap(input, 20);
        Assert.Equal(input, s); // 10 columns of visible text, well within 20.
    }

    // ---- Wrap: Unicode width ----

    [Fact]
    public void Wrap_CjkCountsAsTwoColumnsPerChar()
    {
        // "中文" = 4 cells. Wrap at 4 → fits; wrap at 3 → break between them.
        Assert.Equal("中文", AnsiTextWrap.Wrap("中文", 4));
        Assert.Equal("中\n文", AnsiTextWrap.Wrap("中文", 3));
    }

    [Fact]
    public void Wrap_EmojiCountAsTwoCells()
    {
        // "🚀🌍" = 4 cells.
        Assert.Equal("🚀\n🌍", AnsiTextWrap.Wrap("🚀🌍", 2));
    }
}
