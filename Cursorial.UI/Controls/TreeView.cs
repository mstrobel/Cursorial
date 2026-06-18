using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A hierarchical list (design doc §12.6). <see cref="TreeView"/> owns <b>tree-wide single selection</b> — a flat
/// <see cref="SelectionModel"/> is per-control and can't span the nesting, so the tree coordinates directly rather
/// than deriving from <see cref="SelectingItemsControl"/>. Its <see cref="ItemsControl.Items"/> become top-level
/// <see cref="TreeViewItem"/> containers; each can host its own sub-items, indenting recursively (the item template
/// indents its <c>PART_ItemsHost</c>, so no per-node depth math). The tree is one tab stop
/// (<see cref="UIElement.IsTabStop"/> = <c>false</c>, the top items panel is
/// <see cref="KeyboardNavigationMode.Once"/>); arrow keys navigate the visible nodes and selection follows the
/// keyboard cursor. The data-driven <c>HierarchicalDataTemplate</c> hierarchy is a v2 deferral.
/// </summary>
public class TreeView : ItemsControl
{
    /// <summary>The selected node's data item (an own-container resolves to itself); read-only, two-way-friendly via the containers' <see cref="TreeViewItem.IsSelected"/>.</summary>
    public static readonly DirectProperty<TreeView, object?> SelectedItemProperty =
        UIProperty.RegisterDirect<TreeView, object?>(nameof(SelectedItem), static t => t._selectedItem);

    private object? _selectedItem;
    private TreeViewItem? _selectedContainer;

    /// <summary>Creates a tree view (not itself a tab stop; the whole tree is a single tab stop — the items host is <see cref="KeyboardNavigationMode.Once"/>).</summary>
    public TreeView()
    {
        IsTabStop = false;
        ItemsPanel = new FuncTemplateContent(static _ =>
        {
            var panel = new StackPanel();
            KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Once); // the whole tree is one tab stop
            return panel;
        });
    }

    /// <inheritdoc cref="SelectedItemProperty"/>
    public object? SelectedItem => _selectedItem;

    /// <summary>The selected container (the <see cref="TreeViewItem"/> backing <see cref="SelectedItem"/>), or null.</summary>
    public TreeViewItem? SelectedContainer => _selectedContainer;

    /// <summary>Raised when <see cref="SelectedItem"/> changes (old → new).</summary>
    public event EventHandler<TreeViewSelectionChangedEventArgs>? SelectionChanged;

    /// <inheritdoc/>
    protected override UIElement GetContainerForItemOverride() => new TreeViewItem();

    /// <inheritdoc/>
    protected override bool IsItemItsOwnContainer(object? item) => item is TreeViewItem;

    // Type-ahead over the TOP-LEVEL nodes (the tree's own generator). Matching against each node's Header; full
    // visible-subtree search is a follow-on. Cycles from the selected node when it is a top-level node.
    private protected override bool TextSearchNavigates => true;

    // Cycle from the selected node's TOP-LEVEL ancestor (a nested node isn't in the tree's own generator, so its
    // IndexFromContainer is −1 — walk up to the ancestor that is, instead of silently re-anchoring at index 0).
    private protected override int CurrentTextSearchIndex
    {
        get
        {
            for (UIElement? node = _selectedContainer; node is not null; node = node.LogicalParent)
                if (node is TreeViewItem item && ItemContainerGenerator.IndexFromContainer(item) is var index and >= 0)
                    return index;
            return -1;
        }
    }

    /// <inheritdoc/>
    private protected override string? GetTextSearchText(int index)
    {
        if (ItemContainerGenerator.ContainerFromIndex(index) is not { } container)
            return null;
        if (TextSearch.GetText(container) is { } explicitText)
            return explicitText;
        if (TextSearch.GetTextPath(this) is { Length: > 0 } path)
            return TextSearch.EvaluatePath(ItemContainerGenerator.ItemFromContainer(container), path);
        return (container as TreeViewItem)?.Header?.ToString();
    }

    /// <inheritdoc/>
    private protected override void OnTextSearchMatch(int containerIndex)
    {
        if (ItemContainerGenerator.ContainerFromIndex(containerIndex) is not TreeViewItem node)
            return;
        node.Focus(FocusNavigationMethod.Directional);
        ChangeSelection(node);
    }

    /// <summary>Makes <paramref name="item"/> the tree's single selection, deselecting the prior node.</summary>
    internal void ChangeSelection(TreeViewItem? item)
    {
        if (ReferenceEquals(_selectedContainer, item))
            return;

        var old = _selectedContainer;
        _selectedContainer = item;
        old?.SetIsSelectedFromTree(false);
        item?.SetIsSelectedFromTree(true);

        var oldItem = _selectedItem;
        var newItem = item is null ? null : ResolveDataItem(item);
        if (SetAndRaise(SelectedItemProperty, ref _selectedItem, newItem))
            SelectionChanged?.Invoke(this, new TreeViewSelectionChangedEventArgs(oldItem, newItem));
    }

    /// <summary>Clears the tree's selection iff <paramref name="item"/> is the current selection (an external deselect).</summary>
    internal void ClearSelectionIf(TreeViewItem item)
    {
        if (ReferenceEquals(_selectedContainer, item))
            ChangeSelection(null);
    }

    // The data item a container was realized for (an own-container's stamp is the container itself); the direct
    // owner is the container's logical-parent ItemsControl (this tree for a top node, or a parent TreeViewItem).
    private static object? ResolveDataItem(TreeViewItem container)
    {
        for (UIElement? node = container.LogicalParent; node is not null; node = node.LogicalParent)
            if (node is ItemsControl owner)
                return owner.ItemContainerGenerator.ItemFromContainer(container) ?? container;
        return container;
    }
}

/// <summary>The <see cref="TreeView.SelectionChanged"/> payload — the old and new selected data items.</summary>
public sealed class TreeViewSelectionChangedEventArgs(object? oldItem, object? newItem) : EventArgs
{
    /// <summary>The previously selected data item (null if none).</summary>
    public object? OldItem { get; } = oldItem;

    /// <summary>The newly selected data item (null if the selection was cleared).</summary>
    public object? NewItem { get; } = newItem;
}
