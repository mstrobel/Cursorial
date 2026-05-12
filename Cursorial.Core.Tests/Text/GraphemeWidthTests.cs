using Cursorial.Core.Text;

namespace Cursorial.Core.Tests.Text;

public class GraphemeWidthTests
{
    // ---- CodepointWidth ----

    [Theory]
    [InlineData(0x20, 1)] // space
    [InlineData(0x41, 1)] // 'A'
    [InlineData(0x7E, 1)] // '~'
    [InlineData(0x00, 0)] // NUL
    [InlineData(0x09, 0)] // Tab
    [InlineData(0x7F, 0)] // DEL
    [InlineData(0xA0, 1)] // NBSP
    [InlineData(0xE9, 1)] // 'é'
    public void CodepointWidth_AsciiAndLatin1(int cp, int expected)
    {
        Assert.Equal(expected, GraphemeWidth.CodepointWidth(cp));
    }

    [Theory]
    [InlineData(0x300)]  // Combining grave accent (Mn)
    [InlineData(0x301)]  // Combining acute accent (Mn)
    [InlineData(0x200B)] // Zero-width space (Cf)
    [InlineData(0x200C)] // ZWNJ (Cf)
    [InlineData(0x200D)] // ZWJ (Cf)
    [InlineData(0xFE0E)] // Variation Selector-15 (Mn)
    [InlineData(0xFE0F)] // Variation Selector-16 (Mn)
    [InlineData(0xFEFF)] // BOM (Cf)
    public void CodepointWidth_ZeroWidthCategories_AreZero(int cp)
    {
        Assert.Equal(0, GraphemeWidth.CodepointWidth(cp));
    }

    [Theory]
    [InlineData(0x4E2D)]  // CJK: 中
    [InlineData(0x65E5)]  // CJK: 日
    [InlineData(0xAC00)]  // Hangul: 가
    [InlineData(0xFF21)]  // Fullwidth A: Ａ
    [InlineData(0x1F600)] // Emoji: 😀
    [InlineData(0x1F680)] // Emoji: 🚀
    [InlineData(0x20000)] // CJK Extension B
    public void CodepointWidth_WideRanges_AreTwo(int cp)
    {
        Assert.Equal(2, GraphemeWidth.CodepointWidth(cp));
    }

    [Fact]
    public void CodepointWidth_NegativeOrOutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphemeWidth.CodepointWidth(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphemeWidth.CodepointWidth(0x110000));
    }

    // ---- ClusterWidth ----

    [Fact]
    public void ClusterWidth_Empty_ReturnsZero()
    {
        Assert.Equal(0, GraphemeWidth.ClusterWidth(""));
    }

    [Fact]
    public void ClusterWidth_SingleAscii()
    {
        Assert.Equal(1, GraphemeWidth.ClusterWidth("a"));
    }

    [Fact]
    public void ClusterWidth_LatinWithCombiningMark_StaysOne()
    {
        // 'a' + combining grave accent.
        Assert.Equal(1, GraphemeWidth.ClusterWidth("à"));
    }

    [Fact]
    public void ClusterWidth_CjkChar()
    {
        Assert.Equal(2, GraphemeWidth.ClusterWidth("中"));
    }

    [Fact]
    public void ClusterWidth_Vs16_BumpsToEmojiPresentation()
    {
        // Phone (U+260E) is text-default; VS16 forces emoji presentation, width 2.
        Assert.Equal(2, GraphemeWidth.ClusterWidth("☎️"));
    }

    [Fact]
    public void ClusterWidth_Vs15_PinsToTextPresentation()
    {
        // Default-emoji 😀 has width 2 on its own. VS15 forcing text presentation should
        // reduce to 1.
        Assert.Equal(1, GraphemeWidth.ClusterWidth("☀︎"));
    }

    [Fact]
    public void ClusterWidth_ZwjEmojiSequence()
    {
        // Family emoji: 👨‍👩‍👧 = man + ZWJ + woman + ZWJ + girl. As a cluster, width 2.
        string family = "\U0001F468‍\U0001F469‍\U0001F467";
        Assert.Equal(2, GraphemeWidth.ClusterWidth(family));
    }

    [Fact]
    public void ClusterWidth_SurrogatePair_Emoji()
    {
        // 🚀 = U+1F680, surrogate pair in UTF-16.
        Assert.Equal(2, GraphemeWidth.ClusterWidth("🚀"));
    }

    // ---- StringWidth ----

    [Fact]
    public void StringWidth_Empty_ReturnsZero()
    {
        Assert.Equal(0, GraphemeWidth.StringWidth(""));
    }

    [Fact]
    public void StringWidth_AsciiString_LengthEqualsCharCount()
    {
        Assert.Equal(11, GraphemeWidth.StringWidth("hello world"));
    }

    [Fact]
    public void StringWidth_MixedCjk_CountsTwoPerChar()
    {
        // "Hello中文" = 5 + 2 + 2 = 9.
        Assert.Equal(9, GraphemeWidth.StringWidth("Hello中文"));
    }

    [Fact]
    public void StringWidth_FamilyEmoji_CountsClusterAsTwo()
    {
        // Family emoji is one cluster, width 2.
        Assert.Equal(2, GraphemeWidth.StringWidth("\U0001F468‍\U0001F469‍\U0001F467"));
    }

    [Fact]
    public void StringWidth_StringAndSpanOverloads_Agree()
    {
        string s = "héllo🌍中文";
        Assert.Equal(GraphemeWidth.StringWidth(s), GraphemeWidth.StringWidth(s.AsSpan()));
    }

    [Fact]
    public void StringWidth_NullString_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => GraphemeWidth.StringWidth((string)null!));
    }
}
