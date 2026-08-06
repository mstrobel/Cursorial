using Cursorial.Rendering;

namespace Cursorial.UI.Controls;

/// <summary>
/// The content host inside a <see cref="ListViewItem"/>'s template (<c>PART_Cells</c>) — the part that
/// makes ONE container type serve every view mode.
/// <para>
/// <see cref="ItemsControl.GetContainerForItemOverride"/> takes no item and cannot vary the container per
/// item, and a <see cref="ListView"/> switches its whole presentation at runtime, so the switch has to
/// live BELOW the container: in <see cref="ListViewViewMode.Details"/> this presenter builds one cell per
/// <see cref="ListView.Columns"/> entry and lays them out on the shared
/// <see cref="ListViewColumnLayout"/> (so every row lines up with the header); in the wrapping views it
/// builds a single composed cell (name, or icon + name, or the two-line tile).
/// </para>
/// </summary>
public sealed class ListViewCellsPresenter : Panel
{
    private ListView? _owner;
    private int _builtStructureVersion = -1;
    private ListViewViewMode _builtView = (ListViewViewMode) byte.MaxValue;

    /// <summary>The owning <see cref="ListView"/> (null before attach).</summary>
    public ListView? Owner => _owner;

    /// <summary>Whether this row is currently laid out as a column grid (Details with at least one column).</summary>
    private bool IsColumnar => _owner is { View: ListViewViewMode.Details, Columns.Count: > 0 };

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);

        _owner = FindOwner();
        _owner?.RegisterCellsPresenter(this);
        Rebuild();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        _owner?.UnregisterCellsPresenter(this);
        _owner = null;
        DiscardCells();
        base.OnDetachedFromTree(in e);
    }

    /// <summary>
    /// Drops the built cells, tearing their bindings down first. The cells are presenter-BUILT (by
    /// <see cref="ListView.BuildDetailsCell"/> / <c>BuildComposedCell</c>), and removing a visual
    /// child deliberately does NOT run a teardown — so each discarded cell's Source-anchored
    /// <c>Inverse</c> link back to THIS presenter, and its content binding, would keep the dead cell
    /// observed and alive. <see cref="Rebuild"/> runs on every column-structure or view change, so
    /// that is an unbounded leak, not a one-off (the same rule
    /// <see cref="ContentPresenter"/> follows for its realized child).
    /// </summary>
    private void DiscardCells()
    {
        // TearDown is the permanent-discard sweep (it recurses the subtree, template parts included,
        // and is deliberately NOT folded into Children.Remove — elements are legally reusable). These
        // cells are ours and are never coming back, so running it is the caller's job: ours. It must
        // run AFTER the detach, though — sweeping a still-parented element collides with the style
        // engine retracting its frames.
        var discarded = Children.ToArray();
        Children.Clear();

        foreach (var cell in discarded)
            cell.TearDown();
    }

    // The container's template gives us TemplatedParent = the ListViewItem, whose LogicalParent is the
    // ListView (generated containers are logical children of the items control — punch 43). The UIParent
    // bridge (LogicalParent ?? TemplatedParent) walks both hops in one loop.
    private ListView? FindOwner()
    {
        for (var node = UIParent; node is not null; node = node.UIParent)
        {
            if (node is ListView owner)
                return owner;
        }

        return null;
    }

    /// <summary>Rebuilds the cell elements for the owner's current view / columns (idempotent).</summary>
    internal void Rebuild()
    {
        DiscardCells();

        if (_owner is not {} owner || IsAttachedToTree is false)
        {
            _builtStructureVersion = -1;
            return;
        }

        _builtStructureVersion = owner.ColumnStructureVersion;
        _builtView = owner.View;

        if (owner is { View: ListViewViewMode.Details, Columns.Count: > 0 })
        {
            foreach (var column in owner.Columns)
            {
                var cell = ListView.BuildDetailsCell(column);
                TextElement.ForwardInverse(cell, this);
                Children.Add(cell);
            }
        }
        else
        {
            var cell = owner.BuildComposedCell();
            TextElement.ForwardInverse(cell, this);
            Children.Add(cell);
        }

        InvalidateMeasure();
    }

    private void EnsureBuilt()
    {
        if (_owner is { } owner && (_builtStructureVersion != owner.ColumnStructureVersion || _builtView != owner.View))
            Rebuild();
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureBuilt();

        if (_owner is not { } owner)
            return Size.Empty;

        var children = Children;

        if (!IsColumnar)
        {
            // The wrapping views: one composed cell that owns the whole row rect.
            var width = 0;
            var height = 0;

            for (var i = 0; i < children.Count; i++)
            {
                children[i].Measure(availableSize);
                width = Math.Max(width, children[i].DesiredSize.Columns);
                height = Math.Max(height, children[i].DesiredSize.Rows);
            }

            return new Size(width, height);
        }

        // The row band is what the columns must fit into — reported here, not from the ListView, because
        // rows live inside the scroll viewport and the header does not (see ListViewColumnLayout).
        var layout = owner.ColumnLayout;
        if (!LayoutMath.IsUnbounded(availableSize.Columns))
            layout.ReportAvailableWidth(availableSize.Columns);

        var columns = owner.Columns;
        var spacing = owner.ColumnSpacing;
        var used = 0;
        var rows = 0;

        for (var i = 0; i < children.Count && i < columns.Count; i++)
        {
            var cell = children[i];

            // ① unconstrained probe feeds the Auto tracks …
            cell.Measure(new Size(LayoutMath.Unbounded, availableSize.Rows));
            layout.ReportContentWidth(i, cell.DesiredSize.Columns);

            // ② … then the granted width, which is what actually renders.
            var width = layout.GetWidth(columns, i);
            cell.Measure(new Size(width, availableSize.Rows));

            rows = Math.Max(rows, cell.DesiredSize.Rows);
            used = LayoutMath.Add(used, width);
            if (i < columns.Count - 1)
                used = LayoutMath.Add(used, spacing);
        }

        return new Size(Math.Min(used, LayoutMath.MaxExtent), Math.Max(rows, 1));
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children;

        if (!IsColumnar)
        {
            for (var i = 0; i < children.Count; i++)
                children[i].Arrange(new Rect(0, 0, finalSize.Columns, finalSize.Rows));

            return finalSize;
        }

        var owner = _owner!;
        var columns = owner.Columns;
        var layout = owner.ColumnLayout;
        var spacing = owner.ColumnSpacing;
        var x = 0;

        for (var i = 0; i < children.Count; i++)
        {
            var width = i < columns.Count ? layout.GetWidth(columns, i) : 0;
            var slot = Math.Max(0, Math.Min(width, finalSize.Columns - x));
            children[i].Arrange(new Rect(x, 0, slot, finalSize.Rows));
            x = Math.Min(LayoutMath.MaxExtent, x + width + spacing);
        }

        return finalSize;
    }
}
