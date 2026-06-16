using Cursorial.UI.Input;

// ReSharper disable CheckNamespace
namespace Cursorial.UI.Controls;

/// <summary>
/// Raised when an item is <i>activated</i> (Enter or double-click) — distinct from selection (design doc §12.7).
/// Bubbles from the <see cref="SelectingItemsControl"/>.
/// </summary>
public sealed class ItemActivatedEventArgs(RoutedEvent routedEvent, UIElement source, object? item, int index)
    : RoutedEventArgs(routedEvent, source)
{
    /// <summary>The activated item.</summary>
    public object? Item { get; } = item;

    /// <summary>The activated item's index.</summary>
    public int Index { get; } = index;
}
