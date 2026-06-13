# S1 — oracle-pinned layout matrix (tree, layout, render zones, scroll)

Status: **normative test specification**, authored 2026-06-11 *before any tree/layout code exists* (design doc §14 P1: "WPF-derived oracle layout matrix"; the repo's matrix-first discipline, mirroring `precedence-matrix.md`). Every numbered row below becomes exactly one xUnit `[Fact]`/`[Theory]` in `Cursorial.UI.Tests` (test authoring contract at the end). The S1 implementation is written *to* this matrix; a red row is an implementation bug unless a PR amends this file first.

Canonical semantics sources, in precedence order: `docs/ui-layer-design.md` §5 (+ §0 invariants, §13 resolutions, §14 P1) **over** `docs/ui-layer-design/spec-tree-layout.md`. Three places the spec is superseded by the doc and this matrix pins the doc's side: ① the **banded scroll-scene policy** replaces the spec's extent-sized scenes + `SceneBudgetCells` budget fallback (doc §13.2, §5.7 — LD13); ② the boundary-predicate list includes **`CompositeClip != null`** (doc §5.5 predicate ⑤; absent from the spec's list); ③ the scrollable-axis measure constraint is **`LayoutLimits.MaxScrollExtent`, not `Unbounded`** (doc §12 scrolling note — L202).

Stage mapping (doc §5.12): §§1–8 gate **T1** (LayoutMath, Measure/Arrange, LayoutManager, StackPanel/DockPanel/Canvas), §§9–10 gate **T2** (Grid, WrapPanel), §11 gates **T3** (zones, RenderTree, hit testing), §12 gates **T4** (ScrollContentPresenter), §13 gates the **P2.6 signed-margin batch** (LD19 — rows binding from 2026-06-11, amended alongside L37). Rows for a later stage may stay unimplemented (not red) until that stage opens, but the rows themselves are binding from now.

## 0. Conventions

### 0.1 Fixture

| Symbol | Meaning |
|---|---|
| `Probe(w×h)` | leaf `UIElement` subclass; `MeasureOverride` returns `(w, h)` **unconditionally** (rigid content — ignores the constraint); default `ArrangeOverride` (returns `finalSize`). Records every measure constraint, `MeasureOverride`/`ArrangeOverride`/`ApplyTemplate`/`Render` invocation (counts + arguments). |
| `Fit(w×h)` | `Probe` variant whose `MeasureOverride` returns `min(natural, constraint)` per axis (constraint-respecting content, used where the WPF oracle assumes it). |
| `Root` | the P1 single-root harness: `Probe`-instrumented root attached as visual root with a `LayoutManager`; `layout(W×H)` = `RunLayoutPass(Size(W, H))` (measures the root with `(W, H)`, arranges to `(0, 0, W×H)`). |
| `slot(rect)` | a minimal test container that measures its child with the container's own constraint and arranges the child into exactly `rect` (isolates the child-side arrange contract). |
| `SP` / `DP` / `Cnv` / `G` / `WP` / `SCP` | `StackPanel` / `DockPanel` / `Canvas` / `Grid` / `WrapPanel` / `ScrollContentPresenter`. |
| `U` | `LayoutMath.Unbounded` (= `int.MaxValue`). `MaxExtent` = `LayoutMath.MaxExtent` (= 65535). |
| `M(l,t,r,b)` | `Margins`. Sizes are written `w×h` (= `Size(Columns: w, Rows: h)`); rects `(col, row, w×h)`. |
| render harness | `RenderTree` over `Root` + a `ScenePool`; `render()` = `RunRenderPass()`; `RC(e)` = cumulative `Render`-call count on `e`; `layers` = `CollectLayers` output (bottom-up); `P(b)` = boundary `b`'s published `CompositeParameters`; `LC` = `RenderTree.LayerCount`; `hit(c, r)` = `RenderTree.HitTest(c, r)`. |

Defaults unless a row says otherwise: `Visibility = Visible`, alignments `Stretch`, `Margin` 0, `Min* = 0`, `Max* = U`, `Width/Height = null`. Alignment rows pin the off-axis alignment (`V = Top` for horizontal rows, `H = Left` for vertical rows) so expected bounds are unambiguous.

### 0.2 Notation

- `measure(e, w×h)` / `arrange(e, rect)` = direct `Measure`/`Arrange` calls; `desired(e)` = `DesiredSize`; `bounds(e)` = `Bounds` (parent-relative).
- `constraint(e)` = the availableSize recorded by `e`'s `MeasureOverride` (post margin-deflate, post min/max coercion).
- `mo(e)` / `ao(e)` = cumulative `MeasureOverride` / `ArrangeOverride` call counts.
- Panel rows assume the panel itself is arranged to the stated size (via `Root` at that size) with default Stretch alignment.
- "diagnostic" = the repo's DEBUG-diagnostic convention (assert in DEBUG builds; absence of throw in Release where practical).

### 0.3 Oracle tags

`WPF` = WPF behavior (the primary oracle, integer-adapted); `AV` = Avalonia 11; `WINUI` = WinUI/UWP (Spacing-style members WPF lacks); `PIN` = Cursorial pin with no direct parent-framework analog (this matrix is the decision record); `DEV` = deliberate deviation from a parent framework, always with rationale (inline or via the LD ledger).

### 0.4 Pinned decisions made by this matrix (LD ledger)

Each goes beyond — but never against — the canonical doc text; deliberate and binding until amended.

- **LD1 — Min/Max/explicit resolve order (WPF `MinMax`).** Per axis, with `W?` = explicit `Width` (null = Auto): `max' = Max(Min(W ?? U, MaxWidth), MinWidth)`; `min' = Max(Min(max', W ?? 0), MinWidth)`. Binding strength: **Min > Max > explicit Width/Height > content**. Used identically in measure (constraint coercion + natural-size clamp) and arrange (`size_a` clamp). Oracle: WPF.
- **LD2 — DesiredSize is not clipped to the constraint.** `DesiredSize = clamp(natural, min', max') + Margin` (saturating); it may exceed `availableSize` — parents own overflow policy, and `ScrollContentPresenter`'s extent depends on the unclipped value. DEV from WPF/Avalonia, whose `MeasureCore` clips the returned desired size to the available size; rationale: spec §2.2 / doc §5.2 normative text. `DesiredSize` is never `U` on any axis: a `MeasureOverride` returning `U` is clamped to `MaxExtent` with a diagnostic.
- **LD3 — Alignment offsets.** `Left/Top → 0`; `Center` **and** `Stretch` → `LayoutMath.CenterOffset(slot, used)` (floor — the spare cell goes right/bottom; Stretch that cannot fill the slot, e.g. Max-clamped, centers — WPF); `Right/Bottom → max(0, slot − used)`. All offsets clamp ≥ 0: when `used > slot` content pins to the Left/Top edge and overflows right/bottom. DEV from WPF (which produces negative offsets and centers the clipped overhang); rationale: overflowing content should clip predictably at the trailing edge rather than bleed in both directions. **Amended P2.6 (LD19): the clamp stays.** Signed margins are now the one sanctioned layout-side source of negative placement — negatives enter the final position only through the `margin.Left`/`margin.Top` terms of the position fold (`slot origin + margin + alignment offset`), never through the alignment offsets themselves, and `Bounds` carries them via the signed `LayoutRect` (L224).
- **LD4 — Arrange self-heals measure.** `Arrange(finalRect)` on a measure-invalid element first runs `Measure(_lastMeasureConstraint)`; on a **never-measured** element it runs `Measure(finalRect.Size)` (the margin-inclusive slot). PIN.
- **LD5 — Visibility change routing** (doc §5.6 "custom routing", made explicit): any flip **into or out of `Collapsed`** ⇒ `InvalidateMeasure` on self **and** parent measure (space is released/reclaimed); a **`Visible ↔ Hidden`** flip ⇒ render-side only (boundary: composite-parameters; non-boundary: zone re-raster) — `IsMeasureValid`/`IsArrangeValid` are untouched.
- **LD6 — Layout caches.** The measure cache keys on the **raw** `availableSize` (pre-margin-deflate); the arrange cache keys on `finalRect`. A cache hit returns before `ApplyTemplate` and before any override runs (doc §5.3 ordering: Collapsed early-out → cache hit → `ApplyTemplate`).
- **LD7 — `StackPanel.Spacing` gaps** exist between consecutive **non-Collapsed** children only (`gaps = max(0, visibleCount − 1)`). Oracle: AV/WinUI (WPF has no Spacing).
- **LD8 — Grid star desired contribution.** Under a bounded constraint a star column contributes `min(resolvedStarSize, max non-spanning child desired)` to the Grid's own desired size; under `U` a star behaves as Auto end-to-end (doc §5.4). WPF-adapted (WPF's accounting is double-typed); tag PIN.
- **LD9 — Grid placement clamping.** Registration coercion pins `Row/Column ≥ 0` and spans `≥ 1`; **layout-time** placement additionally clamps `Row/Column` into the definition range and spans to the remaining definitions (WPF). A Grid with zero definitions on an axis behaves as one implicit `1*` definition (WPF).
- **LD10 — WrapPanel oversized item.** An item whose main-axis extent exceeds the line constraint wraps to its own line and is arranged in a slot **clamped to the line constraint**. DEV from WPF (which arranges it at full desired, overflowing); rationale: spec §3.6 pin — keeps arrange rects inside the panel; overflow *rendering* is uniformly the zone-edge clip's job.
- **LD11 — Banded scroll scene (doc §5.7 + §12, made deterministic).** `K = max(viewportRows, 8)`. Band length is constant: `bandLen = min(extent, viewport + 2K)`; `bandStart = clamp(anchor − K, 0, extent − bandLen)` where `anchor` = the offset at the last (re-)anchor (initially 0). **Re-anchor predicate: `|newOffset − anchor| > K`** ⇒ `anchor := newOffset`, one band re-raster; offsets within `±K` of the anchor are pure composite slides. v1 bands the **vertical axis only**. PIN (the doc's "nearing a band edge" given an exact threshold).
- **LD12 — Re-raster observability.** Rows assert "zone re-raster" via element `Render`-call counting (`RC`) and "composite refresh" via collected `SceneLayer.Parameters`; "zero re-raster" = zero `Render` calls that frame. The matrix deliberately avoids depending on a `Scene.RasterVersion` member (doc §5.6 names one, the Drawing layer currently has none — invariant 7 says don't add it for tests' sake).
- **LD13 — Banded policy supersedes the spec.** `ScrollContentPresenter.SceneBudgetCells`, extent-sized scenes, and the degraded viewport-mode fallback (spec §2.4/§3.9) are **not implemented**; doc §13.2's banded DECISION wins. No budget knob, no degraded mode.
- **LD14 — WrapPanel break predicate**: wrap when `lineUsed + itemExtent > lineConstraint` — strictly greater; an exact fit stays on the line. Oracle: WPF.
- **LD15 — DockPanel slot rule (WPF arrange).** A docked child's slot extent on its dock axis = the child's **DesiredSize** on that axis (it may overflow the panel when space is exhausted); the cross axis and all remainder computations clamp ≥ 0; children after exhaustion get zero-extent slots. When `LastChildFill == false`, the last child docks per its own `Dock` (default `Left`).
- **LD16 — Canvas precedence**: `Left` beats `Right`, `Top` beats `Bottom` when both are set. Oracle: WPF.
- **LD17 — StackPanel cross-axis slot** = `max(panel's arranged cross size, child's desired cross size)` (WPF) — a child wider than the panel overflows; clipping is the zone edge's job.
- **LD18 — `LayoutMath` pins.** `Add(a, b)`: `U` absorbs; saturates into `[0, U]` (a finite overflow becomes `U`). `Sub(a, b)`: `U − anything = U`; `finite − U = 0`; floors at 0. `Clamp(v, min, max) = Max(min, Min(v, max))` — min is applied last, so **min wins a min>max conflict** (LD1 depends on this; `Math.Clamp` would throw). `CenterOffset(slot, size) = Max(0, slot − size) / 2`. **Amended P2.6 (LD19):** the second operand of `Add`/`Sub` may be negative (signed margins): `Add(a, −b)` still floors at 0 (this *is* the DesiredSize clamp), `Sub(a, −b)` enlarges (`a + b`, saturating into `[0, U]` — a pathological enlargement saturates to `U` and behaves as Unbounded from there).
- **LD19 — Signed margins + the signed `Bounds` carrier (P2.6; reverses the doc §5.2 v1 cut; amends L37, LD3, LD18).** `Margin` components may be negative, with WPF semantics end-to-end. **Measure:** the margin-deflate `Sub(available, margin)` **enlarges** the inner constraint when a margin sum is negative; `DesiredSize = clamp(natural + margin, ≥ 0)` per axis (`LayoutMath.Add` floors at 0 — WPF). **Arrange:** the slot deflate enlarges identically; the final position fold `slot origin + signed margin + alignment offset` is **signed** — a child may sit at a negative origin relative to its parent (and to the window). Alignment offsets still clamp ≥ 0 (LD3); Canvas offsets still clamp ≥ 0 (L126); `RenderOffset*` remains the composite lane for *animated* placement. **Carrier:** `UIElement.Bounds` is retyped from Rendering's ushort-backed `Rect` to **`LayoutRect`** (`Cursorial.UI`) — signed `int` origin, size validated into `[0, MaxExtent]`; arrange positions clamp into `[−MaxExtent, MaxExtent]` (the L11 clamp made symmetric, diagnostic on either edge; the fold computes in `long` so a wrapping `int` sum cannot sign-flip the clamp). Rendering's `Rect` is unchanged; `Rect → LayoutRect` converts implicitly (widening), `LayoutRect.ToRect()` is the explicit narrowing affordance (no consumer wired today — render/hit paths stay signed end-to-end). **Render/hit:** painting a negative-origin child is a negative zone-local translate — cells above/left of the zone clip per cell (the P2.5 ① push-stack); hit testing reads the signed `Bounds` directly. The `NegativeMarginCoerced` diagnostic kind is historical (no longer emitted). **Arrange content sizing (P2.6 review):** the non-Stretch arrange size uses the natural size cached at measure (post-MinMax, pre-margin — WPF's `_unclippedDesiredSize`), never `DesiredSize − margin`: when the DesiredSize floor clamps to 0, the subtraction would "recover" \|margin\| and inflate the element past its natural size (L225). Oracle: WPF for the measure/arrange math; the LD3 offset clamp remains a recorded DEV.

---

## 1. `LayoutMath` & integer saturation (T1) — L1–L12

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| L1 | — | constants | `Unbounded == int.MaxValue`; `MaxExtent == 65535`; `LayoutLimits.MaxScrollExtent == 32_000`; `IsUnbounded(U)` true, `IsUnbounded(MaxExtent)` false | PIN (doc §5.2) |
| L2 | — | `Add(U, 5)` / `Add(5, U)` / `Add(U, U)` | all `U` (absorbing) | PIN (LD18) |
| L3 | — | `Add(2_147_483_640, 100)` | `U` (saturates; never wraps negative) | PIN (LD18) |
| L4 | — | `Add(3, -5)` | `0` (floors at 0) | PIN (LD18) |
| L5 | — | `Sub(U, 5)` / `Sub(U, U)` | `U` / `U` (Unbounded absorbs on the left) | PIN (LD18) |
| L6 | — | `Sub(5, 9)` / `Sub(5, U)` | `0` / `0` (floors at 0) | PIN (LD18) |
| L7 | — | `Add(Size(10×5), M(1,2,3,4))` / `Sub(Size(10×5), M(1,2,3,4))` | `14×11` / `6×0` — per-axis; Sub's vertical `5 − 6` floors at 0 | PIN (LD18) |
| L8 | — | `Sub(Size(U×10), M(2,2,2,2))` | `U×6` (Unbounded axis absorbs the margin) | PIN (LD18) |
| L9 | — | `Clamp(5, 10, 3)` | `10` — min applied last, min wins | PIN (LD18; WPF MinMax shape) |
| L10 | — | `CenterOffset(10, 4)` / `CenterOffset(9, 4)` / `CenterOffset(4, 10)` | `3` / `2` (floor — spare cell right/bottom) / `0` (never negative) | PIN (doc §5.2) |
| L11 | `Probe(4×2)`, `H = Left`, `V = Top`; `margin.Left` ∈ { `20` @ slot col `65530`, `−70000` @ slot col `0`, `int.MaxValue` @ slot col `65530` } (one `[Theory]` — amended P2.6/LD19) | `arrange(e, (slotCol, 0, 30×5))` | the position fold computes in `long` and clamps **symmetrically** into `[−MaxExtent, MaxExtent]`: `65530 + 20 = 65550` → `+MaxExtent`; `0 − 70000` → `−MaxExtent`; `65530 + int.MaxValue` (which wraps `int`) → `+MaxExtent`, never a sign-flipped clamp; diagnostic on every edge; no `LayoutRect` ctor throw | PIN (doc §5.2; LD19 symmetric clamp) |
| L12 | `Probe` whose `MeasureOverride` returns `(U, 1)` | `measure(e, 10×10)` | `desired(e) = MaxExtent×1`; diagnostic ("DesiredSize is never Unbounded") | PIN (LD2) |

---

## 2. Core Measure contract (T1) — L13–L37

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| L13 | `Probe(5×3)`, `M(1,1,1,1)` | `measure(e, 20×20)` | `desired = 7×5` — **DesiredSize includes Margin** | WPF |
| L14 | `Probe(5×5)`, `M(1,2,3,4)` | `measure(e, 20×20)` | `constraint(e) = 16×14` (margin-deflated); `desired = 9×11` | WPF |
| L15 | `Probe(5×5)`, `M(2,2,2,2)` | `measure(e, U×10)` | `constraint(e) = U×6` — Unbounded propagates through margin deflate | WPF (∞ analog) |
| L16 | `Probe(5×5)`, `MaxWidth = 8` | `measure(e, 20×20)` | `constraint(e).Columns = 8` (max caps the constraint) | WPF |
| L17 | `Probe(5×5)`, `MinWidth = 12` | `measure(e, 8×20)` | `constraint(e).Columns = 12` — min **raises** the constraint above what the parent offered | WPF |
| L18 | `Probe(2×2)`, `Width = 6` | `measure(e, 20×20)` | `constraint(e).Columns = 6`; `desired.Columns = 6` (explicit beats smaller content) | WPF |
| L19 | `Probe(9×2)`, `Width = 6` | `measure(e, 20×20)` | `desired.Columns = 6` (explicit beats larger content) | WPF |
| L20 | `Probe(2×2)`, `Width = 30` | `measure(e, 20×20)` | `constraint(e).Columns = 30`; `desired.Columns = 30` — explicit wins over availability, and desired is not clipped to the constraint | WPF (constraint) + DEV (desired; LD2) |
| L21 | `Probe(5×5)`, `MinWidth = 20`, `MaxWidth = 10` | `measure(e, 40×40)` | effective width 20 — **Min beats Max** (LD1) | WPF |
| L22 | `Probe(2×2)`, `Width = 50`, `MaxWidth = 30` | `measure(e, 60×60)` | `desired.Columns = 30` — **Max beats explicit** | WPF |
| L23 | `Probe(2×2)`, `Width = 5`, `MinWidth = 10` | `measure(e, 60×60)` | `desired.Columns = 10` — **Min beats explicit** | WPF |
| L24 | `Probe(9×2)`, `MaxWidth = 6` | `measure(e, 20×20)` | `desired.Columns = 6` (natural clamped down) | WPF |
| L25 | `Probe(2×2)`, `MinWidth = 5` | `measure(e, 20×20)` | `desired.Columns = 5` (natural raised) | WPF |
| L26 | `Probe(15×3)` | `measure(e, 10×10)` | `desired = 15×3` — exceeds the constraint, by design | DEV (LD2; WPF clips to 10) |
| L27 | `Probe(15×3)` hosted in `slot` | parent reads `e.DesiredSize` for overflow policy | the parent sees `15×3` — overflow is the parent's decision (SCP's extent depends on this) | PIN (LD2 rationale) |
| L28 | `Probe(2×2)`, `MinWidth = 5` | `measure(e, U×U)` | `constraint(e).Columns = U` (min does not finite-ize an unbounded constraint); `desired.Columns = 5` (min applies to the result) | WPF |
| L29 | `Probe(5×5)` | `measure(e, 10×10)` twice | `mo(e) == 1` — constraint cache hit | WPF+AV |
| L30 | `Probe(5×5)` | `measure(e, 10×10)`, then `measure(e, 12×10)` | `mo(e) == 2` — cache keys the raw availableSize (LD6) | WPF |
| L31 | `Probe(5×5)` measured | `InvalidateMeasure()`, then `measure(e, 10×10)` (same constraint) | `mo(e) == 2` — invalidation busts the cache | WPF |
| L32 | `Probe(5×5)` | `measure(e, 10×10)` twice | first call: `ApplyTemplate` recorded **before** `MeasureOverride`; second call (cache hit): neither runs | PIN (doc §5.3 order; LD6) |
| L33 | parent `Probe`-panel with child; both measured | child's natural size changes (test knob) + child re-measured → `desired` changes | parent receives `OnChildDesiredSizeChanged(child)`; default base implementation invalidates the parent's measure | WPF |
| L34 | as L33 | child re-measures to the **same** desired | no `OnChildDesiredSizeChanged` call; parent stays valid | WPF+AV |
| L35 | observer on `DesiredSizeProperty` (DirectProperty) | measure changes desired / re-measure to same desired | one typed change notification with `(old, new)` / silent (equality-gated) | PIN (Fork A DirectProperty lane) |
| L36 | `Fit(3×3)`, `M(6,0,6,0)` | `measure(e, 10×5)` | inner = `max(0, 10−12) = 0` → `constraint(e).Columns = 0`; `desired = 12×3` (0 content + 12 margin, 3 rows) | WPF |
| L37 | `Fit(3×1)`, `M(0,−2,0,0)` | `measure(e, 10×5)` | `Margin` reads back `M(0,−2,0,0)` (no coercion); `constraint(e) = 10×7` — negative margins **enlarge** the margin-deflated constraint; `desired = 3×0` — `content + margin` clamps ≥ 0 per axis | WPF (LD19 — amended P2.6, reversing the v1 coerce-to-0 cut) |

---

## 3. Core Arrange & alignment (T1) — L38–L60

Alignment rows: `Probe(4×2)` in `slot((0,0,10×6))` unless noted; `V = Top` for horizontal rows, `H = Left` for vertical rows.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| L38 | `H = Left` | arrange | `bounds = (0,0,4×2)` | WPF |
| L39 | `H = Center` | arrange | `bounds = (3,0,4×2)` ((10−4)/2) | WPF |
| L40 | `H = Center`, `slot((0,0,9×6))` | arrange | `bounds = (2,0,4×2)` — floor; the spare cell goes right | PIN (integer adaptation of WPF) |
| L41 | `H = Right` | arrange | `bounds = (6,0,4×2)` | WPF |
| L42 | `H = Stretch` | arrange | `bounds = (0,0,10×2)` — slot-filling | WPF |
| L43 | `V = Top` / `Center` / `Bottom` / `Stretch` (one `[Theory]`, `H = Left`) | arrange | `(0,0,4×2)` / `(0,2,4×2)` / `(0,4,4×2)` / `(0,0,4×6)` | WPF |
| L44 | `H = Stretch`, `MaxWidth = 6` | arrange | `bounds = (2,0,6×2)` — Stretch that cannot fill **centers** (LD3) | WPF |
| L45 | `Probe(12×2)`, `H =` Left / Center / Right (`[Theory]`) | arrange into 10-wide slot | `bounds = (0,0,10×2)` in all three — non-stretch arranged size = `min(desired − margin, slot)` | WPF |
| L46 | `Probe(4×2)`, `MinWidth = 12`, `H = Center` | arrange into 10-wide slot | `bounds = (0,0,12×2)` — min forces overflow; offset clamps to 0, content overflows **right** | DEV (LD3; WPF centers via negative offset) |
| L47 | as L46, `H = Right` | arrange | `bounds = (0,0,12×2)` — `max(0, slot − used) = 0` | DEV (LD3) |
| L48 | `M(1,1,0,0)`, `H = Left`, `V = Top` | arrange | `bounds = (1,1,4×2)` — margin offsets the position | WPF |
| L49 | `M(1,0,1,0)`, `H = Right` | arrange into 10-wide slot | inner slot 8; offset `8−4 = 4`; `bounds.Column = 1+4 = 5` | WPF |
| L50 | `M(1,0,1,0)`, `H = Center` | arrange into 10-wide slot | offset `(8−4)/2 = 2`; `bounds.Column = 3` | WPF |
| L51 | `Fit(4×2)`, `M(3,0,3,0)`, `V = Top` | arrange into 4-wide slot | inner = `max(0, 4−6) = 0`; `bounds = (3,0,0×2)` — zero-width content at the margin offset | WPF |
| L52 | `H = Left`, `V = Top` | `arrange(e, (5,3,10×6))` | `bounds = (5,3,4×2)` — parent-relative, slot origin honored | WPF |
| L53 | measured + arranged | `arrange(e, same rect)` again | `ao(e) == 1` — arrange cache hit (keys `finalRect`, LD6) | WPF+AV |
| L54 | as L53 | `arrange(e, different rect)` | `ao(e) == 2` | WPF |
| L55 | measured, then `InvalidateMeasure()` | `arrange(e, rect)` without an intervening measure | `Measure(_lastMeasureConstraint)` runs first (self-heal), then arrange; `mo` incremented | WPF |
| L56 | **never-measured** `Probe(4×2)` | `arrange(e, (0,0,10×6))` | measured with `10×6` (the slot size) before arranging (LD4); no throw | PIN (LD4) |
| L57 | `Probe` variant whose `ArrangeOverride` returns `(70_000, 2)` | `arrange(e, (0,0,10×5))` | `used` is clamped into `[0, MaxExtent]` per axis before `Rect` construction (`Bounds.Columns == 65535`); diagnostic; no throw | PIN (spec §3.4 `used` clamp) |
| L58 | observer on `BoundsProperty` (DirectProperty) | arrange to a new rect / re-arrange to the same rect | one notification `(old, new)` / silent (equality-gated) | PIN |
| L59 | `Probe(2×2)`, `Width = 6`, `H = Stretch` | arrange into 10-wide slot | `bounds = (2,0,6×2)` — explicit size binds under Stretch, then centers (LD1 + LD3) | WPF |
| L60 | `Probe(4×2)` | arrange; inspect recorded `ArrangeOverride` argument | `H = Left` → `(4,2)`; `H = Stretch` → `(10,2)` — `ArrangeOverride` receives the aligned size `size_a`, not the slot | PIN (spec §3.4) |

---

## 4. Visibility (T1) — L61–L70

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| L61 | `Probe(5×3)`, `Visibility = Collapsed` | `measure(e, 20×20)` | `desired = 0×0`; `mo(e) == 0`; `ApplyTemplate` **not** called (early-out precedes it) | WPF |
| L62 | as L61 | `arrange(e, (0,0,10×5))` | `bounds = Rect.Empty`; `ao(e) == 0`; `IsArrangeValid` true | WPF |
| L63 | Collapsed child in a measured `SP` under `Root` | `Visibility = Visible` | child **and** parent measure invalidated (LD5); after `layout()`, child measured/arranged normally | WPF |
| L64 | Visible child A above sibling B in a vertical `SP` | `A.Visibility = Collapsed`; `layout()` | parent reflows: B moves up to row 0; SP desired shrinks by A's height | WPF |
| L65 | `Probe(5×3)`, `Visibility = Hidden` | `layout()` | measured and arranged exactly as Visible — same `desired`, same `bounds` (occupies space) | WPF |
| L66 | measured + arranged element | flip `Visible → Hidden → Visible` | `IsMeasureValid`/`IsArrangeValid` stay true throughout; no layout work queued (LD5 — render-side only) | PIN (LD5; WPF-shaped) |
| L67 | parent Hidden, child Visible | read `child.IsEffectivelyVisible` | `false` (own `Visibility` still `Visible`); parent visible again → `true` | PIN (doc §5.1) |
| L68 | `SP` with `Spacing = 2`, children h=1 ×3, middle Collapsed | `layout()` | SP desired height = `1+2+1 = 4`; visible children at rows 0 and 3 — no gap for the collapsed child (LD7) | AV |
| L69 | as L68 but middle **Hidden** | `layout()` | desired height = `1+2+1+2+1 = 7`; Hidden keeps its space and its spacing gaps | WPF+AV |
| L70 | Collapsed `Probe` | `layout()` ×N, then `Visibility = Visible`, `layout()` | `ApplyTemplate` first called on the first **non-collapsed** measure — template/name-scope materialize late (S8/XAML must tolerate; documented) | PIN (doc §5.3) |

---

## 5. Invalidation routing & `LayoutManager` (T1) — L71–L94

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| L71 | clean 3-level tree under `Root` | set `Width` (an `[AffectsMeasure]` property) on the leaf | leaf `IsMeasureValid == false` **and every ancestor to the root** (the up-walk); `LayoutManager.HasPendingWork == true` | PIN (doc §5.1 "self + ancestor walk"; WPF-shaped) |
| L72 | as L71 | set `Width` to its current value | nothing invalidated, no pending work — equality-gated at the property engine | PIN (Fork A gate) |
| L73 | clean tree | set `HorizontalAlignment` (an `[AffectsArrange]` property) on the leaf | leaf `IsArrangeValid == false` **only**; `IsMeasureValid` stays true; ancestors untouched (arrange invalidation is self-only) | PIN (doc §5.1) |
| L74 | two leaves under one parent, both dirtied | one `layout()` | each element's `MeasureOverride` runs **at most once** — the ancestor walk stops at the first already-invalid ancestor, the queue dedupes | PIN |
| L75 | clean leaf | `InvalidateMeasure()` twice, then `layout()` | one measure (enqueued-bit idempotence) | PIN |
| L76 | custom element registering `AffectsMeasure<T>(P)` in its static ctor; a second styled property with **no** flags | set each | flagged property invalidates measure; unflagged property invalidates nothing | PIN (sugar statics) |
| L77 | child `Probe` in a `DP` under `Root`, clean | `DockPanel.SetDock(child, Dock.Right)` | **parent** measure invalidated (`[AffectsParentMeasure]`); child's `IsMeasureValid` still true (it re-measures only because the parent hands it a new constraint) | PIN (doc §5.5) |
| L78 | a `Probe` whose type's effects table froze **before** `DockPanel`'s static ctor ran (touch the probe type first) | `DockPanel.SetDock(probe, Dock.Bottom)` | effects still fire — `GetEffects` merges the property-global lane (`perTypeTable[id] | GlobalEffects`); without it this write would invalidate nothing | PIN (two-lane contract, doc §5.5) |
| L79 | child in `Cnv` under `Root`, clean | `Canvas.SetLeft(child, 7)` | parent **arrange-only** invalidated (`[AffectsParentArrange]`); no measure work anywhere — the cheap lane | PIN (doc §5.4) |
| L80 | detached element (no `VisualParent`) | `DockPanel.SetDock(e, Dock.Top)` | no throw; no invalidation (`VisualParent?.` routing) | PIN |
| L81 | inheriting styled property registered `AffectsMeasure` on the fixture element type; 3-level tree, descendants entry-less | set the property at the root | every inheriting descendant's measure invalidated via the `OnInheritedPropertyChanged` carrier running the same effects dispatch | PIN (doc §5.5; R6 mechanism) |
| L82 | measured `SP` under `Root` | `Children.Add(probe)` | panel measure invalidated; child attached (visual + logical parent set) | WPF+AV |
| L83 | as L82 | `Children.Remove(probe)` | panel measure invalidated; child detached | WPF+AV |
| L84 | `UIElementCollection` | `Add(null)` / `Add(child already in another panel)` / `Add(same child twice)` | each throws (`ArgumentNullException` / `InvalidOperationException` / `InvalidOperationException`) | PIN (spec §2.3) |
| L85 | parent and child both measure-invalid | `layout()` | parent's `MeasureOverride` runs **before** the child's (depth-keyed shallowest-first heap) | PIN (doc §5.3) |
| L86 | element A whose `MeasureOverride` sets sibling B's `Width` once (one-shot flag) | one `layout()` | B re-measured **within the same pass** — same-tick fixpoint; `HasPendingWork == false` after | PIN (invariant 1) |
| L87 | element whose `MeasureOverride` always invalidates its own measure (oscillator) | `layout()` | pass terminates at the 16-pass cap with a layout-cycle diagnostic naming the element; residual work slips (`HasPendingWork == true`); no hang | PIN (doc §5.3) |
| L88 | handler on `LayoutManager.LayoutUpdated` | `layout()` | handler runs **after** the pass with layout valid (`HasPendingWork == false` at raise time, absent L89's case) | WPF |
| L89 | `LayoutUpdated` handler that dirties layout on its first raise only | one `layout()` | exactly one bounded same-tick re-run (handler observes valid layout twice); a handler dirtying **every** raise degrades to one-frame lag + diagnostic | PIN (doc §5.3) |
| L90 | fully valid tree | `layout()` | zero `MeasureOverride`/`ArrangeOverride` calls (idle guard) | PIN |
| L91 | fully valid tree, warmed | `layout()` | **0 bytes allocated** (steady-state contract; `GC.GetAllocatedBytesForCurrentThread` delta) | PIN (P1 allocation discipline) |
| L92 | parent valid; leaf re-measures (constraint-busting knob) to the **same** desired | `layout()` | parent's `MeasureOverride`/`ArrangeOverride` do not re-run — no notification cascade from an unchanged desired | WPF+AV |
| L93 | child arrange-invalid only (L73's state) | `layout()` | child re-arranged with its cached `_lastArrangeRect`; parent `ArrangeOverride` not re-run | PIN (doc §5.3 queue contract) |
| L94 | `Root` | `layout(W×H)` | root measured with `(W, H)` and arranged to `(0, 0, W×H)` — the P1 single-root contract (S4's WindowManager replaces this at P7) | PIN |

---

## 6. StackPanel (T1) — L95–L106

Vertical orientation (the default) unless noted; panel arranged 20×10 via `Root`.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| L95 | children `Probe(5×2)`, `Probe(8×3)` | measure | `desired(SP) = 8×5` — main axis sums, cross axis maxes | WPF |
| L96 | as L95 | inspect recorded child constraints | each child measured with `(20, U)` — Unbounded on the stacking axis, the incoming constraint on the cross axis | WPF |
| L97 | as L95 | `layout(20×10)` | `bounds(A) = (0,0,20×2)`, `bounds(B) = (0,2,20×3)` — sequential stacking, full-width cross slots | WPF |
| L98 | `Orientation = Horizontal`, children `Probe(5×2)`, `Probe(8×3)` | `layout(20×10)` | `desired = 13×3`; `bounds(A) = (0,0,5×10)`, `bounds(B) = (5,0,8×10)` | WPF |
| L99 | `Spacing = 2`, three children h=1 | measure | `desired.Rows = 1+2+1+2+1 = 7` | AV/WINUI |
| L100 | as L99, middle child Collapsed | measure + arrange | `desired.Rows = 4`; visible children at rows 0 and 3 (LD7 — no gap for collapsed) | AV |
| L101 | children Σh = 15 | `layout(20×10)` | `desired.Rows = 15` (exceeds constraint — LD2); third child arranged at its natural row (e.g. row 12) **past the panel's own extent**; rendering clip is the zone edge's job | WPF |
| L102 | child `Probe(25×2)` | `layout(20×10)` | child slot/bounds 25 wide — cross slot = `max(20, 25)` (LD17); overflows the panel | WPF |
| L103 | child `Probe(5×2)`, `H = Left` | `layout(20×10)` | child `bounds = (0,0,5×2)` — the panel hands a 20-wide slot; the child aligns itself inside it | WPF |
| L104 | measured SP | set `Orientation` / `Spacing` | both `[AffectsMeasure]`: panel measure invalidated; next pass relayouts | PIN (spec §2.3) |
| L105 | `Spacing = 2`, single child / zero children | measure | `desired` = child desired (no gaps) / `0×0` | AV |
| L106 | empty `SP` | measure | `desired = 0×0` | WPF |

---

## 7. DockPanel (T1) — L107–L118

Panel arranged 20×10 via `Root`; `LastChildFill` default true; child sizes are desired sizes (`Probe`). WPF measure formula pinned: each child is measured with the constraint minus the accumulated docked desire on each axis; the panel's desired accumulates the docked axis and maxes the perpendicular (`parentU = max(parentU, accU + childU)` per dock family).

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| L107 | A `Dock.Left` `Probe(5×3)`, B (fill) | `layout(20×10)` | `bounds(A) = (0,0,5×10)` (docked child fills the cross axis); `bounds(B) = (5,0,15×10)` | WPF |
| L108 | A `Dock.Top` `Probe(5×3)`, B (fill) | `layout(20×10)` | `A = (0,0,20×3)`; `B = (0,3,20×7)` | WPF |
| L109 | A Left `Probe(5×?)`, B Top `Probe(?×2)`, C fill | `layout(20×10)` | `A = (0,0,5×10)`, `B = (5,0,15×2)`, `C = (5,2,15×8)` — dock order is child order | WPF |
| L110 | A `Dock.Right` `Probe(4×2)`, B fill | `layout(20×10)` | `A = (16,0,4×10)`; `B = (0,0,16×10)` | WPF |
| L111 | A `Dock.Bottom` `Probe(5×3)`, B fill | `layout(20×10)` | `A = (0,7,20×3)`; `B = (0,0,20×7)` | WPF |
| L112 | child with `Dock` never set | measure/arrange | docks `Left` — the attached property's default | WPF |
| L113 | `LastChildFill = false`, single child `Probe(5×3)` | `layout(20×10)` | `bounds = (0,0,5×10)` — docks per its own `Dock` (Left), no fill (LD15) | WPF |
| L114 | panel 10×10: A Left `Probe(6×2)`, B Left `Probe(6×2)`, C fill | `layout(10×10)` | `A = (0,0,6×10)`, `B = (6,0,6×10)` (docked slot = desired even past exhaustion — overflows; LD15), `C = (12,0,0×10)` (remainder clamps to 0; no negative rects) | WPF |
| L115 | A Left `Probe(5×3)`, B next | inspect recorded constraints | `constraint(B) = (15, 10)` — constraint minus accumulated dock usage, clamped ≥ 0 | WPF |
| L116 | A Left `Probe(5×3)`, B last `Probe(7×4)` | measure with `20×10` | `desired(DP) = 12×4` (accumulated width `5+7`; perpendicular max `max(3, 4)`) | WPF |
| L117 | A Left `Probe(5×3)` **Collapsed**, B fill | `layout(20×10)` | `B = (0,0,20×10)` — a collapsed child's zero desired consumes no edge | WPF |
| L118 | laid-out `DP` | `SetDock(A, Dock.Right)`, `layout()` | A re-docks to the right edge — the `[AffectsParentMeasure]` route works end-to-end | PIN |

---

## 8. Canvas (T1) — L119–L129

Canvas arranged 20×10 via `Root`.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| L119 | child `Probe(5×4)` | measure | `constraint(child) = U×U` — Canvas measures children unbounded | WPF |
| L120 | children present (any) | measure | `desired(Cnv) = 0×0` — zero desired contribution regardless of children | WPF |
| L121 | child `Probe(5×4)`, `Left = 3`, `Top = 2` | `layout()` | `bounds = (3,2,5×4)` | WPF |
| L122 | child `Probe(5×4)`, no attached values | `layout()` | `bounds = (0,0,5×4)` | WPF |
| L123 | child `Probe(5×4)`, `Right = 2` | `layout()` | `bounds.Column = 20−2−5 = 13` | WPF |
| L124 | child `Probe(5×4)`, `Bottom = 1` | `layout()` | `bounds.Row = 10−1−4 = 5` | WPF |
| L125 | `Left = 3` **and** `Right = 2` set (`Top`/`Bottom` analog in the same `[Theory]`) | `layout()` | `Left`/`Top` win (LD16): `bounds.Column = 3` | WPF |
| L126 | child `Probe(5×4)`, `Right = 18` | `layout()` | computed `x = 20−18−5 = −3` → clamped to `0` — Canvas offsets never go negative (amended P2.6/LD19: signed `Margin` is the sanctioned static pull-up lane; `RenderOffset*` the animated one) | DEV (WPF places at −3; doc §5.2/§5.4; LD19 retains this clamp) |
| L127 | child `Probe(5×4)`, `Left = 18` | `layout()` | `bounds = (18,…,5×4)` — extends past the canvas; Canvas applies **no clip** (clipping happens only at the zone/scene edge, T3) | WPF (`ClipToBounds` default false) |
| L128 | child natural `5×4`, `M(1,0,0,0)`, `Left = 3`, `Top = 2` | `layout()` | slot = desired `6×4` at `(3,2)`; child content `bounds = (4,2,5×4)` (margin inside the slot) | WPF |
| L129 | laid-out canvas child | `Canvas.SetLeft(child, 9)`, `layout()` | position updates; **child's `mo` unchanged** (arrange-only lane — no re-measure anywhere) | PIN (doc §5.4: why ParentArrange is a distinct flag) |

---

## 9. Grid (T2) — L130–L159

Notation: `G[cols: …]` with `c5` = `FromCells(5)`, `a` = `Auto`, `*`/`2*` = `Star(weight)`; child `@(row, col)`; spans noted. Star algorithm (doc §5.4, pinned): `R = max(0, available − fixed − auto)`; `ideal_i = R·w_i/Σw`; `base_i = floor(ideal_i)`; leftover cells one each to the largest fractional parts, ties to the lowest definition index (largest-remainder/Hamilton); def Min/Max clamps re-run the distribution over unclamped stars to fixpoint. Grid arranged via `Root` at the stated width; single `c3` row unless noted; `ActualWidth` readbacks are the assertion surface for column rows.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| L130 | `G` with **no definitions**, one child `Probe(4×2)` | `layout(20×10)` | child slot = the whole grid (`(0,0,20×10)`) — implicit `1*` per axis (LD9) | WPF |
| L131 | `G[cols: c3, c4; rows: c2, c2]`, child `@(1,1)` | `layout(20×10)` | child slot `(3,2,4×2)` — cells position cumulatively | WPF |
| L132 | `G[cols: c10]`, col `MaxWidth = 6` | `layout(20×10)` | `ActualWidth = 6` — definition Min/Max clamp fixed columns too | WPF |
| L133 | child `Probe(1×1)` (Stretch) in a `c4×c2` cell | `layout()` | child `bounds` = the full `4×2` cell — the slot is the cell, alignment is the child's | WPF |
| L134 | `G[cols: *,*,*]` | `layout(10×3)` | `ActualWidths = 4,3,3` — floors 3,3,3; leftover 1 to the tie-broken lowest index | PIN (DEV from WPF's double distribution; integer Hamilton, doc §5.4) |
| L135 | `G[cols: *,*,*]` | `layout(11×3)` | `4,4,3` | PIN |
| L136 | `G[cols: *,*,*]` | `layout(8×3)` | `3,3,2` — two leftover cells to the two lowest-index ties | PIN |
| L137 | `G[cols: 2*,1*]` | `layout(10×3)` | `7,3` — floors 6,3; leftover 1 to the larger fraction (.67) | PIN |
| L138 | `G[cols: 1*,2*]` | `layout(10×3)` | `3,7` — leftover follows the fraction, not the index, when fractions differ | PIN |
| L139 | `G[cols: 0*,1*]` | `layout(10×3)` | `0,10` — zero-weight stars get zero | WPF |
| L140 | `G[cols: c4,1*,2*]` | `layout(13×3)` | `4,3,6` (`R = 9`) | PIN |
| L141 | `G[cols: *,*]`, col0 `MinWidth = 8` | `layout(10×3)` | `8,2` — clamp violation pins col0, distribution re-runs over the unclamped star with `R = 2` | WPF (policy) + PIN (remainder) |
| L142 | `G[cols: *,*]`, col0 `MaxWidth = 2` | `layout(10×3)` | `2,8` | WPF |
| L143 | `G[cols: *,*]`, both `MaxWidth = 3` | `layout(10×3)` | `3,3` — all stars clamped; the trailing 4 cells stay unassigned (grid content does not expand past Max) | WPF |
| L144 | `G[cols: *,*]`, both `MinWidth = 5` | `layout(6×3)` | `5,5` — Min wins over available; col1 starts at column 5 and the grid's content overflows its own 6-cell width (zone clip handles rendering) | WPF |
| L145 | `G[cols: *,*,*]`, col0 `MinWidth = 6` | `layout(12×3)` | `6,3,3` — one clamp, one re-run (`R' = 6` over two stars) | WPF+PIN |
| L146 | `G[cols: a,*]`, child `@(0,0)` `Probe(4×1)` | `layout(10×3)` | `4,6` — Auto sizes to content max | WPF |
| L147 | `G[cols: a,*]`, no child in col0 | `layout(10×3)` | `0,10` — empty Auto is zero | WPF |
| L148 | `G[cols: a,*]`, col0 `MaxWidth = 3`, child `Probe(5×1)` | `layout(10×3)` | col0 `ActualWidth = 3` | WPF |
| L148b | `G[cols: a,*]`, col0 `MaxWidth = 3`, child `Probe(5×1)` | `layout(10×3)` | child's measure constraint on the axis = `3` on every pass, never `U` (LD1 constraint coercion — a `MaxWidth`-bounded Auto/Star track measures the child against the cap, unlike uncapped L150; Cell tracks already pass their clamped `Size`) | WPF |
| L149 | `G[cols: a,*]`, col0 `MinWidth = 6`, child `Probe(4×1)` | `layout(10×3)` | col0 `ActualWidth = 6` | WPF |
| L150 | child in an Auto column | inspect recorded constraint | measured with `U` on that axis (auto = content-driven) | WPF |
| L151 | `G[cols: *]`, child `Probe(7×1)` | `measure(G, U×3)` | `desired(G).Columns = 7` — star behaves as Auto under an Unbounded constraint | WPF |
| L152 | `G[cols: c4,*]`, child `@(0,1)` `Probe(5×1)` | `measure(G, 20×3)` | `desired(G).Columns = 4 + min(16, 5) = 9` — star contributes content desired, not allocation (LD8) | WPF-adapted (LD8) |
| L153 | `G[cols: c4,c6]`, child `@(0,0)` `ColumnSpan = 2` | `layout()` | child slot 10 wide (sum of spanned columns) | WPF |
| L154 | `G[cols: c4,c6]`, child `Column = 1`, `ColumnSpan = 5` | `layout()` | span clamps to the remaining definitions (effective span 1; slot = col1) — LD9 | WPF |
| L155 | `G[cols: c3,c3]`, child `Column = 5` | `layout()` | placement clamps to the last column (index 1) — LD9 | WPF |
| L156 | `G[cols: a,a]`, A `@(0,0)` `Probe(3×1)`, B `@(0,0)` `ColumnSpan = 2` `Probe(10×1)` | `layout(20×3)` | cols `6,4` — span deficit `10−3 = 7` spreads evenly (3 each), remainder 1 to the **rightmost** | DEV (WPF distributes proportionally; doc §5.4 v1 simplification, refinement deferred) |
| L157 | `G[cols: *,*]`, child spans both, `Probe(20×1)` | `layout(10×3)` | cols stay `5,5` — spanning content never inflates star columns under a bounded constraint; the child's slot is 10 and its content overflows (zone clip) | WPF-adapted, tag PIN |
| L158 | any element | `Grid.SetRow(e, −1)` / `Grid.SetRowSpan(e, 0)` | coerce to `0` / `1` at registration-coerce time | PIN (doc §5.4) |
| L159 | laid-out `G[cols: c4,*]` | ① `cols[0].Width = 8` post-attach ② re-add `cols[0]` to a second Grid ③ read `ActualWidth` | ① live re-layout (owner-wired definitions invalidate measure) ② throws (one-collection ownership) ③ readbacks match the row's resolved sizes | PIN (spec §2.3) |

---

## 10. WrapPanel (T2) — L160–L172

Horizontal orientation (the default) unless noted; panel arranged via `Root` at the stated size. Items are `Probe`s; line constraint = the panel's main-axis constraint.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| L160 | panel 10 wide; items `4×1, 4×1, 4×1` | `layout(10×5)` | `A = (0,0,4×1)`, `B = (4,0,4×1)`, `C = (0,1,4×1)`; `desired = 8×2` | WPF |
| L161 | panel 10 wide; items `5×1, 5×1` | `layout(10×5)` | one line (`5+5 = 10` is **not** `> 10` — exact fit stays; LD14); `desired = 10×1` | WPF |
| L162 | items `4×1, 4×3` on one line | `layout(10×5)` | slots `(0,0,4×3)` and `(4,0,4×3)` — every slot in a line gets the line's max cross extent | WPF |
| L163 | lines of extent 8 and 4 (items `4,4` then `4`, panel 10 wide, heights 1) | measure | `desired = 8×2` — desired main = **max** line extent, cross = Σ line extents | WPF |
| L164 | `ItemWidth = 6`, panel 12 wide; items desired `4×1`, `9×1` | `layout(12×5)` | one line (6+6 exact fit); slots `(0,0,6×1)`, `(6,0,6×1)` — uniform slots regardless of item desired | WPF |
| L165 | `ItemWidth = 6` | inspect recorded constraints | items measured with `(6, constraint.Rows)` — `ItemWidth`/`ItemHeight` replace the constraint per axis | WPF |
| L166 | `ItemHeight = 2`; items `4×1, 4×3` | `layout(10×6)` | line height 2; both slots `…×2` (taller item's slot is clamped; its own arrange handles the 3-row desired inside a 2-row slot per §3 rules) | WPF |
| L167 | panel 10 wide; item `12×1` between two `4×1` items | `layout(10×5)` | the oversized item gets its own line with slot `(0,1,10×1)` — clamped to the line extent (LD10) | DEV (WPF arranges at full 12; LD10 rationale) |
| L168 | panel 10 wide; 3 items `4×1` | `layout(10×5)` | last line's single item at `(0,1,…)` — partial lines pack from the line start, no justification | WPF |
| L169 | middle item Collapsed | `layout()` | skipped entirely: no slot, no line participation | WPF+AV |
| L170 | `Orientation = Vertical`, panel 10×4; items `1×3, 1×3` | `layout(10×4)` | items flow down then wrap to a new column: `A = (0,0,1×3)`, `B = (1,0,1×3)`; desired transposed (`2×3`) | WPF |
| L171 | items `4×1, 4×1, 4×1` | `measure(WP, U×U)` | single line — no wrapping under an unbounded main axis; `desired = 12×1` | WPF |
| L172 | laid-out `WP` | set `ItemWidth` / `ItemHeight` / `Orientation` | each `[AffectsMeasure]`: relayout next pass | PIN (spec §2.3) |

---

## 11. Render zones, composite order, hit testing (T3) — L173–L200

Harness: the render harness of §0.1. The `Root` element is a render boundary **by construction** (doc §5.5 predicate ① "window root" — at P1 the single full-screen root stands in for S4's window; the P1 render system must say so in its doc comment). Boundary predicates under test: ② `Opacity < 1`, ③ `RenderOffset* ≠ 0`, ④ `ClipToBounds`, ⑤ `CompositeClip != null`, ⑥ `ScrollContentPresenter`, ⑦ `IsRenderBoundary`. Observability per LD12.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| L173 | root + nested non-boundary panels | `render()` | `LC == 1`; all content rasters into the root zone; `Render` call order = pre-order DFS, parent before children | PIN (doc §5.5) |
| L174 | three siblings with `ZIndex` 0, 5, 0 | `render()` | paint order: index 0, index 2, then the `ZIndex = 5` sibling — stable `(ZIndex, index)` sort | WPF (`Panel.ZIndex`) |
| L175 | rendered tree | set a sibling's `ZIndex` | `[AffectsRender + recollect]`: zone re-rasters with the new paint order; cached z-order array rebuilt | PIN |
| L176 | clean rendered tree, nested panel `X` | `X.Opacity = 0.5`; `render()` | predicate ② promotes: `LC` +1 (full-recomposite signal); promotion frame re-rasters the **old** zone (excluding `X`'s subtree) **and** the new zone (`RC` bumps once for members of each — the four-step) | PIN (doc §5.5) |
| L177 | after L176 | `X.Opacity = 1.0`; `render()` ×N | still a boundary — **promotion is sticky until detach**; `LC` stable; no demotion | PIN (doc §5.5) |
| L178 | clean tree, panel `X` | `X.RenderOffsetColumn = 3`; `render()`; then `= 4`; `render()` | first write promotes (③); subsequent writes are **parameters-only**: zero `Render` calls, `P(X).OffsetColumn` shifts — animated slides never re-raster | PIN (invariant 3) |
| L179 | panel `X`, `ClipToBounds = true` | `render()` | promotes (④); `P(X).Clip` = `X`'s absolute bounds ∩ ancestor clips | PIN |
| L180 | panel `X` | `X.CompositeClip = Rect(...)`; `render()`; then `= null`; `render()` | promotes (⑤); `P(X).Clip` includes the composite clip; after null: still a boundary (sticky), clip reverts to the bounds-derived value | PIN (doc §5.5 ⑤) |
| L181 | panel `X` | `X.IsRenderBoundary = true`; later `= false` | promotes (⑦); setting false demotes nothing (documented on the property) | PIN |
| L182 | boundary at `Opacity = 0.5` | `render()` | layer `Parameters.Opacity == 128` (`round(255·0.5)`) | PIN (doc §5.6 formula) |
| L183 | boundary `Opacity 0.5` containing a descendant boundary `Opacity 0.5` | `render()` | inner layer `Opacity == 64` (`round(255·0.25)`) — ancestor boundary opacities multiply (approximation of group opacity, documented) | PIN (doc §5.6) |
| L184 | root zone containing non-boundary panel `X` (has `Background`) and a nested boundary `Y` | change `X.Background` (an `[AffectsRender]` property); `render()` | the **owning zone only** re-rasters: `RC` bumps for root-zone members; `Y`'s zone members' `RC` unchanged (descendant boundaries raster separately) | PIN (invariant 3 + zone model) |
| L185 | boundary `Y` with cached raster | change any `[AffectsComposite]` property on `Y`; `render()` | zero `Render` calls anywhere; only `P(Y)` differs | PIN (invariant 3) |
| L186 | fully rendered, untouched tree | `render()` | zero `Render` calls; every collected layer's `Parameters` unchanged (equality is the change detector — the idle frame) | PIN (doc §5.6) |
| L187 | boundary `Y` arranged to a new **size** | `layout()`, `render()` | `Y`'s scene is recreated (scenes don't resize) + zone re-raster | PIN (doc §5.3 `SetBoundsAndRoute`) |
| L188 | boundary `Y` moved (position-only bounds change, e.g. `Canvas.SetLeft`) | `layout()`, `render()` | parameters-only: `P(Y).Offset*` changes, zero `Render` calls — the cheap layer move | PIN |
| L189 | **non-boundary** `X` moved position-only | `layout()`, `render()` | `X`'s zone re-rasters (cells physically move in the raster) — why position animations must use `RenderOffset*`, never `Margin`/`Canvas.Left` | PIN (invariant 3, documented loudly) |
| L190 | boundary child `B` at sibling index 0; **later-document-order non-boundary sibling** `S` overlapping `B`'s rect | `render()`; inspect composite result | `B`'s layer composites **above** all of the parent zone's raster including `S` — the **zone-base rule** (a zone's scene is the lowest layer of its subtree) | PIN (doc §5.6; DEV from strict document-order fidelity, recorded) |
| L191 | boundaries nested two deep + boundary siblings with `ZIndex` | `render()`; inspect `CollectLayers` order | layer order = pre-order DFS of the **boundary tree** with `(ZIndex, index)` sibling sort; each zone's own scene below its descendant boundary layers | PIN (doc §5.6) |
| L192 | non-boundary `SP` containing an `SCP` (always-boundary) | `SP.Visibility = Hidden`; `render()` | **same frame**: the SCP's layer publishes `Clip == Rect.Empty`; `LC` stable (layer retained) — the doc's pinned oracle scenario | PIN (doc §5.6, explicitly pinned for this matrix) |
| L193 | boundary `Y` | `Y.Visibility = Hidden`; `render()`; then `Visible`; `render()` | both flips are **parameters-only** (empty clip ↔ restored clip); zero `Render` calls; `LC` stable | PIN (doc §5.6 fast path) |
| L194 | non-boundary `X` with painted content | `X.Visibility = Hidden`; `render()` | `X`'s zone re-rasters (the `AffectsRender`-equivalent route — cells must be erased); Hidden subtrees paint nothing | PIN (doc §5.6; terminal deviation ⑧) |
| L195 | boundary `Y` | `Y.Visibility = Collapsed`; `layout()`; `render()`; then back | zero-size boundary **keeps its scene** (else rents 1×1) and publishes `Clip == Rect.Empty`; `LC` stable across collapse/expand; re-expand re-rasters once | PIN (doc §5.5 zero-size pin) |
| L196 | inheriting styled property with `[AffectsRender]`; descendants in two different zones, a third zone with no inheriting descendants | set at root; `render()` | the two affected zones re-raster; the uninvolved zone's `RC` unchanged — inherited fan-out bounded to zones actually containing affected elements | PIN (doc §5.5; R6) |
| L197 | L190's tree | `hit` inside the overlap region | returns `B`'s subtree element — **hit order ≡ composite order**, including the zone-base rule (topmost-first layer walk, then zone descent) | PIN (doc §5.8) |
| L198 | boundary `Y` with `RenderOffsetColumn = 3` | `hit` at the offset position / at the old layout position | hit at offset position finds `Y`'s content (layer effective offset transforms the point); old position falls through to what's beneath; a point outside `P(Y).Clip` skips the layer entirely | PIN (doc §5.8) |
| L199 | ① container `IsHitTestVisible = false` with a child inside it ② Hidden subtree ③ element whose `HitTestCore` returns false | `hit` over each | ① the **child** is still hit; the container itself never is (the gate applies to the leaf, not the subtree — DEV from WPF's subtree semantics; doc §5.8 "gate the leaf", spec §3.10 pseudocode) ② Hidden excludes the whole subtree ③ falls through to the element below | PIN/DEV (doc §5.8) |
| L200 | overlapping siblings with `ZIndex`; a child overflowing its parent's rect; warmed `hit` loop | `hit` | intra-zone descent is descending `(ZIndex, index)`; the overflowing child is hittable outside its parent's rect (live `Bounds`, no parent-rect pre-clip — consistent with the painter); **0 bytes per `HitTest`** | PIN (doc §5.8) |

---

## 12. Scrolling: banded scenes (T4) — L201–L218

Harness: `SCP` under `Root`; `CanScrollVertically` default true, `CanScrollHorizontally` default false; content = a tall `Probe`-instrumented stack. `K = max(viewportRows, 8)`; band per LD11. The banded policy is the doc's (LD13) — no `SceneBudgetCells`, no degraded mode.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| L201 | `SCP` attached with content | `render()` before any scroll | predicate ⑥: SCP is a boundary **from attach** (no mid-life promotion); `LC` includes its layer | PIN (doc §5.7) |
| L202 | content `Probe(10×100)` | measure SCP at `20×10` | content's recorded vertical constraint = `LayoutLimits.MaxScrollExtent` (32 000), **not `U`**; `Extent = 10×100`; `desired(SCP) = min(extent, constraint) = 10×10` | PIN (doc §12 scrolling note; supersedes spec §3.9's Unbounded) |
| L203 | `CanScrollVertically = false` | measure | vertical constraint passes through unchanged; offsets coerce to 0 (no scroll range) | PIN |
| L204 | L202 arranged at `20×10` | inspect | content arranged at `(0, 0, viewportW × max(extentH, viewportH))` in content coordinates (non-negative); `Viewport = SCP arranged size`; `Extent`/`Viewport` are DirectProperty readbacks with change notifications | PIN (spec §3.9 mechanics, doc-compatible) |
| L205 | `Extent.Rows = 100`, `Viewport.Rows = 10` | `ScrollOffsetRow = −5` / `= 95` | coerced at set time into `[0, Extent − Viewport]`: `0` / `90` | PIN (WPF ScrollViewer-shaped) |
| L206 | scrolled to `90`; content shrinks to extent 50 | `layout()` | offset re-coerced **at end of arrange** to `40` same frame; exactly one composite-lane change (fires only on actual movement); the cached raster is never left slid past the content | PIN (doc §5.7) |
| L207 | rendered SCP at anchor 0 | `ScrollOffsetRow = 5` (≤ K); `render()` | **zero `Render` calls** — pure composite slide; layer offset shifts by −5; viewport clip unchanged | PIN (invariant 3; doc §5.7) |
| L208 | viewport 10 rows, extent 100 | inspect the zone scene after first raster | scene rows = `min(100, 10 + 2K) = 30` (K = 10); `bandStart = clamp(anchor − K, 0, extent − 30)` — memory bounded by construction | PIN (LD11) |
| L209 | anchor 0, K = 10 | `ScrollOffsetRow = 11`; `render()`; then `= 15`; `render()` | first write trips the re-anchor (`|11 − 0| > 10`): **one** band re-raster, `anchor = 11`; second write is in-band (`|15 − 11| ≤ 10`): composite-only | PIN (LD11; doc §5.7 "re-anchors once") |
| L210 | scrolled to `Extent − Viewport` (= 90) | `render()` | band clamps to the extent end (`bandStart = extent − bandLen = 70`); no out-of-range raster; further max-offset writes are no-ops | PIN (LD11) |
| L211 | offset write past the re-anchor threshold | observe synchronously, then `render()` | the metadata-changed handler runs the re-anchor **check** and marks the zone dirty only — no raster work inside the property write; the re-raster happens in the next `RunRenderPass` | PIN (doc §5.7: "re-anchor check in the metadata handler") |
| L212 | extent 25 ≤ viewport + 2K (= 30) | scroll across the whole range | band = the whole extent; **no offset value ever re-anchors**; every frame composite-only | PIN (LD11) |
| L213 | `BeginAnimation(SCP, ScrollOffsetRowProperty)` handle | per-frame `handle.SetValue(...)` within the band; one value out of range | offsets are **styled** properties: the Animation lane drives them (smooth scrolling is storyboard-able); in-band frames re-raster nothing; coercion applies to animated writes too | PIN (doc §5.7; S5 A-gate) |
| L214 | `GetEffects` on `ScrollOffsetColumn/Row` for SCP | inspect | exactly `AffectsComposite` (no measure/arrange/render flags) — the lane that guarantees zero re-raster | PIN (doc §5.7) |
| L215 | content desiring 50 000 rows | measure | `Extent.Rows == 32_000` (clamped, one-time diagnostic); offset coercion range uses the capped extent | PIN (doc §5.7/§12) |
| L216 | scrolled SCP with content drawn past the viewport | `render()`; inspect `P(SCP)` | `Clip` = absolute viewport rect ∩ ancestor clips — band content outside the viewport never reaches the screen | PIN (doc §5.7) |
| L217 | a boundary element **inside** scrolled content | scroll; `render()` | the nested layer's effective offset subtracts the scroll, its clip intersects the viewport; scrolled out of view ⇒ clipped away with the layer retained (`LC` stable) | PIN (doc §5.7) |
| L218 | SCP scrolled by 5; content child at content-row 7 | ① `hit(col, row 2)` ② settle, then `render()` | ① returns that child — the layer's effective offset folds `−ScrollOffset`, hit testing inherits scroll for free ② an idle scrolled frame does zero work (no `Render` calls, parameters unchanged) | PIN (doc §5.8/§5.6) |

---

## 13. Signed margins (P2.6 — LD19) — L219–L225

Companions to the amended L37 (measure math). Layout rows use the §0.1 fixtures; render/hit rows use
the render harness. `LayoutRect` rows are written `(col, row, w×h)` like `Rect` rows — the origin may
be negative.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| L219 | `Probe(4×2)`, `M(0,−1,0,0)`, `H = Left`, `V = Top` | `arrange(e, (0,3,10×6))` | `bounds = (0,2,4×2)` — child origin = slot origin + **signed** margin offset (`3 + (−1)`); the slot deflate enlarged the inner slot to `10×7` | WPF (LD19) |
| L220 | vertical `SP` under `Root`; A `Probe(10×2)`, B `Probe(10×1)` with `M(0,−1,0,0)`, C `Probe(10×1)`; distinct fill glyphs | `layout(10×6)`; `render()`; composite | `desired(B) = 10×0` (L37 clamp); `bounds(A) = (0,0,10×2)`, `bounds(B) = (0,1,10×1)` — B **overlaps A's last row**; `bounds(C) = (0,2,10×1)` — the stack advance consumed B's clamped desired (0 rows). Composite cells: row 1 shows B's glyph (later document order paints over), rows 0/2 show A's/C's | WPF (LD19) |
| L221 | `Probe(4×2)`, `M(−1,0,−1,0)`, `H = Stretch`, `V = Top` | `arrange(e, (0,0,10×6))` | inner slot widens to 12; `bounds = (−1,0,12×2)` — negative left+right **widen** beyond the slot, origin signed | WPF (LD19) |
| L222 | render harness: vertical `SP` root 10×4; single child `Probe(4×2)`, `M(0,−1,0,0)`, distinct glyphs per local row | `layout(10×4)`; `render()`; composite | `bounds(child) = (0,−1,10×2)` — negative origin at the zone's top edge; the child's local row 0 **clips above the zone** (cells above/left of the zone never paint — the P2.5 ① per-cell push-stack clip), local row 1 lands on zone row 0; no throw, no wraparound | PIN (LD19; doc §5.7 push-stack) |
| L223 | render harness: vertical `SP` root 10×4; child `Probe(4×2)`, `M(−1,0,−1,0)`, `H = Stretch` (bounds `(−1,0,12×2)`) | `hit(0,0)` / `hit(9,0)` | both return the child — negative-origin bounds hit-test exactly where they paint; the local transform is `point − bounds origin` (window `(0,0)` → local `(1,0)`); cells left of the zone (window column −1) are unreachable by construction; `TranslateToWindow`/`TranslateToLocal` fold the signed origin (`child.TranslateToWindow(0,0) = (−1,0)`, `child.TranslateToLocal(0,0) = (1,0)` — round-trip identity) | PIN (LD19; doc §5.8) |
| L224 | `Probe(4×2)`, `MinWidth = 12`, `M(−1,0,0,0)`, `H = Center` / `Right` (one `[Theory]`) | arrange into `(0,0,10×6)` | `bounds = (−1,0,12×2)` in both — inner slot 11 < used 12, so the LD3 offset clamp **stays** (offset 0, overflow right); the negative origin comes **solely** from the `margin.Left` term (WPF would center the overhang at a further negative offset) | DEV (LD3 retained) + WPF (LD19 margin term) |
| L225 | `Probe(4×1)`, `M(0,−3,0,0)`, `H = Left`, `V = Top` | `arrange(e, (0,5,10×6))` | `bounds = (0,2,4×1)` and `ArrangeOverride` receives `4×1` — the non-Stretch arrange content size is the cached **natural** size (post-MinMax, pre-margin; WPF's `_unclippedDesiredSize`), never the `DesiredSize − margin` reconstruction, which would "recover" \|margin\| (3 phantom rows here) whenever the DesiredSize floor engaged (margin more negative than the natural extent) | WPF (LD19 — the `_unclippedDesiredSize` round-trip) |

---

## 14. Test authoring contract

Each numbered row above becomes **exactly one** xUnit test in `Cursorial.UI.Tests`, named after its row id with a behavior slug: `L134_Grid_EqualStars_LargestRemainder_TiesToLowestIndex` (`[Fact]`) — rows whose Expected cell enumerates a family (L43, L45, L125, L199) become a single `[Theory]` with one case per family member, keeping the row↔test bijection at the row level. Tests live under `Cursorial.UI.Tests/LayoutMatrix/`, one file per section (`Section01_LayoutMath.cs` … `Section13_SignedMargins.cs`), sharing the §0.1 fixture via a common harness class (instrumented `Probe`/`Fit` element types registered once — dense property ids are process-global, so registrations must be idempotent across test classes). Rows are not merged, reordered, or "covered implicitly by" other rows: a row without a matching test is a P1 exit-criterion failure (§14 P1: "layout oracle matrix green"). Rows are staged: §§1–8 must be green at T1 exit, §§9–10 at T2, §11 at T3, §12 at T4, §13 at the P2.6 batch — later-stage rows may be absent (not red) before their stage opens. DEBUG-diagnostic rows (L11, L12, L57, L87, L89) compile their diagnostic assertion under `#if DEBUG` and assert the absence of a throw in Release where practical. Allocation rows (L91, L200's zero-byte clause) follow the repo norm: `GC.GetAllocatedBytesForCurrentThread()` deltas after warm-up, single-threaded `[Fact]`s, not BenchmarkDotNet. When the implementation cannot honor a row, the resolution is a PR that amends this file (and, where the row carries a `PIN`/`DEV` tag, the LD ledger) **before** the code change lands — the matrix is the oracle, not the implementation. Oracle tags document provenance and do not alter test behavior.
