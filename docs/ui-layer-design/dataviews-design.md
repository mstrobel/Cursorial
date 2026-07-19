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

> **Disposition (2026-07-19):** everything below shipped in Wave 2 (§9) or the post-merge
> live-canary rounds, except a small residue — the open remainder is tracked in §10.

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

## 9. Wave 2 (post-merge; the deferred list — design addendum, 2026-07-18)

> **Status (2026-07-18): LANDED — all of §9.1–§9.6 shipped** on `dataviews-datagrid`, built as four
> packages (editor suite + column UX; the expression language; builder/rules dialogs; engine
> extensions) plus the structural package (§9.2 H-scroll/frozen, §9.3 master-detail, §9.4
> cell-range, the §9.6 span-formatter band cache and SCP band-window promotion). Deviations from
> this spec are none of substance; two implementation notes: (1) the §9.3 expander gutter landed as
> `DataGridColumnLayout.GutterWidth` — a synthetic pinned leading REGION inside `FrozenWidth`
> rather than a synthetic `Entry` (an Entry carries the CellPadding invariant a 2-cell gutter can't
> honor; every presenter + hit map still inherits it through the layout, which is what the pin was
> for); (2) `TopBottomRule` evaluation compiles a rank predicate ONCE per rule (the column's own
> sort comparison over a boxed per-publish threshold from `TryGetTopKThreshold`) instead of
> re-compiling a condition per publish. Tests: `DataGridStructuralTests` / `DataGridDialogsTests` /
> `Shaping/{GroupSummarySort,StructRow,TopKThreshold,CriteriaExpression}Tests`.
>
> **Closeout addenda (live-canary + adversarial audit, same wave):** the §3.3 keyboard model
> landed in full — the F6 / Ctrl+Up band cycle with VIRTUAL focus over the header (Left/Right
> walk, Enter cycles the sort, Space appends a level, Alt+Down opens the filter popup, Ctrl+G
> groups), the group panel chips (Enter direction, Delete, Ctrl+Left/Right reorder), and the
> auto-filter cells; a rows-area **context menu** (right-click / Menu key) is the reachability
> surface for the filter dialogs, conditional formatting, the column chooser, per-column
> **summaries** (`ToggleSummary`, Sum/Average gated on numeric keys) and copy. §3.3's
> "Shift+click appends — mouse chords are wire-reliable" was WRONG for Shift: terminals reserve
> Shift+click for native selection while mouse tracking is on, so **Ctrl+click** is the reliable
> append chord (Shift+click still works where delivered). The auto-filter grammar gained `%xyz` /
> `xyz%` / `%xy%` wildcard forms (ends/starts/contains — `FilterOperator.EndsWith` is new) and
> `!` / `!=` not-equal aliases. A 6-dimension adversarial audit over the full wave-2 diff produced
> 23 confirmed findings (3 critical, 6 major), each fixed with a regression test — notably: the
> struct-row new-row commit's cold re-attach missing the §9.6 degrade; stale-rowId edits
> resurrecting freed slots (the `RowStore.IsLive` liveness truth); focus now genuinely re-anchors
> by row id per publish (the substrate the §9.4 prune reads); cell-range corners joined the
> row-id hygiene fan-out; the band presenters' frozen overpaint gates on `FrozenWidth` (the
> gutter is pinned without any Fixed column); and detail-pane height refinements re-dirty the
> band so the fixpoint refills under the corrected content-y map.
>
> **The gesture sweep (2026-07-19, post-merge live-canary program):** a 6-surface promised-vs-
> implemented audit of every input gesture (probe-tested headlessly, each finding adversarially
> verified — 21 confirmed, 0 refuted) closed the full set: **edit sessions are ROW-ID-anchored**
> (the editor rides its row through live churn; commits write the id, never a stale view slot —
> the gallery's "committing didn't work"); the §9.2 **click-away displacement policy** (a press
> outside the open cell/filter editor commits-else-cancels before landing — no more stranded
> editors swallowing the keyboard); header drags are **partition-honest** across the frozen
> boundary (slots clamp to the drag column's partition; a rejected group drop cancels instead of
> reordering); the **group-panel chips drag** (reorder in-band, ungroup below, press-release
> semantics so a promoted drag never also toggles/removes); the **chooser's drag-to-show** landed
> (the mockup headline: a hidden chip drags onto the header, the header adopts the gesture via
> capture hand-off, release inserts AT the slot); the rows **context menu anchors at the pressed/
> focused cell** (Pointer placement for right-click; bottom-edge-translated offsets for the Menu
> key) and right-click focus covers group rows + the placeholder; the reachability keys (Menu/F6/
> Ctrl+Up) survive an EMPTY view; plain Home/End jump rows; band focus repairs when its band
> empties/hides; `AllowDelete` + Delete + a menu lane (opt-in, unpromised parity); the drawn ▲▼
> spin steppers are clickable; the auto-filter editor seeds only grammar-round-trippable text
> (a checklist digest no longer destroys the InSet on Enter), scrolls clear of the frozen region
> on the mouse path, and the checklist's tri-state (Select All) checks all from partial; the edit
> bar's Tab hint says "commit row" on the new-row session.

