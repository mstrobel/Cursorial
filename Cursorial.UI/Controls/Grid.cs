using Cursorial.Rendering;

namespace Cursorial.UI.Controls;

/// <summary>
/// A track-based layout grid (design doc §5.4): children are placed via the
/// <see cref="RowProperty"/>/<see cref="ColumnProperty"/> (and span) attached properties into
/// <see cref="RowDefinitions"/>/<see cref="ColumnDefinitions"/> tracks sized by
/// <see cref="GridLength"/> — fixed cells, Auto (content), or Star (a weighted share of the
/// remainder).
/// <para>
/// <b>Pinned star policy (integer Hamilton / largest remainder):</b> with
/// <c>R = max(0, available − fixed − auto)</c>, each unclamped star's ideal share is
/// <c>R·wᵢ/Σw</c>; floors are assigned first and the leftover cells go one each to the largest
/// fractional parts, ties to the lowest definition index. A definition Min/Max clamp violation
/// pins that track and re-runs the distribution over the remaining unclamped stars to a fixpoint
/// (bounded by the definition count). A star track under an unbounded constraint behaves as Auto
/// end-to-end (WPF rule); under a bounded constraint it contributes
/// <c>min(resolvedStarSize, max non-spanning child desired)</c> to the grid's own desired size
/// (LD8). An axis with no definitions behaves as a single implicit <c>1*</c> track (LD9).
/// </para>
/// <para>
/// Placement is clamped at layout time into the definition range, spans to the remaining
/// definitions (LD9). A child spanning Auto tracks spreads any content deficit evenly across the
/// spanned Auto tracks, remainder to the rightmost (doc §5.4 v1 simplification); spans that cover
/// a bounded star track never inflate it — the star flexes and overflow is the zone clip's job
/// (L157).
/// </para>
/// </summary>
public class Grid : Panel
{
    /// <summary>
    /// The child's row index (default 0; registration coercion pins values ≥ 0, layout additionally
    /// clamps into the definition range — LD9). <c>[AffectsParentMeasure]</c> — carried on the
    /// property-global effects lane so the flag fires on host types whose frozen per-type tables
    /// never saw this registration (doc §5.5).
    /// </summary>
    public static readonly AttachedProperty<int> RowProperty =
        UIProperty.RegisterAttached<Grid, UIElement, int>("Row", coerce: static (_, value) => Math.Max(0, value), targetsChildren: true);

    /// <summary>The child's column index (default 0; coerced ≥ 0). <c>[AffectsParentMeasure]</c> via the global lane.</summary>
    public static readonly AttachedProperty<int> ColumnProperty =
        UIProperty.RegisterAttached<Grid, UIElement, int>("Column", coerce: static (_, value) => Math.Max(0, value), targetsChildren: true);

    /// <summary>The child's row span (default 1; coerced ≥ 1, layout clamps to the remaining definitions — LD9). <c>[AffectsParentMeasure]</c></summary>
    public static readonly AttachedProperty<int> RowSpanProperty =
        UIProperty.RegisterAttached<Grid, UIElement, int>("RowSpan", defaultValue: 1, coerce: static (_, value) => Math.Max(1, value), targetsChildren: true);

    /// <summary>The child's column span (default 1; coerced ≥ 1). <c>[AffectsParentMeasure]</c></summary>
    public static readonly AttachedProperty<int> ColumnSpanProperty =
        UIProperty.RegisterAttached<Grid, UIElement, int>("ColumnSpan", defaultValue: 1, coerce: static (_, value) => Math.Max(1, value), targetsChildren: true);

    static Grid()
    {
        AffectsParentMeasure<Grid>(RowProperty, ColumnProperty, RowSpanProperty, ColumnSpanProperty);
    }

    /// <summary>Creates the grid and its owner-wired definition collections.</summary>
    public Grid()
    {
        ColumnDefinitions = new ColumnDefinitionCollection(this);
        RowDefinitions = new RowDefinitionCollection(this);
    }

    /// <summary>The column tracks; mutation (and owned-definition property writes) invalidates measure live (L159).</summary>
    public ColumnDefinitionCollection ColumnDefinitions { get; }

    /// <summary>The row tracks; mutation (and owned-definition property writes) invalidates measure live (L159).</summary>
    public RowDefinitionCollection RowDefinitions { get; }

    /// <summary>Reads <see cref="RowProperty"/>.</summary>
    public static int GetRow(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(RowProperty);
    }

    /// <summary>Writes <see cref="RowProperty"/>.</summary>
    public static void SetRow(UIElement element, int value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(RowProperty, value);
    }

