# Proposal: `PartialStyle` — Channel-Explicit Style Operations

**Status: PROPOSED (2026-08-05).** Produced during the opacity-group compositing work, from a
maintainer observation while auditing fill/tint call sites. No code written.

**Revised 2026-08-06:** built on the decomposed text axes over mask-pair storage (§10.1), full type
definitions and worked call-site conversions (§10–12), and placement settled (§9a:
`Cursorial.Rendering.Media`, all three types). §8 lists the decisions that remain open. Type names
throughout assume the `Style` → `CellStyle` rename.

---

## 0. The problem, and the thesis

`proposal-textattributes-decomposition.md` opens with the report *"you want to add Bold? Well,
better hope Inverse wasn't already there for a good reason"*, and settles it with a thesis worth
quoting exactly: **the store is not the bug — the granularity is.** Nine axes packed into one
attached property forced producers who cared about *different* things to fight over a whole value.
Decomposing to one `UIProperty` per axis made the existing machinery compose correctly.

That fix landed at the **UI property** layer. The same defect survives one layer down, at the
**Drawing operations** layer, and for the same reason: a fill or a tint has to speak in whole
`CellStyle` values, so it cannot say which channels it actually owns.

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
`Cell.Blank`, stamping `CellStyle.Default` — an operation whose intent was "clear the glyph" silently
claiming a channel it did not own, punching an opaque hole through whatever region the cell
belonged to. Fixed (2026-08-05) by preserving the existing style, but the fix is a convention held
by comments in five places rather than something the types express.

**Diagnosis.** Three sentinel conventions (`Color.Default` means absent; `CellStyle.Default` means
blank; OR means "add") each encode partial intent in a value whose real meaning is something else.
They are not individually wrong; they are individually *ambiguous*, and the ambiguity is
unresolvable within the current vocabulary.

## 2. The type

```csharp
/// A channel-explicit style delta: which channels an operation owns, and what it does to them.
/// OPERATIONS ONLY — never stored in a Cell. Nothing in the cell model accepts one; the only
/// way out is ApplyTo, which yields a CellStyle.
public readonly record struct PartialStyle
{
    public CellStyle ApplyTo(in CellStyle current);
    public PartialStyle Then(in PartialStyle next);   // composition; see §5
}
```

*(Sketch only — §10.2 has the full definition. `record struct` rather than plain `struct`: the
fluent setters are `with` expressions, and the properties in §12 assert with `Assert.Equal`, both of
which want the generated value equality. The `readonly`-versus-`ref` argument below is unaffected by
the record-ness.)*

`readonly struct`, deliberately **not** `ref struct`. The invariant that matters is "never lands in
a `Cell`", and that is already structural: no member of `Cell` or `CellStyle` accepts a `PartialStyle`,
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

`CellStyle` itself stays a plain struct with non-nullable channels. It is per-cell hot data, copied per
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
  best home AT THE TIME — `Color` and `Style` both lived in `Cursorial.Output`, and a brush is
  precisely a `Color` source resolving into a style. (Both have since moved: `Color` to
  `Cursorial.Media`, `Style` renamed to `CellStyle`. The conclusion stands regardless.) That
  namespace belongs to
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

## 5c. The delegates that already have delta semantics — ✅ MIGRATED (see §11.7)

> **Superseded in part.** Both delegates did convert, but `GlyphStyleProvider` was RETIRED rather than
> re-typed: once `IBrush` moved into `Cursorial.Rendering`, the callback's whole reason for existing was
> gone and `StyleDeltaTemplate.Resolve` turned out to BE its signature. `BrushedTextResolver` survives and
> now returns a template, once per run. §11.7 records what actually landed.


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

1. **Does `Hyperlink` belong?** It is a `CellStyle` channel but semantically unlike the others (identity,
   not appearance). Including it is uniform; excluding it keeps the type about rendering.
2. **`UnderlineStyle` + `UnderlineColor`: independent channels or one unit?** They are separate on
   `CellStyle`, and SGR sets them separately, but "underline" as a user concept is one thing.
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
drops them one at a time, the cell `CellStyle` is a bitset, and the Drawing markup tier composes
per-flag. Everything below the operation boundary is decomposed. The operation boundary is not.

**Reconcile before implementing:** the Drawing markup tier's existing per-flag composition may
already contain machinery this type should reuse or replace rather than duplicate.

---

## 9a. Where they live — DECIDED

| type | assembly | namespace |
|---|---|---|
| `PartialStyle`, `StyleChannels` | `Cursorial.Rendering` | **`Cursorial.Rendering.Media`** |
| `StyleDeltaTemplate` | `Cursorial.Rendering` | **`Cursorial.Rendering.Media`** |
| `CellStyle` (unchanged) | `Cursorial.Core` | `Cursorial.Output` |

The split between `CellStyle` and `PartialStyle` is the state/operation line, which is the
distinction this proposal opened with: **`CellStyle` is state** — it lives in a cell, so it belongs
with the cell model in Core. **`PartialStyle` is an operation** over that state, so it belongs with
the painting model. They are not separated by accident of dependency; they are separated because
they are different kinds of thing.

Everything else follows:

- **`StyleDeltaTemplate` was pinned to Rendering anyway** — it needs `IBrush` and `Rect`, neither
  visible to Core. Putting the value form beside it keeps the pair in one place.
- **The namespace is already mapped.** `Cursorial.Rendering` declares
  `[assembly: XmlnsDefinition(..., "Cursorial.Rendering.Media")]` and is in `XamlSchemaContext`'s
  seed list, so using these as theme resources needs no further decision — and specifically does not
  require mapping `Cursorial.Output`, which would drag ten VT writers plus `SgrEncoder`,
  `VtOutputSequences` and `IOutputByteSink` into the markup namespace.
