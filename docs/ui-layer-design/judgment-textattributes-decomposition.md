# Adversarial Judgment — `TextAttributes` Decomposition (Proposals A / B / C)

**Judge lens:** the seven mandated criteria; house calibration per `docs/ui-layer-design/judgment-styling-coherence.md` (concrete flaws with evidence, unsupported claims named, strengths honored, ranked verdict + grafts).

**Preliminary finding that frames everything:** the three proposals are one design wearing three trims. All converge on: per-axis inherited `AttachedProperty`s on `TextElement`, paint-time fold into `Output.TextAttributes`, presence-based shadowing as the tri-state, retire the aggregate via a transitional OR-bridge, split `InteractiveInverseAttributes` into a per-axis resource pair, delete the `ControlThemes.cs:100` TemplateBinding with a parity test, keep `KeyAttributesProperty` aggregate, keep the markup-bake OR residue out of scope. That unanimity — reached independently and consistent with the code I verified — is itself strong evidence the decomposition is the right call. The judgment is therefore about the trims: the Bold/Faint axis, the underline model, naming, perf mitigation, and phasing.

## 0. Verification notes (checked against the worktree)

1. **The read path** (`UIObject.cs:84-96`): no own entry → `property.Inherits && FindInheritedEntry(...)` **unconditionally walks the parent chain** (`:763-776`), probing every ancestor's store; when *nobody* contributes it walks to the root and falls to default. There is **no** global "never contributed anywhere" short-circuit. This falsifies a Proposal C claim (below) and confirms A's and B's naive accounting: 9 never-set inheriting properties = 9 full-depth walks per text render.
2. **Reparent** (`UIObject.cs:705-732, 783-805`): `SetInheritanceParent` diffs **every** registered inheriting property — two chain walks each (old + new) even when both sides are empty (`:790-794`). +8 registrations is a real multiplicative tax; the code's own remarks (`:697-702`) name §2.9 push-down as the benchmark-gated cure. All three proposals state this correctly.
3. **Shadowing is presence-based** (`EffectivePriority: not BindingPriority.Unset`, `:89, :767`): an explicit `false`/`Normal` frame is a contribution and stops the walk. The "turn off an inherited flag with a `false`, `ClearValue` restores" story all three tell is **correct as-is, zero new machinery** — confirmed.
4. **`UnderlineStyle` has `Single = 0`, no `None`** (`TextAttributes.cs:53-62`) — confirms A's parallel-enum offset mapping and C's nullable rationale.
5. **`XamlConverters` unwraps `Nullable<T>`** (`XamlConverters.cs:70,81`) — C's verified claim checks out.
6. **`RenderContext.DrawFormattedText` carries `TextAttributes baseAttributes` only** (`RenderContext.cs:195-209`) — flags, no base `Style`. **Underline *shape* and *color* have no channel into the formatted-text path.** This is the decisive fact for the underline trims.
7. **`TextElement.cs:7-9`** doc comment claims AddOwner'ing that never happened — the scout's C119 drift confirmed; all three fix the doc rather than the code, correctly.

---

## 1. Scores

| Criterion | A (kinship) | B (uniform) | C (merge) |
|---|---|---|---|
| Composability of the motivating case | 9 | 8 | 9 |
| Coherence with lattice + store (machinery budget) | 9 | 9 | 9 |
| Per-flag inheritance incl. turn-off-inherited | 9 | 9 | 9 |
| Migration honesty (scout map = ground truth) | 8 | 9 | 8 |
| XAML/generator ergonomics | 8 | 9 | 8 |
| Render-path perf | 6 | 7 | 8 |
| WPF/Avalonia recognizability vs terminal fidelity | 9 | 7 | 8 |
| **Total** | **58/70** | **58/70** | **59/70** |

The margin is honest: these are trims of one sound design. The verdict rests on structural flaws vs graftable ones.

---

## 2. Adversarial pass

### Proposal A — kinship (`TextWeight` / `TextUnderline` / `Concealed`)

**Concrete flaws:**

