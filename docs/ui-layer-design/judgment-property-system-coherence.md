# Fork A Judgment — Property System
**Judge lens: requirements coverage & architectural coherence**
Sources verified against `/tmp/cursorial-ui-maps/design-doc.md`, `input.md` (§7 access-key conditions), `animation.md`, `drawing-core.md`, `rendering-session.md`.

---

## 1. Scores

| Criterion | P1 "WPF-faithful" | P2 "Avalonia-style" | P3 "terminal-optimized hybrid" |
|---|---|---|---|
| Requirements coverage (strict) | **9.0** | 7.5 | 8.5 |
| Cross-fork composability (styling / XAML / binding / animation) | 8.0 | **8.5** | 7.5 |
| Consistency with stack invariants, idioms, terminology | 7.0 | **9.0** | 8.5 |
| Cross-platform soundness (Win/macOS/Linux terminals) | **9.0** | 9.0 | 8.5 |
| **Total** | 33.0 | **34.0** | 33.0 |

The totals are close because the proposals are genuinely complementary: P1 is the most *complete*, P2 is the most *coherent*, P3 is the most *tailored*. The verdict therefore rests on which weaknesses are graftable and which are foundational (§3).

**Coverage notes.** P1 covers every R9 sub-requirement plus `SetCurrentValue`, read-only keys, `GetValueSource`, `LocalValueEnumerator`, `FromName` — nothing punted that matters. P2 punts `SetCurrentValue`, never sketches the untyped `SetValue` it claims for XAML, has no value-source diagnostics, and enforces read-only by convention only (`DirectProperty` with no setter) — several explicit partials. P3 is nearly as complete as P1 in typed form (SetCurrentValue, keys, eviction, `maxPriority` reads) but **omits the composite-invalidation lane from `PropertyEffects`** — a strict miss against the design doc's headline animation pattern (see §2).

**Composability notes.** P2's frames give store-owned restoration on trigger/selector deactivation — the property the styling fork most needs by construction. P1's pull seam also restores correctly on re-pull but trusts a foreign virtual to sub-order seven buckets (mitigated by its conformance-kit idea). P3 exports *within-priority* arbitration and runner-up restoration to the styling fork — the single riskiest cross-fork bet on the table.

**Coherence notes.** P2 and P3 match the stack's `readonly record struct` / typed / no-Rx / allocation-discipline ethos; P1's boxed-`object` foundation is the one mechanism in any proposal that cuts against it. P1 alone encodes the design doc's re-composite-vs-re-raster invariant in metadata (`AffectsComposite`); P3 cites that invariant and then fails to encode it; P2 delegates it ("build AffectsRender/AffectsMeasure/AffectsComposite on metadata callbacks"), which is clean layering.

---

## 2. Adversarial findings

### Proposal 1 — "WPF-faithful"

1. **Boxing is a one-way architectural door, not an implementation cost.** `GetValue(dp) → object?`, `PropertyChangedCallback(object?, object?)`, boxed storage — this is the *public API*, so it can never be retrofitted to typed without breaking every consumer. The proposal's own asymmetry argument ("our risks are implementation risks; theirs are architectural") is exactly backwards: WPF's precedence *semantics* are portable test oracles usable on any chassis; the boxed *mechanism* is the unportable part.
2. **The allocation defense leans on a strawman baseline.** "Beneath measurement noise next to the per-frame `ArrayBufferWriter` the demos already allocate" — `rendering-session.md` explicitly says "a framework can pool/reuse one (just `Clear()` it)." The demos' laziness is not the framework's noise floor. (The absolute number, ~24–32 KB/s, is still genuinely small — but the argument as written is unsupported.)
3. **Cherry-picked equality-gate math.** "An animated slide at 50 fps moving 10 cells/sec fires ~10 changes/sec" holds only for cell-quantized interpolators (`Int32`/`Rect`/`Size`). `Color`, `double`, and brush animations change every frame; those are the common cases for pulse/fade effects.
4. **Uncited authority claims.** "By its own maintainers' repeated admission" (Avalonia ValueStore intricacy) — no citation; rhetorically load-bearing in the rebuttal.
5. Typed callbacks don't exist: control authors cast boxed old/new in every change handler — discordant with a codebase that passes `in Rect` and `ref struct` everywhere.
6. Minor: `EnumerateInheritanceChildren(dp, Action<DependencyObject>)` invites closure allocation in the cascade; the seven-bucket seam contract is large for a styling fork to implement correctly (the conformance kit is a good mitigation and should survive regardless of winner).
7. Genuine strengths verified: the `BaseValueSource` ordering is in fact WPF-faithful (Style < TemplateTrigger < StyleTrigger < Template < TemplateParentTrigger < Local); the access-key gating matches `input.md` §7 including focus-out clearing; `AffectsComposite` correctly encodes the design doc's "slide/fade re-composites a cached scene; only an animated brush re-rasters."

