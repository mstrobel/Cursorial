using Cursorial.Input;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// The container for a <see cref="ComboBox"/> item (design doc §12.11). Mirrors <see cref="ListBoxItem"/>:
/// <see cref="IsSelected"/> two-way mirrors the owning <see cref="ComboBox"/>'s <see cref="SelectionModel"/>
/// (owner-written via <c>SetCurrentValue</c> so bindings survive; <c>:selected</c> flips through
/// <see cref="PseudoClassMapping"/>). A left click selects it AND commits — the drop-down closes.
/// </summary>
public class ComboBoxItem : ContentControl, ISelectableContainer
{
    /// <summary>Whether the item is selected. Two-way bindable; <c>:selected</c> mirrors it.</summary>
    public static readonly StyledProperty<bool> IsSelectedProperty =
        UIProperty.Register<ComboBoxItem, bool>(nameof(IsSelected), defaultValue: false, changed: OnIsSelectedChanged);

    /// <summary>The bubbling event raised whenever the item becomes selected (<see cref="IsSelected"/> ⇒ true), for both user- and owner/model-driven selection.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> SelectedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(Selected), RoutingStrategy.Bubble, typeof(ComboBoxItem));

    /// <summary>The bubbling event raised whenever the item becomes unselected (<see cref="IsSelected"/> ⇒ false), for both user- and owner/model-driven selection.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> UnselectedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(Unselected), RoutingStrategy.Bubble, typeof(ComboBoxItem));

    /// <summary>The bubbling event raised whenever <see cref="IsSelected"/> changes (either direction) — the selection-side parallel to <see cref="ToggleButton.IsCheckedChanged"/>.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> IsSelectedChangedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(IsSelectedChanged), RoutingStrategy.Bubble, typeof(ComboBoxItem));

    private bool _ownerDriven; // guards the owner→container write so it never echoes back into the model

    static ComboBoxItem() => PseudoClassMapping.Register<ComboBoxItem>(IsSelectedProperty, ":selected");

    /// <summary>Creates a combo-box item (focusable; the drop-down list is the keyboard surface).</summary>
    public ComboBoxItem() => Focusable = true;

    /// <inheritdoc cref="IsSelectedProperty"/>
    public bool IsSelected
    {
        get => GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    /// <summary>CLR sugar over <see cref="SelectedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? Selected { add => AddHandler(SelectedEvent, value!); remove => RemoveHandler(SelectedEvent, value!); }

    /// <summary>CLR sugar over <see cref="UnselectedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? Unselected { add => AddHandler(UnselectedEvent, value!); remove => RemoveHandler(UnselectedEvent, value!); }

    /// <summary>CLR sugar over <see cref="IsSelectedChangedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? IsSelectedChanged { add => AddHandler(IsSelectedChangedEvent, value!); remove => RemoveHandler(IsSelectedChangedEvent, value!); }

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

        if (e.Handled || e.Button != MouseButton.Left || OwnerComboBox is not { } owner)
            return;

        Focus();
        owner.HandleContainerPointerSelect(this, e.Modifiers, e.ClickCount);
        owner.CommitAndClose(); // a click commits the selection and closes the drop-down
        e.Handled = true;
    }

    // The owning combo box is this container's logical parent (generated containers are logical children of the
    // ItemsControl — punch 43); a walk covers an own-container nested under a wrapper.
    private ComboBox? OwnerComboBox
    {
        get
        {
            for (UIElement? node = LogicalParent; node is not null; node = node.LogicalParent)
            {
                if (node is ComboBox box)
                    return box;
            }

            return null;
        }
    }

    private static void OnIsSelectedChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is not ComboBoxItem item)
            return;

        // An external (user/binding) set folds into the owner's model; the owner-driven write is guarded so it
        // never echoes back into the model.
        if (!item._ownerDriven && item.OwnerComboBox is { } owner)
            owner.NotifyContainerIsSelectedChanged(item, newValue);

        // Raise Selected/Unselected OUTSIDE the owner-driven guard (mirroring ListBoxItem) so the item-level pair
        // fires for both user- and owner/model-driven selection, then IsSelectedChanged on every change.
        item.RaiseEvent(item.RentEvent(newValue ? SelectedEvent : UnselectedEvent));
        item.RaiseEvent(item.RentEvent(IsSelectedChangedEvent));
    }
}
