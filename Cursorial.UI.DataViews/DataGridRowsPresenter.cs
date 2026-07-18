using System.Diagnostics.CodeAnalysis;

using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.DataViews.Shaping;
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

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> DataBarFillBrushProperty =
        UIProperty.Register<DataGridRowsPresenter, Cursorial.Drawing.Media.IBrush?>(nameof(DataBarFillBrush));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> DataBarTrackBrushProperty =
        UIProperty.Register<DataGridRowsPresenter, Cursorial.Drawing.Media.IBrush?>(nameof(DataBarTrackBrush));

    /// <summary>Ghost ink for the new-row template (the muted * indicator + per-column placeholders — §3.2).</summary>
    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> MutedBrushProperty =
        UIProperty.Register<DataGridRowsPresenter, Cursorial.Drawing.Media.IBrush?>(nameof(MutedBrush));

    static DataGridRowsPresenter()
    {
        AffectsRender<DataGridRowsPresenter>(
            RowBackgroundProperty, RowAlternationBackgroundProperty, SelectionBackgroundProperty,
            HoverBackgroundProperty, GroupRowBackgroundProperty, TextBrushProperty, AccentBrushProperty,
            FocusCellBackgroundProperty, DataBarFillBrushProperty, DataBarTrackBrushProperty,
            MutedBrushProperty);
    }

    public Cursorial.Drawing.Media.IBrush? RowBackground { get => GetValue(RowBackgroundProperty); set => SetValue(RowBackgroundProperty, value); }
    public Cursorial.Drawing.Media.IBrush? RowAlternationBackground { get => GetValue(RowAlternationBackgroundProperty); set => SetValue(RowAlternationBackgroundProperty, value); }
    public Cursorial.Drawing.Media.IBrush? SelectionBackground { get => GetValue(SelectionBackgroundProperty); set => SetValue(SelectionBackgroundProperty, value); }
    public Cursorial.Drawing.Media.IBrush? HoverBackground { get => GetValue(HoverBackgroundProperty); set => SetValue(HoverBackgroundProperty, value); }
    public Cursorial.Drawing.Media.IBrush? GroupRowBackground { get => GetValue(GroupRowBackgroundProperty); set => SetValue(GroupRowBackgroundProperty, value); }
    public Cursorial.Drawing.Media.IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Cursorial.Drawing.Media.IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public Cursorial.Drawing.Media.IBrush? FocusCellBackground { get => GetValue(FocusCellBackgroundProperty); set => SetValue(FocusCellBackgroundProperty, value); }
    public Cursorial.Drawing.Media.IBrush? DataBarFillBrush { get => GetValue(DataBarFillBrushProperty); set => SetValue(DataBarFillBrushProperty, value); }
    public Cursorial.Drawing.Media.IBrush? DataBarTrackBrush { get => GetValue(DataBarTrackBrushProperty); set => SetValue(DataBarTrackBrushProperty, value); }
    public Cursorial.Drawing.Media.IBrush? MutedBrush { get => GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }

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

        // Conditional-formatting verdicts, evaluated at BAND-FILL time (§2.7 — never at paint).
        // The no-rules fast path shares the static empties (zero per-row cost).
        public CellFormat[] CellFormats { get; init; }
        public double[] BarFractions { get; init; }
        public CellFormat RowFormat { get; init; }
    }

    private static readonly CellFormat[] NoFormats = [];
    private static readonly double[] NoFractions = [];

    // The data-bar glyph pools: the painter slices spans off these (never a per-frame string).
    private const int MaxBarCells = 128;
    private static readonly string BarFillGlyphs = new('█', MaxBarCells);
    private static readonly string BarTrackGlyphs = new('░', MaxBarCells);

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

    /// <summary>Whether the trailing new-row template is live (the owner's eligibility verdict — §3.2).</summary>
    private bool NewRowActive => _owner?.HasNewRowPlaceholder == true;

    /// <summary>The new-row template's view index (== Snapshot.Count — one PAST the real rows), or −1.</summary>
    internal int NewRowViewIndex => NewRowActive ? _owner!.Snapshot.Count : -1;

    public Size GetExtent()
        => new(Math.Max(ColumnLayout.TotalWidth, _viewport.Columns),
               Math.Max(ItemCount, 1));

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

    // The new-row template participates in the scroll extent (reachable at the very bottom) but
    // never in the band CACHE — every snapshot read stays guarded by the real Count (§3.2).
    public int ItemCount => (_owner?.Snapshot.Count ?? 0) + (NewRowActive ? 1 : 0);

    public int EstimateItemAt(int offsetRow) => offsetRow;

    public Rect BringItemIntoView(int itemIndex)
        => new(0, itemIndex, Math.Max(1, ColumnLayout.TotalWidth), 1);

    // ── Band fill (measure-time — the VSP-sanctioned self-mutation site) ─────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        FillBandCache();

        // The hosted editor (the §3.2 element-hosting special case) measures at its cell width
        // (minus the Spin kind's drawn ▲▼ suffix reserve).
        if (_editor is not null && _editColumnIndex >= 0 && _editColumnIndex < ColumnLayout.Entries.Count)
            _editor.Measure(new Size(EditorWidth(ColumnLayout.Entries[_editColumnIndex]), 1));

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
        bool hasRules = controller?.HasFormatRules == true; // the no-rules fast path — static empties
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
                    CellFormats = NoFormats,
                    BarFractions = NoFractions,
                });
            }
            else
            {
                var cells = new string[columns.Count];
                var formats = hasRules ? new CellFormat[columns.Count] : NoFormats;
                var fractions = hasRules ? new double[columns.Count] : NoFractions;
                for (int c = 0; c < columns.Count; c++)
                {
                    cells[c] = controller?.FormatCell(row.RowId, columns[c]) ?? string.Empty;
                    if (hasRules)
                    {
                        // §2.7: verdicts evaluate HERE (band fill), the painter only reads them.
                        formats[c] = controller!.GetCellFormat(row.RowId, columns[c]);
                        fractions[c] = controller.GetDataBarFraction(row.RowId, columns[c]);
                    }
                }
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
                    CellFormats = formats,
                    BarFractions = fractions,
                    RowFormat = hasRules ? controller!.GetRowFormat(row.RowId) : default,
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
        if (owner is null)
            return; // (an empty band still draws the new-row template — an empty AllowAddNew grid)

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
                double fraction = c < row.BarFractions.Length ? row.BarFractions[c] : double.NaN;
                bool hasBar = !double.IsNaN(fraction);

                // A data-bar cell pins its value LEFT with the bar filling the remainder (the
                // mockup's amtcell); everything else honors the column alignment.
                int drawX = !hasBar &&
                            entry.Column.TextAlignment == Cursorial.Rendering.Text.TextAlignment.Right &&
                            textWidth < entry.Width
                    ? cellX + entry.Width - textWidth
                    : cellX;

                // The cell verdict overlays the row verdict (§2.7 — both pre-computed at band fill).
                var format = (c < row.CellFormats.Length ? row.CellFormats[c] : default).OverlayOn(row.RowFormat);
                DrawFormattedCell(context, drawX, y, text, entry.Width, format);

                if (hasBar)
                {
                    int used = Math.Min(textWidth, entry.Width);
                    DrawDataBar(context, cellX + used + 1, y, entry.Width - used - 1, fraction);
                }
            }
        }

        // The Spin editor's ▲▼ affordance beside the hosted TextBox (the mockup's spinbtns) — drawn
        // in the suffix reserve EditorWidth carved out of the edit cell (skipped in cramped cells).
        if (_editor is not null && _editorKind == DataGridEditorKind.Spin &&
            _editColumnIndex >= 0 && _editColumnIndex < entries.Count)
        {
            var entry = entries[_editColumnIndex];
            if (entry.Width >= 6)
            {
                context.DrawText(entry.X + DataGridColumnLayout.CellPadding + entry.Width - 2,
                                 _editViewIndex, "▲▼", AccentBrush ?? TextBrush);
            }
        }

        DrawNewRowTemplate(context, owner, entries, focusRow, focusColumn);
    }

    /// <summary>
    /// The new-row template at view index == Snapshot.Count (the mockup's <c>newrow</c>): a muted
    /// <c>*</c> indicator plus per-column ghost placeholders hinting each editable column's editor
    /// kind. Drawn straight from column state — never cached in the band (it is not a snapshot
    /// row; §3.2). ASCII <c>*</c> stands in for the mockup's fullwidth <c>＊</c>: at CellPadding=1
    /// a width-2 glyph would collide with the first cell's content (and wide glyphs are not
    /// reliable across terminals — the ReliableWideGlyphs policy).
    /// </summary>
    private void DrawNewRowTemplate(RenderContext context, DataGrid owner,
                                    IReadOnlyList<DataGridColumnLayout.Entry> entries,
                                    int focusRow, int focusColumn)
    {
        int y = NewRowViewIndex;
        if (y < 0)
            return;

        // Only ink inside the band scene (the SCP band covers the extent tail because ItemCount
        // includes this row; out-of-band draws would be clipped anyway — skip the work).
        var (windowStart, windowLength) = BandWindow();
        if (y < windowStart || y >= windowStart + windowLength)
            return;

        var ghost = MutedBrush ?? TextBrush;

        // The focused ghost cell keeps the focus-cell well cue (it is focusable like a data row).
        if (y == focusRow && focusColumn >= 0 && focusColumn < entries.Count && FocusCellBackground is not null)
        {
            var focused = entries[focusColumn];
            context.FillOpaque(new Rect(focused.X, y, focused.Width + 2 * DataGridColumnLayout.CellPadding, 1),
                               FocusCellBackground);
        }

        context.DrawText(0, y, "*", ghost);
        var controller = owner.Controller;
        foreach (var entry in entries)
        {
            if (controller?.IsColumnEditable(entry.Column) != true)
                continue;

            string hint = owner.ResolveEditorKind(entry.Column) switch
            {
                DataGridEditorKind.Combo => "(pick)",
                DataGridEditorKind.Date => "yyyy-mm-dd",
                DataGridEditorKind.Spin => "0",
                DataGridEditorKind.None => string.Empty,
                _ => "…", // "…" — the text ghost
            };
            if (hint.Length != 0)
                DrawClipped(context, entry.X + DataGridColumnLayout.CellPadding, y, hint, entry.Width, ghost);
        }
        // (no row fill — the ghost row deliberately stays on the resting background)
    }

    /// <summary>
    /// Draws one data cell honoring its conditional-format verdict: an empty verdict rides the
    /// resting <see cref="TextBrush"/> lane; a colored/attributed one draws through the Color
    /// overload (the format's fg wins; Bold/Inverse/bg fold into the base <see cref="CellStyle"/> —
    /// NoColor tiers keep the attribute cues, §4). Grapheme-truncated like every drawn cell.
    /// </summary>
    private void DrawFormattedCell(RenderContext context, int x, int y, string text, int maxWidth, in CellFormat format)
    {
        if (format.IsEmpty)
        {
            DrawClipped(context, x, y, text, maxWidth, TextBrush);
            return;
        }

        if (text.Length == 0 || (format.Foreground is null && TextBrush is null))
            return;

        var attributes = default(TextAttributes);
        if (format.Bold)
            attributes |= TextAttributes.Bold;
        if (format.Inverse)
            attributes |= TextAttributes.Inverse;

        CellStyle style = default;
        if (attributes != default)
            style = style.WithAttributes(attributes);
        if (format.Background is { } background)
            style = style.WithBackground(background);

        // Truncate on a grapheme boundary (the DrawClipped contract), then emit through whichever
        // foreground lane the verdict picked.
        ReadOnlySpan<char> span = text;
        int width = GraphemeWidth.StringWidth(text);
        bool truncated = width > maxWidth;
        if (truncated)
        {
            var enumerator = text.GetGraphemeEnumerator();
            width = 0;
            int end = 0;
            while (enumerator.MoveNext())
            {
                int next = width + GraphemeWidth.ClusterWidth(enumerator.Current);
                if (next > maxWidth - 1)
                    break;
                width = next;
                end = enumerator.ElementIndex + enumerator.Current.Length;
            }
            span = text.AsSpan(0, end);
        }

        if (format.Foreground is { } foreground)
        {
            context.DrawText(x, y, span, foreground, null, style);
            if (truncated)
                context.DrawText(x + width, y, "…", foreground, null, style);
        }
        else
        {
            context.DrawText(x, y, span, TextBrush!, null, style);
            if (truncated)
                context.DrawText(x + width, y, "…", TextBrush!, null, style);
        }
    }

    /// <summary>The `█░` fill/track run after a data-bar cell's value (glyph shape carries the value in NoColor — §4).</summary>
    private void DrawDataBar(RenderContext context, int x, int y, int width, double fraction)
    {
        if (width < 1)
            return;

        width = Math.Min(width, MaxBarCells);
        int fill = (int)Math.Round(Math.Clamp(fraction, 0, 1) * width);
        if (fill > 0 && DataBarFillBrush is { } fillBrush)
            context.DrawText(x, y, BarFillGlyphs.AsSpan(0, fill), fillBrush);
        if (fill < width && DataBarTrackBrush is { } trackBrush)
            context.DrawText(x + fill, y, BarTrackGlyphs.AsSpan(0, width - fill), trackBrush);
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

    private Control? _editor;
    private DataGridEditorKind _editorKind;
    private int _editViewIndex = -1;
    private int _editColumnIndex = -1;
    private bool _editorErrorFlagged;

    /// <summary>Whether an editor is hosted (the grid's key routing branches on it).</summary>
    internal bool IsEditing => _editor is not null;

    /// <summary>The hosted editor's resolved kind (never <see cref="DataGridEditorKind.Auto"/> while editing).</summary>
    internal DataGridEditorKind EditorKind => _editorKind;

    /// <summary>The hosted editor element (tests + the grid's preview-key routing).</summary>
    internal Control? EditorElement => _editor;

    /// <summary>The edited (viewIndex, columnIndex) while editing.</summary>
    internal (int ViewIndex, int ColumnIndex) EditCell => (_editViewIndex, _editColumnIndex);

    /// <summary>
    /// Whether a drop-down editor's list is open. The grid's tunnel intercept keys off this: a
    /// CLOSED drop-down editor must yield Enter/Esc to the edit contract (commit/cancel), an open
    /// one owns them (Enter = pick, Esc = close) — see <c>DataGrid.OnPreviewKeyDown</c>.
    /// </summary>
    internal bool IsEditorDropDownOpen => _editor switch
    {
        Cursorial.UI.Controls.ComboBox combo => combo.IsDropDownOpen,
        Cursorial.UI.Controls.DatePicker picker => picker.IsDropDownOpen,
        _ => false,
    };

    /// <summary>
    /// Hosts one editor element at a cell, keyed by <paramref name="kind"/> (the generalized §3.2
    /// host — the v1 TextBox path plus the combo/date/spin suite): the presenter adopts the element
    /// as a visual/logical child, arranges it at the cell's CONTENT rect (it scrolls with the band
    /// naturally), and focuses it via the parked-work idiom. The drawn cell underneath is painted
    /// over by the editor's own background. <paramref name="comboItems"/> feeds the Combo kind only.
    /// </summary>
    internal void BeginEdit(int viewIndex, int columnIndex, DataGridEditorKind kind, string initialText,
                            IReadOnlyList<string>? comboItems = null)
    {
        EndEditVisual();

        Control editor = kind switch
        {
            DataGridEditorKind.Combo => CreateComboEditor(initialText, comboItems ?? []),
            DataGridEditorKind.Date => CreateDateEditor(initialText),
            // Text and Spin share the TextBox face — Spin adds Up/Down stepping (the grid's editing
            // key branch calls SpinBy; the ▲▼ affordance is drawn by Render beside the editor).
            _ => CreateTextEditor(initialText),
        };

        _editor = editor;
        _editorKind = kind;
        _editViewIndex = viewIndex;
        _editColumnIndex = columnIndex;

        // Pin the editor to its cell slot: a control theme's own minimum (the TextBox face wants
        // ~12 cells) would otherwise inflate DesiredSize past the slot, and arrange grows an
        // element to its desired size — the editor would paint over the neighboring cells (and
        // the Spin suffix). Min beats Max in the LD1 clamp order, so the theme's MinWidth must be
        // locally overridden alongside the cap; the slot is stable for the edit session.
        if (columnIndex >= 0 && columnIndex < ColumnLayout.Entries.Count)
        {
            int slot = EditorWidth(ColumnLayout.Entries[columnIndex]);
            editor.SetValue(MinWidthProperty, 1);
            editor.SetValue(MaxWidthProperty, slot);
        }

        AdoptChild(editor, index: -1);
        InvalidateMeasure();
        _owner?.NotifyEditingChanged();

        // Focus after the editor materializes (measure/arrange run first) — the parked-work idiom.
        UIApplication.Current?.Dispatcher.Post(() =>
        {
            if (ReferenceEquals(_editor, editor))
            {
                editor.Focus(Cursorial.UI.Input.FocusNavigationMethod.Programmatic);
                (editor as Cursorial.UI.Controls.TextBox)?.SelectAll();
            }
        });
    }

    private Cursorial.UI.Controls.TextBox CreateTextEditor(string initialText)
    {
        var editor = new Cursorial.UI.Controls.TextBox { Text = initialText };
        // The ed-err recovery contract: the danger ink clears on the NEXT text change (§3.2).
        editor.AddHandler(Cursorial.UI.Controls.TextBox.TextChangedEvent, (_, _) => ClearEditorError());
        return editor;
    }

    private Cursorial.UI.Controls.ComboBox CreateComboEditor(string initialText, IReadOnlyList<string> items)
    {
        var combo = new Cursorial.UI.Controls.ComboBox { ItemsSource = items };
        // Preset to the current formatted value (the mockup's selected "East"); no match ⇒ no
        // selection, and commit refuses until the user picks (TryGetEditorText's Combo contract).
        foreach (var item in items)
        {
            if (string.Equals(item, initialText, StringComparison.Ordinal))
            {
                combo.SelectedItem = item;
                break;
            }
        }
        combo.SelectionChanged += (_, _) => ClearEditorError();
        return combo;
    }

    private Cursorial.UI.Controls.DatePicker CreateDateEditor(string initialText)
    {
        // Editable: the face is a typed-text box + the calendar drop-down (the mockup's ed-date).
        var picker = new Cursorial.UI.Controls.DatePicker { IsEditable = true };
        // SelectedDate preset by parsing the current cell text — DateOnly first, then DateTime
        // (a DateTime-keyed column formats through its own formatter; FromDateTime folds it).
        if (DateOnly.TryParse(initialText, System.Globalization.CultureInfo.CurrentCulture,
                              System.Globalization.DateTimeStyles.None, out var date))
        {
            picker.SelectedDate = date;
        }
        else if (DateTime.TryParse(initialText, System.Globalization.CultureInfo.CurrentCulture,
                                   System.Globalization.DateTimeStyles.None, out var dateTime))
        {
            picker.SelectedDate = DateOnly.FromDateTime(dateTime);
        }
        picker.SelectedDateChanged += (_, _) => ClearEditorError();
        return picker;
    }

    /// <summary>
    /// The editor's committed text, per kind (the one seam <c>DataGrid.CommitEdit</c> reads —
    /// every kind funnels into <see cref="Shaping.DataViewController.TrySetCellFromText"/>'s text
    /// lane so parse/validation semantics stay identical across editors): TextBox = its text;
    /// Combo = the selected item (false while nothing is selected — nothing to commit); Date = the
    /// editable draft box's text (the typed text OR the culture "d" push from a calendar pick —
    /// reading the box, not <c>SelectedDate</c>, preserves a draft the picker hasn't parsed yet),
    /// falling back to the selected date's round-trip when the box hasn't templated.
    /// </summary>
    internal bool TryGetEditorText([NotNullWhen(true)] out string? text)
    {
        switch (_editor)
        {
            case Cursorial.UI.Controls.TextBox box:
                text = box.Text;
                return true;

            case Cursorial.UI.Controls.ComboBox combo:
                text = combo.SelectedItem as string;
                return text is not null;

            case Cursorial.UI.Controls.DatePicker picker:
                text = FindDescendant<Cursorial.UI.Controls.TextBox>(picker)?.Text
                       ?? picker.SelectedDate?.ToString("d", System.Globalization.CultureInfo.CurrentCulture)
                       ?? string.Empty;
                return true;

            default:
                text = null;
                return false;
        }
    }

    /// <summary>
    /// The Spin kind's Up/Down stepping (the grid's editing key branch routes here): the numeric
    /// text steps by <paramref name="delta"/> (±1, Shift ±10) and re-selects for a typed replace.
    /// Non-numeric residue is left alone (stepping must never destroy text the user is fixing).
    /// </summary>
    internal void SpinBy(decimal delta)
    {
        if (_editorKind != DataGridEditorKind.Spin || _editor is not Cursorial.UI.Controls.TextBox box)
            return;

        string current = box.Text.Trim();
        decimal value = 0m;
        if (current.Length != 0 &&
            !decimal.TryParse(current, System.Globalization.NumberStyles.Number,
                              System.Globalization.CultureInfo.CurrentCulture, out value))
        {
            return;
        }

        box.Text = (value + delta).ToString(System.Globalization.CultureInfo.CurrentCulture);
        box.SelectAll();
    }

    /// <summary>
    /// Flips the hosted editor into the error look (the mockup's <c>ed-err</c> idiom): the ink goes
    /// through the theme's <see cref="Cursorial.UI.Themes.ThemeKeys.DangerBrush"/> via a live
    /// resource reference (palette flips keep tracking). Cleared on the next text/selection change
    /// (the per-kind change hooks) — the commit path calls this when the parse fails and the
    /// editor stays open for correction (§3.2).
    /// </summary>
    internal void FlagEditorError()
    {
        if (_editor is not { } editor)
            return;
        _editorErrorFlagged = true;
        editor.SetResourceReference(Control.ForegroundProperty, Cursorial.UI.Themes.ThemeKeys.DangerBrush);
    }

    private void ClearEditorError()
    {
        if (!_editorErrorFlagged || _editor is not { } editor)
            return;
        _editorErrorFlagged = false;
        editor.ClearValue(Control.ForegroundProperty); // back to the inherited/themed resting ink
    }

    private static T? FindDescendant<T>(UIElement root) where T : UIElement
    {
        for (int i = 0; i < root.VisualChildrenCount; i++)
        {
            var child = root.GetVisualChild(i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } nested)
                return nested;
        }
        return null;
    }

    /// <summary>Tears the editor down and returns focus to the grid.</summary>
    internal void EndEditVisual()
    {
        if (_editor is null)
            return;
        DisownChild(_editor);
        _editor = null;
        _editorKind = DataGridEditorKind.Auto;
        _editorErrorFlagged = false;
        _editViewIndex = -1;
        _editColumnIndex = -1;
        InvalidateMeasure();
        InvalidateVisual();
        _owner?.NotifyEditingChanged();
        _owner?.Focus(Cursorial.UI.Input.FocusNavigationMethod.Programmatic);
    }

    /// <summary>
    /// The editor's arranged width inside its cell: the Spin kind reserves a 3-cell suffix for the
    /// drawn <c>▲▼</c> affordance when the cell is wide enough (the mockup's spinbtns; skipped in
    /// cramped cells — stepping still works, only the hint is elided).
    /// </summary>
    private int EditorWidth(in DataGridColumnLayout.Entry entry)
        => _editorKind == DataGridEditorKind.Spin && entry.Width >= 6
            ? entry.Width - 3
            : Math.Max(1, entry.Width);

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The editor arranges at its cell's content rect (content coords == local coords). Do NOT
        // chain base.ArrangeOverride: the UIElement default arranges EVERY visual child to the
        // full finalSize, which would re-arrange the editor over the whole presenter — its
        // background then blanks the drawn rows (the latent v1 bug this stage surfaced; the host's
        // children are exclusively hosted editors, so self-owned arrangement is total here).
        if (_editor is not null && _editColumnIndex >= 0 && _editColumnIndex < ColumnLayout.Entries.Count)
        {
            var entry = ColumnLayout.Entries[_editColumnIndex];
            _editor.Arrange(new Rect(entry.X + DataGridColumnLayout.CellPadding, _editViewIndex,
                                     EditorWidth(entry), 1));
        }
        return finalSize;
    }

    // ── Hit testing + mouse (the single hit leaf — §3.2) ─────────────────────────────────────────

    /// <summary>Maps a content position to (viewIndex, columnEntryIndex, isExpander).</summary>
    internal (int ViewIndex, int ColumnIndex, bool OnExpander) HitCell(int x, int y)
    {
        var owner = _owner;
        if (owner is null || y < 0 || y >= owner.Snapshot.Count)
        {
            // The new-row template is clickable like a data row (its view index is one PAST the
            // snapshot — every snapshot read above stays guarded).
            if (owner is not null && y >= 0 && y == NewRowViewIndex)
                return (y, ColumnLayout.EntryAt(x), false);
            return (-1, -1, false);
        }

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
