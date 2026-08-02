namespace Cursorial.UI.Matching;

/// <summary>
/// How a pattern landed on a candidate — the coarse ranking tier that dominates
/// <see cref="FuzzyMatchResult.Score"/>, and the label a completion popup shows beside a row (the
/// design mock's "folder · <em>fuzzy</em>" annotation in
/// <c>docs/ui-layer-design/tokyo-night-terminal-colorpicker-filedialogs.html</c> is exactly
/// <see cref="Subsequence"/>).
/// <para>
/// The numeric values are <b>ordered by rank</b>: a larger value always ranks ahead of a smaller
/// one regardless of the per-character detail score, because <see cref="FuzzyMatcher"/> composes
/// the score as <c>(int)Kind * stride + detail</c> with the detail term saturated below the stride.
/// That gives the popup stable, explainable tiers instead of a single opaque number in which a
/// dense mid-word match could accidentally outrank a literal prefix.
/// </para>
/// <para>
/// There is no WPF/Avalonia analogue — neither framework ships a completion matcher. The tier
/// vocabulary follows the conventions users already know from VS Code's quick-open filters
/// (prefix ≫ word-boundary ≫ scattered), IntelliJ's <c>MinusculeMatcher</c> (whose "all humps"
/// degree is <see cref="Initials"/>), and fzy/fzf.
/// </para>
/// </summary>
public enum FuzzyMatchKind
{
    /// <summary>The pattern is not a subsequence of the candidate — no match at all.</summary>
    None = 0,

    /// <summary>
    /// The pattern was empty, so the candidate matches trivially with a zero score and no spans.
    /// A filter should treat this as "show everything, highlight nothing".
    /// </summary>
    Empty = 1,

    /// <summary>
    /// The pattern's characters appear in order but scattered, with at least one run starting
    /// somewhere that is not a word boundary — the classic "fuzzy" hit. Ranks last among matches.
    /// </summary>
    Subsequence = 2,

    /// <summary>
    /// The whole pattern appears as one contiguous run that neither starts the candidate nor
    /// starts a word (e.g. <c>te</c> inside <c>latest</c>).
    /// </summary>
    Substring = 3,

    /// <summary>
    /// Every contiguous run of the match <em>begins</em> at a word boundary, though some
    /// characters inside a run do not (e.g. <c>hban</c> against <c>hero_banner.png</c>: the runs
    /// <c>h</c> and <c>ban</c> start the name and the <c>banner</c> segment).
    /// </summary>
    Boundary = 4,

    /// <summary>
    /// <em>Every</em> matched character sits on a word boundary — the initials/camel-hump match
    /// (e.g. <c>inpc</c> against <c>INotifyPropertyChanged.cs</c>). Ranks above
    /// <see cref="Boundary"/> because an all-humps hit is the strongest signal short of a literal
    /// prefix.
    /// </summary>
    Initials = 5,

    /// <summary>
    /// The candidate starts with the pattern (case-insensitively) but is longer — the completion
    /// case the popup should always float to the top (<c>te</c> → <c>textures</c>).
    /// </summary>
    Prefix = 6,

    /// <summary>The candidate equals the pattern, ignoring case. The highest tier.</summary>
    Exact = 7,
}
