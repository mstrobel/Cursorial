namespace Cursorial.UI.DataViews;

/// <summary>
/// The resolved horizontal geometry (design doc §1 — the column model): per visible column, its
/// content x-offset and width in cells. Computed by the rows presenter's measure (it owns the band
/// cache Auto sizing reads) and consumed by every band presenter (header/filter/rows/footer draw
/// the same geometry, shifted by the shared horizontal offset).
/// </summary>
internal sealed class DataGridColumnLayout
{
    /// <summary>One resolved column: the column, its content x, and its width (≥ 1).</summary>
    public readonly record struct Entry(DataGridColumn Column, int X, int Width);

    private readonly List<Entry> _entries = [];
    private readonly Dictionary<DataGridColumn, int> _autoGrown = new(); // monotonic within a shape (§1 — no scroll jitter)

    /// <summary>The resolved entries (visible columns, left to right; fixed columns lead — §9.2).</summary>
    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>Total content width in cells (the horizontal extent).</summary>
    public int TotalWidth { get; private set; }

    /// <summary>The count of leading <see cref="DataGridColumnFixed.Left"/> entries (§9.2 — the
    /// caller passes fixed columns first; this is how many).</summary>
    public int FrozenCount { get; private set; }

    /// <summary>The frozen region's width in cells: the §9.3 expander gutter plus the leading fixed
    /// entries — everything left of it is pinned and draws unshifted over the scrolled band (§9.2).
    /// Always ≥ <see cref="GutterWidth"/>.</summary>
    public int FrozenWidth { get; private set; }

    /// <summary>The §9.3 master-detail expander gutter (0 when no detail template): a synthetic
    /// leading region every entry's x starts after — all presenters + hit math inherit it, and it
    /// is pinned like a fixed column (inside <see cref="FrozenWidth"/>).</summary>
    public int GutterWidth { get; private set; }

    /// <summary>The per-cell padding the painters apply inside a column (mirrors the mockup's 1-cell gutter).</summary>
    public const int CellPadding = 1;

    /// <summary>Resets the Auto-growth memory (a new shape/theme may shrink content legitimately).</summary>
    public void ResetAutoGrowth() => _autoGrown.Clear();

    /// <summary>
    /// Resets ONE column's Auto-growth memory (the header-edge double-click best-fit — §1 deferred
    /// UX, now landed): returning a manually-sized column to <c>Auto</c> must re-measure TIGHT
    /// against the current band, not resume the old monotonic high-water mark (which may remember a
    /// wide value that scrolled away long ago). Whole-layout <see cref="ResetAutoGrowth()"/> would
    /// jitter the OTHER Auto columns for no reason — the reset is per-gesture, per-column.
    /// </summary>
    public void ResetAutoGrowth(DataGridColumn column) => _autoGrown.Remove(column);

    /// <summary>
    /// Resolves widths: fixed cells verbatim; Auto = header ∨ widest band cell (via
    /// <paramref name="autoWidth"/>) with monotonic growth within the shape; star shares split the
    /// remaining viewport width by weight. Min/Max clamp every unit (min wins conflicts).
    /// </summary>
    /// <param name="columns">The grid's columns (hidden ones skipped).</param>
    /// <param name="viewportWidth">The viewport width star shares distribute over.</param>
    /// <param name="autoWidth">Content width probe for an Auto column (the band cache's widest formatted cell).</param>
    /// <param name="gutterWidth">The §9.3 expander gutter width (0 = no master-detail).</param>
    public void Resolve(IReadOnlyList<DataGridColumn> columns, int viewportWidth, Func<DataGridColumn, int> autoWidth,
                        int gutterWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(autoWidth);

        _entries.Clear();
        GutterWidth = Math.Max(0, gutterWidth);

        // Pass 1: fixed + auto widths; collect stars.
        Span<int> widths = columns.Count <= 64 ? stackalloc int[columns.Count] : new int[columns.Count];
        double starTotal = 0;
        int fixedTotal = 0;

        for (int i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            if (!column.Visible)
            {
                widths[i] = 0;
                continue;
            }

            switch (column.Width.Unit)
            {
                case DataGridLengthUnit.Cell:
                    widths[i] = Clamp(column, (int)column.Width.Value);
                    fixedTotal += widths[i] + 2 * CellPadding;
                    break;

                case DataGridLengthUnit.Auto:
                {
                    int content = Math.Max(Text.GraphemeWidth.StringWidth(column.EffectiveHeader) + 3, // sort/filter glyph room
                                           autoWidth(column));
                    if (_autoGrown.TryGetValue(column, out int grown) && grown > content)
                        content = grown;      // monotonic within the shape — no width jitter on scroll
                    _autoGrown[column] = content;
                    widths[i] = Clamp(column, content);
                    fixedTotal += widths[i] + 2 * CellPadding;
                    break;
                }

                default:
                    starTotal += column.Width.Value;
                    widths[i] = -1; // resolved in pass 2
                    break;
            }
        }

        // Pass 2: distribute the remainder over stars (largest-remainder rounding keeps totals exact).
        if (starTotal > 0)
        {
            int remaining = Math.Max(0, viewportWidth - fixedTotal - GutterWidth);
            double accumulated = 0;
            int assigned = 0;
            for (int i = 0; i < columns.Count; i++)
            {
                if (widths[i] != -1)
                    continue;
                var column = columns[i];
                accumulated += column.Width.Value / starTotal * remaining;
                int target = (int)Math.Round(accumulated) - assigned;
                assigned += target;
                widths[i] = Clamp(column, Math.Max(1, target - 2 * CellPadding));
            }
        }

        // Pass 3: lay out. The §9.3 gutter leads, then fixed entries (the caller partitions
        // fixed-first — §9.2); the frozen region is the gutter + the leading run of Fixed=Left.
        int x = GutterWidth;
        int frozenCount = 0;
        int frozenWidth = GutterWidth;
        for (int i = 0; i < columns.Count; i++)
        {
            if (!columns[i].Visible)
                continue;
            _entries.Add(new Entry(columns[i], x, widths[i]));
            x += widths[i] + 2 * CellPadding;
            if (frozenCount == _entries.Count - 1 && columns[i].Fixed == DataGridColumnFixed.Left)
            {
                frozenCount++;
                frozenWidth = x;
            }
        }
        TotalWidth = x;
        FrozenCount = frozenCount;
        FrozenWidth = frozenWidth;
    }

    private static int Clamp(DataGridColumn column, int width)
    {
        int min = Math.Max(1, column.MinWidth);
        int max = column.MaxWidth > 0 ? column.MaxWidth : int.MaxValue;
        return Math.Clamp(width, min, Math.Max(min, max)); // min wins a min>max conflict (the layout convention)
    }

    /// <summary>The entry containing content column <paramref name="x"/>, or −1 (hit testing).</summary>
    public int EntryAt(int x)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (x >= entry.X && x < entry.X + entry.Width + 2 * CellPadding)
                return i;
        }
        return -1;
    }
}
