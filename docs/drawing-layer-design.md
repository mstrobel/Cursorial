# Cursorial.Drawing — intermediate drawing layer design

> Status: **living design doc.** Phases 0–6 are implemented (`feature/drawing-layer-foundation`); this is
> the document we implemented from, kept current as the canonical reference. Update it as decisions change
> or follow-up work lands.

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

A terminal cell shows **one solid color**. `Cell`/`Style`/`CellBuffer` carry scalar `Color`, never an
`IBrush`. Because there is no `Rendering → Drawing` edge, the `IBrush` type cannot be named in
Rendering/Core — so `CellBuffer.Set` cannot grow an `IBrush` overload and `Cell` cannot gain a brush
field. **Brushes resolve to a scalar `Color` strictly inside `Cursorial.Drawing`, at draw time**
(`IBrush.ColorAt → Color → Style → CellBufferView.Set`). This also makes *sample-before-quantize*
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
   region resets). First frame / layer-count change ⇒ full target. A layer that declares
   `SceneLayer.Damage` — the scene-local region its single `RasterVersion` bump rewrote, which only
   `Scene.CompositeInto` can honestly report — contributes just that region instead of its footprint,
   and only when the layer is otherwise identical (same `Scene`, same `CompositeParameters`, version
   exactly one ahead). Absent damage the footprint rule is unchanged, which is every ordinary scene:
   `Scene.Draw` re-rasters wholesale and has nothing narrower to say.
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

`DrawingContext` (Phase 1): scalar `Set(col,row,grapheme,style)` and `FillRectangle(in Rect,
IBrush)` (plus a `Color` overload wrapping `SolidColorBrush`). `FillRectangle` writes background-only
cells via the **raw indexer** so a translucent source
color is preserved verbatim (its alpha kept for the compositor); going through `Set` would consume
the alpha by pre-compositing over the transparent backdrop. Per-cell *translucent source* via `Set`
therefore isn't available in Phase 1 — use composite opacity, or `FillRectangle`'s path.

### 3.4 Pooling

`ScenePool.Rent(cols, rows)` recycles backing buffers (the `Scene` ctor re-clears to transparent);
`Scene.Dispose()` returns the buffer. Persistent (cached) scenes are owner-held via
`Scene.Create(...)`. The pool is **size-bucketed** (UI P2.5 batch — it replaced Phase 1's single
free list, which resized/reallocated the recycled buffer on nearly every rent): freed buffers live
in exact-dimension buckets, so the dominant consumer — a UI render tree renting per-zone scenes
whose sizes are stable frame over frame — hits its exact size and the steady-state rent → return
cycle allocates only the small `Scene` wrapper. A size miss allocates fresh and leaves other sizes
pooled. Retention is capped (`MaxRetainedBuffers`, default 32); over the cap, a buffer is dropped
from the least-recently-used size bucket (linear scan — bucket counts are tens at most), so cold
sizes age out. Empty buckets are kept so the steady-state return path stays allocation-free.
`Rent` validates dimensions against the `ushort` `Rect` cap exactly like `Scene.Create`. Not
thread-safe — rent/return from a single render loop.

---

## 4. Brush + premultiplied gradients (Phase 2 — done)

