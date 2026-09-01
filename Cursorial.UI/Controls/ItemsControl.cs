using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

using Cursorial.Markup;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A control that presents a collection (design doc §12.6) — the base of <c>ListBox</c>, <c>Menu</c>,
/// <c>TabControl</c>. Items come from either the direct <see cref="Items"/> collection OR a bound
/// <see cref="ItemsSource"/> (mutually exclusive — WPF rule). An <see cref="ItemContainerGenerator"/> realizes
/// one container per item through the by-type <see cref="DataTemplate"/> chain; the <see cref="ItemsPresenter"/>
/// in the control template hosts them in the <see cref="ItemsPanel"/>. Containers are logical children of this
/// control (inheritance flows from here) and visual children of the panel (punch 43).
/// </summary>
[ContentProperty("Items")]
public class ItemsControl : Control
{
    /// <summary>The bound items source (mutually exclusive with a populated <see cref="Items"/>).</summary>
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        UIProperty.Register<ItemsControl, IEnumerable?>(nameof(ItemsSource), changed: OnItemsSourceChanged);

    /// <summary>The template applied to each item's content (null ⇒ the by-type <see cref="DataTemplate"/> chain).</summary>
    public static readonly StyledProperty<DataTemplate?> ItemTemplateProperty =
        UIProperty.Register<ItemsControl, DataTemplate?>(nameof(ItemTemplate), changed: OnItemTemplateChanged);

    /// <summary>The panel template that lays out the containers (default: a vertical <see cref="StackPanel"/>); the
    /// <see cref="ItemsPresenter"/> sets <see cref="Panel.IsItemsHost"/> on the built panel. Typed
    /// <see cref="ItemsPanelTemplate"/> (WPF parity) so it can be authored in XAML.</summary>
    public static readonly StyledProperty<ItemsPanelTemplate?> ItemsPanelProperty =
        UIProperty.Register<ItemsControl, ItemsPanelTemplate?>(nameof(ItemsPanel),
            defaultValue: new ItemsPanelTemplate(_ => new StackPanel()), changed: OnItemsPanelChanged);

    /// <summary>A style applied to each generated container (at the Explicit layer).</summary>
    public static readonly StyledProperty<Style?> ItemContainerStyleProperty =
        UIProperty.Register<ItemsControl, Style?>(nameof(ItemContainerStyle), changed: OnItemContainerStyleChanged);

    /// <summary>Whether type-ahead (<see cref="TextSearch"/>) is on — printable keys jump the current item to the first match (default <c>true</c>).</summary>
    public static readonly StyledProperty<bool> IsTextSearchEnabledProperty =
        UIProperty.Register<ItemsControl, bool>(nameof(IsTextSearchEnabled), defaultValue: true);

    /// <summary>Whether type-ahead matching is case-sensitive (default <c>false</c>).</summary>
    public static readonly StyledProperty<bool> IsTextSearchCaseSensitiveProperty =
        UIProperty.Register<ItemsControl, bool>(nameof(IsTextSearchCaseSensitive), defaultValue: false);

    private ItemCollection? _items;
    private bool _usingItemsSource;
    private TextSearchController? _textSearch;

    /// <summary>Creates an items control.</summary>
    public ItemsControl() => ItemContainerGenerator = new ItemContainerGenerator(this);

