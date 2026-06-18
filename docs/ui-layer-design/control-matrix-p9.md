# Control matrix — P9 (interactive control set) — normative spec

The oracle-pinned behavior matrix for P9's controls (design doc §12 / the §14 P9 row). Extends the P5
`control-matrix.md` (which is C0/C1/C3-fenced). One test per row, mirrored under
`Cursorial.UI.Tests/ControlMatrix/Section15…`. Oracle column: `WPF` = WPF/Avalonia parity; `PIN` = a Cursorial
decision recorded in the CD-P9 ledger below.

Phasing (the P9 sub-phase plan): **P9.1** §C2 items core · P9.2 SelectionModel · P9.3 ListBox · P9.4 menus ·
P9.5 TabControl · P9.6 TextBox · P9.7 ProgressBar + chrome · P9.8 inspector demo. Sections fill as each lands.

---

## CD-P9 — pinned decision ledger

- **CD-P9-1 — visual-only adoption via `Panel.IsItemsHost`.** Generated containers are LOGICAL children of the
  `ItemsControl` (inheritance/resource/style flow from the control) and VISUAL children of the items panel. The
  WPF-familiar mechanism: the items panel carries `Panel.IsItemsHost = true`, which makes its `Children`
  collection adopt visually only (`UIElement.AddVisualChildOnly` / `RemoveVisualChildOnly`) — the logical parent
  is the `ItemsControl`, set by the generator (`AddLogicalChild`). `UIElementCollection` branches on the owner's
  `AdoptsChildrenVisualOnly` flag; `ValidateAdoptable` permits a pre-existing logical parent in that mode.
