using Cursorial.Text;

namespace Cursorial.Tests.Text;

public class GraphemeEnumeratorTests
{
    // ---- Helpers ----

    private static List<string> EnumerateGraphemes(string input)
    {
        var graphemes = new List<string>();
        var enumerator = input.GetGraphemeEnumerator();
        while (enumerator.MoveNext()) graphemes.Add(enumerator.Current.ToString());
        return graphemes;
    }

    // ---- Empty input ----

    [Fact]
    public void EmptyString_MoveNextReturnsFalseImmediately()
    {
        var enumerator = "".GetGraphemeEnumerator();
        Assert.False(enumerator.MoveNext());
    }

    [Fact]
    public void EmptyString_ProducesNoGraphemes()
    {
        Assert.Empty(EnumerateGraphemes(""));
    }

    // ---- ASCII ----

    [Fact]
    public void SingleAscii_OneGrapheme()
    {
        var graphemes = EnumerateGraphemes("a");
        Assert.Single(graphemes);
        Assert.Equal("a", graphemes[0]);
    }

    [Fact]
    public void AsciiString_OneGraphemePerCodepoint()
    {
        var graphemes = EnumerateGraphemes("hello");
        Assert.Equal(["h", "e", "l", "l", "o"], graphemes);
    }

    // ---- Combining marks ----

    [Fact]
    public void CombiningMark_ClustersWithBase()
    {
        // 'a' + U+0300 (combining grave) is a single grapheme cluster.
        var graphemes = EnumerateGraphemes("à");
        Assert.Single(graphemes);
        Assert.Equal("à", graphemes[0]);
    }

    [Fact]
    public void MultipleCombiningMarks_StayInOneCluster()
    {
        // 'a' + U+0301 + U+0302 — three codepoints, one cluster.
        var graphemes = EnumerateGraphemes("á̂");
        Assert.Single(graphemes);
    }

    // ---- VS16 / VS15 selectors ----

    [Fact]
    public void Vs16Selector_ClustersWithBase()
    {
        // Phone (U+260E) + VS16 (U+FE0F) is one cluster.
        var graphemes = EnumerateGraphemes("☎️");
        Assert.Single(graphemes);
    }

    // ---- ZWJ sequences ----

    [Fact]
    public void ZwjFamilyEmoji_IsSingleCluster()
    {
        // Man + ZWJ + Woman + ZWJ + Girl — one user-visible glyph.
        string family = "\U0001F468‍\U0001F469‍\U0001F467";
        var graphemes = EnumerateGraphemes(family);
        Assert.Single(graphemes);
        Assert.Equal(family, graphemes[0]);
    }

    // ---- Surrogate pairs ----

    [Fact]
    public void EmojiSurrogatePair_IsSingleCluster()
    {
        // 🚀 = U+1F680, encoded as a UTF-16 surrogate pair.
        var graphemes = EnumerateGraphemes("🚀");
        Assert.Single(graphemes);
        Assert.Equal("🚀", graphemes[0]);
        Assert.Equal(2, graphemes[0].Length); // surrogate pair = 2 chars
    }

    [Fact]
    public void MultipleEmoji_EachIsItsOwnCluster()
    {
        var graphemes = EnumerateGraphemes("🚀🌍🎨");
        Assert.Equal(3, graphemes.Count);
        Assert.Equal(["🚀", "🌍", "🎨"], graphemes);
    }

    // ---- Mixed text ----

    [Fact]
    public void MixedAsciiAndCjk_OneGraphemePerVisibleGlyph()
    {
        var graphemes = EnumerateGraphemes("Hi中文");
        Assert.Equal(4, graphemes.Count);
        Assert.Equal(["H", "i", "中", "文"], graphemes);
    }

    [Fact]
    public void MixedAsciiAndEmoji_GraphemeCountMatches()
    {
        var graphemes = EnumerateGraphemes("a🚀b");
        Assert.Equal(3, graphemes.Count);
        Assert.Equal(["a", "🚀", "b"], graphemes);
    }

    // ---- ElementIndex ----

