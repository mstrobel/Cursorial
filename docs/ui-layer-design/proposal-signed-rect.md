# Proposal: Signed `Rect` — Retiring the Origin Restriction (and `LayoutRect`)

**Status: PROPOSED (2026-08-05).** Produced during the opacity-group compositing work, after the
third distinct workaround for the same restriction landed in one session. No code written. §7 lists
the decisions that need an owner; §8 argues for sequencing it *after* group opacity.

---

## 0. The problem, and the thesis

`Rect` forbids negative coordinates. Its origin accessors validate, and its constructor throws:
*"Rectangle anchor coordinates cannot be negative."*

But negative coordinates are ordinary in this system, not exceptional: content scrolled above or left
of its viewport, a window dragged off the top-left, an element arranged at a negative margin
(matrix LD19), a formatted-text painter working in content coordinates. The restriction does not
prevent those situations — it merely prevents *describing* them, so every layer that meets one
invents its own way around.

**The thesis: `Rect` conflates two roles.** A *region of a surface* is non-negative by nature — you
cannot index a buffer at −1. A *geometric rectangle* is signed by nature. One type is currently
asked to be both, and the non-negative role won, so the signed role is served by four separate
compensations.

## 1. The four compensations

1. **`LayoutRect`** (`Cursorial.UI`, 128 lines) — a whole parallel type whose only material
   difference is that its origin may be negative.
2. **`CellBufferView.WithOrigin`** and its dual `_windowColumn` / `OffsetColumn` fields with signed
   `LocalColumnStart` / `LocalColumnEnd`. A re-based view exists because "this view's content origin
   is at (−5, −3)" cannot be a `Rect`. Its own doc concedes the cost: *"`Bounds` (anchored at
   `(0,0)`) is **not** meaningful on a re-based view."*
3. **Ad-hoc clamping in `SceneCompositor.PassThroughFragments`**, which states the cause outright:
   *"Clamp the origin to >= 0 and shrink the extent accordingly (Rect can't carry a negative
   origin): a negative composite offset (scrolled content / a window dragged off the top-left) makes
   anchor+offset < 0."*
4. **Three fixes in a single session (2026-08-05)** — a negative-tolerant `DrawGlyphText` primitive,
   clamping in `TintCells`, and leading-edge trimming in `CellBuffer.Blit` — all downstream of a
   live crash: `Rect Column must be ≥0, was -4`, from an editor scrolled horizontally.

Item 4 is the one that should decide it. The restriction did not prevent an invalid state; it
converted a legitimate geometry into an exception, at runtime, in front of a user.

## 2. The diagnosis: `LayoutRect` *is* `Rect` minus one validation

Both are `readonly record struct`. Both are `int`-backed. Both validate **extents** as non-negative.
They differ in exactly one respect: `LayoutRect`'s constructor documents *"a (possibly negative)
top-left corner"* and does not validate the origin.

There is already an `implicit operator LayoutRect(Rect)` — the widening direction is total and safe.
There is no reverse operator, because narrowing needs validation.

So this is not a representation change or a new type. **It is the removal of a validation**, plus
the deletion of the type that exists to work around it.

Worth noting because it removes an objection before it is raised: `Rect` was `ushort`-backed
historically, when signing it would have meant halving the range or widening the field. It is
`int`-backed now, so the negative range is already present and simply gated off. (`Scene.Create`
still carries a stale comment asserting *"Rect, whose coordinates are ushort"* — see §7.4.)

## 3. Proposal

**Relax `Rect`'s origin validation. Keep extents non-negative. Retire `LayoutRect`.**

- `Column` / `Row` accept any `int`; `Columns` / `Rows` keep their current validation.
- `LayoutRect` becomes redundant. Retire it in whichever way §7.1 decides.
- The clamping in compensations 2–4 becomes deletable — carefully, since some of those sites also
  implement genuine *clipping*, which must survive; only the negativity workaround goes.

## 4. Where non-negativity actually belongs

The safety argument for the current design does not survive inspection: callers clamp *before*
constructing, so the buffer-indexing invariant is already enforced by convention at the boundary,
not by the type. What the constructor throw actually buys is a crash when geometry legitimately goes
negative.

Move the constraint to where it means something — the indexing APIs, which mostly enforce it
already: `CellBufferView.Contains` gates the indexer, `Set` returns 0 for out-of-range, `Blit` and
the compositor clip. That is validation *at use*, where an out-of-range region is genuinely a bug,
rather than *at construction*, where it is often just arithmetic in progress.

## 5. Migration

Blast radius, measured: `Rect` appears in **95 files** — Rendering 16, Drawing 25, UI 43, Bars 5,
DataViews 6. But relaxing a validation is source-compatible: every existing construction still
compiles and still behaves identically, because no current caller passes a negative origin (they
cannot — it throws today). **The change cannot break a working call site.** What it can break is
code *relying on the throw*, which §7.3 makes the first task.

`LayoutRect`'s implicit conversion means UI code can migrate incrementally rather than in one edit.

## 6. What we get back

- One type instead of two, with the layering fixed: `Rect` lives in `Cursorial.Rendering`, which
  `Cursorial.UI` can see — so the rendering layer finally has a signed rectangle available, which is
  the specific thing it lacks today and improvises around.
- `WithOrigin`'s dual origin/window machinery loses its *reason*, though the re-basing itself likely
  survives as a convenience — see §7.2.
- The clamping sites collapse into ordinary intersection.
- Crashes like the `-4` become clipping, which is what every one of those call sites wanted.

## 7. Decisions needed

1. **How to retire `LayoutRect`** — delete outright and rewrite its 43-file UI footprint, or leave it
   as a deprecated alias of `Rect` and let call sites drift over? The alias is cheaper and keeps the
   diff reviewable; it also leaves two names for one concept for a while.
2. **Does `WithOrigin` survive?** Its justification disappears, but re-basing a view is a genuine
   convenience for painters working in content coordinates. Keeping it is fine — the question is
   whether the dual `_windowColumn`/`OffsetColumn` representation simplifies once a signed rect can
   express the offset directly.
3. **Audit for code relying on the throw.** The first implementation task, before any relaxation:
   find every site that treats "`Rect` construction threw" as a guard rather than a bug. Expected to
   be empty or near-empty, but it must be *established*, not assumed.
4. **Restate the bounds.** `MaxDimension`'s doc currently reads *"the `Rect` is `int`-backed, so this
   is the full non-negative range"*, with a caveat that `edge + extent` can overflow for a hand-built
   rect near the cap. Signed origins make that overflow analysis two-sided. Also fix the stale
   `ushort` claim in `Scene.Create` and re-justify (or relax) that cap on its own merits — a scene
   wider than 65,535 cells is still absurd, but the stated reason is defunct.
5. **Does `Rect.Empty` / `default` change meaning?** It should not — `default` remains origin `(0,0)`
   with zero extent — but negative-origin empties now exist and any code branching on `IsEmpty`
   versus a position check should be reviewed.

## 8. Sequencing

**After group opacity, not during.** `Rect` is threaded through `Scene`, `SceneCompositor`,
`CompositeParameters` and `PassThroughFragments` — precisely the code the opacity-group work is
changing. Landing a coordinate-domain change into the same region would make both harder to review
and would blur which change caused any regression.

The compensations are stable and understood; they are not costing anything while they wait. The
right moment is with the compositor quiet.
