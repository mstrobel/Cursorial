# Proposal: Per-Axis Text-Attribute Properties (the `TextAttributes` Decomposition)

**Status: PROPOSAL — Q1/Q2/Q4 decided 2026-07-13 (§8); Q3 carries a recommendation awaiting
owner confirmation.** Produced 2026-07-13 by the panel process recorded in §9; the adversarial
judgment lives in `judgment-textattributes-decomposition.md`. Nothing here is implemented.

---

## 0. The problem, and the thesis

The motivating report (2026-07-12, during the precedence-lattice work): *"TextAttributes is
already a problem because it groups several display characteristics together; you want to add
Bold? Well, better hope Inverse wasn't already there for a good reason."*

`TextElement.TextAttributesProperty` packs nine independent axes (Bold, Faint, Italic, Underline,
Blink, Inverse, Hidden, Strikethrough, Overline) into one inherited attached property. The value
store arbitrates **whole values per property** — the model the completed lattice (PD26/PD27) was
just built against, and the correct one — so producers that care about *different axes* are forced
to fight: a theme's `:focus-visible → Inverse` rule and an app's `Bold` rule cannot coexist; the
winner's whole value replaces the loser's, and inheritance shadowing (nearest whole-value
contributor) clobbers at the same granularity. Meanwhile every layer BELOW the property is already
decomposed: SGR sets and resets each attribute independently (1/2/3/4/5/7/8/9/53, resets
22/23/24/25/27/28/29/55 — `SgrEncoder.cs:147-197`), `StyleQuantizer` drops attributes one at a
time, the cell `Style` is a bitset, and the Drawing markup tier composes per-flag. The UI property
is the only aggregation point in the stack.

**The thesis: the store is not the bug — the granularity is.** Give each axis its own
`UIProperty` and the existing machinery *is* per-flag composition: two producers on different axes
never meet in arbitration; "turn off an inherited flag" is an ordinary `false` contribution that
shadows exactly one axis; `ClearValue` restores inheritance; conditional rules pierce template
values per axis exactly as PD26 intends. **Zero new engine machinery.** `Output.TextAttributes`
(shipped Core; the wire/cell vocabulary) is untouched — renderers fold the per-axis effective
values back into the bitset at paint.

All three independently-authored panel proposals converged on this structure; the judgment
(§9) rated the convergence itself as strong evidence. What follows is the synthesis: the winning
proposal's architecture with the judge's mandated fixes and grafts applied.

---

## 1. Property surface

All properties live on `TextElement` (namespace `Cursorial.UI.Controls`), registered as
`AttachedProperty<T>` with `inherits: true` and `AffectsRender` on the **global effects lane** —
mechanically identical to today's `TextAttributesProperty` (`TextElement.cs:30-38`; inherited
attached properties fan to arbitrary descendant types, so per-owner-type effects don't work). No
`AddOwner` in v1 (parity with today; the control-matrix C119 claim of AddOwner'ing is pre-existing
doc/code drift — fixed in docs, not by adding code).

```csharp
/// <summary>The text weight axis. One axis, three values — Bold and Faint share the terminal's
/// SGR 22 reset, so they are alternatives on a single dial, not independent flags.</summary>
public enum TextWeight : byte { Normal = 0, Faint, Bold }
```

| Property | Type | Default | SGR fold | Precedent |
|---|---|---|---|---|
| `TextWeightProperty` | `TextWeight` | `Normal` | Bold→1, Faint→2, Normal→neither (reset 22) | the axis of WPF `FontWeight` / CSS `font-weight`, not the type (§8 Q1) |
| `ItalicProperty` | `bool` | `false` | 3 / 23 | WPF `FontStyle` minus the inexpressible `Oblique` |
| `UnderlineProperty` | `UnderlineStyle?` | `null` | 4 / 4:n / 24 | presence + shape unified; `null` = no underline (§8 Q2) |
| `UnderlineBrushProperty` | `IBrush?` | `null` | 58 / 59 | CSS `text-decoration-color`; **phase-late, demand-gated** (§8 Q2) |
| `StrikethroughProperty` | `bool` | `false` | 9 / 29 | CSS `line-through` |
| `OverlineProperty` | `bool` | `false` | 53 / 55 | CSS `overline` |
| `InverseProperty` | `bool` | `false` | 7 / 27 | terminal-native — the reverse-video theming axis |
| `BlinkProperty` | `bool` | `false` | 5 / 25 | terminal-native |
| `ConcealedProperty` | `bool` | `false` | 8 / 28 (`TextAttributes.Hidden`) | terminal-native; ANSI "conceal" |

Design arguments per row:

- **`TextWeight` — the Bold/Faint fold.** The shared SGR 22 reset is the wire's own testimony that
  these are one axis: the encoder cannot reset one without re-adding the other. No rule in the
  codebase authors `Bold|Faint` (terminals render the pair as folklore), and "disabled says Faint,
  heading says Bold" is a *genuine same-axis conflict the lattice should arbitrate* — under an
  enum it resolves deterministically through PD26 and `StyleDiagnostics.Explain` can say why;
  under independent bools it composes into `Bold|Faint` folklore that only a downstream quantizer
  special-case could repair. Mutual exclusion by construction, arbitration where arbitration is
  meaningful. (The uniform-bools alternative conceded this point in its own self-critique.)
- **`Italic` as `bool`, not a `FontStyle` enum.** WPF's third value (`Oblique`) has no terminal
  encoding; a two-value enum is ceremony. Plain-adjective naming (`Italic`, `Strikethrough`,
  `Inverse`, …) mirrors the `TextAttributes` member vocabulary and the markup tags (`[i]`, `[s]`):
  these name *attributes present on text*, not element state — `TextElement.Italic="True"` reads
  as the declaration it is.
- **Underline: presence + shape unified as `UnderlineStyle?`; color split.** SGR encodes presence
  *as* shape (`4:0` = off), so a shape-with-no-presence state should be unrepresentable — `null` =
  absent, a value = present in that shape. Core's `UnderlineStyle` is reused **as-is** (`Single =
  0`, no `None` — adding one would renumber a shipped enum; a parallel UI-side enum was rejected
  as an unenforced cross-layer invariant), and the XAML converter ladder already unwraps
  `Nullable<T>` (`XamlConverters.cs:70,81` — verified), so `TextElement.Underline="Curly"` parses
  today and `{x:Null}` clears. Color stays separate: it is a separate wire channel (58/59), a
  different type (brush, tier-quantized), and legitimately restyled without knowing shape (the
  mnemonic indicator, `AccessTextPresenter.cs:111`, today hardcoded). **v1 scope is owner
  question 2** — the formatted-text seam is flags-only today, and this proposal refuses to ship a
  property whose values silently drop (§3.1).
- **`Concealed`, not `Hidden`.** `Visibility.Hidden` already means something else in this
  framework; ANSI's own word is "conceal". The one property whose name deviates from its enum
  member — the mapping is one documented line in the fold.
- **The exotics (Blink, Concealed, Overline) get real properties.** Losslessness: during migration
  the fold ORs `perAxis ∪ legacyAggregate`, and every legacy flag needs a per-axis home or the
  bridge is partial; post-migration, "every SGR attribute is reachable through the styled spine"
  is a clean claim for a terminal-first library. Whether they *inherit* is owner question 3 (they
  are the marginal cost drivers in the reparent diff).
- **No `DefaultResourceKey` on any of them** (contrast `ForegroundProperty`). The defaults are
  semantic zeros — correct at every theme and tier; tier polymorphism belongs in rules and the cue
  resources (§2.3). The machinery stays available (e.g. a future hyperlink theme giving
  `UnderlineProperty` a themed default).
- **`AccessTextPresenter.KeyAttributesProperty` stays aggregate-typed** (`TextAttributes`, default
  `Underline`): a non-inherited leaf knob OR-merged onto the mnemonic cell — a composition input,
  not an arbitration surface. The Core enum remains the right vocabulary below the property layer.

---

## 2. Semantics

### 2.1 Per-axis inheritance, including "turn OFF an inherited flag" — zero new machinery

Shadowing in the store is **presence-based** (`EffectivePriority != Unset` is a contribution —
`UIObject.cs:89, :767`), which is exactly the tri-state the aggregate design lacked:

- **Add a flag under an inherited look.** Ancestor holds `Inverse=true` (say
  `.caps-nocolor ListBoxItem:selected`); a descendant TextBlock gets `TextWeight=Bold` from an app
  rule. Different property ids → different store slots → the `Inverse` read walks to the ancestor,
  the `TextWeight` read stops at the local frame. Fold: `Inverse|Bold`. **The motivating bug is
  structurally impossible.**
- **Remove a flag under an inherited look.** `ListBoxItem:selected TextBlock.badge
  { TextElement.Inverse = false }` — that `false` is a real contribution; the badge's read takes
  it and never walks, while every other axis inherits untouched. `false` ≠ unset: `false` shadows,
  `ClearValue` restores inheritance — the same distinction the store already makes for every
  property. No tri-state type (`bool?` was considered and rejected — `null` would duplicate what
  retraction/`ClearValue` already mean), no masks, no merge frames.
- **Retraction** is the existing cookie-batch path: rule deactivates → frame removed → the read
  falls back to the inherited value. Store-owned promotion, conformance-kit covered.

This unblocks two recorded theme deferrals: the TreeView NoColor selection rule
(`CursorialThemeStyles.cs:104-106` excludes TreeViewItem *because* inherited Inverse leaks into
nested children — a child-scoped `Inverse=false` cancel becomes safe, since it no longer also
strips disabled-Faint), and the P9.3b Inverse+Bold ListBox focus cue (`ControlThemes.cs:271`),
which becomes two composable setters — and the live proof-of-fix (§5 P3).

One honest non-fix: decomposition does not stop inheritance *flowing*; it adds the surgical
countermeasure (per-axis cancel) that whole-value shadowing made too destructive to use.

### 2.2 Interaction with the completed lattice — worked arbitration

Setup: NoColor tier. Theme conditional rule `.caps-nocolor Button:focus → Inverse=true`
(classLike > 0 ⇒ **StyleTrigger, 50**). App resting rule `Button.title → TextWeight=Bold`
(structural ⇒ **Style, 100**). Content TextBlock below.

1. Focus lands; the StyleEngine activates the theme rule → one frame at StyleTrigger on the Button
   for `InverseProperty`. The app's `TextWeightProperty` frame rests at Style on the same Button.
   **Different properties — no arbitration between them ever occurs.** (Today: two frames on ONE
   property; StyleTrigger beats Style; the focused button reads `Inverse` and Bold vanishes — the
   reported bug, verbatim.)
2. The TextBlock renders: `Inverse` walks to the Button's StyleTrigger value (`true`);
   `TextWeight` walks to the Style value (`Bold`). Fold: `Inverse|Bold`. Both cues render.
3. App cancels the theme's inverse for one quiet button: `Button.quiet:focus → Inverse=false`.
   Both rules are conditional (same slot); the packed sort key arbitrates within the slot — the
   `[layer]` field is the top bits and App > Theme, so the app's `false` wins. PD26 untouched.
4. If the cue were a **template-part value** instead (Template lane, 75), a conditional
   `Inverse=false` (StyleTrigger, 50) pierces it — an active state look beating a part's resting
   truth, the lattice's design intent, now available per axis. A SuperTip title's template-lane
   Bold (`CursorialBarsTheme.cs:1080`) coexists with any conditional Inverse rule (different
   property) and yields to a conditional `TextWeight` rule (same axis) — WPF-parity behavior.
5. Blur: retraction removes the `Inverse` frame; `TextWeight` never moves — **no re-arbitration
   even occurs for the axes whose frame stacks didn't change.**

`SetCurrentValue`/PD27, `BindingOperations.Watch`, `When` conditions: the new properties get all
of it for free — they are ordinary registered properties.

### 2.3 Themed defaults and the `InteractiveInverseAttributes` split

`ThemeKeys.InteractiveInverseAttributes` is the tier-polymorphism mechanism: one DynamicResource
meaning Inverse at NoColor, Faint at (Dark|Light, Ansi16), None elsewhere
(`CursorialTheme.cs:168,175,316,474`), consumed by 12 `SetResource` rules (6 core accent-family +
4 Bars + 2 Dialogs). A whole-flags box cannot feed per-axis properties, and seal-time expansion
cannot help (the value resolves live per tier, after seal). The resource splits **along the same
axes as the properties**, with the weight half enum-typed (strictly more expressive than a bool —
a future Bold-cue tier is a value change, not a third key):

```csharp
// ThemeKeys
public const string InteractiveCueInverse = "Theme.InteractiveCueInverse"; // bool
public const string InteractiveCueWeight  = "Theme.InteractiveCueWeight";  // TextWeight
```

| Tier dictionary | `InteractiveCueInverse` | `InteractiveCueWeight` |
|---|---|---|
| `(null, NoColor)` | `true` | `Normal` |
| `(null, Ansi16)` wildcard floor (the CD8 descent stopper) | `false` | `Normal` |
| `(Dark\|Light, Ansi16)` | `false` | `Faint` |
| `(Dark\|Light, Ansi256/RGB)` | `false` | `Normal` |

Each of the 12 consumers becomes two `SetResource` calls (before/after in §4.3). The CD8
tier-descent trick is a property of the `ThemeDictionaries` lookup, not the value type — survives
unchanged. The `(Dark|Light, Ansi16) = Faint` oddity is preserved verbatim and becomes *legible*
in the table for designers to revisit.

**Honest parity note (tempered per the judgment):** at color tiers these rules today contribute a
whole-value `None` at StyleTrigger while active — shadowing **all nine** inherited axes on every
focused control. After the split they contribute `Inverse=false` + `Weight=Normal` at StyleTrigger
while active: the cue rule still *owns the two cue axes* during focus (an app's ambient Bold still
yields to the cue's `Normal` while focused — the known residual, §7), but italic, underline,
strikethrough, and the rest now inherit straight through. The blast radius shrinks from nine axes
to the two the cue actually speaks about; use italic/underline as the example of what's fixed, not
weight.

**Why reverse-video is a NoColor-tier idiom (owner-supplied ground truth, 2026-07-13):** at
color tiers a non-occluding face fills through the glyph-transparent `PaintRectangle` tint, which
deliberately drops attributes on glyphless cells (`Border.cs:151-162` — an attribute-bearing face
needs `FillOpaque` to composite onto the WHOLE face) — so a color-tier Inverse cue inverts only
the label's cells, not the face's whitespace. NoColor forces opaque fills, which is exactly why
whole-face reverse-video works there and why color-tier emphasis is (correctly) faked with brush
swaps instead. And the deeper reason holds even where whole-face Inverse WOULD work
(owner, 2026-07-13): **within a tier, all interactive-state cues must speak one vocabulary.** At
color tiers the sibling states (`:pointerover`, `:pressed`) cue through fg/bg brush swaps; a lone
attribute-based focus cue composes with them as "invert whatever brushes happen to be active" — a
derived color, not a designed one — so focus+hover and focus+pressed render incoherently. At
NoColor, brushes collapse and EVERY cue is an attribute, so the vocabulary stays uniform there
too. The cue-pair tier tables encode this principle (Inverse fires only where brushes cannot
speak); theme authors: do not mix cue vocabularies within a tier.

**Pair coherence** (no proposal had this; the judge added it): a theme test walks every tier
dictionary asserting **both** cue keys are present — the pair-coherence lint all three proposals'
self-critiques asked for, at test-time cost instead of engine cost.

---

## 3. Composition — the fold, and honest perf accounting

### 3.1 The fold

One composition point replaces the four renderers' `GetTextAttributes` reads:

```csharp
/// <summary>Paint-time resolution of the per-axis properties into the Drawing tier's vocabulary.</summary>
public readonly record struct ResolvedTextAttributes(
    TextAttributes Flags,            // the folded bitset, incl. the Underline presence bit
    UnderlineStyle UnderlineShape)   // meaningful only when Flags has Underline
{
    public bool Inverse => (Flags & TextAttributes.Inverse) != 0;
}