- **Every consumer is at Rendering or above**: `CellBuffer.Restyle` (§5d), `IGlyphFont.Paint` (§5f),
  `DrawingContext.TintCells` (Drawing), the presenters (UI). Core needs it nowhere.
- **It keeps the `Core + Rendering` tier self-sufficient** (§5c) — that tier gains brushes, tinting,
  and now the vocabulary to express them.

Note this supersedes the earlier reasoning in §5c, which put `PartialStyle` in `Cursorial.Rendering`
and `StyleDeltaTemplate` in `Cursorial.Drawing` on the assumption that a brush-carrying type could
not descend. `IBrush` has since moved to `Cursorial.Rendering.Media`, which removes that constraint
and lets both live together.

## 10. The types

> **IMPLEMENTED (2026-08-06)** on `feature/styling-redesign`:
> `Cursorial.Rendering/Media/PartialStyle.cs`, `StyleDeltaTemplate.cs`, with the laws of §12 pinned
> in `Cursorial.Rendering.Tests/Media/PartialStyleTests.cs` (610 cases).
>
> **The code is the specification now.** What follows is kept for the REASONING — why the shape is
> what it is — and the listings below are illustrative, not authoritative. Four things changed during
> implementation and the text has not been rewritten around them, because a spec that duplicates
> shipped code drifts silently:
>
> 1. **`StyleChannels` does not exist.** Value channels are nullable; `null` is the only encoding of
>    absent. The presence bitmask read badly at the use site (`d.Channels.HasFlag(…)` versus
>    `d.Foreground is { } fg`), and its only remaining justification was struct size — which §2
>    already disowns for an operation type. Deleting it also simplified `Then` from four `Has()`
>    ternaries to four `??`.
> 2. **No fluent setters for the nullable channels.** With presence and value set in one act,
>    `with { Foreground = c }` is complete, so §10.2's "fluent setters maintain presence, `with` does
>    not" is a rule about a problem that no longer exists. Fluent methods survive only for the
>    ATTRIBUTE axes, where the correct edit is an algebra rather than an assignment.
> 3. **`Clear`/`Xor` are `internal`.** They are storage — the form that composes — not interface.
>    The public reading is `SetAttributes` / `UnsetAttributes` / `ToggledAttributes`, a disjoint and
>    exhaustive partition, so no caller does bit algebra to learn what a delta does.
> 4. **A shape implies the underline flag, structurally.** `ApplyTo` derives it from
>    `UnderlineShape`'s presence, which is what makes a plain `with { UnderlineShape = … }` complete.
>    Removal is carried by the mask and resets the shape — see §12.4.

### 10.0 The listings below are the pre-implementation sketch

Written against the real shapes: `CellStyle(Color Foreground, Color Background, TextAttributes
Attributes, UnderlineStyle UnderlineStyle, Color UnderlineColor, Hyperlink Hyperlink)`,
`TextAttributes` with nine flags, `UnderlineStyle` with five.

### 10.1 Presence, and why the mask shrinks

`PartialStyle` deliberately does NOT mirror `CellStyle`'s flag word. It uses the decomposed axes from
`proposal-textattributes-decomposition.md` §1 — `TextWeight`, `TextStyle`, `UnderlineStyle?` and five
booleans — because when this lands there is little reason for an API consumer to touch `CellStyle`
at all. `CellStyle` is the cell's storage; `PartialStyle` is the vocabulary operations are written
in, and it should carry the better one. `ApplyTo` does the folding.

That choice pays for itself three times over:

- **`Bold | Faint` stops being representable.** They are mutually exclusive on the wire (SGR 1 vs 2,
  both reset by 22) but perfectly expressible as flags. `TextWeight` makes the nonsense state
  unwriteable rather than merely wrong.
- **Enum axes get presence for free.** `TextWeight?` gives leave / Normal / Faint / Bold with no
  extra bit, because "leave" is `null` and `Normal` is a real value distinct from it.
- **The `(Clear, Xor)` mask shrinks from nine flags to five** — `Strikethrough`, `Overline`,
  `Inverse`, `Blink`, `Concealed` — and every one is an axis where TOGGLE is meaningful. Toggling a
  weight is nonsense; toggling `Inverse` is the case that motivated the whole design. The algebra
  stops being a uniform mechanism and becomes the specific tool for the axes that need it.

```csharp
/// <summary>Which value-carrying channels a <see cref="PartialStyle"/> carries.</summary>
[Flags]
public enum StyleChannels : byte
{
    None           = 0,
    Foreground     = 1 << 0,
    Background     = 1 << 1,
    UnderlineColor = 1 << 2,
    Underline      = 1 << 3,   // the SHAPE channel: present + null value == remove the underline
    Hyperlink      = 1 << 4,

    Colors         = Foreground | Background | UnderlineColor,
}
```

Only `Underline` needs a bit here, because its value space already spends `null` on "no underline"
(§1 of the decomposition proposal: *"presence + shape unified"*). A delta needs a third state —
leave / remove / set-to-shape — so the bit says "I have an opinion" and the nullable value says which.

### The decomposed axes are a PROJECTION, not storage

Storage stays the `(Clear, Xor)` pair over the full nine-flag word. `Weight`, `Posture` and the
underline flag are accessors that read and write it. That is not a compromise — it is strictly
better than separate nullable fields:

| | separate fields | projection onto `(Clear, Xor)` |
|---|---|---|
| struct size | + ~6 bytes of nullable enums | two `TextAttributes` |
| composition | needs its own rule per axis | the ONE law already proved |
| `Bold \| Faint` | unrepresentable | unrepresentable *through the API* |

`Weighted(TextWeight.Bold)` is `Clear = Bold|Faint, Xor = Bold` — force Bold on, Faint off — which is
exactly what SGR means, since 22 resets both. And composition needs no special case:

```
Weighted(Bold).Then(Weighted(Faint))  ->  C = B|F,  X = (B & ~(B|F)) ^ F = F      later wins
Weighted(Bold).Then(Toggle(Inverse))  ->  C = B|F,  X = B ^ I                     axes accumulate
```

So the decomposition buys the vocabulary and the illegal-state block without buying a second
algebra. The mask stays the single mechanism; the API stops exposing it where it does not belong.

### 10.2 `PartialStyle`

```csharp
/// <summary>
/// A style DELTA: the channels it carries, and what to do to them. Applying it to a
/// <see cref="CellStyle"/> yields a new one; channels it does not carry pass through.
/// <c>default</c> is the identity — applying it returns the base unchanged.
/// </summary>
/// <remarks>
/// Attributes are a <c>(Clear, Xor)</c> mask pair rather than a value, which is what gives three
/// states plus a fourth operation: <c>result = (base &amp; ~Clear) ^ Xor</c>.
/// <list type="table">
///   <item><term>set on</term>   <description><c>Clear = f, Xor = f</c></description></item>
///   <item><term>set off</term>  <description><c>Clear = f, Xor = 0</c></description></item>
///   <item><term>toggle</term>   <description><c>Clear = 0, Xor = f</c></description></item>
///   <item><term>leave</term>    <description><c>Clear = 0, Xor = 0</c></description></item>
/// </list>
/// Toggle is the one the current code cannot express, and the one selection-on-inverse-text needs.
/// </remarks>
public readonly record struct PartialStyle
{
    public StyleChannels Channels { get; init; }

    public Color     Foreground     { get; init; }
    public Color     Background     { get; init; }
    public Color     UnderlineColor { get; init; }
    public Hyperlink Hyperlink      { get; init; }

    /// <summary>The underline SHAPE. Meaningful only with <see cref="StyleChannels.Underline"/>
    /// present, where <see langword="null"/> REMOVES the underline.</summary>
    public UnderlineStyle? Underline { get; init; }

    // ---- storage: one mask pair over the whole flag word ----

    /// <summary>Flags forced to a definite state (off alone, or on together with <see cref="Xor"/>).</summary>
    public TextAttributes Clear { get; init; }
    /// <summary>Flags inverted after clearing. Set alone to TOGGLE.</summary>
    public TextAttributes Xor   { get; init; }

    // ---- decomposed axes: PROJECTIONS onto the pair above, never separate state ----

    private const TextAttributes WeightMask = TextAttributes.Bold | TextAttributes.Faint;

    /// <summary>The weight this delta imposes, or <see langword="null"/> if it leaves weight alone.</summary>
    public TextWeight? Weight => (Clear & WeightMask) != WeightMask
                                     ? null
                                     : (Xor & TextAttributes.Bold)  != 0 ? TextWeight.Bold
                                     : (Xor & TextAttributes.Faint) != 0 ? TextWeight.Faint
                                     : TextWeight.Normal;

    /// <summary>The posture this delta imposes, or <see langword="null"/> if it leaves it alone.</summary>
    public TextStyle? Posture => (Clear & TextAttributes.Italic) is 0
                                     ? null
                                     : (Xor & TextAttributes.Italic) != 0 ? TextStyle.Italic : TextStyle.Normal;

    /// <summary>How this delta's colours combine with the base's. <see langword="null"/> replaces.</summary>
    public IBlendingMode? Mode { get; init; }

    /// <summary>
    /// True when this delta is inert in EVERY context — applying it returns the base unchanged, and
    /// composing it changes nothing about what follows.
    /// </summary>
    /// <remarks>
    /// <see cref="Mode"/> is included even though it cannot affect <see cref="ApplyTo"/> on its own
    /// (it is read only inside the per-channel colour combine, which no absent channel reaches). It
    /// matters because <see cref="Then"/> PROPAGATES it: a delta carrying only a mode is a blend
    /// carrier that changes how subsequent deltas' colours land.
    /// <code>
    /// var dim = default(PartialStyle).WithBlending(BlendingModes.Multiply);
    /// dim.ApplyTo(s) == s                    // inert applied directly
    /// dim.Then(Foreground(c)) != Foreground(c)   // NOT inert composed
    /// </code>
    /// Excluding it would make this property sound only for the direct-apply fast path and a trap
    /// for the obvious next use — pruning no-op deltas out of a chain, which would silently drop the
    /// blend. The cost of including it is one skipped fast path for a mode-only delta, which is rare
    /// and cheap; the cost of excluding it is a silent wrong render.
    /// </remarks>
    // THE RULE, so the next field added does not repeat the mistake:
    //   every field NOT governed by `Channels` must appear here explicitly.
    // Bit-governed state (the colours, the underline shape) is covered for free by the
    // `Channels is None` test. Everything else has to be remembered — which is exactly how `Mode`
    // was missed. Today the non-bit-governed set is precisely { Clear, Xor, Mode }.
    public bool IsIdentity =>
        Channels is StyleChannels.None && Clear is 0 && Xor is 0 && Mode is null;

    /// <summary>The axes that are genuinely independent booleans — the only ones the flag-level
    /// <see cref="Set"/>/<see cref="Clear(TextAttributes)"/>/<see cref="Toggle"/> factories accept.
    /// Bold, Faint, Italic and Underline have their own axes and are rejected there.</summary>
    public const TextAttributes Booleans =
        TextAttributes.Strikethrough | TextAttributes.Overline | TextAttributes.Inverse |
        TextAttributes.Blink | TextAttributes.Hidden;

    // ---- construction: one factory per channel, composable by `with` ----

    public static PartialStyle Foreground(Color c) =>
        new() { Channels = StyleChannels.Foreground, Foreground = c };

    public static PartialStyle Background(Color c) =>
        new() { Channels = StyleChannels.Background, Background = c };

    /// <summary>Impose a weight — forces Bold ON and Faint OFF, or vice versa, or both off for Normal.
    /// The shared SGR 22 reset is why one mask covers both.</summary>
    public static PartialStyle Weighted(TextWeight w) => new()
    {
        Clear = WeightMask,
        Xor   = w switch { TextWeight.Bold => TextAttributes.Bold,
                           TextWeight.Faint => TextAttributes.Faint,
                           _ => 0 },
    };

    public static PartialStyle Postured(TextStyle p) => new()
    {
        Clear = TextAttributes.Italic,
        Xor   = p is TextStyle.Italic ? TextAttributes.Italic : 0,
    };

    /// <summary>Force <paramref name="flags"/> ON. <paramref name="flags"/> must be within <see cref="Booleans"/>.</summary>
    public static PartialStyle Set(TextAttributes flags) =>
        new() { Clear = Require(flags), Xor = flags };

    /// <summary>Force <paramref name="flags"/> OFF.</summary>
    public static PartialStyle Clear(TextAttributes flags) =>
        new() { Clear = Require(flags) };

    /// <summary>INVERT <paramref name="flags"/> — on becomes off and off becomes on.</summary>
    public static PartialStyle Toggle(TextAttributes flags) =>
        new() { Xor = Require(flags) };

    // Bold/Faint/Italic/Underline reach the mask only by mistake — they have their own axes, and
    // routing them through the flag word is how `Bold | Faint` gets written.
    private static TextAttributes Require(TextAttributes flags) =>
        (flags & ~Booleans) is 0
            ? flags
            : throw new ArgumentOutOfRangeException(
                  nameof(flags),
                  $"{flags & ~Booleans} has its own axis; set Weight / Posture / Underline instead.");

    // ---- fluent setters: these MAINTAIN the presence mask, `with` does not ----
    //
    // Every mutation goes through one of these. A raw `with { Background = c }` would set the value
    // and leave the channel absent, so the delta would silently ignore it — a type that exists to
    // track presence must never make presence the caller's job. If C# had a way to seal `with` on a
    // record struct this would be enforced rather than merely provided.

    public PartialStyle WithForeground(Color c) =>
        this with { Channels = Channels | StyleChannels.Foreground, Foreground = c };

    public PartialStyle WithBackground(Color c) =>
        this with { Channels = Channels | StyleChannels.Background, Background = c };

    /// <summary>Underline in <paramref name="style"/>, coloured <paramref name="color"/>. Sets the
    /// shape channel AND forces the underline flag on — the shape is meaningless without it, so the
    /// factory does both rather than leaving the caller to remember the second half.</summary>
    public PartialStyle WithUnderline(UnderlineStyle style, Color color) =>
        this with { Channels  = Channels | StyleChannels.Underline | StyleChannels.UnderlineColor,
                    Underline = style,
                    UnderlineColor = color,
                    Clear = Clear | TextAttributes.Underline,
                    Xor   = Xor   | TextAttributes.Underline };

    /// <summary>Remove any underline: clears the flag and the shape together.</summary>
    public PartialStyle WithoutUnderline() =>
        this with { Channels  = Channels | StyleChannels.Underline,
                    Underline = null,
                    Clear = Clear | TextAttributes.Underline,
                    Xor   = Xor   & ~TextAttributes.Underline };

    public PartialStyle WithHyperlink(Hyperlink link) =>
        this with { Channels = Channels | StyleChannels.Hyperlink, Hyperlink = link };

    public PartialStyle WithBlending(IBlendingMode? mode) => this with { Mode = mode };

    /// <summary>Force <paramref name="flags"/> ON in addition to whatever this delta already does.</summary>
    public PartialStyle Setting(TextAttributes flags) => Then(Set(flags));

    /// <summary>Force <paramref name="flags"/> OFF in addition to whatever this delta already does.</summary>
    public PartialStyle Clearing(TextAttributes flags) => Then(Clear(flags));

    /// <summary>INVERT <paramref name="flags"/> in addition to whatever this delta already does.</summary>
    public PartialStyle Toggling(TextAttributes flags) => Then(Toggle(flags));

    // ---- application ----

    public CellStyle ApplyTo(in CellStyle b)
    {
        if (IsIdentity) return b;

        return b with
        {
            Foreground     = Has(StyleChannels.Foreground)     ? Combine(Foreground,     b.Foreground)     : b.Foreground,
            Background     = Has(StyleChannels.Background)     ? Combine(Background,     b.Background)     : b.Background,
            UnderlineColor = Has(StyleChannels.UnderlineColor) ? Combine(UnderlineColor, b.UnderlineColor) : b.UnderlineColor,
            Hyperlink      = Has(StyleChannels.Hyperlink) ? Hyperlink : b.Hyperlink,
            UnderlineStyle = Has(StyleChannels.Underline) ? Underline ?? b.UnderlineStyle : b.UnderlineStyle,
            Attributes     = (b.Attributes & ~Clear) ^ Xor,
        };

        Color Combine(Color source, Color backdrop) =>
            Mode is null ? source : Color.Composite(source, backdrop, Mode);
    }

    private bool Has(StyleChannels c) => (Channels & c) != 0;

    /// <summary>
    /// This delta, then <paramref name="next"/> — one delta equivalent to applying both in order.
    /// The attribute algebra composes exactly: <c>Clear = C₁ | C₂</c>, <c>Xor = (X₁ &amp; ~C₂) ^ X₂</c>.
    /// </summary>
    public PartialStyle Then(in PartialStyle next) => new()
    {
        Channels       = Channels | next.Channels,
        Foreground     = next.Has(StyleChannels.Foreground)     ? next.Foreground     : Foreground,
        Background     = next.Has(StyleChannels.Background)     ? next.Background     : Background,
        UnderlineColor = next.Has(StyleChannels.UnderlineColor) ? next.UnderlineColor : UnderlineColor,
        Hyperlink      = next.Has(StyleChannels.Hyperlink)       ? next.Hyperlink      : Hyperlink,
        Underline      = next.Has(StyleChannels.Underline)       ? next.Underline      : Underline,
        Clear          = Clear | next.Clear,
        Xor            = (Xor & ~next.Clear) ^ next.Xor,
        Mode           = next.Mode ?? Mode,
    };
}
```

