using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.Text;
using Cursorial.UI.Input;

namespace Cursorial.UI.DataViews;

/// <summary>
/// The direct-draw header band (design doc §3.1): one cell row of column captions with the sort
/// glyph (<c>▲/▼</c>, multi-sort ordinal digits past the first level) and the filter affordance
/// (<c>▾</c>, amber when the column carries a filter — the mockup's active state). Its own render
/// boundary (ClipToBounds — a 1-row band, so the horizontal-offset re-ink stays band-local, §3.1);
/// mirrors the shared horizontal offset. Header cells hold VIRTUAL focus (a grid-owned index +
/// drawn cue — drawn cells cannot hold framework focus, §3.3); clicks sort (Shift appends).
/// </summary>
public sealed class DataGridHeaderPresenter : UIElement
{
    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> BackgroundProperty =
        UIProperty.Register<DataGridHeaderPresenter, Cursorial.Drawing.Media.IBrush?>(nameof(Background));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> ForegroundProperty =
        UIProperty.Register<DataGridHeaderPresenter, Cursorial.Drawing.Media.IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> SortGlyphBrushProperty =
        UIProperty.Register<DataGridHeaderPresenter, Cursorial.Drawing.Media.IBrush?>(nameof(SortGlyphBrush));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> FilterGlyphBrushProperty =
        UIProperty.Register<DataGridHeaderPresenter, Cursorial.Drawing.Media.IBrush?>(nameof(FilterGlyphBrush));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> ActiveFilterBrushProperty =
        UIProperty.Register<DataGridHeaderPresenter, Cursorial.Drawing.Media.IBrush?>(nameof(ActiveFilterBrush));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> HoverBackgroundProperty =
        UIProperty.Register<DataGridHeaderPresenter, Cursorial.Drawing.Media.IBrush?>(nameof(HoverBackground));

    /// <summary>The shared horizontal offset (the template binds it to the ScrollViewer's — §3.1).</summary>
    public static readonly StyledProperty<int> HorizontalOffsetProperty =
        UIProperty.Register<DataGridHeaderPresenter, int>(nameof(HorizontalOffset));

    static DataGridHeaderPresenter()
    {
        AffectsRender<DataGridHeaderPresenter>(
            BackgroundProperty, ForegroundProperty, SortGlyphBrushProperty, FilterGlyphBrushProperty,
            ActiveFilterBrushProperty, HoverBackgroundProperty, HorizontalOffsetProperty);
    }

    public Cursorial.Drawing.Media.IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Cursorial.Drawing.Media.IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Cursorial.Drawing.Media.IBrush? SortGlyphBrush { get => GetValue(SortGlyphBrushProperty); set => SetValue(SortGlyphBrushProperty, value); }
    public Cursorial.Drawing.Media.IBrush? FilterGlyphBrush { get => GetValue(FilterGlyphBrushProperty); set => SetValue(FilterGlyphBrushProperty, value); }
    public Cursorial.Drawing.Media.IBrush? ActiveFilterBrush { get => GetValue(ActiveFilterBrushProperty); set => SetValue(ActiveFilterBrushProperty, value); }
    public Cursorial.Drawing.Media.IBrush? HoverBackground { get => GetValue(HoverBackgroundProperty); set => SetValue(HoverBackgroundProperty, value); }
    public int HorizontalOffset { get => GetValue(HorizontalOffsetProperty); set => SetValue(HorizontalOffsetProperty, value); }

    private DataGrid? _owner;
    private int _hoverEntry = -1;

