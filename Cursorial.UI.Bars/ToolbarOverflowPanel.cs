using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.UI.Bars;

/// <summary>
/// The layout engine behind <see cref="Toolbar"/>'s discrete overflow (the Actipro live-control re-parent model).
/// It is the toolbar's <c>ItemsPanel</c> (<see cref="Panel.IsItemsHost"/> = true) and an <see cref="IItemsHostPanel"/>
/// that <b>owns its own container adoption</b> — so the <see cref="ItemsPresenter"/> steps back from its index-aligned
/// sync (it would fight the row/popup split). The panel keeps the authoritative ordered container list in sync with
/// the generator, runs a discrete two-pass cell-fit pack in <see cref="MeasureOverride"/>, and distributes the
/// <b>live</b> container instances between two bands by moving them through the bands' public
/// <see cref="Panel.Children"/> collections:
/// <list type="bullet">
/// <item>the <b>row band</b> — this panel itself, on the toolbar's main surface; and</item>
/// <item>the <b>overflow band</b> — a vertical host panel inside the toolbar's overflow <c>Popup</c> (set via
/// <see cref="OverflowHost"/>), which renders only while the popup is open.</item>
/// </list>
/// A re-parent is a pure visual-only move (both bands are <see cref="Panel.IsItemsHost"/>), so an overflowed control
/// stays a logical child of the <see cref="Toolbar"/> — its DataContext / command / style inheritance is unbroken in
/// either band. The chevron affordance lives beside the panel in the toolbar template and is referenced here
/// (<see cref="OverflowToggle"/>) only so its width can be reserved.
/// </summary>
public sealed class ToolbarOverflowPanel : Panel, IItemsHostPanel
{
    // Authoritative ordered container list (synced from the generator — NOT the bands, which hold prefix/tail subsets).
    private readonly List<UIElement> _containers = [];

    private ItemsControl? _owner;
    private Toolbar? _toolbar;
    private ItemContainerGenerator? _generator;

    private Panel? _overflowHost; // the popup-side vertical band (set by Toolbar.OnApplyTemplate)
    private UIElement? _chevron;  // the overflow affordance (measured for its reserve width; arranged by the template)
    private int _chevronWidth;    // the chevron's measured reserve width (carried from MeasureOverride to the arrange-time fold)

    // Anti-thrash latch (the mandatory convergence guard, mirroring VirtualizingStackPanel's _hasMeasured). The
    // arrange-time fold + re-parent is skipped when none of the fold inputs changed, so the re-entrant
    // InvalidateMeasure a re-parent arms settles immediately. (The idempotent reconcile is the real convergence
    // guarantee; the latch avoids the redundant fold computation on the confirm pass.)
    private bool _hasFolded;
    private int _lastFinalWidth = -1;
    private int _lastContainerCount = -1;
    private int _lastWidthHash;
    private int _lastChevronWidth = -1;
    private int _lastModeHash;

    // Reused fold scratch (allocated once; cleared per fold so a latch-miss pass adds no garbage beyond growth).
    private readonly List<UIElement> _visible = [];
    private readonly List<UIElement> _overflow = [];

    /// <summary>The popup-side vertical band the panel pushes overflowed containers into (the toolbar's
    /// <c>PART_OverflowHost</c>). Until set, the panel keeps every item on the row (clipped if too wide).</summary>
    public Panel? OverflowHost
    {
        get => _overflowHost;
        set
        {
            if (ReferenceEquals(_overflowHost, value))
                return;
            _overflowHost = value;
            ResetFoldLatch(); // the host band changed — force a re-fold (the size/width signals may be unchanged)
            InvalidateMeasure();
        }
    }

    /// <summary>The chevron affordance whose width the panel reserves when folding (the toolbar's
    /// <c>PART_OverflowToggle</c>). The panel measures it every pass (so it must never be
    /// <see cref="Visibility.Collapsed"/> — a collapsed element measures to 0, which would break the reserve).</summary>
    public UIElement? OverflowToggle
    {
        get => _chevron;
        set
        {
            if (ReferenceEquals(_chevron, value))
                return;
            _chevron = value;
            ResetFoldLatch(); // the chevron changed — its reserve width feeds the fold; force a re-fold
            InvalidateMeasure();
        }
    }

    /// <summary>Forces a full re-fold on the next arrange (resets the latch + invalidates measure) for a change the
    /// fold's size/width/chevron signals don't capture — e.g. a per-item <see cref="ToolbarOverflowMode"/> flip on a
    /// container currently in the popup band, whose <see cref="UIElement.VisualParent"/> isn't this panel.</summary>
    internal void InvalidateFold()
    {
        ResetFoldLatch();
        InvalidateMeasure();
    }

