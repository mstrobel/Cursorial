# Cursorial.UI.DataViews — the DataGrid + data-shaping design

Status: **PANEL-AMENDED, implementation in progress** (2026-07-18; the four-judge critique +
adversarial verification ran against the draft — every upheld finding is folded in below and marked
*[panel]*). Owner intent: a robust, highly performant DataGrid with DevExpress-WPF-grade UX
(`tokyo-night-terminal-datagrid.html` is the visual spec), large-dataset capable, with live data
shaping. The shaping controller uses **runtime code generation with LINQ expression trees** so
sorting/grouping/formatting are specialized to the row type — no boxing on hot paths. The sort must
excel on **both random and mostly-sorted** data, and live updates must **re-sort as few rows as
possible**. Shaping may run on or off the UI thread (§2.6 is the decision). The owner cleared
**solution-wide changes** for reusable infrastructure (ListView, TreeListView), and mandated
(2026-07-18) that **in-row editing be supported via element hosting as the sanctioned special case**
of the otherwise direct-drawn rows.

## 0. Invariants

1. **The engine is UI-free.** `Cursorial.UI.DataViews.Shaping` references BCL only; the
   `IShapingScheduler` seam abstracts "post to owner thread". Headless-testable to the last branch.
2. **No boxing on shaping hot paths.** Keys live in typed vectors (`TKey[]`); comparisons,
   aggregation, formatting, and filter evaluation run through expression-compiled typed delegates.
   Boxing only at cold boundaries (group captions, Min/Max group results, diagnostics).
3. **The view is an index space.** Shaping produces permutations/structures of `int` row ids (slots).
4. **Incremental beats clever.** A live tick (K changed rows) is O(V + K log K) memory traffic —
   never an O(V log V) re-sort. *(Honest accounting [panel]: the repair sweep+merge touches all V
   view entries — Θ(V) traffic — but does only O(K log K + K·log(V/K)-ish) comparisons; the galloped
   clean-run copy is a recorded optimization, not v1.)*
5. **The UI thread never hitches on big shaping.** Work routes to the background lane by measured
   SIZE, not by kind [panel]: any cycle whose cost is Θ(V) at large V (full reshape OR a live tick
   under grouping at 1M rows) runs off-thread against the sealed snapshot (§2.6); small cycles run
   synchronously. Thresholds are benchmark-tuned constants, overridable.
6. **Rows are drawn, not realized** — except the active edit row, which hosts real editor elements
   (owner mandate). Steady-state (no data change, no input) frames allocate 0 B and re-raster
   nothing; in-band scrolling is a pure composite slide.
7. **Reusable pieces land reusable.** The engine is presentation-free (ListView = the flat path;
   TreeListView = a hierarchy adapter over the same kits); the selection controller and virtual-row
   idioms are designed for lift into `Cursorial.UI` when the second consumer arrives.
8. **AOT posture:** the codegen requires `RequiresDynamicCode`; `ShapingCodegen` is the single
   compile site (a future interpreted fallback slots in behind it).

## 1. Scope

### v1 (this PR)

- **Shaping engine**: typed column model + codegen (accessors, comparers, span formatters,
  aggregators, filter compiler); multi-column sort with the **insertion-sequence tiebreak** [panel —
  slot ids are reuse-scrambled; sequences keep equal-key rows in insertion order]; TimSort full sort
  + K-row incremental repair; the **criteria-tree filter model** [panel — `FilterNode`
  And/Or/Not/Condition/InSet/Custom is the ONE public filter shape all surfaces target, so the
  deferred Filter Builder/expression parser lower into it without breaking]; multi-level grouping
  with per-group summaries; total summaries; live updates (INCC + per-row INPC, coalesced); the
  size-gated sync/background pipeline (§2.6).
