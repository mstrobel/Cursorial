namespace Cursorial.UI.Matching;

/// <summary>
/// One contiguous run of matched characters inside a candidate string, reported by
/// <see cref="FuzzyMatcher"/> as a half-open UTF-16 slice <c>[Start, End)</c> so a presenter can
/// bold it directly with <c>candidate.AsSpan(span.Start, span.Length)</c> — the "matched characters
/// bold-highlighted" contract of the completion dropdown in
/// <c>docs/ui-layer-design/tokyo-night-terminal-colorpicker-filedialogs.html</c> ("Breadcrumb in
/// edit mode — typing a path with completion") and of the tag-input dropdown in
/// <c>tokyo-night-terminal-rich-editors.html</c>.
/// <para>
/// <b>Indices are UTF-16 char offsets, not grapheme ordinals</b> — that is what
/// <c>string.AsSpan(start, length)</c>, <c>RenderContext</c> text draws, and
/// <c>Cursorial.Rendering.Text</c> runs all consume. They are nevertheless always
/// <em>cluster-aligned</em>: <see cref="FuzzyMatcher"/> matches whole grapheme clusters, so a span
/// can never begin or end in the middle of a surrogate pair or of a combining sequence (see the
/// grapheme-safety notes on <see cref="FuzzyMatcher"/>).
/// </para>
/// <para>
/// Spans arrive in ascending, non-overlapping, non-adjacent order (two adjacent runs are always
/// coalesced into one), and every span has <see cref="Length"/> &gt; 0.
/// </para>
/// </summary>
/// <param name="Start">Index of the first matched character, a 0-based UTF-16 offset into the candidate.</param>
/// <param name="Length">Number of matched characters; always greater than zero.</param>
public readonly record struct MatchSpan(int Start, int Length)
{
    /// <summary>Exclusive end of the run — <c>Start + Length</c>.</summary>
    public int End => Start + Length;

    /// <summary>Renders the run as the half-open interval <c>[Start..End)</c>.</summary>
    public override string ToString() => $"[{Start}..{End})";
}
