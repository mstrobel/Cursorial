# Animation matrix (S5 / P8) — normative test spec

The oracle-pinned behavior matrix for `Cursorial.UI`'s animation orchestration (design doc §9). One
test per row, mirrored under `Cursorial.UI.Tests/AnimationMatrix/Section01…`. Authored **before** the
scheduler (doc §9.11 A0 mandate: completion / handoff / PingPong / reentrancy pinned first). Oracle
column: `WPF` = WPF/Avalonia storyboard parity; `PIN` = a Cursorial decision recorded in the AD ledger;
`MECH` = pure `Cursorial.Animation` mechanism (time-free, non-UI).

Phasing (doc §9.11): **A0** §§1–9, 13–15 · **A1** §§10–11 · **A2** §12 + §6 pause/seek rows · **A3** §16.
Sections past A0 are headed now and filled as each sub-phase lands (the prior-phase convention).

Vocabulary: `T_N` = the frozen frame time at `BeginFrame` of frame N. "self-sample" = the synchronous
first `SetValue` a `Begin` writes during the input/dispatcher drain (frame coherence, no tiers).
"effective base" = the winner among sub-`Animation` priorities (Fork A). All times via the session
`TimeProvider` (`FakeTimeProvider` in tests) through the shared `FrameClock` ⇒ deterministic.

---

## AD — pinned decision ledger

- **AD1 — one frozen clock.** `BeginFrame(in FrameTime)` is the frame's FIRST statement; `Clock.Now`
  is constant between `BeginFrame` calls. Every instance sampled in a frame sees one timestamp
  (invariant 1). Wall-clock monotonic (slow terminals drop frames, never stretch time). (doc §9.1)
- **AD2 — self-sample on Begin.** A `Begin` during the input/dispatcher drain stamps `StartTime = T_N`
  and writes its first sample synchronously; `TickNewlyStarted` (after the post-Tick styling flush)
  completes registration + completion processing for edge-ignited storyboards — no one-frame From-snap.
