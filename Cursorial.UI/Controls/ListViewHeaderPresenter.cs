using Cursorial.Rendering;

namespace Cursorial.UI.Controls;

/// <summary>
/// The header strip of a <see cref="ListView"/> in <see cref="ListViewViewMode.Details"/>: one
/// <see cref="ListViewColumnHeader"/> per <see cref="ListView.Columns"/> entry, laid out on the shared
/// <see cref="ListViewColumnLayout"/> so each header sits exactly over its column.
/// <para>
/// It lives OUTSIDE the items <c>ScrollViewer</c> in the control template, so it does not scroll away
/// vertically. That is also why <see cref="ListViewViewMode.Details"/> disables horizontal scrolling:
/// with the header pinned, a horizontally scrolled body would slide out from under it. Columns are
/// instead fitted to the viewport (star tracks absorb the slack), which is what a terminal-width table
/// wants anyway.
/// </para>
/// </summary>
public sealed class ListViewHeaderPresenter : Panel
{
    private ListView? _owner;
    private int _builtStructureVersion = -1;

    /// <summary>The owning <see cref="ListView"/>, resolved from the template's templated parent (null before attach).</summary>
    public ListView? Owner => _owner;

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);

        _owner = FindOwner();
        _owner?.RegisterHeaderPresenter(this);
        Rebuild();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        // Unhook before rewire: the Click handlers point at THIS presenter, so they must go before the
        // headers are dropped, and the owner's registry must not retain a detached presenter.
        ClearHeaders();
        _owner?.UnregisterHeaderPresenter(this);
        _owner = null;
        base.OnDetachedFromTree(in e);
    }

    // The owning ListView: the template's TemplatedParent normally, else the UIParent bridge
    // (LogicalParent ?? TemplatedParent) — the same walk ItemsPresenter uses, so nesting the strip inside
    // a decorator in a custom template still resolves.
    private ListView? FindOwner()
    {
        if (TemplatedParent is ListView templatedOwner)
            return templatedOwner;

        for (var node = UIParent; node is not null; node = node.UIParent)
        {
            if (node is ListView owner)
                return owner;
        }

        return null;
    }

    /// <summary>Rebuilds the header cells from the current <see cref="ListView.Columns"/> (idempotent).</summary>
    internal void Rebuild()
    {
        if (_owner is not { } owner)
        {
            ClearHeaders();
            return;
        }

        _builtStructureVersion = owner.ColumnStructureVersion;
        ClearHeaders();

        foreach (var column in owner.Columns)
        {
            var header = new ListViewColumnHeader { Column = column };
            header.Click += OnHeaderClick;
            Children.Add(header);
        }

        UpdateSortIndicators();
        InvalidateMeasure();
    }

    /// <summary>Re-pushes <see cref="ListView.SortColumn"/>/<see cref="ListView.SortDirection"/> onto the headers.</summary>
    internal void UpdateSortIndicators()
    {
        if (_owner is not { } owner)
            return;

        foreach (var child in Children)
        {
            if (child is not ListViewColumnHeader header)
                continue;

            header.SortDirection = ReferenceEquals(header.Column, owner.SortColumn)
                ? owner.SortDirection
                : ListViewSortDirection.None;
        }
    }

    private void ClearHeaders()
    {
        foreach (var child in Children)
        {
            if (child is ListViewColumnHeader header)
                header.Click -= OnHeaderClick;
        }

        Children.Clear();
    }

    private void OnHeaderClick(object? sender, ClickEventArgs e)
    {
        if (sender is ListViewColumnHeader { Column: { } column })
            _owner?.CycleSort(column);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (_owner is not { } owner)
            return Size.Empty;

        if (_builtStructureVersion != owner.ColumnStructureVersion)
            Rebuild();

        var columns = owner.Columns;
        var layout = owner.ColumnLayout;
        var spacing = owner.ColumnSpacing;
        var children = Children;

        var used = 0;
        var height = 0;

        for (var i = 0; i < children.Count && i < columns.Count; i++)
        {
            var header = children[i];

            // ① probe unconstrained so an Auto column can be as wide as its header text …
            header.Measure(new Size(LayoutMath.Unbounded, availableSize.Rows));
            layout.ReportContentWidth(i, header.DesiredSize.Columns);

            // ② … then measure at the width the layout actually granted.
            var width = layout.GetWidth(columns, i);
            header.Measure(new Size(width, availableSize.Rows));

            height = Math.Max(height, header.DesiredSize.Rows);
            used = LayoutMath.Add(used, width);
            if (i < columns.Count - 1)
                used = LayoutMath.Add(used, spacing);
        }

        return new Size(Math.Min(used, LayoutMath.MaxExtent), Math.Max(height, children.Count > 0 ? 1 : 0));
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_owner is not { } owner)
            return finalSize;

        var columns = owner.Columns;
        var layout = owner.ColumnLayout;
        var spacing = owner.ColumnSpacing;
        var children = Children;
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
