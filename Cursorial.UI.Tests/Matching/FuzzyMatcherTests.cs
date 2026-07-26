using System.Globalization;
using Cursorial.UI.Matching;

namespace Cursorial.Tests.UI.Matching;

/// <summary>
/// The behavioural contract of <see cref="FuzzyMatcher"/> — the shared completion matcher behind
/// the file dialog's path-bar dropdown, the tag input's suggestion list and the DataViews
/// expression editor. The suite pins four things the popup depends on and that a scoring tweak
/// could silently break:
/// <list type="number">
///   <item><description>the named acceptance cases — <c>hban</c> → <c>hero_banner.png</c> and <c>inpc</c> → <c>INotifyPropertyChanged.cs</c>;</description></item>
///   <item><description><b>ranking order</b>, not merely "it matched" — a matcher that finds everything and ranks it badly is useless in a popup;</description></item>
///   <item><description>the highlight spans, including their grapheme safety (a span may never split a surrogate pair or a combining sequence, because the terminal renders whole clusters);</description></item>
///   <item><description>the total ordering, so the list cannot flicker between keystrokes whose scores tie.</description></item>
/// </list>
/// Timing and allocation live next door in <c>Benchmarks/FuzzyMatchBenchmark.cs</c>.
/// </summary>
public class FuzzyMatcherTests
{
    // The two candidates the design was written against; named once so every assertion below reads
    // against exactly the strings the acceptance criteria called out.
    private const string HeroBanner = "hero_banner.png";
    private const string NotifyInterface = "INotifyPropertyChanged.cs";

    /// <summary>Matches with a buffer that can never truncate, returning the spans as an array for easy assertion.</summary>
    private static (FuzzyMatchResult Result, MatchSpan[] Spans) Run(
        string pattern,
        string candidate,
        FuzzyMatchOptions options = FuzzyMatchOptions.None)
    {
        var buffer = new MatchSpan[FuzzyMatcher.MaxSpanCount(pattern)];
        var result = FuzzyMatcher.Match(pattern, candidate, buffer, options);
        return (result, buffer[..result.SpanCount]);
    }

    /// <summary>Ranks candidates exactly the way a completion popup would: match, then sort by <see cref="FuzzyMatcher.CompareRank"/>.</summary>
    private static string[] RankOrder(string pattern, params string[] candidates)
    {
        var ordered = candidates.ToArray();

        Array.Sort(
            ordered,
            (a, b) => FuzzyMatcher.CompareRank(FuzzyMatcher.Match(pattern, a), a, FuzzyMatcher.Match(pattern, b), b));

        return ordered;
    }

    private static int Score(string pattern, string candidate) => FuzzyMatcher.Match(pattern, candidate).Score;

    private static FuzzyMatchKind Kind(string pattern, string candidate) => FuzzyMatcher.Match(pattern, candidate).Kind;

    private static string Slice(string candidate, MatchSpan span) => candidate.Substring(span.Start, span.Length);

    // ───────────────────────────── the named acceptance cases ─────────────────────────────

    [Fact] // "h" + the "banner" segment: a segment/word-boundary prefix match, not a literal substring
    public void Hban_MatchesHeroBanner_HighlightingTheSegmentPrefixes()
    {
        var (result, spans) = Run("hban", HeroBanner);

        Assert.True(result.IsMatch);
        Assert.Equal(FuzzyMatchKind.Boundary, result.Kind);
        Assert.Equal(new[] { new MatchSpan(0, 1), new MatchSpan(5, 3) }, spans);

        // The spans really are "h" and "ban" — the runs the mock draws in bold.
        Assert.Equal("h", Slice(HeroBanner, spans[0]));
        Assert.Equal("ban", Slice(HeroBanner, spans[1]));
    }

    [Fact] // CamelCase hump initials, including the acronym tail (the N of "INotify")
    public void Inpc_MatchesINotifyPropertyChanged_OnHumpInitials()
    {
        var (result, spans) = Run("inpc", NotifyInterface);

        Assert.True(result.IsMatch);
        Assert.Equal(FuzzyMatchKind.Initials, result.Kind);
        Assert.Equal(new[] { new MatchSpan(0, 2), new MatchSpan(7, 1), new MatchSpan(15, 1) }, spans);

        Assert.Equal("IN", Slice(NotifyInterface, spans[0]));
        Assert.Equal("P", Slice(NotifyInterface, spans[1]));
        Assert.Equal("C", Slice(NotifyInterface, spans[2]));
    }

