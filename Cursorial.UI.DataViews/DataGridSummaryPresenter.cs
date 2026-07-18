using Cursorial.Rendering;
using Cursorial.Text;

namespace Cursorial.UI.DataViews;

/// <summary>
/// The direct-draw summary footer (design doc §3.1; the mockup's <c>gfoot</c> band): grand-total
/// summaries aligned under their columns, stacking multiple aggregates per column as rows — the
/// band grows to the max per-column aggregate count (the panel-pinned height rule). Its own render
/// boundary; mirrors the shared horizontal offset.
/// </summary>
public sealed class DataGridSummaryPresenter : UIElement
{
    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> BackgroundProperty =
        UIProperty.Register<DataGridSummaryPresenter, Cursorial.Drawing.Media.IBrush?>(nameof(Background));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> ValueBrushProperty =
        UIProperty.Register<DataGridSummaryPresenter, Cursorial.Drawing.Media.IBrush?>(nameof(ValueBrush));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> LabelBrushProperty =
        UIProperty.Register<DataGridSummaryPresenter, Cursorial.Drawing.Media.IBrush?>(nameof(LabelBrush));

    public static readonly StyledProperty<int> HorizontalOffsetProperty =
        UIProperty.Register<DataGridSummaryPresenter, int>(nameof(HorizontalOffset));

    static DataGridSummaryPresenter()
    {
        AffectsRender<DataGridSummaryPresenter>(BackgroundProperty, ValueBrushProperty, LabelBrushProperty,
                                                HorizontalOffsetProperty);
    }

    public Cursorial.Drawing.Media.IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Cursorial.Drawing.Media.IBrush? ValueBrush { get => GetValue(ValueBrushProperty); set => SetValue(ValueBrushProperty, value); }
    public Cursorial.Drawing.Media.IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public int HorizontalOffset { get => GetValue(HorizontalOffsetProperty); set => SetValue(HorizontalOffsetProperty, value); }

    private DataGrid? _owner;

    internal DataGrid? Owner
    {
        get => _owner;
        set
        {
            if (ReferenceEquals(_owner, value))
                return;
            if (_owner is not null)
                _owner.SnapshotChanged -= OnSnapshotChanged;
            _owner = value;
            if (_owner is not null)
                _owner.SnapshotChanged += OnSnapshotChanged;
            ClipToBounds = true; // own boundary — band-local re-ink (§3.1)
            InvalidateMeasure();
        }
    }

    private void OnSnapshotChanged(object? sender, EventArgs e)
    {
        InvalidateMeasure(); // totals may change the stacked height
        InvalidateVisual();
    }

    /// <summary>Per visible column: the summary cell lines (label + value), aligned by description column.</summary>
    private List<(int EntryIndex, string Label, string Value)> CollectCells()
    {
        var cells = new List<(int, string, string)>();
        var owner = _owner;
        var layout = owner?.RowsPresenter?.ColumnLayout;
        if (owner is null || layout is null)
            return cells;

        var totals = owner.Controller?.Totals ?? [];
        for (int s = 0; s < owner.SummaryDescriptions.Count && s < totals.Count; s++)
        {
            var description = owner.SummaryDescriptions[s];
            if (description.ColumnKey is not DataGridColumn column)
                continue;

            for (int i = 0; i < layout.Entries.Count; i++)
            {
                if (ReferenceEquals(layout.Entries[i].Column, column))
                {
                    cells.Add((i, AggregateLabel(description.Aggregate), totals[s]));
                    break;
                }
            }
        }

        return cells;
    }

    private static string AggregateLabel(Shaping.AggregateKind kind) => kind switch
    {
        Shaping.AggregateKind.Sum => "Σ",
        Shaping.AggregateKind.Average => "x̄",
        Shaping.AggregateKind.Min => "⌄",
        Shaping.AggregateKind.Max => "⌃",
        _ => "#",
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        // Footer height = max per-column aggregate count (the stacked rule); 0 summaries ⇒ 1 row
        // (the template collapses the band via ShowSummaryFooter when unused).
        var cells = CollectCells();
        int height = 1;
        if (cells.Count > 0)
        {
            var counts = new Dictionary<int, int>();
            foreach (var (entry, _, _) in cells)
                counts[entry] = counts.GetValueOrDefault(entry) + 1;
            height = counts.Values.Max();
        }
        return new Size(availableSize.Columns, height);
    }

    protected override void Render(RenderContext context)
    {
        base.Render(context);
        var owner = _owner;
        var layout = owner?.RowsPresenter?.ColumnLayout;
        if (owner is null || layout is null)
            return;

        int rows = Math.Max(1, Bounds.Rows);
        if (Background is not null)
            context.FillOpaque(new Rect(0, 0, Bounds.Columns, rows), Background);

        var stackRow = new Dictionary<int, int>(); // entry index → next stacked row
        var cells = CollectCells();

        // §9.2 paint order (the rows presenter's mirror): scrolling cells shifted first, then the
        // frozen region re-fills and draws unshifted on top. Stacking accounting is per-entry, so
        // the two filtered passes keep each entry's cells in collection order.
        DrawCellPass(context, layout, cells, stackRow, rows, frozen: false);
        if (layout.FrozenCount > 0)
        {
            if (Background is not null && HorizontalOffset > 0)
                context.FillOpaque(new Rect(0, 0, layout.FrozenWidth, rows), Background);
            DrawCellPass(context, layout, cells, stackRow, rows, frozen: true);
        }
    }

    private void DrawCellPass(RenderContext context, DataGridColumnLayout layout,
                              List<(int EntryIndex, string Label, string Value)> cells,
                              Dictionary<int, int> stackRow, int rows, bool frozen)
    {
        foreach (var (entryIndex, label, value) in cells)
        {
            bool isFrozen = entryIndex < layout.FrozenCount;
            if (isFrozen != frozen)
                continue;

            int row = stackRow.GetValueOrDefault(entryIndex);
            stackRow[entryIndex] = row + 1;
            if (row >= rows)
                continue;

            var entry = layout.Entries[entryIndex];
            int baseX = isFrozen ? entry.X : entry.X - HorizontalOffset;
            int leftEdge = isFrozen ? 0 : layout.FrozenWidth;
            if (baseX + entry.Width + 2 * DataGridColumnLayout.CellPadding <= leftEdge || baseX >= Bounds.Columns)
                continue;

            int x = baseX + DataGridColumnLayout.CellPadding;
            // "Σ $203,650" — label muted, value accent, right-aligned within the column.
            int labelWidth = GraphemeWidth.StringWidth(label);
            int valueWidth = GraphemeWidth.StringWidth(value);
            int contentWidth = labelWidth + 1 + valueWidth;
            int drawX = contentWidth < entry.Width ? x + entry.Width - contentWidth : x;

            context.DrawText(drawX, row, label, LabelBrush);
            context.DrawText(drawX + labelWidth + 1, row, value, ValueBrush);
        }
    }
}
