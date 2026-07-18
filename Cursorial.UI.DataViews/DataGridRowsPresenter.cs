using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

using CellStyle = Cursorial.Output.Style;

namespace Cursorial.UI.DataViews;

/// <summary>
/// The DataGrid's direct-draw virtualized viewport (design doc §3.2): the
/// <see cref="ScrollContentPresenter"/>'s content, implementing <see cref="ILogicalScrollHost"/> —
/// every view row is exactly one cell row, so the logical math is trivial
/// (<c>EstimateItemAt(r) = r</c>) and in-band scrolling is a pure composite slide (the SCP band
/// contract). <c>MeasureOverride</c> pre-composes the band cache (formatted cells, group captions)
/// from the published snapshot; <c>Render</c> only draws from it. Rows are NOT elements — this
/// presenter is the single hit leaf (row = y, column = x-range); the active edit row is the one
/// sanctioned element-hosting exception (the edit host arrives with the editing stage).
/// </summary>
/// <remarks>
/// This element must NEVER acquire a render-boundary predicate (no ClipToBounds / Opacity /
/// RenderOffset — its bounds are the full extent; a boundary would rent an extent-sized scene,
/// design doc §3.1). The SCP above it is the boundary with banded scenes.
/// </remarks>
public sealed class DataGridRowsPresenter : UIElement, ILogicalScrollHost
{
    // Styled look properties the theme sets via SetResource (drawn rows can't carry pseudo-classes —
    // §3.2; AffectsRender so a palette flip re-inks).
    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> RowBackgroundProperty =
        UIProperty.Register<DataGridRowsPresenter, Cursorial.Drawing.Media.IBrush?>(nameof(RowBackground));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> RowAlternationBackgroundProperty =
        UIProperty.Register<DataGridRowsPresenter, Cursorial.Drawing.Media.IBrush?>(nameof(RowAlternationBackground));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> SelectionBackgroundProperty =
        UIProperty.Register<DataGridRowsPresenter, Cursorial.Drawing.Media.IBrush?>(nameof(SelectionBackground));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> HoverBackgroundProperty =
        UIProperty.Register<DataGridRowsPresenter, Cursorial.Drawing.Media.IBrush?>(nameof(HoverBackground));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> GroupRowBackgroundProperty =
        UIProperty.Register<DataGridRowsPresenter, Cursorial.Drawing.Media.IBrush?>(nameof(GroupRowBackground));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> TextBrushProperty =
        UIProperty.Register<DataGridRowsPresenter, Cursorial.Drawing.Media.IBrush?>(nameof(TextBrush));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> AccentBrushProperty =
        UIProperty.Register<DataGridRowsPresenter, Cursorial.Drawing.Media.IBrush?>(nameof(AccentBrush));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> FocusCellBackgroundProperty =
        UIProperty.Register<DataGridRowsPresenter, Cursorial.Drawing.Media.IBrush?>(nameof(FocusCellBackground));

    static DataGridRowsPresenter()
    {
        AffectsRender<DataGridRowsPresenter>(
            RowBackgroundProperty, RowAlternationBackgroundProperty, SelectionBackgroundProperty,
            HoverBackgroundProperty, GroupRowBackgroundProperty, TextBrushProperty, AccentBrushProperty,
            FocusCellBackgroundProperty);
    }

    public Cursorial.Drawing.Media.IBrush? RowBackground { get => GetValue(RowBackgroundProperty); set => SetValue(RowBackgroundProperty, value); }
    public Cursorial.Drawing.Media.IBrush? RowAlternationBackground { get => GetValue(RowAlternationBackgroundProperty); set => SetValue(RowAlternationBackgroundProperty, value); }
    public Cursorial.Drawing.Media.IBrush? SelectionBackground { get => GetValue(SelectionBackgroundProperty); set => SetValue(SelectionBackgroundProperty, value); }
    public Cursorial.Drawing.Media.IBrush? HoverBackground { get => GetValue(HoverBackgroundProperty); set => SetValue(HoverBackgroundProperty, value); }
    public Cursorial.Drawing.Media.IBrush? GroupRowBackground { get => GetValue(GroupRowBackgroundProperty); set => SetValue(GroupRowBackgroundProperty, value); }
    public Cursorial.Drawing.Media.IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Cursorial.Drawing.Media.IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public Cursorial.Drawing.Media.IBrush? FocusCellBackground { get => GetValue(FocusCellBackgroundProperty); set => SetValue(FocusCellBackgroundProperty, value); }