`Then` is worth stating as a law, because it is testable and it is what makes the type composable
rather than merely convenient:

> `a.Then(b).ApplyTo(s)` ≡ `b.ApplyTo(a.ApplyTo(s))`, for every `a`, `b`, `s`.

### 10.3 `StyleDeltaTemplate`

The same shape with `IBrush` where `PartialStyle` has `Color` — the unresolved form, for callers
that paint over a region and need per-cell colour.

```csharp
/// <summary>
/// A <see cref="PartialStyle"/> whose colour channels are BRUSHES, resolved per cell. The form an
/// operation is authored in; <see cref="Resolve"/> produces the value form for a given cell.
/// </summary>
public readonly record struct StyleDeltaTemplate
{
    public IBrush?    Foreground     { get; init; }
    public IBrush?    Background     { get; init; }
    public IBrush?    UnderlineColor { get; init; }
    public Hyperlink? Hyperlink      { get; init; }

    /// <summary>Present iff <see cref="HasUnderlineOpinion"/>; <see langword="null"/> removes it.</summary>
    public UnderlineStyle? Underline    { get; init; }
    public bool HasUnderlineOpinion     { get; init; }

    public TextAttributes Clear { get; init; }
    public TextAttributes Xor   { get; init; }
    public IBlendingMode? Mode  { get; init; }

    /// <summary>
    /// True when every present brush is position-independent, so <see cref="Resolve"/> returns the
    /// same value for every cell and a fill loop can hoist it. The common case.
    /// </summary>
    public bool IsUniform =>
        Foreground is null or SolidColorBrush &&
        Background is null or SolidColorBrush &&
        UnderlineColor is null or SolidColorBrush;

    public PartialStyle Resolve(int column, int row, in Rect bounds)
    {
        var channels = StyleChannels.None;
        if (Foreground     is not null) channels |= StyleChannels.Foreground;
        if (Background     is not null) channels |= StyleChannels.Background;
        if (UnderlineColor is not null) channels |= StyleChannels.UnderlineColor;
        if (Hyperlink      is not null) channels |= StyleChannels.Hyperlink;
        if (HasUnderlineOpinion)        channels |= StyleChannels.Underline;

        return new PartialStyle
        {
            Channels       = channels,
            Foreground     = Foreground?.ColorAt(column, row, bounds)     ?? default,
            Background     = Background?.ColorAt(column, row, bounds)     ?? default,
            UnderlineColor = UnderlineColor?.ColorAt(column, row, bounds) ?? default,
            Underline      = Underline,
            Hyperlink      = Hyperlink ?? default,
            Clear          = Clear,
            Xor            = Xor,
            Mode           = Mode,
        };
    }
}
```

