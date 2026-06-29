using Cursorial.Rendering.Text;
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
        var l = TextLayout.Build(t, wrapWidth: 0, WrapMode.NoWrap);

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
        var l = TextLayout.Build(t, 0, WrapMode.NoWrap);

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
        var l = TextLayout.Build(t, 0, WrapMode.NoWrap);

        Assert.Equal(2, l.LineCount);
        Assert.Equal("", LineText(t, l, 1));
        Assert.Equal(4, l.LineContentStart(1));
    }

    [Fact] // CRLF (and lone CR) count as a single break
    public void CrLf_IsSingleBreak()
    {
        const string t = "ab\r\ncd";
        var l = TextLayout.Build(t, 0, WrapMode.NoWrap);

        Assert.Equal(2, l.LineCount);
        Assert.Equal("ab", LineText(t, l, 0));
        Assert.Equal("cd", LineText(t, l, 1));
        Assert.Equal(4, l.LineContentStart(1)); // past the two-char '\r\n'
    }

    [Fact]
    public void Empty_IsOneEmptyLine()
    {
        var l = TextLayout.Build("", 0, WrapMode.NoWrap);

        Assert.Equal(1, l.LineCount);
        Assert.Equal(0, l.LineWidth(0));
        Assert.Equal((0, 0), l.Locate(0));
    }

    [Fact] // wrap is lossless (no '\n' inserted), and no visual line exceeds the width
    public void Wrap_BreaksAtWidth_PreservesContent()
    {
        const string t = "one two three four";
        var l = TextLayout.Build(t, wrapWidth: 8, WrapMode.WordWrap);

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
        var l = TextLayout.Build(t, wrapWidth: 4, WrapMode.WordWrap);

        Assert.True(l.LineCount >= 3);
        for (var i = 0; i < l.LineCount; i++)
            Assert.True(l.LineWidth(i) is > 0 and <= 4);
    }

    [Fact] // Up/Down preserve a desired column, clamping on a shorter line (via OffsetAt)
    public void OffsetAt_PreservesColumn_ClampsShortLine()
    {
        const string t = "hello\nhi\nworld";
        var l = TextLayout.Build(t, 0, WrapMode.NoWrap);

        Assert.Equal(l.LineContentEnd(1), l.OffsetAt(1, 4)); // "hi" is too short → clamps to its end
        Assert.Equal((2, 4), l.Locate(l.OffsetAt(2, 4)));    // "world" keeps column 4
    }

    [Fact] // a soft-wrap boundary offset resolves to either line by affinity (audit #1/#2 root cause)
    public void SoftWrapBoundary_ResolvesByAffinity()
    {
        const string t = "aaaabbbb"; // one word, width 4 → "aaaa"[0,4) + "bbbb"[4,8), both soft-wrapped
        var l = TextLayout.Build(t, wrapWidth: 4, WrapMode.WordWrap);

        Assert.Equal(2, l.LineCount);
        Assert.Equal((1, 0), l.Locate(4));                 // default: start of the next line
        Assert.Equal((1, 0), l.Locate(4, preferLineEnd: false));
        Assert.Equal((0, 4), l.Locate(4, preferLineEnd: true)); // end-affinity: visual end of the earlier line
        Assert.True(l.IsLineEndBoundary(0, 4));            // line 0's end coincides with line 1's start
        Assert.False(l.IsLineEndBoundary(1, 8));           // the last line's end is not a boundary (no next line)
    }

    [Fact] // a hard-break offset is never a soft-wrap boundary (the break char leaves a gap)
    public void HardBreak_IsNotASoftWrapBoundary()
    {
        const string t = "ab\ncd";
        var l = TextLayout.Build(t, 0, WrapMode.NoWrap);

        Assert.False(l.IsLineEndBoundary(0, l.LineContentEnd(0))); // end of "ab" (offset 2) — the '\n' separates it from "cd"
        Assert.Equal((0, 2), l.Locate(2, preferLineEnd: true));    // affinity has no effect across a hard break
        Assert.Equal((0, 2), l.Locate(2, preferLineEnd: false));
    }

    [Fact] // WordWrapOverflow keeps an over-long word whole (overflows the width) instead of breaking it (audit #3)
    public void WordWrapOverflow_KeepsLongWordWhole()
    {
        const string t = "aaaaaa bb"; // "aaaaaa" (6 cols) exceeds width 4 and has no internal break
        var l = TextLayout.Build(t, wrapWidth: 4, WrapMode.WordWrapOverflow);

        Assert.Equal(2, l.LineCount);
        Assert.True(l.LineWidth(0) > 4, $"the long word was broken (line 0 width {l.LineWidth(0)})"); // overflowed, not split
        var rebuilt = LineText(t, l, 0) + LineText(t, l, 1);
        Assert.Equal(t, rebuilt); // lossless

        // WordWrap (the default) DOES break the same long word at the width.
        var wrapped = TextLayout.Build(t, wrapWidth: 4, WrapMode.WordWrap);
        Assert.True(wrapped.LineWidth(0) <= 4);
    }

    [Fact] // CharacterWrap fills to the exact width, ignoring word boundaries (audit #3)
    public void CharacterWrap_BreaksAtExactWidth()
    {
        const string t = "ab cdefgh";
        var l = TextLayout.Build(t, wrapWidth: 4, WrapMode.CharacterWrap);

        Assert.Equal("ab c", LineText(t, l, 0)); // filled past the space (WordWrap would stop at "ab ")
        for (var i = 0; i < l.LineCount; i++)
            Assert.True(l.LineWidth(i) is > 0 and <= 4);
        var rebuilt = new System.Text.StringBuilder();
        for (var i = 0; i < l.LineCount; i++)
            rebuilt.Append(LineText(t, l, i));
        Assert.Equal(t, rebuilt.ToString()); // lossless
    }
}
