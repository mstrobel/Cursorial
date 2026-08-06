using Cursorial.Rendering;
using Cursorial.UI.Controls;

namespace Cursorial.UI.Bars;

/// <summary>
/// The compact ribbon's tab-strip layout (bars guide §"QAT placement in the compact ribbon"): left-packed tabs fill
/// the strip while a trailing column right-hugs the strip's dead space, hosting the Quick Access Toolbar on the tab-
/// LABEL row and the minimize pin on the selection-UNDERLINE row.
///
/// <para>Its one job beyond placement is the guide's <b>[DECISION] collapse-first overflow</b>: when the width is
/// tight enough that the inline QAT and the tabs would compete for cells, the <b>QAT collapses first (before the
/// tabs)</b> to a single <c>⋯▾</c> popup button — tabs always win the space contest, since losing a tab is worse than
/// tucking pinned commands behind a popup. The panel only <i>decides</i> (does <c>tabs + inline-QAT</c> fit?) and
/// reports it via <see cref="IsCollapsed"/> / <see cref="CollapseChanged"/>; the <see cref="Ribbon"/> owns the
/// response (stamping <c>:qat-collapsed</c> so the theme flips the inline cluster for the <c>⋯▾</c> button, and
/// re-hosting the QAT commands into the popup). The decision reads the inline QAT's cached natural width, so the
/// theme collapsing that cluster to zero can't feed back and oscillate.</para>
/// </summary>
public sealed class RibbonStripPanel : Panel
{
    // The role children, assigned by the ribbon template (positional identity would be brittle across theme edits).
    // Tabs fill the left; QatFull is the inline QAT cluster; QatCollapsed is the ⋯▾ popup button shown when the inline
    // QAT can't fit; Pin is the minimize chevron on the underline row. All four are also this panel's Children.
    /// <summary>The tab items host (fills the left; clips at the trailing column when the strip is too narrow).</summary>
    public UIElement? Tabs { get; set; }

    /// <summary>The inline QAT cluster (commands + customize ▾) shown on the label row when it fits beside the tabs.</summary>
    public UIElement? QatFull { get; set; }

    /// <summary>The <c>⋯▾</c> popup button shown on the label row (in place of <see cref="QatFull"/>) when the inline
    /// QAT would compete with the tabs for cells.</summary>
    public UIElement? QatCollapsed { get; set; }

    /// <summary>The minimize pin, right-hugging the selection-underline row (unaffected by the QAT collapse).</summary>
    public UIElement? Pin { get; set; }

    // A minimum breathing gutter between the left-packed tabs and the trailing QAT, so the QAT collapses just BEFORE it
    // would butt against the last tab rather than at the exact touch point (the guide's "natural gutter").
    private const int MinGutter = 1;

    // The inline QAT's last known natural (unbounded) width, captured while it is VISIBLE. Once the ribbon collapses
    // the inline cluster (Visibility.Collapsed on :qat-collapsed), it measures to 0 — reading that live zero into the
    // fit test would make the collapsed QAT always "fit", so the panel would immediately un-collapse and thrash. The
    // cache is the fold input so the decision is stable across the collapse it triggers.
    private int _qatFullNaturalWidth;

    private bool _collapsed;

    /// <summary>Whether the inline QAT currently doesn't fit beside the tabs (the collapse-first verdict). The
    /// <see cref="Ribbon"/> combines this with placement + has-QAT to decide the effective collapse.</summary>
    public bool IsCollapsed => _collapsed;

    /// <summary>Raised when <see cref="IsCollapsed"/> flips (a width change crossed the fit threshold). The ribbon
    /// re-evaluates its effective collapse state on this signal.</summary>
    public event EventHandler? CollapseChanged;

    // The tabs' natural width, carried from Measure to the arrange-time fold (the fold reads it against the real
    // allocated width). Cached because Arrange must not re-measure.
    private int _tabsNaturalWidth;

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = availableSize.Rows;
        _tabsNaturalWidth = MeasureNatural(Tabs, rows);

        var qatFullW = MeasureNatural(QatFull, rows);
        // Refresh the cached inline width ONLY while genuinely uncollapsed. During the collapse transition the ribbon
        // re-hosts the QAT commands OUT of the (still-Visible, pre-restyle) cluster before the theme's :qat-collapsed
        // Visibility flip lands — so the cluster momentarily measures narrow (the ▾ alone, >0). Re-seeding the cache
        // from that emptied-but-visible width would flip the fold verdict back and cost an extra convergence pass;
        // freezing it at the last real inline-with-commands width keeps the verdict stable across the re-host.
        if (qatFullW > 0 && !_collapsed)
            _qatFullNaturalWidth = qatFullW;

