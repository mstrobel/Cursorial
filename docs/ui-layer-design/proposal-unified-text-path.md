# Proposal: A Unified Text Path — One Mechanism, a Plain Fast Path, an Access-Key Option

**Status: PROPOSED (2026-08-05).** Produced during the opacity-group compositing work, from a
maintainer observation that the text-bearing controls have diverged. No code written. §8 lists the
decisions that need an owner.

---

## 0. The problem, and the thesis

Four controls format text, by four mechanisms:

| control | mechanism | sizing / fonts |
|---|---|---|
| `TextBlock` | always `FormattedText`, cached on a 9-part key | builds a `GlyphSource` from `TextElement.Font`/`Sizing` |
| `RichTextPresenter` | `RichText`/markup → `FormattedText` | per-run via `TextRun.Source` |
| `TextPresenter` (TextBox) | bespoke `TextLayout`/`GraphemeLayout` + a face-lane painter | its own `EditingSource` |
| `AccessTextPresenter` | direct `DrawText`; measure is `GraphemeWidth.StringWidth` × 1 row | **none** |

**The thesis:** the divergence is already being paid for — the same capability implemented three
times and missing from the fourth — and the bill grows with every feature that should work
"anywhere". Text sizing and glyph fonts are the current example; they will not be the last.

The reach argument is the decisive one. Set a FIGlet font on a menu item or a button caption today
and `AccessTextPresenter` measures one cell per cluster while painting something else entirely. Not
because anyone decided labels shouldn't support fonts, but because a fourth mechanism existed and
nobody taught it.

## 1. The fast path already exists — in the wrong place

`AccessTextPresenter` *is* the optimized single-line plain-text path: no `FormattedText`, measure by
`StringWidth`, paint by one `DrawText`. It is correct, it is fast, and it is unavailable to the
control that would benefit most, because `TextBlock` always routes through the formatter — paying
for runs, lines and layout to render `"OK"`.

So the work is not "write a fast path". It is: extract the one that exists, give it a defensible
domain, and let every text-bearing control use it.

## 2. The domain predicate already exists too

The fast path is only sound where one cluster occupies one cell and nothing is emitted through a
protocol. That is exactly `GlyphSource.PaintsAsCells`:

```csharp
Font is null or MonospaceFont && Sizing.IsNormal
```

So the boundary is not a heuristic anyone has to invent and maintain:

> **fast path ⟺ `PaintsAsCells` ∧ plain (no markup) ∧ single-line ∧ fits (no wrap, no trim)**

Everything else goes to the formatter, which already handles glyph runs, bands and per-run metrics
from the glyph-runs work.

The value of deriving the predicate from `PaintsAsCells` rather than inventing one: a scaled or
FIGlet run **cannot** silently take the fast path and mis-measure, because it fails the predicate by
construction rather than by someone remembering to check.

## 3. The access key is the only genuine difference — so make it an option

Strip sizing and fonts away and `AccessTextPresenter` differs from a plain label in exactly one
respect: one grapheme cluster is styled differently. Everything else — measure, paint, trimming —
is plain-text behaviour.

Give the shared mechanism an access-key option and that control collapses into a configuration of
the shared path. The payload is:

- **an index** (which cluster), and
- **a style delta** for it.

That delta is precisely a `PartialStyle` (see `proposal-partial-style.md`): today the code writes
`style.WithUnderlineStyle(keyUnderlineStyle).WithUnderlineColor(indicatorBrush.ColorAt(...))` and
optionally replaces the foreground — two or three channels touched, every other channel inherited.
That is a channel-explicit delta written by hand because the vocabulary for it does not exist yet.

**Keep it access-key-specific; do not generalise it into arbitrary styled spans.** A general span
facility on the fast path would drift into reimplementing the formatter badly, and the boundary at
§2 would blur. The access key earns its special case honestly: it is universal in UI chrome, it is
exactly one cluster, and it is already a first-class framework concept (`AccessText`,
`RecognizesAccessKey`, `AccessKeyManager`, `InteractionState.AccessKeyCue`). Anything richer than
one styled cluster is what `RichText` is for.

## 4. The cue is dynamic — so it must be paint-time, not layout-time

`InteractionState.AccessKeyCue` is a state bit toggled at runtime (Alt, plus the
`input.alwaysShowAccessKeyCues` user option). The underline appears and disappears **without the
geometry changing** — the cluster occupies the same cells either way.

This is a constraint, not a detail. If the access key were modelled as a run split in
`FormattedText`, every cue toggle would invalidate the layout. `TextBlock`'s cache key does not
include cue state, so a naive run-splitting implementation would either thrash the cache or render a
stale cue.

