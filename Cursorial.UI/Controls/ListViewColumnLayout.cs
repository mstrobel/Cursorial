namespace Cursorial.UI.Controls;

/// <summary>
/// The shared column-width table for a <see cref="ListView"/> in <see cref="ListViewViewMode.Details"/>:
/// one array of cell widths that the <see cref="ListViewHeaderPresenter"/> and every row's
/// <see cref="ListViewCellsPresenter"/> read, so the header sits exactly over its column.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a two-pass negotiation.</b> An <see cref="GridUnitType.Auto"/> column is as wide as its
/// widest cell, but cells only exist inside rows, and rows only measure AFTER the control does. So a pass
/// works like this: <see cref="BeginPass"/> clears the accumulator; the header and each row measure their
/// content unconstrained and call <see cref="ReportContentWidth"/>; <see cref="ListView.MeasureOverride"/>
/// then calls <see cref="Reconcile"/>, which folds the accumulated maxima into <see cref="Widths"/>. If that
/// changed anything the ListView invalidates its consumers and the next layout pass lays out at the settled
/// widths. It converges in exactly one extra pass and cannot oscillate: the reported widths are measured at
/// an UNBOUNDED constraint, so they do not depend on the widths we hand back.
/// </para>
/// <para>
/// The <b>row</b> width — not the control's — drives the distribution (<see cref="ReportAvailableWidth"/> is
/// called by the cells presenter). That is deliberate: rows live inside the scroll viewport, so when a
/// vertical scroll bar appears the star columns shrink to the narrower row band and the header's trailing
/// residue lands over the scroll bar gutter, which is exactly where a header should stop.
/// </para>
/// </remarks>
internal sealed class ListViewColumnLayout
{
    private int[] _widths = [];
    private int[] _reported = [];
    private int _available = -1;
    private int _pendingAvailable = -1;
    private int _totalWidth;

    /// <summary>Bumped whenever <see cref="Widths"/> actually changes — consumers cache against it.</summary>
    public int Version { get; private set; }

    /// <summary>The settled per-column cell widths (empty before the first <see cref="Reconcile"/>).</summary>
    public IReadOnlyList<int> Widths => _widths;

    /// <summary>The sum of <see cref="Widths"/> plus the inter-column spacing folded in by <see cref="Reconcile"/>.</summary>
    public int TotalWidth => _totalWidth;

    /// <summary>Clears the per-pass accumulator and re-sizes it to <paramref name="columnCount"/>.</summary>
    public void BeginPass(int columnCount)
    {
        if (_reported.Length != columnCount)
            _reported = new int[columnCount];
        else
            Array.Clear(_reported);

        _pendingAvailable = -1;
    }

    /// <summary>A row reports the width it was measured at (the viewport band the columns must fit).</summary>
    public void ReportAvailableWidth(int width)
    {
        if (width > _pendingAvailable)
            _pendingAvailable = width;
    }

    /// <summary>The header or a row reports one cell's unconstrained desired width.</summary>
    public void ReportContentWidth(int index, int width)
    {
        if ((uint) index < (uint) _reported.Length && width > _reported[index])
            _reported[index] = width;
    }

    /// <summary>
    /// The width to lay a cell out at. Before the first <see cref="Reconcile"/> this is the column's own
    /// floor (fixed cells, else <see cref="ListViewColumn.MinWidth"/>) so pass 1 renders something sane
    /// rather than nothing.
    /// </summary>
    public int GetWidth(IReadOnlyList<ListViewColumn> columns, int index)
    {
        if ((uint) index < (uint) _widths.Length)
            return _widths[index];

        if ((uint) index >= (uint) columns.Count)
            return 0;

        var column = columns[index];
        return Math.Max(column.MinWidth, column.Width.IsCell ? column.Width.Cells : 0);
    }

    /// <summary>
    /// Folds this pass's reports into <see cref="Widths"/>. Returns <see langword="true"/> when anything
    /// moved (the caller must then invalidate the header and the rows).
    /// </summary>
    /// <param name="columns">The live column list.</param>
    /// <param name="spacing">The inter-column gap in cells (subtracted from the star budget).</param>
    public bool Reconcile(IReadOnlyList<ListViewColumn> columns, int spacing)
    {
        var count = columns.Count;
        var widths = new int[count];

        if (_pendingAvailable >= 0)
            _available = _pendingAvailable;

        var available = _available < 0 ? 0 : _available;

        // ① fixed + auto tracks take their intrinsic width; star tracks are parked at their floor.
        var consumed = count > 1 ? (count - 1) * spacing : 0;
        var starWeight = 0.0;

        for (var i = 0; i < count; i++)
        {
            var column = columns[i];
            var length = column.Width;
            var floor = Math.Max(0, column.MinWidth);

            if (length.IsStar)
            {
                widths[i] = floor;
                starWeight += double.IsFinite(length.StarWeight) && length.StarWeight > 0 ? length.StarWeight : 0;
            }
            else if (length.IsCell)
            {
                widths[i] = Math.Max(floor, length.Cells);
            }
            else
            {
                widths[i] = Math.Max(floor, i < _reported.Length ? _reported[i] : 0);
            }

            consumed += widths[i];
        }

        // ② the star budget is whatever the row band has left; distributed by largest-remainder so the
        //    integer cells sum EXACTLY to the budget (the Grid's pinned Hamilton distribution, §5.4).
        if (starWeight > 0 && available > consumed)
            DistributeStars(columns, widths, available - consumed, starWeight);

        var total = 0;
        for (var i = 0; i < count; i++)
            total += widths[i];
        total += count > 1 ? (count - 1) * spacing : 0;

        var changed = _widths.Length != widths.Length || !_widths.AsSpan().SequenceEqual(widths);
        _widths = widths;
        _totalWidth = total;

        if (changed)
            Version++;

        return changed;
    }

    private static void DistributeStars(IReadOnlyList<ListViewColumn> columns, int[] widths, int budget, double totalWeight)
    {
        var remainders = new (int Index, double Fraction)[columns.Count];
        var assigned = 0;
        var candidates = 0;

        for (var i = 0; i < columns.Count; i++)
        {
            var length = columns[i].Width;
            if (!length.IsStar)
                continue;

            var weight = double.IsFinite(length.StarWeight) && length.StarWeight > 0 ? length.StarWeight : 0;
            var exact = budget * (weight / totalWeight);
            var whole = (int) exact;

            widths[i] += whole;
            assigned += whole;
            remainders[candidates++] = (i, exact - whole);
        }

        // The leftover cells go to the largest fractional parts, ties broken by declaration order.
        Array.Sort(remainders, 0, candidates, RemainderComparer.Instance);

        for (var i = 0; i < budget - assigned && i < candidates; i++)
            widths[remainders[i].Index]++;
    }

    private sealed class RemainderComparer : IComparer<(int Index, double Fraction)>
    {
        public static readonly RemainderComparer Instance = new();

        public int Compare((int Index, double Fraction) x, (int Index, double Fraction) y)
        {
            var byFraction = y.Fraction.CompareTo(x.Fraction);
            return byFraction != 0 ? byFraction : x.Index.CompareTo(y.Index);
        }
    }
}
