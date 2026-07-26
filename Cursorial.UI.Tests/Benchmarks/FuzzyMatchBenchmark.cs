using System.Diagnostics;
using Cursorial.UI.Matching;
using Xunit.Abstractions;

namespace Cursorial.Tests.UI.Benchmarks;

/// <summary>
/// The cost contract of <see cref="FuzzyMatcher"/>. A completion popup re-matches <em>every</em>
/// candidate on <em>every</em> keystroke, so two properties matter as much as correctness and are
/// just as easy to regress:
/// <list type="bullet">
///   <item><description><b>No allocation.</b> Scratch is <c>stackalloc</c>-ed for ordinary sizes and pooled above them, so a whole filter pass must show a zero <c>GC.GetAllocatedBytesForCurrentThread</c> delta. A future change that returns a <c>List&lt;MatchSpan&gt;</c>, boxes a result, or forgets to return a rented array fails here.</description></item>
///   <item><description><b>No pathological blow-up.</b> A long pattern must be <em>cheaper</em> than a short one, not quadratically dearer, because the scratch-free subsequence gate rejects it in one pass. The absolute ceilings below sit two orders of magnitude above the measured numbers so a loaded CI box cannot flake them; the printed timings are the real signal (and are only meaningful from a Release run).</description></item>
/// </list>
/// </summary>
[Trait("Category", "Benchmark")]
public class FuzzyMatchBenchmark(ITestOutputHelper output)
{
    private const int CandidateCount = 5_000;
    private const int Passes = 10;

    /// <summary>
    /// A deterministic corpus shaped like a large directory listing: mixed case, digits, and the
    /// separators the boundary rules key on, at realistic file-name lengths.
    /// </summary>
    private static string[] BuildCorpus(int count, int seed = 20260726)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-./ ";

        var random = new Random(seed);
        var corpus = new string[count];

        for (var i = 0; i < count; i++)
        {
            var length = 8 + random.Next(48);
            var chars = new char[length];

            for (var k = 0; k < length; k++)
                chars[k] = alphabet[random.Next(alphabet.Length)];

            corpus[i] = new string(chars);
        }

        return corpus;
    }

    private static long FilterPass(string[] corpus, string pattern, Span<MatchSpan> spans)
    {
        var checksum = 0L;

        foreach (var candidate in corpus)
            checksum += FuzzyMatcher.Match(pattern, candidate, spans).Score;

        return checksum;
    }

    [Fact]
    public void FilterPass_DoesNotAllocate()
    {
        var corpus = BuildCorpus(512);

        // The stackalloc lives outside the loop deliberately (CA2014) and is sized by the
        // documented MaxSpanCount idiom.
        Span<MatchSpan> spans = stackalloc MatchSpan[FuzzyMatcher.MaxSpanCount("banner")];

        for (var warmup = 0; warmup < 5; warmup++)
            FilterPass(corpus, "banner", spans);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var checksum = FilterPass(corpus, "banner", spans);
        var delta = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"512 candidates, pattern 'banner': {delta} bytes allocated (checksum {checksum})");
        Assert.Equal(0, delta);
    }

    [Fact] // the pooled path (scratch past the stackalloc budget) must return what it rents
    public void PooledScratchPath_DoesNotAllocate()
    {
        // 40 x 400 cells is well past both the stackalloc budget and any real completion input,
        // so this exercises the ArrayPool rent/return on every call.
        var candidate = string.Concat(Enumerable.Repeat("abcdefghij", 40));
        var pattern = new string(Enumerable.Range(0, 40).Select(i => candidate[i * 10]).ToArray());

        Span<MatchSpan> spans = stackalloc MatchSpan[FuzzyMatcher.MaxSpanCount(pattern)];

        for (var warmup = 0; warmup < 5; warmup++)
            FuzzyMatcher.Match(pattern, candidate, spans);

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 100; i++)
            Assert.True(FuzzyMatcher.Match(pattern, candidate, spans).IsMatch);

        var delta = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"100 pooled-scratch matches ({pattern.Length} x {candidate.Length}): {delta} bytes allocated");
        Assert.Equal(0, delta);
    }

    [Fact]
    public void LongPattern_IsNotSlowerThanAShortOne_OverALargeCorpus()
    {
        var corpus = BuildCorpus(CandidateCount);

        Span<MatchSpan> shortSpans = stackalloc MatchSpan[FuzzyMatcher.MaxSpanCount("ban")];
        var longPattern = new string(Enumerable.Range(0, 256).Select(i => (char) ('a' + i % 26)).ToArray());
        Span<MatchSpan> longSpans = stackalloc MatchSpan[64];

        // Warm up both shapes before either is timed.
        FilterPass(corpus, "ban", shortSpans);
        FilterPass(corpus, longPattern, longSpans);

        var watch = Stopwatch.StartNew();

        for (var pass = 0; pass < Passes; pass++)
            FilterPass(corpus, "ban", shortSpans);

        var shortElapsed = watch.Elapsed;
        watch.Restart();

        for (var pass = 0; pass < Passes; pass++)
            FilterPass(corpus, longPattern, longSpans);

        var longElapsed = watch.Elapsed;

        output.WriteLine($"{Passes} x {CandidateCount} candidates, 3-char pattern:   {shortElapsed.TotalMilliseconds:F1} ms");
        output.WriteLine($"{Passes} x {CandidateCount} candidates, 256-char pattern: {longElapsed.TotalMilliseconds:F1} ms");

        // The subsequence gate means a long pattern dies on its first missing character, so it can
        // never cost a multiple of the short-pattern pass. The ceilings are ~100x the measured
        // numbers on developer hardware — they catch an accidental O(pattern x candidate) on the
        // reject path, not a slow machine.
        Assert.True(shortElapsed < TimeSpan.FromSeconds(10), $"short-pattern pass took {shortElapsed}");
        Assert.True(longElapsed < TimeSpan.FromSeconds(10), $"long-pattern pass took {longElapsed}");
        Assert.True(
            longElapsed < shortElapsed + TimeSpan.FromSeconds(5),
            $"a long pattern must not cost more than a short one (short {shortElapsed}, long {longElapsed})");
    }

    [Fact] // the worst realistic case: a pattern that matches *everything*, so the DP runs every time
    public void MatchingEveryCandidate_StaysWithinBudget()
    {
        var corpus = BuildCorpus(CandidateCount)
            .Select(candidate => $"banner_{candidate}")
            .ToArray();

        Span<MatchSpan> spans = stackalloc MatchSpan[FuzzyMatcher.MaxSpanCount("banner")];

        var matched = 0;

        foreach (var candidate in corpus)
        {
            if (FuzzyMatcher.Match("banner", candidate, spans).IsMatch)
                matched++;
        }

        Assert.Equal(corpus.Length, matched); // every candidate really does reach the scorer

        var watch = Stopwatch.StartNew();

        for (var pass = 0; pass < Passes; pass++)
            FilterPass(corpus, "banner", spans);

        watch.Stop();

        output.WriteLine($"{Passes} x {CandidateCount} all-matching candidates: {watch.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"all-matching pass took {watch.Elapsed}");
    }
}
