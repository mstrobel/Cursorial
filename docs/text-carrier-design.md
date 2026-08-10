# Text pipeline carrier redesign — decisions

Settled with Mike, 2026-08-09. Supersedes the "§C carrier migration" framing in the
long-horizon plan, which mis-stated the goal.

## The organising idea

The pipeline carries **policies**, which become **coordinates** only at paint.

| type | what it is |
|---|---|
| `CellStyle` | resolved values, policy discarded |
| `PartialStyle` | resolved values + statedness |
| `StyleDeltaTemplate` | **policy awaiting coordinates**; `Resolve` applies it |

Two instances of the same shape, both Mike's framing:
- Alignment is a policy until `bounds` arrives — "known in terms of policy, not actual coordinates."
- Brush mapping is a policy until `(column, row, bounds)` arrives.

Corollary: **it is not the brush that wins an arbitration, it is the mapping policy.** Two
structurally identical gradients at different scopes are different policies. So brush equality
is irrelevant *by construction* — never needed, not merely unreliable.

## The rule

Every level of the object model carries a **nullable** foreground brush.

    null => missing; use the enclosing block/element's scope AND brush.

Scope is **inferred from the declaration site**, never carried. `DeclarationScope` becomes
redundant. `InlineRunScope` already implements inference at one level (a declaration site
produces a scope; descendant runs share it; `TextFormatter.cs:451` back-fills `TotalWidth`).

Inheriting the enclosing *scope* along with the brush is what makes a block gradient span its
runs instead of restarting per run — and why the wrap-invariant inline strip works today.

## The ladder

| level | declaration site | scope |
|---|---|---|
| preference | `StyleDeltaTemplate` into `Paint` via DC/RC | document extent |
| document | `RichText` / `FormattedText.DefaultStyle` | document extent |
| block | `Block` subtypes | block rect (correctly anchored) |
| inline | `TextRun` | inline strip |

Composition: `preferences.Then(document).Then(block).Then(run)` — weakest first, object model
wins wherever it speaks. Same precedence algebra as the UI property lanes.

**SETTLED: `docBounds` must be DERIVED from the formatted content, not aliased to `bounds`.**
Today `DrawingContext.cs:1120` reads `Rect docBounds = bounds;   // can't capture an 'in'
parameter in the resolver closure` — the name is aspirational, the value is the element box.
So `DeclarationScope.Document` has always sampled the element box, and the earlier "`:1194` is
right, `:1213` is wrong" read was built on a false premise: they are not two rects.

Why it must be derived (Mike): a Stretch-oriented element with centred content occupying a
small part of its box. 80-wide box, centred "Hello" (5 wide) landing at columns 37-41, red→blue
ramp:

| sampling rect | spans | ramp fraction across the text | result |
|---|---|---|---|
| `docBounds` (= `bounds`, 80 wide) | 0-79 | 0.468 → 0.519 | ~5% of the ramp, flat purple |
| `ctx.Block` (today's document brush) | **0-4** | 9.25 → 10.25, clamped | flat end-colour |
| document extent (wanted) | 37-41 | 0.0 → 1.0 | the full ramp |

(Arithmetic off the source — NOT yet proven by a rendered frame. Prove before citing.)

**Derivation: DONE in phase 2** (`refactor/extract-anchor-arithmetic`). The arithmetic now lives in
`Cursorial.Rendering/Text/FormattedBlockWalker.cs` — a struct enumerator the paint loop drives,
owning the anchors, margin collapse, the row budget, `FillEntireBounds` re-centring, and
`PaintedColumns`. Each `FormattedBlockPlacement` carries BOTH answers: `SamplingRect` (today's
divergent one, Left-forced for paragraphs) and `Extent` (anchored by the block's real alignment).
`FormattedText.ComputeExtent(in Rect)` exposes the union; `Rect.Union` was added for it, with an
empty operand as the identity — `Rect.Empty` sits at the origin, so a naive accumulator would come
back stretched to `(0,0)`.

`ComputeExtent` is deliberately UNCONSUMED until 7b, and covered by
`FormattedTextExtentTests` — which checks it against a painted buffer's ink bounding box rather
than re-derived arithmetic, so drift between walk and painter moves real ink.

Phase 2 also found the arithmetic was computed THREE times, not twice: `PaintBlock` built a rect
byte-identical to the one `PaintParagraph` rebuilt, discarded for every paragraph and read only by
the content-block arm. And `PaintParagraph` never used the block anchor for placement — its sole
reader was the sampling rect, which is the defect stated structurally rather than numerically.

**`FillEntireBounds` is NOT a special case** (corrected — I claimed twice that it makes
`docBounds == bounds`; it does not). Per Mike it exists for rendering rich text directly into a
`CellBuffer` outside the UI layer: format against the whole terminal, centre the document's
bounding box within it, save the caller the layout math. The document's box is still `Size` —
merely centred rather than top-anchored (`FormattedText.cs:63`,
`row = bounds.Row + (bounds.Rows - Size.Rows) / 2`) — with the surround cleared. So there are
simply TWO OPERATIONS, each sampling the rect it covers:

- `ClearCells(bounds, DefaultStyle)` owns the whole box → samples `bounds`
- the document paints its own extent → samples the derived extent

That is the proposal's ownership rule falling out unaided, and it removes the need for both a
per-channel scope rule and a `FillEntireBounds` redefinition. The derivation must account for
the vertical centring when computing the extent.

Corollary: the earlier worry that `MeasurePrimaryContent` ignores `FillEntireBounds` was
misplaced — the UI layer does its own layout, so the flag has little to say there.

**Rejected:** granular control via preference→`bounds` / document→`docBounds` (Mike's own
proposal, withdrawn) — a no-op today since the two are the same variable, and the element box
has no demonstrated use as a sampling rect once the extent is derived.

## Carrier

**`StyleDeltaTemplate`, single type.** Mike: "Nothing is lost by using StyleDeltaTemplate as
the carrier." `IsUniform` fast-paths the no-brush case. Rejected: two types
(`PartialStyle` + template) — two composition paths eventually disagree.

### Naming — SETTLED, two-step rename (Mike drives, IDE)

1. `BrushedStyle` → **`ScopedBrush`** (the `IBrush`-unreachable remnant, deleted later; the name
   is honest — it carries a `DeclarationScope` selector, not bounds)
2. `StyleDeltaTemplate` → **`BrushedStyle`**

Renaming the doomed type out of the way FIRST means `BrushedStyle` denotes exactly one thing at
every commit. Why `BrushedStyle`: the type documents itself as `PartialStyle` with `IBrush` where
`PartialStyle` has `Color`, everything else identical (`StyleDeltaTemplate.cs:14-19`), so
brush-ness IS the whole difference. Participle + head noun — `Style` claims the full payload,
`Brushed` says only what form the colour channels take; `BrushStyle` (nominal modifier) reads as
a contents claim and undersells, which was Mike's objection. Family: `CellStyle` / `PartialStyle`
/ `BrushedStyle`. `Template` is disqualified — the repo's four `*Template` types
(`ControlTemplate`, `DataTemplate`, `HierarchicalDataTemplate`, `ItemsPanelTemplate`) are all
visual-tree factories. `StyleDescriptor` disqualified — 200+ `*Descriptor` uses here all mean
reflection-style metadata. `StyleBrush` disqualified — already an identifier at
`TemplateLanePrecedenceTests.cs:25` meaning the opposite thing.

**What the IDE rename misses** (~78 string literals; 4 doc files / 35 mentions;
5 test files to rename; the wiki):
- Strings: `DrawingContext.cs` 36, `RenderContext.cs` 19, `IGlyphFont.cs` 4,
  `PartialStyleTests.cs` 4, `BrushedTextResolver.cs` 3, +6 files with one each
- Docs: `drawing-layer-design.md`, `proposal-partial-style.md`, `proposal-fragment-opacity.md`,
  `proposal-glyph-runs.md`
- Rename: `StyleDeltaTemplateTests`, `GlyphTemplatePaintTests`, `FillTemplateTests`,
  `DrawTextTemplateTests`, `RenderContextFillTemplateTests`
- **DO NOT rename** `TemplateLoweringTests` (XAML templates) or `TemplateLanePrecedenceTests`
  (the Template binding lane) — a blanket regex sweep wrecks exactly the two files whose
  `Template` is the sense this rename exists to stop colliding with
- Wiki: currently 0 mentions, but the running fix round will introduce some (it teaches the live
  `StyleDeltaTemplate` overload in place of the obsolete `Color` ones). Sweep after.

**Rejected: also renaming `PartialStyle` → `CellDelta`** — 482 refs across 62 files, and
`proposal-partial-style.md`'s own filename would go stale, to fix a complaint about a different
type. (`proposal-partial-style.md:526` does record Mike's doubt: "'Partial' reads as
'incomplete'; 'delta' reads as 'change', which is nearer the semantics.")

Declaration points become deltas:
- `TextMarkupOptions.DefaultStyle` (`TextMarkup.cs:36`)
- `RichTextBuilder` ctor (`:59`), `Push`, `Run` overloads
- `TextRun.Style`, `Block` subtypes' `Style`

## Markup: `fg`/`bg` resolve to BRUSHES (Mike, 2026-08-09)

"`BrushMarkup` should be effectively obsolete after the refactor. The fg and bg tags should
resolve to brushes rather than colors."

Consequences:
- `TextMarkup.cs:281`'s `options.DefaultStyle.WithForeground(ParseColor(...))` — which flattens to
  a concrete `Color` at parse time — is replaced by a delta stating only the foreground BRUSH.
- **`MarkupColor.TryParse` loses its markup caller.** The session opened on why `TryParse` and
  `TryParseBrush` could not be unified: `TryParse` flattens indices >= 16 to RGB, destroying the
  index `TryParseBrush` needs to intern against `BrushPalette.Ansi256`. With markup on the brush
  path, only `TryParseBrush` is consulted and the two-mappings-must-agree problem is gone.
  `IsThemePaletteIndex` (Mike's `de076a20`) stays as the boundary, consulted once.
- The separate `[brush=…]` tag, `TextMarkupOptions.BrushResolver`, `ScopedBrush`-on-`Tag`, and
  `BrushedTextResolver`-as-closure all die together — a run's brush lives in its own delta.
- `TextMarkup.cs:489`'s stale error ("the Drawing layer wires one up", wrong since `BrushMarkup`
  moved to `Cursorial.Rendering.Media`) needs no fix; the mechanism goes away.
- `[fg=…]` gains gradients for free, at inline scope, without anyone stating a scope.

**SETTLED (Mike): the registry STAYS and moves behind `fg`/`bg`.** "It's genuinely useful because
inline gradient declarations in markup are verbose." So it is the `[brush=…]` TAG that is
obsolete, not the mechanism — `BrushMarkup.Options(defaultStyle, registry)` survives as the way to
wire names, and `fg`/`bg`'s parser takes a resolver.

Resulting vocabulary for `fg`/`bg`:
- `[fg=red]`, `[fg=#f92672]`, palette indices — syntactic, via `TryParseBrush`
- `[fg=linear:#f92672,#66d9ef]` — possible but verbose, which is why the registry exists
- `[fg=sunset]` — registry lookup

`TextMarkupOptions.BrushResolver` sheds its own remnant while it is there: it is
`Func<string, object?>` today, returning `object?` only because `Rendering` could not name
`IBrush`. It can now, so it becomes `Func<string, IBrush?>` and the opacity dies with the tag.

**SETTLED (Mike): REGISTRY FIRST, then the parser** — the reverse of what I recommended, and his
reasoning is better. Prompted by wanting to ship named GRADIENT brushes alongside the named colour
brushes, which creates collisions. Current code resolves parser-first; he is flipping it.

Why registry-first wins: it makes an override as narrow as the thing overridden. Under
built-in-first, an author who wants a different `red` cannot reach it from the registry at all and
must redefine it **theme-wide** through the ANSI brush resources — the only other lever, and far
broader than the intent. My "built-in-first is predictable" argument weighed predictability in
isolation and never priced the alternative. Granularity of override is the deciding axis, not
surprise.

**OPEN (raised, not decided):** are the built-ins *in the parser*, or seeded into a DEFAULT
REGISTRY that the author's entries overlay? Two ordered lookups work, but layered maps make
precedence structural rather than procedural — a miss falls through, and no ordering rule exists
for anyone to get backwards later. Same null-means-fall-through shape as the brush ladder, and it
gives the new built-in gradients a home instead of a special case in the parser. Note `#hex` and
`linear:…` are syntax, not names, so they stay in the parser either way — which may be the natural
seam: names through the layered registry, syntax through the parser, nothing arbitrating.

## What this deletes

- `StyleExtensions.Compose` (`RichTextBuilder.cs:417-432`) → `PartialStyle.Then`
- the `DefaultStyle.WithForeground` rebuild (`TextMarkup.cs:276-285`)
- `object? Tag` + `BrushedStyle` — remnants of `IBrush` being unreachable from `Rendering`
- `BrushedTextResolver` as a closure — the preference layer arrives as a value
- `BrushedTextContext.BaseStyle` — existed solely to feed the equality test
- `DeclarationScope` — inferable
- the equality test at `DrawingContext.cs:1209-1210`

## Constraints and hazards

**Equality.** `PartialStyle` is value-equal; a template carries references. Rule: **the template
travels, caches key on what it resolved to.** Affected: `SizedTextFragment.Key` (`:80`, and
`:74-79` records reference identity was rejected on purpose), `ScaledText._placeholderStyle`
(`:136`, `:149-153`), `FrameRenderer.FragmentsMatch` (`:1102`). Likely cheap because the
fragment path collapses to one sample (`FormattedText.cs:263-265`) *before* the key is formed.

**Phase 3 (DONE): the rule is a marked census.** `grep -rn "CACHE KEY:"` returns every
declaration that must stay a resolved value: `Cell.Style`, `FragmentEntry.AnchorStyle`,
`IBufferFragment.StyleOverride`, `SizedTextFragment.Key`, `FrameRenderer._currentStyle`, and the
two `ScaledText` staleness comparisons (marked with the forward rule: resolve at the anchor
before comparing; a `!IsUniform` template rebuilds unconditionally). `_currentStyle` was found by
a four-lens sweep of the tree, not the plan — the plan's list stopped at the buffer boundary and
missed that the renderer also memoizes a bare style for SGR suppression. Everything else the
sweep surfaced (`_frontCells`, `_frontFragments`, the diff short-circuits, `_fragmentsByKey`)
CONSUMES a marked declaration and derives its soundness from it. The deliberate outlier is
`Icon._cachedEffectiveBrush`, marked as outside the census: reference comparison of brushes is
fine where a false inequality only costs a spurious property-changed dispatch. Each `ScaledText`
cache is pinned by a two-direction pincer in `ScaledTextCacheKeyTests` — a missed rebuild and a
spurious rebuild are distinct failures, and each mutation direction kills exactly one test.

**`TextBlock`'s cache key** (`:364-373`) has no style term deliberately — brush and attributes
merge at paint. Preserved as long as resolution stays at paint. Only breaks if brushes are baked
in at format time, which is the opposite of carrying a policy.

**Widening, not replacement.** Runs carry a template *alongside* a base. Then A
(`IGlyphFont.Paint`) and B (`IContent.Paint`) do not block — and `IGlyphFont.cs:114-118` says the
base is required by design: "without a base there is nothing for the channels it declines to
state to fall through to."

**Characterisation baselines are the harness, not collateral.**
`TextPipelineCharacterisationTests.cs:1-21` — captured *before* this migration, which is
"supposed to be PURELY STRUCTURAL — not one glyph moves, not one output byte changes." 13,985
lines across 4 files, 81 corpus cases (12,147 lines / 73 cases before phase 0a appended eight).
A moving baseline is the failure signal.

**SPLIT INTO TWO COMMITS** (audit `wf_d2907f1f-091`, 5 findings survived of 33). The carrier
migration is structural and baselines must NOT move. The scope rule (document ⇒ `docBounds`)
legitimately moves **10** corpus cases — measured by flipping `DrawingContext.cs:1213` to
`docBounds` and re-recording: `brush-document-scope-horizontal`, `brush-document-scope-vertical`,
`brush-over-document-default-foreground`, `brush-explicit-foreground-wins`,
`brush-inline-content-fallback-glyph`, `figlet-brushed-per-cell`, `sized-brushed-block-scope` (the
7 this document counted before phase 0a), plus `align-center-brushed-wider-than-budget`,
`figlet-brushed-explicit-background` and `brush-fill-entire-bounds` (appended by phase 0a).
`sized-brushed-block-scope` is still the only one of the ten that feeds tier 3 and so moves emitted
VT bytes. Landed together, the harness cannot distinguish intent from regression. Carrier first
with baselines frozen; scope rule second with baselines re-recorded.

**Audit outcome:** the rule survives. 28 of 33 findings refuted — three lenses (levels-exist,
other-channels, collapse-points) came back fully empty after adversarial verification. Adding a
nullable brush to `Block`/`FormattedBlock` is the additive *implementation* of the rule, not an
obstacle: `IBrush` is same-assembly, the namespace is already imported in `Rendering/Text`, and
`Block.Alignment` (`TextAlignment?`, resolved as `block.Alignment ?? Alignment` at
`TextFormatter.cs:265`) is the same nullable-means-inherit shape already shipping.

**Two confirmed issues:**
1. Document brush samples `ctx.Block` not `docBounds` — pinned by
   `BrushResolverDeltaTests.cs:99-100` against deliberately-distinct fixture rects
   (`Doc = (0,0,10,4)`, `Block = (1,1,8,2)`), and by corpus DESCRIPTIONS, which are dumped into
   the baselines and so are fixture bytes ("…and resets between blocks", `TextCorpus.cs:460-465`).
2. A run's declared brush is dropped on FIGlet/sized runs — `FormattedText.cs:167-168` hard-codes
   `tag: null`. Latent bug today; under the rule it erases the stated/silent distinction exactly
   where the rule depends on it.

## Defects found while designing

1. **Mis-anchored block rect — PROVEN, phase 0a; still live, fixed in 7b.** The paragraph's
   sampling rect is anchored Left (`FormattedText.cs:101` forces it) while its lines anchor with
   the paragraph's real alignment. Corpus case `align-center-brushed-wider-than-budget` shows it:
   line 0 starts at column 5 and the ramp clamps to `#0000ff` at columns 12-13, because the
   sampling rect ends at 11. Reaches the most-travelled brush path via `ctx.Block` at
   `DrawingContext.cs:1213`. Since phase 2 both answers are carried side by side on
   `FormattedBlockPlacement` (`SamplingRect` vs `Extent`); 7b switches the consumer over.
   NOTE for 7b: that corpus case's own description cites the pre-phase-2 line numbers and the rect
   is no longer built in `PaintParagraph` at all. Descriptions are fixture bytes, so it can only be
   corrected in a phase that re-records the baselines — 7b is that phase.
2. **Two rects both called "document."** `:1194` uses `docBounds` for
   `DeclarationScope.Document`; `:1213` uses `ctx.Block` for the document brush. The ladder says
   `:1194` is right.
3. **`FigletPresenter.CachedState`** (`:59`) has no `ParseFreshness` sibling — same shape as the
   `RichTextPresenter` bug fixed in `9e199230`, apparently unfixed.
4. **`FillEntireBounds`** clears with `DefaultStyle`, a `CellStyle` (`FormattedText.cs:60-64`),
   so a *background* brush cannot fill that region.
5. **`TextMarkup.TryParseBrush`** (`:503-508`) is dead — private, zero call sites, discards its
   bool. (Distinct from the live `MarkupColor.TryParseBrush`.)
