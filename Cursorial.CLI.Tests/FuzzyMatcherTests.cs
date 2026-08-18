using Cursorial.CLI.Commandlets;

namespace Cursorial.Tests.CLI;

/// <summary>The `filter` match engine, pinned without a host: subsequence semantics, the
/// (first-match-index, length, original-order) ranking, and case folding.</summary>
public class FuzzyMatcherTests
{
    [Fact]
    public void TryMatch_SubsequenceInOrder_Matches()
    {
        Assert.True(FuzzyMatcher.TryMatch("cursorial", "csl", out var first));
        Assert.Equal(0, first); // 'c' lands at 0
    }

    [Fact]
    public void TryMatch_OutOfOrder_DoesNotMatch()
    {
        Assert.False(FuzzyMatcher.TryMatch("cursorial", "lsc", out _)); // chars must appear in order
    }

    [Fact]
    public void TryMatch_MissingChar_DoesNotMatch()
    {
        Assert.False(FuzzyMatcher.TryMatch("alpha", "z", out var first));
        Assert.Equal(0, first); // failure reports 0, never a partial anchor
    }

    [Fact]
    public void TryMatch_CaseInsensitive_BothDirections()
    {
        Assert.True(FuzzyMatcher.TryMatch("README", "rdm", out _));
        Assert.True(FuzzyMatcher.TryMatch("readme", "RDM", out _));
    }

    [Fact]
    public void TryMatch_ReportsFirstMatchIndex()
    {
        Assert.True(FuzzyMatcher.TryMatch("beta", "et", out var first));
        Assert.Equal(1, first); // 'e' lands at 1
    }

    [Fact]
    public void TryMatch_EmptyQuery_MatchesAtZero()
    {
        Assert.True(FuzzyMatcher.TryMatch("anything", "", out var first));
        Assert.Equal(0, first);
    }

    [Fact]
    public void Filter_RanksByFirstMatchIndex_ThenLength()
    {
        // "a": aa (first 0, len 2) < alpha (first 0, len 5) < gamma (first 1)
        var matches = FuzzyMatcher.Filter(["gamma", "alpha", "aa"], "a");
        Assert.Equal(new[] { "aa", "alpha", "gamma" }, matches);
    }

    [Fact]
    public void Filter_Ties_KeepOriginalOrder()
    {
        // both first-match 1, both length 3 — the input order is the tiebreak
        var matches = FuzzyMatcher.Filter(["bat", "cat"], "at");
        Assert.Equal(new[] { "bat", "cat" }, matches);
    }

    [Fact]
    public void Filter_EmptyQuery_ReturnsAllInOriginalOrder()
    {
        var matches = FuzzyMatcher.Filter(["one", "two", "three"], "");
        Assert.Equal(new[] { "one", "two", "three" }, matches);
    }

    [Fact]
    public void Filter_NullQuery_ReturnsAllInOriginalOrder()
    {
        var matches = FuzzyMatcher.Filter(["one", "two"], null);
        Assert.Equal(new[] { "one", "two" }, matches);
    }

    [Fact]
    public void Filter_NoMatches_ReturnsEmpty()
    {
        Assert.Empty(FuzzyMatcher.Filter(["alpha", "beta"], "zz"));
    }

    [Fact]
    public void Filter_CaseInsensitive()
    {
        var matches = FuzzyMatcher.Filter(["Feature/CLI", "docs"], "cli");
        Assert.Equal("Feature/CLI", Assert.Single(matches));
    }
}