        var qatCollapsedW = MeasureNatural(QatCollapsed, rows);
        var pinW = MeasureNatural(Pin, rows);

        // Measure-only: the collapse verdict lives in ArrangeOverride, which mutates sibling subtrees (the ribbon
        // re-hosts the QAT commands on the flip) — the ToolbarOverflowPanel precedent keeps that tree mutation out of
        // the measure pass. Report the natural content size; the DockPanel clamps the stretched strip to its width.
        var trailingW = Math.Max(Math.Max(qatFullW, qatCollapsedW), pinW);
        var width = LayoutMath.IsUnbounded(availableSize.Columns)
            ? LayoutMath.Add(_tabsNaturalWidth, trailingW)
            : availableSize.Columns;
        var height = Math.Max(1, Max4(HeightOf(Tabs), HeightOf(QatFull), HeightOf(QatCollapsed), HeightOf(Pin)));
        return new Size(width, height);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var available = finalSize.Columns;
        var rows = finalSize.Rows;

        // The collapse-first fold against the REAL allocated width. Tabs win: collapse when the inline QAT plus a
        // gutter would push past the edge. The verdict reads the CACHED inline-QAT natural width, so the ribbon
        // collapsing that cluster to zero (on the :qat-collapsed we trigger) can't feed back and oscillate.
        var collapsed = LayoutMath.Add(LayoutMath.Add(_tabsNaturalWidth, _qatFullNaturalWidth), MinGutter) > available;
        if (collapsed != _collapsed)
        {
            _collapsed = collapsed;
            // The ribbon stamps :qat-collapsed + re-hosts here → a Visibility flip + Items move → a re-measure/arrange.
            // The verdict recomputes the same value next pass (via the cache), so the re-entrant invalidation settles.
            CollapseChanged?.Invoke(this, EventArgs.Empty);
        }

        // The label-row width is whichever QAT form is currently shown (the other is Collapsed → DesiredSize 0).
        var labelW = Math.Max(WidthOf(QatFull), WidthOf(QatCollapsed));
        var pinW = WidthOf(Pin);
        var trailingW = Math.Max(labelW, pinW);
        var tabsSlot = Math.Max(0, available - trailingW);

        ArrangeChild(Tabs, new Rect(0, 0, tabsSlot, rows)); // left-packed; clips into the slot if the tabs overrun

        // The active QAT form + pin hug the right edge — the QAT on the label row (top), the pin on the underline row
        // (bottom). The hidden QAT form is Collapsed (DesiredSize 0) → ArrangeChild folds it to Rect.Empty.
        ArrangeFlushRight(QatFull, available, top: 0, rows);
        ArrangeFlushRight(QatCollapsed, available, top: 0, rows);
        ArrangeFlushRight(Pin, available, top: rows - HeightOf(Pin), rows);
        return finalSize;
    }

    private static int MeasureNatural(UIElement? child, int rows)
    {
        if (child is null)
            return 0;
        child.Measure(new Size(LayoutMath.Unbounded, rows));
        return child.Visibility == Visibility.Collapsed ? 0 : child.DesiredSize.Columns;
    }

    private static int WidthOf(UIElement? child)
        => child is null || child.Visibility == Visibility.Collapsed ? 0 : child.DesiredSize.Columns;

    private static int HeightOf(UIElement? child)
        => child is null || child.Visibility == Visibility.Collapsed ? 0 : child.DesiredSize.Rows;

    // Arrange a child flush against the right edge at its natural width on the given row band; a Collapsed child folds
    // to Rect.Empty (renders nothing) so the hidden QAT form leaves no ghost.
    private static void ArrangeFlushRight(UIElement? child, int rightEdge, int top, int rows)
    {
        if (child is null)
            return;
        if (child.Visibility == Visibility.Collapsed)
        {
            child.Arrange(Rect.Empty);
            return;
        }

        var w = child.DesiredSize.Columns;
        var h = Math.Min(child.DesiredSize.Rows, rows);
        child.Arrange(new Rect(Math.Max(0, rightEdge - w), Math.Max(0, top), w, h));
    }

    private static void ArrangeChild(UIElement? child, Rect rect)
        => child?.Arrange(child.Visibility == Visibility.Collapsed ? Rect.Empty : rect);

    private static int Max4(int a, int b, int c, int d) => Math.Max(Math.Max(a, b), Math.Max(c, d));
}