    // ───────────────────────────── IItemsHostPanel (the adoption seam) ─────────────────────────────

    // The Toolbar always owns its row/popup distribution (regardless of generator mode), so the presenter must skip
    // its index-aligned Children sync entirely.
    bool IItemsHostPanel.ManagesContainerAdoption => true;

    void IItemsHostPanel.OnItemsHostConnected(ItemsControl owner)
    {
        _owner = owner;
        _toolbar = owner as Toolbar;
        _generator = owner.ItemContainerGenerator;
        _generator.ContainersChanged += OnContainersChanged;

        // Catch up to whatever the generator already realized (it may have run before this panel existed — mirrors
        // ItemsPresenter's attach-time SyncAll). Bands are populated lazily by the first MeasureOverride fold.
        _containers.Clear();
        for (var i = 0; i < _generator.ContainerCount; i++)
            if (_generator.ContainerFromIndex(i) is { } container)
                _containers.Add(container);

        _toolbar?.RegisterOverflowPanel(this);
        ResetFoldLatch();
        InvalidateMeasure();
    }

    void IItemsHostPanel.OnItemsHostDisconnected()
    {
        _toolbar?.UnregisterOverflowPanel(this);

        if (_generator is not null)
            _generator.ContainersChanged -= OnContainersChanged;

        // Release BOTH bands so a re-attach / re-template re-adopts cleanly (logical parentage stays the Toolbar's).
        // The presenter clears this panel's Children itself after this returns; we additionally clear the popup band.
        _overflowHost?.Children.Clear();
        Children.Clear();
        _containers.Clear();
        _generator = null;
        _owner = null;
        _toolbar = null;
        _overflowHost = null;
        _chevron = null;
        ResetFoldLatch();
    }

    private void OnContainersChanged(object? sender, ContainersChangedEventArgs e)
    {
        if (_generator is null)
            return;

        switch (e.Action)
        {
            case ContainersChangedAction.Realized:
                for (var i = 0; i < e.Count; i++)
                    if (_generator.ContainerFromIndex(e.StartIndex + i) is { } container)
                        _containers.Insert(Math.Min(e.StartIndex + i, _containers.Count), container);
                break;

            case ContainersChangedAction.Unrealized:
                // CD-P9-3: the generator fires this AFTER ClearContainerForItem but BEFORE FinishUnrealize (its logical
                // detach). The host's VISUAL detach must happen HERE, synchronously, so it precedes the logical detach
                // — deferring it to the next measure would run RemoveContainerLogical while the container is still
                // visually parented to a band, leaving a dangling visual parent and an un-retracted value store.
                if (e.RemovedContainers is { } removed)
                    foreach (var container in removed)
                    {
                        _containers.Remove(container);
                        DetachFromBands(container);
                    }
                break;

            case ContainersChangedAction.Moved:
            {
                var block = new UIElement[e.Count];
                for (var i = 0; i < e.Count; i++)
                    block[i] = _generator.ContainerFromIndex(e.StartIndex + i)!;
                foreach (var container in block)
                    _containers.Remove(container);
                for (var i = 0; i < block.Length; i++)
                    _containers.Insert(Math.Min(e.StartIndex + i, _containers.Count), block[i]);
                break;
            }

            default:
            case ContainersChangedAction.Reset:
                // Release both bands, then rebuild the ordered list from the fresh generation.
                _overflowHost?.Children.Clear();
                Children.Clear();
                _containers.Clear();
                for (var i = 0; i < _generator.ContainerCount; i++)
                    if (_generator.ContainerFromIndex(i) is { } container)
                        _containers.Add(container);
                break;
        }

        ResetFoldLatch(); // the container set changed — force a re-fold
        InvalidateMeasure();
    }

    // ───────────────────────────── layout (the fold) ─────────────────────────────

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        // Measure-only: the FOLD lives in ArrangeOverride, because the available width passed to Measure is unreliable
        // for a fold decision — a Stretch toolbar is measured to-content (unbounded) and only ARRANGED at its real
        // width, so a measure-time fold never sees the true width and the row never unfolds when the bar widens.
        // arrangeFinalSize is always the real allocated width. Here we just measure children (natural widths) + the
        // chevron and report the natural content size (StackPanel semantics: the parent clamps; arrange folds).
        _chevronWidth = 0;
        if (_chevron is not null)
        {
            // The chevron is measured every pass so its reserve width is known at fold time (Visibility.Hidden still
            // measures — only Collapsed zeroes it). It is a template sibling, so the panel never arranges it.
            _chevron.Measure(new Size(LayoutMath.Unbounded, availableSize.Rows));
            _chevronWidth = _chevron.DesiredSize.Columns;
        }

