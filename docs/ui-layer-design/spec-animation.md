# S5 — Animation Orchestration (`Cursorial.UI` storyboard layer) — Subsystem Spec (FINAL, post-critique)

Status: implementable spec for `docs/ui-layer-design.md` §Animation. Conforms to `/tmp/cursorial-ui-design/DECISIONS.md` (Forks A/B/C + invariants 1–7) and the mechanism/orchestration split (design-doc §7: *"orchestration (clock, render loop, invalidation, triggers/storyboards, element lifecycle) lives in the future Cursorial.UI"*; *"the drawing layer stays time-free"*). Incorporates the adversarial-critique resolutions (disposition appended).

---

## 1. Scope

**This subsystem owns:**

- **`FrameClock`** — the TimeProvider-backed, monotonic, frame-frozen time source. S6 advances it once per frame; every animation in that frame samples one timestamp.
- **`AnimationScheduler`** — the active-animation registry and per-frame sampler; the source of the "keep rendering vs idle" signal S6 reads (the demos' `Animated`-flag pattern, generalized); the **element-detach stop pass** (production behavior, not a debug aid) and the **`Shutdown()` teardown** that restores base values before session end.
- **Imperative surface** — `element.BeginAnimation(Property, IAnimation<T>, options)` → `AnimationHandle` (pause/resume/seek/stop/skip-to-end, completion event, `FillBehavior`).
- **Declarative `Storyboard`** — `AnimationTrack` children with `TargetName`/`TargetProperty`, instanced per scope element, plus the styling fork's ignition actions `BeginStoryboard`/`StopStoryboard` with `HandoffBehavior.SnapshotAndReplace`, and the **no-throw runtime-ignition error policy** (`AnimationDiagnostics`).
- **Transitions** (Avalonia-style implicit property-change animations) — **decided: IN**, phase-gated (A3) behind one property-system seam (§4 item 5 — *not* small; budgeted in A3).
- **Property-system integration** — driving `AnimatedValueHandle<T>` at `BindingPriority.Animation` with per-frame in-place writes (zero steady-state allocation), completion semantics over Cursorial.Animation's universal clamp/HoldEnd, Stop = handle disposal = store-owned retraction (invariant 4).
- **New combinators** — `DelayAnimation<T>` and `SequenceAnimation<T>` as `IAnimation<T>` decorators, **added additively to `Cursorial.Animation`** (justified §3.10); `Parallel` deliberately *not* an `IAnimation<T>` (it is what a `Storyboard` is).
- **UI-local interpolators** — `ThicknessInterpolator` (iff S1 mints `Thickness`; see Open Q1) and the default-interpolator registry `Interpolator.For<T>()`.
- **Additive `Cursorial.Animation` extras** — `Easings.TryParse(string, out Easing)` (XAML needs name→delegate; also accepts `cubic-bezier(x1,y1,x2,y2)` syntax so the A2 factory is reachable from markup), and Elastic/Bounce/CubicBezier easings (phase A2; the map records the catalog gap).

**Explicitly NOT owned:**

- The render loop, frame pacing, idle strategy (S6 — we provide `BeginFrame`/`SampleFrame`/`HasActiveAnimations`/`Shutdown`; S6 decides when to call them and when to sleep).
- Styling activation/retraction edges and selector evaluation (S3 — we only implement the edge-action interface S3 invokes; edge actions are **no-throw by contract**, owned here).
- The `ValueStore`, `AnimatedValueHandle<T>` internals, priority arbitration (S2 — we are a pure client of `BeginAnimation<T>`/`GetValue`/`GetBaseValue`).
- Element tree, namescopes, layout, the registration of `PropertyEffects` flags on properties (S1 — we *comply* with the routing metadata; we never choose it). S1's attach/detach notification is a hard dependency (the detach stop pass).
- Pure timeline math (`Cursorial.Animation` stays the mechanism; everything we add there is timeline arithmetic, never clocks).
- Deferred outright: `SpeedRatio`, `HandoffBehavior.Compose`, additive/`By` animations, wake-at-time scheduling (§7).

---

## 2. Public API sketch

All new UI types: namespace `Cursorial.UI`, folder `Cursorial.UI/Animation/`. Additions to the mechanism layer: namespace `Cursorial.Animation`.

### 2.1 Clock and scheduler (the S6 contract surface)

```csharp
/// Monotonic, frame-frozen clock. Between BeginFrame calls, Now never moves —
/// every animation sampled during a frame sees the same timestamp (invariant 1).
public sealed class FrameClock
{
    public FrameClock(TimeProvider timeProvider);          // GetTimestamp-based; wall-clock jumps cannot affect it
    public TimeSpan Now { get; }                           // elapsed since clock creation, frozen at last BeginFrame
    internal void Tick();                                  // capture TimeProvider.GetElapsedTime(origin); scheduler-only
}

/// One per UI thread (invariant 6). S6 creates and installs it at loop start.
public sealed class AnimationScheduler
{
    public AnimationScheduler(TimeProvider timeProvider);

    public static AnimationScheduler Current { get; }      // ambient per-thread; throws if not installed
    public static void Install(AnimationScheduler s);      // S6 (or a test fixture) calls once per thread; replace allowed in DEBUG/tests

    public FrameClock Clock { get; }

    /// True while any instance is Running or Delayed (NOT Paused/Holding/Completed).
    /// S6's idle gate: false ⇒ event-driven frames only; true ⇒ timed frames.
    public bool HasActiveAnimations { get; }

    public void BeginFrame();                              // freeze the clock; call FIRST each frame, before input drain
    public bool SampleFrame();                             // sample all running instances at Clock.Now, raise completions;
                                                           //   returns true iff any store write actually changed a value.
                                                           //   NOT a "frame will paint" signal — see §3.9.
    /// Reduced-motion / slow-link switch. Full semantics §3.11: while false, finite
    /// animations snap to ValueAt(Duration) and complete through the normal completion
    /// pass; perpetual animations do not start (base values show — nothing pins the slot).
    public bool AnimationsEnabled { get; set; } = true;

    /// Session teardown (§3.10): retracts every live handle (bases resurface so a final
    /// restore frame can render base values), evicts all instances/storyboards, raises NO
    /// Completed events. Idempotent; subsequent Begin throws InvalidOperationException.
    public void Shutdown();
}
```

### 2.2 Imperative surface

```csharp
public enum FillBehavior : byte { HoldEnd = 0, Stop = 1 }
public enum HandoffBehavior : byte { SnapshotAndReplace = 0 }   // Compose deferred (recorded §7)

public enum AnimationState : byte { Delayed, Running, Paused, Holding, Completed, Stopped }

/// Controller for one running (element, property) animation. NOT the store handle —
/// it owns one AnimatedValueHandle<T> internally.
public sealed class AnimationHandle
{
    public AnimationState State { get; }
    public UIObject Target { get; }
    public UIProperty Property { get; }

    public void Pause();                  // freezes at current value (store keeps last write); leaves registry's active set.
                                          //   Legal in Delayed too — the pre-pause state is captured and restored by Resume.
    public void Resume();                 // restores the state captured at Pause (Delayed stays Delayed; Running resumes)
    public void Seek(TimeSpan offset);    // position on the animation's own timeline (post-BeginTime); clamped; works while paused.
                                          //   Seeking a Delayed instance to ≥ 0 starts it (handle attach + From snapshot happen then).
    public void Stop();                   // dispose store handle ⇒ retraction ⇒ base resurfaces (invariant 4). No Completed raise.
    public void SkipToEnd();              // finite only: write ValueAt(Duration), apply FillBehavior, raise Completed.
                                          //   Perpetual (Duration == TimeSpan.MaxValue) ⇒ InvalidOperationException.
                                          //   On a Delayed instance: attaches the handle, runs the From snapshot, writes the
                                          //   end value, completes. On Holding/Completed/Stopped: no-op (no second raise).

    /// Raised on the UI thread, after the frame's sampling pass, on natural completion or SkipToEnd.
    /// Never on Stop, never on detach-stop (§3.10), never on Shutdown. At most ONCE per instance
    /// lifetime — replaying via Seek does not re-raise; Begin a new animation to get a fresh event.
    public event Action<AnimationHandle>? Completed;
}

public readonly record struct AnimationStartOptions(
    TimeSpan BeginTime = default,                          // property untouched until BeginTime elapses (cf. DelayAnimation, §3.3)
    FillBehavior Fill = FillBehavior.HoldEnd,
    HandoffBehavior Handoff = HandoffBehavior.SnapshotAndReplace);

public static class ElementAnimationExtensions
{
    /// The element.BeginAnimation(Property, IAnimation<T>) entry point.
    /// First sample is written synchronously (frame coherence: a Begin during frame N's
    /// input drain is visible to frame N's layout/render).
    public static AnimationHandle BeginAnimation<T>(
        this UIObject target, StyledProperty<T> property, IAnimation<T> animation,
        AnimationStartOptions options = default);

    /// Stops whatever animation/transition currently holds the Animation slot for the property. No-op when none.
    public static void StopAnimation(this UIObject target, UIProperty property);
}
```

(No collision with S2's raw seam `UIObject.BeginAnimation<T>(StyledProperty<T>) → AnimatedValueHandle<T>` — different arity; that overload remains the engine-level handle factory, used only by this subsystem and documented as such.)

### 2.3 Declarative Storyboard

```csharp
/// Avalonia-style optional value (XAML: a converter on the inner type; unset = absent).
public readonly struct Optional<T>
{
    public bool HasValue { get; }
    public T Value { get; }                                // throws when !HasValue
    public static Optional<T> Unset => default;
    public static implicit operator Optional<T>(T value);
}

/// WPF-kin repeat descriptor; XAML converter accepts "1x", "3x", "Forever".
public readonly record struct RepeatBehavior
{
    public static RepeatBehavior Once { get; }
    public static RepeatBehavior Forever { get; }
    public static RepeatBehavior Count(int count);         // throws if < 1
    public bool IsForever { get; }
    public int  Iterations { get; }                        // 1 for Once
}

public abstract class AnimationTrack
{
    public string?     TargetName { get; set; }            // null ⇒ the Begin scope element; resolved via S1 namescope
    public UIProperty? TargetProperty { get; set; }        // required; XAML converter resolves "Control.Background"
    public TimeSpan    BeginTime { get; set; }             // stagger within the storyboard; property untouched until then
    public RepeatBehavior Repeat { get; set; } = RepeatBehavior.Once;
    public bool        AutoReverse { get; set; }           // maps onto the mechanism 2×2: Repeat/PingPong/Loop/AutoReverse
    public FillBehavior Fill { get; set; } = FillBehavior.HoldEnd;

    internal abstract AnimationInstance CreateInstance(UIObject target, AnimationScheduler scheduler, HandoffBehavior handoff);
}

public class AnimationTrack<T> : AnimationTrack
{
    public Optional<T> From { get; set; }                  // unset ⇒ snapshot GetValue(property) at TRACK START (§3.5 — the only snapshot)
    public Optional<T> To   { get; set; }
    public TimeSpan    Duration { get; set; }
    public Easing?     Easing { get; set; }                // XAML: name via Easings.TryParse ("QuadOut", "cubic-bezier(.4,0,.2,1)", …)
    public IInterpolator<T>? Interpolator { get; set; }    // null ⇒ Interpolator.For<T>()
    public IList<Keyframe<T>>? Keyframes { get; set; }     // Cursorial.Animation.Keyframe<T>; alternative to From/To
    public IAnimation<T>? Source { get; set; }             // code-built escape hatch; when set, overrides From/To/Keyframes.
                                                           //   Repeat/AutoReverse wrap Source in RepeatAnimation exactly as
                                                           //   they wrap built timelines (uniform — never silently ignored).
}

// Sealed non-generic conveniences (XAML-friendly; interpolator baked):
public sealed class DoubleTrack : AnimationTrack<double> { }
public sealed class Int32Track  : AnimationTrack<int> { }
public sealed class ColorTrack  : AnimationTrack<Color> { }                 // Cursorial.Media.Color
public sealed class BrushTrack  : AnimationTrack<IBrush> { }                // BrushInterpolator (allocates per sample; documented)
public sealed class RectTrack   : AnimationTrack<Rect> { }
public sealed class SizeTrack   : AnimationTrack<Size> { }
public sealed class ThicknessTrack : AnimationTrack<Thickness> { }          // iff S1 mints Thickness (Open Q1)

public sealed class Storyboard
{
    public IList<AnimationTrack> Children { get; }         // seals on first Begin OR on seal of a style holding a BeginStoryboard
                                                           //   referencing it (§3.6); mutation after sealing throws
    public StoryboardHandle Begin(UIElement scope, HandoffBehavior handoff = HandoffBehavior.SnapshotAndReplace);
    public void Stop(UIElement scope);                     // stops the imperatively-begun instance on this scope, if any (§3.6 keying)
}

public sealed class StoryboardHandle
{
    public bool IsCompleted { get; }                       // all finite tracks done; a perpetual track ⇒ never (documented)
    public void Pause(); public void Resume();
    public void Seek(TimeSpan offset);                     // STORYBOARD-timeline offset; per-track BeginTime subtracted (§3.4)
    public void Stop();
    public void SkipToEnd();                               // validates up front: ANY perpetual track ⇒ InvalidOperationException
                                                           //   before mutating anything (no half-skipped siblings)
    public event Action<StoryboardHandle>? Completed;      // at most once per instance lifetime
}
```

A shared `Storyboard` (e.g. one instance in a `Style`) is a *description*; `Begin` creates a per-scope `StoryboardInstance` (§3.5/§3.6). `Completed` lives on the handle, never on the shared description.

### 2.4 Styling ignition (the Fork B named contract)

```csharp
/// Implemented by storyboard actions; invoked by the styling engine on rule edges.
/// The styling fork owns where these hang (EnterActions-equivalent on a rule).
/// NO-THROW CONTRACT: implementations never propagate exceptions to the styling engine;
/// runtime failures route through AnimationDiagnostics (§3.6).
public interface IStyleEdgeAction
{
    void OnActivated(UIElement scope);                     // rule's conditions became met
    void OnRetracted(UIElement scope);                     // rule's conditions ceased to be met
}

public sealed class BeginStoryboard : IStyleEdgeAction
{
    public Storyboard? Storyboard { get; set; }            // typically {StaticResource}
    public HandoffBehavior Handoff { get; set; } = HandoffBehavior.SnapshotAndReplace;
    public bool StopOnRetraction { get; set; } = true;     // ":focus pulse while focused" without an explicit exit action
    // OnActivated ⇒ Storyboard.Begin-equivalent keyed by THIS action instance (§3.6);
    // OnRetracted ⇒ if StopOnRetraction, stop that instance.
}

public sealed class StopStoryboard : IStyleEdgeAction
{
    public Storyboard? Storyboard { get; set; }            // by object reference, not name string (deliberate WPF divergence:
                                                           //   no BeginStoryboardName registry; resources give identity for free)
    // OnActivated ⇒ stop EVERY live instance of the referenced storyboard on scope,
    //   regardless of which igniter began it (§3.6 keying); OnRetracted ⇒ no-op.
}

/// Runtime-ignition diagnostics hook (mirrors StyleDiagnostics): receives (storyboard,
/// track index, target name, scope) on name-resolution failure during edge-ignited Begin.
/// Default handler logs in DEBUG; apps may attach their own. The failing track is skipped.
public static class AnimationDiagnostics
{
    public static event Action<StoryboardTrackError>? TrackError;
}
```

### 2.5 Transitions (implicit animations — Phase A3)

```csharp
public abstract class Transition
{
    public UIProperty? Property { get; set; }
    public TimeSpan Duration { get; set; }
    public TimeSpan Delay { get; set; }
    public Easing? Easing { get; set; }

    // The attached property styles set (themes can declare hover fades):
    public static readonly AttachedProperty<TransitionCollection?> TransitionsProperty; // host: UIElement
    public static TransitionCollection? GetTransitions(UIElement e);
    public static void SetTransitions(UIElement e, TransitionCollection? value);
}
public class Transition<T> : Transition { public IInterpolator<T>? Interpolator { get; set; } }
public sealed class DoubleTransition : Transition<double> { }
public sealed class ColorTransition  : Transition<Color> { }
public sealed class BrushTransition  : Transition<IBrush> { }
public sealed class Int32Transition  : Transition<int> { }
public sealed class ThicknessTransition : Transition<Thickness> { }
public sealed class TransitionCollection : Collection<Transition> { }   // SEALS ON ARM (§3.7); mutation after arm throws
```

Semantics (pinned): a change to the **effective base** value (the winner among sub-Animation priorities — a Style flip shadowed by a LocalValue is *not* an effective-base change and does not transition) of a listed property, while the element is attached, starts an Animation-priority run with **From = the old presented value** (pinned precisely in §3.7 — the naive "From = `GetValue` at notification time" is wrong in the common non-animated case, where the store has already mutated the effective value before delivery), **To = the new effective base**, `FillBehavior.Stop` (on completion the handle retracts and the base value itself shows — zero steady-state animation entries). Transition writes are at Animation priority and therefore never re-trigger themselves. Equal From/To values skip. Initial style application and attach do not transition (armed only post-attach). `AnimationsEnabled == false` ⇒ no transition starts (base shows immediately — the reduced-motion rendition).

### 2.6 Interpolator registry and UI-local interpolators

```csharp
public static class Interpolator
{
    /// Default lookup used by tracks/transitions when none specified. Pre-seeded:
    /// double, int, Color (Cursorial.Animation); PointD, Size, Rect, RelativePoint, IBrush,
    /// CompositeParameters (Cursorial.Drawing); Thickness (Cursorial.UI). Throws with a
    /// "register or specify an interpolator" message for unknown T.
    public static IInterpolator<T> For<T>();
    public static void Register<T>(IInterpolator<T> interpolator);
    // Threading stance (pinned): process-global registry; Register at app startup on the UI
    // thread (DEBUG-asserted); For<T> reads an immutable snapshot swapped copy-on-write, so
    // lookups are lock-free. Pinned now because Open Q3 contemplates multi-session later.
}

public sealed class ThicknessInterpolator : IInterpolator<Thickness>  // per-side linear, rounded ties-away-from-zero, NO clamp —
{ public static ThicknessInterpolator Instance { get; } }             //   Thickness is signed (that is WHY S1 mints it; negative
                                                                      //   margins are legitimate); nothing detonates on overshoot.
                                                                      //   Clamping stays with the genuinely unsigned Size/Rect family.
```

Dependency direction holds: Animation ← Drawing ← UI. UI-type interpolators live here; nothing in lower layers learns about UI types.

### 2.7 Additive members in `Cursorial.Animation` (invariant 7 — additive only)

```csharp
public sealed class DelayAnimation<T> : IAnimation<T>      // holds inner.ValueAt(Zero) during delay
{
    public DelayAnimation(IAnimation<T> inner, TimeSpan delay);   // finite inner: Duration = checked(delay + inner.Duration) —
}                                                                 //   OverflowException at ctor; inner perpetual ⇒ TimeSpan.MaxValue
                                                                  //   (no arithmetic — guarded)

public sealed class SequenceAnimation<T> : IAnimation<T>   // children back-to-back; value of child k at (elapsed − Σ durations<k)
{
    public SequenceAnimation(params IAnimation<T>[] children);
    // Duration = checked sum (OverflowException at ctor). A perpetual child is legal ONLY in last
    // position (Duration ⇒ MaxValue); elsewhere ⇒ ArgumentException("perpetual animation cannot
    // precede another"). Boundary semantics (pinned): children own half-open intervals
    // [start_k, start_k+1) — at an exact boundary the NEXT child wins; a zero-duration child in
    // non-final position is therefore never sampled (documented: use a Keyframe for a step);
    // elapsed ≥ total Duration clamps to the LAST child at its own Duration.
}

public static class AnimationExtensions   // additions to the existing class
{
    public static IAnimation<T> Delay<T>(this IAnimation<T> animation, TimeSpan delay);
    public static IAnimation<T> Then<T>(this IAnimation<T> animation, IAnimation<T> next);   // 2-ary Sequence
}

public static class Easings   // additions
{
    public static bool TryParse(string name, out Easing easing);   // case-insensitive catalog lookup ("QuadOut" …);
                                                                   //   ALSO accepts "cubic-bezier(x1,y1,x2,y2)" (A2)
    public static Easing ElasticOut { get; } /* + ElasticIn/InOut, BounceIn/Out/InOut */     // phase A2
    public static Easing CubicBezier(double x1, double y1, double x2, double y2);            // phase A2; CSS-style factory
}
```

**Placement justification:** these are pure `elapsed → value` timeline arithmetic with no clock, no UI types, no state — mechanism by the §7 definition, exactly the seam the animation map names ("the decorator pattern of `RepeatAnimation<T>` generalizes — Sequence, Parallel, Delay … each just another `IAnimation<T>`"). Putting them in `Cursorial.Animation` keeps them usable by demos/non-UI consumers and is additive (invariant 7). **`Parallel` is deliberately absent**: an `IAnimation<T>` yields one value; "parallel" over one property has no composition semantics without additive animation (deferred). Parallelism across *properties* is what a `Storyboard` is; staggering is per-track `BeginTime`.

### 2.8 Consumer example — a notification toast

```csharp
// Code-behind ignition: slide in from off-screen right + fade in, then auto-dismiss after 4 s.
var toast = (Panel)window.FindName("toast")!;

var slideIn = new Int32Animation(from: 30, to: 0, TimeSpan.FromMilliseconds(250), Easings.CubicOut);
var fadeIn  = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(250));
var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(400), Easings.QuadIn)
                  .Delay(TimeSpan.FromSeconds(4));                       // mechanism combinator

toast.BeginAnimation(UIElement.OffsetColumnProperty, slideIn);           // AffectsComposite ⇒ recomposite only
var fade = toast.BeginAnimation(UIElement.OpacityProperty,               // AffectsComposite ⇒ recomposite only
    new SequenceAnimation<double>(fadeIn, fadeOut),
    new AnimationStartOptions(Fill: FillBehavior.HoldEnd));
fade.Completed += _ => window.CloseToast(toast);                         // completion event, UI thread
// CloseToast detaches the panel ⇒ the scheduler's detach pass (§3.10) retracts and evicts
// both Holding instances — no scheduler reference to the panel or this closure survives.

// Declarative equivalent in a style (XAML), ignited by the styling engine's activation edge:
// <Style Selector="Panel.toast:visible">
//   <Style.Enter>  <BeginStoryboard Storyboard="{StaticResource ToastIn}"/>  </Style.Enter>
// </Style>
// <Storyboard x:Key="ToastIn">
//   <Int32Track  TargetProperty="UIElement.OffsetColumn" From="30" To="0" Duration="0:0:0.25" Easing="CubicOut"/>
//   <DoubleTrack TargetProperty="UIElement.Opacity"      To="1.0"        Duration="0:0:0.25"/>
// </Storyboard>
// And an implicit hover fade, set by a theme style:
// <Setter Property="Transition.Transitions"> <TransitionCollection>
//     <BrushTransition Property="Control.Background" Duration="0:0:0.15" Easing="QuadOut"/>
// </TransitionCollection> </Setter>
```

---

## 3. Mechanics

### 3.1 Data structures (all single-UI-thread; no locks, DEBUG `VerifyAccess`)

```
AnimationScheduler
├── FrameClock _clock                                  // frozen TimeSpan per frame
├── List<AnimationInstance> _running                   // Delayed + Running; index-iterated, swap-removed in a post-pass
├── Dictionary<(UIObject, int propId), AnimationInstance> _byTarget
│       // ALL live instances incl. Holding/Paused — the handoff + StopAnimation + detach-pass lookup
├── Dictionary<(object igniter, UIObject scope), StoryboardInstance> _storyboards
│       // igniter = the BeginStoryboard action instance for edge ignitions, the Storyboard itself
│       // for imperative Begin (§3.6 — two rules sharing one storyboard resource never fight)
├── List<AnimationInstance> _completedScratch          // reused per frame; drained index-over-live-Count (§3.12)
└── int _activeCount                                   // Delayed + Running (excludes Paused) ⇒ HasActiveAnimations

internal abstract class AnimationInstance              // non-generic for the lists
{
    UIObject Target; UIProperty Property;
    TimeSpan StartTime;                                // frame-clock time of Begin
    TimeSpan BeginTime; TimeSpan PauseStartedAt;
    AnimationState State;
    AnimationState StateBeforePause;                   // Resume restores it (Delayed×Pause is legal, §3.4)
    FillBehavior Fill;
    bool CompletedRaised;                              // Completed at most once per lifetime (§3.12)
    abstract bool Sample(TimeSpan frameNow);           // returns "store write changed something"
    abstract void Retract();                           // dispose store handle; IDEMPOTENT (guard flag + S2 contract)
}

internal sealed class AnimationInstance<T> : AnimationInstance
{
    IAnimation<T> _animation;                          // built lazily at track start when From is unset; retained for the
                                                       //   instance lifetime (backward seek does NOT rebuild it — §3.4)
    Func<UIObject, IAnimation<T>>? _factory;           // tracks with unset From; runs AT MOST ONCE per instance
    AnimatedValueHandle<T>? _handle;                   // created at track start (post-BeginTime), not at Begin; null ⇒ Delayed
    bool _perpetual;                                   // Duration == TimeSpan.MaxValue, captured once
}
```

**Allocation accounting (corrected):** starting an animation costs a bounded handful of allocations — the instance, the store handle, and for tracks the factory closure + the built `IAnimation<T>` (storyboards add a `StoryboardInstance`, `StoryboardHandle`, and registry entries). The claim that matters is **per-frame steady state: zero** — `ValueAt` is allocation-free for value types (map-pinned), `SetValue` mutates the store entry in place, list iteration is index-based, the completed-scratch list is reused. (`BrushTrack` is the documented exception: one brush per sample, per the `BrushInterpolator` contract — fine at frame rate.) Under any-event mouse motion, edge-ignited Begin churn is bounded by *enter/leave edges* (pseudo-class flips), not by per-cell `Move` events — `:pointerover` hit-test churn never reaches this subsystem.

### 3.2 Frame protocol (S6 contract; invariant 1)

```
Frame N (S6's loop):
  1. scheduler.BeginFrame()          // clock freezes at T_N
  2. input drain + dispatch          // handlers may BeginAnimation/Stop/Begin storyboards:
                                     //   Begin stamps StartTime = T_N and writes its first sample SYNCHRONOUSLY
                                     //   (ValueAt(0) for immediate starts) — so the new value participates in
                                     //   frame N's layout/render: frame coherence holds with no tiers.
  3. scheduler.SampleFrame()         // see §3.3; instances begun in step 2 resample at elapsed 0 — equality-gated no-op
  4. S1 layout, S6 composite/render  // consume the invalidations the store's metadata callbacks raised
  5. idle decision: scheduler.HasActiveAnimations || pending-invalidation ? timed frame : await input
```

`SampleFrame` iterates `_running` with a count snapshot (instances begun during callbacks are appended and skipped this frame — they already self-sampled), marks completions, then sweeps state changes, then raises `Completed` callbacks from `_completedScratch`. Callbacks run after all sampling so every animated value for frame N is coherent before user code observes completions. The reentrancy contract for callbacks that call back into the scheduler is pinned in §3.12.

### 3.3 Per-instance sampling algorithm

```
Sample(frameNow):
    if State is Paused/Holding/Completed/Stopped: return false   (unless _seekPending — §3.4)
    raw = frameNow − StartTime
    if raw < BeginTime:                        // Delayed: property is UNTOUCHED (no handle) —
        return false                           //   distinct from DelayAnimation, which holds ValueAt(0); both available, documented
    if _handle is null:                        // track start (keyed on the handle, NOT on State == Delayed —
                                               //   survives Pause-while-Delayed/Resume, finding-4 fix)
        animation = _factory?.Invoke(Target) ?? _animation     // THE From snapshot: GetValue(property) NOW (§3.5 single rule)
        _handle = Target.BeginAnimation(property)              // store detaches any prior handle (last-started wins)
        State = Running
    effective = raw − BeginTime
    changed = _handle.SetValue(animation.ValueAt(effective))   // clamp inside ValueAt ⇒ hold-start/hold-end for free
    if State != Running: return changed        // reentrancy guard: the write's own change notification may have
                                               //   Stopped this instance synchronously (§3.12); skip completion branch
    if (!_perpetual && effective >= animation.Duration):       // NEVER StartTime + Duration — MaxValue overflow guard
        if Fill == HoldEnd:  State = Holding   // handle stays attached; value held by the Animation slot; leaves _running
        else /* Stop */:     Retract(); State = Completed      // store-owned retraction; base resurfaces (invariant 4)
        enqueue Completed                      // skipped if CompletedRaised already set
    return changed
```

Notes pinned from the maps:

- **Perpetual guard:** `_perpetual = animation.Duration == TimeSpan.MaxValue` is captured once at start; perpetual instances never enter the completion branch and their `Duration` never participates in arithmetic (the map's overflow hazard). `RepeatAnimation` finite-count overflow (`iteration × count`) throws at construction — we surface it from `Begin`/track build with the track identified.
- **PingPong-ends-at-start gotcha:** we never "set the property to `To` on completion" — the final write is always `ValueAt(Duration)`, which for a finite auto-reversed repeat is the *start* value. With `HoldEnd` it holds that start value (timeline-correct); documentation calls out that `PingPong + HoldEnd` is visually equivalent to `Stop` only when the base equals `From`.
- **Zero-duration asymmetry:** `Animation<T>` with `Duration == Zero` reports `To` even at elapsed 0 (map-pinned); a zero-duration track therefore acts as a one-frame set + immediate completion — reused by `AnimationsEnabled == false` snapping (§3.11).

### 3.4 Pause / Resume / Seek (offset bookkeeping over the pure timeline)

- `Pause()`: `StateBeforePause = State; PauseStartedAt = clock.Now; State = Paused; if (StateBeforePause != Delayed) /*already inactive? no—Delayed counts active*/ _activeCount--`. The store keeps the last written value; nothing is retracted. Legal from Delayed or Running.
- `Resume()`: `StartTime += clock.Now − PauseStartedAt; State = StateBeforePause; _activeCount++`. A resumed Delayed instance stays Delayed; its handle attach happens at track start as normal (the attach branch keys on `_handle is null`, so no NRE path exists).
- `Seek(offset)` (handle-level; offset on the animation's own timeline, post-`BeginTime`): `StartTime = clock.Now − BeginTime − clamp(offset, 0, finite ? Duration : ∞)`; if paused, one sample is forced at the next `SampleFrame` (a `_seekPending` flag bypasses the Paused early-out once). Seeking a Delayed instance to ≥ 0 starts it (handle attach + From snapshot happen then). Seeking a **Holding** instance to `< Duration` re-enters Running (rejoins `_running`, `_activeCount++`); `Completed` is **not** re-raised on the subsequent completion (`CompletedRaised` stays set — at-most-once, §2.2).
- **Storyboard-level operations are defined on the storyboard timeline** (finding-7 fix). `StoryboardHandle.Seek(t)`: for each child, `childOffset = t − track.BeginTime`:
  - `childOffset < 0` ⇒ the child returns to **Delayed**: `Retract()` its handle (idempotent; legal because Delayed-with-no-handle is the canonical pre-start state), `_handle = null`, `State = Delayed`. The built `_animation` (and its From snapshot) is **retained** — the factory runs at most once per instance lifetime, so re-crossing `BeginTime` re-attaches a handle and resumes sampling the *same* timeline deterministically.
  - else ⇒ child `Seek(clamp(childOffset, 0, finite ? trackDuration : ∞))` per the handle-level rules above.
- `StoryboardHandle.SkipToEnd()`: validates **up front** — if any track is perpetual, throw `InvalidOperationException` before mutating anything (no half-skipped sibling state). Otherwise each child runs the handle-level `SkipToEnd` (Delayed children attach + snapshot + snap, per §2.2).
- `StoryboardHandle.Pause/Resume/Stop` fan out per-child with the handle-level semantics. `StoryboardInstance` tracks remaining-incomplete count and raises its own `Completed` once, when it hits zero (perpetual children keep it nonzero forever — documented).

### 3.5 Handoff — `SnapshotAndReplace`

**The single From-snapshot rule (pinned, finding-2 fix): the only `From` snapshot is the factory invocation at track start** — which for immediate starts (`BeginTime == 0`) executes synchronously inside `Begin`. There is no separate snapshot at `Begin` for delayed tracks; §3.3 is the sole authority.

On `Begin` (imperative or track start) targeting an (element, property) that already has a live instance in `_byTarget`:

1. **Retire the old instance now:** `State = Stopped`, evicted from `_byTarget`/`_running` (and its storyboard's incomplete count). No `Completed` raise.
2. **Old handle fate depends on the replacement's start timing:**
   - **Immediate replacement (`BeginTime == 0`):** build the new animation first (the factory snapshot reads `GetValue(property)` *now* — while the old handle is still attached, this is the presented animated value, so no visual jump; the map's handoff note: *"snapshot current value as new From on retarget"*). Attach the new handle — the store's last-started-wins detaches the old one with no value perturbation (the new first sample writes in the same synchronous call). Then defensively `Retract()` the old handle — a no-op on a detached handle (§4 item 4).
   - **Delayed replacement (`BeginTime > 0`):** `Retract()` the old handle **at `Begin`** — the base resurfaces for the delay window, consistent with "Delayed = property untouched" (§3.3). No zombie window exists: the new instance has no handle until track start, and stopping it before `BeginTime` merely evicts the instance. The From snapshot happens at track start and observes whatever is presented then (typically the base) — one rule, no §3.3/§3.5 divergence.
3. If the new track has unset `From`, the track-start snapshot becomes `From`; an explicit `From` always wins (the factory is not built).
4. **No spurious transition:** retraction promotes the base to effective, but the *base itself did not change* — the §3.7 observer fires only on winning-base changes, so a delayed handoff's mid-delay base resurface cannot ignite a transition.

`GetBaseValue<T>` is *not* used for handoff (that would jump to base); it is available to control authors for "animate back to rest" patterns. **Transitions** read their retarget destination from the observer args, not from `GetBaseValue` (§3.7).

### 3.6 Storyboard instancing, ignition, and the runtime error policy

`Storyboard.Begin(scope, handoff)` / `BeginStoryboard.OnActivated(scope)`:

1. Seal `Children` if not yet sealed (mutation after sealing throws — styles precedent). **Element-independent validation runs at seal:** every track's `TargetProperty` type vs the track's `T`, `Source` perpetuity vs `Repeat` legality, `RepeatAnimation` overflow. When a `BeginStoryboard` is held by a `Style`, the style's seal-on-attach **also seals the referenced storyboard**, so type errors surface at style attach with (storyboard, track index, property) — the styling fork's diagnostics-first seal-time convention — rather than on first hover.
2. Registry key = `(igniter, scope)` where igniter is the `BeginStoryboard` action instance for edge ignitions and the `Storyboard` object for imperative `Begin` (finding-14 fix: two rules sharing one storyboard resource on one scope own independent instances and cannot stop each other via `StopOnRetraction`). If an instance already exists under that key: stop it first (re-trigger restarts — WPF `SnapshotAndReplace` behavior). `StopStoryboard.OnActivated` stops **every** live instance of the referenced storyboard on the scope, across igniters (scan); imperative `Storyboard.Stop(scope)` stops only the imperative-keyed instance.
3. For each track: resolve target = `TargetName is null ? scope : scope.FindName(TargetName)` (S1 namescope; template-aware — a storyboard begun from a template-scoped rule resolves names in the template namescope, so the template barrier is respected by construction).
4. **Error policy split (finding-8 fix):** imperative `Storyboard.Begin` **throws** on an unresolvable `TargetName` (caller's bug; diagnostics include storyboard, track index, name). **Edge-ignited begins never throw** — edge ignition is runtime, driven by pseudo-class flips from any-event mouse motion, and a typo'd theme name must not detonate mid-input-dispatch. Resolution failures route through `AnimationDiagnostics.TrackError` (storyboard, track index, name, scope); the failing track is skipped; sibling tracks proceed. The no-throw contract for `IStyleEdgeAction` is owned *here* (we catch); S3 may add defensive isolation but is not required to.
5. Build per-track `AnimationInstance` with the storyboard's shared clock start; register; immediate-start tracks write their first sample now (handoff per §3.5).
6. Return the `StoryboardHandle` wrapping the instance set.

`BeginStoryboard.OnRetracted` = `Stop` of the captured handle when `StopOnRetraction` (stop = retract all child handles; bases resurface — the styling engine's own setter retraction proceeds independently and the two never conflict because Animation and Style are different priority slots).

### 3.7 Transitions engine (Phase A3)

A scheduler-owned `TransitionService` keyed by element:

- **Arming:** the `TransitionsProperty` metadata `Changed` callback + S1 attach/detach notifications. On arm (attached ∧ collection non-null): **seal the `TransitionCollection`** (mutation after arm throws — finding-12 fix; replacing the collection property re-arms), then for each `Transition`, register a **winning-base observer** on its property (the §4 item-5 seam). On detach or collection-property change: dispose observers, stop running transition instances for that element.
- **Observer semantics (pinned, finding-1 + finding-6 fix):** the seam fires **only when the effective base — the winner among sub-Animation priorities — changes** (a Style-priority flip under a LocalValue changes nothing effective and must not fire; otherwise we'd animate to a value that never wins and snap back on retraction). Delivery carries `(T oldEffectiveBase, T newEffectiveBase, bool isAnimated)`.
- **From-value algorithm (pinned):**

```
on (oldEffectiveBase, newEffectiveBase, isAnimated):
    from = isAnimated ? target.GetValue(property)   // Animation slot pins the presented value — retarget from mid-flight position
                      : oldEffectiveBase            // NOT GetValue: per Fork A, a base write with no animation in flight mutates
                                                    // the effective value BEFORE notification, so GetValue already == newEffectiveBase
                                                    // at delivery time; oldEffectiveBase IS the old presented value here.
    if Equals(from, newEffectiveBase): return       // genuine no-op only — never suppresses the steady-state hover fade
    BeginAnimation(property,
        new Animation<T>(from, newEffectiveBase, transition.Duration, interp, easing)
            [.Delay(transition.Delay)],
        Fill: FillBehavior.Stop, Handoff: SnapshotAndReplace)
```

  Rapid flips (hover in/out/in) retarget smoothly from wherever the value currently is (the `isAnimated` branch). **Oracle test pinned for A3: "a hover flip with no prior animation in flight starts a transition from the old value"** — the exact case the naive `GetValue`-as-From algorithm gets wrong.
- Loop safety is structural: the observer fires only on sub-Animation-priority winning-base changes; transition writes are Animation priority.
- `AnimationsEnabled == false`: no transition starts (the base change simply shows — §3.11).

### 3.8 Routing compliance — the property-targeting table (invariant 3)

This subsystem **never** touches `Scene`/`CellBuffer`/compositor (invariant 2): every effect flows through the store and S1's `PropertyEffects` routing. Authoring guidance + the v1 table:

| Animation intent | Target property (S1) | Effects flag | Per-frame cost & gating |
|---|---|---|---|
| Move / slide a panel, toast, drawer | `OffsetColumn` / `OffsetRow` (S1's signed composite-offset ints; negative placement lives here per DECISIONS) | `AffectsComposite` | `CompositeParameters` refresh → compositor recomposites old+new footprint union of the **cached** raster. `Int32Interpolator` quantizes to cells, store equality gate absorbs sub-cell frames → writes only on cell crossings. **Never re-rasters.** |
| Fade in/out, modal dim | `Opacity` (double 0–1, coerce-clamped; S1 maps to `CompositeParameters.Opacity` byte) | `AffectsComposite` | Recomposite only. Byte quantization at the params mapping means ≤256 distinct compositor updates per fade; identical-byte frames diff to equal `CompositeParameters` ⇒ compositor no-op. |
| Reveal / wipe / collapse-without-layout | `CompositeClip` (`Rect?`) | `AffectsComposite` | Recomposite; `RectInterpolator` rounds+clamps (ushort `Rect` safety, Back* overshoot tolerated). Clip in `CompositeParametersInterpolator` snaps at 0.5 — animate the `Rect` property directly instead. |
| Color / brush pulse (background, border, foreground) | `Background` / `BorderBrush` / `Foreground` | `AffectsRender` | `Scene.Invalidate()` → **re-raster** of that element's scene on each frame where the sampled value actually changed (store equality gate is the cadence valve; a `ColorAnimation` changes every frame at truecolor — keep pulsing scenes small, or pulse `Opacity` instead). |
| Size / position in layout (accordion, splitter) | `Width`/`Height`/`Margin` (`AffectsMeasure`) | `AffectsMeasure` | Full measure/arrange — legitimate but the expensive lane; cell-quantized interpolators (`Size`/`Rect`; `Thickness` rounds but does not clamp) gate to actual cell changes. DEBUG diagnostic: warn when a **perpetual** animation targets an `AffectsMeasure` property (layout thrash). |
| Text/content change (typewriter, counters) | `Text`, content props | `AffectsRender` (+Measure) | Re-raster; `Int32Animation` over an index + equality gate gives per-character cadence. |

The invariant-3 sentence for this spec: **animated slides/fades/clips write only composite-shaped properties and therefore re-composite a cached raster; only brush/content-shaped targets re-raster, at the store's equality-gated cadence.**

### 3.9 Idle signal (the demos' `Animated` flag, generalized)

`HasActiveAnimations` ≡ `_activeCount > 0` (Delayed + Running; excludes Paused/Holding/Completed). Holding instances cost nothing per frame (the Animation slot holds the value statically). Delayed instances keep the flag true so S6 can't sleep through a `BeginTime` (wake-at-time scheduling is the recorded optimization, §7); frames during delay are cheap end-to-end (no writes ⇒ compositor `false` path ⇒ renderer emits nothing). The detach pass (§3.10) guarantees a detached element's perpetual animation cannot pin the flag true — the idle gate is production-correct, not best-effort.

**`SampleFrame`'s bool is "at least one stored value changed", not "this frame will paint"** (finding-13 pin): a double-typed `Opacity` fade changes the stored double every frame even when the byte-quantized `CompositeParameters` and the compositor's `Composite(...) == false` path — the authoritative no-op gates — produce zero wire bytes. S6 may use `false` as a strong "skip everything" hint but must not treat `true` as a paint predictor.

### 3.10 Lifetime, teardown, and leaks

- **Detach stops animations — production behavior, Phase A1 (finding-3 fix).** On S1's detach notification, the scheduler stops (retract + evict from all registries; **no `Completed` raise**) every `AnimationInstance` whose `Target` is in the detached subtree and every `StoryboardInstance` whose scope is. This is the same S1 notification Transitions arming already requires; it runs unconditionally, not DEBUG-only. Consequences, documented: **re-attach does not restore HoldEnd values** (HoldEnd persists only while attached — begin a new animation after reattach); a detached element's perpetual animation can no longer hold `HasActiveAnimations` true (idle-gate correctness); the §2.8 toast example leaks nothing — `CloseToast`'s detach evicts both Holding instances, their handles, and the user's `Completed` closure. Idempotent against the styling engine's own retraction edges firing on the same detach (`Retract` is idempotent, eviction is a no-op the second time).
- **Holding is bounded by design:** one entry per (element, property) in `_byTarget`, evicted on detach. Stopped/Completed-with-Stop entries are removed immediately.
- **DEBUG leak tracker (A2)** narrows to the residual shape detach-stop can't see: live instances whose target was **never attached** for > N frames (mirrors the styling fork's subscription-leak tracker). It supplements the production pass; it is not the answer to it.
- **Teardown (finding-19 fix):** `AnimationScheduler.Shutdown()` (A0) retracts every live handle — bases resurface, so S6 can render one final restore frame with un-animated values before session disposal — evicts all instances and storyboards, raises no `Completed` events, and leaves the scheduler inert (`HasActiveAnimations == false`; subsequent `Begin` throws `InvalidOperationException`). S6 calls it during session teardown, before the terminal-mode restore.
- Imperative perpetual animations on *attached* elements remain the caller's to stop — documented; storyboard ignition via styles auto-stops on retraction (`StopOnRetraction`), which covers the dominant declarative pattern.

### 3.11 `AnimationsEnabled` semantics (pinned; finding-9 + finding-20 fix)

- **`Begin` while `false`:** finite animation ⇒ attach the handle, run the From snapshot, write `ValueAt(Duration)` **synchronously**, apply `FillBehavior` (HoldEnd ⇒ Holding entry; Stop ⇒ retract immediately), and **enqueue `Completed` for the next frame's completion pass** — never raised synchronously from `Begin`, preserving §2.2's "after the sampling pass" ordering. Perpetual animation ⇒ **no handle is attached**; the returned `AnimationHandle` is born `Stopped` and the property shows base. (Decision over the original "snap to `ValueAt(Zero)`": pinning a pulse at its time-zero frame forever at Animation priority would block base styling and create exactly the Holding-forever entries findings 3/19 police; the base value *is* the reduced-motion rendition of a pulse.)
- **Flip `true → false`:** applied at the next `SampleFrame`. Every live finite instance (Delayed, Running, or Paused) jumps to `ValueAt(Duration)` and runs the **normal completion branch** (Delayed instances attach + snapshot first; `Completed` raises in the normal pass). Every live perpetual instance is retracted (`Stopped`, base resurfaces, no `Completed`). Holding instances are unaffected (already at end value). Storyboards mid-stagger follow per-track rules; a storyboard whose tracks all complete raises its `Completed` normally. In-flight transitions are finite and snap-complete like any other instance (with `FillBehavior.Stop` they retract — base shows).
- **Flip `false → true`:** prospective only; nothing snapped is resurrected.
- New transitions do not start while `false` (§3.7).

### 3.12 Reentrancy contract (pinned; finding-5 fix)

All scheduler state is single-UI-thread; "reentrancy" means synchronous callbacks (metadata `Changed` callbacks raised by our own store writes, observers, `Completed` handlers) calling back into `Begin`/`Stop`/`Pause`/`Seek`/`SkipToEnd`:

- **State mutations apply immediately; `_running` membership changes are deferred to the end-of-`SampleFrame` sweep.** The sampling pass iterates by index with a count snapshot; `Begin` during any callback appends (and self-samples synchronously — visible this frame via the store, sampled by the loop next frame); removals are flag-then-sweep.
- **`Sample` re-reads `State` after every `_handle.SetValue`** (§3.3): a reentrant `Stop` fired from the write's own change notification marks the instance `Stopped` mid-sample; `Sample` returns without running the completion branch on a stopped instance.
- **`Retract()` is idempotent** (instance-side guard flag), and S2's `AnimatedValueHandle.Dispose` is contractually idempotent with post-detach `SetValue` a silent no-op (§4 item 4) — double-dispose via reentrant Stop + sweep is safe.
- **`_completedScratch` is drained with an index loop over the live `Count`** — completions enqueued *during* the raise (e.g. `SkipToEnd` called from a `Completed` handler) are delivered in the same frame's pass; the list is cleared after the loop exhausts.
- **`Completed` raises at most once per instance lifetime** (`CompletedRaised` flag); `SkipToEnd` on Holding/Completed/Stopped is a no-op. Replay-with-completion = begin a new animation.

---

## 4. Cross-subsystem contracts

**REQUIRES from S2 (property system):**

1. `AnimatedValueHandle<T> UIObject.BeginAnimation<T>(StyledProperty<T>)` — last-started-wins, dispose ⇒ retraction + promotion, in-place equality-short-circuited `SetValue` (all per DECISIONS Fork A; exists).
2. `T GetValue<T>(StyledProperty<T>)` / `T GetBaseValue<T>(StyledProperty<T>)` (exists).
3. **NEW (small):** `bool AnimatedValueHandle<T>.SetValue(T value)` — return "effective value actually changed" (the store already computes it in `SetEffective`). Fallback if declined: `SampleFrame` returns `_running.Count > 0`, losing write-precision for S6's dirty signal.
4. **NEW (small):** `bool AnimatedValueHandle<T>.IsDetached { get; }`; `SetValue` after detach is a silent no-op; **`Dispose` is idempotent** — the scheduler retires superseded instances defensively and the reentrancy contract (§3.12) double-disposes legally.
5. **NEW (gates Phase A3; NOT small — budgeted in A3):** winning-base change notification while an animation holds the slot — e.g. `IDisposable AddObserver<T>(StyledProperty<T>, IValueObserver<T>, ObserverOptions options)` with `ObserverOptions.IncludeBaseChanges`, firing **only when the effective base (the winner among sub-Animation priorities) changes** and delivering `(T oldEffectiveBase, T newEffectiveBase, bool isAnimated)`. Two non-trivial obligations make this medium-sized: (a) the store must detect winning-base changes under an active Animation entry, where its documented behavior is "no notification on base write under animation"; (b) **inherited changes must ride this seam too** — they arrive via Fork A's "second small carrier" on entry-less descendants, which must be routed into the same observer with the same old/new effective-base shape. Without this seam, transition retargeting is blind (§3.7); with a weaker any-base-write version, transitions animate to values that never win (finding 6).

**REQUIRES from S1 (element tree / layout):**

- `UIElement : UIObject` + namescope lookup `UIObject? FindName(string)` (template-aware) for `TargetName` resolution.
- **Attach/detach lifecycle notification (event or virtual) — production-critical from A1**: drives the detach stop pass (§3.10), Transitions arming (A3), and the DEBUG leak tracker. Detach notification must cover whole-subtree removal (one notification per detached element, or a subtree root + enumerable — either shape works; the scheduler indexes by element).
- Property registrations with correct `PropertyEffects` per the §3.8 table (`OffsetColumn`/`OffsetRow`/`Opacity`/`CompositeClip` = `AffectsComposite`); the decision on `Thickness` vs reusing Rendering's `Margins` (Open Q1).

**REQUIRES from S3 (styling):**

- Invoke `IStyleEdgeAction.OnActivated`/`OnRetracted` on each rule's activation/retraction edges, in rule-document order, for actions held in its enter/exit collections (the DECISIONS Fork B ignition vocabulary). Actions are shared per style; we own per-element instancing. **The no-throw contract is ours** (§3.6) — S3 may add defensive exception isolation around edge actions but is not required to.
- Style seal-on-attach must seal `Storyboard` resources referenced by `BeginStoryboard` actions (§3.6 step 1), surfacing type errors at attach time.

**REQUIRES from S7 (XAML):**

- Converter dispatch by `PropertyType` for: `UIProperty` (`"Control.Background"` via the registry), `Easing` (delegating to `Easings.TryParse`, which covers both catalog names and `cubic-bezier(…)`), `Optional<T>` (inner-type converter), `RepeatBehavior` (`"3x"`/`"Forever"`), `TimeSpan` (BCL). `Storyboard`/`TransitionCollection` are ordinary objects in resource dictionaries — no deferral contract needed.

**REQUIRES from S6 (render loop):**

- Construct + `AnimationScheduler.Install` once per UI thread with the session's `TimeProvider`; call `BeginFrame()` first each frame, `SampleFrame()` after input dispatch; read `HasActiveAnimations` (and optionally `SampleFrame`'s bool, with the §3.9 caveat) for the timed-frames-vs-idle decision; call `Shutdown()` at session teardown before terminal-mode restore (one final frame after it renders base values).

**PROVIDES:**

- To S6: `AnimationScheduler` (`BeginFrame`/`SampleFrame`/`HasActiveAnimations`/`AnimationsEnabled`/`Shutdown`), `FrameClock`.
- To S3: `IStyleEdgeAction`, `BeginStoryboard`, `StopStoryboard`, `HandoffBehavior`, `AnimationDiagnostics`.
- To S1/control authors/apps: `BeginAnimation`/`StopAnimation` extensions, `AnimationHandle`, `Storyboard` + tracks, `Transition` family, `Interpolator.For<T>`/`Register<T>`, `Optional<T>`, `RepeatBehavior`.
- To everyone via `Cursorial.Animation` (additive): `DelayAnimation<T>`, `SequenceAnimation<T>`, `.Delay`/`.Then`, `Easings.TryParse`, later Elastic/Bounce/CubicBezier.

---

## 5. Requirement mapping & invariant compliance

- **R10 (rich animation) — primary.** Full WPF/Avalonia-shaped surface: imperative `BeginAnimation`, declarative `Storyboard` with targeting/stagger/repeat/autoreverse/fill, implicit `Transitions`, easing/keyframe/interpolator reuse from `Cursorial.Animation` + `Cursorial.Drawing` (zero duplication — UI adds only UI-type interpolators), pause/resume/seek (incl. storyboard-timeline seek), completion events, handoff, reduced-motion switch, deterministic teardown.
- **R8 (setters + hybrid trigger/selector model) — the animation half.** `EventTrigger`-class behavior is delivered as the edge actions the styling fork explicitly ceded; activation/retraction edges igniting `BeginStoryboard`/`StopStoryboard` with `SnapshotAndReplace` is the named Fork B contract, implemented here — with a no-throw runtime error policy so theme typos degrade to diagnostics, not crashes.
- **R1 (styling/templating) — supporting.** Storyboards and `TransitionCollection`s are resource-dictionary citizens settable by styles (`Transition.TransitionsProperty` is attached, hence style-settable); capability classes (`caps-ansi16`, Fork B) let themes swap cheap/expensive storyboards.
- **R7 (XAML) — supporting.** All declarative types are parameterless-ctor + settable-property shaped; converters enumerated in §4.
- **R5 (modal/modeless windows) — supporting pattern.** The window manager's `obscured` class (Fork B) + a `DoubleTransition` on `Opacity` gives dim-fade for free at composite cost.

**Invariants:** (1) frame coherence — frozen `FrameClock` per frame, synchronous first write at Begin, single sample pass, completion callbacks after sampling; no priority tiers. (2) Never touches Scene/CellBuffer — all effects via store writes + `PropertyEffects` routing. (3) Re-composite vs re-raster — §3.8 table; composite-shaped targets only for motion/fade/clip; DEBUG diagnostics for perpetual-on-`AffectsMeasure`. (4) Retraction is store-owned — `Stop`/`FillBehavior.Stop`/transition completion/detach-stop/`Shutdown` are all *handle disposal*; this subsystem never writes a "restored" value. (5) Template barrier — storyboard name resolution delegates to the template-aware namescope; no selector matching happens here. (6) Single UI thread — scheduler is thread-ambient with DEBUG `VerifyAccess`; `TimeProvider`-backed for testability (consistent with the input layer's `TimeProvider` use); the interpolator registry's threading stance is pinned (§2.6). (7) Lower layers additive-only — every `Cursorial.Animation`/`Cursorial.Drawing` change in this spec is a new type or new static member.

---

## 6. Terminal-specific design (deviations from WPF/Avalonia)

1. **No composition thread, no 60 Hz vsync clock.** WPF runs animations on a render thread; here one thread runs everything and S6 *chooses* frame cadence (20–60 fps). The scheduler is therefore pull-shaped (`SampleFrame`) instead of timer-driven, and `HasActiveAnimations` exists so a terminal app can drop to fully event-driven idle — terminals are frequently idle and `Composite == false` ⇒ zero bytes on the wire (drawing-core map). The detach stop pass (§3.10) is part of this deliverable: a leaked perpetual animation would otherwise pin the idle gate open forever.
2. **Wall-clock monotonic, not frame-index time.** The demos use `FrameInterval * frame` (a slow terminal slows animation); the UI layer uses `TimeProvider.GetTimestamp` so slow terminals drop frames instead of stretching time (animation map's explicit recommendation), while `FakeTimeProvider` keeps tests deterministic.
3. **No transform animations.** There is no `RenderTransform`: `CompositeParameters` is integer cell translation + opacity + clip only ("a cell grid can't do sub-cell rotate/scale", drawing-core). Motion = `Int32` offsets; the cell-quantizing interpolators + the store's equality gate turn a smooth timeline into writes only at cell crossings — the equality-gated cadence is the terminal's natural animation frame limiter.
4. **Re-raster is the expensive lane and is opt-in by property choice.** WPF apps animate `Background` freely; here a brush pulse re-rasters a scene per changed frame (gradient sampling per cell), so the §3.8 table steers motion/fade to composite-shaped properties. This is invariant 3 turned into authoring guidance.
5. **Color-depth interaction.** On `ansi256`/`ansi16` terminals an animated truecolor pulse quantizes at emit (`StyleQuantizer`), so many timeline values map to identical wire output — cost remains in re-raster, not bytes. Themes should branch on capability classes to substitute opacity fades; with `OrderedDither` on, remember dither disables scroll detection (rendering map) — animated full-screen scrolls + dither don't mix.
6. **Fragment-bearing panels.** Sliding a scene with Sixel images re-anchors fragments and forces re-encode every cell crossing (design-doc §8 diff-churn caveat); guidance: animate image panels only on Kitty-class terminals or keep image anchors static.
7. **Reduced motion as a first-class switch.** `AnimationsEnabled = false` snaps finite animations to end values through the normal completion path and lets perpetual ones rest at base (§3.11) — meaningful over SSH/slow links and for accessibility; cheap because snapping is just the normal completion branch at elapsed = ∞ (with the perpetual guard), and base-as-rest means nothing is pinned at Animation priority.
8. **`Rect` is ushort/non-negative** — `Size`/`Rect` tracks default to the clamping interpolators (round-and-clamp) so overshooting easings (`BackOut`) can never detonate a `Rect` ctor (map gotcha, honored end-to-end). `Thickness` is the deliberate exception: it is signed (Open Q1's rationale), so its interpolator rounds but does not clamp.

---

## 7. Phasing (repo §11 convention: numbered phases, deferrals recorded with reasons)

- **A0 — spine:** `FrameClock`, `AnimationScheduler` (`BeginFrame`/`SampleFrame`/`HasActiveAnimations`/`Shutdown`), `AnimationInstance<T>` (incl. idempotent `Retract`, `StateBeforePause`, `CompletedRaised`), `BeginAnimation`/`StopAnimation` + `AnimationHandle`, `FillBehavior`, completion events, perpetual/overflow guards, the §3.12 reentrancy contract, `Interpolator.For<T>` registry (threading stance pinned); additive `DelayAnimation`/`SequenceAnimation`/`.Delay`/`.Then`/`Easings.TryParse` in `Cursorial.Animation` (with the §2.7 boundary/checked-sum pins). Tests: FakeTimeProvider-driven, oracle-pinned completion/handoff/PingPong-end/reentrancy matrices **authored before the scheduler** (mirroring Fork A's precedence-matrix-first rule).
- **A1 — storyboards + lifetime:** `Storyboard`/`StoryboardHandle`/`AnimationTrack` family, `Optional<T>`, `RepeatBehavior`, namescope targeting, `(igniter, scope)` instancing, `IStyleEdgeAction` + `BeginStoryboard`/`StopStoryboard`, seal-on-begin + seal-time type validation + `AnimationDiagnostics` (the no-throw edge-ignition policy), **the production detach stop pass** (moved from A2 — it is behavior, not a diagnostic; the toast example depends on it). Demo: storyboard-driven toast/focus-pulse in `Cursorial.Demo` (repo demo-per-capability norm).
- **A2 — control surface:** Pause/Resume/Seek (handle + storyboard-timeline semantics per §3.4), `SkipToEnd` (incl. up-front storyboard validation), full `AnimationsEnabled` flip semantics (§3.11), Elastic/Bounce/`CubicBezier` easings + `cubic-bezier(…)` in `TryParse` (additive), DEBUG never-attached leak tracker + perpetual-on-`AffectsMeasure` diagnostic.
- **A3 — Transitions:** gated on the S2 winning-base-observer seam (§4 item 5 — medium-sized: winning-base detection under animation + inherited-change routing; budget it here, not as a "small" line item); `Transition` family, `TransitionService`, attach/detach arming, collection seal-on-arm, the §3.7 From-algorithm oracle tests.
- **Deferred (recorded):** `SpeedRatio` (multiplies into pause/seek bookkeeping; add when a consumer exists); `HandoffBehavior.Compose` + additive/`By` animations (needs value composition in the Animation slot — store change; re-addable); wake-at-time scheduling for `BeginTime` idle (frames during delay are cheap; optimize only if idle-power profiling demands); keyframe binary search (map notes O(n) scan is fine at typical counts); weak-target instances (the production detach pass + `IDisposable` discipline covers it; DEBUG tracker for the never-attached residue); `Parallel` as `IAnimation<T>` (no single-value semantics without additive composition).

---

## 8. Open questions (max 3)

1. **`Thickness` vs reusing Rendering's `Margins` (to S1).** If S1 mints `Thickness`, `ThicknessInterpolator`/`ThicknessTrack`/`ThicknessTransition` live in `Cursorial.UI` (dependency direction preserved). If S1 reuses `Margins`, the interpolator should instead land additively in `Cursorial.Drawing` beside `Size`/`Rect` interpolators — but note it would then need their unsigned clamp, surrendering negative-margin animation. **Recommendation:** S1 mints `Thickness` (WPF kinship + signed-math freedom the ushort-backed types lack — which is also why its interpolator does not clamp, §2.6); we ship it in UI.
2. **`Opacity` property type: `double` 0–1 vs `byte`.** **Recommendation: `double` with coerce-clamp** — WPF/Avalonia-familiar, easing-friendly; quantization to the `CompositeParameters` byte happens in S1's composite mapping, where record equality on identical bytes makes the compositor the final no-op gate. The store sees per-frame double changes, but the notification is one typed callback — negligible (and §3.9 documents the consequence for `SampleFrame`'s bool).
3. **Scheduler discovery: thread-ambient `AnimationScheduler.Current` vs per-`Window` service.** **Recommendation: thread-ambient** — invariant 6 guarantees one UI thread; one scheduler then serves all windows (modal children, req 5, share one clock and one idle signal), works for not-yet-attached elements, and avoids tree-plumbing every Begin. Revisit only if multi-session-per-process (multiple terminals, one process) becomes a goal — the interpolator registry's pinned threading stance (§2.6) keeps that door open.

---

## 9. Critique disposition

| # | Sev | Disposition |
|---|---|---|
| 1 | P0 | **ACCEPTED.** §3.7 From-algorithm rewritten: `From = isAnimated ? GetValue : oldEffectiveBase` (Fork A mutates effective before notification in the non-animated case, so `GetValue`-as-From self-cancels); seam 5 now delivers `(oldEffectiveBase, newEffectiveBase, isAnimated)`; the "hover flip with no prior animation" oracle test is pinned for A3. |
| 2 | P0 | **ACCEPTED.** §3.5 pins one snapshot rule (factory at track start only; synchronous-at-Begin for immediate starts) and splits handle fate: immediate replacement attaches-new-then-defensively-retracts-old (no value perturbation); delayed replacement retracts the old handle at Begin (base shows during delay, matching "Delayed = untouched") — no zombie window, no §3.3/§3.5 divergence. Added §3.5(4): retraction promotion is not a base change, so delayed handoff can't spuriously ignite a transition. |
| 3 | P0 | **ACCEPTED.** §3.10: production detach stop pass (retract + evict, no Completed) on S1 detach notification, moved to **A1**; reattach-doesn't-restore documented; idle-gate correctness called out in §6.1; toast example annotated; DEBUG tracker narrowed to never-attached targets and demoted to supplement. |
| 4 | P1 | **ACCEPTED.** `StateBeforePause` captured at Pause and restored by Resume; the track-start branch keys on `_handle is null` (§3.3) — both fixes, belt and braces. |
| 5 | P1 | **ACCEPTED.** New §3.12 reentrancy contract: immediate state mutation + deferred list membership, `State` re-check after every store write, idempotent `Retract` (+ S2 idempotent `Dispose`, §4 item 4), index-over-live-Count scratch drain (same-frame delivery of callback-enqueued completions), `Completed` at-most-once. |
| 6 | P1 | **ACCEPTED.** Seam 5 fires on **winning-base** changes only; inherited changes explicitly routed via Fork A's second carrier; seam relabeled "NOT small" and budgeted in A3 (§4 item 5, §7). |
| 7 | P1 | **ACCEPTED.** §3.4: storyboard ops defined on the storyboard timeline (per-child `t − BeginTime`); backward seek past `BeginTime` ⇒ retract handle + return to Delayed with the built timeline retained (factory runs once per lifetime — deterministic re-entry); storyboard `SkipToEnd` validates all-finite up front; Holding + Seek re-enters Running without re-raising `Completed` (at-most-once pinned). |
| 8 | P1 | **ACCEPTED.** §3.6: imperative `Begin` throws; edge-ignited begins are no-throw — type errors surface at style-seal (which now seals referenced storyboards), runtime name failures route through `AnimationDiagnostics.TrackError` and skip the track. No-throw contract ownership pinned here; S3 isolation optional. |
| 9 | P1 | **ACCEPTED.** New §3.11: flip true→false applies at next `SampleFrame` to all live instances (finite snap-complete through the normal completion pass; perpetual retract); Begin-while-false writes synchronously but enqueues `Completed` for the completion pass (ordering preserved); false→true is prospective; transitions/storyboards covered. |
| 10 | P1 | **ACCEPTED.** `ThicknessInterpolator` is per-side linear, rounded ties-away-from-zero, **no clamp** (§2.6, §3.8, §6.8, Open Q1) — clamping stays with the unsigned `Size`/`Rect` family. |
| 11 | P2 | **ACCEPTED.** `SkipToEnd` on Delayed pinned (§2.2): attach, snapshot, write end value, complete. |
| 12 | P2 | **ACCEPTED.** `TransitionCollection` seals on arm (styles precedent); mutation after arm throws; replacing the collection property re-arms (§2.5, §3.7). |
| 13 | P2 | **ACCEPTED.** §3.9 documents `SampleFrame`'s bool as "stored value changed", with the CompositeParameters byte equality and `Composite(...) == false` named as the authoritative no-op gates. |
| 14 | P2 | **ACCEPTED.** Registry keyed `(igniter, scope)` — per-`BeginStoryboard`-action for edge ignitions, per-`Storyboard` for imperative; `StopStoryboard` stops all instances of the referenced storyboard on scope across igniters (§3.1, §3.6). |
| 15 | P2 | **ACCEPTED.** `Easings.TryParse` accepts `cubic-bezier(x1,y1,x2,y2)` (A2, alongside the factory); S7's converter delegates (§2.7, §4). |
| 16 | P2 | **ACCEPTED.** Pinned: track `Repeat`/`AutoReverse` wrap `Source` uniformly (§2.3); `SequenceAnimation` half-open intervals — next child wins at boundaries, non-final zero-duration children never sampled, clamp-to-last at/after total Duration; `DelayAnimation` finite sum is `checked` (§2.7). |
| 17 | P2 | **ACCEPTED.** §3.1 allocation accounting corrected to "bounded handful at Begin; per-frame steady state zero", with the explicit note that edge-ignition cost under any-event motion is bounded by enter/leave edges, not per-Move events. |
| 18 | P2 | **ACCEPTED.** `Interpolator` registry threading pinned (§2.6): startup-time, UI-thread DEBUG-asserted registration; lock-free immutable-snapshot reads; rationale tied to Open Q3. |
| 19 | P2 | **ACCEPTED.** `AnimationScheduler.Shutdown()` added in A0 (§2.1, §3.10): retract-all (final frame renders base values), evict-all, no Completed, inert thereafter; S6 calls it before terminal restore. |
| 20 | P2 | **ACCEPTED (with a decision change).** Perpetual + `AnimationsEnabled == false` ⇒ **no handle, handle born Stopped, base shows** — the original "snap to ValueAt(Zero)" is dropped because it would pin the Animation slot forever (the exact entry shape findings 3/19 police) and freeze pulses at an arbitrary frame; base is the correct reduced-motion rendition (§3.11). |

No findings rebutted; finding 20's fix amends the original §2.1 comment rather than implementing the critique's literal alternative, for the grounded reason stated.