1. **`TextUnderline` is a hand-maintained parallel enum.** Core's `UnderlineStyle` is shipped with `Single = 0` (verified); A's new enum shadows it with a `-1` cast offset (`(UnderlineStyle)(value - 1)`). That is a cross-layer invariant enforced by nothing — a Core addition is safe, but any reorder or a UI-side insertion silently corrupts the mapping, and the two enums will read as a redundancy to every future contributor. C's `UnderlineStyle?` achieves the identical semantics ("null = absent, value = shape") with zero duplication, and the nullable path through the converter ladder is verified working (`XamlConverters.cs:70,81`).
2. **`UnderlineBrushProperty` ships in the v1 table with no render seam and no plumbing budget.** `DrawFormattedText`'s base channel is flags-only (verified, `RenderContext.cs:195-209`); `ComposeUnderline` returning `(UnderlineStyle, Color)` has **no consumer that can use it** on the main text path, and sampling a `Color` out of an `IBrush?` requires geometry the signature doesn't carry (`ColorAt` needs a position). The same gap applies to A's unified shape property: `TextElement.Underline="Curly"` on a TextBlock folds to the presence *flag* and silently renders `Single`. A phase-plans neither the `DrawFormattedText` widening nor the honest deferral. B saw this; A didn't.
3. **The benchmark lands at P4, after the migration is mostly done** — self-confessed (self-critique #1). Given the verified full-depth-walk-per-never-set-property fact (§0.1), the 9× read amplification is real and *measured last*. The contingency ("one small internal ValueStore hook") is a sentence where C wrote a design.
4. Minor: the property table says nine properties but with `UnderlineBrush` it registers ten inheriting entries into the reparent diff set; the accounting text says "grows by 8 net." Sloppy, not load-bearing.

**Unsupported claims:** "nanoseconds against a paint that writes cells" — explicitly un-benchmarked, and self-flagged, to its credit. "No theme in the codebase ever wants Bold+Faint simultaneously (grep-verified)" — plausible and consistent with the scout inventory; accepted but unverifiable from the excerpt alone.

**Real strengths:** The **`TextWeight` axis argument is the best single design argument in any proposal** — SGR's shared reset 22 as "the wire's own testimony that these are one axis," making the Bold-vs-Faint conflict *arbitrable* (deterministic, `Explain`-able, PD26-governed) instead of composable-into-folklore. The **`Concealed` naming catch** (collision with `Visibility.Hidden` in the same framework, ANSI's own word) is exactly the kind of thing this codebase's conventions reward. The **refinement-not-reversal framing of the `:680` pin** (refuse WPF's `FontWeight` *struct* and 100–900 lie; adopt the *axis*; name it `TextWeight` to signal the different domain) is the most defensible doc-amendment story — it preserves the original pin's rationale instead of overwriting it. The enum-typed weight cue resource is strictly more expressive than B's bool (a future Bold-cue tier is a value change, not a third key). Honest self-critique #2 (the inert-`false` still occupies the conditional slot) is the most precise statement of the residual wart in any proposal.

### Proposal B — uniform (nine bools)

**Concrete flaws:**

1. **Bold/Faint as independent bools is the wrong trim, by the project's own criteria.** The concrete case: focused CTA button at `(Dark|Light, Ansi16)`, where the tier cue is Faint (verified in the scout: `CursorialTheme.cs:474`). App resting rule `Bold=true` (Style, 100); theme conditional cue `Faint=true` (StyleTrigger, 50). Under B these are *different properties* — no arbitration — and the fold emits `Bold|Faint`, whose rendering B itself calls "terminal-defined folklore." Under A/C the weight axis arbitrates: conditional Faint beats resting Bold while focused, deterministically, and `StyleDiagnostics.Explain` can say why. B's counter ("an intensity enum recreates the motivating bug in miniature") inverts the diagnosis: the motivating bug is clobbering across *unrelated* axes; Bold-vs-Faint is the *same perceptual axis*, and arbitrating same-axis conflicts is precisely what the lattice is for. B's own self-critique #2 concedes the point and offers a *downstream quantizer collapse* as the fix — new special-case machinery to pay for upstream uniformity, on a design whose banner is "no new machinery."
2. **The `SetTextAttributes` expansion helper (Phase 4) is a nine-frame clobber footgun.** It writes all nine flags as LocalValue frames — shadowing every inherited axis, the exact pathology being killed, now with no single `ClearValue` to undo it (nine clears required). Keeping `GetTextAttributes`/`SetTextAttributes` alive as aliases also keeps two vocabularies breathing after the design's stated bar is "one mental model."
3. **The bool cue pair is under-expressive.** `InteractiveFaintCue` can only say Faint; the weight dimension of the cue is locked to one value per key. A/C's enum-typed weight resource covers Normal/Faint/Bold with one key. B's own self-critique #3 identifies the pair-coherence burden but not the expressiveness gap.

**Unsupported claims:** "single-digit microseconds per element... well under a millisecond" — asserted, self-flagged. "The reason they were commentable-out was plausibly aggregate-clobber anxiety" (on the XAML accent setters) — speculation presented as motivation for the "enliven" recommendation.

**Real strengths:** **The most accurate perf accounting of the three** — B's "~9 × D presence checks," "the M266 fast path short-circuits only when *no* entry exists" matches the verified code exactly (§0.1), where C got it wrong. **The only honest underline phasing**: presence-bool now (folds into the existing flags channel with zero new plumbing — verified the channel is flags-only), shape/color later *when the seam exists*. This is the only proposal in which no property value is silently dropped in v1. The **P9.3b-as-live-demonstration** move (P3: implement the deferred Inverse+Bold ListBox focus cue in the Gallery canary as the composability exit proof) is the best phase-plan idea in any proposal — it converts the motivating scenario into a shipping, hands-on-testable artifact, matching the project's live-canary habit. The uniform-bool XAML surface (`Value="True"`) is genuinely the lowest-friction authoring story, and the three-stage retirement with the seal-time DEBUG diagnostic is thoughtful transition engineering.

### Proposal C — merge (WPF names, nullable underline, worked alternative)

**Concrete flaws:**

1. **Factual error at the center of the perf story.** C's mitigation #1: "The M266 fast path: a property never contributed anywhere resolves without walking... those reads are near-free." **Verified false** — `UIObject.cs:92` calls `FindInheritedEntry` whenever the object has no own entry, and the walk (`:763-776`) probes every ancestor to the root before falling to default. There is no global "ever contributed" flag. Never-set axes (Blink, Hidden, Overline...) are the *most* expensive reads, not the cheapest. The perf story survives only because mitigation #2 (the batched single-pass walk) independently fixes it — but a design record containing a false claim about the engine it rides on must be corrected before it calcifies (house precedent: the P3 WPF-precedence flaw in the calibration judgment — the behavior stood, the justification was rewritten).
2. **`FontWeight`/`FontStyle` name reuse with non-WPF domains is a muscle-memory trap.** `FontWeight="SemiBold"`, `FontWeight="600"`, `FontStyle="Oblique"` are the first things a WPF/Avalonia hand will type, and all fail. The `:680` pin's *words* are "No font converters" — C reverses the pin and then reintroduces the exact names the pin refused, with incompatible shapes. Recognizability that breaks on first use is worse than a new name that signals the different domain (A's `TextWeight`). `FontStyle` as a two-value enum is also pure ceremony over A/B's `Italic` bool.
3. **The underline shape channel gap, shared with A:** `UnderlineProperty` (`UnderlineStyle?`) ships v1; `ResolvedTextAttributes` dutifully carries the shape; **no formatted-text consumer can accept it** (verified flags-only seam). `Underline="Curly"` renders Single on every TextBlock in v1 and nothing in the phase plan funds the widening. C at least *deferred* `UnderlineBrush` for exactly this reason ("plumbing shape+color through DrawFormattedText's flags-only seam is the actual work") — and then didn't apply its own reasoning to shape.
4. **"A live bug this quietly fixes" oversells.** C's headline example — "an app's ambient Bold vanishes on focus at truecolor" — is *not* fixed: the cue rule still pins `FontWeight=Normal` at StyleTrigger while focused, so ambient Bold still vanishes. C's own fine print admits the cue rule "still owns those two axes while active"; what's fixed is the *other seven* axes (italic/underline/strikethrough now inherit through focus). Real improvement, wrong poster child. A described the identical behavior honestly as "no improvement on the inert-false wrinkle."

