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
- **CD-P9-2 — eager realization (v1).** Every item is realized; the unrealize retraction sequence
  (ClearContainer → visual detach → logical remove → DataContext clear) and the range-based
  `ItemContainerGenerator.ContainersChanged` event are the seam a future recycling/virtualizing host re-enters at.
- **CD-P9-3 — the 4-step Unrealize order.** ClearContainer (unhook while bindings live) precedes the visual
  detach (the subtree detach IS the store-retraction trigger), which precedes the logical removal + DataContext
  clear. The generator fires `Unrealized` (host removes visually) *before* dropping the range from its list, so
  the containers stay index-addressable through the detach.
- **CD-P9-4 — ItemsSource ⊥ Items.** Setting `ItemsSource` with a populated `Items` throws; mutating `Items`
  while `ItemsSource` is set throws (WPF rule). Both lanes normalize through one internal `ItemsSourceView` so
  the generator has a single realize/unrealize driver.
- **CD-P9-5 — `ItemContainerStyle` at the Explicit layer.** Applied as `container.Style`, so app-level
  type-selector styles (and later `:selected`/`:alternate`) compose underneath it.
- **CD-P9-6 — runtime template/source-shape change ⇒ Reset.** Changing `ItemTemplate`/`ItemContainerStyle`/
  `ItemsPanel` at runtime unrealizes all + re-realizes (v1) rather than diffing in place.
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
| C2.6/7 | bound source | inspect the container | LOGICAL parent = the `ItemsControl`; VISUAL parent = the items panel (≠ the control) — punch 43 | PIN (CD-P9-1) |
| C2.8 | bound source | Insert at index 1 | one container realized at 1; index 0 unchanged; later items shift | WPF |
| C2.9 | bound source | Remove at 0 | the removed container's LOGICAL + VISUAL parents are cleared (the 4-step retraction ran); evicted; survivors shift | PIN (CD-P9-3) |
| C2.11 | bound source of 3 | Move 0→2 | the SAME container instances, reordered — no realize/unrealize | WPF |
| C2.13 | bound source | Clear | all containers unrealized | WPF |
| C2.14 | `ItemContainerStyle` set | shown | each container's `Style` is the given style (Explicit layer) | PIN (CD-P9-5) |
| C2.15 | runtime `ItemTemplate` change | assign | containers re-realized (Reset policy) — new instances | PIN (CD-P9-6) |
| C2.18 | `HeaderedItemsControl` | set `Header` + add an item | `Header` round-trips; the items host realizes (MenuItem's base — smoke) | WPF |

**Deferred to later sub-phases (noted, not silently dropped):** `:alternate` row-striping (P9.3 ListBox — only
visible with striping rules); a non-vertical/scrolling `ItemsPanel` (P9.3); the `SelectionModel.ItemsInserted/
Removed/Reset` index-fixup hooks the generator forwards to (P9.2); per-container DataTemplate-by-type rendering
assertions (when a styled item renders through the chain — exercised end-to-end at P9.3).
