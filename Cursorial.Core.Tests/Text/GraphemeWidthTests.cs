using Cursorial.Text;

namespace Cursorial.Tests.Text;

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
    public void StringWidth_NullString_ReturnsZero()
    {
        Assert.Equal(0, GraphemeWidth.StringWidth((string)null!));
    }

    // ---- ClusterCount ----

    [Fact]
    public void ClusterCount_Empty_ReturnsZero()
    {
        Assert.Equal(0, GraphemeWidth.ClusterCount(""));
    }

    [Fact]
    public void ClusterCount_AsciiStringMatchesLength()
    {
        Assert.Equal(5, GraphemeWidth.ClusterCount("hello"));
    }

    [Fact]
    public void ClusterCount_SurrogateEmojiIsOneCluster()
    {
        // 🚀 occupies 2 UTF-16 chars but is one grapheme cluster.
        Assert.Equal(1, GraphemeWidth.ClusterCount("🚀"));
    }

    [Fact]
    public void ClusterCount_ZwjFamilyEmojiIsOneCluster()
    {
        Assert.Equal(1, GraphemeWidth.ClusterCount("\U0001F468‍\U0001F469‍\U0001F467"));
    }

    [Fact]
    public void ClusterCount_CombiningMarkClustersWithBase()
    {
        // 'a' + combining grave (U+0300) is one cluster.
        Assert.Equal(1, GraphemeWidth.ClusterCount("à"));
    }

    [Fact]
    public void ClusterCount_Vs16EmojiIsOneCluster()
    {
        // Phone (U+260E) + VS16 — one cluster.
        Assert.Equal(1, GraphemeWidth.ClusterCount("☎️"));
    }

    [Fact]
    public void ClusterCount_MixedClustersMatchVisibleGlyphs()
    {
        // "Hi🚀中" — 4 user-visible glyphs.
        Assert.Equal(4, GraphemeWidth.ClusterCount("Hi🚀中"));
    }

    // ---- CodepointWidth: control / format ranges ----

    [Theory]
    [InlineData(0x80)] // C1 control
    [InlineData(0x9F)] // C1 control range upper bound
    public void CodepointWidth_C1Controls_AreZero(int cp)
    {
        Assert.Equal(0, GraphemeWidth.CodepointWidth(cp));
    }

    [Theory]
    [InlineData(0xAD)]   // SOFT HYPHEN (Cf)
    [InlineData(0x180E)] // Mongolian Vowel Separator (Cf historically)
    [InlineData(0x2060)] // Word Joiner (Cf)
    public void CodepointWidth_FormatControls_AreZero(int cp)
    {
        Assert.Equal(0, GraphemeWidth.CodepointWidth(cp));
    }

    [Theory]
    [InlineData(0x1100)] // Hangul Jamo start
    [InlineData(0x115F)] // Hangul Jamo end
    [InlineData(0x2329)] // LEFT-POINTING ANGLE BRACKET
    [InlineData(0x232A)] // RIGHT-POINTING ANGLE BRACKET
    [InlineData(0x3041)] // Hiragana start
    [InlineData(0xFF60)] // Fullwidth Forms end of wide range
    [InlineData(0x2FFFD)] // CJK supplementary plane upper
    public void CodepointWidth_BoundaryWideCodepoints_AreTwo(int cp)
    {
        Assert.Equal(2, GraphemeWidth.CodepointWidth(cp));
    }

    [Theory]
    [InlineData(0x100AB)] // Linear B Syllables (BMP-outside, narrow)
    [InlineData(0x1FB00)] // Symbols for Legacy Computing — explicitly narrow
    [InlineData(0x1FBFF)] // Symbols for Legacy Computing — explicitly narrow
    public void CodepointWidth_SupplementaryButNotWide_IsOne(int cp)
    {
        Assert.Equal(1, GraphemeWidth.CodepointWidth(cp));
    }

    [Theory]
    [InlineData(0xD800)] // High surrogate — invalid scalar
    [InlineData(0xDFFF)] // Low surrogate — invalid scalar
    public void CodepointWidth_SurrogateHalves_AreZero(int cp)
    {
        Assert.Equal(0, GraphemeWidth.CodepointWidth(cp));
    }

    // ---- ClusterWidth: edge cases ----

    [Fact]
    public void ClusterWidth_OnlyZwj_ReturnsZero()
    {
        // A lone ZWJ shouldn't claim cell width.
        Assert.Equal(0, GraphemeWidth.ClusterWidth("‍"));
    }

    [Fact]
    public void ClusterWidth_OnlyVs16_ReturnsTwo()
    {
        // A lone VS16 bumps to 2 by the spec even with no base — defensive.
        Assert.Equal(2, GraphemeWidth.ClusterWidth("️"));
    }

    [Fact]
    public void ClusterWidth_AsciiWithTrailingZeroWidth_StaysOne()
    {
        // 'a' followed by a zero-width joiner.
        Assert.Equal(1, GraphemeWidth.ClusterWidth("a‍"));
    }

    // ---- IsAmbiguousWidth ----

    [Theory]
    [InlineData(0x2500)] // ─ box drawing light horizontal (the horizontal-rule glyph)
    [InlineData(0x2550)] // ═ box drawing double horizontal
    [InlineData(0x2502)] // │ box drawing light vertical
    [InlineData(0x2580)] // ▀ upper half block
    [InlineData(0x25B2)] // ▲ black up-pointing triangle (scroll indicator)
    [InlineData(0x25BC)] // ▼ black down-pointing triangle
    [InlineData(0x2190)] // ← leftwards arrow
    [InlineData(0x00B7)] // · middle dot
    [InlineData(0x03B1)] // α greek small alpha
    [InlineData(0x2070)] // ⁰ superscript zero
    [InlineData(0x2075)] // ⁵ superscript five
    [InlineData(0x2080)] // ₀ subscript zero
    [InlineData(0x2085)] // ₅ subscript five
    [InlineData(0x207F)] // ⁿ superscript latin n
    public void IsAmbiguousWidth_AmbiguousCodepoints_ReturnTrue(int cp)
    {
        Assert.True(GraphemeWidth.IsAmbiguousWidth(cp));
    }

    [Theory]
    [InlineData('a')]     // ASCII letter
    [InlineData('1')]     // ASCII digit
    [InlineData(' ')]     // ASCII space
    [InlineData(0x4E2D)]  // 中 — unambiguously WIDE, not ambiguous
    [InlineData(0x1F600)] // 😀 — emoji, unambiguously wide
    public void IsAmbiguousWidth_NonAmbiguousCodepoints_ReturnFalse(int cp)
    {
        Assert.False(GraphemeWidth.IsAmbiguousWidth(cp));
    }
}
