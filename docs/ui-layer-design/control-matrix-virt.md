# Control matrix — ItemsControl container virtualization (#80, post-P9)

The oracle for the virtualization workstream (design doc §12.6 "the visualization seam" realized). Built on the
10-agent scroll/virtualization design synthesis. **Cell-native:** Cursorial stays a pure cell-scroller — there is
exactly ONE offset coordinate (the `ScrollContentPresenter`'s int-cell `ScrollOffsetRow`/`ScrollOffsetColumn`).
"Whole-item (logical) scroll" is NOT a second coordinate; it is (a) a *step-size policy* (a line/page step lands on
an item top) + (b) an *extent estimate* the panel supplies instead of measuring all children. The banded
composite-slide engine (`RenderTree`/`ScrollContentPresenter` band geometry) is reused **verbatim** and is
mode-agnostic.

## Locked decisions (maintainer sign-off)

- **Recycling in v1** — `VirtualizationMode.Recycling` is the default: unrealized generated containers return to a
  per-generator pool and are re-prepared for new items (the GC win); `Standard` mode discards. Own-containers (the
  item *is* a `UIElement`) are never pooled.
- **Opt-in through V5** — `ListBox`'s default `ItemsPanel` stays the eager `StackPanel`; virtualization is opt-in
  (set a `VirtualizingStackPanel` `ItemsPanel`). The default-panel flip (WPF parity) is a separate, reversible
  post-soak change.
- **`VirtualizingPanel.IsVirtualizing` attached property** — the WPF-style opt-in surface (`IsVirtualizing` /
  `VirtualizationMode` / `ScrollUnit` set on the `ItemsControl`, read by the panel), so a list can flip without
  retemplating once its panel is a `VirtualizingStackPanel`.
- **Lift the 32K `MaxScrollExtent` cap in virtualizing mode only** — the cap's sole effect is to bound the
  *reachable offset* (`offset ≤ extent − viewport`); the rented scene is bounded by the **band** (`viewport + 2K`),
  never the extent. So a huge logical extent costs nothing in virtualizing mode (the panel realizes only the band),
  and capping it would make a >32K-row list's tail physically unreachable. The cap stays for non-virtualized
  content (whose panel genuinely allocates that many rows — the existing L215 diagnostic guard). Gated on
  `!IsScrollClient`.

## Phase map

- **§V0** — generator virtualization API + sparse realized store + the `ContainersRealizedChanged` channel +
  recycling pool + focus keep-alive. Generator-only; driven directly from tests. **(this workstream)**
- **§V1** — `IScrollContentHost`/`ILogicalScrollHost` + `ScrollContentPresenter` delegation; OFF-path
  byte-identical drift gate.
- **§V2** — `VirtualizingStackPanel` (uniform-height item mode); the band=realization-window coverage invariant;
  in-band scroll = zero re-raster + zero realize churn (the invariant-3 ON-path gate).
- **§V3** — keyboard/selection/focus/caret across the realization boundary; reconcile-on-realize; caret keep-alive.
- **§V4** — variable-height sticky per-item cache + monotone extent + convergence under the `LayoutManager`
  fixpoint.
- **§V5** — fling-storm perf gate + demo canary + adversarial closeout.

---

## §V0 — Generator virtualization API (`ItemContainerGenerator`)

### Store design

The generator runs in one of two modes:

- **Eager (default, unchanged):** `_containers : List<UIElement>` index-aligned with the source; every item is
  realized. `ContainerCount == _containers.Count`. `IndexFromContainer` is the existing `List.IndexOf` (O(n)). The
  whole eager path — `SetSource`/`InsertRange`/`RemoveRange`/`MoveRange`/`ResetFromSource`/`Restripe`/the
  `ContainersChanged` Realized/Unrealized/Moved/Reset semantics — is **byte-identical** (VV0.13).
- **Virtualizing (opt-in via `EnableVirtualization(mode)`):** a **sparse realized store** — only materialized
  containers are held:
  - `_itemCount : int` — the logical item count (== source count). `ContainerCount == _itemCount`.
  - `_realized : Dictionary<int, UIElement>` — item-index → realized container (only realized keys present).
  - `_indexByContainer : Dictionary<UIElement, int>` — the O(1) back-map (also the realized-container enumerator).
  - `_recyclePool : Stack<UIElement>` — spare generated containers (Recycling mode only).

  This is **strictly less memory than a dense `UIElement?[]`** (the synthesis's first cut): nothing is stored for
  unrealized indices, so a 1M-item list with a 60-row band holds ~60 + slack containers, not a 1M-pointer array.
  `ContainerFromIndex(i)` is a dict lookup (null when unrealized but in `[0, itemCount)`); item-structural index
  shifts re-key only the realized entries (bounded by the band, not the item count).

### Two notification channels (the keystone)

- **`ContainersChanged` (existing) — the ITEM-STRUCTURE channel.** Fires on source structural changes
  (insert/remove/move/reset of *items*) in BOTH modes, carrying item-index ranges. `SelectingItemsControl` maps
  it onto `SelectionModel.ItemsInserted/ItemsRemoved/ItemsMoved/Reset` — so selection indices stay aligned (it is
  null-tolerant: `ReconcileContainers` null-checks each index). **The eager `ItemsPresenter` is index-aligned and is
  NOT a virtualizing host** — its sparse store would make `_panel.Children` adoption by item-index throw — so it
  *no-ops* its structural handlers (`OnContainersChanged`/`SyncAll`) when `_generator.IsVirtualizing`; the V2
  `VirtualizingStackPanel` owns adoption via the materialization channel below. (A virtualizing list therefore needs
  a `VirtualizingStackPanel` `ItemsPanel`; with the default eager panel it renders nothing — by design, opt-in.)
- **`ContainersRealizedChanged` (NEW) — the MATERIALIZATION channel.** Fires only in virtualizing mode, on
  `RealizeRange`/`UnrealizeRange` (containers created/destroyed by scrolling). Actions: `Realized`/`Unrealized`
  only (it reuses `ContainersChangedEventArgs`/`ContainersChangedAction`). It carries NO structural meaning — the
  `SelectionModel`'s item indices are untouched by which containers happen to be materialized. **The authoritative
  payload is an instance list, not the `StartIndex`/`Count` span:** `Realized` carries
  `RealizedContainers` (the newly-realized instances) and `Unrealized` carries `RemovedContainers`; the host adopts/
  releases exactly those (the span can bridge a kept-alive interior container, so it cannot be iterated safely).
  `Count` mirrors the instance-list length. The `VirtualizingStackPanel` (V2) consumes it to adopt/release its own
  `Children`; `SelectingItemsControl` (V3) consumes only its `Realized` action to re-apply `IsSelected` to a
  freshly-materialized container (the reconcile-on-realize). **This separation is what keeps selection/keyboard/
  `:alternate` correct without touching `SelectingItemsControl`'s structural handler at all** (the synthesis's
  Correction 3, verified against `SelectingItemsControl.OnContainersChanged` mapping `Realized→ItemsInserted` /
  `Unrealized→ItemsRemoved`).

### Rows

| Row | Scenario | Expected |
| --- | --- | --- |
| **VV0.1** | `EnableVirtualization(mode)` then `SetSource(view)` of N items. | `ContainerCount == N`; **zero realized** — `ContainerFromIndex(i) == null` for every `i ∈ [0, N)`. One `ContainersChanged(Reset, 0, N)` fires (the `SelectionModel` sees N items); **no** realize loop (resolves "Reset re-realizes all N"). |
| **VV0.2** | `RealizeRange(start, count)` over unrealized slots. | `ContainerFromIndex(i)` non-null for `i ∈ [start, start+count)`, null elsewhere; `ContainerCount` unchanged (`== N`). Each realized container is logical-parented to the owner + stamped + prepared (the `RealizeCore` contract). |
| **VV0.3** | `RealizeRange` fires which channel? | `ContainersRealizedChanged(Realized, …, realizedContainers)` carrying the newly-realized instances (`Count == RealizedContainers.Count`) — **`ContainersChanged` does NOT fire** on realize (no spurious `SelectionModel.ItemsInserted`). |
| **VV0.4** | `UnrealizeRange(start, count)` over realized slots. | Those slots become null (`ContainerFromIndex == null`); fires `ContainersRealizedChanged(Unrealized, …, removedContainers)` with `Count == RemovedContainers.Count`; **`ContainersChanged` does NOT fire** (no `SelectionModel.ItemsRemoved`). `ContainerCount` unchanged. |
| **VV0.5** | Unrealize step order. | The CD-P9-3 4-step is honored: `ClearContainerForItem` (bindings live) → drop from `_realized`/`_indexByContainer` → fire `ContainersRealizedChanged(Unrealized,…,removed)` (the host's visual detach = store-retraction trigger) → `FinishUnrealize` (logical detach + DataContext/stamp clear), in that order. |
| **VV0.6** | `IndexFromContainer` after a realize. | O(1) back-map: the item index for a realized container; **−1** for a stranger. After an item-structural shift (VV0.12) a still-realized container's `IndexFromContainer` reflects its NEW item index. |
| **VV0.7** | `:alternate` parity. | Item-indexed: a container realized for item `i` carries `:alternate` iff `(i & 1) == 1` — independent of realization *order* or which other indices are realized (the stamp is applied at realize from the item index, not the realized position). |
| **VV0.8** | Keep-alive: `UnrealizeRange` spanning a focused container. | A container with `IsKeyboardFocusWithin == true` is **skipped** — it stays realized (`ContainerFromIndex` non-null), is NOT in `removedContainers`, `ClearContainerForItem` does NOT run on it. The rest of the range unrealizes normally. (Caret keep-alive lands at V3.) |
| **VV0.9** | Recycling reuse. | `VirtualizationMode.Recycling`: `UnrealizeRange` of a generated (non-own) container returns it to the pool (its `ClearContainerForItem` ran). A later `RealizeRange` for a *different* item reuses the pooled instance (**same object reference**) re-prepared with the new item (DataContext/Content swapped, new stamp, new logical-parent). `Standard` mode: a fresh instance each realize (pool stays empty). |
| **VV0.10** | Recycle reset contract. | Before reuse, a recycled container's **owner-specific** transient state is reset via `ItemsControl.ResetRecycledContainerCore` so it does not bleed across items: `ISelectableContainer.IsSelected == false` (the base), and a control overrides for extra state (`TreeView` clears `TreeViewItem.IsExpanded`/`IsSelected` — VV0.25). The new stamp + DataContext are re-applied by `PrepareContainerForItem`. **Interaction pseudo-classes** (`:pointerover`/`:pressed`/`:focus`) are cleared by the host's *visual detach* during the `Unrealized` event (before pooling), not by the generator — they live in the interaction-state bitfield and cannot be set via `PseudoClasses.Set`. (A container that *was* selected, unrealized, then recycled for an unselected item shows unselected.) |
| **VV0.11** | Own-container is never recycled. | An own-container (item is a `UIElement`): `UnrealizeRange` runs `FinishUnrealize` (logical detach) but leaves the user's element **intact** (not `ClearContainer`'d, not pooled). Re-realizing the same own-container item returns the **same** user element (identity preserved); it never enters the recycle pool. |
| **VV0.12** | Item structural change in virtualizing mode. | Source `Insert(k)` ⇒ `_itemCount` grows by 1; realized containers at index `≥ k` shift to `index+1` (back-map re-keyed, `:alternate` re-stamped on the shifted tail); fires `ContainersChanged(Realized, k, 1)` (so `SelectionModel.ItemsInserted` runs). A realized container that was at `k` keeps its identity at `k+1`. `Remove(k)`: symmetric, `ContainersChanged(Unrealized, k, 1)`; if `k` was realized, that container unrealizes (4-step). `Move`/`Reset` analogous. |
| **VV0.13** | Eager mode untouched. | Without `EnableVirtualization`, every existing behavior is byte-identical: the eager full-realize on `SetSource`, the `ContainersChanged` Realized/Unrealized/Moved/Reset semantics + ordering, `Restripe`, the O(n) `IndexFromContainer`. The full existing generator / `SelectingItemsControl` / `ItemsPresenter` / `ListBox` / `ComboBox` / `TreeView` / `Menu` / `TabControl` test suites stay green with zero edits. |
| **VV0.14** | Teardown in virtualizing mode. | `ReleaseSource` unhooks the view's `INotifyCollectionChanged` (a detached source no longer drives the generator), and **tears down + clears the recycle pool** (`UIElement.TearDown` per pooled container — they are detached, so the owner's logical-child sweep would miss them, leaking any `Source`/`ElementName` binding their template subtree holds). Realized containers are still logical children, so the owner's `TearDown` sweep reaps them (as in eager mode). Idempotent. |
| **VV0.15** | Idempotence. | `RealizeRange` over already-realized slots is a no-op (no duplicate container, no duplicate event, no leak); `UnrealizeRange` over already-unrealized slots is a no-op. A partially-realized range realizes only its null slots. |
| **VV0.16** | `VirtualizingPanel` attached properties. | `VirtualizingPanel` (abstract `: Panel`) hosts `IsVirtualizingProperty` (bool, default **false** — opt-in), `VirtualizationModeProperty` (`Standard`/`Recycling`, default **Recycling**), `ScrollUnitProperty` (`Item`/`Cell`, default **Item**) as attached properties settable on an `ItemsControl`; `Get*`/`Set*` round-trip. (The panel reads them at V2; V0 only defines the host + accessors.) |
| **VV0.17** | Eager → virtualizing switch. | `EnableVirtualization` on an already-sourced eager generator tears down the eager containers and resets to item-indexed, zero-realized (`ContainerCount` preserved, `0` realized). |
| **VV0.18** | Virtualizing `Move` re-keys. | A source `Move(old, new)` re-keys every realized container so `ContainerFromIndex(i)` holds the item the source now has at `i` (forward AND backward moves), back-map consistent, `:alternate` re-stamped to the new parity; fires `ContainersChanged(Moved, new, count, old)` on the structural channel only (materialization silent). Matches the eager `RemoveRange+InsertRange` remap. |
| **VV0.19** | Realize across a keep-alive hole. | Pin an interior container (focus), unrealize the band (it survives), then re-realize the band: the `Realized` event's `RealizedContainers` excludes the still-realized pinned container, and a host adopting `RealizedContainers` never double-adopts (no "already in this collection" throw). |
| **VV0.20** | `:alternate` re-stamp on shift. | A structural shift flips the `:alternate` pseudo-class of a still-realized container to its NEW item-index parity (`Insert` ahead of it 4→5 gains `:alternate`; `Remove` 5→4 loses it). |
| **VV0.21** | Real `ItemsPresenter` + virtualizing generator. | A virtualizing generator hosted by the *eager* `ItemsPresenter` (default panel) no-ops the structural channel — source `Move`/`Insert`/`Remove`/`Clear` do **not** throw (pre-fix the index-aligned `Moved`/`SyncAll` crashed on the sparse store). |
| **VV0.22** | `SelectedItem` for an unrealized index. | With nothing realized, `SelectedIndex = k` ⇒ `SelectedItem`/`SelectedItems` resolve item `k` from the source view (not a null container); `SelectedItem = item` ⇒ `SelectedIndex` is set (does NOT silently clear). |
| **VV0.23** | Recycle push-guards. | `Standard` mode never pools (`PooledCount == 0` after unrealize); own-containers never pool (an own-container unrealize leaves `PooledCount == 0`; a generated one increments it). |
| **VV0.24** | Pooled-container teardown. | A `Source` binding installed on a recycled container is **torn down** by `ReleaseSource` (the source INPC subscription is released — no leak). |
| **VV0.25** | `TreeView` recycle reset. | A recycled `TreeViewItem` (not an `ISelectableContainer`) has `IsExpanded`/`IsSelected` cleared by `TreeView`'s `ResetRecycledContainerCore` override before reuse — neither bleeds to the next data node. |