        var naturalSum = 0;
        var rowHeight = 0;
        for (var i = 0; i < _containers.Count; i++)
        {
            var c = _containers[i];
            c.Measure(new Size(LayoutMath.Unbounded, availableSize.Rows));
            naturalSum = LayoutMath.Add(naturalSum, c.DesiredSize.Columns);
            rowHeight = Math.Max(rowHeight, c.DesiredSize.Rows);
        }

        return new Size(naturalSum, _containers.Count == 0 ? 0 : Math.Max(1, rowHeight));
    }

    // The discrete two-pass pack (mirrors the toolbar design guide + WrapPanel's strictly-greater wrap test — an
    // exact fit STAYS on the row). Fills _visible (row band, document order) and _overflow (popup band, document order).
    private void ComputeFold(int availableWidth, int chevronWidth)
    {
        _visible.Clear();
        _overflow.Clear();

        // With no overflow band wired yet (pre-OnApplyTemplate), everything stays on the row — there is nowhere to
        // push a tail, so the row clips rather than dropping items.
        if (_overflowHost is null)
        {
            _visible.AddRange(_containers);
            return;
        }

        var anyAlways = false;
        var neverWidth = 0;
        foreach (var c in _containers)
        {
            var mode = ModeOf(c);
            if (mode == ToolbarOverflowMode.Always)
                anyAlways = true;
            else if (mode == ToolbarOverflowMode.Never)
                neverWidth = LayoutMath.Add(neverWidth, c.DesiredSize.Columns);
        }

        // PASS 1 — does the full set fit with NO chevron reserve (and no Always item forcing one)?
        if (!anyAlways)
        {
            var total = 0;
            var fits = true;
            foreach (var c in _containers)
            {
                total = LayoutMath.Add(total, c.DesiredSize.Columns);
                if (total > availableWidth) { fits = false; break; } // strictly-greater: exact fit stays
            }
            if (fits)
            {
                _visible.AddRange(_containers);
                return;
            }
        }

        // PASS 2 — overflow exists (or an Always item forces the chevron): re-pack against the reserved boundary.
        var boundary = Math.Max(0, availableWidth - chevronWidth);
        var running = neverWidth; // pinned Never items are charged unconditionally
        var folded = false;       // once an AsNeeded item overflows, every later AsNeeded item overflows too
        foreach (var c in _containers)
        {
            switch (ModeOf(c))
            {
                case ToolbarOverflowMode.Never:
                    _visible.Add(c); // already charged via neverWidth
                    break;
                case ToolbarOverflowMode.Always:
                    _overflow.Add(c);
                    break;
                default: // AsNeeded
                    if (folded)
                        _overflow.Add(c);
                    else if (LayoutMath.Add(running, c.DesiredSize.Columns) > boundary)
                    {
                        folded = true;
                        _overflow.Add(c);
                    }
                    else
                    {
                        running = LayoutMath.Add(running, c.DesiredSize.Columns);
                        _visible.Add(c);
                    }
                    break;
            }
        }

        // LD10 — a first item wider than the bar must never vanish into an empty popup: if nothing landed on the row,
        // pull the first overflowed non-Always item back (it clips at the zone edge).
        if (_visible.Count == 0)
        {
            for (var i = 0; i < _overflow.Count; i++)
                if (ModeOf(_overflow[i]) != ToolbarOverflowMode.Always)
                {
                    _visible.Add(_overflow[i]);
                    _overflow.RemoveAt(i);
                    break;
                }
        }
    }

    // Drop leading/trailing separators and collapse adjacent runs (a band never starts or ends with │, and never
    // shows two in a row) — a pure post-process over the fold result; it never re-packs (a separator pulled off the
    // tail can't un-overflow a command item, so this is monotone and cannot oscillate).
    private static void FilterSeparators(List<UIElement> band)
    {
        for (var i = band.Count - 1; i >= 0; i--)
        {
            if (band[i] is not BarSeparator)
                continue;

            var prevIsSeparatorOrStart = i == 0 || band[i - 1] is BarSeparator;
            var nextIsSeparatorOrEnd = i == band.Count - 1 || band[i + 1] is BarSeparator;
            if (prevIsSeparatorOrStart || nextIsSeparatorOrEnd)
                band.RemoveAt(i);
        }
    }

    // Move the live containers so the row band holds exactly _visible and the overflow band holds exactly _overflow
    // (trimmed separators land in neither — they render nowhere). Only boundary-crossers move; idempotent at steady
    // state, so it cannot oscillate even without the latch.
    private void ReconcileBands(bool hasOverflow)
    {
        // 1) Evict trimmed/non-rendered containers from whichever band currently holds them.
        foreach (var c in _containers)
            if (!_visible.Contains(c) && !_overflow.Contains(c))
                DetachFromBands(c);

        // 2) Row band = _visible, in order.
        PlaceInBand(this, _visible);

        // 3) Overflow band = _overflow, in order (only if a host is wired; otherwise everything was kept on the row).
        if (_overflowHost is not null)
            PlaceInBand(_overflowHost, _overflow);

        _ = hasOverflow; // reserved for future affordance hooks
    }

    // Ensure `band.Children` is exactly `list` in order, moving in any container not already there (removing it from
    // the other band first) and reordering the rest. Mirrors ItemsPresenter.SyncAll's index reconcile.
    private void PlaceInBand(Panel band, List<UIElement> list)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var c = list[i];
            var current = band.Children.IndexOf(c);
            if (current < 0)
            {
                DetachFromBands(c); // remove from the other band (if any) before adopting — single visual parent
                band.Children.Insert(Math.Min(i, band.Children.Count), c);
            }
            else if (current != i)
            {
                band.Children.Move(current, i);
            }
        }
    }

    private void DetachFromBands(UIElement container)
    {
        if (Children.Contains(container))
            Children.Remove(container);
        else if (_overflowHost is not null && _overflowHost.Children.Contains(container))
            _overflowHost.Children.Remove(container);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_containers.Count == 0)
        {
            _toolbar?.SetOverflowState(hasOverflow: false, overflowCount: 0);
            _hasFolded = false;
            return finalSize;
        }

        // Fold against the REAL allocated width (finalSize) — reliable for every alignment (Stretch → slot width,
        // explicit Width → that width, Left auto-size → content = no overflow). The latch skips the fold + re-parent
        // when nothing relevant changed, so the re-entrant InvalidateMeasure a re-parent arms settles immediately.
        // The width signal is a per-item polynomial hash, NOT the SUM: a plain sum hides two items trading widths
        // (A +N, B −N) even though the fold boundary moves, which would wrongly reuse a stale fold.
        var widthHash = 17;
        var modeHash = 17;
        foreach (var c in _containers)
        {
            widthHash = widthHash * 31 + c.DesiredSize.Columns;
            modeHash = modeHash * 31 + (int) ModeOf(c) + 1;
        }

        if (!(_hasFolded
              && finalSize.Columns == _lastFinalWidth
              && _containers.Count == _lastContainerCount
              && widthHash == _lastWidthHash
              && _chevronWidth == _lastChevronWidth
              && modeHash == _lastModeHash))
        {
            ComputeFold(finalSize.Columns, _chevronWidth);
            FilterSeparators(_visible);
            FilterSeparators(_overflow);

            var hasOverflow = _overflow.Count > 0;
            ReconcileBands(hasOverflow);
            _toolbar?.SetOverflowState(hasOverflow, CountCommands(_overflow));

            _lastFinalWidth = finalSize.Columns;
            _lastContainerCount = _containers.Count;
            _lastWidthHash = widthHash;
            _lastChevronWidth = _chevronWidth;
            _lastModeHash = modeHash;
            _hasFolded = true;
        }

        // Place the row band (this.Children == _visible after reconcile) left-to-right at natural widths. The chevron
        // (a template sibling) is arranged by the toolbar template at the right edge; the overflow band is arranged by
        // the popup host. Content exceeding finalSize clips at the zone edge (LD10 / Never overrun).
        var x = 0;
        var children = Children;
        for (var i = 0; i < children.Count; i++)
        {
            var c = children[i];
            var w = c.DesiredSize.Columns;
            c.Arrange(new Rect(x, 0, w, finalSize.Rows));
            x = LayoutMath.Add(x, w);
        }
        return finalSize;
    }

    private ToolbarOverflowMode ModeOf(UIElement container) => Toolbar.GetOverflowMode(container);

    private static int CountCommands(List<UIElement> band)
    {
        var n = 0;
        foreach (var c in band)
            if (c is not BarSeparator)
                n++;
        return n;
    }

    // The _hasFolded=false flag is the real gate; the -1/0 field values are belt-and-suspenders sentinels (the
    // width/mode hashes seed at 17 and grow, so 0 is unreachable from a real fold, and widths/counts are never < 0).
    private void ResetFoldLatch()
    {
        _hasFolded = false;
        _lastFinalWidth = -1;
        _lastContainerCount = -1;
        _lastWidthHash = 0;
        _lastChevronWidth = -1;
        _lastModeHash = 0;
    }
}
