using Cursorial.Input;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// The container for a <see cref="ComboBox"/> item (design doc §12.11). Mirrors <see cref="ListBoxItem"/>:
/// <see cref="SelectableItemContainer.IsSelected"/> two-way mirrors the owning <see cref="ComboBox"/>'s
/// <see cref="SelectionModel"/> (owner-written via <c>SetCurrentValue</c> so bindings survive;
/// <c>:selected</c> flips through <see cref="PseudoClassMapping"/>). A left click selects it AND commits —
/// the drop-down closes.
/// </summary>
public class ComboBoxItem : SelectableItemContainer
{
    /// <summary>Creates a combo-box item (focusable; the drop-down list is the keyboard surface).</summary>
    public ComboBoxItem() => Focusable = true;

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.Handled || e.Button != MouseButton.Left || OwnerComboBox is not {} owner)
            return;

        base.OnMouseDown(e);
        owner.CommitAndClose();
    }

    // The owning combo box is this container's logical parent (generated containers are logical children of the
    // ItemsControl — punch 43); a walk covers an own-container nested under a wrapper.
    private ComboBox? OwnerComboBox => (ComboBox?) OwnerSelector;
}
