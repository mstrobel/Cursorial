---

# Fork A Judgment: Cursorial.UI Property System

## Summary

Three proposals were evaluated for the Cursorial.UI dependency-property system. All three are competent; none is obviously wrong. The real fault lines are not semantic (all three reproduce WPF/Avalonia observable behavior) but *mechanical*: how values are stored, whether the API boxes value types in the hot path, and how much implementation complexity lands in the property engine vs. the styling/binding forks that build on it. The right answer for a cell-grid TUI whose entire styling vocabulary is small `readonly record struct` value types is clearly the Hybrid. The Avalonia proposal is a stronger second than the WPF proposal advertises.

---

## Scoring Table

| Criterion | WPF | Avalonia | Hybrid |
|---|---|---|---|
| Common-case ergonomics (declare, style, bind) | 8 | 8 | 8 |
| Debuggability (binding errors, style not applying) | 8 | 7 | 7 |
| XAML readability / tooling integration | 9 | 8 | 8 |
| WPF/Avalonia veteran learning curve | 9 | 8 | 7 |
| Footgun density | 6 | 7 | 7 |
| Consistency with existing Cursorial conventions | 5 | 7 | 9 |
| Performance at 20-60 fps, value-typed props | 5 | 8 | 9 |
| Internal implementability / auditability | 6 | 6 | 9 |
| Animation / styling integration seam quality | 8 | 9 | 8 |
| Expressiveness for advanced widget authors | 8 | 8 | 8 |
| **Total** | **72** | **76** | **80** |

---

## Consumer Experience Assessment

A typical widget author registering a styled property and reacting to it:

```csharp
// WPF proposal
public static readonly DependencyProperty AccentProperty =
    DependencyProperty.Register(nameof(Accent), typeof(Color), typeof(Button),
        new FrameworkPropertyMetadata(Colors.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));
public Color Accent { get => GetValue<Color>(AccentProperty); set => SetValue(AccentProperty, value); }
// Setter body: SetValue(AccentProperty, Boxes.Of(value))  ← the box is hidden but present every frame

// Avalonia proposal
public static readonly StyledProperty<Color> AccentProperty =
    UIProperty.Register<Button, Color>(nameof(Accent), defaultValue: Colors.Cyan);
public Color Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
// CLR wrapper is zero-box; notification args are typed ref structs

// Hybrid proposal
public static readonly UIProperty<Color> AccentProperty =
    UIProperty.Register<Button, Color>(nameof(Accent), Colors.Cyan, PropertyEffects.Render);
public Color Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
// Same zero-box hot path; effects declared inline; no FrameworkPropertyMetadata ceremony
```

All three read and feel like the WPF/Avalonia authoring idiom. The Hybrid has the most compact registration line. WPF's extra metadata ceremony (wrapping in `FrameworkPropertyMetadata`, boxing the default, `Boxes.Of` in the setter wrapper) is the clearest differentiator in everyday use.

---

## Findings

### Critical

**[WPF] Boxing on every hot-path read and write of value-typed properties**

Location: `DependencyObject.GetValue<T>` / `SetValue`, the entire storage layer.

The WPF proposal boxes every `Color`, `Rect`, `Pen`, `CompositeParameters`, `Size`, and `Thickness` on every `GetValue` and `SetValue` call. The proposal attempts to mitigate this with `Boxes.Of`, `GetValue<T>` sugar, and equality-gating, but the mitigation is partial. The equality gate requires computing `object.Equals(oldResolved, newResolved)`, which itself requires both values to be boxed. The `Boxes` cache helps for small integers and booleans but not for the actual hot types in this system.

The codebase's own constraint from the design doc: "allocation discipline matters (per-frame allocs add up at 50 fps)." At 50 fps, animating 20 properties with value-type values produces at minimum 1,000 boxes per second on the current frame, plus a parallel 1,000 boxes for the equality comparison on the prior value. With `Color` (8 bytes managed object header + 8 bytes data = ~24 bytes on x64 managed heap), this is ~48 KB/sec of gen0 garbage from the property system alone — not counting the draw path. The proposal's own math of "~24–32 KB/sec" is the best case with perfect equality gating; the equality gate requires boxing first.

Recommendation: This is not fixable within the WPF design. The entire storage model must change to typed storage (as both Avalonia and Hybrid do) to eliminate boxing on the hot path.

**[WPF] `TryGetNonLocalBaseValue` seam is a correctness footgun at scale**

Location: `DependencyObject.TryGetNonLocalBaseValue`, §5.2.

The WPF proposal hands sub-bucket ordering entirely to the styling fork via the `TryGetNonLocalBaseValue` seam. The property engine enforces only that the styling-fork values fall somewhere between `Inherited` and `Local`. A styling-fork bug that inverts the relative order of `StyleTrigger` vs `TemplateTrigger` is invisible to the engine and will manifest as "my trigger doesn't apply correctly" — the hardest class of bug to diagnose in a property system. WPF's own bugs in this area are legendary.