`IBrush` is an **open interface** — one method, `Color ColorAt(int column, int row, Rect bounds)`.
A brush maps a cell (given the painted element's bounds) to a scalar `Color`; that's the entire
contract, so `ImageBrush` / `TileBrush` / a custom procedural brush drop in later without touching the
core. (Earlier draft used a closed `readonly record struct` discriminated union; it was replaced with
the interface for extensibility — the allocation argument that motivated the struct is weak because
brushes are created once and reused, not per-cell.) `ColorAt` takes bare `int column, int row` rather
than the input-layer `CellPosition` (flagged by the API review) — that type's `PixelX`/`PixelY` were
always null here and pulled input-domain semantics into the brush contract; aspect-correct sub-cell
sampling, when it lands, gets a purpose-built parameter. The doc commits `ColorAt` to pure /
allocation-free / safe-for-concurrent-invocation and warns that `bounds` may be empty (guard before
dividing by its extent).

- **`SolidColorBrush`** — `ColorAt` returns its color regardless of position. Ctor takes an optional
  `opacity` (0–1) folded into the color's alpha (RGB only — palette/default carry no alpha). The
  implicit `Color → SolidColorBrush` operator keeps `Color`-as-brush ergonomic, and `FillRectangle`/
  `DrawText` expose `Color` overloads so the solid case never names a brush type.
- **`Brushes`** — static cache mirroring `Colors` (`Brushes.Transparent`, `Brushes.Red`, …). Since a
  `SolidColorBrush` is immutable it's safe to share; reach for these instead of allocating a fresh
  `new SolidColorBrush(Color.Xxx)`. `DrawingContext.DrawText`'s default background is
  `Brushes.Transparent`.
- **`GradientBrush`** (abstract base) holds the sorted stops, `GradientSpread` (Pad/Repeat/Reflect),
  and opacity, and owns the shared per-cell resolution: subclasses map a cell to a parameter `t`
  (`ComputeOffset`), the base applies spread (`ApplySpread`) and interpolates the stops. Concrete:
  `LinearGradientBrush` (`StartPoint`/`EndPoint`), `RadialGradientBrush`
  (`Center`/`RadiusX`/`RadiusY`/`GradientOrigin`), `ConicGradientBrush` (`Center`/`AngleDegrees`).
- **`RelativePoint`** (`readonly record struct (double X, double Y)`, WPF/Avalonia-style) expresses
  gradient geometry as a **fraction of the paint bounds**: `(0,0)` = top-left, `(1,1)` = bottom-right;
  `X` runs left→right across the width, `Y` top→bottom down the height. Values outside `[0,1]` are
  valid (the point lies outside the box) — this is what lets an animated gradient scroll its endpoints
  past the box (see §7). Named compass constants (`TopLeft`, `Center`, `BottomRight`, …) and an implicit
  `(double,double)` tuple conversion keep call sites readable. **This replaced the earlier
  `startColumn`/`startRow`/… `double` params**, which borrowed the integer-cell `Column`/`Row`
  vocabulary the rest of the codebase reserves for discrete cell addresses and hid the fractional
  `[0,1]` contract (flagged by the API review). Radii stay scalar `double` fractions (`RadiusX`/
  `RadiusY`). Defaults: horizontal sweep (`TopLeft`→`TopRight`) / centered ellipse / centered sweep, so
  the brush is **bounds-agnostic** — the paint site supplies the bounds = the painted element's bounds
  (run / paragraph / fragment / shape / scene), i.e. relative-to-bounding-box.
- **Sampling**: `ColorAt` samples the **cell center** (`+0.5`), in **cells**, relative to the bounds
  origin — sampled **directly per cell, no LUT**. (The earlier 256-entry lookup table was dropped:
  terminal cell counts are small, so full-precision per-cell math is cheap and avoids the banding a
  256-step ramp introduces.) Math (verified against Consolonia `feature/gradient-improvements`):
  linear `t = dot(p−start, v)/dot(v,v)`; radial SVG focal/two-point unit-ellipse quadratic `t = 1/s`;
  conic `frac((atan2(dx,−dy)·180/π − Angle)/360)`. Spread maps raw `t` into range before a
  nearest-enclosing-stop scan; out-of-coverage pads to end colors (conic always wraps, so it overrides
  `ApplySpread` to the identity). **As-built:** gradients are box-relative (a radial fills the bounds
  as an ellipse) by default; **cell-pixel aspect correction is now available** via
  `RadialGradientBrush.CellAspectRatio` (opt-in, default `1.0` = the box ellipse) — set it to
  `WindowCapabilities.CellPixelWidth / CellPixelHeight` (≈0.5) and an equal-radius radial fills a true
  on-screen *circle* (the vertical radius is scaled so it's shorter in rows than wide in columns). The brush
  stays capability-free (no `ColorAt` caps param); the consumer plumbs the ratio. The `draw` demo shows
  ellipse-vs-circle side by side.
- **Premultiplied-alpha interpolation** (sRGB channels): premultiply each stop's RGB by alpha, lerp,
  un-premultiply to a **straight** `Color`. This flows through the existing straight-alpha
  `Color.Composite` correctly and removes fade-to-transparent fringing (so `Color.Transparent` =
  `0x00000000` stays fine). Brush opacity folds uniformly into every sampled color (solid **and**
  gradient — fixing Consolonia's solid-vs-gradient asymmetry). Keep sampling math in floats; quantize
  only the final straight `Color`. A low-alpha (α 1–4) precision test guards hue preservation.
  **As of 5b this interpolation lives in Core's `Color.Lerp`** (single source of truth, shared with the
  animation `ColorInterpolator`); `GradientBrush` calls it and applies its whole-gradient opacity itself.
- Banding on low-color terminals: nearest-palette-per-cell by default; **ordered (Bayer) dither is an
  opt-in** (`FrameRendererOptions.OrderedDither`, via `StyleQuantizer.QuantizeDithered(style, col, row)`) —
  spatially perturbs RGB by cell position before palette reduction so gradients stipple instead of striping.
  No-op at truecolor / for non-RGB. Disables vertical scroll detection while on (the dither phase is
  position-dependent and wouldn't survive a row shift).
- **Convenience (post-review):** each gradient brush has a **two-color** ctor
  (`new LinearGradientBrush(start, end)` / `RadialGradientBrush(centerColor, edgeColor)` /
  `ConicGradientBrush(start, end)`) delegating to the `IReadOnlyList<GradientStop>` ctor, restoring
  parity with `SolidColorBrush`'s implicit-conversion + `Brushes`-cache ergonomics. An implicit
  `(double offset, Color)` → `GradientStop` conversion lets multi-stop lists drop the
  `new GradientStop(...)` noise (`[(0.0, Colors.Red), (1.0, Colors.Blue)]`). Gradient geometry is
  validated **finite** (NaN/Inf throws, mirroring the opacity guard) — but **not** range-clamped, since
  out-of-`[0,1]` points are legal (animation).

Phase 2 also delivers `DrawingContext.DrawText` — **unlaid-out** brush text: walk grapheme
clusters, sample fg/bg per cell across the run, build a scalar `Style`, `Set`. (Originally
single-line; multi-line via embedded line breaks since the 2026-06-11 line-break batch — see §13.)

---

## 5. Pen + line/box/junction engine (Phase 3 — finalized; design-panel + review)

**Weight selects a glyph family, never pixel thickness** (a stroke is one cell wide). Consolonia
(`feature/even-better-boxes`) is the **capability bar**, not a template — Cursorial separates color
(`IBrush`) from stroke attributes (`Pen`), avoiding Consolonia's `LineBrush` color/style coupling and
its pattern-byte + decoration-byte split.

### 5.1 Public surface

`Pen` is a **`readonly record struct`** (mirrors `Style`); `default(Pen)` is a usable light/sharp/solid
pen because a null `Brush` resolves to `Colors.Default` at flush:

```csharp
public readonly record struct Pen {
    public Pen(Color color);                          // sibling ctor → SolidColorBrush
    public Pen(IBrush? brush);                        // null = default foreground
    public IBrush?       Brush      { get; init; }    // gradient samples vs the shape's bounds at flush
    public StrokeWeight  Weight     { get; init; }    // Light(default)/Heavy/Double
    public CornerStyle   Corners    { get; init; }    // Sharp(default)/Rounded
    public LineDash      Dash       { get; init; }    // None(default)/Double/Triple/Quadruple
    public EndCap        EndCap     { get; init; }    // None(default)/Stub
    public JunctionMode  Junction   { get; init; }    // Merge(default)/Break/Overlay
    public GlyphSet      GlyphSet   { get; init; }    // Unicode(default)/Ascii
    public TextAttributes Attributes { get; init; }   // bold/dim/blink edges (color still from Brush)
    // With* helpers (WithColor/WithWeight/…) echo Style's density; internal ResolveBrush() => Brush ?? Brushes.Default
}
public static class Pens { Light, Heavy, Double, Rounded, Dashed, Ascii }  // presets at default fg, color via call
```

Enums: `StrokeWeight {Light, Heavy, Double}`, `CornerStyle {Sharp, Rounded}`,
`LineDash {None, Double=2, Triple=3, Quadruple=4}` (named by dash count), `EndCap {None, Stub}`,
`JunctionMode {Merge, Break, Overlay}`, `GlyphSet {Unicode, Ascii}` (a *consumer* choice — Drawing
can't see terminal caps; those resolve at `FrameRenderer` emit).

**No implicit `Color → Pen`** (a design-panel empirical test showed it misbinds `Draw…(color)` to the
wrong overload). Every `Pen`-typed draw method ships a sibling `Color` overload — the IBrush/Color
convention.

**No `BorderPen`** (dropped after review). Corners auto-merge across separate `DrawLine` calls (each
deposits per-direction arms; a top edge's `right` arm + a left edge's `down` arm merge to `┌` via the
accumulator), so an asymmetric / partial border is just composed `DrawLine`s — `BorderPen` was sugar
that fragmented the API into a non-convertible second pen type. Re-addable later (additive) if one-call
asymmetric borders are demanded.

### 5.2 Engine

Rides a **per-`DrawingContext` accumulator** (`StrokeAccumulator`, internal), **scoped to the scene**,
so junctions form across separate draw calls *within a scene* but never across scenes.

- **Per-cell arms**: a `byte`, 2 bits/direction (Up/Right/Down/Left), code `None=0 / Light=1 / Heavy=2
  / Double=3`. Plus a **record-id per cell** (`0` = untouched, else index+1 into the call's
  `StrokeRecord{ IBrush Brush, Rect Bounds, StrokeDecoration{Corners,Dash,Cap} }`). Brush is **sampled
  at flush** (deferred — keeps blended-junction color open; v1 junction color is last-writer-wins).
- **Merge is per-direction MAX, NOT bitwise-OR.** (Resolves the old §5/§6 contradiction: on a 2-bit
  ordinal, `Light(01) | Heavy(10) = 11 = Double` would fabricate a weight nobody drew. Unpack each
  2-bit field, `Math.Max`, repack.)
- **Record-id-per-call** makes a cell touched twice by the *same* call (a box's own corner) self-merge
  **unconditionally** — corners always close regardless of `JunctionMode`. `JunctionMode` governs only
  *cross-call* conflicts (Merge = MAX-union; Break = incoming yields, *incoming* shows the gap; Overlay
  = incoming replaces, *prior* shows the gap).
- **Glyph ladder** (data, never throws): `Ascii` short-circuit → decoration overlay (**drop-decoration-
  to-sharp KEEPING weight** *before* weight-collapse: a heavy rounded corner → sharp-heavy `┏`, never
  light-rounded) → exact lookup (single-weight + the ~46 mixed light/heavy + light/double glyphs) →
  per-arm weight downgrade (Double→Heavy→Light, for heavy+double mixes Unicode has no glyph for) →
  collapse-all-to-Light (always hits — the light box set is topologically complete) → ASCII (`-`/`|`/`+`
  /space). Glyphs are interned string literals (zero per-cell alloc). Dashes on straight 2-arm runs
  only; caps on 1-arm cells only; rounded on the 4 all-light corners only.

### 5.3 Flush + eviction

`Scene.Draw` calls `ctx.FlushDeferredStrokes()` **after** the draw delegate. `Set`/`FillRectangle`/
`DrawText` keep writing **immediately**; the box accumulator flushes **last** — so **text-beats-
decoration is ordering**, not a priority field. One eviction predicate at flush: **skip the box glyph
where `current.Grapheme` is non-empty OR `current.Kind == WideContinuation`** (a background-only fill
has a null grapheme → the box correctly outlines a filled panel; a wide glyph's both halves are
protected → one-cell gap, glyph survives). A per-call **`bool overwrite = false`** (a *placement*
decision, on the call not the `Pen`) bypasses eviction. Box glyphs are written **through `Set`** with a
transparent background (so a translucent fill underneath survives for the compositor), single-width
(no continuation bookkeeping). This scene-local eviction is **distinct** from the §11 compositor
right-edge wide-glyph hardening.

### 5.4 Draw methods (each `Pen`-typed method ships a `Color` sibling + `bool overwrite = false`)

- `DrawLine(int x0,y0,x1,y1, in Pen)` — one **axis-aligned** run; a diagonal request **throws**
  `ArgumentException` (diagonal/Braille is Phase 4).
- `DrawBox(in Rect, in Pen)` — uniform outline (one call → corners always close).
- `DrawRectangle(in Rect, in Pen, IBrush? fill = null)` — outline **+ optional interior fill**
  (distinct from `DrawBox`; fill is the immediate background-only path, outline flushes over it).

### 5.5 Figures (`BeginFigure` / `EndFigure`)

A **figure** is a discrete group of strokes. `using (ctx.BeginFigure()) { … }` returns a disposable
`FigureScope` (flat — nested `BeginFigure` throws; the id-token makes double-dispose / manual
`EndFigure` safe). A figure does two things, **strokes-only** (immediate `Fill`/`DrawText` are
unaffected):

- **Junction partition** — strokes merge within a figure, never across. Cross-figure conflict at a cell
  is **structural last-writer-wins** (the later figure resets the cell, bypassing `JunctionMode`), so a
  cross-figure crossing shows no junction glyph (later wins the cell, a one-cell gap in the earlier).
  Set at *deposit* time, so a forgotten scope still isolates correctly.
- **Brush bounds** — `BeginFigure()` samples each member's gradient against the **union** of the
  figure's stroke rects (lazy back-patch at scope close); `BeginFigure(in Rect)` pins an **explicit**
  rect. The **implicit root (figure 0) keeps per-call bounds** — it junctions everything as one unit but
  does *not* union bounds (so a lone gradient `DrawBox` spans itself; independent shapes don't bleed).
  `FlushDeferredStrokes` closes any leaked figure before sampling (degrades to a union figure, never
  throws).

Hierarchy: **figure** (cross-figure later-wins) **> record** (`JunctionMode`) **> same record**
(unconditional self-merge).

Files: `Pen/{Pen,Pens,StrokeWeight,CornerStyle,LineDash,EndCap,JunctionMode,GlyphSet}.cs`;
`Strokes/{StrokeRecord,StrokeDecoration,StrokeAccumulator,BoxGlyphs}.cs`;
`Scenes/FigureScope.cs`. The `BoxGlyphs` table is **generated from the Unicode block via the
`unicodedata` oracle** (offline), guarding the mixed light/heavy codepoints against recall error.

**Status: implemented + tested** (90 `Cursorial.Drawing.Tests`, full suite green).

---

## 6. Charts + sub-cell rasters (Phase 4 — design-panel finalized; 4a shipping)

**Rejected §6's original "one generalized `uint` mask with pluggable layouts."** A design panel
(+ judges) showed it conflates two things: *merge + glyph-resolve* (genuinely varies per family —
MAX/dir, OR, MAX-level — and is trivial) with *deposit-conflict policy* (`StrokeAccumulator`'s
figure → record → junction hierarchy, which is **box-only** and is the bulk of its complexity).
Charts have **no junctions** (a line ORs dots, a bar MAXes a level). So a unified mask would drag
box-only complexity everywhere and need a per-cell layout tag while *still* not merging across
families — pure added state. **Decision: keep `StrokeAccumulator` untouched; add a `BrailleRaster`
(OR-merge) and a stateless `BlockGlyphs` resolver; share only the emit/eviction tail.**

| Family | Mechanism | Merge | Glyphs |
|---|---|---|---|
| Box (§5) | `StrokeAccumulator` (unchanged) | MAX/dir + junctions | U+2500–257F |
| Braille (4b) | `BrailleRaster` (OR mask, deferred) | `dots \|=` | `0x2800 \| mask`, `bit(dx,dy)=dy<3?dx*3+dy:6+dx` |
| Block (4a) | `BlockGlyphs` (stateless, write-once) | MAX level | lower `0x2580+L` / left `0x2590−L` |

- **`BlockGlyphs`** — only the **lower** (vertical bars) and **left** (horizontal bars) ramps are
  complete 8-step block families in Unicode, so `BlockAxis` is `{Vertical, Horizontal}` only (up/right
  eighths are sparse). Codepoints oracle-verified + test-pinned. Block cells are written once per cell,
  so no accumulator — bars/sparklines just `ctx.Set` the glyph (fg = sampled brush, transparent bg).
- **Charts are draw-ops, not scenes:** `IChart.Render(ctx, in Rect area)`. "A chart is a scene" = the
  consumer wraps it (`scene.Draw(ctx => chart.Render(ctx, area))`). A `ToLayers` layer-caching path was
  explored for multi-series and **cut** — it gave no per-series-color benefit over single-surface `Render`
  (see §6); it returns with `LineChart.FillArea`, where per-layer *background* compositing pays off.
- **Cross-layer flush order** (priority = write order + the existing grapheme-eviction, no new field).
  **As built:** immediate writes (text, fills, **and block bars** — bars are write-once `ctx.Set`, not a
  deferred stage) land first; then `FlushDeferred` runs **braille, then box**. So realized priority is
  `text/fill/block → braille → box` — i.e. **block sits *above* braille** (a block cell evicts braille).
  Harmless until **4c**, where `LineChart.FillArea` puts block fill + a braille curve in the same cells
  and wants line-*over*-fill: resolve there by making the area-fill **deferred** (so flush order governs)
  *or* giving the curve's `BrailleRecord` an **`Overwrite`** against the fill — decide at 4c.
- **Curves (4c):** Linear / **centripetal** Catmull-Rom (α=0.5 — uniform overshoots/cusps) /
  **Fritsch–Carlson monotone-cubic**. Correction to the old text: FC's precondition is **strictly-
  increasing X** (a line-graph tool), not "monotonic data"; on unsorted X it sorts / falls back, never
  throws. Diagonal `DrawLine` (currently throws) routes into the braille raster (Bresenham, no AA).
- **`Rect` is `ushort`/non-negative** (throws), but sub-cell projection goes transiently negative → a
  plain-`int` `SubCell`/`PointD` value type + a single clamp chokepoint, and a `PlotProjector` that
  isolates the Y-flip in one place.
- **Multi-series (4d):** single-surface, last-writer-wins color at crossings (each series otherwise keeps
  its own color). A per-series `ToLayers` path was prototyped and **cut**: opaque single-width braille means
  the top glyph *replaces* the lower one, so crossings matched single-surface `Render` (no color mix —
  blending can't change that) while losing the lower series' dots (see §6).

**Sub-phasing:** **4a** = bars + sparklines + the chart model (`BlockGlyphs`, `AxisRange`, `IChart`,
`BarChart`, `Sparkline`, `ChartDrawingExtensions`) — **shipping, no braille/curves**; **4b** =
`BrailleRaster` + diagonal `DrawLine` + the `EmitDecorationCell` seam + four-stage flush; **4c** =
`ScatterChart`/`LineChart` + curve interpolation + area-fill; **4d** = axes/ticks/labels (box strokes →
junctions) + multi-series line charts (`MultiLineChart`). **4a + 4b: implemented + tested.** 4a = block charts (ramps
oracle-pinned; bars bottom/left-anchored, later extended to signed bars about a zero baseline). 4b = `BrailleGlyphs`
(dot→bit `dy<3?dx*3+dy:6+dx`, oracle-pinned) + `BrailleRaster` (OR-merge, deferred brush, last-writer
color) + diagonal `DrawLine` (Bresenham at 2×4 sub-cell res; weight/corners/dash/cap N/A for diagonals)
+ the `EmitDecorationCell` seam shared by box & braille; `Scene.Draw` flush renamed `FlushDeferred`,
order braille-then-box (data over axes). 4c = `PointD`/`CurveInterpolation`/`MarkerStyle`,
`Curves` (Linear / centripetal Catmull-Rom α=0.5 / Fritsch-Carlson monotone-cubic — **numerically
oracle-pinned**), `PlotProjector` (Y-flip isolated), `ScatterChart` (markers / braille dots), `LineChart`
(braille curve + optional markers), `ctx.LineChart`/`ctx.ScatterChart` sugar; charts plot via the
internal braille seam (`AddBrailleRecord` + `PlotBrailleSegment`/`PlotBrailleDot`). **4a + 4b + 4c:
implemented + tested.** (`LineChart.FillArea` and NaN-gap-as-break were deferred at 4c and have since
landed — see the post-Phase-4 note below.) **4d.1 (axes): implemented + tested.** `AxisRange.Nice()` (Heckbert nice-number ticks),
`Axis` config, `Axes` (Y-left + X-bottom box-strokes meeting at `└`, numeric tick labels, optional
gridlines → junctions; returns the inset `PlotLayout` { Plot, nice X, nice Y } so axis + data align).
4d.2 = `ChartSeries` + `MultiLineChart`: single-surface `Render` — each series keeps its own color; where two
series cross a cell their dots OR-merge into one glyph painted in the later series' color (a terminal cell has
one foreground). A per-series-scene `ToLayers` path was prototyped, **cut**, then re-added once `FillArea`
gave it a purpose (translucent fills, below). Empirical finding (probe, code-cited): with opaque single-width braille, `SceneCompositor.CompositeCell`'s
single-width-glyph branch *replaces* the lower glyph and its color with the top layer's via a raw-indexer write
that **bypasses the blend stack**, so crossings resolve to the top series' color **identically to `Render`** — no
color benefit, and `ToLayers` is in fact *worse* at crossings (it drops the lower series' dots). Blending/alpha
can't rescue it: that branch composites the top foreground only against the merged *background*, never the lower
series' *foreground* — alpha just darkens the top color toward the backdrop. Per-layer compositing earns its keep
only for translucent **filled** regions: backgrounds *do* alpha-blend across layers (the same method's
background-composite step, RGB-on-RGB), so red-fill ∩ blue-fill → genuine purple. That is the home `ToLayers` ultimately earned, gated on
`FillArea`. **Phase 4 (a–d) complete + tested** (186 `Cursorial.Drawing.Tests`; curve + glyph tables
oracle-pinned; axes/markers/gridlines share the 2×4 projector so everything aligns). **Post-Phase-4 enhancements
(since landed):** `LineChart.FillArea` (coverage-thresholded at 0.35, with per-layer `ToLayers` compositing for
translucent area overlaps), NaN-gap-as-break, signed bars about a zero baseline, and bar-chart category axes.

---

## 7. Animation (Phase 5 — 5a implemented + tested; 5b/5c pending)

**Status — 5a (mechanism) implemented + tested.** The `Cursorial.Animation` project (Core-only, non-packable
like its siblings) ships `IAnimation<T>` (pure `elapsed → value`, clamped to `[0, Duration]`), the `Easing`
delegate + `Easings` catalog (Penner / easings.net curves, **oracle-pinned**), `IInterpolator<T>` +
`Double`/`Int32` interpolators, `Animation<T>` + `KeyframeAnimation<T>` (each keyframe's easing shapes the
segment *leading into* it; stable `OrderBy` so coincident keyframes give deterministic step semantics), the
repeat combinators as a 2×2 over direction × length — `Repeat(n)`/`PingPong(n)` (finite forward / bounce) and
`Loop()`/`AutoReverse()` (perpetual forward / bounce; perpetual reports `Duration = TimeSpan.MaxValue` and
wraps), and `DoubleAnimation`/`Int32Animation` conveniences (thin
subclasses over the generic engine — WPF ergonomics, one implementation). 70 tests; edge cases (huge repeat
counts, auto-reverse final-frame parity, turn-point continuity, NaN propagation, zero-duration inner) probed.

**5b (Color) — implemented + tested.** The premultiplied-sRGB lerp now lives in **`Cursorial.Core`** as the
clean 3-arg `Color.Lerp(from, to, t)` (premultiplied channels, non-RGB snap to the nearer endpoint) — the
single source of truth. `GradientBrush` calls it then applies its own whole-gradient `Opacity` via the existing
`ApplyOpacity` (its private `LerpPremultiplied` + non-RGB branch are gone; all 186 Drawing/gradient tests stay
green — a ≤1/255 alpha rounding difference on `opacity<1` is imperceptible and consumed at composite). Keeping
`opacity` off the foundational `Color.Lerp` keeps it idiomatic on the one packable assembly. `Cursorial.Animation`
adds `ColorInterpolator` (delegates to `Color.Lerp`) + a `ColorAnimation` convenience + the `Interpolators.Color`
shortcut. `Color.Lerp` numerically oracle-pinned (Core tests).

**5c (brush + composite) — implemented + tested. Phase 5 complete.** In **`Cursorial.Drawing`** (so the
`Drawing → Animation → Core` arrow stays acyclic — these value types are Drawing's): `RelativePointInterpolator`;
`BrushInterpolator` (same-shape gradient/solid blend — endpoints/center/radii/angle, opacity, pairwise stops via
`Color.Lerp`; discrete `GradientSpread` + disparate/mismatched-stop pairs **snap** at the midpoint); and
`CompositeParametersInterpolator` (offset + opacity blend; clip/mode snap) for sliding/fading a **cached** scene
with no re-raster. Conveniences `BrushAnimation` / `CompositeParametersAnimation`. Geometry interpolators round
out the set — `PointInterpolator` (continuous `PointD`, value space) plus the cell-quantized `SizeInterpolator`
and `RectInterpolator` (rounded ties-away-from-zero, clamped ≥ 0 so an overshooting ease can't go negative or
trip the `Rect` ctor), with `PointAnimation` / `SizeAnimation` / `RectAnimation` conveniences; all live in
Drawing for the same acyclic reason (`Size`/`Rect`/`PointD` are Rendering/Drawing types, not Core's).
Added 2026-06-11: `PenInterpolator` + `PenAnimation` — the brush routes through `BrushInterpolator`
(reference-equal brushes pass through unchanged, alloc-free; a `null` endpoint — terminal default
foreground — snaps); every other `Pen` member is a glyph-family/flag selection, so the whole discrete
remainder snaps at the midpoint, `CompositeParametersInterpolator`-style. The Consolonia
scrolling-gradient case (endpoints swept past 1 + `Reflect`, looped) is validated by an animation test. New
`animate` demo (the demo owns the only clock — frame × `FrameInterval`): an `AutoReverse` gradient sweep, an
easing progress bar with a live curve plot and `←`/`→` keystroke cycling of the catalog, and a
`CompositeParametersAnimation` slide+fade of a cached scene.

Split mechanism vs orchestration:
- **Mechanism** (pure, `elapsed → immutable snapshot`): `Animation<T>` / easing / keyframe timeline,
  no clock/thread/timer, no drawing/UI dependency. Lives in its own lean **`Cursorial.Animation`**
  project (depends on `Cursorial.Core` only); the `IBrush` interpolator lives in `Cursorial.Drawing`
  (keeping the arrow `Drawing → Animation → Core` acyclic). The `Color`/`IBrush` interpolators are
  premultiplied (consistent with §4).
- **Targets:** an animated brush yields a fresh immutable `IBrush` per frame; composite params
  (opacity, integer offset).
  - *Validated against Consolonia* (`feature/gradient-improvements`, `GalleryBorders.axaml`): a
    scrolling border gradient keyframes a `LinearGradientBrush`'s endpoints `0→1`, `1→2`, `2→3` (in
    bounds fractions) with `SpreadMethod=Reflect`. Cursorial supports this **today, no API change**:
    `RelativePoint` is unbounded (endpoints past 1 are valid), `GradientSpread.Reflect` mirrors the
    out-of-box vector back into the box, and a fresh immutable `LinearGradientBrush` per frame is the
    per-frame target. Covered by `LinearEndpointsBeyondUnit_WithReflect_TileTheRamp`.
- **Orchestration** (clock, render loop, invalidation, triggers/storyboards, element lifecycle) lives
  in the future `Cursorial.UI`. **The drawing layer stays time-free** (the consumer/UI advances the
  clock).
- **Content slide/fade** re-composites a **cached** scene with new offset/opacity (content rasterized
  once); only an *animated brush* re-rasters its own scene.

---

## 8. Brush-aware text & images in Drawing (Phase 6 — in progress)

**Status:** **6a.1 + 6a.2 done** — the brush-free `BrushedTextResolver` seam in `FormattedText.Paint` +
`DrawingContext.DrawFormattedText(ft, bounds, brush, caps)`, a single document brush sampled per block (2-D),
routed through **every** block/run type: text + rules per cell, FIGlet / sized text / inline content one color
at their center — so a glyph an image/icon **degrades to** picks up the gradient. Precedence: the brush colors
cells that inherited the document default foreground (unset, or equal to `FormattedText.DefaultStyle`'s
foreground); a run's own *differing* color wins — so a document that sets a default text color still receives
the gradient. A `brushtext` demo shows it. **B done** — per-run brushes: a `BrushedStyle` (`IBrush` +
`DeclarationScope` Inline/Block/Document) rides the source `TextRun`'s opaque `Tag` (propagated through
wrap-splits onto each `FormattedTextRun`); `RichTextBuilder.BrushedRun(text, brushedStyle)` authors it;
`DrawFormattedText` samples a run's brush at its scope (Inline = the run's piece rect / Block / Document) and it
**wins over** the document brush, with a per-run-only `DrawFormattedText(ft, bounds, caps)` overload.
**6b.1 done** — images in scenes: `SceneCompositor` now carries each scene's out-of-band fragments onto the
target (offset-translated anchors; tracked + rebuilt each work-frame so they move/disappear correctly; the
renderer's Key+anchor diff keeps stable ones from re-emitting; layer semantics handled by the renderer off the
target). `DrawingContext.DrawContent(rect, IContent, caps)` authors an image / icon / sized text into a scene.
Verified across **Kitty / iTerm2 / Sixel** (an `imagescene` demo).
**6b.2 done** — per-protocol image clipping via `IBufferFragment.Clip(in Rect visible)` (default null = suppress).
`SceneCompositor` computes a partially-clipped fragment's visible cell footprint and calls `Clip`: a returned
fragment re-anchors at the visible origin; null suppresses it. **Sixel** re-crops its retained RGBA to the visible
cells (pixels-per-cell) and re-encodes. **Kitty** re-places the same image with a source rectangle (`x,y,w,h` in
image pixels, mapped from the visible cells via the native pixel size) — *not* all-or-nothing; only **iTerm2**
suppresses (pre-encoded passthrough). The Kitty source-rect needs the native pixel dims, now plumbed
(`Image.CreateFragment` → `KittyImageFragment(pixelSize)`). Demo: `imageclip` (image straddling a scene clip edge).
**Also (no-distortion aspect scaling):** when an image is sized in exactly one dimension (`renderSize (cols,0)` /
`(0,rows)`), the present dimension marks the *other* as aspect-free (`AspectFreeDimension`), and the placement omits
it on the wire so the protocol scales to native aspect instead of stretching into the rounded whole-cell box —
**Kitty** omits `c=`/`r=`, **iTerm2** emits `width=auto`/`height=auto` with `preserveAspectRatio=1`. `GetSize` still
reserves a whole-cell footprint for layout (the rendered image may diverge sub-cell). At least one axis is always
pinned (clamped ≥1) so a degenerate 0 can't drop both qualifiers into an unsized native-pixel placement. Sixel is
inherently cell-quantized (it resamples to the whole-cell pixel box), so aspect-free doesn't apply to it.
**Hardened (post-review):** both `Clip` paths clamp the source rectangle to the image edge (origin → last valid
pixel, extent → remaining pixels) so a fractional px/cell ratio can't emit `x+w > width` (Kitty) or throw a
`min>max` `Math.Clamp` (Sixel, sub-1-px/cell).
**A done — true inline 1-D wrap-invariant sampling:** the tokenizer stamps each wrapped piece with its cumulative
logical offset within the source run (`FormattedTextRun.LogicalStart`) plus a shared, back-filled total width
(`InlineRunScope` — both pure column geometry, so Rendering stays brush-blind); `BrushedTextContext` carries
`LogicalColumn`/`ScopeWidth` and the inline resolver samples `ColorAt(LogicalColumn, 0, Rect(0,0,ScopeWidth,1))`, so
a wrapped run's gradient flows continuously across the wrap — identical to the unwrapped layout (pinned by
wrapped-vs-unwrapped color-equality tests, including wide CJK glyphs to fix the width-metric hinge). Synthetic
glyphs (soft-hyphen at a wrap, ellipsis, alignment padding) carry no brush tag and stay flat — acceptable
degradation. **Phase 6 complete.** (The `Image.CreateFragment` aspect-free derivation is now covered
end-to-end — both the wire behavior given the enum and the baseSize→enum mapping.)

**Goal:** author images and brush-aware rich text from the Drawing layer (Scene / DrawingContext / `IBrush`).
**Approach — bridge, not relocate.** The text-layout + content + fragment machinery stays in
`Cursorial.Rendering` (it's the cell+fragment *production / emission* layer — `TextFormatter`,
`IContent`/`Image`/`Icon`/`ScaledText`, the image codecs, glyph fonts, `IBufferFragment`, and `FrameRenderer`
emission are deeply interdependent and coupled to `CellBufferView` / `OutputCapabilities`). Drawing already
references Rendering, so it consumes these directly. The cut: **Rendering *produces* cells + fragments; Drawing
*authors & composites* them with brushes / scenes.** (Relocating the engine would drag the codecs/fonts and
still depend on `FrameRenderer` for emission — large churn, no gain. Revisit only if bridging proves worse.)

**Two paint paths today** (both → `FrameRenderer.Render` → bytes): **cells** (`CellBuffer.Set`, diffed) and
**fragments** (`IBufferFragment` escape payloads on the `CellBuffer.Fragments` sidecar, emitted *after* the
cell pass). Fragments come in two layers (`FragmentLayer`): **Cells** (Sixel / iTerm2 / OSC-66 sized-text —
anchored to cells, scroll with the grid; covered cells paint background-only) and **Overlay** (Kitty graphics —
independent plane, placement IDs, real delete). `CellBuffer` already *stores* fragments; the gap is that
`SceneCompositor` is cell-only and ignores `Scene.Buffer.Fragments`.

### 6a — brush-aware rich text (cells; no compositor change)
- **`IBrush` stays out of Rendering** (the §9 invariant). The formatter is brush-blind: colors with Core
  `Style`, measures **color-free**. Brushes ride a separate channel: an **opaque `object? Tag`** on
  `FormattedTextRun` / `FormattedBlock`, preserved through layout/wrap (Rendering treats it opaquely — no
  `IBrush` dependency, `Style` unchanged); `TextFormatter.EmitRun` copies the tag when it splits a run.
- Drawing boxes a **`BrushedStyle`** (fg / bg / underline `IBrush` + a `DeclarationScope`) into that tag via a
  **Drawing-side authoring surface** (a `BrushedRichText` builder / attach step — the Rendering
  `RichTextBuilder` / `TextMarkup` only speak `Color`).
- **`DrawingContext.DrawFormattedText(ft, bounds[, brush])`** reads the tag and samples per cell.
  **Declaration-scoped sampling — sample in the declaring element's natural geometry:**
  - **Block / document → 2-D box** (the laid-out region; vertical/radial fills span it; its extent grows with
    wrapping — natural paragraph fill).
  - **Inline (run) → 1-D reading-order strip, wrap-invariant:** sample `ColorAt(logicalX, 0, Rect(0,0,W,1))`
    where `logicalX` is the grapheme's cumulative logical offset in the scope and `W` the scope's total logical
    width. `TextFormatter` records each wrapped piece's logical start + `W` (both known at split), so a
    grapheme's color is independent of where it wraps. (Consequence: vertical/radial/conic on an inline
    degenerate to ~constant — correct for a 1-D strip.)
  - **Inline content (icons / images) are logical constituents:** they reserve gradient space (advance
    `logicalX` by their width) but the *image itself* isn't tinted — `Content.Paint` renders it opaquely;
    surrounding text flows continuously across them. (This is the *natural* offset accounting — excluding
    content would be extra work. Tinting image payloads is a future image-brush.)
  - **Graceful degradation (free):** when content lacks its graphics protocol and falls back to a *glyph*
    (Icon's configured fallback glyph, Image's placeholder, ScaledText's FIGlet), that glyph is an ordinary
    cell — routing the fallback through the same brush sampling makes it **pick up the gradient**, integrating
    the fallback with the surrounding text. Caveat: only **text-presentation** glyphs honor SGR foreground;
    **emoji-presentation** glyphs (color emoji / VS16) are painted by the terminal in their own colors
    regardless, so an emoji fallback won't tint (inherent to terminals — we can't override an emoji palette).
    Policy: content with an *explicit* fallback color keeps it; otherwise it inherits the scope's brush.
    Per-cell caveat: `IContent.Paint` takes a single `Style`, so a multi-cell fallback samples one color (at
    its span), not a per-cell gradient across its footprint — a fine approximation for small fallbacks.
- Markup gradients **landed**: a `[brush=VALUE]…[/brush]` tag. Rendering's `TextMarkup` calls an opaque
  `TextMarkupOptions.BrushResolver` (`Func<string, object?>` — no `IBrush` type, §9 intact) and stamps the run
  `Tag` via a new `RichTextBuilder.PushTag` scope; the shared `MarkupColor` parses the color tokens. Drawing's
  `BrushMarkup.Resolver`/`Options` parse **inline** gradient syntax (`linear:`/`radial:`/`conic:` + a color list,
  hex / palette / named) *or* look up a name in a `BrushedStyle` registry — both authoring styles, one tag.

### 6b — images in scenes (fragment-passthrough)
- **`SceneCompositor` fragment-passthrough** (the new mechanism): carry `Scene.Buffer.Fragments` through
  compositing — translate anchors by the layer offset, intersect the clip, re-register on the target buffer,
  respecting `FragmentLayer` (Cells → also mark covered cells; Overlay → leave cells). Drawing already
  references Rendering, so consuming `IBufferFragment` is legal (no cycle).
- **`DrawingContext.DrawImage(rect, Image)` / `DrawScaledText(...)`** — call `IContent.Paint` into the scene's
  `CellBufferView`, which registers the fragment on the scene buffer.
- **Per-protocol image capabilities (honest limits; documented degradation):**

  | protocol (layer) | slide | clip | opacity | fit |
  |---|---|---|---|---|
  | Sixel (cell) | ✓ | ✓ pixel-crop the RGBA before encode | ✗ | resample |
  | Kitty (overlay) | ✓ | ✓ native source-rect `x,y,w,h` (+ `z`) | image-alpha only | — |
  | iTerm2 (cell) | ✓ | all-or-nothing (suppress on spill) | ✗ | ✓ `preserveAspectRatio` |

  `preserveAspectRatio` letterboxes (scale-to-fit-within, never crops) — a *fit* knob, orthogonal to clipping.
- **Diff-churn caveat:** a scene that moves every frame re-anchors its fragments → loses Key-based diff-skip →
  re-emits (cheap for Kitty via image-ID reuse; expensive for Sixel — decode / quantize / encode).

---

## 9. Resolved decisions

- **Brush representation:** open `IBrush` interface — `Color ColorAt(int column, int row, Rect bounds)`.
  (Superseded the earlier closed struct-DU; the interface is extensible — `ImageBrush`/`TileBrush`/
  custom brushes drop in — and the struct's zero-alloc argument was weak since brushes are reused, not
  per-cell.) `SolidColorBrush` keeps `Color → brush` ergonomic via an implicit operator; `Brushes`
  caches the common solids; gradients carry two-color ctors + an implicit `(double, Color)` →
  `GradientStop`.
- **`IBrush` value-passing convention:** because an interface can't be an implicit-conversion target,
  there is no `Color → IBrush`. So every `IBrush`-typed public parameter ships a sibling `Color`
  overload (as `FillRectangle`/`DrawText` do); Phase 3's `Pen` and later brush-taking APIs follow suit.
- **`IBrush` evolution:** future additive members ship as **default interface methods** (C# 13) with
  sensible defaults, so external implementers don't break — the open-interface contract is forward-
  compatible by policy, not frozen.
- **Gradient color space:** straight **sRGB** channels, **premultiplied** alpha; linear-light is an
  opt-in note only.
- **Text brush coordinate space:** Block scope samples physical paint bounds (the block rect); Inline
  scope samples a wrap-invariant logical-span strip (`ColorAt(logicalX, 0, …)`, Phase 6 item A).
- **Markup gradients:** done — `[brush=linear:#f92672,#66d9ef]…[/brush]` inline or `[brush=name]` registry, via
  `BrushMarkup` + the opaque-tag channel (the `[fg=…]`/`[bg=…]` solid-color tags are unchanged).
- **Wide-glyph collision:** **evict** by default (text beats decoration), opt-in overwrite.
- **Multi-series Braille color:** **single-surface, last-writer-wins** at crossings. (A per-series-scene
  `ToLayers` path was prototyped and cut — opaque single-width braille gives it no per-series-color benefit;
  it returns with `LineChart.FillArea`, where per-layer background compositing pays off. See §6.)
- **Base-layer form:** ship **both** (uniform `Style` + stored `CellBuffer`); default the loop to
  uniform-fill.
- **Animation packaging:** a separate `Cursorial.Animation` project, decided at Phase 5.

---

## 10. Phased plan & status

| Phase | Scope | Status |
|---|---|---|
| **0** | `Style.Transparent` (Core, additive). | **Done** |
| **1** | `Cursorial.Drawing` project; `IBrush` + `SolidColorBrush` + implicit `Color→SolidColorBrush`; `Scene` (cached raster) + `DrawingContext` (`Set`, solid `FillRectangle`) + `SceneCompositor` (invariant + dirty-union + both base overloads) + `CompositeParameters`/`SceneLayer`/`ScenePool`. | **Done** |
| **2** | `GradientStop`/`GradientSpread` + `GradientBrush` base (premultiplied, verified math, per-cell no-LUT) + `Linear`/`Radial`/`ConicGradientBrush` + `Brushes` cache; gradient `FillRectangle`; single-line `DrawText`. | **Done** |
| **3** | `StrokeAccumulator` (per-dir MAX, record-id-per-call) + `BoxGlyphs` ladder; `Pen` + `Pens` + the six stroke enums (no `BorderPen`); `DrawLine`/`DrawBox`/`DrawRectangle`; flush + text-beats-decoration eviction. | **Done** |
| **4** | `BrailleGlyphs`/`BrailleRaster` + `BlockGlyphs`; `IChart`/`BarChart`/`Sparkline`/`ScatterChart`/`LineChart` + curve interpolation; axes/ticks/labels; multi-series line charts (single-surface — `ToLayers` cut, see §6). | **Done** |
| **5** | `Cursorial.Animation` (mechanism) → Color lerp in Core → `BrushInterpolator` + composite-param animation in Drawing. | **Done** (5a + 5b + 5c) |
| **6** | Brush-aware text + images in Drawing (bridge): 6a `DrawFormattedText` + per-run `BrushedStyle`; 6b `SceneCompositor` fragment-passthrough + `DrawContent` + per-protocol clip; A inline wrap-invariant sampling. | **COMPLETE** — 6a + per-run (B) + 6b.1 images + 6b.2 clip (Sixel pixel-crop / Kitty source-rect / iTerm2 suppress) + Kitty & iTerm2 no-distortion aspect scaling (adversarially reviewed) + A inline 1-D wrap-invariant sampling, all done (see §8) |

Phases 0–4 are the v1 spine (all **Done**); **Phase 5 (animation) is complete**; Phase 6 (laid-out brush text)
remains gated. The only Core/Rendering public-surface additions beyond `Style.Transparent` are additive: 5b's
`Color.Lerp` helper (see §7).

---

## 11. Known deferrals / hardening (carry forward)

**Hardened (deferred-items program):**
- **Wide glyph at the composite union's *right* edge** — a `WideLeft` source landing at the union edge
  left `CompositeCell` writing the continuation one column past the reset range and the `MarkDirty`
  union, leaving a stale `WideContinuation` a `RestrictToDirtyRegions` renderer never revisits (a
  dirty-region hole, not just a visual glitch). *Fixed:* `CompositeCell` degrades a `WideLeft` to a
  blank single cell when `column + 1 >= unionColEnd` (the trick `CellBuffer.Set` uses at the buffer edge).
- The adversarial-review P2 guardrails are closed: the radial focal point projects back onto the unit
  ellipse; `CompositeParameters` normalizes `null` vs explicit `BlendingModes.Default`; `Scene.Create`
  validates its size against the `ushort` `Rect` cap.
- The scroll-false-positive probe (a sliding scene over patterned rows must not trip `FrameRenderer`'s
  scroll detection) is in the suite.

**Hardened (UI P2.5 batch, 2026-06-11): push-stack full coverage.** The §12 intra-scene clip/translate
stack now applies to *every* `DrawingContext` draw path (formatted text, content, deferred strokes,
chart braille, shadows, titled boxes — see the reworked §12 bullet). Lower-layer changes made for it
(per the invariant-7 amendment — Rendering accepts first-class improvements):
- **`CellBufferView.WithOrigin(originColumn, originRow)`** (`Cursorial.Rendering`) — re-bases a view's
  local origin independently of its clip window (the origin may be negative: content scrolled
  above/left of a viewport). All coordinate checks generalized from `[0, Columns)` to the window's
  local range; every pre-existing view (origin == window start) behaves identically. This is the
  primitive that lets `FormattedText.Paint` / `IContent.Paint` — whose `Rect` bounds cannot express a
  negative origin — render scrolled, clipped content unchanged.
- **`FragmentDictionary` origin fix** (`Cursorial.Rendering`) — the view-local translation used by
  `ContainsKey`/`TryGetValue`/`Keys` previously translated by the view's *local* bounds (always
  `(0,0)` — a latent no-op that broke fragment lookups through any offset sub-view); it now carries
  the view's origin as signed ints. Full-buffer views are unaffected.
- `DrawingContext.IsVisible(column, row)` (new, public) — the push-aware visibility pre-test the chart
  painters now use in place of raw scene-bounds guards.

**Hardened (UI P2.5 batch, stage ②): pool + observability.** Two further Drawing-side changes for the
UI layer (invariant-7 amendment), landed with the UI `RenderContext` simplification (that type is now
a thin veneer: one pushed translate scope per element render on the §12 stack — no UI-side coordinate
arithmetic remains):
- **`Scene.RasterVersion` is public read-only** (was internal). It always had two consumers — the
  compositor's content-change detection and test assertions of "this frame re-emitted from the cache"
  — and the second forced an `InternalsVisibleTo("Cursorial.UI.Tests")` into this project, which is
  now removed. The counter still bumps only inside `Scene.Draw` on an actual re-raster; there is no
  public setter or bump path.
- **`ScenePool` is size-bucketed with an LRU retention cap** — see §3.4 for the design (exact-dimension
  buckets, `MaxRetainedBuffers` default 32, least-recently-used-bucket eviction, steady-state re-rent
  allocates only the `Scene` wrapper — covered by an allocation assertion in the suite). This closes
  the "single free list, resize-on-rent" deferral recorded at Phase 1. `Rent` now also validates its
  dimensions against the `ushort` `Rect` cap, matching `Scene.Create`.

**Hardened (UI P2.5 review pass, 2026-06-11):** three review findings applied on top of the batch:
- **Transformed fills pre-clamp to the clip.** `FillRectangle`/`FillOpaque` under an active push used
  to iterate the full requested region with a per-cell map-and-reject — O(region area) for an
  oversized rect (`Rect` permits 65535×65535 ≈ 4.3G cells) where the unpushed paths clamp to the
  surface. Both transformed paths now pre-intersect the iteration range with the clip mapped back
  into local space (O(visible)); a perf-guard test rides the push-stack suite.
- **`ScenePool` sweeps empty bucket metadata.** Empty buckets are still retained for the zero-alloc
  steady-state return, but once the bucket table exceeds 4× `MaxRetainedBuffers`, eviction sweeps the
  empties — unbounded size churn (an animated resize, one cell per frame for hours) can no longer
  accrete a `Bucket` + `Stack` entry per distinct size forever. `RetainedBufferCount` is now public
  (pool-health observability for consumers configuring a non-default cap).
- **`DrawText` return-value contract documented for the transformed path**: it returns the columns
  *advanced* in local coordinates — under a push, clusters the clip suppresses still advance (the
  full local run width); with no push it stops at the surface edge and returns the clamped width.
  (Since the §13 line-break batch the return is a `Size` — the per-line advance keeps exactly this
  contract; the `Size` is widest-line advance × line count.)

**Residual limitations (push-stack fragments — documented, deliberate):**
- A fragment whose **anchor** (its translated bounds origin) maps outside the active clip — or off the
  scene — is dropped whole rather than partially shown (registration is anchor-keyed; the
  "anchored-above-the-clip, lower-half-visible" case only the compositor can express stays out of
  reach at draw time).
- A **cached** fragment a `FragmentContent` reuses across re-rasters is not re-cropped when only the
  clip changed (the content's cache sees "fragment present + size unchanged" and skips re-creation).
  For a *moving* viewport over protocol images, use the compositor-level `CompositeParameters.Clip`,
  which re-crops from the uncropped fragment every frame.

**Still carried forward (out of scope for this layer):**
- Box vs image/sized-text **fragment** overdraw: v1 scenes are **cell-only**; fragments stay on the
  main buffer and emit after the cell pass. Offscreen fragment compositing is deferred.

(Two former carried-forward items are now **resolved**: *"`FillRectangle` cannot occlude lower-layer glyphs"*
by `DrawingContext.FillOpaque`, and *"blended junction color at flush"* by the opt-in
`JunctionMode.Blend` — `StrokeAccumulator` tracks the records that cross a cell and the flush averages their
colors (premultiplied) instead of last-writer-wins. Both are covered in §12.)

---

## 12. UI drawing primitives (post-Phase-6)

A batch of authoring capabilities a `Cursorial.UI` layer needs, designed via a multi-agent design +
adversarial-critique pass and landed individually (each commit complete + tested). All hold the §9
brush-blind invariant (no `IBrush` enters `Cursorial.Rendering`) and the compositing invariant.

- **Ordered (Bayer) dither** (`Cursorial.Core`) — `StyleQuantizer.QuantizeDithered(style, col, row)` +
  `FrameRendererOptions.OrderedDither`; see §6 note. Opt-in; disables scroll detection.
- **`ImageBrush` / `TileBrush`** (`Cursorial.Drawing/Brush`) — fill shapes / text with a decoded RGBA image
  (`Fill`/`None`/`Uniform`/`UniformToFill`, cell-aspect-corrected) or a repeating tile
  (`Tile`/`FlipX`/`FlipY`/`FlipXY`/`None`). Bilinear reuses `Color.Lerp` premultiplied sRGB. `FromPng`
  factories. New `IBrush` implementations — no `DrawingContext` API change.
- **Intra-scene clip + translate stack** (`DrawingContext.PushClip`/`PushTranslate`/`Push` → nesting
  `DrawingStateScope`; `CurrentClip`/`CurrentTranslate`). Honored by **every** draw path, incl.
  **negative** translate (scrolled content); a wide glyph at the clip's right edge degrades to blank.
  Coverage, by mechanism:
  - **Immediate per-cell paths** (`Set`/`FillRectangle`/`FillOpaque`/`DrawText`) — translate + clip at
    write time; brushes sample in **local** coordinates (gradients travel with the content). The
    original v1 surface.
  - **Deferred `Pen` strokes (box) + chart/diagonal braille** — the ambient state is captured at
    **record** time: stroke arms / braille dots are translated into scene coordinates and clipped as
    they deposit, so junctions form in final scene coords (strokes of one figure recorded under
    *different* translates still merge where they actually cross), and a clipped line runs to the
    viewport edge with its arm intact. Record sampling bounds are signed (`SampleBounds`), normalized
    back to the `IBrush.ColorAt` bounds-relative contract at flush — local-frame sampling equivalence
    holds, so a translated stroke's gradient is byte-identical to the untranslated one. Explicit
    `BeginFigure(bounds)` bounds are taken in current-local coordinates. The flush pass itself never
    remaps (`FlushDeferred` clears the stack first, by design).
  - **Shadows + titled boxes / panels** — translate as units; the painted band is bounded by the clip.
  - **Formatted text + `DrawContent`** — painted through a clip-windowed, origin-re-based
    `CellBufferView` (`View(clip).WithOrigin(dx, dy)` — see the lower-layer note below), so the
    painter and the brush resolver keep working in local coordinates while every cell write is
    translated + clipped (negative translate = the scrolled-document case). Fragments mirror the
    compositor's clip rules **at draw time**: a body straddling the clip is cropped via
    `IBufferFragment.Clip` or suppressed when the protocol can't crop. Two residuals (see §11).

  Grouped opacity stays at composite granularity (`CompositeParameters.Opacity`, the §11/§1 "free via
  nesting" mechanism).

  > *History:* as landed in the §12 batch (v1), only the per-cell write paths honored the stack —
  > formatted text / content / deferred strokes / chart braille / shadows / titled-box outlines had to
  > be drawn in absolute coordinates or isolated in a sub-scene. That scope gotcha was closed on
  > **2026-06-11** by the UI P2.5 batch (full-coverage rework above); the warning is preserved here
  > only as history.
- **`FillOpaque`** (occlude) — space-bearing fill cells that *hide* lower-layer glyphs (panels, modals),
  vs background-only `FillRectangle`. A bordered opaque panel = `FillOpaque` + `DrawBox(overwrite: true)`
  (an overwriting stroke over an opaque fill keeps the fill background under the glyph). Alpha-preserving
  raw write with wide-orphan cleanup.
- **Titled boxes / panels** (`DrawTitledBox` / `DrawPanel` + `PanelTitle` / `TitlePosition`) — a one-call
  group box with a label on the top edge. All four edges deposit under **one** stroke record (like
  `DrawBox`), so corners are JunctionMode-independent and the gradient samples the full rect; the title
  is grapheme-clipped to the interior between the corners; too-narrow → plain box.
- **Drop / inner shadows** (`DrawDropShadow` / `DrawInnerShadow` + `ShadowGeometry` / `ShadowEdges`) — drop
  = deferred-translucent background outside the element (compositor darkens the target); inner = draw-time
  composite-and-store-opaque against the cell's own fill (preserves the glyph). Linear falloff, per-edge.
- **Brush-aware FIGlet headlines** — a `.Figlet` block in a formatted-text document painted with a brush
  samples it **per rendered cell** (gradient across the big glyphs), via a `StyleDeltaTemplate` handed to the face
  default-interface-method on `IGlyphFont` that `FigletFont` overrides. OSC 66 sized text stays one solid
  color (protocol limit).
- **`JunctionMode.Blend`** — an opt-in pen junction mode: where two strokes from different records cross,
  the junction glyph still forms (arms max-union, as `Merge`) but the cell's color is the premultiplied
  **average** of the crossing strokes' colors instead of the last writer's. `StrokeAccumulator` tracks the
  crossing records per cell (sparse) and the flush averages; also shadows now blend the foreground (not just
  the background) so a glyph a drop/inner shadow falls on dims, and the drop shadow uses an offset-displaced
  silhouette + radius fringe (offset trims the lit corners; the fringe spills only into a casting corner).

> **Adversarial review (Phases 0–2):** passed — foundations (the compositing invariant, premultiplied
> gradient math, transparency model, one-way dependency) independently confirmed correct. Two P0
> silent-failure bugs fixed (compositor scene-identity miss; stored-backdrop out-of-bounds) plus
> P1s (rect `Fill` transparent-clear consistency; conic `spread` dropped as a no-op; `ScenePool`
> double-dispose idempotency; `CompositeParameters.WithMode`) — all with regression tests. The
> low-alpha premultiplied precision test (α 1–4) is now in the suite.

---

## 13. Line breaks across the text tier (2026-06-11, UI P2.6 fixes batch)

Until this batch every plain-text entry point treated its input as a single line, and a stray
control character became a width-1 junk cell. This section pins the line-break and control-character
contract per tier; the changes landed together (user-pinned decisions, restated normatively here).

### 13.1 `DrawingContext.DrawText` — multi-line (both overloads)

- **Line breaks**: `\r\n`, `\n`, and `\r` are all line breaks (one rule, three forms — a lone `\r`
  is a break, never an overstrike). Each subsequent line continues at the **original start column**,
  one row down. Empty lines consume a row; a trailing newline yields a final empty line that counts.
- **Clip/translate**: honored per line through the push stack (each cell write maps and clips
  exactly as v1 single-line text did).
- **Brush extent**: the brush samples against the **full multi-line extent** — widest sanitized
  line width × line count, anchored at the call's (column, row) — so a gradient flows down the
  lines of one call instead of restarting per line.
- **Return type** changed `int` → `Size` (**breaking-but-cleared**; every caller swept repo-wide):
  the text's bounding box — widest line's column advance × line count. Per-line advance keeps the
  v1 §11 contract: the full local width under a push (clipped clusters still advance); clamped at
  the surface's right edge — and 0 for an off-surface row — without one.
- **Negative starts never throw** (P2.6 review fix — the multi-line rewrite briefly built the
  brush bounds before the v1 row guard, turning a graceful no-op into a render-pass crash when
  centering math went negative). Coordinates are signed end-to-end. With no push: rows off the
  surface on **either** side (negative or past the bottom) draw nothing and advance 0; clusters
  that start left of column 0 are skipped (not painted) while the run still advances through them
  — unlike the right edge, which stops the line (so the advance counts local columns from the
  possibly-negative start). Under a push, negative local coordinates flow through the
  translate/clip map as v1 did for non-text draws. Brush sampling rides the internal signed
  `SampleBounds` carrier (the deferred-stroke one): a negative anchor shifts both the rect and the
  sample point to a zero origin — contract-equivalent under bounds-relative sampling — and an
  extent past the ushort `Rect` cap (65,535 lines/columns) clamps defensively (the gradient
  parameter compresses) instead of throwing.
- **Sanitization**: `\t` is substituted with **one space** + a DEBUG diagnostic; all other C0/C1
  controls (including DEL and the C1 range U+0080–U+009F) are **skipped** (zero columns) + a DEBUG
  diagnostic. Sanitization is width-coherent: the measured brush extent applies the same rules.
- **Diagnostics channel**: `DrawingDiagnostics` (+ `DrawingDiagnosticKind`/`DrawingDiagnosticEvent`)
  mirrors `Cursorial.UI`'s `LayoutDiagnostics` shape — a static event raised through a
  `[Conditional("DEBUG")]` emit, so the behavior (substitute/skip) is identical in release while
  the channel compiles away.

### 13.2 Single-line slots sanitize to the first line

`PanelTitle` text in `DrawTitledBox` / `DrawPanel` cuts at the first `\r` or `\n` before
truncation/gap math (+ `MultiLineTextInSingleLineSlot` DEBUG diagnostic). An empty first line
degrades to a plain box, same as an empty title. No other Drawing-owned single-line slot exists
today (window/OSC titles are Core's, out of scope).

### 13.3 The per-tier behavior table (audited 2026-06-11)

| Tier — entry point | `\r\n` / `\n` / `\r` | `\t` | Other C0/C1 | Status |
|---|---|---|---|---|
| Core — `AnsiTextWrap.Wrap` | Hard breaks (all three forms), re-emitted as `WrapOptions.NewLine` | Passes through verbatim, counted zero-width; also a wrap **break-opportunity** (like space — `IsWrapWhitespace`) and trimmed when trailing under `TrimTrailingSpaces` | Passes through verbatim, zero-width (ANSI escapes are recognized pass-through tokens) | Pre-existing; verified |
| Core — `TextSizingWriter.Write/WriteSplit` | Raw passthrough into the OSC 66 payload — caller sanitizes; control bytes on the wire are terminal-defined | same | same | Pre-existing; recorded (callers are single-line by usage) |
| Rendering — `CellBuffer.Write` / `CellBufferView.Write` / `ICellSurface.Write` | **Stops at the first C0/C1 control** and returns columns written (single-row by contract; split lines yourself or use Drawing's `DrawText`) | stop | stop | **Changed this batch** (was: width-1 junk cells) |
| Rendering — `CellBuffer.Set` / `CellBufferView.Set` | Single-cluster raw primitive: a control cluster is stored as given (junk cell) — callers own sanitization | as given | as given | Unchanged; recorded |
| Rendering — `RichTextBuilder` / `TextMarkup` / `TextFormatter` (`FormattedText`) | Hard breaks are **structural**: `LineBreak` inlines (`.LineBreak()`, `[br/]`) and paragraph boundaries. A literal `\n` in run text is **not** a break — the formatter deliberately treats it as a word character (wrap whitespace excludes `\r`/`\n`; markup preserves source whitespace) and it reaches `Set` as a junk cell | Expanded to `TabWidth` spaces (a `SpaceAtom` — wrappable like a space) | junk cell | Pre-existing; verified + recorded (hardening candidate if it bites; tab cell corrected post-audit — the formatter expands, it does not store) |
| Rendering — FIGlet (`FigletFont.Measure/Paint`) | Single-line; any codepoint without a glyph — including `\n` — falls back to the **space glyph** | space glyph | space glyph | Pre-existing; recorded |
| Rendering — `ScaledText` (OSC 66 content) | Text passes unsanitized to the protocol fragment or the fallback `IGlyphFont`; single-line by usage | as above | as above | Pre-existing; recorded |
| Drawing — `DrawingContext.DrawText` (Color + IBrush) | **Multi-line** (§13.1) | One space + DEBUG diagnostic | Skipped + DEBUG diagnostic | **Changed this batch** |
| Drawing — `PanelTitle` (`DrawTitledBox`/`DrawPanel`) | **First line only** (§13.2); the title then rides `DrawText`'s tab/control rules | space | skipped | **Changed this batch** |
| Drawing — charts (`Axes` labels, `BarChart` value/category labels) | Labels ride `DrawText`, so an embedded break now continues one row down at the start column; labels are single-line by convention and numeric formatting never produces breaks | space | skipped | Inherited; recorded |
| UI — `RenderContext.DrawText` | Thin veneer over Drawing's: multi-line + `Size` return (element-local) | space | skipped | **Changed this batch** |
| UI — text-bearing leaves (demo labels today, `TextBlock` at S8) | Single-call `DrawText` leaves inherit multi-line; document-shaped text rides `FormattedText` (structural breaks) | — | — | Recorded |
