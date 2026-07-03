using Cursorial.Rendering;
using Cursorial.UI.Controls;

namespace Cursorial.UI.Bars;

/// <summary>
/// A <see cref="RibbonGroup"/>'s internal layout (the guide's <c>.gr</c> controls row): a single horizontal row of bar
/// controls of mixed size. The row height is the tallest control (a <see cref="RibbonButtonSize.Large"/> glyph-over-
/// label spans it); shorter controls (small/medium buttons, combos, galleries) are vertically centered in the row.
///
/// <para>It is also the group's <see cref="IItemsHostPanel"/> adoption owner (<see cref="IItemsHostPanel.ManagesContainerAdoption"/> =
/// <see langword="true"/>), so the <see cref="ItemsPresenter"/> steps back from its index-aligned sync and the panel
/// keeps the authoritative ordered container list. That ownership is what lets the group's <b>Collapsed</b> density
/// tier move the LIVE controls into a flyout: <see cref="SetPopupHost"/> relocates every container (all-or-nothing)
/// into a popup-side panel and back, a pure visual-only move — the containers stay logical children of the group, so
/// their commands / bindings / inherited style survive (the <c>ToolbarOverflowPanel</c> re-parent model, degenerate to
/// a single binary band split).</para>
/// </summary>
public sealed class RibbonGroupPanel : Panel, IItemsHostPanel
{
    // The authoritative ordered container list (synced from the generator — NOT this panel's Children, which are empty
    // while the containers live in the popup band).
    private readonly List<UIElement> _containers = [];

    private ItemContainerGenerator? _generator;
    private RibbonGroup? _group;
    private Panel? _popupHost; // the flyout band the containers move into while Collapsed (null ⇒ inline, on this panel)

    /// <summary>The authoritative ordered container list (the group's live controls), for the band's analytic width
    /// estimate — valid regardless of which band currently holds them.</summary>
    internal IReadOnlyList<UIElement> Containers => _containers;

    // ───────────────────────────── IItemsHostPanel (adoption) ─────────────────────────────

    bool IItemsHostPanel.ManagesContainerAdoption => true;

    void IItemsHostPanel.OnItemsHostConnected(ItemsControl owner)
    {
        _group = owner as RibbonGroup;
        _generator = owner.ItemContainerGenerator;
        _generator.ContainersChanged += OnContainersChanged;

        // Catch up to whatever the generator already realized (it may have run before this panel existed).
        _containers.Clear();
        for (var i = 0; i < _generator.ContainerCount; i++)
            if (_generator.ContainerFromIndex(i) is { } container)
                _containers.Add(container);

        _group?.RegisterGroupPanel(this);
        ReconcileHost();
    }

    void IItemsHostPanel.OnItemsHostDisconnected()
    {
        _group?.RegisterGroupPanel(null);
        if (_generator is not null)
            _generator.ContainersChanged -= OnContainersChanged;

        // Release both bands so a re-attach / re-template re-adopts cleanly (logical parentage stays the group's).
        _popupHost?.Children.Clear();
        Children.Clear();
        _containers.Clear();
        _generator = null;
        _group = null;
        _popupHost = null;
    }

    // The group flips the flyout band when its density crosses the Collapsed boundary: non-null ⇒ move every container
    // into the popup band (this panel goes empty); null ⇒ move them back inline. Idempotent.
    internal void SetPopupHost(Panel? popupHost)
    {
        if (ReferenceEquals(_popupHost, popupHost))
            return;
        // The flyout band adopts the SAME live containers visual-only — mark it IsItemsHost like this panel, or its
        // Children.Add would claim LOGICAL ownership of a container the group already owns ("already attached").
        if (popupHost is not null)
            popupHost.IsItemsHost = true;
        _popupHost = popupHost;
        ReconcileHost();
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
                // CD-P9-3: the host's VISUAL detach must happen HERE (before the generator's logical detach), or a
                // container is left dangling. Remove from whichever band currently holds it.
                if (e.RemovedContainers is { } removed)
                    foreach (var container in removed)
                    {
                        _containers.Remove(container);
                        Detach(container);
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
                _popupHost?.Children.Clear();
                Children.Clear();
                _containers.Clear();
                for (var i = 0; i < _generator.ContainerCount; i++)
                    if (_generator.ContainerFromIndex(i) is { } container)
                        _containers.Add(container);
                break;
        }

        ReconcileHost();
    }

    // Ensure the ACTIVE band (inline this panel, or the popup band) holds exactly _containers in order. A container in
    // the other band is pulled across (removed from its real current parent first — a single visual parent). Idempotent
    // — only boundary-crossers move, so it cannot oscillate.
    private void ReconcileHost()
    {
        var active = _popupHost ?? (Panel) this;
        for (var i = 0; i < _containers.Count; i++)
        {
            var c = _containers[i];
            var current = active.Children.IndexOf(c);
            if (current < 0)
            {
                DetachFromCurrentParent(c); // wherever it is now (the other band) — before adopting into active
                active.Children.Insert(Math.Min(i, active.Children.Count), c);
            }
            else if (current != i)
            {
                active.Children.Move(current, i);
            }
        }
    }

    private static void DetachFromCurrentParent(UIElement container)
    {
        if (container.VisualParent is Panel p && p.Children.Contains(container))
            p.Children.Remove(container);
    }

    private void Detach(UIElement container) => DetachFromCurrentParent(container);

    // ───────────────────────────── layout (a single centered row) ─────────────────────────────

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children;
        var width = 0;
        var height = 1;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(new Size(LayoutMath.Unbounded, LayoutMath.Unbounded));
            if (child.Visibility == Visibility.Collapsed)
                continue;

            width = LayoutMath.Add(width, child.DesiredSize.Columns);
            height = Math.Max(height, child.DesiredSize.Rows);
        }

        return new Size(width, height);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children;
        var h = finalSize.Rows;
        var x = 0;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Arrange(Rect.Empty);
                continue;
            }

            var w = child.DesiredSize.Columns;
            var rows = Math.Min(child.DesiredSize.Rows, h);
            var y = Math.Max(0, (h - rows) / 2); // center a short control in the band height; a Large control fills it
            child.Arrange(new Rect(x, y, w, rows));
            x = LayoutMath.Add(x, w);
        }

        return finalSize;
    }
}
