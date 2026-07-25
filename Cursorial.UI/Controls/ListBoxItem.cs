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

    /// <summary>The bubbling event raised whenever the item becomes selected (<see cref="IsSelected"/> ⇒ true), for both user- and owner/model-driven selection.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> SelectedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(Selected), RoutingStrategy.Bubble, typeof(ListBoxItem));

    /// <summary>The bubbling event raised whenever the item becomes unselected (<see cref="IsSelected"/> ⇒ false), for both user- and owner/model-driven selection.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> UnselectedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(Unselected), RoutingStrategy.Bubble, typeof(ListBoxItem));

    /// <summary>The bubbling event raised whenever <see cref="IsSelected"/> changes (either direction) — the selection-side parallel to <see cref="ToggleButton.IsCheckedChanged"/>.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> IsSelectedChangedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(IsSelectedChanged), RoutingStrategy.Bubble, typeof(ListBoxItem));

    private bool _ownerDriven; // guards the owner→container write so it never echoes back into the model

    static ListBoxItem() => PseudoClassMapping.Register<ListBoxItem>(IsSelectedProperty, ":selected");

    /// <summary>Creates a list-box item (focusable; the items host is a single tab stop — design doc §12.7).</summary>
    public ListBoxItem() => Focusable = true;

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

        if (e.Handled || e.Button != MouseButton.Left || OwnerSelector is not {} owner)
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
            {
                if (node is SelectingItemsControl selector)
                    return selector;
            }

            return null;
        }
    }

    private static void OnIsSelectedChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is not ListBoxItem item)
            return;

        // An external (user/binding) set folds into the owner's model; the owner-driven write is guarded so it
        // never echoes back (CD-P9-9).
        if (!item._ownerDriven && item.OwnerSelector is { } owner)
            owner.NotifyContainerIsSelectedChanged(item, newValue);

        // Raise Selected/Unselected OUTSIDE the owner-driven guard (mirroring TreeViewItem.OnIsExpandedChanged) so
        // the item-level pair fires for both user- and owner/model-driven selection.
        item.RaiseEvent(item.RentEvent(newValue ? SelectedEvent : UnselectedEvent));
        item.RaiseEvent(item.RentEvent(IsSelectedChangedEvent)); // fires on every change (cf. ToggleButton.IsCheckedChanged)
    }
}