### Audit focus (V0)

The synthesis's verified corruption risks become the adversarial-audit checklist: (a) a realize/unrealize must
NOT reach `SelectionModel.ItemsInserted/Removed` (VV0.3/VV0.4 — selection-index corruption); (b) `ContainerCount`
must stay `== itemCount` so `ListBox.OnKeyDown` End/PageDown and `SelectingItemsControl.SetSelectedIndexExternal`
clamp against the item count, not the realized window; (c) recycle state bleed (VV0.10); (d) own-container
mutation (VV0.11); (e) the back-map must not leak entries across unrealize/recycle (VV0.6/VV0.14); (f) `:alternate`
parity by item index, not realized position (VV0.7).

### Audit resolution (V0 adversarial review — 8 dimension-finders + refute-by-default verify)

A 43-agent audit confirmed 29 findings (6 refuted). Distinct defects fixed (each with a regression row above):

- **`ItemsPresenter` crashed on a virtualizing source `Move`/`SyncAll`** (the index-aligned host vs the sparse store —
  `Remove(null)`/out-of-range). Fixed: it no-ops its structural handlers when `_generator.IsVirtualizing` (VV0.21).
- **`SelectedItem`/`SelectedItems`/set-by-item broke for an unrealized index** (resolved via the container). Fixed:
  `ItemContainerGenerator.ItemFromIndex(int)` reads the source view; `SelectingItemsControl` routes through it (VV0.22).