- **AD3 — completion is at-most-once, post-sampling.** `Completed` raises after the whole sampling pass
  (frame N's values coherent before user code observes), at most once per instance lifetime, and
  **never** on `Stop` / detach-stop / `Shutdown`.
- **AD4 — single From-snapshot.** The only `From` snapshot is the factory invocation at track start
  (synchronous inside `Begin` for `BeginTime == 0`). `GetBaseValue` is never used for handoff.
- **AD5 — PingPong final write is the start value.** The final write is always `ValueAt(Duration)`; for
  an AutoReverse/PingPong cycle that is the *start* value (documented, not a bug).
- **AD6 — perpetual guard.** `Duration == TimeSpan.MaxValue` is captured once and kept out of all
  arithmetic; perpetual instances never complete, can't `SkipToEnd` (throws), and a perpetual instance
  still pins the idle gate.
- **AD7 — reentrancy: flag-then-sweep.** State mutations apply immediately; `_running` membership
  changes are flag-then-sweep; the completed scratch drains index-over-live-Count; `Sample` re-reads
  `State` after every store write (a reentrant `Stop` from the write's own notification skips the
  completion branch); `Retract()` is idempotent.
- **AD8 — zero steady-state allocation.** A bounded handful at `Begin` (instance, handle, factory
  closure, built timeline); per-frame steady state is zero (`ValueAt` allocation-free for value types,
  in-place `SetValue`, reused scratch). `BrushTrack` is the one documented per-sample allocation.
- **AD9 — store equality gate is the cadence valve.** `AnimatedValueHandle<T>.SetValue` returns
  "effective value actually changed"; cell-quantized/byte-quantized interpolators + the store's gate
  mean writes land only on real changes (invariant 3). The sampler never returns render dirtiness.
- **AD10 — detach stops, silently.** S1 detach ⇒ retract + evict every instance targeting the subtree
  + every storyboard scoped in it; no `Completed`; idempotent against Fork B's retraction on the same
  detach. Re-attach does not restore HoldEnd.
- **AD11 — idle gate = Delayed + Running + running UITimers.** Excludes Paused/Holding/Completed
  (Holding costs nothing per frame — the Animation slot holds the value statically). Delayed pins the
  flag so S6 can't sleep through a `BeginTime`.
- **AD12 — interpolator registry is process-global, thread-agnostic, lock-free reads.** `Interpolator.For<T>()`
  throws a "register or specify" message for unknown `T`; `Register<T>()` is copy-on-write under a write lock,
  reads are lock-free immutable-snapshot. **Amended (2026-06-15):** the registry lives in the pure
  `Cursorial.Animation` layer, which has no UI-thread / `Dispatcher` concept, so there is **no UI-thread DEBUG
  assertion** — `Register` is safe from any thread by construction (the original "startup on the UI thread,
  DEBUG-asserted" note was an authoring convention for the UI layer's seeding, not a runtime contract). Pre-seeded:
  double/int/`Color` (Animation); `PointD`/`Size`/`Rect`/`RelativePoint`/`IBrush`/`CompositeParameters`/`Margins`
  (Drawing).
- **AD13 — `MarginsInterpolator` is signed** (LD19): per-side linear, rounded, **no zero-clamp** —
  tracks may interpolate through negative side values.
- **AD14 — Begin overload arity.** `target.BeginAnimation<T>(StyledProperty<T>, IAnimation<T>, opts)`
  (S5) does not collide with Fork A's `UIObject.BeginAnimation<T>(StyledProperty<T>) →
  AnimatedValueHandle<T>` (different arity) — the latter is the engine handle factory this layer uses.
- **AD15 — `AnimationsEnabled == false`:** finite ⇒ attach + snapshot + write `ValueAt(Duration)`
  synchronously + apply Fill + enqueue `Completed` for the next pass (never raised from `Begin`);
  perpetual ⇒ no handle, born `Stopped`, base shows. Flip `true→false` snaps finite at next `Tick`,
  retracts perpetual; `false→true` is prospective.

---

## 1. Frame clock + driver (A0) — N1–N12

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N1 | scheduler installed | `BeginFrame(FrameTime{Elapsed=100ms})` then read `Clock.Now` twice | both reads == 100ms (frozen between BeginFrame calls) | PIN (AD1) |
| N2 | clock frozen at 100ms | a `Begin` with `BeginTime=0` during the drain | the instance's `StartTime == 100ms` and it has written its first sample synchronously (self-sample) | PIN (AD2) |
| N3 | one finite Running instance | `Tick()` at a frozen `Now` | the instance is sampled exactly once; its `SetValue(ValueAt(Now−StartTime))` ran | PIN (AD1) |
| N4 | a `Begin` issued from inside another instance's sample callback | `Tick()` | the new instance is appended but **skipped this frame** (count snapshot); it self-sampled at Begin | PIN (AD2/AD7) |
| N5 | edge-ignited storyboard begun on this frame's styling flush | `TickNewlyStarted()` after the post-Tick flush | its registration + completion processing complete; no one-frame From-snap | PIN (AD2) |
| N6 | nothing started this frame | `TickNewlyStarted()` | cheap no-op (asserted: no allocation, no iteration cost) | PIN (AD2) |
| N7 | no instances, no timers | read `HasActiveAnimations` | false (S6 may idle) | PIN (AD11) |
| N8 | one Running instance | read `HasActiveAnimations` | true | PIN (AD11) |
| N9 | one Delayed instance (BeginTime in the future) | read `HasActiveAnimations` | true (S6 can't sleep through BeginTime) | PIN (AD11) |
| N10 | one Holding (finite, completed, FillBehavior.HoldEnd) instance | read `HasActiveAnimations` | false (Holding costs nothing per frame) | PIN (AD11) |
| N11 | one Paused / one Completed / one Stopped instance | read `HasActiveAnimations` | false for each | PIN (AD11) |
| N12 | a running UITimer + no instances | read `HasActiveAnimations` | true (running timers count — §9.8) | PIN (AD11) |

## 2. BeginAnimation + handle lifecycle (A0) — N13–N28

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N13 | attached element | `BeginAnimation(P, finite anim)` | returns an `AnimationHandle`; `State == Running`; `Target`/`Property` set; first sample written | WPF |
| N14 | Running finite, Duration 200ms | advance to `Now == Start+200ms`, `Tick` | final write == `ValueAt(200ms)`; `State → Holding` (FillBehavior.HoldEnd default); `Completed` raised once | WPF |
| N15 | Running finite, `Fill = Stop` | reaches Duration | the handle retracts (base resurfaces, invariant 4); `State → Completed`; `Completed` raised once | WPF |
| N16 | Holding instance | `Stop()` | store handle disposed ⇒ retraction ⇒ base resurfaces; `State → Stopped`; **no** `Completed` | WPF (AD3) |
| N17 | finite, `BeginTime = 50ms` | frames within [Start, Start+50ms) | `State == Delayed`; the property is **untouched** (no handle attached yet) | WPF |
| N18 | Delayed (BeginTime 50ms) | clock crosses Start+50ms, `Tick` | attaches, self-snaps From, `State → Running`; first sample at elapsed 0 | WPF/AD4 |
| N19 | zero-duration finite | `Begin` | reports `To` at elapsed 0 (one-frame set + completion); `State → Holding`/`Completed` per Fill | PIN |
| N20 | Running | `Begin` a second animation on the SAME (element, property) | the old instance retires (`Stopped`, evicted, **no** `Completed`); the new runs (handoff §4) | WPF (AD3) |
| N21 | two different properties on one element animated | both | independent instances, independent handles, independent completion | WPF |
| N22 | finite Running | read `Target`/`Property`/`State` mid-flight | reflect the live instance | WPF |
| N23 | handle whose `Completed` already raised | reaches a second nominal completion (e.g. re-seek) | `Completed` does **not** raise again (at-most-once) | PIN (AD3) |
| N24 | `Begin` on a **detached** element | — | no live handle (detach-stop covers it); does not pin idle | PIN (AD10) |
| N25 | finite, FillBehavior.HoldEnd, completed (Holding) | element detaches | instance retracted + evicted; **no** `Completed`; re-attach does not restore the held value | PIN (AD10) |
| N26 | perpetual (Duration MaxValue) Running | reach any large elapsed | never completes; never raises `Completed`; arithmetic never touches MaxValue | PIN (AD6) |
| N27 | `RepeatAnimation` whose repeat count × inner would overflow `TimeSpan` | `Begin` | guarded — no overflow; validated at seal (storyboards) or construction | PIN (AD6) |
| N28 | `StopAnimation(target, property)` with no running animation on it | call | no-op (idempotent) | PIN |

## 3. Completion semantics (A0) — N29–N34

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N29 | finite Running, Completed subscriber | reaches Duration during `Tick` | `Completed` raises **after** the whole sampling pass (frame-N values coherent first) | PIN (AD3) |
| N30 | two finite instances completing in the same frame | `Tick` | both sampled, then both `Completed` raised (completed scratch drained index-over-live-Count) | PIN (AD7) |
| N31 | a Completed handler that `Begin`s a new animation | completion frame | the new instance is enqueued and self-samples; same-frame delivery of callback-enqueued completions is drained | PIN (AD7) |
| N32 | finite | `Stop()` before Duration | no `Completed` ever | WPF (AD3) |
| N33 | finite Holding | `Shutdown()` | no `Completed`; handle retracted; base resurfaces | PIN (AD3/§9.6) |
| N34 | finite Running | element detaches before Duration | no `Completed`; retracted | PIN (AD10) |

## 4. Handoff — SnapshotAndReplace (A0) — N35–N41

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N35 | live Running on (E,P) at presented value V | `Begin` a new anim (BeginTime 0) with `From` unset | old retires (`Stopped`, evicted, no `Completed`); new `From` snapshots **V** (the presented animated value — no visual jump) | PIN (AD4/§9.4) |
| N36 | live Running on (E,P) | immediate replacement sequence | new timeline built while the old handle is still attached; new handle attaches (store last-started-wins detaches old); old defensively retracted (idempotent no-op) | PIN (§9.4) |
| N37 | live Running on (E,P) | `Begin` a **delayed** replacement (BeginTime > 0) | the old handle retracts at `Begin`; base shows during the delay window | PIN (§9.4) |
| N38 | delayed handoff in flight | a Transition is armed on P | the delayed handoff's retraction-promotion is **not** a base change ⇒ does not spuriously ignite the transition | PIN (§9.4) |
| N39 | new anim with explicit `From` | handoff | the explicit `From` wins (no snapshot) | WPF |
| N40 | retarget twice rapidly in one frame | two `Begin`s | only the last instance is live; intermediate retires with no `Completed` | PIN (AD3) |
| N41 | handoff | — | `GetBaseValue` is never consulted for the snapshot | PIN (AD4) |

## 5. PingPong / AutoReverse + Repeat (A0) — N42–N48

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N42 | AutoReverse finite (From A → To B) | sample at the forward midpoint | value interpolates A→B | WPF |
| N43 | AutoReverse finite | sample at the reverse midpoint | value interpolates B→A | WPF |
| N44 | AutoReverse finite | reaches full Duration (one there-and-back) | final write == `ValueAt(Duration)` == the **start** value A | PIN (AD5) |
| N45 | `Repeat = Count(3)` finite inner | total elapsed | three cycles; completes after the third; final = `ValueAt(Duration)` | WPF |
| N46 | `Repeat = Forever` | any elapsed | perpetual — never completes; pins idle (AD6/AD11) | WPF |
| N47 | AutoReverse + Repeat(2) | total | A→B→A→A→B→A cadence; final == start A | WPF (AD5) |
| N48 | `Repeat` wrapping a `Source` `IAnimation<T>` | — | Repeat/AutoReverse wrap the source uniformly | PIN |

## 6. Pause / Resume / Seek / SkipToEnd (Stop in A0; rest A2) — N49–N60

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N49 | Running | `Pause()` | `State → Paused`; `StateBeforePause` captured; value holds; drops out of idle (AD11) | WPF |
| N50 | Delayed | `Pause()` | legal; pauses the delay; `StateBeforePause = Delayed` | PIN |
| N51 | Paused | `Resume()` | restores captured state; `StartTime += pause span` (no time jump) | WPF |
| N52 | Running | `Seek(t)` on its own timeline (post-BeginTime), clamped | jumps to `ValueAt(t)`; works while Paused | WPF |
| N53 | Delayed | `Seek(t≥0)` | starts it (leaves Delayed) | PIN |
| N54 | Holding | `Seek(t < Duration)` | re-enters Running **without** re-raising `Completed` (at-most-once) | PIN (AD3) |
| N55 | Running | `Stop()` | retraction, base resurfaces; `State → Stopped`; no `Completed` | WPF (AD3) |
| N56 | finite | `SkipToEnd()` | snaps to end value + completes (one pass) | WPF |
| N57 | Delayed | `SkipToEnd()` | attach + From snapshot + end value + complete | PIN |
| N58 | Holding / Completed / Stopped | `SkipToEnd()` | no-op | PIN |
| N59 | **perpetual** | `SkipToEnd()` | throws `InvalidOperationException` | PIN (AD6) |
| N60 | StoryboardHandle.SkipToEnd with any perpetual track | call | validates all-finite **up front** ⇒ throws before touching any track | PIN (AD6) |

## 7. Reentrancy (A0) — N61–N66

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N61 | a store change notification (from a `SetValue`) calls `Stop()` on the same instance | `Tick`/sample | `Sample` re-reads `State` after the write and skips the completion branch | PIN (AD7) |
| N62 | a sample callback `Begin`s another animation | `Tick` | appended; skipped this frame; self-sampled | PIN (AD7) |
| N63 | a `Completed` handler stops a sibling instance | completion pass | flag-then-sweep keeps `_running` consistent; sibling not double-processed | PIN (AD7) |
| N64 | `Retract()` called twice (e.g. handoff + detach on same edge) | — | idempotent (second is a no-op) | PIN (AD7) |
| N65 | a callback disposes the handle whose completion is mid-delivery | — | no double-raise; no exception; registry consistent | PIN (AD7) |
| N66 | callback throws | `Tick` | propagates to S6's guarded tick; scheduler state stays consistent (state updated before callback) | PIN (§9.8/AD7) |

## 8. Perpetual + overflow guards (A0) — N67–N70

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N67 | `Duration == TimeSpan.MaxValue` | sample at huge elapsed | no overflow; the perpetual flag captured once short-circuits arithmetic | PIN (AD6) |
| N68 | `DelayAnimation(delay, perpetual inner)` | construct | `Duration` guarded (no `delay + MaxValue` overflow) | PIN (§9.11) |
| N69 | `SequenceAnimation` with a perpetual non-final child | construct | rejected (perpetual legal only in last position) | PIN (§9.11) |
| N70 | `SequenceAnimation` finite children | `checked` sum overflow | guarded (throws, not wraps) | PIN (§9.11) |

## 9. Interpolator registry (A0) — N71–N78

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N71 | fresh process | `Interpolator.For<double>()` | the pre-seeded double interpolator | PIN (AD12) |
| N72 | — | `For<int>()` / `For<Color>()` | pre-seeded (Animation) | PIN (AD12) |
| N73 | — | `For<Size>()` / `For<Rect>()` / `For<Margins>()` / `For<CompositeParameters>()` / `For<IBrush>()` | pre-seeded (Drawing) | PIN (AD12) |
| N74 | unknown `T` (unregistered struct) | `For<T>()` | throws with a "register `For<T>` or specify an interpolator" message | PIN (AD12) |
| N75 | `Register<T>(custom)` at startup | `For<T>()` | returns the registered one | PIN (AD12) |
| N76 | `MarginsInterpolator` From `(2,-1,0,3)` To `(6,3,0,-1)` | interpolate at 0.5 | per-side linear, rounded, **signed** (negative sides preserved) | PIN (AD13) |
| N77 | track with an explicit `Interpolator` | — | the explicit one wins over `For<T>()` | PIN |
| N78 | `Register<T>` off any thread | call concurrently with `For<T>()` reads | succeeds (COW under a write lock); reads stay lock-free and see a consistent snapshot. *Resolved 2026-06-15:* the pure registry is thread-agnostic — no UI-thread DEBUG assertion (AD12 amended); the substantive contract is thread-safe registration + lock-free reads | PIN (AD12) |

## 13. Mechanism combinators — `Cursorial.Animation` (A0) — N79–N88

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N79 | `DoubleAnimation(0,1,200ms)` | `ValueAt(100ms)` | 0.5 (linear) | MECH |
| N80 | same, `ValueAt(-10ms)` / `ValueAt(500ms)` | — | clamps to `ValueAt(0)` / `ValueAt(Duration)` (no extrapolation) | MECH |
| N81 | with an `Easing` | `ValueAt(t)` | eased progress applied before interpolation | MECH |
| N82 | `DelayAnimation(50ms, inner 200ms)` | `ValueAt(25ms)` | holds `inner.ValueAt(0)` during the delay | MECH (§9.11) |
| N83 | `DelayAnimation` | `Duration` | `checked(delay + inner.Duration)` | MECH |
| N84 | `SequenceAnimation(a 100ms, b 100ms)` | `ValueAt(100ms)` | the **next** child (b) wins at the boundary (half-open `[start,end)`) | MECH (§9.11) |
| N85 | `SequenceAnimation` with a non-final zero-duration child | sample around it | the zero-duration child is never sampled | MECH (§9.11) |
| N86 | `SequenceAnimation` | `ValueAt(≥ total)` | clamps to the last child's last value | MECH (§9.11) |
| N87 | `KeyframeAnimation` | `ValueAt` between keys | interpolates between bracketing keyframes (O(n) scan) | MECH |
| N88 | `.Delay(d)` / `.Then(next)` extensions | compose | equivalent to `DelayAnimation` / `SequenceAnimation` | MECH (§9.11) |

## 14. UITimer (shipped P2 — re-pinned here) — N89–N95

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N89 | `UITimer.Start(50ms, cb)` | clock crosses 50ms, `Tick` | `cb` fires once; `IsRunning → false` (one-shot) | PIN (§9.8) |
| N90 | `UITimer.Start(50ms, 30ms, cb)` | several `Tick`s past due | fires at most once per frame; re-arms from the frozen clock (ND20) | PIN (§9.8) |
| N91 | timer started from a timer callback | same `Tick` | armed at the frozen clock; does **not** fire this frame (count snapshot, N195) | PIN (§9.8) |
| N92 | timer callback throws | `Tick` | state updated **before** the callback ⇒ a thrower never re-fires; registry consistent | PIN (§9.8/N194) |
| N93 | running timer | read `HasActiveAnimations` | true (idle guard covers it) | PIN (AD11) |
| N94 | `UITimer.Restart()` | — | re-arms `dueTime` from the current frame clock | PIN (§9.8) |
| N95 | `Stop()` / `Dispose()` (twice) | — | idempotent | PIN (§9.8) |

## 15. Idle + detach-stop + Shutdown (A0) — N96–N101

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N96 | one Running instance | element subtree detaches | instance retracted + evicted; no `Completed`; idle gate drops to false | PIN (AD10) |
| N97 | a storyboard scoped under a detaching element | detach | every scoped instance retracted; idempotent vs Fork B's retraction on the same detach | PIN (AD10) |
| N98 | several live handles | `Shutdown()` | all retracted (bases resurface for one final restore frame); evicted; no `Completed`; scheduler inert | PIN (§9.6) |
| N99 | after `Shutdown()` | `Begin(...)` | throws (inert) | PIN (§9.6) |
| N100 | a clean steady-state animated slide | per-frame | zero `Scene.Invalidate()` calls (composite-shaped target); GC-asserted zero steady-state allocation | PIN (AD8/§9.9) |
| N101 | idle with no instances/timers, then a `Begin` | — | the gate flips true; S6 wakes and renders | PIN (AD11) |

---

## 10. Storyboards + tracks + ignition (A1) — N102–N121

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N102 | `Storyboard` with two finite tracks (different durations, different properties) | `Begin(scope)` | both run as independent child instances; each writes its own property; the storyboard completes when the longer finishes | WPF |
| N103 | a track with `TargetName` naming an element in the scope | `Begin` | resolves the named element via the template-aware `FindName` and targets it | WPF |
| N104 | a track with `TargetName == null` | `Begin` | targets the `Begin` scope element itself | PIN |
| N105 | a track with `From` unset | `Begin` | snapshots `GetValue(property)` at track start as `From` (AD4) | WPF |
| N106 | a track with an explicit `From` | `Begin` | the explicit `From` wins — no snapshot | WPF |
| N107 | a track with `BeginTime > 0` | frames within `[Begin, Begin+BeginTime)` then past | Delayed (property untouched) during the stagger, then Running | WPF |
| N108 | all-finite storyboard, `Completed` subscriber | the longest track reaches its end | `StoryboardHandle.Completed` raises once, after the sampling pass | WPF (AD3) |
| N109 | storyboard with one perpetual (`Loop`) track | any large elapsed | never completes (`Completed` never raises); pins the idle gate | PIN (AD6) |
| N110 | running storyboard | `StoryboardHandle.Stop()` | every child retracts (bases resurface); state stops; no `Completed` | WPF |
| N111 | begin the SAME storyboard on the SAME scope twice | second `Begin` | the first `(storyboard, scope)` instance retires (no `Completed`); the second runs (handoff §9.4) | PIN |
| N112 | begin the same storyboard on two different scopes | both, then `Stop(scopeA)` | two independent instances; scopeB keeps running | PIN |
| N113 | detach the scope element mid-flight | detach | every scoped child retracts; no `Completed`; idle drops to false (this is A0's N97 realized) | PIN (AD10) |
| N114 | a `Repeat = Count(3)` track | total elapsed | three cycles then completes; final = `ValueAt(Duration)` | WPF |
| N115 | a `Source`-based track wrapped by `Repeat` | run | `Repeat`/`AutoReverse` wrap the `Source` uniformly | PIN |
| N116 | a track whose `T` ≠ the property's value type | `Begin` (seal) | throws at seal — element-independent validation, before first run | PIN (§9.3) |
| N117 | a track with a `Repeat` that overflows `TimeSpan` | `Begin` (seal) | throws at seal (the `RepeatAnimation` guard) | PIN (AD6) |
| N118 | `Storyboard.Children` mutated after `Begin` | mutate | throws (sealed) | PIN |
| N119 | imperative `Begin` with an unresolvable `TargetName` | `Begin` | throws; no partial group left running (rolled back) | PIN (§9.3) |
| N120 | multi-track storyboard, one track `From` unset + one explicit, both on distinct properties | `Begin` | each track's `From` resolves independently | PIN |
| N121 | `Storyboard.Stop(scope)` when not running on that scope | call | no-op | PIN |

## 11. Edge actions (`BeginStoryboard`/`StopStoryboard`) + `AnimationDiagnostics` (A1) — N122–N130

| # | Setup | Operation | Expected | Oracle |
|---|---|---|---|---|
| N122 | a `Style` rule with a `BeginStoryboard` in `Enter` | the rule activates (pseudo-class flip) | the storyboard begins on the matched element (igniter = the `BeginStoryboard` action instance) | WPF |
| N123 | the SAME `BeginStoryboard` in BOTH `Enter` and `Exit` (do/undo — the SD16 seam delivers `OnActivated` to `Enter` entries, `OnRetracted` to `Exit` entries) | activate then deactivate | begins on activate, stops (retracts) on deactivate; no `Completed` | PIN |
| N124 | a `BeginStoryboard` in `Enter` only | the rule deactivates | the storyboard keeps running (no `Exit` undo; its `Fill` governs) | PIN |
| N125 | two rules sharing one `Storyboard` resource, both active on one element | activate both | distinct `(igniter, scope)` instances — they don't fight; each keyed by its own `BeginStoryboard` | PIN (§9.3) |
| N126 | a `StopStoryboard` (by object reference) in `Enter` | the rule activates | every live instance of that storyboard on the element stops, across igniters | PIN (§9.3) |
| N127 | an edge-ignited `BeginStoryboard` with an unresolvable `TargetName` | activation | does **not** throw; routes to `AnimationDiagnostics.TrackError`; sibling tracks proceed | PIN (§9.3) |
| N128 | multiple edge actions in one rule's `Enter` | activation | invoked in rule-document order on the edge (Fork B seam; re-pinned here) | PIN |
| N129 | the scope detaches while an edge-ignited storyboard runs | detach | retracts; no `Completed` | PIN (AD10) |
| N130 | `BeginStoryboard.Storyboard == null` / `StopStoryboard.Storyboard == null` | activation | no-op (no throw) | PIN |
## 12. `AnimationsEnabled` (reduced motion) full flip semantics (A2) — TBD
## 16. Transitions (implicit animations, winning-base observer) (A3) — TBD
