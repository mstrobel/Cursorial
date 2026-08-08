using Cursorial.Input;
using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Text;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Input;

namespace Cursorial.UI.DataViews;

/// <summary>
/// The direct-draw group panel band (design doc §3.1; the mockup's <c>grouppanel</c>): one cell row
/// of grouping-level chips — each chip carries the level's sort glyph (<c>▲/▼</c> per its
/// <see cref="GroupDescription.Direction"/>), the column caption, and an <c>✕</c> remove zone,
/// levels separated by a muted <c>▸</c>. Empty shows the ghosted drag prompt (drawn only when it
/// fits). Its own render boundary (ClipToBounds — a 1-row band, §3.1). Chips are live, riding the
/// header presenter's press-defer discipline: a press CAPTURES AND WAITS — a release without
/// threshold movement is the click (the ✕ zone removes the level; elsewhere toggles its direction
/// IN PLACE — a Replace edit, the observable collection being the one source of truth, so the
/// gesture edit reshapes and re-inks through the ordinary pipeline), while ≥2 cells of movement
/// promote a BODY press into a chip drag (floating <c>▣</c> chip + <c>▾</c> drop slot): release
/// in-band reorders the levels (<c>GroupDescriptions.Move</c>), release below the band ungroups
/// the dragged level, release above — or mid-drag Esc — cancels. Acting on the release instead of
/// the down is what makes a promoted drag never ALSO toggle/remove (the press-cancel rule). The
/// band collapses to zero rows while <see cref="DataGrid.ShowGroupPanel"/> is off (the Visibility
/// lane, kept in-measure so the template needs no binding).
/// </summary>
public sealed class DataGridGroupPanel : UIElement
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        UIProperty.Register<DataGridGroupPanel, IBrush?>(nameof(Background));

    public static readonly StyledProperty<IBrush?> ChipBackgroundProperty =
        UIProperty.Register<DataGridGroupPanel, IBrush?>(nameof(ChipBackground));

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        UIProperty.Register<DataGridGroupPanel, IBrush?>(nameof(TextBrush));

    public static readonly StyledProperty<IBrush?> GlyphBrushProperty =
        UIProperty.Register<DataGridGroupPanel, IBrush?>(nameof(GlyphBrush));

    public static readonly StyledProperty<IBrush?> PromptBrushProperty =
        UIProperty.Register<DataGridGroupPanel, IBrush?>(nameof(PromptBrush));

    static DataGridGroupPanel()
    {
        AffectsRender<DataGridGroupPanel>(
            BackgroundProperty, ChipBackgroundProperty, TextBrushProperty, GlyphBrushProperty, PromptBrushProperty);
    }

    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public IBrush? ChipBackground { get => GetValue(ChipBackgroundProperty); set => SetValue(ChipBackgroundProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? GlyphBrush { get => GetValue(GlyphBrushProperty); set => SetValue(GlyphBrushProperty, value); }
    public IBrush? PromptBrush { get => GetValue(PromptBrushProperty); set => SetValue(PromptBrushProperty, value); }

    private const string EmptyPrompt = "— drag a column header here to add a grouping level —";

    private DataGrid? _owner;

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
            InvalidateMeasure();
        }
    }

    // Every publish re-inks the band: chip glyphs render live GroupDescriptions state, and every
    // grouping edit funnels through a reshape (the observable-collections truth).
    private void OnSnapshotChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize)
        => _owner is { ShowGroupPanel: true }
            ? new Size(availableSize.Columns, 1)
            : new Size(availableSize.Columns, 0); // collapsed — the band spends no row

    /// <summary>
    /// One chip's geometry: <c>[pad][▲][sp]Header[sp][✕][pad]</c> — the ✕ zone is the trailing 2
    /// cells. Deterministic from the descriptions alone, so hit testing never depends on a render
    /// having run (the header presenter's EntryAt posture).
    /// </summary>
    private readonly record struct Chip(int Index, DataGridColumn Column, int X, int Width)
    {
        public int RemoveZoneStart => X + Width - 2;
    }

    private IEnumerable<Chip> Chips()
    {
        var owner = _owner;
        if (owner is null)
            yield break;

        int x = 1;
        for (int i = 0; i < owner.GroupDescriptions.Count; i++)
        {
            if (owner.GroupDescriptions[i].ColumnKey is not DataGridColumn column)
                continue;
            int width = GraphemeWidth.StringWidth(column.EffectiveHeader) + 6; // pad+glyph+sp … sp+✕+pad
            yield return new Chip(i, column, x, width);
            x += width + 3; // " ▸ " separator
        }
    }

    protected override void Render(RenderContext context)
    {
        base.Render(context);
        var owner = _owner;
        if (owner is null || Bounds.Rows < 1)
            return;

        if (Background is not null)
            context.FillOpaque(new Rect(0, 0, Bounds.Columns, 1), Background);

        bool any = false;

        var glyphBrush = GlyphBrush ?? TextBrush ?? Brushes.Default;
            var promptBrush = PromptBrush ?? TextBrush ??  Brushes.Default;

        foreach (var chip in Chips())
        {
            if (any) // separator before every chip but the first
                context.DrawText(chip.X - 2, 0, "▸", promptBrush);
            any = true;

            if (ChipBackground is not null)
                context.FillOpaque(new Rect(chip.X, 0, chip.Width, 1), ChipBackground);

            // The §3.3 virtual band focus: the focused chip wears the glyph accent as a frame
            // (⟨…⟩ corners drawn in the padding cells — no new theme key for a keyboard cue).
            if (chip.Index == owner.GroupChipFocusIndex)
            {
                context.DrawText(chip.X, 0, "⟨", glyphBrush);
                context.DrawText(chip.X + chip.Width - 1, 0, "⟩", glyphBrush);
            }

            var direction = owner.GroupDescriptions[chip.Index].Direction;
            context.DrawText(chip.X + 1, 0, direction == SortDirection.Ascending ? "▲" : "▼", glyphBrush);
            context.DrawText(chip.X + 3, 0, chip.Column.EffectiveHeader, TextBrush ?? Brushes.Default);
            context.DrawText(chip.RemoveZoneStart, 0, "✕", promptBrush);
        }

        if (!any && GraphemeWidth.StringWidth(EmptyPrompt) <= Bounds.Columns - 2)
            context.DrawText(1, 0, EmptyPrompt, PromptBrush ?? Brushes.Default); // drawn only when it fits (no truncated prompt)

        // ── Chip-drag overlay: the ▾ drop slot + the floating ▣ chip (drawn LAST, over all — the
        // header presenter's adorner idiom) ──────────────────────────────────────────────────────
        if (_gesture == ChipGesture.Dragging && _pressColumn is { } dragColumn)
        {
            // The ▾ marks the live insertion boundary between chips. Suppressed beside the dragged
            // chip itself (a no-op slot) and whenever the pointer is off the band (DropSlot −1) —
            // the cue never promises a no-op (the header's slotLive discipline).
            int dragIndex = IndexOfLevel(owner, dragColumn);
            bool slotLive = DropSlot >= 0 && dragIndex >= 0 &&
                            DropSlot != dragIndex && DropSlot != dragIndex + 1;
            if (slotLive)
            {
                int boundaryX = SlotBoundaryX(DropSlot);
                if (boundaryX >= 0 && boundaryX < Bounds.Columns)
                    context.DrawText(boundaryX, 0, "▾", glyphBrush);
            }

            // The floating chip follows the pointer, clamped into the 1-row band (ClipToBounds —
            // there is nowhere else to float it). Accent ink in-band; muted ink once the pointer
            // leaves the band — the header's hide-zone honesty cue that release is no longer a
            // reorder (below = ungroup, above = cancel).
            string chip = "▣ " + dragColumn.EffectiveHeader;
            int chipWidth = GraphemeWidth.StringWidth(chip);
            int chipX = Math.Clamp(_dragLocal.Column, 0, Math.Max(0, Bounds.Columns - chipWidth));
            if (ChipBackground is not null)
                context.FillOpaque(new Rect(chipX, 0, chipWidth, 1), ChipBackground);
            context.DrawText(chipX, 0, chip, _dragLocal.Row == 0 ? glyphBrush : promptBrush);
        }
    }

    // ── Chip gesture state (the header presenter's Pending→threshold→drag pattern) ──────────────
    //
    // One left-button gesture at a time, disambiguated at PRESS by zone within the hit chip, with
    // the ACTION deferred to the release: a body press is Pending (sub-threshold release = the
    // direction-toggle click; ≥2 cells of movement promote to a chip drag), a ✕ press is Pending
    // too (sub-threshold release = remove) but movement CANCELS it rather than promoting — the ✕
    // is a destructive affordance, and a drag that started on it must neither remove nor reorder.
    // Deferring the click actions from the down to the up is the press-cancel fix: a promoted drag
    // can never ALSO toggle/remove.
    //
    // Zone geometry (documented decision): the panel is a 1-ROW band, so unlike the header's
    // HideZoneRows=2 buffer there is no jitter allowance below it — the very next row down IS the
    // header band, the DevExpress "drag the chip back onto the header" ungroup target, and an
    // inert buffer row would swallow a meaningful target. ANY release with local.Row > 0 while a
    // drag is live therefore UNGROUPS the dragged level (mirroring the header's drag-off-to-hide);
    // the cue keeps it honest — the ▾ slot drops out and the floating chip re-inks muted the
    // moment the pointer leaves the band, and Esc always bails. Above the band (local.Row < 0)
    // there is nothing to target — release there cancels.
    private enum ChipGesture { None, Pending, Dragging, Canceled }

    private ChipGesture _gesture;
    private DataGridColumn? _pressColumn;   // the pressed chip's column (indices re-resolve at release)
    private bool _pressOnRemoveZone;        // press landed on the ✕ — release removes, movement cancels
    private CellPosition _gestureAnchor;    // SCREEN position at press (deltas stay honest under capture)
    private CellPosition _dragLocal;        // last pointer position local to the band (adorners)
    private UIElement? _escapeHookRoot;     // the root carrying the mid-drag Esc tunnel handler

    /// <summary>The drop slot (insertion index in GroupDescriptions space) the drag targets, or −1.</summary>
    internal int DropSlot { get; private set; } = -1;

    /// <summary>Whether a chip drag is live (tests observe promotion/cancel).</summary>
    internal bool IsDraggingChip => _gesture == ChipGesture.Dragging;

    /// <summary>Movement (cells, either axis) that promotes a pending body press into a chip drag.</summary>
    private const int DragThreshold = 2;

    /// <summary>The dragged column's CURRENT level index (re-resolved so a shifted collection can't mis-target).</summary>
    private static int IndexOfLevel(DataGrid owner, DataGridColumn column)
    {
        for (int i = 0; i < owner.GroupDescriptions.Count; i++)
        {
            if (ReferenceEquals(owner.GroupDescriptions[i].ColumnKey, column))
                return i;
        }
        return -1;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Handled || e.Button != MouseButton.Left || _owner is null)
            return;

        var position = e.GetPosition(this);
        foreach (var chip in Chips())
        {
            if (position.Column < chip.X || position.Column >= chip.X + chip.Width)
                continue;

            // Capture and wait — a release without threshold movement IS the click (toggle/✕).
            if (CaptureMouse())
            {
                _gesture = ChipGesture.Pending;
                _pressColumn = chip.Column;
                _pressOnRemoveZone = position.Column >= chip.RemoveZoneStart;
                _gestureAnchor = e.ScreenPosition;
                _dragLocal = position;
            }
            e.Handled = true;
            return;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);

        switch (_gesture)
        {
            case ChipGesture.Pending:
            {
                int dx = Math.Abs(e.ScreenPosition.Column - _gestureAnchor.Column);
                int dy = Math.Abs(e.ScreenPosition.Row - _gestureAnchor.Row);
                if (dx < DragThreshold && dy < DragThreshold)
                    return; // sub-threshold jitter — still a click-in-waiting

                if (_pressOnRemoveZone)
                {
                    // A ✕ press that moves is a CANCELLED click, never a drag: promoting the
                    // destructive affordance into a reorder would put a remove-or-move under an
                    // accidental twitch's release. Capture holds until the up (which does nothing).
                    _gesture = ChipGesture.Canceled;
                    return;
                }

                // Promote to a chip drag: hook the mid-drag Esc cancel on the ROOT's tunnel pass
                // (the band is never focusable, so its own key virtuals can't fire; the preview
                // tunnel sees the key first wherever focus sits inside this surface).
                _gesture = ChipGesture.Dragging;
                HookEscape();
                UIApplication.Current?.InputDispatcher.UpdateCursor(); // Grabbing
                goto case ChipGesture.Dragging;
            }

            case ChipGesture.Dragging:
                _dragLocal = position;
                DropSlot = ComputeDropSlot(position);
                InvalidateVisual();
                e.Handled = true;
                return;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButton.Left || _gesture == ChipGesture.None)
            return;

        var gesture = _gesture;
        var column = _pressColumn;
        bool onRemoveZone = _pressOnRemoveZone;
        var local = e.GetPosition(this);

        // Release-then-act: ReleaseMouseCapture raises LostMouseCapture, whose handler clears the
        // gesture state — snapshot first, then let the abort path do the (idempotent) cleanup.
        ReleaseMouseCapture();
        e.Handled = true;

        var owner = _owner;
        if (owner is null || column is null)
            return;

        int index = IndexOfLevel(owner, column);
        if (index < 0)
            return; // the level vanished under the capture (programmatic edit) — nothing to act on

        switch (gesture)
        {
            case ChipGesture.Pending when onRemoveZone:
                // The ✕ click, on the RELEASE (a press that dragged away went Canceled instead).
                owner.GroupDescriptions.RemoveAt(index);
                break;

            case ChipGesture.Pending:
            {
                // The body click: toggle the level's direction IN PLACE (a Replace edit; `with`
                // preserves OrderBySummary/SummaryDirection — the toggle only flips the key order).
                var description = owner.GroupDescriptions[index];
                owner.GroupDescriptions[index] = description with
                {
                    Direction = description.Direction == SortDirection.Ascending
                        ? SortDirection.Descending
                        : SortDirection.Ascending,
                };
                break;
            }

            case ChipGesture.Dragging when local.Row > 0:
                // Below the band (the header/rows area — any Row > 0; see the zone-geometry note):
                // ungroup the dragged level, the drag-off mirror of the header's drop-to-group.
                owner.GroupDescriptions.RemoveAt(index);
                break;

            case ChipGesture.Dragging:
            {
                // In-band (Row == 0): reorder. Above the band ComputeDropSlot returned −1 (cancel);
                // beside-itself slots are the suppressed no-ops the adorner never promised.
                int slot = ComputeDropSlot(local);
                if (slot >= 0 && slot != index && slot != index + 1)
                    owner.GroupDescriptions.Move(index, slot > index ? slot - 1 : slot);
                break;
            }

            // ChipGesture.Canceled: the dragged-away ✕ press — release does nothing.
        }
    }

    protected override void OnLostMouseCapture(RoutedEventArgs e)
    {
        // Every gesture end funnels here (release, Esc cancel, capture theft): clear the state +
        // adorners. A canceled drag simply never called Move/RemoveAt — nothing to undo.
        bool wasDragging = _gesture == ChipGesture.Dragging;
        _gesture = ChipGesture.None;
        _pressColumn = null;
        _pressOnRemoveZone = false;
        DropSlot = -1;
        UnhookEscape();
        InvalidateVisual();
        if (wasDragging)
            UIApplication.Current?.InputDispatcher.UpdateCursor(); // revert the Grabbing shape
        base.OnLostMouseCapture(e);
    }

    /// <inheritdoc/>
    protected override void OnQueryCursor(QueryCursorEventArgs e)
    {
        base.OnQueryCursor(e);
        if (_gesture == ChipGesture.Dragging)
        {
            e.Cursor = MouseCursorShape.Grabbing;
            e.Handled = true;
        }
    }

    /// <summary>
    /// The insertion slot (GroupDescriptions index space, via the deterministic <see cref="Chips"/>
    /// geometry) for an in-band pointer position: a chip's left half inserts before it, the right
    /// half after; past the right end appends. −1 off the band (below = the ungroup zone, above =
    /// cancel — no slot either way, so the adorner drops the ▾).
    /// </summary>
    private int ComputeDropSlot(CellPosition local)
    {
        if (local.Row != 0)
            return -1;

        Chip? last = null;
        foreach (var chip in Chips())
        {
            if (local.Column < chip.X + chip.Width)
                return local.Column < chip.X + chip.Width / 2 ? chip.Index : chip.Index + 1;
            last = chip;
        }
        return last is { } l ? l.Index + 1 : -1;
    }

    /// <summary>The band x of a slot's insertion boundary (the cell left of the chip it precedes).</summary>
    private int SlotBoundaryX(int slot)
    {
        Chip? last = null;
        foreach (var chip in Chips())
        {
            if (chip.Index >= slot)
                return chip.X - 1;
            last = chip;
        }
        return last is { } l ? l.X + l.Width : -1;
    }

    private void HookEscape()
    {
        if (_escapeHookRoot is not null || VisualRoot is not { } root)
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
        if (e.Key != Key.Escape || _gesture != ChipGesture.Dragging)
            return;
        // Cancel: releasing capture funnels through OnLostMouseCapture, which clears every bit of
        // drag state (and this hook). The level never moved — nothing to undo.
        e.Handled = true;
        ReleaseMouseCapture();
    }
}
