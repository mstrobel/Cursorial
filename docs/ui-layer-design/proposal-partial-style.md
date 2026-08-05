# Proposal: `PartialStyle` — Channel-Explicit Style Operations

**Status: PROPOSED (2026-08-05).** Produced during the opacity-group compositing work, from a
maintainer observation while auditing fill/tint call sites. No code written. §8 lists the decisions
that need an owner before implementation.

---

## 0. The problem, and the thesis

`proposal-textattributes-decomposition.md` opens with the report *"you want to add Bold? Well,
better hope Inverse wasn't already there for a good reason"*, and settles it with a thesis worth
quoting exactly: **the store is not the bug — the granularity is.** Nine axes packed into one
attached property forced producers who cared about *different* things to fight over a whole value.
Decomposing to one `UIProperty` per axis made the existing machinery compose correctly.

That fix landed at the **UI property** layer. The same defect survives one layer down, at the
**Drawing operations** layer, and for the same reason: a fill or a tint has to speak in whole
`Style` values, so it cannot say which channels it actually owns.

The thesis is therefore identical, applied to operations rather than properties: an operation
should declare the channels it carries. `PartialStyle` is that declaration.

## 1. What the missing concept costs today

Every operation that means "change some of this cell's style" currently improvises, and each
improvisation has a failure it cannot express its way out of.

**`DrawingContext.TintCells` hand-rolls a channel mask in prose:**

```csharp
var tinted = cell.Style with { Attributes = cell.Style.Attributes | style.Attributes };
if (!style.Background.IsDefault)
    tinted = tinted with { Background = style.Background };
```

`Color.Default` is pressed into service as "absent". The consequence is exact and live: **you
cannot tint a cell *to* the terminal default background**, because the value that would say so is
the one that means "leave it alone".

**The same operation cannot un-invert.** On the NoColor tier the selection tint *is*
`TextAttributes.Inverse`, applied by OR. Tint already-inverse text and `Inverse | Inverse` is
`Inverse` — the selection is invisible on precisely the content where it matters most. OR can only
turn flags on; replacement clobbers the other eight axes; neither can say "flip this one".

**Blank/orphan rules had the mirror problem.** Wide-glyph orphan cells were reset with
`Cell.Blank`, stamping `Style.Default` — an operation whose intent was "clear the glyph" silently
claiming a channel it did not own, punching an opaque hole through whatever region the cell
belonged to. Fixed (2026-08-05) by preserving the existing style, but the fix is a convention held
by comments in five places rather than something the types express.

**Diagnosis.** Three sentinel conventions (`Color.Default` means absent; `Style.Default` means
blank; OR means "add") each encode partial intent in a value whose real meaning is something else.
They are not individually wrong; they are individually *ambiguous*, and the ambiguity is
unresolvable within the current vocabulary.

## 2. The type

```csharp
/// A channel-explicit style delta: which channels an operation owns, and what it does to them.
/// OPERATIONS ONLY — never stored in a Cell. Nothing in the cell model accepts one; the only
/// way out is ApplyTo, which yields a Style.
public readonly struct PartialStyle
{
    public Style ApplyTo(in Style current);
    public PartialStyle Then(in PartialStyle next);   // composition; see §5
}
```

`readonly struct`, deliberately **not** `ref struct`. The invariant that matters is "never lands in
a `Cell`", and that is already structural: no member of `Cell` or `Style` accepts a `PartialStyle`,
so there is no path into storage. Ref-ness would additionally forbid fields, arrays, lambda
capture and use across `await` — four constraints, the last two surfacing far from the type's
definition, bought to prevent a mistake that is not available to make. Revisit if misuse is ever
observed; tightening later is cheap, loosening after call sites exist is not.

A consequence worth stating, because it is an argument *for* the choice rather than merely an absence
of cost: a plain struct can be a **theme resource**. Resources live in dictionaries, are looked up by
key, and travel through `DynamicResource` — all storage, all foreclosed by ref-ness, and foreclosed
invisibly: nobody discovers the limitation until they try, by which point the type is load-bearing.
`{DynamicResource Theme.SelectionDelta}` stays available whether or not it is ever wanted. See §8.5 —
the brush-carrying template is the form a theme would actually publish, since a selection tint is
naturally brush-backed (`ThemeKeys.SelectionBrush` is a brush, not a colour) and a gradient-backed
tint must resolve per cell at use, not at declaration. That argues for the template being a
first-class public type rather than an overload hidden behind the fold. It also gives §5a a second
entry point: a delta may come from a resource lookup instead of an element fold, and both must
produce the same value — which is the property a type claiming to be "a declaration of channels
owned" should have.