### Proposal 2 — "Avalonia-style"

1. **`SetCurrentValue` punted — and history says it will be needed.** The punt note ("add if TextBox-style controls need binding-preserving internal writes") describes a certainty, not a contingency: requirement 2 ("powerful data binding") plus any interactive control (TextBox, ScrollBar, ToggleButton) hits the case where an internal write must not promote to a binding-killing/style-shadowing local value. Avalonia itself added `SetCurrentValue` in 11.0 after living without it. Punting the historically subtlest primitive doesn't remove the problem; it defers the design to a worse time.
2. **The `ref struct` args carrier has a real reentrancy bug shape.** `GetOldValue<T>()` reads `entry.Previous`, "valid only during a synchronous notification window" — but reentrancy is explicitly allowed. A handler that sets the *same* property reentrantly overwrites `Previous` while the outer notification is still in flight; the outer handler's subsequent `GetOldValue` returns corrupted data. WPF/P3 avoid this by copying values into the args. As specified, this is a latent correctness defect, not a style nit.
3. **The untyped XAML surface is claimed, not designed.** §4 asserts "untyped `SetValue(UIProperty, object?, priority)`" — it appears nowhere in the API sketch (§2.3 has only untyped `GetValue`). No local-value enumeration for serialization, no `GetValueSource` diagnostics. For a fork whose chassis must carry requirement 7, the boxed side door is under-specified.
4. **Lazy-read inheritance is on the wrong side of the read/write asymmetry.** Reads happen during re-raster (the expensive, hot path per the design doc); a `Foreground` read walking 8–12 parents per element per raster is the one place this stack actually rereads in a loop. The "terminal trees are shallow" defense is plausible but unbenchmarked; P3's push-down/shared-box scheme gives O(1) reads for the same correctness.
5. Four `T` slots per `EffectiveValue<T>` quadruple storage for fat structs (`Style`, `Pen`); acknowledged, with a hand-waved mitigation.
6. Read-only enforcement is conventional, not structural — and read-only *attached* properties have no story at all.
7. Self-aware risk concentration: the ~900-LOC ValueStore with the effective/base split is precisely where Avalonia's own subtlest bugs lived; the proposal admits this and correctly invokes the repo's adversarial-review convention.

### Proposal 3 — "terminal-optimized hybrid"