- **`RealizeRange`'s Realized span bridged kept-alive holes** → host double-adopt. Fixed: the event carries the
  authoritative `RealizedContainers` instance list (symmetric with `RemovedContainers`); `Count` mirrors it (VV0.3/VV0.19).
- **Recycle reset missed type-specific state** (`TreeViewItem.IsExpanded`). Fixed: the reset is the owner virtual
  hook `ResetRecycledContainerCore` (base clears `ISelectableContainer.IsSelected`; `TreeView` overrides — VV0.25).
- **Pooled containers bypassed `TearDown`** (binding leak). Fixed: `ReleaseSource` tears down pooled containers (VV0.24).
- **`SelectingItemsControl` Reset ran an O(itemCount) reconcile** at zero-realized. Fixed: skipped under virtualization.
- **Unrealized event `Count` diverged from `RemovedContainers.Count`** under keep-alive/partial. Fixed: `Count` = the
  removed-instance count (VV0.4).
- **Test gaps** (Move, event span, `:alternate` re-stamp, real-`ItemsPresenter`, Standard/own-container pool guards):
  rows VV0.18–VV0.25 + the internal `PooledCount` hook.

Refuted (no code change): interaction pseudo-classes bleeding as a *correctness* bug (the host's visual detach clears
them before pooling, and they cannot be set via `PseudoClasses.Set`); a recycle write to the previous item's binding
(DataContext is cleared before pooling). The matrix's VV0.10 wording was corrected to match the real ownership.