    /// <summary>Reads <see cref="ColumnProperty"/>.</summary>
    public static int GetColumn(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(ColumnProperty);
    }

    /// <summary>Writes <see cref="ColumnProperty"/>.</summary>
    public static void SetColumn(UIElement element, int value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ColumnProperty, value);
    }

    /// <summary>Reads <see cref="RowSpanProperty"/>.</summary>
    public static int GetRowSpan(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(RowSpanProperty);
    }

    /// <summary>Writes <see cref="RowSpanProperty"/>.</summary>
    public static void SetRowSpan(UIElement element, int value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(RowSpanProperty, value);
    }

    /// <summary>Reads <see cref="ColumnSpanProperty"/>.</summary>
    public static int GetColumnSpan(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(ColumnSpanProperty);
    }

    /// <summary>Writes <see cref="ColumnSpanProperty"/>.</summary>
    public static void SetColumnSpan(UIElement element, int value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ColumnSpanProperty, value);
    }

    // ───────────────────────────── track resolution scratch ─────────────────────────────
    //
    // Working state is cached in arrays that only grow (no steady-state allocation on the layout
    // path); measure and arrange both resolve from scratch off the same inputs (definitions +
    // child desired sizes), so they need no shared hidden state and stay deterministic.

    private TrackScratch[] _columnScratch = [];
    private TrackScratch[] _rowScratch = [];
    private Placement[] _placements = [];

    private struct TrackScratch
    {
        public GridUnitType Unit;
        public double Weight;
        public int Min;
        public int Max;
        public int Size;     // resolved track size (fixed clamp / content / star share)
        public int Content;  // max non-spanning child desired on the axis (Auto sizing + LD8 star contribution)
        public int Offset;   // arrange-time prefix sum
        public double Fraction;
        public bool Resolved;
        public bool Bumped;
    }

    private readonly record struct Placement(int Column, int ColumnSpan, int Row, int RowSpan);

    // ───────────────────────────── measure ─────────────────────────────

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var columnCount = InitializeAxis(ref _columnScratch, ColumnDefinitions);
        var rowCount = InitializeAxis(ref _rowScratch, RowDefinitions);
        ComputePlacements(columnCount, rowCount);

        var children = Children;
        var columnsBounded = !LayoutMath.IsUnbounded(availableSize.Columns);
        var rowsBounded = !LayoutMath.IsUnbounded(availableSize.Rows);

        // 1. Interim measure: Cell tracks contribute their clamped size; Auto and not-yet-resolved
        //    Star tracks contribute Unbounded (content-driven axes measure with U — L150).
        for (var i = 0; i < children.Count; i++)
        {
            var placement = _placements[i];
            children[i].Measure(new Size(
                InterimConstraint(_columnScratch, placement.Column, placement.ColumnSpan),
                InterimConstraint(_rowScratch, placement.Row, placement.RowSpan)));
        }

        // 2. Content sizes: Auto always; Star when the axis is unbounded (star-as-Auto — L151).
        ResolveContentSizes(_columnScratch, columnCount, columnsBounded, columns: true);
        ResolveContentSizes(_rowScratch, rowCount, rowsBounded, columns: false);

        // 3. Stars on bounded axes: Hamilton distribution + clamp fixpoint.
        if (columnsBounded)
            ResolveStars(_columnScratch, columnCount, availableSize.Columns);
        if (rowsBounded)
            ResolveStars(_rowScratch, rowCount, availableSize.Rows);

        // 4. Children spanning bounded star tracks now have a finite constraint — re-measure them
        //    with it (a same-constraint call is a cache hit, so only star-spanning children re-run).
        for (var i = 0; i < children.Count; i++)
        {
            var placement = _placements[i];
            children[i].Measure(new Size(
                FinalConstraint(_columnScratch, placement.Column, placement.ColumnSpan, columnsBounded),
                FinalConstraint(_rowScratch, placement.Row, placement.RowSpan, rowsBounded)));
        }

        // 4b. The step-4 re-measure can REFLOW content: a wrapping child measured against an
        //     interim Unbounded axis reported one long line, and at the final bounded star width
        //     it wraps TALLER — changing not just its star track's content (LD8's min(resolved,
        //     content) would read the stale step-2 value) but ALSO any AUTO track on the CROSS
        //     axis (a bounded star column re-wraps text, growing the Auto row hosting it — the
        //     wizard's key-binding grid). Reset every non-Cell track's content and re-resolve
        //     from the post-step-4 desired sizes — the exact fresh accounting ArrangeOverride
        //     performs, so measure desired and arrange outcome cannot disagree (L152a; the
        //     fit-to-content clipping bug, observed live in the first-run wizard).
        ResetContentAccounting(_columnScratch, columnCount);
        ResetContentAccounting(_rowScratch, rowCount);
        ResolveContentSizes(_columnScratch, columnCount, columnsBounded, columns: true);
        ResolveContentSizes(_rowScratch, rowCount, rowsBounded, columns: false);

        return new Size(
            ComputeDesiredAxis(_columnScratch, columnCount, columnsBounded),
            ComputeDesiredAxis(_rowScratch, rowCount, rowsBounded));
    }

    /// <summary>Step 4b (L152a): zeroes the non-Cell tracks' content so the re-resolution
    /// accumulates fresh maxima from the final-constraint desired sizes (Content only ever
    /// Max-accumulates; without the reset, stale step-2 maxima would survive).</summary>
    private static void ResetContentAccounting(TrackScratch[] axis, int count)
    {
        for (var i = 0; i < count; i++)
        {
            ref var track = ref axis[i];

            if (track.Unit != GridUnitType.Cell)
                track.Content = 0;
        }
    }

    // ───────────────────────────── arrange ─────────────────────────────

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        // Re-resolve at the final size (always bounded): fixed clamps, content sizes from the
        // children's current desired sizes (identical to the measure-time values), stars over the
        // final extent — deterministic with no measure-time carry-over.
        var columnCount = InitializeAxis(ref _columnScratch, ColumnDefinitions);
        var rowCount = InitializeAxis(ref _rowScratch, RowDefinitions);
        ComputePlacements(columnCount, rowCount);

        ResolveContentSizes(_columnScratch, columnCount, bounded: true, columns: true);
        ResolveContentSizes(_rowScratch, rowCount, bounded: true, columns: false);
        ResolveStars(_columnScratch, columnCount, finalSize.Columns);
        ResolveStars(_rowScratch, rowCount, finalSize.Rows);

        ComputeOffsets(_columnScratch, columnCount);
        ComputeOffsets(_rowScratch, rowCount);

        var children = Children;
        for (var i = 0; i < children.Count; i++)
        {
            var placement = _placements[i];
            var column = _columnScratch[placement.Column].Offset;
            var row = _rowScratch[placement.Row].Offset;
            children[i].Arrange(new Rect(
                column, row,
                SpanSize(_columnScratch, placement.Column, placement.ColumnSpan, column),
                SpanSize(_rowScratch, placement.Row, placement.RowSpan, row)));
        }

        // Post-layout readbacks (L132/L159 ③). Implicit tracks have no definition objects.
        var columnDefinitions = ColumnDefinitions;
        for (var i = 0; i < columnDefinitions.Count; i++)
            columnDefinitions[i].ActualSize = _columnScratch[i].Size;
        var rowDefinitions = RowDefinitions;
        for (var i = 0; i < rowDefinitions.Count; i++)
            rowDefinitions[i].ActualSize = _rowScratch[i].Size;

        return finalSize;
    }

    // ───────────────────────────── axis resolution ─────────────────────────────

    private static int InitializeAxis<T>(ref TrackScratch[] scratch, GridDefinitionCollection<T> definitions)
        where T : GridDefinition
    {
        var count = definitions.Count;
        if (count == 0)
        {
            // An axis with no definitions behaves as one implicit 1* track (LD9).
            EnsureCapacity(ref scratch, 1);
            scratch[0] = new TrackScratch { Unit = GridUnitType.Star, Weight = 1.0, Max = LayoutMath.Unbounded };
            return 1;
        }

        EnsureCapacity(ref scratch, count);
        for (var i = 0; i < count; i++)
        {
            var definition = definitions[i];
            var length = definition.Length;
            ref var track = ref scratch[i];
            track = default;
            track.Unit = length.UnitType;
            track.Min = definition.MinSize;
            track.Max = definition.MaxSize;
            if (length.UnitType == GridUnitType.Cell)
                track.Size = LayoutMath.Clamp(Math.Max(0, length.Cells), track.Min, track.Max);
            else if (length.UnitType == GridUnitType.Star)
                track.Weight = double.IsFinite(length.StarWeight) && length.StarWeight > 0 ? length.StarWeight : 0;
        }

        return count;
    }

    private void ComputePlacements(int columnCount, int rowCount)
    {
        var children = Children;
        EnsureCapacity(ref _placements, children.Count);
        for (var i = 0; i < children.Count; i++)
        {
            // Registration coercion pins Row/Column ≥ 0, spans ≥ 1; layout additionally clamps the
            // placement into the definition range and spans to the remaining definitions (LD9).
            var child = children[i];
            var column = Math.Min(GetColumn(child), columnCount - 1);
            var row = Math.Min(GetRow(child), rowCount - 1);
            _placements[i] = new Placement(
                column, Math.Min(GetColumnSpan(child), columnCount - column),
                row, Math.Min(GetRowSpan(child), rowCount - row));
        }
    }

    // Both constraint builders coerce each track's contribution to the definition's Min/Max
    // (LD1 "constraint coercion" — a child in a MaxWidth-bounded Auto/Star track measures against
    // that cap, not Unbounded). One `LayoutMath.Clamp(value, Min, Max)` per track, matching every
    // other clamp site in this file (InitializeAxis / ResolveContentSizes / ResolveStars): Min is
    // applied last, so **min wins a min>max conflict** (LD18). Defaults are no-ops — Min is 0
    // (clamp floor) and Max is Unbounded (clamp ceiling), so an unconstrained Auto/Star track still
    // contributes Unbounded and a Cell track its already-clamped size.

    private static int InterimConstraint(TrackScratch[] axis, int start, int span)
    {
        var sum = 0;
        for (var i = start; i < start + span; i++)
        {
            ref var track = ref axis[i];
            var constraint = track.Unit == GridUnitType.Cell ? track.Size : LayoutMath.Unbounded;
            sum = LayoutMath.Add(sum, LayoutMath.Clamp(constraint, track.Min, track.Max));
        }

        return sum;
    }

    private static int FinalConstraint(TrackScratch[] axis, int start, int span, bool bounded)
    {
        var sum = 0;
        for (var i = start; i < start + span; i++)
        {
            ref var track = ref axis[i];
            var constraint = track.Unit switch
                             {
                                 GridUnitType.Cell              => track.Size,
                                 GridUnitType.Star when bounded => track.Size,
                                 _                              => LayoutMath.Unbounded // Auto stays content-driven (L150); star-as-Auto when unbounded
                             };
            sum = LayoutMath.Add(sum, LayoutMath.Clamp(constraint, track.Min, track.Max));
        }

        return sum;
    }

    /// <summary>
    /// Resolves content-sized tracks (Auto always; Star when the axis is unbounded): the max
    /// non-spanning child desired, clamped to the track Min/Max, then the spanning-Auto deficit
    /// spread — evenly across the spanned content-sized tracks, remainder to the rightmost
    /// (doc §5.4; L156). Star <see cref="TrackScratch.Content"/> is accumulated on bounded axes too
    /// for the LD8 desired-size contribution, but bounded stars are never inflated by spans (L157).
    /// </summary>
    private void ResolveContentSizes(TrackScratch[] axis, int count, bool bounded, bool columns)
    {
        var children = Children;

        // Non-spanning children: per-track content max.
        for (var i = 0; i < children.Count; i++)
        {
            var placement = _placements[i];
            var (start, span) = columns
                ? (placement.Column, placement.ColumnSpan)
                : (placement.Row, placement.RowSpan);
            if (span != 1)
                continue;

            ref var track = ref axis[start];
            if (track.Unit == GridUnitType.Cell)
                continue;

            var desired = children[i].DesiredSize;
            track.Content = Math.Max(track.Content, columns ? desired.Columns : desired.Rows);
        }

        for (var i = 0; i < count; i++)
        {
            ref var track = ref axis[i];
            if (track.Unit == GridUnitType.Auto || (track.Unit == GridUnitType.Star && !bounded))
                track.Size = LayoutMath.Clamp(track.Content, track.Min, track.Max);
        }

        // Spanning children (document order): deficit spread over spanned content-sized tracks.
        for (var i = 0; i < children.Count; i++)
        {
            var placement = _placements[i];
            var (start, span) = columns
                ? (placement.Column, placement.ColumnSpan)
                : (placement.Row, placement.RowSpan);
            if (span <= 1)
                continue;

            var targets = 0;
            var allocated = 0;
            var hasBoundedStar = false;
            for (var d = start; d < start + span; d++)
            {
                ref var track = ref axis[d];
                if (track.Unit == GridUnitType.Star && bounded)
                {
                    hasBoundedStar = true;
                    break;
                }

                allocated = LayoutMath.Add(allocated, track.Size);
                if (track.Unit != GridUnitType.Cell)
                    targets++;
            }

            // A bounded star inside the span flexes on its own — spanning content never inflates
            // star tracks (L157).
            if (hasBoundedStar || targets == 0)
                continue;

            var desired = children[i].DesiredSize;
            var deficit = (columns ? desired.Columns : desired.Rows) - allocated;
            if (deficit <= 0)
                continue;

            var share = deficit / targets;
            var remainder = deficit - (share * targets);
            var seen = 0;
            for (var d = start; d < start + span; d++)
            {
                ref var track = ref axis[d];
                if (track.Unit == GridUnitType.Cell)
                    continue;

                seen++;
                var grown = track.Size + share + (seen == targets ? remainder : 0);
                track.Size = LayoutMath.Clamp(grown, track.Min, track.Max);
            }
        }
    }

    /// <summary>
    /// The pinned integer star distribution: Hamilton (largest remainder, ties to the lowest index)
    /// over <c>R = max(0, available − fixed − auto)</c>, with Min/Max clamp violations pinned and
    /// the distribution re-run over the remaining unclamped stars to a fixpoint (doc §5.4).
    /// </summary>
    private static void ResolveStars(TrackScratch[] axis, int count, int available)
    {
        var remaining = available;
        var unresolved = 0;
        for (var i = 0; i < count; i++)
        {
            ref var track = ref axis[i];
            if (track.Unit == GridUnitType.Star)
            {
                track.Resolved = false;
                track.Size = 0;
                unresolved++;
            }
            else
            {
                remaining -= track.Size;
            }
        }

        if (unresolved == 0)
            return;

        while (unresolved > 0)
        {
            remaining = Math.Max(0, remaining);

            var totalWeight = 0.0;
            for (var i = 0; i < count; i++)
            {
                ref var track = ref axis[i];
                if (track.Unit == GridUnitType.Star && !track.Resolved)
                    totalWeight += track.Weight;
            }

            // Floor the ideal shares; leftover cells go to the largest fractional parts, ties to
            // the lowest definition index. All-zero weights distribute nothing (L139).
            var assigned = 0;
            for (var i = 0; i < count; i++)
            {
                ref var track = ref axis[i];
                if (track.Unit != GridUnitType.Star || track.Resolved)
                    continue;

                var ideal = totalWeight > 0 ? remaining * track.Weight / totalWeight : 0.0;
                var floor = (int)Math.Floor(ideal);
                track.Size = floor;
                track.Fraction = ideal - floor;
                track.Bumped = false;
                assigned += floor;
            }

            var leftover = totalWeight > 0 ? remaining - assigned : 0;
            while (leftover > 0)
            {
                var best = -1;
                for (var i = 0; i < count; i++)
                {
                    ref var track = ref axis[i];
                    if (track.Unit != GridUnitType.Star || track.Resolved || track.Bumped)
                        continue;
                    if (best < 0 || track.Fraction > axis[best].Fraction)
                        best = i;
                }

                if (best < 0)
                    break;

                axis[best].Size++;
                axis[best].Bumped = true;
                leftover--;
            }

            // Clamp check: pin every violated star at its clamp and re-run over the rest.
            var pinned = 0;
            for (var i = 0; i < count; i++)
            {
                ref var track = ref axis[i];
                if (track.Unit != GridUnitType.Star || track.Resolved)
                    continue;

                var clamped = LayoutMath.Clamp(track.Size, track.Min, track.Max);
                if (clamped == track.Size)
                    continue;

                track.Size = clamped;
                track.Resolved = true;
                remaining -= clamped;
                pinned++;
            }

            if (pinned == 0)
                break;

            unresolved -= pinned;
        }
    }

    private static int ComputeDesiredAxis(TrackScratch[] axis, int count, bool bounded)
    {
        var desired = 0;
        for (var i = 0; i < count; i++)
        {
            ref var track = ref axis[i];
            // Under a bounded constraint a star contributes min(resolved, content), clamped — its
            // content desire, not its allocation (LD8). Everything else contributes its size.
            var contribution = track.Unit == GridUnitType.Star && bounded
                ? LayoutMath.Clamp(Math.Min(track.Size, track.Content), track.Min, track.Max)
                : track.Size;
            desired = LayoutMath.Add(desired, contribution);
        }

        return desired;
    }

    private static void ComputeOffsets(TrackScratch[] axis, int count)
    {
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            axis[i].Offset = offset;
            offset = Math.Min(offset + axis[i].Size, LayoutMath.MaxExtent);
        }
    }

    private static int SpanSize(TrackScratch[] axis, int start, int span, int offset)
    {
        var size = 0;
        for (var i = start; i < start + span; i++)
            size += axis[i].Size;
        return Math.Min(size, LayoutMath.MaxExtent - offset);
    }

    private static void EnsureCapacity<T>(ref T[] array, int count)
    {
        if (array.Length < count)
            array = new T[Math.Max(count, array.Length * 2)];
    }
}
