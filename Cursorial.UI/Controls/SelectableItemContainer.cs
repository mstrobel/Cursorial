using Cursorial.Input;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

public abstract class SelectableItemContainer : ContentControl, ISelectableContainer
{
    /// <summary>
    /// Whether the item is selected. Two-way bindable; <c>:selected</c> mirrors it. Setting it from outside
    /// the owner folds into the owner's selection (CD-P9-9: the model stays the source of truth).
    /// </summary>
    public static readonly StyledProperty<bool> IsSelectedProperty =
        SelectingItemsControl.IsSelectedProperty.AddOwner<SelectableItemContainer>();

    /// <summary>The bubbling event raised whenever the item becomes selected (<see cref="IsSelected"/> ⇒ true), for both user- and owner/model-driven selection.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> SelectedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(Selected), RoutingStrategy.Bubble, typeof(SelectableItemContainer));

    /// <summary>The bubbling event raised whenever the item becomes unselected (<see cref="IsSelected"/> ⇒ false), for both user- and owner/model-driven selection.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> UnselectedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(Unselected), RoutingStrategy.Bubble, typeof(SelectableItemContainer));

    /// <summary>The bubbling event raised whenever <see cref="IsSelected"/> changes (either direction) — the selection-side parallel to <see cref="ToggleButton.IsCheckedChanged"/>.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> IsSelectedChangedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(IsSelectedChanged), RoutingStrategy.Bubble, typeof(SelectableItemContainer));

    private bool _ownerDrivenSelectionInProgress;

    static SelectableItemContainer()
    {
        IsSelectedProperty.OverrideMetadata<SelectableItemContainer>(
            new PropertyMetadata<bool>(Changed: OnContainerIsSelectedChanged)
        );

        PseudoClassMapping.Register<SelectableItemContainer>(IsSelectedProperty, ":selected");

        AffectsRender<SelectableItemContainer>(IsSelectedProperty);
    }

    // ReSharper disable once EmptyConstructor
    protected SelectableItemContainer() {}

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

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Handled || e.Button != MouseButton.Left || OwnerSelector is not {} owner)
            return;

        Focus();
        owner.HandleContainerPointerSelect(this, e.Modifiers, e.ClickCount);
        e.Handled = true;
    }

    void ISelectableContainer.SetIsSelectedFromOwner(bool selected)
    {
        _ownerDrivenSelectionInProgress = true;

        try
        {
            SetCurrentValue(IsSelectedProperty, selected); // SetCurrentValue preserves a two-way IsSelected binding
        }
        finally
        {
            _ownerDrivenSelectionInProgress = false;
        }
    }

    protected SelectingItemsControl? OwnerSelector
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

    private static void OnContainerIsSelectedChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is not SelectableItemContainer sic) return;
        sic.OnIsSelectedChanged(sic._ownerDrivenSelectionInProgress, oldValue, newValue);
    }

    /// <summary>
    /// Overridable handler for a selection state change. The base method raises <see cref="SelectedEvent"/> or
    /// <see cref="UnselectedEvent"/>, and then <see cref="IsSelectedChangedEvent"/>.
    /// </summary>
    /// <param name="setByOwner">
    /// Whether the selection was set by the owning <see cref="SelectingItemsControl"/>, a <c>false</c> value
    /// indicating that the owner needs to be notified.
    /// </param>
    /// <param name="oldValue">Whether the container was previously selected.</param>
    /// <param name="newValue">Whether the container is selected now.</param>
    // ReSharper disable once UnusedParameter.Global
    protected internal virtual void OnIsSelectedChanged(bool setByOwner, bool oldValue, bool newValue)
    {
        // An external (user/binding) set folds into the owner's model; the owner-driven write is guarded so it
        // never echoes back (CD-P9-9).
        if (!setByOwner && OwnerSelector is {} owner)
            owner.NotifyContainerIsSelectedChanged(this, newValue);

        // Raise Selected/Unselected OUTSIDE the owner-driven guard (mirroring TreeViewItem.OnIsExpandedChanged) so
        // the item-level pair fires for both user- and owner/model-driven selection.
        RaiseEvent(RentEvent(newValue ? SelectedEvent : UnselectedEvent));
        RaiseEvent(RentEvent(IsSelectedChangedEvent)); // fires on every change (cf. IsCheckedChanged)
    }
}