    [Fact]
    public void ElementIndex_TracksOffsetInChars()
    {
        // "a🚀b" — offsets: a=0, 🚀=1 (surrogate pair 2 chars wide), b=3.
        var enumerator = "a🚀b".GetGraphemeEnumerator();
        var offsets = new List<int>();
        while (enumerator.MoveNext()) offsets.Add(enumerator.ElementIndex);
        Assert.Equal([0, 1, 3], offsets);
    }

    [Fact]
    public void ElementIndex_BeforeMoveNext_Throws()
    {
        var enumerator = "abc".GetGraphemeEnumerator();
        try
        {
            _ = enumerator.ElementIndex;
            Assert.Fail("Expected InvalidOperationException.");
        }
        catch (InvalidOperationException) { /* expected */ }
    }

    // ---- Reset ----

    [Fact]
    public void Reset_RestartsEnumeration()
    {
        var enumerator = "abc".GetGraphemeEnumerator();
        Assert.True(enumerator.MoveNext());
        Assert.Equal("a", enumerator.Current.ToString());
        Assert.True(enumerator.MoveNext());
        Assert.Equal("b", enumerator.Current.ToString());

        enumerator.Reset();
        Assert.True(enumerator.MoveNext());
        Assert.Equal("a", enumerator.Current.ToString());
    }

    // ---- Exhaustion semantics ----

    [Fact]
    public void MoveNext_AfterEnd_ReturnsFalse()
    {
        var enumerator = "a".GetGraphemeEnumerator();
        Assert.True(enumerator.MoveNext());
        Assert.False(enumerator.MoveNext());
        Assert.False(enumerator.MoveNext()); // idempotent past end
    }

    [Fact]
    public void Current_AfterEnd_Throws()
    {
        var enumerator = "a".GetGraphemeEnumerator();
        Assert.True(enumerator.MoveNext());
        Assert.False(enumerator.MoveNext());
        try
        {
            _ = enumerator.Current;
            Assert.Fail("Expected InvalidOperationException.");
        }
        catch (InvalidOperationException) { /* expected */ }
    }

    [Fact]
    public void Current_BeforeFirstMoveNext_Throws()
    {
        var enumerator = "a".GetGraphemeEnumerator();
        try
        {
            _ = enumerator.Current;
            Assert.Fail("Expected InvalidOperationException.");
        }
        catch (InvalidOperationException) { /* expected */ }
    }

    // ---- String + span overload equivalence ----

    [Fact]
    public void StringAndSpanExtensions_ProduceSameGraphemes()
    {
        const string source = "Hello🚀中文";

        var stringGraphemes = new List<string>();
        var stringEnum = source.GetGraphemeEnumerator();
        while (stringEnum.MoveNext()) stringGraphemes.Add(stringEnum.Current.ToString());

        var spanGraphemes = new List<string>();
        var spanEnum = source.AsSpan().GetGraphemeEnumerator();
        while (spanEnum.MoveNext()) spanGraphemes.Add(spanEnum.Current.ToString());

        Assert.Equal(stringGraphemes, spanGraphemes);
    }

    // ---- Round-trip invariants ----

    [Fact]
    public void Concatenation_OfAllGraphemes_EqualsOriginal()
    {
        const string source = "héllo 🌍 中文 with combining marks: à è ñ ç";
        var graphemes = EnumerateGraphemes(source);
        Assert.Equal(source, string.Concat(graphemes));
    }

    [Fact]
    public void GraphemeLengths_SumToInputLength()
    {
        const string source = "x́y\U0001F600z";
        var graphemes = EnumerateGraphemes(source);
        int totalChars = graphemes.Sum(g => g.Length);
        Assert.Equal(source.Length, totalChars);
    }

    // ---- Foreach pattern (duck-typed) ----

    [Fact]
    public void ForeachLoop_OverEnumerator_Works()
    {
        var collected = new List<string>();
        var enumerator = "abc".GetGraphemeEnumerator();
        while (enumerator.MoveNext())
            collected.Add(enumerator.Current.ToString());

        Assert.Equal(["a", "b", "c"], collected);
    }
}