The Avalonia and Hybrid approaches push sub-bucket arbitration down to the store as a verifiable contract (frame priority, packed key), which turns "style not applying" from a behavioral mystery into a testable invariant.

Recommendation: If WPF is chosen, the seam contract must ship with a conformance test suite pinned against concrete input/output scenarios. The proposal acknowledges this in §7 but frames it as the styling fork's problem; it is the engine's problem when the engine trusts the fork.

---

### Major

**[WPF] `FrameworkPropertyMetadata` construction ceremony vs. `PropertyEffects` flags**

Location: §2.2.

```csharp
// WPF
new FrameworkPropertyMetadata(
    Colors.Cyan,
    FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits,
    propertyChangedCallback: ...)

// Hybrid
UIProperty.Register<Button, Color>(nameof(Accent), Colors.Cyan,
    affects: PropertyEffects.Render, inherits: true, changed: ...)
```

The `FrameworkPropertyMetadata` object allocation exists primarily as an indirection layer that the Hybrid design eliminates. For a codebase that already uses `init`-property records and named-parameter constructors throughout, the WPF ceremony pattern is a clear regression in ergonomics. This is a minor issue for one property; it accumulates across hundreds of properties in a real app.

**[Avalonia] `ValueStore` effective/base split complexity**

Location: §3.2, `EffectiveValue<T>`.

The Avalonia proposal's `EffectiveValue<T>` holds four `T` slots: `Value`, `Previous`, `LocalValue`, `AnimatedValue`. For `Style` (a struct with ~6 fields), this is a substantial per-entry cost. The proposal acknowledges this: "four `T` slots per entry inflate for fat structs" — and defers the fix to "a side allocation for fat `T`." That deferral means the initial implementation will have an under-specified memory profile for the most common styled property type.

More importantly, the distinction between `EffectiveValue.EffectivePriority` and `EffectiveValue.BasePriority` requires careful reasoning at every write path. The Hybrid's coercion-as-slot-15 approach achieves the same observability with a single integer sort key; this is a strictly simpler invariant.

**[Avalonia] `GetBaseValue<T>` and `BeginAnimation<T>` are not orthogonal to `SetValue`**

Location: §2.3, `UIObject.BeginAnimation<T>`.

The Avalonia proposal introduces `AnimatedValueHandle<T>` as a first-class public API on `UIObject`. This is a reasonable design, but it means the public surface of the base object class understands animation as a concept — which is a violation of the mechanism/orchestration split that the design doc calls out explicitly as a governing principle: "Orchestration (clock, render loop, invalidation, triggers/storyboards, element lifecycle) lives in the future Cursorial.UI." The animation layer is the mechanism; `UIObject.BeginAnimation` is orchestration touching the base.

The WPF proposal's `SetAnimatedValue` / `ClearAnimatedValue` has the same problem in a less egregious form (it's named for the effect, not for animation lifecycle management). The Hybrid's `SetValue(prop, value, ValuePriority.Animation)` / `ClearValue(prop, ValuePriority.Animation)` is the cleanest: the storyboard fork just writes at a known priority, no special animation API on the base object required.

**[Hybrid] `SetValue(prop, value, priority, owner: IValueEvictionListener?)` overload exposes engine internals on the public API**

Location: §2.4.

```csharp
public void SetValue<T>(UIProperty<T> property, T value,
                        ValuePriority priority,
                        IValueEvictionListener? owner = null);  // ← engine-internal seam on the public API
```

The `IValueEvictionListener` and `owner` parameter are a binding-fork/styling-fork implementation detail. Making them a parameter of the primary `SetValue` overload means every XAML attribute write and every `button.Accent = Colors.Red` call has a nullable parameter that means nothing to app code. This leaks the engine's cell/eviction model to consumers who have no interest in it.

Recommendation: Move the engine-seam overload to an explicit internal or explicitly-named seam method (e.g., `SetValueFromSource<T>(prop, value, priority, IValueEvictionListener source)` on an internal interface that the binding and styling forks implement, not on the public `UIObject`). The Avalonia proposal's `BindingEntry<T>` push-handle pattern is the right shape: give the binding fork a typed handle that encapsulates the eviction contract, not a raw parameter on the base class method.

**[All three] Access-key property name choice**

