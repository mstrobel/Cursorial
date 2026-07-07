using Cursorial.Rendering;
using Cursorial.UI.Controls;

namespace Cursorial.UI.Bars;

/// <summary>
/// A <see cref="RibbonControlGroup"/>'s items host: packs the children into one or two rows. Two-row packing is the
/// contiguous definition-order split minimizing the wider row (wide children like combos claim a row of their own;
/// small buttons pair up); a <see cref="RibbonControlGroup.RowBreakProperty"/> child pins the split. Single-row
/// layout is a plain left-to-right run (matching <see cref="RibbonGroupPanel"/>'s treatment of direct children).
/// </summary>
/// <remarks>
/// The row count comes from the owner's <see cref="RibbonControlGroup.RowBehaviorProperty"/> resolved against the
/// band's AUTHORED height (<see cref="Ribbon.BandContentRowsProperty"/>, stamped by <see cref="RibbonBand"/> from
/// authored faces only) — never from live/demoted faces, so density folds re-ink faces without re-flowing rows.
/// The packing arithmetic is exposed via <see cref="PackedWidth"/> so the group's analytic density estimates run the
/// SAME split over hypothetical (e.g. icon-only) child widths.
/// </remarks>
public sealed class RibbonControlGroupPanel : Panel, IItemsHostPanel
{
    private RibbonControlGroup? _owner;

    bool IItemsHostPanel.ManagesContainerAdoption => false; // default presenter index-sync; no band moves within

    void IItemsHostPanel.OnItemsHostConnected(ItemsControl owner)
    {
        _owner = owner as RibbonControlGroup;
        _owner?.RegisterPanel(this);
    }

    void IItemsHostPanel.OnItemsHostDisconnected()
    {
        _owner?.RegisterPanel(null);
        _owner = null;
    }

    /// <summary>The rows this panel packs into right now (1 or 2) — resolved from the owner's behavior and the
    /// band's authored height, both stable authored facts.</summary>
    internal int ResolvedRows
    {
        get
        {
            var behavior = _owner?.RowBehavior ?? RibbonControlGroupRowBehavior.Auto;

            return behavior switch
            {
                RibbonControlGroupRowBehavior.SingleRow => 1,
                // A TwoRow pin is an AUTHOR vote; Simplified/Compact LayoutMode is the USER's word and wins —
                // the band already stamps 1 row under the simplified signal, and the pin flattens with it
                // (audit F3: an unflattened pin left a 2-row band inside the "one-row" mode).
                RibbonControlGroupRowBehavior.TwoRow => GetValue(Ribbon.IsLayoutSimplifiedProperty) ? 1 : 2,
                _ => Math.Max(1, Math.Min(2, GetValue(Ribbon.BandContentRowsProperty))),
            };
        }
    }

    /// <summary>
    /// The two-row packing arithmetic over an ordered width list: the contiguous split minimizing the wider row,
    /// honoring a pinned break index (<c>-1</c> = unpinned). Returns the packed width (the wider row). Shared by
    /// layout and by the analytic density estimates (which feed hypothetical icon-only widths through it).
    /// </summary>
    internal static int PackedWidth(IReadOnlyList<int> widths, int pinnedBreak, out int breakIndex)
    {
        if (widths.Count <= 1)
        {
            breakIndex = widths.Count; // everything on row 0
            return widths.Count == 0 ? 0 : widths[0];
        }

        if (pinnedBreak >= 1 && pinnedBreak < widths.Count)
        {
            breakIndex = pinnedBreak;
            return Math.Max(Sum(widths, 0, pinnedBreak), Sum(widths, pinnedBreak, widths.Count));
        }

        var best = int.MaxValue;
        breakIndex = 1;

        for (var k = 1; k < widths.Count; k++)
        {
            var packed = Math.Max(Sum(widths, 0, k), Sum(widths, k, widths.Count));

            if (packed < best)
            {
                best = packed;
                breakIndex = k;
            }
        }

        return best;
    }

