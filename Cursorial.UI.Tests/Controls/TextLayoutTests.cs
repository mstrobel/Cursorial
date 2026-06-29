using Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Controls;

// Phase 1 spec for the multi-line TextLayout engine (the foundation of multi-line TextBox): hard-break
// splitting, CRLF, the trailing blank line, grapheme-aware word wrap, and the offset ↔ (line, column) maps
// that drive caret navigation and rendering.
public class TextLayoutTests
{
    private static string LineText(string text, in TextLayout layout, int line)
        => text.Substring(layout.LineContentStart(line), layout.LineContentEnd(line) - layout.LineContentStart(line));

    [Fact] // degenerate single line == a bare GraphemeLayout
    public void NoBreaks_NoWrap_IsOneLineSpanningAll()
    {
        const string t = "hello world";
        var l = TextLayout.Build(t, wrapWidth: 0, wrap: false);

        Assert.Equal(1, l.LineCount);
        Assert.Equal(t, LineText(t, l, 0));
        Assert.Equal(11, l.LineWidth(0));
        Assert.Equal((0, 6), l.Locate(6));
        Assert.Equal(6, l.OffsetAt(0, 6));
    }

    [Fact] // hard newlines split into visual lines; End is before the break
    public void HardNewlines_SplitIntoLines()
    {
        const string t = "abc\ndef";
        var l = TextLayout.Build(t, 0, false);

        Assert.Equal(2, l.LineCount);
        Assert.Equal("abc", LineText(t, l, 0));
        Assert.Equal("def", LineText(t, l, 1));
        Assert.Equal(3, l.LineContentEnd(0));   // before the '\n'
        Assert.Equal((1, 0), l.Locate(4));      // 'd' is line 1, column 0
        Assert.Equal(4, l.OffsetAt(1, 0));
        Assert.Equal((0, 3), l.Locate(3));      // the '\n' offset resolves to the end of line 0
    }

    [Fact] // a trailing hard break yields a blank final line for the caret
    public void TrailingNewline_AddsBlankLine()
    {
        const string t = "abc\n";
        var l = TextLayout.Build(t, 0, false);

        Assert.Equal(2, l.LineCount);
        Assert.Equal("", LineText(t, l, 1));
        Assert.Equal(4, l.LineContentStart(1));
    }

    [Fact] // CRLF (and lone CR) count as a single break
    public void CrLf_IsSingleBreak()
    {
        const string t = "ab\r\ncd";
        var l = TextLayout.Build(t, 0, false);

        Assert.Equal(2, l.LineCount);
        Assert.Equal("ab", LineText(t, l, 0));
        Assert.Equal("cd", LineText(t, l, 1));
        Assert.Equal(4, l.LineContentStart(1)); // past the two-char '\r\n'
    }

    [Fact]
    public void Empty_IsOneEmptyLine()
    {
        var l = TextLayout.Build("", 0, false);

        Assert.Equal(1, l.LineCount);
        Assert.Equal(0, l.LineWidth(0));
        Assert.Equal((0, 0), l.Locate(0));
    }

    [Fact] // wrap is lossless (no '\n' inserted), and no visual line exceeds the width
    public void Wrap_BreaksAtWidth_PreservesContent()
    {
        const string t = "one two three four";
        var l = TextLayout.Build(t, wrapWidth: 8, wrap: true);

        Assert.True(l.LineCount > 1);
        var rebuilt = new System.Text.StringBuilder();
        for (var i = 0; i < l.LineCount; i++)
        {
            Assert.True(l.LineWidth(i) <= 8, $"line {i} '{LineText(t, l, i)}' exceeds the wrap width");
            rebuilt.Append(LineText(t, l, i));
        }

        Assert.Equal(t, rebuilt.ToString());
    }

    [Fact] // a word longer than the budget hard-breaks, never producing a zero-length line
    public void Wrap_LongWord_HardBreaks()
    {
        const string t = "abcdefghij";
        var l = TextLayout.Build(t, wrapWidth: 4, wrap: true);

        Assert.True(l.LineCount >= 3);
        for (var i = 0; i < l.LineCount; i++)
            Assert.True(l.LineWidth(i) is > 0 and <= 4);
    }

    [Fact] // Up/Down preserve a desired column, clamping on a shorter line (via OffsetAt)
    public void OffsetAt_PreservesColumn_ClampsShortLine()
    {
        const string t = "hello\nhi\nworld";
        var l = TextLayout.Build(t, 0, false);

        Assert.Equal(l.LineContentEnd(1), l.OffsetAt(1, 4)); // "hi" is too short → clamps to its end
        Assert.Equal((2, 4), l.Locate(l.OffsetAt(2, 4)));    // "world" keeps column 4
    }
}