## 11. Worked examples — what each call site becomes

### 11.1 Selection tint (`DrawingContext.TintCells`) — ✅ MIGRATED

Before, with the mask hardcoded and no way to express "invert":

```csharp
var tinted = cell.Style with { Attributes = (cell.Style.Attributes & ~TextAttributes.Inverse) | style.Attributes };
if (!style.Background.IsDefault)
    tinted = tinted with { Background = style.Background };
```

After, the whole body — the operation is the caller's, so the method has none of its own:

```csharp
var cell = _surface[sceneColumn, sceneRow];
_surface[sceneColumn, sceneRow] = cell with { Style = style.ApplyTo(cell.Style) };
```

`DrawingContext.TintCells` and its `RenderContext` passthrough now take a `PartialStyle`; the one
caller (`TextPresenter.DrawFaceLine`, the glyph-face selection highlight) states both legs:

```csharp
// nocolor / no selection brush: FORCE Inverse, in whichever direction the run needs
tint = inverse ? PartialStyle.WithCleared(TextAttributes.Inverse)
               : PartialStyle.WithSet(TextAttributes.Inverse);

// colour: paint the selection background AND clear Inverse — which the old spelling did
// invisibly, via the hardcoded `& ~Inverse`, while the caller passed no attributes at all
tint = PartialStyle.WithBackground(color).Clearing(TextAttributes.Inverse);
```

Three things the migration had to be careful about, each now pinned by a test:

1. **The `& ~Inverse` ran on BOTH paths**, including the colour one where `style.Attributes` was
   `None` — so the clear was invisible at the call site. A bare `WithBackground(colour)` leaves
   selected inverse text inverted under its new background.
2. **One `CellStyle` spelling meant two opposite operations.** `CellStyle.Default.WithAttributes(
   inverse ? None : Inverse)` was a *clear* when the run was already inverse and a *set* when it was
   not — legible only once you knew `TintCells` cleared the flag first. The two are now separate
   factories, and say so.
3. **The `Background.IsDefault` guard is load-bearing and survives, at the call site.** `Brushes.Default`
   is a legal `TextBox.SelectionBrush` and samples to `Color.Default` everywhere, so "the brush stated
   no background" is reachable. To a `PartialStyle` a present-but-default background is an ordinary
   opinion — which is the point (§5d) — so the caller must now decide, rather than have the sentinel
   decide for it.

The `Toggle` this section originally reached for is available (`WithToggled`) and covered by
`DrawingContextTintTests.TintCells_TogglesPerCell`, but the selection call site does not need it:
`inverse` is a whole-run property the presenter already knows, so a forced set/clear is both
sufficient and more predictable than a per-cell flip.

### 11.2 `TextPresenter`'s hand-rolled algebra (§5f, five sites)

```csharp
// :573   baseStyle.Attributes ^ TextAttributes.Inverse
PartialStyle.Toggle(TextAttributes.Inverse)

// :541   .WithAttributes(noColor ? attr.Flags : attr.Flags & ~TextAttributes.Inverse)
noColor ? PartialStyle.Set(attr.Flags) : PartialStyle.Set(attr.Flags).Clearing(TextAttributes.Inverse)

// :505   CellStyle.Default.WithAttributes(TextAttributes.Inverse)   ← "delta" faked via Default
PartialStyle.Set(TextAttributes.Inverse)
```

The third is the tell: `CellStyle.Default.WithAttributes(...)` only works because `Default` reads as
"unset" for the colour channels. It is a `PartialStyle` spelled in a type that cannot say so.

### 11.3 Glyph paint: stamp versus box (§5f) — ⏳ NOT YET

The brushed `IGlyphFont.Paint` overload has migrated (§11.7), but this section is about the FLAT overload,
which still takes a whole `CellStyle`. Presence-decides-stamp-vs-box is a separate step.


Presence does the work — no new flag, no second overload:

```csharp
// stamp: ink the strokes, gaps show whatever is underneath (a FIGlet over existing content)
face.Paint(buffer, column, row, text, PartialStyle.Foreground(fg));

// box: fill the glyph's box first, then ink — no pre-fill needed by the caller
face.Paint(buffer, column, row, text, PartialStyle.Foreground(fg).WithBackground(bg));
```

The second is what the nocolor inverse pre-fill exists to fake today.

### 11.4 The access key (`proposal-unified-text-path.md` §3)

Two channels and one attribute, everything else inherited — and it is a *value*, so the formatter
can carry it through layout and apply it at paint without a closure:

```csharp
var cue = default(PartialStyle).WithUnderline(UnderlineStyle.Single, indicator);
```

Note what is *not* written: `Set(TextAttributes.Underline)`. That call would throw — `Underline` has
its own axis, so `Require` rejects it — and `WithUnderline` already forces the flag on, because a
shape without the flag is meaningless. The API refuses the half-expressed version of the operation.

### 11.5 Composition, and why `Then` matters

A run inside a selection inside a disabled panel — three independent deltas, applied once:

```csharp
var effective = disabledDim.Then(selectionTint).Then(runStyle);
foreach (var cell in region)
    surface[cell] = effective.ApplyTo(surface[cell].Style);
```

Attribute algebra composes correctly through all three: a later `Set`/`Clear` wins over an earlier
`Toggle` on the same flag (its `Clear` bit masks the earlier `Xor`), while toggles on *different*
flags accumulate. That is the `Clear = C₁ | C₂`, `Xor = (X₁ & ~C₂) ^ X₂` law, and it is exactly the
case the hand-rolled sites get wrong when they are composed by accident.

### 11.6 Deriving a delta from an element's value sources

The `ComposeAttributes`-shaped path: a channel is present iff the element actually set it, which the
property system already knows.

```csharp
public static StyleDeltaTemplate FromElement(UIElement e) => new()
{
    Foreground     = IsSet(e, TextElement.ForegroundProperty)     ? TextElement.GetForeground(e)     : null,
    Background     = IsSet(e, TextElement.BackgroundProperty)     ? TextElement.GetBackground(e)     : null,
    UnderlineColor = IsSet(e, TextElement.UnderlineColorProperty) ? TextElement.GetUnderlineColor(e) : null,
};

// ...then fold in the axes the element has an opinion about, each through its own factory so the
// mask is never assembled by hand:
if (IsSet(e, TextElement.WeightProperty))  delta = delta.Then(PartialStyle.Weighted(TextElement.GetWeight(e)));
if (IsSet(e, TextElement.StyleProperty))   delta = delta.Then(PartialStyle.Postured(TextElement.GetStyle(e)));
if (IsSet(e, TextElement.InverseProperty)) delta = delta.Then(TextElement.GetInverse(e)
                                                                  ? PartialStyle.Set(TextAttributes.Inverse)
                                                                  : PartialStyle.Clear(TextAttributes.Inverse));

// "the element has an opinion" == the value did not come from the default or from inheritance
static bool IsSet(UIElement e, StyledProperty p) =>
    e.GetValueSource(p) is not { Kind: ValueSourceKind.Default or ValueSourceKind.Inherited, IsCurrentValue: false };
```