public static ResolvedTextAttributes ComposeAttributes(UIElement element) { /* §5 P1/P2 */ }
```

Point edits per renderer (the complete reader set, per the scout inventory):

- **TextBlock** (`:118`) — the fold's `Flags` as `baseAttributes`; the pinned C166b/C166c contract
  (merge at paint, never in the `FormattedText` cache key) is untouched — the fold happens exactly
  where the single read happened.
- **Border** (`:145-165`) — the fill decision reads `GetInverse(this)` directly (a *cleaner*
  statement of the deliberate Inverse-only fill asymmetry than today's flag pluck); the
  `PanelTitle` takes the full fold; the NoColor force-opaque branch is untouched.
- **AccessTextPresenter** (`:86-118`) — base = fold; mnemonic cell = `KeyAttributes | base` plus
  the existing hardcoded underline shape/color (until/unless `UnderlineBrushProperty` ships).
- **ToggleGlyph** (`ControlThemes.cs:1950-1990`) — fold on the glyph cells; NoColor disabled-Faint
  parity preserved.
- **TextPresenter** — *newly able* to participate (today TextBox content ignores the inherited
  spine entirely — a recorded gap); optional follow-on, not gating. Its placeholder-Faint and
  NoColor-selection-Inverse cell bakes stay (leaf visuals, no producer fight).

Downstream of the fold, **nothing changes**: `RenderContext.DrawFormattedText(…, baseAttributes)`
→ `DrawingContext.DrawFormattedCore`'s OR merge (`:913-915`), `Pen.Attributes`,
`PanelTitle.Attributes`, `DecoratedFont`, `SgrEncoder`, `StyleQuantizer` all keep consuming
`Output.TextAttributes` — which keeps every lower-layer suite decomposition-neutral by
construction.

**The paint-merge OR stays OR.** Per-axis *removal* happens upstream at the property level, so the
render pipeline never needs a subtraction channel. Content-baked markup flags (`[b]` in cached
`FormattedText` runs) remain un-strippable from properties — a scoping rule (markup is inner-scope
content; content wins), same as today, pinned as a residual in §7.

**No silent drops — resolved (Q2, decided 2026-07-13):** the formatted-text seam carries flags
only today (`RenderContext.DrawFormattedText(…, TextAttributes baseAttributes)`, verified), so the
`DrawFormattedText`/`RenderContext` base-style widening (underline shape carried alongside the
flags into `DrawFormattedCore`'s merge) **ships in the same phase as the property** (§5 P2):
`Underline="Curly"` renders Curly from day one. The rejected alternative (presence-only v1 behind
a `Validate` gate) is recorded here only so the silent-drop failure mode stays named.

### 3.2 Perf accounting (corrected per the judgment)

**The read path, honestly.** There is **no** "never contributed anywhere" fast path: when an
element has no own entry, `GetValue` unconditionally walks the parent chain probing every
ancestor's store (`UIObject.cs:92, :763-776`) — a never-set inheriting property is the *most*
expensive read, walking to the root before falling to default. Naively, nine properties ≈ 9 × D
store probes per text-element render (D = tree depth, 10–20 in real apps). Text elements render
per dirty zone, not per frame, and the probes are null-check + small-map lookups — but the design
treats the amplification as a first-class constraint, not a footnote:

- **The batched single-pass walk** ships in the same phase as the properties (§5 P2): the fold
  walks the `IInheritanceNode` chain **once**, carrying a bitmask of unresolved axes, probing each
  node for the remaining ids, stopping when the mask empties or the root is reached. One chain
  traversal, k probes per node (k shrinking as axes resolve). Needs one small `internal` read-only
  helper on `UIObject`/`ValueStore` ("does this object contribute property P, with what effective
  value") — an accessor over machinery `FindInheritedEntry` already has, not a new lane or cache.
- **The equivalence gate**: a property test asserting the batched walk ≡ nine naive `GetValue`
  calls across the store-state matrix (own entries, animated lanes, shadowing ancestors, unset),
  plus a `ComposeAttributes` micro-benchmark beside `StoreSpikeBenchmark`, both landing WITH the
  walk — not after the migration.
- **A cached composite is deliberately NOT built** (it needs per-axis invalidation hooks — new
  machinery — to save time the benchmark must first prove is being lost).

**The reparent tax.** `SetInheritanceParent` diffs *every* registered inheriting property over the
old and new chains — two walks each, even when both sides are empty (`UIObject.cs:726-729,
:790-794`). +8 registrations is a real multiplicative cost on tree churn (template application,
items realization, window open). The design doc already names the cure (§2.9 push-down shared
boxes, deliberately unbuilt); this proposal is the first real pressure on that deferral. §5 P3
takes reparent-heavy measurements (gallery page swap, items-host realization); owner question 3
sets the gate number that would force either demoting the exotics to non-inheriting or funding
§2.9.

**Store footprint.** A plain TextBlock: zero entries today, zero after. A NoColor-focused button:
one StyleTrigger frame → two (Inverse + Weight); the template Border *loses* its Template-lane
entry (§4.2). Net wash. Fold allocation: zero (typed bool/enum reads, bit-ORs).

**Change fan-out.** Nine properties ride the existing global-`AffectsRender` eager fan-out; a
focus flip that changed one property now changes one or two. Every rule in the theme inventory
sets one or two axes; the motion-storm gates don't read these properties and re-assert unchanged.

---

## 4. Compatibility + migration

### 4.1 Fate of `TextAttributesProperty`: bridge, then delete — no shorthand, no survivors

During migration the aggregate stays registered and the fold ORs it in (`perAxis |
GetValue(TextAttributesProperty)`), so both surfaces coexist green phase by phase; the final phase
**deletes** it — property, accessors, setter sites (the UI layer is cleared for breaking changes;
every consumer is in-repo; Gallery has *zero* references).

- **Seal-time shorthand expansion — rejected.** The largest legacy producers are the 12
  `SetResource` sites whose values resolve live per tier *after* seal (the shorthand would work
  everywhere except where it's used); and expansion semantics fork badly — CSS-style
  reset-all-longhands recreates the clobber in a new costume, additive-only makes
  `TextAttributes="None"` a silent no-op.
- **Computed read-only aggregate — rejected.** Breaks the TemplateBinding writer and the three
  imperative writers anyway, and keeps two vocabularies alive forever.
- **The `SetTextAttributes` expansion helper — rejected (judge-mandated).** Writing all nine axes
  as LocalValue frames is a nine-frame clobber footgun — shadowing every inherited axis, the exact
  pathology being killed, with no single `ClearValue` to undo it. The aggregate *name* dies with
  the property. (`Output.TextAttributes` the enum lives on: wire type, `Style`/`Pen`/`PanelTitle`/
  markup currency, `KeyAttributesProperty` type, and the fold's return vocabulary. Its
  `TextAttributesConverter` survives for those uses; its false "pipe-separated" comment gets fixed
  in passing.)

### 4.2 The `ControlThemes.cs:100` TemplateBinding: delete it (unanimous)

The button face's Border self-forwards the aggregate via TemplateBinding — redundant under an
inheriting property (the templated parent is the part's ancestor; Bars' `BarItemTemplate` already
relies on pure inheritance for the same job). The arbitration delta is real and gets a **pinned
lane-change parity test**: today the part holds a Template-lane (75) frame that resting
part-targeted rules cannot pierce; after deletion the value arrives via the Inherited lane, which
any part rule beats. No in-repo rule targets a part's text attributes, so rendering is
byte-identical — the test pins that and documents the lane change for future template authors.

### 4.3 Site-by-site (from the scout inventory; complete worklist in the inventory itself)

```csharp
// (1) code-first literal — CapsNoColorInteractiveInverse (CursorialThemeStyles.cs:82)
.Set(TextElement.TextAttributesProperty, TextAttributes.Inverse)   // before
.Set(TextElement.InverseProperty, true)                            // after

