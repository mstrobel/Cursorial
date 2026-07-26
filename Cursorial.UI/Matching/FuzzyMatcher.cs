using System.Buffers;
using System.Diagnostics;
using System.Text;
using Cursorial.Text;

namespace Cursorial.UI.Matching;

/// <summary>
/// The shared fuzzy/subsequence matcher behind every completion surface in the framework: the file
/// dialog's path-bar dropdown ("matches filtered on the partial segment with the matched characters
/// bold-highlighted … fuzzy matches included and labelled" —
/// <c>docs/ui-layer-design/tokyo-night-terminal-colorpicker-filedialogs.html</c>, "Breadcrumb in
/// edit mode"), the tag input's suggestion list
/// (<c>tokyo-night-terminal-rich-editors.html</c>), and the DataViews criteria/expression editor's
/// field and function completion.
/// <para>
/// <b>What it does.</b> <see cref="Match(ReadOnlySpan{char},ReadOnlySpan{char},Span{MatchSpan},FuzzyMatchOptions)"/>
/// decides whether a pattern occurs in a candidate as an ordered subsequence, picks the
/// <em>best</em> of the (often many) possible alignments, scores it, classifies it into a
/// <see cref="FuzzyMatchKind"/> ranking tier, and writes the matched character runs into a
/// caller-owned <see cref="MatchSpan"/> buffer so the popup can bold exactly those cells.
/// </para>
/// <para>
/// <b>Match qualities.</b> Word boundaries drive the score: the first character of the candidate,
/// the first character after a <c>_ - . space / \</c> separator, a camel hump (<c>aB</c>, plus the
/// acronym tail <c>HTMLParser</c> → <c>P</c> / <c>INotifyPropertyChanged</c> → <c>N</c>), and
/// digit↔letter transitions. That is what makes <c>hban</c> find <c>hero_banner.png</c> and
/// <c>inpc</c> find <c>INotifyPropertyChanged.cs</c> while a literal prefix (<c>te</c> →
/// <c>textures</c>) still outranks a mid-word hit (<c>te</c> → <c>latest</c>): the tier
/// (<see cref="FuzzyMatchKind"/>) dominates the score, and the per-character detail only orders
/// candidates <em>within</em> a tier.
/// </para>
/// <para>
/// <b>Alignment choice is exact, not greedy.</b> Greedy scanning gets <c>inpc</c> wrong from one
/// side or the other — a left-to-right walk of <c>INotifyPropertyChanged</c> is lucky, a
/// right-to-left walk lands <c>p</c> on the lowercase <c>p</c> of <c>Property</c> and loses a hump.
/// The matcher therefore runs a small dynamic program over (pattern cluster × candidate cluster)
/// that maximises the very scoring function <see cref="ScoreAlignment"/> defines, and recovers the
/// winning positions by traceback. It degrades to a two-pass greedy alignment only above
/// <see cref="DpCellBudget"/> cells, which realistic completion input never reaches.
/// </para>
/// <para>
/// <b>Cost and allocation.</b> Every call is <c>O(pattern × candidate)</c> in the worst case, but a
/// single <c>O(candidate)</c> subsequence gate rejects non-matches <em>before any scratch is
/// touched</em> — the case that dominates a keystroke over a large directory, and a longer pattern
/// is therefore <em>cheaper</em> to reject, not dearer. Scratch is <c>stackalloc</c>-ed for ordinary
/// sizes and rented from <see cref="ArrayPool{T}"/> above that, so a filter pass over thousands of
/// candidates allocates nothing; there is deliberately no per-candidate
/// <c>List&lt;MatchSpan&gt;</c>. The allocation and timing contracts are asserted in
/// <c>Cursorial.UI.Tests/Benchmarks/FuzzyMatchBenchmark.cs</c>.
/// </para>
/// <para>
/// <b>Grapheme safety.</b> Matching operates on <em>grapheme clusters</em> (via
/// <c>Cursorial.Text.GraphemeEnumerator</c>, the same segmentation the renderer uses), never on raw
/// UTF-16 code units, so a reported <see cref="MatchSpan"/> can never split a surrogate pair or cut
/// a combining sequence off its base character — a highlight always covers whole terminal cells.
/// The consequence, which is intended: a cluster matches as a unit, so the pattern <c>e</c> does
/// <em>not</em> match the cluster <c>é</c> (<c>é</c>). Comparison is ordinal-ignore-case and
/// culture-independent, with no Unicode normalization — a decomposed <c>é</c> and a precomposed
/// <c>é</c> are different clusters. An all-ASCII pattern <em>and</em> candidate (the overwhelmingly
/// common case) skips segmentation entirely.
/// </para>
/// <para>
/// <b>Parity.</b> There is no WPF or Avalonia counterpart to copy — neither framework ships a
/// completion matcher, and <c>ICollectionView</c>-style filtering has no notion of a score or of
/// highlight spans. The behaviour users expect here comes from editors instead: VS Code's
/// <c>filters.ts</c> (prefix ≫ word-boundary ≫ scattered), IntelliJ's <c>MinusculeMatcher</c>
/// (camel humps), and fzy/fzf's alignment scorers, whose gap/consecutive/bonus decomposition this
/// scoring function follows.
/// </para>
/// </summary>
public static class FuzzyMatcher
{
    // ───────────────────────────── the score model ─────────────────────────────
    //
    // Score = (int)Kind * TierStride + clamp(DetailBias + detail, 0, TierStride - 1).
    //
    // The tier is the coarse, explainable ordering the popup shows; `detail` only breaks ties
    // *within* a tier. The clamp is what guarantees that — a pathological candidate (a match
    // spread over a hundred-thousand-character path) saturates its detail term instead of
    // bleeding into a neighbouring tier and inverting the ranking. DetailBias re-centres the
    // signed detail so the clamp's floor is never hit by any realistic input.