- **CD-P9-1a — the `ItemForItemContainer` stamp (WPF parity).** The generator stamps each container with its
  source item via the internal `ItemContainerGenerator.ItemForItemContainerProperty` attached property — the
  single source of truth for "which item does this container represent". It backs the public
  `ItemFromContainer(container)` (O(1), survives reordering — P9.2/P9.3 selection re-enter here) and the unrealize
  **own-container test**: for an own-container the stamp equals the container, so `container == item` identifies it.
  The container's `DataContext` cannot serve this role — the generator deliberately never sets DataContext on an
  own-container, so `container != DataContext` would mis-classify it and clear the user's element. `ClearContainerForItem`
  / `ClearContainerForItemOverride` therefore take `(container, item)` (WPF's `container != item` gate), and the
  stamp is cleared in the unrealize tail. The **DataContext clear is symmetric with the set** — only a generated
  container's DataContext is cleared (it was set to the item); an own-container's DataContext is the user's and is
  left untouched (mirrors the `Style` skip — rows C2.10/C2.17).
- **CD-P9-1b — source release on teardown.** A bound `ItemsSource` is a raw `INotifyCollectionChanged`
  subscription (not a binding), so a live viewmodel collection would pin the control. `UIElement.TearDown` gained a
  `protected virtual OnTearDown()` (runs after the child sweep, before the value-store/binding sweep; **not** on
  transient detach — re-attach rebuilds); `ItemsControl.OnTearDown` calls `ItemContainerGenerator.ReleaseSource()`
  to unhook + dispose the current `ItemsSourceView`. Swapping the source (incl. `→ null`) disposes the old view too.
- **CD-P9-2 — eager realization (v1).** Every item is realized; the unrealize retraction sequence
  (ClearContainer → visual detach → logical remove → DataContext/stamp clear) and the range-based
  `ItemContainerGenerator.ContainersChanged` event are the seam a future recycling/virtualizing host re-enters at.
- **CD-P9-3 — the 4-step Unrealize order.** ClearContainer (unhook while bindings live) precedes the visual
  detach (the subtree detach IS the store-retraction trigger), which precedes the logical removal + DataContext
  clear. The generator fires `Unrealized` (host removes visually) *before* dropping the range from its list, so
  the containers stay index-addressable through the detach. **The full-reset/re-source teardown reuses the same
  staged `RemoveRange`** (ClearContainer → `Unrealized` event → FinishUnrealize) so the wholesale path honors the
  same ordering rather than tearing down logical-first (rows C2.9b/C2.13).
- **CD-P9-4 — ItemsSource ⊥ Items.** Setting `ItemsSource` with a populated `Items` throws; mutating `Items`
  while `ItemsSource` is set throws (WPF rule). Both lanes normalize through one internal `ItemsSourceView` so
  the generator has a single realize/unrealize driver.
- **CD-P9-5 — `ItemContainerStyle` at the Explicit layer.** Applied as `container.Style`, so app-level
  type-selector styles (and later `:selected`/`:alternate`) compose underneath it.
- **CD-P9-6 — runtime template change ⇒ Reset; panel change ⇒ re-host.** Changing `ItemTemplate` or
  `ItemContainerStyle` at runtime unrealizes all + re-realizes (v1 — the containers' content/style is what changed).
  Changing `ItemsPanel`, by contrast, is a **re-host**: the `ItemsPresenter` rebuilds the panel and re-adopts the
  *same* container instances visually (the generator is untouched — containers don't depend on the panel). Row
  C2.19 pins the same-instance re-host.
- **CD-P9-7 — configurable items panel deferred shape.** `ItemsControl.ItemsPanel` (default a vertical
  `StackPanel`) is built by the `ItemsPresenter`, which sets `IsItemsHost` on it and delegates layout to it. v1
  ships the default; non-vertical panels (WrapPanel, horizontal) ride P9.3/P9.5 when their consumers land.
- **CD-P9-25 — `:alternate` row-striping, stamped by the container generator (P9 tail).** The positional
  `:alternate` pseudo-class is stamped by `ItemContainerGenerator` on **odd** 0-based-indexed containers
  (`index & 1`) via the internal `UIElement.SetPseudoClassFromMapping` write path, re-run (`Restripe()`) after
  every structural change (set-source/insert/remove/move/reset) since those shift indices; the writes are
  change-only, so the restyle is bounded to the parity-flipped tail, not the whole list. Index 0 is the base
  row (not alternate). It applies to **all** items hosts (the generator is shared — `ListBox`, plain
  `ItemsControl`) including own-containers (harmless). The look is **opt-in**: no default theme stripe ships
  ("only visible with striping rules"; control-theme brushes come from the mockups, not invented), so an app
  authors a `…:alternate { Background }` rule to target the stamped class. Not WPF's N-way `AlternationCount` —
  a single 2-way stripe (the design's "row-striping"); positional `:nth-child` stays unsupported by design (§3.10).
- **CD-P9-26 — the resource-inspector hook (P9 tail).** `ResourceDiagnostics.GetResourceKey(element, property)`
  is the **reverse** of `Trace`: it answers "which resource key did this property's effective value resolve
  *through*?" — checking the element's instance `SetResourceReference`/`{DynamicResource}` producers first
  (they win at the Local/Template lane) then the winning style/theme `{DynamicResource}` setter, returning the
  key or null. It is a **separate** cold-path surface (it does **not** touch `StyleDiagnostics.Explain`'s pinned
  one-line format, SD13). Seams: `IResourceSubscriber.ResourceProvenance` (instance), and the
  `ValueFrame.TryGetResourceKey` virtual overridden by `StyleRuleFrame` over its resource-backed setters (style),
  resolved by `ValueStore.ResolveWinningStyleResourceKey`. The `inspect` demo appends `← resource '<key>'` to a
  resource-backed property's line — the resource companion to the style inspector (design doc §14 P9).

---

## §C2 — Items pipeline (P9.1) — tests in `Section15_Items`

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C2.1 | `ItemsControl` bound to `ObservableCollection` of 3 | shown | one container realized per item (3); index 3 is null | WPF |
| C2.1b | `ItemsControl` with 2 direct `Items` | shown | one container per item | WPF |
| C2.3 | populated `Items`, then set `ItemsSource` / `ItemsSource` set, then mutate `Items` | each | throws `InvalidOperationException` (mutually exclusive) | WPF (CD-P9-4) |
| C2.4 | bound source | — | the default container is a `ContentPresenter` | WPF |
| C2.5 | item that IS a `UIElement` | — | used directly as its own container (no presenter wrapper) | WPF |
| C2.6/7 | bound source | inspect the container | LOGICAL parent = the `ItemsControl`; VISUAL parent = the items panel (≠ the control) — punch 43; **resources resolve up through the container to the control** (logical-parentage payoff) | PIN (CD-P9-1) |
| C2.8 | bound source | Insert at index 1 | one container realized at 1; index 0 unchanged; later items shift | WPF |
| C2.9 | bound source + `ItemContainerStyle` | Remove at 0 | the FULL 4-step retraction ran — Content/ContentTemplate cleared, `Style` dropped (step 1), LOGICAL + VISUAL parents cleared (steps 2-3), DataContext + item stamp cleared (step 4); evicted; survivors shift | PIN (CD-P9-3) |
| C2.9b | bound source + `ItemContainerStyle` | Remove at 0, observe `DetachedFromLogicalTree` | at logical-detach time the container's `VisualParent` is already null and ClearContainer's effects are applied (ordering proof) | PIN (CD-P9-3) |
| C2.10 | own-container item with a user `Style` | Remove it | ClearContainer SKIPS the own-container (user `Style` preserved) yet it is still detached (logical + visual) | PIN (CD-P9-1a) |
| C2.11 | bound source of 3 | Move 0→2 | the SAME container instances, reordered — no realize/unrealize | WPF |
| C2.11b | custom `IList`+INCC of 6 | multi-item Move (0→3, count 2) | the panel's VISUAL child order stays in lockstep with the generator's logical order (block lift/insert, not per-slot) | PIN |
| C2.12 | bound source of 3 | Replace index 1 | old container unrealized, a NEW instance realized in place; neighbors + count unchanged | WPF |
| C2.13 | bound source | Clear | all containers unrealized (staged teardown ran — parents cleared) | WPF |
| C2.14 | `ItemContainerStyle` set | shown | each container's `Style` is the given style (Explicit layer) | PIN (CD-P9-5) |
| C2.14b | app `ContentPresenter` type style + `ItemContainerStyle` contest one prop | shown | the Explicit `ItemContainerStyle` wins the contested prop; the type-selector layer still composes underneath on the uncontested prop | PIN (CD-P9-5) |
| C2.15 | runtime `ItemTemplate` change | assign | containers re-realized (Reset policy) — new instances | PIN (CD-P9-6) |
| C2.16 | construct with `ItemsSource`, then `ItemsSource = null` | — | reverts to the direct `Items` lane: `HasItemsSource` false, containers dropped, `Items.Add` works + realizes | PIN (CD-P9-4 inverse) |
| C2.16b | `IList` source that is NOT INCC | mutate it underneath | static — live-by-index, no subscription, generator does not react | PIN |
| C2.16c | INCC source that is NOT `IList` | raise `CollectionChanged` | frozen snapshot — never subscribed, generator does not react | PIN |
| C2.16d | bound `ObservableCollection` (no host) | `TearDown` then mutate the collection | the generator released the source (`OnTearDown` → `ReleaseSource`) — no reaction; the control is no longer pinned | PIN (CD-P9-1b) |
| C2.16e | bound source A | swap to source B, then mutate A | B realized, A's containers unrealized; the OLD view disposed — mutating A does nothing | PIN (CD-P9-1b) |
| C2.16f | shown `ItemsControl` | detach then re-attach | the SAME containers re-adopted exactly once (no double-adoption; generator is control-lifetime) | PIN |
| C2.17 | mixed data + own-container source | `ItemFromContainer` | round-trips the data item for a generated container; returns the container itself for an own-container | PIN (CD-P9-1a) |
| C2.18 | `HeaderedItemsControl` | set `Header` + add an item | `Header` round-trips; the items host realizes (MenuItem's base — smoke) | WPF |
| C2.19 | bound source | runtime `ItemsPanel` change | the SAME containers are re-hosted in a freshly-built panel (no re-realize, no double-adoption) | PIN (CD-P9-6) |
| C2.21 | static bound source | run idle frames | 0 B steady-state allocation (no per-frame generator/presenter churn) | PIN |
| C2.22 | bound source of 4 | shown | the generator stamps the positional `:alternate` pseudo-class on **odd** 0-based-indexed containers (1, 3), not on even (0, 2) — "with the container generator"; the look is opt-in (no default theme stripe — only visible with a `…:alternate` rule) | PIN (CD-P9-25; design §14 P9) |
| C2.23 | bound source of 4 | Insert at index 1 | survivors from index 1 shift parity, so `:alternate` re-stripes over the new order — re-run after the structural change; change-only, so unaffected rows don't restyle | PIN (CD-P9-25) |
| C2.24 | bound source of 4 | Remove at index 0 | survivors shift up one, parity flips, `:alternate` re-stripes the post-removal order | PIN (CD-P9-25) |
| C2.25 | bound source of 4 + an app `ListBoxItem:alternate { Background = X }` rule | shown | the odd-row containers render `X`, even rows do not — the striping rule sees the stamped pseudo-class (the end-to-end payoff) | PIN (CD-P9-25) |

**Deferred to later sub-phases (noted, not silently dropped):** a non-vertical/scrolling `ItemsPanel` (P9.3); per-container DataTemplate-by-type
rendering assertions (when a styled item renders through the chain — exercised end-to-end at P9.3).

---

## CD-P9 ledger — SelectionModel (P9.2)

- **CD-P9-8 — pure index-based model, count-agnostic.** `SelectionModel` (design doc §12.6) holds NO element
  references and does NOT track the total item count. `Select`/`Toggle`/`SelectRangeFromAnchor` are the
  non-structural operations; `ItemsInserted`/`ItemsRemoved`/`Reset` are the structural index-fixup hooks the
  generator forwards to. The model is reused verbatim by ListBox (P9.3) and TabControl (P9.5).
- **CD-P9-9 — the model/ListBox boundary on removal.** `ItemsRemoved(index, count)` does pure index math: drop
  selected indices in `[index, index+count)`, shift selected indices `≥ index+count` down by `count`, and if the
  **lead** (`SelectedIndex`) was dropped, relocate it to the nearest *surviving selected* index (tie → smaller),
  or `−1` if no selection survives. The model **never auto-selects a previously-unselected item** — the doc's
  "removing selected items moves selection to the nearest surviving **item**" (which needs the item count) is a
  **ListBox policy** (P9.3), layered on top: when the model's selection empties on a removal, the ListBox re-selects
  the nearest surviving item index.
- **CD-P9-10 — `SelectionMode { Single, Multiple }`; mode caps cardinality.** Single mode caps the selection at one
  index across every operation (`Toggle`/`SelectRangeFromAnchor` collapse to ≤1; range collapses to its endpoint).
  Switching `Multiple → Single` with several selected collapses to the lead. WPF's `Extended` is not a distinct
  mode — its modifier policy (plain-click = `Select`/replace, Ctrl = `Toggle`, Shift = `SelectRangeFromAnchor`) is
  the ListBox input layer's mapping onto these three model primitives (P9.3).
- **CD-P9-11 — `SelectionChanged` fires on membership change only.** The event fires when the set of selected
  *items* changes — `AddedIndexes`/`RemovedIndexes` are a clean set-diff for the non-structural ops (same index
  space). A pure shift (`ItemsInserted`, or `ItemsRemoved` of unselected indices) is **not** a membership change →
  no event, even though indices move (the same items stay selected — WPF parity). An idempotent op (re-`Select` the
  same index) raises nothing.
- **CD-P9-12 — hardening (post-audit).** (a) Toggling the **lead** off in Multiple mode while others remain selected
  promotes the **highest** remaining index to lead (`Max`) — a deliberate, pinned choice (row C3.8b). (b) The
  structural hooks are **lenient on out-of-range input** (`index < 0` or `count ≤ 0` ⇒ no-op), consistent with
  `Select(-1)`; valid input never produces a negative index (`x ≥ end ⇒ x − count ≥ index ≥ 0`). (c)
  `SelectedIndexes` returns a `ReadOnlyCollection<int>`, never the live backing array (no external mutation via a
  downcast). Rows C3.25–C3.28.
- **CD-P9-13 — `ItemsMoved` fixup (P9.3-driven).** The doc §12.6 model lists only Inserted/Removed/Reset, but a
  collection `Move` must carry selection with the item (else a moved selected item desyncs). `ItemsMoved(old, new,
  count)` remaps the selected indices by the same block-move permutation the generator applies (post-removal
  `newIndex`, matching `ObservableCollection.Move`); membership is unchanged so it raises no event. Rows C3.29/C3.30.
- **CD-P9-14 — the selection base is `SelectingItemsControl`, not `Selector` (P9.3).** WPF's `Selector` name would
  collide with the styling `Cursorial.UI.Selector` for any consumer importing both `Cursorial.UI` and
  `Cursorial.UI.Controls` (the common case). Following Avalonia, the selection-aware `ItemsControl` base is named
  `SelectingItemsControl`. `SelectedIndex`/`SelectedItem` are two-way `DirectProperty`s mirroring the model;
  `ListBoxItem`/`TabItem` selection rides `ISelectableContainer` (`SetCurrentValue` so a two-way `IsSelected`
  binding survives — design doc §12.6).
- **CD-P9-15 — the generator trims before `Unrealized` (synchronous selection reconcile).** `RemoveRange` now
  drops the range from its index list *before* firing `Unrealized`, and the event carries the removed container
  instances (`RemovedContainers`) so the host detaches them directly rather than by a now-stale index. The
  CD-P9-3 ordering is preserved (ClearContainer → visual detach → logical remove). The payoff: a
  `SelectingItemsControl` reconciles against a *settled* generator inside the event — `ContainerCount`/
  `ItemFromIndex` are accurate — so `SelectedItem` never goes stale after a removal-before-the-lead, the
  re-target/empty handling is synchronous (no dispatcher hop, no-app-safe), and a re-source/Reset can't spuriously
  re-select index 0 (the whole-generation removal sees `ContainerCount == 0`). `SelectingItemsControl` additionally:
  (a) **clamps an out-of-range `SelectedIndex` to −1** so `SelectedIndex`/`SelectedItem` never disagree; (b) on
  (re)realization, **folds a pre-selected own-container** (`new ListBoxItem { IsSelected = true }`) into the model.
  Rows C4.21–C4.26.

## §C3 — SelectionModel (P9.2) — tests in `Section16_Selection`

`SelectedIndex` = the **lead** (the most-recent primary; always a member of the set when non-empty, `−1` when
empty). `AnchorIndex` = the range anchor (settable; `Select`/`Toggle` set it; `SelectRangeFromAnchor` reads it).
`SelectedIndexes` = the selected set in ascending order. Oracle column: `WPF` = WPF/Avalonia parity; `PIN` = a
CD-P9 decision.

| # | Mode | Setup → Operation | Expected | Oracle |
|---|---|---|---|---|
| C3.1 | Single | `Select(2)` | `{2}`, `SelectedIndex=2`, `AnchorIndex=2` | WPF |
| C3.2 | Single | `Select(1)` then `Select(3)` | `{3}` (replace) — 1 deselected | WPF |
| C3.3 | Single | `Toggle(2)` (not selected) | selects `{2}` | WPF |
| C3.4 | Single | `Select(2)` then `Toggle(2)` | deselects → `{}`, `SelectedIndex=−1` | WPF |
| C3.5 | Single | `Select(1)` then `SelectRangeFromAnchor(4)` | collapses to `{4}` (no range in single) | PIN (CD-P9-10) |
| C3.6 | Multiple | `Select(2)` | replaces → `{2}` (plain-click semantics) | WPF |
| C3.7 | Multiple | `Toggle(1)`, `Toggle(3)` | `{1,3}`, `SelectedIndexes` ascending | WPF |
| C3.8 | Multiple | …then `Toggle(1)` | removes 1 → `{3}` | WPF |
| C3.9 | Multiple | `Select(1)` then `SelectRangeFromAnchor(4)` | `{1,2,3,4}`, `AnchorIndex=1`, `SelectedIndex=4` | WPF |
| C3.10 | Multiple | `Select(4)` then `SelectRangeFromAnchor(1)` | `{1,2,3,4}` (backward range), `SelectedIndex=1` | WPF |
| C3.11 | Multiple | `SelectedIndex=2`; then `SelectedIndex=−1` | sets `{2}`; then clears `{}` | WPF |
| C3.12 | Multiple | `AnchorIndex=0` then `SelectRangeFromAnchor(2)` | `{0,1,2}` (manual anchor honored) | PIN |
| C3.13 | Multiple | `Select(2)` → `ItemsInserted(0,2)` | selected shifts to `{4}`; **no** `SelectionChanged` | PIN (CD-P9-11) |
| C3.14 | Multiple | `Select(2)` → `ItemsInserted(3,1)` / `ItemsInserted(2,1)` | insert-after leaves `{2}`; insert-at shifts to `{3}` | WPF |
| C3.15 | Multiple | `Select(3)` → `ItemsRemoved(0,2)` | shifts to `{1}`; no `SelectionChanged` | PIN (CD-P9-11) |
| C3.16 | Single | `Select(2)` → `ItemsRemoved(2,1)` | the selected was dropped → `{}`, `SelectedIndex=−1` (model goes empty; ListBox re-targets at P9.3) | PIN (CD-P9-9) |
| C3.17 | Multiple | `{1,3,5}`, lead 5 → `ItemsRemoved(1,1)` | drop 1, shift → `{2,4}`, lead 5→4 survives, `SelectedIndex=4` | PIN (CD-P9-9) |
| C3.18 | Multiple | `{1,3,5}`, lead 3 → `ItemsRemoved(3,1)` | the lead (3) is dropped, 5→4 → `{1,4}`; lead relocates to the nearest survivor `4`; `SelectedIndex=4`, `RemovedIndexes=[3]` | PIN (CD-P9-9) |
| C3.19 | Multiple | `Select(1)`,`Toggle(3)` → `Reset()` | `{}`, `SelectedIndex=−1`, `AnchorIndex=−1`; `SelectionChanged` `RemovedIndexes=[1,3]` | PIN (CD-P9-8) |
| C3.20 | Multiple | subscribe → `Toggle(2)` | `SelectionChanged` `AddedIndexes=[2]`, `RemovedIndexes=[]` | WPF |
| C3.21 | Multiple | `Select(2)` then `Select(2)` | idempotent — no second `SelectionChanged` | PIN (CD-P9-11) |
| C3.22 | Single | `Select(1)` then `Toggle(3)` | single caps → `{3}` (1 replaced) | PIN (CD-P9-10) |
| C3.23 | Multiple | `SelectRangeFromAnchor(2)` with `AnchorIndex=−1` | behaves as `Select(2)` → `{2}`, `AnchorIndex=2` | PIN |
| C3.24 | Multiple→Single | `{1,2,3}` then set `Mode=Single` | collapses to the lead `{3}`; fires once `RemovedIndexes=[1,2]` | PIN (CD-P9-10) |
| C3.8b | Multiple | `{1,3}` lead 3 → `Toggle(3)` | removes the lead → `{1}`, lead promotes to `Max`=1 | PIN (CD-P9-12) |
| C3.17b | Multiple | `{1,3}` lead 3 → `ItemsRemoved(1,1)` | a non-lead selected index dropped; lead survives (3→2) → `{2}`; fires `RemovedIndexes=[1]` | PIN (CD-P9-11) |
| C3.18b | Multiple | `{2,3,5}` lead 3 → `ItemsRemoved(3,1)` | lead dropped; survivors `{2,4}` equidistant from 3 → tie resolves to the **smaller**, lead=2 | PIN (CD-P9-9) |
| C3.25 | Multiple | `{2,3,4,5}` anchor 2 lead 5 → `ItemsRemoved(0,1)` | anchor shifts 2→1, lead 5→4 | PIN (CD-P9-12) |
| C3.26 | Multiple | anchor 2 lead 2 → `ItemsRemoved(2,1)` | anchor was in the removed range → re-anchors to the (relocated) lead | PIN (CD-P9-12) |
| C3.27 | Multiple | `Select(2)` (anchor 2) → `ItemsInserted(0,1)` | anchor shifts 2→3 (with the lead) | PIN (CD-P9-12) |
| C3.28 | any | `ItemsRemoved(-1,1)` / `SelectedIndexes` downcast | out-of-range hook is a no-op (no negative leak); `SelectedIndexes` is not an `int[]` | PIN (CD-P9-12) |
| C3.29 | Multiple | `{0,2}` lead 2 → `ItemsMoved(0,3,2)` | selection follows the items (0→3, 2→0), lead→0; no event (a permutation) | PIN (CD-P9-13) |
| C3.30 | Multiple | `{1,4}` lead 4 → `ItemsMoved(4,0,1)` | single-element move relocates the selected index (4→0, 1→2) | PIN (CD-P9-13) |

---

## §C4 — ListBox / Selector / ListBoxItem (P9.3) — tests in `Section17_ListBox`

`SelectingItemsControl : ItemsControl` is the selection-aware base (shared with TabControl at P9.5; named after
Avalonia's — WPF's `Selector` would collide with the styling `Cursorial.UI.Selector`, CD-P9-14): it owns a
`SelectionModel`, projects it onto two-way `SelectedIndex`/`SelectedItem` `DirectProperty`s + the containers'
`IsSelected` (via `ISelectableContainer`/`SetCurrentValue`), forwards the generator's structural changes to the
model, and raises `ItemActivated`. `ListBox : SelectingItemsControl` uses `ListBoxItem` containers; `ListBoxItem.IsSelected`
two-way mirrors the model (`:selected` via `PseudoClassMapping`). The pointer gesture maps Ctrl = toggle, Shift =
range, plain = replace. **P9.3a = selection core (mouse + model wiring + theme); P9.3b = keyboard navigation.**

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C4.1 | `ListBox` bound to 3 strings | shown | containers are `ListBoxItem`s (not `ContentPresenter`) | WPF |
| C4.2 | item that IS a `ListBoxItem` | shown | used as its own container | WPF |
| C4.3 | Single `ListBox` | click item 1 | `SelectedIndex=1`, `SelectedItem`=item, container `IsSelected`+`:selected` | WPF |
| C4.4 | Single, item 1 selected | click item 2 | selection replaces → only item 2 selected | WPF |
| C4.5 | Multiple | Ctrl-click 0, Ctrl-click 2 | both selected (`SelectedItems` = {0,2}) | WPF |
| C4.6 | Multiple | click 1, Shift-click 3 | range {1,2,3} selected | WPF |
| C4.7 | Single | set `SelectedIndex=2` | item 2's container `IsSelected` true; `:selected` on | WPF |
| C4.8 | Single | set `SelectedItem`=item | its index becomes `SelectedIndex`; container selected | WPF |
| C4.9 | Single | click item 2 | `SelectedIndex`/`SelectedItem` `DirectProperty`s update (binding surface) | WPF |
| C4.10 | Single | set `ListBoxItem.IsSelected=true` directly | folds into the model → `SelectedIndex` updates | WPF |
| C4.11 | Single, selected | deselect (select another) | the old container's `:selected` clears | WPF |
| C4.12 | Single, bound `ObservableCollection`, item 1 selected | remove item 1 | selection re-targets to the nearest survivor (`SelectedIndex` stays valid) | PIN (CD-P9-9) |
| C4.13 | Single, last item selected | remove it | selection re-targets to the new last item | PIN (CD-P9-9) |
| C4.14 | `ListBox` | double-click item 2 | `ItemActivated` fires with that item + index | WPF |
| C4.15 | `ListBox`, subscribe | click item 1 | `SelectionChanged` fires | WPF |
| C4.16 | Multiple, {0,1,2} | set `SelectionMode=Single` | collapses to the lead | PIN (CD-P9-10) |
| C4.17 | Single, bound, item 2 selected | insert at 0 | `SelectedIndex` follows the item (→3) | PIN |
| C4.18 | Single `ListBox` shown | click item 1 | the selected row paints the `SelectionBrush` fill (cell assertion) | PIN |
| C4.19 | `ListBox` | inspect | `ListBox.IsTabStop` false; `ListBoxItem.Focusable` true | WPF |
| C4.20 | Multiple, bound, {0,2} | `ObservableCollection.Move(0,2)` | selection follows the moved items | PIN (CD-P9-13) |

**P9.3a theme** (default-theme gallery mockup — `default-theme-gallery-final.html` `.item.*`): the `ListBox` is a
`WellBrush` well; a `ListBoxItem` is a full-width bar — `:selected` = `SelectionBrush` fill keeping the inherited
`TextBrush` ink (milder than pressed's accent pair, adoption-spec line 14), `:pointerover` = `HoverBrush`,
`:disabled` = `MutedBrush`.

**Landed since P9.3a:** (1) **`ScrollViewer` integration (P9.3c)** — the ListBox template wraps the
`ItemsPresenter` in a `ScrollViewer` so a long list scrolls (row C4.27). The presenter had gone unhosted because
the `ScrollContentPresenter`'s content-hosting clears the `ItemsPresenter`'s `TemplatedParent`; the fix
(CD-P9-17) is that `ItemsPresenter` resolves its owner the WPF way — `TemplatedParent`, else the nearest
`ItemsControl` visual ancestor. (2) **keyboard navigation (P9.3b)** — §C5.

**Still deferred:** PageUp/PageDown + Ctrl+A select-all (P9.3b notes). (`:alternate` row-striping landed at the
P9 tail — §C2 rows C2.22–C2.25, stamped by the container generator.)
| C4.21 | Single, bound | remove an UNSELECTED item before the lead | `SelectedItem` stays aligned with `SelectedIndex` (no stale-by-count) | PIN (CD-P9-15) |
| C4.22 | own-container `new ListBoxItem{IsSelected=true}` in the source | shown | folds into the model — `SelectedIndex`=0, `SelectedItem`=the leaf; single-mode click elsewhere clears it | PIN (CD-P9-15) |
| C4.23 | Single | `SelectedIndex = 99` (out of range) | clamps to −1; `SelectedItem` null (consistent) | PIN (CD-P9-15) |
| C4.24 | Multiple, bound, {1,3} | remove a NON-selected item (index 0) | selection shifts to {0,2}, survivors stay selected; **no** `SelectionChanged` | PIN (CD-P9-11) |
| C4.25 | Multiple, bound, {1,3} | remove ONE selected item (index 1) | just it drops; `SelectionChanged` fires once; the other survives selected | WPF |
| C4.26 | Single, item 0 selected | swap `ItemsSource` | selection clears (`SelectedIndex`=−1) — never spuriously re-selects index 0 | PIN (CD-P9-15) |
| C4.27 | `ListBox` of 20 items in a small viewport | scroll the `ScrollViewer` | all 20 realized (eager); the items host through the ScrollViewer and scroll (item 0 slides up out of view) | PIN (CD-P9-17) |

---

## §C5 — ListBox keyboard navigation (P9.3b) — tests in `Section18_ListBoxKeyboard`

The "current" item is the keyboard cursor (the focused container's index), tracked via `ListBoxItem.OnGotFocus`,
distinct from selection. Arrows/Home/End move it and focus the target with `FocusNavigationMethod.Directional`
(⇒ `:focus-visible`, the reverse-video focus-row cue — gallery `.item.rev`); selection follows per mode +
modifiers; Space toggles/selects the current; Enter activates it. **Deferred:** PageUp/PageDown (needs the
viewport row count) and Ctrl+A select-all — noted for a later pass.

| # | Mode | Setup → key | Expected | Oracle |
|---|---|---|---|---|
| C5.1 | Single | focus item 0 → Down | current+selection → 1; item 1 focused | WPF |
| C5.2 | Single | focus item 2 → Up | → 1 | WPF |
| C5.3 | Single | focus last → Down | clamps at last | WPF |
| C5.4 | Single | focus 0 → Up | clamps at 0 | WPF |
| C5.5 | Single | focus 2 → Home | → 0 | WPF |
| C5.6 | Single | focus 0 → End | → last | WPF |
| C5.7 | Single | Down | selection-follows-focus (current == selected) | WPF |
| C5.8 | Multiple | select 1 → Down (plain) | selection replaced → only 2 | WPF |
| C5.9 | Multiple | select 0 → Ctrl+Down | focus moves to 1, selection unchanged (still 0) | WPF |
| C5.10 | Multiple | focus 1, Space (anchor 1) → Shift+Down | range extends → {1,2} | WPF |
| C5.11 | Single | focus 1 → Space | selects current (1) | WPF |
| C5.12 | Multiple | focus 1 → Space | toggles current selected | WPF |
| C5.13 | any | focus 1 → Enter | `ItemActivated` fires for item 1 | WPF |
| C5.14 | Single | keyboard-nav focus vs mouse-click | the keyboard current row renders `:focus-visible` reverse-video, distinct from a mouse-selected row | PIN |
| C5.15 | Single, ObservableCollection | focus item 1, remove item 0, then Down | lands on the contiguous item (no stale-cursor skip) | PIN (CD-P9-16) |
| C5.16 | Single, ObservableCollection | focus item 1, insert at 0, then Down | Down still advances by one (no stall) | PIN (CD-P9-16) |

- **CD-P9-16 — the keyboard cursor is the live focused item, not a cached index (P9.3b, audit).** `ListBox.OnKeyDown`
  resolves the "current" item by walking up from the routed event's `OriginalSource` to the owning container — so it
  is always the container that actually holds focus, correct after any insert/remove/move (the cached `_currentIndex`
  it replaced went stale because nothing reindexed it). When focus is outside the items it falls back to the selected
  index, and to −1 (no anchor) when there is none: the first arrow then enters at item 0 (not index 1), and Enter/Space
  no-op rather than phantom-activating index 0. Rows C5.15/C5.16.
- **CD-P9-17 — `ItemsPresenter` resolves its owner across hosting boundaries (P9.3c ScrollViewer / P9.4 menu).** A
  content host that adopts the presenter as its `Content` — the ListBox's `ScrollContentPresenter`, or a MenuItem's
  submenu `Popup` — **clears the presenter's `TemplatedParent`**, so relying on `TemplatedParent` alone left it
  ownerless (no panel, 0×0). `ItemsPresenter` now finds its owner WPF-style (`ItemsControl.GetItemsOwner`):
  `TemplatedParent` if it is an `ItemsControl`, else walk the **`UIParent` bridge** (`LogicalParent ?? TemplatedParent`;
  a `Popup` overrides it to `PlacementTarget`) up to the first `ItemsControl`. The `UIParent` chain crosses a popup
  **surface boundary** back to the owning control (a submenu's presenter resolves to its `MenuItem`), where a
  visual-tree walk dead-ends at the popup root. Rows C4.27 (scroll), C6.6 (submenu hosts).
- **CD-P9-18 — menus on `Popup` (P9.4a).** `Menu` is a horizontal `ItemsControl` (top-level `MenuItem`s); `MenuItem`
  derives from `HeaderedItemsControl` (header = label, items = submenu) and — since it can't also extend `ButtonBase`
  (single inheritance) — carries its own click/command surface. A **leaf** (no sub-items) invokes on click (raise
  `Click`, toggle `IsChecked` if `IsCheckable`, execute `Command`, dismiss the chain); a **submenu header** toggles
  its submenu `Popup` (`PART_Popup`, manually two-way-synced with `IsSubmenuOpen` so light-dismiss/Esc write back).
  `:highlighted`/`:open` are `DirectProperty`-backed (flipped via `PseudoClasses.Set`); `:checked` via
  `PseudoClassMapping`. Placement: top-level → `Bottom`, nested → `Right`. `Separator` is a non-focusable own-container
  rule. **P9.4a ships the mouse-driven structural core**; keyboard cycling + hover-open (P9.4b), access keys +
  menu-mode + focus scope (P9.4c), `ContextMenu` + `ToolTip` (P9.4d) follow. **Audit fixes:** `MenuItem` mirrors
  ButtonBase's CD25 command coupling — subscribes `Command.CanExecuteChanged` on attach, re-points on Command change,
  unsubscribes on detach, and re-gates on `CommandParameter` change (a live CanExecute flip now re-enables the item,
  row C6.13); and it closes its submenu on detach so an open submenu's `Popup` surface never leaks (row C6.14).

---

## §C6 — Menu / MenuItem / Separator (P9.4a) — tests in `Section19_Menu`

`Menu` (horizontal bar) / `MenuItem : HeaderedItemsControl` / `Separator`, on S4's `Popup`. P9.4a = the
mouse-driven structural core + theme.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| C6.1 | `Menu` with a `MenuItem` | shown | the MenuItem is its own container | WPF |
| C6.2 | submenu with a `Separator` | shown | the Separator is its own container; not focusable | WPF |
| C6.3 | a header MenuItem + a leaf MenuItem | — | `HasItems` true for the header, false for the leaf | WPF |
| C6.4 | leaf with a `Command` | click it | `Click` raised + `Command` executed (with parameter) | WPF |
| C6.5 | `IsCheckable` leaf | click it twice | `IsChecked` toggles; `:checked` mirrors | WPF |
| C6.6 | submenu header | click it | `IsSubmenuOpen` + `:open`; the submenu `Popup` opens + hosts the sub-items | WPF |
| C6.7 | open submenu | click far outside | light-dismiss closes it (`IsSubmenuOpen` false) | WPF |
| C6.8 | MenuItem | hover it | `IsHighlighted` + `:highlighted` | WPF |
| C6.10 | leaf with `Command` (CanExecute false) | click it | disabled (event-transparent) — `Command` never runs | WPF (CD25) |
| C6.12 | top-level header + nested header | open | top-level submenu placement `Bottom`, nested `Right` | PIN (CD-P9-18) |
| C6.13 | leaf, `Command` CanExecute false | raise `CanExecuteChanged` (now true) | the item re-enables (live command coupling, not one-shot) | WPF (CD25) |
| C6.14 | open submenu | detach the menu from the tree | the submenu closes; its `Popup` surface is released (no leak) | PIN (CD-P9-18) |
| C6.15 | 2-level nested submenu | open File then Recent | the nested submenu hosts its grandchild items | WPF |
| C6.16 | submenu header | hover, wait 250 ms | opens on the hover-open `UITimer` (not before the delay) | WPF |
| C6.17 | submenu header | hover, leave before 250 ms | the pending hover-open is cancelled | WPF |
| C6.18 | two bar headers, one open | hover the other | switches immediately (no delay), closes the first | WPF |
| C6.19 | submenu header | hover (arm timer), detach the menu | the parked timer is stopped — never fires post-detach (no popup) | PIN (CD-P9-18b) |
| C6.20 | leaf (no sub-items) | hover, wait 250 ms | arms no timer, opens nothing | WPF |
| C6.21 | open header | re-hover it | stays open, no churn (one popup) | WPF |
| C6.22 | focused bar header | Down | opens its submenu + moves focus to the first sub-item | WPF |
| C6.23 | open submenu | Down / Up | focus moves among sub-items | WPF |
| C6.24 | focused leaf in submenu | Enter | invokes the leaf + dismisses the chain | WPF |
| C6.25 | nested header | Right then Left | Right descends (focus first grandchild); Left closes + re-focuses the parent header | WPF |
| C6.26 | bar headers | Left / Right | move focus between top-level headers | WPF |
| C6.27 | keyboard-opened submenu | Esc | closes (focus is inside the submenu) | WPF |
| C6.28 | focused leaf | Right (unhandled by the leaf) | does NOT hijack the ancestor header (no bar jump, submenu not stranded) | PIN (CD-P9-18c) |
| C6.29 | open submenu | Down | the focused item is `:highlighted`, others not (highlight follows focus) | PIN (CD-P9-18c) |
| C6.30 | submenu `[item, Separator, item]` | Down ×3 | skips the Separator, then wraps | WPF |
| C6.31 | bar header `Header="_File"` | Alt-down then Alt+F **through the dispatcher** | the manager finds the attach-registered folded `f`, opens the submenu | PIN (CD-P9-19) |
| C6.32 | leaf `Header="_Quit"` + `Command` | Alt+Q through the dispatcher | single match → focus + `Invoke` → the command runs once | PIN (CD-P9-19) |
| C6.33 | two items `_Save`/`_Send` (both fold to `s`) | focus `_Save`, Alt+S through the dispatcher | a real multi-match → focus cycles to `_Send`, **neither** command invoked (ND18) | PIN (CD-P9-19) |
| C6.34 | a `Menu` shown | inspect `AccessKeys.MainMenu` | the menu registered itself as the app main menu on attach (`IMainMenu`) | WPF |
| C6.35 | a `Menu` with a focusable top item | `OnEnterMenuMode()` | focus moves to the first top-level item (Alt/F10 entry) | WPF |
| C6.36 | disabled leaf (`Command` CanExecute false) | check eligibility + Alt+Q through the dispatcher | not access-key eligible **and** the manager skips it — the command never runs | WPF |
| C6.37 | attached leaf `Header="_Quit"` + `Command` | reassign `Header="_Exit"`, then Alt+Q / Alt+E | `OnHeaderChanged` re-folds: the stale `q` activates nothing, the new `e` invokes | PIN (CD-P9-19) |
| C6.38 | leaf `_Quit` + `Command` | Alt+Q (runs), `Items.Remove`, Alt+Q again | attach registered the mnemonic; detach unregistered it — the second activation is a no-op | PIN (CD-P9-19) |
| C6.39 | a `Menu` shown then root swapped out | inspect `MainMenu` | detach releases the `IMainMenu` registration (`ReferenceEquals`-guarded clear) — no leak | PIN (CD-P9-19) |
| C6.40 | two menus (last-wins `MainMenu`) | detach the non-owner, then the owner | the non-owner detach leaves `MainMenu` intact; only the owner's detach clears it | PIN (CD-P9-19) |
| C6.41 | checkable leaf `Header="_Wrap"` | Alt+W through the dispatcher | the access-key activation toggles `IsChecked` (Invoke's `SetCurrentValue`) + `:checked` | WPF |
| C6.42 | settled leaf | reassign `Header` | `IsMeasureValid` flips false — the `HeaderProperty` `OverrideMetadata` preserved the inherited `AffectsMeasure` | PIN (CD-P9-19) |

**Landed in P9.4b:** 250 ms hover-open + immediate sibling-switch when the menu is active (rows C6.16–C6.21;
**CD-P9-18c (P9.4c) — keyboard navigation + highlight-follows-focus.** `MenuItem` is focusable; highlight =
focused OR pointer-over (`RefreshHighlight` from the focus + hover hooks). Bar (horizontal): Left/Right cycle
headers, Down opens + enters the submenu. Submenu (vertical): Up/Down move focus (wrap, skip non-focusable
Separators), Right descends into a nested header, Left closes + re-focuses the parent, Enter invokes/opens. A
keyboard open moves focus into the submenu, so Esc routes back through the `Popup` and closes it. **`OnKeyDown`
gates on `IsFocused`** — it is a class handler on the whole bubble route, so without the gate a key a focused
sub-item leaves unhandled would bubble to an ancestor header (via the popup→`PlacementTarget` bridge) and be
re-interpreted (rows C6.22–C6.30). **Deferred:** WPF-parity Right/Left-on-a-submenu-leaf jumping to the adjacent
bar menu (a no-op today); a `Menu`-level helper lands with the SubTree-capture session model.

**CD-P9-18b** — each submenu-header MenuItem owns a `UITimer` armed on hover-enter, cancelled on leave/detach;
`OpenSubmenu` closes sibling submenus via the owner generator; a sibling already open ⇒ open immediately).

**Landed in P9.4c:** keyboard navigation (Left/Right/Down/Up/Enter) + Esc-close + highlight-follows-focus
(CD-P9-18c, rows C6.22–C6.30).

**CD-P9-19 (P9.4c-2) — access-key folding + menu-mode entry.** `MenuItem : HeaderedItemsControl, IAccessKeyTarget`
(it cannot also inherit `ButtonBase` — single inheritance — so it carries its own `Click`/`Command` coupling,
mirroring `ButtonBase`). The static ctor does `HeaderProperty.OverrideMetadata<MenuItem>(… ParsesAccessKeyLiterals
= true, Changed: OnHeaderChanged)`: the loader/`GetAccessText()` folds an `AccessText`-typed `Header` literal
(`"_Quit"`→mnemonic `q`), and the metadata merge **preserves** the inherited `AffectsMeasure(HeaderProperty)`
(effects live in a separate per-type registry, independent of `PropertyMetadata` — row C6.42 pins this). The item
registers its folded mnemonic with the `AccessKeyManager` on attach (`OnAttachedToTree→RegisterAccessKey`),
re-registers on a live `Header` change (`OnHeaderChanged`, drop-old-then-add-new), and unregisters on detach. Single
match → focus + `OnAccessKey` (HasItems ⇒ open submenu, else Invoke); multi-match → the manager cycles focus only,
never invoking (ND18). `Menu : ItemsControl, IMainMenu` registers as `AccessKeys.MainMenu` on attach (last-wins) and
clears it on detach **only when it is still the owner** (`ReferenceEquals` guard); `OnEnterMenuMode` (Alt-tap/F10)
focuses the first top-level item. **Tests drive end-to-end through the real `AccessKeyManager`** (Alt-down +
Alt+`<char>` via `InputDispatcher.ProcessEvent`, mirroring `InputMatrix/Section12`) so the whole registration spine
is exercised — not the `IAccessKeyTarget.OnAccessKey` reaction body in isolation (rows C6.31–C6.42; an audit found
the first-cut C6.31–C6.33 were false-green against a no-op registration and they were rewritten).

**Still deferred to P9.4c-2/d (noted, not silently dropped):** re-click-on-header toggle-close + sibling-switch by
*click* + Right/Left-on-a-submenu-leaf jumping to the adjacent bar menu (all need the SubTree-capture menu-session
model — the press is otherwise swallowed by light-dismiss); sub-item invoke + nested-header hover *via a
popup-surface click* (the test harness needs screen-absolute popup coords — the mechanism is covered by the
keyboard + direct-state tests); the check-glyph + submenu-▸ glyph columns. **P9.4 menus are complete**
(§C6 menu/access-keys, §C7 ContextMenu, §C8 ToolTip/ToolTipService).

**Landed in P9.4c-2:** access-key folding + registration lifecycle (attach/Header-change/detach) + menu-mode entry
(Alt/F10 + `IMainMenu`), driven end-to-end through the `AccessKeyManager` (CD-P9-19, rows C6.31–C6.42).

## §C7 — ContextMenu (P9.4d) — tests in `Section20_ContextMenu`

`ContextMenu : ItemsControl` — a popup-rooted vertical menu shown on right-click / the `Key.Menu` context key.
Reuses `MenuItem` (containers, invoke, submenus, access keys — §C6). Tests drive the **real `InputDispatcher`**
router default + the `Popup` light-dismiss.

| # | Setup | Action | Expectation | Source |
|---|-------|--------|-------------|--------|
| C7.1 | `ContextMenu` with a `MenuItem` + a data item | open | the MenuItem is its own container; the data item is wrapped in a `MenuItem` | WPF |
| C7.2 | element | `SetMenu`/`GetMenu` | the attached `ContextMenu.Menu` property round-trips | WPF |
| C7.3 | element carrying `ContextMenu.Menu` | right-click it (through the dispatcher) | the menu opens at the pointer (`IsOpen`, one popup surface) — the router default | PIN (CD-P9-20) |
| C7.4 | element with no `ContextMenu.Menu` | right-click it | nothing opens (the parent-chain walk finds no menu) | PIN (CD-P9-20) |
| C7.5 | focused element carrying a menu | `Key.Menu` | the focused element's context menu opens (the keyboard context key) | PIN (CD-P9-20) |
| C7.6 | open menu, two items | open, then Down | opening focuses the first item; Down moves focus to the next (keyboard nav is live) | WPF |
| C7.7 | open menu, leaf + `Command` | focus leaf, Enter | the command runs **and** the menu dismisses (a leaf invoke closes the whole menu) + popup released | PIN (CD-P9-20) |
| C7.8 | open menu | Escape | the menu closes (`Popup.CloseOnEscape`) | WPF |
| C7.9 | open menu | click far outside | light-dismiss closes it | WPF |
| C7.10 | open menu | `Open` again with a new offset | one popup surface (relocate, not stack), the new offset is applied, **and a fresh surface is placed** (re-open relocates — CD-P9-20 audit fix) | PIN (CD-P9-20) |
| C7.11 | menu with a submenu header | open, open the nested header | the nested submenu hosts its grandchild items (two popup surfaces) | WPF |
| C7.12 | open menu | `Close()` | the menu closes; the popup surface is released | WPF |
| C7.13 | child element inside an element carrying `ContextMenu.Menu` | right-click the child | the router walks the `UIParent` chain to the ancestor and opens its menu | PIN (CD-P9-20) |
| C7.14 | element carrying a menu, a `PreviewMouseUp` handler sets `Handled` | right-click | the menu does **not** open — the router default respects the handled mark | PIN (CD-P9-20) |
| C7.15 | right-click-opened (Pointer placement) menu | click far outside | light-dismiss closes it (the right-click path, not just programmatic Bottom open) | WPF |
| C7.16 | focused element with **no** `ContextMenu.Menu` | `Key.Menu` | nothing opens (the key-path parent-chain walk finds no menu) | PIN (CD-P9-20) |

**CD-P9-20 (P9.4d) — ContextMenu hosting + the router default.** A `ContextMenu` owns an internal `Popup` whose
`Child` is the menu itself, so its items realize in the popup surface and inherit DataContext/resources through the
placement target (the standalone-popup inheritance bridge, §8.4). `Open(target, position)` places at the pointer
(`PlacementMode.Pointer`) when `position` is null — the right-click case — else below the target offset by
`position`. The **router default** lives in `InputDispatcher` (S3 owns the trigger, §12.7): an uncaptured
right-button **release** over an element carrying `ContextMenu.Menu` (walked up the styling/event parent chain) and
the focused element's menu on `Key.Menu` both open it; a routed handler that already consumed the event wins. A leaf
invoke dismisses the whole menu via `MenuItem.CloseMenuChain`, which now terminates at an owning `ContextMenu`
(it is not a `MenuItem`). `CursorialTheme.BuiltIn` keys an occluding `Theme.ContextMenu` panel (`PanelBrush` +
`BorderPen`) hosting the `PART_ItemsHost` `ItemsPresenter`. **Deferred:** clicking a popup-surface item by screen
coordinate (the harness limitation noted in §C6 — covered here by keyboard + the direct `Open`/`IsSubmenuOpen` API).

**CD-P9-20 audit (P9.4d-1 follow-up).** An adversarial audit (8 confirmed findings) caught a real HIGH correctness
bug: **re-opening an already-open `ContextMenu` did not relocate the popup.** A `Popup` re-places its surface only
on the closed→open edge (`OpenCore`→`PlacePopup`), so `SetCurrentValue(IsOpenProperty, true)` on an already-open
popup is a no-op and stranded the menu at the old position; the offset/placement property writes had no re-placement
trigger. Fixed in `ContextMenu.Open`: close first when already open, then re-open at the new placement (a fresh
surface is placed). The audit also caught the first-cut C7.3/C7.9/C7.10 as false-greens (they asserted only
`IsOpen`/popup-count, never the placement mode or offsets) and coverage gaps (the `UIParent` ancestor walk, the
`Handled`-suppresses-menu branch, the right-click-then-dismiss path, the key-path no-menu walk). Rows C7.3/C7.10 were
strengthened (assert `PlacementMode.Pointer` / the applied offset / a fresh `PopupSurface` — the last
mutation-verified to fail without the relocation fix) and rows C7.13–C7.16 added.

## §C8 — ToolTip / ToolTipService (P9.4d) — tests in `Section21_ToolTip`

`ToolTip : ContentControl` (never focusable, never hit-tested) shown by the process-wide `ToolTipService` in a
hit-test-transparent `Popup`, hover-driven on S3's `InputDispatcher.HoverChanged` hook + an S5 `UITimer`.

| # | Setup | Action | Expectation | Source |
|---|-------|--------|-------------|--------|
| C8.1 | element with `ToolTipService.Tip` | hover, wait the delay | the tooltip shows after `InitialDelay` (not before) | WPF |
| C8.2 | tip-bearing element | hover, leave before the delay | the pending open is cancelled (no show) | WPF |
| C8.3 | shown tooltip | leave the element | the tooltip closes | WPF |
| C8.4 | shown tooltip | any mouse button press | closes (`DismissTransients`) | PIN (CD-P9-21) |
| C8.5 | shown tooltip | a non-modifier key press (Enter) | closes | PIN (CD-P9-21) |
| C8.6 | shown tooltip | a bare modifier key press (Shift) | stays shown — a standalone modifier never dismisses | PIN (CD-P9-21) |
| C8.7 | tooltip shown then closed | re-hover within 100 ms | shows immediately (quick-show, delay 0) | WPF |
| C8.8 | shown tooltip | inspect the popup surface | it is hit-test-transparent (never steals hover/clicks) | PIN (CD-P9-21) |
| C8.9 | shown tooltip | terminal focus-out | closes (must not outlive the focused terminal) | PIN (CD-P9-21) |
| C8.10 | element | get/set `Tip`/`InitialDelay`/`ShowOnFocus` | round-trip; defaults are `null` / 500 ms / `null` (auto) | WPF |
| C8.11 | a `ToolTip` | inspect | not focusable, not hit-test-visible | spec §12.7 |
| C8.12 | element with a 100 ms `InitialDelay` | hover, wait 120 ms | shows (the custom delay is honored, well short of 500 ms) | WPF |
| C8.13 | tip-bearing element, timer pending | clear the `Tip` (set null) | the tooltip never shows (`Show` bails on a null tip — CD-P9-21 audit fix) | PIN (CD-P9-21) |
| C8.14 | nested tip elements (outer + inner) | hover the inner | the innermost tip wins (the popup anchors to the inner element) | PIN (CD-P9-21) |
| C8.15 | tip element with a no-tip child | hover bare, partway, then move into the child | the open timer is NOT reset (shows at the original deadline) | PIN (CD-P9-21) |
| C8.16 | tooltip shown then closed | re-hover **after** the 100 ms quick-show window | NOT immediate — the full delay re-applies (quick-show window expires) | PIN (CD-P9-21) |
| C8.17 | two tip-bearing elements | hover one | exactly one popup — one controller per app (`Ensure` idempotent) | PIN (CD-P9-21) |

**CD-P9-21 (P9.4d) — ToolTipService on the hover stream.** One `ToolTipController` per `UIApplication` (a
`ConditionalWeakTable`, created lazily when the first `Tip` is set) subscribes the dispatcher's `HoverChanged`
hook and owns one reusable hit-test-transparent `Popup`. Entering a `Tip`-bearing element arms a `UITimer`
(`InitialDelay`, default 500 ms); intra-element moves don't change the hover chain so the timer is never reset by
them; the innermost tip in the added chain wins. The tooltip closes on hover-leave (the element appears in the
removed chain — covers detach, which truncates the chain), on any button press / non-modifier key press (the new
internal `InputDispatcher.DismissTransients` signal — a bare modifier never raises it, `IsStandaloneModifier`), and
on terminal focus-out (`TerminalFocusChanged`). Quick-show: a close arms a 100 ms window during which the next
hover shows with no delay. `Popup.IsHitTestTransparent` (new) flows to the surface in `WindowManager.OpenPopup`.
`CursorialTheme.BuiltIn` keys an occluding `Theme.ToolTip` panel capped at `MaxWidth = 40`. **Deferred:**
`ShowOnFocus` is declared (`bool?`, null = auto from `!MouseCapabilities.Motion`) but the focus-triggered show
itself is a recorded deferral — the hover path is the v1 behavior (no public focus-changed hook to ride yet).

**CD-P9-21 audit (P9.4d-2 follow-up).** An adversarial audit (6 confirmed findings) caught a real MEDIUM bug:
**clearing the `Tip` while the open timer was pending still showed an empty tooltip** — `Show` now bails when the
tip is null (row C8.13). The other five were coverage gaps in correct-but-untested behavior (each mutation-verified):
the innermost-tip selection (C8.14), the intra-element-move no-reset (C8.15), the quick-show-window expiry (C8.16),
and `Ensure` idempotency (C8.17) are now pinned. The `Show()` re-target staleness guard
(`!ReferenceEquals(_target, owner)`) is **defensive and unreachable** in the single-threaded frame model
(an `Arm` always `Reset`s — stopping the prior open timer — so a stale pending callback cannot fire); left
in place as a guard against future re-entrancy, not given an artificial test.

## §C9 — TabControl / TabItem (P9.5) — tests in `Section22_TabControl`

`TabControl : SelectingItemsControl` (single mode) with `TabItem : HeaderedContentControl, ISelectableContainer`
containers. The themed template hosts the tab strip (`PART_TabStrip`, the headers) over a content host
(`PART_ContentHost`, the selected tab's `SelectedContent`).

| # | Setup | Action | Expectation | Source |
|---|-------|--------|-------------|--------|
| C9.1 | `TabControl` with a `TabItem` + a data item | shown | the TabItem is its own container; the data item is wrapped | WPF |
| C9.2 | three tabs | shown | the first tab auto-selects (`SelectedIndex` 0, `IsSelected`, `:selected`) | PIN (CD-P9-22) |
| C9.3 | three tabs | set `SelectedIndex` | `SelectedContent` shows the selected tab's `Content` | PIN (CD-P9-22) |
| C9.4 | three tabs | click a tab header | selects it (`:selected`), deselects the others (single mode), `SelectedContent` follows | WPF |
| C9.5 | focused tab | Left / Right | selection moves (selection-follows-focus) | WPF |
| C9.6 | focused tab | Home / End | selection jumps to the ends | WPF |
| C9.7 | focused tab | Ctrl+PageUp / Ctrl+PageDown | selection cycles (wrap) — the universal chord | WPF |
| C9.8 | three tabs | set `SelectedIndex` | `SelectedItem` is the selected container | WPF |
| C9.9 | three tabs | set a `TabItem.IsSelected = true` | folds into the single-selection model — the old selection clears | WPF |
| C9.10 | three tabs | set `SelectedIndex` | exactly one tab carries `:selected` | WPF |
| C9.11 | selected middle tab | Ctrl+Left / Ctrl+Right / Ctrl+Home / Ctrl+End | selection unchanged — Ctrl is reserved for the cycle (CD-P9-22 audit) | PIN (CD-P9-22) |
| C9.12 | selected middle tab | PageUp / PageDown **without** Ctrl | selection unchanged — the cycle is Ctrl-gated | PIN (CD-P9-22) |
| C9.13 | tabs with **UIElement** content | switch A→B→A | the content switches cleanly (single visual parent — no double-hosting) | PIN (CD-P9-22) |
| C9.14 | **empty** TabControl | Ctrl+PageDown / arrow | safe no-op (the `count==0` guard; no `DivideByZero` on the cycle `% count`) | PIN (CD-P9-22) |
| C9.15 | selected tab | click it again | stays selected (idempotent) | WPF |

**CD-P9-22 (P9.5) — TabControl on the selection base.** `TabControl` reuses `SelectingItemsControl` (single mode)
unchanged — `SelectedIndex`/`SelectedItem`/`SelectionModel`/the `ISelectableContainer` mirror all come from P9.3.
It adds: a read-only `SelectedContent` (the selected tab's `Content`) the content-host `ContentPresenter` shows via
`TemplateBinding`, updated on `SelectionChanged`; auto-selection of the first tab on container realization
(`ItemContainerGenerator.ContainersChanged`, WPF parity — a tab control always shows a tab); horizontal-strip
keyboard nav (Left/Right/Home/End move + select; Ctrl+PageUp/PageDown cycle — Ctrl+Tab deferred as wire-ambiguous).
A `TabItem`'s **template renders only its `Header`** (the strip label); its `Content` is presented *only* by the
content host, so a UIElement content is never double-hosted. `CursorialTheme.BuiltIn` keys `Theme.TabControl`
(DockPanel: strip top + bordered body) and `Theme.TabItem` (fill-bounded header, `:selected` = `SelectionBrush`).
**Deferred:** `TabItem` access-key folding (Alt+mnemonic selects the tab — the `IAccessKeyTarget` + Header-fold
treatment like `MenuItem`); selection by click/keyboard is the v1 behavior.

**CD-P9-22 audit (P9.5 follow-up).** An adversarial audit (5 confirmed findings — **all coverage gaps; no correctness
bugs**, and the "no double-hosting" claim was verified sound by mutation) added the negative-case + edge tests the
first cut missed (each mutation-verified): the `!ctrl` arrow/Home/End guards (C9.11), the Ctrl-gated PageUp/PageDown
(C9.12), **UIElement** (non-string) content switching without double-hosting (C9.13), the empty-TabControl `count==0`
guard against a `DivideByZero` on the cycle (C9.14), and the idempotent re-click (C9.15).

## §C10 — ProgressBar (P9.7) — tests in `Section23_ProgressBar`

`ProgressBar : Control` paints itself in `Render` (no template): the track is `Background`, the determinate fill
is `Fill` across `round((Value−Min)/(Max−Min) · width)` cells, the indeterminate marquee is a ~⅓-width block at
`round(IndeterminatePhase · (width − blockWidth))` (the phase is the normalized animation target).

| # | Setup | Action | Expectation | Source |
|---|-------|--------|-------------|--------|
| C10.1 | `Max=100` | `Value=50` / `25` | `FilledFraction` = 0.5 / 0.25 | WPF |
| C10.2 | `[0,100]` | `Value=150` / `−10` | coerced to 100 / 0 | WPF |
| C10.3 | `[0,100]` | `Value=Min` / `Max` | `FilledFraction` 0 / 1 | WPF |
| C10.4 | `Min==Max` | inspect | `FilledFraction` 0 (no divide-by-zero) | PIN (CD-P9-23) |
| C10.5 | `[10,20]` | `Value=15` | `FilledFraction` 0.5 (offset range) | WPF |
| C10.6 | `IsIndeterminate` true→false | inspect | `:indeterminate` flips | PIN (CD-P9-23) |
| C10.7 | 30% of a 10-wide bar | render | cells 0–2 are filled, cell 5 is track (the fill differs from the track) | PIN (CD-P9-23) |
| C10.8 | full / empty bar | render | uniformly filled / uniformly track | WPF |
| C10.9 | indeterminate, 12-wide | render | a ~width/3 (=4) sweep block of fill cells is visible (distinct from the track) | PIN (CD-P9-23) |
| C10.10 | indeterminate, attached | advance time / `IsIndeterminate=false` | the marquee perpetually advances `IndeterminatePhase` in [0,1]; turning it off stops it + resurfaces the phase to 0 (P2A) | PIN (CD-P9-23a) |

**CD-P9-23 (P9.7) — ProgressBar paints in `Render`.** A terminal progress bar is a row of solid cells, so
`ProgressBar` overrides `Render` and `FillRectangle`s the track (`Background`) then the determinate fill (`Fill`)
across `round(FilledFraction · width)` cells — no template, no per-cell child layout. `Value` is coerced into
`[Minimum, Maximum]`; `FilledFraction` clamps to `[0,1]` and returns 0 on an empty range (no divide-by-zero).
`IsIndeterminate` → `:indeterminate` via `PseudoClassMapping`; `CursorialTheme.BuiltIn` keys `Theme.ProgressBar`
(`WellBrush` track, `GreenBrush` determinate fill, `AccentBrush` on `:indeterminate` — the gallery mockup). All
geometry/paint properties are `AffectsRender`. **Deferred:** the animated indeterminate sweep as a
storyboard-ignited, composite-only persistent highlight layer (design doc §3.9 resolution 1 — `IndeterminateOffset`
as an `AffectsComposite` target); v1 draws the offset block in `Render` (a consumer can animate the offset).

## §C11 — TextBox (P9.6) — tests in `Section24_TextBox`

`TextBox : Control` is a single-line editable field. The caret is the **real terminal cursor**
(`CursorShape.BlinkingBar`) published by `TextPresenter` (the `PART_TextPresenter` part) through S1's
`ITerminalCaretService` while the field holds physical focus. Caret offsets pin to grapheme-cluster boundaries
and all horizontal math is in display columns (`GraphemeWidth.ClusterWidth` — a wide cluster is two columns).
`Text` is two-way by default with per-change source push; Copy/Cut route through the OSC 52 clipboard service.

| # | Setup | Action | Expectation | Source |
|---|-------|--------|-------------|--------|
| C11.1 | `Text` binding `Mode=Default` | inspect | `EffectiveMode` is `TwoWay` (BindsTwoWayByDefault) | PIN (CD-P9-24) |
| C11.1b | two-way `Text` binding to a VM | type "ab" | the source updates per keystroke (per-change push, §3.9) | PIN (CD-P9-24) |
| C11.2 | `"ac"`, caret 1 | type "b" | `"abc"`, caret 2 | WPF |
| C11.3 | `"a😀b"` | set caret mid-emoji / Right from 1 | pins to the cluster start (1) / steps over to 3 | PIN (CD-P9-24) |
| C11.4 | `"hello"` | Left / Home / End | caret 4 / 0 / 5 | WPF |
| C11.5 | `"one two three"` | Ctrl+Right ×2 / Ctrl+Left | 3 / 7 / 4 (word, whitespace-delimited) | WPF |
| C11.6 | `"hello"`, caret 0 | Shift+Right ×2 | selection `[0,2)` = "he" | WPF |
| C11.7 | `"abc"`, caret 2 | Backspace / Delete | `"ac"` caret 1 / `"a"` | WPF |
| C11.8 | `"hello"`, sel `[1,4)` | Backspace | `"ho"`, caret 1, no selection | WPF |
| C11.9 | `"hello"` | Ctrl+A | whole text selected | WPF |
| C11.10 | `"abc"`, `MaxLength=4`, caret 3 | type "xy" | `"abcx"` (capped) | WPF |
| C11.11 | `"abc"` read-only | type / Backspace / Right | text unchanged; caret moves (navigable) | WPF |
| C11.12 | `"hello"`, sel `[1,4)` | get / set `SelectedText="i"` | "ell" / `"hio"` | WPF |
| C11.13 | empty | paste `"a\r\nb\tc"` | `"a b c"` (newlines/tabs flattened) | PIN (CD-P9-24) |
| C11.14 | focused field | inspect / blur | terminal caret visible (`BlinkingBar`) / cleared | PIN (CD-P9-24) |
| C11.15 | focused field | detach the tree | caret publication cleared (no stale cursor) | PIN (CD-P9-24) |
| C11.16 | `"one two"` | click 't' / double-click | caret to the cluster / "two" selected | WPF |
| C11.17 | 40 `x` in a 12-wide field | caret home / end | scroll 0 / scrolled so the caret is visible | PIN (CD-P9-24) |
| C11.18 | — | inspect; set `Text`; set `IsReadOnly` | `:empty` flips with text; `:readonly` mirrors `IsReadOnly` | PIN (CD-P9-24) |
| C11.19 | `Placeholder="name"` | empty → set `Text` | placeholder renders, then text replaces it | WPF |
| C11.20 | `"hello"` | select `[0,2)` | the selected cell's style differs from unselected | PIN (CD-P9-24) |
| C11.21 | clipboard-write terminal, sel | Ctrl+C | OSC 52 write emitted | PIN (CD-P9-24) |
| C11.22 | no-clipboard terminal, sel | Ctrl+C | no OSC 52 (silent no-op; `CanWrite` false) | PIN (CD-P9-24) |
| C11.23 | clipboard-write terminal, sel `[0,2)` | Ctrl+X | text `"llo"` + OSC 52 emitted | WPF |
| C11.24 | `"hello"`, no selection | Ctrl+C | not consumed — bubbles (an app may bind quit) | PIN (CD-P9-24) |
| C11.16b | `"hello"` | left-down at 1, drag to 4, release | selection "ell" extends from the anchor and persists | WPF |
| C11.25 | sel / no-sel | Shift+Delete | cuts the selection / is not consumed → bubbles (like Ctrl+X) | PIN (CD-P9-24 audit) |
| C11.26 | scrolled field | collapse the viewport to 0 width | scroll offset resets to 0 (no stale/negative caret column) | PIN (CD-P9-24 audit) |

**CD-P9-24 (P9.6) — TextBox: terminal-caret editor, grapheme-pinned, per-change two-way.** `TextBox : Control`
with a required `PART_TextPresenter` (`TextPresenter : UIElement`). **Caret = the real terminal cursor**
(§3.9-TextBox / §5.9): the presenter publishes element-local `(caretColumn − scrollOffset, 0)` with
`CursorShape.BlinkingBar` through `UIApplication.Current.CaretService` whenever the owning `TextBox` `IsFocused`
(only the active window's editor is the app's focused element, so `IsFocused` subsumes the window-active gate),
and `Clear`s on detach (belt-and-braces with the service's stale-owner drop). The presenter is a **clipped render
boundary** (`ClipToBounds = true`): a wide cluster straddling either viewport edge clips per cell (no bleed into
the field chrome), the scroll-offset change re-rasters only the one-row presenter zone — strictly better than the
spec's "window-scene re-raster" simplification — and the zone-clip gate auto-hides a scrolled-out caret.
**Grapheme model:** caret/selection offsets pin to cluster boundaries via `GraphemeLayout` (`StringInfo`
enumeration); horizontal math is in display columns (`GraphemeWidth.ClusterWidth`, a wide cluster = 2 columns).
**`Text`** is a two-way-by-default `StyledProperty<string>` (`BindsTwoWayByDefault`) that pushes to its source
**per change** (the pinned default — §3.9; the `UpdateSourceTrigger.PropertyChanged` resolution makes
validation-reactive UI react per keystroke), `AffectsMeasure`, raising the bubbling `TextChanged`.
**`CaretIndex`/`SelectionStart`/`SelectionLength`/`SelectedText`** are plain CLR state over `(_caretIndex active
end, _selectionAnchor fixed end)` — not styleable/bindable in v1; mutating them re-anchors scroll + re-publishes
the caret + invalidates the presenter visual. **Pseudo-classes:** `:empty` is driven directly via
`PseudoClasses.Set` (constructor + `OnTextChanged`) — `PseudoClassMapping` applies only on a value *change*, never
the initial default, and a never-edited empty field must read as `:empty`; `:readonly` via `PseudoClassMapping`.
**Keyboard:** printable input arrives at `OnTextInput` (control keys at `OnKeyDown`); Left/Right cluster,
Ctrl+Left/Right word (whitespace-delimited), Home/End, Shift extends; Backspace/Delete delete the selection or one
cluster, Ctrl variants delete a word; Ctrl+A select-all; **Ctrl+C/Ctrl+Insert copy is NOT consumed when there is no
selection** (it bubbles — an app may bind quit); Ctrl+X/Shift+Delete cut; Ctrl+V/Shift+Insert paste is a consumed
**no-op in v1** (OSC 52 read is unnegotiated — the terminal's own paste, `TextInput{FromPaste}`, is the inbound
path); **Enter/Escape are never handled** so `IsDefault`/`IsCancel` buttons work (spec §13). Read-only swallows
typing and blocks edits while staying navigable + copyable. Paste flattens `\r\n|\n|\r`/`\t` to spaces; typed
control chars are filtered; `MaxLength` trims at a cluster boundary (never splits a surrogate). **Mouse:** left-down
focuses + places the caret + captures for drag-select; double-click selects the word, triple selects all.
**Clipboard:** `IClipboardService` (`UIApplication.Clipboard`, punch 30) wraps `ClipboardWriter.WriteSet` over
`QueueControlSequence` (OSC 52, OSC-class only) gated on `Capabilities.Output.Protocol.ClipboardWrite`;
`CanRead` is false (no family negotiates OSC 52 read) so `TryGetTextAsync` returns null. **Theme:** `Theme.TextBox`
is the cell-faithful spine — resting `SurfaceBrush`/`TextBrush`, `:pointerover` `HoverBrush`, **`:focus` the recessed
`WellBrush` (not reverse-video — text focus is the well + the blinking-bar caret, adoption-spec §1/§7; this
supersedes the older spec-controls.md `Pens.Light`/`Pens.Heavy` border sketch)**, `:disabled` the disabled pair, a
themed `SelectionBrush`, and `MinWidth=12` so an unconstrained empty field is usable; the presenter paints the
selection (`SelectionBrush`, or `TextAttributes.Inverse` on the NoColor tier) and the placeholder (`MutedBrush` +
`Faint`). **Deferred (spec §15):** undo/redo, multi-line, PasswordBox, OSC 52 read, drag-selection auto-scroll, an
`UpdateSourceTrigger` knob.

**CD-P9-24 audit (P9.6 follow-up).** An adversarial audit (3 skeptic lenses, 27 candidate findings, 6 confirmed
after refutation — the other 21 were code-correct test-coverage gaps) found **two real bugs** the green tests
missed, each fixed and mutation-verified: (1) **`TextPresenter.RefreshCaretAndScroll` left a stale `_scrollOffset`
when the viewport collapsed to 0** (the `viewport > 0` guard skipped re-anchoring), so the published caret column
could go negative — now an `else` resets the offset (the zone-clip already hid it, but the element-local coordinate
must stay valid); row C11.26. (2) **`Shift+Delete` (cut) didn't check `Cut()`'s return value**, so with no
selection (or read-only) it consumed the key instead of bubbling — inconsistent with `Ctrl+X`/`Ctrl+C`; now it
returns unhandled when there is nothing to cut; row C11.25. The audit also surfaced genuine coverage gaps over
correct code — added: triple-click select-all (C11.16) and mouse drag-extend (C11.16b). The refuted findings
(verified code-correct by tracing) included surrogate-pair backspace/MaxLength trimming, paste-into-selection,
control-char filtering, the `:empty`/`SelectionStart`-preserves-length semantics, Enter/Escape bubbling, and the
NoColor-tier `Inverse` selection path.

---

## §C12 — ComboBox (P2B, post-P9)

A single-selection drop-down (design doc §12.11 — the ListBox-in-Popup recipe). `ComboBox : SelectingItemsControl`
hosts a face (a `ContentPresenter` bound `{TemplateBinding SelectedItem}` plus a `v` drop glyph) and a
`PART_Popup` whose items host is the control's own `ItemsPresenter`. Containers are `ComboBoxItem`s (a
`ListBoxItem` twin — `ISelectableContainer`, `:selected`/`:pointerover`/`:focus-visible`/`:disabled` looks);
selection rides the `SelectingItemsControl` base (`SelectionMode.Single`). The face is the tab stop
(`Focusable=true`); the drop-down items live on the Popup's own surface. Tests:
`Cursorial.UI.Tests/ControlMatrix/Section25_ComboBox.cs`.

| Row | Setup | Action | Expected | Source |
|-----|-------|--------|----------|--------|
| C12.1 | items {alpha,beta,gamma} | set `SelectedIndex=1`; set `SelectedItem="gamma"` | `SelectedItem=="beta"`; `SelectedIndex==2` (face presents it, no drop-down needed) | PIN (CD-P2B-1) |
| C12.2 | — | set `IsDropDownOpen=true` | Popup opens; the items generate `ComboBoxItem` containers (`ContainerCount==3`); `false` closes | PIN (CD-P2B-1) |
| C12.3 | — | left-click the face; click again | first click opens (`:open`), second closes — the anchor owns the toggle (no dismiss-then-reopen race) | PIN (CD-P2B-1) |
| C12.4 | focused | Down (closed→open); Down, Down (highlight); Enter | open; selection follows highlight (index 0→1); Enter commits `"beta"` + closes | PIN (CD-P2B-1) |
| C12.5 | open | Escape | closes without changing the selection | PIN (CD-P2B-1) |
| C12.6 | open | click a drop-down item | commits a selection (`SelectedItem` non-null) + closes (exact item is surface-placement-dependent; C12.4 pins precise-item commit) | PIN (CD-P2B-1) |

**CD-P2B-1 — ComboBox: ListBox-in-Popup, single-select, anchor-owned toggle.** The face is a `ContentPresenter`
on `{TemplateBinding SelectedItem}`; `IsDropDownOpen` is a `DirectProperty` two-way with the templated `Popup`
(`SetDropDownOpen` → `SetAndRaise` + imperative `PseudoClasses.Set(":open", …)` since it's DirectProperty-backed,
cf. `MenuItem`; drives `Popup.IsOpen`; restores face focus on close). Open while closed on
Down/Up/Enter/F4/Space; while open Down/Up/Home/End move the selection (selection-follows-highlight — the face
updates live and the container takes `:focus-visible`), Enter commits the highlight + closes, Escape/light-dismiss
close unchanged. The face-click toggle uses the new **`Popup.KeepOpenOnAnchorPress`** opt-in: the `WindowManager`'s
light-dismiss sweep skips a press that lands on the popup's `PlacementTarget`, so the anchor's `OnMouseDown` owns
the open/close (without it, a click while open dismissed *then* re-opened — a race). `ComboBoxItem` mirrors
`ListBoxItem` (owner-driven `IsSelected`, `:selected` mapping); its `OnMouseDown` selects through
`owner.HandleContainerPointerSelect` then calls `owner.CommitAndClose()`. The editable (text-entry) variant is a
v2 deferral. The XAML control-theme overlay twin is deferred — `ComboBox`/`ComboBoxItem` fall through to the
code-first `CursorialTheme.BuiltIn` themes (the chain backstop).

---

## §C13 — TreeView / TreeViewItem (P2C, post-P9)

A hierarchical selector (design doc §12.6 — the headered-items recipe). `TreeView : ItemsControl` owns
**tree-wide single selection** (a flat `SelectionModel` is per-control and can't span the nesting, so `TreeView`
coordinates directly, not via `SelectingItemsControl`); `TreeViewItem : HeaderedItemsControl` is both a row
(its `Header`) and a sub-items host (its `Items`). Each item template is `[twisty(2)][Header]` over a
`PART_ItemsHost` `ItemsPresenter` indented 2 cells and `Visibility`-gated on `IsExpanded` — so nesting indents
**recursively** (no per-item depth math) and a child's twisty aligns under its parent's header. Containers are
`TreeViewItem`s. The tree is one tab stop (`TreeView.IsTabStop=false`, the top items panel is
`KeyboardNavigationMode.Once`); arrows navigate the **visible** tree (collapsed subtrees are skipped) and
selection follows the keyboard cursor. Tests: `Cursorial.UI.Tests/ControlMatrix/Section26_TreeView.cs`.

| Row | Setup | Action | Expected | Source |
|-----|-------|--------|----------|--------|
| C13.1 | root with 2 child `TreeViewItem`s, one with a grandchild | show | own-container `TreeViewItem`s realize; the parent reports `HasItems`, the leaf does not | PIN (CD-P2C-1) |
| C13.2 | a parent node | set `IsExpanded=true`/`false` | `PART_ItemsHost` `Visibility` flips `Visible`/`Collapsed`; `:expanded` mirrors `IsExpanded`; raises `Expanded`/`Collapsed` | PIN (CD-P2C-1) |
| C13.3 | two sibling nodes | click node A's header, then node B's | A then B selected; selecting B clears A (tree-wide single); `TreeView.SelectedItem` tracks the data item; `SelectionChanged` raised | PIN (CD-P2C-1) |
| C13.4 | a collapsed parent, selection elsewhere | click its twisty glyph | toggles `IsExpanded` only — the selection does **not** move to the parent (twisty ≠ row) | PIN (CD-P2C-1) |
| C13.5 | a focused collapsed parent | Right; Right again | first Right expands (focus stays); second Right moves focus+selection to the first child | PIN (CD-P2C-1) |
| C13.6 | a focused expanded-parent's first child | Left; Left again | first Left (on a leaf) moves to the parent; on an expanded node the first Left collapses, the next moves to parent | PIN (CD-P2C-1) |
| C13.7 | expanded tree | Down from the root; Up back | Down steps root→child1→(child1's child if expanded)→child2 in visible order, selection follows; Up reverses | PIN (CD-P2C-1) |
| C13.8 | a collapsed parent above a sibling | Down from the parent | skips the parent's hidden children → lands on the next sibling | PIN (CD-P2C-1) |
| C13.9 | nested expanded tree | inspect a grandchild container | its window X is greater than its parent's (recursive indent) | PIN (CD-P2C-1) |
| C13.10 | a selected child node | remove it from its parent's `Items` | the tree's `SelectedContainer`/`SelectedItem` clear (no dangling pointer) | PIN (CD-P2C-1) |
| C13.11 | a node with `IsSelected=true` set before it is parented | show the tree; then select a sibling | the preset selection folds into the tree on realization; the later select clears it (tree-wide single) | PIN (CD-P2C-1 audit) |
| C13.12 | a focused node | Ctrl+Space; then a modifier-free Space | Ctrl+Space does NOT select (bubbles unhandled); modifier-free Space selects | PIN (CD-P2C-1 audit) |

**CD-P2C-1 — TreeView: tree-wide single selection, recursive-indent template, visible-tree keyboard nav.**
`TreeView : ItemsControl` holds `SelectedItem` (read-only `DirectProperty`) + an internal selected-container
pointer; `ChangeSelection(item)` deselects the prior container (`SetIsSelectedFromTree(false)`), selects the new,
recomputes `SelectedItem` via the owning generator's `ItemFromContainer` (an own-container resolves to itself),
and raises `SelectionChanged`. `TreeViewItem.IsSelected`/`IsExpanded` are `StyledProperty`s with
`:selected`/`:expanded` `PseudoClassMapping`s; an external `IsSelected=true` folds into the tree
(`ChangeSelection(this)`) under a `_treeDriven` guard (cf. `ListBoxItem._ownerDriven`), `IsExpanded` flips the
`PART_ItemsHost` visibility + twisty glyph (`>`/`v`/leaf-blank, ASCII-safe per the ambiguous-width memory) and
raises `Expanded`/`Collapsed`. Mouse: a press whose `OriginalSource` is within `PART_Twisty` toggles expansion
(no select); any other press selects (`Focus()` + `ChangeSelection`). Keyboard (only when `IsFocused`, the
`MenuItem` class-handler rule): Right expands-then-descends, Left collapses-then-ascends, Up/Down walk
`PrevVisible`/`NextVisible` (descend into expanded subtrees, skip collapsed ones, walk up at a level's edge),
all directional moves select-on-focus. A node caches its owning tree at attach (`_ownerTree`) so that on detach
(its removal from a parent's `Items`) it can clear the tree's selection if it was selected — no dangling
`SelectedContainer`/`SelectedItem`. **Realization is eager (not gated on expansion)** — collapsed children
exist but measure 0×0 under the collapsed host (WPF-faithful; container virtualization is a v2 deferral, noted
so the "covered everything" read is honest). v1 uses own-container `TreeViewItem`s; `HierarchicalDataTemplate`
(data-driven hierarchy) is a v2 deferral. The XAML control-theme overlay twin is deferred — `TreeView`/
`TreeViewItem` fall through to the code-first `CursorialTheme.BuiltIn` themes (the chain backstop).

**CD-P2C-1 audit (P2C follow-up).** A 3-lens adversarial audit (selection-lifecycle / navigation / layout-theme,
each finding independently refutation-verified through `UITestHost`) confirmed **3 real bugs** the green tests
missed, each fixed + regression-tested: **(1)** a `TreeViewItem` with `IsSelected=true` set *before* it was parented
under a `TreeView` dropped the intent (`OnIsSelectedChanged`'s `OwnerTree` was null) and a later selection then left
**two** nodes `:selected` (tree-wide-single broken) — `OnAttachedToTree` now folds a preset `IsSelected` into
`ChangeSelection` once the owner resolves (the `SelectingItemsControl.ReconcileContainers` analog); row C13.11.
**(2)** a `^:pointerover` fill on `TreeViewItem` lit the whole **ancestor** header-bar chain, because
`InteractionState.PointerOver` is set on every ancestor of the hovered leaf and tree items nest — the hover rule was
removed (a tree node highlights on selection + keyboard focus only; WPF-faithful). **(3)** `IsSpace` lacked a
modifier guard, so **Ctrl+Space** (the lone real `Key.Space` wire) selected the node and swallowed the key — now
gated `Modifiers == None` (mirroring `ButtonBase.IsActivationSpace`), so it bubbles for an ancestor command binding;
row C13.12.

---

## §C14 — Calendar / CalendarDayButton (P2D, post-P9)

A month-view date picker (design doc §12 — the WPF `Calendar` analog, month mode only). `Calendar : Control` shows
`DisplayDate`'s month as a 7-column grid the control builds in code into `PART_MonthView`: a culture-ordered
day-of-week header row (`DateTimeFormatInfo.ShortestDayNames`, from `FirstDayOfWeek`) + six week rows of
`CalendarDayButton` cells. A header label + previous/next month buttons chrome it. Clicking a day (or arrow-key
navigation) sets `SelectedDate`; each cell restamps `:today`/`:selected`/`:inactive` (the leading/trailing
adjacent-month fill). The whole widget is one tab stop (`KeyboardNavigationMode.Once`); the `Calendar` handles
arrows. Tests: `Cursorial.UI.Tests/ControlMatrix/Section27_Calendar.cs` (June 2026 pinned, Sunday-start).

| Row | Setup | Action | Expected | Source |
|-----|-------|--------|----------|--------|
| C14.1 | DisplayDate=Jun 2026, Today=Jun 18 | show | 42 day cells; Jun 18 `:today`; the May 31 / Jul 2 fill cells `:inactive` | PIN (CD-P2D-1) |
| C14.2 | — | click Jun 10's cell | `SelectedDate==Jun 10`; the cell `:selected`; `SelectedDateChanged` raised | PIN (CD-P2D-1) |
| C14.3 | focused cell | PageDown; PageUp×2 | `DisplayDate` moves +1 then −2 months (May); the new month's grid realizes | PIN (CD-P2D-1) |
| C14.3b | — | click the previous button | `DisplayDate` moves back one month (the prev/next wiring) | PIN (CD-P2D-1) |
| C14.4 | SelectedDate=Jun 15, focused | Right, Down; Jun 30 then Right | ±1 / +7 day moves; crossing into July moves `DisplayDate` to July | PIN (CD-P2D-1) |
| C14.5 | — | click a `:inactive` trailing Jul 2 cell | selects Jul 2 and moves the view to July (now active) | PIN (CD-P2D-1) |
| C14.6 | Sunday-start | inspect May 31; set FirstDayOfWeek=Monday | Sunday-start shows the May 31 leading fill; Monday-start begins at Jun 1 (no May 31 cell) | PIN (CD-P2D-1) |
| C14.7 | — | set `SelectedDate=Aug 20` programmatically | `DisplayDate` syncs to August; Aug 20's cell `:selected` | PIN (CD-P2D-1) |
| C14.8 | shown month diverges from Today | PageDown then Right | the arrow navigates within the shown month (Jul 2), not back to a Today-relative June day | PIN (CD-P2D-1 audit) |
| C14.9 | — | click a day, then Enter | focus stays on the clicked day (Enter doesn't page the month — focus didn't jump to the prev button) | PIN (CD-P2D-1 audit) |
| C14.10 | — | set `DisplayDate` to Dec 9999 / Jan 0001; PageDown/PageUp | no throw; the grid clamps and month nav clamps at the representable bounds | PIN (CD-P2D-1 audit) |

**CD-P2D-1 — Calendar: code-built month grid, culture-ordered, selection by click + arrows.** `Calendar : Control`
holds `DisplayDate` (the shown month), `SelectedDate : DateOnly?` (two-way), `Today` (the `:today` reference —
defaults to the system date at construction, settable for determinism), and `FirstDayOfWeek` (defaults to the
current culture). `RebuildMonthView` (run at `OnApplyTemplate` + on any of those changing) clears + repopulates
`PART_MonthView`: a header row of `ShortestDayNames` and six week rows of `CalendarDayButton`s from the Sunday-/
Monday-/…-aligned grid start (`lead = (firstOfMonth.DayOfWeek − FirstDayOfWeek + 7) % 7`), each stamped
`IsToday`/`IsSelected`/`IsInactive` and wired `Click → SelectDate(cell.Date)`. `CalendarDayButton : Button` carries
those three as `:today`/`:selected`/`:inactive` `PseudoClassMapping`s (day cells don't nest, so its `:pointerover`
is safe — unlike `TreeViewItem`). Selecting a day in an adjacent month moves `DisplayDate` to it (`OnSelectedDateChanged`).
Keyboard (the `Calendar` is the class handler): Left/Right ±1 day, Up/Down ±7 (anchored at `SelectedDate ?? Today`,
selection-follows-focus onto the rebuilt cell), Home/End to the month edges, PageUp/PageDown ∓1 month (re-focusing a
cell in the new month so the next key still routes here); the prev/next buttons share `ChangeMonth`. **Deferrals:**
the year/decade drill-down `DisplayMode`s, date bounds (`DisplayDateStart`/`End` + blackout), multi-select, and the
XAML control-theme overlay twin (it falls through to the code-first `CursorialTheme.BuiltIn` backstop).

**CD-P2D-1 audit (P2D follow-up).** A 3-lens adversarial audit (date/culture, selection/nav, theme/layout — each
finding refutation-verified through `UITestHost`) confirmed **7 real bugs** the green tests missed, fixed +
regression-tested: **(1+6)** arrow navigation anchored at `SelectedDate ?? Today` (DisplayDate-blind), so the first
arrow after a PageUp/PageDown/prev-next **snapped the view back** to a stale month — `ResolveAnchorDate` now anchors
at the focused day cell (the `ListBox.ResolveCurrent` idiom) or, with no cell focused, a day in the shown month
(`InMonthAnchor`); rows C14.8. **(2)** mouse-clicking a day rebuilt the grid without re-focusing, so focus repair
jumped to the prev-month button (a subsequent Enter then paged the month) — `OnDayClick` now re-focuses the picked
cell; row C14.9. **(3+4+5)** `DateOnly` overflow at the extremes — the grid-start `AddDays(-lead)` underflowed near
Jan 0001, the 42-cell loop overflowed near Dec 9999, and `ChangeMonth`'s `AddMonths` threw crossing either bound —
all clamped (lead clamp + day-number range guard + `ChangeMonth` bound check); row C14.10. **(7)** the prev/next
chrome buttons were focusable tab stops (compounding #1) — set `Focusable=false`/`IsTabStop=false` (the ScrollBar
line-button precedent), so the whole widget is genuinely one tab stop landing on the day grid.

---

## §C15 — DatePicker (P2E, post-P9)

A drop-down date field (design doc §12 — the WPF `DatePicker` / WinUI `CalendarDatePicker` analog, the **calendar**
variant). `DatePicker : Control` shows a read-only `SelectedDate` (formatted with the culture short-date pattern, or
the `Watermark` when empty) plus a `v` drop button; opening drops a `Popup` hosting a `Calendar` (`PART_Calendar`).
Picking a day commits the date (the calendar's `SelectedDateChanged` → `DatePicker.SelectedDate`) and closes. The
**inline** variant is the standalone `Calendar` control (§C14 — a complete always-visible month picker). The
field-click toggle reuses `Popup.KeepOpenOnAnchorPress` (the ComboBox mechanism). Tests:
`Cursorial.UI.Tests/ControlMatrix/Section28_DatePicker.cs`.

| Row | Setup | Action | Expected | Source |
|-----|-------|--------|----------|--------|
| C15.1 | — | set `SelectedDate=Jun 18` | the field text becomes the culture short-date string; `SelectedDateChanged` raised | PIN (CD-P2E-1) |
| C15.2 | — | set `IsDropDownOpen=true`/`false` | the `Popup` opens hosting the `Calendar`; `false` closes | PIN (CD-P2E-1) |
| C15.3 | — | left-click the field; click again | first opens (`:open`), second closes — the anchor owns the toggle | PIN (CD-P2E-1) |
| C15.4 | focused | Down (open); Escape (close) | Down/F4/Enter/Space opens; Escape closes | PIN (CD-P2E-1) |
| C15.5 | open | commit a date (Enter on a day) | `SelectedDate` commits to that date and the drop-down closes | PIN (CD-P2E-1) |
| C15.6 | no date | inspect; then set a date | the `Watermark` shows while empty; a date replaces it | PIN (CD-P2E-1) |
| C15.7 | a date already selected | open | the drop-down stays open (the open-time calendar sync isn't a commit) | PIN (CD-P2E-1 audit) |
| C15.8 | open, a cell focused | Right (browse); then Enter | the arrow browses without closing (`SelectedDate` unchanged); Enter commits the browsed date + closes | PIN (CD-P2E-1 audit) |
| C15.9 | open on the selected day | Enter on that same day | still closes (the commit fires even when the date is unchanged) | PIN (CD-P2E-1 audit) |

**CD-P2E-1 — DatePicker: calendar-popup date field (inline variant = the standalone Calendar).** `IsDropDownOpen`
is a `DirectProperty` two-way with the templated `Popup` (`SetDropDownOpen` → `SetAndRaise` + imperative
`PseudoClasses.Set(":open", …)`, drives `Popup.IsOpen`, restores field focus on close — the ComboBox shape). On open
it pushes `SelectedDate`/`DisplayDate` into the `Calendar` and best-effort `FocusDate()`s into the grid; the
calendar's `SelectedDateChanged` commits via `SetCurrentValue(SelectedDateProperty)` and closes. Mouse: a left press
on the field toggles (`OnMouseDown`, anchor-owned via `KeepOpenOnAnchorPress`). Keyboard: closed Down/F4/Enter/Space
open, open Escape closes (the popup also light-dismisses). The display text is the culture short-date (`"d"`) or the
`Watermark`. **Deferrals:** editable text entry (typing a date), date bounds, and the XAML control-theme overlay twin
(it falls through to the code-first `CursorialTheme.BuiltIn` backstop).

**CD-P2E-1 audit (P2E follow-up).** A 2-lens adversarial audit (popup-lifecycle / commit-semantics, refutation-verified
through `UITestHost`) confirmed **3 real bugs**, fixed + regression-tested. The root flaw: the commit-close was wired
to the calendar's `SelectedDateChanged`, which (a) is **suppressed** when re-picking the current date (equality-gated)
so re-confirming the selected day never closed (rows C15.9), and (b) fires on every **arrow browse** so the first
arrow inside the open calendar committed the adjacent date and slammed the popup shut (row C15.8). The fix adds
`Calendar.DateCommitted` — raised in `OnDayClick` (a click, or Enter/Space which a `CalendarDayButton` routes through
`Click`) **unconditionally**, never on an arrow browse — and the `DatePicker` now closes on `DateCommitted` instead
of `SelectedDateChanged`. (The third finding — opening with a preexisting date commit-and-closed via the open-time
push — was caught pre-commit and fixed by the same redesign: a property push fires `SelectedDateChanged` but not
`DateCommitted`, so it can't close; row C15.7. The interim `_syncingCalendar` guard was removed as redundant.)

---

## §C16 — TextSearch (type-ahead) for ItemsControls (post-P9)

A shared `ItemsControl`-level type-ahead facility (the WPF `TextSearch` model). Printable keys accumulate a prefix
(reset after a ~1 s idle), and the control moves its current item to the first match; re-pressing a single character
cycles among items that start with it. Per-item match text is `TextSearch.Text` (attached, on the container/item),
else the control's `TextSearch.TextPath` (attached) evaluated against the item, else `item.ToString()`.
`IsTextSearchEnabled` (default true) + `IsTextSearchCaseSensitive` (default false). The engine lives in
`ItemsControl` (pure `TextSearchMatcher` + buffered `TextSearchController`); only controls that actually move a
current item opt in (`TextSearchNavigates`) so a plain `ItemsControl`/`Menu` never swallows a key. `ListBox`/`ComboBox`
(via `SelectingItemsControl`) select the match (ListBox also focuses it); `TreeView` matches its **top-level** nodes'
`Header` and selects+focuses (full visible-subtree search is a follow-on). Tests:
`Cursorial.UI.Tests/ControlMatrix/Section29_TextSearch.cs`.

| Row | Setup | Action | Expected | Source |
|-----|-------|--------|----------|--------|
| C16.1 | items alpha/apple/ant/bravo/charlie | match "c" / "br" / "z" | charlie / bravo / no-match (−1) | PIN (CD-P2F-1) |
| C16.2 | — | repeat "a" from index 0/1/2 | cycles apple→ant→alpha (from after current, wrapping) | PIN (CD-P2F-1) |
| C16.3 | Alpha/beta | "a" case-insensitive vs sensitive; "A" sensitive | match / no-match / match | PIN (CD-P2F-1) |
| C16.4 | can/car/cat/dog | type c, a, t | prefix extends "c"→"ca"→"cat"; lands on cat | PIN (CD-P2F-1) |
| C16.5 | — | leading space; then a,a,a | space ignored (−1); then alpha→apple→ant cycle | PIN (CD-P2F-1) |
| C16.6 | ListBox | type "c"; "h" (rapid); idle; "b" | charlie; still charlie ("ch"); after reset "b"→bravo | PIN (CD-P2F-1) |
| C16.7 | ComboBox (closed) | type "g" | selects gamma without opening the drop-down | PIN (CD-P2F-1) |
| C16.8 | TreeView top-level apple/banana/cherry | type "c" | selects+focuses the cherry node (matched on Header) | PIN (CD-P2F-1) |
| C16.9 | ListBox of Person, TextPath=Name | type "g" | selects "Grace" via TextPath (not ToString) | PIN (CD-P2F-1) |

**CD-P2F-1 — TextSearch: shared ItemsControl type-ahead.** Attached `TextSearch.TextPath`/`Text`; `ItemsControl`
gains `IsTextSearchEnabled`/`IsTextSearchCaseSensitive`, a lazy `TextSearchController`, an `OnTextInput` driver
(ignores paste + control chars + a leading space; gated on `TextSearchNavigates`), and the virtuals
`CurrentTextSearchIndex`/`GetTextSearchText`/`OnTextSearchMatch`. `TextSearchMatcher.FindMatchIndex` is the pure
prefix search (repeat → from after current, extend → from current, both wrapping). The controller extends the prefix
on a new char and cycles on a re-pressed single char, adopting the prefix only on a hit, with a frame-aligned
`UITimer` idle reset. **Deferral:** TreeView full visible-subtree search (top-level only for now); a filtering
`AutoCompleteBox`-style control is a separate item.

---

## §C17 — ComboBox v2 (editable mode + parity, post-P9)

WPF/Avalonia parity for `ComboBox`, headlined by an optional editable text-entry mode. **Non-editable** (default)
keeps the §C12 behavior (read-only `PART_ContentSite` face, type-ahead via §C16). **Editable** (`IsEditable`,
`:editable`): the face is a `PART_EditableTextBox` (`TextBox`) — typing edits `Text` as free text, the
`PART_DropDown` button opens the list, navigating it updates the text, Enter / focus-loss commits (an exact
case-insensitive item match selects it, else the free text is kept and the selection clears), and Escape reverts to
the current selection. Focus delegates from the ComboBox to the text box (so its caret publishes); the text box is
the tab stop when editable (`IsTabStop` flips). Parity props: `Text` (two-way), `IsReadOnly`, `PlaceholderText`,
`MaxDropDownHeight`, `StaysOpenOnEdit`, `SelectionBoxItem`. Tests:
`Cursorial.UI.Tests/ControlMatrix/Section30_ComboBoxV2.cs`.

| Row | Setup | Action | Expected | Source |
|-----|-------|--------|----------|--------|
| C17.1 | — | set `IsEditable` true | the text box shows + becomes the tab stop; the content site collapses (`IsTabStop` false) | PIN (CD-P2G-1) |
| C17.2 | editable | type "ch" | `Text=="ch"` as free text; no type-ahead selection jump (`SelectedItem` null) | PIN (CD-P2G-1) |
| C17.3 | editable | type "banana", Enter | commits the exact match (`SelectedItem=="banana"`) + closes | PIN (CD-P2G-1) |
| C17.4 | editable, "apple" selected | overwrite with "xyzzy", Enter | free text kept (`Text=="xyzzy"`); selection cleared | PIN (CD-P2G-1) |
| C17.5 | editable | set `SelectedItem="cherry"` | `Text` (and the box) sync to "cherry" | PIN (CD-P2G-1) |
| C17.6 | editable, `IsReadOnly` | type | typing rejected (`Text` unchanged) | PIN (CD-P2G-1) |
| C17.7 | editable | `ComboBox.Focus()` | focus delegates to the text box (its caret publishes) | PIN (CD-P2G-1) |
| C17.8 | editable, `StaysOpenOnEdit` | type | the drop-down opens | PIN (CD-P2G-1) |
| C17.9 | editable, "apple" selected, open | overwrite with "zzz", Escape | reverts `Text` to "apple" + closes | PIN (CD-P2G-1) |
| C17.10 | non-editable | type "c" | type-ahead selects "cherry"; `SelectionBoxItem` tracks it (parity unbroken) | PIN (CD-P2G-1) |

**CD-P2G-1 — ComboBox: editable text-entry + parity.** `Text` (DirectProperty, two-way) ↔ `PART_EditableTextBox.Text`
round-trips through a `_syncingText` guard; the user's `TextChanged` adopts free text (no selection change). `Text`
follows `SelectedItem` via the `SelectionChanged` handler EXCEPT while committing (`_committing` guard keeps the typed
text). `CommitText` (Enter / `OnLostFocus` when keyboard focus leaves) exact-matches the text to an item
(case-insensitive) → `Selection.Select(match)` (−1 clears). `OnGotFocus` delegates to the text box when editable;
`UpdateEditableState` toggles the two faces' visibility + `IsTabStop`. Type-ahead is suppressed when editable
(`TextSearchNavigates => !IsEditable`); Space opens only when non-editable (it types when editable). The
`PART_DropDown` button toggles the list (the face press toggles only when non-editable, so an editable face click
lands the caret). **Deferral:** inline text-completion/autocomplete + filtering (the `AutoCompleteBox`-style control)
is a separate item.

---

## §C18 — Calendar v2a (bounds + blackout, post-P9)

`DisplayDateStart`/`DisplayDateEnd` (nullable, unbounded by default) clamp `DisplayDate` and gate selection;
`BlackoutDates` (a list of `CalendarDateRange`s) marks non-selectable cells. Coercion (`CoerceDisplayDate`/
`CoerceSelectedDate`) clamps the view and clears an out-of-range / blacked-out selection; blackout cells are
`:blackout` + `IsEnabled=false` (so `ButtonBase` raises no Click — unpickable, muted via the existing `:disabled`
look). Keyboard nav clamps to `[Start,End]` and skips blackout dates (`NearestSelectable`). `IsTodayHighlighted`
(default true) gates the `:today` marker. (The DisplayMode Month/Year/Decade drill-down is v2b.) Tests:
`Cursorial.UI.Tests/ControlMatrix/Section31_CalendarV2.cs`.

| Row | Setup | Action | Expected | Source |
|-----|-------|--------|----------|--------|
| C18.1 | — | set Start=Jun10 / End=Jun20; set DisplayDate out of range | DisplayDate clamps into [Start,End] | PIN (CD-P2D-2) |
| C18.2 | Start=Jun10,End=Jun20 | set SelectedDate=Jun5 then Jun15 | out-of-range → null; in-range kept | PIN (CD-P2D-2) |
| C18.3 | SelectedDate=Jun5 | set Start=Jun10 | the now-out-of-range selection clears to null | PIN (CD-P2D-2) |
| C18.4 | BlackoutDates={Jun10} | inspect the cell; try to select Jun10 | cell `:blackout`+disabled; selection refused (null) | PIN (CD-P2D-2) |
| C18.5 | SelectedDate=Jun12 | blackout Jun10–14 | the selection (now blacked out) clears | PIN (CD-P2D-2) |
| C18.6 | — | set IsTodayHighlighted=false | the today cell's `:today` clears | PIN (CD-P2D-2) |
| C18.7 | blackout Jun16, SelectedDate=Jun15 focused | Right | skips Jun16 → selects Jun17 | PIN (CD-P2D-2) |
| C18.8 | End=Jun20, SelectedDate=Jun20 focused | Right | clamped at the bound → stays Jun20 | PIN (CD-P2D-2) |

**CD-P2D-2 — Calendar bounds + blackout.** `DisplayDate`/`SelectedDate` carry coerce callbacks
(`UIProperty.Register(coerce:)`, the ScrollBar pattern); `OnBoundsChanged`/`OnBlackoutChanged` call `CoerceValue`
on both + rebuild. `CalendarDateRange` (inclusive, order-agnostic `Contains`) models blackout ranges.
`NearestSelectable(from, dir, lo, hi)` powers blackout-skipping + bound-clamped arrow nav and Home/End
(`SelectInMonth`). `CalendarDayButton.IsBlackout` (`:blackout`). **Deferral:** the DisplayMode drill-down (v2b).