`Style` itself stays a plain struct with non-nullable channels. It is per-cell hot data, copied per
pixel through compositing; the expressiveness is needed at operation boundaries, not in storage.
`Color` already spends its "default" state on *the terminal's default colour*, so nullability there
would be a third state layered on an occupied one — which is how the current ambiguity arose.

## 3. Attribute channels: four states, two masks

Attributes need **on / off / unset / toggle**. Toggle is not a luxury — §1's inverse-on-inverse bug
is exactly a missing XOR, and relative inversion over a heterogeneous region cannot be expressed by
any absolute value.

Encode as two `TextAttributes`-sized masks, applied `(current & ~Clear) ^ Xor`:

| intent | `Clear` | `Xor` |
|---|---|---|
| unset (leave alone) | 0 | 0 |
| turn on | 1 | 1 |
| turn off | 1 | 0 |
| toggle | 0 | 1 |

Two properties worth stating:

- **No invalid states.** Separate `Add`/`Remove`/`Toggle` sets admit a flag in two of them and need
  precedence rules to say what that means. Here every bit pattern is meaningful.
- **`default(PartialStyle)` is the identity.** Storing `Clear` rather than `Keep` makes a zeroed
  struct inert. The opposite encoding would make the default "turn every attribute off" — the same
  shape of footgun as `Cell.Blank`.

## 4. Colour and remaining channels: presence

`Foreground`, `Background`, `UnderlineColor`, `UnderlineStyle` and `Hyperlink` need only
**unset / set** — one presence bit each, carried separately from the value.

This is what retires `Color.Default`-as-absent: with presence explicit, `Background = Color.Default`
means *set the background to the terminal default*, which no fill or tint can currently request.

## 5. Apply and compose

`ApplyTo` is per-channel and total: unset channels pass through untouched, set channels replace,
attributes follow §3.

Composition matters for tint-over-tint and for group compositing, where a stack of deltas should
collapse before touching cells rather than being applied per cell in sequence:

```
Clear = C₁ | C₂
Xor   = (X₁ & ~C₂) ^ X₂
```

with presence-based channels taking the later value where both are set. This makes `Then`
associative, so a stack folds in any order — worth a property test, since that is the sort of claim
that is easy to assert and easy to get subtly wrong.

## 5a. Deriving the mask from the property system

The decomposition makes the channel mask **derivable rather than declared**. `TextElement` already
has a `ComposeAttributes`-style fold over the per-axis properties; the analogous method yields a
`PartialStyle` by asking each axis for its value source instead of collapsing everything into a
`TextAttributes` bitset:

```
{ Kind: Default or Inherited, IsCurrentValue: false }  ->  channel UNSET
anything else                                          ->  channel SET (to its effective value)
```

`ValueSource(BindingPriority Priority, bool IsCurrentValue)` with its `Kind` carries exactly this.
The `IsCurrentValue` carve-out is load-bearing: a `SetCurrentValue` landing over a Default lane is
an explicit assignment and must read as SET, which is what distinguishes it from an untouched
property.

The consequence is that **authors never write a channel mask**. They set the axes they care about
in XAML or a theme rule; the mask falls out of the property system's own record of who set what.
This is the payoff the decomposition earned: before it, nine axes shared one property and one value
source, so "unset" and "off" were indistinguishable at the fold.

**Boundary:** value source yields *unset / on / off* — not *toggle*. That is correct rather than a
gap. Toggle is an operation concept, not an authoring one: `Inverse="True"` means "be inverse",
never "flip whatever is there". Authored styles derive three states; toggle is constructed in code
by the operation wanting relative semantics (the NoColor selection tint asking to toggle `Inverse`
rather than OR it). Putting a `Toggle` member on the per-axis property type would move the concept
into markup — not recommended, but it is the fork.

## 5b. Brush-backed channels — the one thing §5a cannot auto-populate

The value-source fold yields concrete values, and a `SolidColorBrush` resolves to one. A brush that
samples per cell location (gradients, and anything else whose `ColorAt(column, row, bounds)` varies)
has **no single colour to bake into a channel** — the very call `TintCells` already makes per cell.