    private DataGrid? _owner;
    private Size _viewport;
    private bool _bandDirty = true;

    // The band cache (§3.2): one entry per cached view row. String cells v1; the span-formatter
    // pooled-char upgrade is a recorded follow-up (design doc §2.2).
    private readonly List<CachedRow> _band = [];
    private int _bandStart;
    private int _snapshotVersion = -1;

    private readonly struct CachedRow
    {
        public required bool IsGroup { get; init; }
        public required int RowId { get; init; }
        public required int GroupNodeIndex { get; init; }
        public required int Level { get; init; }
        public required string[] Cells { get; init; }  // per visible column (empty for group rows)
        public required string GroupCaption { get; init; }
        public required string GroupSummary { get; init; }
        public required bool GroupCollapsed { get; init; }
    }

    /// <summary>The owning grid (stamped by the grid when the template applies).</summary>
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
            InvalidateBand();
        }
    }

    /// <summary>The resolved column geometry (computed here per measure; the other band presenters read it).</summary>
    internal DataGridColumnLayout ColumnLayout { get; } = new();

    /// <summary>The hover view row (presenter state — drawn rows have no elements to flip pseudo-classes on).</summary>
    internal int HoverViewIndex { get; private set; } = -1;

    private void OnSnapshotChanged(object? sender, EventArgs e) => InvalidateBand();

    /// <summary>Marks the band cache stale (data/shape/selection change) and schedules re-fill + re-ink.</summary>
    internal void InvalidateBand()
    {
        _bandDirty = true;
        InvalidateMeasure();
        InvalidateVisual();
    }

    // ── IScrollContentHost / ILogicalScrollHost (fixed-height rows ⇒ trivial math) ───────────────

    public bool IsScrollClient => true;
    public ScrollContentPresenter? ScrollOwner { get; set; }
    public bool CanScrollHorizontally { get; set; }
    public bool CanScrollVertically { get; set; }
    public bool IsLogicalScroll => true;

    public Size GetExtent()
        => new(Math.Max(ColumnLayout.TotalWidth, _viewport.Columns),
               Math.Max(_owner?.Snapshot.Count ?? 0, 1));

    public void SetViewport(Size viewport)
    {
        // The SCP hands the arranged viewport at END of arrange "before the host's next measure
        // realizes its band" (the seam contract) — the host owns triggering that next measure. The
        // first hand-off is the band bootstrap: without this invalidation the initial fill ran with
        // a zero viewport and the grid rendered blank until the next unrelated invalidation.
        if (viewport != _viewport)
        {
            _viewport = viewport;
            InvalidateBand();
        }
    }

    public void InvalidateRealization() => InvalidateBand();

    public int LineStep(int currentOffset, int sign, bool vertical) => 1;

    public int PageStep(int currentOffset, int sign, bool vertical)
        => vertical ? Math.Max(1, _viewport.Rows - 1) : Math.Max(1, _viewport.Columns - 1);

    public int ItemCount => _owner?.Snapshot.Count ?? 0;

    public int EstimateItemAt(int offsetRow) => offsetRow;

    public Rect BringItemIntoView(int itemIndex)
        => new(0, itemIndex, Math.Max(1, ColumnLayout.TotalWidth), 1);

    // ── Band fill (measure-time — the VSP-sanctioned self-mutation site) ─────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        FillBandCache();

        // The hosted editor (the §3.2 element-hosting special case) measures at its cell width.
        if (_editor is not null && _editColumnIndex >= 0 && _editColumnIndex < ColumnLayout.Entries.Count)
            _editor.Measure(new Size(Math.Max(1, ColumnLayout.Entries[_editColumnIndex].Width), 1));

        // The SCP host path measures content at the viewport; the extent publishes via GetExtent.
        return new Size(Math.Min(ColumnLayout.TotalWidth, availableSize.Columns), Math.Min(ItemCount, availableSize.Rows));
    }

    private (int Start, int Length) BandWindow()
    {
        // The SCP band internals (IVT — design doc §3.1; the public-seam promotion is a recorded
        // follow-up). Without an adopting SCP (a bare presenter in tests), the window is the viewport.
        if (ScrollOwner is { } scp)
            return (scp.BandStartRow, scp.BandLength);
        return (0, Math.Max(_viewport.Rows, 1));
    }

    private void FillBandCache()
    {
        var owner = _owner;
        if (owner is null)
        {
            _band.Clear();
            return;
        }

        var snapshot = owner.Snapshot;
        var (start, length) = BandWindow();
        start = Math.Clamp(start, 0, Math.Max(0, snapshot.Count - 1));
        length = Math.Min(length, snapshot.Count - start);

        if (!_bandDirty && start == _bandStart && _band.Count == length && snapshot.Version == _snapshotVersion)
            return;

        // Resolve columns BEFORE formatting (the cache stores per-visible-column cells) — but Auto
        // widths need the formatted content, so: format first into the cache, then resolve widths
        // from it (the band-limited Auto contract, §1).
        var columns = new List<DataGridColumn>(owner.Columns.Count);
        foreach (var column in owner.Columns)
        {
            if (column.Visible)
                columns.Add(column);
        }

        _band.Clear();
        var controller = owner.Controller;
        for (int i = 0; i < length; i++)
        {
            var row = snapshot.GetRow(start + i);
            if (row.IsGroup)
            {
                var node = snapshot.Groups[row.GroupNodeIndex];
                string caption = $"{HeaderOf(owner, node)}: {node.FormattedKey}";
                string summary = node.Summaries.Length > 0 ? string.Join(" · ", node.Summaries) : string.Empty;
                _band.Add(new CachedRow
                {
                    IsGroup = true,
                    RowId = -1,
                    GroupNodeIndex = row.GroupNodeIndex,
                    Level = row.Level,
                    Cells = [],
                    GroupCaption = $"{caption} ({node.RowCount})",
                    GroupSummary = summary,
                    GroupCollapsed = node.IsCollapsed,
                });
            }
            else
            {
                var cells = new string[columns.Count];
                for (int c = 0; c < columns.Count; c++)
                    cells[c] = controller?.FormatCell(row.RowId, columns[c]) ?? string.Empty;
                _band.Add(new CachedRow
                {
                    IsGroup = false,
                    RowId = row.RowId,
                    GroupNodeIndex = -1,
                    Level = row.Level,
                    Cells = cells,
                    GroupCaption = string.Empty,
                    GroupSummary = string.Empty,
                    GroupCollapsed = false,
                });
            }
        }

        _bandStart = start;
        _snapshotVersion = snapshot.Version;
        _bandDirty = false;

        // Band-limited Auto widths: the widest formatted cell in the cache (monotonic via the layout).
        ColumnLayout.Resolve(columns, Math.Max(1, _viewport.Columns), column =>
        {
            int index = columns.IndexOf(column);
            int widest = 0;
            foreach (var cached in _band)
            {
                if (!cached.IsGroup && index < cached.Cells.Length)
                    widest = Math.Max(widest, GraphemeWidth.StringWidth(cached.Cells[index]));
            }
            return widest;
        });
    }

    private static string HeaderOf(DataGrid owner, Shaping.GroupNode node)
    {
        // The group level's column header: group levels are ordered; node.Level indexes them.
        if (node.Level < owner.GroupDescriptions.Count &&
            owner.GroupDescriptions[node.Level].ColumnKey is DataGridColumn column)
        {
            return column.EffectiveHeader;
        }
        return string.Empty;
    }

    // ── Render (draw-only — the cache is the truth) ──────────────────────────────────────────────

    protected override void Render(RenderContext context)
    {
        base.Render(context);
        var owner = _owner;
        if (owner is null || _band.Count == 0)
            return;

        var selection = owner.RowSelection;
        int focusRow = owner.FocusViewIndex;
        int focusColumn = owner.FocusColumnIndex;
        var entries = ColumnLayout.Entries;
        int totalWidth = Math.Max(ColumnLayout.TotalWidth, _viewport.Columns);

        for (int i = 0; i < _band.Count; i++)
        {
            int y = _bandStart + i;
            var row = _band[i];

            // Background lanes: group tint > selection > hover > alternation.
            Cursorial.Drawing.Media.IBrush? background = row.IsGroup
                ? GroupRowBackground
                : selection is not null && selection.IsSelected(row.RowId)
                    ? SelectionBackground
                    : HoverViewIndex == y
                        ? HoverBackground
                        : (y & 1) == 1
                            ? RowAlternationBackground
                            : RowBackground;

            if (background is not null)
                context.FillOpaque(new Rect(0, y, totalWidth, 1), background);

            if (row.IsGroup)
            {
                // ▾/▸ expander, indent by level, caption, right-aligned summary.
                int x = row.Level * 2;
                string glyph = row.GroupCollapsed ? "▸" : "▾";
                context.DrawText(x, y, glyph, AccentBrush ?? TextBrush);
                DrawClipped(context, x + 2, y, row.GroupCaption, int.MaxValue, TextBrush);

                if (row.GroupSummary.Length > 0)
                {
                    int width = GraphemeWidth.StringWidth(row.GroupSummary);
                    int summaryX = Math.Max(x + 2, totalWidth - width - 1);
                    DrawClipped(context, summaryX, y, row.GroupSummary, int.MaxValue, AccentBrush ?? TextBrush);
                }
                continue;
            }

            for (int c = 0; c < entries.Count && c < row.Cells.Length; c++)
            {
                var entry = entries[c];
                int cellX = entry.X + DataGridColumnLayout.CellPadding;

                // The focus cell's well-fill (the mockup's focuscell).
                if (y == focusRow && c == focusColumn && FocusCellBackground is not null)
                    context.FillOpaque(new Rect(entry.X, y, entry.Width + 2 * DataGridColumnLayout.CellPadding, 1), FocusCellBackground);

                string text = row.Cells[c];
                int textWidth = GraphemeWidth.StringWidth(text);
                int drawX = entry.Column.TextAlignment == Cursorial.Rendering.Text.TextAlignment.Right && textWidth < entry.Width
                    ? cellX + entry.Width - textWidth
                    : cellX;
                DrawClipped(context, drawX, y, text, entry.Width, TextBrush);
            }
        }
    }

    /// <summary>Draws text grapheme-truncated to <paramref name="maxWidth"/> (there is no clip stack inside Render — §3.2).</summary>
    private static void DrawClipped(RenderContext context, int x, int y, string text, int maxWidth, Cursorial.Drawing.Media.IBrush? brush)
    {
        if (text.Length == 0 || brush is null)
            return;

        if (maxWidth < int.MaxValue && GraphemeWidth.StringWidth(text) > maxWidth)
        {
            // Truncate on a grapheme boundary with an ellipsis cell.
            var enumerator = text.GetGraphemeEnumerator();
            int width = 0;
            int end = 0;
            while (enumerator.MoveNext())
            {
                int next = width + GraphemeWidth.ClusterWidth(enumerator.Current);
                if (next > maxWidth - 1)
                    break;
                width = next;
                end = enumerator.ElementIndex + enumerator.Current.Length;
            }
            context.DrawText(x, y, text.AsSpan(0, end), brush);
            context.DrawText(x + width, y, "…", brush);
            return;
        }

        context.DrawText(x, y, text, brush);
    }

    // ── In-cell editing — the sanctioned element-hosting special case (§3.2, owner mandate) ──────

    private Cursorial.UI.Controls.TextBox? _editor;
    private int _editViewIndex = -1;
    private int _editColumnIndex = -1;

    /// <summary>Whether an editor is hosted (the grid's key routing branches on it).</summary>
    internal bool IsEditing => _editor is not null;

    /// <summary>The edited (viewIndex, columnIndex) while editing.</summary>
    internal (int ViewIndex, int ColumnIndex) EditCell => (_editViewIndex, _editColumnIndex);

    /// <summary>
    /// Hosts the TextBox editor at a cell (v1 — the editor suite rides the same host later): the
    /// presenter adopts the element as a visual/logical child, arranges it at the cell's CONTENT
    /// rect (it scrolls with the band naturally), and focuses it. The drawn cell underneath is
    /// painted over by the editor's own background.
    /// </summary>
    internal void BeginEdit(int viewIndex, int columnIndex, string initialText)
    {
        EndEditVisual();

        var editor = new Cursorial.UI.Controls.TextBox { Text = initialText };
        _editor = editor;
        _editViewIndex = viewIndex;
        _editColumnIndex = columnIndex;
        AdoptChild(editor, index: -1);
        InvalidateMeasure();

        // Focus after the editor materializes (measure/arrange run first) — the parked-work idiom.
        UIApplication.Current?.Dispatcher.Post(() =>
        {
            if (_editor == editor)
            {
                editor.Focus(Cursorial.UI.Input.FocusNavigationMethod.Programmatic);
                editor.SelectAll();
            }
        });
    }

    /// <summary>The editor's current text (the commit path reads before teardown).</summary>
    internal string? EditorText => _editor?.Text;

    /// <summary>Tears the editor down and returns focus to the grid.</summary>
    internal void EndEditVisual()
    {
        if (_editor is null)
            return;
        DisownChild(_editor);
        _editor = null;
        _editViewIndex = -1;
        _editColumnIndex = -1;
        InvalidateMeasure();
        InvalidateVisual();
        _owner?.Focus(Cursorial.UI.Input.FocusNavigationMethod.Programmatic);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The editor arranges at its cell's content rect (content coords == local coords).
        if (_editor is not null && _editColumnIndex >= 0 && _editColumnIndex < ColumnLayout.Entries.Count)
        {
            var entry = ColumnLayout.Entries[_editColumnIndex];
            _editor.Arrange(new Rect(entry.X + DataGridColumnLayout.CellPadding, _editViewIndex,
                                     Math.Max(1, entry.Width), 1));
        }
        return base.ArrangeOverride(finalSize);
    }

    // ── Hit testing + mouse (the single hit leaf — §3.2) ─────────────────────────────────────────

    /// <summary>Maps a content position to (viewIndex, columnEntryIndex, isExpander).</summary>
    internal (int ViewIndex, int ColumnIndex, bool OnExpander) HitCell(int x, int y)
    {
        var owner = _owner;
        if (owner is null || y < 0 || y >= owner.Snapshot.Count)
            return (-1, -1, false);

        var row = owner.Snapshot.GetRow(y);
        if (row.IsGroup)
        {
            int expanderX = row.Level * 2;
            return (y, -1, x >= expanderX && x <= expanderX + 1);
        }

        return (y, ColumnLayout.EntryAt(x), false);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Handled || e.Button != MouseButton.Left || _owner is null)
            return;

        var position = e.GetPosition(this);
        var (viewIndex, columnIndex, onExpander) = HitCell(position.Column, position.Row);
        if (viewIndex < 0)
            return;

        _owner.HandleRowPress(viewIndex, columnIndex, onExpander, e.Modifiers, e.ClickCount);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_owner is null)
            return;

        var position = e.GetPosition(this);
        int hover = position.Row >= 0 && position.Row < ItemCount ? position.Row : -1;
        if (hover != HoverViewIndex)
        {
            HoverViewIndex = hover;
            InvalidateVisual(); // re-ink only (the band cache is position-keyed, not hover-keyed)
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (HoverViewIndex != -1)
        {
            HoverViewIndex = -1;
            InvalidateVisual();
        }
    }
}