Only non-solid brushes cannot be auto-populated this way, because their colour depends on the cell
being painted — which is precisely the reason the template form exists.

### 11.7 The per-cell glyph styling chain — ✅ MIGRATED

Tracked as two steps (the `BrushedTextResolver` return type, and the `GlyphStyleProvider` return type).
**They were done as ONE**, because they are one chain: `FormattedText` built the provider *from* the
resolver, so migrating either alone would have lost information at exactly that adapter — a resolver
returning a delta feeding a provider expected to return a whole style, or the reverse. There was no
intermediate state worth having.

The step also came out differently from the sketch above, in a way worth recording.

**`GlyphStyleProvider` is gone, not re-typed.** The plan was to change its return type to `PartialStyle`.
That was done first, and it worked — and then it was thrown away, because the delegate itself was the
artefact of a constraint that no longer holds:

```csharp
// before: a callback, because Cursorial.Rendering could not name IBrush, so the CALLER had to sample
public delegate CellStyle GlyphStyleProvider(int column, int row);
Size Paint(…, ReadOnlySpan<char> text, GlyphStyleProvider styleProvider);

// after: the value form. StyleDeltaTemplate.Resolve IS that signature, plus the sampling bounds the
// closure was capturing, plus IsUniform
Size Paint(…, ReadOnlySpan<char> text, in CellStyle baseStyle, in StyleDeltaTemplate delta, in Rect bounds);
```

Three things follow from passing the value instead of a closure over it:

1. **`IsUniform` becomes readable.** A delegate is opaque, so every painted cell had to call it even for a
   solid colour. `MonospaceFont` and `FigletFont` now resolve a uniform template ONCE and take the same
   path as a flat style. Pinned by `Monospace_ResolvesAUniformTemplateOnce` (one brush sample for a
   six-cell run) against `Monospace_ResolvesANonUniformTemplatePerCell` (six).
2. **The base style became a parameter.** It had to: a delta with no base has nothing for its absent
   channels to fall through to. `ShadowedFont` had been passing `default` for it — the provider overload
   had no base to pass — so its shadow pass silently lost the run's underline shape. That was the one
   pre-existing defect this step fixed, and it is what the red test caught.
3. **The bounds became a parameter too, and are deliberately NOT the painted footprint.** They are the
   brush's coordinate space, which belongs to the scope the brush was declared at — a block, the document,
   or an inline run's reading-order strip — and defaulting them to the glyphs being painted would restart
   every gradient at each run boundary.

**`BrushedTextResolver` moved from per-CELL to per-RUN.** Everything it decides — which brush wins, at what
scope, whether the run's foreground was inherited, which inherited attributes merge in — is a property of
the run; only the sampling was per cell, and a template samples itself. It now returns
`BrushedTextStyle(StyleDeltaTemplate Delta, Rect Bounds)`:

```csharp
// the run declares its own brush:      new StyleDeltaTemplate { Foreground = bs.Foreground }, at its scope
// no document brush:                   the IDENTITY — where it used to rebuild ctx.BaseStyle to say "no change"
// document brush, foreground inherited: new StyleDeltaTemplate { Foreground = documentBrush }, over the block
// document brush, foreground its own:   the IDENTITY again
```

The two identity legs are the payoff. A `CellStyle` return could only spell "no change" as a copy of the
base — which the painter then applied on top of the value it was copied from, per cell.

Three things this migration had to be careful about, each now pinned by a test:

1. **`AddAttributes` is an OR, and the per-axis factories cannot express one.** `Weighted(Bold)` forces
   Faint OFF — they share the SGR 22 reset — so decomposing the inherited-attribute leg into axes would
   strip a run's own Faint under an inherited Bold. This needed a new factory, `PartialStyle.WithAdded`
   (and `StyleDeltaTemplate.Adding`): the flag-word union, the only one that accepts the axis-owning flags.
   It is not a hole in `WithSet`'s guard — that guard catches routing an axis through the boolean
   factories by ACCIDENT, and here the union is the intent. Verified by mutation: making it a replace
   fails three tests, including `BaseAttributes_DoNotClearTheRunsOppositeWeightFlag`.

   > **Retracted.** `PartialStyle.WithAdded`, `PartialStyle.Adding` and `StyleDeltaTemplate.Adding` have
   > since been REMOVED; the paragraph above is kept only so the reversal is legible. It concedes its own
   > defeat in the phrase "strip a run's own Faint": the union's one distinguishing capability is reaching
   > `Bold | Faint`, and that is not a state the wire has. The encoder emits `ESC[1m` to reach it from a
   > Faint predecessor and `ESC[2m` from a Bold one — same destination, different bytes, whichever arrived
   > last wins — while `PartialStyle.Weight` reports plain `Bold` for it either way, so the accessor and
   > the frame disagree in silence. Nor could a guard have been bolted on: composition is exact, so
   > `WithAdded(Bold).Adding(Faint)` equals `WithAdded(Bold | Faint)` and any per-call check is evaded by
   > splitting the call. The inherited-attribute leg now folds its flag word one axis at a time —
   > `Weighing` for weight (Bold wins a word carrying both, deterministically), `Posturing` for posture,
   > `Setting` for the genuine booleans, and the run's own shape re-stated to carry the underline flag.
   > `BaseAttributes_DoNotClearTheRunsOppositeWeightFlag` was inverted into
   > `BaseAttributes_Bold_ImposesTheWeight_ClearingTheRunsFaint`, and
   > `EveryPublicFlagWordEntryPoint_RejectsTheAxisOwningFlags` now states the guard over the whole public
   > surface by reflection, so a future unguarded sibling fails a test rather than a terminal.