> **Rule: the access key affects style only, never geometry.** Layout is computed once, ignorant of
> the cue; the delta is applied at paint to the cluster at the recorded index.

That also makes the fast path's job easy — it is already a `DrawText` loop that can style one
cluster differently, which is what the existing code does.

## 4a. Recognition flags — and why they must not force the slow path

`RecognizesAccessKey` and `RecognizesMarkup` are the same KIND of thing: flags that decide how an
input string is *interpreted* before anything is laid out. Today they have three shapes —
`RecognizesMarkup` and `RecognizesAccessKey` as flags on `ContentPresenter`, `Markup` as a separate
string property on `TextBlock` that "wins over `Text`", and nothing at all on
`AccessTextPresenter`. A unified path makes them uniform, so `RecognizesMarkup` becomes available
anywhere text is shown rather than wherever a presenter happened to grow it.

**The flags simply propagate to the shared engine; controls do no special handling.** A control
holds the properties and forwards them — it does not inspect the string, choose a strategy, or
select a presenter. Interpretation and the fast-vs-formatted decision both live inside the engine,
which is what keeps §8.4 answerable: exactly one place decides, because only one place *can*.

That collapses more than the seam. `ContentRealization` currently picks a presenter by content type
— `AccessText` yields an `AccessTextPresenter`, a string yields a `TextBlock` — which is a routing
decision made outside the engine on the basis of what the string is going to turn into. With flags
propagating, that branch has nothing left to decide.

**The refinement, and it is load-bearing:** the predicate must test the INTERPRETED RESULT, not the
flags. `RecognizesMarkup = true` on a string containing no markup still yields plain text and must
still take the fast path — a cheap delimiter scan decides. The same for `RecognizesAccessKey` on a
string with no marker.

Getting this wrong is the difference between a feature people enable and one they avoid: if setting
`RecognizesMarkup` deoptimized every label whether or not it used markup, the sensible thing would
be to leave it off, which defeats the reach goal this proposal exists to serve. So:

> **fast path ⟺ `PaintsAsCells` ∧ *interpreted-plain* ∧ single-line ∧ fits**

with "interpreted-plain" meaning the front end produced plain text with at most an access-key index
— not that recognition was disabled.

## 5. What the shared seam needs

- plain text, a width budget, wrap/trim/alignment settings, a `GlyphSource`;
- an optional `(clusterIndex, PartialStyle, visibilityPredicate)` for the access key;
- one entry point that decides fast-vs-formatted by §2 and is the *only* place that decision is made.

Controls become configuration: `TextBlock` passes no access key; `AccessTextPresenter` passes one;
`RichTextPresenter` bypasses the plain path entirely (its input is already rich); `TextPresenter`
keeps its editing-specific concerns (caret, selection, scroll) but should share measurement so an
editor and a label never disagree about how wide a string is.

## 6. The equivalence harness — build it first

The hazard is not writing the fast path; it is a fast path that measures *slightly* differently from
the formatter, producing visible jitter exactly when text crosses the threshold — a label that
shifts a cell when it grows a character, or a resize that moves glyphs without changing content.