- **DataGrid control**: explicit + auto-generated columns (`AutoGenerateColumns : bool`, default
  true, ignored when `Columns` non-empty; public instance properties in declaration order,
  `[Browsable(false)]` respected, per-type format defaults) [panel]; **column geometry** [panel]:
  `DataGridColumn.Width : DataGridLength` (fixed cells / `*` star / `Auto`), `MinWidth`/`MaxWidth`,
  Auto = header caption ∨ widest formatted cell **in the current band** (never an O(N) sweep;
  re-evaluated on re-anchor, monotonic-grow within a shape to avoid jitter), auto-columns default
  Auto; header band with sort glyphs + filter affordances; **observable
  `SortDescriptions`/`GroupDescriptions` collections as the one source of truth** [panel — gestures
  edit them, glyphs render them, persistence reads them]; virtualized direct-draw rows; **row-id
  keyed selection** (§3.3 [panel]); keyboard model per §3.3 (legacy-safe gestures [panel]); group
  panel with keyboard-reachable chips [panel]; summary footer incl. stacked cells (footer height =
  max per-column aggregate count [panel]); filter popup (checklist + search + tri-state);
  auto-filter row with per-column cell kinds `Text | DistinctPicker | Disabled` [panel] and the
  operator grammar (`> >= < <= = <> ""`=contains/starts-with per column option); conditional
  formatting engine + API: **DataBar, Threshold, ColorScale, Predicate** (a shared per-column stats
  block — min/max recomputed with summaries — anchors DataBar and ColorScale identically [panel]);
  **basic in-row editing** (owner mandate): the edit-row element host + the TextBox editor path
  (F2/Enter/double-click begins; Enter commits through the column's compiled setter; Esc cancels;
  Tab advances); **Ctrl+C copy** (selected rows, visible columns, TSV, formatted values — rides the
  existing `ClipboardWriter`/`IClipboardService`; native terminal selection is unavailable under
  mouse tracking, so the grid must provide extraction [panel]).
- **Theme**: code-first over the ThemeKeys spine + grid-specific keys (row alternation, data-bar
  fill/track, heat tints), assembly theme-contribution tier, NoColor/Ansi16 degradations.
- **Canary**: Gallery page (large ticking dataset).
- **Benchmarks**: §6.

### Deferred (seams carved, recorded here)

Full editor suite (combo/date/spin/new-row/validation — rides the v1 edit host); column chooser +
header drag-reorder (API reorder exists; the drag UX later); header-edge mouse resize (the width
model is v1; the gesture later); rules-manager/rule-dialog UI; **TopBottom format rules** (needs
top-k in the stats block — seam recorded [panel]); the expression language (Filter Builder tree UI +
text editor + IntelliSense → lowers to `FilterNode`); **sort-groups-by-summary** (an optional
comparator pass over sibling `GroupNode` ranges before flatten — the flatten already re-derives
per reshape, so the seam is natural [panel]); **group sort direction ≠ data sort direction** (the
chip's ▲ toggles the group level's direction — v1 ships it as the group level IS a sort level;
independent directions later); master-detail; frozen columns; cell-range selection; in-presenter
column virtualization (for extreme column counts — v1 scenes are full content width); the galloped
repair merge; struct rows (`IRowIdentity<T>` seam — v1 is `where T : class` [panel]).

## 2. The shaping engine (`Cursorial.UI.DataViews.Shaping`)

### 2.1 Row store and identity