---

## §V1–§V5 (sharpened when each phase lands)

- **§V1** — `IScrollContentHost { IsScrollClient, ScrollOwner, GetExtent, SetViewport, LineStep, PageStep,
  IsLogicalScroll }` + `ILogicalScrollHost : IScrollContentHost { BringItemIntoView, ItemCount, EstimateItemAt }`
  (internal, cell-integer throughout). SCP `_scrollHost` discovery on its direct `Content`; `InvalidateScrollExtent`
  (the WPF `InvalidateScrollInfo` analog); gated measure (viewport-constraint, not `MaxScrollExtent`) / arrange
  (`SetViewport`) / extent-from-host; the 32K cap lifted when `IsScrollClient`. `ItemsPresenter` forwards the
  contract to its panel + re-establishes on `RebuildPanel` (`ScrollContentChanged` pulse). The X174-analog gate:
  ListBox-over-ScrollViewer byte-identical pre/post (the `_scrollHost == null` path).
- **§V2** — `VirtualizingStackPanel : VirtualizingPanel, ILogicalScrollHost`; band-derived realization window in
  its own `MeasureOverride` (sanctioned §5.3 self-mutation) + the no-op measure guard; true-content-row arrange;
  uniform `avgItemRows` extent; the band=realization-window coverage invariant; in-band scroll = zero re-raster +
  zero realize churn (invariant-3 ON-path gate); re-anchor = exactly one realize batch; short-list reports exact
  rows.
- **§V3** — `ListBox` End/PageDown over `itemCount` + `EnsureItemVisible → BringItemIntoView` realize-then-focus
  (post-layout boundary); focus/caret keep-alive end-to-end; `SelectingItemsControl` subscribes
  `ContainersRealizedChanged.Realized` → `ReconcileContainers` (selected-but-unrealized item's `IsSelected`
  re-applied on realize); `TextBox`-in-virtualized-item caret survives scroll-out/in via keep-alive. input-matrix
  N-VIRT rows.
- **§V4** — sticky per-item measured-height cache (estimate only truly-unrealized); monotone extent; prefix-sum
  arrange + `EstimateItemAt` binary search; thumb-settle ≤1 frame after a drag; convergence under the
  `LayoutManager` 16-pass fixpoint (no `LayoutCycle`/`AbandonPendingLayout` on realistic heterogeneous lists).
- **§V5** — fling-storm benchmark (10K list, <33 ms/frame, 0 B steady-state in-band slides) + control-gallery
  virtualized-list tab + adversarial closeout. Default-`ItemsPanel` flip deferred to post-soak.
