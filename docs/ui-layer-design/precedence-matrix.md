# Fork A — oracle-pinned precedence matrix

Status: **normative test specification**, authored 2026-06-10 *before any engine code exists* (design doc §2.5, §14 P0, probe 2). Every numbered row below becomes exactly one xUnit `[Fact]`/`[Theory]` in `Cursorial.UI.Tests` (see the test authoring contract at the end). The engine is written *to* this matrix; a red row is an engine bug unless a PR amends this file first. Canonical semantics source: `docs/ui-layer-design.md` §2 (including the amendment ledger A1–A25) over `proposal-property-system-avalonia.md`; §0 invariants and §13 apply.

## 0. Conventions

### 0.1 Fixture

All rows assume a freshly constructed host unless the Setup column says otherwise.

| Symbol | Registration |
|---|---|
| `Host` | `UIObject` subclass; `DerivedHost : Host`; `OtherHost : UIObject` (AddOwner target) |
| `P` | `StyledProperty<int>` on `Host`, default `0`, no coerce/validate/changed |
| `Pc` | `StyledProperty<int>` on `Host`, default `0`, coerce = clamp to `[0,100]` |
| `Pc2` | `StyledProperty<int>` on `Host`, default `500`, coerce = clamp to `[0,100]` (default-not-coerced row) |
| `Pv` | `StyledProperty<int>` on `Host`, default `0`, validate = `v >= 0` |
| `Pcv` | `StyledProperty<int>` on `Host`, default `0`, validate = `v != 13 && v <= 150`, coerce = clamp to `[0,100]` (order-of-operations rows) |
| `Pi` | `StyledProperty<int>` on `Host`, default `0`, `inherits: true` |
| `Pcmp` | `StyledProperty<string?>` on `Host`, default `null`, metadata `Comparer` = `OrdinalIgnoreCase` |
| `Pa` | `AttachedProperty<int>` `RegisterAttached<Tab, Host, int>("Index")`, default `0` (declaring type `Tab` is not a `Host`) |
| `Pro` / `Kro` | read-only `StyledProperty<int>` + its `UIPropertyKey<int>`, default `0` |
| `Pd` | `DirectProperty<Host,int>` over field `_d`, setter present, `unsetValue: -1` |
| `F(k)` | minimal test `ValueFrame` subclass at `StyleSortKey` `k`; `k1 < k2 < k3`, larger sorts **stronger** (wins) |
| `E` | `BindingEntry<int>` from `Bind(P, LocalValue, listener)`; `EF` from `BindInFrame(P, frame, listener)` |
| `H` | `AnimatedValueHandle<int>` from `BeginAnimation(P)` |
| `root → mid → leaf` | three `Host`s chained via `SetInheritanceParent` (leaf's parent is mid, mid's is root) |

### 0.2 Notation

