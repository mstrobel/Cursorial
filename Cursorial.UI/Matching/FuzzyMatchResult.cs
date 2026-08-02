namespace Cursorial.UI.Matching;

/// <summary>
/// The verdict for one <c>(pattern, candidate)</c> pair: whether it matched, how well
/// (<see cref="Score"/>), in what shape (<see cref="Kind"/>), and how many highlight runs were
/// written into the caller's <see cref="MatchSpan"/> buffer.
/// <para>
/// The struct deliberately carries <em>no</em> span storage of its own. A completion popup
/// re-matches every candidate on every keystroke, so the matcher writes runs into a buffer the
/// caller owns — normally <c>stackalloc MatchSpan[FuzzyMatcher.MaxSpanCount(pattern)]</c>, or a
/// pooled array when the caller wants to keep the highlights for the visible rows. That keeps a
/// filter pass over thousands of candidates free of per-candidate allocation, which a
/// <c>List&lt;MatchSpan&gt;</c>-returning API could not be.
/// </para>
/// </summary>
public readonly struct FuzzyMatchResult
{
    internal FuzzyMatchResult(FuzzyMatchKind kind, int score, int spanCount, bool spansTruncated)
    {
        Kind = kind;
        Score = score;
        SpanCount = spanCount;
        SpansTruncated = spansTruncated;
    }

    /// <summary>The shape of the match — also the coarse ranking tier. <see cref="FuzzyMatchKind.None"/> when nothing matched.</summary>
    public FuzzyMatchKind Kind { get; }

    /// <summary>
    /// Ranking score, <b>higher is better</b>, and <c>0</c> for a non-match or for the empty
    /// pattern. Scores are only comparable between candidates matched against the <em>same</em>
    /// pattern; treat the magnitude as opaque. Two candidates can legitimately tie — use
    /// <see cref="FuzzyMatcher.CompareRank"/> for a total order rather than sorting on this alone.
    /// </summary>
    public int Score { get; }

    /// <summary>
    /// Number of <see cref="MatchSpan"/> entries written to the caller's buffer (0 when no buffer
    /// was supplied, when nothing matched, or for the empty pattern).
    /// </summary>
    public int SpanCount { get; }

    /// <summary>
    /// <see langword="true"/> when the match produced more highlight runs than the caller's buffer
    /// could hold; the first <see cref="SpanCount"/> runs are still valid and in order. A buffer of
    /// <see cref="FuzzyMatcher.MaxSpanCount"/> entries never truncates, and an <em>empty</em>
    /// destination means "spans were not requested" rather than "truncated", so the score-only
    /// overloads always report <see langword="false"/>.
    /// </summary>
    public bool SpansTruncated { get; }

    /// <summary>Whether the pattern matched at all — equivalently, <c>Kind != FuzzyMatchKind.None</c>.</summary>
    public bool IsMatch => Kind != FuzzyMatchKind.None;

    /// <summary>The canonical "did not match" result — <see langword="default"/>.</summary>
    public static FuzzyMatchResult NoMatch => default;

    /// <summary>Renders the verdict as <c>Kind(score=…, spans=…)</c> for diagnostics and test failures.</summary>
    public override string ToString()
        => IsMatch ? $"{Kind}(score={Score}, spans={SpanCount}{(SpansTruncated ? "+" : "")})" : "None";
}