Keep `PartialStyle` free of geometry and put sampling in a resolve step:

```csharp
public readonly struct StyleDeltaTemplate      // holds IBrush? per colour channel
{
    public PartialStyle Resolve(int column, int row, in Rect bounds);
}
```

`ApplyTo` therefore never takes coordinates, `PartialStyle` stays a pure value, and the per-cell
cost is explicit at the call site rather than hidden in the apply. Constructing one per cell is
cheap — a small `readonly struct` on the stack, which is what the paint loops already do with the
sampled `Color`.

Two consequences worth pinning:

- **All-solid templates are loop-invariant.** When every brush is a `SolidColorBrush`, `Resolve`
  returns the same value for every cell, so callers can hoist it out of the loop. Worth an explicit
  fast path or at least a documented note, since the common case is all-solid.
- **Composition happens on templates, resolution once per cell.** Composing resolved values per cell
  would sample every brush at every level for every cell; composing templates first samples each
  brush once per cell. §5's compose laws apply to the resolved values, so template composition must
  preserve them — a property test worth having.

### The layering: move `IBrush` down, and the delegates disappear entirely

`IBrush` lives in `Cursorial.Drawing/Media/`, and `Cursorial.Rendering` references only
`Cursorial.Core` and `Cursorial.Shared` — it cannot see brushes. That is why `BrushedTextResolver`
exists: Rendering defines a callback shape, Drawing supplies the implementation, and
`BrushedTextContext` ferries `BaseStyle` + position OUT so a full `Style` can come back — a delegate
hop per cell, purely to work around a dependency Rendering is not allowed to have.

**But `IBrush` is in the wrong assembly by history, not by dependency.** Its entire surface is
`double Opacity`, `bool IsOpaque`, and `Color ColorAt(int column, int row, Rect bounds)`. `Color` is
in `Cursorial.Output` (Core); `Rect` is in `Cursorial.Rendering`. The file imports nothing from
`Cursorial.Drawing` — it already compiles against Rendering-and-below.

Its own documentation already states the sampler contract: *"A color **source** the drawing layer
samples per cell... resolved to a scalar `Color` at draw time"*, with `ColorAt` specified as pure,
allocation-free, and safe under concurrent invocation. That is precisely the discipline a
Rendering-level primitive needs, written before anyone proposed moving it.

**Proposal: move the `IBrush` interface to `Cursorial.Rendering`. Implementations stay in
`Cursorial.Drawing`.** Implementations bind to the interface, not the reverse, so nothing is dragged
along:

| descends to Rendering | stays in Drawing |
|---|---|
| `IBrush` — the interface, and nothing else | `SolidColorBrush`, gradients, `ImageBrush`/`TileBrush`/`ImageSampler`, `Pen*` |
| | `BrushAnimation`, `BrushInterpolator`, `PenAnimation`, `PenInterpolator` — the only `Media/` files touching `Cursorial.Animation` |

**Rejected alternative: a separate sampling interface in Rendering that `IBrush` implements.** It
works, but it creates two names for "thing that yields a color at a position", and every downstream
API must then choose one. That is the `Rect`/`LayoutRect` shape — a parallel type existing to paper
over an assembly boundary — which `proposal-signed-rect.md` exists to delete. Adding a second
instance of the pattern while removing the first is hard to justify.

**What this buys, and it is larger than a better return type: both delegates are retired, not
improved.** `GlyphStyleProvider` and `BrushedTextResolver` exist *solely* because Rendering could not
name a brush. Once it can, Rendering holds the brush directly and the callback seam disappears —
`FormattedText`, `ScaledText` and `ShadowedFont` take a value instead of a closure.
`BrushedTextContext.BaseStyle` also becomes unnecessary: Rendering already holds the base and no
longer ships it out to receive a reconstructed whole style in return.

### Which namespace, and why the implementations must not follow

The governing rule: **an interface may diverge from its implementations; implementations must live
side by side.** Interface-apart-from-implementation is an ordinary, legible pattern — the contract
published where consumers need it. Concrete siblings split across namespaces is not: a consumer
writing `using Cursorial.Drawing.Media;` should get every brush, and a split would separate
`SolidColorBrush` from the gradients for a reason invisible from the call site.

