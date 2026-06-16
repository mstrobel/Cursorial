# Fork B Judgment — Styling Model (Requirements 1, 3, 8)

**Judge lens:** requirements coverage & architectural coherence (cross-fork composition, stack-invariant consistency, cross-platform soundness).
**Inputs:** all three proposals in full; `/tmp/cursorial-ui-maps/design-doc.md`, `drawing-core.md`, `rendering-session.md`, `input.md`, `animation.md`.

---

## 1. Scores

| Criterion | P1 wpf-triggers | P2 avalonia-selectors | P3 hybrid |
|---|---|---|---|
| Req 1 — rich styling & templating | 8 | 8 | 9 |
| Req 3 — resource/style inheritance | 9 | 7 | 9 |
| Req 8 — setters + triggers/selectors | 9 | 7 | 9 |
| Cross-fork composability (Fork A store ↔ styling ↔ XAML/templates ↔ bindings) | 9 | 7 | 9 |
| Consistency with existing stack invariants & terminology | 8 | 8 | 9 |
| Cross-platform terminal soundness | 9 | 9 | 9 |
| **Total** | **52/60** | **46/60** | **54/60** |

Scoring notes (strictness applied):
- **P1 Req 1 = 8, not 9:** cross-element/theming reach is conceded in its own §8 — styling unnamed descendants outside templates requires the inherited-attached-flag workaround, and per-element keyed-style assignment is the variant mechanism. That is a real partial on "similar to WPF *and Avalonia*."
- **P2 Req 3 = 7:** plain `Style` has no `BasedOn` (only `ControlTheme` does) — definitional inheritance for rules is "compose via cascade," which is weaker than what both rivals offer; `ThemeVariant` is a bare string key with no color-depth tier (compensated by capability classes, but resource-level tiered fallback in P1/P3 avoids rule duplication for depth-specific palettes).
- **P2 Req 8 = 7:** the DataTrigger replacement (`Classes.Bind` on elements, or laundering VM state through properties for `[Prop=Value]`) moves the data condition out of the style and onto every element instance. P3's critique is correct: N buttons need N bindings where one `When` style covers the type. Requirement 8's "or" is technically satisfied; data-driven styling coverage is genuinely second-class, and I am instructed to call out partial satisfaction.
- **P2 Cross-fork = 7:** three seam gaps detailed in §2 below (namescopes, deferred activation drain, presenter punt).

---

## 2. Flaws, risks, unsupported claims (adversarial pass)

### Proposal 1 — "WPF Triggers"

**Concrete flaws:**
1. **Template-trigger-on-templated-parent ambiguity.** §2.1 declares `TemplateSetter/TemplateTrigger` slots are used *only on template part elements* and "the templated parent itself only ever receives Theme/Style/Local/Animation values." But §2.3's `Trigger` inside `ControlTemplate.Triggers` with no `TargetName` must set *something* — in WPF that's the templated parent at ParentTemplateTrigger priority. Under this slot table that write has no legal slot. The "one table, no asterisks" claim hides an unresolved corner.
2. **`EventTrigger : TriggerBase` inherits `EnterActions`/`ExitActions`** it cannot honor ("owns no setters, retracts nothing"). WPF has the same wart (throws at runtime); the proposal neither forbids nor defines it.
3. **`Cursorial.Output.Style` name collision unaddressed.** The proposal introduces `Cursorial.UI.Styling.Style` into a codebase where `Style` is the load-bearing SGR record consumed by every rendering call. Resolvable, but a proposal this detailed should have caught it (P3 did).
4. **Cycle guard is per-element.** Depth-32 counter catches self-cascades; a cross-element loop through an inherited attached property (its own recommended descendant-styling mechanism) won't trip a per-element counter deterministically.
5. **Ten priority slots is the heaviest possible demand on Fork A's storage** — req 9 asks for *efficient* storage; per-property holder lists across 10 slots, pooled, allocation-free, is a tall contract the proposal merely "assumes" (§5.1).