    [Fact] // the trap a right-to-left greedy walk falls into: `p` must land on the P of Property
    public void Inpc_PrefersTheHump_OverTheNearerLowercaseLetter()
    {
        var (_, spans) = Run("inpc", NotifyInterface);

        Assert.Equal(7, spans[1].Start); // 'P' of Property — not index 10, the 'p' inside it
    }

    // ───────────────────────────── ranking order ─────────────────────────────

    [Fact] // the headline ranking requirement: an exact prefix beats every other shape
    public void ExactPrefix_RanksAboveEveryOtherShape()
    {
        var order = RankOrder("te", "latest", "the_end", "template.json", "textures");

        Assert.Equal(new[] { "textures", "template.json", "the_end", "latest" }, order);
    }

    [Fact] // …and the tiers behind that order are the ones the popup labels
    public void ExactPrefix_RankingIsDrivenByTier()
    {
        Assert.Equal(FuzzyMatchKind.Prefix, Kind("te", "textures"));
        Assert.Equal(FuzzyMatchKind.Prefix, Kind("te", "template.json"));
        Assert.Equal(FuzzyMatchKind.Initials, Kind("te", "the_end"));
        Assert.Equal(FuzzyMatchKind.Substring, Kind("te", "latest"));

        Assert.True(Score("te", "textures") > Score("te", "the_end"));
        Assert.True(Score("te", "the_end") > Score("te", "latest"));
    }

    [Fact]
    public void Exact_RanksAbovePrefix()
    {
        Assert.Equal(FuzzyMatchKind.Exact, Kind("readme", "readme"));
        Assert.Equal(FuzzyMatchKind.Prefix, Kind("readme", "readme.md"));
        Assert.True(Score("readme", "readme") > Score("readme", "readme.md"));
        Assert.Equal(new[] { "readme", "readme.md" }, RankOrder("readme", "readme.md", "readme"));
    }

    [Fact]
    public void Boundary_RanksAboveSubstring_WhichRanksAboveSubsequence()
    {
        Assert.Equal(FuzzyMatchKind.Boundary, Kind("hban", HeroBanner));
        Assert.Equal(FuzzyMatchKind.Substring, Kind("ann", HeroBanner));
        Assert.Equal(FuzzyMatchKind.Subsequence, Kind("hoan", HeroBanner));

        Assert.True(Score("hban", HeroBanner) > Score("ann", HeroBanner));
        Assert.True(Score("ann", HeroBanner) > Score("hoan", HeroBanner));
    }

    [Fact] // plain fuzzy still matches — it just sorts last (the mock's "folder · fuzzy" row)
    public void PlainSubsequence_StillMatches_ButRanksLast()
    {
        var (result, spans) = Run("hoan", HeroBanner);

        Assert.True(result.IsMatch);
        Assert.Equal(FuzzyMatchKind.Subsequence, result.Kind);
        Assert.Equal(4, spans.Sum(s => s.Length));
        Assert.Equal(new[] { "hoan.txt", HeroBanner }, RankOrder("hoan", "hoan.txt", HeroBanner));
    }

    [Fact] // inside a tier the tighter match wins
    public void WithinATier_TheTighterMatchWins()
    {
        Assert.Equal(FuzzyMatchKind.Boundary, Kind("hban", "hb_analysis.txt"));
        Assert.Equal(FuzzyMatchKind.Boundary, Kind("hban", HeroBanner));
        Assert.True(Score("hban", "hb_analysis.txt") > Score("hban", HeroBanner));
    }