**Unsupported claims:** "each extra property costs... nothing on the hot read path" — only true given the batched walk, and even then it's one probe per node per unresolved axis, not nothing. "Semantics are identical by construction" for the batched walk — plausible, but the construction touches `ValueStore` internals (the Unset-priority sentinel, animation-lane effective values) and is promised an equivalence property test rather than shown; self-critique #1 concedes this fairly.

**Real strengths:** **The worked Angle-C rejection (§6) is the most valuable single section in any proposal and the only one that did the task's full job** ("or a defensible alternative"). The six-feature enumeration — winning-base observers/Transitions, `Explain`/S164, `SetCurrentValue`/PD27, typed zero-box push, the conformance kit + both matrices, the inheritance fork — is precise, matches the subsystems as documented, and produces the tally that should go in the design record verbatim: *a merge lane touches ~6 core store features to save ~40 mechanical call sites*. **The P1 pure-refactor compose-seam-first phasing** is the best migration structure offered: the fold lands byte-identical and pinned *before* any semantics change, giving every later phase a stable seam and the smallest reviewable diffs. **`UnderlineStyle?` reuse** kills A's parallel-enum drift hazard outright, and C verified the nullable converter path rather than assuming it. **The batched single-pass walk is designed in at P2 with a benchmark and an equivalence test** — the only proposal that treats the (verified-real) read amplification as a design input rather than a P4 surprise. The §2.2 lattice walk is the most precise of the three (it alone works the within-slot layer-field arbitration for the app-cancels-theme case).

---

## 3. Ranked verdict

