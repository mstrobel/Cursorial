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

## §V1 — `IScrollContentHost`/`ILogicalScrollHost` + `ScrollContentPresenter` delegation

The scroll abstraction (cell-integer throughout, internal): the SCP **delegates** extent reporting + viewport
hand-off to its content when that content opts in, instead of measuring all children. Cursorial keeps ONE offset
coordinate (the SCP's styled `ScrollOffsetRow`/`ScrollOffsetColumn`); the host only advertises the extent estimate +
step size — the offset stays SCP-owned and storyboard-animatable (the deliberate deviation from WPF `IScrollInfo`,
which makes the panel own the offset).

```csharp
internal interface IScrollContentHost
{
    bool IsScrollClient { get; }                 // false ⇒ the SCP runs its verbatim legacy measure-at-MaxScrollExtent path
    ScrollContentPresenter? ScrollOwner { get; set; } // the SCP injects itself (adopt) / clears (disown); the back-channel
    bool CanScrollHorizontally { get; set; }     // flow from the SCP's own axis enables
    bool CanScrollVertically { get; set; }
    Size GetExtent();                            // total scrollable content in CELLS (estimated); the SCP publishes it as Extent
    void SetViewport(Size viewport);             // the SCP hands the host its arranged viewport before the host's next measure
    int LineStep(int currentOffset, int sign, bool vertical); // step quantization (cells); consumed by ScrollViewer at V2
    int PageStep(int currentOffset, int sign, bool vertical);
    bool IsLogicalScroll { get; }
}

internal interface ILogicalScrollHost : IScrollContentHost
{
    Rect BringItemIntoView(int itemIndex);       // realize-if-needed + the item's estimated cell rect (V3)
    int ItemCount { get; }
    int EstimateItemAt(int offsetRow);           // inverse map (keyboard/thumb)
}
```

### Rows

| Row | Scenario | Expected |
| --- | --- | --- |
| **VV1.1** | The interface shape. | `IScrollContentHost`/`ILogicalScrollHost` are **internal**, cell-integer; `ILogicalScrollHost : IScrollContentHost`. The SCP consumes only the base; a panel implements the derived; `ItemsPresenter` forwards. |
| **VV1.2** | SCP host discovery. | The SCP resolves `_scrollHost = Content as IScrollContentHost` in its `Content` setter; sets `host.ScrollOwner = this` on adopt and `= null` on disown/re-host (no stale owner across a content swap). |
| **VV1.3** | OFF-path (no host / `IsScrollClient == false`). | A real `ListBox`-over-`ScrollViewer` (its `StackPanel` is not a host) runs the SCP's **verbatim legacy** measure/arrange/extent/coercion/band path — byte-identical. The existing `ScrollViewer`/`ListBox`/Phase5 scroll suites stay green. |
| **VV1.4** | Host-active measure. | When `IsScrollClient`, the SCP flows its `CanScroll*` enables to the host, measures `Content` at the **viewport** on scrollable axes (`min(availableSize, MaxScrollExtent)` — never raw `MaxScrollExtent`, and the `min` keeps an `Unbounded` parent from handing the panel an `int.MaxValue` window — VV1.4b), and publishes `Extent = host.GetExtent()` instead of `content.DesiredSize`. |
| **VV1.4b** | Finite measure constraint under an unbounded parent. | A `ScrollViewer` in a vertical `StackPanel` (which measures at `Unbounded` height) still hands the host a **finite** constraint (`MaxScrollExtent`), mirroring the legacy substitution — a virtualizing panel never receives `int.MaxValue`. |
| **VV1.5** | The 32K cap is lifted under a host (up to the `Rect` ceiling). | A host `GetExtent()` above `MaxScrollExtent` (32K) publishes past the legacy cap — `SCP.Extent`/`ScrollViewer.Extent`/`ScrollBar.Maximum` reflect it and the offset reaches the tail (`Extent − Viewport`). **The ceiling is `LayoutMath.MaxExtent` (`int.MaxValue / 2` ≈ 1.07 B cells)** — the layout clamp, decoupled from the wider `Rect.MaxDimension` geometry cap (`int.MaxValue`); the content is arranged at the full extent height which the layout clamps to `MaxExtent`, and the half-int value keeps a layout `Rect`'s edge arithmetic from overflowing and stays distinct from the `Unbounded` sentinel — effectively unbounded for any real list. The legacy (non-host) path keeps the lower `MaxScrollExtent` sanity cap + its diagnostic. |
| **VV1.6** | Host-active arrange. | The SCP publishes `Viewport`, then hands the host `SetViewport(finalSize)` **before** the host's next measure, then arranges `Content` at `(0,0,max(extent,viewport))`, re-coerces both offsets, and `RefreshBandGeometry` (all mode-agnostic). |
| **VV1.7** | `InvalidateScrollExtent` (the WPF `InvalidateScrollInfo` analog). | Re-publishes `Extent` from `GetExtent()`, re-coerces both offsets, and marks measure dirty — the host's estimate-refinement back-channel (called via the injected `ScrollOwner`). |
| **VV1.7b** | `InvalidateScrollExtent` marks measure dirty. | A GROWN host extent with the offset still in range re-publishes through `InvalidateMeasure` alone (no offset change to ride the coercion side channel) — `ScrollViewer.Extent` reflects the new value, the offset is unchanged. |
| **VV1.8** | `ItemsPresenter` forwarding + back-channel wiring. | `ItemsPresenter : ILogicalScrollHost` forwards every member to `_panel as ILogicalScrollHost` (`IsScrollClient == false` when the panel is not a host). **`EnsurePanel` wires the panel's `ScrollOwner` from the retained `_scrollOwner` on every build** (initial load, lazy build, re-attach, swap), so the host→SCP back-channel is live on the primary path, not only after a swap. |
| **VV1.8b** | `ItemsPanel` host→host swap. | `RebuildPanel` disowns the OLD panel (`oldHost.ScrollOwner = null`, mirroring the SCP's Content-setter disown — no dangling back-channel), `EnsurePanel` wires the new one, and the SCP is pulsed via `InvalidateScrollExtent`. |
| **VV1.9** | The X174-analog OFF-path drift gate. | A `ScrollViewer` over plain content (`IsScrollClient == false`) renders the same cells from the top + scrolls the same (`row00` at row 0 → `row05` after a 5-row scroll) with the delegation code present — the OFF-path is unperturbed. |

### Audit focus (V1)

(a) the OFF-path must be a pure no-op — `IsScrollClient == false` for every existing tree (the `ItemsPresenter`
forwards `false` because no shipped panel is a host); (b) the host extent must cap at `LayoutMath.MaxExtent`
(`int.MaxValue / 2`, the layout clamp — the content is arranged at the full extent height, which the layout clamps
there; decoupled from the wider `Rect.MaxDimension` geometry cap; VV1.5/VV1.5b); (c) the viewport-constraint measure is a behavioral fork for any intervening template element
between the SCP and the panel — audit the template subtree; (d) `ScrollOwner` lifetime across content swap / template
re-host (no stale back-channel); (e) the host path must keep the band geometry + coercion + composite-slide invariants
the legacy path guarantees.

### Audit resolution (V1 adversarial review — 6 dimension-finders + refute-by-default verify)

A 35-agent audit confirmed 22 findings (7 refuted), consolidating to 4 distinct defects + test gaps:

- **The panel's `ScrollOwner` was wired only by `RebuildPanel`** (HIGH) — the back-channel was dead on the primary
  path (a host `ItemsPanel` present at construction, and every detach/re-attach). Fixed: `EnsurePanel` wires it on
  every build (VV1.8).
- **`RebuildPanel` left the old panel's `ScrollOwner` dangling** on a host→host swap (MED). Fixed: it disowns the
  old panel, mirroring the SCP's Content-setter disown (VV1.8b).
- **`MeasureWithHost` forwarded an `Unbounded` constraint to the panel** (MED/HIGH) — the legacy path substitutes a
  finite `MaxScrollExtent`; the host path now clamps `min(availableSize, MaxScrollExtent)` per scrollable axis, so a
  virtualizing panel never receives an `int.MaxValue` realization window (VV1.4b).
- **The SCP never flowed `CanScroll*` to the host** (MED). Fixed: `MeasureWithHost` flows the axis enables before
  measuring (VV1.4).
- **Test gaps:** the VV1.9 OFF-path drift gate was unimplemented; VV1.8 never asserted the panel received a
  `ScrollOwner`; `InvalidateScrollExtent`'s `InvalidateMeasure` survived a mutation (the offset-coercion side channel
  masked it). Closed by VV1.7b/VV1.8/VV1.8b/VV1.9 (VV1.7b mutation-verified).

Refuted (no code change): a stale `ScrollOwner` after the host flips `IsScrollClient` false (the gate already no-ops);
a missing SCP detach hook to clear the host owner (the ScrollViewer's `OnTemplateDetaching` clears via
`presenter.Content = null`).

---

## §V2 — `VirtualizingStackPanel` (uniform-height item mode)

The panel that makes virtualization actually render: `VirtualizingStackPanel : VirtualizingPanel, ILogicalScrollHost`.
It drives realization from its OWN `MeasureOverride` (the sanctioned §5.3 self-mutation — the panel IS the element
being measured, like WPF/Avalonia), arranges realized containers at their TRUE content-row positions inside the
full-extent rect, and reports the estimated extent through the `IScrollContentHost` contract (V1). v1 is
uniform-height (every item `avgItemRows` tall, refined from the first measured container); variable-height sticky
caching is V4.

**The realization window** is read from the SCP band the V1 `ScrollOwner` exposes
(`BandStartRow`/`BandLength`/`BandPadding` + `ScrollOffsetRow`/`Viewport`): the panel realizes every item whose
content rows intersect `[BandStartRow, BandStartRow + BandLength)` (+ band-derived slack), so realization coverage
is a **superset of the band by construction** — the headline invariant.

### Rows

| Row | Scenario | Expected |
| --- | --- | --- |
| **VV2.1** | Panel identity + mode. | `VirtualizingStackPanel : VirtualizingPanel, ILogicalScrollHost`; `IsScrollClient`/`IsLogicalScroll` are decided at attach from `VirtualizingPanel.GetIsVirtualizing/ScrollUnit(owner)` (NOT from item count — stable before the SCP's first measure, so no one-frame mode flip). |
| **VV2.2** | Attach wiring. | On attach the panel resolves its owning `ItemsControl`, calls `generator.EnableVirtualization(mode)` when `IsVirtualizing`, and subscribes `ContainersRealizedChanged` to adopt (`RealizedContainers` → `Children.Add`) / release (`RemovedContainers` → `Children.Remove`) — it owns its `Children`, not the `ItemsPresenter` (whose structural handler no-ops in virtualizing mode, V0). |
| **VV2.3** | Measure = the realization driver (§5.3). | `MeasureOverride` reads the band from `ScrollOwner`, computes `[firstItem, lastItem]` covering `[BandStartRow, BandStartRow+BandLength)` + slack (`ceil(BandPadding / avgItemRows)` each side), `UnrealizeRange` outside / `RealizeRange` inside (the panel's own measure — sanctioned), measures the realized containers, and returns the estimated full extent. |
| **VV2.4** | Extent estimate. | `GetExtent().Rows == itemCount × avgItemRows` (uniform) — for 1-row items, exactly `itemCount`. Published as `SCP.Extent` via the V1 contract → `ScrollViewer.Extent` → `ScrollBar.Maximum`, all proportional without realizing all N. |
| **VV2.5** | True-content-row arrange. | `ArrangeOverride` arranges each realized container at content row `itemIndex × avgItemRows` (uniform) within the full-extent rect (cross-axis like `StackPanel`); the SCP band fold + composite slide place the band on screen at the viewport. |
| **VV2.6** | Band = realization coverage (no blank rows). | Every item whose rows intersect `[BandStartRow, BandStartRow+BandLength)` is realized after measure — the realized set is a superset of the band, so no band row is ever un-rastered (slack is derived FROM `BandPadding`, not guessed). |
| **VV2.7** | **In-band scroll = zero re-raster + zero realize churn (the invariant-3 ON-path gate).** | An offset change WITHIN `±K` of the anchor (no re-anchor) does NOT re-measure the panel (the offset is `AffectsComposite`, not `AffectsMeasure`; the SCP's `RunReAnchorCheck` returns early in-band without `InvalidateRealization`) and does NOT call `RealizeRange`/`UnrealizeRange` — a pure composite slide. Asserted: the SCP band `Scene.RasterVersion` is unchanged AND the generator's realized set is unchanged across the slide. |
| **VV2.8** | Re-anchor = exactly one realize batch. | An offset change crossing `±K` re-anchors the band (the SCP marks the zone dirty + calls `InvalidateRealization`); the panel re-measures, `RealizeRange`s the newly-covered items and `UnrealizeOutside`s the now-uncovered — O(1) per re-anchor, never O(n). |
| **VV2.9** | Short list. | When `itemCount × avgItemRows < viewport`, the panel realizes all items and reports the EXACT realized sum as the extent (not the viewport) — a 3-item list reports 3 rows, so the `ScrollBar` hides (no false overflow). |
| **VV2.10** | No-op measure guard. | A re-measure with the **window inputs** `(BandStartRow, BandLength, itemCount, availableWidth)` unchanged returns the cached desired size and realizes nothing (the offset is deliberately NOT a key — it doesn't move the window; the band does). A structural Move/equal-Replace changes none of these but DOES move item identity, so the structural handler busts the guard (`_hasMeasured = false`) — VV2.12. |
| **VV2.11** | End-to-end (UITestHost). | A virtualizing `ListBox` of 1000 items shows only the band's worth of `ListBoxItem`s (`CountRealized ≈ viewport + slack`, not 1000), `ContainerCount == 1000`, the visible window renders the right items, a wheel/offset scroll re-anchors and shows the new window, and selection of an off-screen index stays correct (V0 item-indexing). |
| **VV2.12** | Structural change under virtualization. | A source `Move` of an unrealized item INTO the band, and an equal-count `Replace` at a realized index, both reconcile the window (the moved/replaced index realizes + carries the right item, no blank band row, no stray realized container) — the structural handler busts the no-op guard so the next measure re-runs `UnrealizeOutside`/`RealizeRange`. |
| **VV2.13** | Recycle of `UIElement`-content. | A virtualizing list of `UIElement` items (each wrapped in a generated container) survives a scroll far-and-back-twice without an "already has a visual parent" crash — a recycled container's `ContentPresenter` releases its directly-hosted `UIElement` child on detach (before pooling), so the same item re-hosts cleanly. |

### Audit focus (V2)

(a) the band=realization coverage invariant (VV2.6) — a wrong slack or a stale `avgItemRows` must not leave blank
band rows even for one frame (the `LayoutManager` fixpoint re-measures same-frame; the V4 sticky cache makes it
monotone); (b) invariant-3 (VV2.7) — the offset write must NOT invalidate the panel measure on an in-band slide
(the no-op guard is load-bearing); (c) realization churn cadence == re-anchor cadence, never per-cell; (d) the
measure-time self-mutation must stay within §5.3 (the panel mutates its OWN children + the owner's logical children
via the generator — sanctioned — and never a sibling); (e) the convergence of the extent↔band↔realize loop under
the fixpoint (uniform mode converges in one pass; the pathological seed-`avgItemRows` case is a bounded one-frame
transient).

### Audit resolution (V2 adversarial review — 6 dimension-finders + refute-by-default verify)

A 25-agent audit confirmed 11 findings (7 refuted), consolidating to 3 code defects + test gaps:

- **The no-op guard swallowed a structural Move / equal-count Replace** (CRITICAL) — its key
  `(BandStartRow, BandLength, itemCount, availableWidth)` is unchanged by a Move/Replace, so the re-measure the
  structural handler scheduled was short-circuited → a blank band row + a stray realized container. Fixed:
  `OnContainersStructurallyChanged` sets `_hasMeasured = false` to bust the guard (VV2.12, mutation-verified).
- **Recycling a container whose `ContentPresenter` hosted a `UIElement` item crashed** (HIGH) on the second
  re-anchor ("already has a visual parent") — the pooled container's `ContentPresenter` never released the child's
  visual parentage (the rebuild is lazy/measure-time, and a pooled container never measures). Fixed:
  `ContentPresenter.OnDetachedFromTree` releases a directly-hosted `UIElement` child (VV2.13, mutation-verified).
- **A redundant extra measure pass on uniform 1-row lists** (LOW) — the `refined` flag fired on a no-op `1→1` avg
  assignment. Fixed: it fires only on a real avg change.
- **Test gaps:** no structural-change-under-virtualization test, VV2.7 never asserted `RasterVersion` (the
  invariant-3 "zero re-raster" half), VV2.5 true-content-row arrange was unproven (no avg≠1 fixture). Closed by
  VV2.5/VV2.7/VV2.12/VV2.13.

Refuted (no code change): the no-op guard omitting the offset/viewport from its key (correct by design — the offset
doesn't move the window, and a width-only change IS keyed via `availableWidth`); the avg-seed one-frame transient
(documented + fixpoint-bounded).

## §V3a — selection + caret across the realization boundary

The correctness pieces of V3: a selected item must SHOW selected when it scrolls into view, and a container the user
is editing (a `TextBox` publishing a terminal caret) must not be unrealized out from under them.

### Rows

| Row | Scenario | Expected |
| --- | --- | --- |
| **VV3.1** | Reconcile-on-realize. | A selected-but-unrealized item (`SelectedIndex = 700` while 700 is off-screen) shows `IsSelected`/`:selected` the moment it scrolls into view — `SelectingItemsControl` subscribes the generator's `ContainersRealizedChanged.Realized` and re-applies each materializing container's selection FROM the model. |
| **VV3.1b** | Reconcile drives from the model. | A non-selected neighbour materializing in the same window realizes UNselected (the reconcile is model-driven, not stale-state-driven). |
| **VV3.2** | Caret keep-alive. | A container that owns a live terminal-caret publication (within its subtree) is **pinned** — `UnrealizeRange`/`UnrealizeOutside` skip it (the generator's `IsContainerPinned` adds the caret leg via `CaretService.HasPublicationWithin`); clearing the caret unpins it. (Focus keep-alive is V0; this adds the caret leg.) |
| **VV3.2b** | Caret keep-alive via a descendant owner. | The keep-alive honors a publication owned by a visual descendant of the container (the real shape — a `TextPresenter`/`Caret` inside a `TextBox` inside the item), not just the container itself. |

Eager mode is unaffected: the materialization channel is dormant (so reconcile-on-realize never fires), and a
non-virtualized list never `UnrealizeRange`s (so the caret leg is never consulted). Both V3a mechanisms are
mutation-verified (dropping the reconcile subscription fails VV3.1; dropping the caret leg fails VV3.2/VV3.2b).

## §V3b–§V5 (sharpened when each phase lands)

- **§V3b** — `ListBox` keyboard nav across the boundary: End/Home/PageDown over `itemCount` + `BringItemIntoView`
  realize-then-focus (scroll → realize on the next layout → focus at the post-layout boundary, since there is no
  synchronous `UpdateLayout`). NOTE: scroll-on-focus is absent even in the eager `ListBox`, so this is a new feature,
  not a virtualization regression — scoped as its own unit. input-matrix N-VIRT rows.
- **§V4** — sticky per-item measured-height cache (estimate only truly-unrealized); monotone extent; prefix-sum
  arrange + `EstimateItemAt` binary search; thumb-settle ≤1 frame after a drag; convergence under the
  `LayoutManager` 16-pass fixpoint (no `LayoutCycle`/`AbandonPendingLayout` on realistic heterogeneous lists).
- **§V5** — fling-storm benchmark (10K list, <33 ms/frame, 0 B steady-state in-band slides) + control-gallery
  virtualized-list tab + adversarial closeout. Default-`ItemsPanel` flip deferred to post-soak.