The repo runs two conventions — `Cursorial.Core` decouples (`Cursorial.Input`, `Cursorial.Output`,
`Cursorial.Terminal`, `Cursorial.Text`), while `Cursorial.Rendering` and `Cursorial.Drawing` are
strictly assembly-aligned. Two measurements then narrow the choice to one:

- **No namespace spans two assemblies today** — every namespace is owned by exactly one. A shared
  `Cursorial.Media` across both DLLs would break an invariant that currently holds universally.
- **`Cursorial.Output` is excluded by a hard constraint, not preference.** It is the conceptually
  best home — `Color` (`Cursorial.Core/Output/Color.cs`) and `Style` both live there, and a brush is
  precisely a `Color` source resolving into a `Style`. But that namespace belongs to
  `Cursorial.Core`, and `ColorAt` names a `Rect` from `Cursorial.Rendering`. Core sits *below*
  Rendering and cannot reference the parameter type.

  Unblocking it would mean flattening `bounds` into four ints — losing the documented "`bounds` is
  the brush's coordinate space" semantics — or moving `Rect` down to Core, which drags the whole
  geometry vocabulary with it. Neither is worth it. **Settled: this is not a live alternative**;
  recorded only so the constraint does not have to be rediscovered.
- **Keep the `Media` leaf; do not go flat.** Rendering already groups topically — `.Fonts`,
  `.Text`, `.Fragments`, `.Imaging`, `.Content` — reserving the flat namespace for core value types
  (`Cell`, `Rect`, `CellBuffer`). A flat `IBrush` would be the anomaly *within* Rendering. The
  parallel `.Media` leaf is a feature, not a collision: `*.Media` means "brushes" in both
  assemblies, so the divergence sits entirely in the layer prefix, which is exactly what the
  divergence is.

> **Decision (settled): `Cursorial.Rendering.Media.IBrush`.** Assembly-aligned, preserves
> one-namespace-one-assembly, keeps the topical leaf the family is known by, and can name the `Rect`
> its signature already depends on. Every implementation stays in `Cursorial.Drawing.Media`.

### Why the move is cheap, and why it must stay scoped to the interface

Measured on the current tree: `IBrush` is referenced in **77 C# files** and **0 `.xaml` files**. The
zero is structural, not luck — an interface cannot be instantiated in markup, so XAML always names
the concrete type, and a property *typed* `IBrush` never surfaces in markup at all.

That matters because an IDE namespace refactor updates C# usage sites but **not** XAML. Here the
XAML delta is empty, so the automated refactor is complete by itself — provided the move stays
scoped to the interface. `SolidColorBrush` appears in 2 `.xaml` files, which is a second, independent
reason it should not come along: moving it would convert a zero-risk mechanical change into one with
a hand-edited markup surface, for no benefit this proposal needs — on top of the sibling-divergence
argument above.

One item to verify rather than assume at implementation time:
`Cursorial.Drawing/Properties/XmlnsDefinitions.cs:5` declares
`[assembly: XmlnsDefinition("https://cursorial.dev/ui", "Cursorial.Drawing.Media")]`. Removing
`IBrush` from that CLR namespace does not disturb the mapping for the types that remain, but a
property typed `IBrush` assigned from a string in markup (`Foreground="Red"`) resolves its type
converter by CLR type. That path should get an explicit test, not an assumption.

`StyleDeltaTemplate` can then also live in Rendering, holding `IBrush?` per channel directly. The
§5b fast path still applies — all-solid brushes make `Resolve` loop-invariant, so it resolves ONCE
and only genuinely position-varying brushes pay per-cell work.

## 5c. The delegates that already have delta semantics

Two public delegates produce styling per position, and BOTH return a whole `Style`:

```csharp
public delegate Style GlyphStyleProvider(int column, int row);
public delegate Style BrushedTextResolver(in BrushedTextContext context);
```

The delta semantics are already there — only the type is missing. `FormattedText.ResolveStyle`
takes a **base style** and a resolver and produces the final style; that is "apply this delta to
this base", written with a full-`Style` return because no vocabulary for the delta exists. The cost
is borne by every implementation: each must remember to preserve the channels it did not mean to
touch, and the signature cannot say which those are.

`FormattedText` (~:205) also closes over `glyphText.Style` to feed a `GlyphStyleProvider`,
allocating a lambda per paint to carry a base the painter already holds.

