using System.Text;

using Cursorial.UI.Matching;

namespace Cursorial.UI.Controls;

/// <summary>
/// One <see cref="CompletionItem"/> after <see cref="CompletionPopup"/> has matched it: the item, the
/// <see cref="FuzzyMatcher"/> verdict, the highlight runs, and the pre-built BBCode the row renders.
/// These are the objects the popup's list actually holds, so a custom row template can bind straight
/// to them.
/// </summary>
public sealed class CompletionEntry
{
    private static readonly MatchSpan[] NoSpans = [];

    private readonly MatchSpan[] _spans;

    internal CompletionEntry(CompletionItem item, in FuzzyMatchResult match, MatchSpan[] spans, string? kindLabel, int sourceIndex)
    {
        Item = item;
        Match = match;
        _spans = spans;
        KindLabel = kindLabel;
        SourceIndex = sourceIndex;
        DisplayMarkup = BuildDisplayMarkup(item.Display, spans);
    }

    /// <summary>The candidate this row came from.</summary>
    public CompletionItem Item { get; }

    /// <summary>The tier the pattern landed in — <see cref="FuzzyMatchKind.Empty"/> when nothing was typed yet.</summary>
    public FuzzyMatchKind MatchKind => Match.Kind;

    /// <summary>The raw <see cref="FuzzyMatchResult.Score"/>. Only comparable within one filter pass; treat it as opaque.</summary>
    public int Score => Match.Score;

    /// <summary>The full matcher verdict — kept whole so the popup can rank through <see cref="FuzzyMatcher.CompareRank"/>.</summary>
    internal FuzzyMatchResult Match { get; }

    /// <summary>The candidate's index in the provider's list — the sort's final, order-preserving tiebreak.</summary>
    internal int SourceIndex { get; }

    /// <summary>
    /// The matched character runs inside <see cref="CompletionItem.Display"/>, ascending and
    /// cluster-aligned. Empty when the pattern was empty. A custom presenter can paint these itself
    /// instead of using <see cref="DisplayMarkup"/>.
    /// </summary>
    public ReadOnlySpan<MatchSpan> MatchSpans => _spans;

    /// <summary>
    /// <see cref="CompletionItem.Display"/> as BBCode with the matched runs wrapped in <c>[b]…[/b]</c> —
    /// what the default row template feeds <c>TextBlock.Markup</c> to bold exactly the matched cells.
    /// Markup metacharacters in the display text are escaped, so a candidate literally named
    /// <c>[Price]</c> renders as <c>[Price]</c> and not as a malformed tag.
    /// </summary>
    public string DisplayMarkup { get; }

    /// <summary>
    /// The composed right-aligned label: the item's <see cref="CompletionItem.KindLabel"/>, with the
    /// popup's <see cref="CompletionPopup.FuzzyLabel"/> folded in when this row only matched as a
    /// scattered subsequence — the design mock's <c>folder · fuzzy</c>. <see langword="null"/> ⇒ no label column.
    /// </summary>
    public string? KindLabel { get; }

    /// <summary>Whether the pattern matched as a scattered subsequence rather than a prefix / word-boundary hit.</summary>
    public bool IsFuzzyMatch => MatchKind == FuzzyMatchKind.Subsequence;

    /// <summary>The display text — so a plain <c>{Binding}</c> row template needs no ceremony.</summary>
    public override string ToString() => Item.Display;

    /// <summary>The canonical "no highlight runs" array, shared so an unfiltered list allocates none.</summary>
    internal static MatchSpan[] EmptySpans => NoSpans;

    // ───────────────────────────── highlight markup ─────────────────────────────
    //
    // The rows are painted by a plain TextBlock in Markup mode rather than by a bespoke presenter,
    // because the text tier already knows how to carry per-run attributes through formatting, the
    // width negotiation and the cell buffer — a hand-rolled painter would have to re-derive grapheme
    // clustering and wide-cell handling to bold a run correctly.
    //
    // The escape pass is NOT optional decoration. A completion popup's candidates are user data, and
    // the two reference consumers produce display strings full of markup metacharacters: the criteria
    // editor completes bracketed field names ("[Price]") and a path bar completes Windows paths
    // ("C:\Users"). Feeding those to the BBCode parser unescaped throws FormatException ("unknown tag
    // name") on the first row — the parser is strict by design. Escaping per CHARACTER (never per run)
    // is also what keeps the [b] boundaries honest: a MatchSpan offset can never land inside one of
    // our two-character escapes.

    /// <summary>
    /// Escapes <paramref name="text"/> so the BBCode parser reproduces it literally — the no-highlight
    /// form of <see cref="DisplayMarkup"/>, for a row driven straight from a raw item.
    /// </summary>
    /// <param name="text">The literal text to render.</param>
    internal static string EscapeMarkup(string text) => BuildDisplayMarkup(text, ReadOnlySpan<MatchSpan>.Empty);

    private static string BuildDisplayMarkup(string display, ReadOnlySpan<MatchSpan> spans)
    {
        if (spans.IsEmpty && !NeedsEscape(display))
            return display;

        var builder = new StringBuilder(display.Length + spans.Length * 7);
        var next = 0;
        var bold = false;

        for (var i = 0; i < display.Length; i++)
        {
            if (bold && spans[next].End == i)
            {
                builder.Append("[/b]");
                bold = false;
                next++;
            }

            if (!bold && next < spans.Length && spans[next].Start == i)
            {
                builder.Append("[b]");
                bold = true;
            }

            AppendEscaped(builder, display[i]);
        }

        if (bold)
            builder.Append("[/b]");

        return builder.ToString();
    }

    private static bool NeedsEscape(string text) => text.AsSpan().IndexOfAny('[', ']', '\\') >= 0;

    private static void AppendEscaped(StringBuilder builder, char c)
    {
        if (c is '[' or ']' or '\\')
            builder.Append('\\');

        builder.Append(c);
    }
}