    [Theory] // every tier, spelled out
    [InlineData("readme", "readme", FuzzyMatchKind.Exact)]
    [InlineData("README", "readme", FuzzyMatchKind.Exact)]
    [InlineData("te", "textures", FuzzyMatchKind.Prefix)]
    [InlineData("inpc", NotifyInterface, FuzzyMatchKind.Initials)]
    [InlineData("hp", "HTMLParser.cs", FuzzyMatchKind.Initials)]
    [InlineData("f2", "file2.txt", FuzzyMatchKind.Initials)]
    [InlineData("hban", HeroBanner, FuzzyMatchKind.Boundary)]
    [InlineData("ban", HeroBanner, FuzzyMatchKind.Boundary)]
    [InlineData("te", "latest", FuzzyMatchKind.Substring)]
    [InlineData("hoan", HeroBanner, FuzzyMatchKind.Subsequence)]
    [InlineData("zz", "textures", FuzzyMatchKind.None)]
    [InlineData("", "textures", FuzzyMatchKind.Empty)]
    public void Kind_ClassifiesTheMatchShape(string pattern, string candidate, FuzzyMatchKind expected)
        => Assert.Equal(expected, Kind(pattern, candidate));

    // ───────────────────────────── case: a tiebreak, never a gate ─────────────────────────────

    [Fact]
    public void Case_IsIgnoredForMatching()
    {
        Assert.True(FuzzyMatcher.Match("INPC", NotifyInterface).IsMatch);
        Assert.True(FuzzyMatcher.Match("HBAN", HeroBanner).IsMatch);
        Assert.True(FuzzyMatcher.Match("hero_BANNER.PNG", HeroBanner).IsMatch);
    }

    [Fact]
    public void Case_BreaksTiesBetweenOtherwiseIdenticalCandidates()
    {
        // Same tier, same length, same alignment — only the case of the first character differs.
        Assert.Equal(FuzzyMatchKind.Prefix, Kind("ab", "abc"));
        Assert.Equal(FuzzyMatchKind.Prefix, Kind("ab", "Abc"));
        Assert.True(Score("ab", "abc") > Score("ab", "Abc"));
        Assert.Equal(new[] { "abc", "Abc" }, RankOrder("ab", "Abc", "abc"));

        // …and both matched characters differing costs twice as much.
        Assert.True(Score("ab", "Abc") > Score("ab", "ABC"));
    }

    [Fact] // …and never more than a tiebreak: structure always beats case agreement
    public void Case_NeverOutranksStructure()
    {
        Assert.Equal(FuzzyMatchKind.Prefix, Kind("ab", "ABxy"));    // wrong case, right structure
        Assert.Equal(FuzzyMatchKind.Substring, Kind("ab", "zzab")); // right case, worse structure
        Assert.True(Score("ab", "ABxy") > Score("ab", "zzab"));
    }

    // ───────────────────────────── non-matches and degenerate input ─────────────────────────────

    [Theory]
    [InlineData("sx", "textures")]    // both letters present, wrong order
    [InlineData("zzz", "textures")]   // absent entirely
    [InlineData("teee", "textures")]  // one 'e' too many
    [InlineData("abcdefghij", "abc")] // pattern longer than the candidate
    [InlineData("a", "")]             // empty candidate
    public void NoMatch_ReportsNothing(string pattern, string candidate)
    {
        var (result, spans) = Run(pattern, candidate);

        Assert.False(result.IsMatch);
        Assert.Equal(FuzzyMatchKind.None, result.Kind);
        Assert.Equal(0, result.Score);
        Assert.Empty(spans);
        Assert.False(result.SpansTruncated);
        Assert.False(FuzzyMatchResult.NoMatch.IsMatch);
    }

    [Fact] // an empty pattern is "no filter", not "no match" — the popup shows the unfiltered list
    public void EmptyPattern_MatchesEverythingWithZeroScoreAndNoSpans()
    {
        foreach (var candidate in new[] { "textures", HeroBanner, "", "\U0001F600" })
        {
            var (result, spans) = Run("", candidate);

            Assert.True(result.IsMatch);
            Assert.Equal(FuzzyMatchKind.Empty, result.Kind);
            Assert.Equal(0, result.Score);
            Assert.Empty(spans);
            Assert.False(result.SpansTruncated);
        }
    }

    [Fact]
    public void EmptyPattern_NeedsNoSpanBuffer()
    {
        Assert.Equal(0, FuzzyMatcher.MaxSpanCount(""));

        // The documented stackalloc idiom must stay legal for the empty pattern.
        Span<MatchSpan> spans = stackalloc MatchSpan[FuzzyMatcher.MaxSpanCount("")];
        Assert.Equal(0, FuzzyMatcher.Match("", "anything", spans).SpanCount);
    }

