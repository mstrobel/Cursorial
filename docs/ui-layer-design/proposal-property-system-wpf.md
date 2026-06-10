# Cursorial.UI Property System — Fork A Proposal: The WPF-Faithful Design

**Author:** Fork A (property system, requirement 9)
**Namespace:** `Cursorial.UI` (single flat namespace, per module convention)
**Dependencies:** `Cursorial.Core` only (the property engine itself needs nothing from Drawing/Rendering; the *widget* layer above it maps metadata flags onto Scene/layout invalidation)

---

## 1. Executive summary & philosophy

This design ports the WPF `DependencyProperty` architecture — static object-keyed property identity, `GetValue`/`SetValue` over boxed values, a packed per-instance `EffectiveValueEntry[]` sorted by global index, per-type `PropertyMetadata` with `OverrideMetadata`, WPF's base-value-source precedence with **animation layered above local**, and coercion as a transform on top of whatever source wins.

The philosophy: **the WPF property engine is a 20-year-proven design whose complexity lives in exactly two places — the precedence pipeline and the effective-value cache — and both are *small* at terminal scale.** Everything that made WPF's implementation hairy (Freezable sub-property invalidation, cross-thread Dispatcher verification, DeferredReference for theme dictionaries, tens of thousands of elements) is structurally absent from Cursorial: brushes are immutable by `IBrush` contract, there is one render thread by stack-wide contract, and apps have hundreds of elements. We keep WPF's *observable semantics* — which app authors have muscle memory for and which requirement 8 ("Setters + Triggers like WPF *or* selectors like Avalonia") explicitly anticipates — and shed its incidental complexity. Where the terminal changes the calculus (cell-grid invalidation is coarse and re-raster is the expensive path), we adapt deliberately: change-suppression by boxed equality is load-bearing, and a new `AffectsComposite` metadata flag distinguishes "re-composite the cached scene" from "re-raster it."

One honest cost up front: this design boxes value types at the storage boundary. I quantify why that is negligible at this scale (§7) and why the alternative's complexity is not (§8).

---

## 2. Public API sketch

### 2.1 Property identity & registration

```csharp
namespace Cursorial.UI;

public delegate void PropertyChangedCallback(DependencyObject d, in DependencyPropertyChangedEventArgs e);
public delegate object? CoerceValueCallback(DependencyObject d, object? baseValue);
public delegate bool ValidateValueCallback(object? value);

public sealed class DependencyProperty
{
    // Registration (static-ctor time; thread-safe; throws on duplicate (name, ownerType))
    public static DependencyProperty Register(
        string name, Type propertyType, Type ownerType,
        PropertyMetadata? typeMetadata = null,
        ValidateValueCallback? validateValueCallback = null);

    public static DependencyProperty RegisterAttached(
        string name, Type propertyType, Type ownerType,
        PropertyMetadata? defaultMetadata = null,
        ValidateValueCallback? validateValueCallback = null);

    public static DependencyPropertyKey RegisterReadOnly(
        string name, Type propertyType, Type ownerType,
        PropertyMetadata typeMetadata,
        ValidateValueCallback? validateValueCallback = null);

    public static DependencyPropertyKey RegisterAttachedReadOnly(
        string name, Type propertyType, Type ownerType,
        PropertyMetadata defaultMetadata,
        ValidateValueCallback? validateValueCallback = null);

    // Metadata
    public void OverrideMetadata(Type forType, PropertyMetadata typeMetadata);
    public PropertyMetadata GetMetadata(Type forType);
    public PropertyMetadata GetMetadata(DependencyObjectType forType);
    public PropertyMetadata DefaultMetadata { get; }

    // Multi-owner aliasing (WPF AddOwner — TextElement.Foreground reused by Control)
    public DependencyProperty AddOwner(Type ownerType, PropertyMetadata? typeMetadata = null);

    public string Name { get; }
    public Type PropertyType { get; }
    public Type OwnerType { get; }
    public bool ReadOnly { get; }
    public ValidateValueCallback? ValidateValueCallback { get; }
    public bool IsValidValue(object? value);          // type check + validate callback

    internal ushort GlobalIndex { get; }              // registry-assigned, the storage key

    // XAML plumbing (requirement 7): name → property resolution over the owner-type chain,
    // including attached properties registered against any type.
    public static DependencyProperty? FromName(string name, Type ownerType);

    // The unset sentinel
    public static readonly object UnsetValue;
}

/// Grants write access to a read-only property (IsFocused etc.). Held privately by the declaring class.
public sealed class DependencyPropertyKey
{
    public DependencyProperty DependencyProperty { get; }
    public void OverrideMetadata(Type forType, PropertyMetadata typeMetadata);
}
```

### 2.2 Metadata

```csharp
public class PropertyMetadata
{
    public PropertyMetadata();
    public PropertyMetadata(object? defaultValue);
    public PropertyMetadata(object? defaultValue,
                            PropertyChangedCallback? propertyChangedCallback = null,
                            CoerceValueCallback? coerceValueCallback = null);

    public object? DefaultValue { get; init; }                       // must be immutable or a value type
    public PropertyChangedCallback? PropertyChangedCallback { get; init; }
    public CoerceValueCallback? CoerceValueCallback { get; init; }

    // OverrideMetadata merge: derived default replaces; changed callbacks CHAIN (base fires first,
    // exactly WPF); coercion REPLACES (most-derived wins).
    protected virtual void Merge(PropertyMetadata baseMetadata, DependencyProperty dp);
}

[Flags]
public enum FrameworkPropertyMetadataOptions
{
    None                 = 0,
    AffectsMeasure       = 1 << 0,   // → InvalidateMeasure on the owning element
    AffectsArrange       = 1 << 1,   // → InvalidateArrange
    AffectsRender        = 1 << 2,   // → Scene.Invalidate() — RE-RASTER. The expensive one.
    AffectsComposite     = 1 << 3,   // → CompositeParameters refresh only — cached raster reused.
                                     //    TERMINAL-SPECIFIC (see §6): offset/opacity/clip animations
                                     //    must not force re-raster.
    AffectsParentMeasure = 1 << 4,   // attached layout props: Grid.Row, Dock.Side …
    AffectsParentArrange = 1 << 5,
    Inherits             = 1 << 6,   // value inherits down the element tree
    NotDataBindable      = 1 << 7,
    BindsTwoWayByDefault = 1 << 8,   // consumed by the binding fork
    Journal              = 0,        // reserved; not used in v1
}

public class FrameworkPropertyMetadata : PropertyMetadata
{
    public FrameworkPropertyMetadata(object? defaultValue,
                                     FrameworkPropertyMetadataOptions options = default,
                                     PropertyChangedCallback? propertyChangedCallback = null,
                                     CoerceValueCallback? coerceValueCallback = null);
    public FrameworkPropertyMetadataOptions Options { get; init; }
    public bool Inherits        => (Options & FrameworkPropertyMetadataOptions.Inherits) != 0;
    public bool AffectsRender   => (Options & FrameworkPropertyMetadataOptions.AffectsRender) != 0;
    // … accessor per flag
}
```

