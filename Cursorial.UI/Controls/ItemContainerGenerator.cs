using System.Collections.Specialized;

namespace Cursorial.UI.Controls;

/// <summary>
/// Maps an <see cref="ItemsControl"/>'s items to their realized containers (design doc §12.6). One per control,
/// control-lifetime. It runs in one of two modes:
/// <list type="bullet">
/// <item><b>Eager (default):</b> every item is realized into the index-aligned <c>_containers</c> list;
/// <see cref="ContainersChanged"/> reports realize/unrealize/move/reset. Byte-identical to the pre-virtualization
/// behavior.</item>
/// <item><b>Virtualizing (opt-in via <see cref="EnableVirtualization"/>):</b> a sparse realized store
/// (<c>index → container</c>) holds only materialized containers; the panel drives realization through
/// <see cref="RealizeRange"/>/<see cref="UnrealizeRange"/>, which report on the separate
/// <see cref="ContainersRealizedChanged"/> channel. <see cref="ContainerCount"/> stays equal to the <i>item</i>
/// count in BOTH modes (so selection/keyboard nav are item-indexed); <see cref="ContainerFromIndex"/> returns null
/// for an unrealized-but-valid index.</item>
/// </list>
/// Containers are <b>logical children of the <see cref="ItemsControl"/></b> (so DataContext/resource/style
/// inheritance flows from the control) and <b>visual children of the panel</b> (the host adopts them via
/// <c>AddVisualChildOnly</c> — punch 43).
/// </summary>
/// <remarks>
/// Each container is stamped with its source item via the internal
/// <see cref="ItemForItemContainerProperty"/> attached property (WPF parity) — the single source of truth for
/// "which item does this container represent". It powers <see cref="ItemFromContainer"/> (O(1), survives
/// reordering) and the unrealize own-container test: for an <b>own-container</b> (the item itself was a
/// <see cref="UIElement"/>) the stamp equals the container, so <c>container == item</c> identifies it — a
/// distinction the container's <see cref="UIElement.DataContext"/> cannot carry, since the generator never sets
/// DataContext on an own-container.
/// </remarks>
public sealed class ItemContainerGenerator
{
    /// <summary>Stamps each container with the item it was realized for (internal — the item↔container back-link).</summary>
    internal static readonly AttachedProperty<object?> ItemForItemContainerProperty =
        UIProperty.RegisterAttached<ItemContainerGenerator, UIElement, object?>("ItemForItemContainer");

    /// <summary>Stamps each container with the items control that currently owns it (internal — the item↔container back-link).</summary>
    internal static readonly AttachedProperty<ItemsControl?> ItemsControlForItemContainerProperty =
        UIProperty.RegisterAttached<ItemContainerGenerator, UIElement, ItemsControl?>("ItemsControlForItemContainer");

    private readonly ItemsControl _owner;
    private readonly List<UIElement> _containers = []; // EAGER: index-aligned with the current source (ordering; item is the stamp)
    private ItemsSourceView? _view;

    // ── Virtualizing-mode state (untouched in eager mode) ──────────────────────────────────────────────
    private bool _virtualizing;
    private VirtualizationMode _mode;
    private int _itemCount;                                              // == _view.Count; the logical item count
    private readonly Dictionary<int, UIElement> _realized = [];          // item-index → realized container
    private readonly Dictionary<UIElement, int> _indexByContainer = [];  // O(1) back-map + realized-set enumerator
    private readonly Stack<UIElement> _recyclePool = [];                 // Recycling mode: spare generated containers
    private readonly List<KeyValuePair<int, UIElement>> _shiftScratch = []; // reused re-key scratch (no per-shift alloc)

    internal ItemContainerGenerator(ItemsControl owner) => _owner = owner;

    /// <summary>Raised (range-based) on <i>item-structure</i> changes — realize(insert)/unrealize(remove)/move/reset.
    /// Fires in both modes; the selection model maps it to keep indices aligned. In virtualizing mode the affected
    /// container may be unrealized (null) — consumers null-check.</summary>
    public event EventHandler<ContainersChangedEventArgs>? ContainersChanged;

