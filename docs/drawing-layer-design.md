# Cursorial.Drawing — intermediate drawing layer design

> Status: **living design doc.** Phases 0–2 are implemented (`feature/drawing-layer-foundation`);
> Phases 3–6 are designed here and not yet built. This is the document we implement from; update it
> as phases land or decisions change.

`Cursorial.Drawing` is an intermediate layer between the cell-buffer renderer (`Cursorial.Rendering`)
and a future widget/UI layer (`Cursorial.UI`). It adds the authoring capabilities the cell grid
deliberately lacks: **brushes** (solid + linear/radial/conic gradients), a **pen + line/box engine**
with automatic junctions, **charts** (bars, scatter, lines/curves) on a shared sub-cell raster, and
**scene compositing** with cached rasters and animation — all while the cell grid stays a flat grid
of solid-color cells.

---

## 0. The compositing invariant (the spine)

> **A scene cell is never composited onto a previously-composited cell; it is always composited onto
> the base layer (or a lower scene that was itself freshly composited onto base, in z-order).**

This is what makes retained/cached scenes correct. Compositing a 50%-red scene onto a *retained*
target each frame drifts `(127,0,128) → (190,0,64) → (222,0,32) → …` — a static translucent panel
visibly saturates and re-emits every frame. Compositing onto **base** each frame is stable at
`(127,0,128)` forever → an empty renderer delta. The compositor enforces this by **resetting the
affected region to base, then compositing the z-stack over it.**

---

## 1. Placement, dependency direction, and the scalar-color invariant

```
Cursorial.UI (future)
        │
Cursorial.Drawing          ← this layer
        │  ProjectReference (the only edge)
Cursorial.Rendering        Cell, CellBuffer, CellBufferView, FrameRenderer, Cursorial.Rendering.Text/*
        │
Cursorial.Core             Color, Style (ns Cursorial.Output), IBlendingMode, GraphemeWidth, OutputCapabilities
```

A terminal cell shows **one solid color**. `Cell`/`Style`/`CellBuffer` carry scalar `Color`, never a
`Brush`. Because there is no `Rendering → Drawing` edge, the `Brush` type cannot be named in
Rendering/Core — so `CellBuffer.Set` cannot grow a `Brush` overload and `Cell` cannot gain a `Brush`
field. **Brushes resolve to a scalar `Color` strictly inside `Cursorial.Drawing`, at draw time**
(`GradientSampler → Color → Style → CellBufferView.Set`). This also makes *sample-before-quantize*
hold structurally: sampling happens at write time in Drawing; `StyleQuantizer` runs at emit time in
`FrameRenderer` — different layers, crossed in that order.

- `ICellSurface` stays **internal**; the layer targets the public `CellBufferView` seam.
- No public API change to Core/Rendering. The **only** additive Core change is `Style.Transparent`.

---

## 2. `Style.Transparent` (Phase 0 — done)

`Style.Transparent` = foreground/background/underline-color all `Color.Transparent`, hyperlink
`None`. A cell carrying it contributes no color when composited (`Color.Composite` short-circuits a
transparent source to the backdrop), so it is what a scene buffer is cleared to — unpainted cells
leave the composite target untouched. Distinct from `Style.Default`, which paints terminal-default
colors *opaquely*.

---

## 3. Scene / DrawingContext / Compositor (Phase 1 — done)

**Model.** A `Scene` owns a backing `CellBuffer` cleared to `Style.Transparent`. You author via a
`DrawingContext`; you composite scenes onto a target via a `SceneCompositor`.

### 3.1 Cached raster (pseudo-retained)

A scene's rasterized cells **persist**. `Scene.Draw(Action<DrawingContext>)` re-rasters only when the
owner has called `Invalidate()`; otherwise it is a no-op and the cached raster is reused. The
expensive work (gradient sampling, junction resolution, text layout, curve interpolation) lives
behind that gate. The layer is **memoryless** — it records no draw operations, so it cannot
auto-detect content change or re-flow on resize; invalidation is owner-driven and coarse
(whole-scene). `Scene.RasterVersion` bumps on each actual re-raster and is the compositor's change
signal (since `Draw` clears `IsDirty` before the compositor runs).

