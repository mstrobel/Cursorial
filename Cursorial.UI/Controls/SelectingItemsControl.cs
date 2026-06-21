using Cursorial.Input;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// The selection-aware <see cref="ItemsControl"/> base (design doc §12.6) — the shared parent of
/// <see cref="ListBox"/> (P9.3) and TabControl (P9.5). It owns a <see cref="SelectionModel"/> and projects it onto
/// the two-way <see cref="SelectedIndex"/>/<see cref="SelectedItem"/> <c>DirectProperty</c>s and the containers'
/// <c>IsSelected</c> (via <see cref="ISelectableContainer"/>), forwards the generator's structural changes to the
/// model so selection indices stay aligned, and raises <see cref="ItemActivated"/>.
/// </summary>
public abstract class SelectingItemsControl : ItemsControl
{
    /// <summary>How many items may be selected (mirrors <see cref="SelectionModel.Mode"/>).</summary>
    public static readonly StyledProperty<SelectionMode> SelectionModeProperty =
        UIProperty.Register<SelectingItemsControl, SelectionMode>(nameof(SelectionMode), defaultValue: SelectionMode.Single, changed: OnSelectionModeChanged);

    /// <summary>The lead selected index (<c>−1</c> = none), two-way bindable. Mirrors <see cref="SelectionModel.SelectedIndex"/>.</summary>
    public static readonly DirectProperty<SelectingItemsControl, int> SelectedIndexProperty =
        UIProperty.RegisterDirect<SelectingItemsControl, int>(nameof(SelectedIndex), static s => s._selectedIndex, static (s, v) => s.SetSelectedIndexExternal(v));

    /// <summary>The lead selected item (<c>null</c> = none), two-way bindable.</summary>
    public static readonly DirectProperty<SelectingItemsControl, object?> SelectedItemProperty =
        UIProperty.RegisterDirect<SelectingItemsControl, object?>(nameof(SelectedItem), static s => s._selectedItem, static (s, v) => s.SetSelectedItemExternal(v));

    /// <summary>Bubbling event raised when an item is activated (Enter / double-click — distinct from selection).</summary>
    public static readonly RoutedEvent<ItemActivatedEventArgs> ItemActivatedEvent =
        RoutedEvent<ItemActivatedEventArgs>.Register(nameof(ItemActivated), RoutingStrategy.Bubble, typeof(SelectingItemsControl));

    private readonly SelectionModel _selection = new();
    private int _selectedIndex = -1;
    private object? _selectedItem;
    private bool _suppressContainerSync; // structural fixups: survivors are already correct, so skip the delta-sync

    /// <summary>Initializes the selection wiring.</summary>
    protected SelectingItemsControl()
    {
        _selection.SelectionChanged += OnModelSelectionChanged;
        ItemContainerGenerator.ContainersChanged += OnContainersChanged;
        ItemContainerGenerator.ContainersRealizedChanged += OnContainersRealized; // V3 reconcile-on-realize (virtualizing only)
    }

    /// <inheritdoc cref="SelectionModeProperty"/>
    public SelectionMode SelectionMode { get => GetValue(SelectionModeProperty); set => SetValue(SelectionModeProperty, value); }

    /// <inheritdoc cref="SelectedIndexProperty"/>
    public int SelectedIndex { get => _selectedIndex; set => SetSelectedIndexExternal(value); }

    /// <inheritdoc cref="SelectedItemProperty"/>
    public object? SelectedItem { get => _selectedItem; set => SetSelectedItemExternal(value); }

    /// <summary>The selected items in ascending index order (a snapshot).</summary>
    public IReadOnlyList<object?> SelectedItems
    {
        get
        {
            var indexes = _selection.SelectedIndexes;
            var items = new object?[indexes.Count];
            for (var i = 0; i < indexes.Count; i++)
                items[i] = ItemFromIndex(indexes[i]);
            return items;
        }
    }

    /// <summary>CLR sugar over <see cref="ItemActivatedEvent"/>.</summary>
    public event EventHandler<ItemActivatedEventArgs>? ItemActivated
    {
        add => AddHandler(ItemActivatedEvent, value!);
        remove => RemoveHandler(ItemActivatedEvent, value!);
    }

    /// <summary>Raised when the selected set changes (membership only; index-based — see <see cref="SelectionModel"/>).</summary>
    public event EventHandler<SelectionChangedEventArgs>? SelectionChanged;

    /// <summary>The selection model (the source of truth — the input layer drives it).</summary>
    private protected SelectionModel Selection => _selection;