    [Fact]
    public void PatternLongerThanCandidate_IsRejected()
    {
        Assert.False(FuzzyMatcher.Match("aaaa", "aaa").IsMatch);
        Assert.False(FuzzyMatcher.Match("aaaa", "aaa", FuzzyMatchOptions.RequirePrefix).IsMatch);
    }

    [Theory] // separator-only patterns must not trip the boundary rules
    [InlineData("_", "a_b")]
    [InlineData("..", "a.b.c")]
    [InlineData("/", "src/main")]
    [InlineData("  ", "a b c")]
    public void SeparatorPatterns_MatchWithoutExploding(string pattern, string candidate)
        => Assert.True(FuzzyMatcher.Match(pattern, candidate).IsMatch);

    // ───────────────────────────── highlight spans ─────────────────────────────

    [Theory]
    [InlineData("hban", HeroBanner)]
    [InlineData("inpc", NotifyInterface)]
    [InlineData("te", "latest")]
    [InlineData("hoan", HeroBanner)]
    [InlineData("srcmaincs", "src/main/Program.cs")]
    [InlineData("readme", "readme")]
    public void Spans_AreOrderedNonOverlappingCoalescedAndInBounds(string pattern, string candidate)
    {
        var (result, spans) = Run(pattern, candidate);

        Assert.True(result.IsMatch);
        Assert.NotEmpty(spans);

        var previousEnd = -1;

        foreach (var span in spans)
        {
            Assert.True(span.Length > 0, "spans are never empty");
            Assert.True(span.Start > previousEnd, "spans ascend, never overlap, and adjacent runs are coalesced");
            Assert.True(span.End <= candidate.Length, "spans stay inside the candidate");
            previousEnd = span.End;
        }
    }

    [Fact]
    public void Spans_CoverExactlyThePatternsCharacters()
    {
        const string candidate = "src/main/Program.cs";
        var (_, spans) = Run("srcmaincs", candidate);

        Assert.Equal("srcmaincs", string.Concat(spans.Select(s => Slice(candidate, s))));
    }

    [Fact]
    public void Spans_TruncateGracefullyWhenTheBufferIsTooSmall()
    {
        var one = new MatchSpan[1];
        var result = FuzzyMatcher.Match("inpc", NotifyInterface, one);

        Assert.True(result.IsMatch);
        Assert.Equal(1, result.SpanCount);
        Assert.True(result.SpansTruncated);
        Assert.Equal(new MatchSpan(0, 2), one[0]); // what did fit is still the leading run
    }

    [Fact] // an empty destination means "spans not requested", which is not truncation
    public void Spans_NotRequested_IsNotReportedAsTruncation()
    {
        var result = FuzzyMatcher.Match("inpc", NotifyInterface);

        Assert.True(result.IsMatch);
        Assert.Equal(0, result.SpanCount);
        Assert.False(result.SpansTruncated);
    }

    [Theory]
    [InlineData("hban", HeroBanner)]
    [InlineData("inpc", NotifyInterface)]
    [InlineData("abcdef", "a_b_c_d_e_f")]
    public void MaxSpanCount_IsAlwaysEnough(string pattern, string candidate)
    {
        var (result, _) = Run(pattern, candidate);

        Assert.True(result.IsMatch);
        Assert.False(result.SpansTruncated);
        Assert.True(result.SpanCount <= FuzzyMatcher.MaxSpanCount(pattern));
    }

    [Fact]
    public void SpanlessOverloads_AgreeWithTheSpanOverload()
    {
        foreach (var candidate in new[] { HeroBanner, NotifyInterface, "latest", "zzz" })
        {
            var full = Run("ban", candidate).Result;
            var spanless = FuzzyMatcher.Match("ban", candidate);
            var withOptions = FuzzyMatcher.Match("ban", candidate, FuzzyMatchOptions.None);

            Assert.Equal(full.Kind, spanless.Kind);
            Assert.Equal(full.Score, spanless.Score);
            Assert.Equal(full.Kind, withOptions.Kind);
            Assert.Equal(full.Score, withOptions.Score);
        }
    }