    private const int TierStride = 100_000;
    private const int DetailBias = 50_000;

    /// <summary>Bonus for matching the very first cluster of the candidate.</summary>
    private const int BonusStart = 16;

    /// <summary>Bonus for matching the first cluster after a <c>_ - . space / \</c> separator.</summary>
    private const int BonusSeparator = 12;

    /// <summary>Bonus for matching a camel hump — <c>aB</c>, or the <c>B</c> of an acronym tail <c>ABc</c>.</summary>
    private const int BonusCamel = 12;

    /// <summary>Bonus for matching across a digit↔letter transition (<c>file2</c>, <c>v2x</c>).</summary>
    private const int BonusDigit = 8;

    /// <summary>
    /// Bonus for a cluster matched immediately after the previous matched cluster. Deliberately
    /// below <see cref="BonusSeparator"/>/<see cref="BonusCamel"/>: a run that continues *into* a
    /// word boundary takes the boundary bonus instead (the two are a max, never a sum).
    /// </summary>
    private const int BonusConsecutive = 10;

    /// <summary>
    /// Tiebreak-only bonus for a cluster whose case matches the pattern exactly. One point, an
    /// order of magnitude below every structural bonus, so case can separate two otherwise
    /// identical candidates but can never reorder a structurally better one.
    /// </summary>
    private const int BonusExactCase = 1;

    /// <summary>Penalty per cluster skipped before the first match — capped by <see cref="MaxLeadingGap"/>.</summary>
    private const int LeadingGapPerUnit = -1;

    /// <summary>
    /// Floor for the leading-gap penalty. Uncapped, a long directory prefix would out-penalize
    /// every boundary bonus and the matcher would prefer an early mid-word hit over a clean
    /// segment start deep in the path.
    /// </summary>
    private const int MaxLeadingGap = -8;

    /// <summary>Penalty per cluster skipped between two matched clusters — the compactness pressure.</summary>
    private const int InnerGapPerUnit = -1;

    /// <summary>Sentinel for "no alignment reaches this cell"; divided down so it survives an addition.</summary>
    private const int NegativeInfinity = int.MinValue / 4;

    /// <summary>
    /// Above this many <c>(pattern cluster × candidate cluster)</c> cells the exact dynamic program
    /// is replaced by the greedy fallback. 32k cells is a 64-character pattern against a
    /// 512-character path — an order of magnitude past anything a completion popup types.
    /// </summary>
    private const int DpCellBudget = 1 << 15;

    /// <summary>Scratch below this many ints comes off the stack; above it, from <see cref="ArrayPool{T}"/>.</summary>
    private const int StackAllocIntBudget = 512;

    // ───────────────────────────── public surface ─────────────────────────────

    /// <summary>
    /// The <see cref="MatchSpan"/> buffer size that can never truncate for this pattern: a match
    /// produces at most one run per pattern cluster, and there are never more clusters than chars.
    /// Use it as <c>stackalloc MatchSpan[FuzzyMatcher.MaxSpanCount(pattern)]</c>.
    /// </summary>
    /// <param name="pattern">The pattern the buffer will be used with.</param>
    public static int MaxSpanCount(ReadOnlySpan<char> pattern) => pattern.Length;

    /// <summary>
    /// Scores <paramref name="pattern"/> against <paramref name="candidate"/> without reporting
    /// highlight spans — the cheap form for a filter pass that only needs to know "does it match,
    /// and how well".
    /// </summary>
    /// <param name="pattern">What the user typed. Empty matches every candidate with score 0.</param>
    /// <param name="candidate">The candidate string (a file name, a field name, a tag…).</param>
    public static FuzzyMatchResult Match(ReadOnlySpan<char> pattern, ReadOnlySpan<char> candidate)
        => Match(pattern, candidate, Span<MatchSpan>.Empty, FuzzyMatchOptions.None);

    /// <summary>
    /// Scores <paramref name="pattern"/> against <paramref name="candidate"/> under
    /// <paramref name="options"/> without reporting highlight spans.
    /// </summary>
    /// <param name="pattern">What the user typed. Empty matches every candidate with score 0.</param>
    /// <param name="candidate">The candidate string (a file name, a field name, a tag…).</param>
    /// <param name="options">Match constraints; see <see cref="FuzzyMatchOptions"/>.</param>
    public static FuzzyMatchResult Match(ReadOnlySpan<char> pattern, ReadOnlySpan<char> candidate, FuzzyMatchOptions options)
        => Match(pattern, candidate, Span<MatchSpan>.Empty, options);

