# Fork A Judgment — Property System (Requirement 9)

**Judge lens:** implementation cost, performance, maintainability.
**Inputs:** all three proposals in full; design-doc.md, drawing-core.md, rendering-session.md, input.md, animation.md (read in full); repo conventions (oracle-pinned tests, additive lower layers, readonly-record-struct vocabulary, single render thread, re-composite vs re-raster split).

---

## 1. Scores

10 = best in class for this lens. Scores are system-level (engine + the work it forces onto sibling forks), not store-internal only.

| Criterion | P1 WPF-faithful | P2 Avalonia-style | P3 Hybrid |
|---|---|---|---|
| Realistic effort to build & test | 5 | 7 | 7 |
| Alloc/CPU at terminal scale (animation frames, pseudo-class flips, restyling storms) | 6 | 8 | 9 |
| Memory per element | 9 | 6 | 8 |
| Complexity of invariants the implementer must hold | 5 | 7 | 5 |
| Risk of subtle bugs (priority interactions, retraction/restoration, namescope-class bugs) | 6 | 7 | 5 |
| Degradation under later requirements (virtualization, a11y, hot reload, DevTools) | 7 | 8 | 7 |
| Trimming / AOT trajectory | 9 | 8 | 6 |
| **Total** | **47** | **51** | **47** |

---

## 2. Adversarial findings

### Proposal 1 — "WPF-faithful"

**Flaws & risks**

1. **It imports WPF's actual historical bug nest while claiming to shed complexity.** The pitch is "two small structures" (pipeline + entry array), but `ModifiedValue` × `IsCurrent` × `IsCoerced` × `IsExpression` × expression `TrySetValue` two-way handshake is precisely the state-space where WPF's subtlest bugs lived. "Small at terminal scale" conflates *data* scale (hundreds of elements — true, irrelevant) with *state-space* scale (combinatorial modifier interactions — unchanged from WPF). The proposal keeps all of it: SetCurrentValue semantics, expression offer/detach, coercion-sees-animated, chained metadata merge, sealing rules.
2. **Largest test surface of the three, undersold.** Its own mitigation is "every `BaseValueSource` pair × {set, clear, invalidate, animate, coerce}" — with a 10-bucket ladder that is a ~500-cell matrix before modifier interactions. The 2,500–3,500 LOC estimate for registry + DOT + metadata + storage + pipeline + expressions + inheritance + listeners + a styling-seam conformance kit is the most optimistic claim in any of the three documents.
3. **The pull seam (`TryGetNonLocalBaseValue`) splits precedence correctness across two forks.** The engine "enforces only their relative order" — it cannot; it trusts the reported bucket. Seven of the ten precedence levels live behind a contract the engine can't check at runtime. The conformance kit is a good mitigation, but this is the design's structural maintainability risk: the most bug-prone behavior in any property system (trigger-exit restoration) is implemented *outside* the system that defines its semantics.
4. **The boxing defense uses an invalid baseline.** "≤1,000 boxes/sec… beneath measurement noise next to the per-frame `ArrayBufferWriter` the demos already allocate" — rendering-session.md explicitly says a framework should pool that writer. Comparing your steady-state garbage to an allocation the framework is expected to eliminate is not a defense. The absolute number (~24–32 KB/s gen0 worst case) is honestly quantified and probably acceptable, but it is the worst steady-state profile of the three, in a project whose stated bar is "per-frame allocs add up at 50 fps."
5. **Boxed `Equals` on every gate.** The equality gate — load-bearing, correctly identified as such — runs `object.Equals` on boxed record structs per write (type-check + unbox + field compare). Cheap individually; it is, however, the hot path of the hottest write (animation), and the typed designs do the same gate with `EqualityComparer<T>.Default` and zero boxing.
6. **Rhetorical asymmetry in the steelman.** It attacks Avalonia for "maintaining both a typed and untyped surface" while itself maintaining *only* the untyped one, making `GetValue<T>` a cast veneer. The criticism is symmetric; only one side of it is priced.

**Genuinely strong and worth keeping:** `AffectsComposite` as a first-class metadata flag with the storyboard contract "offset/opacity/clip animations must never mark `AffectsRender`" — the sharpest terminal-specific insight in any proposal, directly grounded in the design doc's re-composite/re-raster split. Also: the oracle-pinned precedence matrix written *before* the engine; the `Boxes` interning cache; the richest `GetValueSource` diagnostics; best memory-at-rest; best trim/AOT shape (no generics to speak of).