    // ───────────────────────────── the contiguous-prefix mode ─────────────────────────────

    [Fact]
    public void RequirePrefix_AcceptsOnlyAContiguousPrefix()
    {
        var (prefix, spans) = Run("her", HeroBanner, FuzzyMatchOptions.RequirePrefix);

        Assert.True(prefix.IsMatch);
        Assert.Equal(FuzzyMatchKind.Prefix, prefix.Kind);
        Assert.Equal(new[] { new MatchSpan(0, 3) }, spans);

        Assert.False(FuzzyMatcher.Match("ban", HeroBanner, FuzzyMatchOptions.RequirePrefix).IsMatch);
        Assert.False(FuzzyMatcher.Match("hban", HeroBanner, FuzzyMatchOptions.RequirePrefix).IsMatch);
        Assert.True(FuzzyMatcher.Match("hban", HeroBanner).IsMatch); // …which the fuzzy mode still finds
    }

    [Fact]
    public void RequirePrefix_IsCaseInsensitiveAndReportsExactForAWholeCandidate()
    {
        Assert.Equal(FuzzyMatchKind.Prefix, FuzzyMatcher.Match("HERO", HeroBanner, FuzzyMatchOptions.RequirePrefix).Kind);
        Assert.Equal(FuzzyMatchKind.Exact, FuzzyMatcher.Match("HERO_BANNER.PNG", HeroBanner, FuzzyMatchOptions.RequirePrefix).Kind);
    }

    [Fact]
    public void RequirePrefix_MatchesTheEmptyPatternLikeTheFuzzyMode()
    {
        var result = FuzzyMatcher.Match("", HeroBanner, FuzzyMatchOptions.RequirePrefix);

        Assert.True(result.IsMatch);
        Assert.Equal(FuzzyMatchKind.Empty, result.Kind);
    }

    // ───────────────────────────── word boundaries ─────────────────────────────

    [Theory]
    [InlineData(HeroBanner, 0, true)]                // the start of the text
    [InlineData(HeroBanner, 5, true)]                // after '_'
    [InlineData(HeroBanner, 6, false)]               // mid-word
    [InlineData(HeroBanner, 12, true)]               // after '.'
    [InlineData("INotifyPropertyChanged", 1, true)]  // the acronym tail: I|No…
    [InlineData("INotifyPropertyChanged", 2, false)] // Upper→lower is not a hump
    [InlineData("INotifyPropertyChanged", 7, true)]  // lower→Upper is
    [InlineData("file2.txt", 4, true)]               // letter→digit
    [InlineData("v2x", 2, true)]                     // digit→letter
    [InlineData("src/main", 4, true)]                // after '/'
    [InlineData("C:\\Users", 3, true)]               // after '\' — Windows paths segment too
    [InlineData("hello world", 6, true)]             // after ' '
    [InlineData("a-b", 2, true)]                     // after '-'
    [InlineData("abc", 9, false)]                    // past the end
    [InlineData("abc", -1, false)]                   // before the start
    public void IsWordBoundary_ImplementsTheDocumentedRules(string text, int index, bool expected)
        => Assert.Equal(expected, FuzzyMatcher.IsWordBoundary(text, index));

    [Fact] // an offset inside a grapheme cluster is never a boundary
    public void IsWordBoundary_RejectsMidClusterOffsets()
    {
        const string text = "a\U0001F600b"; // 'a', a two-char surrogate pair, 'b'

        Assert.True(FuzzyMatcher.IsWordBoundary(text, 0));
        Assert.False(FuzzyMatcher.IsWordBoundary(text, 2)); // the low surrogate
    }

    [Fact]
    public void BackslashSeparator_DrivesBoundaryMatchesInWindowsPaths()
    {
        const string candidate = @"C:\Users\mike\docs";
        var (result, spans) = Run("umd", candidate);

        Assert.True(result.IsMatch);
        Assert.Equal(FuzzyMatchKind.Initials, result.Kind);
        Assert.Equal(new[] { new MatchSpan(3, 1), new MatchSpan(9, 1), new MatchSpan(14, 1) }, spans);
    }