Two properties, both testable, and worth having before the optimization rather than after (the same
approach that made the compositor's intermediate-surface work safe):

1. **Agreement.** For any input satisfying the §2 predicate, fast and formatted produce identical
   size *and* identical painted cells.
2. **Routing.** Any input failing the predicate actually takes the formatted path — the guard against
   a scaled or FIGlet run silently falling into monospace measurement.

## 7. Known call sites and a live defect

`TextBlock`, `AccessTextPresenter`, `RichTextPresenter`, `TextPresenter`; `ContentPresenter`'s
realization path chooses between them (`RecognizesAccessKey`, the fallback `TextBlock`).

**A live inconsistency the normalization removes by construction:** `AccessTextPresenter` measures
`Text.Text` but arranges `label.Text.Trim()` — untrimmed in `MeasureOverride`, trimmed in
`ArrangeOverride`. A label with leading or trailing whitespace over-reports its desired width. Minor
in practice, but it means the existing fast path should not be treated as a reference implementation
without review.

## 8. Decisions needed

1. **Does `TextPresenter` share the path, or only the measurement?** Full sharing is the tidiest
   story but the editor has real extra concerns (caret geometry, selection tinting, horizontal
   scroll, band-aware hit testing). Sharing *measurement only* may capture most of the value at a
   fraction of the risk.
2. **How far does the presenter collapse go?** If flags propagate and the engine routes (§4a), the
   content-type branch in `ContentRealization` loses its purpose. Does `AccessTextPresenter` disappear
   entirely, leaving one text presenter with flags, or does it remain as a named configuration for
   compatibility?
3. **Fast-path caching.** `TextBlock` caches its `FormattedText` on a 9-part key. Does the fast path
   need a cache at all, or is `StringWidth` cheap enough to recompute? Measure before deciding — a
   cache that is never a hit is worse than none.
4. **Where does the predicate live** so that exactly one place decides fast-vs-formatted? If two
   places can decide, they will eventually disagree.
5. **Does `TextBlock.Markup` survive as a property**, or collapse into `Text` + `RecognizesMarkup`?
   The flag form generalises (§4a) and is what the other presenters would gain; the separate-property
   form is a breaking change to remove. A deprecation that maps `Markup` onto the flag is likely the
   cheap path.

## 8a. The engine can live in `Cursorial.Rendering` — two enums are the only blocker

Placing the shared engine in Rendering rather than UI makes `Core + Rendering` a self-sufficient
text stack. Measured, almost everything it needs is already at or below that line:

| dependency | where it lives today | status |
|---|---|---|
| `FormattedText`, `TextTrimming`, `TextAlignment` | `Cursorial.Rendering.Text` | ✅ already there |
| `GlyphSource` / `PaintsAsCells` | `Cursorial.Rendering.Fonts` | ✅ already there |
| `FrameRenderer` | `Cursorial.Rendering` | ✅ already there |
| text markup (`TextMarkup`, `RichText`, `MarkupColor`) | `Cursorial.Rendering.Text` | ✅ already there |
| `TextSizing`, `TextAttributes`, `Style`, `Color` | `Cursorial.Core/Output` | ✅ below Rendering |
| `PartialStyle` (§3's access-key delta) | proposed for Rendering | ✅ per `proposal-partial-style.md` §5c |
| **`TextStyle`, `TextWeight`** | `Cursorial.UI/Controls/TextElement.cs` | ❌ **the only blocker** |

The `RecognizesMarkup` half of §4a needs no layering work: `TextMarkup.cs` already lives in
`Cursorial.Rendering.Text`. (Do not confuse it with the `Cursorial.Markup` namespace in
`Cursorial.Shared` — that is XAML infrastructure: `XmlnsDefinitionAttribute`,
`ContentPropertyAttribute`, `TypeConverterAttribute`, `ValueSerializerAttribute`,
`DictionaryKeyPropertyAttribute`, `CursorialUri`. Unrelated to text markup.)

**The two stragglers belong in `Cursorial.Output` (Core), not Rendering.** They document themselves
entirely in SGR terms — `TextStyle` as *"SGR 23 — the reset state"* / *"SGR 3 — italic"*,
`TextWeight` as *"SGR 1"* / *"SGR 2"* / *"the shared reset 22 state"* — and their siblings are
already there: `TextAttributes.cs`, `Style.cs`, `SgrEncoder.cs`, and notably `TextSizing.cs`. The
sizing axis already descended; these two were stranded only because they were declared in the same
file as the `TextElement` control. They are byte enums naming SGR codes, with no UI dependency.

Core sits below Rendering, so this serves the goal strictly better than a move to Rendering would:
the engine gets them, and so does every other consumer, including a Core-only one already using
`SgrEncoder` and `Style`. As with the `IBrush` descent, an enum's namespace does not surface in XAML
— attribute values resolve against the property's type — so the markup delta should be nil, which
`proposal-partial-style.md` §5c notes still deserves a test rather than an assumption.

## 9. Relationship to the other proposals

- **`proposal-partial-style.md`** — the access-key delta *is* a `PartialStyle`. The two proposals
  should land in either order, but the access-key option is cleaner if `PartialStyle` exists first,
  since otherwise it needs a hand-rolled two-channel override of exactly the kind that proposal
  retires.
- **The `IBrush` descent** (`proposal-partial-style.md` §5c) — §3's access-key delta reaches for
  `indicatorBrush.ColorAt(...)` from a presenter, and the shared engine would need the same. If
  `IBrush` moves to `Cursorial.Rendering`, the engine can hold a brush directly instead of taking a
  `BrushedTextResolver` callback, which is the difference between the access-key option being a
  value on a struct and being another delegate parameter threaded through the seam.
- **`proposal-textattributes-decomposition.md`** — the same thesis a layer up, and the reason the
  access key's styling can be expressed per-axis at all.
- **The glyph-runs work** — supplies `GlyphSource`/`PaintsAsCells`, which §2 turns into the domain
  predicate. This proposal is what makes that capability reach the controls that do not have it.