**Convert both to return `PartialStyle`.** They should NOT merge — they differ in the context they
receive (a position versus a richer `BrushedTextContext`), which is a real distinction. What unifies
them is the return type. The painter then applies the delta to the base it already has, and the base
stops being something every callback receives, carries, and faithfully reconstructs.

Layering, once converted:

| form | shape | when |
|---|---|---|
| `StyleDeltaTemplate` (§5b) | declarative, brush per channel, no closure | the common case |
| the two delegates → `PartialStyle` | arbitrary logic, now channel-explicit | the extensibility hook |

**This is also the concrete case behind §2's `ref struct` rejection.** The residual risk identified
there was "a deferred-draw callback carrying a restyle". These delegates are exactly that, and they
are real, not hypothetical — a `ref struct` cannot usefully be a delegate return type here. The
`readonly struct` decision is therefore load-bearing for this conversion, not merely convenient.

## 5d. Tinting descends to `CellBuffer` — and `TintCells` is already a hand-rolled `PartialStyle`

Once `IBrush` is in Rendering (§5c), nothing keeps tinting in the drawing layer. `CellBuffer` can
host the primitive directly, beside the `Fill(in Rect, in Cell)` that already exists at
`CellBuffer.cs:742` — though that one *replaces* cells while this one modifies style in place, so it
wants a different verb (`Restyle`/`Tint`) rather than a `Fill` overload.

The existing `DrawingContext.TintCells` (`:962`) is the proof the abstraction is right:

```csharp
var tinted = cell.Style with { Attributes = (cell.Style.Attributes & ~TextAttributes.Inverse) | style.Attributes };
if (!style.Background.IsDefault)
    tinted = tinted with { Background = style.Background };
```

That is a `(Clear, Xor)` mask pair with the mask **hardcoded**: `Clear = Inverse`,
`Xor = style.Attributes`, background gated on `IsDefault` standing in for a presence signal, and
foreground / underline unreachable. It is also the three-state-flag problem solved by fiat — one
channel special-cased because there was no vocabulary for "toggle this attribute". Replacing it with
a template generalises the operation instead of merely relocating it.

Three questions to settle before implementing:

1. **The brush needs two rects, not one.** `ColorAt(column, row, Rect bounds)` documents `bounds` as
   the brush's coordinate space. A gradient tint over a selection must anchor to the *element*, not
   the selection sub-rect, or the gradient restarts whenever the selection resizes. The signature
   likely mirrors `Blit`: a region to affect plus a separate sampling space.
2. **Wide pairs at the region edge.** A continuation carries no style of its own now that the frame
   renderer emits the leading half's, so tinting a region covering only the continuation is a visual
   no-op, while tinting only the leading half tints the whole glyph including the column outside the
   region. Same family as the figlet selection-tint off-by-one; it needs a decided answer.
3. **`TintCells` does not disappear — it transforms and delegates.** `CellBuffer` has no ambient
   transform, so `DrawingContext` keeps the method and maps into scene space first.

   Measured: `TryMap` (`DrawingContext.cs:141`) is a pure translate plus a **rectangular** clip —
   there is no non-rectangular clipping. So the delegating form is strictly better than the current
   per-cell loop: translate `bounds` into scene space, intersect once with `s.Clip` (both in the
   same space, so nothing is mixed), then make one buffer call instead of W×H `TryMap`
   invocations. The existing comment warns that "a rectangle-level intersection here would mix
   spaces whenever a translate is active" — true of the naive version that intersects local-space
   bounds against a scene-space clip, but translate-first is exactly what the loop computes, hoisted.

   **Sequencing catch:** under a negative translate (a scrolled editor) the translated rect has a
   negative origin, which `Rect` cannot represent. The per-cell loop dodges this by rejecting cells
   individually (`if (sceneCol < 0 || sceneRow < 0) return false`); a rect-level version must clamp
   the origin to zero and shrink the extent — which is compensation #3 in `proposal-signed-rect.md`
   verbatim. **This adds a fifth instance of that workaround unless signed `Rect` lands first.**

### Why this matters beyond tidiness: the `Core + Rendering` tier