    /// <summary>Raised on <i>materialization</i> changes — containers created/destroyed by scrolling
    /// (<see cref="RealizeRange"/>/<see cref="UnrealizeRange"/>). Virtualizing mode only; carries no structural
    /// meaning (the selection model's item indices are untouched). The panel adopts/releases its children from this;
    /// the selector re-applies <c>IsSelected</c> on <see cref="ContainersChangedAction.Realized"/> (V3).</summary>
    public event EventHandler<ContainersChangedEventArgs>? ContainersRealizedChanged;

    /// <summary>Whether the generator is virtualizing (the panel drives realization via
    /// <see cref="RealizeRange"/>/<see cref="UnrealizeRange"/>).</summary>
    public bool IsVirtualizing => _virtualizing;

    /// <summary>The number of containers held in the recycle pool (test hook — pins the recycle push-guards).</summary>
    internal int PooledCount => _recyclePool.Count;
    
    /// <summary>The realized container for <paramref name="index"/>, or null if out of range OR (virtualizing)
    /// not currently materialized.</summary>
    public UIElement? ContainerFromIndex(int index)
        => _virtualizing
            ? _realized.GetValueOrDefault(index)
            : index >= 0 && index < _containers.Count ? _containers[index] : null;

    /// <summary>The item index of <paramref name="container"/>, or −1. O(1) in virtualizing mode (the back-map).</summary>
    public int IndexFromContainer(UIElement container)
        => _virtualizing
            ? _indexByContainer.GetValueOrDefault(container, -1)
            : _containers.IndexOf(container);

    /// <summary>The source item <paramref name="container"/> represents (its <see cref="ItemForItemContainerProperty"/>
    /// stamp), or null if it is not one of this control's containers. For an own-container this is the container itself.</summary>
    public object? ItemFromContainer(UIElement container) => container.GetValue(ItemForItemContainerProperty);

    /// <summary>The source item at <paramref name="index"/> read directly from the items view — independent of
    /// realization, so selection (<c>SelectedItem</c>/<c>SelectedItems</c>) resolves correctly for an unrealized
    /// index in virtualizing mode. In eager mode this equals the realized container's stamp. Null if out of range.</summary>
    public object? ItemFromIndex(int index) => _view is { } v && index >= 0 && index < v.Count ? v[index] : null;

    /// <summary>The number of <i>items</i> — the host iterates <c>[0, ContainerCount)</c> via
    /// <see cref="ContainerFromIndex"/>. Equals the item count in BOTH modes (virtualizing keeps selection/keyboard
    /// nav item-indexed; <see cref="ContainerFromIndex"/> is null for an unrealized index).</summary>
    public int ContainerCount => _virtualizing ? _itemCount : _containers.Count;

    /// <summary>The total number of items in the items source.</summary>
    internal int ItemCount => _virtualizing ? _itemCount : _containers.Count;

