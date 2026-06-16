using Cursorial.UI.Input;

// ReSharper disable CheckNamespace
namespace Cursorial.UI.Controls;

/// <summary>
/// A selectable list of items (design doc §12.6/§12.7). Containers are <see cref="ListBoxItem"/>s; selection rides
/// the <see cref="SelectingItemsControl"/> base over a <see cref="SelectionModel"/>. The list itself is not a tab stop — its
/// items host is a single tab stop (<see cref="Input.KeyboardNavigationMode.Once"/>) and the items are focusable
/// (keyboard navigation lands in P9.3b). Removing the selected item re-targets to the nearest survivor (CD-P9-9).
/// </summary>
public class ListBox : SelectingItemsControl
{
    /// <summary>Creates a list box (not itself a tab stop; the items host is the single tab stop).</summary>
    public ListBox()
    {
        IsTabStop = false;
        ItemsPanel = new FuncTemplateContent(static _ =>
        {
            var panel = new StackPanel();
            KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Once); // the group is one tab stop
            return panel;
        });
    }

    /// <inheritdoc/>
    protected override UIElement GetContainerForItemOverride() => new ListBoxItem();

    /// <inheritdoc/>
    protected override bool IsItemItsOwnContainer(object? item) => item is ListBoxItem;

    /// <inheritdoc/>
    private protected override void OnSelectionEmptiedByRemoval(int removalIndex)
    {
        // CD-P9-9: a removal dropped the whole selection — re-select the nearest surviving item (the item that
        // slid into the removed slot, clamped to the end).
        var count = ItemContainerGenerator.ContainerCount;
        if (count > 0)
            Selection.Select(Math.Min(removalIndex, count - 1));
    }
}