1. **One-winner-per-priority exports the engine's core job.** The proposal's own rebuttal of INPC says it: "value restoration *is* a priority store." Cross-priority restoration is automatic (`ClearValue(priority)` rescans), but *within-priority* restoration is not: when the winning trigger of two active triggers deactivates, the styling fork must compute and write the runner-up. That is per-(element, property, priority) bookkeeping — a mini value store rebuilt in the styling fork, in exactly the precedence-bug territory P1 identifies as the classic failure mode. If Fork B's strongest proposal assumes store-side restoration (both parent frameworks provide it; any Avalonia-shaped styling fork will), P3 composes worst of the three. The "widen the slot bits later" mitigation is additive but unproven.
2. **No `AffectsComposite` despite citing the invariant.** `PropertyEffects` = Render/Arrange/Measure/ParentArrange/ParentMeasure. The design doc's strongest UI constraint (§7: slide/fade re-composites a *cached* scene; only an animated brush re-rasters) has no metadata lane: an offset/opacity animation routed through `PropertyEffects.Render` re-rasters every changed frame — the exact failure P1 names as "the difference between free and the whole frame budget." The claim that the equality gate "composes perfectly with `SceneCompositor.Composite` returning `false`" only covers frames where the quantized value *didn't* change; it oversells. Fixable in one enum member + widget-fork routing, but as proposed it's a miss against the doc the proposal quotes.
3. **Collapsed single `Trigger` level** merges style triggers and template triggers — combined with one-winner-per-priority, the styling fork now arbitrates template-trigger vs style-trigger conflicts too. Each collapse is individually defensible; their conjunction compounds finding 1.
4. **LOC estimate is optimistic.** 1.2–1.5 kLOC for registry + frozen metadata tables + packed-key store + cells + eviction + coercion slots + inheritance push-down + watchers + untyped paths + `SetCurrentValue`, when P1 budgets 2.5–3.5k for comparable semantics. The honest read is ~2× the claim.
5. Coercion-as-priority-slot-15 is novel machinery in the most safety-critical corner (the proposal flags this itself); inherited-value coercion semantics are unspecified (P2 at least documents "inherited reads skip coercion").
6. Generic-virtual `OnPropertyChanged<T>` carries acknowledged JIT-dictionary costs and unacknowledged NativeAOT wrinkles — minor, but the only platform-shaped wrinkle in any proposal.
7. The Avalonia-history narrative ("each rewrite moved away from observables toward flat frames") is broadly accurate but conveniently truncated: the converged design *kept frames*, which P3 then declines to adopt.

---

## 3. Ranked verdict

1. **P2 "Avalonia-style"** — the right chassis. Typed end-to-end matches the stack's `readonly record struct` vocabulary; no-Rx matches the stack's idioms (and is argued from them, not just asserted); binding-as-producer-at-a-priority is the cleanest unification of requirements 1/2/8/10; and frames put restoration-on-deactivation — the thing every other fork depends on being correct — inside the store, by construction. Its real defects (no `SetCurrentValue`, the `Previous`-slot reentrancy hazard, thin untyped surface, lazy-read inheritance) are all **graftable** without disturbing the chassis.
2. **P3 "terminal-optimized hybrid"** — the best-tailored mechanism (packed-key table, cells, push-down inheritance, frame coherence, the crispest seam contracts: eviction, `maxPriority`, `SetCurrentValue`) and the best parts list to raid. Loses second-to-first on the one-winner-per-priority contract, which shifts the engine's defining correctness obligation onto the styling fork — the worst cross-fork bet among the three — plus the `AffectsComposite` omission.
3. **P1 "WPF-faithful"** — the most complete requirements matrix, the best invalidation metadata, the best test program, and the most precise access-key/window-inheritance treatments. Ranked last for one reason: its irreplaceable part is the boxed-`object` foundation, and that is the only foundational element in any proposal that contradicts the stack's established character. Everything uniquely valuable in P1 — semantics, flags, oracles, ladder ordering — is portable; its mechanism is not. P1's own asymmetry argument, inverted, is the verdict.

---

## 4. Recommendation (decisive)

**Adopt Proposal 2's architecture as the chassis — typed `UIProperty`/`StyledProperty<T>`/`DirectProperty`, the `ValueStore` with priority frames, binding-as-producer, `AnimatedValueHandle<T>` — with mandatory grafts from Proposals 3 and 1 folded in before P0, not deferred:**

From **P3** (engine-seam hardening):
- `SetCurrentValue<T>` with P3's two-sentence semantics (in-place effective overwrite, source-preserving; no entry ⇒ Local). Non-negotiable for the binding fork.
- The **copied-value change carrier** (`readonly struct` with `T OldValue/NewValue` fields, passed by `in`) replacing the ref-struct-over-entry design — closes the reentrancy corruption hole at the cost of one struct copy.
- `IValueEvictionListener` as the explicit binding-death notification.
- `GetValue<T>(property, maxPriority)` generalizing `GetBaseValue` (handoff + trigger-exit introspection in one primitive).
- **Push-down inheritance with shared boxes** in place of lazy read-time walks (O(1) reads on the raster-hot path; eager notify already required for selectors either way). Internal change; no API impact, so it may land as a P2-phase swap if benchmarks demand staging.
- `UIPropertyKey<T>`-style structural read-only enforcement alongside `DirectProperty` (read-only *styled/attached* state — `IsKeyboardFocusWithin` — needs the key, not a convention).
- Box interning (`BoxCache`) for the untyped XAML lane; frozen per-type metadata tables; the source-generator sugar as a late, optional phase.