### Proposal 2 — "Avalonia-style"

**Flaws & risks**

1. **Memory claim is understated by roughly an order of magnitude.** `EffectiveValue<T>` carries four `T` slots; the proposal's own per-entry range is 40–250 B. At its own scenario (500 elements × ~8 touched properties) that is ~160 KB–1 MB, not "low single-digit KB×10s." For `Style`-typed properties (~56 B struct) every touched entry is ~250 B. The fat-T mitigation (side allocation) is gestured at in risk (e) but not designed. Not fatal at terminal scale, but the claim should not have survived self-review.
2. **Lazy-read inheritance puts a tree walk on the render path.** Every `GetValue(Foreground)` on an element with no local contribution walks `_inheritanceParent` ~8–12 levels, binary-searching each ancestor's table — per read, forever, including reads inside re-raster draw delegates and restyling storms. "The walk is cheaper than maintaining per-descendant cache entries" is asserted, never quantified. Probably fine at hundreds of elements; it is still the design's weakest hot path and the one place P3 is clearly superior.
3. **The ref-struct change-args carrier has a hole.** `UIPropertyChangedEventArgs` "wraps the in-place entry" — but inherited-value changes fire on descendants that *have no entry* (that's the point of lazy-read). The notification pipeline for inheritance needs a second carrier the proposal never designs. Solvable, but it means the headline "allocation-free notification" trick doesn't cover one of the four notification channels as specified.
4. **`SetCurrentValue` punted on a thin argument.** "Last-writer-wins at LocalValue covers the main scenario" covers binding-push-after-local-set; it does not cover a control reflecting user interaction *without* establishing a local value that outranks a style/trigger (toggle state under a styled checkbox), nor coercion-modified two-way write-back. This will be re-added under pressure from the first real TextBox; better to design it now (P3 did, in two sentences).
5. **Frames are the risk concentration, by its own admission**, and within-priority ordering ("later-added wins; the styling fork expresses specificity by add order") makes frame *re-ordering* on specificity changes an implicit re-add/remove protocol that isn't specified. Restyling storms = frame churn; `DeferNotifications` helps with notification storms, not frame-list churn itself.
6. **Minor oversells:** "Avalonia shipped a decade of real apps on five tiers" (Avalonia's tier set and its `TemplatedParent` machinery are larger than five in practice); `RegisterAttached` host-type validation is DEBUG-only (fine, but it's presented as a feature).

**Genuinely strong:** the *mechanism has a real-world oracle* — the effective/base two-slot split plus frames is where Avalonia 11 converged after three rewrites, so the design starts at a proven optimum rather than at a predecessor (P1) or at novelty (P3). Typed end-to-end with zero steady-state animation allocation. The cross-fork contracts are *compile-time artifacts* (`BindingEntry<T>`, `AnimatedValueHandle<T>`, `ValueFrame`, `IValueObserver<T>`) rather than prose contracts — the strongest maintainability property in any of the three: the styling/binding/animation forks physically cannot hold the seam wrong in the ways P1's pull seam and P3's one-winner clause permit. Restoration-on-deactivation — the classic bug class — is owned *once, in the store*, where it can be exhaustively tested.

### Proposal 3 — "terminal-optimized hybrid"

**Flaws & risks**

1. **One-winner-per-priority quietly rebuilds the value store inside the styling fork.** To deactivate a trigger, the styling fork must compute and write the runner-up — which means it must *retain* per-element, per-property knowledge of every suppressed setter. That is a second value store, with the restoration bug class (the thing all three proposals agree ad-hoc designs get wrong) reimplemented outside the tested engine. The proposal's own anti-INPC argument — "you don't avoid building the value store; you build three bad ones, scattered" — applies to its own §5.2 contract. The mitigation ("widen the slot bits later") is additive in the key encoding but not in the semantics: within-priority frames would also need entry lists and an eviction story, i.e., most of P2's frames.
2. **Novel semantics with no oracle, in exactly the places bugs live.** Coercion-as-slot-15 has unexamined corners: a coerced *default* (Slider `Min=10`, default `Value=0`) creates a stored entry for an otherwise-unset property — `IsSet` now answers true, `GetValueSource` needs a second search to skip slot 15, and `ClearValue` semantics around it are unspecified. The inheritance push-down has a stale-cache-under-shadow invariant the proposal never states: descendants beneath a shadowing entry stop receiving pushes, so when the shadow is *cleared*, the un-shadowed subtree's `_inherited[slot]` is stale and must be re-pulled and re-pushed — a re-entry path that is specified for reparenting (`SetInheritanceParent` "pulls + diffs") but not for `ClearValue` of an inheritable. These are findable in review, but "no oracle + novel slot semantics" is where the repo's own history says the hard bugs come from.
3. **`PropertyEffects` omits the single most important terminal flag.** Render/Arrange/Measure/ParentArrange/ParentMeasure — no `Composite`. The design doc is explicit that re-composite (cheap, cached raster) vs re-raster (expensive) is *the* invalidation split, and the proposal itself leans on it in §4/R10 — yet an animated offset property registered with `Render` would re-raster at 50 fps. One-line fix; real internal inconsistency.
4. **Generic-virtual `OnPropertyChanged<T>` is the worst AOT pattern present in any proposal.** GVM dispatch over many value-type instantiations means per-(type,T) dispatch tables on NativeAOT and dictionary-lookup dispatch on CoreCLR. "Measured noise" is claimed with no measurement, and the fallback is gestured ("a non-generic pre-check") rather than designed. P2's non-generic virtual taking a `ref struct` with typed accessors gets the same zero-alloc result without the GVM.
5. **Headline oversells.** "Typed end-to-end, zero boxing on every hot path" — its own storage table says cold value-typed local writes box once per write (fine, but the headline and the fine print disagree). "~1.5 kLOC one person can audit in an afternoon" — contradicted by its own six-phase plan, three storage forms, eviction protocol, and novel slots. "~300 B heavily styled" omits the boxes' own footprint (~2× understatement). The embedded `ValueStore` struct also puts ~40 B into *every* `UIObject` including never-styled ones — small, but the "zero footprint at defaults" claim is only true relative to heap allocations.

**Genuinely strong:** best steady-state runtime profile of the three — `ValueCell<T>` in-place mutation for engine writers, interned boxes, O(1) inherited reads via shared-box push-down (one box per change for an entire subtree — elegant), typed `in`-passed change structs, lazy untyped boxing only when an untyped watcher exists. `SetCurrentValue` and `IValueEvictionListener` are the cleanest binding-lifecycle primitives in any proposal. `FrozenDictionary` metadata tables. The frame-coherence argument (§6.4 — no dispatcher tiers, values set in frame N's input drain visible to frame N's render) is the best articulation of a real terminal advantage and should be a documented invariant of Cursorial.UI regardless of which store wins.

---

## 3. Ranked verdict

1. **Proposal 2 (Avalonia-style)** — wins on the lens. It is the only design where (a) the *mechanism* has a shipping oracle (Avalonia 11's converged effective/base + frames store), not just the semantics; (b) the steady-state animation path is allocation-free without novel storage forms; and (c) the cross-fork seams are typed, compile-checked handles rather than prose contracts — which is where multi-fork projects actually rot. Its real flaws (memory math, inherited-read walk, missing inherited-args carrier, punted `SetCurrentValue`) are point fixes inside one component, not architecture.
2. **Proposal 3 (hybrid)** — best raw performance and the smallest store, but it spends its simplicity budget twice: once on novel semantics with no oracle (slot-15 coercion, stale-shadow inheritance, eviction), and once by exporting the restoration problem to the styling fork. At this lens's weighting, "fastest" loses to "fewest places the subtle bugs can hide." Several of its best ideas are detachable and should be stolen (below).
3. **Proposal 1 (WPF-faithful)** — a close third, and the best *document* in terms of self-awareness, but it has the largest build-and-test surface, the worst steady-state allocation profile, and the structurally riskiest seam (precedence split across forks via a pull contract). Its claim that "this design's risks are implementation-effort risks" is exactly the problem under an implementation-cost lens: it keeps WPF's full modifier state-space, whose complexity does not shrink with element count.

---

## 4. RECOMMENDATION

**Build Proposal 2's architecture, amended with a specific graft set from Proposals 3 and 1.** Concretely:

**Adopt from P2 as the spine:** `UIProperty` / `StyledProperty<T>` / `AttachedProperty<T>` / `DirectProperty<TOwner,T>`; the `ValueStore` with the effective/base split and priority frames; `BindingPriority` with Animation above Local; `BindingEntry<T>`, `AnimatedValueHandle<T>`, `ValueFrame`, `IValueObserver<T>`; `GetBaseValue<T>`; `DeferNotifications`; lazy-read/eager-notify inheritance (ship the simple version first); the P0–P3 phasing.

**Mandatory amendments (conditions of the win):**

1. **Add `SetCurrentValue<T>` in P0–P1**, with Proposal 3's two-sentence semantics (replace the effective value in place without changing its source; no entry ⇒ behaves as Local). Do not wait for the TextBox to force it.
2. **Adopt `PropertyEffects`-style flags (P3) including `Composite` (P1's framing) as the standard metadata payload** the tree fork's `AffectsRender<T>`/`AffectsMeasure<T>`/`AffectsComposite<T>` helpers consume, with P1's storyboard contract documented verbatim: offset/opacity/clip animations route to Composite, never Render. The property system stays rendering-blind (P2's seam); the flags are just data.
3. **Design the inherited-change notification carrier now** (the ref-struct args cannot wrap a nonexistent entry on descendants) — a second small carrier or a synthetic stack-local holder; specify before P2 of the build.
4. **Re-validate the memory model:** measure `EffectiveValue<T>` for `Style`-sized `T` and implement the fat-T side-allocation split (P2's own risk-e mitigation) if entries exceed ~96 B.
5. **Specify within-priority frame ordering as a stable, re-orderable contract** (specificity changes = documented re-add protocol), and ship P1's seam-conformance test kit repurposed for `ValueFrame` implementers.
6. **Write P1's oracle-pinned precedence matrix before the store** — every priority pair × {set, clear, bind, unset, frame-activate/deactivate, animate, coerce}, pinned against WPF/Avalonia where semantics are shared. Five tiers makes this matrix actually finishable; it is the repo's established discipline and the cheapest insurance available.

**Explicitly rejected:** P1's 10-bucket ladder and expression-in-local-slot two-way handshake (P2's `BindingEntry` + observers is the simpler equivalent); P3's coercion-as-slot-15 (coerce inside `SetEffective` per P2); P3's generic-virtual `OnPropertyChanged<T>` (P2's ref-struct virtual achieves the same with better AOT shape); P3's one-winner-per-priority styling contract (frames own restoration).

---

## 5. GRAFT LIST

**From Proposal 3 (hybrid):**
- `SetCurrentValue` semantics, verbatim (§2.4) — the tightest spec of the historically subtle corner.
- **Shared-box push-down inheritance with O(1) reads** — keep as the documented, API-compatible upgrade path if profiling shows the lazy walk on the render path (it changes only `ValueStore` internals). Record the stale-shadow re-pull invariant when adopting.
- `FrozenDictionary` per-type resolved-metadata tables + one-element inline cache (§3.6).
- Box interning (`BoxCache`: bools, small ints, enum zeros, per-property default boxes) for the untyped XAML/diagnostics lane.
- **Frame-coherent synchronous dispatch as a named invariant** (§6.4) — "a property set during frame N's input drain is visible to frame N's layout and render; no dispatcher tiers exist." Put it in the UI design doc's §0.
- `IValueEvictionListener` — hold in reserve; if the binding fork wants explicit detach-on-overwrite signals beyond P2's last-writer-wins, this is the shape.
- GC-count assertions around animated frames as regression tests (§7 P4) — matches the repo's empirical-pinning culture.
- The 4-bit-slot packed-key trick — irrelevant to the frames store, but the "spare bits = additive headroom" framing is worth keeping in the doc's rejected-alternatives section.

**From Proposal 1 (WPF-faithful):**
- **`AffectsComposite` and its §5.4 storyboard contract** — the best terminal-specific idea in the fork; adopt the concept and the prose.
- The precedence test matrix written before the engine, oracle-pinned (§7 risk 1).
- The seam conformance kit concept (§7 risk 4) — repurpose for `ValueFrame`/styling-fork conformance.
- Rich `ValueSource`-style diagnostics (`GetValueSource` returning base source + IsAnimated/IsCoerced/IsCurrent) for DevTools — P2's priority-only answer is too thin for tooling.
- The `Boxes` cache idea (overlaps with P3's interning; one implementation serves both lanes).
- `AddOwner` metadata-merge rules pinned to WPF (changed callbacks chain base-first; defaults replace) — P2 names the mechanism; P1 specifies the rules; take the rules.
- The documented-cut convention applied to the priority ladder: record "theme/template-trigger buckets cut; re-addable additively" with reasons, per the repo's §9/§11 discipline.