- `L(v)` = `SetValue(P, v)` (LocalValue); `SCV(v)` = `SetCurrentValue(P, v)`; `CV` = `ClearValue(P)`; `Co` = `CoerceValue(P)`.
- `F(k){P=v}` = frame at sort key `k` carrying an `IValueEntry<int>` for `P` with value `v`; `F(k){P=∅}` = entry present, `HasValue == false`. A plain `F` arbitrates in the RESTING `Style` slot; `Ft(k){P=v}` = a CONDITIONAL frame arbitrating in the `StyleTrigger` slot (PD26 — the fixture's `TestValueFrame(k, priority: StyleTrigger)`).
- `E.Set(v)` / `E.Unset()` / `E.Dispose()` = entry pushes; `H.Set(v)` / `H.Dispose()` = animation pushes.
- Expected cells: `eff` = `GetValue(P)`; `base` = `GetBaseValue(P)`; `src` = `GetValueSource(P)` as `Priority[+cur]` (`+cur` = `IsCurrentValue`); `IsSet` per A23.
- `notify(old→new, Pr)` = exactly one delivery per subscribed channel, args carrying that old value, new value, and `BindingPriority` `Pr`. `silent` = zero deliveries on the metadata-Changed / typed-observer / untyped-observer / `OnPropertyChanged` channels. `baseNotify(oldB→newB, anim:bool)` = one delivery on an A20 `IncludeBaseChanges` observer. `evict(X)` = `OnEvicted(X)` fires exactly once. Unless a row subscribes a channel explicitly, "notify" asserts all four ordinary channels.
- Oracle tags: `AV` = Avalonia 11 behavior; `WPF` = WPF behavior; `AV+WPF` = both agree; `PIN` = Cursorial pin (no direct parent-framework analog — this matrix is the decision record); `DEV` = deliberate deviation from a parent framework, with rationale.

### 0.3 Global arbitration rules restated (rows assert instances of these)

1. Ladder (strong→weak), **amended 2026-07-12 (the completed Avalonia lattice — the activator split, PD26)**: `Animation(-100) > LocalValue(0) > StyleTrigger(50, the CONDITIONAL style slot — rules carrying any pseudo-class/.class/When condition, within-slot StyleSortKey) > Template(75, the template-instantiation lane) > Style(100, the RESTING style slot — purely structural rules, within-slot StyleSortKey) > Inherited(200, resolution-only) > Default(300, resolution-only)`. `Unset = int.MaxValue` is an internal sentinel, never reported by `GetValueSource`. The **Template lane** (§20, added 2026-06-16, PD24 as amended) carries everything a **control** template *authors* on its parts — a literal `SetValue`, a `{TemplateBinding}`/`{Binding}`, a `SetResourceReference`: BELOW `StyleTrigger` so state-driven looks pierce a template's authored part values while active, ABOVE resting `Style` so a template author's literals and TemplateBinding plumbing are the part's resting truth (a broad structural rule cannot wreck template wiring; re-skinning at rest flows through the control's own properties via the forwarding spine, or through conditional rules). *History:* the 2026-06-16 half-adoption put ALL styles above Template (the inverse of WPF, motivated by the close-button repro); it made template literals useless in the other direction and was completed into the Avalonia lattice on 2026-07-12 — the §20 rows carry both pins in their history. The lane is *not* a producer `SetValue`/`Bind`/a frame accept directly: an ambient template-instantiation scope (open while a control template's content tree is built) reroutes the ordinary local-lane producers to Template; outside the scope they stay LocalValue (PD24 — `DataTemplate.Build` deliberately does NOT open it).
2. Change-args priority = the priority of the **new effective value** (promotion reports the promoted lane) — except `SetCurrentValue`, which per A11 reports the **replaced** lane.
3. Equality gate everywhere: metadata `Comparer` ?? `EqualityComparer<T>.Default`; a write whose post-coercion result equals the current effective value at the same winning lane produces zero notifications and zero downstream work.
4. A source change with no value change (equal-value promotion) is **silent**, though `GetValueSource` updates (PD9).
5. Notification channel order, synchronous: metadata `Changed` → typed observers → untyped observers → virtual `OnPropertyChanged` (design doc §2.3).

### 0.4 Pinned decisions made by this matrix (PD ledger)

Rows below reference these. Each goes beyond (but never against) the canonical text; they are deliberate and binding until amended.

- **PD1** — `SetValue` accepts `BindingPriority.LocalValue` only; `Animation`, `Style`, `Inherited`, `Default`, `Unset` throw `ArgumentException`. One producer per lane: frames are the sole Style producer (A6 symmetry), `AnimatedValueHandle<T>` the sole Animation producer. DEV from Avalonia (which accepts style priorities on `SetValue`); rationale: keeps "what to restore to" unambiguous (§2 conventions), and the parameter survives for the re-addable cut rungs (§2.9).
- **PD2** — Store-initiated eviction order: `OnEvicted` fires **before** the resulting promotion's change notification (the listener must be able to tear down expression machinery before observers run; no echo from a dead binding).
- **PD3** — `ValueFrame.SetActive(false)` never evicts entries. Eviction happens only on `RemoveFrame`, cookie retraction, `TemplateInstance.Detach()`, `ClearValue` (A9), displacement (PD12), and `TearDown` (A13).
- **PD4** — A fresh `AnimatedValueHandle<T>` is inert until its first `SetValue`: no effective change, no notification, `GetValueSource` unchanged. (Analog of entry-holds-unset A8; the orchestrator owns the clock — until a tick produces a value there is nothing to apply.)
- **PD5** — Untyped `SetValue(p, UIProperty.UnsetValue, LocalValue)` ≡ `ClearValue(p)` in full, **including** A9's local-binding eviction. "`SetValue` never kills a binding" governs value-bearing writes; the `UnsetValue` sentinel is the documented untyped spelling of `ClearValue` (the XAML lane needs it).
- **PD6** — The local slot stores the **raw (pre-coercion)** value; the effective slot stores the coerced result. `CoerceValue` re-runs the coercer against the raw value (WPF's desired-value model).
- **PD7** — `Validate` runs on the **raw** value before `Coerce`; the coerced result is not re-validated (WPF order).
- **PD8** — The Default lane is returned as registered: metadata defaults are neither coerced nor validated (WPF).
- **PD9** — Equal-value promotion is silent; `GetValueSource` still updates (notification is value-change-driven, not source-change-driven).
- **PD10** — Promotion notifications (clear/remove/unset/dispose) carry the **new** winning lane's priority.
- **PD11** — `IsSet` = a value-bearing local contribution (local value or local entry with `HasValue`) **or** a value-bearing entry in an **active** frame. Animation, inherited, and default contributions do not count; valueless entries do not count. (DEV from Avalonia's local-only `IsSet`; rationale: S8 auto-aliasing must also yield to style/template-provided values — flagged for S8 review.)
- **PD12** — `OnEvicted` is store-initiated only: self-`Dispose` of an entry does **not** fire it. Installing a second local-priority entry for the same property **displaces** (evicts, with `OnEvicted`) the prior local entry (the A8 resource-producer displacement channel). A plain `SetValue` never displaces (A9).
- **PD13** — `ValueStore.TearDown()` fires `OnEvicted` per entry and **no** property-change notifications; afterwards the store is inert — reads return per-type defaults (inherited reads still walk, since the parent pointer is S1's to clear).
- **PD14** — Read-only properties (`UIPropertyKey<T>`): `SetValue`/`SetCurrentValue`/`ClearValue`/`Bind`/`BindInFrame`/`BeginAnimation` without the key throw `InvalidOperationException`; frames carrying an entry for a read-only property are rejected at `AddFrame`. `SetValue(key, v)` writes the LocalValue lane. (WPF read-only DP spirit.)
- **PD15** — `DeferNotifications` flushes coalesced changes in first-change order; `OnEvicted` is a lifecycle signal and fires immediately, never deferred.
- **PD16** *(amended 2026-07-12)* — `GetValue(p, maxPriority)` accepts `Animation | LocalValue | StyleTrigger | Template | Style | Inherited | Default` ("strongest considered lane"); `Unset` throws `ArgumentException`. `GetBaseValue(p)` ≡ `GetValue(p, LocalValue)`. Each probe cascades down the ladder from its cap — EXCEPT the `Style`-capped probe, which resolves the resting slot directly and **deliberately skips the stronger Template lane** ("the strongest resting-slot contribution", M281): the Template-capped probe is the one that sees the template value.
- **PD17** — `SetCurrentValue` with a `Validate`-rejecting value throws like `SetValue` (it is a mouth, not a producer).
- **PD18** — Reentrant writes dispatch synchronously: the nested change's notifications complete before the outer dispatch finishes delivering (WPF-style inversion, documented as accepted); copied-value args make this safe (§2.1).
- **PD19** — Observer subscription does **not** replay the current value; observers fire on changes only. DEV from Avalonia's `GetObservable` (which replays); rationale: no Rx surface (§2.6-1), consumers read `GetValue` at subscribe time.
- **PD20** — A gated (comparer-equal) write does not replace the stored value (the first-stored representative survives — observable with `Pcmp`). *(2026-07-08 amendment: the gate governs the stored **coerced representative**, notification silence, and `+cur` — NOT the raw desired-value slot, which is **last-writer-wins** under the gate (M231/M231a, M299b) so `CoerceValue` re-runs against the author's latest write; the write-provenance flags — `BaseIsCoerced`, `LocalValueFromEntry`, the M118 graft marker — re-derive from the new write. The original implementation returned before recording the raw, silently reverting a gated-then-unwired write to a stale value.)*
- **PD21** — `AddFrame` of an already-added frame throws `InvalidOperationException`; `RemoveFrame` of a never-added frame throws `ArgumentException` (deterministic over forgiving).
- **PD22** *(added at engine-4)* — **Propagated** inherited deliveries (a change reaching an entry-less descendant via eager-notify — A3/A4) ride a dedicated channel set, in order: typed observers → untyped observers → the `OnInheritedPropertyChanged` virtual (the A3 carrier). The metadata `Changed` callback and the ordinary `OnPropertyChanged` virtual are **origin-site** channels and do not fire on descendants — §2.3 enumerates descendant delivery as exactly "A3 **and** A4", and §5.5 has `UIElement` run its effects dispatch from the A3 virtual, which would be redundant if the `Changed`/virtual channels fired there. Where a row's Expected cell says `notify(…)` on a descendant for a *propagated* change (the leaf cells of M108–M111, M182, M184's leaf, M186, M189, M194–M196), it asserts this channel set; operations executed on the node itself (e.g. M85's `leaf.H.Dispose()`, M97's `leaf.CV`) keep the full four-channel meaning. Propagated deliveries dispatch immediately and are not captured by a *descendant's* defer scope — the origin's scope coalesces the whole fan-out (M194; PD15 spirit). The origin's equality gate is the only gate: no per-descendant re-gating with descendant-type comparers.
- **PD23** *(added at engine-4)* — `ValueSource` equality compares exactly the matrix-pinned `src` pair (`Priority`, `IsCurrentValue`). The §2.1 diagnostics grafts — `BasePriority`, `IsAnimated`, `IsCoerced` — are non-equality annotations on the same struct. Inherited-sourced reads report `IsAnimated`/`IsCoerced` as `false`: those details live at the contributing ancestor.
- **PD24** *(added 2026-06-16, §20; amended 2026-07-12)* — The **Template lane** (`BindingPriority.Template`, wire 75 — moved from 150 under the §2.9 gap contract when `StyleTrigger` landed) sits one rung below the conditional `StyleTrigger` slot and one ABOVE the resting `Style` slot. There is no new public producer mouth: a thread-static **template-instantiation scope** is opened around the build of a **control** template's content tree (`ControlTemplate.Instantiate`; `ItemsPanelTemplate.Build` — a control-template fragment — also opens it), and while it is open the three ordinary local-lane producers reroute to Template — a literal `SetValue`/`SetCurrentValue:false` (`SetTemplateValue`), a free-standing `{Binding}`/`{TemplateBinding}` install (priority captured into the binding's activation context at install time, since the entry may materialize on a later attach **outside** the scope), and a `SetResourceReference` (`SetTemplateValue` via a Template-priority binding entry). **`DataTemplate.Build` deliberately does NOT open the scope** *(2026-07-12 pin, reversing the original text)*: data-template content lands at LocalValue — a DataTemplate is authoring-equivalent to hand-writing the same element tree at the use site (reusable APP CONTENT, not a control's swappable skin; no part contract, no re-skinning story; matches Avalonia, which gives DataTemplate content no TemplatedParent and plain local values; the CD18 barrier exemption already treats it as app content). Outside the scope every producer stays `LocalValue` — the scope is the *only* trigger and it restores on dispose (nesting and re-entrancy are last-open-wins; closing pops). `SetValue(p, v, BindingPriority.Template)` is **not** accepted on the public mouth (PD1 stands — the lane is scope-driven, not parameter-driven). The lane is a structural twin of the local lane (literal value + at-most-one binding entry, last-writer-wins within the lane) and **can be masked** by StyleTrigger/Local/Animation, so it carries its own coerced/raw storage and resolves through `Reevaluate` rather than winning unconditionally. The lattice position matches **Avalonia** (`StyleTrigger > Template > Style`); WPF puts template values above all styles — rationale history in §20's header and `docs/ui-layer-design/judgment-styling-coherence.md` (Flaw 1).
- **PD25** *(added 2026-06-16, §20.6)* — `ValueSource` gains a non-equality `Kind : ValueSourceKind` annotation (joining the PD23 grafts; excluded from equality). It refines *how* the winning contribution was produced beyond the `Priority` lane: `Local`, `TemplateLiteral`, `TemplateBinding`, `TemplateResource`, `StyleSetter`, `StyleWhen`, `Animation`, `Inherited`, `Default`. The lane-level distinctions a consumer asked for — "is this a template default or a style?" — fall out of `Priority` alone; `Kind` adds the within-lane provenance (literal vs binding vs resource inside Template; setter vs `When`-guarded rule inside either style slot). It is derived from store state plus a lightweight source tag carried on binding entries / frames; a contribution whose finer origin is unknown reports the lane's generic kind (`Local`/`Default`/`Inherited`/`Animation`).
- **PD26** *(added 2026-07-12 — the activator split)* — The single Style slot splits into TWO slots by rule SHAPE: a rule carrying **any activation condition** — a pseudo-class or `.class` simple on any compound, or a `When` data-condition — arbitrates at `StyleTrigger(50)`; a purely structural rule (types, `#name`s, combinators, `/template/` hops) arbitrates at `Style(100)`. The predicate is `CompiledRule.IsConditional ⇔ ClassLike > 0` (`CountSpecificity` buckets every Class + PseudoClass simple across all compounds into classLike; `Style.EmitRules` folds the `When` count in per SD5) — derived from shape, so re-matches classify identically (the ApplyMatchDiff survivor contract) and the frame's slot is fixed at construction (`ValueFrame.Priority`). Classes count as conditions because they toggle at runtime (`.obscured`, `.caps-*` depend on piercing template values) — Avalonia's formulation verbatim ("style class, pseudo class … are conditional; name and type selectors are not"). **The slot beats the sort key**: an active conditional rule beats every resting rule regardless of `StyleLayer` or specificity — the accepted cross-layer consequence (an activated theme rule beats a resting app rule; an activated scoped `.class` rule beats a resting Explicit style — the unconditional per-element override is `SetValue`). Within each slot the packed `StyleSortKey` arbitrates exactly as before (layer beats specificity, SD5/SD6). Frame-hosted binding entries inherit their host frame's slot (A5). **AV** (the Avalonia activator model, adopted whole: "who wants to memorize three sets of rules?").
- **PD27** *(added 2026-07-12 — SetCurrentValue provenance + the universal undo)* — Three coordinated amendments make `SetCurrentValue` fully provenance-transparent (WPF parity) and universally undoable: **(a) M118 amended** — the no-contribution graft stays local *for storage* (it shadows the subtree and evaporates on a producer's arrival, unchanged) but `GetValueSource` reports the UNDERLYING source it overlays — `Default`/`Inherited` with `IsCurrentValue = true`, never `LocalValue` — so `Kind == Default/Inherited` is a sound "not set deliberately — safe to replace" test; the change notification carries the replaced (underlying) lane per A11 (M118/M127/M133/M243 re-pinned). **(b) M264c flipped** — `ReadLocalValue`/`TryReadLocalValue` no longer report the pure graft (`UnsetValue`/false); a real `SetValue` that `SetCurrentValue` later overwrote still reports its latest raw write (WPF: SCV is invisible to `ReadLocalValue` — the former DEV is retired). **(c) M125 amended** — `ClearValue` also strips a `+cur` overlay riding a producer lane (SCV over StyleTrigger/Template/Style), restoring the lane's stored source value in one notification at that lane ("ClearValue undoes SetCurrentValue" is universal; the bit clears first so the M120 maintained-overwrite gate cannot hold it, then re-evaluation re-derives the lane — the M122 clobber shape). Under an ACTIVE animation ClearValue leaves the overlay to the Animation lane's own clobber rules — the next push (M129) or handle disposal (M130) — keeping the `+cur` bit truthful (a Holding animation never pushes again, so a bit-drop would record an undo that never happened; M125d as re-pinned by the 2026-07-12 audit). The strip is independent of a co-evicted valueless local entry (M125e); an animation episode over the graft cannot erase its provenance (the graft's existence IS the `+cur` signal while it wins — M118c); the inheriting graft fans out to descendants at `Inherited` (contribution-tested, M127b); and a graft re-coercion notifies at the underlying lane (M133b). `IsSet` is unchanged (the graft still counts — storage truth; PD11's auto-aliasing gate must yield to a visibly-grafted value). Framework code that used `Kind == Default` as an "untouched" gate must now test `Kind == Default && !IsCurrentValue` (ToggleButton FB-27 updated).

---

## 1. Defaults, registration, metadata (M1–M14)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M1 | fresh `Host` | `GetValue(P)` | `0`; no `ValueStore` allocated (internal debug accessor) | AV+WPF |
| M2 | fresh `Host` | `GetValueSource(P)` | `Default`, `+cur` false | AV+WPF |
| M3 | fresh `Host` | `IsSet(P)` | `false` | AV+WPF |
| M4 | fresh `Host` | `GetBaseValue(P)` | `0` | AV+WPF |
| M5 | `P.OverrideDefaultValue<DerivedHost>(9)` | read on `DerivedHost` / on `Host` | `9` src=`Default` / `0` src=`Default` | AV+WPF |
| M6 | as M5, variable statically typed `Host` holding a `DerivedHost` | `GetValue(P)` | `9` — default resolves against **runtime** type | AV+WPF |
| M7 | a `DerivedHost` instance has touched `P` (a `GetValue` counts) | `OverrideMetadata<DerivedHost>(…)` | throws `InvalidOperationException` | PIN (§2.3: "throws after first touch") |
| M8 | `P` registered with `Changed` cb₁; `OverrideMetadata<DerivedHost>` adds cb₂ | `L(1)` on `DerivedHost` | both fire, **base-first** (cb₁ then cb₂) | WPF (merged metadata, chained) |
| M9 | `Pcmp`; `OverrideMetadata<DerivedHost>` replaces `Comparer` with `Ordinal` | `L("abc")` then `L("ABC")` on `DerivedHost` / on `Host` | DerivedHost: second write notifies (ordinal ≠) · Host: second write silent (ignore-case =) — nearest-wins, non-`Changed` members don't chain | WPF |
| M10 | `Pc2` (default 500, clamp [0,100]) | fresh `GetValue(Pc2)` | `500` — defaults bypass coercion (PD8) | WPF |
| M11 | `var P2 = P.AddOwner<OtherHost>()` | identity + storage | `ReferenceEquals(P, P2)` is the contract of shared id: same `Id`, registry finds `(OtherHost,"P")`; value set via either alias reads back via the other | AV |
| M12 | as M11 + `OverrideDefaultValue<OtherHost>(7)` | reads | `OtherHost` instance → `7`; `Host` instance → `0` | AV |
| M13 | `P` | `GetMetadata(typeof(DerivedHost))` twice | same cached instance (allocation behavior; merged-result cache) | PIN |
| M14 | fresh `Host` | untyped `GetValue((UIProperty)P)` | boxed `0` — **never** `UnsetValue`; `UnsetValue` is a write-side sentinel only | AV |

---

## 2. Local lane (M15–M32)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M15 | fresh | `L(1)` | eff=1 base=1 src=`Local` IsSet=true; `notify(0→1, Local)` | AV+WPF |
| M16 | metadata `Changed`, typed observer, untyped observer, `OnPropertyChanged` override all recording | `L(1)` | delivery order: `Changed` → typed → untyped → virtual; each exactly once | PIN (§2.3 order) |
| M17 | after `L(1)` | `L(1)` | `silent` on all channels; zero downstream work | AV+WPF |
| M18 | after `L(1)` | `L(2)` then `L(3)` | `notify(1→2, Local)` then `notify(2→3, Local)` | AV+WPF |
| M19 | `Pcmp` | `L("abc")` then `L("ABC")` | second `silent`; `GetValue` still `"abc"` (PD20) | PIN |
| M20 | after `L(1)` | `CV` | eff=0 src=`Default` IsSet=false; `notify(1→0, Default)` (PD10) | AV+WPF |
| M21 | fresh | `CV` | no-op, `silent`, still no store entry | AV+WPF |
| M22 | fresh | `SetValue(P, 1, Style)` | throws `ArgumentException` (PD1) | DEV (AV accepts; rationale PD1) |
| M23 | fresh | `SetValue(P, 1, Animation)` | throws `ArgumentException` (PD1) | PIN |
| M24 | fresh | `SetValue(P, 1, Inherited)` / `Default` / `Unset` | throws `ArgumentException` (resolution-only tiers) | AV+WPF-adjacent, tag PIN |
| M25 | typed + untyped observers | `L(1)` | both observer args carry `Priority == LocalValue` (A10) | AV |
| M26 | fresh | `AddObserver(P, o)` alone | no replay delivery (PD19); `GetValueSource`=`Default`, IsSet=false | DEV (AV `GetObservable` replays) |
| M27 | observer subscribed, then its `IDisposable` disposed | `L(1)` | no delivery to the disposed observer | AV+WPF |
| M28 | observer o₁ subscribes o₂ during o₁'s `OnPropertyChanged` | `L(1)` | o₂ not invoked for the in-flight change; invoked for the next | PIN (COW arrays) |
| M29 | o₁ disposes o₂ during o₁'s delivery (o₂ later in array) | `L(1)` | o₂ **is** still delivered the in-flight change (snapshot semantics) | PIN (COW arrays) |
| M30 | `OnPropertyChanged` override | `L(1)` | `args.GetOldValue<int>()`=0, `GetNewValue<int>()`=1; `GetNewValue<string>()` throws `InvalidCastException` | PIN (copied-value carrier) |
| M31 | `L(1)`; `E = Bind(P, LocalValue)` | `E.Set(2)`; `L(3)`; `E.Set(4)` | last-writer-wins within the lane: eff goes 2 → 3 → 4, src stays `Local`, three notifies; the binding survives the interleaved `SetValue` (A9) | AV |
| M32 | after M20 sequence | `IsSet(P)` | `false` | AV+WPF |

---

## 3. Style slot — frames and within-slot ordering (M33–M53)

*(2026-07-12 note: since PD26 there are TWO style slots — plain `F` frames here are the RESTING `Style` slot; every row's within-slot arbitration applies identically inside the `StyleTrigger` slot, whose frames simply arbitrate one tier higher. §20's re-pinned rows carry the cross-slot contests.)*

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M33 | fresh | `AddFrame F(k1){P=5}` (active) | eff=5 base=5 src=`Style` IsSet=true; `notify(0→5, Style)` | AV |
| M34 | fresh | `AddFrame F(k1){P=∅}` | `silent`; eff=0 src=`Default`; IsSet=false (valueless entry contributes nothing, PD11) | AV (A8) |
| M35 | after M33 | `RemoveFrame F(k1)` | eff=0 src=`Default`; `notify(5→0, Default)` (PD10) | AV |
| M36 | after M33 | `F(k1).SetActive(false)` then `SetActive(true)` | deactivate: `notify(5→0, Default)`, **no** `evict` (PD3); reactivate: `notify(0→5, Style)` | AV |
| M37 | fresh | add `F(k2){P=8}` **then** `F(k1){P=5}` | eff=8 — sort key order, not add order; adding the weaker frame is `silent` | AV (store-level analog) |
| M38 | fresh | add `F(k1){P=5}` then `F(k1′){P=6}` (equal keys) | eff=6 — later-added wins within equal keys; `notify(5→6, Style)` | AV |
| M39 | `F(k1){P=5}` + `F(k2){P=8}` | `RemoveFrame F(k2)` | eff=5; `notify(8→5, Style)` — within-slot promotion | AV |
| M40 | as M39 | `F(k2).SetActive(false)` / `(true)` | same promotion/reclaim as M39 with no eviction; reclaim `notify(5→8, Style)` | AV |
| M41 | `F(k1){P=5}` + `F(k2){P=∅}` | resolve | eff=5 — stronger frame's valueless entry skipped (unset promotion within slot) | AV (A8) |
| M42 | after M33 | entry value 5→6; frame raises `OnEntryChanged(entry)` | `notify(5→6, Style)`; no `evict`, no remove/re-add observable | AV (§2.4 in-place re-emit) |
| M43 | after M33 | `OnEntryChanged` with unchanged value | `silent` | AV |
| M44 | as M39, change **k1**'s entry 5→7 + `OnEntryChanged` | masked re-emit | `silent` (k2 still wins); then `RemoveFrame F(k2)` ⇒ `notify(8→7, Style)` | AV |
| M45 | `L(3)` | `AddFrame F(k1){P=5}` | `silent`; eff=3 src=`Local`; `GetValue(P, Style)`=5 | AV+WPF |
| M46 | as M45 | `CV` | eff=5 src=`Style`; `notify(3→5, Style)` | AV+WPF |
| M47 | `L(7)` + `F(k1){P=7}` | `CV` | `silent` (equal-value promotion, PD9) but src flips `Local`→`Style` | AV+WPF |
| M48 | as M45 | `RemoveFrame F(k1)` | `silent`; src stays `Local`, eff=3 | AV+WPF |
| M49 | fresh | `AddFrame F(k1){P=5, Pc=120}` | one notify per affected property; `Pc`: eff=100 (coerced), `notify(0→100, Style)` | AV+WPF |
| M50 | after M33 | `AddFrame F(k1)` again (same instance) | throws `InvalidOperationException` (PD21) | PIN |
| M51 | fresh | `RemoveFrame` of a never-added frame | throws `ArgumentException` (PD21) | PIN |
| M52 | frame constructed inactive, `AddFrame F(k1){P=5}` | resolve / then `SetActive(true)` | add is `silent`, eff=0; activation ⇒ `notify(0→5, Style)` | AV |
| M53 | after M33 | `GetValueSource(P)` | `Style` (frame-sourced values report the single Style slot regardless of sort-key layer — template-local included) | AV+decisions (§2.2) |

---

## 4. Animation lane (M54–M75)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M54 | `L(3)` | `H = BeginAnimation(P)` (no `Set` yet) | inert: eff=3 src=`Local`, `silent` (PD4) | PIN |
| M55 | as M54 | `H.Set(9)` | eff=9 base=3 src=`Animation`; `notify(3→9, Animation)`; returns `true` (A18) | AV+WPF |
| M56 | after M55 | `H.Set(9)` | `silent`; returns `false` (equality gate, A18) | AV+WPF gate; `bool` PIN (A18) |
| M57 | after M55 | `H.Set(10)` | `notify(9→10, Animation)`; returns `true` | AV+WPF |
| M58 | after M55 | `H.Dispose()` | base resurfaces: eff=3 src=`Local`; exactly one `notify(9→3, Local)` (PD10) | AV+WPF |
| M59 | `L(3)` | `H.Set(3)` — first push equals the base | `silent`, returns `false` (equal-value **lane flip**: src becomes `Animation`, no notification — PD9 applied to lane flips) | PIN |
| M60 | after M59 | `H.Dispose()` | `silent` (animated value equals base); src back to `Local` | AV+WPF (equality gate) |
| M61 | after M55 | `L(4)` | **masked base write**: eff stays 9, `silent` on ordinary channels (§2.3 fast path); base=4; src=`Animation` | AV+WPF |
| M62 | after M61 | `H.Dispose()` | eff=4 src=`Local`; one `notify(9→4, Local)` | AV+WPF |
| M63 | after M55 | `H2 = BeginAnimation(P)` | last-started wins: `H.IsDetached`=true; eff still 9 until `H2.Set` (PD4 inertia) | AV+WPF spirit; PIN detail |
| M64 | after M63 | `H.Set(12)` | silent no-op, returns `false` (A19); eff unchanged | PIN (A19) |
| M65 | after M63 | `H2.Set(11)` then `H.Dispose()` | eff=11 (`H` is detached; disposing it does nothing); `H2` still attached | PIN (A19) |
| M66 | after M55 | `H.Dispose()` twice | second dispose idempotent, `silent` | PIN (A19) |
| M67 | fresh (no base) | `H.Set(9)`; `H.Dispose()` | eff=9 src=`Animation` `notify(0→9, Animation)`; dispose ⇒ eff=0 src=`Default` `notify(9→0, Default)` | AV+WPF |
| M68 | `F(k1){P=5}` (no local) | `H.Set(9)`; then `RemoveFrame` | eff=9; base flips 5→0; ordinary channels `silent` on the frame removal (effective unchanged); then `H.Dispose()` ⇒ `notify(9→0, Default)` | AV+WPF |
| M69 | after M55 | `CV` | local removed **under** the animation: eff stays 9, ordinary channels `silent`; base=0 (`GetBaseValue`), src stays `Animation`; `H.Dispose()` ⇒ `notify(9→0, Default)` | AV+WPF |
| M70 | after M55 | `GetBaseValue(P)` / `GetValue(P, LocalValue)` | both `3` (PD16 equivalence) | AV (`GetBaseValue`) |
| M71 | `Pc`; `H = BeginAnimation(Pc)` | `H.Set(250)` | eff=100 (coerced at effective computation — the §2.6-6 guardrail); `notify(0→100, Animation)` | WPF (animated values coerced) |
| M72 | after M71 | `H.Set(300)` | `silent`, returns `false` — different raw, same coerced result (equality on the coerced value) | PIN |
| M73 | `Pv`; `H = BeginAnimation(Pv)` | `H.Set(-5)` | rejected: `UIDiagnostics.OnRejectedValue` fires, previous value kept, `silent`, returns `false` (producer-mouth rejection, §2.3) | AV-shaped; PIN |
| M74 | `L(3)`; `H.Set(9)` | `IsSet(P)` | `true` — but from the **local** contribution; with no base (M67 setup) `IsSet` = `false` (animation doesn't count, PD11) | PIN |
| M75 | after M55 | `GetValueSource(P)` | `Animation`, `+cur` false | AV+WPF (`IsAnimated`) |

---

## 5. Pairwise precedence — every priority pair × {apply, withdraw, masked write, equal-value promotion} (M76–M117)

Lane producers (PD1): `Local` = `SetValue`; `Style` = a frame; `Animation` = a handle; `Inherited` = a contributing inheritance parent (property `Pi`); `Default` = metadata. "Withdraw" = the lane-appropriate retraction: `CV` / `RemoveFrame` / `H.Dispose()` / parent `CV` (or `SetInheritanceParent(null)`). All rows assert `eff`, `base`, `src`, and the exact notification; "masked" rows assert ordinary-channel silence (§10 covers the A20 channel). W-value = 5, S-value = 9 unless noted; equal-promotion rows use 7/7.

| # | Pair (S over W) | Operation | Expected | Oracle |
|---|---|---|---|---|
| M76 | Anim over Local | `L(5)` then `H.Set(9)` | eff=9 base=5 src=`Animation`; `notify(5→9, Animation)` | AV+WPF |
| M77 | Anim over Local | withdraw S (`H.Dispose()`) | eff=5 src=`Local`; `notify(9→5, Local)` | AV+WPF |
| M78 | Anim over Local | masked write W: `L(6)` under H | `silent`; eff=9 base=6 | AV+WPF |
| M79 | Anim over Local | `L(7)`, `H.Set(7)`, `H.Dispose()` | dispose `silent` (equal promotion); src flips to `Local` (PD9) | AV+WPF |
| M80 | Anim over Style | `F(k1){P=5}` then `H.Set(9)` | eff=9 base=5 src=`Animation`; `notify(5→9, Animation)` | AV+WPF |
| M81 | Anim over Style | `H.Dispose()` | eff=5 src=`Style`; `notify(9→5, Style)` | AV+WPF |
| M82 | Anim over Style | masked: entry 5→6 + `OnEntryChanged` under H | `silent`; base=6, eff=9 | AV+WPF |
| M83 | Anim over Style | `F(k1){P=7}`, `H.Set(7)`, `H.Dispose()` | `silent`; src flips to `Style` | AV+WPF |
| M84 | Anim over Inherited | `root.L(Pi,5)`; `leaf.H.Set(9)` | leaf: eff=9 base=5 src=`Animation` | AV+WPF |
| M85 | Anim over Inherited | `leaf.H.Dispose()` | leaf: eff=5 src=`Inherited`; `notify(9→5, Inherited)` | AV+WPF |
| M86 | Anim over Inherited | masked: `root.L(Pi,6)` under leaf's H | leaf ordinary channels `silent`; leaf base=6 (walk-up); root notifies normally | AV+WPF |
| M87 | Anim over Inherited | root holds 7; `leaf.H.Set(7)`; dispose | `silent`; leaf src flips to `Inherited` | AV+WPF |
| M88 | Anim over Default | `H.Set(9)` on fresh | eff=9 base=0 src=`Animation`; `notify(0→9, Animation)` | AV+WPF |
| M89 | Anim over Default | `H.Dispose()` | eff=0 src=`Default`; `notify(9→0, Default)` | AV+WPF |
| M90 | Anim over Default | masked write W | n/a — Default has no writer; row asserts `GetBaseValue`=0 under H | AV+WPF |
| M91 | Anim over Default | `H.Set(0)` on fresh; dispose | both `silent` (equal lane flips, PD9) | PIN |
| M92 | Local over Style | `F(k1){P=5}` then `L(9)` | eff=9 src=`Local`; `notify(5→9, Local)` | AV+WPF |
| M93 | Local over Style | `CV` | eff=5 src=`Style`; `notify(9→5, Style)` | AV+WPF |
| M94 | Local over Style | masked: entry 5→6 + `OnEntryChanged` under local | `silent`; `GetValue(P, Style)`=6 | AV+WPF |
| M95 | Local over Style | `F(k1){P=7}`, `L(7)`, `CV` | `CV` `silent`; src flips to `Style` | AV+WPF |
| M96 | Local over Inherited | `root.L(Pi,5)`; `leaf.L(Pi,9)` | leaf: eff=9 src=`Local`; `notify(5→9, Local)` — old value is the inherited one | AV+WPF |
| M97 | Local over Inherited | `leaf.CV(Pi)` | leaf: eff=5 src=`Inherited`; `notify(9→5, Inherited)` | AV+WPF |
| M98 | Local over Inherited | masked: `root.L(Pi,6)` under leaf local | leaf `silent` (shadowed — see also M171) | AV+WPF |
| M99 | Local over Inherited | root holds 7; `leaf.L(Pi,7)`; `leaf.CV(Pi)` | `CV` `silent`; leaf src flips to `Inherited` | AV+WPF |
| M100 | Local over Default | `L(9)` / `CV` | covered by M15/M20; row asserts the pair end-to-end with src transitions `Default→Local→Default` | AV+WPF |
| M101 | Local over Default | `L(0)` on fresh | `silent` (equal to default) **but** src=`Local`, IsSet=true (PD9 lane flip) | AV+WPF |
| M102 | Style over Inherited | `root.L(Pi,5)`; `leaf.AddFrame F(k1){Pi=9}` | leaf: eff=9 src=`Style`; `notify(5→9, Style)` | AV+WPF |
| M103 | Style over Inherited | `leaf.RemoveFrame` | leaf: eff=5 src=`Inherited`; `notify(9→5, Inherited)` | AV+WPF |
| M104 | Style over Inherited | masked: `root.L(Pi,6)` under leaf frame | leaf `silent` (frame shadows the walk) | AV+WPF |
| M105 | Style over Inherited | root holds 7; leaf frame value 7; `RemoveFrame` | `silent`; src flips to `Inherited` | AV+WPF |
| M106 | Style over Default | `F(k1){P=9}` / remove | covered by M33/M35; row asserts src transitions `Default→Style→Default` | AV |
| M107 | Style over Default | frame value 0 (equals default) added | `silent`; src=`Style`, IsSet=true | AV |
| M108 | Inherited over Default | `root.L(Pi,9)` (leaf entry-less) | leaf: eff=9 src=`Inherited`; leaf delivery per A3/A4 with `Priority=Inherited` | AV+WPF |
| M109 | Inherited over Default | `root.CV(Pi)` | leaf: eff=0 src=`Default`; `notify(9→0, Default)` on leaf | AV+WPF |
| M110 | Inherited over Default | `root.L(Pi,0)` (equals default) on fresh tree | leaf `silent`; leaf src flips to `Inherited` (PD9) | PIN |
| M111 | Inherited over Default | `SetInheritanceParent(null)` on leaf while root holds 9 | leaf: `notify(9→0, Default)` — detach re-resolves over `InheritingPropertyIds` | AV+WPF |
| M112 | full ladder | `root.L(Pi,2)`; leaf: `F(k1){Pi=5}`, `L(7)`, `H.Set(9)` | eff=9; peel top-down: `H.Dispose()`⇒`notify(9→7, Local)`; `CV`⇒`notify(7→5, Style)`; `RemoveFrame`⇒`notify(5→2, Inherited)`; `root.CV`⇒`notify(2→0, Default)` — four notifications, each exactly once, each at the promoted lane | AV+WPF |
| M113 | full ladder | same stack, `GetBaseValue` at each step | 7, then (after `CV`) 5, then 2, then 0 — base tracks the strongest sub-Animation lane | AV |
| M114 | full ladder | `GetValue(P, maxPriority)` probes on the M112 stack | `Animation`→9, `LocalValue`→7, `Style`→5, `Inherited`→2, `Default`→0 | PIN (PD16) |
| M115 | re-apply after peel | after M112 fully peeled, `L(7)` again | eff=7; `notify(0→7, Local)` — the store holds no ghost state | AV+WPF |
| M116 | apply-below-apply | fresh: `H.Set(9)` first, **then** `L(5)`, `F(k1){P=3}` | each weaker apply `silent`; eff stays 9; base settles 5; `GetValue(P, Style)`=3 | AV+WPF |
| M117 | withdraw-out-of-order | M112 stack; remove the **middle** lanes first: `CV` then `RemoveFrame` under H | both `silent` on ordinary channels (masked); `H.Dispose()` ⇒ `notify(9→2, Inherited)` — promotion skips the vacated lanes | AV+WPF |

---

## 6. SetCurrentValue — in-place overwrite preserving source (M118–M135)

Cross-cutting block 1 (gates S4 W3 and S2's recorded divergence). A11: observer args carry the **replaced** lane's priority — `Animation` while an animation holds the property, else the base lane. A12: non-echo `SetValue` **and** `SetCurrentValue` feed TwoWay write-back; S2 filters `Animation`-priority args. At P0 (no binding engine) the rows assert the *args priority* and provenance — the observable contract S2's filter is built on.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M118 *(amended 2026-07-12, PD27)* | fresh (no entry) | `SCV(4)` | grafts as local **storage** (shadows the subtree, evaporates on a producer's arrival) but PROVENANCE reports the underlying source: eff=4 src=`Default+cur`; `notify(0→4, Default)` (A11 — the replaced lane) | WPF (src=Default+current); storage remains the P3 graft, §2.2 — the former DEV is retired |
| M118b *(added 2026-07-12, PD27)* | graft + animation: `SCV(4)` then `H.Set(9)` | `GetValueSource` / `ReadLocalValue` | `Priority=Animation`, `BasePriority=Default` (the base is the graft ⇒ the underlying source), `ReadLocalValue`=`UnsetValue` | PIN (PD27) |
| M118c *(added 2026-07-12, audit)* | graft, then a full animation episode: `SCV(4)`, `H.Set(9)`, `H.Dispose()` | `GetValueSource` | the graft resurfaces as the base and its provenance KEEPS `+cur`: src=`Default+cur` (the graft's existence is the `+cur` signal — the shared entry bit was cleared by M129/M130 but the value is still the deliberate overwrite; "Default without +cur ⇒ safe to replace" stays sound); `CV` still undoes it | PIN (PD27, audit fix) |
| M119 | `L(3)` | `SCV(4)` | eff=4 src=`Local+cur`; `notify(3→4, Local)`; local raw slot now 4 (a later `Co` coerces against 4) | AV+WPF |
| M120 | `F(k1){P=5}` | `SCV(6)` | eff=6 src=`Style+cur` — provenance unchanged; `notify(5→6, Style)` (A11: replaced lane) | WPF (`IsCurrent`); args priority PIN (A11) |
| M121 | after M120 | `base` / `IsSet` | base=6 (the overwrite **is** the base while un-animated); IsSet=true (style contribution) | PIN |
| M122 | after M120 | frame entry re-emits 5→8 via `OnEntryChanged` | re-evaluation from the replaced lane clobbers the overwrite: eff=8 src=`Style` (`+cur` cleared); `notify(6→8, Style)` | WPF (current value lost on re-evaluation) |
| M123 | after M120 | `RemoveFrame` | overwrite evaporates with its lane: eff=0 src=`Default`; `notify(6→0, Default)` | WPF |
| M124 | after M120 | `L(9)` | stronger lane wins normally: eff=9 src=`Local`; `notify(6→9, Local)` | AV+WPF |
| M125 *(amended 2026-07-12, PD27)* | after M120 | `CV` | strips the `+cur` overlay riding the producer lane — "ClearValue undoes SetCurrentValue" is universal: eff=5 src=`Style` (`+cur` cleared); `notify(6→5, Style)`; a second `CV` is the silent no-op (M21) | WPF (ClearValue invalidates ⇒ the source re-asserts); was a pinned no-op |
| M125b *(added 2026-07-12, PD27)* | `Ft(k1){P=5}`; `SCV(6)` | `CV` | strips the overlay on the `StyleTrigger` lane: eff=5 src=`StyleTrigger`; `notify(6→5, StyleTrigger)` | PIN (PD27) |
| M125c *(added 2026-07-12, PD27)* | `T(5)`; `SCV(6)` (the M288 overwrite) | `CV`, then `CV` again | first strips the overlay on the Template lane: eff=5 src=`Template`; `notify(6→5, Template)`. Second is the M292 no-op (`silent`) — CV never removes the template VALUE, only the overlay | PIN (PD27; M292 unchanged) |
| M125d *(added 2026-07-12, PD27; re-pinned same day, audit)* | `F(k1){P=3}`; `H.Set(9)`; `SCV(11)` (the M128 animated-effective overwrite) | `CV` | `silent` no-op — the overlay rode the ANIMATED effective (M131) and only the animation can re-produce its value; the `+cur` bit STAYS (truthful: a Holding animation never pushes again, so dropping it would record an undo that never happened). The overlay dies by the lane's own rules: the next push (`H.Set(12)` ⇒ `notify(11→12, Animation)`, `+cur` cleared — M129) or handle disposal (M130) | PIN (PD27, audit fix) |
| M125e *(added 2026-07-12, audit)* | `F(k1){P=5}`; a valueless local entry `E` (A8, never pushed); `SCV(6)` riding the producer lane | `CV` | BOTH effects: `evict(E)` (A9) AND the overlay strips — eff=5 src=`Style`; `notify(6→5, Style)` (the strip is independent of the co-evicted entry; the original implementation skipped it, leaving the overlay to the M120 gate) | PIN (PD27, audit fix) |
| M126 | after M118 | `CV` | the as-Local current value **is** a local contribution: eff=0 src=`Default`; `notify(4→0, Default)` | PIN (consequence of the M118 graft) |
| M127 *(amended 2026-07-12, PD27)* | `root.L(Pi,5)`, leaf entry-less | `leaf.SCV(Pi,6)` | lazy-read inheritance holds no leaf entry ⇒ grafts as local storage on leaf; provenance reports the underlying source: src=`Inherited+cur`, `notify(5→6, Inherited)`; leaf still shadows its subtree | PIN (M118 rule applied to Inherited) |
| M127b *(added 2026-07-12, audit)* | inheriting `Pi`; root + entry-less leaf | `root.SCV(Pi,5)` | the graft's ORIGIN notification carries `Default` (PD27/A11), but the descendant fan-out lane is `Inherited` — the origin's storage CONTRIBUTES (the leaf reads 5 through it and reports src=`Inherited`), so the M108/M109 discriminator tests contribution, not the origin lane | PIN (PD27, audit fix) |
| M128 | `L(3)`; `H.Set(9)` | `SCV(11)` | in-place overwrite of the **animated** effective: eff=11 src=`Animation+cur`; `notify(9→11, Animation)` (A11); base stays 3 | PIN (A11) |
| M129 | after M128 | `H.Set(12)` | animation lane reclaims: eff=12, `+cur` cleared; `notify(11→12, Animation)` | PIN |
| M130 | after M128 | `H.Dispose()` | overwrite dies with the lane: eff=3 src=`Local`; `notify(11→3, Local)` | PIN |
| M131 | after M128 | `GetBaseValue(P)` | `3` — `SCV` under animation never touches the base | PIN (A11/A12 joint premise) |
| M132 | **joint A11×A12 row** | mid-animation `SCV(11)` vs un-animated `SCV(4)`, observer recording priorities | animated: args `Priority=Animation` (⇒ S2's TwoWay filter drops it; source never written). Un-animated: args `Priority=Local`/base lane (⇒ S2 writes through). P0 asserts the two priorities; the S2 consequence is restated in B0's matrix. Pinned fallback if write-through proves unhonorable: Popup writes `SetValue(LocalValue)` through the binding (§2.5) | PIN (gates S4 W3, S2) |
| M133 *(amended 2026-07-12)* | `Pc` | `SCV(250)` | coerced like any mouth write: eff=100; `notify(0→100, Default)` (the graft's replaced lane, PD27) | WPF |
| M133b *(added 2026-07-12, audit)* | `Pcd` (instance ceiling 100); `SCV(250)` (graft, eff=100) | raise ceiling, `Co` | re-runs against the grafted raw 250 ⇒ eff=250; `notify(100→250, Default)` — a graft re-coercion notifies at the underlying lane, never LocalValue (PD27) | PIN (PD27, audit fix) |
| M134 | `Pv` | `SCV(-5)` | throws `ArgumentException` (PD17), store untouched, `silent` | PIN |
| M135 | `Pcmp` holding `"abc"` (local) | `SCV("ABC")` | `silent` (comparer-equal); stored value still `"abc"` (PD20); `+cur` **not** set | PIN |

---

## 7. Binding entries, free-standing (M136–M152)

`Bind(P, LocalValue, listener)` — A6/A7/A8/A9. The entry is the producer; the store arbitrates.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M136 | fresh | `E = Bind(P, LocalValue)` | entry installs **valueless**: `E.HasValue`=false, eff=0 src=`Default`, `silent` (A8) | AV-shaped; PIN (A8) |
| M137 | after M136 | `E.Set(5)` | eff=5 src=`Local` IsSet=true; `notify(0→5, Local)` | AV |
| M138 | after M137 | `E.Set(5)` | `silent` (equality gate in the entry/store) | AV |
| M139 | after M137 | `E.Unset()` | `HasValue`=false ⇒ promotion: eff=0 src=`Default`; `notify(5→0, Default)`; entry stays installed — a later `E.Set(6)` re-applies | AV (A8) |
| M140 | `F(k1){P=3}` + M137 | `E.Unset()` | promotes to the frame: eff=3 src=`Style`; `notify(5→3, Style)` | AV |
| M141 | after M137 | `E.SetValue((object?)UIProperty.UnsetValue)` (untyped base lane) | ≡ `E.Unset()`: `HasValue`=false, promotion (A8 — never null/default-clobber) | AV (A8) |
| M142 | after M137 | `L(8)` | transient override: eff=8, `notify(5→8, Local)`; **no eviction**, no `OnEvicted` (A9); `E.Set(9)` afterwards wins again ⇒ `notify(8→9, Local)` | AV |
| M143 | after M137 | `CV` | **the binding kill** (A9): `evict(E)` exactly once, then promotion `notify(5→0, Default)` — in that order (PD2) | PIN (A9 + PD2) |
| M144 | after M143 | `E.Set(7)` | dead entry: silent no-op (post-eviction pushes are discarded); eff stays 0 | PIN |
| M145 | after M137 | `E.Dispose()` | entry removed, value withdrawn: `notify(5→0, Default)`; **no** `OnEvicted` (self-initiated, PD12); `Dispose` again = idempotent no-op | PIN (A7 + PD12) |
| M146 | listener whose `OnEvicted` calls `entry.Dispose()` | `CV` | legal re-entrant dispose (A7): `evict(E)` once, no double-fire, promotion notification still exactly once | PIN (A7) |
| M147 | after M137 | `E2 = Bind(P, LocalValue)` | **displacement** (PD12): `evict(E)` fires; `E2` installs valueless ⇒ eff promotes to next lane until `E2.Set` (`notify(5→0, Default)`) | PIN (A8 displacement channel) |
| M148 | fresh | `Bind(P, Style)` / `Bind(P, Default)` / `Bind(P, Animation)` / `Bind(P, Inherited)` | each throws `ArgumentException` (A6: free-standing bind is LocalValue-only) | PIN (A6) |
| M149 | `Pv` + entry | `E.Set(-5)` | discarded: `UIDiagnostics.OnRejectedValue` fires, previous value kept, `silent`; entry still reports its prior state (a valueless entry stays valueless) | AV (validation at the mouth, §2.3) |
| M150 | `Pc` + entry | `E.Set(250)` | eff=100 (coerced at effective computation); `notify(0→100, Local)` | AV+WPF |
| M151 | `H.Set(9)` active + entry | `E.Set(5)` | masked base write under animation: ordinary channels `silent`; base=5; `H.Dispose()` ⇒ `notify(9→5, Local)` | AV+WPF |
| M152 | after M137 | `TearDown()` | `evict(E)` exactly once; **no** change notifications (PD13); reads afterwards return defaults | PIN (A13 + PD13) |

---

## 8. Frame-hosted entries — eviction order (M153–M166)

Cross-cutting block 3 (gates S2 and Fork B). `BindInFrame(P, hostFrame, listener)` — A5/A7/A8. Eviction fires on frame **removal** (and cookie retraction / `TemplateInstance.Detach()`, which reduce to removal at engine level), never on deactivation (PD3). Order per PD2: evict → recompute → notify.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M153 | `F(k1)` added | `EF = BindInFrame(P, F(k1))` | installs valueless: `silent`, eff=0 (A8) | PIN (A5/A8) |
| M154 | after M153 | `EF.Set(5)` | eff=5 src=`Style` — the entry contributes **at the frame's sort key**; `notify(0→5, Style)` | PIN (A5) |
| M155 | M154 + `F(k2){P=8}` | within-slot arbitration | eff=8 (k2 stronger); `EF.Set(6)` while masked is `silent`; `RemoveFrame F(k2)` ⇒ `notify(8→6, Style)` | PIN (A5: full sort-key citizenship) |
| M156 | after M154 | `EF.Unset()` | `HasValue`=false ⇒ promotion `notify(5→0, Default)`; entry remains hosted (A8) | PIN |
| M157 | after M154 | `RemoveFrame F(k1)` | order pinned: ① `evict(EF)` ② store recomputes ③ `notify(5→0, Default)` — listener observes the eviction **before** any observer sees the change (PD2) | PIN (PD2) |
| M158 | frame with `EF₁`, `EF₂` (two properties) | `RemoveFrame` | `evict(EF₁)` then `evict(EF₂)` in frame-entry index order, each exactly once; then one promotion notify per changed property | PIN |
| M159 | after M154 | `F(k1).SetActive(false)` | promotion `notify(5→0, Default)` but **no** `evict` (PD3); `SetActive(true)` re-applies the entry's held value ⇒ `notify(0→5, Style)` | PIN (PD3) |
| M160 | listener disposes `EF` from `OnEvicted` during `RemoveFrame` | re-entrant dispose | legal (A7); `evict(EF)` once; no double promotion | PIN (A7) |
| M161 | after M154 | `EF.Dispose()` directly | entry leaves the frame's contribution: `notify(5→0, Default)`; no `OnEvicted` (PD12); frame remains installed and otherwise functional | PIN |
| M162 | after M154 | `CV` | **no** eviction — A9 kills *local*-priority entries only; frame-hosted entry untouched, eff stays 5 (`CV` with no local contribution is a no-op) | PIN (A9 scope) |
| M163 | after M154 | `TearDown()` | sweeps frame-hosted and free-standing alike: `evict(EF)` exactly once, no change notifications (A13 + PD13) | PIN |
| M164 | M154 + `L(9)` | `RemoveFrame F(k1)` | `evict(EF)` fires even though the frame's value was masked; ordinary channels `silent` (effective unchanged) — eviction is lifecycle, not value-driven | PIN |
| M165 | after M157 | `EF.Set(7)` | dead entry: silent no-op; eff unchanged | PIN |
| M166 | fresh | `BindInFrame(P, frame)` where `frame` was never `AddFrame`d to this object | throws `ArgumentException` (host frame must be installed on the same store) | PIN |

---

## 9. Winning-base observer — `ObserverOptions.IncludeBaseChanges` (M167–M178)

Cross-cutting block 2 (gates S5 A3 — transitions are blind without it). Delivery shape: `(oldEffectiveBase, newEffectiveBase, bool isAnimated)`. Fires **only** when the winner among sub-Animation priorities changes; detected even under an active Animation entry (the sanctioned exception to "no notification on base write under animation"). Inherited changes ride the same seam (A20).

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M167 | base observer + plain observer; `L(3)`; `H.Set(9)` | `L(4)` | plain `silent` (M61); `baseNotify(3→4, anim:true)` exactly once | PIN (A20) |
| M168 | as M167 | `H.Set(10)` | animated write: plain `notify(9→10, Animation)`; base observer **silent** (base unchanged) | PIN (A20) |
| M169 | no animation; base observer + plain | `L(4)` | both fire: plain `notify(…, Local)`, `baseNotify(0→4, anim:false)` | PIN (A20) |
| M170 | `F(k1){P=5}`; `H.Set(9)` | `RemoveFrame` | plain `silent`; `baseNotify(5→0, anim:true)` | PIN (A20) |
| M171 | `L(3)`; `H.Set(9)` | `CV` | plain `silent`; `baseNotify(3→0, anim:true)`; `evict` of local entries still ordered per PD2 | PIN (A20) |
| M172 | `L(3)`; `H.Set(9)` | `H.Dispose()` | base unchanged ⇒ base observer **silent**; plain `notify(9→3, Local)` — animation attach/detach alone is not a base change | PIN |
| M173 | `L(3)` | `H = BeginAnimation(P)`; `H.Set(9)` | base observer `silent` on both (base still 3); plain `notify(3→9, Animation)` on the `Set` | PIN |
| M174 | `root.L(Pi,5)`; leaf entry-less, leaf base observer; `leaf.H.Set(9)` | `root.L(Pi,6)` | **inherited routing**: leaf plain channels `silent`; leaf `baseNotify(5→6, anim:true)` | PIN (A20 inherited seam) |
| M175 | as M174 without the animation | `root.L(Pi,6)` | leaf `baseNotify(5→6, anim:false)` **and** leaf plain delivery (A4) — both, exactly once each | PIN (A20+A4) |
| M176 | `L(3)`; `H.Set(9)`; base observer | `SCV(11)` | base observer `silent` (A11/A12: `SCV` under animation replaces the animated value, base untouched — M131) | PIN |
| M177 | base observer; `L(4)` twice | second `L(4)` | `silent` on the base channel too (equality-gated) | PIN |
| M178 | base observer subscription disposed | any base change | no delivery; disposal of an `ObserverOptions` subscription is independent of plain subscriptions on the same property | PIN |

---

## 10. Inheritance — lazy-read, eager-notify, shadowing, reparent (M179–M196)

Propagated deliveries on entry-less descendants assert the PD22 channel set (typed + untyped observers + the `OnInheritedPropertyChanged` virtual; metadata-`Changed` and the ordinary virtual stay silent); operations executed on a node itself keep the full four-channel `notify` meaning.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M179 | `root.L(Pi,5)` | `leaf.GetValue(Pi)` | `5` — walk-up to nearest contributing ancestor; leaf has **no store entry** (internal assert) | AV+WPF (lazy-read PIN) |
| M180 | as M179 | `leaf.GetValueSource(Pi)` | `Inherited` | AV+WPF |
| M181 | fresh tree | `leaf.GetValue(Pi)` | `0` src=`Default` (no contributing ancestor) | AV+WPF |
| M182 | leaf + mid entry-less, observers on both | `root.L(Pi,5)` | each descendant gets `OnInheritedPropertyChanged` (A3) **and** observer delivery (A4), args `(0→5, Inherited)`, exactly once each; root itself gets the ordinary `notify(0→5, Local)` | PIN (A3/A4) |
| M183 | `mid.L(Pi,7)` (shadow), then | `root.L(Pi,5)` | mid + leaf `silent` — propagation stops at the shadowing subtree root; leaf reads 7 | AV+WPF |
| M184 | as M183 | `mid.CV(Pi)` | shadow removal: mid `notify(7→5, Inherited)`; leaf `notify(7→5, Inherited)` via A3/A4 | AV+WPF |
| M185 | `root.L(Pi,5)`; separate `root2.L(Pi,8)` | `leaf.SetInheritanceParent(root2)` | reparent re-pull: leaf `notify(5→8, Inherited)` — diff over `InheritingPropertyIds` | AV+WPF |
| M186 | `root.L(Pi,5)`; `root2` non-contributing | reparent leaf to root2 | leaf `notify(5→0, Default)` | AV+WPF |
| M187 | `root.L(Pi,5)`; `root2.L(Pi,5)` | reparent leaf | `silent` (equal-value reparent diff) | AV+WPF |
| M188 | `root` animated: `root.H.Set(9)` on `Pi` | `leaf.GetValue(Pi)` | `9` — descendants inherit the ancestor's **effective** (animated) value | WPF (§2.3 pinned) |
| M189 | as M188 | `root.H.Set(10)` | leaf notified `(9→10, Inherited)` per animation frame (eager-notify; equality gate still applies) | PIN |
| M190 | `Pc`-like inheriting property with coercer (register `Pic`: inherits + clamp [0,100]); `root.L(Pic,250)` | `leaf.GetValue(Pic)` | root eff=100 (coerced at root); leaf=100 via walk; **inherited reads skip re-coercion** (single coercion at the set site) | WPF (§2.3: "misses skip coercion") |
| M191 | `root.L(Pi,5)`; `leaf.F(k1){Pi=9}` | interplay | leaf eff=9 src=`Style`; `root.L(Pi,6)` ⇒ leaf `silent` (style shadows inherited) | AV+WPF |
| M192 | `root.L(Pi,5)` | `leaf.IsSet(Pi)` | `false` — inherited contributions don't count (PD11) | AV+WPF |
| M193 | non-inheriting `P` set on root | `leaf.GetValue(P)` | `0` — no walk for `Inherits == false` | AV+WPF |
| M194 | `root.L(Pi,5)`, leaf entry-less | `root.DeferNotifications()` scope: `root.L(Pi,6)`, `L(Pi,7)`, dispose | leaf receives **one** coalesced delivery `(5→7, Inherited)` after flush (defer rides the propagation) | PIN |
| M195 | deep chain `root→a→b→c→leaf`, root contributes | `leaf.GetValue(Pi)` | correct value at depth (walk length ≥ 4); reparenting `b` re-pulls `b..leaf` only | AV+WPF |
| M196 | **DataContext rebind shape** (A4's motivating row): inheriting `StyledProperty<object?>` `Pdc`; observer on entry-less leaf | `root.SetValue(Pdc, newVm)` | leaf observer fires `(oldVm→newVm, Inherited)` — the S2 rebind hook; no leaf store entry created by observation | PIN (A4 + A22 countersignature) |

---

## 11. Attached properties (M197–M204)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M197 | `Host` instance | `SetValue(Pa, 2)` (i.e. `Tab.SetIndex(host, 2)`) | eff=2 src=`Local`; `notify(0→2, Local)`; full ladder semantics identical to non-attached | AV+WPF |
| M198 | fresh | `GetValue(Pa)` | `0` (declared default; storage is instance-keyed by dense id — nothing special) | AV+WPF |
| M199 | `Pa` with `GlobalEffects = AffectsArrange` written during the registration window | `Pa.GetEffects(typeof(Host))` | includes `AffectsArrange` — `Host`'s frozen per-type table never saw `Tab`'s registration; the **global lane** delivers (A1) | PIN (A1) |
| M200 | per-type lane: effects added for `DerivedHost` pre-freeze | `GetEffects(typeof(DerivedHost))` / `GetEffects(typeof(Host))` | derived = `perType \| Global`; base = `Global` only — two-lane OR (A1) | PIN (A1) |
| M201 | after `Host` resolved `Pa`'s **effects** (`GetEffects(typeof(Host))`) | write `GlobalEffects`, or `AddPerTypeEffects` for `Host` or a **base** of `Host` | throws `InvalidOperationException` — the global lane is frozen, and a per-type registration for `Host`/its base would invalidate `Host`'s cached result | PIN (A1) — **amended 2026-06-21**: the freeze is now PER-TYPE (mirrors `OverrideMetadata`'s `_resolved.Keys` gate), not a single property-wide flag. The old global freeze ("after **any** instance touched `Pa`, **any** effects write throws") was the cause of an `AddOwner` sibling-cascade: a sibling owner (e.g. a `Panel` rendering `Background`) closed the whole window, so a later sibling's `AffectsRender<Control>(Background)` in `Control..cctor` threw `TypeInitializationException` — pure static-ctor-order roulette. |
| M201a | after `Host` resolved `Pa`'s effects | `AddPerTypeEffects` for an **unrelated sibling** type, or a **derived** type of `Host` | **succeeds** (the sibling's resolution is independent; a derived type's registration can't invalidate the base's cache, and the derived type has not resolved) — the cascade fix | PIN (A1, amended) |
| M201b | after `Host` resolved `Pa`'s **metadata** only (`GetMetadata`/`GetValue`) | write `GlobalEffects` / `AddPerTypeEffects` (any type) | **succeeds** — metadata resolution is decoupled from the effects lane; it seals only metadata **overrides** for `Host` (`OverrideMetadata<Host>` throws), not effects | PIN (A1, amended) |
| M202 | frame `F(k1){Pa=3}` | add/remove | attached properties are styleable: src=`Style`, promotion on removal — same as M33/M35 | AV |
| M203 | inheriting attached `Pai` (`RegisterAttached(…, inherits: true)`) on root | `leaf.GetValue(Pai)` | walks the inheritance chain (the `ShowAccessKeys` shape) | AV+WPF |
| M204 | DEBUG build | `SetValue(Pa, 2)` on a non-`Host` `UIObject` | debug assertion (host-type validation); release: no check, write proceeds | PIN (proposal §2.1 `THost` note) |

---

## 12. Read-only properties — `UIPropertyKey<T>` (M205–M212)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M205 | fresh | `SetValue(Kro, 5)` | eff=5 src=`Local`; `notify(0→5, Local)` — key writes land in the LocalValue lane | WPF (`DependencyPropertyKey`) |
| M206 | fresh | `SetValue(Pro, 5)` (no key) | throws `InvalidOperationException` (PD14) | WPF |
| M207 | fresh | `SetCurrentValue(Pro, 5)` | throws `InvalidOperationException` (PD14) | PIN |
| M208 | after M205 | `ClearValue(Pro)` | throws `InvalidOperationException`; pinned: clearing requires the key surface too (key-holder API to be added when a consumer needs it) | PIN (PD14) |
| M209 | fresh | `Bind(Pro, LocalValue)` / `BeginAnimation(Pro)` | both throw `InvalidOperationException` (PD14) | WPF (read-only DPs reject binding/animation) |
| M210 | frame carrying an entry for `Pro` | `AddFrame` | throws `InvalidOperationException` at install (PD14) — Fork B surfaces this as a seal-time error before it ever reaches the store | WPF (read-only not styleable) |
| M211 | after M205 | `GetValue(Pro)` / `GetValueSource(Pro)` | reads are unrestricted: 5 / `Local` | WPF |
| M212 | observers on `Pro` | `SetValue(Kro, …)` | observation unrestricted; ordinary delivery (selector engines watch read-only state like `IsPressed` analogs) | WPF |

---

## 13. Direct properties — `SetAndRaise` (M213–M220)

No store, no coercion, no styling, no inheritance, no animation lane, no `PropertyEffects` routing (A24).

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M213 | `_d = 0` | `SetAndRaise(Pd, ref _d, 5)` | returns `true`; field = 5; observers + `OnPropertyChanged` fire `(0→5, LocalValue)`; **no metadata-Changed channel** (direct properties have no styled metadata) | AV |
| M214 | `_d = 5` | `SetAndRaise(Pd, ref _d, 5)` | returns `false`; `silent` (equality gate, `EqualityComparer<T>.Default`) | AV |
| M215 | fresh | `GetValue((UIProperty)Pd)` untyped | routes through the getter: boxed field value; no store allocation | AV |
| M216 | fresh | untyped `SetValue((UIProperty)Pd, 7)` | routes through the setter; raises as M213 | AV |
| M217 | setter-less direct property `Pdr` | untyped `SetValue(Pdr, 7)` | throws `InvalidOperationException` (read-only direct) | AV |
| M218 | fresh | untyped `SetValue((UIProperty)Pd, UIProperty.UnsetValue)` | setter receives the registered `unsetValue` (−1) — the descriptor's fallback contract | AV (`DirectPropertyBase.UnsetValue`) |
| M219 | fresh | `BeginAnimation`-equivalent / `AddFrame` with an entry for `Pd` / `Bind(Pd…)` typed-styled surface | unrepresentable or throwing: styled-typed APIs don't accept `DirectProperty` (compile-time); untyped frame entry for `Pd` ⇒ `AddFrame` throws `ArgumentException` (A24) | PIN (A24) |
| M220 | fresh | `GetValueSource((UIProperty)Pd)` | `Local`, `+cur` false — pinned: direct properties always report `Local` (field semantics; no ladder) | PIN |

---

## 14. Untyped lane (M221–M229)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M221 | fresh | untyped `SetValue((UIProperty)P, 5)` | identical to `L(5)` — same ladder, same notifications; typed `GetValue(P)` reads it back unboxed | AV+WPF |
| M222 | fresh | untyped `SetValue(P, "wrong-type")` | throws `ArgumentException` (type check at the mouth; no silent conversion) | AV |
| M223 | fresh | untyped `SetValue(P, null)` for non-nullable `int` | throws `ArgumentException`; for `Pcmp` (`string?`) null is a legal value | AV |
| M224 | `L(5)` + `E = Bind(P, LocalValue)` with value | untyped `SetValue(P, UIProperty.UnsetValue, LocalValue)` | ≡ `ClearValue(P)` **in full**: local value removed, `evict(E)`, promotion (PD5) | PIN (PD5) |
| M225 | `L(5)` | untyped `GetValue((UIProperty)P)` | boxed 5; semantic assertion is `Equals` only — box identity is an allocation behavior, not a contract (see M267) | AV |
| M226 | typed + untyped observers | `L(5)` | untyped observer receives a boxed copy with `Priority=LocalValue` (A10); fires **after** typed observers (M16 order) | PIN (A10) |
| M227 | fresh | `GetValue((UIProperty)P, maxPriority)` untyped overload parity | same lane-probing semantics as M114, boxed results | PIN (PD16) |
| M228 | enum-typed property `Pe` | untyped `SetValue(Pe, boxedEnum)` / `GetValue` | round-trips; interning covers small enums like bools/common values where feasible (allocation behavior only) | PIN |
| M229 | fresh | untyped `SetValue(P, 5, Style)` | throws `ArgumentException` (PD1 applies to the untyped mouth too) | PIN |

---

## 15. Coercion and validation (M230–M243)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M230 | `Pc` | `L(250)` | eff=100 (clamped); `notify(0→100, Local)`; raw slot holds 250 (PD6 — observable via M232) | WPF |
| M231 | `Pc` holding eff=100 from raw 250 | `L(120)` | raw differs (250→120) but coerced result equal ⇒ `silent`; `GetValue`=100; the raw slot now holds **120** (last-writer-wins under the gate — PD20 amendment; observable via `ReadLocalValue`) | WPF |
| M231a *(added 2026-07-08)* | `PcDyn` (ceiling from `Pmax`=100); `L(250)` (eff=100), then `L(120)` (gated, silent) | raise ceiling to 300; `CoerceValue(PcDyn)` | eff=**120** — the dance re-runs against the author's LAST write, never the first; `notify(100→120, Local)`. The regression row for the gate returning before recording the raw | WPF (PD6/PD20 amendment) |
| M231b | `Pc`; `L(250)` (eff=100, `IsCoerced`=true), then `L(100)` (gated, silent) | `GetValueSource(Pc)` | `IsCoerced`=**false** — the gate re-derives the provenance flags from the new raw (100 → 100 is not coercer-modified); `ReadLocalValue`=100 | PIN (PD20 amendment) |
| M232 | coercer reads instance state (clamp ceiling from another property `Pmax`); `Pc` raw 250, ceiling 100 (eff=100) | raise ceiling to 300; `CoerceValue(Pc)` | re-runs against the **raw** 250 ⇒ eff=250; `notify(100→250, Local)` — the WPF Maximum/Value dance, only possible because the raw value survives (PD6) | WPF (PD6) |
| M233 | as M232 | lower ceiling to 50; `CoerceValue(Pc)` | eff=50; `notify(250→50, Local)`; priority = current effective lane | WPF |
| M234 | `Pc` untouched | `CoerceValue(Pc)` | no-op, `silent` (default lane is not coerced, PD8) | WPF |
| M235 | `Pc` + frame `F(k1){Pc=250}` | resolve | frame values coerced at effective computation: eff=100 src=`Style` | AV+WPF |
| M236 | `Pcv` | `L(13)` (validate rejects) | throws `ArgumentException`; store untouched, `silent` | AV+WPF |
| M237 | `Pcv` | `L(140)` | validate sees **raw** 140 (≤150 passes), coerce clamps ⇒ eff=100 — PD7 order proven: a validate-after-coerce would also pass, so the discriminating case is M238 | WPF |
| M238 | `Pcv` | `L(160)` | validate rejects raw 160 (>150) **even though** coercion would have clamped it to a valid 100 ⇒ throws — validate-before-coerce pinned | WPF (PD7) |
| M239 | `Pcv` + entry | `E.Set(160)` | producer mouth: discarded + `OnRejectedValue`, no throw, previous value kept | AV |
| M240 | `Pv` | untyped `SetValue(Pv, −5)` | throws (same mouth as typed) | AV+WPF |
| M241 | coercer that throws | `L(v)` | exception propagates to the `SetValue` caller; store unmodified (strong guarantee on the local mouth) | PIN |
| M242 | `Pic` (M190) with a **counting** coercer; `root.L(Pic,250)` | `leaf.GetValue(Pic)` × 10, `mid.GetValue(Pic)` × 10 | coercer invocation count stays exactly 1 (the set site) — inherited reads never re-enter the coercer | WPF (§2.3) |
| M243 *(amended 2026-07-12)* | `Pc` | `SCV(250)` | covered by M133; row pins parity: `SetCurrentValue` coerces exactly like `SetValue` (the notification LANE differs by design — the graft replaces `Default`, a real write IS `Local`; PD27) | WPF |

---

## 16. `DeferNotifications` batching (M244–M251)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M244 | fresh | defer { `L(1)`; `L(2)`; `L(3)` } | inside: `silent`, `GetValue`=latest (1, then 2, then 3 — reads are live); at dispose: **one** `notify(0→3, Local)` (first old, last new) | AV (`BatchUpdate` spirit); PIN details |
| M245 | `L(5)` | defer { `L(9)`; `L(5)` } | zero notifications (first old == last new, equality-gated at flush) | PIN |
| M246 | fresh | defer { `L(1)`; `F(k1){Pc=120}` added } | one notification per property at flush, in **first-change order** (`P` then `Pc`) (PD15) | PIN |
| M247 | nested | defer { defer { `L(1)` } `L(2)` } | flush only at the **outermost** dispose: one `notify(0→2, Local)` | AV+WPF |
| M248 | entry `E` with value, defer scope | defer { `CV` } | `evict(E)` fires **immediately** inside the scope (lifecycle, PD15); the promotion's change notification flushes at dispose | PIN (PD15) |
| M249 | defer + animation | defer { `H.Set(9)` } | animated writes coalesce like any change: flush delivers `(old→9, Animation)` once | PIN |
| M250 | defer + `SCV` | defer { `F(k1){P=5}` exists; `SCV(6)` } | flush delivers `(5→6, Style)` (A11 priority preserved through coalescing) | PIN |
| M251 | observer mutates during flush | flush delivering property A's change triggers handler that sets property B (outside any defer) | B's notification dispatches synchronously (nested, PD18) — defer does not capture writes made after its dispose began | PIN |

---

## 17. Reentrancy (M252–M258)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M252 | observer o₁ on `P` sets `P` to 3 when it sees value 2; observer o₂ also subscribed | `L(2)` | **copied-value args stay uncorrupted**: both observers receive the outer change with old/new exactly `(0→2)` and the nested change as `(2→3)`; the nested dispatch completes before the outer finishes delivering (PD18 inversion); each delivery exactly once; final eff=3 | PIN (copied carriers, §2.1) |
| M253 | handler sets the **same value** it just observed (`L(2)` from inside `(…→2)` delivery) | `L(2)` | nested write equality-gated ⇒ no second dispatch; terminates | AV+WPF |
| M254 | convergent cycle: observer on `P` sets `Pc`, observer on `Pc` sets `P`, with converging values | `L(1)` | terminates via the equality gate once values stabilize; every intermediate change delivered exactly once | AV+WPF |
| M255 | divergent cycle (each handler increments) in DEBUG | `L(1)` | DEBUG depth assert trips at depth 64 (fail-fast diagnostics); release: unbounded by design (consumer bug) | PIN (§2.3 debug depth assert) |
| M256 | metadata `Changed` cb sets the same property | `L(2)` → cb sets 5 | cb runs before observers within each dispatch (M16); the nested dispatch completes first (PD18), so observers receive `(2→5)` and then `(0→2)`, each exactly once; store consistent, eff=5 *(amended at engine-2: the original "observers see (2→5) after (0→2)" wording contradicted PD18's pinned synchronous recursion — PD18 governs)* | WPF-shaped; PIN |
| M257 | handler calls `CV` from inside delivery of `(0→2)` | `L(2)` | nested clear dispatches `(2→0, Default)` synchronously; outer delivery continues with uncorrupted `(0→2)` args | PIN |
| M258 | handler disposes the animation handle from inside an Animation-priority delivery | `H.Set(9)` → handler `H.Dispose()` | legal; nested promotion `(9→base, …)` delivered; no corruption (copied args), no double-dispose effects | PIN |

---

## 18. Read surfaces — `GetValue(maxPriority)`, `GetBaseValue`, `GetValueSource`, `ReadLocalValue` (M259–M264e)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M259 | M112 full stack | `GetValue(P, LocalValue)` | 7 ≡ `GetBaseValue` (PD16) | PIN |
| M260 | M112 full stack | `GetValue(P, Style)` | 5 — skips Animation **and** Local | PIN (PD16) |
| M261 | M112 full stack | `GetValue(P, Inherited)` / `(P, Default)` | 2 / 0 | PIN |
| M262 | any | `GetValue(P, Unset)` | throws `ArgumentException` (PD16) | PIN |
| M263 | every scenario family | `GetValueSource` reports: fresh=`Default`; inherited=`Inherited`; frame=`Style`; local=`Local`; animated=`Animation`; `SCV`-overwritten adds `+cur`; never `Unset` | one consolidated `[Theory]` over the family table | PIN |
| M264 | diagnostics enumeration | with M112's stack, the frame/local enumeration surface lists: animation entry, local raw, each frame entry (sort-keyed), inherited provenance | shape pinned loosely — names/types are P0 implementation freedom; the *content set* is the contract | PIN |
| M264a *(added 2026-07-08)* | `Pc` (clamp [0,100]); `L(250)` | `ReadLocalValue((UIProperty)Pc)` / `TryReadLocalValue(Pc, out v)` | boxed **250** / `true`, v=250 — the **raw** pre-coercion value (PD6); `GetValue` stays 100. The typed/untyped raw-local read mouths, WPF `ReadLocalValue` parity | WPF (PD6) |
| M264b | fresh; frame-only (`F(k1){P=5}`); inherited-only (`root.L(Pi,2)`, read at leaf) | `ReadLocalValue(P/Pi)` | `UIProperty.UnsetValue` in all three — only a LOCAL contribution surfaces (the sentinel is this mouth's contract; M14 governs the effective-value mouths only). `TryReadLocalValue` = `false` | WPF |
| M264c *(amended 2026-07-12, PD27)* | fresh; `SCV(4)` | `ReadLocalValue(P)` / `TryReadLocalValue` | `UnsetValue` / `false` — the pure graft is local for STORAGE only, invisible to the local-authorship read (consistent with `GetValueSource` `Default+cur`); a real `SetValue` later overwritten by `SCV` still reports its latest raw write (M119) | WPF (`SetCurrentValue` is invisible to `ReadLocalValue`) — the former DEV is retired |
| M264d | direct `Pd = 5` | `ReadLocalValue((UIProperty)Pd)` | boxed 5 — field semantics, always local (M220 parity) | PIN |
| M264e | after M264a, `CV` | `ReadLocalValue(Pc)` | `UnsetValue` again; `TryReadLocalValue` = `false` (the raw slot dies with the local contribution) | WPF |

---

## 19. Allocation assertions (M265–M269)

Repo norm: deterministic oracle-pinned tables; assert via `GC.GetAllocatedBytesForCurrentThread()` deltas after warm-up, single-threaded, server-GC-agnostic. These are `[Fact]`s, not BenchmarkDotNet.

| # | Scenario | Expected |
|---|---|---|
| M265 | steady-state animated write: warm `H.Set(v)` loop with changing values on `StyledProperty<int>` and on a `readonly record struct` property (e.g. `Color`) | **0 bytes/write** after the first (entry exists, in-place mutate; copied-value args are structs) |
| M266 | `GetValue<T>` hot path on set and on default-valued properties | 0 bytes/read |
| M267 | untyped `GetValue(UIProperty)` repeated on an unchanged value | 0 bytes/read after first box (box-interning cache; M225's identity freedom is exactly this row's mechanism) |
| M268 | equality-gated `SetValue`/`H.Set` no-ops | 0 bytes/op |
| M269 | one-time costs (first write, first observer add) | pinned as *bounded*, not zero: first write allocates the store + one `EffectiveValue<T>`; observer add allocates the COW array slot + subscription token (bounded per add, never zero — each add returns a distinct `IDisposable`) — asserted as "the second write (after warm-up, observers subscribed) allocates 0" *(amended at engine-2 to scope "second identical operation allocates 0" to writes)* |

---

## 20. Template lane (M270–M301)

Added 2026-06-16 (PD24/PD25); **amended 2026-07-12 (PD26/PD24' — the completed Avalonia lattice)**. The Template lane carries everything a **control template authors on its parts** — a literal `SetValue`, a `{TemplateBinding}`/`{Binding}`, a `SetResourceReference` (`DataTemplate` content is app content at LocalValue — PD24 amended) — BELOW the conditional `StyleTrigger` slot so state-driven looks (`:pointerover`, `.obscured`, `When`) pierce a template's authored part values while active, and ABOVE the resting `Style` slot so a template author's literals and TemplateBinding plumbing are the part's **resting truth** (a broad structural rule cannot wreck template wiring; re-skinning at rest flows through the CONTROL's own properties — which resting styles CAN set — via the `{TemplateBinding}` forwarding spine, or through conditional rules).

*History.* The original 2026-06-16 pin put the lane below ALL styles — motivated by the close-button repro (a part literal stuck at LocalValue was unstylable) and justified at the time as the "inverse of WPF" (`judgment-styling-coherence.md` Flaw 1 later corrected the WPF claim: WPF actually puts template values ABOVE all styles). That half-adoption made template literals useless in the opposite direction — any resting rule stomped them — which is what drove the 2026-07-12 completion into Avalonia's `StyleTrigger > Template > Style` lattice. Rows below are pinned to the completed lattice; the pre-amendment expectations survive only in this note.

The lane is a **structural twin of the local lane** (a literal value + at most one binding entry, last-writer-wins within the lane) that *can be masked* by Style/Local/Animation, so it carries its own coerced + raw storage and resolves through `Reevaluate` rather than winning unconditionally. There is **no new public producer**: the lane is reached only through a thread-static **template-instantiation scope** open while a template's content tree is built (PD24). New notation:

- `T(v)` = a literal `SetValue(P, v)` issued **inside an open template-instantiation scope** — engine: routes to the Template lane (`SetTemplateValue`). Same value-bearing semantics as `L`, one rung weaker than Style. `[withdraw T]` = the lane-appropriate retraction (`TearDown` of the part, or the entry form's `Unset`/`Dispose`).
- `TE` = a Template-lane binding entry from `Bind(P, BindingPriority.Template)` (the engine twin of `E`; the in-template `{Binding}`/`{TemplateBinding}` install path). `TE.Set(v)`/`TE.Unset()`/`TE.Dispose()` as for `E`.
- W-value = 5, S-value = 9 as in §5 unless noted. Rows assert `eff`, `base`, `src`, and the exact notification.

### 20.1 Template over the resolution tiers (Template is the stronger side)

| # | Pair (S over W) | Operation | Expected | Oracle |
|---|---|---|---|---|
| M270 | Template over Inherited | `root.L(Pi,5)`; `leaf.T(9)` | leaf eff=9 base=9 src=`Template`; `notify(5→9, Template)` — a template default beats the inherited value | PIN (PD24) |
| M271 | Template over Inherited (withdraw) | `root.L(Pi,5)`; `leaf.TE.Set(9)` then `leaf.TE.Unset()` | leaf eff=5 src=`Inherited`; `notify(9→5, Inherited)` | PIN |
| M272 | Template over Default | `T(9)` on fresh | eff=9 base=9 src=`Template` IsSet=true; `notify(0→9, Template)` | PIN |
| M273 | Template over Default (equal) | `T(0)` on fresh (equals default) | `silent`; src=`Template`, IsSet=true (PD9 lane flip) | PIN |

### 20.2 The stronger lanes mask Template

| # | Pair (S over W) | Operation | Expected | Oracle |
|---|---|---|---|---|
| M274 *(re-pinned 2026-07-12)* | Trigger over Template; Template over resting Style | `T(5)` then `F(k1){P=3}` then `Ft(k1){P=9}` | the resting rule is masked `silent` (eff stays 5 src=`Template` — the literal is the part's resting truth); the CONDITIONAL rule pierces: eff=9 src=`StyleTrigger`; `notify(5→9, StyleTrigger)` | AV (the activator lattice; WPF puts template above both) |
| M275 *(re-pinned 2026-07-12)* | Trigger over Template (withdraw) | `T(5)`, `Ft(k1){P=9}`, then `RemoveFrame(Ft)` | eff=5 src=`Template`; `notify(9→5, Template)` — the template value resurfaces (clean retraction) | PIN |
| M276 *(re-pinned 2026-07-12)* | Trigger over Template (masked write) | `T(5)`, `Ft(k1){P=9}`; `T(6)` (template re-emit while masked) | `silent`; eff stays 9; `GetValue(P, Template)`=6 | PIN |
| M276b *(added 2026-07-12)* | Template over resting Style (masked write) | `F(k1){P=3}`, `T(5)` (Template wins); frame re-emits 3→4 | `silent`; eff stays 5; `GetValue(P, Style)`=4; `RemoveFrame` of the masked rung is also `silent` (src stays `Template`) | PIN (the inverse mask) |
| M277 | Local over Template | `T(5)` then `L(9)` | eff=9 src=`Local`; `notify(5→9, Local)` | PIN |
| M278 | Local over Template (withdraw) | after M277, `CV` | eff=5 src=`Template`; `notify(9→5, Template)` | PIN |
| M279 | Anim over Template | `T(5)` then `H.Set(9)` | eff=9 base=5 src=`Animation`; `notify(5→9, Animation)`; `H.Dispose()` ⇒ `notify(9→5, Template)` | PIN |

### 20.3 Full seven-rung ladder *(re-pinned 2026-07-12)*

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M280 | `root.L(Pi,2)`; leaf: `F(k1){P=3}`, `TE.Set(4)`, `Ft(k1){P=6}`, `L(7)`, `H.Set(9)` | peel top-down | eff=9; `H.Dispose()`⇒`notify(9→7, Local)`; `CV`⇒`notify(7→6, StyleTrigger)`; `RemoveFrame(Ft)`⇒`notify(6→4, Template)`; `TE.Unset()`⇒`notify(4→3, Style)`; `RemoveFrame(F)`⇒`notify(3→2, Inherited)`; `root.CV`⇒`notify(2→0, Default)` — six notifications, each exactly once, each at the promoted lane | PIN |
| M281 | the M280 stack | `GetValue(P, maxPriority)` probes | `Animation`→9, `LocalValue`→7, `StyleTrigger`→6, `Template`→4, `Style`→3 (the resting-slot probe deliberately skips the stronger Template lane, PD16), `Inherited`→2, `Default`→0 | PIN (PD16 amended) |
| M282 | the M280 stack | `GetBaseValue` at each peel step | 7, 6, 4, 3, 2, 0 — base tracks the strongest sub-Animation lane | PIN |
| M283 *(re-pinned 2026-07-12)* | apply-below-apply | fresh: `L(7)` first, then `T(5)`, `F(k1){P=3}`, `Ft(k1){P=8}` | the weaker rungs apply `silent`; eff stays 7; `GetValue(P, StyleTrigger)`=8, `GetValue(P, Template)`=5, `GetValue(P, Style)`=3; then `CV` ⇒ `notify(7→8, StyleTrigger)`; `RemoveFrame(Ft)` ⇒ `notify(8→5, Template)` — the trigger beats Template beats the resting rule | PIN |

### 20.4 The theme-forwarding invariant (the whole point) + the reported repro

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M284 *(re-pinned 2026-07-12)* | a part `border` carries `TE.Set(themeBrush)` (a `{TemplateBinding}` forwarding the control's style-set `Background`) | add a RESTING page rule `F(k1){Background=pageBrush}`, then an ACTIVATED one `Ft(k1){Background=hoverBrush}` | the resting rule is masked `silent` — the forwarded value IS the part's resting truth (`border` eff=`themeBrush` src=`Template`); re-skinning at rest styles the CONTROL's property (the forwarding spine). The activated rule pierces: eff=`hoverBrush` src=`StyleTrigger` | AV (the completed lattice; the 2026-06-16 "page style overrides forwarded value" pin is history) |
| M285 | M284 without the page frames | resolve | `border` eff=`themeBrush` src=`Template` — the forwarded value wins over Default | PIN |
| M286 *(re-pinned 2026-07-12)* | **close-button repro** | part `btn` with a template literal `T(Background=transparent)`; a resting window/page rule `F(k1){Background=accent}`; an armed-inactive `:pointerover` rule `Ft(k1){Background=hover}` toggled active/inactive | the resting rule is masked (eff stays `transparent` — the literal resists); the hover rule pierces while active (`notify(transparent→hover, StyleTrigger)`) and retracts cleanly (`notify(hover→transparent, Template)`). The ORIGINAL repro (literal stuck at LocalValue, unstylable even by conditional rules) stays fixed — the literal now lives on a maskable lane | AV (regression guard, both directions) |

### 20.5 SetCurrentValue × Template (mirrors §6)

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M287 | fresh | `SCV(4)` (the M118 as-Local graft) then `T(8)` | the graft **yields to the Template producer** (PD24's "one extra branch", mirroring M118→S100 for Style): eff=8 src=`Template`; the graft evaporates; `notify(4→8, Template)` | PIN |
| M288 | `T(5)` | `SCV(6)` | eff=6 src=`Template+cur` — provenance unchanged; `notify(5→6, Template)` (A11 replaced lane); base=6, IsSet=true | PIN (mirror M120/M121) |
| M289 | after M288 | template re-emits 5→8 (`T(8)`) | re-evaluation from the replaced lane clobbers the overwrite: eff=8 src=`Template` (`+cur` cleared); `notify(6→8, Template)` | PIN (mirror M122) |
| M289b | after M288 | template re-emits the **same** value (`T(5)`) | the re-emit still clobbers — a producer re-asserting drops the manual overlay, and the template's source (5) was held separately under the overwrite (the Style M122 analog, **not** the Local M119 case where SCV becomes the raw): eff=5 src=`Template` (`+cur` cleared); `notify(6→5, Template)`. The clobber is unconditional on a re-emit (precedes the equal-value gate). | PIN (Style-analog; the Template source is held separately) |
| M290 | after M288 with the rung as `TE` | `TE.Dispose()` | the overwrite evaporates with its lane: eff=0 src=`Default`; `notify(6→0, Default)` | PIN (mirror M123) |
| M291 *(re-pinned 2026-07-12)* | after M288 | `Ft(k1){P=9}` | a stronger lane wins and the overwrite is lost: eff=9 src=`StyleTrigger`; `notify(6→9, StyleTrigger)` (a RESTING frame sits below Template and would leave the overwrite in place) | PIN (mirror M124) |
| M292 | `T(5)` | `CV` | no *local* contribution to clear ⇒ no-op, `silent`; eff stays 5 src=`Template` — the Template lane is not "local" (`CV`/A9 kills local-priority producers only; only `[withdraw T]` removes it) | PIN |

### 20.6 Install seams, ValueSourceKind (PD25), IsSet, diagnostics

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M293 | scope routing | inside an open scope: a literal `SetValue`, a `Bind` install, a `SetResourceReference` | all three land at `Template`; the **identical** operations outside any scope land at `LocalValue` — the scope is the only trigger and restores on dispose (nested/re-entrant scopes are last-open-wins) | PIN (PD24) |
| M294 | public mouth unchanged | `SetValue(P, 1, BindingPriority.Template)` | throws `ArgumentException` — the lane is scope-driven, not parameter-driven (PD1 stands; PD24) | PIN |
| M295 | bind priorities | `Bind(P, BindingPriority.Template)` vs `Bind(P, Style/Animation/Inherited/Default)` | `Template` installs a Template-lane entry (the in-template binding install path); the others still throw `ArgumentException` — **M148 amended**: free-standing `Bind` now accepts `LocalValue` or `Template` (A6) | PIN (amends M148) |
| M296 | `Kind` annotation (PD25) | `GetValueSource(P).Kind` over the producer family | literal-local→`Local`; `T(v)`→`TemplateLiteral`; in-template `Bind`→`TemplateBinding`; in-template `SetResourceReference`→`TemplateResource`; plain setter→`StyleSetter`; `When`-guarded rule→`StyleWhen`; animation→`Animation`; inherited→`Inherited`; default→`Default`. `Kind` is a non-equality annotation (PD25) | PIN (PD25) |
| M297 | IsSet counts Template | `T(5)` | `IsSet(P)`=true — a template contribution is *set* (PD11 extended: S8 auto-aliasing yields to template-provided values exactly as it does to style/local ones) | PIN (PD11) |
| M298 *(re-pinned 2026-07-12)* | diagnostics enumeration | `GetValueDiagnostics(P)` on a stack carrying trigger + Template + resting contributions | rows strongest-first in ladder order: `StyleTrigger` frame rows, the `Template` row, resting `Style` frame rows, the Inherited provenance | PIN |

### 20.7 Coercion (PD6 parity — the Template lane is the local lane's twin)

The Template lane carries its own raw + coerced storage (`RawTemplateValue`/`TemplateValue`), so coercion mirrors the local lane (§15): the stored value is coerced at write, and `CoerceValue` re-runs the coercer against the **raw** template value (the WPF Maximum/Value dance) — notifying when Template wins, silent when masked.

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| M299 | `Pc` (clamp [0,100]) | `T(Pc, 250)` | eff=100 (coerced); src=`Template`; `IsCoerced` true; the raw template slot holds 250 (PD6 parity — observable via M300) | WPF (PD6) |
| M299b *(added 2026-07-08)* | `PcDyn` (ceiling from `Pmax`=100); `T(250)` (eff=100), then `T(120)` (gated, silent) | raise ceiling to 300; `CoerceValue(PcDyn)` | eff=**120** — the raw template slot is last-writer-wins under the gate (the local M231a twin; PD20 amendment) | WPF (PD6/PD20 amendment) |
| M300 | a coercer reading instance state (ceiling from `Pmax`); `T(Pcd, 250)` while Template wins, ceiling 100 ⇒ eff=100 | raise ceiling to 300; `CoerceValue(Pcd)` | re-runs against the **raw** 250 ⇒ eff=250; `notify(100→250, Template)` — the Maximum/Value dance on the Template lane (M232 analog), only possible because the raw template value survives | WPF (PD6) |
| M301 *(re-pinned 2026-07-12)* | M299 stack + a CONDITIONAL frame `Ft(k1){Pc=9}` masking the template (eff=9 — only the trigger slot masks Template now) | raise ceiling, `CoerceValue(Pc)` | the masked template re-coerces **silently** (eff stays 9 at StyleTrigger); removing the frame resurfaces the re-coerced template value | PIN |

---

## 21. Test authoring contract

Each numbered row above becomes **exactly one** xUnit test in `Cursorial.UI.Tests`, named after its row id with a behavior slug: `M042_OnEntryChanged_InPlaceReEmit_NotifiesAtStyle` (`[Fact]`) — rows whose Expected cell enumerates a family (M24, M114, M148, M263) become a single `[Theory]` with one `[InlineData]`/`MemberData` case per family member, keeping the row↔test bijection at the row level. Tests live under `Cursorial.UI.Tests/PrecedenceMatrix/`, one file per section (`Section01_Defaults.cs` … `Section19_Allocation.cs`, `Section20_TemplateLane.cs`), sharing the §0.1 fixture via a common harness class that registers the fixture properties once (`ModuleInitializer` or static lazy — dense ids are process-global, so registrations must be idempotent across test classes). Rows are not merged, reordered, or "covered implicitly by" other rows: a row without a matching test is a P0 exit-criterion failure (§14 P0: "precedence matrix green against the store"). DEBUG-only rows (M204, M255) compile under `#if DEBUG` and assert the absence of the check in release where practical. When the engine cannot honor a row, the resolution is a PR that amends this file (and, where the row carries a `PIN`/`DEV` tag, the PD ledger) **before** the engine change lands — the matrix is the oracle, not the implementation. Oracle tags (`AV`/`WPF`/`PIN`/`DEV`) are documentation of provenance and do not alter test behavior.