All three proposals name the access-key indicator property `ShowAccessKeys`, `ShowAccessKeysProperty`, or `UseAccessKeyIndicators`. The behavior the requirement describes is: "underscore indicators toggle with Alt, or are permanently visible." The property name should reflect the rendering state, not the user action. `AccessKeyIndicatorsVisible` (or the Hybrid's `UseAccessKeyIndicators`) is clearer. A minor but concrete naming issue worth fixing early because this is an inherited ambient property that will appear in selector sheets and XAML attributes.

**[WPF] Static `DependencyProperty.FromName` for XAML with attached property resolution**

Location: §2.1.

The proposal's XAML name resolution walks `ownerType` chain and consults attached registrations. This is correct for `{Binding}` path segments but the mechanics of "also consult attached properties registered against any type" are not specified precisely. When the XAML parser encounters `Grid.Row="0"`, it splits on `.`, constructs owner type `Grid`, name `Row` — but what if two types in scope both have an attached property named `Row`? The WPF runtime resolves this by type-qualified lookup, but the proposal's `FromName(string name, Type ownerType)` signature takes only one type. This is a real implementation gap; the Hybrid's `UIProperty.Find(Type type, string name)` with the same lookup mechanics has the same gap, but names it explicitly as requiring `RuntimeHelpers.RunClassConstructor` forcing.

---

### Minor

**[WPF] `DependencyObjectType` as public API is unnecessary ceremony**

Location: §2.3.

`DependencyObjectType` exists in WPF to speed up `is`/`IsSubclassOf` checks without reflection in 100k-element scenarios. At terminal scale (hundreds of elements) it is measurable noise. Making it public API adds to the surface consumers must learn without giving them observable benefit. The Hybrid's `MetadataTable.For(Type)` is internal; the public API just takes `Type` at registration. This is a minor ergonomic issue but it illustrates how the WPF design carries machinery sized for a different scale.

**[Avalonia] `BindingPriority.Animation = -1` negative integer is odd**

Location: §2.2.

Using a negative enum value for the highest priority inverts the natural mental model (higher integer = higher priority, as in CSS specificity, z-index, etc.). The Hybrid uses `ValuePriority : byte` with `Animation = 6` as the highest non-internal value, which reads correctly. This is a naming/mental-model issue but will cause confusion whenever someone checks `priority > BindingPriority.LocalValue`.

**[Hybrid] `PropertyEffects.Render` vs. `AffectsComposite` gap**

Location: §2.1.

The design doc explicitly calls out the re-composite vs. re-raster split as terminal-specific: "offset/opacity/clip animations must not force re-raster." The WPF proposal creates `AffectsComposite` for this. The Hybrid's `PropertyEffects` enum has only `Render`, `Arrange`, `Measure`, `ParentArrange`, `ParentMeasure` — no `Composite`. This means animating `CompositeParameters.Offset` (the slide-a-scene path) would trigger `PropertyEffects.Render`, forcing a full scene re-raster when all that's needed is a compositor pass with new offset. The Hybrid must add `PropertyEffects.Composite` before the animation storyboard fork can use it correctly.

**[All three] Coercion reentrancy is underspecified**

All three proposals describe a reentrancy guard (depth limit, equality cycle-break) but none specifies what happens to the queued set when the guard fires. Does the second set fail silently? Throw? Queue? WPF throws in release builds for coercion cycles. The correct answer for Cursorial's single-threaded model is almost certainly "throw `InvalidOperationException` with a clear diagnostic," and this should be specified explicitly — it is the difference between "mysterious property not updating" and "visible crash during development."

---

## Strengths

**WPF:** The full `BaseValueSource` enum is the best diagnostic artifact of the three proposals. `GetValueSource` returning a 10-bucket enum lets a debugger/DevTools show exactly why a property has its value (inherited from parent, overridden by trigger, set by template, etc.) in a way that `BindingPriority` with five levels cannot. WPF's `SetCurrentValue` semantics are precisely specified and the proposal explains the mechanics clearly.

**Avalonia:** `BindingEntry<T>` is the best public API for the binding-fork seam of the three proposals. It gives the binding engine a typed handle that encapsulates priority, eviction, and the unset path in a single disposable object, without leaking these concepts onto `UIObject`'s public surface. The `AnimatedValueHandle<T>` is the same insight applied to animation. Both are cleaner seams than the WPF expression-in-slot model or the Hybrid's `IValueEvictionListener` parameter on `SetValue`.

**Hybrid:** The packed `(GlobalIndex << 4) | priority` uint key is the right data structure for this scale. It is auditable in an afternoon, can be searched with a single integer comparison, and keeps the entire store's semantics as a testable invariant: "the effective value is the entry at the highest key for this property index." The coercion-as-slot-15 trick is elegant — it falls naturally out of the sort order and makes `GetValue(maxPriority)` work by construction. The `PropertyEffects` flag inline at registration (no separate `FrameworkPropertyMetadata` object) is the most ergonomic of the three for the common case.

---

## Ranked Verdict

1. **Hybrid** — Best overall fit for Cursorial's constraints. Zero-boxing hot path, auditable flat storage, minimal ceremony at declaration time, animation handled via priority writes with no special API on the base class, effects declared inline. The missing `PropertyEffects.Composite` and the `IValueEvictionListener` exposure on `SetValue` are fixable before the first commit.

2. **Avalonia** — Strong proposal with the best seam APIs (`BindingEntry<T>`, `AnimatedValueHandle<T>`). Held back by the four-slot `EffectiveValue<T>` complexity, the effective/base split subtlety, and the `BeginAnimation<T>` orchestration-in-mechanism leak. For a team that has already shipped an Avalonia-based property system, this is the right choice. For a greenfield system it carries more implementation risk than the Hybrid.

3. **WPF** — Technically correct semantics, familiar to veterans, but structurally misaligned with the codebase's value-type conventions. Boxing is not a secondary concern for a 50 fps terminal renderer; it is load-bearing architecture. The `TryGetNonLocalBaseValue` seam design transfers a class of property-system correctness bugs to the styling fork with limited enforcement. At this scale the WPF-specific complexity (the extra priority rungs, `DependencyObjectType` as public API, `DependencyPropertyDescriptor`-free AddValueChanged as a new addition) is justified only if the team already knows WPF internals.

---

## RECOMMENDATION: Hybrid wins, with two surgical grafts from the other proposals

**The storage design and priority model of the Hybrid should be adopted as written**, with the following changes before implementation begins:

**Graft 1 — from WPF:** Add a `ValueSource` diagnostic enum with 7–9 named levels (Default, Inherited, Style, Template, Trigger, Local, Animation, Coerced) and a `GetValueSource(UIProperty)` method returning it. This is the single highest-value diagnostic feature in the WPF proposal and costs nothing to add to the Hybrid's storage model (the `Key` field already encodes priority; map it). Remove or demote WPF's 11-level full ladder; 7 is sufficient.

**Graft 2 — from Avalonia:** Replace `IValueEvictionListener` as a parameter on `SetValue` with a typed `UIBindingEntry<T>` push-handle (Avalonia's `BindingEntry<T>`) and a corresponding `UIAnimationHandle<T>`. The binding and storyboard forks interact with the store through these handles, not through overloaded `SetValue` parameters. The handles encapsulate eviction notification and priority ownership in a disposable object. The public `UIObject` API remains: `GetValue<T>`, `SetValue<T>`, `ClearValue`, `SetCurrentValue<T>`, `Watch<T>`, `GetValueSource` — nothing engine-internal.

**Two fixes to make before implementation:**

1. Add `PropertyEffects.Composite` to `PropertyEffects` (the re-composite vs. re-raster distinction is a governing constraint from the design doc; its absence from the Hybrid is a gap, not a choice).

2. Move `SetValue(prop, value, ValuePriority priority)` (the priority-explicit overload) to an internal seam interface rather than public `UIObject`. App code should only ever call `SetValue<T>(prop, value)` (Local) and `SetCurrentValue<T>(prop, value)` (two-way binding target write). The rest is engine plumbing.

---

## Graft List from Losing Proposals

**From WPF (steal these):**

- `GetValueSource(UIProperty) → ValueSource` diagnostic method with a named enum (vs. returning `ValuePriority` raw).
- The `AddOwner<TOwner>` multi-owner aliasing pattern on `StyledProperty<T>` (the Hybrid has `UIProperty<T>.AddOwner`; make sure the XAML lookup also walks `AddOwner` chains).
- Notification ordering: "class callbacks → virtual override → instance watchers" as an explicit documented contract (the Hybrid specifies this but WPF's version is more precisely described; adopt the precise ordering).
- The `Boxes` cache by name — the Hybrid uses it implicitly; name it explicitly in the implementation so the boxing discipline is visible.

**From Avalonia (steal these):**

- `BindingEntry<T>` typed push-handle as the binding fork's store-side API.
- `AnimatedValueHandle<T>` as the storyboard fork's store-side API (replaces the Hybrid's `SetValue(ValuePriority.Animation)` + `ClearValue(ValuePriority.Animation)` direct calls — encapsulating them in a handle gives the storyboard fork a cleaner evict-on-dispose contract).
- `DeferNotifications()` scope for theme swaps and template application (the Hybrid mentions "deferred notification" in the risk list but doesn't specify a public API for it; Avalonia's scoped `IDisposable` batch is the right shape).
- `DirectProperty<TOwner, T>` for the hot-scroll-offset / IsPressed / Bounds lane (plain field, getter/setter delegates, no store). The Hybrid has no analog. This is the right safety valve for properties that are internal state, never styled, animated 60 fps, and account for the bulk of per-frame reads in a real widget tree.