    /// <summary>
    /// The analytic width this panel would pack to if each child measured <paramref name="widthOf"/> cells — the
    /// group-level density estimates feed hypothetical (icon-only) widths through the SAME visible/pinned walk and
    /// split arithmetic the live layout uses, so the fold's stable widths can never disagree with a later re-layout.
    /// </summary>
    internal int EstimateWidth(Func<UIElement, int> widthOf)
    {
        var pinned = -1;
        var count = 0;
        var children = Children;
        _estimateWidths.Clear();

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];

            if (child.Visibility == Visibility.Collapsed)
                continue;

            if (pinned < 0 && count > 0 && RibbonControlGroup.GetRowBreak(child))
                pinned = count;

            _estimateWidths.Add(widthOf(child));
            count++;
        }

        if (count == 0)
            return 0;

        if (ResolvedRows < 2 || count == 1)
            return Sum(_estimateWidths, 0, _estimateWidths.Count);

        return PackedWidth(_estimateWidths, pinned, out _);
    }

    private readonly List<int> _estimateWidths = [];

    private static int Sum(IReadOnlyList<int> widths, int start, int end)
    {
        var total = 0;
        for (var i = start; i < end; i++)
            total = LayoutMath.Add(total, widths[i]);
        return total;
    }

    // Reused scratch (a handful of children per group — allocate once).
    private readonly List<UIElement> _visible = [];
    private readonly List<int> _widths = [];

    // Gather visible children + their 1-row widths; returns the pinned break index (-1 when no RowBreak child).
    private int CollectVisible()
    {
        _visible.Clear();
        _widths.Clear();

        var pinned = -1;
        var children = Children;

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(new Size(LayoutMath.Unbounded, LayoutMath.Unbounded));

            if (child.Visibility == Visibility.Collapsed)
                continue;

            if (pinned < 0 && _visible.Count > 0 && RibbonControlGroup.GetRowBreak(child))
                pinned = _visible.Count;

            // A child measuring >1 row (an explicitly-authored Large face — unsupported inside a control group,
            // since a 2-row face cannot stack in a 2-row band) clamps to its row in ArrangeRun.
            _visible.Add(child);
            _widths.Add(child.DesiredSize.Columns);
        }

        return pinned;
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var pinned = CollectVisible();

        if (_visible.Count == 0)
            return new Size(0, 0);

        if (ResolvedRows < 2 || _visible.Count == 1)
            return new Size(Sum(_widths, 0, _widths.Count), 1);

        return new Size(PackedWidth(_widths, pinned, out _), 2);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var pinned = CollectVisible(); // cheap re-collect: measures are cache hits, the lists are reused scratch

        // Collapsed children still need an (empty) arrange.
        var children = Children;
        for (var i = 0; i < children.Count; i++)
            if (children[i].Visibility == Visibility.Collapsed)
                children[i].Arrange(Rect.Empty);

        if (_visible.Count == 0)
            return finalSize;

        if (ResolvedRows < 2 || _visible.Count == 1)
        {
            ArrangeRun(0, _visible.Count, row: 0, finalSize);
            return finalSize;
        }

        PackedWidth(_widths, pinned, out var breakIndex);
        ArrangeRun(0, breakIndex, row: 0, finalSize);
        ArrangeRun(breakIndex, _visible.Count, row: 1, finalSize);
        return finalSize;
    }

    private void ArrangeRun(int start, int end, int row, Size finalSize)
    {
        var x = 0;

        for (var i = start; i < end; i++)
        {
            var child = _visible[i];
            var w = child.DesiredSize.Columns;
            child.Arrange(new Rect(x, row, w, Math.Min(child.DesiredSize.Rows, Math.Max(1, finalSize.Rows - row))));
            x = LayoutMath.Add(x, w);
        }
    }
}
