# Cursorial.UI.DataViews — the DataGrid + data-shaping design

Status: **DRAFT for panel critique** (2026-07-18). Owner intent: a robust, highly performant DataGrid
with DevExpress-WPF-grade UX (`tokyo-night-terminal-datagrid.html` is the visual spec), large-dataset
capable, with live data shaping. The shaping controller uses **runtime code generation with LINQ
expression trees** so sorting/grouping/formatting are specialized to the row type — no boxing on hot
paths. The sort must excel on **both random and mostly-sorted** data, and live updates must **re-sort
as few rows as possible**. Shaping may run on or off the UI thread (design decision below). The owner
has cleared **solution-wide changes** where they produce reusable infrastructure for future core
controls (ListView, TreeListView).

## 0. Invariants

1. **The engine is UI-free.** `Cursorial.UI.DataViews.Shaping` references BCL only — no `UIElement`,
   no dispatcher types (a thin `IShapingScheduler` seam abstracts "post back to owner thread").
   Headless-testable to the last branch.
2. **No boxing on shaping hot paths.** Keys live in typed vectors (`TKey[]`); comparisons, aggregation,
   formatting, and filter evaluation all run through expression-tree-compiled typed delegates.
   Boxing is permitted only at cold boundaries (group-key display objects, diagnostics).
3. **The view is an index space.** Shaping produces permutations/structures of `int` row ids; row
   objects are touched only by compiled accessors during key extraction and by INPC tracking.
4. **Incremental beats clever.** A live tick (K changed rows, K ≪ N) never triggers an O(N log N)
   sort — repair is O(N + K log K) worst case, O(K log N + K·moves) typical.
5. **The UI thread never hitches on a big reshape.** Full reshapes above a threshold run off-thread
   over a stable snapshot; the swap is atomic on the UI thread. Small repairs run synchronously.
6. **Rows are drawn, not realized.** The rows presenter paints cells directly through the Drawing
   layer — no per-cell (or per-row) UIElement realization. Steady-state (no data change, no input)
   frames allocate 0 B and re-raster nothing.
7. **Reusable pieces land reusable.** Anything a ListView/TreeListView will need (virtual row-host
   seam, selection controller, column primitives) is factored so a future control consumes it without
   surgery — in `Cursorial.UI` proper when it is control-agnostic.
8. **AOT posture:** the shaping codegen requires `RequiresDynamicCode` (expression-tree compile).
   A future interpreted/AOT fallback is a seam (`ShapingCodegen` is the single compile site), not v1.

## 1. Scope

### v1 (this PR)

- **Shaping engine**: typed column model + codegen (accessors, comparers, formatters, aggregators,
  filter predicates); multi-column sort with implicit row-id tiebreak; **TimSort-derived adaptive
  stable sort** over the index permutation + **incremental K-row repair**; filtering (predicate +
  per-column value sets + auto-filter conditions); multi-level grouping with per-group summaries;
  total summaries; live updates (INCC + per-row INPC, coalesced per frame); sync/background shaping.
