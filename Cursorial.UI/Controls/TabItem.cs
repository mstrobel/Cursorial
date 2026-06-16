using Cursorial.Input;
using Cursorial.UI.Input;

// ReSharper disable CheckNamespace
namespace Cursorial.UI.Controls;

/// <summary>
/// A tab in a <see cref="TabControl"/> (design doc §12.7). Its <see cref="HeaderedContentControl.Header"/> is the
/// clickable tab label (shown in the tab strip); its <see cref="ContentControl.Content"/> is the tab body (the
/// <see cref="TabControl"/> hosts only the selected tab's content). <see cref="IsSelected"/> two-way mirrors the
/// owning <see cref="SelectingItemsControl"/>'s selection (the owner writes it via <c>SetCurrentValue</c> so
/// bindings survive; <c>:selected</c> flips through <see cref="PseudoClassMapping"/>); a left click selects it.
/// </summary>
/// <remarks>Access-key folding of the <see cref="HeaderedContentControl.Header"/> (Alt+mnemonic selects the tab) is
/// a recorded deferral — selection by click and keyboard is the v1 behavior.</remarks>
public class TabItem : HeaderedContentControl, ISelectableContainer
{
    /// <summary>Whether this tab is selected. Two-way bindable; <c>:selected</c> mirrors it. Setting it from outside
    /// the owner folds into the owner's single-selection model.</summary>
    public static readonly StyledProperty<bool> IsSelectedProperty =
        UIProperty.Register<TabItem, bool>(nameof(IsSelected), defaultValue: false, changed: OnIsSelectedChanged);

    private bool _ownerDriven; // guards the owner→container write so it never echoes back into the model

    static TabItem() => PseudoClassMapping.Register<TabItem>(IsSelectedProperty, ":selected");

    /// <summary>Creates a tab (focusable — the tab strip is a single tab stop and arrows move among the tabs).</summary>
    public TabItem() => Focusable = true;

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
    // TabControl — punch 43); a walk covers an own-container nested under a wrapper.
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
        if (sender is TabItem { _ownerDriven: false } item && item.OwnerSelector is { } owner)
            owner.NotifyContainerIsSelectedChanged(item, newValue);
    }
}