**Unsupported claims:** "well under a millisecond" theme flip and "zero allocation" hover are unbenchmarked and the latter is contingent on Fork A pooling it does not control (acknowledged, to its credit). "Avalonia's issue tracker documents years of 'why doesn't my selector apply'" — rhetorical, uncited. The "selector sugar can be added later as pure compilation onto triggers" claim is glib: selectors over *tree structure* (descendant/child combinators) do not compile to property-watch triggers without exactly the matching engine it declined to build.

**Real strengths to honor:** the most precise Fork A contract of the three (`IPropertyValueStore` holder model, read-only keys, `GlobalIndex`); the most complete template machinery (`ITemplateContent`, `TemplateInstance.Detach` retraction, namescope, `TargetName`/`SourceName` with load-time validation); diagnostics designed first; the only proposal with a full `EventTrigger`/`BeginStoryboard` ignition surface for req 10.

### Proposal 2 — "Avalonia selectors"

**Concrete flaws:**
1. **Data-driven styling is structurally second-class** (scored above). The §4 parity table's DataTrigger row is the weakest cell in any proposal's matrix: "bind a class per element" is a per-instance mechanism standing in for a per-rule one. Avalonia's own ecosystem (Behaviors' `DataTriggerBehavior`) keeps reinventing this — P3's steelman §8 lands the punch cleanly.
2. **Deferred activation drain breaks read-after-write coherence.** Activator notifications queue and drain "at a defined point in the UI tick" (§3.5). Code that sets a class (or a control that flips a pseudo-class) and immediately reads a styled property observes the stale value until tick end. WPF/Avalonia apply triggers synchronously; nothing in the proposal defines the observable semantics inside the window, and tests/imperative code will hit it. P3 gets the same batching benefit with an *explicit* scope (`BeginInteractionUpdate`) and synchronous default.
3. **Namescope gap.** §2.5 never defines `INameScope`; `#name` matching rides `StyledElement.Name`, but XAML `x:Name` registration, `GetTemplateChild`-style part lookup for control authors, and Fork C's part resolution are all punted ("full presenter design is Fork A's"). The judge instruction says templates need deferred content + namescopes — this proposal supplies the template *barrier* but not the namescope.
4. **`ITemplate` deferred-content seam is thin:** `ControlTemplate.Build` is a concrete `Func` the XAML fork must "compile into" — no interface seam equivalent to P1's `ITemplateContent` for a deferred node tree.
5. It carries the **full grammar** (`:not`, property selectors, selector lists) whose invalidation cost its own architecture absorbs — fine — but never engages P3's strongest argument (sibling/positional invalidation entangling the layout fork); it just defers `:nth-child` without the principled fence.
6. `Cursorial.Output.Style` collision also unaddressed.

