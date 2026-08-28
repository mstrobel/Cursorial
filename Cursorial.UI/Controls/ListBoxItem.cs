namespace Cursorial.UI.Controls;

/// <summary>
/// The container for a <see cref="ListBox"/> item (design doc §12.6/§12.7). <see cref="SelectableItemContainer.IsSelected"/>
/// two-way mirrors the owning <see cref="SelectingItemsControl"/>'s <see cref="SelectionModel"/> (the owner writes it via
/// <c>SetCurrentValue</c> so bindings survive; <c>:selected</c> flips through <see cref="PseudoClassMapping"/>).
/// A left mouse-down selects it per the modifiers (Ctrl = toggle, Shift = range), and a double-click activates it.
/// </summary>
public class ListBoxItem : SelectableItemContainer
{
    /// <summary>Creates a list-box item (focusable; the items host is a single tab stop — design doc §12.7).</summary>
    public ListBoxItem() => Focusable = true;
}