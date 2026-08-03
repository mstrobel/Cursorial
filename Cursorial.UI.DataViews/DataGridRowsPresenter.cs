using System.Diagnostics.CodeAnalysis;

using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

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
    public static readonly StyledProperty<IBrush?> RowBackgroundProperty =
        UIProperty.Register<DataGridRowsPresenter, IBrush?>(
            nameof(RowBackground),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.ElevationWell });

    public static readonly StyledProperty<IBrush?> RowAlternationBackgroundProperty =
        UIProperty.Register<DataGridRowsPresenter, IBrush?>(
            nameof(RowAlternationBackground),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.AlternateRowBrush });

    public static readonly StyledProperty<IBrush?> RowAlternationForegroundProperty =
        UIProperty.Register<DataGridRowsPresenter, IBrush?>(
            nameof(RowAlternationForeground),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.AlternateRowInk });

    public static readonly StyledProperty<IBrush?> SelectionBackgroundProperty =
        UIProperty.Register<DataGridRowsPresenter, IBrush?>(
            nameof(SelectionBackground),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.SelectionActiveBrush });

    public static readonly StyledProperty<IBrush?> SelectionForegroundProperty =
        UIProperty.Register<DataGridRowsPresenter, IBrush?>(
            nameof(SelectionForeground),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.SelectionInk });

    public static readonly StyledProperty<IBrush?> SelectionInactiveBackgroundProperty =
        UIProperty.Register<DataGridRowsPresenter, IBrush?>(
            nameof(SelectionInactiveBackground),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.SelectionInactiveBrush });

    public static readonly StyledProperty<IBrush?> HoverBackgroundProperty =
        UIProperty.Register<DataGridRowsPresenter, IBrush?>(
            nameof(HoverBackground),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.HoverBrush });

    public static readonly StyledProperty<IBrush?> HoverForegroundProperty =
        UIProperty.Register<DataGridRowsPresenter, IBrush?>(
            nameof(HoverForeground),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.OnHoverBrush });

    public static readonly StyledProperty<IBrush?> GroupRowBackgroundProperty =
        UIProperty.Register<DataGridRowsPresenter, IBrush?>(
            nameof(GroupRowBackground),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.SurfaceBrush });

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        UIProperty.Register<DataGridRowsPresenter, IBrush?>(
            nameof(TextBrush),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.TextBrush });

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        UIProperty.Register<DataGridRowsPresenter, IBrush?>(
            nameof(AccentBrush),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.AccentBrush });

    public static readonly StyledProperty<IBrush?> FocusCellBackgroundProperty =
        UIProperty.Register<DataGridRowsPresenter, IBrush?>(
            nameof(FocusCellBackground),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.WellBrush });

    public static readonly StyledProperty<IBrush?> FocusCellForegroundProperty =
        UIProperty.Register<DataGridRowsPresenter, IBrush?>(
            nameof(FocusCellForeground),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.TextBrush });

    public static readonly StyledProperty<IBrush?> DataBarFillBrushProperty =
        UIProperty.Register<DataGridRowsPresenter, IBrush?>(
            nameof(DataBarFillBrush),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.PurpleBrush });

    public static readonly StyledProperty<IBrush?> DataBarTrackBrushProperty =
        UIProperty.Register<DataGridRowsPresenter, IBrush?>(
            nameof(DataBarTrackBrush),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.ProgressTrackBrush });

    /// <summary>Ghost ink for the new-row template (the muted * indicator + per-column placeholders — §3.2).</summary>
    public static readonly StyledProperty<IBrush?> MutedBrushProperty =
        UIProperty.Register<DataGridRowsPresenter, IBrush?>(
            nameof(MutedBrush),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.MutedBrush });

    static DataGridRowsPresenter()
    {
        AffectsRender<DataGridRowsPresenter>(
            RowBackgroundProperty, RowAlternationBackgroundProperty, SelectionBackgroundProperty,
            HoverBackgroundProperty, GroupRowBackgroundProperty, TextBrushProperty, AccentBrushProperty,
            FocusCellBackgroundProperty, DataBarFillBrushProperty, DataBarTrackBrushProperty,
            MutedBrushProperty);
    }

    public IBrush? RowBackground
    {
        get => GetValue(RowBackgroundProperty);
        set => SetValue(RowBackgroundProperty, value);
    }

    public IBrush? RowAlternationBackground
    {
        get => GetValue(RowAlternationBackgroundProperty);
        set => SetValue(RowAlternationBackgroundProperty, value);
    }

    public IBrush? RowAlternationForeground
    {
        get => GetValue(RowAlternationForegroundProperty);
        set => SetValue(RowAlternationForegroundProperty, value);
    }

    public IBrush? SelectionBackground
    {
        get => GetValue(SelectionBackgroundProperty);
        set => SetValue(SelectionBackgroundProperty, value);
    }

    public IBrush? SelectionForeground
    {
        get => GetValue(SelectionForegroundProperty);
        set => SetValue(SelectionForegroundProperty, value);
    }

    public IBrush? SelectionInactiveBackground
    {
        get => GetValue(SelectionInactiveBackgroundProperty);
        set => SetValue(SelectionInactiveBackgroundProperty, value);
    }

    public IBrush? HoverBackground
    {
        get => GetValue(HoverBackgroundProperty);
        set => SetValue(HoverBackgroundProperty, value);
    }

    public IBrush? HoverForeground
    {
        get => GetValue(HoverForegroundProperty);
        set => SetValue(HoverForegroundProperty, value);
    }

    public IBrush? GroupRowBackground
    {
        get => GetValue(GroupRowBackgroundProperty);
        set => SetValue(GroupRowBackgroundProperty, value);
    }

    public IBrush? TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public IBrush? AccentBrush
    {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public IBrush? FocusCellBackground
    {
        get => GetValue(FocusCellBackgroundProperty);
        set => SetValue(FocusCellBackgroundProperty, value);
    }

    public IBrush? FocusCellForeground
    {
        get => GetValue(FocusCellForegroundProperty);
        set => SetValue(FocusCellForegroundProperty, value);
    }

    public IBrush? DataBarFillBrush
    {
        get => GetValue(DataBarFillBrushProperty);
        set => SetValue(DataBarFillBrushProperty, value);
    }

    public IBrush? DataBarTrackBrush
    {
        get => GetValue(DataBarTrackBrushProperty);
        set => SetValue(DataBarTrackBrushProperty, value);
    }

    public IBrush? MutedBrush
    {
        get => GetValue(MutedBrushProperty);
        set => SetValue(MutedBrushProperty, value);
    }

    private DataGrid? _owner;
    private Size _viewport;
    private bool _bandDirty = true;
    // §9.4/§10.1 — every cell range's view rect, refilled once per render pass (a reused buffer, so
    // steady-state paints allocate nothing); a cell is selected when ANY rect contains it.
    private readonly List<(int FirstRow, int LastRow, int FirstColumn, int LastColumn)> _renderCellRanges = [];

    // The band cache (§3.2): one entry per cached view row. Data cells are (start, length) RUNS
    // into the band-shared pooled char buffer (§9.6 — the span-formatter lane; zero per-cell
    // strings per fill), sliced back out by CellText at paint.
    private readonly List<CachedRow> _band = [];
    private int _bandStart;
    private int _snapshotVersion = -1;
    private char[] _cellChars = new char[1024]; // the pooled cell-text buffer (reused across fills)
    private int _cellCharsUsed;

    /// <summary>One formatted cell's slice of the pooled band buffer (§9.6).</summary>
    private readonly record struct CellRun(int Start, int Length);

    private readonly struct CachedRow
    {
        public required bool IsGroup { get; init; }
        public required int RowId { get; init; }
        public required int GroupNodeIndex { get; init; }
        public required int Level { get; init; }
        public required CellRun[] Cells { get; init; } // per visible column (empty for group rows)
        public required string GroupCaption { get; init; }
        public required string GroupSummary { get; init; }                    // the concatenated banner string
        public string[]? GroupSummaries { get; init; }                        // per-summary cells (parallel to SummaryDescriptions) — the in-column lane
        public required bool GroupCollapsed { get; init; }

        // Conditional-formatting verdicts, evaluated at BAND-FILL time (§2.7 — never at paint).
        // The no-rules fast path shares the static empties (zero per-row cost).
        public CellFormat[] CellFormats { get; init; }
        public double[] BarFractions { get; init; }
        public CellFormat RowFormat { get; init; }
    }

    private static readonly CellFormat[] NoFormats = [];
    private static readonly double[] NoFractions = [];
    private static readonly CellRun[] NoCells = [];

    // Format-background fills reuse one brush per color (the rule palette is tiny; the paint path
    // re-inks per hover move and must not allocate per cell).
    private readonly Dictionary<Color, SolidColorBrush> _formatBrushes = [];

    // The per-column data-bar text reserve (live-canary fix: the bar used to start right after
    // EACH row's value text, so the track origin and width shifted with the number's character
    // count — a per-row scale). The reserve is the band's widest bar-cell value per column: every
    // bar shares ONE origin and ONE track width, so equal fractions render equal bars. Recomputed
    // per band fill; 0 = the column has no bars this band.
    private int[] _barReserve = [];

    // The per-column data-bar ICON reserve (§10.7): a bar cell used to suppress its verdict icon
    // because a per-row icon would shift the column-uniform bar track. The reserve is the widest
    // icon across the column's bar cells — reserved UNIFORMLY for every bar cell in the column (a
    // row without an icon leaves the slot blank), so an icon + bar coexist with the track origin
    // still column-uniform. 0 = no bar cell in the column carries an icon this band.
    private int[] _barIconReserve = [];

    private IBrush BrushFor(Color color)
    {
        if (!_formatBrushes.TryGetValue(color, out var brush))
            _formatBrushes[color] = brush = new SolidColorBrush(color);

        return brush;
    }

    /// <summary>A cached cell's text, sliced from the pooled buffer (§9.6).</summary>
    private ReadOnlySpan<char> CellText(in CachedRow row, int c)
    {
        var run = row.Cells[c];
        return _cellChars.AsSpan(run.Start, run.Length);
    }

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
            {
                _owner.SnapshotChanged -= OnSnapshotChanged;
                _owner.GotFocus -= OnOwnerFocusChanged;
                _owner.LostFocus -= OnOwnerFocusChanged;
            }

            _owner = value;

            if (_owner is not null)
            {
                _owner.SnapshotChanged += OnSnapshotChanged;
                _owner.GotFocus += OnOwnerFocusChanged;
                _owner.LostFocus += OnOwnerFocusChanged;
            }

            InvalidateBand();
        }
    }

    /// <summary>The resolved column geometry (computed here per measure; the other band presenters read it).</summary>
    internal DataGridColumnLayout ColumnLayout { get; } = new();

    /// <summary>The hover view row (presenter state — drawn rows have no elements to flip pseudo-classes on).</summary>
    internal int HoverViewIndex { get; private set; } = -1;

    private void OnSnapshotChanged(object? sender, EventArgs e) => InvalidateBand();

    private void OnOwnerFocusChanged(object? sender, FocusChangedEventArgs focusChangedEventArgs) => InvalidateBand();

    /// <summary>Marks the band cache stale (data/shape/selection change) and schedules re-fill + re-ink.</summary>
    internal void InvalidateBand()
    {
        _bandDirty = true;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// An H-offset tick (§9.2): re-measure (hosted children — the cell editor, detail hosts —
    /// arrange at shifted x, and the band fill's early-out makes the re-measure a no-op) and
    /// re-ink. NEVER dirties the band cache — per-row strings are offset-independent.
    /// </summary>
    internal void OnHorizontalOffsetChanged()
    {
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

    // The horizontal axis is presenter-owned (§9.2): the SCP scrolls vertically only, so the
    // published horizontal extent IS the viewport (the host's obligation — the SCP publishes
    // GetExtent verbatim on both axes) and the grid's HorizontalOffset shifts the painters.
    // Vertically the extent is rows + Σ expanded-detail heights (§9.3).
    public Size GetExtent()
        => new(Math.Max(_viewport.Columns, 1),
               Math.Max(ItemCount + _detailHeightSum, 1));

    /// <summary>The grid's one horizontal truth as the painters consume it (0 without an owner).</summary>
    private int HOffset => _owner?.HorizontalOffset ?? 0;

    /// <summary>The arranged viewport width in cells (the grid's clamp ceiling input — §9.2).</summary>
    internal int ViewportColumns => _viewport.Columns;

    /// <summary>
    /// A column entry's painted x (§9.2): frozen entries sit at their unshifted content x; scrolling
    /// entries shift by the grid offset (they slide UNDER the frozen region, which repaints last).
    /// </summary>
    internal int DrawXOf(int entryIndex)
    {
        var entry = ColumnLayout.Entries[entryIndex];
        return entryIndex < ColumnLayout.FrozenCount ? entry.X : entry.X - HOffset;
    }

    /// <summary>Whether an entry's slot intersects the visible viewport band (column virtualization — §9.2).</summary>
    private bool IsEntryVisible(int entryIndex)
    {
        var entry = ColumnLayout.Entries[entryIndex];
        int drawX = DrawXOf(entryIndex);
        int slot = entry.Width + 2 * DataGridColumnLayout.CellPadding;
        // A scrolling entry fully under the frozen region (or scrolled off left) is skipped; the
        // straddling one still draws — the frozen pass overpaints the overlap (no clip stack).
        int leftEdge = entryIndex < ColumnLayout.FrozenCount ? 0 : ColumnLayout.FrozenWidth;
        return drawX + slot > leftEdge && drawX < Math.Max(_viewport.Columns, 1);
    }

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

    public int EstimateItemAt(int offsetRow) => ViewIndexAtY(offsetRow).ViewIndex;

    public Rect BringItemIntoView(int itemIndex)
        => new(0, ContentYOf(itemIndex), Math.Max(1, _viewport.Columns), 1);

    // ── Master-detail (§9.3 — the content-y map; presenter-side geometry over a 1-row engine) ────

    /// <summary>One expanded pane's geometry for the current pass (rebuilt per measure; the
    /// realized element lives in <see cref="_detailElements"/> so it survives map rebuilds).</summary>
    private sealed class DetailPane
    {
        public required int RowId { get; init; }
        public required int ViewIndex { get; init; }
        public int YStart; // content y of the pane's first row (anchor row's y + 1)
        public int Height; // measured, or the 1-row estimate until first measure (VSP refinement)
    }

    private readonly List<DetailPane> _details = [];                  // sorted by ViewIndex
    private readonly Dictionary<int, UIElement> _detailElements = []; // rowId → realized pane
    private readonly Dictionary<int, int> _detailHeights = [];        // rowId → last measured height
    private int _detailHeightSum;

    /// <summary>Whether master-detail is active this pass (the gutter + map engage).</summary>
    private bool DetailsActive => _owner?.DetailTemplate is not null;

    /// <summary>
    /// Re-derives the pane geometry from the grid's expansion set against the CURRENT snapshot
    /// (§9.3): view indices re-resolve through the grid's per-publish inverse map, panes sort by
    /// view position, and the prefix sums fix each pane's content-y start. Heights come from the
    /// per-row memory (1-row estimate before first measure — refined by <see cref="RealizeDetails"/>).
    /// </summary>
    private void RebuildDetailMap()
    {
        _details.Clear();
        int sum = 0;
        var owner = _owner;

        if (owner?.DetailTemplate is not null && owner.ExpandedDetails.Count > 0)
        {
            foreach (int rowId in owner.ExpandedDetails)
            {
                int view = owner.ViewIndexOfRow(rowId);

                if (view >= 0)
                    _details.Add(new DetailPane
                                 {
                                     RowId = rowId, ViewIndex = view,
                                     Height = _detailHeights.GetValueOrDefault(rowId, 1)
                                 });
            }

            _details.Sort(static (a, b) => a.ViewIndex.CompareTo(b.ViewIndex));

            foreach (var pane in _details)
            {
                pane.YStart = pane.ViewIndex + sum + 1;
                sum += pane.Height;
            }
        }

        _detailHeightSum = sum;
    }

    /// <summary>A view row's content y (§9.3: <c>y = viewIndex + Σ heights(panes above)</c>).</summary>
    internal int ContentYOf(int viewIndex)
    {
        if (_details.Count == 0)
            return viewIndex;

        int sum = 0;

        foreach (var pane in _details)
        {
            if (pane.ViewIndex >= viewIndex)
                break;

            sum += pane.Height;
        }

        return viewIndex + sum;
    }

    /// <summary>The inverse map (§9.3): a content y inside a pane reports its ANCHOR row + the flag.</summary>
    internal (int ViewIndex, bool InDetail) ViewIndexAtY(int y)
    {
        if (_details.Count == 0)
            return (y, false);

        int offset = 0;

        foreach (var pane in _details)
        {
            if (y < pane.YStart)
                break;

            if (y < pane.YStart + pane.Height)
                return (pane.ViewIndex, true);

            offset += pane.Height;
        }

        return (y - offset, false);
    }

    /// <summary>
    /// Realizes panes whose y-range intersects the band (± the band's own padding — the §9.3
    /// tall-detail predicate: an anchor row outside the band with its pane inside is the common
    /// case) and releases the rest. Measured heights refine the estimates; only an actual Σ delta
    /// republishes the extent (the VSP refinement discipline — convergence under the 16-pass
    /// fixpoint). Runs in MeasureOverride (the sanctioned self-mutation site).
    /// </summary>
    private void RealizeDetails()
    {
        var owner = _owner;

        if (owner is null)
            return;

        var (bandStart, bandLength) = BandWindow();
        int bandEnd = bandStart + bandLength;
        bool geometryChanged = false;

        // Release: no longer expanded/visible, or fully outside the band.
        if (_detailElements.Count > 0)
        {
            List<int>? drop = null;

            foreach (var (rowId, element) in _detailElements)
            {
                bool keep = false;

                foreach (var pane in _details)
                {
                    if (pane.RowId == rowId)
                    {
                        keep = pane.YStart < bandEnd && pane.YStart + pane.Height > bandStart;
                        break;
                    }
                }

                if (!keep)
                {
                    DisownChild(element);
                    (drop ??= []).Add(rowId);
                }
            }

            if (drop is not null)
            {
                foreach (int rowId in drop)
                    _detailElements.Remove(rowId);
            }
        }

        // Realize + measure the intersecting panes (fresh subtree per expansion — the DataTemplate
        // contract; DataContext = the row object, boxed once for value-type rows).
        foreach (var pane in _details)
        {
            if (pane.YStart >= bandEnd || pane.YStart + pane.Height <= bandStart)
                continue;

            if (!_detailElements.TryGetValue(pane.RowId, out var element))
            {
                if (owner.DetailTemplate is not {} template || owner.Controller is not {} controller)
                    continue;

                element = template.Build(controller.GetRowObject(pane.RowId));
                _detailElements[pane.RowId] = element;
                AdoptChild(element, index: -1);
            }

            element.Measure(new Size(Math.Max(1, _viewport.Columns), LayoutLimits.MaxScrollExtent));
            int measured = Math.Max(1, element.DesiredSize.Rows);

            if (measured != pane.Height)
            {
                _detailHeights[pane.RowId] = measured;
                geometryChanged = true;
            }
        }

        if (geometryChanged)
        {
            // Re-run the prefix sums with the refined heights, then republish the extent (the
            // height-delta-only republish — §9.3).
            int sum = 0;

            foreach (var pane in _details)
            {
                pane.Height = _detailHeights.GetValueOrDefault(pane.RowId, 1);
                pane.YStart = pane.ViewIndex + sum + 1;
                sum += pane.Height;
            }

            _detailHeightSum = sum;
            ScrollOwner?.InvalidateScrollExtent();

            // Audit W2-7: a refinement moves the content-y map, so the band WINDOW covers a
            // different view-row set than the fill that just ran — re-dirty the band so the
            // fixpoint refills under the corrected map (a shrink otherwise left the rows the
            // shorter map pulled into the window uncached — blank rows until a re-anchor). The
            // next pass re-measures cache-hit panes (measured == Height → no change), so this
            // converges in one extra pass.
            InvalidateBand();
        }

        // Height memory hygiene (audit W2-7 adjunct): a collapsed row's stale height must not
        // become a recycled row id's estimate.
        if (_detailHeights.Count > 0 && owner.ExpandedDetails.Count < _detailHeights.Count)
        {
            List<int>? stale = null;

            foreach (int rowId in _detailHeights.Keys)
            {
                if (!owner.ExpandedDetails.Contains(rowId))
                    (stale ??= []).Add(rowId);
            }

            if (stale is not null)
            {
                foreach (int rowId in stale)
                    _detailHeights.Remove(rowId);
            }
        }
    }

    /// <summary>The pane element holding keyboard focus, or null (the grid's stand-down guard — §9.3).</summary>
    internal UIElement? FocusedDetailElement()
    {
        foreach (var element in _detailElements.Values)
        {
            if (element.IsKeyboardFocusWithin)
                return element;
        }

        return null;
    }

    /// <summary>Ctrl+Down enters the focused row's pane: the first focusable in its subtree (§9.3).</summary>
    internal bool TryFocusDetail(int rowId)
    {
        if (!_detailElements.TryGetValue(rowId, out var element))
            return false;

        if (FindFocusable(element) is {} target)
        {
            target.Focus(FocusNavigationMethod.Programmatic);
            return true;
        }

        return false;
    }

    private static UIElement? FindFocusable(UIElement root)
    {
        if (root is { Focusable: true, IsEffectivelyEnabled: true })
            return root;

        for (int i = 0; i < root.VisualChildrenCount; i++)
        {
            if (FindFocusable(root.GetVisualChild(i)) is {} match)
                return match;
        }

        return null;
    }

    // ── Band fill (measure-time — the VSP-sanctioned self-mutation site) ─────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        RebuildDetailMap(); // §9.3: the content-y map first — the band walk + realization read it
        FillBandCache();
        RealizeDetails();

        // The hosted editor (the §3.2 element-hosting special case) measures at its cell width
        // (minus the Spin kind's drawn ▲▼ suffix reserve).
        if (_editor is not null && _editColumnIndex >= 0 && _editColumnIndex < ColumnLayout.Entries.Count)
            _editor.Measure(new Size(EditorWidth(ColumnLayout.Entries[_editColumnIndex]), 1));

        // The SCP host path measures content at the viewport; the extent publishes via GetExtent.
        return new Size(Math.Min(ColumnLayout.TotalWidth, availableSize.Columns),
                        Math.Min(ItemCount + _detailHeightSum, availableSize.Rows));
    }

    private (int Start, int Length) BandWindow()
    {
        // The SCP's public realization window (§9.6 — the promoted seam, no IVT reach-through; the
        // GetRealizationWindow() accessor bundles the pair). Without an adopting SCP (a bare
        // presenter in tests), the window is the viewport.
        if (ScrollOwner is { } scp)
        {
            var window = scp.GetRealizationWindow();
            return (window.Start, window.Length);
        }
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
        // The band window is CONTENT-y (§9.3) — route it through the inverse map to a VIEW-row
        // window (a y inside a pane resolves to its anchor row, so tall panes keep their anchor
        // row's cells cached while any part of the pane is banded).
        var (windowStart, windowLength) = BandWindow();
        int viewStart = ViewIndexAtY(windowStart).ViewIndex;
        int viewEnd = ViewIndexAtY(windowStart + Math.Max(1, windowLength) - 1).ViewIndex + 1;
        int start = Math.Clamp(viewStart, 0, Math.Max(0, snapshot.Count - 1));
        int length = Math.Min(Math.Max(0, viewEnd - start), snapshot.Count - start);

        if (!_bandDirty && start == _bandStart && _band.Count == length && snapshot.Version == _snapshotVersion)
            return;

        // Resolve columns BEFORE formatting (the cache stores per-visible-column cells) — but Auto
        // widths need the formatted content, so: format first into the cache, then resolve widths
        // from it (the band-limited Auto contract, §1). Fixed columns partition to the FRONT
        // (§9.2 — the frozen region leads the layout regardless of declaration order); the stable
        // two-pass keeps each partition in collection order.
        var columns = new List<DataGridColumn>(owner.Columns.Count);

        foreach (var column in owner.Columns)
        {
            if (column is { Visible: true, Fixed: DataGridColumnFixed.Left })
                columns.Add(column);
        }

        foreach (var column in owner.Columns)
        {
            if (column.Visible && column.Fixed != DataGridColumnFixed.Left)
                columns.Add(column);
        }

        _band.Clear();
        _cellCharsUsed = 0; // the pooled buffer resets per fill (§9.6 — runs never outlive the band)

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
                              Cells = NoCells,
                              GroupCaption = $"{caption} ({node.RowCount})",
                              GroupSummary = summary,
                              GroupSummaries = node.Summaries,
                              GroupCollapsed = node.IsCollapsed,
                              CellFormats = NoFormats,
                              BarFractions = NoFractions,
                          });
            }
            else
            {
                var cells = new CellRun[columns.Count];
                var formats = hasRules ? new CellFormat[columns.Count] : NoFormats;
                var fractions = hasRules ? new double[columns.Count] : NoFractions;

                for (int c = 0; c < columns.Count; c++)
                {
                    // §9.6: format into the pooled buffer through the span lane (−1 = grow + retry).
                    int written = 0;

                    while (controller is not null &&
                           (written = controller.FormatCellTo(row.RowId, columns[c],
                                                              _cellChars.AsSpan(_cellCharsUsed))) < 0)
                    {
                        Array.Resize(ref _cellChars, _cellChars.Length * 2);
                    }

                    cells[c] = new CellRun(_cellCharsUsed, written);
                    _cellCharsUsed += written;

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
        // The §9.3 expander gutter is a synthetic leading region (2 cells when master-detail is on).
        ColumnLayout.Resolve(columns, Math.Max(1, _viewport.Columns), gutterWidth: DetailsActive ? 2 : 0,
                             autoWidth: column =>
                                        {
                                            int index = columns.IndexOf(column);
                                            int widest = 0;

                                            foreach (var cached in _band)
                                            {
                                                if (!cached.IsGroup && index < cached.Cells.Length)
                                                    widest = Math.Max(
                                                        widest, GraphemeWidth.StringWidth(CellText(cached, index)));
                                            }

                                            return widest;
                                        });

        // The data-bar text reserve: per bar-bearing column, the widest bar-cell value in the band
        // (uniform bar origin/track — equal fractions must render equal bars; see _barReserve).
        if (_barReserve.Length < columns.Count)
            _barReserve = new int[columns.Count];
        if (_barIconReserve.Length < columns.Count)
            _barIconReserve = new int[columns.Count];
        Array.Clear(_barReserve, 0, _barReserve.Length);
        Array.Clear(_barIconReserve, 0, _barIconReserve.Length);
        if (hasRules)
        {
            foreach (var cached in _band)
            {
                if (cached.IsGroup)
                    continue;

                for (int c = 0; c < columns.Count && c < cached.BarFractions.Length; c++)
                {
                    if (double.IsNaN(cached.BarFractions[c]))
                        continue;
                    _barReserve[c] = Math.Max(_barReserve[c], GraphemeWidth.StringWidth(CellText(cached, c)));
                    // A bar cell that also carries a verdict icon widens the column's icon reserve
                    // (uniform across the column so the bar origin stays fixed).
                    var overlaid = (c < cached.CellFormats.Length ? cached.CellFormats[c] : default).OverlayOn(cached.RowFormat);
                    if (overlaid.Icon is { } icon)
                        _barIconReserve[c] = Math.Max(_barIconReserve[c], GraphemeWidth.StringWidth(icon) + 1);
                }
            }
        }

        // §9.2: the resolved geometry may have shrunk under the current H offset — the grid
        // re-clamps and refreshes its bar (the end-of-arrange re-coercion analog).
        owner.OnColumnGeometryResolved();
    }

    private static string HeaderOf(DataGrid owner, GroupNode node)
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
        // NoColor tier: selection/focus background fills resolve to Default (invisible), so the
        // direct-drawn cells carry the cue as reverse-video (selection/range/focus) + bold (the focus
        // cell) — the framework's `.caps-nocolor` Inverse styles never reach direct-draw (§4).
        bool noColor = context.Capabilities.Color.Depth == ColorDepth.NoColor;
        var entries = ColumnLayout.Entries;
        int frozenCount = ColumnLayout.FrozenCount;
        int frozenWidth = ColumnLayout.FrozenWidth;
        int viewWidth = Math.Max(_viewport.Columns, 1);

        int gutterWidth = ColumnLayout.GutterWidth;
        owner.CollectCellRangeViewRects(_renderCellRanges); // §9.4/§10.1 — every rect, derived once per pass
        for (int i = 0; i < _band.Count; i++)
        {
            int view = _bandStart + i; // view-row space (focus/hover/striping)
            int y = ContentYOf(view);  // content-y (§9.3 — panes push rows down)
            var row = _band[i];

            // Background lanes: group tint > selection > hover > alternation.
            IBrush? background = row.IsGroup
                                     ? GroupRowBackground
                                     : selection is not null && selection.IsSelected(row.RowId)
                                         ? owner is { IsKeyboardFocusWithin: true, FocusBand: DataGridFocusBand.Rows } 
                                               ? SelectionBackground
                                               : SelectionInactiveBackground
                                         : HoverViewIndex == view
                                             ? HoverBackground
                                             : (view & 1) == 1
                                                 ? RowAlternationBackground
                                                 : RowBackground;

            if (background is not null)
                context.FillOpaque(new Rect(0, y, viewWidth, 1), background);

            if (row.IsGroup)
            {
                // ▾/▸ expander, indent by level, caption, then the summary. Group rows are
                // VIEWPORT-anchored for the caption/banner (never shifted — reads at any scroll
                // position; §9.2). NoColor tier: the accent/tint resolves to Default, so the banner
                // wears Bold to stand out (design §4).
                int x = row.Level * 2;
                string glyph = row.GroupCollapsed ? "▸" : "▾";
                CellStyle groupStyle = noColor ? default(CellStyle).WithAttributes(TextAttributes.Bold) : default;
                var groupBrush = AccentBrush ?? TextBrush ?? Brushes.Default;
                context.DrawText(x, y, glyph, groupBrush, null, groupStyle);
                DrawClipped(context, x + 2, y, row.GroupCaption, int.MaxValue, TextBrush ?? Brushes.Default, groupStyle);

                if (owner.GroupSummaryDisplay == GroupSummaryDisplay.InColumn && row.GroupSummaries is { Length: > 0 } groupSummaries)
                {
                    // Each per-group summary aligns UNDER its column (like the footer), scrolling
                    // with the columns via DrawXOf — not one right-aligned banner string. Multiple
                    // summaries on one column join (a group row is a single line; the footer stacks).
                    // The caption still rides the left; a summary on the group-by column overpaints it
                    // (author group-by columns leftmost, aggregates on the columns to their right).
                    var descs = owner.SummaryDescriptions;

                    for (int e = 0; e < entries.Count; e++)
                    {
                        string? combined = null;

                        for (int s = 0; s < descs.Count && s < groupSummaries.Length; s++)
                        {
                            if (groupSummaries[s].Length == 0 || !ReferenceEquals(descs[s].ColumnKey, entries[e].Column))
                                continue;

                            // Prefix the aggregate glyph (Σ/x̄/⌄/⌃/#) — same label the footer draws, so
                            // the two present identically and stacked aggregates on one column disambiguate.
                            string labeled = $"{DataGridSummaryPresenter.AggregateLabel(descs[s].Aggregate)} {groupSummaries[s]}";
                            combined = combined is null ? labeled : $"{combined}  {labeled}";
                        }

                        if (combined is null)
                            continue;

                        int drawBase = DrawXOf(e);
                        int cellX = drawBase + DataGridColumnLayout.CellPadding;
                        int valWidth = GraphemeWidth.StringWidth(combined);
                        int drawX = valWidth < entries[e].Width ? cellX + entries[e].Width - valWidth : cellX;
                        DrawClipped(context, drawX, y, combined, entries[e].Width, groupBrush, groupStyle);
                    }
                }
                else if (row.GroupSummary.Length > 0)
                {
                    int width = GraphemeWidth.StringWidth(row.GroupSummary);
                    int summaryX = Math.Max(x + 2, viewWidth - width - 1);

                    DrawClipped(context, summaryX, y, row.GroupSummary, int.MaxValue, groupBrush, groupStyle);
                }

                continue;
            }

            // §9.2 paint order: scrolling cells first (shifted — a straddler slides UNDER the
            // frozen boundary), then the frozen region (gutter + fixed columns) re-fills its
            // background and draws its content on top (overpaint instead of a clip stack).
            bool rowSelected = selection is not null && selection.IsSelected(row.RowId);

            for (int c = frozenCount; c < entries.Count && c < row.Cells.Length; c++)
                DrawDataCell(context, row, c, view, y, focusRow, focusColumn, rowSelected, noColor);

            if (frozenWidth > 0)
            {
                var erase = background ?? owner.Background;

                if (erase is not null && HOffset > 0)
                    context.FillOpaque(new Rect(0, y, frozenWidth, 1), erase);

                for (int c = 0; c < frozenCount && c < row.Cells.Length; c++)
                    DrawDataCell(context, row, c, view, y, focusRow, focusColumn, rowSelected, noColor);
            }

            // The §9.3 expander gutter glyph (a data row's ▶/▼ — group rows keep their own ▸/▾).
            if (gutterWidth > 0 && row.RowId >= 0)
            {
                context.DrawText(0, y, owner.IsDetailExpanded(row.RowId) ? "▼" : "▶",
                                 AccentBrush ?? TextBrush ?? Brushes.Default);
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
                context.DrawText(DrawXOf(_editColumnIndex) + DataGridColumnLayout.CellPadding + entry.Width - 2,
                                 ContentYOf(_editViewIndex), "▲▼", AccentBrush ?? TextBrush ?? Brushes.Default);
            }
        }

        DrawNewRowTemplate(context, owner, entries, focusRow, focusColumn);
    }

    /// <summary>
    /// One data cell at its §9.2 draw position (shifted for scrolling entries, unshifted for frozen
    /// ones; skipped entirely when the slot misses the viewport — column virtualization). The body
    /// is the v1 cell painter: focus well, formatted text honoring the column alignment, data bar.
    /// <paramref name="view"/> is the view-row index (focus compares), <paramref name="y"/> its
    /// content-y draw row (§9.3).
    /// </summary>
    private void DrawDataCell(RenderContext context, in CachedRow row, int c, int view, int y, int focusRow,
                              int focusColumn, bool rowSelected, bool noColor)
    {
        if (!IsEntryVisible(c))
            return;

        IBrush defaultBrush = TextBrush ?? Brushes.Default;

        IBrush textBrush = rowSelected
                               ? c == focusColumn
                                     ? FocusCellForeground ?? SelectionForeground ?? defaultBrush
                                     : SelectionForeground ?? defaultBrush
                               : HoverViewIndex == view
                                   ? HoverForeground ?? defaultBrush
                                   : (view & 1) == 1
                                       ? RowAlternationForeground ?? defaultBrush
                                       : defaultBrush;

        var entry = ColumnLayout.Entries[c];
        int drawBase = DrawXOf(c);
        int cellX = drawBase + DataGridColumnLayout.CellPadding;

        // The §9.4/§10.1 cell-range fill (under the focus well + text; group rows never route here) —
        // a cell is in the selection when ANY range's rect contains it (ranges rarely overlap; when
        // they do the fill is idempotent).
        bool cellInRange = false;
        if (_renderCellRanges.Count > 0)
        {
            for (int r = 0; r < _renderCellRanges.Count; r++)
            {
                var range = _renderCellRanges[r];
                if (view >= range.FirstRow && view <= range.LastRow && c >= range.FirstColumn && c <= range.LastColumn)
                {
                    cellInRange = true;
                    break;
                }
            }

            if (cellInRange && SelectionBackground is not null && !noColor) // NoColor: the reverse-video fill below carries it
                context.FillOpaque(new Rect(drawBase, y, entry.Width + 2 * DataGridColumnLayout.CellPadding, 1), SelectionBackground);
        }

        // NoColor tier: the row/range/focus background fills above and below all resolve to Default
        // (invisible), so the cell carries the cue in reverse-video (selection, cell-range) plus a bold
        // weight on the focus cell to set the cursor position apart from mere selection (§4; the
        // direct-draw mirror of the framework's `.caps-nocolor` list-focus cue).
        bool isFocusCell = view == focusRow && c == focusColumn;
        bool forceInverse = noColor && (rowSelected || cellInRange || isFocusCell);
        bool forceBold = noColor && isFocusCell;

        // Lay a reverse-video block across the whole cell slot so the cue reads as a SOLID bar (not
        // reverse-video text islands) — adjacent slots tile without gaps into a row/range bar. The
        // icon/text then redraw WITH inverse over it so no glyph punches a non-inverse hole.
        if (forceInverse)
            FillInverse(context, drawBase, y, entry.Width + 2 * DataGridColumnLayout.CellPadding);

        // The cell verdict overlays the row verdict (§2.7 — both pre-computed at band fill). A
        // format BACKGROUND is a WHOLE-CELL fill (live-canary fix: the glyph layer's DrawText
        // overwrites the base style's background with its transparent default, so a style-carried
        // background never rendered — and cell-wide is the DevExpress look anyway; it also covers
        // empty cells). A selected row's tint outranks it (the selection must stay legible).
        var format = (c < row.CellFormats.Length ? row.CellFormats[c] : default).OverlayOn(row.RowFormat);

        // These color fills would paint invisibly under NoColor AND clobber the reverse-video bar
        // laid down above (the fill runs after FillInverse), so they are skipped there — the cue is
        // carried by the inverse attribute on the cells instead.
        if (format.Background is {} formatBackground && !rowSelected && !noColor)
        {
            context.FillOpaque(new Rect(drawBase, y, entry.Width + 2 * DataGridColumnLayout.CellPadding, 1),
                               BrushFor(formatBackground));
        }

        // The focus cell's well-fill (the mockup's focuscell).
        if (view == focusRow && c == focusColumn && FocusCellBackground is not null && !noColor)
        {
            context.FillOpaque(new Rect(drawBase, y, entry.Width + 2 * DataGridColumnLayout.CellPadding, 1),
                               FocusCellBackground);
        }

        ReadOnlySpan<char> text = CellText(row, c); // §9.6 — sliced from the pooled band buffer
        int textWidth = GraphemeWidth.StringWidth(text);
        double fraction = c < row.BarFractions.Length ? row.BarFractions[c] : double.NaN;
        bool hasBar = !double.IsNaN(fraction);

        // The verdict's Icon glyph rides the cell's LEFT edge wearing the format foreground (the
        // editor's ▲●▼ icon sets). The reserve is UNIFORM per column on a bar cell (§10.7 — the
        // band's widest bar-cell icon, blank when this row has none) so an icon + bar coexist with
        // the track origin fixed; on a plain cell it is just this row's own icon width. The value
        // keeps its alignment in the width that remains.
        int iconReserve = hasBar
            ? (c < _barIconReserve.Length ? _barIconReserve[c] : 0)
            : (format.Icon is { } plainIcon && GraphemeWidth.StringWidth(plainIcon) is var piw && piw > 0 && piw + 1 < entry.Width ? piw + 1 : 0);
        if (iconReserve > 0 && format.Icon is { } icon && GraphemeWidth.StringWidth(icon) > 0)
            DrawFormattedCell(context, textBrush, cellX, y, icon, Math.Max(1, iconReserve), format, forceInverse); // glyphs get Inverse-only

        // A data-bar cell pins its value LEFT with the bar filling the remainder (the
        // mockup's amtcell); everything else honors the column alignment.
        int avail = Math.Max(0, entry.Width - iconReserve);
        int drawX = !hasBar &&
                    entry.Column.TextAlignment == Rendering.Text.TextAlignment.Right &&
                    textWidth < avail
                        ? cellX + iconReserve + (avail - textWidth)
                        : cellX + iconReserve;

        DrawFormattedCell(context, textBrush, drawX, y, text, avail, format, forceInverse, forceBold);

        if (hasBar)
        {
            // The bar starts after the COLUMN's icon reserve + text reserve, not this row's text
            // (the live-canary uniform-scale fix): one origin + one track width per column.
            int reserve = c < _barReserve.Length && _barReserve[c] > 0 ? _barReserve[c] : textWidth;
            int used = iconReserve + Math.Min(reserve, avail);
            DrawDataBar(context, cellX + used + 1, y, entry.Width - used - 1, fraction);
        }
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
        int viewIndex = NewRowViewIndex;

        if (viewIndex < 0)
            return;

        int y = ContentYOf(viewIndex); // §9.3: panes above push the ghost row down

        // Only ink inside the band scene (the SCP band covers the extent tail because the extent
        // includes this row; out-of-band draws would be clipped anyway — skip the work).
        var (windowStart, windowLength) = BandWindow();

        if (y < windowStart || y >= windowStart + windowLength)
            return;

        var ghost = MutedBrush;
        var ghostStyle = CellStyle.Default;

        if (ghost is null)
        {
            ghost = TextBrush ?? Brushes.Default;
            ghostStyle = ghostStyle.WithAttributes(TextAttributes.Faint);
        }

        var controller = owner.Controller;
        int frozenCount = ColumnLayout.FrozenCount;
        int frozenWidth = ColumnLayout.FrozenWidth;

        void DrawGhostCell(int c)
        {
            var entry = entries[c];

            if (!IsEntryVisible(c))
                return;

            // The focused ghost cell keeps the focus-cell well cue (it is focusable like a data row).
            if (viewIndex == focusRow && c == focusColumn && FocusCellBackground is not null)
            {
                context.FillOpaque(new Rect(DrawXOf(c), y, entry.Width + 2 * DataGridColumnLayout.CellPadding, 1),
                                   FocusCellBackground);
            }

            if (controller?.IsColumnEditable(entry.Column) != true)
                return;

            string hint = owner.ResolveEditorKind(entry.Column) switch
                          {
                              DataGridEditorKind.Combo => "(pick)",
                              DataGridEditorKind.Date  => "yyyy-mm-dd",
                              DataGridEditorKind.Spin  => "0",
                              DataGridEditorKind.None  => string.Empty,
                              _                        => "…", // "…" — the text ghost
                          };

            if (hint.Length != 0)
            {
                DrawClipped(context, DrawXOf(c) + DataGridColumnLayout.CellPadding, y,
                            hint, entry.Width, ghost, ghostStyle);
            }
        }

        // §9.2 paint order (audit W2-4 — the data rows' mirror): scrolling ghosts first, then the
        // frozen region re-fills and draws its ghosts unshifted on top.
        for (int c = frozenCount; c < entries.Count; c++)
            DrawGhostCell(c);

        if (frozenWidth > 0)
        {
            if (owner.Background is {} erase && HOffset > 0)
                context.FillOpaque(new Rect(0, y, frozenWidth, 1), erase);

            for (int c = 0; c < frozenCount; c++)
                DrawGhostCell(c);
        }

        context.DrawText(0, y, "*", ghost);
        // (no row fill — the ghost row deliberately stays on the resting background)
    }

    /// <summary>
    /// Draws one data cell honoring its conditional-format verdict: an empty verdict rides the
    /// resting <see cref="TextBrush"/> lane; a colored/attributed one draws through the Color
    /// overload (the format's fg wins; Bold/Inverse/bg fold into the base <see cref="CellStyle"/> —
    /// NoColor tiers keep the attribute cues, §4). Grapheme-truncated like every drawn cell.
    /// </summary>
    private void DrawFormattedCell(RenderContext context, IBrush textBrush, int x, int y, ReadOnlySpan<char> text, int maxWidth,
                                   in CellFormat format, bool forceInverse = false, bool forceBold = false)
    {
        // The NoColor focus/selection cue (§4): reverse-video (and, on the focus cell, bold) folded
        // over whatever the conditional-format verdict already carries.
        var forced = default(TextAttributes);

        if (forceInverse)
            forced |= TextAttributes.Inverse;

        if (forceBold)
            forced |= TextAttributes.Bold;

        if (format.IsEmpty)
        {
            DrawClipped(context, x, y, text, maxWidth, textBrush,
                        forced == default ? default : default(CellStyle).WithAttributes(forced));
            return;
        }

        if (text.Length == 0)
            return;

        var attributes = forced;

        if (format.Bold)
            attributes |= TextAttributes.Bold;

        if (format.Inverse)
            attributes |= TextAttributes.Inverse;

        CellStyle style = default;

        if (attributes != default)
            style = style.WithAttributes(attributes);

        // Deliberately NO WithBackground: DrawText's background parameter (transparent by default)
        // OVERWRITES the base style's background per cell, so a style-carried background never
        // reached the frame — the cell-wide fill in DrawDataCell is the background rendering, and
        // the transparent glyph background lets it show through under the text.

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

            span = text[..end];
        }

        if (format.Foreground is {} foreground)
        {
            context.DrawText(x, y, span, foreground, null, style);

            if (truncated)
                context.DrawText(x + width, y, "…", foreground, null, style);
        }
        else
        {
            context.DrawText(x, y, span, textBrush!, null, style);

            if (truncated)
                context.DrawText(x + width, y, "…", textBrush!, null, style);
        }
    }

    /// <summary>
    /// Paints a reverse-video bar of <paramref name="width"/> space-bearing cells at (x, y) — the
    /// NoColor selection/focus fill. SGR 7 swaps the terminal's real default fg/bg, so the bar is
    /// visible even though every NoColor brush resolves to Default (§4). The text/icon then redraw
    /// with inverse over it so no glyph punches a non-inverse hole.
    /// </summary>
    private void FillInverse(RenderContext context, int x, int y, int width)
    {
        if (width <= 0)
            return;

        context.FillOpaque(new Rect(x, y, width, 1), TextBrush ?? Brushes.Default, TextAttributes.Inverse);
    }

    /// <summary>The `█░` fill/track run after a data-bar cell's value (glyph shape carries the value in NoColor — §4).</summary>
    private void DrawDataBar(RenderContext context, int x, int y, int width, double fraction)
    {
        if (width < 1)
            return;

        width = Math.Min(width, MaxBarCells);
        int fill = (int) Math.Round(Math.Clamp(fraction, 0, 1) * width);

        if (fill > 0 && DataBarFillBrush is {} fillBrush)
            context.DrawText(x, y, BarFillGlyphs.AsSpan(0, fill), fillBrush);

        if (fill < width && DataBarTrackBrush is {} trackBrush)
            context.DrawText(x + fill, y, BarTrackGlyphs.AsSpan(0, width - fill), trackBrush);
    }

    /// <summary>Draws text grapheme-truncated to <paramref name="maxWidth"/> (there is no clip stack
    /// inside Render — §3.2). Span-based (§9.6) — string callers convert implicitly.</summary>
    private static void DrawClipped(RenderContext context, int x, int y, ReadOnlySpan<char> text, int maxWidth,
                                    IBrush? foreground, CellStyle style = default)
    {
        if (text.Length == 0 || foreground is null)
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

            context.DrawText(x, y, text[..end], foreground, background: null, style);
            context.DrawText(x + width, y, "…", foreground, background: null, style);
            return;
        }

        context.DrawText(x, y, text, foreground, background: null, style);
    }

    // ── In-cell editing — the sanctioned element-hosting special case (§3.2, owner mandate) ──────

    private Control? _editor;
    private DataGridEditorKind _editorKind;
    private int _editViewIndex = -1;
    private int _editColumnIndex = -1;
    private int _editRowId = -1;
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
    /// The edited DATA row's id (−1 on the new-row placeholder session) — the session's ONE
    /// identity (live-canary fix): live publishes permute the view beneath the open editor, so a
    /// view-index-anchored session either aliased a different row at commit (wrong-row write) or
    /// fell off the end (the silently-discarded commit the gallery report hit). The grid
    /// re-anchors the view slot per publish and commits through THIS id.
    /// </summary>
    internal int EditRowId => _editRowId;

    /// <summary>Re-anchors the edit session's view slot after a publish moved its row (the grid
    /// resolves the id → view; the hosted editor then re-arranges onto its row).</summary>
    internal void ReanchorEditRow(int viewIndex)
    {
        if (_editor is null || _editViewIndex == viewIndex)
            return;

        _editViewIndex = viewIndex;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// Whether a drop-down editor's list is open. The grid's tunnel intercept keys off this: a
    /// CLOSED drop-down editor must yield Enter/Esc to the edit contract (commit/cancel), an open
    /// one owns them (Enter = pick, Esc = close) — see <c>DataGrid.OnPreviewKeyDown</c>.
    /// </summary>
    internal bool IsEditorDropDownOpen => _editor switch
                                          {
                                              ComboBox combo    => combo.IsDropDownOpen,
                                              DatePicker picker => picker.IsDropDownOpen,
                                              _                 => false,
                                          };

    /// <summary>
    /// Hosts one editor element at a cell, keyed by <paramref name="kind"/> (the generalized §3.2
    /// host — the v1 TextBox path plus the combo/date/spin suite): the presenter adopts the element
    /// as a visual/logical child, arranges it at the cell's CONTENT rect (it scrolls with the band
    /// naturally), and focuses it via the parked-work idiom. The drawn cell underneath is painted
    /// over by the editor's own background. <paramref name="comboItems"/> feeds the Combo kind only.
    /// </summary>
    internal void BeginEdit(int viewIndex, int columnIndex, DataGridEditorKind kind, string initialText,
                            IReadOnlyList<string>? comboItems = null, int rowId = -1)
    {
        EndEditVisual();
        _editRowId = rowId;

        Control editor = kind switch
                         {
                             DataGridEditorKind.Combo => CreateComboEditor(initialText, comboItems ?? []),
                             DataGridEditorKind.Date  => CreateDateEditor(initialText),
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
                                                       editor.Focus(FocusNavigationMethod.Programmatic);
                                                       (editor as TextBox)?.SelectAll();
                                                   }
                                               });
    }

    private TextBox CreateTextEditor(string initialText)
    {
        var editor = new TextBox { Text = initialText, Padding = Margins.Zero };
        // The ed-err recovery contract: the danger ink clears on the NEXT text change (§3.2).
        editor.AddHandler(TextBox.TextChangedEvent, (_, _) => ClearEditorError());
        return editor;
    }

    private ComboBox CreateComboEditor(string initialText, IReadOnlyList<string> items)
    {
        var combo = new ComboBox { ItemsSource = items };

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

    private DatePicker CreateDateEditor(string initialText)
    {
        // Editable: the face is a typed-text box + the calendar drop-down (the mockup's ed-date).
        var picker = new DatePicker { IsEditable = true };

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
        // Audit fix: free-typed text that has not yet parsed to a new date raises no
        // SelectedDateChanged — clear the error cue on the raw keystroke too (parity with the
        // TextBox/Spin editors), else a rejected commit's message + danger ink persist while the
        // user types the correction.
        picker.EditableTextEdited += (_, _) => ClearEditorError();
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
            case TextBox box:
                text = box.Text;
                return true;

            case ComboBox combo:
                text = combo.SelectedItem as string;
                return text is not null;

            case DatePicker picker:
                text = FindDescendant<TextBox>(picker)?.Text
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
        if (_editorKind != DataGridEditorKind.Spin || _editor is not TextBox box)
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
        if (_editor is not {} editor)
            return;

        _editorErrorFlagged = true;
        editor.SetResourceReference(Control.ForegroundProperty, ThemeKeys.DangerBrush);
    }

    private void ClearEditorError()
    {
        _owner?.ClearEditValidationError(); // §10.2 — a fresh keystroke retires the validation message
        if (!_editorErrorFlagged || _editor is not {} editor)
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

            if (FindDescendant<T>(child) is {} nested)
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
        _editRowId = -1;
        InvalidateMeasure();
        InvalidateVisual();
        _owner?.NotifyEditingChanged();
        _owner?.Focus(FocusNavigationMethod.Programmatic);
    }

    /// <summary>
    /// The editor's arranged width inside its cell: the Spin kind reserves a 3-cell suffix for the
    /// drawn <c>▲▼</c> affordance when the cell is wide enough (the mockup's spinbtns; skipped in
    /// cramped cells — stepping still works, only the hint is elided).
    /// </summary>
    private int EditorWidth(in DataGridColumnLayout.Entry entry)
        => _editorKind == DataGridEditorKind.Spin && entry.Width >= 6
               ? entry.Width - 2
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
            // §9.2: the arrange x is the SHIFTED draw position (hosted children re-arrange per
            // H-tick — the offset registers AffectsMeasure on the grid); the grid's offset-change
            // policy commits an editor before it would slide under the frozen region. The arrange
            // row maps through the §9.3 content-y map.
            var entry = ColumnLayout.Entries[_editColumnIndex];

            _editor.Arrange(new Rect(DrawXOf(_editColumnIndex) + DataGridColumnLayout.CellPadding,
                                     ContentYOf(_editViewIndex), EditorWidth(entry), 1));
        }

        // §9.3: realized panes arrange at their content-y range, horizontally VIEWPORT-anchored
        // (x=0 viewport-wide, never shifted by the H offset — the hosted-children-over-frozen
        // policy). A pane's rect may exceed the band; the scene crops.
        foreach (var pane in _details)
        {
            if (_detailElements.TryGetValue(pane.RowId, out var element))
                element.Arrange(new Rect(0, pane.YStart, Math.Max(1, _viewport.Columns), pane.Height));
        }

        return finalSize;
    }

    // ── Hit testing + mouse (the single hit leaf — §3.2) ─────────────────────────────────────────

    /// <summary>
    /// Maps a LOCAL (viewport-space) position to (viewIndex, columnEntryIndex, isExpander). The
    /// §9.2 split: x under the frozen width hits the frozen region directly (it draws on top);
    /// anything right of it maps through the horizontal offset. Group rows are viewport-anchored,
    /// so their expander check reads the local x unmapped.
    /// </summary>
    internal (int ViewIndex, int ColumnIndex, bool OnExpander) HitCell(int x, int y)
    {
        var owner = _owner;

        if (owner is null)
            return (-1, -1, false);

        // §9.3: content-y → view row first; a y inside a pane belongs to the pane's own hosted
        // children (elements take their hits before the presenter leaf — nothing for us).
        var (viewIndex, inDetail) = ViewIndexAtY(y);

        if (inDetail)
            return (-1, -1, false);

        if (viewIndex < 0 || viewIndex >= owner.Snapshot.Count)
        {
            // The new-row template is clickable like a data row (its view index is one PAST the
            // snapshot — every snapshot read above stays guarded).
            if (viewIndex >= 0 && viewIndex == NewRowViewIndex)
                return (viewIndex, ColumnLayout.EntryAt(ContentXAt(x)), false);

            return (-1, -1, false);
        }

        var row = owner.Snapshot.GetRow(viewIndex);

        if (row.IsGroup)
        {
            int expanderX = row.Level * 2;
            return (viewIndex, -1, x >= expanderX && x <= expanderX + 1);
        }

        // The §9.3 gutter zone is the data row's detail expander.
        if (ColumnLayout.GutterWidth > 0 && x >= 0 && x < ColumnLayout.GutterWidth)
            return (viewIndex, -1, true);

        return (viewIndex, ColumnLayout.EntryAt(ContentXAt(x)), false);
    }

    /// <summary>The §9.2 local→content x map (frozen region identity, scrolled region shifted).</summary>
    internal int ContentXAt(int localX)
        => localX < ColumnLayout.FrozenWidth ? localX : localX + HOffset;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Handled || _owner is null)
            return;

        var position = e.GetPosition(this);

        // Sweep [19]: the drawn ▲▼ spin steppers are CLICKABLE (the mockup's spinbtns — an
        // affordance inside the edit cell that users WILL press): ▲ steps +1, ▼ −1, Shift ×10;
        // focus stays with the editor (the press must never strand the session).
        if (e.Button == MouseButton.Left && _editor is not null && _editorKind == DataGridEditorKind.Spin &&
            _editColumnIndex >= 0 && _editColumnIndex < ColumnLayout.Entries.Count)
        {
            var editEntry = ColumnLayout.Entries[_editColumnIndex];

            if (editEntry.Width >= 6 && position.Row == ContentYOf(_editViewIndex))
            {
                int zoneStart = DrawXOf(_editColumnIndex) + DataGridColumnLayout.CellPadding + editEntry.Width - 2;

                if (position.Column == zoneStart || position.Column == zoneStart + 1)
                {
                    SpinBy((position.Column == zoneStart ? 1m : -1m) *
                           ((e.Modifiers & KeyModifiers.Shift) != 0 ? 10m : 1m));

                    _editor.Focus(FocusNavigationMethod.Programmatic);
                    e.Handled = true;
                    return;
                }
            }
        }

        var (viewIndex, columnIndex, onExpander) = HitCell(position.Column, position.Row);

        // Right-press opens the grid command menu at the pressed cell (the reachability surface:
        // sort/group lanes, the filter dialogs, formatting, summaries, copy). Focus follows the
        // press like a left-click — INCLUDING group rows (row focus) and the new-row placeholder
        // (past-the-end focus) — so the menu's column lanes match what the user sees focused
        // (sweep [7]/[8]). No position: the menu lands at the POINTER cell (sweep [6] — an
        // explicit position means bottom-edge placement, which pinned the menu to the screen
        // bottom regardless of the press row).
        if (e.Button == MouseButton.Right)
        {
            if (viewIndex >= 0)
                _owner.SetContextPressFocus(viewIndex, columnIndex);

            _owner.OpenGridContextMenu(columnIndex);
            e.Handled = true;
            return;
        }

        if (e.Button != MouseButton.Left || viewIndex < 0)
            return;

        _owner.HandleRowPress(viewIndex, columnIndex, onExpander, e.Modifiers, e.ClickCount);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_owner is null || IsPointerOver is false)
            return;

        var position = e.GetPosition(this);
        var (viewIndex, inDetail) = ViewIndexAtY(position.Row); // §9.3: pane rows never hover-highlight
        int hover = !inDetail && viewIndex >= 0 && viewIndex < ItemCount ? viewIndex : -1;

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