    /// <summary>The item at <paramref name="index"/> read from the items view, or null. Independent of realization
    /// (so <see cref="SelectedItem"/>/<see cref="SelectedItems"/> resolve for a selected-but-unrealized index in
    /// virtualizing mode); in eager mode this equals the realized container's stamp.</summary>
    private protected object? ItemFromIndex(int index) => ItemContainerGenerator.ItemFromIndex(index);

    private protected int IndexFromItem(object? item)
    {
        for (var i = 0; i < ItemContainerGenerator.ContainerCount; i++)
            if (Equals(ItemFromIndex(i), item))
                return i;
        return -1;
    }

    // ── selection ops driven by the input layer / containers ──────────────────────────────────────────

    /// <summary>Applies a pointer/keyboard selection gesture at <paramref name="index"/> per the modifiers
    /// (Ctrl = toggle, Shift = range-from-anchor, otherwise replace) — the input mapping onto the model primitives.</summary>
    private protected void SelectByGesture(int index, KeyModifiers modifiers)
    {
        var ctrl = (modifiers & KeyModifiers.Control) != 0;
        var shift = (modifiers & KeyModifiers.Shift) != 0;

        if (SelectionMode == SelectionMode.Multiple && shift)
            _selection.SelectRangeFromAnchor(index);
        else if (SelectionMode == SelectionMode.Multiple && ctrl)
            _selection.Toggle(index);
        else
            _selection.Select(index);
    }

    /// <summary>Raises <see cref="ItemActivatedEvent"/> for the item at <paramref name="index"/>.</summary>
    private protected bool RaiseItemActivated(int index)
    {
        if (index < 0)
            return false;

        var args = new ItemActivatedEventArgs(ItemActivatedEvent, this, ItemFromIndex(index), index);

        RaiseEvent(args);
        
        return args.Handled;
    }

    /// <summary>A container reported a pointer selection gesture (its own <c>OnMouseDown</c>): select per the
    /// modifiers, and activate on a double-click.</summary>
    internal void HandleContainerPointerSelect(UIElement container, KeyModifiers modifiers, int clickCount)
    {
        var index = ItemContainerGenerator.IndexFromContainer(container);
        if (index < 0)
            return;

        SelectByGesture(index, modifiers);
        if (clickCount >= 2)
            RaiseItemActivated(index);
    }

    /// <summary>A container's <c>IsSelected</c> was set from outside the owner (binding / direct assignment) — fold
    /// it into the model.</summary>
    internal void NotifyContainerIsSelectedChanged(UIElement container, bool isSelected)
    {
        var index = ItemContainerGenerator.IndexFromContainer(container);
        if (index < 0)
            return;

        if (isSelected && !_selection.IsSelected(index))
        {
            if (SelectionMode == SelectionMode.Single)
                _selection.Select(index);
            else
                _selection.Toggle(index);
        }
        else if (!isSelected && _selection.IsSelected(index))
        {
            if (SelectionMode == SelectionMode.Single)
                _selection.Select(-1);
            else
                _selection.Toggle(index);
        }
    }

    /// <summary>Selectors navigate on type-ahead.</summary>
    private protected override bool TextSearchNavigates => true;

    /// <summary>Type-ahead cycles from the current selection.</summary>
    private protected override int CurrentTextSearchIndex => _selectedIndex;

    /// <summary>A type-ahead match selects the item (selectors); subclasses refine with focus.</summary>
    private protected override void OnTextSearchMatch(int containerIndex) => _selection.Select(containerIndex);

    /// <summary>Re-target hook (CD-P9-9): the selection emptied because a removal dropped every selected item.
    /// The base does nothing; <see cref="ListBox"/> re-selects the nearest surviving item.</summary>
    private protected virtual void OnSelectionEmptiedByRemoval(int removalIndex)
    {
    }

    // ── model → control sync ──────────────────────────────────────────────────────────────────────────

    private void OnModelSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        PushSelectedProperties();

        if (!_suppressContainerSync)
        {
            foreach (var i in e.RemovedIndexes)
                SetContainerSelected(i, false);
            foreach (var i in e.AddedIndexes)
                SetContainerSelected(i, true);
        }