    /// <summary>
    /// Matches <paramref name="pattern"/> against <paramref name="candidate"/>, writing the matched
    /// character runs into <paramref name="spans"/> for highlighting.
    /// <para>
    /// Matching is case-insensitive (ordinal, culture-independent); an exact-case cluster only
    /// earns a one-point tiebreak. Pass a buffer of <see cref="MaxSpanCount"/> entries — normally
    /// <c>stackalloc</c>-ed — to be sure the highlight is complete; a shorter buffer is legal and
    /// sets <see cref="FuzzyMatchResult.SpansTruncated"/>. Pass <see cref="Span{T}.Empty"/> to skip
    /// span reporting altogether.
    /// </para>
    /// </summary>
    /// <param name="pattern">
    /// What the user typed. An empty pattern matches every candidate with
    /// <see cref="FuzzyMatchKind.Empty"/>, score 0 and no spans — the unfiltered list.
    /// </param>
    /// <param name="candidate">The candidate string (a file name, a field name, a tag…).</param>
    /// <param name="spans">
    /// Caller-owned destination for the matched runs, in ascending order. Only the first
    /// <see cref="FuzzyMatchResult.SpanCount"/> entries are written; the rest are left untouched.
    /// </param>
    /// <param name="options">Match constraints; see <see cref="FuzzyMatchOptions"/>.</param>
    public static FuzzyMatchResult Match(
        ReadOnlySpan<char> pattern,
        ReadOnlySpan<char> candidate,
        Span<MatchSpan> spans,
        FuzzyMatchOptions options = FuzzyMatchOptions.None)
    {
        if (pattern.IsEmpty)
            return new FuzzyMatchResult(FuzzyMatchKind.Empty, 0, 0, false);

        // A cluster can only match a cluster of the same UTF-16 length (ordinal-ignore-case is
        // length-preserving), so a pattern longer *in chars* than the candidate can never match —
        // the cheapest possible reject, and it also guards the buffer sizing below.
        if (candidate.IsEmpty || pattern.Length > candidate.Length)
            return FuzzyMatchResult.NoMatch;

        // The ASCII gate is the whole reason segmentation costs nothing on the hot path: file
        // names, identifiers and tags are ASCII in the overwhelming majority, and for ASCII the
        // cluster table is the identity map, so StringInfo is never consulted.
        var asciiOnly = Ascii.IsValid(pattern) && Ascii.IsValid(candidate);
        var requirePrefix = (options & FuzzyMatchOptions.RequirePrefix) != 0;

        // ───────────────────────────── the scratch-free fast reject ─────────────────────────────
        //
        // Ordering matters more here than anywhere else in the file. A completion popup re-matches
        // *every* candidate on *every* keystroke and nearly all of them fail, so the reject path
        // must not touch the scratch buffer at all: a `stackalloc` is zero-initialized, and paying
        // a couple of kilobytes of memset per rejected candidate measurably dominated the whole
        // filter pass before this gate was hoisted above the allocation. Everything below the gate
        // runs only for candidates that really do contain the pattern.

        if (requirePrefix)
        {
            if (!candidate.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                return FuzzyMatchResult.NoMatch;
        }
        else if (!IsSubsequence(pattern, candidate, asciiOnly))
        {
            return FuzzyMatchResult.NoMatch;
        }

        var patternUnits = CountUnits(pattern, asciiOnly);
        var candidateUnits = CountUnits(candidate, asciiOnly);

        if (patternUnits > candidateUnits)
            return FuzzyMatchResult.NoMatch;

        var useDynamicProgram = (long) patternUnits * candidateUnits <= DpCellBudget;
        var predecessorCells = useDynamicProgram ? patternUnits * candidateUnits : 0;

        // One scratch allocation for everything: the two cluster-start tables (+1 sentinel each),
        // the per-cluster boundary bonuses, the six rolling DP row arrays, three position vectors,
        // and the traceback predecessor matrix.
        var scratchLength = 4 * patternUnits + 8 * candidateUnits + 2 + predecessorCells;

        int[]? rented = null;
        var scratch = scratchLength <= StackAllocIntBudget
            ? stackalloc int[scratchLength]
            : (rented = ArrayPool<int>.Shared.Rent(scratchLength)).AsSpan(0, scratchLength);

        try
        {
            var offset = 0;
            var patternStarts = Take(scratch, ref offset, patternUnits + 1);
            var candidateStarts = Take(scratch, ref offset, candidateUnits + 1);
            var bonus = Take(scratch, ref offset, candidateUnits);
            var dPrevious = Take(scratch, ref offset, candidateUnits);
            var dCurrent = Take(scratch, ref offset, candidateUnits);
            var gPreviousValue = Take(scratch, ref offset, candidateUnits);
            var gPreviousIndex = Take(scratch, ref offset, candidateUnits);
            var gCurrentValue = Take(scratch, ref offset, candidateUnits);
            var gCurrentIndex = Take(scratch, ref offset, candidateUnits);
            var forward = Take(scratch, ref offset, patternUnits);
            var backward = Take(scratch, ref offset, patternUnits);
            var positions = Take(scratch, ref offset, patternUnits);
            var predecessors = Take(scratch, ref offset, predecessorCells);

            FillUnitStarts(pattern, asciiOnly, patternStarts);
            FillUnitStarts(candidate, asciiOnly, candidateStarts);

            // The context takes a read-only view of `bonus`, which is still blank at this point:
            // ComputeBonuses writes through the mutable alias below and the context sees it, so the
            // table is only ever built on a path that actually needs a score.
            var context = new MatchContext(pattern, candidate, patternStarts, candidateStarts, bonus, asciiOnly);

            // ── the contiguous-prefix mode: the alignment is already known ──
            if (requirePrefix)
            {
                // StartsWith proved the characters; this proves the *clusters*. Without it the
                // pattern "e" would count as a prefix of "éclair" spelled e + combining acute,
                // and the highlight would cut the accent off its base character.
                if (candidateStarts[patternUnits] != pattern.Length)
                    return FuzzyMatchResult.NoMatch;

                for (var i = 0; i < patternUnits; i++)
                    positions[i] = i;

                ComputeBonuses(context, bonus);
                return Finish(context, positions, ScoreAlignment(context, positions), spans);
            }

            ComputeBonuses(context, bonus);

            if (useDynamicProgram)
            {
                RunDynamicProgram(context, bonus, dPrevious, dCurrent, gPreviousValue, gPreviousIndex, gCurrentValue, gCurrentIndex, predecessors, positions);
            }
            else
            {
                // Fallback for absurd inputs (see DpCellBudget): score the two greedy alignments —
                // leftmost, and rightmost-compact — and keep the better one. Deterministic, and
                // O(candidate) instead of O(pattern × candidate).
                var aligned = GreedyForward(context, forward);
                Debug.Assert(aligned, "the subsequence gate above already proved an alignment exists");

                GreedyBackward(context, backward);

                var forwardScore = ScoreAlignment(context, forward);
                var backwardScore = ScoreAlignment(context, backward);
                (backwardScore > forwardScore ? backward : forward).CopyTo(positions);
            }

            return Finish(context, positions, ScoreAlignment(context, positions), spans);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<int>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// The <b>total</b> ordering a completion list should sort by: better score first, then the
    /// shorter candidate, then ordinal. Returns a negative number when <paramref name="left"/>
    /// should be listed first.
    /// <para>
    /// Score alone is not a total order — many candidates tie — and a sort that leaves ties in
    /// input order makes the popup visibly reshuffle as the user types (the directory enumeration
    /// order is not stable across refreshes). The length-then-ordinal fallback pins the order to
    /// the strings themselves, so identical input always produces an identical list. Non-matches
    /// sort after every match.
    /// </para>
    /// </summary>
    /// <param name="left">Result for <paramref name="leftCandidate"/>.</param>
    /// <param name="leftCandidate">The candidate <paramref name="left"/> was produced from.</param>
    /// <param name="right">Result for <paramref name="rightCandidate"/>.</param>
    /// <param name="rightCandidate">The candidate <paramref name="right"/> was produced from.</param>
    public static int CompareRank(
        in FuzzyMatchResult left,
        ReadOnlySpan<char> leftCandidate,
        in FuzzyMatchResult right,
        ReadOnlySpan<char> rightCandidate)
    {
        if (left.IsMatch != right.IsMatch)
            return left.IsMatch ? -1 : 1;

        if (left.Score != right.Score)
            return right.Score.CompareTo(left.Score); // higher score first

        if (leftCandidate.Length != rightCandidate.Length)
            return leftCandidate.Length.CompareTo(rightCandidate.Length); // shorter first

        return leftCandidate.CompareTo(rightCandidate, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="index"/> starts a "word" under the matcher's rules: the first
    /// cluster of the text, the first cluster after a <c>_ - . space / \</c> separator, a camel
    /// hump (<c>aB</c> or the <c>B</c> of <c>ABc</c>), or a digit↔letter transition. Exposed
    /// because a completion UI often wants to segment the same way the ranking does (for example
    /// to decide what a Tab-completion should insert).
    /// </summary>
    /// <param name="text">The text to inspect.</param>
    /// <param name="index">A UTF-16 offset into <paramref name="text"/>; an offset in the middle of a grapheme cluster is never a boundary.</param>
    public static bool IsWordBoundary(ReadOnlySpan<char> text, int index)
    {
        if ((uint) index >= (uint) text.Length)
            return false;

        if (index == 0)
            return true;

        // Walk clusters keeping a three-wide window (previous, current, next) — the acronym-tail
        // rule needs the follower, so the decision for a cluster can only be made once the one
        // after it has been read.
        var previousClass = UnitClass.Other;
        var currentClass = UnitClass.Other;
        var currentStart = -1;

        var enumerator = text.GetGraphemeEnumerator();

        while (enumerator.MoveNext())
        {
            var start = enumerator.ElementIndex;
            var next = ClassOf(enumerator.GetCurrentGrapheme());

            if (currentStart == index)
                return BoundaryBonus(false, previousClass, currentClass, next) > 0;

            if (start > index)
                return false; // the index fell inside a cluster

            previousClass = currentClass;
            currentClass = next;
            currentStart = start;
        }

        return currentStart == index && BoundaryBonus(false, previousClass, currentClass, UnitClass.Other) > 0;
    }

    // ───────────────────────────── scoring ─────────────────────────────

    /// <summary>
    /// The single, authoritative definition of a match's detail score, given the cluster index each
    /// pattern cluster landed on. Both alignment strategies (the dynamic program and the greedy
    /// fallback) are scored through here, so the two can never drift apart, and the dynamic
    /// program's recurrence is written to maximise exactly this sum.
    /// </summary>
    private static int ScoreAlignment(in MatchContext context, ReadOnlySpan<int> positions)
    {
        var score = 0;

        for (var i = 0; i < positions.Length; i++)
        {
            var j = positions[i];

            if (i == 0)
                score += context.Bonus[j] + Math.Max(LeadingGapPerUnit * j, MaxLeadingGap);
            else if (j == positions[i - 1] + 1)
                score += Math.Max(BonusConsecutive, context.Bonus[j]);
            else
                score += context.Bonus[j] + InnerGapPerUnit * (j - positions[i - 1] - 1);

            score += context.CaseBonus(i, j);
        }

        return score;
    }

    /// <summary>
    /// The exact best-alignment search. <c>D[i][j]</c> is the best score of an alignment of the
    /// first <c>i+1</c> pattern clusters whose last cluster sits at candidate cluster <c>j</c>:
    /// <code>
    /// D[i][j] = caseBonus + max( D[i-1][j-1] + max(consecutive, bonus[j])              // continue a run
    ///                          , bonus[j] + max over k ≤ j-2 of D[i-1][k] + gap*(j-1-k) ) // start a new run
    /// </code>
    /// The inner "max over k" would make this cubic, so it is carried in a companion running
    /// maximum <c>G[i][j] = max(D[i][j], G[i][j-1] + gap)</c> — legal precisely because the inner
    /// gap penalty is linear — with a parallel <c>argmax</c> row so the traceback knows which
    /// <c>k</c> won. Only the previous row of each is kept; the traceback instead reads a
    /// predecessor matrix of one int per cell.
    /// </summary>
    private static void RunDynamicProgram(
        in MatchContext context,
        ReadOnlySpan<int> bonus,
        Span<int> dPrevious,
        Span<int> dCurrent,
        Span<int> gPreviousValue,
        Span<int> gPreviousIndex,
        Span<int> gCurrentValue,
        Span<int> gCurrentIndex,
        Span<int> predecessors,
        Span<int> positions)
    {
        var patternUnits = context.PatternUnitCount;
        var candidateUnits = context.CandidateUnitCount;

        // Row 0: no predecessor, so the only term is the boundary bonus less the (capped) cost of
        // everything skipped ahead of the match.
        for (var j = 0; j < candidateUnits; j++)
        {
            var d = context.UnitMatches(0, j)
                ? bonus[j] + Math.Max(LeadingGapPerUnit * j, MaxLeadingGap) + context.CaseBonus(0, j)
                : NegativeInfinity;

            dCurrent[j] = d;
            UpdateRunningMaximum(gCurrentValue, gCurrentIndex, j, d);
        }

        for (var i = 1; i < patternUnits; i++)
        {
            Swap(ref dPrevious, ref dCurrent);
            Swap(ref gPreviousValue, ref gCurrentValue);
            Swap(ref gPreviousIndex, ref gCurrentIndex);

            var rowBase = i * candidateUnits;

            for (var j = 0; j < candidateUnits; j++)
            {
                var d = NegativeInfinity;

                // j >= i because positions strictly increase: the first i clusters need i slots
                // ahead of j, which also makes the j-1 index below safe.
                if (j >= i && context.UnitMatches(i, j))
                {
                    var best = NegativeInfinity;
                    var bestPredecessor = -1;

                    if (dPrevious[j - 1] != NegativeInfinity)
                    {
                        best = dPrevious[j - 1] + Math.Max(BonusConsecutive, bonus[j]);
                        bestPredecessor = j - 1;
                    }

                    if (j >= 2 && gPreviousValue[j - 2] != NegativeInfinity)
                    {
                        var gapped = bonus[j] + gPreviousValue[j - 2] + InnerGapPerUnit;

                        if (gapped > best)
                        {
                            best = gapped;
                            bestPredecessor = gPreviousIndex[j - 2];
                        }
                    }

                    if (best != NegativeInfinity)
                    {
                        d = best + context.CaseBonus(i, j);
                        predecessors[rowBase + j] = bestPredecessor;
                    }
                }

                dCurrent[j] = d;
                UpdateRunningMaximum(gCurrentValue, gCurrentIndex, j, d);
            }
        }

        // The winning end position is the best cell of the last row; scan ascending and keep the
        // strict maximum so ties resolve to the earliest end — the stable, least-surprising
        // highlight.
        var bestScore = NegativeInfinity;
        var bestEnd = -1;

        for (var j = patternUnits - 1; j < candidateUnits; j++)
        {
            if (dCurrent[j] > bestScore)
            {
                bestScore = dCurrent[j];
                bestEnd = j;
            }
        }

        Debug.Assert(bestEnd >= 0, "the subsequence pre-filter guarantees at least one alignment");

        positions[patternUnits - 1] = bestEnd;

        for (var i = patternUnits - 1; i >= 1; i--)
            positions[i - 1] = predecessors[i * candidateUnits + positions[i]];
    }

    /// <summary>
    /// Extends the running maximum <c>G[j] = max(D[j], G[j-1] + gap)</c> and its <c>argmax</c> by
    /// one column. Ties keep the later column, which costs nothing (the scores are equal) and keeps
    /// the recurrence branch-light.
    /// </summary>
    private static void UpdateRunningMaximum(Span<int> values, Span<int> indices, int j, int d)
    {
        if (j == 0)
        {
            values[0] = d;
            indices[0] = 0;
            return;
        }

        var carried = values[j - 1] == NegativeInfinity ? NegativeInfinity : values[j - 1] + InnerGapPerUnit;

        if (d >= carried)
        {
            values[j] = d;
            indices[j] = j;
        }
        else
        {
            values[j] = carried;
            indices[j] = indices[j - 1];
        }
    }

    // ───────────────────────────── alignment helpers ─────────────────────────────

    /// <summary>
    /// The scratch-free subsequence gate: does <paramref name="pattern"/> occur in
    /// <paramref name="candidate"/> in order at all? Runs before a single int of scratch is
    /// touched, because this is the answer for nearly every candidate on nearly every keystroke.
    /// <para>
    /// The ASCII branch compares code units directly — for ASCII a grapheme cluster is a character,
    /// so the cluster tables would be pure overhead. The general branch walks both strings with
    /// grapheme enumerators so a cluster only ever matches a whole cluster.
    /// </para>
    /// </summary>
    private static bool IsSubsequence(ReadOnlySpan<char> pattern, ReadOnlySpan<char> candidate, bool asciiOnly)
    {
        if (asciiOnly)
        {
            var j = 0;

            foreach (var p in pattern)
            {
                while (j < candidate.Length && !AsciiOrExactEquals(p, candidate[j]))
                    j++;

                if (j == candidate.Length)
                    return false;

                j++;
            }

            return true;
        }

        var patternClusters = pattern.GetGraphemeEnumerator();

        if (!patternClusters.MoveNext())
            return true;

        var wanted = patternClusters.GetCurrentGrapheme();
        var candidateClusters = candidate.GetGraphemeEnumerator();

        while (candidateClusters.MoveNext())
        {
            if (!UnitEqualsIgnoreCase(wanted, candidateClusters.GetCurrentGrapheme()))
                continue;

            if (!patternClusters.MoveNext())
                return true;

            wanted = patternClusters.GetCurrentGrapheme();
        }

        return false;
    }

    /// <summary>
    /// Leftmost greedy alignment — the starting point for the over-budget fallback, and the
    /// cluster-index form of what <see cref="IsSubsequence"/> already proved. Returns
    /// <see langword="false"/> if no alignment exists.
    /// </summary>
    private static bool GreedyForward(in MatchContext context, Span<int> positions)
    {
        var j = 0;

        for (var i = 0; i < context.PatternUnitCount; i++)
        {
            while (j < context.CandidateUnitCount && !context.UnitMatches(i, j))
                j++;

            if (j == context.CandidateUnitCount)
                return false;

            positions[i] = j++;
        }

        return true;
    }

    /// <summary>
    /// Rightmost-compact greedy alignment, walking the pattern backwards. Only used by the
    /// over-budget fallback, and only after <see cref="GreedyForward"/> proved an alignment exists.
    /// </summary>
    private static void GreedyBackward(in MatchContext context, Span<int> positions)
    {
        var j = context.CandidateUnitCount - 1;

        for (var i = context.PatternUnitCount - 1; i >= 0; i--)
        {
            while (j >= 0 && !context.UnitMatches(i, j))
                j--;

            Debug.Assert(j >= 0, "a forward alignment exists, so a backward one must too");
            positions[i] = j--;
        }
    }

    // ───────────────────────────── result assembly ─────────────────────────────

    /// <summary>
    /// Turns a winning alignment into the public verdict: classifies the tier, folds tier and
    /// detail into the score, and emits the matched runs (coalescing consecutive clusters) as
    /// UTF-16 <see cref="MatchSpan"/>s.
    /// </summary>
    private static FuzzyMatchResult Finish(in MatchContext context, ReadOnlySpan<int> positions, int detail, Span<MatchSpan> spans)
    {
        var patternUnits = positions.Length;

        var everyClusterOnBoundary = context.Bonus[positions[0]] > 0;
        var everyRunStartOnBoundary = everyClusterOnBoundary;
        var runCount = 1;

        for (var i = 1; i < patternUnits; i++)
        {
            var onBoundary = context.Bonus[positions[i]] > 0;

            if (positions[i] != positions[i - 1] + 1)
            {
                runCount++;
                everyRunStartOnBoundary &= onBoundary;
            }

            everyClusterOnBoundary &= onBoundary;
        }

        var contiguous = runCount == 1;
        var anchored = positions[0] == 0;

        var kind = contiguous && anchored
                       ? patternUnits == context.CandidateUnitCount ? FuzzyMatchKind.Exact : FuzzyMatchKind.Prefix
                   : everyClusterOnBoundary ? FuzzyMatchKind.Initials
                   : everyRunStartOnBoundary ? FuzzyMatchKind.Boundary
                   : contiguous ? FuzzyMatchKind.Substring
                                : FuzzyMatchKind.Subsequence;

        var written = 0;
        var truncated = false;
        var runStart = positions[0];
        var runEnd = positions[0];

        for (var i = 1; i <= patternUnits; i++)
        {
            if (i < patternUnits && positions[i] == runEnd + 1)
            {
                runEnd = positions[i];
                continue;
            }

            var startChar = context.CandidateStarts[runStart];
            var endChar = context.CandidateStarts[runEnd + 1];

            if (written < spans.Length)
                spans[written++] = new MatchSpan(startChar, endChar - startChar);
            else if (!spans.IsEmpty)
                truncated = true; // an empty destination means "spans not requested", not "truncated"

            if (i < patternUnits)
            {
                runStart = positions[i];
                runEnd = positions[i];
            }
        }

        var score = (int) kind * TierStride + Math.Clamp(DetailBias + detail, 0, TierStride - 1);
        return new FuzzyMatchResult(kind, score, written, truncated);
    }

    // ───────────────────────────── clusters and character classes ─────────────────────────────

    /// <summary>The character classes the word-boundary rules are written against.</summary>
    private enum UnitClass : byte
    {
        /// <summary>One of <c>_ - . space / \</c>.</summary>
        Separator,

        /// <summary>A lowercase letter.</summary>
        Lower,

        /// <summary>An uppercase letter.</summary>
        Upper,

        /// <summary>A decimal digit.</summary>
        Digit,

        /// <summary>Anything else — punctuation, symbols, caseless scripts, emoji.</summary>
        Other,
    }

    /// <summary>Number of grapheme clusters in <paramref name="text"/> (its length, for ASCII).</summary>
    private static int CountUnits(ReadOnlySpan<char> text, bool asciiOnly)
    {
        if (asciiOnly)
            return text.Length;

        var count = 0;
        var enumerator = text.GetGraphemeEnumerator();

        while (enumerator.MoveNext())
            count++;

        return count;
    }

    /// <summary>
    /// Fills the cluster-start table — one UTF-16 offset per cluster plus a trailing sentinel equal
    /// to the text length, so cluster <c>k</c> is <c>text[starts[k]..starts[k + 1]]</c>.
    /// </summary>
    private static void FillUnitStarts(ReadOnlySpan<char> text, bool asciiOnly, Span<int> starts)
    {
        if (asciiOnly)
        {
            for (var i = 0; i < text.Length; i++)
                starts[i] = i;
        }
        else
        {
            var enumerator = text.GetGraphemeEnumerator();
            var k = 0;

            while (enumerator.MoveNext())
                starts[k++] = enumerator.ElementIndex;
        }

        starts[^1] = text.Length;
    }

    /// <summary>
    /// Precomputes the word-boundary bonus of every candidate cluster once per call — the dynamic
    /// program reads it <c>pattern</c> times, and the classification of a cluster costs a Rune
    /// decode.
    /// </summary>
    private static void ComputeBonuses(in MatchContext context, Span<int> bonus)
    {
        var count = bonus.Length;
        var previous = UnitClass.Other;
        var current = ClassOf(context.CandidateUnit(0));

        for (var k = 0; k < count; k++)
        {
            var next = k + 1 < count ? ClassOf(context.CandidateUnit(k + 1)) : UnitClass.Other;

            bonus[k] = BoundaryBonus(k == 0, previous, current, next);

            previous = current;
            current = next;
        }
    }

    /// <summary>
    /// The one place the word-boundary rules live — shared by the scoring tables and by the public
    /// <see cref="IsWordBoundary"/> so the two can never disagree. A non-zero result means "this
    /// cluster starts a word".
    /// </summary>
    private static int BoundaryBonus(bool isFirst, UnitClass previous, UnitClass current, UnitClass next)
    {
        if (isFirst)
            return BonusStart;

        if (previous == UnitClass.Separator && current != UnitClass.Separator)
            return BonusSeparator;

        if (previous == UnitClass.Lower && current == UnitClass.Upper)
            return BonusCamel;

        // The acronym tail: in INotifyPropertyChanged the N starts a word even though the I before
        // it is also uppercase, because the O after it is lowercase. Same rule that makes the P of
        // HTMLParser a hump. Without it, `inpc` cannot be an Initials match.
        if (previous == UnitClass.Upper && current == UnitClass.Upper && next == UnitClass.Lower)
            return BonusCamel;

        if (current != UnitClass.Separator && (previous == UnitClass.Digit) != (current == UnitClass.Digit))
            return BonusDigit;

        return 0;
    }

    /// <summary>Classifies a cluster by its first Unicode scalar (a combining sequence inherits its base character's class).</summary>
    private static UnitClass ClassOf(ReadOnlySpan<char> unit)
    {
        if (unit.Length == 1)
        {
            var c = unit[0];

            if (IsSeparator(c))
                return UnitClass.Separator;

            if (c < 0x80)
            {
                return c switch
                       {
                           >= 'a' and <= 'z' => UnitClass.Lower,
                           >= 'A' and <= 'Z' => UnitClass.Upper,
                           >= '0' and <= '9' => UnitClass.Digit,
                           _                 => UnitClass.Other
                       };
            }
        }

        if (Rune.DecodeFromUtf16(unit, out var rune, out _) != OperationStatus.Done)
            return UnitClass.Other;

        if (Rune.IsDigit(rune))
            return UnitClass.Digit;

        if (Rune.IsUpper(rune))
            return UnitClass.Upper;

        if (Rune.IsLower(rune))
            return UnitClass.Lower;

        return UnitClass.Other;
    }

    /// <summary>
    /// The separator set: <c>_ - . space / \</c>. The backslash is in deliberately — this matcher's
    /// first consumer is a cross-platform file dialog, where a Windows path's segments must break
    /// on <c>\</c> exactly as a POSIX path's break on <c>/</c>.
    /// </summary>
    private static bool IsSeparator(char c) => c is '_' or '-' or '.' or ' ' or '/' or '\\';

    /// <summary>Ordinal case-insensitive cluster equality, with a single-char fast path.</summary>
    private static bool UnitEqualsIgnoreCase(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length == 1 && b.Length == 1)
            return AsciiOrExactEquals(a[0], b[0]);

        return a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Case-insensitive equality for two single characters. The ASCII fold is written out rather
    /// than deferred to <see cref="char.ToUpperInvariant"/> because this is the innermost
    /// comparison of both the subsequence gate and the dynamic program — it runs once per
    /// (pattern cluster × candidate cluster) cell.
    /// <para>
    /// Only <c>'a'</c>–<c>'z'</c> differ from their uppercase form in ASCII, and always by exactly
    /// <c>0x20</c>, so OR-ing in that bit folds the case; the range guard is what stops the fold
    /// from equating non-letters that happen to differ in the same bit (<c>'@'</c>/<c>'`'</c>,
    /// <c>'_'</c>/DEL). Non-ASCII characters fall through to the framework's invariant mapping.
    /// </para>
    /// </summary>
    private static bool AsciiOrExactEquals(char x, char y)
    {
        if (x == y)
            return true;

        var folded = (uint) (x | 0x20);

        if (folded - 'a' <= 'z' - 'a')
            return folded == (uint) (y | 0x20);

        return x >= 0x80 && char.ToUpperInvariant(x) == char.ToUpperInvariant(y);
    }

    // ───────────────────────────── scratch plumbing ─────────────────────────────

    /// <summary>Carves the next <paramref name="length"/> ints out of the single scratch buffer.</summary>
    private static Span<int> Take(Span<int> scratch, ref int offset, int length)
    {
        var slice = scratch.Slice(offset, length);
        offset += length;
        return slice;
    }

    /// <summary>
    /// Swaps two row spans (the DP rolls its rows rather than materialising a matrix). Spelled out
    /// rather than written as a tuple deconstruction because <c>Span&lt;T&gt;</c> is a ref struct
    /// and cannot be a tuple element (CS9244).
    /// </summary>
    private static void Swap(ref Span<int> a, ref Span<int> b)
    {
        var temporary = a;
        a = b;
        b = temporary;
    }

    /// <summary>
    /// Everything the matching passes need about the two strings, bundled so the helpers can stay
    /// small: the raw text, the cluster-start tables, and the precomputed candidate bonuses. A
    /// <c>ref struct</c> because every field is a span into caller memory or into the call's
    /// scratch buffer.
    /// </summary>
    private readonly ref struct MatchContext(
        ReadOnlySpan<char> pattern,
        ReadOnlySpan<char> candidate,
        ReadOnlySpan<int> patternStarts,
        ReadOnlySpan<int> candidateStarts,
        ReadOnlySpan<int> bonus,
        bool asciiOnly)
    {
        /// <summary>
        /// Whether both strings are pure ASCII, in which case cluster index == char index and the
        /// two hot predicates below skip slicing entirely. This is the dynamic program's inner
        /// loop, so the branch pays for itself many times over on the common input.
        /// </summary>
        public bool AsciiOnly { get; } = asciiOnly;

        /// <summary>The pattern text.</summary>
        public ReadOnlySpan<char> Pattern { get; } = pattern;

        /// <summary>The candidate text.</summary>
        public ReadOnlySpan<char> Candidate { get; } = candidate;

        /// <summary>Cluster-start offsets into <see cref="Pattern"/>, with a trailing length sentinel.</summary>
        public ReadOnlySpan<int> PatternStarts { get; } = patternStarts;

        /// <summary>Cluster-start offsets into <see cref="Candidate"/>, with a trailing length sentinel.</summary>
        public ReadOnlySpan<int> CandidateStarts { get; } = candidateStarts;

        /// <summary>Word-boundary bonus of each candidate cluster.</summary>
        public ReadOnlySpan<int> Bonus { get; } = bonus;

        /// <summary>Number of grapheme clusters in the pattern.</summary>
        public int PatternUnitCount => PatternStarts.Length - 1;

        /// <summary>Number of grapheme clusters in the candidate.</summary>
        public int CandidateUnitCount => CandidateStarts.Length - 1;

        /// <summary>Cluster <paramref name="i"/> of the pattern.</summary>
        public ReadOnlySpan<char> PatternUnit(int i) => Pattern[PatternStarts[i]..PatternStarts[i + 1]];

        /// <summary>Cluster <paramref name="j"/> of the candidate.</summary>
        public ReadOnlySpan<char> CandidateUnit(int j) => Candidate[CandidateStarts[j]..CandidateStarts[j + 1]];

        /// <summary>Whether pattern cluster <paramref name="i"/> matches candidate cluster <paramref name="j"/>, ignoring case.</summary>
        public bool UnitMatches(int i, int j)
            => AsciiOnly
                   ? AsciiOrExactEquals(Pattern[i], Candidate[j])
                   : UnitEqualsIgnoreCase(PatternUnit(i), CandidateUnit(j));

        /// <summary>The case tiebreak for pairing pattern cluster <paramref name="i"/> with candidate cluster <paramref name="j"/>.</summary>
        public int CaseBonus(int i, int j)
        {
            var same = AsciiOnly
                           ? Pattern[i] == Candidate[j]
                           : PatternUnit(i).SequenceEqual(CandidateUnit(j));

            return same ? BonusExactCase : 0;
        }
    }
}
