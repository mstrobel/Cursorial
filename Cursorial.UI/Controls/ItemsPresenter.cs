using Cursorial.Rendering;
using Cursorial.UI.Data;

namespace Cursorial.UI.Controls;

/// <summary>
/// Hosts an <see cref="ItemsControl"/>'s generated containers (design doc §12.6). It is the <c>PART_ItemsHost</c>
/// of the items-control template, reachable as <see cref="ItemsControl.ItemsHost"/>. It builds the control's
/// <see cref="ItemsControl.ItemsPanel"/> (default a vertical <see cref="StackPanel"/>), marks it
/// <see cref="Panel.IsItemsHost"/> so the panel adopts the containers <b>visually only</b> (they stay logical
/// children of the <see cref="ItemsControl"/> — punch 43), and keeps the panel's <see cref="Panel.Children"/> in
/// sync with the generator. Layout delegates to the panel.
/// </summary>
public sealed class ItemsPresenter : UIElement
{
    private ItemContainerGenerator? _generator;
    private Panel? _panel;

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);

        if (TemplatedParent is not ItemsControl owner)
            return;

        _generator = owner.ItemContainerGenerator;
        EnsurePanel(owner);
        _generator.ContainersChanged += OnContainersChanged;
        SyncAll(); // adopt whatever is already realized (the generator may have run before this part existed)
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        if (_generator is not null)
        {
            _generator.ContainersChanged -= OnContainersChanged;
            _generator = null;
        }

        // Free the containers (logical parentage stays the ItemsControl's) so a re-attach / re-template re-adopts.
        if (_panel is not null)
        {
            _panel.Children.Clear();
            RemoveVisualChild(_panel);
            _panel = null;
        }

        base.OnDetachedFromTree(in e);
    }

    /// <summary>Rebuilds the items panel after <see cref="ItemsControl.ItemsPanel"/> changes (v1: full re-host).</summary>
    internal void RebuildPanel()
    {
        if (TemplatedParent is not ItemsControl owner || !IsAttachedToTree)
            return;

        if (_panel is not null)
        {
            _panel.Children.Clear();
            RemoveVisualChild(_panel);
            _panel = null;
        }

        EnsurePanel(owner);
        SyncAll();
        InvalidateMeasure();
    }

    private void EnsurePanel(ItemsControl owner)
    {
        if (_panel is not null)
            return;

        var content = owner.ItemsPanel ?? new FuncTemplateContent(_ => new StackPanel());
        var built = content.Build(new TemplateBuildContext(owner, new NameScopeDictionary()));

        _panel = built as Panel ??
                 throw new InvalidOperationException($"ItemsControl.ItemsPanel must build a Panel; got {built.GetType().Name}.");

        _panel.IsItemsHost = true; // its Children adopt the containers visually only (logical parent = the ItemsControl)
        AddVisualChild(_panel);
    }

    private void OnContainersChanged(object? sender, ContainersChangedEventArgs e)
    {
        if (_generator is null || _panel is null)
            return;

        switch (e.Action)
        {
            case ContainersChangedAction.Realized:
                for (var i = 0; i < e.Count; i++)
                {
                    if (_generator.ContainerFromIndex(e.StartIndex + i) is {} container)
                        _panel.Children.Insert(e.StartIndex + i, container);
                }

                break;

            case ContainersChangedAction.Unrealized:
                // Fired BEFORE the generator drops the range (the visual detach IS the retraction trigger and must
                // precede the logical removal), so the containers are still index-addressable.
                for (var i = e.Count - 1; i >= 0; i--)
                {
                    if (_generator.ContainerFromIndex(e.StartIndex + i) is {} container)
                        _panel.Children.Remove(container);
                }

                break;

            case ContainersChangedAction.Moved:
                if (_generator.ContainerFromIndex(e.StartIndex) is {} moved)
                {
                    var from = _panel.Children.IndexOf(moved);
                    if (from >= 0)
                        _panel.Children.Move(from, e.StartIndex);
                }

                break;

            default:
            case ContainersChangedAction.Reset:
                _panel.Children.Clear();
                SyncAll();
                break;
        }
    }

    // Reconcile the panel's children to the generator's containers (attach / Reset). Idempotent.
    private void SyncAll()
    {
        if (_generator is null || _panel is null)
            return;

        var target = _generator.Containers;

        for (var i = 0; i < target.Count; i++)
        {
            var container = target[i];
            var current = _panel.Children.IndexOf(container);

            if (current < 0)
                _panel.Children.Insert(i, container);
            else if (current != i)
                _panel.Children.Move(current, i);
        }
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (_panel is null && TemplatedParent is ItemsControl owner)
            EnsurePanel(owner);

        if (_panel is null)
            return new Size(0, 0);

        _panel.Measure(availableSize);
        return _panel.DesiredSize;
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        _panel?.Arrange(new Rect(0, 0, finalSize.Columns, finalSize.Rows));
        return finalSize;
    }
}