From **P1** (metadata + verification program):
- The full `FrameworkPropertyMetadataOptions`-style effects set **including `AffectsComposite`** and `AffectsParentMeasure/Arrange`, built on P2's metadata `Changed` channel as P2 already sketches — with `AffectsComposite` specified in the design doc as the routing for offset/opacity/clip animation targets (design-doc §7 compliance).
- The **oracle-pinned precedence test matrix written before the engine** (every priority pair × {set, clear, frame add/remove, activate/deactivate, animate, coerce} with expected effective value *and* expected notifications), plus the **styling-seam conformance kit** shipped with the frames phase.
- The XAML completeness items P2 hand-waved: a fully specified untyped `SetValue(UIProperty, object?, BindingPriority)`, value-source diagnostics (`GetValueSource`), and local/frame-value enumeration for serialization.
- P1's access-key wiring as the reference recipe (inherited attached `bool`, gated on `DistinguishesKeyUpDown && (ReportsRepeats || Protocol.Win32InputMode)` per `input.md` §7, permanently `true` otherwise, cleared on `FocusEvent { HasFocus: false }`).

**Explicitly rejected:** P1's boxed storage foundation (irreversible API decision against the stack's grain); P3's one-winner-per-priority contract (frames carry within-priority ordering instead); P3's coercion-as-slot-15 (keep coercion in the effective-value computation, P2-style — less novelty in the most delicate corner); any `IObservable` surface (all three agree).

This composite satisfies all ten requirements with no punts in the property layer itself, keeps restoration semantics inside the store where the styling and animation forks need them guaranteed, and stays idiomatically continuous with `Cursorial.Drawing`/`Animation` (typed structs, `in` parameters, equality-gated change detection mirroring `CompositeParameters` diffing, single-thread-by-contract with debug asserts).

---

## 5. Graft list (from the losing proposals, beyond the mandatory grafts above)

From **P1 (WPF-faithful):**
- `DependencyObjectType`-style cached type identity for integer/reference-compare metadata resolution.
- The metadata **merge rules** as a pinned spec: changed-callbacks chain base-first; coercion replaces; `Inherits` immutable across the type lattice (all three converged here — write it down once).
- The popup/child-window inheritance redirect pattern (inheritance parent → placement target / owner window) as the documented requirement-5 recipe; P2's `SetInheritanceParent` already carries the mechanism.
- `SetValue(dp, UnsetValue) ≡ ClearValue` as the untyped-lane clearing convention.
- The "reentrancy permitted, equality gate terminates convergent cycles, debug depth assert" contract wording — clearest of the three.
- The phase-exit criterion style ("the precedence matrix passes for Default/Local") for the UI layer's phase table.

From **P3 (hybrid):**
- The **frame-coherence guarantee** as a documented invariant of the dispatch design ("a property set during frame N's input drain is visible to frame N's layout and render") — a real differentiator vs desktop dispatchers; deserves a named place in the UI design doc.
- The packed-key `(index << bits) | slot` trick — useful *inside* frame/entry tables even on the P2 chassis.
- `ValueCell<T>`-style owner-tagged mutable slots as the documented mechanism guaranteeing zero steady-state garbage for binding pushes (P2 guarantees it for animation; extend the guarantee to binding writes).
- GC-count assertion tests around animated frames (the repo's empirical-pinning discipline applied to allocation claims).
- The diagnostics hook for rejected binding/frame values (`OnRejectedValue`-style) — better than silent discard.
- The spare-bits/additive-evolution note: reserve headroom in priority encodings so cut WPF rungs (theme-style tiers, template-parent triggers) can return additively if a theming ecosystem ever materializes.