    /// <summary>Swaps the items source (control's ItemsSource/Items change): unrealizes the old, realizes the new
    /// (eager) or resets the sparse store realizing nothing (virtualizing), fires Reset.</summary>
    internal void SetSource(ItemsSourceView? view)
    {
        if (_view is not null)
        {
            _view.CollectionChanged -= OnSourceCollectionChanged;
            _view.Dispose(); // unhook the old view's INotifyCollectionChanged subscription (no leak across re-source)
        }

        if (_virtualizing)
            UnrealizeAllVirtual();
        else
            UnrealizeAllCore(); // staged teardown (ClearContainer → Unrealized event → FinishUnrealize) before the swap

        _view = view;
        if (_view is not null)
            _view.CollectionChanged += OnSourceCollectionChanged;

        if (_virtualizing)
        {
            // Virtualizing: allocate the logical count, realize NOTHING (the panel realizes its band on measure).
            _itemCount = _view?.Count ?? 0;
            ContainersChanged?.Invoke(this, new ContainersChangedEventArgs(ContainersChangedAction.Reset, 0, _itemCount));
            return;
        }

        if (_view is not null)
            for (var i = 0; i < _view.Count; i++)
                _containers.Add(RealizeCore(i));

        Restripe();
        ContainersChanged?.Invoke(this, new ContainersChangedEventArgs(ContainersChangedAction.Reset, 0, _containers.Count));
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                InsertRange(e.NewStartingIndex, e.NewItems?.Count ?? 0);
                break;
            case NotifyCollectionChangedAction.Remove:
                RemoveRange(e.OldStartingIndex, e.OldItems?.Count ?? 0);
                break;
            case NotifyCollectionChangedAction.Replace:
                RemoveRange(e.OldStartingIndex, e.OldItems?.Count ?? 0);
                InsertRange(e.NewStartingIndex, e.NewItems?.Count ?? 0);
                break;
            case NotifyCollectionChangedAction.Move:
                MoveRange(e.OldStartingIndex, e.NewStartingIndex, e.NewItems?.Count ?? 1);
                break;
            case NotifyCollectionChangedAction.Reset:
            default:
                ResetFromSource();
                break;
        }
    }

    private void InsertRange(int start, int count)
    {
        if (_virtualizing)
        {
            InsertRangeVirtual(start, count);
            return;
        }

        for (var i = 0; i < count; i++)
            _containers.Insert(start + i, RealizeCore(start + i));

        Restripe(); // everything from `start` shifted parity
        ContainersChanged?.Invoke(this, new ContainersChangedEventArgs(ContainersChangedAction.Realized, start, count));
    }

    private void RemoveRange(int start, int count)
    {
        if (count <= 0)
            return;

        if (_virtualizing)
        {
            RemoveRangeVirtual(start, count);
            return;
        }

        // Capture the removed instances up front so the index list can be trimmed BEFORE the event — selection
        // hosts then reconcile against a settled (post-trim) generator (no stale ContainerCount / ItemFromIndex).
        var removed = new UIElement[count];
        for (var i = 0; i < count; i++)
            removed[i] = _containers[start + i];

        // Step order (§12.6 / CD-P9-3): ClearContainer on each (unhook while bindings are still live) → trim the
        // index list → fire Unrealized so the host visually detaches THESE instances (the subtree detach is the
        // store-retraction trigger) → finish each container's logical detach + stamp clear (after the visual detach).
        for (var i = 0; i < count; i++)
            _owner.ClearContainerForItem(removed[i], ItemFromContainer(removed[i]));

        _containers.RemoveRange(start, count); // trim first: ContainerCount/ContainerFromIndex are now post-removal

        Restripe(); // survivors from `start` shifted parity
        ContainersChanged?.Invoke(this, new ContainersChangedEventArgs(
            ContainersChangedAction.Unrealized, start, count, removedContainers: removed));

        for (var i = 0; i < count; i++)
            FinishUnrealize(removed[i], ItemFromContainer(removed[i])); // stamp still set — read the item before clearing it
    }

    private void MoveRange(int oldIndex, int newIndex, int count)
    {
        if (count <= 0)
            return;

        if (_virtualizing)
        {
            MoveRangeVirtual(oldIndex, newIndex, count);
            return;
        }

        // Lift the block out and re-insert it at the new index — the SAME container instances, reordered (no
        // realize/unrealize). The host (ItemsPresenter) brings its panel children into the same order on the event.
        var block = new UIElement[count];
        for (var i = 0; i < count; i++)
            block[i] = _containers[oldIndex + i];

        _containers.RemoveRange(oldIndex, count);
        _containers.InsertRange(newIndex, block);
        Restripe(); // the moved block + the gap between old/new indices shifted parity
        ContainersChanged?.Invoke(this, new ContainersChangedEventArgs(ContainersChangedAction.Moved, newIndex, count, oldStartIndex: oldIndex));
    }

    private void ResetFromSource()
    {
        if (_virtualizing)
        {
            ResetFromSourceVirtual();
            return;
        }

        UnrealizeAllCore();
        if (_view is not null)
            for (var i = 0; i < _view.Count; i++)
                _containers.Add(RealizeCore(i));

        Restripe();
        ContainersChanged?.Invoke(this, new ContainersChangedEventArgs(ContainersChangedAction.Reset, 0, _containers.Count));
    }

    // Row-striping (design doc §14 P9 — the :alternate pseudo-class, "with the container generator"): stamp the
    // positional :alternate pseudo-class on odd 0-based-indexed containers, so a striping rule (e.g.
    // `ListBoxItem:alternate { Background … }`) can target alternate rows. Re-run after every structural change
    // since insert/remove/move shift indices; PseudoClasses.Set is change-only, so only containers whose parity
    // actually flipped restyle (the styling pass is bounded to the shifted tail, not the whole list).
    private void Restripe()
    {
        for (var i = 0; i < _containers.Count; i++)
            _containers[i].SetPseudoClassFromMapping(":alternate", (i & 1) == 1);
    }

    // Creates + logical-parents + stamps + prepares one container (no visual adoption — the host does that on the event).
    private UIElement RealizeCore(int index)
    {
        var item = _view![index];
        var container = _owner.CreateContainer(item, out var isOwnContainer);
        container.SetValue(ItemForItemContainerProperty, item); // the item↔container back-link (own-container ⇒ stamp == container)
        container.SetValue(ItemsControlForItemContainerProperty, _owner); // the item↔container back-link (own-container ⇒ stamp == container)
        _owner.AddContainerLogical(container); // logical child of the ItemsControl ⇒ inheritance flows from the control
        _owner.PrepareContainerForItem(container, item, isOwnContainer);
        return container;
    }

    // The tail of the 4-step Unrealize (after ClearContainer + the host's visual detach): logical detach + clear the stamp/DataContext.
    private void FinishUnrealize(UIElement container, object? item)
    {
        _owner.RemoveContainerLogical(container);
        // The generator only set DataContext on a GENERATED container (== item); an own-container's DataContext is
        // the user's — leave it (symmetric with PrepareContainerForItem / ClearContainerForItem).
        if (!ReferenceEquals(container, item))
            container.ClearValue(UIElement.DataContextProperty);
        container.ClearValue(ItemForItemContainerProperty);
        container.ClearValue(ItemsControlForItemContainerProperty);
    }

    /// <summary>Releases the source subscription on owner teardown — unhooks the view's
    /// <see cref="INotifyCollectionChanged"/> handler so a live external source no longer pins the control
    /// (the realized containers are torn down by the normal logical-child sweep, as in eager mode), and
    /// <b>tears down + clears the recycle pool</b> (pooled containers are detached — not logical children — so the
    /// owner's sweep would miss them, leaking any binding their template subtree still holds). Idempotent.</summary>
    internal void ReleaseSource()
    {
        if (_view is not null)
        {
            _view.CollectionChanged -= OnSourceCollectionChanged;
            _view.Dispose();
            _view = null;
        }

        // Pooled containers bypass the owner's logical-child TearDown sweep (they're detached), so a Source/
        // ElementName binding inside a recycled template would otherwise leak. Tear each down before dropping.
        foreach (var pooled in _recyclePool)
            pooled.TearDown();
        _recyclePool.Clear();
    }

    private void UnrealizeAllCore()
    {
        // Reuse the staged RemoveRange so the full-reset teardown honors CD-P9-3 ordering: ClearContainer (bindings
        // live) → Unrealized event (the host detaches them visually — the store-retraction trigger) → FinishUnrealize
        // (logical detach + stamp/DataContext clear). The caller fires Reset afterward to adopt the new generation.
        if (_containers.Count > 0)
            RemoveRange(0, _containers.Count);
    }

    // ───────────────────────────── virtualization (design doc §12.6 — the visualization seam) ─────────────────────────────

    /// <summary>
    /// Switches the generator into virtualizing mode (the panel then drives realization via
    /// <see cref="RealizeRange"/>/<see cref="UnrealizeRange"/>). Tears down the eager containers (if any) and
    /// re-publishes the source as an item-indexed, zero-realized generation (a <see cref="ContainersChangedAction.Reset"/>
    /// so the selection model adopts the item count). Idempotent; an already-virtualizing generator only updates the mode.
    /// </summary>
    public void EnableVirtualization(VirtualizationMode mode = VirtualizationMode.Recycling)
    {
        if (_virtualizing)
        {
            _mode = mode;
            if (mode == VirtualizationMode.Standard)
                _recyclePool.Clear(); // Standard never recycles — drop the pool
            return;
        }

        UnrealizeAllCore(); // eager teardown (fires Unrealized so the selection model + host settle)

        _virtualizing = true;
        _mode = mode;
        _itemCount = _view?.Count ?? 0;
        ContainersChanged?.Invoke(this, new ContainersChangedEventArgs(ContainersChangedAction.Reset, 0, _itemCount));
    }

    /// <summary>
    /// Materializes the unrealized items in <c>[start, start+count)</c> (the panel's band). Already-realized slots
    /// are skipped (idempotent). Each newly-realized container is recycled-or-created, stamped, logical-parented,
    /// prepared, and <c>:alternate</c>-striped by its item index; fires one
    /// <see cref="ContainersRealizedChanged"/> <see cref="ContainersChangedAction.Realized"/> carrying the
    /// newly-realized instances in <see cref="ContainersChangedEventArgs.RealizedContainers"/> (none ⇒ no event).
    /// Does NOT touch <see cref="ContainerCount"/> or the selection model.
    /// </summary>
    public void RealizeRange(int start, int count)
    {
        RequireVirtualizing();
        if (count <= 0)
            return;

        var end = Math.Min(start + count, _itemCount);
        var first = Math.Max(0, start);

        List<UIElement>? newlyRealized = null;
        var firstNew = -1;
        for (var i = first; i < end; i++)
        {
            if (_realized.ContainsKey(i))
                continue; // idempotent: already materialized (incl. a kept-alive interior hole)

            var container = RealizeVirtual(i);
            _realized[i] = container;
            _indexByContainer[container] = i;
            if (firstNew < 0)
                firstNew = i;
            (newlyRealized ??= []).Add(container);
        }

        if (newlyRealized is null)
            return; // nothing newly realized → no event

        // The instance list is the authoritative payload: the newly-realized set can be NON-contiguous when an
        // interior index is already realized (a kept-alive container the band scrolled across), so a span alone
        // cannot express it. The host adopts exactly these instances (mirrors RemovedContainers on the unrealize
        // side); StartIndex/Count are informational (Count == the instance count).
        ContainersRealizedChanged?.Invoke(this, new ContainersChangedEventArgs(
            ContainersChangedAction.Realized, firstNew, newlyRealized.Count, realizedContainers: newlyRealized));
    }

    /// <summary>
    /// Unrealizes the realized containers in <c>[start, start+count)</c> (the panel scrolled them out). Honors the
    /// CD-P9-3 4-step (ClearContainer → drop from the store → <see cref="ContainersRealizedChanged"/>
    /// <see cref="ContainersChangedAction.Unrealized"/> → FinishUnrealize), <b>skipping kept-alive containers</b>
    /// (keyboard focus within — caret keep-alive lands at V3). Generated containers return to the recycle pool
    /// (Recycling mode); own-containers and Standard-mode containers are discarded. Already-unrealized slots are
    /// skipped (idempotent). Does NOT touch <see cref="ContainerCount"/> or the selection model.
    /// </summary>
    public void UnrealizeRange(int start, int count)
    {
        RequireVirtualizing();
        if (count <= 0)
            return;

        var end = Math.Min(start + count, _itemCount);
        var first = Math.Max(0, start);

        List<UIElement>? removed = null;
        for (var i = first; i < end; i++)
        {
            if (!_realized.TryGetValue(i, out var container))
                continue; // already unrealized
            if (IsContainerPinned(container))
                continue; // keep-alive: focused (V3 adds caret); stays realized

            _owner.ClearContainerForItem(container, ItemFromContainer(container)); // step 1: unhook while bindings live
            _realized.Remove(i);                                                   // step 2: drop from the store
            _indexByContainer.Remove(container);
            (removed ??= []).Add(container);
        }

        FinishUnrealizeBatch(start, count, removed);
    }

    /// <summary>
    /// Unrealizes every realized container OUTSIDE <c>[windowStart, windowEnd)</c> in one batch (the panel's
    /// reconcile-to-window primitive — robust to a prior window of any shape, a structural index shift, or scattered
    /// keep-alive holes). Honors the keep-alive skip + the CD-P9-3 sequence; fires one
    /// <see cref="ContainersRealizedChanged"/> <see cref="ContainersChangedAction.Unrealized"/> carrying the removed
    /// instances. The panel then <see cref="RealizeRange"/>s the window.
    /// </summary>
    public void UnrealizeOutside(int windowStart, int windowEnd)
    {
        RequireVirtualizing();

        _shiftScratch.Clear();
        foreach (var kv in _realized)
            if (kv.Key < windowStart || kv.Key >= windowEnd)
                _shiftScratch.Add(kv);

        if (_shiftScratch.Count == 0)
            return;

        List<UIElement>? removed = null;
        foreach (var (index, container) in _shiftScratch)
        {
            if (IsContainerPinned(container))
                continue; // keep-alive: focused (V3 adds caret); stays realized outside the window

            _owner.ClearContainerForItem(container, ItemFromContainer(container));
            _realized.Remove(index);
            _indexByContainer.Remove(container);
            (removed ??= []).Add(container);
        }

        FinishUnrealizeBatch(windowStart, 0, removed); // start informational; the removed instance list is authoritative
    }

    // Steps 3+4 of the unrealize sequence shared by scroll-unrealize and structural remove: fire the realized
    // channel so the host detaches the instances, then logical-detach + recycle/discard each.
    private void FinishUnrealizeBatch(int start, int count, List<UIElement>? removed)
    {
        if (removed is null || removed.Count == 0)
            return; // nothing actually unrealized → no event

        // Count == the actually-removed instance count (a keep-alive skip or a partially-realized range makes the
        // requested span larger): RemovedContainers is authoritative, and Count mirrors it so a host that reads
        // Count never over-detaches (consistent with UnrealizeAllVirtual).
        ContainersRealizedChanged?.Invoke(this, new ContainersChangedEventArgs(
            ContainersChangedAction.Unrealized, start, removed.Count, removedContainers: removed));

        foreach (var container in removed)
        {
            var item = ItemFromContainer(container); // stamp still set until FinishUnrealize clears it
            var isOwn = ReferenceEquals(container, item);
            FinishUnrealize(container, item);
            if (!isOwn && _mode == VirtualizationMode.Recycling)
                _recyclePool.Push(container); // a generated container is reusable; own-containers never pool
        }
    }

    // Recycle-or-create + stamp + logical-parent + prepare + item-indexed :alternate stripe.
    private UIElement RealizeVirtual(int index)
    {
        var item = _view![index];
        var isOwn = _owner.IsOwnContainer(item);

        UIElement container;
        if (!isOwn && _mode == VirtualizationMode.Recycling && _recyclePool.TryPop(out var pooled))
        {
            container = pooled;
            // VV0.10 recycle reset: clear OWNER-specific transient state that PrepareContainerForItem won't
            // overwrite and would otherwise bleed to the next item (e.g. IsSelected, TreeViewItem.IsExpanded).
            // Interaction state (:pointerover/:pressed/:focus) is cleared by the host's visual detach during the
            // Unrealized event (before pooling), not here — it can't be reset via PseudoClasses.Set.
            _owner.ResetRecycledContainer(container);
        }
        else
        {
            container = isOwn ? (UIElement)item! : _owner.CreateGeneratedContainer();
        }

        container.SetValue(ItemForItemContainerProperty, item);
        container.SetValue(ItemsControlForItemContainerProperty, _owner);
        _owner.AddContainerLogical(container);
        _owner.PrepareContainerForItem(container, item, isOwn);
        container.SetPseudoClassFromMapping(":alternate", (index & 1) == 1); // item-indexed parity (VV0.7)
        return container;
    }

    // Keep-alive predicate (VV0.8 / VV3): never unrealize a container the user is interacting with — keyboard focus
    // within (V0), OR a live terminal-caret publication owned within its subtree (V3 — a TextBox editing inside a
    // virtualized item must survive a scroll-out/in without losing its caret + edit state).
    private static bool IsContainerPinned(UIElement container)
        => container.IsKeyboardFocusWithin
           || (UIApplication.Current?.CaretService.HasPublicationWithin(container) ?? false);

    private void RequireVirtualizing()
    {
        if (!_virtualizing)
            throw new InvalidOperationException(
                "ItemContainerGenerator: RealizeRange/UnrealizeRange require virtualizing mode (call EnableVirtualization first).");
    }

    // ── virtualizing item-structure operations (the sparse-store variants of Insert/Remove/Move/Reset) ──

    private void InsertRangeVirtual(int start, int count)
    {
        if (count <= 0)
            return;

        ShiftRealized(start, count); // realized containers at/after `start` shift up (re-key + back-map)
        _itemCount += count;
        RestripeVirtual();           // parity of shifted containers may have flipped
        ContainersChanged?.Invoke(this, new ContainersChangedEventArgs(ContainersChangedAction.Realized, start, count));
        // No materialization — the inserted items stay unrealized until the panel scrolls them into the band.
    }

    private void RemoveRangeVirtual(int start, int count)
    {
        // Tear down realized containers WITHIN the removed range (their items are gone): step 1 + drop from store.
        List<UIElement>? removed = null;
        var end = start + count;
        for (var i = start; i < end; i++)
        {
            if (!_realized.TryGetValue(i, out var container))
                continue;

            _owner.ClearContainerForItem(container, ItemFromContainer(container));
            _realized.Remove(i);
            _indexByContainer.Remove(container);
            (removed ??= []).Add(container);
        }

        ShiftRealized(end, -count); // survivors above the range shift down (re-key + back-map)
        _itemCount = Math.Max(0, _itemCount - count);
        RestripeVirtual();

        // Item-structure channel (selection model) settles against the post-removal generator, then the realized
        // channel detaches the destroyed containers (steps 3+4) so a host releases them.
        ContainersChanged?.Invoke(this, new ContainersChangedEventArgs(ContainersChangedAction.Unrealized, start, count));
        FinishUnrealizeBatch(start, count, removed);
    }

    private void MoveRangeVirtual(int oldIndex, int newIndex, int count)
    {
        if (count <= 0)
            return;

        MoveRealized(oldIndex, newIndex, count); // re-key every realized container through the move remap
        RestripeVirtual();
        ContainersChanged?.Invoke(this, new ContainersChangedEventArgs(ContainersChangedAction.Moved, newIndex, count, oldStartIndex: oldIndex));
    }

    private void ResetFromSourceVirtual()
    {
        UnrealizeAllVirtual();
        _itemCount = _view?.Count ?? 0;
        ContainersChanged?.Invoke(this, new ContainersChangedEventArgs(ContainersChangedAction.Reset, 0, _itemCount));
    }

    // Drop every realized container (logical detach), clear the back-map + pool, zero the count. Fires the realized
    // channel so a host detaches them. Used by SetSource / Reset / teardown in virtualizing mode.
    private void UnrealizeAllVirtual()
    {
        if (_realized.Count > 0)
        {
            _shiftScratch.Clear();
            foreach (var kv in _realized)
                _shiftScratch.Add(kv);

            var removed = new List<UIElement>(_shiftScratch.Count);
            foreach (var kv in _shiftScratch)
            {
                _owner.ClearContainerForItem(kv.Value, ItemFromContainer(kv.Value));
                removed.Add(kv.Value);
            }

            _realized.Clear();
            _indexByContainer.Clear();

            ContainersRealizedChanged?.Invoke(this, new ContainersChangedEventArgs(
                ContainersChangedAction.Unrealized, 0, removed.Count, removedContainers: removed));

            foreach (var container in removed)
                FinishUnrealize(container, ItemFromContainer(container));
        }

        _recyclePool.Clear(); // a full reset starts a fresh generation
        _itemCount = 0;
    }

    // Shift realized container indices in [from, ∞) by delta (re-key _realized + _indexByContainer). Two passes
    // (snapshot-affected → remove → re-add shifted) avoid key collisions during the shift.
    private void ShiftRealized(int from, int delta)
    {
        if (delta == 0)
            return;

        _shiftScratch.Clear();
        foreach (var kv in _realized)
            if (kv.Key >= from)
                _shiftScratch.Add(kv);

        foreach (var kv in _shiftScratch)
            _realized.Remove(kv.Key);

        foreach (var kv in _shiftScratch)
        {
            var moved = kv.Key + delta;
            _realized[moved] = kv.Value;
            _indexByContainer[kv.Value] = moved;
        }
    }

    // Re-key every realized container through the block-move index remap (matches the eager RemoveRange+InsertRange).
    private void MoveRealized(int oldIndex, int newIndex, int count)
    {
        _shiftScratch.Clear();
        foreach (var kv in _realized)
            _shiftScratch.Add(kv);

        _realized.Clear();
        foreach (var kv in _shiftScratch)
        {
            var moved = RemapMoveIndex(kv.Key, oldIndex, newIndex, count);
            _realized[moved] = kv.Value;
            _indexByContainer[kv.Value] = moved;
        }
    }

    // The final index of an item originally at `j` after moving `count` items from `oldIndex` to `newIndex`
    // (newIndex is the post-removal insertion index — the eager RemoveRange(old,count)+InsertRange(new,block) semantics).
    private static int RemapMoveIndex(int j, int oldIndex, int newIndex, int count)
    {
        if (j >= oldIndex && j < oldIndex + count)
            return newIndex + (j - oldIndex); // a moved item

        var postRemoval = j < oldIndex ? j : j - count;
        return postRemoval < newIndex ? postRemoval : postRemoval + count;
    }

    private void RestripeVirtual()
    {
        foreach (var (index, container) in _realized)
            container.SetPseudoClassFromMapping(":alternate", (index & 1) == 1);
    }
}