- **DataGrid control**: explicit + auto-generated columns; header band (sort glyphs `▲▼`, filter
  affordance `▾` with active tint, keyboard: focusable header cells — Enter/Space sorts, Shift adds
  multi-sort, Alt+Down opens the filter popup); virtualized direct-draw rows (alternating rows, hover,
  row selection, focus cell, group rows with `▾/▸` + key + `(count)` + inline summaries, indent);
  vertical + horizontal scrolling with shared column offset (header/filter/rows/footer aligned);
  group panel (chips with sort glyph + `✕`, keyboard + API grouping; drag deferred); summary footer
  (incl. stacked multi-aggregate cells); filter dropdown (checklist popup with search + tri-state
  select-all); auto-filter row (per-column text conditions with operator grammar `> >= < <= = ""`);
  conditional formatting **engine + API** (data bars, threshold/predicate color rules, status tints —
  the visual language of the mockup's flat grid).
- **Theme**: code-first, ThemeKeys-spine-driven (Tokyo Night parity per the mockup), registered via
  the assembly theme-contribution tier; NoColor/Ansi16 degradations.
- **Canary**: Gallery page with a live-updating feed demo (large dataset + ticking updates).
- **Benchmarks**: sort (random / mostly-sorted / K-repair vs full), shaping throughput, steady-state
  render allocation gates.

v1 also includes **basic in-row editing** (owner mandate): the edit-row element-hosting seam in the
rows presenter + the TextBox editor path (begin/commit/cancel/Tab-advance, commit through compiled
setters) — the proof that the special case works end-to-end.

### Deferred (seams carved, not built)

The full editor suite (combo/date/spin editors, new-row template, validation UX — rides the v1
edit-row host); column chooser + header drag-reorder (command/API-level reorder exists; mouse drag
UX later); conditional-formatting **manager/rule-dialog UI** (the rules API is v1; the dialogs
later); the expression **language** (filter builder tree + text editor + IntelliSense — the filter
model is expression-shaped so a parser can target it later); master-detail; frozen columns; cell
selection ranges (cell FOCUS is v1; multi-cell selection later).

## 2. The shaping engine (`Cursorial.UI.DataViews.Shaping`)

### 2.1 Row store and identity

`RowStore<T>` mirrors the source into **stable slots**: `T[] _rows` grown geometrically, free-list
reuse, `int` row id = slot index (never shifts on insert/remove). Key vectors align to slots, so
adding/removing rows never re-extracts other rows' keys. INCC events map to slot ops; Reset rebuilds.
A `Dictionary<T,int>` (reference equality) maps row → id for INPC dispatch (reference-type rows;
duplicate instances documented unsupported for INPC tracking).

### 2.2 Columns and codegen (`ShapingCodegen`)

`ShapedColumn<T,TKey>` (behind non-generic `ShapedColumn`) carries the compiled kit:

- `Func<T,TKey> Getter` — from a property path (`Expression.Property` chain, null-propagating for
  reference hops) or a caller lambda.
- `Comparison<TKey>` — specialized: `IComparable<TKey>.CompareTo` inlined for value types (no
  interface dispatch through constrained call), configured `StringComparison`/culture for strings,
  null-first for `Nullable<>`/reference keys.
- `Func<TKey,string> Formatter` — format string + culture compiled against the concrete key type
  (`v.ToString("$#,##0", ci)` — no box).
- Aggregators — per requested aggregate, compiled loops over `(int[] view, int start, int count,
  TKey[] keys)`; Sum/Avg via widened accumulators (long/double/decimal per key type), Min/Max via the
  column comparison, Count trivially.
- Filter kit — value-set membership (`HashSet<TKey>.Contains`), auto-filter condition compilers
  (operator + literal parsed per key type → `Func<TKey,bool>`).

The **multi-column comparer** is one compiled `Comparison<int>` reading the active key vectors in
sort order with per-level direction, ending with the **row-id tiebreak** (`a - b`): a total order, so
the sort needs no stability guarantee, binary-search positions are unique, and results are
deterministic. All compilation happens once per shape change, cached per `(rowType, path, culture,
options)`.

### 2.3 Sort — adaptive full sort + incremental repair

- **Full sort**: TimSort over `int[]` (natural-run detection, galloping merges) with the compiled
  `Comparison<int>`. O(N) on sorted/reversed input, O(N log N) worst, and the run detection is what
  makes "mostly sorted" cheap — exactly the profile after a burst of live updates or a re-sort of an
  almost-ordered column. (Benchmarked against `Array.Sort` introsort; the benchmark table is a
  deliverable — if introsort wins on pure random by more than TimSort wins on mostly-sorted, the
  full-sort entry point picks by a presortedness probe.)
- **Incremental repair** (the live-update path): dirty row ids accumulate in a `DirtySet` (bitset +
  list). Per coalesced update cycle: (1) one sweep partitions the view into clean (order preserved —
  already sorted) and dirty; (2) re-extract dirty keys; (3) sort the K dirty ids; (4) single merge
  pass clean+dirty → new view. O(N + K log K), allocation-free against pooled scratch buffers.
  Inserts and un-filtered rows join the dirty set; removes/filtered-out rows drop in the sweep.
- **Threshold**: when K > ~N/8 the repair degenerates; fall back to full TimSort (which is itself
  adaptive on the mostly-sorted result).

### 2.4 Filter

An AND-composed visibility verdict per row: programmatic predicate (compiled from
`Expression<Func<T,bool>>` or delegate) ∧ per-column value sets ∧ auto-filter conditions. Stored as
a bitset over slots; evaluated fully on filter change, per-row on live ticks. The view permutation
contains visible rows only. Distinct-value lists for the checklist popup come from the key vector
(typed dedupe → formatted, with counts).

### 2.5 Grouping and summaries

Grouping prepends the group columns to the sort description. Group structure derives from the sorted
view in one O(V) walk using compiled per-level `SameGroup(a,b)` equality: `GroupNode { Level, Parent,
FirstViewIndex, RowCount, FormattedKey, Aggregates[] }`. Collapse state keys on the formatted key
path (survives reshape). The **flattened view** (what the grid binds) is an `int[]` of packed entries
(data row id | group node index) rebuilt O(V) per reshape — cheap relative to sort, so incremental
group repair is deliberately NOT attempted in v1 (repair fixes the sort; groups re-derive).
Summaries: per-group aggregates computed over group ranges (compiled, typed); grand totals over the
view. Only groups whose membership changed recompute on a live tick.

### 2.6 Live updates and threading (the design decision)

The controller's public API is owner-thread-affine. Internally:

- **Small work runs synchronously** (the common live tick): K-repair + regroup + aggregate patch +
  view swap, all on the UI thread inside the coalescing window — sub-millisecond at 100k rows.
- **Big work runs in the background**: full reshapes (sort/filter/group change, Reset, bulk load)
  above `BackgroundThreshold` (default 32Ki rows) execute on the ThreadPool against a **sealed
  snapshot** (slots are stable; the shaper reads key vectors it extracted before departure). Source
  mutations arriving mid-shape queue as pending ticks; on completion the result publishes to the
  owner thread (via `IShapingScheduler.Post`), pending ticks replay as ordinary incremental repairs.
  A superseding reshape request cancels the in-flight one (`CancellationToken` checked between merge
  passes).
- The published artifact is a `DataViewSnapshot` (permutation + groups + summaries + version); the
  grid reads only the published snapshot — never intermediate state. One shape at a time; version
  stamps make stale publishes detectable and droppable.

INPC: one shared `PropertyChangedEventHandler`; the sender resolves to a row id; changed property
name filters against shaped columns (a tick on an unshaped column only invalidates formatting for
that row). Coalescing: ticks mark dirty and schedule one repair per frame (the scheduler seam lets
the UI adapter align this with the frame loop's update phase).

### 2.7 Conditional formatting engine

`FormatRule` variants (all API-level in v1): **DataBar** (column min/max anchors from the key vector,
fill fraction → block-glyph bar), **Threshold** (ordered predicate → fg/bg/attrs, the `▲●▼` badges),
**Predicate** (compiled row predicate → row-level format, the "dim Cancelled" case). Rules compile
against typed keys; evaluation happens at paint for visible rows only, no caching (40×8 cells/frame).
`GetCellFormat(viewIndex, col)` returns a struct; no allocation.

## 3. The DataGrid control (`Cursorial.UI.DataViews`)

*(Integration facts below are scout-verified against the existing engine — file refs in the scout
maps under the session notes; the load-bearing ones are restated here.)*

### 3.1 Anatomy (template parts)

```
DataGrid : Control                      Focusable one-tab-stop; HandlesScrolling => true
├─ PART_GroupPanel     GroupPanel                  (chips; collapses when empty + grouping off)
├─ PART_Header         DataGridHeaderPresenter     (direct-draw header cells + sort/filter glyphs)
├─ PART_AutoFilterRow  DataGridAutoFilterRow       (optional; per-column condition editors)
├─ PART_ScrollViewer   ScrollViewer
│   └─ (SCP)           DataGridRowsPresenter       (direct-draw viewport; IScrollContentHost +
│                                                    ILogicalScrollHost content of the SCP)
└─ PART_Footer         DataGridSummaryPresenter    (direct-draw summary band, stacked cells)
```

**Scrolling rides the existing spine** — no new scroll machinery. The rows presenter is the
`ScrollContentPresenter`'s content and implements the public `IScrollContentHost` +
`ILogicalScrollHost` seam (the `VirtualizingStackPanel` precedent): `IsScrollClient = true`,
`IsLogicalScroll = true`, `GetExtent() = (totalColumnWidth, viewRowCount)`. **Every view row is
exactly one cell row** (data, group, and stacked-summary rows alike — the footer band is outside
the viewport), so `EstimateItemAt(r) = r`, `BringItemIntoView(i) = (0, i, width, 1)` — no prefix
sums, no height cache, no refinement loops. The SCP's banded scenes (viewport + 2K rows) make
in-band scrolling a **pure composite slide** (zero Render calls, the invariant-3 contract);
re-anchor calls `InvalidateRealization()` → the presenter re-fills its band cache next measure.
ScrollViewer supplies bars/wheel/`EnsureVisible`; `HandlesScrolling => true` on the grid keeps
arrow keys out of the ScrollViewer. Horizontal axis: SCP full-content-width scenes (v1 — grids are
typically ≤ ~300 cells wide; in-presenter column virtualization is a recorded seam for extreme
column counts). Header/filter/footer presenters mirror `ScrollViewer.HorizontalOffset` for aligned
column shift (their 1–2-row re-raster on H-scroll is trivial).

### 3.2 Rows presenter — direct draw over the band

`MeasureOverride` (the VSP-sanctioned self-mutation site) snapshots the band window
`[BandStartRow, BandStart+BandLength)` and **pre-composes** the band's row content from the
`DataViewSnapshot` (formatted cell strings via the compiled formatters, group-row captions,
conditional-format verdicts) into a pooled band cache — render never allocates or touches row
objects. `Render` draws from the cache: background (alternation/hover/selection/group tint via
`FillOpaque`) → per-column cell runs (`DrawText` spans, grapheme-truncated to column width — there
is no clip stack inside `Render`, truncation is the painter's job) → data bars (`█░` runs) → badges
→ focus-cell well-fill → group rows (indent, `▾/▸`, key, `(count)`, right-aligned summary).
Invalidation: data ticks re-fill the band cache (dispatcher side, never inside `Render` — the
render pass is guarded read-only) and `InvalidateVisual()`; the whole band re-rasters (~3×viewport
rows — the granularity contract).

**In-row editing is the sanctioned element-hosting special case** (owner mandate 2026-07-18):
painting rows directly is the general rule, but the ACTIVE edit row hosts real editor
`UIElement`s. The presenter supports visual/logical children for exactly this: an
`DataGridEditRowHost` child arranged at the edit row's content position (cells at column
x-ranges), painting above the drawn rows (parent-before-children render order), hit-testable
(children of the hit leaf stay hittable), scrolling naturally with the content (its bounds are
content-frame). Entering edit mode realizes the host + per-column editors (v1: the TextBox
editor — F2/Enter/double-click begins, Enter commits through the column's compiled setter,
Esc cancels, Tab advances cell; combo/date/spin editors + the new-row template are the editing
suite's next phase, riding the same host). Exiting tears the host down; the row repaints from
data. The band cache skips the hosted row while editing (the editors own those cells).

Rows are otherwise **not elements**: the presenter is the single hit leaf. `OnMouseDown/Move/Wheel` +
`e.GetPosition(this)` arrive in content coordinates (the chain folds `ChildScrollOffset*`), so
row = y, column = x-range lookup — the TextBox/ScrollBar sub-region idiom. Hover row is presenter
state; pseudo-classes/styles cannot target drawn rows, so state looks come from **styled brush/
attribute properties on the presenter** (e.g. `SelectionBackground`, `RowAlternationBackground`,
`DataBarFill`…) that the theme sets via `SetResource` setters — `AffectsRender`-registered, so a
palette flip re-inks. NoColor tier: selection/current-row cues fold to Inverse/Bold via the
`TextElement` per-axis attributes composed at paint.

### 3.3 Interaction model

- **Selection**: row selection (single + Shift range + Ctrl toggle), focus cell (row,col) with the
  mockup's well-fill; `SelectedItem(s)`/`SelectedIndex` in row-object terms.
- **Keyboard**: Up/Down/PgUp/PgDn/Home/End (+Ctrl variants) move the focus row; Left/Right move the
  focus cell; on a group row Left/Right collapse/expand, Enter toggles; Space (+Shift/Ctrl) selects.
  The header band is reachable (Ctrl+Up from row 0 / Tab stop): Left/Right walk header cells,
  Enter/Space sorts (Shift+Enter appends multi-sort level), Alt+Down opens the filter popup,
  Ctrl+G groups by the focused column.
- **Mouse**: click selects / focuses cell; header click sorts (Shift+click appends); `▾` click opens
  the filter popup; expander click toggles; wheel scrolls; group-panel chip `✕` ungroups.

### 3.4 Filter surfaces

The checklist popup (Popup + search TextBox + ListBox of distinct values with counts + tri-state
select-all + OK/Cancel) writes a column value-set filter. The auto-filter row hosts one lightweight
text editor per column; text parses through the column's condition grammar (`>`, `>=`, `<`, `<=`,
`=`, bare text = contains/starts-with per column option) into a typed condition. Active filters tint
the header `▾` amber (the mockup's active state).

## 4. Theming

`CursorialDataViewsTheme` (code-first) + `DataViewsThemeModule` `[ModuleInitializer]` into the
assembly theme-contribution tier (the Bars pattern). All brushes ride the core `ThemeKeys` spine +
a small set of grid-specific keys (row-alternation, data-bar fill/track, heat tints) added to
`ThemeKeys` with values per tier dictionary; NoColor tier degrades: selection→Inverse, group rows→
Bold, data bars keep glyph shape, badges keep `▲●▼` glyphs (shape carries meaning without color —
the mockup's glyph choices are deliberately color-independent).

## 5. Reuse posture (scout-corrected)

The infrastructure I planned to invent **already exists** — the design reuses it rather than
duplicating:

- **Scrolling/virtualization seam**: `IScrollContentHost`/`ILogicalScrollHost` (public) + SCP
  banding — the rows presenter implements it (§3.1). The element-realizing virtualization lane
  (`ItemContainerGenerator` V0–V5 + `VirtualizingStackPanel`) stays untouched; the DataGrid's
  direct-draw lane is a *deliberate deviation* for cell-density + 1M-row reasons (a per-row
  element tree at 9 columns × band rows costs layout/styling per re-anchor that a painter doesn't),
  documented here as the second sanctioned virtualization idiom — TreeListView/ListView pick per
  their density.
- **Selection**: `SelectionModel` (public, index-space, structural-shift ops) is the row-selection
  state machine as-is; the DataGrid maps gestures itself (it does not derive
  `SelectingItemsControl` — its rows aren't containers; the thin gesture mapping is ~30 lines).
- **The shaping engine is presentation-free**: ListView = the flat path over the same controller;
  TreeListView = hierarchy adapter (parent/child accessor instead of group-by) over the same sort/
  filter/format kits — the engine's view composition is already "rows + structural rows", which is
  exactly the tree flatten.
- **Solution changes**: `Cursorial.UI.csproj` gains `InternalsVisibleTo` for
  `Cursorial.UI.DataViews` (+ `.Tests`) — the Bars precedent; anything deeper (promoting a
  `private protected` member) is flagged in review notes when hit, not pre-emptively.

## 6. Testing & verification

Repo conventions apply in full: a normative **`dataviews-matrix.md`** (numbered rows, one test per
row, pinned-decision `DV` ledger) authored ahead of each implementation stage; test project
`Cursorial.UI.DataViews.Tests` (RootNamespace `Cursorial.Tests.UI.DataViews`, serialized assembly,
UIHeadlessHost substrate, the allocation-determinism csproj knobs from `Cursorial.UI.Tests`).

- Engine: sort correctness vs reference incl. adaptive paths; **repair ≡ full-sort equivalence under
  randomized tick streams** (the property-style oracle: shaped output ≡ LINQ reference); filter/
  group/summary oracles; INCC/INPC pipelines; background-shaping determinism via a stub scheduler.
- Benchmarks (ND25 methodology: warm → SettleJit → best-of-5 timing / worst-rep allocation; the
  `VirtualizationFlingBenchmark` median-re-anchor + steady-state-bytes patterns): sort random /
  sorted / mostly-sorted (1%, 5%, 20% perturbed) / K-repair, N ∈ {10k, 100k, 1M}, vs `Array.Sort`
  baseline — table recorded here; live-tick throughput; scroll fling with `Scene.RasterVersion`
  slide proofs; steady-state 0 B gates.
- Control: UIHeadlessHost integration (cell assertions against the mockup's layouts, keyboard/mouse
  flows, group expand/collapse, filter surfaces, live-tick repaint, theme tiers NoColor/Ansi16/RGB).
- Adversarial audit before PR (project convention).

## 7. Open questions for the panel

1. TimSort-only vs presortedness-probe hybrid for the full sort? (Or: implement TimSort, benchmark
   against `Array.Sort`, decide by table?)
2. Repair threshold K/N — fixed fraction or measured crossover?
3. Group re-derive always O(V) per tick — acceptable at 1M rows, or does v1 need incremental group
   repair?
4. Auto-filter row editors: one TextBox per visible column vs a single roving TextBox over a drawn
   filter line?
5. Background snapshot: sealed key vectors + pending-tick replay (chosen) vs full copy-on-write —
   holes?
6. Direct-draw vs element-realizing rows: any reason the existing `ItemContainerGenerator`
   virtualization should host grid rows instead (per-row containers, cells drawn by the row
   element)? The middle ground (row = element, cells = drawn) trades band re-anchor cost for
   reusing container idioms (`:alternate`, ISelectableContainer) — is the full-painter approach
   right?
7. The engine publishes formatted strings in the band cache — should Kind-specific cell painters
   (data bar, badge) be a column-type seam (`ICellPainter`) from day one so custom columns don't
   need engine changes?
