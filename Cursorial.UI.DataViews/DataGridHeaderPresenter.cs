using System.Globalization;

using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Text;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

namespace Cursorial.UI.DataViews;

/// <summary>
/// The direct-draw header band (design doc §3.1): one cell row of column captions with the sort
/// glyph (<c>▲/▼</c>, multi-sort ordinal digits past the first level) and the filter affordance
/// (<c>▾</c>, amber when the column carries a filter — the mockup's active state). Its own render
/// boundary (ClipToBounds — a 1-row band, so the horizontal-offset re-ink stays band-local, §3.1);
/// mirrors the shared horizontal offset. Header cells hold VIRTUAL focus (a grid-owned index +
/// drawn cue via the §3.3 band cycle — drawn cells cannot hold framework focus); clicks sort
/// (Ctrl+click appends — the wire-reliable chord; Shift+click too where the terminal delivers it).
/// The §1-deferred column UX lives here too: header-edge drag resize (the right padding cell is
/// the grab zone), press-and-drag reorder with the mockup's <c>draghdr</c> adorners (floating
/// <c>▣</c> chip, cyan <c>▾</c> drop slot, ghosted shift target), drag-down-off-the-band to hide,
/// and the right-click column chooser.
/// </summary>
public sealed class DataGridHeaderPresenter : UIElement
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        UIProperty.Register<DataGridHeaderPresenter, IBrush?>(
            nameof(Background),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.SurfaceBrush });

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        UIProperty.Register<DataGridHeaderPresenter, IBrush?>(
            nameof(Foreground),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.AccentBrush });

    public static readonly StyledProperty<IBrush?> SortGlyphBrushProperty =
        UIProperty.Register<DataGridHeaderPresenter, IBrush?>(
            nameof(SortGlyphBrush),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.CoolBrush });

    public static readonly StyledProperty<IBrush?> FilterGlyphBrushProperty =
        UIProperty.Register<DataGridHeaderPresenter, IBrush?>(
            nameof(FilterGlyphBrush),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.MutedBrush });

    public static readonly StyledProperty<IBrush?> ActiveFilterBrushProperty =
        UIProperty.Register<DataGridHeaderPresenter, IBrush?>(
            nameof(ActiveFilterBrush),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.AmberBrush });

    public static readonly StyledProperty<IBrush?> HoverBackgroundProperty =
        UIProperty.Register<DataGridHeaderPresenter, IBrush?>(
            nameof(HoverBackground),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.HoverBrush });

    /// <summary>The shared horizontal offset (the template binds it to the ScrollViewer's — §3.1).</summary>
    public static readonly StyledProperty<int> HorizontalOffsetProperty =
        UIProperty.Register<DataGridHeaderPresenter, int>(nameof(HorizontalOffset));

    static DataGridHeaderPresenter()
    {
        AffectsRender<DataGridHeaderPresenter>(
            BackgroundProperty, ForegroundProperty, SortGlyphBrushProperty, FilterGlyphBrushProperty,
            ActiveFilterBrushProperty, HoverBackgroundProperty, HorizontalOffsetProperty);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? SortGlyphBrush
    {
        get => GetValue(SortGlyphBrushProperty);
        set => SetValue(SortGlyphBrushProperty, value);
    }

    public IBrush? FilterGlyphBrush
    {
        get => GetValue(FilterGlyphBrushProperty);
        set => SetValue(FilterGlyphBrushProperty, value);
    }

    public IBrush? ActiveFilterBrush
    {
        get => GetValue(ActiveFilterBrushProperty);
        set => SetValue(ActiveFilterBrushProperty, value);
    }

    public IBrush? HoverBackground
    {
        get => GetValue(HoverBackgroundProperty);
        set => SetValue(HoverBackgroundProperty, value);
    }

    public int HorizontalOffset
    {
        get => GetValue(HorizontalOffsetProperty);
        set => SetValue(HorizontalOffsetProperty, value);
    }

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

        var entries = layout.Entries;

        // Reorder-drag adorner inputs (the mockup's draghdr row): the ghost tint on the dragged
        // cell (lifted) and the shift target (the cell the drop displaces right) rides INSIDE the
        // caption loop — an after-the-fact fill would wipe the caption it tints. In the hide zone
        // (pointer below the band) there is no slot: the ▾/ghost drop out, signalling release=hide.
        bool dragging = _gesture == HeaderGesture.Reordering && _dragColumn is not null;
        bool hideZone = dragging && _dragLocal.Row > HideZoneRows;
        bool groupZone = dragging && InGroupZone(_dragLocal, _dragColumn!);

        bool slotLive = dragging && !hideZone && !groupZone && DropSlot >= 0 &&
                        DropSlot != _gestureEntry && DropSlot != _gestureEntry + 1; // beside-itself = no-op slot

        int ghostIndex = slotLive && DropSlot < entries.Count ? DropSlot : -1;

        // §9.2 paint order (the rows presenter's mirror): scrolling cells first (shifted), then the
        // frozen region re-fills its background and draws its cells unshifted on top.
        for (int i = layout.FrozenCount; i < entries.Count; i++)
            DrawHeaderCell(context, owner, layout, i, dragging, ghostIndex);

        if (layout.FrozenWidth >
            0) // width, not count — the §9.3 gutter is pinned even with no Fixed column (audit W2-1)
        {
            if (Background is not null && HorizontalOffset > 0)
                context.FillOpaque(new Rect(0, 0, layout.FrozenWidth, 1), Background);

            for (int i = 0; i < layout.FrozenCount; i++)
                DrawHeaderCell(context, owner, layout, i, dragging, ghostIndex);
        }

        // ── Reorder-drag overlay: the ▾ drop slot + the floating ▣ chip (drawn LAST, over all) ───
        if (dragging)
        {
            var foreground = Foreground ?? Brushes.Default;

            if (slotLive)
            {
                // The cyan slot marker at the insertion boundary — SortGlyphBrush is the theme's
                // CoolBrush (the mockup's accent-2 cyan), reused deliberately: no new theme key for
                // a glyph that means "here" in the same accent family.
                int boundaryX = DropSlot < entries.Count
                                    ? DrawXOf(layout, DropSlot)
                                    : layout.TotalWidth - HorizontalOffset;

                // A scrolling-partition boundary never draws INSIDE the frozen band (sweep [0]'s
                // cue defect: the seam slot's shifted x landed under the frozen headers).
                if (DropSlot >= layout.FrozenCount)
                    boundaryX = Math.Max(boundaryX, layout.FrozenWidth);

                if (boundaryX >= 0 && boundaryX < Bounds.Columns)
                    context.DrawText(boundaryX, 0, "▾", SortGlyphBrush ?? foreground);
            }

            // The floating chip follows the pointer, clamped into the 1-row band (the terminal has
            // no out-of-band overlay to float it in). Muted ink in the hide zone — the visual cue
            // that release now hides instead of dropping; the accent ink + "▸ group" suffix in the
            // GROUP zone (over the panel above) — release adds the grouping level the panel's own
            // prompt advertises.
            string chip = "▣ " + _dragColumn!.EffectiveHeader + (groupZone ? " ▸ group" : string.Empty);
            int chipWidth = GraphemeWidth.StringWidth(chip);
            int chipX = Math.Clamp(_dragLocal.Column, 0, Math.Max(0, Bounds.Columns - chipWidth));

            if (HoverBackground is not null)
                context.FillOpaque(new Rect(chipX, 0, chipWidth, 1), HoverBackground);

            context.DrawText(chipX, 0, chip,
                             groupZone
                                 ? SortGlyphBrush ?? foreground
                                 : hideZone
                                     ? FilterGlyphBrush ?? foreground
                                     : foreground);
        }
    }

    /// <summary>One header cell at its §9.2 draw position (the caption + glyph cluster + drag tints).</summary>
    private void DrawHeaderCell(RenderContext context, DataGrid owner, DataGridColumnLayout layout, int i,
                                bool dragging, int ghostIndex)
    {
        var entry = layout.Entries[i];
        int x = DrawXOf(layout, i);
        int cellWidth = entry.Width + 2 * DataGridColumnLayout.CellPadding;
        int leftEdge = i < layout.FrozenCount ? 0 : layout.FrozenWidth;

        if (x + cellWidth <= leftEdge || x >= Bounds.Columns)
            return;

        // The §3.3 virtual band focus shares the hover tint (one look for "this header cell is
        // where the next gesture lands", pointer- or keyboard-driven).
        bool tinted = dragging
                          ? i == ghostIndex || i == _gestureEntry // ghost the shift target + the lifted source
                          : i == _hoverEntry || i == owner.HeaderFocusIndex;

        if (tinted && HoverBackground is not null)
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
        var foreground = Foreground ?? Brushes.Default;

        if (entry.Column.AllowFilter)
        {
            bool active = owner.HasColumnFilter(entry.Column);

            context.DrawText(glyphX, 0, "▾", 
                             active 
                                 ? ActiveFilterBrush ?? FilterGlyphBrush ?? foreground 
                                 : FilterGlyphBrush ?? foreground);
            glyphX -= 2;
        }

        if (direction is {} d)
        {
            if (ordinal > 0 && ordinal < 9)
            {
                context.DrawText(glyphX, 0, (ordinal + 1).ToString(CultureInfo.InvariantCulture),
                                 SortGlyphBrush ?? foreground);

                glyphX -= 1;
            }

            context.DrawText(glyphX, 0, d == Shaping.SortDirection.Ascending ? "▲" : "▼", 
                             SortGlyphBrush ?? foreground);
        }
    }

    private static void DrawTruncated(RenderContext context, int x, string text, int maxWidth, IBrush? brush)
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

    /// <summary>The layout entry under a local x (through the §9.2 split map), or −1.</summary>
    private int EntryAt(int localX) => Layout?.EntryAt(ContentXAt(localX)) ?? -1;

    /// <summary>A layout entry's painted x (§9.2 — frozen entries never shift).</summary>
    private int DrawXOf(DataGridColumnLayout layout, int index)
    {
        var entry = layout.Entries[index];
        return index < layout.FrozenCount ? entry.X : entry.X - HorizontalOffset;
    }

    /// <summary>The §9.2 local→content x map (frozen region identity, scrolled region shifted).</summary>
    private int ContentXAt(int localX)
        => Layout is {} layout && localX < layout.FrozenWidth ? localX : localX + HorizontalOffset;

    // ── Column-UX gesture state (§1 deferred, now landed) ────────────────────────────────────────
    //
    // One left-button gesture at a time, disambiguated at PRESS by zone within the hit cell:
    //   [content .. ▾ glyph][edge] — the edge (the right CellPadding cell, the seam between two
    // columns) starts a RESIZE; the ▾ glyph cell opens the filter popup (unchanged, on the DOWN);
    // everything else is a PENDING press — the sort-vs-reorder decision defers to movement:
    // ≥ 2 cells promotes to a reorder drag, release without promotion IS the click (sort). The
    // deferral moves the sort from down to up — an accepted cost: no consumer can tell a click's
    // sort apart from a down's sort, but a drag must never ALSO sort.
    private enum HeaderGesture
    {
        None,
        Pending,
        Resizing,
        Reordering
    }

    private HeaderGesture _gesture;
    private int _gestureEntry = -1;      // layout-entry index at press (−1 = an EXTERNAL drag)
    private DataGridColumn? _dragColumn; // the dragged column (entry-derived, or the chooser's hidden column)

    private CellPosition _gestureAnchor; // SCREEN position at press (chrome-drag idiom: the

    // header never moves under the gesture, but screen
    // anchoring keeps deltas honest under capture anyway)
    private KeyModifiers _gestureModifiers; // press-time modifiers (the deferred click replays them)
    private int _resizeStartWidth;          // the RESOLVED content width at press (Auto → pinned)
    private CellPosition _dragLocal;        // last pointer position local to the band (adorners)
    private UIElement? _escapeHookRoot;     // the root carrying the mid-drag Esc tunnel handler

    /// <summary>The drop slot (insertion index in visible-entry space) the drag currently targets, or −1.</summary>
    internal int DropSlot { get; private set; } = -1;

    /// <summary>Whether a reorder drag is live (tests observe promotion/cancel).</summary>
    internal bool IsDragging => _gesture == HeaderGesture.Reordering;

    /// <summary>Local row past which a drop means "hide the column" (the chooser's drag-off affordance).</summary>
    private const int HideZoneRows = 2;

    /// <summary>Movement (cells, either axis) that promotes a pending press into a reorder drag.</summary>
    private const int DragThreshold = 2;

    /// <summary>
    /// Sweep [2]/[15] — the chooser's drag-to-show hand-off: adopts a HIDDEN column into this
    /// band's reorder machinery mid-press (the chooser closes; mouse capture transfers HERE while
    /// the button is still down, so the moves/release route to the header with band-local
    /// coordinates). Release in-band shows the column at the drop slot; the hide zone, a rejected
    /// group zone, or Esc cancel (it stays hidden); a valid group-zone release groups by it.
    /// </summary>
    internal bool BeginExternalColumnDrag(DataGridColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (_owner is null || Layout is null || _gesture != HeaderGesture.None || !CaptureMouse())
            return false;

        _gesture = HeaderGesture.Reordering;
        _gestureEntry = -1; // no source entry — the column is hidden
        _dragColumn = column;
        DropSlot = -1;
        _hoverEntry = -1;
        HookEscape();
        UIApplication.Current?.InputDispatcher.UpdateCursor(); // Grabbing
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// Whether a drag position sits in the GROUP zone: above the band while the group panel is
    /// shown, with a groupable-and-not-yet-grouped drag column (the cue must never promise a
    /// no-op). Release adds the grouping level; the panel band sits directly above, and the header
    /// holds capture, so its coordinates simply go negative.
    /// </summary>
    private bool InGroupZone(CellPosition local, DataGridColumn column)
        => local.Row < 0 && _owner is { ShowGroupPanel: true } owner &&
           column.AllowGroup &&
           !owner.GroupDescriptions.Any(g => ReferenceEquals(g.ColumnKey, column));

    /// <summary>The right CellPadding cell of the entry's span — the resize grab zone.</summary>
    private bool OnResizeEdge(DataGridColumnLayout layout, int index, int localX)
        => localX == DrawXOf(layout, index) + layout.Entries[index].Width + 2 * DataGridColumnLayout.CellPadding - 1;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Handled || _owner is null || Layout is null)
            return;

        var position = e.GetPosition(this);
        int index = EntryAt(position.Column);

        // Right-press anywhere on the band opens the column chooser (the DevExpress header context
        // affordance). On the DOWN rather than up: the band has no context-menu subsystem to defer
        // to, and a right-drag has no meaning here.
        if (e.Button == MouseButton.Right)
        {
            _owner.OpenColumnChooser(position.Column);
            e.Handled = true;
            return;
        }

        if (e.Button != MouseButton.Left || index < 0)
            return;

        var entry = Layout.Entries[index];
        var column = entry.Column;

        if (OnResizeEdge(Layout, index, position.Column))
        {
            // Double-click the seam = best-fit: back to Auto AND drop the monotonic growth memory,
            // so the next resolve measures tight against the current band (not the high-water mark).
            if (e.ClickCount >= 2)
            {
                column.Width = DataGridLength.Auto;
                Layout.ResetAutoGrowth(column);
                _owner.NotifyColumnGeometryChanged();
                e.Handled = true;
                return;
            }

            if (CaptureMouse())
            {
                _gesture = HeaderGesture.Resizing;
                _gestureEntry = index;
                _gestureAnchor = e.ScreenPosition;
                _resizeStartWidth = entry.Width; // the RESOLVED width — dragging an Auto column pins it
                // Cursor is gesture state, not hover position — force one re-resolution now
                // (the window-chrome resize precedent; per-frame re-resolution rides after).
                UIApplication.Current?.InputDispatcher.UpdateCursor();
            }

            e.Handled = true;
            return;
        }

        // The ▾ glyph cell opens the filter popup on the DOWN (unchanged v1 behavior). The zone
        // narrows from 2 cells to the glyph cell itself now that the outer padding cell belongs to
        // resize — the ▾ is drawn at cellRight − 2, so that exact cell is the popup affordance.
        int cellRight = DrawXOf(Layout, index) + entry.Width + 2 * DataGridColumnLayout.CellPadding;

        if (column.AllowFilter && position.Column == cellRight - 2)
        {
            _owner.OpenFilterPopup(column);
            e.Handled = true;
            return;
        }

        // Body press: capture and wait — a release without threshold movement is the sort click.
        if (CaptureMouse())
        {
            _gesture = HeaderGesture.Pending;
            _gestureEntry = index;
            _gestureAnchor = e.ScreenPosition;
            _gestureModifiers = e.Modifiers;
            _dragLocal = position;
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        _lastPointerLocal = position; // OnQueryCursor's per-frame re-resolution reads this

        switch (_gesture)
        {
            case HeaderGesture.Resizing when Layout is {} layout && _gestureEntry < layout.Entries.Count:
            {
                // Live resize: screen-anchored delta onto the press-time RESOLVED width, clamped by
                // the column's own Min/Max (min wins — the layout convention). Writing Width as
                // fixed cells converts an Auto column to pinned (the DevExpress behavior: a manual
                // resize is an authored width). Every write re-resolves + re-inks all four bands
                // through the grid's geometry funnel.
                var column = layout.Entries[_gestureEntry].Column;
                int delta = e.ScreenPosition.Column - _gestureAnchor.Column;
                int min = Math.Max(1, column.MinWidth);
                int max = column.MaxWidth > 0 ? column.MaxWidth : int.MaxValue;
                int target = Math.Clamp(_resizeStartWidth + delta, min, Math.Max(min, max));

                if (column.Width != DataGridLength.Cells(target))
                {
                    column.Width = DataGridLength.Cells(target);
                    _owner?.NotifyColumnGeometryChanged();
                }

                e.Handled = true;
                return;
            }

            case HeaderGesture.Pending:
            {
                int dx = Math.Abs(e.ScreenPosition.Column - _gestureAnchor.Column);
                int dy = Math.Abs(e.ScreenPosition.Row - _gestureAnchor.Row);

                if (dx >= DragThreshold || dy >= DragThreshold)
                {
                    // Promote to a reorder drag: hook the mid-drag Esc cancel on the ROOT's tunnel
                    // pass (the band is never focusable, so its own key virtuals can't fire; the
                    // preview tunnel sees the key first wherever focus sits inside this surface).
                    _gesture = HeaderGesture.Reordering;

                    _dragColumn = Layout is {} promoted && _gestureEntry >= 0 && _gestureEntry < promoted.Entries.Count
                                      ? promoted.Entries[_gestureEntry].Column
                                      : null;

                    HookEscape();
                    _hoverEntry = -1; // the drag adorners own the band's feedback now
                    UIApplication.Current?.InputDispatcher.UpdateCursor(); // Grabbing
                }
                else
                {
                    return; // sub-threshold jitter — still a click-in-waiting
                }

                goto case HeaderGesture.Reordering;
            }

            case HeaderGesture.Reordering:
                _dragLocal = position;
                DropSlot = _dragColumn is {} dragColumn ? ComputeDropSlot(position, dragColumn) : -1;
                InvalidateVisual();
                e.Handled = true;
                return;
        }

        // No gesture: plain hover highlight (unchanged).
        int hover = EntryAt(position.Column);

        if (hover != _hoverEntry)
        {
            _hoverEntry = hover;
            InvalidateVisual();
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button != MouseButton.Left || _gesture == HeaderGesture.None)
            return;

        var gesture = _gesture;
        int entryIndex = _gestureEntry;
        var modifiers = _gestureModifiers;
        var dragColumn = _dragColumn;
        var local = e.GetPosition(this);

        // Release-then-act: ReleaseMouseCapture raises LostMouseCapture, whose handler clears the
        // gesture state — snapshot first, then let the abort path do the (idempotent) cleanup.
        ReleaseMouseCapture();

        if (_owner is null || Layout is null)
        {
            e.Handled = true;
            return;
        }

        // The gesture's column: entry-derived for a native press; the adopted HIDDEN column for an
        // external chooser drag (sweep [2]/[15] — entryIndex is −1 there by construction).
        DataGridColumn? column = entryIndex >= 0 && entryIndex < Layout.Entries.Count
                                     ? Layout.Entries[entryIndex].Column
                                     : gesture == HeaderGesture.Reordering
                                         ? dragColumn
                                         : null;

        if (column is null)
        {
            e.Handled = true;
            return;
        }

        bool external = entryIndex < 0;

        switch (gesture)
        {
            case HeaderGesture.Pending:
                // No promotion happened: this IS the click — sort, replayed with the press-time
                // modifiers. Shift OR Ctrl appends a sort level: most terminals reserve Shift+click
                // for native text selection while mouse tracking is on (the click never reaches the
                // app), so Ctrl+click is the wire-reliable chord and Shift+click works where the
                // terminal deigns to deliver it (a live-canary finding — the §3.3 "mouse chords are
                // wire-reliable" claim was wrong for Shift specifically).
                if ((modifiers & (KeyModifiers.Shift | KeyModifiers.Control)) != 0)
                    _owner.AppendSort(column);
                else
                    _owner.CycleSort(column);

                break;

            case HeaderGesture.Reordering when InGroupZone(local, column):
                // Dropped on the group panel above: add the column as a grouping level — the
                // gesture the panel's empty prompt advertises (live-canary find: it promised the
                // drop but nothing handled a release above the band).
                _owner.GroupBy(column);
                break;

            case HeaderGesture.Reordering when local.Row > HideZoneRows:
                // Dropped well below the band: hide the column (the chooser's drag-off-to-hide
                // affordance, done here where the drag already lives). Shaping identity survives —
                // Visible only filters layout. An EXTERNAL (already-hidden) drag just cancels.
                if (!external)
                {
                    column.Visible = false;
                    _owner.NotifyColumnGeometryChanged();
                }

                break;

            case HeaderGesture.Reordering:
            {
                int slot = ComputeDropSlot(local, column);

                if (external)
                {
                    // Sweep [2]/[15] — the chooser's drag-to-show lands here: the drop ORDERS the
                    // still-hidden column at the slot (the move is anchor-instance-based, so it
                    // works pre-show), then reveals it; a cancelled slot leaves it hidden.
                    if (slot >= 0)
                    {
                        _owner.DropColumnAtSlot(column, slot);
                        column.Visible = true;
                        _owner.NotifyColumnGeometryChanged();
                    }
                }
                else
                {
                    _owner.DropColumnAtSlot(column, slot);
                }

                break;
            }
        }

        e.Handled = true;
    }

    protected override void OnLostMouseCapture(RoutedEventArgs e)
    {
        // Every gesture end funnels here (release, Esc cancel, a modal stealing capture): clear the
        // state + adorners. Resize needs no revert — widths applied live are authored truth; a
        // cancelled reorder simply never called Move.
        bool wasResizeOrDrag = _gesture is HeaderGesture.Resizing or HeaderGesture.Reordering;
        _gesture = HeaderGesture.None;
        _gestureEntry = -1;
        _dragColumn = null;
        DropSlot = -1;
        UnhookEscape();
        InvalidateVisual();

        if (wasResizeOrDrag)
            UIApplication.Current?.InputDispatcher.UpdateCursor(); // revert the transient shape

        base.OnLostMouseCapture(e);
    }

    /// <inheritdoc/>
    protected override void OnQueryCursor(QueryCursorEventArgs e)
    {
        base.OnQueryCursor(e);

        // Gesture state first (position-independent while captured), then the hover zone. The
        // per-frame §7.6 re-resolution rides _lastPointer, updated by OnMouseMove above.
        if (_gesture == HeaderGesture.Resizing)
        {
            e.Cursor = MouseCursorShape.EwResize;
            e.Handled = true;
        }
        else if (_gesture == HeaderGesture.Reordering)
        {
            e.Cursor = MouseCursorShape.Grabbing;
            e.Handled = true;
        }
        else if (_gesture == HeaderGesture.None && Layout is {} layout && _hoverEntry >= 0 &&
                 _hoverEntry < layout.Entries.Count && _lastPointerLocal is {} pointer &&
                 OnResizeEdge(layout, _hoverEntry, pointer.Column))
        {
            e.Cursor = MouseCursorShape.EwResize;
            e.Handled = true;
        }
    }

    private CellPosition? _lastPointerLocal;

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _lastPointerLocal = e.GetPosition(this);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _lastPointerLocal = null;

        if (_hoverEntry != -1)
        {
            _hoverEntry = -1;
            InvalidateVisual();
        }
    }

    // ── Reorder drag internals ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The insertion slot for a pointer position: over a cell's left half inserts before it, the
    /// right half after (the standard column-drag convention); past the right end appends. −1 while the
    /// pointer is in the hide zone (below the band) — no slot, release hides.
    /// </summary>
    private int ComputeDropSlot(CellPosition local, DataGridColumn column)
    {
        var layout = Layout;

        if (layout is null || layout.Entries.Count == 0)
            return -1;

        if (local.Row > HideZoneRows)
            return -1; // hide zone — the render pass drops the ▾/ghost to signal it

        // Sweep [1]: above the band with the group panel VISIBLE, the only live action is a valid
        // group drop (the caller's InGroupZone arm) — a REJECTED drop (AllowGroup=false / already
        // grouped) cancels instead of falling through to an accidental reorder from the pointer's x.
        if (local.Row < 0 && _owner is { ShowGroupPanel: true })
            return -1;

        int contentX = ContentXAt(local.Column);
        var entries = layout.Entries;
        int slot = entries.Count;

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            int span = entry.Width + 2 * DataGridColumnLayout.CellPadding;

            if (contentX < entry.X + span)
            {
                slot = contentX < entry.X + span / 2 ? i : i + 1;
                break;
            }
        }

        // Sweep [0]: the slot clamps to the DRAG COLUMN'S partition — the fixed-first layout
        // diverges from the Columns order, so a cross-partition slot both lied (the adorner
        // ghosted a frozen cell) and mis-landed or self-moved on release. Crossing the boundary
        // is Fixed-toggling territory, deliberately NOT a drag-reorder.
        return column.Fixed == DataGridColumnFixed.Left
                   ? Math.Min(slot, layout.FrozenCount)
                   : Math.Max(slot, layout.FrozenCount);
    }

    private void HookEscape()
    {
        if (_escapeHookRoot is not null || VisualRoot is not {} root)
            return;

        _escapeHookRoot = root;
        root.AddHandler(PreviewKeyDownEvent, OnRootPreviewKeyDown);
    }

    private void UnhookEscape()
    {
        _escapeHookRoot?.RemoveHandler(PreviewKeyDownEvent, OnRootPreviewKeyDown);
        _escapeHookRoot = null;
    }

    private void OnRootPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _gesture != HeaderGesture.Reordering)
            return;

        // Cancel: releasing capture funnels through OnLostMouseCapture, which clears every bit of
        // drag state (and this hook). The column never moved — nothing to undo.
        e.Handled = true;
        ReleaseMouseCapture();
    }
}