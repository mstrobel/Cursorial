using System.Diagnostics;

using Cursorial.Input;
using Cursorial.Rendering;
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
    protected internal enum SelectionKind
    {
        None,
        Single,
        SingleOrClear,
        Toggle,
        RangeFromAnchor
    }

    /// <summary>
    /// Whether the item is selected. Two-way bindable; <c>:selected</c> mirrors it. Setting it from outside
    /// the owner folds into the owner's selection (CD-P9-9: the model stays the source of truth).
    /// </summary>
    public static readonly AttachedProperty<bool> IsSelectedProperty =
        UIProperty.RegisterAttached<SelectingItemsControl, ContentControl, bool>("IsSelected");

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

    public static bool GetIsSelected(Control element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(IsSelectedProperty);
    }

    public static void SetIsSelected(Control element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsSelectedProperty, value);
    }

    private readonly SelectionModel _selection = new();
    private int _selectedIndex = -1;
    private int _pendingFocusIndex = -1;
    private object? _pendingFocusItem;
    private FocusNavigationMethod? _pendingFocusMethod;
    private bool _pendingItemScheduled;
    private object? _selectedItem;
    private bool _suppressContainerSync; // structural fixups: survivors are already correct, so skip the delta-sync
    
    /// <summary>Initializes the selection wiring.</summary>
    protected SelectingItemsControl()
    {
        _selection.SelectionChanged += OnModelSelectionChanged;

        ItemContainerGenerator.ContainersChanged += OnContainersChanged;
        ItemContainerGenerator.ContainersRealizedChanged += OnContainersRealized; // V3 reconcile-on-realize (virtualizing only)
    }

    static SelectingItemsControl()
    {
        AddGlobalEffects(PropertyEffects.BindsTwoWayByDefault, SelectedItemProperty);
        AddGlobalEffects(PropertyEffects.BindsTwoWayByDefault, SelectedIndexProperty);
        
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
    protected SelectionModel Selection => _selection;

    /// <summary>The item at <paramref name="index"/> read from the items view, or null. Independent of realization
    /// (so <see cref="SelectedItem"/>/<see cref="SelectedItems"/> resolve for a selected-but-unrealized index in
    /// virtualizing mode); in eager mode this equals the realized container's stamp.</summary>
    protected object? ItemFromIndex(int index) => ItemContainerGenerator.ItemFromIndex(index);

    protected int IndexFromItem(object? item)
    {
        for (var i = 0; i < ItemContainerGenerator.ContainerCount; i++)
        {
            if (Equals(ItemFromIndex(i), item))
                return i;
        }

        return -1;
    }

    // ── selection ops driven by the input layer / containers ──────────────────────────────────────────

    protected void MoveCurrent(int target, KeyModifiers modifiers)
    {
        var selection = SelectionMode switch
                        {
                            _ when modifiers.HasFlag(KeyModifiers.Control) => SelectionKind.None,
                            _ when modifiers.HasFlag(KeyModifiers.Shift)   => SelectionKind.RangeFromAnchor,
                            _                                              => SelectionKind.Single
                        };
        
        // ⇒ :focus-visible (realized now, or scrolled-then-focused when virtualized)
        BringItemIntoView(target, null, selection, FocusNavigationMethod.Directional);
    }

    /// <summary>Applies a pointer/keyboard selection gesture at <paramref name="index"/> per the modifiers
    /// (Ctrl = toggle, Shift = range-from-anchor, otherwise replace) — the input mapping onto the model primitives.</summary>
    protected void SelectByGesture(int index, KeyModifiers modifiers)
    {
        if (!IsIndexSelectable(index))
            return; // a non-selectable container (a command tab) is focus-only — a gesture never selects it

        var ctrl = (modifiers & KeyModifiers.Control) != 0;
        var shift = (modifiers & KeyModifiers.Shift) != 0;

        if (SelectionMode == SelectionMode.Multiple && shift)
            BringItemIntoView(index, null, SelectionKind.RangeFromAnchor, FocusNavigationMethod.Pointer);
        else if (ctrl)
            BringItemIntoView(index, null, SelectionKind.Toggle, FocusNavigationMethod.Pointer);
        else
            BringItemIntoView(index, null, SelectionKind.Single, FocusNavigationMethod.Pointer);
    }

    /// <summary>Raises <see cref="ItemActivatedEvent"/> for the item at <paramref name="index"/>.</summary>
    protected bool RaiseItemActivated(int index)
    {
        if (index < 0)
            return false;

        var args = new ItemActivatedEventArgs(ItemActivatedEvent, this, ItemFromIndex(index), index);

        RaiseEvent(args);
        
        return args.Handled;
    }

    /// <summary>A container reported a pointer selection gesture (its own <c>OnMouseDown</c>): select per the
    /// modifiers, and activate on a double-click.</summary>
    protected internal void HandleContainerPointerSelect(UIElement container, KeyModifiers modifiers, int clickCount)
    {
        var index = ItemContainerGenerator.IndexFromContainer(container);
        if (index < 0)
            return;

        SelectByGesture(index, modifiers);

        if (clickCount % 2 == 0)
            RaiseItemActivated(index);
    }

    /// <summary>A container's <c>IsSelected</c> was set from outside the owner (binding / direct assignment) — fold
    /// it into the model.</summary>
    protected internal void NotifyContainerIsSelectedChanged(UIElement container, bool isSelected)
    {
        var index = ItemContainerGenerator.IndexFromContainer(container);
        if (index < 0)
            return;

        if (isSelected && !IsIndexSelectable(index))
        {
            // A non-selectable container (a command tab) reported IsSelected=true (a binding / direct set) — reject it:
            // leave the model unchanged and drive the container's IsSelected back to false so the two stay consistent.
            if (container is ISelectableContainer selectable)
                selectable.SetIsSelectedFromOwner(false);
            return;
        }

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
    protected override bool TextSearchNavigates => true;

    /// <summary>Type-ahead cycles from the current selection.</summary>
    protected override int CurrentTextSearchIndex => _selectedIndex;

    /// <summary>A type-ahead match selects the item (selectors); subclasses refine with focus.</summary>
    protected override void OnTextSearchMatch(int containerIndex)
    {
        if (IsIndexSelectable(containerIndex))
            _selection.Select(containerIndex);
    }

    /// <summary>Whether the container at <paramref name="index"/> may be SELECTED (default true). A subclass whose
    /// containers can be focusable-but-not-selectable (a TabControl's command tab) overrides this so the "never
    /// selected" rule holds on <b>every</b> model entry — auto-select, programmatic <see cref="SelectedIndex"/>/
    /// <see cref="SelectedItem"/>, a container <c>IsSelected=true</c> fold, gesture, and type-ahead — not just the
    /// input gates.</summary>
    protected virtual bool IsIndexSelectable(int index)
        => ItemContainerGenerator.ContainerFromIndex(index) switch
           {
               {} e => IsContainerSelectable(e),
               _    => true
           };

    protected virtual bool IsContainerSelectable(UIElement container) => true;

    /// <summary>Re-target hook (CD-P9-9): the selection emptied because a removal dropped every selected item.
    /// The base does nothing; <see cref="ListBox"/> re-selects the nearest surviving item.</summary>
    protected virtual void OnSelectionEmptiedByRemoval(int removalIndex)
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
        PrimeFocusMemoryFromSelection();
    }

    // Seed the items-host focus scope's memory from the (lead) selection so a later Tab-in lands on the selected
    // item rather than the first (the ND33 entry ladder reads scope memory). Skipped while the list holds keyboard
    // focus — that memory is live navigation we must not clobber — and when the selected container is not realized
    // (virtualized off-screen; nothing to point at yet). Re-runs as containers realize (PushSelectedProperties is
    // called from the generator's Realized path), so a selection set before layout still primes once it materializes.
    private void PrimeFocusMemoryFromSelection()
    {
        if (IsKeyboardFocusWithin || _selection.SelectedIndex < 0)
            return;

        // Only seed memory when the items host IS the container's focus scope (ListBox/ItemsControl/TreeView, whose
        // host is marked IsFocusScope — P1). For a control whose items host is NOT a focus scope (ComboBox/TabControl)
        // GetFocusScope climbs PAST it to an enclosing window/surface root; priming there would corrupt that root's
        // activation memory and steal focus on re-activation. The scope-identity check fences that off (no-op for them).
        if (ItemContainerGenerator.ContainerFromIndex(_selection.SelectedIndex) is {} container &&
            FocusManager.GetFocusScope(container) is {} scope&&
            ReferenceEquals(scope, ItemsPanelFromItemsControl(this)))
        {
            FocusManager.SetFocusedElement(scope, container);
        }
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_pendingFocusIndex >= 0)
            TryProcessPendingFocus();
        else if (_selectedIndex is >= 0 and var i && i < ItemContainerGenerator.ItemCount)
            BringItemIntoView(i, null, SelectionKind.Single);
    }

    internal void SetContainerSelected(int index, bool selected)
    {
        if (ItemContainerGenerator.ContainerFromIndex(index) is not ISelectableContainer container)
            return;

        container.SetIsSelectedFromOwner(selected);
    }

    // Clamp out-of-range to "no selection" so SelectedIndex and SelectedItem can never disagree (the model itself is
    // count-agnostic; the control knows the item count). A negative value clears via the model's own −1 handling.
    private void SetSelectedIndexExternal(int index)
    {
        BringItemIntoView(index, null, SelectionKind.SingleOrClear, FocusNavigationMethod.Programmatic);
    }

    private void SetSelectedItemExternal(object? value)
    {
        BringItemIntoView(-1, value, SelectionKind.SingleOrClear, FocusNavigationMethod.Programmatic);
    }

    private static void OnSelectionModeChanged(UIObject sender, SelectionMode oldValue, SelectionMode newValue)
    {
        if (sender is SelectingItemsControl selector)
            selector._selection.Mode = newValue; // a narrowing collapse fires the model event → syncs back
    }

    protected void RepairFocus(int focusedIndex, object? focusedItem = null)
    {
        BringItemIntoView(focusedIndex, focusedItem, SelectionKind.None, FocusNavigationMethod.Restore);
    }
    
    // Bring the target container into view. Optionally focus immediately (its GotFocus brings it into view
    // through the ScrollViewer). Virtualized + off-band (the container is not materialized) ⇒ scroll its
    // ESTIMATED position into the realization window, then focus it the moment it materializes
    // (OnContainersRealizedForPendingFocus) — keyboard nav reaches an item that does not exist yet.
    protected void BringItemIntoView(int index, 
                                     object? item,
                                     SelectionKind selectionMode = SelectionKind.None,
                                     FocusNavigationMethod? focusMethod = null)
    {
        var wantFocus = focusMethod is not null;
        var wantSelect = selectionMode is not SelectionKind.None;

        if (wantFocus is false)
            ClearPendingFocus();

        if (wantSelect && !wantFocus && _selection.Mode is SelectionMode.Single)
        {
            // If we have single selection, and we already have focus in the items control,
            // there's no reason not to focus the container too.
            focusMethod = FocusNavigationMethod.Programmatic;
            wantFocus = true;
        }

        var isValidIndex = IsValidIndex(index);

        if (isValidIndex is false && item is not null)
        {
            index = IndexFromItem(item);
            isValidIndex = IsValidIndex(index);
        }
        else if (item is not null && isValidIndex && Equals(item, ItemFromIndex(index)))
        {
            item = null;
        }

        if (isValidIndex is false && selectionMode != SelectionKind.SingleOrClear)
            return;

        if (wantSelect)
            ApplySelection(index, selectionMode, bringIntoView: true);
        else if (wantFocus && ItemContainerGenerator.IsVirtualizing)
            TryBringContainerIntoView(index);

        if (isValidIndex is false || wantFocus is false || ApplyFocus(index, focusMethod!.Value))
            return;

        _pendingFocusItem = item;
        _pendingFocusIndex = index;
        _pendingFocusMethod = focusMethod;
    }

    private void ApplySelection(int index, SelectionKind kind, bool bringIntoView = false)
    {
        var selectable = IsValidIndex(index) && IsIndexSelectable(index);

        if (kind is SelectionKind.SingleOrClear && selectable is false)
        {
            _selection.Select(-1);
            return;
        }

        if (kind is SelectionKind.None || selectable is false) return;

        if (kind is SelectionKind.Toggle)
            _selection.Toggle(index);
        else if (kind is SelectionKind.RangeFromAnchor)
            _selection.SelectRangeFromAnchor(index);
        else
            _selection.Select(index);

        SetContainerSelected(index, _selection.IsSelected(index));

        if (bringIntoView is false) return;

        TryBringContainerIntoView(index);
    }

    private void TryBringContainerIntoView(int index)
    {
        if (ItemContainerGenerator.ContainerFromIndex(index) is {} container)
            TryEnsureContainerInView(container);
        else if (FindItemsScrollViewer() is {} scroll && ItemsHost is ILogicalScrollHost logical)
            scroll.EnsureVisible(logical.BringItemIntoView(index)); // scroll the estimate in ⇒ the panel realizes.
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
        {
            if (ItemContainerGenerator.ContainerFromIndex(start + i) is {} element)
                ReconcileContainer(start + i, element);
        }
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

    protected bool HasPendingFocusRepair => _pendingFocusMethod is FocusNavigationMethod.Restore &&
                                            (_pendingFocusIndex >= 0 || _pendingFocusItem is not null);

    protected bool HasPendingFocus => _pendingFocusMethod is not null &&
                                      (_pendingFocusIndex >= 0 || _pendingFocusItem is not null);

    protected void ClearPendingFocus()
    {
        _pendingFocusItem = null;
        _pendingFocusIndex = -1;
        _pendingFocusMethod = null;
    }

    protected void ClearPendingFocusRepair()
    {
        if (HasPendingFocusRepair)
            ClearPendingFocus();
    }

    protected bool ScheduleProcessPendingItem()
    {
        if (_pendingItemScheduled || _pendingFocusIndex < 0 && _pendingFocusItem is null)
            return false;

        _pendingItemScheduled = true;
        UIApplication.Current?.Dispatcher.Post(TryProcessPendingFocus);
        return true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var result = base.MeasureOverride(availableSize);
        ScheduleProcessPendingItem();
        return result;
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        
        if (HasPendingFocusRepair && (IsKeyboardFocusWithin is false || e.Method.IsUserInitiated()))
            ClearPendingFocusRepair();
    }

    // V3 reconcile-on-realize: when virtualization MATERIALIZES a container (scroll-in), re-apply its selected-ness
    // from the model — a selected-but-unrealized item shows selected the moment it scrolls into view. Fires only in
    // virtualizing mode (the materialization channel is dormant in eager mode), so eager selection is unchanged.
    //
    // Also completes a parked keyboard-nav focus: when the scrolled-to container materializes, focus it. Deferred
    // via the dispatcher because the realize channel fires DURING the panel's measure pass — focusing synchronously
    // there would re-enter layout (focus raises routed events + restyles). Re-resolves the container by index at
    // post time so a recycle between realization and the post can't focus a stale container.
    private void OnContainersRealized(object? sender, ContainersChangedEventArgs e)
    {
        if (e.Action != ContainersChangedAction.Realized || e.RealizedContainers is not {} realized)
            return;

        TryProcessPendingFocus();

        foreach (var element in realized)
        {
            var index = ItemContainerGenerator.IndexFromContainer(element);
            if (index >= 0)
                ReconcileContainer(index, element);
        }
    }

    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        ClearPendingFocus();
        base.OnDetachedFromTree(in e);
    }

    protected void TryProcessPendingFocus()
    {
        _pendingItemScheduled = false;

        var pendingFocus = _pendingFocusMethod;
        if (pendingFocus is null)
        {
            ClearPendingFocus();
            return;
        }

        if (IsAttachedToTree is false)
        {
            ClearPendingFocusRepair(); // clear repair only
            return;
        }

        var pendingIndex = _pendingFocusIndex;
        var pendingIndexValid = IsValidIndex(pendingIndex);

        var pendingItem = pendingIndexValid ? null : _pendingFocusItem;
        if (pendingItem is null)
        {
            _pendingFocusItem = null;
        }
        else if (IndexFromItem(pendingItem) is var index and >= 0 &&
                 index < ItemContainerGenerator.ItemCount)
        {
            _pendingFocusItem = null;
            _pendingFocusIndex = pendingIndex = index;
        }

        if (ItemContainerGenerator.ContainerFromIndex(pendingIndex) is null)
            return;

        if (ApplyFocus(pendingIndex, pendingFocus.Value)) // immediate path
            return;

        if (UIApplication.Current?.Dispatcher is {} dispatcher) // delayed path
            dispatcher.Post(() => ApplyFocus(pendingIndex, pendingFocus.Value));
    }

    private bool IsValidIndex(int index)
        => index >= 0 && index < ItemContainerGenerator.ItemCount;

    private void TryEnsureContainerInView(UIElement container)
    {
        if (FindItemsScrollViewer() is { Presenter: {} scp } sv && scp.TryGetContentRect(container, out var rect))
            sv.EnsureVisible(rect);
    }

    // Returning TRUE means either a definitive focus attempt was made, or it will be attempted again.

    private bool ApplyFocus(int index, FocusNavigationMethod method)
    {
        if (ItemContainerGenerator.ContainerFromIndex(index) is not { IsAttachedToTree: true } container)
        {
            if (HasPendingFocus)
            {
                // Restore will be attempted on the next measure; do nothing.
                if (IsMeasureValid is false) return true;

                // If layout work is still queued, reschedule the attempt. Otherwise, give up.
                if (method is FocusNavigationMethod.Restore && GetLayoutManager()?.HasQueuedWork is true)
                {
                    ScheduleProcessPendingItem();
                    return true;
                }

                ClearPendingFocus();
            }

            return false;
        }

        ClearPendingFocus();

        var containerScope = FocusManager.GetFocusScope(container);

        // If we're only RESTORING focus (not DEMANDING focus), take focus only if no other element in a
        // focus-retaining scope has it, -OR- focus is already within the items control. If we can't take
        // focus, the best we can do is set the logical focus for the items panel and scroll the container
        // into view (this would have happened automatically if we were taking focus).
        if (FocusManager.Current?.FocusedElement is var focused &&
            containerScope.IsKeyboardFocusWithin is false &&
            focused is not null)
        {
            if (// Programmatic never steals physical focus.
                method is FocusNavigationMethod.Programmatic ||

                // If we're restoring focus, and the focus scope currently holding focus is
                // already set to auto-return it to a scope _different_ from our container's,
                // don't interfere.
                (method is FocusNavigationMethod.Restore &&
                 (FocusManager.GetFocusScope(focused) is not {} fs ||
                  FocusManager.GetRetainsFocus(fs) is true ||
                  (fs.GetValue(FocusManager.RetainedReturnAutoProperty) &&
                   fs.GetValue(FocusManager.RetainedReturnScopeProperty) is {} returnScope &&
                   ReferenceEquals(returnScope, containerScope) is false))))
            {
                // For both of the above cases, bring the container into view, set logical focus,
                // and then return.

                TryEnsureContainerInView(container);

                if (ReferenceEquals(containerScope, container) is false &&
                    IsAncestorOf(containerScope))
                {
                    containerScope.SetValue(FocusManager.FocusedElementProperty, container);
                }
                
                return true;
            }
        }

        // We're taking focus, so the container will be brought into view automatically, as will setting items
        // panel's logically focused element.
        return container.Focus(method);
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