Two cache tiers compose:
1. **Scene raster cache** — skips the expensive draw.
2. **`FrameRenderer` front-buffer diff** — skips emit.

A fully static frame: no scene re-rasters, the compositor finds an empty dirty union and returns
`false` (target untouched), and `FrameRenderer` emits an empty delta.

### 3.2 Compositor

`SceneCompositor.Composite(ReadOnlySpan<SceneLayer>, in CellBufferView target) → bool`:
1. Compute the **dirty-region union** of layers whose `RasterVersion` changed or whose
   `CompositeParameters` changed (unioning the **old and new** footprints, so a moved layer's vacated
   region resets). First frame / layer-count change ⇒ full target.
2. Empty union ⇒ return `false` (no work; target untouched).
3. **Pass 1:** reset the whole union to base (before any compositing, so a wide glyph written in
   pass 2 isn't clobbered when its continuation column is reset).
4. **Pass 2:** composite the full z-stack over the union, bottom-up. Every cell lands on base or on a
   lower layer freshly composited this pass — the invariant (§0) holds.
5. `target.MarkDirty(union)` so a `RestrictToDirtyRegions` renderer gets a correct bounded repaint;
   return `true`.

The target is treated as a **retained** buffer the compositor maintains incrementally — do not clear
it between frames.

**Per-cell composite** (`CompositeCell`): skip `WideContinuation`; scale the source style's alpha by
the layer opacity; `mergedBg = Color.Composite(sourceBg, dstBg, mode)`. If the source grapheme is
null/empty → **background-only** path (keep target glyph/fg/hyperlink, swap bg) via the **raw
indexer** (the compositor already composited, so routing through `Set` would double-composite). Else
→ **glyph** path: `mergedFg = Color.Composite(sourceFg, mergedBg, mode)`; `WideLeft` goes through
`target.Set` (so the orphaned neighbor is cleaned up), single cells via the raw indexer. The scene
owns its glyphs (and their hyperlinks); background-only cells keep the target's hyperlink.

**Base** is region-reconstructable: a uniform `Style` (`new SceneCompositor(Style)`) or a stored
backdrop `CellBuffer` (`new SceneCompositor(CellBuffer)`, target-buffer coordinates).

**Composite parameters:** integer cell translation (no affine — a cell grid can't do sub-cell
rotate/scale), uniform `Opacity` (stored as a complement so `default` is the **opaque identity**),
optional `Clip`, optional blend `Mode`. Scene **nesting** (compositing onto a parent scene for
grouped opacity/transform) is deferred but free — the composite target is a `CellBufferView`, so a
parent scene's view can later be the target with no API change.

### 3.3 Drawing

`DrawingContext` (Phase 1): scalar `Set(col,row,grapheme,style)` and `FillRectangle(in Rect, in
Brush)`. `FillRectangle` writes background-only cells via the **raw indexer** so a translucent source
color is preserved verbatim (its alpha kept for the compositor); going through `Set` would consume
the alpha by pre-compositing over the transparent backdrop. Per-cell *translucent source* via `Set`
therefore isn't available in Phase 1 — use composite opacity, or `FillRectangle`'s path.

### 3.4 Pooling

`ScenePool.Rent(cols, rows)` recycles backing buffers (resizing on reuse; the ctor re-clears to
transparent); `Scene.Dispose()` returns the buffer. Persistent (cached) scenes are owner-held via
`Scene.Create(...)`. Phase 1 keeps the pool simple (single free list); size-bucketing is a later
refinement.

---

## 4. Brush + premultiplied gradients (Phase 2 — done)

`Brush` is a closed value-type discriminated union (`readonly record struct`), kinds Solid + Linear +
Radial + Conic, mirroring `Color`. Zero-alloc implicit `Color → Brush` is the back-compat keystone.
The gradient payload sits behind one reference (`GradientData` sealed record) so the struct stays
small and the solid path is alloc-free. `default(Brush)` is an opaque solid over `Color.Default`
(not invisible).

- **`GradientData`** uses **nullable** geometry params (so `(0,0)` is never a magic sentinel); stops
  are sorted in the ctor. `GradientSpread` = Pad/Repeat/Reflect.
- **`BrushExtent`** (already added) is a signed/fractional extent — `Rect` is `ushort` and throws on
  a negative origin, so a gradient anchored off-screen or larger than its clip needs this. The brush
  is **bounds-agnostic**; the paint site supplies the extent = the painted element's bounds (run /
  paragraph / fragment / shape / scene), i.e. relative-to-bounding-box.