    // ───────────────────────────── unicode / grapheme safety ─────────────────────────────

    [Fact] // a surrogate pair is one cluster: a span covers both code units or neither
    public void SurrogatePairs_AreNeverSplit()
    {
        const string candidate = "a\U0001F600b";
        var (result, spans) = Run("\U0001F600", candidate);

        Assert.True(result.IsMatch);
        Assert.Equal(new[] { new MatchSpan(1, 2) }, spans);
        Assert.Equal("\U0001F600", Slice(candidate, spans[0]));
    }

    [Fact] // …including when the pair sits inside a longer run
    public void SurrogatePairs_AreNeverSplitInsideARun()
    {
        const string candidate = "x\U0001F600y";
        var (_, spans) = Run("\U0001F600y", candidate);

        Assert.Equal(new[] { new MatchSpan(1, 3) }, spans);
    }

    [Fact] // 'e' + COMBINING ACUTE is one cluster; a highlight may not orphan the accent
    public void CombiningSequences_AreNeverSplit()
    {
        const string candidate = "cafe\u0301_bar.txt"; // "cafe\u0301_bar.txt", decomposed
        var (result, spans) = Run("e\u0301b", candidate);

        Assert.True(result.IsMatch);
        Assert.Equal(new[] { new MatchSpan(3, 2), new MatchSpan(6, 1) }, spans);
        Assert.Equal("e\u0301", Slice(candidate, spans[0]));
    }

    [Fact] // a ZWJ emoji sequence is a single cluster of eight code units
    public void ZeroWidthJoinerSequences_MatchAsOneCluster()
    {
        const string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";
        var candidate = $"x{family}y";
        var (result, spans) = Run(family, candidate);

        Assert.True(result.IsMatch);
        Assert.Equal(8, family.Length);
        Assert.Equal(new[] { new MatchSpan(1, family.Length) }, spans);
    }

    [Fact] // the deliberate consequence of cluster-atomic matching, documented on FuzzyMatcher
    public void ABaseCharacterAloneDoesNotMatchACombinedCluster()
    {
        Assert.False(FuzzyMatcher.Match("e", "cafe\u0301").IsMatch);
        Assert.True(FuzzyMatcher.Match("e\u0301", "cafe\u0301").IsMatch);
        Assert.True(FuzzyMatcher.Match("c", "cafe\u0301").IsMatch); // the other clusters still match
    }

    [Fact] // …and the prefix mode needs the same guard, or Tab-completion would cut the accent off
    public void RequirePrefix_RejectsAPartialCluster()
    {
        Assert.False(FuzzyMatcher.Match("e", "e\u0301clair", FuzzyMatchOptions.RequirePrefix).IsMatch);
        Assert.True(FuzzyMatcher.Match("e\u0301", "e\u0301clair", FuzzyMatchOptions.RequirePrefix).IsMatch);
    }

    [Fact]
    public void CaseInsensitivity_AppliesToNonAsciiClusters()
    {
        Assert.True(FuzzyMatcher.Match("\u00e9", "caf\u00c9").IsMatch);       // e-acute matches its uppercase form
        Assert.True(FuzzyMatcher.Match("\u00c9", "caf\u00e9").IsMatch);
        Assert.True(FuzzyMatcher.Match("e\u0301", "CAFE\u0301").IsMatch);     // …and so does a combining sequence
    }

    [Theory] // the general invariant: every reported edge is a real cluster boundary
    [InlineData("ac", "a\U0001F600b\u0301c")]
    [InlineData("\U0001F600c", "a\U0001F600b\u0301c")]
    [InlineData("cf", "\u0e01\u0e33c\u00e1fe")]
    public void EverySpanEdge_LandsOnAGraphemeClusterBoundary(string pattern, string candidate)
    {
        var (result, spans) = Run(pattern, candidate);
        Assert.True(result.IsMatch);

        var boundaries = new HashSet<int> { candidate.Length };
        var enumerator = StringInfo.GetTextElementEnumerator(candidate);

        while (enumerator.MoveNext())
            boundaries.Add(enumerator.ElementIndex);

        foreach (var span in spans)
        {
            Assert.Contains(span.Start, boundaries);
            Assert.Contains(span.End, boundaries);
        }
    }

