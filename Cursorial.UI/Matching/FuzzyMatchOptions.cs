namespace Cursorial.UI.Matching;

/// <summary>
/// Opt-in constraints for <see cref="FuzzyMatcher.Match(System.ReadOnlySpan{char},System.ReadOnlySpan{char},System.Span{MatchSpan},FuzzyMatchOptions)"/>.
/// The default (<see cref="None"/>) is the completion-popup behaviour: case-insensitive, fuzzy,
/// with case and word boundaries folded into the score rather than used as gates.
/// </summary>
[Flags]
public enum FuzzyMatchOptions
{
    /// <summary>Case-insensitive subsequence matching — the default.</summary>
    None = 0,

    /// <summary>
    /// Require the candidate to <b>start with</b> the pattern as one contiguous, case-insensitive
    /// run; anything else is rejected outright. This is the "Tab completes" half of the path
    /// bar (the completion the popup would insert must be a real prefix of what the user typed),
    /// and it is also the cheap mode for very large candidate sets — the whole fuzzy pipeline is
    /// skipped in favour of a single comparison, so results are only ever
    /// <see cref="FuzzyMatchKind.Prefix"/> or <see cref="FuzzyMatchKind.Exact"/>.
    /// </summary>
    RequirePrefix = 1 << 0,
}
