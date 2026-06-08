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

## 6. Shared accumulator + charts (Phase 4 — designed)

One generalized mechanism: a per-cell `uint` mask accumulated during a scene draw pass, resolved to a
glyph on flush, with pluggable layouts:

| Layout | Mask | Merge | Used by |
|---|---|---|---|
| `BoxEdgeLayout` | 2 bits/dir × 4 dirs | MAX/dir | pen/junctions (§5) |
| `BrailleDotLayout` | 8 dots (`U+2800 \| mask`) | OR | scatter / line / curve |
| `BlockFractionLayout(axis)` | fill level 0–8 | MAX | bars |

The accumulator stores the source `IBrush` per cell and samples (`ColorAt`) at flush (so a gradient
brush resolves against the element bounds; sample-before-quantize preserved). `BlockFractionLayout`
carries an axis, so it's instance-keyed (not a singleton).

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
- **Multi-series Braille color:** **per-series scenes** by default, last-writer-wins opt-in.
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