`RowStore<TRow>` (implemented): stable slots (row id = slot, never shifted by others' churn),
free-list reuse bounded by the high-water mark, source-order array (the INCC index space), a
reference-equality `Dictionary<TRow,int>` for INPC dispatch (last-insert-wins for duplicate
instances — documented), and the **monotonic insertion-sequence vector** (`long[] Sequences`,
stamped per occupancy) — the sort tiebreak, so equal-key ordering is insertion order regardless of
slot reuse [panel]. Rows are reference types in v1 (`where T : class`) [panel].

INPC at scale [panel]: subscription is one shared handler; 1M rows ≈ 1M event adds (bulk loads
chunk the sweep), the id map ≈ tens of MB (documented cost of `LiveUpdates=true`; opt-out for
static snapshots), and **teardown is a contract**: `DataViewController.Dispose` unsubscribes
everything (leak-tracked in DEBUG, tested).

### 2.2 Columns and codegen (implemented core)

`ShapedColumn<TRow,TKey>` carries the key vector + compiled kit; **compiled comparers bind vectors
through field indirection** (`Expression.Field(Constant(column))`) so growth re-allocation is always
observed — never a captured array [panel; regression-tested]. `ShapingCodegen` compiles: getters
(null-propagating property-path lambdas or caller expressions); nulls-first comparisons (value-type
`CompareTo` inlined, `Nullable<V>` unboxed, string comparison configurable); the fused multi-column
`Comparison<int>` (descending = operand swap — never negate; ends with the sequence tiebreak);
formatters.

Two panel-driven amendments to land with the controller:

- **Collation-key string sorting** [panel]: culture/case-insensitive string comparison through ICU
  costs 100–300 ns/compare — a 1M-row sort would take seconds. For any non-Ordinal string sort
  column, extract **`CompareInfo.GetSortKey` bytes** at key-extraction time — one pooled per-column
  byte **blob** with an `(offset, length)` pair per slot (span APIs, `GetSortKeyLength` to size);
  the comparer compares sort-key spans ordinally (`SequenceCompareTo` — SIMD memcmp): culture order
  at ordinal speed. The blob seals with the key vectors under the §2.6 invariants; dirty-row
  re-extraction rewrites its slot's range (append + periodic compaction). Display/grouping/filters
  still read the string vector — collation keys are sort-order-only. Ordinal columns skip the blob
  entirely (`string.CompareOrdinal` is already memcmp-speed). Memory cost ≈ string-length-order
  bytes per row, only for non-Ordinal sort columns (documented).
- **Span formatters** [panel]: `Func<TKey,string>` allocates per cell per band fill (~1–5k strings
  per re-anchor/tick under a feed). The band cache formats through **`ISpanFormattable`-constrained
  span writers** (closed-generic helpers — expression trees cannot take `Span<char>`) into pooled
  char buffers; `string` cells copy; the `Func<TKey,string>` formatter remains for cold paths
  (group captions, summaries, clipboard).

### 2.3 Sort (implemented)

TimSort over the `int[]` permutation (natural runs, corrected 4-run stack invariants, galloping
merges, caller-owned scratch — steady-state allocation-free) — O(N) on sorted/mostly-sorted, the
shaping profile's dominant case; benchmarked vs `Array.Sort` with the table recorded in §6.
Incremental repair: one partition sweep + K-sort + one merge; Θ(V) index traffic per cycle,
O(K log K) dirty sort, and — with the **galloping repair merge** [panel-upheld: the specified
algorithm, replacing the naive per-element merge] — O(K·log(V/K)) comparer invocations: each sorted
dirty element binary/exponential-searches its insertion point in the clean run and the intervening
clean gaps bulk-copy (`Array.Copy`), so the pass is memory-bandwidth-bound instead of
compare-bound (each compare is 2+ random loads into multi-MB vectors — the dominant cost at 1M).
`FullSortThreshold = K/V > 1/8` falls back to full TimSort (crossover re-derived from the honest
cost model by the §6 table). Repair ≡ full-sort equivalence is fuzz-tested (150 rounds of mixed
change/insert/remove batches).

### 2.4 Filter (model implemented; compiler with the controller)

The **criteria tree** (`FilterNode`: And/Or/Not groups; `Condition(column, op, value[, value2])`
with Equals/NotEquals/Lt/Le/Gt/Ge/Contains/StartsWith/Between; `InSet(column, values)` (null a
legal member — "(Blanks)"); `Custom(lambda)`) [panel]. The controller compiles the active tree into
one `Func<int,bool>` over the typed key vectors (literals convert to key types at compile time —
nothing boxes at evaluation); visibility is a slot bitset; live ticks re-evaluate only the ticked
row. The checklist popup and auto-filter row WRITE tree fragments (per-column subtrees AND-composed
with the programmatic tree); distinct-value lists come from the key vector (typed dedupe, formatted,
with counts).

### 2.5 Grouping and summaries

Grouping prepends group columns to the sort description (a group level IS a sort level in v1 —
see Deferred for independent directions). Group structure derives from the sorted view in one O(V)
walk (compiled per-level `SameGroup`); collapse state keys on the formatted key path. The flattened
view is an `int[]`; **group entries encode as `~nodeIndex`** (sign-discriminated — row ids keep the
full non-negative range) [panel]. The public snapshot read surface is **accessor structs, packing
internal** [panel]: `Count`, `GetRow(viewIndex) → ViewRowInfo { IsGroup, RowId, GroupNodeIndex,
Level }`, group-node accessors — the shape a TreeListView flatten also fits. Summaries ride the
implemented `ColumnAggregator` kit (typed loops; decimal exact; nulls skipped); per-group
aggregates recompute only for membership-dirty groups on ticks; the footer's stats double as the
format-rule anchor block (§2.7).

### 2.6 Live updates and threading (the design decision; panel-hardened)

Owner-thread-affine API; internally a **size-gated two-lane pipeline** [panel]:

- Every shaping cycle (tick repair OR full reshape) estimates its cost from (V, K, grouping):
  below the lane threshold (default 32Ki-row visible sets; benchmark-tuned) it runs synchronously
  inside the coalescing window; above, it runs on the ThreadPool against the sealed snapshot and
  publishes back through `IShapingScheduler.Post`. A superseding request cancels in-flight work.
- **Snapshot integrity invariants** [panel — the verified holes, now pinned]:
  1. Key vectors and the sequence vector are written ONLY on the owner thread and NEVER while a
     shape is in flight — INCC-Add extraction defers to the post-publish replay (the pending-tick
     queue), so a mid-shape Add's slot is invisible to the in-flight shape.
  2. Slot reclamation is deferred while any snapshot (in-flight or published) references the slot:
     the free list holds a generation gate; frees replay after publish.
  3. Publishes are versioned; a stale publish (superseded) drops.
  4. The published `DataViewSnapshot` is immutable-by-contract; the grid reads only it.
- INPC ticks: shared handler → row id → dirty set; property-name filter against shaped columns
  ("" / "Item[]" always match — the binding-engine convention); one coalesced repair per frame.

### 2.7 Conditional formatting

Rule set [panel-widened]: **DataBar** (fill fraction from the stats block), **Threshold** (ordered
predicate → fg/bg/attrs — the `▲●▼` badges), **ColorScale** (2/3-stop heat over the stats range —
the mockup's heat rule + the shipped heat ThemeKeys), **Predicate** (compiled row predicate →
row-level format — "dim Cancelled"). TopBottom deferred (stats top-k seam). Rules compile against
typed keys; **evaluation happens at band-fill time** (with the cell pre-compose — §3.2), not at
paint [panel — the draft's paint-site text contradicted §3.2]; the stats block recomputes once per
publish. `GetCellFormat` returns a struct; no allocation.

## 3. The DataGrid control (`Cursorial.UI.DataViews`)

### 3.1 Anatomy

```
DataGrid : Control                      Focusable one-tab-stop; HandlesScrolling => true
├─ PART_GroupPanel     GroupPanel                  (chips; collapses when empty + grouping off)
├─ PART_Header         DataGridHeaderPresenter     (direct-draw; own render boundary [panel])
├─ PART_AutoFilterRow  DataGridAutoFilterRow       (optional; per-column cell kinds)
├─ PART_ScrollViewer   ScrollViewer
│   └─ (SCP)           DataGridRowsPresenter       (direct-draw viewport; IScrollContentHost +
│                                                    ILogicalScrollHost content of the SCP)
└─ PART_Footer         DataGridSummaryPresenter    (direct-draw; own render boundary [panel])
```

Scrolling rides the existing spine: the rows presenter implements the public seam
(`IsScrollClient/IsLogicalScroll = true`, `GetExtent() = (totalColumnWidth, viewRowCount)`; every
view row is exactly one cell row, so `EstimateItemAt(r)=r`, `BringItemIntoView(i)=(0,i,w,1)`). SCP
banding gives in-band composite-slide scrolling; the presenter reads the band window via the
`ScrollOwner` internals under the granted IVT (promoting the band window into the public seam is a
recorded follow-up [panel]). `DataGrid.OnKeyDown` owns navigation because the grid is the focus
leaf; `HandlesScrolling => true` is defense-in-depth for the editing future [panel — mechanism
corrected]. Header/filter/footer presenters mirror `HorizontalOffset` and are **their own render
boundaries** (`ClipToBounds` — 1–2-row bands, so their H-scroll re-ink stays band-local instead of
re-rastering the window zone [panel]). The rows presenter must NEVER acquire a boundary predicate
(no ClipToBounds/Opacity/RenderOffset — its bounds are the full extent; a boundary would rent an
extent-sized scene [panel]; DEBUG-asserted).

**Column API** [panel]: `DataGridColumn : UIObject` (XAML-instantiable description object — the
loader fills the get-only `Columns` collection; property-system-backed so bindings/dynamic
resources can arrive later): `FieldName` (property-path subset) or `KeySelector` (lambda,
code-only), `Header`, `Width/MinWidth/MaxWidth`, `TextAlignment`, `Format`, `AllowSort/Group/
Filter`, `SortMode` (culture/ordinal), `FilterCellKind`, `Visible`, `FormatRules`. The controller
builds `ShapedColumn`s from these; the grid is non-generic (`ItemsSource : IEnumerable`; row type
discovered from `IEnumerable<T>`/first row, `DataViewController.Create(rowType)` closes the
generic).

### 3.2 Rows presenter — direct draw over the band

`MeasureOverride` snapshots the band window and pre-composes the band cache (span-formatted cells
into pooled char buffers, group captions, conditional-format verdicts) — **ticks refill only dirty
rows; whole-band refills happen only on re-anchor/reshape** [panel]. `Render` draws from the cache
(backgrounds → cell runs grapheme-truncated to column widths → data bars → badges → focus cell →
group rows). Invalidation from the controller side only (render pass is guarded read-only).

**In-row editing is the sanctioned element-hosting special case** (owner mandate): the presenter
supports visual/logical children for exactly one `DataGridEditRowHost` arranged at the edit row's
content position, painting above the drawn rows, hit-testable, scrolling with content. v1 ships the
TextBox editor path (begin/commit/cancel/Tab-advance; commit through the column's compiled setter);
the editor suite rides the same host later. The band cache skips the hosted row while editing.

Otherwise rows are not elements: the presenter is the single hit leaf (row = y, column = x-range —
the TextBox/ScrollBar idiom); hover is presenter state; drawn-row looks come from styled
brush/attribute properties on the presenter set by theme styles (`AffectsRender`-registered);
NoColor cues fold per-axis text attributes at paint.

### 3.3 Interaction model

- **Selection** [panel — the draft's `SelectionModel`-as-is claim was false in both index spaces]:
  a new **`DataGridSelectionController`** keyed on **row ids** — membership survives every reshape
  by construction; gestures (click, Shift-range, Ctrl-toggle, Space) resolve view ranges to id sets
  at gesture time against the current snapshot; paint asks `IsSelected(rowId)`. Large selections
  represent **compactly** [panel]: an explicit id set below a size threshold, flipping to an
  **all-except inversion** for select-all-scale membership — Ctrl+A / Shift+Ctrl+End at 1M rows is
  O(1)-ish, never a 1M-entry hash set; removal drops ids lazily. Anchor/lead track as (id,
  view-index-at-gesture) pairs re-projected per snapshot. §6 gains rows pinning selection identity
  across randomized repair/re-sort tick streams. Designed control-agnostic for later lift to
  `Cursorial.UI`; `SelectionModel` stays the element-container selectors' tool.
- **Keyboard** [panel — legacy-safe]: rows: Up/Down/PgUp/PgDn/Home/End (+Ctrl) move the focus row;
  Left/Right move the focus cell; on a group row Left/Right collapse/expand, Enter toggles; Space
  selects (modifier-free wire is `(Key.Character," ")`). **Band cycle**: Ctrl+Up from row 0 (or
  F6) walks rows → header → group panel → auto-filter → rows. Header band (virtual focus — a
  grid-owned index + drawn cue; drawn cells cannot hold framework focus): Left/Right walk columns,
  **Enter cycles asc→desc→none (replace), Space appends/cycles this column as an additional sort
  level** (no chord — Shift+Enter has no legacy wire encoding), Alt+Down opens the filter popup,
  Ctrl+G adds the column to grouping. Group panel: Left/Right walk chips, Enter toggles the chip's
  direction, Delete removes the level, Ctrl+Left/Right reorders. F2/Enter begins editing on the
  focused cell. Ctrl+C copies.
- **Mouse**: click selects/focuses; header click sorts (Shift+click appends — mouse chords are
  wire-reliable); `▾` opens the filter popup; expander click toggles; chip `✕` ungroups; wheel
  scrolls (horizontal via Shift/`WheelDeltaX`).

### 3.4 Filter surfaces

The checklist popup and the auto-filter row write per-column `FilterNode` fragments (§2.4).
Auto-filter cell kinds per column [panel]: `Text` (operator grammar), `DistinctPicker` (the
checklist anchored to the cell — the mockup's `(All) ▾` cells), `Disabled`. Active filters tint
the header `▾` amber.

## 4. Theming

`CursorialDataViewsTheme` + `[ModuleInitializer]` contribution (the Bars pattern), reusing the core
ThemeKeys spine + grid keys (row alternation, data-bar fill/track, heat tints) valued per tier
dictionary. NoColor: selection/current-row → Inverse (+Bold cue), group rows → Bold, data bars keep
`█░` shape, badges keep `▲●▼` glyphs (shape carries meaning without color). Presenters take their
brushes via theme styles targeting their styled properties.

## 5. Reuse posture

- Scrolling/virtualization: the public `IScrollContentHost`/`ILogicalScrollHost` seam + SCP banding
  (band-window promotion recorded); the element-realizing lane (`ItemContainerGenerator` V0–V5)
  stays untouched — direct-draw is the second sanctioned idiom, chosen per control density.
- Selection: `DataGridSelectionController` (row-id space) is the reusable piece [panel — replaces
  the draft's false `SelectionModel`-as-is claim]; `SelectionModel` remains the element-container
  selector's tool.
- The engine is presentation-free: ListView = flat path; TreeListView = hierarchy adapter (the
  snapshot's `ViewRowInfo` shape already carries level/structure).
- Solution changes so far: `InternalsVisibleTo` for DataViews (+Tests) in `Cursorial.UI.csproj`.

## 6. Testing & verification

The matrix (`dataviews-matrix.md`, DV ledger) rows accompany each stage; test project conventions
per the repo (serialized, headless, alloc-determinism knobs). Engine: differential oracles
(implemented for sort/repair/codegen/aggregates — 73 green at this writing), filter/group/summary
oracles, INCC/INPC pipelines, background determinism via a stub scheduler, teardown leak tests.
Benchmarks (ND25 + fling methodology): sort table (random/sorted/1%-5%-20%-perturbed/K-repair ×
10k/100k/1M vs `Array.Sort`), tick throughput under grouping, fling with `Scene.RasterVersion`
slide proofs, steady-state 0 B gates, collation-key vs ICU compare table. Control: UIHeadlessHost
cell assertions against the mockup layouts, keyboard/mouse flows, editing begin/commit/cancel,
copy, theme tiers. Adversarial audit before PR.

### 6.1 Recorded benchmark results (2026-07-18, Apple M-series, Release; `ShapingSortBenchmark`)

**Sort table** (TimSort ms / `Array.Sort` ms / ratio) — the §7-Q1 verdict: **no hybrid**. Random
costs 1.17–1.28× vs introsort (a small constant, stable across N); every mostly-sorted profile wins
big (4× at 1% perturbed, ~parity at 20%), sorted/reversed win 13–30×:

| N | random | sorted | 1% | 5% | 20% | reversed |
|---:|---|---|---|---|---|---|
| 10k | 1.53/1.28 (1.19) | 0.05/0.55 (0.08) | 0.13/0.58 (0.23) | 0.43/0.62 (0.69) | 0.94/0.90 (1.05) | 0.05/0.90 (0.06) |
| 100k | 21.0/17.9 (1.17) | 0.47/6.77 (0.07) | 1.76/7.21 (0.24) | 5.27/8.36 (0.63) | 11.7/12.8 (0.91) | 0.52/13.2 (0.04) |
| 1M | 337/288 (1.17) | 5.0/105 (0.05) | 23/94 (0.25) | 62/134 (0.46) | 161/200 (0.81) | 5.3/159 (0.03) |

**Repair vs full sort** (N = 1M): K=1 → 3.4 ms (85× vs the source-order reshape, 4.6× vs the
best-case old-view re-sort); K=100 → 1.7 ms (163×/9.1×); K=10k → 6.1 ms (52×/4.3×) — the 1/8
threshold has generous headroom, and the >⅛ fallback now re-sorts the OLD view (near-linear; the
benchmark surfaced that the original fallback sorted source order, ~18× slower — fixed).

**Live ticks**: 0.286 ms/tick at 100k sorted rows (~3,500 ticks/s, 17× under the 5 ms gate).
**Compare contract**: a 3-level fused comparison over 100k rows runs 47.9 ns/compare at exactly
0 B across 1M invocations (the ordinal-string path; culture columns ride collation keys).

## 7. Panel Q&A record

Q1 TimSort vs hybrid → TimSort + benchmark table; hybrid only if the table demands (open).
Q2 repair threshold → 1/8 default, benchmark-tuned constant. Q3 O(V) regroup per tick → NOT
acceptable at 1M [upheld]; size-gated background routing (§2.6). Q4 auto-filter editors → per-column
cell kinds; Text cells are lightweight editors, one focused editor at a time (roving), DistinctPicker
reuses the popup. Q5 sealed snapshot → the three integrity invariants (§2.6) [upheld holes].
Q6 direct-draw vs element rows → direct-draw upheld; editing hosts elements (owner mandate);
TreeListView picks per density. Q7 cell-painter seam → yes: column `CellKind`/painter seam so
custom columns don't touch the engine (DataBar/badge are the built-in painters).
