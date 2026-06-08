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

`DrawingContext` (Phase 1): scalar `Set(col,row,grapheme,style)` and `FillRectangle(in Rect,
IBrush)` (plus a `Color` overload wrapping `SolidColorBrush`). `FillRectangle` writes background-only
cells via the **raw indexer** so a translucent source
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
  as an ellipse); cell-pixel aspect correction (true on-screen circles via
  `WindowCapabilities.CellPixelWidth/Height`) is **deferred** as a refinement.
- **Premultiplied-alpha interpolation** (sRGB channels): premultiply each stop's RGB by alpha, lerp,
  un-premultiply to a **straight** `Color`. This flows through the existing straight-alpha
  `Color.Composite` correctly and removes fade-to-transparent fringing (so `Color.Transparent` =
  `0x00000000` stays fine). Brush opacity folds uniformly into every sampled color (solid **and**
  gradient — fixing Consolonia's solid-vs-gradient asymmetry). Keep sampling math in floats; quantize
  only the final straight `Color`. A low-alpha (α 1–4) precision test guards hue preservation.
- Banding on low-color terminals is accepted for v1 (nearest palette per cell); ordered dither is
  deferred.
- **Convenience (post-review):** each gradient brush has a **two-color** ctor
  (`new LinearGradientBrush(start, end)` / `RadialGradientBrush(centerColor, edgeColor)` /
  `ConicGradientBrush(start, end)`) delegating to the `IReadOnlyList<GradientStop>` ctor, restoring
  parity with `SolidColorBrush`'s implicit-conversion + `Brushes`-cache ergonomics. An implicit
  `(double offset, Color)` → `GradientStop` conversion lets multi-stop lists drop the
  `new GradientStop(...)` noise (`[(0.0, Colors.Red), (1.0, Colors.Blue)]`). Gradient geometry is
  validated **finite** (NaN/Inf throws, mirroring the opacity guard) — but **not** range-clamped, since
  out-of-`[0,1]` points are legal (animation).

Phase 2 also delivers `DrawingContext.DrawText` — **single-line, unlaid-out** brush text: walk
grapheme clusters, sample fg/bg per cell across the run, build a scalar `Style`, `Set`.

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
oracle-pinned; bars non-negative bottom/left-anchored — signed bars deferred). 4b = `BrailleGlyphs`
(dot→bit `dy<3?dx*3+dy:6+dx`, oracle-pinned) + `BrailleRaster` (OR-merge, deferred brush, last-writer
color) + diagonal `DrawLine` (Bresenham at 2×4 sub-cell res; weight/corners/dash/cap N/A for diagonals)
+ the `EmitDecorationCell` seam shared by box & braille; `Scene.Draw` flush renamed `FlushDeferred`,
order braille-then-box (data over axes). 4c = `PointD`/`CurveInterpolation`/`MarkerStyle`,
`Curves` (Linear / centripetal Catmull-Rom α=0.5 / Fritsch-Carlson monotone-cubic — **numerically
oracle-pinned**), `PlotProjector` (Y-flip isolated), `ScatterChart` (markers / braille dots), `LineChart`
(braille curve + optional markers), `ctx.LineChart`/`ctx.ScatterChart` sugar; charts plot via the
internal braille seam (`AddBrailleRecord` + `PlotBrailleSegment`/`PlotBrailleDot`). **4a + 4b + 4c:
implemented + tested.** **Deferred:** `LineChart.FillArea` (block fill under the curve — needs the
line-over-fill resolution: deferred fill or braille `Overwrite`); NaN-gap-as-break (4c skips non-finite
points for now). **4d.1 (axes): implemented + tested.** `AxisRange.Nice()` (Heckbert nice-number ticks),
`Axis` config, `Axes` (Y-left + X-bottom box-strokes meeting at `└`, numeric tick labels, optional
gridlines → junctions; returns the inset `PlotLayout` { Plot, nice X, nice Y } so axis + data align).
4d.2 = `ChartSeries` + `MultiLineChart`: single-surface `Render` — each series keeps its own color; where two
series cross a cell their dots OR-merge into one glyph painted in the later series' color (a terminal cell has
one foreground). A per-series-scene `ToLayers` path was prototyped and **cut** (re-add it with `FillArea`,
below). Empirical finding (probe, code-cited): with opaque single-width braille, `SceneCompositor.CompositeCell`'s
single-width-glyph branch *replaces* the lower glyph and its color with the top layer's via a raw-indexer write
that **bypasses the blend stack**, so crossings resolve to the top series' color **identically to `Render`** — no
color benefit, and `ToLayers` is in fact *worse* at crossings (it drops the lower series' dots). Blending/alpha
can't rescue it: that branch composites the top foreground only against the merged *background*, never the lower
series' *foreground* — alpha just darkens the top color toward the backdrop. Per-layer compositing earns its keep
only for translucent **filled** regions: backgrounds *do* alpha-blend across layers (the same method's
background-composite step, RGB-on-RGB), so red-fill ∩ blue-fill → genuine purple. That is the real future home for `ToLayers`, gated on
`FillArea`. **Phase 4 (a–d) complete + tested** (186 `Cursorial.Drawing.Tests`; curve + glyph tables
oracle-pinned; axes/markers/gridlines share the 2×4 projector so everything aligns). **Remaining enhancements
(deferred, not blocking):** `LineChart.FillArea` (+ its per-layer `ToLayers` compositing for translucent
area/heatmap overlaps), NaN-gap-as-break, signed bars, bar-chart category axes.

---

## 7. Animation (Phase 5 — designed, gated)

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

## 8. FormattedText brush story (Phase 6 — designed, gated)

Additive, not a rename or move. `FormattedText`/`RichText`/`TextFormatter`/`TextMarkup` **stay in
`Cursorial.Rendering`** (their color coupling is to Core `Style`, not `Brush`). Phase 2's `DrawText`
covers single-line brush text. A laid-out brush-aware formatter (continuous gradients across wrapped,
aligned text) reuses `TextFormatter`'s color-free measurement and a parallel **`BrushedStyle`** in
Drawing (`IBrush` never goes inside `Style`). Markup keeps returning `Color` (`[fg=…]` → implicit
`Color → SolidColorBrush`); gradient markup grammar is deferred.

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
- **Text brush coordinate space:** physical paint bounds (verbatim run copy); logical-span anchoring
  is a deferred opt-in.
- **Markup gradients:** deferred (solid-only markup in v1).
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
| **3** | `StrokeAccumulator` (per-dir MAX, record-id-per-call) + `BoxGlyphs` ladder; `Pen` + `Pens` + the six stroke enums (no `BorderPen`); `DrawLine`/`DrawBox`/`DrawRectangle`; flush + text-beats-decoration eviction. | Finalized (impl) |
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