`FrameRenderer` lives in `Cursorial.Rendering`, so that tier already owns the entire path from cells
to escape sequences: input, output, `CellBuffer`/`CellBufferView`, frame diffing, 12 files of fonts,
16 of text layout, plus fragments, imaging and content. It is not a thin tier — it is **one
capability short of self-sufficient**. A consumer can lay out and emit text but cannot fill a region
with anything but a flat `Style`, nor tint at all, because all 37 files of `Media/` sit above the
line.

The invariant §5c and §5d restore is worth stating as a placement rule for future arguments:

> **`Cursorial.Rendering` owns everything that touches cells. `Cursorial.Drawing` owns everything
> that composes scenes** — `Scenes/`, `Charts/`, `Geometry/`, `Animation/`.

`Media/` is currently the sole violation: brushes and tinting are cell-level operations living in
the scene layer. Moving them is what makes `Core + Rendering` a coherent thing to consume on its own
— a complete immediate-mode terminal renderer without the element tree, layout system, theming,
binding and XAML that `Cursorial.UI` adds.

## 5e. Blending mode belongs in the template, and `IContent.Paint` should take one

**Blending mode is the colour-side equivalent of the attribute mask.** `PartialStyle`'s
`(Clear, Xor)` pair specifies how *attributes* combine with what is already there — on, off, unset,
toggle. Colours, as specified so far, only replace. That asymmetry means the type would carry merge
semantics for one channel family and leave the other implicit. A blending mode closes it.

`DrawingContext.TintCells` (§5d) shows that stopping halfway would be arbitrary: it hardcodes *both*
rules — clear `Inverse` then OR the rest, and replace background when set. Generalising only the
attribute half leaves the other hardcoded for no principled reason.

Layering is free: `IBlendingMode` and `BlendingModes` live in `Cursorial.Core/Output` (namespace
`Cursorial.Output`), below Rendering, so a template in Rendering carries one with no descent needed.

### `IContent.Paint` — the evidence is already in the tree

```csharp
Rect Paint(in CellBufferView buffer, in Rect bounds, in Style style, OutputCapabilities capabilities);
```

Implementors are `Icon`, `Image`, `ScaledText`. **`ScaledText` needed brush-based styling, could not
express it through the flat `Style` parameter, and grew a separate `public BrushedTextResolver?
BrushResolver { get; set; }` property.** That pair — a flat style plus a side-channel per-cell
colour source — *is* a `StyleDeltaTemplate`, hand-rolled and split across two places. Taking a
template as the parameter retires the property and gives `Icon` and `Image` the capability for free.

### Two scopes, and they must not be conflated

| scope | carrier | meaning |
|---|---|---|
| element / subtree | `UIElement.BlendingMode` → `CompositeParameters.Mode` | how a whole surface composites onto its parent |
| single operation | `StyleDeltaTemplate.Mode` | how one fill/tint/paint's colours combine with the cells it touches |

Both are legitimate — the same way opacity exists on both an element and a brush — but they need
names that do not imply setting one affects the other.

## 5f. `IGlyphFont.Paint` — and the clearest evidence in the tree

```csharp
Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, in Style style);
```

A full `Style` cannot say **"leave the attributes alone"**, so a glyph paint clobbers whatever
`Inverse` state the cells already carried. `TextPresenter` works around it by pre-filling the bounds
with inverse attributes in nocolor mode — and then does the attribute algebra by hand:

```csharp
:505  var tint = CellStyle.Default.WithAttributes(TextAttributes.Inverse);
:525  CellStyle.Default.WithAttributes(inverse ? TextAttributes.None : TextAttributes.Inverse)
:541  .WithAttributes(noColor ? attr.Flags : attr.Flags & ~TextAttributes.Inverse)
:573  baseStyle.Attributes ^ TextAttributes.Inverse
:615  baseStyle.WithAttributes(style.Attributes ^ TextAttributes.Inverse)
```

`:573` and `:615` are the `Xor` mask computed by hand; `:541` is the `Clear` mask. And
`CellStyle.Default.WithAttributes(...)` is `PartialStyle` emulated through `Default`-as-unset — the
same `IsDefault`-as-presence-signal trick §5d found in `TintCells`. A control is doing bit algebra
because the type system offers no word for the operation.

### The presence signal makes the pre-fill unnecessary, not merely survivable

The pre-fill exists because `Paint` is **ink-only**: a FIGlet writes its strokes and never touches
the gaps, so nothing carries selection into them. A delta expresses the alternative without adding a
flag, using per-channel presence:

| background channel | behaviour |
|---|---|
| **present** | fill the glyph's box with it, ink the strokes with foreground — no pre-fill needed |
| **absent** | ink only; gaps show what is underneath — required when stamping a FIGlet over existing content |

So the same parameter that stops the clobbering also decides whether a glyph is a stamp or a box.
Ink-only cannot simply become box-filling unconditionally, which is exactly why a presence-carrying
type is needed rather than a wider `Style`.

## 6. What it retires

| convention | replaced by |
|---|---|
| `Color.Default` means "channel absent" (tint) | presence bit; `Default` recovers its literal meaning |
| `Style.Default` means "blank" (orphan/clear paths) | a `PartialStyle` owning no style channels |
| `Attributes \| x` means "add" | `Clear`/`Xor` per §3 |
| prose comments describing which channels an op touches | the signature |
| a callback returning a whole `Style` it mostly copied | a callback returning only what it owns (§5c) |
| `TintCells`' hardcoded one-channel mask | a caller-supplied template (§5d) |
| `ScaledText.BrushResolver`, a side channel beside `Style` | one template parameter (§5e) |
| `TextPresenter`'s hand-rolled XOR/mask algebra (~5 sites) | the `(Clear, Xor)` pair (§5f) |
| the nocolor inverse pre-fill before a FIGlet paint | a background channel's presence (§5f) |

## 7. Call-site inventory (to be completed before implementation)

Known: `DrawingContext.TintCells`; `CellBuffer` orphan/blank paths (five sites, currently holding
the convention by comment); `Clear`/`ClearCells`/`Fill`/`FillOpaque` and their `Extensions.cs`
wrappers; `SceneCompositor`'s intermediate-mode tint resolution.

The maintainer has flagged additional fill/tint sites a layer or two above `CellBuffer`/
`CellBufferView` for personal review. **The audit is a prerequisite, not a follow-up** — the type's
shape should be validated against real call sites before it is fixed, since a channel set that
fits four sites and fights the fifth is worse than the status quo.

## 8. Decisions needed

1. **Does `Hyperlink` belong?** It is a `Style` channel but semantically unlike the others (identity,
   not appearance). Including it is uniform; excluding it keeps the type about rendering.
2. **`UnderlineStyle` + `UnderlineColor`: independent channels or one unit?** They are separate on
   `Style`, and SGR sets them separately, but "underline" as a user concept is one thing.
3. **Is `Then` needed in v1**, or is single-delta application enough until a caller wants a stack?
4. **Naming**: `PartialStyle` vs `StyleDelta` vs `StyleMask`. "Partial" reads as "incomplete";
   "delta" reads as "change", which is nearer the semantics.
5. **One type or two?** Still open, but now on design grounds rather than layering. An earlier
   revision of this document claimed the split was *forced* because `PartialStyle` could not hold an
   `IBrush` from `Cursorial.Rendering`; moving `IBrush` down (§5c) removes that constraint, so both
   types can live in Rendering and the question is genuinely about whether a resolving template and
   a resolved value want distinct names.
6. **Does moving `IBrush` belong in this proposal or its own?** It is a small mechanical move with a
   large blast radius in `using` directives, and it retires two public delegates — arguably its own
   change, sequenced before this one.
6. **Does anything need per-channel *blending*** (compose the incoming colour over the existing one)
   rather than replacement? The compositor does this today; if a tint ever needs it, presence alone
   is insufficient and a per-channel op enum would be required. Deferring is fine, but knowing now
   whether it is on the roadmap changes the encoding.

## 9. Relationship to the `TextAttributes` decomposition

That proposal decomposed the **producer** side: two rules on different axes no longer clobber each
other, because the store arbitrates per axis. This proposal decomposes the **operation** side: a
fill, tint, or clear declares the channels it owns instead of implying them through sentinel values.

They are the same thesis at two layers, and the lower one is now the only remaining aggregation
point: that document notes SGR already sets and resets each attribute independently, `StyleQuantizer`
drops them one at a time, the cell `Style` is a bitset, and the Drawing markup tier composes
per-flag. Everything below the operation boundary is decomposed. The operation boundary is not.

**Reconcile before implementing:** the Drawing markup tier's existing per-flag composition may
already contain machinery this type should reuse or replace rather than duplicate.
