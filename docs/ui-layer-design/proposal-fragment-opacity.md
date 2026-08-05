# Proposal: Fragment Opacity — Closing the Group-Opacity Hole for Terminal-Drawn Content

**Status: PROPOSED (2026-08-05).** Found while designing `RenderOptions.BlendingMode`, during the
group-opacity compositing work. No code written. §6 lists the decisions that need an owner.

---

## 0. The defect

Graphics-protocol fragments — images and OSC 66 sized text — are **positioned and clipped, but never
dimmed or blended**. An image inside an `Opacity = 0.5` window renders fully opaque while everything
around it dims.

This is not a bug at one call site. `SceneCompositor.PassThroughFragments` reads exactly three
things from `CompositeParameters`:

| parameter | fragment handling |
|---|---|
| `OffsetColumn` / `OffsetRow` | reimplemented by hand (`tc = anchor.Column + p.OffsetColumn`) |
| `Clip` | reimplemented by hand, delegating to `IBufferFragment.Clip` |
| `Opacity` | **absent** |
| `Mode` | **absent** |

It re-implements precisely the parameters expressible without pixel access and omits precisely the
two that need pixels. `Cursorial.Rendering/Fragments/` contains no mention of opacity or alpha at all.

**The group-opacity work inherits this hole.** Its acceptance criterion (a dialog at 95%) will not
catch it unless the dialog shows an image — but a file dialog with a thumbnail preview is exactly
where a user meets it first.

## 1. Why the compositor cannot fix it, and why the element cannot either

A fragment is **not drawn into** the intermediate surface. It is hoisted out and re-anchored on the
next surface up, repeatedly, until it reaches the final target — the code says so: *"the outer pass
collects it from the group surface's **forwarded** fragments — which is the same set, already in
those coordinates."* Cells get `p.Opacity` applied at every level
(`CompositeCell(..., p.Opacity, mode, ...)`); a forwarded fragment skips every one of them.

So it needs the **accumulated** opacity, not any single level's — a fragment three levels deep
bypasses three composites. That is exactly `zone.EffectiveOpacity` (`RenderTree.cs:836`).

Two tempting fixes both fail:

- **Apply it in the compositor.** By the time `PassThroughFragments` runs, the fragment is an opaque
  handle to encoded bytes. The pixels are gone.
- **Apply it at paint time in `IContent`.** `IContent.Paint` is invoked from `DrawingContext`, and
  neither `DrawingContext` nor `RenderContext` knows the effective opacity. Threading it through
  both is new plumbing for a seam that already exists (§2).

## 2. The design: reuse the `Clip` seam

`IBufferFragment` already solves this exact problem for clipping, as a default interface member:

```csharp
IBufferFragment? Clip(in Rect visible) => null;      // IBufferFragment.cs:128
```

`PassThroughFragments` uses it and documents the contract: *"Crop the fragment to the final visible
sub-rect (fragment-local), or suppress if it can't crop."* So the established pattern is:

1. the compositor computes a parameter it cannot apply itself,
2. it asks the **fragment** to produce a derived variant,
3. if the fragment declines (`null`), the compositor acts on a stated policy.

**Opacity should use the identical shape** — a default interface member applied where `Clip` is
applied. Accumulation then falls out for free: forwarding is level-by-level, so each level multiplies
exactly the way cells do. Neither `RenderContext`, `DrawingContext`, nor `IContent.Paint` needs to
know anything.

The seam must offer a **backdrop colour**, because implementors that cannot carry alpha to the
terminal have to flatten against it before encoding.

§4 widens this from an opacity-specific member to a single `Apply(in StyleDeltaTemplate, Color)`
covering opacity, tint and blend together — read it before implementing this section.

## 3. All four implementors can honour it

| fragment | mechanism | needs backdrop? |
|---|---|---|
| `KittyImageFragment` | native — we already emit format 32 (RGBA) and PNG; scale the alpha channel | no, if the terminal blends |
| `ITerm2ImageFragment` | PNG payload, alpha-capable in principle | no, if the terminal blends |
| `SizedTextFragment` | **not pixels** — styled text, so `ScaleSourceAlpha` on its style, the same path every cell takes (`SceneCompositor.cs:516`); OSC 66 emits opaque SGR, so flatten before emission | yes |
| `SixelFragment` | we own the pipeline: it retains `_rgba` (`:114`) after `MedianCutQuantizer.Quantize` → `SixelEncoder.Encode`. Blend toward the backdrop, re-quantize, re-encode | yes |

Two observations that make this cheaper than it looks:

- **`SizedTextFragment` needs no pixel work at all** and is probably the most common fragment in a
  TUI. Its opacity is the existing cell mechanism.
- **`SixelFragment` already retains `_rgba` for re-encoding** (that is what `Clip` uses), so opacity
  costs the same as clipping — a cost already accepted.

## 4. Blending is achievable too — and one seam covers everything