**Unsupported claims:** "order-of-magnitude markup reduction" across a real theme — directional, never measured against an actual Cursorial theme; "~600-line span-based parser" — an estimate presented as accounting; "1–3 ms window-open" — no benchmark; "match-once / activate-forever" oversells — class changes re-run Phase-1 matching for the element (admitted in §3.3's attach flow but not in the headline).

**Real strengths:** the activator/frame engine is the best-engineered hot path of the three and is essentially what P3 builds on; capability classes on the root (`:root.caps-ansi16`) re-stamped on `RenegotiateAsync` is the single most elegant terminal-native idea in any proposal; the template barrier is a crisp encapsulation rule; "the engine never touches Scene/CellBuffer — it cannot violate the compositing invariant" is exactly the right relationship to the §0 invariant.

### Proposal 3 — "principled hybrid"

**Concrete flaws:**
1. **Factually wrong about WPF precedence.** §3.8: "`TemplateBinding` lives at `Template` priority so both page styles and local values on parts beat it — the WPF behavior people actually expect." WPF's actual lattice puts template values *above* style values on parts. The chosen lattice (Style > Template) is **Avalonia's** (StyleTrigger > TemplatedParent), and it only stays safe because the template barrier keeps unconditional outer styles off parts. The behavior is defensible; the justification is false and must be rewritten before it calcifies in a design doc. **[RESOLVED 2026-06-16]** The `BindingPriority.Template` lane landed (precedence-matrix §20/PD24, design doc §2.2/§3.4) and its header states the truth plainly: Style > Template is the **deliberate inverse of WPF** (a control's parts should be re-skinnable by an app's styles), *not* "what WPF does". The safety argument is now explicit: the template barrier keeps *non-`/template/`* outer styles off parts, while a `/template/`-crossing style (Style-priority) deliberately overrides a part's Template-lane authored value — which is the whole point of the lane.
2. **`Theme(2)` vs `ControlTheme(0)` layer distinction is asserted, never defined.** What populates the `Theme` layer if control themes are layer 0 and app styles layer 3? Underspecified.
3. **`When` binding sources underspecified:** "evaluated against the *target element's* DataContext / source" — whether self-property and ancestor-element sources are expressible depends entirely on Fork A's `BindingBase`. Without self-source bindings, arbitrary-property triggers (WPF `Trigger Property=X Value=Y` for unmapped, non-bool properties) have no equivalent — the WPF-parity claim in §8 leans on `PseudoClassMapping`, which only covers properties the *control author* registered. This needs to be a stated requirement on Fork A, not an assumption.
4. **Armed `When` watchers are live subscriptions** even when the style never activates — N rules × M structurally-matched elements of standing binding traffic. Acknowledged with a deferral, but it's the one place the "zero steady-state cost" story leaks.
5. **Specificity is a real import of CSS cognition** — the packed sort key (`[layer][names][classLike][types][scopeDepth][order]`) is a sixth thing to learn alongside layers. Mitigation is tooling, which is the right answer, but P1's "one slot table, observable" critique applies partially here too.
6. Deferring selector lists (`,`) is over-curation: a list compiles to N `CompiledRule`s sharing a `Setter[]` — near-zero engine cost for a real ergonomic gain.
7. "Phase S3 ≈ 1 week" for `When` (watcher lifecycle + specificity integration + DataContext-change tolerance + reentrancy) is optimistic.

**Unsupported claims:** "smaller than either parent system alone" — the deletion inventory is honest, but the additions (specificity engine, ancestor-dependency bookkeeping, fixpoint queue, two condition vocabularies) make "smaller" closer to "comparable, better factored." "The WPF behavior people actually expect" — see flaw 1.

**Real strengths:** the unified activation predicate with **one priority slot + sort key + cookie-based batch retraction** is the cleanest property-system contract offered and the lightest load on Fork A (req 9); the only proposal that caught the `Cursorial.Output.Style` collision; `Background`-not-inherited aligned with the compositor's transparency model; the access-key treatment (`:access-keys` bit, capability-gated exactly per input.md §7, permanent-on fallback) maps requirement 6 onto pure styling with the most precise capability conditions of the three; `InteractionState` bitmask + `PseudoInterestMask` early-out is the cheapest hot path; the generation-counter loop diagnostic names the offending rule pair instead of throwing at depth 32.

---

## 3. Ranked verdict

**1. P3 — hybrid (54/60).** It covers the union of the requirement surface: selector reach for theming/cascade (reqs 1, 3) *and* in-style data conditions (req 8's DataTrigger power) — each the thing one parent system gets wrong. Its cross-fork contract is the most coherent: one `Style` slot with an internally sorted entry list and cookie batch retraction is both the lightest demand on Fork A's storage and the closest to a production-proven shape (Avalonia's `BindingPriority`). It shows the deepest awareness of the actual codebase (name collision, transparency model, exact Kitty/Win32 access-key gating). Its flaws are documentation errors and underspecified corners, not structural defects.

**2. P1 — wpf-triggers (52/60).** The most rigorously specified proposal — best template lifecycle, best diagnostics narrative, most precise Fork A interfaces — and fully compliant with req 8's WPF branch. It loses on requirements 1/3 reach (cross-element theming is conceded and the workaround is ceremonious), on the 10-slot precedence table it itself admits is WPF's most-litigated wart (with a fresh ambiguity of its own at the templated parent), and on day-to-day density. Beaten, not refuted.

**3. P2 — avalonia-selectors (46/60).** The best *engine* and the best single terminal-native idea (capability classes), undermined by second-class data styling, an asynchronous activation drain with undefined intra-tick semantics, and punted namescope/presenter seams that the XAML and control-author stories need. Its architecture survives in P3, which is effectively this engine with the grammar pruned and `When` added.

---

## 4. RECOMMENDATION

**Adopt Proposal 3 (hybrid) as the Fork B styling model**, with the graft list below and four mandated fixes before the design doc freezes:

1. Rewrite the Template-priority rationale: the lattice is Avalonia's (`Style > Template` works *because of* the template barrier), not WPF's. Document the deviation per the project's "deliberate deviation, recorded with reasons" convention.
2. Define the `Theme` layer or delete it from `StyleSortKey`.
3. Promote "self-source and ancestor-source `BindingBase`" from assumption to a numbered Fork A requirement (C-item), since WPF-trigger parity for unmapped properties depends on it.
4. Un-defer selector lists (`,`) — they compile to N rules sharing setters; the deferral buys nothing.

This is not a compromise pick: P3's "one predicate, one slot, one sort key, one retraction path" is the only design here where requirement 8's two halves (state triggers, data triggers), requirement 3's three inheritance axes, and requirement 9's storage efficiency all land in a single mechanism — and where the §0-style spine ("clean retraction = cookie removal + promotion, never set-back") is enforced in exactly one place.

---

## 5. GRAFT LIST

**From P1 (wpf-triggers):**
- **`ITemplateContent` deferred-content interface + `TemplateInstance` lifecycle** (§2.6): replace P3's concrete `Func<TemplatedControl, INameScope, Element>` ctor with the interface seam so the XAML fork can supply a deferred node tree, and adopt the `Detach()` contract (retract all template-sourced values, dispose bindings/subscriptions, with a debug leak tracker). This is the strongest template-instantiation spec in any proposal.
- **`EffectiveValueReport` shape** (§2.8): `(Priority/SortKey, Value, Contributor, RuleIndex, ResourceKey)` — fold into P3's `StyleDiagnostics.Explain` so provenance includes the *resource key* a dynamic value came through; plus the in-terminal style-inspector overlay as a Demo command (SSH-grade debuggability is a genuinely terminal-specific requirement P1 articulated best).
- **`BeginStoryboard`/`StopStoryboard` action surface with `HandoffBehavior`** as the named Fork-D-facing contract for frame activation/retraction edges — P3 ceded event→storyboard wiring; adopt P1's vocabulary for it so req 10's ignition has a concrete shape.
- **Read-only enforcement principle:** interaction state must never be settable from app code at local-value priority (P1's read-only property keys → P3's `PseudoClassSet` already protected; write the rule down as an invariant).
- **Load-time seal validation detail:** errors name the style, rule index, and property (P1 §3.1) — adopt verbatim as P3's seal contract.

**From P2 (avalonia-selectors):**
- **Capability classes stamped on the visual root** from `TerminalCapabilities` (`caps-truecolor|ansi256|ansi16|nocolor`, `caps-motion`, `caps-kitty-keyboard`, `caps-unicode|ascii`), re-stamped on `RenegotiateAsync` — composes with P3's grammar as ordinary class simples and covers cases ThemeVariant tiers can't (per-rule capability adaptation). The best terminal-native idea in the fork.
- **Type-keyed candidate cache with permanent `NeverThisType` exclusion** per `Styles` collection (§3.2) — complements P3's `StyleIndex` and makes repeated attaches of the same control type near-free.
- **Pause-not-destroy subscription lifecycle on deactivation** (§3.5): hover flicker must not churn binding/resource subscription objects — extend P3's `When`-watcher caching to setter bindings and `DynamicResource` subscriptions.
- **`Classes.Replace` batch swap** with single notification, and the **modal-dimming idiom**: window manager sets an `obscured` class on windows behind a modal so dimming is a theme rule, not a compositor hack — hand this to Fork C as the composition contract.
- **The "styling never touches Scene/CellBuffer" guarantee**, stated as an invariant: the engine only raises property changes; control metadata drives `Scene.Invalidate()` — write it into P3's §5 guarantees verbatim.
- **Zero-allocation exit criterion in tests** (`GC.GetAllocatedBytesForCurrentThread` assertions on the flip path) — adopt as Phase S1's gate, per the project's oracle-pinning habit.