// (2) CapsNoColorDisabledFaint (:96)
.Set(TextElement.TextAttributesProperty, TextAttributes.Faint)     // before
.Set(TextElement.TextWeightProperty, TextWeight.Faint)             // after

// (3) the 12 resource-driven cue rules (:137-140 et al., Bars ×4, Dialogs ×2)
.SetResource(TextElement.TextAttributesProperty, ThemeKeys.InteractiveInverseAttributes)  // before
.SetResource(TextElement.InverseProperty,    ThemeKeys.InteractiveCueInverse)             // after
.SetResource(TextElement.TextWeightProperty, ThemeKeys.InteractiveCueWeight)              //   (pair)
```

```xml
<!-- (4) XAML twins (Default/IndigoDusk Styles.xaml) -->
<Setter Property="TextElement.TextAttributes" Value="Inverse"/>  <!-- before -->
<Setter Property="TextElement.Inverse" Value="True"/>            <!-- after -->
<Setter Property="TextElement.TextAttributes" Value="Faint"/>    <!-- before -->
<Setter Property="TextElement.TextWeight" Value="Faint"/>        <!-- after -->
```

- **Imperative writers (3):** `TaskDialog.cs:136`, `FirstRunWizard.cs:111` →
  `SetTextWeight(el, TextWeight.Bold)` (LocalValue, as today); `CursorialBarsTheme.cs:1080` the
  same inside its template scope (Template lane, unchanged mechanics).
- **XAML converter + generator: zero new code.** `TextWeight` rides the generic enum path;
  `UnderlineStyle?` rides the existing `Nullable<T>` unwrap; bools ride the bool converter; all
  context-free (parse-time constant folding preserved). The generator discovers the properties via
  the `<Name>Property` convention (attached pass exists); the dual-run drift gate (X174) gets one
  row exercising a new property through both providers.
- **Inline markup (`[b][i][u][s]`):** untouched — bakes `Output.TextAttributes` into runs below
  the property system.
- **Hardcoded cell bakes** (TextPresenter `:295/:372`, Demo drawing code): untouched — they write
  `Output.Style` directly, below the property layer.
- **Drift reconciliation rider:** since all four theme files are open anyway, the XAML phase
  reconciles the recorded code-first↔XAML drift: the pseudo-class set mismatch (`:pressed` vs
  `:pointerover`), IndigoDusk's missing selection rule *(IndigoDusk is WIP/not-shipping — apply
  opportunistically, not as a gate)*, and DELETING the commented-out accent `:focus-visible`
  setters (Q4, decided 2026-07-13 — see §8 for the recorded reason).
- **Tests.** Updated mechanically: ControlMatrix Section09 (22 refs), Section05 (8), Section04
  (7, incl. C100f's retraction assert → per-axis default), XamlThemeStylesTests (6), Bars (2).
  New rows: per-axis inheritance walk; cancel-inherited-flag through each lane; the §2.2
  conditional-over-resting and conditional-over-template walks; weight-axis exclusivity; the
  tier-resource pair per tier + pair-coherence; TemplateBinding-deletion parity; batched-walk
  equivalence. Lower-layer suites (SgrEncoder 18, Style 12, Quantizer 9, RichTextBuilder 9):
  untouched by construction.
- **Docs.** `ui-layer-design.md:680` — recorded as a **refinement, not a reversal**: the pin's
  rationale stands (refuse WPF's font-object model, `FontWeight` struct, and the 100–900 numeric
  lie; no font converters); what changes is only the *aggregation*, and the deviated names signal
  the deviated domain. Also: `:2374` API map; control-matrix C119 (fixing the pre-existing
  AddOwner drift) + C166b/c re-wording; a precedence-matrix companion note ("per-axis text
  properties are the granularity companion to PD26"); CLAUDE.md status blurb; a new pinned
  decision recording the decomposition, the `TextWeight` fold, the cue-resource split, the
  TemplateBinding drop, and the §7 residuals.

---

## 5. Phase plan (each increment green; compose-seam-first)

1. **P1 — Compose seam (pure refactor).** `ResolvedTextAttributes` + `ComposeAttributes`
   (legacy-aggregate-only fold); re-point the four renderers. **Byte-identical rendering**;
   existing tests pin it. Every later phase has a stable seam and a small diff.
2. **P2 — Properties + semantics + the batched walk + the underline seam.** Register the
   per-axis properties + `TextWeight`; fold becomes `perAxis | legacy`; the batched single-pass
   walk lands WITH its naive-equivalence property test and micro-benchmark; the
   `DrawFormattedText`/`RenderContext` base-style widening lands so the underline SHAPE renders
   from day one (Q2); new matrix rows (inheritance, cancel, lattice walks, exclusivity) prove the
   store semantics before any theme moves.
3. **P3 — Code-first theme migration.** Cue-resource pair into ThemeKeys + tier dictionaries;
   rewrite 6 literal + 12 resource rules (core/Bars/Dialogs); 3 imperative writers; delete the
   TemplateBinding with its parity test; **implement the P9.3b Inverse+Bold ListBox focus cue in
   the Gallery canary as the composability exit proof** (the motivating scenario, live and
   hands-on-testable). Reparent/paint measurements taken here against the Q3 gate.
4. **P4 — XAML surface.** Rewrite Default (and opportunistically IndigoDusk) `Styles.xaml` +
   drift reconciliation; XamlThemeStylesTests; the generator drift-gate row.
5. **P5 — Retirement.** Delete `TextAttributesProperty` + accessors + the bridge OR; converter
   comment fix; doc/matrix amendments land in full.

Rollback is clean at every boundary: P1–P2 additive, P3–P4 mechanical with the bridge live, only
P5 burns the boat. Per project policy, **P3 and P5 get adversarial audits** before commit (the
theme-semantics and the point-of-no-return phases).

---

## 6. The rejected alternative: keep the group, add merge semantics

The strongest keep-the-aggregate variant was fully worked (patch-valued setters
`{Set, Clear}` masks + a per-property `IValueMerger` fold in the store) and it genuinely solves
the motivating bug. **It loses on an evidence-backed enumeration** — the whole-value "one frame
wins" identity is a load-bearing assumption of roughly six store features:

1. the winning-base observer channel (`IncludeBaseChanges` — the Transitions seam): a fold has no
   "winning base" to transition from/to;
2. `GetValueSource`/`StyleDiagnostics.Explain` (the S164-pinned one-line derivation names *the*
   winning rule; a fold's answer is a list per bit);
3. `SetCurrentValue`/PD27 grafts and `ClearValue` universal-undo (defined against a single
   underlying producer);
4. compiled bindings' typed zero-box push + box interning (value-in/value-out per lane);
5. the `ValueFrame` conformance kit and both freshly-completed normative matrices (a merge lane is
   a second arbitration model needing its own rows and a re-audit of every lane interaction);
6. inheritance (inherit the fold and the nearest-contributor contract breaks; inherit the patch
   chain and eager fan-out carries non-value state).

And after all that, the paint-time OR over markup-baked runs *still* can't subtract — the merge
lane fixes store arbitration only, same as decomposition. **Tally: ~6 core store features and both
matrices touched, to save ~40 mechanical call sites that decomposition edits instead.** The group
was never a semantic unit; it was nine axes sharing a `ushort`. The store already knows how to
arbitrate axes — give it axes.

---

## 7. Pinned residuals (known, accepted, documented — not bugs to rediscover)

1. **The inert-`false` conditional-slot wart.** At color tiers the cue rules contribute
   `Inverse=false` + `Weight=Normal` at StyleTrigger while active — so an app rule wanting
   `Inverse=true` or `Bold` on a *focused* control at Style priority still loses to a theme
   contribution that means "nothing," exactly as today (CD8 floor parity). Decomposition narrows
   the clobber to the cue axes but does not eliminate resource-driven inert contributions; a real
   fix (tier-scoped rule arming, or "unset" resource sentinels the engine treats as
   no-contribution) is new machinery this proposal explicitly declines. A wart is preserved to
   preserve the machinery budget.
2. **The markup-bake OR residue.** A property-level `false` cannot strip a flag baked into a
   markup run (`[b]` content vs `TextWeight=Normal` ambience — content wins; the
   `Style.AddAttributes` OR at `DrawingContext.cs:913` has no subtraction channel). Same behavior
   as today; documented as a scoping rule. A user who learns "set it false to turn it off" will
   find markup text exempt — the doc for the properties says so explicitly.
3. **The TextPresenter gap.** TextBox content ignores the inherited attribute spine today and
   still will until the optional follow-on lands. Recorded, not entrenched.

---

## 8. Owner decisions (record of 2026-07-13)

1. **`TextWeight` vs `FontWeight` naming — DECIDED: `TextWeight`.** No WPF muscle-memory trap
   (`FontWeight="SemiBold"`/`"600"` would fail here); continuity with the `:680` pin's "no font
   types" rationale — the deviated name signals the deviated domain.
2. **Underline v1 scope — DECIDED: full `DrawFormattedText` support.** The base-style widening
   (underline shape through the formatted-text seam) ships in the same phase as the property
   (§3.1, §5 P2); no deferred-semantics property, no silent drop. `UnderlineBrushProperty` stays
   demand-gated.
3. **Do Blink/Concealed/Overline register as inheriting? — RECOMMENDED (awaiting confirmation):
   inherit, uniformly, with a demotion gate.** The decisive argument is the content boundary, not
   template plumbing: a `TemplateBinding` forward sets the value ON a template part, and a
   non-inheriting property stops there — it cannot reach text inside APP CONTENT hosted by a
   `ContentPresenter`, so ambient subtree uses (`Concealed` on a container to redact everything
   inside it) become inexpressible; non-inheritance makes these leaf-only properties, a semantic
   reduction. Costs: the batched walk makes three never-set axes ≈ three extra presence-probes
   per ancestor inside ONE traversal (noise); the real tax is the reparent diff, which is
   per-structural-change and measured at P3. Asymmetry favors starting uniform: demoting later is
   a one-flag change justified by a number; promoting later silently changes app behavior.
   **Gate: >5% wall-clock regression on the reparent-heavy benchmarks (gallery page swap,
   100-item ListBox realization) vs the pre-decomposition baseline ⇒ demote the three exotics to
   non-inheriting first; still over ⇒ fund §2.9 push-down** (which erases the tax for every
   inherited property at once).
4. **The commented-out XAML accent `:focus-visible` setters — DECIDED: delete, with the real
   reason recorded.** They were an experiment in a color-tier Inverse focus cue, parked because
   at color tiers the non-occluding face's glyph-transparent tint drops attributes on glyphless
   cells (`Border.cs:151-162`) — Inverse inverted only the label's cells, not the face's
   whitespace — so the look was faked with brush swaps instead, which is the CORRECT color-tier
   idiom (whole-face Inverse would require `Occludes=true`, changing compositing semantics).
   Reviving via the split cue pair would be a no-op at color tiers (`InteractiveCueInverse=false`
   there) and redundant at NoColor (the live `.caps-nocolor` button-family rules already apply
   Inverse, and NoColor's forced-opaque fill is what makes it whole-face there). A second,
   independent reason holds even where the tint mechanics don't bite: color-tier sibling states
   (`:pointerover`/`:pressed`) cue through brush swaps, and an attribute cue composing over
   swapped brushes yields derived, undesigned colors — one cue vocabulary per tier. See §2.3's
   ground-truth note.

---

## 9. Provenance

Produced by the house design-panel process (2026-07-13): a scout inventory of every registration,
read/write, theme, XAML, markup, and test touchpoint (verified file:line evidence; it also
surfaced that `Cursorial.UI.Dialogs` — TaskDialog/CommandLink — exists but is not yet in
CLAUDE.md's project list); three independently-authored proposals (WPF-kinship decomposition,
uniform terminal-native booleans, keep-the-group-with-merge-semantics); and an adversarial
judgment (`judgment-textattributes-decomposition.md`) that verified the proposals' load-bearing
claims against the code — including falsifying one perf claim ("never-contributed properties
resolve without walking") that this document's §3.2 corrects. All three proposals independently
converged on per-axis decomposition with paint-time folding; this synthesis adopts the
highest-scored proposal's architecture with the judgment's mandated fixes and cross-proposal
grafts applied. The full proposal texts were session-scoped working artifacts; the judgment is
archived because it records *why* each trim was chosen and what each alternative got right.
