# Proposal: Per-Axis Text-Attribute Properties (the `TextAttributes` Decomposition)

**Status: IMPLEMENTED (2026-07-13, PR pending) — all owner decisions in §8 settled; the five-phase build (P1–P5) landed with adversarial audits at P3/P5.** Produced
2026-07-13 by the panel process recorded in §9, then re-cut the same day to the owner-directed
**non-inheriting** flow model (§9 notes the redirect); the adversarial judgment lives in
`judgment-textattributes-decomposition.md`. This document is the canonical design record; where the
landed code refined it (the glyph Inverse-only rule §7.7, the RelativeSource-Binding forward idiom,
the P3 audit residuals §7.5–7.7), the amendments are dated inline.

---

## 0. The problem, and the thesis

The motivating report (2026-07-12, during the precedence-lattice work): *"TextAttributes is
already a problem because it groups several display characteristics together; you want to add
Bold? Well, better hope Inverse wasn't already there for a good reason."*

`TextElement.TextAttributesProperty` packs nine independent axes (Bold, Faint, Italic, Underline,
Blink, Inverse, Hidden, Strikethrough, Overline) into one attached property. The value store
arbitrates **whole values per property** — the model the completed lattice (PD26/PD27) was just
built against, and the correct one — so producers that care about *different axes* are forced to
fight: a theme's `:focus-visible → Inverse` rule and an app's `Bold` rule cannot coexist; the
winner's whole value replaces the loser's. Meanwhile every layer BELOW the property is already
decomposed: SGR sets and resets each attribute independently (1/2/3/4/5/7/8/9/53, resets
22/23/24/25/27/28/29/55 — `SgrEncoder.cs:147-197`), `StyleQuantizer` drops attributes one at a
time, the cell `Style` is a bitset, and the Drawing markup tier composes per-flag. The UI property
is the only aggregation point in the stack.

**The thesis: the store is not the bug — the granularity is.** Give each axis its own
`UIProperty` and the existing machinery *is* per-flag composition: two producers on different axes
never meet in arbitration; conditional rules pierce template values per axis exactly as PD26
intends. **Zero new engine machinery.** `Output.TextAttributes` (shipped Core; the wire/cell
vocabulary) is untouched — renderers fold the per-axis effective values back into the bitset at
paint.

**The flow model (owner decision ③): the axes are NON-inheriting and flow like `Background`, not
like `Foreground`.** Usage leans virtually exclusively to element-level application, and the
interactive-cue model already rides the control-property → `TemplateBinding` spine for its brush
half — the attribute half now rides the same spine, so the NoColor cue (`Inverse`) and the
color-tier cue (brush swap) flow through ONE mechanism, completing the one-vocabulary-per-tier
principle (§2.3). This deletes the proposal's riskiest engineering outright: no inheritance walks,
no read amplification, no reparent-diff participation, no §2.9 pressure (§3.2).

All three independently-authored panel proposals converged on per-axis decomposition with
paint-time folding; the judgment rated the convergence itself as strong evidence. The panel
assumed inherited axes; the owner redirected the flow model after reviewing usage — §9 records
both.

---

## 1. Property surface

