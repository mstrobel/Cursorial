// ReSharper disable CheckNamespace
namespace Cursorial.UI.Controls;

/// <summary>
/// A container whose selected state a <see cref="SelectingItemsControl"/> owns (ListBoxItem, TabItem). The selector flips it
/// through <see cref="SetIsSelectedFromOwner"/> (which uses <c>SetCurrentValue</c> internally so bindings survive,
/// per design doc §12.6), and the container guards its own echo so the owner-driven write never loops back.
/// </summary>
internal interface ISelectableContainer
{
    /// <summary>The container's current selected state (read so a pre-selected own-container can fold into the model on realization).</summary>
    bool IsSelected { get; }

    /// <summary>Sets the container's selected state on behalf of the owning <see cref="SelectingItemsControl"/> (echo-guarded).</summary>
    void SetIsSelectedFromOwner(bool selected);
}