### 2.3 DependencyObject

```csharp
public readonly record struct DependencyPropertyChangedEventArgs(
    DependencyProperty Property, object? OldValue, object? NewValue, PropertyMetadata Metadata);

public delegate void DependencyPropertyChangedHandler(DependencyObject sender, in DependencyPropertyChangedEventArgs e);

public readonly record struct ValueSource(
    BaseValueSource BaseValueSource, bool IsExpression, bool IsAnimated, bool IsCoerced, bool IsCurrent);

public enum BaseValueSource : byte
{
    Unknown        = 0,
    Default        = 1,   // metadata default — never stored
    Inherited      = 2,   // from inheritance parent chain (cached lazily)
    ThemeStyle     = 3,   // ┐
    ThemeTrigger   = 4,   // │  these five buckets are REPORTED by the styling fork
    Style          = 5,   // │  through one seam (§5.2); the property engine enforces
    TemplateTrigger= 6,   // │  only their relative order
    StyleTrigger   = 7,   // │
    Template       = 8,   // │  values applied by template expansion (templated parent)
    TemplateParentTrigger = 9, // ┘
    Local          = 10,  // SetValue / binding expressions
    // Animation and Coercion are MODIFIERS above the base source, not sources (WPF-faithful).
}

public abstract class DependencyObject
{
    // ── Core read/write (the WPF surface) ───────────────────────────────────
    public object? GetValue(DependencyProperty dp);
    public void SetValue(DependencyProperty dp, object? value);          // throws on ReadOnly
    public void SetValue(DependencyPropertyKey key, object? value);
    public void SetCurrentValue(DependencyProperty dp, object? value);   // overwrite effective value
                                                                         // WITHOUT changing base source
                                                                         // (doesn't kill bindings/styles)
    public void ClearValue(DependencyProperty dp);                       // remove local; re-evaluate
    public void ClearValue(DependencyPropertyKey key);
    public void CoerceValue(DependencyProperty dp);                      // re-run coercion pipeline
    public void InvalidateProperty(DependencyProperty dp);               // full re-evaluation
                                                                         // (styling/trigger engines call this)
    public object? ReadLocalValue(DependencyProperty dp);                // local or UnsetValue
    public ValueSource GetValueSource(DependencyProperty dp);            // diagnostics / DevTools
    public LocalValueEnumerator GetLocalValueEnumerator();               // XAML serialization, templates

    // Typed sugar (zero-cost casts at call sites; storage is still boxed — see §3.6)
    public T GetValue<T>(DependencyProperty dp) => (T)GetValue(dp)!;

    // ── Targeted change notification (bindings, triggers, selector watchers) ─
    public IDisposable AddValueChanged(DependencyProperty dp, DependencyPropertyChangedHandler handler);

    // ── Animation slot (storyboard fork writes here; see §3.5, §5.4) ─────────
    public void SetAnimatedValue(DependencyProperty dp, object? value);
    public void ClearAnimatedValue(DependencyProperty dp);
    public bool HasAnimatedValue(DependencyProperty dp);

    // ── Engine seams (overridden by UIElement in the widget fork; see §5) ────
    protected internal virtual DependencyObject? InheritanceParent => null;
    protected internal virtual void EnumerateInheritanceChildren(
        DependencyProperty dp, Action<DependencyObject> visit) { }
    protected internal virtual bool TryGetNonLocalBaseValue(
        DependencyProperty dp, out object? value, out BaseValueSource source)
        { value = null; source = BaseValueSource.Unknown; return false; }

    // Class-handler hook (fires for EVERY effective-value change before instance listeners)
    protected virtual void OnPropertyChanged(in DependencyPropertyChangedEventArgs e) { }

    // Called by the widget fork when the element is (re)parented — re-resolves Inherits properties.
    protected internal void OnInheritanceParentChanged();

    public DependencyObjectType DependencyObjectType { get; }            // cached on first use

    [Conditional("DEBUG")] protected void VerifyAccess();                // single-render-thread assert
}

public sealed class DependencyObjectType
{
    public static DependencyObjectType FromSystemType(Type type);
    public int Id { get; }
    public Type SystemType { get; }
    public DependencyObjectType? BaseType { get; }
    public bool IsSubclassOf(DependencyObjectType other);
}
```

### 2.4 The expression seam (binding fork attaches here)

