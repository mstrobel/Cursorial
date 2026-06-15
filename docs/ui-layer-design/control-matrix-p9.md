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

**Deferred to later sub-phases (noted, not silently dropped):** `:alternate` row-striping (P9.3 ListBox — only
visible with striping rules); a non-vertical/scrolling `ItemsPanel` (P9.3); the `SelectionModel.ItemsInserted/
Removed/Reset` index-fixup hooks the generator forwards to (P9.2); per-container DataTemplate-by-type rendering
assertions (when a styled item renders through the chain — exercised end-to-end at P9.3).
