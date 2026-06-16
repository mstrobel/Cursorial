using System.Collections;
using System.Collections.ObjectModel;

namespace Cursorial.UI.Controls;

/// <summary>
/// A control that presents a collection (design doc §12.6) — the base of <c>ListBox</c>, <c>Menu</c>,
/// <c>TabControl</c>. Items come from either the direct <see cref="Items"/> collection OR a bound
/// <see cref="ItemsSource"/> (mutually exclusive — WPF rule). An <see cref="ItemContainerGenerator"/> realizes
/// one container per item through the by-type <see cref="DataTemplate"/> chain; the <see cref="ItemsPresenter"/>
/// in the control template hosts them in the <see cref="ItemsPanel"/>. Containers are logical children of this
/// control (inheritance flows from here) and visual children of the panel (punch 43).
/// </summary>
public class ItemsControl : Control
{
    /// <summary>The bound items source (mutually exclusive with a populated <see cref="Items"/>).</summary>
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        UIProperty.Register<ItemsControl, IEnumerable?>(nameof(ItemsSource), changed: OnItemsSourceChanged);

    /// <summary>The template applied to each item's content (null ⇒ the by-type <see cref="DataTemplate"/> chain).</summary>
    public static readonly StyledProperty<DataTemplate?> ItemTemplateProperty =
        UIProperty.Register<ItemsControl, DataTemplate?>(nameof(ItemTemplate), changed: OnItemTemplateChanged);

    /// <summary>The panel template that lays out the containers (default: a vertical <see cref="StackPanel"/>); the
    /// <see cref="ItemsPresenter"/> sets <see cref="Panel.IsItemsHost"/> on the built panel.</summary>
    public static readonly StyledProperty<ITemplateContent?> ItemsPanelProperty =
        UIProperty.Register<ItemsControl, ITemplateContent?>(nameof(ItemsPanel),
            defaultValue: new FuncTemplateContent(_ => new StackPanel()), changed: OnItemsPanelChanged);

    /// <summary>A style applied to each generated container (at the Explicit layer).</summary>
    public static readonly StyledProperty<Style?> ItemContainerStyleProperty =
        UIProperty.Register<ItemsControl, Style?>(nameof(ItemContainerStyle), changed: OnItemContainerStyleChanged);

    private ItemCollection? _items;
    private bool _usingItemsSource;

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
    public ITemplateContent? ItemsPanel { get => GetValue(ItemsPanelProperty); set => SetValue(ItemsPanelProperty, value); }

    /// <inheritdoc cref="ItemContainerStyleProperty"/>
    public Style? ItemContainerStyle { get => GetValue(ItemContainerStyleProperty); set => SetValue(ItemContainerStyleProperty, value); }

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

    // ── Generator hooks (internal) ───────────────────────────────────────────────────────────────────

    internal UIElement CreateContainer(object? item, out bool isOwnContainer)
    {
        isOwnContainer = IsItemItsOwnContainer(item);
        return isOwnContainer ? (UIElement)item! : GetContainerForItemOverride();
    }

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

    private static void OnItemsPanelChanged(UIObject sender, ITemplateContent? oldValue, ITemplateContent? newValue)
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
        ItemsHost = GetTemplatePart<ItemsPresenter>("PART_ItemsHost");
    }

    /// <inheritdoc/>
    protected override void OnTearDown()
    {
        base.OnTearDown();
        ItemContainerGenerator.ReleaseSource(); // unhook a live ItemsSource so it no longer pins this control
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