    // ───────────────────────────── total ordering and stability ─────────────────────────────

    [Fact]
    public void CompareRank_FallsBackToTheShorterCandidate_ThenOrdinal()
    {
        // Identical shape and score, different lengths → shorter first.
        Assert.Equal(Score("ab", "abc"), Score("ab", "abcd"));
        Assert.Equal(new[] { "abc", "abcd" }, RankOrder("ab", "abcd", "abc"));

        // Identical shape, score and length → ordinal.
        Assert.Equal(Score("ab", "abc"), Score("ab", "abd"));
        Assert.Equal(new[] { "abc", "abd" }, RankOrder("ab", "abd", "abc"));
    }

    [Fact]
    public void CompareRank_SortsNonMatchesLast()
    {
        var matched = FuzzyMatcher.Match("ab", "abc");
        var missed = FuzzyMatcher.Match("ab", "zzz");

        Assert.True(FuzzyMatcher.CompareRank(matched, "abc", missed, "zzz") < 0);
        Assert.True(FuzzyMatcher.CompareRank(missed, "zzz", matched, "abc") > 0);
    }

    [Fact]
    public void CompareRank_IsAReflexiveAntisymmetricTotalOrder()
    {
        string[] candidates = ["textures", "latest", "template.json", "the_end", "zzz", "te", "TE"];

        foreach (var a in candidates)
        {
            var resultA = FuzzyMatcher.Match("te", a);
            Assert.Equal(0, FuzzyMatcher.CompareRank(resultA, a, resultA, a));

            foreach (var b in candidates)
            {
                var resultB = FuzzyMatcher.Match("te", b);
                var forward = FuzzyMatcher.CompareRank(resultA, a, resultB, b);
                var backward = FuzzyMatcher.CompareRank(resultB, b, resultA, a);

                Assert.Equal(Math.Sign(forward), -Math.Sign(backward));
                Assert.True(a == b || forward != 0, "distinct candidates must never compare equal");
            }
        }
    }

    [Fact] // the anti-flicker contract: the popup's order may not depend on enumeration order
    public void RankOrder_IsIndependentOfTheInputOrder()
    {
        string[] candidates =
        [
            "textures", "template.json", "latest", "the_end", "test", "TEST", "tree_export", "attend"
        ];

        var expected = RankOrder("te", candidates);
        var random = new Random(20260726);

        for (var trial = 0; trial < 25; trial++)
        {
            var shuffled = candidates.ToArray();

            for (var i = shuffled.Length - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }

            Assert.Equal(expected, RankOrder("te", shuffled));
        }
    }

    [Fact]
    public void Match_IsDeterministic()
    {
        var (first, firstSpans) = Run("inpc", NotifyInterface);

        for (var i = 0; i < 50; i++)
        {
            var (again, againSpans) = Run("inpc", NotifyInterface);

            Assert.Equal(first.Kind, again.Kind);
            Assert.Equal(first.Score, again.Score);
            Assert.Equal(firstSpans, againSpans);
        }
    }

    // ───────────────────────────── the over-budget fallback ─────────────────────────────

    [Fact] // past the dynamic-program budget the matcher must still answer, and answer sanely
    public void VeryLargeInputs_FallBackToGreedyAlignment_AndStillMatch()
    {
        var candidate = string.Concat(Enumerable.Repeat("abcdefghij", 400));            // 4,000 chars
        var pattern = new string(Enumerable.Range(0, 200).Select(i => candidate[i * 20]).ToArray());

        var (result, spans) = Run(pattern, candidate);

        Assert.True(result.IsMatch);
        Assert.False(result.SpansTruncated);
        Assert.Equal(pattern.Length, spans.Sum(s => s.Length));
        Assert.Equal(pattern, string.Concat(spans.Select(s => Slice(candidate, s))));
    }

    [Fact]
    public void VeryLargeInputs_StillRejectWhenNoAlignmentExists()
    {
        var candidate = string.Concat(Enumerable.Repeat("abcdefghij", 400));

        Assert.False(FuzzyMatcher.Match(new string('z', 200), candidate).IsMatch);
    }
}
