using Cursorial.Input;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

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
[TemplatePart(PartUnderline, typeof(Separator))]
public class TabItem : HeaderedContentControl, ISelectableContainer, IAccessKeyTarget
{
    /// <summary>The active-tab accent underline rule (the gallery "active tab marked by accent bar"); shown only when selected.</summary>
    private const string PartUnderline = "PART_Underline";

    /// <summary>Whether this tab is selected. Two-way bindable; <c>:selected</c> mirrors it. Setting it from outside
    /// the owner folds into the owner's single-selection model.</summary>
    public static readonly StyledProperty<bool> IsSelectedProperty =
        UIProperty.Register<TabItem, bool>(nameof(IsSelected), defaultValue: false, changed: OnIsSelectedChanged);

    private bool _ownerDriven; // guards the owner→container write so it never echoes back into the model
    private Separator? _underline;

    static TabItem()
    {
        PseudoClassMapping.Register<TabItem>(IsSelectedProperty, ":selected");
        HeaderProperty.OverrideMetadata<TabItem>(new PropertyMetadata<object?>() { ParsesAccessKeyLiterals = true });
    }

    /// <summary>Creates a tab (focusable — the tab strip is a single tab stop and arrows move among the tabs).</summary>
    public TabItem() => Focusable = true;

    /// <inheritdoc cref="IsSelectedProperty"/>
    public bool IsSelected
    {
        get => GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // The active-tab accent underline (the gallery "active tab marked by accent bar (━ cells)"): a
        // Separator whose Heavy --accent pen is applied here at LocalValue — a /template/ theme rule can't
        // override the Separator's OWN control theme pen (the Caret focus indicator is driven in code for the
        // same reason). The pen rides SetResourceReference so it tracks variant flips; visibility is gated on
        // selection (the bar row stays laid out — Hidden, not Collapsed — so the tab strip aligns).
        _underline = GetTemplatePart<Separator>(PartUnderline);
        _underline?.SetResourceReference(Control.BorderPenProperty, ThemeKeys.TabUnderlinePen);
        UpdateUnderline();
    }

    private void UpdateUnderline()
    {
        if (_underline is {} underline)
            underline.Visibility = IsSelected ? Visibility.Visible : Visibility.Hidden;
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

        if (e.Handled || e.Button != MouseButton.Left || OwnerSelector is not {} owner)
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
        if (sender is not TabItem item)
            return;

        item.UpdateUnderline(); // show/hide the accent underline as selection flips

        if (item is { _ownerDriven: false, OwnerSelector: {} owner })
            owner.NotifyContainerIsSelectedChanged(item, newValue);
    }

    // ───────────────────────────── access key (doc §12.5) ─────────────────────────────

    /// <inheritdoc/>
    bool IAccessKeyTarget.IsAccessKeyEligible => IsEffectivelyEnabled && IsEffectivelyVisible;

    /// <inheritdoc/>
    void IAccessKeyTarget.OnAccessKey(AccessKeyEventArgs e) => OnAccessKey(e);

    /// <summary>The access-key reaction (doc §12.5): a button clicks. Multi-match focuses only (ND18).</summary>
    protected virtual void OnAccessKey(AccessKeyEventArgs e)
    {
        if (IsSelected && e.IsMultiMatch)
            return; // the manager already focused us; multi-match never invokes (ND18)

        IsSelected = true;
    }
}