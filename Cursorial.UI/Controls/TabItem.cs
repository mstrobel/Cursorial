using Cursorial.Input;
using Cursorial.Markup;
using Cursorial.UI.Input;

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
[TemplatePart(PartHeaderSite, typeof(UIElement))]
[ContentProperty(nameof(Content))]
public class TabItem : HeaderedContentControl, ISelectableContainer, IAccessKeyTarget
{
    /// <summary>The active-tab accent underline rule (the gallery "active tab marked by accent bar"); shown only when selected.</summary>
    private const string PartUnderline = "PART_Underline";
    private const string PartHeaderSite = "PART_HeaderSite";

    /// <summary>Whether this tab is selected. Two-way bindable; <c>:selected</c> mirrors it. Setting it from outside
    /// the owner folds into the owner's single-selection model.</summary>
    public static readonly StyledProperty<bool> IsSelectedProperty =
        SelectableItemContainer.IsSelectedProperty.AddOwner<TabItem>();

    /// <summary>Whether this tab can be SELECTED (default true). A non-selectable tab still participates in the strip's
    /// focus/arrow navigation (it is reachable and highlights when focused) but is never made the selected tab — a
    /// click/arrow onto it FOCUSES it without changing the shown body. Used for a command-style tab that has no body
    /// of its own (the Ribbon's File tab, which opens Backstage on activation rather than selecting a band).</summary>
    public static readonly StyledProperty<bool> IsSelectableProperty =
        UIProperty.Register<TabItem, bool>(nameof(IsSelectable), defaultValue: true);

    /// <summary>The bubbling event raised whenever this tab becomes the selected tab (<see cref="IsSelected"/> ⇒ true) —
    /// a per-tab activation hook distinct from the owner's selection-changed notification.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> SelectedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(Selected), RoutingStrategy.Bubble, typeof(TabItem));

    /// <summary>The bubbling event raised whenever this tab stops being the selected tab (<see cref="IsSelected"/> ⇒ false).</summary>
    public static readonly RoutedEvent<RoutedEventArgs> UnselectedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(Unselected), RoutingStrategy.Bubble, typeof(TabItem));

    /// <summary>The bubbling event raised whenever <see cref="IsSelected"/> changes (either direction) — the selection-side parallel to <see cref="ToggleButton.IsCheckedChanged"/>.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> IsSelectedChangedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(IsSelectedChanged), RoutingStrategy.Bubble, typeof(TabItem));

    private bool _ownerDriven; // guards the owner→container write so it never echoes back into the model
    private Separator? _underline;

    static TabItem()
    {
        IsSelectedProperty.OverrideMetadata<TabItem>(
            new PropertyMetadata<bool>(DefaultValue: false, Changed: OnIsSelectedChanged)
        );
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

    /// <inheritdoc cref="IsSelectableProperty"/>
    public bool IsSelectable
    {
        get => GetValue(IsSelectableProperty);
        set => SetValue(IsSelectableProperty, value);
    }

    /// <summary>CLR sugar over <see cref="SelectedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? Selected { add => AddHandler(SelectedEvent, value!); remove => RemoveHandler(SelectedEvent, value!); }

    /// <summary>CLR sugar over <see cref="UnselectedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? Unselected { add => AddHandler(UnselectedEvent, value!); remove => RemoveHandler(UnselectedEvent, value!); }

    /// <summary>CLR sugar over <see cref="IsSelectedChangedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? IsSelectedChanged { add => AddHandler(IsSelectedChangedEvent, value!); remove => RemoveHandler(IsSelectedChangedEvent, value!); }

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
        if (IsSelectable) // a command tab (IsSelectable=false) focuses on click but never selects (its own OnMouseDown activates)
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

        // Per-tab activation hook (Selector.Selected/Unselected, MenuItem.Checked/Unchecked analog): bubbles off the
        // item so a handler on the TabControl sees which tab flipped, independent of the owner's SelectionChanged.
        var routedEvent = newValue ? SelectedEvent : UnselectedEvent;
        item.RaiseEvent(item.RentEvent(routedEvent));
        item.RaiseEvent(item.RentEvent(IsSelectedChangedEvent)); // fires on every change (cf. ToggleButton.IsCheckedChanged)

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

        if (!IsSelectable)
            return; // a command tab is focus-only on access-key (the manager already focused it) — never selects

        IsSelected = true;
    }
}