        SelectionChanged?.Invoke(this, e);
    }

    private void PushSelectedProperties()
    {
        SetAndRaise(SelectedIndexProperty, ref _selectedIndex, _selection.SelectedIndex);
        SetAndRaise(SelectedItemProperty, ref _selectedItem, ItemFromIndex(_selection.SelectedIndex));
    }

    private void SetContainerSelected(int index, bool selected)
    {
        if (ItemContainerGenerator.ContainerFromIndex(index) is ISelectableContainer container)
            container.SetIsSelectedFromOwner(selected);
    }

    // Clamp out-of-range to "no selection" so SelectedIndex and SelectedItem can never disagree (the model itself is
    // count-agnostic; the control knows the item count). A negative value clears via the model's own −1 handling.
    private void SetSelectedIndexExternal(int value)
        => _selection.Select(value >= ItemContainerGenerator.ContainerCount ? -1 : value);

    private void SetSelectedItemExternal(object? value) => _selection.Select(value is null ? -1 : IndexFromItem(value));

    private static void OnSelectionModeChanged(UIObject sender, SelectionMode oldValue, SelectionMode newValue)
    {
        if (sender is SelectingItemsControl selector)
            selector._selection.Mode = newValue; // a narrowing collapse fires the model event → syncs back
    }

    // ── generator structural changes → model fixups ─────────────────────────────────────────────────────

    private void OnContainersChanged(object? sender, ContainersChangedEventArgs e)
    {
        // The generator settles its index list before firing every action (Unrealized now trims first — CD-P9-15),
        // so ContainerCount / ContainerFromIndex / ItemFromIndex are accurate here and the reconcile is synchronous.
        switch (e.Action)
        {
            case ContainersChangedAction.Realized:
                RunStructural(() => _selection.ItemsInserted(e.StartIndex, e.Count));
                ReconcileContainers(e.StartIndex, e.Count);
                PushSelectedProperties();
                break;

            case ContainersChangedAction.Unrealized:
                var hadSelection = _selection.SelectedIndex >= 0;
                RunStructural(() => _selection.ItemsRemoved(e.StartIndex, e.Count));
                PushSelectedProperties();
                if (hadSelection && _selection.SelectedIndex < 0)
                    OnSelectionEmptiedByRemoval(e.StartIndex); // count==0 (whole reset) ⇒ ListBox no-ops, no spurious select
                break;

            case ContainersChangedAction.Moved:
                RunStructural(() => _selection.ItemsMoved(e.OldStartIndex, e.StartIndex, e.Count));
                PushSelectedProperties();
                break;

            default:
            case ContainersChangedAction.Reset:
                RunStructural(_selection.Reset);
                // Virtualizing Reset materializes nothing, so an O(itemCount) reconcile over an all-unrealized store
                // would be pure waste (defeating the sparse-store scale promise — a 1M-item bind ran 1M no-op lookups).
                // The per-realize reconcile (the ContainersRealizedChanged.Realized handler, V3) applies IsSelected as
                // containers materialize.
                if (!ItemContainerGenerator.IsVirtualizing)
                    ReconcileContainers(0, ItemContainerGenerator.ContainerCount);
                PushSelectedProperties();
                break;
        }
    }

    // On (re)realization, reconcile each container's selected-ness: an own-container the user PRE-SELECTED folds INTO
    // the model (the container carried the intent); every other container is driven FROM the model (the source of truth).
    private void ReconcileContainers(int start, int count)
    {
        for (var i = 0; i < count; i++)
            if (ItemContainerGenerator.ContainerFromIndex(start + i) is { } element)
                ReconcileContainer(start + i, element);
    }

    private void ReconcileContainer(int index, UIElement element)
    {
        if (element is not ISelectableContainer container)
            return;

        var isOwn = ReferenceEquals(ItemContainerGenerator.ItemFromContainer(element), element);
        if (isOwn && container.IsSelected && !_selection.IsSelected(index))
            NotifyContainerIsSelectedChanged(element, true); // fold the preset own-container selection into the model
        else
            SetContainerSelected(index, _selection.IsSelected(index)); // drive from the model
    }

    // V3 reconcile-on-realize: when virtualization MATERIALIZES a container (scroll-in), re-apply its selected-ness
    // from the model — a selected-but-unrealized item shows selected the moment it scrolls into view. Fires only in
    // virtualizing mode (the materialization channel is dormant in eager mode), so eager selection is unchanged.
    private void OnContainersRealized(object? sender, ContainersChangedEventArgs e)
    {
        if (e.Action != ContainersChangedAction.Realized || e.RealizedContainers is not { } realized)
            return;

        foreach (var element in realized)
        {
            var index = ItemContainerGenerator.IndexFromContainer(element);
            if (index >= 0)
                ReconcileContainer(index, element);
        }
    }

    // Structural fixups don't change any surviving container's selected-ness (only its index), so suppress the
    // membership delta-sync while forwarding them; the model still fires for a dropped selection.
    private void RunStructural(Action fixup)
    {
        _suppressContainerSync = true;
        try
        {
            fixup();
        }
        finally
        {
            _suppressContainerSync = false;
        }
    }
}
