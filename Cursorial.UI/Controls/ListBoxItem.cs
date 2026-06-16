using Cursorial.Input;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// The container for a <see cref="ListBox"/> item (design doc §12.6/§12.7). <see cref="IsSelected"/> two-way
/// mirrors the owning <see cref="SelectingItemsControl"/>'s <see cref="SelectionModel"/> (the owner writes it via
/// <c>SetCurrentValue</c> so bindings survive; <c>:selected</c> flips through <see cref="PseudoClassMapping"/>).
/// A left mouse-down selects it per the modifiers (Ctrl = toggle, Shift = range), and a double-click activates it.
/// </summary>
public class ListBoxItem : ContentControl, ISelectableContainer
{
    /// <summary>Whether the item is selected. Two-way bindable; <c>:selected</c> mirrors it. Setting it from outside
    /// the owner folds into the owner's selection (CD-P9-9: the model stays the source of truth).</summary>
    public static readonly StyledProperty<bool> IsSelectedProperty =
        UIProperty.Register<ListBoxItem, bool>(nameof(IsSelected), defaultValue: false, changed: OnIsSelectedChanged);

    private bool _ownerDriven; // guards the owner→container write so it never echoes back into the model

    static ListBoxItem() => PseudoClassMapping.Register<ListBoxItem>(IsSelectedProperty, ":selected");

    /// <summary>Creates a list-box item (focusable; the items host is a single tab stop — design doc §12.7).</summary>
    public ListBoxItem() => Focusable = true;

    /// <inheritdoc cref="IsSelectedProperty"/>
    public bool IsSelected { get => GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }

    void ISelectableContainer.SetIsSelectedFromOwner(bool selected)
    {
        _ownerDriven = true;
        try
        {
            SetCurrentValue(IsSelectedProperty, selected); // SetCurrentValue preserves a two-way IsSelected binding
        }
        finally
        {
            _ownerDriven = false;
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Handled || e.Button != MouseButton.Left || OwnerSelector is not { } owner)
            return;

        Focus();
        owner.HandleContainerPointerSelect(this, e.Modifiers, e.ClickCount);
        e.Handled = true;
    }

    // The owning selector is this container's logical parent (generated containers are logical children of the
    // ItemsControl — punch 43); a walk covers an own-container nested under a wrapper.
    private SelectingItemsControl? OwnerSelector
    {
        get
        {
            for (UIElement? node = LogicalParent; node is not null; node = node.LogicalParent)
                if (node is SelectingItemsControl selector)
                    return selector;
            return null;
        }
    }

    private static void OnIsSelectedChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is ListBoxItem { _ownerDriven: false } item && item.OwnerSelector is { } owner)
            owner.NotifyContainerIsSelectedChanged(item, newValue);
    }
}