    /// <summary>The owning grid (stamped when the template applies).</summary>
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
            ClipToBounds = true; // own boundary — band-local re-ink (§3.1; safe: a 1-row band)
            InvalidateVisual();
        }
    }

    // Sort/filter state + column widths render from live grid state — every publish re-inks the band
    // (a 1-row raster; the glyphs and Auto widths must track the shape).
    private void OnSnapshotChanged(object? sender, EventArgs e) => InvalidateVisual();

    private DataGridColumnLayout? Layout => _owner?.RowsPresenter?.ColumnLayout;

    protected override Size MeasureOverride(Size availableSize) => new(availableSize.Columns, 1);

    protected override void Render(RenderContext context)
    {
        base.Render(context);
        var owner = _owner;
        var layout = Layout;
        if (owner is null || layout is null)
            return;

        if (Background is not null)
            context.FillOpaque(new Rect(0, 0, Bounds.Columns, 1), Background);

        int shift = -HorizontalOffset;
        var entries = layout.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            int x = entry.X + shift;
            int cellWidth = entry.Width + 2 * DataGridColumnLayout.CellPadding;
            if (x + cellWidth <= 0 || x >= Bounds.Columns)
                continue;

            if (i == _hoverEntry && HoverBackground is not null)
                context.FillOpaque(new Rect(x, 0, cellWidth, 1), HoverBackground);

            // Caption, truncated to leave glyph room on the right.
            string caption = entry.Column.EffectiveHeader;
            int glyphRoom = 2; // "▾" + gap; sort glyph adds another below when present
            var (direction, ordinal) = owner.GetSortState(entry.Column);
            if (direction is not null)
                glyphRoom += ordinal > 0 ? 3 : 2;

            DrawTruncated(context, x + DataGridColumnLayout.CellPadding, caption,
                          Math.Max(1, entry.Width - glyphRoom), Foreground);

            // Right-aligned glyph cluster: [sort][ordinal] [filter▾].
            int glyphX = x + cellWidth - DataGridColumnLayout.CellPadding - 1;
            if (entry.Column.AllowFilter)
            {
                bool active = owner.HasColumnFilter(entry.Column);
                context.DrawText(glyphX, 0, "▾", active ? ActiveFilterBrush ?? FilterGlyphBrush : FilterGlyphBrush);
                glyphX -= 2;
            }
            if (direction is { } d)
            {
                if (ordinal > 0 && ordinal < 9)
                {
                    context.DrawText(glyphX, 0, (ordinal + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), SortGlyphBrush);
                    glyphX -= 1;
                }
                context.DrawText(glyphX, 0, d == Shaping.SortDirection.Ascending ? "▲" : "▼", SortGlyphBrush);
            }
        }
    }

    private static void DrawTruncated(RenderContext context, int x, string text, int maxWidth, Cursorial.Drawing.Media.IBrush? brush)
    {
        if (brush is null)
            return;
        if (GraphemeWidth.StringWidth(text) <= maxWidth)
        {
            context.DrawText(x, 0, text, brush);
            return;
        }

        var enumerator = text.GetGraphemeEnumerator();
        int width = 0, end = 0;
        while (enumerator.MoveNext())
        {
            int next = width + GraphemeWidth.ClusterWidth(enumerator.Current);
            if (next > maxWidth - 1)
                break;
            width = next;
            end = enumerator.ElementIndex + enumerator.Current.Length;
        }
        context.DrawText(x, 0, text.AsSpan(0, end), brush);
        context.DrawText(x + width, 0, "…", brush);
    }

    /// <summary>The layout entry under a local x (accounting for the shared shift), or −1.</summary>
    private int EntryAt(int localX) => Layout?.EntryAt(localX + HorizontalOffset) ?? -1;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Handled || e.Button != MouseButton.Left || _owner is null || Layout is null)
            return;

        var position = e.GetPosition(this);
        int index = EntryAt(position.Column);
        if (index < 0)
            return;

        var column = Layout.Entries[index].Column;

        // The ▾ zone opens the filter popup (the popup stage wires OpenFilterPopup; sorting owns the
        // rest of the cell). Approximation: the last 2 cells of the cell rect are the filter zone.
        var entry = Layout.Entries[index];
        int cellRight = entry.X - HorizontalOffset + entry.Width + 2 * DataGridColumnLayout.CellPadding;
        bool onFilterZone = column.AllowFilter && position.Column >= cellRight - 2;

        if (onFilterZone)
            _owner.OpenFilterPopup(column);
        else if ((e.Modifiers & KeyModifiers.Shift) != 0)
            _owner.AppendSort(column);
        else
            _owner.CycleSort(column);

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        int hover = EntryAt(position.Column);
        if (hover != _hoverEntry)
        {
            _hoverEntry = hover;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverEntry != -1)
        {
            _hoverEntry = -1;
            InvalidateVisual();
        }
    }
}