- **`GradientSampler`** (per-fill object, **not** in the struct) holds a 256-entry LUT, samples at
  the **cell center** (`+0.5`), in **cells**, relative to the extent. Math (verified against
  Consolonia `feature/gradient-improvements`): linear `t = dot(p−start, v)/dot(v,v)`; radial SVG
  focal/two-point unit-ellipse quadratic `t = 1/s`; conic `frac((atan2(dx,−dy)·180/π − Angle)/360)`.
  Spread maps raw `t` into range before a nearest-enclosing-stop scan; out-of-coverage pads to end
  colors. **As-built:** gradients are box-relative (a radial fills the extent as an ellipse);
  cell-pixel aspect correction (true on-screen circles via `WindowCapabilities.CellPixelWidth/Height`)
  is **deferred** as a refinement.
- **Premultiplied-alpha interpolation** (sRGB channels): premultiply each stop's RGB by alpha, lerp,
  un-premultiply to a **straight** `Color`. This flows through the existing straight-alpha
  `Color.Composite` correctly and removes fade-to-transparent fringing (so `Color.Transparent` =
  `0x00000000` stays fine). Brush opacity folds uniformly into every LUT entry (solid **and**
  gradient — fixing Consolonia's solid-vs-gradient asymmetry). Keep LUT math in floats; quantize only
  the final straight `Color`. Add a low-alpha (α 1–4) precision test.
- Banding on low-color terminals is accepted for v1 (nearest palette per cell); ordered dither is
  deferred.

Phase 2 also delivers `DrawingContext.DrawText` — **single-line, unlaid-out** brush text: walk
grapheme clusters, sample fg/bg per cell across the run, build a scalar `Style`, `Set`.

---

## 5. Pen + line/box/junction engine (Phase 3 — designed)

`Pen` = `Brush` + `StrokeWeight {Light, Heavy, Double}` + `CornerStyle {Sharp, Rounded}` + `LineDash`
+ `EndCap {None, Stub}` + `JunctionMode {Merge, Break, Overlay}`. `BorderPen` for per-side/per-corner
asymmetry. **Weight selects a glyph family, never pixel thickness** (a stroke is one cell wide).
Consolonia (`feature/even-better-boxes`) is the **capability bar**, not a template — design clean;
don't inherit its decoration-byte split or `LineBrush` color/style coupling.

The engine rides the **shared per-cell accumulator** (§6), **scoped to the scene**, so junctions form
across separate draw calls *within a scene* but never between unrelated scenes (separate
accumulators). A stroke contributes per-direction half-edges; junctions resolve automatically at
flush (no separate corner code). The glyph table (light/heavy/double + mixed, rounded, dashed,
capped) becomes data with a **fallback ladder** that never throws (exact → per-arm weight downgrade →
collapse to Light → drop decoration → built-in). Dashes are per-cell glyph density (dropped at
junctions); caps are lone-direction stubs; box glyphs are interned (no per-cell string alloc).
**Wide-glyph collision:** a box write on/adjacent to a wide cell **evicts** the affected edge entries
(wide glyph survives, one-cell gap), with an opt-in `Overwrite` policy.

---

## 6. Shared accumulator + charts (Phase 4 — designed)

One generalized mechanism: a per-cell `uint` mask accumulated during a scene draw pass, resolved to a
glyph on flush, with pluggable layouts:

| Layout | Mask | Merge | Used by |
|---|---|---|---|
| `BoxEdgeLayout` | 2 bits/dir × 4 dirs | MAX/dir | pen/junctions (§5) |
| `BrailleDotLayout` | 8 dots (`U+2800 \| mask`) | OR | scatter / line / curve |
| `BlockFractionLayout(axis)` | fill level 0–8 | MAX | bars |

The accumulator stores the source `Brush` per cell and samples at flush (so the per-fill
`GradientSampler` applies; sample-before-quantize preserved). `BlockFractionLayout` carries an axis,
so it's instance-keyed (not a singleton).

**Charts are scenes** (inheriting the cached-raster tier):
- **Bars** — eighth-block fractional heights, brush fill.
- **Scatter** — markers / Braille dots, per-series brush.
- **Line/curve** — rasterized into the Braille 2×4 grid; interpolation linear / Catmull-Rom /
  **monotone-cubic (Fritsch–Carlson)** preferred for monotonic data (no overshoot).
- **Axes** are ordinary `Pen` strokes through the same box accumulator (ticks/corners resolve as
  junctions).
- **Multi-series color:** a Braille cell has one foreground, so **per-series scenes composited** is
  the default (true per-series color); last-writer-wins single accumulator is the cheaper opt-in.

---

## 7. Animation (Phase 5 — designed, gated)

Split mechanism vs orchestration:
- **Mechanism** (pure, `elapsed → immutable snapshot`): `Animation<T>` / easing / keyframe timeline,
  no clock/thread/timer, no drawing/UI dependency. Lives in its own lean **`Cursorial.Animation`**
  project (depends on `Cursorial.Core` only); the `Brush` interpolator lives in `Cursorial.Drawing`
  (keeping the arrow `Drawing → Animation → Core` acyclic). The `Color`/`Brush` interpolators are
  premultiplied (consistent with §4).
- **Targets:** an animated brush yields a fresh immutable `Brush` per frame; composite params
  (opacity, integer offset).
- **Orchestration** (clock, render loop, invalidation, triggers/storyboards, element lifecycle) lives
  in the future `Cursorial.UI`. **The drawing layer stays time-free** (the consumer/UI advances the
  clock).
- **Content slide/fade** re-composites a **cached** scene with new offset/opacity (content rasterized
  once); only an *animated brush* re-rasters its own scene.

---

## 8. FormattedText brush story (Phase 6 — designed, gated)

Additive, not a rename or move. `FormattedText`/`RichText`/`TextFormatter`/`TextMarkup` **stay in
`Cursorial.Rendering`** (their color coupling is to Core `Style`, not `Brush`). Phase 2's `DrawText`
covers single-line brush text. A laid-out brush-aware formatter (continuous gradients across wrapped,
aligned text) reuses `TextFormatter`'s color-free measurement and a parallel **`BrushedStyle`** in
Drawing (`Brush` never goes inside `Style`). Markup keeps returning `Color` (`[fg=…]` → implicit
`Color → Brush`); gradient markup grammar is deferred.

---

## 9. Resolved decisions

- **Brush representation:** closed value-type struct-DU (zero-alloc `Color → Brush`; `BrushKind`
  fixed; a future `Custom`/`IBrushSource` escape hatch is the only allocating case).
- **Gradient color space:** straight **sRGB** channels, **premultiplied** alpha; linear-light is an
  opt-in note only.
- **Text brush coordinate space:** physical paint bounds (verbatim run copy); logical-span anchoring
  is a deferred opt-in.
- **Markup gradients:** deferred (solid-only markup in v1).
- **Wide-glyph collision:** **evict** by default (text beats decoration), opt-in overwrite.
- **Multi-series Braille color:** **per-series scenes** by default, last-writer-wins opt-in.
- **Base-layer form:** ship **both** (uniform `Style` + stored `CellBuffer`); default the loop to
  uniform-fill.
- **Animation packaging:** a separate `Cursorial.Animation` project, decided at Phase 5.

---

## 10. Phased plan & status

| Phase | Scope | Status |
|---|---|---|
| **0** | `Style.Transparent` (Core, additive). | **Done** |
| **1** | `Cursorial.Drawing` project; `Brush`(solid) + implicit `Color→Brush` + `BrushExtent`; `Scene` (cached raster) + `DrawingContext` (`Set`, solid `FillRectangle`) + `SceneCompositor` (invariant + dirty-union + both base overloads) + `CompositeParameters`/`SceneLayer`/`ScenePool`. | **Done** |
| **2** | `GradientData`/`GradientStop`/`GradientSpread`/`GradientKind` + `GradientSampler` (LUT, premultiplied, verified math) + gradient `Brush` factories; gradient `FillRectangle`; single-line `DrawText`. | **Done** |
| **3** | Shared accumulator + `BoxEdgeLayout`; `Pen`/`BorderPen`; `DrawLine`/`DrawBox`/`DrawBorder`/`DrawRectangle`; wide-glyph eviction. | Designed |
| **4** | `BrailleDotLayout` + `BlockFractionLayout`; `Chart`/`BarChart`/`ScatterChart`/`LineChart` + curve interpolation; multi-series scenes. | Designed |
| **5** (gated) | `Cursorial.Animation` + `BrushInterpolator`. | Designed |
| **6** (gated) | Laid-out brush formatter (`BrushedStyle`). | Designed |

Phases 0–4 are the v1 spine; 5–6 are gated. No phase changes a Core/Rendering public signature beyond
the additive `Style.Transparent`.

---

## 11. Known deferrals / hardening (carry forward)

- **Wide glyph at the composite union's *right* edge** — when a `WideLeft` source lands at the union
  edge, `CompositeCell`'s `target.Set` writes the continuation one column past the reset range and the
  `MarkDirty` union, leaving a stale `WideContinuation` a `RestrictToDirtyRegions` renderer never
  revisits (a dirty-region hole, not just a visual glitch). The left edge is fine (continuation falls
  inside the reset range). Phase 3 hardening: in `CompositeCell`, degrade a `WideLeft` to a blank
  single cell when `column + 1 >= colEnd` (the same trick `CellBuffer.Set` uses at the buffer edge).
- Box vs image/sized-text **fragment** overdraw: v1 scenes are **cell-only**; fragments stay on the
  main buffer and emit after the cell pass. Offscreen fragment compositing is deferred.
- Animated/large-area gradient emit cost is mitigated by cached raster + per-scene invalidation; a
  scroll-false-positive probe (a sliding scene over patterned rows must not trip `FrameRenderer`'s
  scroll detection) is a Phase 5 test.
- **`FillRectangle` cannot occlude** lower-layer glyphs (background-only by design). An additive
  `FillBlock`/`occlude` primitive (space-bearing cells) is a UI-layer need, not a Phase-3 blocker.
- Deferred guardrails from the adversarial review (P2s, non-blocking): radial focal-point outside the
  unit ellipse; normalizing `null` vs explicit `BlendingModes.Default` in `CompositeParameters`;
  `Scene.Create` size cap vs `ushort` `Rect`. Verified solid and left as-is otherwise.

> **Adversarial review (Phases 0–2):** passed — foundations (the compositing invariant, premultiplied
> gradient math, transparency model, one-way dependency) independently confirmed correct. Two P0
> silent-failure bugs fixed (compositor scene-identity miss; stored-backdrop out-of-bounds) plus
> P1s (rect `Fill` transparent-clear consistency; conic `spread` dropped as a no-op; `ScenePool`
> double-dispose idempotency; `CompositeParameters.WithMode`) — all with regression tests. The
> low-alpha premultiplied precision test (α 1–4) is now in the suite.