```csharp
/// WPF's Expression. A binding stored at Local priority that computes its value indirectly.
public abstract class PropertyValueExpression
{
    protected internal abstract object? GetValue(DependencyObject d, DependencyProperty dp);

    /// Two-way support: SetValue on a property whose local slot holds an expression first OFFERS
    /// the value here. Return true to consume (write to binding source; value flows back through
    /// GetValue). Return false to be detached and replaced by a plain local value. (WPF-faithful.)
    protected internal virtual bool TrySetValue(DependencyObject d, DependencyProperty dp, object? value) => false;

    protected internal virtual void OnAttach(DependencyObject d, DependencyProperty dp) { }
    protected internal virtual void OnDetach(DependencyObject d, DependencyProperty dp) { }

    /// The expression's source changed — ask the engine to re-pull GetValue and run the pipeline.
    protected void NotifySourceChanged(DependencyObject d, DependencyProperty dp)
        => d.InvalidateProperty(dp);
}
```

`SetValue(dp, someExpression)` stores the expression at Local with the `IsExpression` modifier — exactly how WPF stores `BindingExpression`. The property engine knows *nothing* about binding paths, `DataContext` resolution, or converters; it knows "a local value that computes itself and pings me."

### 2.5 Consumer usage example

```csharp
public class Button : ContentControl
{
    // Standard property with type-level metadata
    public static readonly DependencyProperty BackgroundProperty =
        DependencyProperty.Register(nameof(Background), typeof(IBrush), typeof(Button),
            new FrameworkPropertyMetadata(
                Brushes.Default,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public IBrush Background
    {
        get => GetValue<IBrush>(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    // Read-only property: only Button can write it (via the key); styles/triggers can READ it
    private static readonly DependencyPropertyKey IsPressedPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(IsPressed), typeof(bool), typeof(Button),
            new FrameworkPropertyMetadata(Boxes.False, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsPressedProperty = IsPressedPropertyKey.DependencyProperty;

    public bool IsPressed
    {
        get => GetValue<bool>(IsPressedProperty);
        private set => SetValue(IsPressedPropertyKey, Boxes.Of(value));
    }
}

// Attached property — the Grid.Row pattern (requirement: attached properties)
public partial class Grid : Panel
{
    public static readonly DependencyProperty RowProperty =
        DependencyProperty.RegisterAttached("Row", typeof(int), typeof(Grid),
            new FrameworkPropertyMetadata(Boxes.Zero, FrameworkPropertyMetadataOptions.AffectsParentArrange),
            validateValueCallback: static v => (int)v! >= 0);

    public static int GetRow(DependencyObject d) => d.GetValue<int>(RowProperty);
    public static void SetRow(DependencyObject d, int value) => d.SetValue(RowProperty, Boxes.Of(value));
}

// Inherited ambient property (requirement 6 — access-key indicator visibility)
public static class AccessKeyManager
{
    public static readonly DependencyProperty ShowAccessKeysProperty =
        DependencyProperty.RegisterAttached("ShowAccessKeys", typeof(bool), typeof(AccessKeyManager),
            new FrameworkPropertyMetadata(Boxes.False,
                FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    // Window sets it true on Alt-down (Kitty/Win32 paths) or permanently (legacy terminals);
    // every Label/MenuItem in the subtree re-renders its underscore — one SetValue, cascade does the rest.
}

// Coercion interplay — the classic Slider/ScrollBar trio
public class RangeBase : Control
{
    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(RangeBase),
            new FrameworkPropertyMetadata(100.0,
                propertyChangedCallback: static (d, in e) => d.CoerceValue(ValueProperty)));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(RangeBase),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender,
                coerceValueCallback: static (d, v) =>
                {
                    var r = (RangeBase)d;
                    return Math.Clamp((double)v!, r.Minimum, r.Maximum);
                }));
}

// Subtype metadata override — a dark-theme console button defaults to a palette brush
static Button() // static ctor of some derived FancyButton
{
    BackgroundProperty.OverrideMetadata(typeof(FancyButton),
        new FrameworkPropertyMetadata(Brushes.Blue, FrameworkPropertyMetadataOptions.AffectsRender));
}
```

And the animation interplay an app author sees (storyboard fork drives the clock; the property system just layers):

```csharp
// User set a local background…
button.Background = Brushes.Red;
// …a hover storyboard animates it (SetAnimatedValue under the hood each frame):
//    GetValue(BackgroundProperty) → the animated brush          (animation above local)
// …storyboard stops, ClearAnimatedValue:
//    GetValue(BackgroundProperty) → Brushes.Red                 (local restored, nothing recomputed
//                                                                from styles — base was retained)
```

---

## 3. Internal architecture

### 3.1 Registry