    /// <inheritdoc cref="ItemsSourceProperty"/>
    public IEnumerable? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }

    /// <summary>The direct items collection (used when <see cref="ItemsSource"/> is null).</summary>
    public ItemCollection Items
    {
        get
        {
            if (_items is null)
            {
                _items = new ItemCollection(this);
                if (!_usingItemsSource)
                    UpdateGeneratorSource(); // the direct collection becomes the source
            }
            return _items;
        }
    }

    /// <inheritdoc cref="ItemTemplateProperty"/>
    public DataTemplate? ItemTemplate { get => GetValue(ItemTemplateProperty); set => SetValue(ItemTemplateProperty, value); }

    /// <inheritdoc cref="ItemsPanelProperty"/>
    public ItemsPanelTemplate? ItemsPanel { get => GetValue(ItemsPanelProperty); set => SetValue(ItemsPanelProperty, value); }

    /// <inheritdoc cref="ItemContainerStyleProperty"/>
    public Style? ItemContainerStyle { get => GetValue(ItemContainerStyleProperty); set => SetValue(ItemContainerStyleProperty, value); }

    /// <inheritdoc cref="IsTextSearchEnabledProperty"/>
    public bool IsTextSearchEnabled { get => GetValue(IsTextSearchEnabledProperty); set => SetValue(IsTextSearchEnabledProperty, value); }

    /// <inheritdoc cref="IsTextSearchCaseSensitiveProperty"/>
    public bool IsTextSearchCaseSensitive { get => GetValue(IsTextSearchCaseSensitiveProperty); set => SetValue(IsTextSearchCaseSensitiveProperty, value); }

    /// <summary>The container generator (control-lifetime).</summary>
    public ItemContainerGenerator ItemContainerGenerator { get; }

    /// <summary>True when items come from <see cref="ItemsSource"/> (the direct <see cref="Items"/> lane is read-only then).</summary>
    public bool HasItemsSource => _usingItemsSource;

    // ── Container policy (overridable; design doc §12.6) ─────────────────────────────────────────────

    /// <summary>The container element for an item (default: a <see cref="ContentPresenter"/>). Override for typed containers (e.g. ListBoxItem).</summary>
    protected virtual UIElement GetContainerForItemOverride() => new ContentPresenter();

    /// <summary>Whether <paramref name="item"/> is already its own container (a <see cref="UIElement"/> is — no presenter wrapper).</summary>
    protected virtual bool IsItemItsOwnContainer(object? item) => item is UIElement;

    /// <summary>Prepares a freshly-realized container for its item (DataContext + content). Override to extend.
    /// Handles both a bare <see cref="ContentPresenter"/> (the default container) and a <see cref="ContentControl"/>
    /// container (e.g. <c>ListBoxItem</c>/<c>TabItem</c>) — both get the item as content + the item template.</summary>
    protected virtual void PrepareContainerForItemOverride(UIElement container, object? item)
    {
        switch (container)
        {
            case ContentPresenter presenter:
                presenter.Content = item;
                presenter.ContentTemplate = ItemTemplate;
                break;
            case ContentControl control:
                control.Content = item;
                control.ContentTemplate = ItemTemplate;
                break;
        }
    }

    /// <summary>Undoes <see cref="PrepareContainerForItemOverride"/> before the container is detached (unhook while
    /// bindings are live). <paramref name="item"/> is the source item the container was prepared for (so subclasses
    /// can unhook item-specific state — WPF parity).</summary>
    protected virtual void ClearContainerForItemOverride(UIElement container, object? item)
    {
        switch (container)
        {
            case ContentPresenter presenter:
                presenter.ClearValue(ContentPresenter.ContentProperty);
                presenter.ClearValue(ContentPresenter.ContentTemplateProperty);
                break;
            case ContentControl control:
                control.ClearValue(ContentControl.ContentProperty);
                control.ClearValue(ContentControl.ContentTemplateProperty);
                break;
        }
    }

    // ── Type-ahead (TextSearch; design doc §12.6) ─────────────────────────────────────────────────────

    /// <summary>Whether this control actually moves a current item on a type-ahead match. Off for a plain
    /// <see cref="ItemsControl"/> / <c>Menu</c> (so they never swallow a printable key); selectors + the tree opt in.</summary>
    protected virtual bool TextSearchNavigates => false;

    /// <summary>The current item index type-ahead cycles from (default −1 = none). Overridden by selectors to the selection.</summary>
    protected virtual int CurrentTextSearchIndex => -1;

    /// <summary>The match text for the item at <paramref name="index"/>: per-item <see cref="TextSearch.TextProperty"/>, else
    /// the control's <see cref="TextSearch.TextPathProperty"/> evaluated against the item, else the item's <c>ToString()</c>.</summary>
    protected virtual string? GetTextSearchText(int index)
    {
        if (ItemContainerGenerator.ContainerFromIndex(index) is not { } container)
            return null;

        if (TextSearch.GetText(container) is { } onContainer)
            return onContainer;

        var item = ItemContainerGenerator.ItemFromContainer(container);
        if (item is UIElement element && TextSearch.GetText(element) is { } onItem)
            return onItem;

        if (TextSearch.GetTextPath(this) is { Length: > 0 } path && item is not null)
            return TextSearch.EvaluatePath(item, path);

        // An own-container item (a ComboBoxItem/ListBoxItem used directly as an item) is a control element, not
        // data — its ToString() is a type name. A ContentControl displays its Content, so match that. Data items
        // (strings/view-models) aren't ContentControls and pass through unchanged. Header-based navigating
        // containers match their own text by overriding this (TreeView → TreeViewItem.Header); Menu and TabControl
        // don't type-ahead-navigate (TextSearchNavigates is false), so they never reach here.
        var display = item is ContentControl contentControl ? contentControl.Content : item;

        return display?.ToString();
    }

    /// <summary>Reacts to a type-ahead match (default: nothing). Selectors select it; the tree focuses + selects the node.</summary>
    protected virtual void OnTextSearchMatch(int containerIndex)
    {
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(in UIPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(in args);

        // Leaving the control resets the type-ahead buffer (the documented focus-loss reset; intra-control item-to-item
        // focus keeps IsKeyboardFocusWithin true, so a multi-keystroke search isn't disturbed).
        if (ReferenceEquals(args.Property, IsKeyboardFocusWithinProperty) && !args.GetNewValue<bool>())
            _textSearch?.Reset();
    }

    /// <inheritdoc/>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);

        if (e.Handled || e.FromPaste || !IsTextSearchEnabled || !TextSearchNavigates)
            return;

        var text = e.Text;
        if (text.Length == 0 || char.IsControl(text.Span[0]))
            return; // not printable input (e.g. a synthesized control char)

        var count = ItemContainerGenerator.ContainerCount;
        if (count == 0)
            return;

        _textSearch ??= new TextSearchController();
        var match = _textSearch.Handle(text.ToString(), count, GetTextSearchText, CurrentTextSearchIndex, IsTextSearchCaseSensitive);
        if (match < 0)
            return;

        OnTextSearchMatch(match);
        e.Handled = true;
    }

    // ── Generator hooks (internal) ───────────────────────────────────────────────────────────────────

    internal UIElement CreateContainer(object? item, out bool isOwnContainer)
    {
        isOwnContainer = IsItemItsOwnContainer(item);
        return isOwnContainer ? (UIElement)item! : GetContainerForItemOverride();
    }

    /// <summary>Whether <paramref name="item"/> is its own container (the generator's recycle gate — own-containers
    /// are never pooled). Internal accessor over the <see cref="IsItemItsOwnContainer"/> policy hook.</summary>
    internal bool IsOwnContainer(object? item) => IsItemItsOwnContainer(item);

    /// <summary>Creates a fresh generated (non-own) container via the policy hook — the recycle-miss path
    /// (the pool was empty), so the generator never wraps an own-container here.</summary>
    internal UIElement CreateGeneratedContainer() => GetContainerForItemOverride();

    internal void AddContainerLogical(UIElement container) => AddLogicalChild(container);

    internal void RemoveContainerLogical(UIElement container) => RemoveLogicalChild(container);

    internal void PrepareContainerForItem(UIElement container, object? item, bool isOwnContainer)
    {
        if (!isOwnContainer)
        {
            container.DataContext = item;
            PrepareContainerForItemOverride(container, item);
            if (ItemContainerStyle is { } style)
                container.Style = style; // applies at the Explicit layer; type-selector styles compose underneath
        }
    }

    internal void ClearContainerForItem(UIElement container, object? item)
    {
        // Own-container ⇒ container IS the item (CreateContainer returned the item itself); leave the user's element
        // untouched. Identity is the only reliable signal — the container's type is always UIElement, and its
        // DataContext can't distinguish the cases (an own-container's DataContext is never set to the item).
        if (ReferenceEquals(container, item))
            return;

        ClearContainerForItemOverride(container, item);
        container.Style = null; // drop the Explicit-layer ItemContainerStyle (Style is a plain property)
    }

    internal void ResetRecycledContainer(UIElement container) => ResetRecycledContainerCore(container);

    /// <summary>Resets a recycled container's owner-specific transient state before it is re-prepared for a new item
    /// (design doc §12.6 recycle reset contract). The base clears <see cref="ISelectableContainer.IsSelected"/> (so a
    /// previously-selected container does not show selected for an unselected item). Controls whose container carries
    /// extra transient state (e.g. <c>TreeViewItem.IsExpanded</c>) override and call <c>base</c>. Interaction state
    /// (<c>:pointerover</c>/<c>:pressed</c>/<c>:focus</c>) is NOT reset here — the host's visual detach clears it
    /// before pooling (it cannot be set via <c>PseudoClasses.Set</c>).</summary>
    protected virtual void ResetRecycledContainerCore(UIElement container)
    {
        if (container is ISelectableContainer selectable)
            selectable.SetIsSelectedFromOwner(false);
    }

    // ── Source wiring ────────────────────────────────────────────────────────────────────────────────

    private static void OnItemsSourceChanged(UIObject sender, IEnumerable? oldValue, IEnumerable? newValue)
    {
        if (sender is not ItemsControl control)
            return;

        if (newValue is not null && control._items is { Count: > 0 })
            throw new InvalidOperationException(
                "ItemsControl: cannot set ItemsSource while Items is populated directly — use one or the other (WPF rule).");

        control._usingItemsSource = newValue is not null;
        control.UpdateGeneratorSource();
    }

    private static void OnItemTemplateChanged(UIObject sender, DataTemplate? oldValue, DataTemplate? newValue)
        => (sender as ItemsControl)?.ResetContainers(); // runtime template change ⇒ full re-realize (v1 policy)

    private static void OnItemsPanelChanged(UIObject sender, ItemsPanelTemplate? oldValue, ItemsPanelTemplate? newValue)
        => (sender as ItemsControl)?.ItemsHost?.RebuildPanel();

    private static void OnItemContainerStyleChanged(UIObject sender, Style? oldValue, Style? newValue)
        => (sender as ItemsControl)?.ResetContainers();

    private void UpdateGeneratorSource()
    {
        var effective = _usingItemsSource ? ItemsSource : _items;
        ItemContainerGenerator.SetSource(effective is null ? null : new ItemsSourceView(effective));
    }

    private void ResetContainers() => UpdateGeneratorSource(); // re-realize against the same source with new policy

    /// <summary>The mutation guard for the direct <see cref="Items"/> lane (thrown when <see cref="ItemsSource"/> owns the items).</summary>
    internal void ThrowIfUsingItemsSource()
    {
        if (_usingItemsSource)
            throw new InvalidOperationException(
                "ItemsControl: cannot mutate Items while ItemsSource is set — clear ItemsSource first (WPF rule).");
    }

    /// <summary>The <see cref="ItemsPresenter"/> template part (null until the template expands).</summary>
    internal ItemsPresenter? ItemsHost { get; private set; }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        ItemsHost = FindItemsPresenter();
    }

    protected internal ItemsPresenter? FindItemsPresenter()
        => GetTemplatePart<ItemsPresenter>("PART_ItemsHost") ?? FindItemsPresenter(this, VisualChildrenList);

    private static ItemsPresenter? FindItemsPresenter(UIElement element, List<UIElement>? children)
    {
        if (children is null) return element as ItemsPresenter;

        foreach (var child in children)
        {
            if (child is ItemsPresenter presenter)
                return presenter;

            if (FindItemsPresenter(child, child.VisualChildrenList) is {} childResult)
                return childResult;
        }

        foreach (var child in children)
        {
            if (FindItemsPresenter(child, child.LogicalChildrenList) is {} childResult)
                return childResult;
        }

        return null;
    }

    // ── keyboard paging support (the Control.HandlesScrolling selectors) ──────────────────────────────

    /// <summary>The <see cref="ScrollViewer"/> that scrolls this control's items — the nearest
    /// <see cref="ScrollViewer"/> ancestor of the items host (the template nests the
    /// <see cref="ItemsPresenter"/> inside it). <see langword="null"/> when the items are not hosted in a
    /// scroll viewport (the ComboBox / Menu popups host the items directly in v1), in which case everything
    /// is visible and a "page" is the whole list.</summary>
    internal ScrollViewer? FindItemsScrollViewer()
    {
        for (UIElement? node = (UIElement?)ItemsHost ?? ItemsPanelFromItemsControl(this);
             node is not null;
             node = node.VisualParent)
        {
            if (node is ScrollViewer scroll)
                return scroll;
        }

        return null;
    }

    /// <summary>The number of items that move on one PageUp/PageDown step: the count of items that fit in the
    /// scroll viewport (extent ÷ count gives the average row height; v1 items are ~1 row). When the items are
    /// not in a scroll viewport (everything is visible), a page is the whole list so PageUp/PageDown collapse
    /// to Home/End — the correct behavior for a non-scrolling list.</summary>
    internal int ItemsPerPage()
    {
        var count = ItemContainerGenerator.ContainerCount;
        if (count <= 0)
            return 1;

        if (FindItemsScrollViewer() is not { } scroll || scroll.Viewport.Rows <= 0)
            return count; // not scrolling ⇒ a page is the whole list (PageUp/PageDown ≡ Home/End)

        var avgRows = scroll.Extent.Rows > 0 ? Math.Max(1.0, (double) scroll.Extent.Rows / count) : 1.0;
        return Math.Clamp((int) (scroll.Viewport.Rows / avgRows), 1, count);
    }

    /// <inheritdoc/>
    protected override void OnTearDown()
    {
        base.OnTearDown();
        ResetTextSearch();
        ItemContainerGenerator.ReleaseSource(); // unhook a live ItemsSource so it no longer pins this control
    }

    protected void ResetTextSearch()
    {
        _textSearch?.Reset(); // stop the idle-reset timer
    }

    /// <summary>
    /// Retrieves the items panel that serves as the container for the items of a specified ItemsControl instance.
    /// </summary>
    /// <param name="owner">The ItemsControl instance whose associated items panel is to be obtained.</param>
    /// <returns>The items panel associated with the given ItemsControl, or null if no items panel is found.</returns>
    public static Panel? ItemsPanelFromItemsControl(ItemsControl? owner)
    {
        var children = owner?.ItemsHost?.VisualChildrenList;
        if (children is null) return null;

        foreach (var child in children)
        {
            if (child is Panel { IsItemsHost: true } panel)
                return panel;

        }

        return null;
    }

    private static readonly AttachedProperty<object?> ItemForItemContainerProperty =
        ItemContainerGenerator.ItemForItemContainerProperty;
    private static readonly AttachedProperty<ItemsControl?> ItemsControlForItemContainerProperty =
        ItemContainerGenerator.ItemsControlForItemContainerProperty;

    /// <summary>
    /// Retrieves the items control that owns an item container.
    /// </summary>
    /// <param name="container">The item container whose owner is to be obtained.</param>
    /// <returns>The items control that owns the given container, or null if no owner is found.</returns>
    public static ItemsControl? ItemsControlForItemContainer(UIElement? container)
        => container?.GetValue(ItemContainerGenerator.ItemsControlForItemContainerProperty);

    /// <summary>Indicates whether a given UI element is a item container for an <see cref="ItemsControl"/>.</summary>
    public static bool IsItemContainer([NotNullWhen(true)] UIElement? container)
        => container?.GetValueSource(ItemForItemContainerProperty) is { Kind: ValueSourceKind.Local };

    /// <summary>
    /// Indicates whether a given UI element is a item container for an <see cref="ItemsControl"/> and attempts
    /// to locate the <see cref="ItemsControl"/> to which the container belongs. While failure to locate the
    /// <see cref="ItemsControl"/> is unlikely, it is possible.
    /// </summary>
    public static bool IsItemContainer(UIElement? container, out ItemsControl? itemsControl)
    {
        itemsControl = null;

        if (IsItemContainer(container))
        {
            if (container.LogicalParent is ItemsControl lp)
                itemsControl = lp;
            else if (container.VisualParent is Panel { IsItemsHost: true, UIParent: ItemsControl ic })
                itemsControl = ic;
            return true;
        }

        return false;
    }
}

/// <summary>The direct-items lane of an <see cref="ItemsControl"/> (design doc §12.6): an observable list that
/// forwards changes to the generator and rejects mutation while <see cref="ItemsControl.ItemsSource"/> is set.</summary>
public sealed class ItemCollection : ObservableCollection<object?>
{
    private readonly ItemsControl _owner;

    internal ItemCollection(ItemsControl owner) => _owner = owner;

    /// <inheritdoc/>
    protected override void InsertItem(int index, object? item)
    {
        _owner.ThrowIfUsingItemsSource();
        base.InsertItem(index, item);
    }

    /// <inheritdoc/>
    protected override void SetItem(int index, object? item)
    {
        _owner.ThrowIfUsingItemsSource();
        base.SetItem(index, item);
    }

    /// <inheritdoc/>
    protected override void RemoveItem(int index)
    {
        _owner.ThrowIfUsingItemsSource();
        base.RemoveItem(index);
    }

    /// <inheritdoc/>
    protected override void ClearItems()
    {
        _owner.ThrowIfUsingItemsSource();
        base.ClearItems();
    }
}