2. **The inline-vs-block scope distinction had to survive a per-cell → per-run reshaping.** Inline sampling
   was `ColorAt(LogicalColumn, 0, Rect(0, 0, ScopeWidth, 1))` — a remapped COORDINATE — and a per-run
   resolver hands back a rect, not a coordinate. It is expressible because the remap is a constant offset
   within a line-piece: the painter now supplies `BrushedTextContext.InlineScope`, a 1-row rect REBASED so
   that sampling at the cell's own `(column, row)` yields its logical offset. One sampling convention, both
   scopes. Pinned by `FormattedText_InlineScopeIsWrapInvariant` (the ramp continues across a wrap instead
   of restarting) and `RunBrush_SamplesAgainstItsDeclarationScope`.
3. **`BrushedTextContext.BaseStyle` stays.** The resolver still READS it — `fg.IsDefault || fg ==
   documentForeground` is the inherited-foreground test — it merely stops returning it. The base played two
   roles and only the second one went away.

No `IsDefault`-as-sentinel was lost. The one occurrence in this chain is that inherited-foreground test,
which is an input the resolver reads, not an encoding of absence in what it returns; and a brush that
samples to `Color.Default` still lands as a real foreground opinion, because the template carries the
BRUSH and only `null` means absent.

## 12. What the type has to prove

**All of these now exist** as `PartialStyleTests`. Two were only discovered BY writing them, and both
are recorded here because they are the kind of defect inspection does not find:

- **§12.4a — `Then` resurrected a removed underline.** `next.UnderlineShape ?? UnderlineShape` treats
  a null shape as "no opinion", but `WithoutUnderline` also has a null shape, meaning "no shape". So
  `WithUnderline(Double).Then(WithoutUnderline())` kept the `Double`, and shape-implies-flag then
  turned the underline back ON. That is exactly the sentinel-doing-double-duty defect §1 opens by
  criticising, reintroduced on one channel. Resolved by `HasUnderlineOpinion`, which disambiguates
  from the mask — where removal is actually recorded.
- **§12.4b — removal left a stale shape.** Set-then-remove applied in sequence leaves the SET shape;
  the composed equivalent leaves the BASE's. Invisible in rendering — a shape means nothing with the
  flag off — but it breaks the composition law on a field nothing reads. Removal now resets the shape.

Neither is reachable through the public API by accident, and neither would survive a hand-picked test
set: both were found by asserting the law over ALL 196 ordered pairs of a 14-delta sample.

Testable properties, in the order they should be written:

1. **Identity.** `default(PartialStyle).ApplyTo(s) == s`, for every `s` — including styles with every
   attribute set and non-default colours.
2. **Composition.** `a.Then(b).ApplyTo(s) == b.ApplyTo(a.ApplyTo(s))`, exhaustively over the four
   operations × the five boolean axes, over `Weighted`/`Postured`/`WithUnderline` in both orders,
   and over channel-present/absent combinations.
3. **Toggle is an involution.** `Toggle(f).Then(Toggle(f))` is the identity on `f`.
4. **Clear beats an earlier Xor.** `Toggle(f).Then(Set(f))` forces on; `Toggle(f).Then(Clear(f))`
   forces off — regardless of the base.
5. **Absence is not `Default`.** A delta with no `Foreground` leaves a non-default foreground alone —
   the bug the `CellStyle.Default.WithAttributes(...)` idiom is one refactor away from introducing.
6. **`Resolve` is loop-invariant when `IsUniform`.** Same input cell range, one call versus N, equal
   results — the property that lets fill loops hoist it.
7. **Every fluent setter maintains presence.** For each `With*`, the resulting `Channels` contains
   the bit it set. Cheap to write exhaustively, and it is the invariant a stray `with { … }` breaks
   — see §10.2. Worth a source-level lint too, since the compiler cannot forbid the raw form.
8. **The axes reject the flag word.** `Set`/`Clear`/`Toggle` throw for `Bold`, `Faint`, `Italic` and
   `Underline`; `Weighted`/`Postured`/`WithUnderline` are the only routes to them. This is the test
   that keeps the decomposed vocabulary from decaying back into flags one call site at a time.
9. **Weight round-trips.** `Weighted(w).Weight == w` for all three values, and `Weight is null` for
   any delta that did not set it — the projection is lossless in both directions.
10. **`IsIdentity` is inert under composition, not just application.** A mode-only delta reports
    `false`, and `a.Then(b)` for identity `a` equals `b` for every `b` — including deltas with colour
    channels, which is the case a mode-only carrier would otherwise change. This is the property that
    makes it safe to prune deltas from a chain.
11. **The underline flag and the underline shape never disagree.** `StyleChannels.Underline` present
    ⟺ `Underline` in `Clear`, and `Underline` non-null ⟺ `Underline` in `Xor` — through every
    factory and through `Then` in both orders.

    This holds only because `WithUnderline`/`WithoutUnderline` are the sole route to either half
    (`Require` blocks `Set`/`Clear`/`Toggle(TextAttributes.Underline)`, so the flag cannot be moved
    on its own), which makes it a COROLLARY of property 7 rather than an independent guarantee. A
    raw `with { Underline = null }` over a `WithUnderline` delta produces flag-on-with-no-shape.
    Worth pinning separately anyway: it is the invariant a future factory would most plausibly break
    by setting one half.