### 9.1 The expression language (`Shaping/Expressions/`)

The mockup's criteria grammar, one parser serving the filter editor, the Filter Builder, and CF
Expression rules:

```
expr        := or
or          := and ('Or' and)*
and         := not ('And' not)*
not         := 'Not' not | comparison
comparison  := additive ( ('='|'<>'|'<'|'<='|'>'|'>=') additive
                        | 'In' '(' additive (',' additive)* ')'
                        | 'Between' additive 'And' additive
                        | 'Like' additive )?
additive    := multiplicative (('+'|'-') multiplicative)*
multiplicative := unary (('*'|'/'|'%') unary)*
unary       := '-' unary | primary
primary     := '[' field ']' | number | 'quoted string' | #date# | true|false|null
             | function '(' args ')' | '(' expr ')'
functions   := Contains|StartsWith|EndsWith|Upper|Lower|Len|Trim|Abs|Round|IsNull|IsNullOrEmpty
```

Keywords case-insensitive; positions on every token (the validation strip's column numbers). The
pipeline: `ExpressionParser.Parse(text)` → a positioned AST + diagnostics; `ExpressionCompiler`
lowers the AST to a typed `Expression<Func<TRow,bool>>` over the column set (field refs bind by
FieldName/EffectiveHeader; numeric promotion int→long→decimal→double on mixed operands; string
relational ops honor the column's SortMode comparison; `Like` translates `%`/`_` wildcards to a
compiled regex; `Between` inclusive) → `FilterNode.Custom`. `AstToFilterNode` recognizes the
tree-shaped subset (And/Or over simple field-op-literal comparisons) for Filter Builder round-trip;
`FilterNodeToAst`/`AstToText` complete the loop (Builder ⇄ text). Non-boolean roots and unknown
fields/functions are positioned diagnostics, never throws past Parse.

**Panel amendments (implemented):** ONE semantic authority — the compiled lane adopts the ENGINE's
comparison semantics (string relational/equality via the involved column's SortMode; nullable
relational via null-first total order — `[A] < 5` is TRUE for a null A, exactly the Condition
lane; `= null` ≡ null-ness). Structural lowering to Condition/InSet leaves is guarded by literal
EXACTNESS (round-trip through the key type — `[IntCol] = 2.5` stays compiled so ChangeType can
never round it to `= 2`). Field binding: exact FieldName wins → unique display alias → ambiguity
is a positioned diagnostic. Literals parse invariant (numbers; `#date#` ISO-first) so saved
filters are portable; `Like` gains `[%]`/`[_]` literal escapes and its regex case-ness follows the
column mode. The compiled-predicate fallback (`FilterNode.Custom`) does not text-round-trip — the
ORIGINAL SOURCE TEXT is retained alongside the filter by the editor surfaces (the builder shows a
read-only "expression" row for it; a future source-carrying FilterNode is the recorded upgrade).

### 9.2 In-presenter horizontal scrolling = frozen columns + column virtualization (ONE item)

Frozen columns are IMPOSSIBLE under SCP horizontal scrolling — the compositor slides the whole
band scene, so a "fixed" column would slide with it. The fix is the already-recorded column-
virtualization seam: **the rows presenter takes ownership of the horizontal axis**. The SCP scrolls
vertically only (`CanScrollHorizontally=false`; horizontal extent = viewport); the grid's existing
shared `HorizontalOffset` becomes the one horizontal truth (the ScrollViewer's H-bar binds to it);
every band presenter already draws shifted by it — the rows presenter now does the same (drawing
only columns intersecting the viewport — column virtualization for free) instead of letting the
scene slide. Scenes shrink from content-width to viewport-width (a memory win at wide grids).
`DataGridColumn.Fixed { None, Left }`: fixed columns resolve first at x 0..F and draw LAST at
UNSHIFTED x (overpaint — the painter fills their background, no clip stack needed); scrolling
columns draw shifted, SKIPPING cells that would start under the frozen region (truncate at the
boundary). Hit-testing: x < frozenWidth → fixed lookup, else x+offset. Header/filter/footer mirror
the same split.

**Panel amendments (the mechanism, corrected against the shipped code):**
- The one horizontal truth is a new `DataGrid.HorizontalOffset` styled property, clamped to
  `[0, max(0, TotalWidth − viewportColumns)]` at set time AND re-clamped after the presenter's
  measure resolves `ColumnLayout` (the SCP end-of-arrange re-coercion analog — a hide/resize while
  scrolled right snaps back the same frame). All four band presenters re-bind to it; the filter
  popup's anchor math reads it.
- The ScrollViewer CANNOT host the H-bar (its bar wiring pins to the SCP offset, which coerces to 0
  once `CanScrollHorizontally=false`): the template docks a **grid-owned horizontal `ScrollBar`
  part** bound to `DataGrid.HorizontalOffset` (shown only when TotalWidth > viewport).
- `DataGridRowsPresenter.GetExtent` reports viewport columns (the host's obligation — the SCP
  publishes GetExtent verbatim on both axes).
- Wheel: `DataGrid.OnMouseWheel` owns Shift+wheel/`WheelDeltaX` (routes into `HorizontalOffset`,
  handled even at the extremes so an outer scroller never captures the gesture mid-grid).
- The presenter's H-offset registers **AffectsMeasure** (not just render): hosted children — the
  cell editor, §9.3 details — arrange at `entry.X − HOffset` and must re-arrange per tick (the
  band cache's early-out keeps the re-measure a no-op).
- **Hosted-children policy over the frozen region** (children paint OVER drawn cells and steal
  hits — overpaint cannot clip them): `BeginEdit`/Tab-advance first auto-scroll the target cell
  clear of the frozen width; an H-scroll that would push a hosted editor under the frozen region
  commits it (cancel on commit-failure); §9.3 detail elements are horizontally viewport-anchored
  (arranged at x=0 viewport-wide, never shifted).
- `ScrollColumnIntoView(columnIndex)`: Fixed → no-op; scrolling → minimal scroll of the entry into
  `[HOffset + frozenWidth, HOffset + viewportWidth)`, leading-edge-aligned when wider; called from
  focus-cell moves, `SetFocusCell`, `BeginEdit`/Tab-advance, and header virtual-focus walks.
- Cost, honestly: an H-tick re-rasters the BAND scene (≈3× viewport rows — the vertical
  composite-slide contract requires every band row valid). Acceptable: hover already whole-band
  re-inks per move, H-ticks are low-frequency, and ticks must invalidate RENDER/ARRANGE only —
  never the band cache (per-row strings are offset-independent).

### 9.3 Master-detail

`DataGrid.DetailTemplate : DataTemplate?` + a 2-cell expander gutter column (drawn `▶/▼`) when set.
**The engine stays 1-row-per-entry** — detail geometry is presenter-side: an expanded-set map
(rowId → hosted detail element + measured height, sorted by view position per snapshot) turns
view-index↔y into prefix-sum arithmetic (`y = viewIndex + Σ heights(expanded above)`; the expanded
count is small — linear/binary over it is trivial). Extent = snapshot.Count + Σ heights;
`EstimateItemAt` inverts the map. Detail elements are hosted children (the editing precedent,
N-at-once): realized while their anchor row is in/near the band, released outside it and when the
row id leaves the view (refilter/removal). `DataContext` = the row object; the template builds per
expansion (fresh subtree — the DataTemplate contract). Keyboard: the expander cell via
Left/Right-on-gutter or Ctrl+Right/Left; mouse: expander click.

**Panel amendments:** the band window itself is CONTENT-Y space — one bidirectional map
(viewIndex→yStart prefix sums over the sorted expanded set; y→viewIndex-or-detail inverse) routes
EVERY conflated site: `FillBandCache`'s window walk, `Render`'s y loop, `HitCell`, `EstimateItemAt`,
`BringItemIntoView`, `ScrollRowIntoView`, `PageStep`, the edit host's arrange row, and the focus
math. Detail realization predicate = "detail y-range intersects the band (± slack)" (an anchor row
outside the band with its detail inside is the common tall-detail case); a detail's arrange rect
may exceed the band (the scene crops). Heights capture at child measure inside `MeasureOverride`;
`InvalidateScrollExtent` fires only on an actual Σheights delta (the VSP refinement discipline —
convergence under the 16-pass fixpoint). Focus: a "focus is within a detail host" stand-down guard
in `OnKeyDown` (the popup/editor precedent); Ctrl+Down enters the focused row's detail, Esc
returns. The expander gutter is a SYNTHETIC first `ColumnLayout` entry (all presenters + hit math
inherit it). Value-type rows: the detail `DataContext` is boxed ONCE per expansion build.

### 9.4 Cell-range selection

`DataGrid.SelectionUnit { Row, Cell }`. Cell mode: ONE rectangular range (the DevExpress default;
multi-range deferred). **Panel-corrected model — corner truth:** the range IS
`(anchorRowId, leadRowId, anchorColumn, leadColumn)` with the COLUMN EDGES keyed by
`DataGridColumn` identity (never visible index — the column-UX package reorders/hides at runtime);
membership derives per snapshot from the re-projected corners (an id→viewIndex inverse map
maintained per publish — the same substrate the stale view-space anchor/focus already need).
Reshapes legitimately change membership (the Excel/DevExpress semantic); a corner whose row id
leaves the view collapses the range to the focus cell; a hidden endpoint column clamps to the
nearest visible. Group rows are never members (the lead passes THROUGH them keeping its column).
Mode switch clears both selections and keeps the focus cell. Shift+arrow/click extends; Ctrl+C
copies the rectangle as TSV; Ctrl+A stays row-mode-only. Row mode keeps the v1 controller.

### 9.5 Group ordering extensions (engine)

`GroupDescription.OrderBySummary : SummaryDescription?` (+ `SummaryDirection`). **Panel-pinned
two-array discipline:** `_sortedView` stays KEY-ORDERED forever — it is the repair/fallback
substrate (the gallop merge's precondition is comparison order; permuting it would corrupt every
subsequent tick). The summary ordering is a PER-PUBLISH PROJECTION inside `PublishFromSorted`:
derive nodes on the key-ordered view → aggregate → permute sibling segments into a projection
array → flatten from the projection. `DeriveAndFlatten` splits into derive + flatten passes
(aggregation and the permutation run between them); collapse-state `PathKey`s are order-independent
(they chain formatted keys, not positions) and survive. `CompileShape` compiles each grouped
level's `OrderBySummary` aggregator whether or not it is displayed. Group `Direction` independent
of data sorts already holds structurally; pinned by test. TopBottom CF rules ride a `TopK` addition
to the stats block (a bounded selection pass per rule, recomputed with stats).

### 9.6 Struct rows / misc

**Struct rows (panel-scoped honestly):** the `where T : class` constraint relaxes to runtime
guards — `AttachSource(liveUpdates: true)` THROWS for value-type rows (no INPC identity; the
row↔slot map is already null for value types — no `IRowIdentity` seam needed for this opt-in), and
editing takes a position: `TrySetCellFromText` writes through a new `RowStore.SetRow(slot, row)`
write-back (the setter mutates a boxed COPY otherwise — a silent no-op); `GetRowObject` boxes fresh
per call, so rowId is the ONLY identity for value-type rows (id-keyed consumers never round-trip
through it). Span formatters wire into the band cache (pooled char buffers replace per-cell
strings). The SCP band window (BandStartRow/BandLength) promotes into `IScrollContentHost` as a
`GetRealizationWindow()`-style surface (the recorded IVT follow-up; solution-wide change).

## 10. Open deferrals (live-canary addendum, 2026-07-19)

The still-open remainder of §1's "Deferred" list plus the gaps recorded since, each with where it
is recorded. (Independent group `Direction` is NOT here — it shipped, §9.5, pinned by test.)

1. **Multi-range cell selection** — `SelectionUnit.Cell` is ONE rectangle (the DevExpress
   default); Ctrl-accumulated additional ranges later (§9.4).
2. **Cell validation hooks** — the one editor-suite slice that never shipped: commit refuses
   unparseable text, but there is no `CellValidating`-style event, per-column validator, or
   error-cue UX.
3. **Expression-editor syntax highlighting + IntelliSense** — v1 is the live validation strip +
   Columns/Functions token inserters (`DataGridExpressionEditor` header doc: "the v2 surface").
4. **Multi-entry `ThresholdRule` editing** — the rule editor edits the FIRST entry only (a
   3-entry shape deliberately reads as the Icon Set preset); the rules manager still lists and
   orders every entry (`DataGridRuleEditor` seed logic).
5. **`Between` in the Highlight pane** — the engine and criteria language support it; the pane
   lacks the two-bound UI (`DataGridRuleEditor` operator table).
6. **Preset-only formatting pickers** — the rule editor offers 4 fixed color-scale presets and
   the fixed ▲●▼ icon set; the ENGINE accepts arbitrary 2/3-stop lists and any `CellFormat.Icon`
   glyph, so this is dialog surface only.
7. **Icon glyphs on data-bar cells** — a cell showing a bar skips its verdict's icon: per-row
   icon presence would wobble the column-uniform track origin (`DrawDataCell`).
8. **Hand-built `PredicateRule` re-edit** — a code-authored lambda rule has no `SourceText`, so
   the expression field seeds empty; text-authored rules round-trip (`PredicateRule.SourceText`).
9. **Nerd Font icon tier** — the rules-manager toolbar `IconCarrier`s ship the emoji + text-floor
   tiers only, pending a verified glyph audit (no guessed PUA codepoints).
10. **The SCP band-window IVT promotion** — `BandStartRow`/`BandLength` reach the presenter via
    `InternalsVisibleTo`; the recorded follow-up is a `GetRealizationWindow()`-style
    `IScrollContentHost` surface (solution-wide change, §9.6).