**1. Proposal C (59/70).** It wins on the two things the others cannot graft in: the merge-lane rejection that completes the task's "defensible alternative" obligation with an evidence-backed enumeration, and a migration/perf architecture (compose-seam-first; batched walk designed in with an equivalence gate) that treats the verified read-amplification as a first-class constraint. Its flaws — the false M266 claim, the WPF-name trap, the shape-channel gap, the oversold bug-fix — are every one of them correctable by edit or graft without touching the structure. Per house precedent (the P3 WPF-precedence flaw), a sound mechanism with a false justification outranks a sound justification on a weaker mechanism.

**2. Proposal A (58/70).** The best *arguments* of the three — `TextWeight` from SGR 22, `Concealed`, the refinement framing of `:680` — attached to two structural warts (the parallel `TextUnderline` enum; `UnderlineBrush`/shape shipped v1 with no render seam) and the worst perf timing (benchmark at P4). Nearly everything valuable in A grafts cleanly into C. Beaten, not refuted.

**3. Proposal B (58/70).** The tied score flatters it: its unique strengths (underline phasing, perf honesty, the P9.3b demonstration) graft into any winner, while its central trim — Bold/Faint as independent bools — is the one *structural* wrong call in the field, conceded by its own self-critique and repaired only by downstream quantizer machinery its own thesis forbids. The most accurate proposal about the engine; the least right about the domain.

---

## 4. Synthesis recommendation

**Adopt Proposal C's structure with the following mandated fixes and grafts before the design doc freezes:**

*Mandated fixes to C:*
1. **Delete the M266 mitigation claim** and rewrite §3.2 on B's accounting (9 × D probes, never-set axes walk to the root — `UIObject.cs:92, :763-776`). The batched walk carries the story alone; the benchmark and the naive-vs-batched equivalence test stay at P2 as specified.
2. **Rename to A's vocabulary:** `TextWeight { Normal, Faint, Bold }`, `Italic` as bool (drop `FontStyle`), `Concealed` (drop `Hidden`). Record `:680` as A's *refinement* (refuse WPF's font-object model and numeric weights; adopt the axis shape; the deviated names signal the deviated domain), not C's reversal.
3. **Temper §2.3:** the cue rules still pin Inverse+Weight while active at every tier; what decomposition fixes is leakage on the *other* axes. Use italic/underline as the example, not Bold.

*Grafts from B:*
4. **Underline phasing:** either ship presence-only semantics in v1 (shape values accepted but documented as deferred) **or** fund the `DrawFormattedText` base-style/shape widening in the same phase the shape property ships. No silently-dropped values: `Underline="Curly"` must render Curly or be a compile-visible deferral. (Keep C's `UnderlineStyle?` type either way — it's the right type.)
5. **P9.3b as the composability exit proof:** the Inverse+Bold ListBox focus cue (`ControlThemes.cs:271`), implemented in the theme-migration phase, live in the Gallery canary.
6. **No aggregate survivors:** do *not* adopt B's `SetTextAttributes` expansion helper; the aggregate name dies with the property.

*Grafts from A:*
7. The enum-typed weight cue resource (`InteractiveCueWeight : TextWeight`) — already C's shape; keep it over B's bools.
8. A's self-critique #2 wording (the inert-`false` conditional-slot wart) goes into the pinned-decision record verbatim as the known residual, alongside the markup-bake OR residue all three document.

*Unanimous items to adopt without further debate:* delete the `ControlThemes.cs:100` TemplateBinding with the lane-change parity test; keep `KeyAttributesProperty` aggregate; reconcile the four-theme-file drift in the XAML phase; adversarial audit on the theme-migration and retirement phases per project policy. Add one small item no proposal had: a theme test walking every tier dictionary asserting **both** cue keys are present (the pair-coherence lint all three self-critiques asked for, at test-time cost instead of engine cost).

---

## 5. Open questions only the owner can decide

1. **`TextWeight` vs `FontWeight` naming** — I recommend `TextWeight` (muscle-memory safety, `:680` continuity), but WPF-name recognizability is a legitimate taste call the owner has exercised before (deliberate deviations must be argued; both sides have an argument).
2. **Underline v1 scope** — presence-only now (B's phasing, zero plumbing) vs funding the `DrawFormattedText` base-style widening so shape ships working. Cost is one seam widening in Drawing/RenderContext; benefit is no deferred-semantics property. Pick one; don't ship the silent drop.
3. **Do Blink/Concealed/Overline register as *inheriting*?** Uniformity says yes (all three proposals); the verified reparent tax (+2 chain walks per property per reparent, `UIObject.cs:728`) says they're the first demotion candidates if the P2/P3 reparent measurements (gallery page swap, items realization) regress. Decide the measurement gate now: what number forces either demotion or funding §2.9 push-down.
4. **The commented-out XAML accent setters** — delete (C: match current live behavior, code-first-only accent cues) or revive via the split resources (B: the XAML twins gain the resource indirection). This is a theming-parity policy question, not a design question.