An earlier revision of this document argued that blending was impossible for fragments and that a
`WithBlend` member would be dead API. **That was wrong**, for a reason worth recording so it is not
re-derived: a blend needs *a* backdrop, and the compositor has one. A fragment's footprint covers
target cells, each carrying a background colour. The compositor can supply that, and the fragment
blends its RGBA against it.

**And there is no accuracy caveat**, contrary to a second wrong turn this document took. A terminal
cell *has* exactly one background colour — that is not a lossy sample of something finer, it is the
whole thing. Blending each pixel against the background of the cell it occupies is therefore
**exact**, always. A gradient backdrop is merely a set of per-cell solid colours, and blending
against those is equally exact. There is no pixel-resolution backdrop being approximated because no
such thing exists at this layer.

### Theme-driven icon tinting makes the pipeline mandatory anyway

A planned feature — app authors ship ONE icon set, tinted and luminosity-adjusted per theme —
requires decoding the PNG and processing RGBA regardless. Once that pipeline exists, opacity and
blending are additional transforms over the same buffer rather than new machinery.

### One seam, not three

Opacity, tint, and blend are all "derive a fragment by transforming its RGBA against a backdrop".
Per-channel colour plus a blending mode is exactly what `StyleDeltaTemplate` carries
(`proposal-partial-style.md` §5b, §5e). So the seam is singular:

```csharp
IBufferFragment? Apply(in StyleDeltaTemplate template, ReadOnlySpan<Color> backdropByCell) => null;
```

The backdrop is **per cell over the footprint**, not a single colour — a single `Color` would only
serve a uniform footprint. Pixel → cell is well defined because the fragment already knows its
pixels-per-cell ratio (`KittyImageFragment` computes `pixelsPerColumn` / `pixelsPerRow` when
cropping), and an implementor can fast-path when every cell in the span matches.

This puts the same type on both sides of the fragment boundary — `IContent.Paint` takes a template,
and a fragment applies one. Consequences:

- **`SizedTextFragment` uses the WHOLE template**, attributes included, because it genuinely is
  styled text. Image fragments use the colour channels and ignore the rest.
- **Icon tinting stops being a feature** and becomes a foreground brush in a template.
- The `Clip` seam stays separate — it is geometric, not a pixel transform.

## 5. Failure policy — split it from `Clip`'s

> **Suppress when failing would corrupt other content; ignore when it only degrades itself.**

`Clip` suppresses, and that is right: an uncropped fragment spills outside its clip and damages its
neighbours. An unblended or undimmed fragment is wrong only about itself, and making an image vanish
because an ancestor set `Multiply` is a worse outcome than showing it unblended.

So: `Clip` keeps suppression; opacity and blending fall back to rendering unmodified.

## 6. Decisions needed

1. **Does Kitty actually alpha-blend what we send?** We verified only that we *emit* format 32 /
   PNG. Whether the terminal composites alpha against the cell background is protocol behaviour that
   must be confirmed against a real terminal before relying on it. If it does not, Kitty joins the
   flatten-against-backdrop group — which, given §4, is no longer a meaningful distinction.
1b. ~~**Backdrop resolution.**~~ **RESOLVED — not a limitation.** Cell-granular blending is exact,
   because a cell's background colour is the backdrop in full, not a sample of a finer one. The only
   API consequence is that the backdrop is passed per cell rather than as a single colour.
2. **What backdrop does the compositor supply?** Exact for a uniform backdrop; over a gradient it is
   a cell-resolution approximation beneath a pixel-resolution image. Decide whether to sample per
   footprint cell or take a single representative colour.
3. **Fragment identity.** `object Key => this;` (`:78`) is the tracking identity for ghost removal
   via `_fragmentAnchors`. A derived fragment must preserve the original's `Key`, or a re-opacitied
   image is treated as a new fragment every frame. The `Clip` path already has this exposure —
   whatever it does is the precedent.
4. **Caching.** An animated opacity over a Sixel image re-quantizes per frame. Cache derived
   fragments on `(source, opacity, backdrop)`, in the shape of the `ChartPresenter` compositing
   scratch cache — including its invalidate-on-size-change discipline. Identity (`opacity == 255`)
   must return `this` so the ordinary path costs nothing.

## 7. Relationship to the other proposals

- **The group-opacity work** — this is a hole in its correctness story, not a follow-on feature. It
  should land close behind, or the feature ships with a visible inconsistency.
- **`proposal-partial-style.md` §5e** — `IContent.Paint` taking a `StyleDeltaTemplate` still stands
  on its own (it retires `ScaledText.BrushResolver` and gives `Icon`/`Image` brush support), but it
  is *not* load-bearing for fragment opacity. That was the design before the `Clip` seam was found.
- **`RenderOptions.BlendingMode`** — §4 is why fragments CAN participate, at cell-resolution
  backdrop accuracy, rather than needing a blanket non-participation policy.