A static, lock-protected registry assigns each `DependencyProperty` a monotonically increasing `ushort GlobalIndex` at registration. A `ConcurrentDictionary<(Type, string), DependencyProperty>` backs `FromName` (the XAML loader's resolution path, which also walks base types and consults attached registrations). Registration happens in static constructors; the registry is effectively append-only and never cleaned (properties live for the process — WPF-identical, and fine: a big terminal app registers a few hundred properties, each ~100 bytes of registry weight).

`DependencyPropertyKey` wraps the property; `SetValue(DependencyProperty)` on a read-only property throws `InvalidOperationException`; `SetValue(DependencyPropertyKey)` bypasses. The key is a separate object precisely so visibility is enforced by C# accessibility (private static field), not by checks.

### 3.2 Per-type metadata

Each `DependencyProperty` holds:

- `_defaultMetadata` — from registration; its `DefaultValue` is **boxed once** and that box is returned forever (default reads never allocate).
- `_metadataMap` — a tiny insertion-sorted array of `(DependencyObjectType, PropertyMetadata)` pairs, populated only by `OverrideMetadata` calls (rare — most properties have zero overrides).
- A per-property single-entry MRU cache `(DependencyObjectType lastType, PropertyMetadata lastResolved)` — resolution for a concrete element type walks the `DependencyObjectType.BaseType` chain looking for the nearest override, then memoizes. In practice `GetMetadata` is a reference compare + return.

`OverrideMetadata(forType, md)` merges per WPF rules: default value replaces, `PropertyChangedCallback` **chains** (base-most first), `CoerceValueCallback` replaces, `FrameworkPropertyMetadataOptions` ORs except `Inherits` which must match the default metadata (changing inheritance behavior per-subtype is a WPF footgun we forbid outright — `InvalidOperationException`). Overriding after the property has been read on that type throws (sealing rule, WPF-identical), enforced by a `Sealed` bit set on first resolution.

`DependencyObjectType` is a cached, identity-comparable wrapper over `Type` with an `int Id` and `BaseType` link — built once per CLR type via `ConcurrentDictionary<Type, DependencyObjectType>`. It exists so metadata resolution is integer/reference work, not reflection.

### 3.3 Per-instance storage: the packed entry array

```csharp
[StructLayout(LayoutKind.Auto)]
internal struct EffectiveValueEntry           // 16 bytes on x64
{
    private object? _value;   // resolved boxed value | PropertyValueExpression | ModifiedValue
    private uint    _packed;  // [0..15]  property GlobalIndex
                              // [16..19] BaseValueSource (4 bits)
                              // [20] IsExpression  [21] IsAnimated  [22] IsCoerced
                              // [23] IsCurrent (SetCurrentValue overwrote the resolved value)
                              // [24] IsInheritedCache (entry is a lazily-cached inherited value)
}

internal sealed class ModifiedValue           // allocated ONLY when modifiers are present
{
    public object? BaseValue;        // the source-determined value (or PropertyValueExpression)
    public object? ExpressionValue;  // last evaluated expression result
    public object? AnimatedValue;    // last animated overlay
    public object? CoercedValue;     // post-coercion result
}
```

`DependencyObject` carries exactly three storage fields:

```csharp
private EffectiveValueEntry[]? _effectiveValues;  // sorted ascending by GlobalIndex
private ushort _effectiveCount;
private object? _listeners;   // null | ListenerEntry | ListenerEntry[]  (frugal map keyed by GlobalIndex)
```

- **Lookup** is binary search over `_effectiveCount` entries. A typical element holds 5–25 entries (locals + cached inherited + style-cached); that is ≤5 comparisons. Misses fall through to metadata default — the precomputed box.
- **Insert** is an `Array.Copy` shift; arrays grow ×2 from 4. At ≤32 entries this is a sub-microsecond memmove, and inserts happen at *mutation* rate, not read rate.
- **The common case is the empty case.** A property never touched on an instance costs **zero bytes** on that instance — the entire point of the sparse design. A `TextBlock` with 40 registered properties and 3 set ones stores 3 entries.
- The resolved (post-modifier) value is always denormalized into the entry (`_value` directly when no modifiers; `ModifiedValue.CoercedValue ?? AnimatedValue ?? ExpressionValue ?? BaseValue` resolution is precomputed at update time and cached so `GetValue` never re-resolves). **`GetValue` is: binary search → return reference.** No pipeline runs on read.

### 3.4 The value pipeline (runs on writes/invalidations, never on reads)

```
UpdateEffectiveValue(dp, metadata, reason):
  1. DETERMINE BASE
     a. local entry present (SetValue or expression)         → Local
     b. TryGetNonLocalBaseValue(dp, …)  [styling-fork seam]  → ThemeStyle … TemplateParentTrigger
     c. metadata.Inherits → resolve via InheritanceParent chain (use nearest cached entry; cache
        result locally with IsInheritedCache)                 → Inherited
     d. metadata default (shared box)                         → Default (NOT stored)
  2. EXPRESSION: if base is PropertyValueExpression → ExpressionValue = expr.GetValue(this, dp)
  3. ANIMATION: if IsAnimated → AnimatedValue overlays (animation above local — §3.5)
  4. COERCION: if metadata.CoerceValueCallback != null → CoercedValue = callback(this, valueSoFar);
     coerced result must satisfy ValidateValueCallback (else throw — WPF rule)
  5. EQUALITY GATE: object.Equals(oldResolved, newResolved)?  → stop. Nothing fires.
  6. COMMIT + NOTIFY (in order):
     a. write entry (denormalized resolved value)
     b. metadata.PropertyChangedCallback chain (class handlers)
     c. virtual OnPropertyChanged(args)            ← widget fork maps Affects* → invalidation here
     d. per-instance AddValueChanged listeners     ← bindings (two-way write-back), triggers, selectors
     e. if metadata.Inherits → inheritance cascade (§3.7)
```

The **equality gate** (step 5) compares boxed values via `Equals`. Every interesting type in this stack — `Color`, `Style`, `Rect`, `Size`, `Pen`, `CompositeParameters`, `Thickness` — is a `readonly record struct` with value equality, and brushes are immutable references. This gate is what makes per-frame animation writes cheap (§3.5) and what protects `Scene.Invalidate()` (re-raster, the expensive path) from no-op churn.

`SetValue` semantics worth pinning:

- `SetValue(dp, UnsetValue)` ≡ `ClearValue(dp)` (WPF rule).
- `SetValue` on a local slot holding an expression first offers `expr.TrySetValue` (two-way bindings stay alive and route the value to the source); refusal detaches the expression (one-way binding replaced by local — WPF rule).
- `SetCurrentValue` replaces the *resolved* value and sets `IsCurrent` without touching the base slot — the next invalidation from any source recomputes normally. This is the sanctioned way for controls to reflect user interaction (toggle state, scroll offset) without destroying a binding or style. It exists because "animation above local" plus "bindings are local" needs a non-destructive write path — see §8.
- `ClearValue` removes the local slot and re-runs the pipeline: the value falls back to trigger/style/inherited/default *and listeners observe the transition*. "Unset restores the lower layer, observably" is the acceptance test for the whole engine.

### 3.5 Animation layering (above local — and why)

`SetAnimatedValue(dp, box)` sets `IsAnimated`, stores the overlay in `ModifiedValue.AnimatedValue`, and runs steps 4–6 only (base is retained untouched). `ClearAnimatedValue` drops the overlay and re-resolves from the retained base — **no style/inheritance re-query needed**, because the base value was never discarded. That retention is exactly what `ModifiedValue` exists for.

**Justification for animation-above-local** (the design point I'm required to defend):

1. **Both reference frameworks agree.** WPF layers animation above local; Avalonia's `BindingPriority.Animation` is likewise its highest priority. There is no faithful-to-either design with animation below local — a fork proposing that is proposing novelty, not familiarity.
2. **The dominant use case demands it.** Trigger-driven storyboards ("flash the row background on update", "pulse focus ring") target properties whose base is a *binding or local value*. If local beat animation, every animated property would need its base relocated into a style — an authoring tax on the 90% case to serve the 10% case.
3. **Mechanics compose with Cursorial.Animation's clamp semantics.** `IAnimation<T>.ValueAt` holds its end value (HoldEnd for free, per animation.md). The storyboard fork decides hold-vs-release; releasing is `ClearAnimatedValue`, which restores the *retained* base. With animation-below-local, "release" semantics would require the storyboard to have snapshotted and restored user state — a correctness bug factory.
4. **The escape hatch exists and is precedented.** "User input should beat the animation" is handled the WPF way: the interaction *stops the storyboard* (orchestration owns lifecycle — exactly the mechanism/orchestration split the Drawing design doc assigns to Cursorial.UI), or writes via `SetCurrentValue` which the storyboard's next frame overlays (visually animation wins until stopped — the correct UX for e.g. a reveal transition).

**Per-frame cost:** one box per changed sample per animated property. A heavy screen animating 20 properties at 50 fps allocates ≤1,000 boxes/sec ≈ 24–32 KB/sec gen0 — beneath measurement noise next to the per-frame `ArrayBufferWriter` the demos already allocate. Cell-quantized animations (`Int32`, `Rect`, `Size` interpolators round to cells) produce *identical* boxed values across many frames; the equality gate then suppresses everything downstream including invalidation — an animated slide at 50 fps moving 10 cells/sec fires ~10 property changes/sec, not 50. For `bool`/small-`int`/`enum`-typed properties, a static `Boxes` cache (true/false, −1…255, common enum members) eliminates boxing entirely on the hottest discrete types.

### 3.6 Boxing discipline (stated plainly)

`GetValue`/`SetValue` traffic in `object?`. Mitigations, all WPF-precedented: shared default boxes in metadata; `Boxes` cache for discrete types; `record struct` `Equals` working correctly on boxes; typed `GetValue<T>` sugar so call sites stay clean (the unbox is a single `unbox.any`). What we do **not** do is pretend storage is unboxed — see §8 for why I consider Avalonia-style typed storage a bad trade here.

### 3.7 Property value inheritance

- `Inherits` metadata flag; the inheritance topology comes from the widget fork via `InheritanceParent` / `EnumerateInheritanceChildren` (§5.1). Making the parent a *virtual* is deliberate: popups and child windows (requirement 5) override it to inherit from their placement target / owner window across visual-tree boundaries, exactly like WPF's inheritance context.
- **Read path:** miss in the entry array + `Inherits` → walk `InheritanceParent` chain to the nearest node with an entry (or take the default), then **cache locally** with `IsInheritedCache`. Subsequent reads are O(log entries). At depth ~10–20 the first-read walk is trivial.
- **Change path:** when a node's effective value of an `Inherits` property changes, the engine cascades: for each inheritance child — if the child's entry has `BaseValueSource > Inherited` (local/style/template), **stop** (that subtree's ancestry is unaffected, WPF-identical); else update the child's cached entry (or just recurse if it never cached and has no listeners), fire its notification chain (steps 6b–d), recurse. At hundreds of elements and a handful of inherited properties (`DataContext`, `Foreground`, `TextAttributes`, `ShowAccessKeys`, `IsEnabled`-style ambient flags), a full-tree cascade is microseconds.
- **Reparenting:** `OnInheritanceParentChanged()` purges all `IsInheritedCache` entries and re-evaluates every `Inherits` property that has listeners or cached entries, firing changes for actual deltas. The widget fork must call it on attach/detach — stated in the contract (§5.1).

This is simpler than WPF's `TreeWalkHelper` machinery (which exists for 100k-element trees) while preserving its *observable* semantics: changes notify, local values block descent, `DataContext` flows.

### 3.8 Targeted change notification

`AddValueChanged(dp, handler)` stores `(GlobalIndex, handler)` in the frugal `_listeners` field (null → single → array). This fixes WPF's worst wart — there, per-instance listening requires the global-leak-prone `DependencyPropertyDescriptor` — while keeping everything else faithful. Consumers: two-way `BindingExpression`s (write-back on target change), `Trigger`/`DataTrigger` condition watchers, Avalonia-style selector activators, and the access-key subsystem. Returns `IDisposable`; the styling/binding forks own unsubscription on detach.

Notification ordering is fixed and documented: class callbacks → virtual → instance listeners → inheritance cascade. Reentrancy (a callback that sets the same property) is permitted; the engine re-runs the pipeline iteratively and the equality gate terminates cycles that converge; a non-converging cycle is an app bug, capped by a Debug-only reentrancy depth assert (WPF behaves the same way, minus the assert).

---

## 4. Requirement satisfaction

| # | Requirement | How this design serves it |
|---|---|---|
| 1 | Styling & templating | Styles/templates are *value sources*, not value writers: the `TryGetNonLocalBaseValue` seam + `BaseValueSource` buckets give setters, theme styles, and template-expanded values their WPF slots; `ClearValue`/trigger-exit correctly *reveals* the next source down. Template expansion uses `GetLocalValueEnumerator` + `Template`/`TemplateParentTrigger` sources. |
| 2 | Data binding | `PropertyValueExpression` at Local priority, `TrySetValue` two-way handshake, `NotifySourceChanged` re-pull, `BindsTwoWayByDefault` metadata, `NotDataBindable` guard. `DataContext` is just an `Inherits` property — the cascade *is* the DataContext propagation mechanism, with change notification driving binding re-resolution for free. |
| 3 | Resource/style inheritance | Two mechanisms, properly separated: *property value inheritance* (this engine, `Inherits` flag) for ambient values like `Foreground`; *resource lookup* (styling fork, walking the logical tree) for `DynamicResource`-style references — which plug in as a `PropertyValueExpression` whose `GetValue` resolves the resource, so resource changes ping through the standard invalidation path. |
| 4 | Logical/physical focus | Read-only properties via `DependencyPropertyKey` (`IsFocused`, `IsKeyboardFocused`, `IsKeyboardFocusWithin` — the last one maintained by the focus engine writing keys up the chain), `FocusManager.FocusedElement` as an attached property on focus scopes — the WPF pattern transplants directly. Triggers/selectors restyle on focus because read-only properties still notify. |
| 5 | Modal/modeless windows | Virtual `InheritanceParent` lets a child window/popup inherit ambient properties (theme foreground, `DataContext`, `ShowAccessKeys`) from its owner across tree boundaries. Modal state itself is an ordinary read-only property windows/triggers can react to. |
| 6 | Access keys | `AccessKeyManager.ShowAccessKeysProperty` (attached, `Inherits`, `AffectsRender`, §2.5): the window flips one value on Alt-down/up (capability-gated per input.md §7 — `ReportsRepeats == true` or `Win32InputMode`; cleared on `FocusEvent { HasFocus: false }`) or sets it permanently `true` on terminals without Alt events; the inheritance cascade re-renders every mnemonic underline in one call. The property system is exactly the right delivery vehicle for this ambient, capability-dependent flag. |
| 7 | XAML | `FromName(name, ownerType)` resolution including attached properties (`Grid.Row` parses to owner `Grid` + name `Row`), static `Get/Set` attached conventions for the loader, `UnsetValue` for "no value", `GetLocalValueEnumerator` for serialization round-trips, metadata defaults available to the editor/tooling via `GetMetadata(Type)`. Boxed `object` storage is *an advantage* here: the loader's converter output slots straight in with no generic dispatch. |
| 8 | Setters + Triggers *or* selectors | Deliberately agnostic: the engine defines the priority *buckets* and the invalidation/listen primitives; whether the styling fork activates values via WPF `Trigger`s or Avalonia selectors, it reports a `BaseValueSource` and calls `InvalidateProperty` on activation flips. `AddValueChanged` serves `DataTrigger` condition watching and selector `:pseudo-class` tracking identically. Requirement 8's "either" is only satisfiable if the property engine doesn't bake in one model — this one doesn't. |
| 9 | The property system itself | §2–3 in full: typed registration, attached, per-type metadata + `OverrideMetadata`, inheritance, the WPF precedence order, sparse packed storage, notification, `GetValue`/`SetValue` + typed sugar, `ClearValue`/`UnsetValue`/`SetCurrentValue`, and the three integration seams. |
| 10 | Rich animation | `SetAnimatedValue` overlay above local with retained base; equality-gated per-frame writes; `AffectsComposite` so transform/opacity animations re-composite cached scenes instead of re-rastering (the Drawing doc's "content slide/fade re-composites a cached scene" path); storyboard fork stays a pure consumer of `IAnimation<T>.ValueAt` + this API. |

---

## 5. Cross-fork contract

What the property system **requires** from the rest of Cursorial.UI, stated as interfaces. (If the other forks are competing property designs rather than sibling subsystems, read this section as the contract the winning property fork must honor toward the styling/binding/widget/animation work regardless.)

### 5.1 From the widget-tree / layout fork

```csharp
// UIElement : DependencyObject must:
protected internal override DependencyObject? InheritanceParent { get; }       // visual/logical parent;
                                                                               // popups/windows may redirect to owner
protected internal override void EnumerateInheritanceChildren(
    DependencyProperty dp, Action<DependencyObject> visit);                    // children for the cascade
// MUST call OnInheritanceParentChanged() on attach/detach/reparent.

protected override void OnPropertyChanged(in DependencyPropertyChangedEventArgs e);
// MUST map FrameworkPropertyMetadata.Options:
//   AffectsMeasure        → InvalidateMeasure()
//   AffectsArrange        → InvalidateArrange()
//   AffectsRender         → invalidate the element's cached Scene (Scene.Invalidate())
//   AffectsComposite      → refresh the element's CompositeParameters / layer params only
//   AffectsParentMeasure/Arrange → walk to parent panel and invalidate it
// MUST guarantee all property access happens on the single render/UI thread
// (input arrives via the dispatcher queue per the established pump→queue→drain pattern).
```

### 5.2 From the styling/templating fork

```csharp
// Implements (typically on UIElement, consulting its applied style/template state):
protected internal override bool TryGetNonLocalBaseValue(
    DependencyProperty dp, out object? value, out BaseValueSource source);
// CONTRACT:
//  - resolution INSIDE the seam must honor the order ThemeStyle < ThemeTrigger < Style
//    < TemplateTrigger < StyleTrigger < Template < TemplateParentTrigger and report the bucket used;
//  - pure read: no side effects, no allocation on the steady path (style tables are pre-indexed
//    by GlobalIndex at style-application time);
//  - on ANY activation change (trigger flips, selector match changes, style re-applied),
//    call element.InvalidateProperty(dp) for each affected property — the engine will re-pull;
//  - trigger/selector condition WATCHING uses element.AddValueChanged(dp, …) and disposes on detach;
//  - DynamicResource-style references are PropertyValueExpression subclasses, not seam values.
```

### 5.3 From the binding fork

```csharp
public sealed class BindingExpression : PropertyValueExpression { /* path walking, converters, modes */ }
// CONTRACT:
//  - attach via d.SetValue(dp, expression); detach honors OnDetach;
//  - two-way: implement TrySetValue (consume + write source) AND AddValueChanged on the target
//    for animation/coercion-modified write-back per BindsTwoWayByDefault/metadata;
//  - DataContext consumption: GetValue(DataContextProperty) + AddValueChanged — the inheritance
//    cascade is the propagation mechanism, the binding fork builds nothing for it;
//  - thread: source-change notifications arriving off-thread MUST be marshaled to the UI thread
//    before NotifySourceChanged (the engine asserts thread affinity in Debug);
//  - respect NotDataBindable.
```

### 5.4 From the animation/storyboard fork

```csharp
// Per animated (element, property): store (IAnimation<T> anim, TimeSpan start) and per frame:
//   element.SetAnimatedValue(dp, Boxes.Of(anim.ValueAt(now - start)));   // engine equality-gates
// On stop/complete-with-release: element.ClearAnimatedValue(dp).
// On complete-with-hold: keep writing or leave the last overlay in place (engine retains it).
// CONTRACT: never write animation results via SetValue (would destroy the local/binding base);
// route transform-ish targets (offset/opacity/clip) at properties marked AffectsComposite,
// never AffectsRender, so cached scenes are re-composited, not re-rastered;
// remember PingPong/AutoReverse finite repeats end at the START value (animation.md) before
// deciding hold-vs-clear.
```

### 5.5 What the property system guarantees back

Stable notification ordering (§3.8); `ClearValue`/trigger-exit reveals the next source with a single observable change; `GetValueSource` diagnostics for DevTools; no allocation on `GetValue`; `InvalidateProperty` is idempotent and equality-gated; all callbacks fire synchronously on the UI thread.

---

## 6. Terminal-specific adaptations (deviations from WPF, each justified)

1. **`AffectsComposite` metadata flag (new).** WPF has no analog because WPF doesn't have the re-composite/re-raster split. In Cursorial, `Scene.Invalidate()` (re-raster: per-cell brush sampling, junction resolution) is *the* expensive operation, while moving/fading a cached scene via `CompositeParameters` is nearly free. Properties like `Opacity`, render offset, and clip must invalidate the *composite*, not the raster. Without this flag, every slide animation would re-raster 50×/sec — the difference between "free" and "the whole frame budget."
2. **No Freezable system.** WPF's deepest complexity (Freezable, sub-property invalidation, `DefaultValueFactory` for mutable defaults) exists because WPF brushes are mutable. `IBrush` is immutable by contract, so metadata defaults like `Brushes.Default` are safely shared singletons, brush "animation" is whole-value replacement via `BrushInterpolator`, and an entire WPF subsystem evaporates. This is the single biggest simplification terminal scale + this codebase's conventions buy us.
3. **No Dispatcher thread-verification in Release.** The stack already contracts a single render thread (CellBuffer/Scene/compositor are not thread-safe); we inherit that contract rather than re-policing it per `GetValue`. Debug-only `VerifyAccess` assert. Saves a branch on the hottest path.
4. **Simplified inheritance machinery.** Lazy walk-up + cached entries + small-tree cascade instead of WPF's `TreeWalkHelper`/`InheritanceBehavior` apparatus (built for 10⁵-node trees). Observable semantics identical; implementation an order of magnitude smaller. Justified by "hundreds, not tens of thousands of elements."
5. **Equality gate as a first-class invariant, not an optimization.** On WPF a spurious change costs a re-render of a region; here it can cost a full scene re-raster plus diff pressure. The gate (boxed `record struct` equality) plus cell-quantizing interpolators means animated properties change at *cell* rate, not *frame* rate. This is the design's main concession to the 20–60 fps / allocation-discipline constraint.
6. **First-class per-instance listeners** (`AddValueChanged`) instead of WPF's `DependencyPropertyDescriptor` global tables — needed anyway by both trigger models of requirement 8, and the WPF approach is a known leak hazard.
7. **`ushort` indices, frugal listener storage, 16-byte entries** — sized for terminal scale (≤~2,000 registered properties, ≤32 entries/element typical) rather than WPF's generality.
8. **Implicit-style bucket folded into `Style`** (WPF distinguishes `ImplicitStyleReference`); at our scale the distinction buys nothing and costs a precedence level. Recorded as a deliberate cut, per the project's "rejected/cut with reasons" convention.

---

## 7. Costs, risks, phasing

**Effort.** Engine core (registry, DOT, metadata, storage, pipeline, inheritance, listeners): ~2,500–3,500 LOC plus a comparable test suite — comfortably the established phase-table playbook (design doc → numbered phases → adversarial review). No dependencies beyond `Cursorial.Core`, so it can land first and unblock the other forks against the §5 contracts.

**Performance profile.**
- `GetValue` (cached): binary search ≤5 compares + reference return; zero alloc.
- `GetValue` (default): metadata MRU hit + shared box; zero alloc.
- `SetValue`: search/insert + pipeline + callbacks; allocs only for the box (cached for discrete types) and first-time `ModifiedValue`.
- Animated frame writes: ≤1 box per *changed* sample (§3.5 math: ~24–32 KB/sec worst realistic case, equality-gated to far less for cell-quantized values).
- Memory: ~16 B/set-property/element + 3 fields/element. A 500-element app with 10 entries each ≈ 80 KB. Registry ≈ 100 B/property.

**Risks & mitigations.**
1. *Precedence bugs* (the classic dependency-property failure mode: trigger-exit doesn't restore style value, clear doesn't reveal inherited, etc.). Mitigation: an oracle-pinned precedence test matrix — every `BaseValueSource` pair × {set, clear, invalidate, animate, coerce} with expected effective value and expected notification — written *before* the engine, per the project's oracle-pinning convention.
2. *Coercion/animation interaction edge cases* (coerce sees animated value; clearing animation must re-coerce base). The `ModifiedValue` retention design handles it structurally; tests pin WPF behavior.
3. *Inheritance cascade on reparent* — subtle (cache purge + delta notification). Bounded by tree size; fuzz-test attach/detach sequences.
4. *Styling-seam contract drift* — the seam's internal sub-ordering is the styling fork's responsibility; mitigated by shipping a contract test kit (a fake seam implementation + conformance suite) alongside Phase 3.
5. *Boxing churn regressions* — guard with an allocation test on the animated-frame path (the repo already does perf-shaped pinning).

**Phasing.**
- **Phase 1 (spine):** registry, `DependencyObjectType`, metadata + `OverrideMetadata`, packed storage, local/default, validate/coerce, change callbacks, `ClearValue`/`UnsetValue`, `Boxes`. *Exit: the precedence matrix passes for Default/Local.*
- **Phase 2:** inheritance (flag, walk, cache, cascade, reparent), attached properties, read-only keys, `AddValueChanged`.
- **Phase 3:** `PropertyValueExpression` + two-way handshake; `TryGetNonLocalBaseValue` seam + `BaseValueSource` buckets + conformance kit; `SetCurrentValue`; `GetValueSource`/`LocalValueEnumerator`.
- **Phase 4:** animation overlay (`Set/ClearAnimatedValue`, `ModifiedValue` retention), `AffectsComposite` plumbing docs, allocation pinning.
- **Punted (recorded as §11-style deferrals):** property-changed *batching*/deferral scopes (measure first; the equality gate may make it moot), `DesignerCoerceValueCallback`-style tooling hooks, weak-event variants of `AddValueChanged`, per-property changed-event args pooling.

---

## 8. Steelman & rebuttal

### Steelman 1 — the Avalonia-faithful fork (`StyledProperty<T>` / `DirectProperty<T>`, typed `ValueStore` with priority frames, observables)

*The strongest honest case:* End-to-end generics mean **zero boxing** and compile-time type safety — `button.GetValue(BackgroundProperty)` is statically `IBrush`, callbacks are `Action<AvaloniaPropertyChangedEventArgs<T>>`, no casts anywhere. Per-priority **value frames** make style application/removal O(1) reversible *inside the store* — popping a style frame reveals the value beneath without re-querying a styling engine, which is architecturally cleaner than my pull-seam: the store owns precedence end-to-end instead of trusting a foreign fork to sub-order its buckets. `IObservable<T>` integration gives bindings and selector activation a uniform reactive substrate. `DirectProperty<T>` gives plain-field-backed properties for hot, never-styled values (scroll offsets) with no store entry at all. And Avalonia proves the model ships, on .NET, today, with selectors — which this project might prefer for requirement 8.

*Rebuttal:* **(a) The boxing win is small and the complexity cost is large, at this scale.** The quantified worst case for boxing here is tens of KB/sec gen0 (§3.5), against a stack that allocates a frame buffer per frame. Meanwhile Avalonia's `ValueStore` is — by its own maintainers' repeated admission across refactors — one of the most intricate parts of that codebase: frames, frame generations, effective-value dictionaries, binding entries, and the typed/untyped dual surface (`AvaloniaProperty` still needs an untyped `object?` path for XAML — the generic purity leaks the moment the loader shows up, and you end up maintaining both). I'd rather implement WPF's two well-understood structures correctly than Avalonia's five intricate ones approximately. **(b) Per-instance frames cost memory per applied style per element**; the WPF pull model stores one cached entry per *property* and re-queries on invalidation — cheaper at rest, and "at rest" is most of a terminal UI's life. **(c) Requirement 8 explicitly allows either trigger model**; my seam serves both, while a frame-based store is shaped around selector-pushed values and makes WPF-style `Trigger` semantics the awkward guest. **(d) Observables are a dependency and an idiom tax** this codebase hasn't bought anywhere else; `AddValueChanged` + `IAsyncEnumerable` idioms match the existing stack. **(e)** The genuinely good Avalonia idea — `DirectProperty` for unstyled hot values — is *additively adoptable later* as a `DependencyProperty` subclass that bypasses storage; nothing in my design forecloses it (and the lower layers' "additive changes only" philosophy suggests exactly that path).

### Steelman 2 — the minimalist fork (INPC + source-generated properties, no property engine)

*The strongest honest case:* A terminal UI with hundreds of elements arguably doesn't need WPF's machinery at all. `[ObservableProperty]`-style source generators give change notification with zero runtime engine, plain fields are as fast as memory gets, debugging is transparent (a property is a property), AOT-trim-friendly, and the whole engine's implementation cost goes to zero. Styling could be "apply = write properties, remember previous values."

*Rebuttal:* This fails the requirements list on contact, not on taste. **(a)** Requirement 9 *is* "multiple prioritized value sources with efficient storage" — "remember previous values to undo a style" is a hand-rolled, per-feature effective-value table; with triggers + styles + animation + inheritance stacking (a focused, hovered, animated, data-bound button is the *normal* case), ad-hoc undo logs become a combinatorial correctness swamp. The engine is precisely the deduplication of that logic. **(b)** Attached properties (`Grid.Row`) have no home in CLR properties — and layout panels need them on day one. **(c)** Per-instance backing fields invert the storage economics: every element pays for every property even though ~95% sit at default; the sparse entry array is *more* memory-efficient, not less. **(d)** Property value inheritance (`DataContext`, `Foreground`, `ShowAccessKeys`) over plain properties means hand-written tree walks per feature. **(e)** XAML (requirement 7) needs runtime name→property resolution and a uniform untyped set-path — which is a property registry, reinvented. The minimalist fork doesn't avoid the engine; it amortizes it badly across every feature that needs it.

### Why WPF-faithful wins

The judges should weigh one asymmetry: **this design's risks are implementation-effort risks; the alternatives' risks are architectural.** Every hard problem here (precedence, retention, cascade) has a 20-year-old reference answer we can pin tests against, the repo's own naming conventions already lean WPF-ward (`RelativePoint`, `Brushes`, `GradientSpread`, `*Animation`), and the terminal's real constraints — coarse owner-driven invalidation, re-raster cost, allocation discipline at 50 fps — are addressed by *mechanisms in the design* (equality gate, `AffectsComposite`, retained bases, boxed-default sharing), not by hoping the scale stays small. Adapt where the terminal demands it; everywhere else, ship the system app authors already know how to use.