All properties live on `TextElement` (namespace `Cursorial.UI.Controls`), registered as
**non-inheriting** `AttachedProperty<T>` with `AffectsRender` on the **global effects lane**
(attached to arbitrary host types, so per-owner-type effects registration doesn't apply). No
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
| `TextStyleProperty` | `TextStyle { Normal, Italic }` | `Normal` | 3 / 23 | the posture axis; enum per the 2026-07-13 amendment (below) |
| `UnderlineProperty` | `UnderlineStyle?` | `null` | 4 / 4:n / 24 | presence + shape unified; `null` = no underline (§8 Q2) |
| `UnderlineBrushProperty` | `IBrush?` | `null` | 58 / 59 | CSS `text-decoration-color`; **phase-late, demand-gated** |
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
- **`TextStyle { Normal, Italic }` — the posture axis as an enum (owner amendment, 2026-07-13;
  supersedes the judge's Italic-as-bool trim).** Two reasons: *discoverability* — the `Text*`
  prefix groups the axis family (`TextWeight`/`TextStyle`) as a set in completion lists; and
  *headroom* — future terminal posture standards slot in as values, not new properties (SGR 20
  fraktur is the historical precedent). WPF's `Oblique` is still refused (no terminal encoding).
  The remaining boolean axes keep plain-adjective naming (`Strikethrough`, `Inverse`, …),
  mirroring the `TextAttributes` member vocabulary and the markup tags (`[s]`, `[u]`).
- **Underline: presence + shape unified as `UnderlineStyle?`; color split.** SGR encodes presence
  *as* shape (`4:0` = off), so a shape-with-no-presence state should be unrepresentable — `null` =
  absent, a value = present in that shape. Core's `UnderlineStyle` is reused **as-is** (`Single =
  0`, no `None` — adding one would renumber a shipped enum; a parallel UI-side enum was rejected
  as an unenforced cross-layer invariant), and the XAML converter ladder already unwraps
  `Nullable<T>` (`XamlConverters.cs:70,81` — verified), so `TextElement.Underline="Curly"` parses
  today and `{x:Null}` clears. Color stays separate: a separate wire channel (58/59), a different
  type (brush, tier-quantized), legitimately restyled without knowing shape.
- **`Concealed`, not `Hidden`.** `Visibility.Hidden` already means something else in this
  framework; ANSI's own word is "conceal". The one property whose name deviates from its enum
  member — the mapping is one documented line in the fold.
- **The exotics (Blink, Concealed, Overline) get real properties.** Losslessness: during migration
  the fold ORs `perAxis ∪ legacyAggregate`, and every legacy flag needs a per-axis home or the
  bridge is partial; post-migration, "every SGR attribute is reachable through the styled spine"
  is a clean claim for a terminal-first library. Under the non-inheriting cut they carry no
  walk/reparent cost at all — the old demotion question (panel Q3) is moot.
- **No `DefaultResourceKey` on any of them** (contrast `ForegroundProperty`). The defaults are
  semantic zeros — correct at every theme and tier; tier polymorphism belongs in rules and the cue
  resources (§2.3). The machinery stays available (e.g. a future hyperlink theme giving
  `UnderlineProperty` a themed default).
- **`AccessTextPresenter.KeyAttributesProperty` stays aggregate-typed** (`TextAttributes`, default
  `Underline`): a non-inherited leaf knob OR-merged onto the mnemonic cell — a composition input,
  not an arbitration surface. The Core enum remains the right vocabulary below the property layer.

---

## 2. Semantics

### 2.1 The flow model: element-level values + the forwarding spine

Three delivery paths, all existing idioms — this is exactly how `Background` reaches a button's
face today:

1. **Element-level (the common case).** The value is set (by rule, resource, or code) on the
   element that renders it — a `TextBlock`'s own `TextWeight=Bold`, a Border's own `Inverse`.
   Reads are own-entry-or-default; nothing flows.
2. **Template parts: `TemplateBinding` forwards.** A control-level value (where theme rules land —
   `.caps-nocolor Button:focus → Inverse=true` targets the *Button*) reaches the parts that
   consume it through live per-axis TemplateBindings authored in the template — the brush idiom,
   verbatim: the face Border forwards `Inverse` for its fill; a title-bearing part forwards the
   axes its title renders. Forwards land on the Template lane (75), so a conditional rule
   targeting the part still pierces them (PD26), and a control-value change re-pushes through the
   forward exactly as brush forwards re-push today.
3. **Framework-generated presentation leaves: presenter forwards.** `ContentPresenter` (and the
   `AccessTextPresenter` path) forwards the `TextElement` axes from the templated parent onto the
   presentation elements **it generates itself** — the string-content `TextBlock`, the access-text
   leaf. **`DataTemplate`-built content is never touched**: app content is app-styleable (the
   PD24′ principle) and receives no ambient attributes — an app styles its own item-template text
   directly (`ListBoxItem:selected TextBlock { … }` descendant rules match app content; the
   template barrier only guards template parts).

Composition across producers is per-axis and needs no flow at all: the theme's `Inverse` frame and
the app's `TextWeight` frame arbitrate on the *same control* in *different store slots* — the
motivating bug is structurally impossible. "Turn off" is now trivial: there is no ambient flow to
cancel; within one element the lattice arbitrates `false` vs `true` contributions like any other
property.

Two recorded problems dissolve outright under this cut: the **TreeView NoColor selection leak**
(`CursorialThemeStyles.cs:104-106` deferred the rule *because* inherited Inverse bled into nested
items — with no inheritance there is no bleed; the row's face and generated header text invert via
forwards, nested items are untouched), and the **P9.3b Inverse+Bold ListBox focus cue**
(`ControlThemes.cs:271`) becomes two composable setters — the live proof-of-fix (§5 P3).

### 2.2 Interaction with the completed lattice — worked arbitration

Setup: NoColor tier. Theme conditional rule `.caps-nocolor Button:focus → Inverse=true`
(classLike > 0 ⇒ **StyleTrigger, 50**). App resting rule `Button.title → TextWeight=Bold`
(structural ⇒ **Style, 100**). Both target the **Button** — arbitration happens in exactly one
place, per axis.

1. Focus lands; the StyleEngine activates the theme rule → one frame at StyleTrigger on the Button
   for `InverseProperty`. The app's `TextWeightProperty` frame rests at Style on the same Button.
   **Different properties — no arbitration between them ever occurs.** (Today: two frames on ONE
   property; StyleTrigger beats Style; the focused button reads `Inverse` and Bold vanishes — the
   reported bug, verbatim.)
2. Delivery: the face Border's `Inverse` forward re-pushes `true` → the fill inverts; the label
   leaf (presenter forward) re-pushes both axes → the text renders `Inverse|Bold`. Both cues
   compose.
3. App cancels the theme's inverse for one quiet button: `Button.quiet:focus → Inverse=false`.
   Both rules are conditional (same slot); the packed sort key arbitrates within the slot — the
   `[layer]` field is the top bits and App > Theme, so the app's `false` wins **on the control**,
   and the forwards carry whatever won. PD26 untouched.
4. A conditional rule targeting a *part* still pierces that part's forwarded value (StyleTrigger
   50 over Template 75) — per axis, the lattice's design intent.
5. Blur: retraction removes the `Inverse` frame on the control; the forwards re-push the resting
   value; `TextWeight` never moves — **no re-arbitration even occurs for the axes whose frame
   stacks didn't change.**

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
speak); theme authors: do not mix cue vocabularies within a tier. Under the non-inheriting cut the
two cue halves also share one *delivery* mechanism — control property → forwards — completing the
symmetry.

**Honest parity note:** at color tiers the cue rules today contribute a whole-value `None` at
StyleTrigger while active — shadowing **all nine** axes on every focused control. After the split
they contribute `Inverse=false` + `Weight=Normal` at StyleTrigger while active: the cue rule still
*owns the two cue axes* during focus (an app's Bold on the same control still yields to the cue's
`Normal` while focused — the known residual, §7), but every other axis is untouched.

**Pair coherence** (no proposal had this; the judge added it): a theme test walks every tier
dictionary asserting **both** cue keys are present — the pair-coherence lint all three proposals'
self-critiques asked for, at test-time cost instead of engine cost.

**`DefaultResourceKey`:** assigned to none of the new properties at v1 (§1); documented as
available.

---

## 3. Composition — the fold, and the (now short) perf story

### 3.1 The fold

One composition point replaces the four renderers' `GetTextAttributes` reads — reading the
element's **own** effective values:

```csharp
/// <summary>Paint-time resolution of the per-axis properties into the Drawing tier's vocabulary.</summary>
public readonly record struct ResolvedTextAttributes(
    TextAttributes Flags,            // the folded bitset, incl. the Underline presence bit
    UnderlineStyle UnderlineShape)   // meaningful only when Flags has Underline
{
    public bool Inverse => (Flags & TextAttributes.Inverse) != 0;
}

public static ResolvedTextAttributes ComposeAttributes(UIElement element) { /* nine own-value reads */ }
```

Point edits per renderer (the complete reader set, per the scout inventory):

- **TextBlock** (`:118`) — the fold's `Flags` as `baseAttributes`; the pinned C166b/C166c contract
  (merge at paint, never in the `FormattedText` cache key) is untouched. **The underline seam
  widening (Q2)** carries `UnderlineShape` alongside the flags through
  `RenderContext.DrawFormattedText` → `DrawFormattedCore`, so `Underline="Curly"` renders Curly
  from day one — the flags-only seam (verified) is widened in the same phase the property ships;
  no silently-dropped values.
- **Border** (`:145-165`) — the fill decision reads `GetInverse(this)` directly (a *cleaner*
  statement of the deliberate Inverse-only fill asymmetry than today's flag pluck); the
  `PanelTitle` takes the full fold; the NoColor force-opaque branch is untouched. The face's value
  arrives via the template's `Inverse` forward.
- **AccessTextPresenter** (`:86-118`) — base = fold of its own (forwarded) values; mnemonic cell =
  `KeyAttributes | base` plus the existing hardcoded underline shape/color (until/unless
  `UnderlineBrushProperty` ships).
- **ToggleGlyph** (`ControlThemes.cs:1950-1990`) — fold on the glyph cells (forwarded axes);
  NoColor disabled-Faint parity preserved.
- **TextPresenter** — *newly able* to participate via a TextBox-template forward (today TextBox
  content ignores the attribute spine entirely — a recorded gap); optional follow-on, not gating.

**The forwarding spine** (the §2.1 flow model's delivery half, all landing in framework/theme
code):

- `ContentPresenter` forwards the `TextElement` axes from its templated parent onto the
  presentation elements it **generates** (string→`TextBlock`, the access-text leaf) — never onto
  `DataTemplate`-built content.
- Control templates forward the axes their parts consume (`part.SetBinding(TextElement
  .InverseProperty, new TemplateBinding(TextElement.InverseProperty))`, inside the template build
  → Template lane). The button family forwards `Inverse` to the face Border; the label path gets
  the cue axes via the presenter forward. The existing aggregate forward at `ControlThemes.cs:100`
  becomes these per-axis forwards (§4.2).

Downstream of the fold, **nothing changes**: `DrawingContext.DrawFormattedCore`'s OR merge
(`:913-915`), `Pen.Attributes`, `PanelTitle.Attributes`, `DecoratedFont`, `SgrEncoder`,
`StyleQuantizer` all keep consuming `Output.TextAttributes` — every lower-layer suite is
decomposition-neutral by construction.

**The paint-merge OR stays OR.** Per-axis *removal* happens at the property level; the render
pipeline never needs a subtraction channel. Content-baked markup flags (`[b]` in cached
`FormattedText` runs) remain un-strippable from properties — a scoping rule (markup is inner-scope
content; content wins), same as today, pinned as a residual in §7.

### 3.2 Perf accounting — mostly deleted by the flow-model decision

The panel's heaviest engineering existed to pay for inheritance; the non-inheriting cut removes
the bill:

- **Reads:** own-entry probe or metadata default — no ancestor walks, no read amplification, no
  batched single-pass walk, no equivalence gate. (The panel-era hazard is recorded for history:
  never-set *inheriting* properties walk to the root per read, `UIObject.cs:92, :763-776`; these
  properties never walk at all.)
- **Reparenting:** non-inheriting properties do not participate in `SetInheritanceParent`'s
  per-property chain diffs — zero marginal reparent cost, no demotion gate, no new §2.9 pressure.
- **Forward cost:** one Template-lane entry per consumed axis per part instance — bounded by what
  templates author, the same order as the brush forwards controls already carry. The presenter
  forward adds the same per generated leaf.
- **The fold:** nine local reads + bit-ORs, zero allocation. One `StoreSpikeBenchmark` fold row
  lands for hygiene; the motion-storm gates don't read these properties and re-assert unchanged.

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
  as LocalValue frames is a nine-frame clobber footgun with no single `ClearValue` to undo it. The
  aggregate *name* dies with the property. (`Output.TextAttributes` the enum lives on: wire type,
  `Style`/`Pen`/`PanelTitle`/markup currency, `KeyAttributesProperty` type, and the fold's return
  vocabulary. Its `TextAttributesConverter` survives for those uses; its false "pipe-separated"
  comment gets fixed in passing.)

### 4.2 The `ControlThemes.cs:100` TemplateBinding: from one aggregate forward to per-axis forwards

Under the non-inheriting cut the forward is not deleted (the panel's inherited-flow assumption) —
it becomes the *pattern*: the button face's Border forwards `Inverse`; other templates forward the
axes their parts consume. Forwards are template-authored (Template lane), so conditional
part-targeted rules pierce them (PD26) and resting part rules cannot — the part's resting truth is
what the template wired, exactly the lattice's contract for brushes. A parity test pins the button
family's cue delivery end-to-end (control rule → forward → face fill + label text) at NoColor and
a color tier.

### 4.3 Site-by-site (from the scout inventory; complete worklist in the inventory itself)

```csharp
// (1) code-first literal — CapsNoColorInteractiveInverse (CursorialThemeStyles.cs:82)
.Set(TextElement.TextAttributesProperty, TextAttributes.Inverse)   // before
.Set(TextElement.InverseProperty, true)                            // after   (rule shape unchanged)

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

- **Templates gain the per-axis forwards** their parts consume (button family: `Inverse` to the
  face Border; ToggleGlyph's host template: the cue axes; TabItem header per the selection rule) —
  the §4.2 pattern, replacing the single aggregate forward.
- **`ContentPresenter` gains the generated-leaf forward** (framework code, §3.1) — the one
  genuinely NEW mechanism in the migration, and it is a targeted application of the existing
  binding machinery, not engine work.
- **Imperative writers (3):** `TaskDialog.cs:136`, `FirstRunWizard.cs:111` →
  `SetTextWeight(el, TextWeight.Bold)` (LocalValue, as today — both already target the text
  element itself, i.e. element-level, confirming the flow model); `CursorialBarsTheme.cs:1080` the
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
  setters (Q4 — see §8 for the recorded reasons).
- **Tests.** Updated mechanically: ControlMatrix Section09 (22 refs), Section05 (8), Section04
  (7, incl. C100f — its theme-rule-reaches-the-label behavior re-pins against the forward chain
  instead of inheritance), XamlThemeStylesTests (6), Bars (2). New rows: per-axis arbitration on
  one control (compose + same-axis contest); forward delivery (control rule → part fill + label
  text); presenter forwards generated leaves ONLY (a DataTemplate-content TextBlock receives
  nothing — the §7 scoping rule, pinned); conditional part rule pierces a forwarded value;
  weight-axis exclusivity; the tier-resource pair per tier + pair-coherence; underline shape
  end-to-end through the widened seam. Lower-layer suites: untouched by construction.
- **Docs.** `ui-layer-design.md:680` — recorded as a **refinement, not a reversal**: the pin's
  rationale stands (refuse WPF's font-object model, `FontWeight` struct, and the 100–900 numeric
  lie; no font converters); what changes is only the *aggregation*, and the deviated names signal
  the deviated domain. Also: `:2374` API map; control-matrix C119 (fixing the pre-existing
  AddOwner drift) + C166b/c re-wording; a precedence-matrix companion note ("per-axis text
  properties are the granularity companion to PD26"); CLAUDE.md status blurb; a new pinned
  decision recording the decomposition, the `TextWeight` fold, the **non-inheriting
  flows-like-Background model**, the cue-resource split, the forwarding spine, and the §7
  residuals.

---

## 5. Phase plan (each increment green; compose-seam-first)

1. **P1 — Compose seam (pure refactor).** `ResolvedTextAttributes` + `ComposeAttributes`
   (legacy-aggregate-only fold); re-point the four renderers. **Byte-identical rendering**;
   existing tests pin it. Every later phase has a stable seam and a small diff.
2. **P2 — Properties + semantics + the underline seam + presenter forwards.** Register the
   non-inheriting per-axis properties + `TextWeight`; fold becomes `perAxis | legacy`; the
   `DrawFormattedText`/`RenderContext` base-style widening lands so the underline SHAPE renders
   from day one (Q2); `ContentPresenter`'s generated-leaf forward lands with its
   app-content-untouched row; new matrix rows (per-axis arbitration, forwards, piercing,
   exclusivity) prove the semantics before any theme moves. The `StoreSpikeBenchmark` fold row
   lands here. **Rider (§10): the XAML hex-color convention fix** lands in this phase too (it
   touches the same converter file the new properties exercise; depends only on Core's
   `Color.TryParseHex` having landed).
3. **P3 — Code-first theme migration.** Cue-resource pair into ThemeKeys + tier dictionaries;
   rewrite 6 literal + 12 resource rules (core/Bars/Dialogs); per-axis template forwards replace
   the aggregate forward (with the §4.2 parity test); 3 imperative writers; **implement the P9.3b
   Inverse+Bold ListBox focus cue in the Gallery canary as the composability exit proof** (the
   motivating scenario, live and hands-on-testable).
4. **P4 — XAML surface.** Rewrite Default (and opportunistically IndigoDusk) `Styles.xaml` +
   drift reconciliation (incl. the Q4 deletion); XamlThemeStylesTests; the generator drift-gate
   row.
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
6. inheritance/flow (moot under the non-inheriting cut, but the panel-era analysis stands: either
   fold direction broke a store contract).

And after all that, the paint-time OR over markup-baked runs *still* can't subtract — the merge
lane fixes store arbitration only, same as decomposition. **Tally: ~6 core store features and both
matrices touched, to save ~40 mechanical call sites that decomposition edits instead.** The group
was never a semantic unit; it was nine axes sharing a `ushort`. The store already knows how to
arbitrate axes — give it axes.

---

## 7. Pinned residuals (known, accepted, documented — not bugs to rediscover)

1. **The inert-`false` conditional-slot wart.** At color tiers the cue rules contribute
   `Inverse=false` + `Weight=Normal` at StyleTrigger while active — so an app rule wanting
   `Inverse=true` or `Bold` **on the same control** at Style priority still loses to a theme
   contribution that means "nothing," exactly as today (CD8 floor parity). Decomposition narrows
   the clobber to the cue axes but does not eliminate resource-driven inert contributions; a real
   fix (tier-scoped rule arming, or "unset" resource sentinels the engine treats as
   no-contribution) is new machinery this proposal explicitly declines.
2. **The markup-bake OR residue.** A property-level `false`/`Normal` cannot strip a flag baked
   into a markup run (`[b]` content vs `TextWeight=Normal` — content wins; the
   `Style.AddAttributes` OR at `DrawingContext.cs:913` has no subtraction channel). Same behavior
   as today; documented as a scoping rule.
3. **App content receives no ambient attributes (the flow-model scoping rule, Q3).** Text nested
   inside a custom `DataTemplate` gets neither inherited nor forwarded attribute values — the
   row face and framework-generated leaves carry the cue; app item-template text is styled by the
   app (descendant rules reach app content freely). This is the consciously-accepted trade of the
   non-inheriting cut; the presenter forwards deliberately stop at generated leaves so app-authored
   values are never clobbered.
4. **The TextPresenter gap.** TextBox content ignores the attribute spine today and still will
   until the optional template-forward follow-on lands. Recorded, not entrenched.
5. **The TreeView NoColor selection cue is a follow-up (not yet landed).** `CapsNoColorSelectionInverse`
   covers ListBoxItem/ComboBoxItem/TabItem; the TreeViewItem cue lands later on the same
   control-level + face-forward pattern (§2.1's "can land as a follow-up"). Until then a NoColor
   TreeView's selected row shows no cue. (P3 audit, 2026-07-13.)
6. **Border-title underline SHAPE collapses to Single.** A titled `Border` (GroupBox idiom) folds
   the underline PRESENCE onto its `PanelTitle` but the shape rides at `Single` — `PanelTitle`
   carries no shape channel, and Q2's seam widening scoped v1 to the `DrawFormattedText`/DrawText
   paths (both label paths carry the shape; the box-title path does not). Rare; widening
   `PanelTitle` is a clean Drawing-layer follow-up if a real case appears. (P3 audit.)
7. **Glyphs/icons carry ONLY the Inverse cue** (owner rule, 2026-07-13): weight/style/underline are
   meaningless on a symbol, and Inverse alone keeps a glyph swapping fg/bg in unison with its face
   (no half-inverted hole). Consequence: a NoColor **disabled** control dims its label (Faint) but
   NOT its glyph box/icon (they are symbols) — a deliberate simplification of the review-#1
   whole-control-dims behavior.

---

## 8. Owner decisions (record of 2026-07-13)

1. **`TextWeight` vs `FontWeight` naming — DECIDED: `TextWeight`.** No WPF muscle-memory trap
   (`FontWeight="SemiBold"`/`"600"` would fail here); continuity with the `:680` pin's "no font
   types" rationale — the deviated name signals the deviated domain.
2. **Underline v1 scope — DECIDED: full `DrawFormattedText` support.** The base-style widening
   (underline shape through the formatted-text seam) ships in the same phase as the property
   (§3.1, §5 P2); no deferred-semantics property, no silent drop. `UnderlineBrushProperty` stays
   demand-gated.
3. **Flow model — DECIDED: all nine NON-inheriting ("flows like `Background`").** Usage leans
   virtually exclusively to element-level application; control-level cues reach their consumers
   through the existing forwarding idioms (per-axis `TemplateBinding`s on parts;
   `ContentPresenter` forwarding onto the presentation leaves it generates — never onto
   `DataTemplate` content). This deletes the panel's entire inheritance perf apparatus (walks,
   batched fold, reparent gate, §2.9 pressure) and unifies the cue model's delivery with its brush
   half. Accepted trades, recorded in §7.3: app item-template text gets no ambient attributes
   (styled directly instead), and templates carry the per-axis forwards their parts consume. The
   panel's uniform-INHERITING recommendation and its demotion-gate machinery are superseded; kept
   in the judgment doc for the record.
4a. **Posture axis shape (amendment, 2026-07-13) — DECIDED: `TextStyle { Normal, Italic }`**
   instead of the judge's Italic-as-bool: `Text*`-prefix discoverability of the axis family, and
   enum headroom for possible future terminal text standards (SGR 20 fraktur as precedent).

4. **The commented-out XAML accent `:focus-visible` setters — DECIDED: delete, with the real
   reasons recorded.** (a) Mechanical: at color tiers the non-occluding face's glyph-transparent
   tint drops attributes on glyphless cells (`Border.cs:151-162`) — Inverse inverted only the
   label's cells, so the look was faked with brush swaps, the correct color-tier idiom (whole-face
   Inverse would require `Occludes=true`). (b) Principled, and decisive even where (a) doesn't
   bite: color-tier sibling states (`:pointerover`/`:pressed`) cue through brush swaps, and an
   attribute cue composing over swapped brushes yields derived, undesigned colors — **one cue
   vocabulary per tier.** Reviving via the split cue pair would be a no-op at color tiers
   (`InteractiveCueInverse=false` there) and redundant at NoColor (the live `.caps-nocolor`
   button-family rules already apply Inverse). See §2.3's ground-truth note.

---

## 9. Provenance

Produced by the house design-panel process (2026-07-13): a scout inventory of every registration,
read/write, theme, XAML, markup, and test touchpoint (verified file:line evidence; it also
surfaced that `Cursorial.UI.Dialogs` — TaskDialog/CommandLink — exists but is not yet in
CLAUDE.md's project list); three independently-authored proposals (WPF-kinship decomposition,
uniform terminal-native booleans, keep-the-group-with-merge-semantics); and an adversarial
judgment (`judgment-textattributes-decomposition.md`) that verified the proposals' load-bearing
claims against the code — including falsifying one perf claim ("never-contributed properties
resolve without walking") that shaped the panel-era design. All three proposals independently
converged on per-axis decomposition with paint-time folding.

**The owner redirect (same day):** the panel uniformly assumed the axes inherit (matching today's
aggregate); on review the owner observed that real usage is virtually exclusively element-level
and directed the **non-inheriting** flow model this document now records (§0/§2.1/§8 Q3) — which
deleted the panel's heaviest engineering (the batched inheritance walk, its equivalence gate, and
the reparent demotion gate) in exchange for the forwarding spine and the §7.3 scoping rule. The
judgment doc retains the panel-era analysis, including the inheritance-cost verification that
still documents why inheriting-by-default would have been expensive.

---

## 10. Rider: the XAML hex-color convention fix (owner-requested, 2026-07-13)

**The inconsistency.** The house convention for alpha is RGBA, alpha-LAST: `Color.FromRgba`,
`Color.TryParseHex` (8-digit → `#RRGGBBAA`, the in-flight Core change this rider depends on),
`MarkupColor` (delegates to it), and `StyleDiagnostics.FormatValue` (prints
`#{R:X2}{G:X2}{B:X2}{A:X2}`). The one outlier is the XAML converter:
`XamlConverters.ParseHex` (`Cursorial.UI.Xaml/Conversion/XamlConverters.cs:460-483`) parses
8-digit hex as `#AARRGGBB` (the WPF convention, alpha-first). Its comment also claims a 4-digit
`#ARGB` form that was never implemented (4-digit throws — the comment lies).

**The fix (lands in §5 P2):**

1. `ParseHex` delegates ALL hex forms (3/6/8-digit) to `Color.TryParseHex` — one parser, one
   convention, no duplicated digit logic; the converter keeps its contextual diagnostic, with the
   error text updated to `expected #RGB, #RRGGBB, or #RRGGBBAA` and the false `#ARGB` comment
   removed (a 4-digit `#RGBA` shorthand is Core's call to add later; the converter inherits
   whatever `TryParseHex` accepts).
2. **Value migration — the complete known blast radius:** `IndigoDusk/Palette.xaml:103,159` —
   `ObscuredOverlayBrush` `#60000000` (α=0x60 black under AARRGGBB) → `#00000060`. IndigoDusk is
   WIP/not-shipping, so this is opportunistic, but it rides along since the rider redefines the
   digits' meaning. An implementation-time grep for 8-digit hex across `.xaml` and string
   literals re-verifies nothing new appeared.
3. **Test re-pin:** the Section07 converter row pinning `#80ff0000` (alpha-first) re-pins to the
   RGBA reading (e.g. `#ff000080` for the same color) — a deliberate matrix amendment, recorded
   in `xaml-matrix.md`'s color-converter row alongside the convention note ("XAML hex is
   `#RRGGBBAA` — a deliberate DEV from WPF's `#AARRGGBB`: one alpha convention across the whole
   stack; `FormatValue`'s output is round-trippable into the converter").
4. **Generator:** zero work — the converter is runtime-shared (`XamlConverters.For`), so both the
   reflection loader and the emitted provider change together; the X174 drift row already covers
   the parity.

**Dependency:** Core's `Color.TryParseHex` (the owner's in-flight `Color.cs`/`MarkupColor.cs`
change) must land first; the rider is otherwise independent of the decomposition phases and can
land the moment P2 opens the converter file.
