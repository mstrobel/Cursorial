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
        if (sender is ComboBoxItem { _ownerDriven: false } item && item.OwnerComboBox is { } owner)
            owner.NotifyContainerIsSelectedChanged(item, newValue);
    }
}
