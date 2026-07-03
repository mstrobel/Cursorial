# Fork B Judgment — Styling Model (lens: implementation cost, performance, maintainability)

Judge basis: all three proposals read in full; verified against `/tmp/cursorial-ui-maps/design-doc.md`, `drawing-core.md`, `rendering-session.md`, `input.md`, `animation.md` (scene invalidation granularity, single-render-thread rule, `Rect` ushort constraints, the additive-only lower-layer rule, the access-key capability matrix, and the demos' allocation discipline all factored into scoring).

---

## 1. Scores

All criteria scored 1–10, higher is better. "Effort" = realistic build+test cost (inverted: higher = cheaper).

| Criterion | P1 wpf-triggers | P2 avalonia-selectors | P3 hybrid |
|---|---|---|---|
| Build + test effort | **6** | **4** | **7** |
| Hot-path cost (pseudo-state flips, steady state) | **7** | **9** | **9** |
| Restyle storms / attach / theme swap | **9** | **7** | **8** |
| Memory per element | **7** | **6** | **8** |
| Invariant complexity an implementer must hold | **6** | **4** | **7** |
| Subtle-bug risk (priority, retraction, namescopes) | **7** | **4** | **7** |
| Degradation under later reqs (virtualization, a11y, hot reload) | **6** | **7** | **8** |
| Trimming / AOT trajectory | **9** | **6** | **7** |
| **Total** | **57** | **47** | **61** |

Notes on the two non-obvious cells:

- *P1 restyle storms = 9*: it is the only design with **zero matching cost at attach** — implicit style resolution is a type-keyed dictionary hit. At virtualization-recycling rates this is its single strongest property.
- *P2 effort = 4*: P2's own §7 calls its engine "the single most intricate piece of Cursorial.UI." I agree, and it also carries the heaviest Fork A coupling (the `IValueFrame` contract, below).

---

## 2. Adversarial findings per proposal

### Proposal 1 — "WPF Triggers"

**Flaws and risks**

1. **The taxonomy is the cost center.** `Trigger`, `MultiTrigger`, `DataTrigger`, `MultiDataTrigger`, `EventTrigger`, `Condition` (dual property/binding form), `EnterActions`/`ExitActions`, `EventSetter`, `BeginStoryboard`/`StopStoryboard` with `HandoffBehavior` — each with pinned semantics, seal validation, and a test matrix. P2's rebuttal is correct that triggers need activator-equivalent machinery anyway (watch maps, met-counts, binding watchers, retraction); P1 spends what it saves on matching on breadth instead. It builds strictly *more total system* than P3 for the same three requirements, because it also owns the event→storyboard wiring P2/P3 cede to the animation fork.
2. **Pseudo-state-as-properties taxes the hottest input path.** Any-event motion tracking (`MouseEventKind.Move`, on by default per the input reference) means every cell crossing writes `IsPointerOver` through Fork A's store on the old and new hit chains — store entry churn, change notification, watch-map probe — *even on elements with no hover styling*. P3's `PseudoInterestMask` bitmask exits in one AND with no store touch. Both are microseconds; P1's is measurably more per-move work and more per-element store residue.
3. **Memory claim undercounts.** "~64 bytes per scope" ignores the `ResourceSubscriptionRegistry` entries — and themed elements with `DynamicResource` setters (the normal case for every palette brush) each carry one registry node per dynamic setter. Real per-element styling overhead is closer to P2/P3's honest numbers.
4. **"Selector sugar can be added later, compiling to triggers" is half-true.** Only subject-state selectors (`Button:focus.primary`) compile to triggers. Descendant/structural selectors do not — §8 itself concedes structural reach is permanently traded for inherited attached flags. The forward-compat claim oversells; the cross-element gap is a one-way door.
5. **Inherited-attached-flag descendant styling shifts cost to theme authors.** `theme:Density.Compact` works only if every targeted control's style *pre-wires a trigger on that flag*. Themes accrete a blessed-flag vocabulary; third-party styles that didn't anticipate a flag can't be reached. Maintainability of the shipped theme degrades over time exactly where selectors stay flat.
6. **Namescope machinery is the bug farm.** `TargetName` + `SourceName` + `EventTrigger.SourceName`, resolved per template instance, with partition tables and per-part listener registration — this is the classically litigated WPF surface (dangling part listeners after re-templating, stale namescope captures). The proposal's leak tracker is the right mitigation but the surface is the widest of the three.
7. **10-slot lattice.** Simplified vs WPF, yes, and the "template slots only on parts" rule is genuinely better — but it is itself an asterisk to remember, and Fork A must implement and test a 10-level lattice vs P3's 6.

**Unsupported claims:** "theme swap well under a millisecond" (unbenchmarked, acknowledged); "~64 B/element"; "selector sugar later, no rewrite."

**Genuine strengths to credit:** the §0 retraction invariant stated as holder-removal (never save/restore) is exactly right; diagnostics designed in §2.8 from day one with exact provenance; the best AOT story (no string DSL anywhere); cheapest attach path; WPF gives an external behavioral oracle for testing, which matters in a project that pins oracles.

### Proposal 2 — "Avalonia selectors"

**Flaws and risks**

1. **Largest engine, largest invariant set.** Parser, six activator node types, descendant residues with `OrActivator` per candidate ancestor, `:not` inversion, `[Property=Value]` observation, selector lists, candidate caches with invalidation, frame realize/pause lifecycle, a batching ring buffer with a defined drain point, the template barrier, `^` nesting chained into parent compiled nodes. Each is individually reasonable; the *product* of their interactions (e.g. `.toolbar:not(.compact) Button` under an ancestor class flip during a batched drain) is the largest test matrix on the table — at a scale (10² elements) where, by P2's own §7 numbers, most of this engineering buys nothing the simpler designs don't already have.
2. **The Fork A seam is the riskiest of the three.** `IValueFrame`/`AddFrames`/`ReevaluateFrame` inverts ownership: the store must scan priority-sorted frames, track per-frame active state, call back `ValueAt(index, target)`, support batched add/remove and *deferred reentrancy*. Effective-value caching — load-bearing, since property *reads* (layout, render) vastly outnumber writes — is unspecified and lands on Fork A. P2 admits frames are "the blast radius" if the store shifts. P1's holder model and P3's cookie sink are both smaller, more conventional contracts.
3. **The DataTrigger answer is structurally weaker.** `Classes.Bind("urgent", binding)` relocates the condition to *per-element* markup/code: N list items need N bindings declared on elements, where one `When`/`DataTrigger` style covers the type. The lifecycle (disposal on detach, rebind on DataContext change) is unspecified — a retraction-leak class waiting to happen. The rebuttal's "laundering is a feature" argument has merit for *named, reusable* states, but the mechanism's cost accounting is hand-waved.
4. **Attach is not one-time under virtualization.** "1–3 ms per 200 elements, window-open cost" becomes a recurring cost when a virtualized list recycles realized containers at scroll speed. Frame/activator allocation per attach (sealed classes, graphs) churns precisely where the project's allocation discipline bites. Mitigable (per-type frame caching) but unplanned.
5. **Static-fact contract pushed to Fork A:** `Name` immutable after attach, re-parent = detach/attach — reasonable, but it is a tree-lifecycle constraint another fork must enforce forever so this engine's match-once premise holds.
6. **Overclaims:** "order-of-magnitude markup reduction" (true per-rule in XML lines, not in code-first C#); "~600-line parser" (with `:not`, lists, `:is`, `/template/`, positions, and canonical `ToString` round-trip, closer to double that with tests).

**Genuine strengths to credit:** the match-once/activate-forever split is the correct architecture (P3 adopts it); the **template barrier** is the best-specified encapsulation rule of the three; **capability classes on the root** (`:root.caps-ansi16`) is the single best terminal-native idea in the fork; theme-variant flip as a pure resource event with *no re-match* is the cheapest variant-switch design; "no specificity arithmetic" is a defensible simplification (though slot-split `StyleTrigger > Style` is a mini-specificity with its own cross-scope surprise).

### Proposal 3 — "principled hybrid"

**Flaws and risks**

1. **Specificity is a real, imported cognitive cost.** The packed `StyleSortKey` is cheap to implement but the *rule* — DataConditions count as class units; layer dominates specificity dominates order — must be learned by every theme author and pinned by oracle tests. Edge: `Button` + two `When` clauses ties `Button.primary:pointerover` (classLike 2 vs 2) and falls to declaration order — correct but non-obvious. The proposal's mitigation (ship `Explain` in S1) is right, but score it as cost, not zero.
2. **The template barrier is unstated.** §3.3/§3.8 define `/template/` hopping, but never explicitly state that ordinary rules *skip* elements with a non-null `TemplatedParent` (P2 §2.5 states it precisely). Without that rule, app styles leak into template internals and the encapsulation story collapses. Almost certainly intended; it must be specified and tested.
3. **`When` lifecycle has a visible unresolved seam.** The proposal contains a literal mid-sentence self-correction about watcher parking ("watchers park while structurally matched but never active? No —"). Watchers live-while-armed is the stated design (honest cost note included), but DataContext-change behavior, initial-value-unknown semantics (unset binding = unmet?), and the S3 "≈1 week" estimate are optimistic. Budget 2 weeks and pin "unknown ⇒ unmet" explicitly.
4. **Layer-first precedence diverges from WPF/Avalonia intuition.** In P1/P2, conditional beats unconditional across scopes (trigger-slot > setter-slot); in P3, a nearer-scope unconditional rule beats a farther-scope conditional one (layer outranks specificity). Concrete consequence: an element-scoped `Button { Background=X }` kills the app theme's `:pointerover` feedback. Defensible ("nearer always wins" is more predictable) but it is a deliberate semantic choice that must be documented as such — and it will surprise WPF migrants.
5. **Non-bool / unmapped property conditions have a gap.** WPF's `<Trigger Property="Orientation" Value="Vertical">` requires either a control-registered `PseudoClassMapping` classify or a `When` self-source binding — the latter depends on Fork A's `BindingBase` supporting element-self sources (assumed, not contracted).
6. **Minor spec gaps:** which host the `Theme(2)` layer binds to (theme-bundle `Styles` vs app `Styles` needs a marker); `StyleSortKey` field widths (classLike 10 bits, order 27 bits) need overflow guards at seal.

**Unsupported claims:** "Phase S3 ≈ 1 week"; "smaller than either parent system alone" (true on type count by its own §8 inventory, but it still ships *both* a matcher *and* a data-condition system — the honest claim is "smaller than the sum, not smaller than each").

**Genuine strengths to credit:** the unified activation predicate (one frame type, one `UnmetCount`, one cookie retraction path) is the smallest invariant core of the three; `ActivationFrame` as 32-byte structs in arrays + interned ints + bitmask early-out is the best storage/hot-path engineering; the single Style slot gives Fork A the simplest lattice (6 levels); frame-diff-by-rule-identity on class change (cookies and watchers survive) is the right virtualization-friendly primitive; the grammar cuts are each justified by an *invalidation-graph* argument, not taste — exactly the right razor; the up-front `Cursorial.Output.Style` name-collision resolution and the `:access-keys` mapping to the input reference's exact capability conditions show the closest reading of the actual stack.

---

## 3. Ranked verdict

1. **P3 — hybrid (61).** Smallest invariant core, best hot path, best per-element storage, cheapest Fork A contract, honest cost accounting, and the cuts are principled (everything that breaks element-local invalidation is out). Its weaknesses — specificity cognition, the unstated template barrier, `When` lifecycle looseness — are specification gaps, fixable in a design-doc revision, not architectural defects.
2. **P1 — wpf-triggers (57).** Architecturally sound and the most debuggable/AOT-clean design, with the §0 retraction invariant stated best of the three. It loses on totals: it builds the most machinery (full taxonomy + storyboard actions + namescope plumbing) for permanently less reach (no structural styling), and the pseudo-state-as-properties choice taxes the mouse-move path the terminal stack makes hot by default. At equal hot-path engineering, P3 covers P1's feature matrix (property trigger ⊂ pseudo-class mapping; MultiTrigger ⊂ compound; DataTrigger ⊂ When) with fewer mechanisms.
3. **P2 — avalonia-selectors (47).** The right activation architecture wrapped in the wrong-sized grammar for this scale, with the riskiest Fork A seam and the weakest data-conditional story. P3 is essentially P2 with the web-scale parts amputated and DataTriggers restored — which is a strictly better trade under this lens.

## 4. RECOMMENDATION

**Adopt Proposal 3 (hybrid) as the Fork B architecture**, with mandatory pre-Phase-S0 revisions and the grafts below. Composition, precisely:

- P3's object model, single-slot `StyleSortKey` design, `IStyleValueSink` cookie contract, `ActivationFrame`/bitmask engine, `PseudoClassMapping`, and `When`.
- **P2's template barrier**, stated verbatim as a §0-grade rule, plus P2's capability-classes-on-root mechanism and its no-re-match theme-variant flip.
- **P1's diagnostics-and-validation discipline**: seal-time errors naming (style, rule, property); `Explain`/`MatchedRules` shipped in the first phase as P1's §2.8 does; the debug-build subscription leak tracker; the in-terminal style-inspector overlay demo.

Pre-S0 revisions required: (a) write the template barrier into the grammar spec; (b) pin `When` semantics — unknown binding value ⇒ unmet, watcher lifetime = armed lifetime, DataContext-change rebind; (c) document the layer-beats-specificity divergence from WPF/Avalonia with the toolbar-override example, as a deliberate decision per the project's §9 "resolved decisions" convention; (d) re-budget S3 to 2 weeks.

## 5. GRAFT LIST

From **P2 (avalonia-selectors)**:
1. **Template barrier rule** (§2.5) — selectors never match `TemplatedParent != null` elements without `/template/`; engine skips such elements *before* candidate scan (the perf win rides along).
2. **Capability classes on the root** — `:root.caps-ansi16`, `caps-motion`, `caps-ascii`, re-stamped on `RenegotiateAsync`; reuses the activator machinery for capability adaptation instead of a parallel system. Slots directly into P3's grammar.
3. **Theme-variant flip = resource event only, no re-match** — P3's theme swap does a theme-layer re-match; split the cases: variant flip (Dark→Light, depth tier) re-resolves `DynamicResource` subscriptions only; only Styles-collection mutation re-matches.
4. **Lazy frame realization** — bindings/resource subscriptions instantiated on *first activation*, paused-not-destroyed on deactivation (P3 already keeps `When` watchers; extend the reuse discipline to setter bindings and resource subscriptions).
5. **`StylesInvalidated` as the explicit hot-reload hook**, with frame-diff-by-rule-identity preserving cookies across the re-match (P3 has the diff; adopt P2's framing of mutation as a supported, coarse, documented operation).

From **P1 (wpf-triggers)**:
6. **Diagnostics-first ship discipline** — `EffectiveValueReport`-grade provenance (value, sort key, contributing rule, resource key) and the terminal-rendered style-inspector overlay as a named deliverable of the first phase, not a risk-mitigation bullet.
7. **Seal-time validation contract** — every error names the style, rule index, and property; conversion of setter constants and `When` comparands happens once at seal with a non-XAML fallback converter set (primitives, enums, `Color.FromHex`, `BrushMarkup`) so code-first is never blocked on the XAML fork.
8. **Debug leak tracker** asserting subscription-registry emptiness after tree teardown — aimed exactly at the retraction-leak class (template detach, `When` watchers, resource subscriptions).
9. **`HandoffBehavior.SnapshotAndReplace`** as the named contract for the animation fork's falling-edge storyboard handoff (P3 defers the mechanism; adopt P1's vocabulary so the cross-fork seam is nameable now).
10. **Read-only enforcement of interaction state** — P1's read-only property keys translate to: `PseudoClassSet`/`IInteractionStateSink` writable only by Fork C and control authors, with `Classes.Add(":x")` rejected (P3 has the rejection; add the "framework-services-only" enforcement note to the contract).

From **P2 and P1 jointly**: the steelman sections' shared kernel — *no specificity arithmetic beyond what tooling can explain in one line* — should be pinned as an acceptance test: `StyleDiagnostics.Explain` must render every winning value's full sort-key derivation in a single human-readable line, or the specificity design gets revisited.