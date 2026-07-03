# Cursorial.UI Property System — Design Proposal (Fork A: Avalonia-style)

**Status:** design proposal for `docs/ui-layer-design.md` §Property-System · **Author:** Fork A · **Targets requirement 9, with explicit seams for 1, 2, 3, 7, 8, 10**

---

## 1. Executive summary & philosophy

I propose an Avalonia-shaped property system — `StyledProperty<T>` / `DirectProperty<TOwner,T>` / `AttachedProperty<T>` over a per-instance `ValueStore` of priority frames, with a `UIObject` base exposing `GetValue<T>` / `SetValue<T>` — adapted in three deliberate ways for a terminal cell grid:

1. **Typed end-to-end, zero boxing on every hot path.** Cursorial's styling vocabulary is almost entirely value types (`Color`, `Style`, `Pen`, `Rect`, `Size`, `Margins`, `CompositeParameters`). A WPF-style `object`-slot store boxes every one of them on every read, write, and change notification. Here, values live in `EffectiveValue<T>` holders, notifications flow through typed delegates/observers, and even the virtual change-args carrier is a `ref struct` with allocation-free `GetNewValue<T>()`.
2. **Avalonia's IObservable surface is cut.** No Rx contract, no `GetObservable()` subscription allocations. Replaced by three channels sized to actual consumers: per-type metadata callbacks (invalidation), typed `IValueObserver<T>` lists (binding/selector engines), and one virtual `OnPropertyChanged` (subclass hooks).
3. **Priority is a property of the *write*, not of the writer's machinery.** Avalonia's single best idea, kept intact: a binding is not a special thing living in the local slot (WPF); it is a value producer feeding the store *at a priority*. Styles, selectors, templates, animations, and bindings all converge on one arbitration algorithm. This is what makes requirements 1, 2, 8, and 10 compose instead of collide.

Scale honesty: terminal apps have hundreds of elements, dozens of registered properties per element type, and 20–60 fps frames. The design optimizes for **sparse storage** (most properties are at default), **O(log n) tiny-n lookup** (sorted arrays, not hashtables), and **allocation-free steady state** (an animation writing `Color` at 50 fps allocates nothing after the first frame).

---

## 2. Public API sketch

All types in namespace `Cursorial.UI` (assembly `Cursorial.UI`, project-referencing `Cursorial.Drawing` → `Cursorial.Rendering` → `Cursorial.Core`, plus `Cursorial.Animation`).

### 2.1 Property identity & registration

```csharp
/// Non-generic base for all property kinds; also hosts registration statics and sentinels.
public abstract class UIProperty
{
    public static readonly object UnsetValue;   // singleton sentinel: "this source contributes nothing"

    public int Id { get; }            // dense, process-global, assigned at registration (array-index friendly)
    public string Name { get; }
    public Type PropertyType { get; }
    public Type OwnerType { get; }
    public bool Inherits { get; }     // fixed at registration — deliberately NOT per-type-overridable (see §6)
    public bool IsAttached { get; }
    public bool IsDirect { get; }

    public static StyledProperty<T> Register<TOwner, T>(
        string name,
        T defaultValue = default!,
        bool inherits = false,
        Func<UIObject, T, T>? coerce = null,
        Func<T, bool>? validate = null,
        PropertyChangedCallback<T>? changed = null)
        where TOwner : UIObject;

    public static AttachedProperty<T> RegisterAttached<TOwner, THost, T>(
        string name,
        T defaultValue = default!,
        bool inherits = false,
        Func<UIObject, T, T>? coerce = null,
        PropertyChangedCallback<T>? changed = null)
        where THost : UIObject;       // THost: what instances the property may be set on (DEBUG-asserted)

    public static DirectProperty<TOwner, T> RegisterDirect<TOwner, T>(
        string name,
        Func<TOwner, T> getter,
        Action<TOwner, T>? setter = null,     // null ⇒ read-only (the read-only-property story; no PropertyKey)
        T unsetValue = default!)              // pushed when a binding produces UnsetValue
        where TOwner : UIObject;
}

public delegate void PropertyChangedCallback<T>(UIObject sender, T oldValue, T newValue);

/// Per-type metadata. Sealed record per repo convention; merge semantics on override
/// (null members fall through to the base type's metadata).
public sealed record PropertyMetadata<T>(
    T DefaultValue = default!,
    Func<UIObject, T, T>? Coerce = null,
    Func<T, bool>? Validate = null,
    PropertyChangedCallback<T>? Changed = null,
    IEqualityComparer<T>? Comparer = null);   // null ⇒ EqualityComparer<T>.Default
```

```csharp
public class StyledProperty<T> : UIProperty
{
    public PropertyMetadata<T> GetMetadata(Type forType);                 // cached per concrete type
    public void OverrideMetadata<TOwner>(PropertyMetadata<T> metadata)    // merge; THROWS once any TOwner
        where TOwner : UIObject;                                          //   instance has touched the property
    public void OverrideDefaultValue<TOwner>(T defaultValue) where TOwner : UIObject;  // sugar
    public StyledProperty<T> AddOwner<TOwner>() where TOwner : UIObject;  // WPF-style reuse; registers
}                                                                         //   (TOwner, Name) for XAML lookup

public sealed class AttachedProperty<T> : StyledProperty<T>
{
    public Type HostType { get; }
}

public sealed class DirectProperty<TOwner, T> : UIProperty where TOwner : UIObject
{
    public Func<TOwner, T> Getter { get; }
    public Action<TOwner, T>? Setter { get; }
    public T UnsetValue { get; }
    public bool IsReadOnly => Setter is null;
}
```

### 2.2 The priority model

```csharp
public enum BindingPriority
{
    Animation    = -1,   // storyboard/transition writes — ABOVE local (justified in §3.6)
    LocalValue   =  0,   // SetValue / local {Binding}
    StyleTrigger =  1,   // selector- or trigger-activated setters (:focus, :pointerover, DataTrigger)
    Template     =  2,   // values applied by a control template to its parts
    Style        =  3,   // plain style setters

    // Resolution-only tiers — never assignable to a frame or SetValue:
    Inherited    =  4,   // resolved by walking InheritanceParent
    Default      =  5,   // per-type metadata default
    Unset        = int.MaxValue,
}
```

**Binding is not a priority.** A binding is a value *producer* that contributes at whatever priority it was installed at (`LocalValue` for `{Binding}` on an element, `Style`/`StyleTrigger` for binding-valued setters). Within one priority, *last writer wins* and a binding's push counts as a write — exactly Avalonia's semantics, which makes "set a local value, binding later produces a new value, binding wins again" work without WPF's `SetCurrentValue` contortions in v1.

### 2.3 `UIObject` — the base everything styleable derives from

```csharp
public abstract class UIObject
{
    // ----- read -----
    public T GetValue<T>(StyledProperty<T> property);            // hot path; never boxes
    public T GetBaseValue<T>(StyledProperty<T> property);        // effective value IGNORING Animation
                                                                 //   (storyboard handoff snapshot)
    public object? GetValue(UIProperty property);                // untyped: XAML/tooling/diagnostics only
    public bool IsSet(UIProperty property);                      // any local/frame contribution present?

    // ----- write -----
    public void SetValue<T>(StyledProperty<T> property, T value,
                            BindingPriority priority = BindingPriority.LocalValue);
    public void SetValue<TOwner, T>(DirectProperty<TOwner, T> property, T value) where TOwner : UIObject;
    public void ClearValue(UIProperty property);                 // removes LocalValue value AND detaches
                                                                 //   local-priority bindings; lower tiers resurface
    public void CoerceValue(UIProperty property);                // re-run coercion (Maximum changed → re-coerce Value)

    // ----- integration seams (consumed by the other forks; see §5) -----
    public IDisposable AddObserver<T>(StyledProperty<T> property, IValueObserver<T> observer);
    public IDisposable AddObserver<TOwner, T>(DirectProperty<TOwner, T> property, IValueObserver<T> observer);
    public BindingEntry<T> Bind<T>(StyledProperty<T> property,
                                   BindingPriority priority = BindingPriority.LocalValue);
    public AnimatedValueHandle<T> BeginAnimation<T>(StyledProperty<T> property);
    public void AddFrame(ValueFrame frame);                      // styling/template forks
    public void RemoveFrame(ValueFrame frame);
    public void SetInheritanceParent(UIObject? parent);          // tree fork calls on attach/detach

    // ----- subclass surface -----
    protected bool SetAndRaise<TOwner, T>(DirectProperty<TOwner, T> property, ref T field, T value)
        where TOwner : UIObject;                                 // direct-property write helper; returns "changed"
    protected virtual void OnPropertyChanged(in UIPropertyChangedEventArgs args);
}
```

### 2.4 Change-notification carriers (all allocation-free)

```csharp
/// ref struct: lives only for the synchronous notification; GetXValue<T> casts the internal
/// EffectiveValue<T> holder — typed access with zero boxing and zero allocation.
public readonly ref struct UIPropertyChangedEventArgs
{
    public UIProperty Property { get; }
    public BindingPriority Priority { get; }    // priority of the new effective value
    public T GetOldValue<T>();
    public T GetNewValue<T>();
}

public interface IValueObserver<T>
{
    void OnPropertyChanged(UIObject source, UIProperty property,
                           T oldValue, T newValue, BindingPriority priority);
}
```

### 2.5 Producer handles

```csharp
/// Held by the binding engine; pushes values into the store at the entry's priority.
public sealed class BindingEntry<T> : IDisposable
{
    public BindingPriority Priority { get; }
    public void SetValue(T value);     // invalid per metadata.Validate ⇒ discarded + diagnostic hook
    public void SetUnset();            // contribution withdrawn; lower priorities resurface
    public void Dispose();             // detach permanently
}

/// Held by the storyboard layer; one active handle per (object, property) —
/// BeginAnimation while another is active detaches the prior one (last-started wins;
/// richer composition/handoff is the storyboard's job, using GetBaseValue for snapshots).
public sealed class AnimatedValueHandle<T> : IDisposable
{
    public void SetValue(T value);     // per-frame write: mutates the entry in place, equality-short-circuits,
                                       //   allocates nothing
    public void Dispose();             // animation ends: base value resurfaces with one change notification
}
```

### 2.6 Frames (the styling/template contribution unit)

```csharp
public abstract class ValueFrame
{
    protected ValueFrame(BindingPriority priority);   // Style, StyleTrigger, or Template only
    public BindingPriority Priority { get; }
    public bool IsActive { get; }
    protected void SetActive(bool active);            // selector/trigger toggles; store recomputes affected props
    protected void OnEntryChanged(IValueEntry entry); // a binding-valued setter produced a new value

    public abstract int EntryCount { get; }
    public abstract IValueEntry GetEntry(int index);
}

public interface IValueEntry
{
    UIProperty Property { get; }
    bool HasValue { get; }            // false ⇒ Unset (skip to next source)
}

public interface IValueEntry<T> : IValueEntry
{
    T GetValue();
}
```

The styling fork subclasses `ValueFrame` (one per applied style per element — the *setter list* is shared/immutable; the frame is the thin per-element activation shim). Within a priority, frames added later win; the styling fork expresses specificity by add order.

### 2.7 Consumer usage example

```csharp
public class Button : ContentControl
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Panel.BackgroundProperty.AddOwner<Button>();

    public static readonly StyledProperty<Pen> BorderPenProperty =
        UIProperty.Register<Button, Pen>(nameof(BorderPen), defaultValue: Pens.Rounded);

    public static readonly StyledProperty<bool> IsDefaultProperty =
        UIProperty.Register<Button, bool>(nameof(IsDefault));

    // Read-only state = DirectProperty with no public setter (the Avalonia read-only idiom):
    public static readonly DirectProperty<Button, bool> IsPressedProperty =
        UIProperty.RegisterDirect<Button, bool>(nameof(IsPressed), static b => b._isPressed);
    private bool _isPressed;

    static Button()
    {
        FocusableProperty.OverrideDefaultValue<Button>(true);          // per-type metadata
        AffectsRender<Button>(BackgroundProperty, BorderPenProperty);  // tree fork's invalidation sugar
    }

    public Pen BorderPen
    {
        get => GetValue(BorderPenProperty);
        set => SetValue(BorderPenProperty, value);
    }

    internal void UpdatePressed(bool value) =>
        SetAndRaise(IsPressedProperty, ref _isPressed, value);         // selectors observe :pressed via this
}

public class Grid : Panel
{
    public static readonly AttachedProperty<int> RowProperty =
        UIProperty.RegisterAttached<Grid, UIElement, int>("Row",
            coerce: static (_, v) => Math.Max(0, v));                  // cell grid: never negative

    public static int  GetRow(UIElement e) => e.GetValue(RowProperty);
    public static void SetRow(UIElement e, int value) => e.SetValue(RowProperty, value);
}
```

Priority arbitration, end to end:

```csharp
var b = new Button();
_ = b.GetValue(Button.BackgroundProperty);              // null            (Default)
// theme style applied by styling fork:   Background = Brushes.Blue        (Style frame)
// ":focus" selector becomes active:      Background = Brushes.Cyan        (StyleTrigger frame)
b.SetValue(Button.BackgroundProperty, Brushes.Red);     // red             (LocalValue beats both)
using var anim = b.BeginAnimation(Button.BackgroundProperty);
anim.SetValue(pulse.ValueAt(elapsed));                  // pulsing         (Animation beats local)
// … storyboard disposes `anim` on completion …         // red resurfaces  (base value retained underneath)
b.ClearValue(Button.BackgroundProperty);                // cyan            (StyleTrigger resurfaces)
```

---

## 3. Internal architecture

### 3.1 Registry

`UIPropertyRegistry` (internal static): an append-only `UIProperty[]` indexed by `Id`; a `Dictionary<(Type, string), UIProperty>` for XAML/name lookup (fed by `Register*` and `AddOwner`); a cached `int[] InheritingPropertyIds` (tiny — `DataContext`, `Foreground`, `TextAttributes`, `ShowAccessKeys`, a handful more). Registration happens in static constructors; `RuntimeHelpers.RunClassConstructor` is invoked by the XAML loader before name lookup on a type (same trick Avalonia uses).

### 3.2 Per-instance storage

`UIObject` holds one nullable field: `ValueStore? _store`, allocated on the first write/observe/frame. A default-valued, non-inheriting element costs **zero** property-system bytes beyond the field.

```
ValueStore
├── EffectiveValueTable                     // SPARSE: only properties with a non-default contribution
│     int[] _ids;                           // sorted ascending; binary search (n ≤ ~32 typical, ≤5 compares)
│     EffectiveValueBase[] _entries;        // parallel; doubling growth, insertion keeps sort
├── ValueFrame[] _frames                    // sorted by Priority then add order; typically 0–4
├── ObserverTable                           // property id → IValueObserver<T>[] (copy-on-write arrays)
├── UIObject? _inheritanceParent
└── int _deferDepth + pending-change list   // batching (theme swaps, template application)
```

The per-property entry — the heart of the design:

```csharp
internal abstract class EffectiveValueBase { /* flags, priorities */ }

internal sealed class EffectiveValue<T> : EffectiveValueBase
{
    public T Value;                  // post-coercion effective value — what GetValue returns
    public T Previous;               // valid only during a synchronous notification window
    public T LocalValue;             // valid when Flags.HasLocal
    public T AnimatedValue;          // raw (pre-coercion) animated value, valid when Flags.HasAnimation
    public BindingPriority EffectivePriority;
    public BindingPriority BasePriority;      // priority of the strongest non-Animation source
    // Flags: HasLocal | HasAnimation | LocalIsFromBinding | …
}
```

This mirrors Avalonia 11's effective/base split: because `Animation` is the *only* priority above `LocalValue`, two value slots plus flags suffice — no per-priority array (WPF) and no local-value frame. Entries are **mutated in place** across changes: one allocation per `(instance, property)` that ever leaves its default, ever.

### 3.3 Resolution algorithm

```
ResolveBase(entry, property):
    if entry.HasLocal                          → (LocalValue, entry.LocalValue)
    for frame in _frames where IsActive, high priority → low, later-added first within a priority:
        if frame has IValueEntry<T> for property with HasValue → (frame.Priority, entry.GetValue())
    if property.Inherits                       → (Inherited, walk-up result)      // §3.5
    else                                       → (Default, metadata default for runtime type)

SetEffective(entry, priority, raw):
    coerced  = metadata.Coerce?(owner, raw) ?? raw
    if comparer.Equals(entry.Value, coerced) and priority == entry.EffectivePriority → STOP (no-op)
    entry.Previous = entry.Value; entry.Value = coerced; entry.EffectivePriority = priority
    Notify(entry)                                                                  // §3.4
```

Write paths are short-circuited:

- **`SetValue` (local), no animation active:** write `LocalValue`, set flag, `SetEffective(LocalValue, value)` — no frame scan.
- **`SetValue` while animated:** update `LocalValue` + `BasePriority` only; effective value (animated) untouched; no notification (the base will resurface on animation end).
- **`AnimatedValueHandle.SetValue`:** write `AnimatedValue`, `SetEffective(Animation, value)`. No scan, no allocation; equality short-circuit means an `Int32Animation` that quantizes to the same cell offset for several frames produces **zero** downstream work.
- **`ClearValue` / frame deactivation / `SetUnset`:** full `ResolveBase` rescan for the affected property ids only. Frame activation toggles collect their setter list's property ids and recompute each.
- **Frame add/remove:** recompute the union of the frame's property ids (style application is the "expensive" path; it's still O(setters)).

Validation runs at the mouth: local `SetValue` throws `ArgumentException` on invalid; binding/frame-produced invalid values are discarded with a diagnostic callback (`UIDiagnostics.OnRejectedValue`), keeping the previous value — Avalonia's behavior, correct for live data.

### 3.4 Notification flow

On a real effective-value change, synchronously and in this order:

1. **Metadata `Changed` callback** (per-type, typed, allocation-free) — the invalidation channel (`AffectsRender` et al. are built on it).
2. **Typed observers** for that property id — binding engine (two-way/source updates), selector engine (`:focus`, `DataTrigger` predicates). Observer arrays are copy-on-write so raising never fights mutation.
3. **Virtual `OnPropertyChanged(in UIPropertyChangedEventArgs)`** — the args ref struct wraps the entry; `GetOldValue<T>()` reads `entry.Previous`, `GetNewValue<T>()` reads `entry.Value`. Zero allocation; handlers needing retention copy values out.
4. **Inheritance propagation** if `property.Inherits` (§3.5).

Reentrancy (setting properties from handlers) is allowed and synchronous; the equality short-circuit is the cycle breaker, plus a DEBUG-only depth-64 assert. `DeferNotifications()` (an `IDisposable` scope) batches steps 1–4 for theme swaps and template application: changes are coalesced per property (first old, last new) and flushed once.

### 3.5 Inheritance: lazy-read, eager-notify

- **Read:** `GetValue` on a miss for an inheriting property walks `_inheritanceParent` to the nearest ancestor whose table has an entry, returning that ancestor's *effective* value (an animated `Foreground` on a panel inherits its animated value — WPF-consistent); no ancestor ⇒ metadata default. Terminal trees are shallow (~8–12 deep) and hundreds of elements wide; the walk is cheaper than maintaining per-descendant cache entries, and costs zero memory.
- **Notify:** when an inheriting property's effective value changes on a node (or a node is reparented), the store recurses into inheritance children, **stopping at any subtree whose root has its own contribution** for that property (it shadows), and raises the full notification pipeline on each affected node with `(oldParentValue, newParentValue)`. Reparent diffs all `InheritingPropertyIds` between old and new chains — O(#inheriting × depth), negligible.
- The tree fork maintains parent pointers and child enumeration via `SetInheritanceParent` + an internal `IInheritanceNode` children accessor (see §5).

Inherited reads skip coercion (coercion applies where a value is *set*); this is documented, matches WPF, and avoids re-coercing per descendant per read.

### 3.6 Animation layering: **above local**, and why

`Animation = -1` sits above `LocalValue`, as in both WPF and Avalonia. Justification, not just precedent:

- **Trigger-driven animations must beat the value they animate.** A `:focus` pulse on `Background` has to win against an app's local `Background = Red`, or animations only ever work on purely styled properties — an arbitrary and surprising cliff.
- **Restoration falls out for free.** The base value (local or style or inherited) keeps living in `LocalValue`/frames underneath; disposing the handle resurfaces it with a single notification. Below-local animation would require the storyboard to snapshot and restore values itself — racy against concurrent local writes.
- **Handoff is solved by one API**, `GetBaseValue<T>`: a storyboard retargeting "animate from wherever you are to the new target" snapshots `ValueAt(elapsed)` as the new `From` (the `Cursorial.Animation` layer is immutable; handoff = construct-new, per its own docs).
- The known cost — *a local write during an active animation is invisible until the animation ends* — is mitigated structurally: storyboards are owned by this same UI layer (mechanism/orchestration split), so control authors can specify completion behavior (`Stop` → base resurfaces, including the new local value; `HoldEnd` → handle kept). WPF's confusing `HandoffBehavior`/`FillBehavior` matrix is replaced by "dispose the handle or don't."

### 3.7 Metadata resolution

Each `StyledProperty<T>` holds the registration metadata plus an override list `(Type, PropertyMetadata<T>)`. `GetMetadata(Type)` resolves by walking the runtime type to `OwnerType`, merging (`Changed` callbacks **chain**, base first; other members nearest-override-wins), and caches the merged result in a per-property `Dictionary<Type, PropertyMetadata<T>>` plus a one-element "last type" inline cache (call sites are overwhelmingly monomorphic). `OverrideMetadata` after any instance of that type has touched the property **throws** — this removes the entire cache-invalidation problem class.

### 3.8 Direct properties

`DirectProperty` bypasses the store entirely for storage: the value is a plain field on the control; `SetAndRaise` compares, assigns, and runs notification channels 2–3 (no metadata coercion, no styling, no animation, no inheritance — documented contract). Bindings to direct properties go through `Getter`/`Setter` delegates. This is the high-frequency-internal-state lane (`Bounds`, `IsPressed`, scroll offsets) where even sparse-table lookup is unwelcome.

---

## 4. Requirement satisfaction

- **R9 (the assignment):** typed declaration/registration (§2.1); attached properties (storage is instance-keyed by dense id, so `Grid.Row` needs nothing special — `AttachedProperty<T>` adds host-type validation and the `Get/Set` static idiom); per-type metadata with default/changed/coerce/validate + `OverrideMetadata`/`OverrideDefaultValue` (§2.1, §3.7); inheritance (§3.5); the full priority model with binding orthogonal to priority (§2.2); sparse, boxing-free storage (§3.2); three notification channels (§3.4); `GetValue`/`SetValue` + CLR wrappers + untyped XAML path (§2.3); `ClearValue`/`UnsetValue` (§2.3, §2.5); explicit integration handles for binding, styling, animation (§2.5, §2.6, §5).
- **R1 styling/templating:** styles and templates contribute `ValueFrame`s at `Style`/`Template`; template-applied part values lose to user `SetValue` and to trigger setters, exactly the WPF/Avalonia expectation. `OverrideDefaultValue` gives per-control theme defaults without any frame.
- **R2 binding:** `BindingEntry<T>` (push, typed, priority-tagged, `Unset`-capable) is the entire store-side contract the binding fork needs; `AddObserver` is the target-watch side for two-way. `DataContext` is just an inherited `StyledProperty<object?>` — inheritance gives binding its backbone for free.
- **R3 resource/style inheritance:** property *value* inheritance covered here; resource lookup is the styling fork's, but it rides the same tree walk and `Inherited` tier semantics.
- **R8 setters + triggers/selectors:** setters are `IValueEntry<T>`s in frames; trigger/selector activation is `ValueFrame.SetActive` — value restoration on deactivation is automatic (rescan), the precise thing ad-hoc designs get wrong. Selector predicates (`:focus`, `:pressed`, property-equals) subscribe via typed observers — including on direct properties.
- **R10 animation:** `AnimatedValueHandle<T>` + `Animation` priority + `GetBaseValue` are everything the storyboard layer needs; the pure `IAnimation<T>.ValueAt` results funnel through `SetValue(T)` per frame, allocation-free, with equality short-circuit absorbing cell-quantized no-ops.
- **R7 XAML:** `(Type, Name) → UIProperty` registry lookup, `PropertyType` for converter selection, untyped `SetValue(UIProperty, object?, priority)`, attached-syntax resolution (`Grid.Row` → owner type + name), and static-ctor forcing. Template namescopes/`TemplateBinding` land at `Template` priority via frames.
- **R4/R6 (focus, access keys), supporting role:** focus state as read-only direct properties feeding `:focus` selectors; `ShowAccessKeys` as an **inherited attached `StyledProperty<bool>`** set once at the window root on Alt down/up (Kitty `ReportAllKeysAsEscapeCodes` path) — one write, shadow-aware subtree notification, and every mnemonic label invalidates itself via its metadata callback. Cleared on `FocusEvent { HasFocus: false }` by the input fork.

---

## 5. Cross-fork contract

What I require from / provide to the other workstreams, stated as the seam surface:

```csharp
// ── Provided BY the property system ───────────────────────────────────────────
// To the binding fork:
//   BindingEntry<T> UIObject.Bind<T>(StyledProperty<T>, BindingPriority)
//   IDisposable     UIObject.AddObserver<T>(...)              (target-change watch, typed)
//   object          UIProperty.UnsetValue                     (fallback semantics)
//   DirectProperty  Getter/Setter delegates                   (POCO-speed lane)
// To the styling fork:
//   ValueFrame (subclass), IValueEntry<T>, SetActive, OnEntryChanged
//   UIObject.AddFrame/RemoveFrame; within-priority "later wins" ordering
//   AddObserver — selector predicate watching (styled AND direct properties)
// To the animation/storyboard fork:
//   AnimatedValueHandle<T> UIObject.BeginAnimation<T>(...)
//   T UIObject.GetBaseValue<T>(...)                           (handoff snapshot)
// To the XAML fork:
//   UIPropertyRegistry.Find(Type, string); UIProperty.PropertyType;
//   untyped GetValue/SetValue; DeferNotifications() for bulk apply
// To the tree/layout/render fork:
//   metadata Changed callbacks (build AffectsRender/AffectsMeasure/AffectsComposite on them —
//   the property system itself never references scenes, invalidation, or rendering)

// ── Required FROM other forks ─────────────────────────────────────────────────
public interface IInheritanceNode        // implemented by the tree fork's element base
{
    UIObject? InheritanceParent { get; } // kept current via SetInheritanceParent on attach/detach/reparent
    ReadOnlySpan<UIObject> InheritanceChildren { get; }   // for eager inheritance notification
}
// Threading: ALL property access on the single UI/render thread; the input/dispatcher fork
// marshals events before touching properties (DEBUG thread-affinity assert only).
// Styling fork: frames are per-element shims over shared immutable setter lists; it owns
// selector evaluation and calls SetActive — the store never evaluates selectors.
// Storyboard fork: owns clocks (TimeProvider), completion policy, multi-animation composition;
// the store guarantees only last-handle-wins and base restoration.
```

---

## 6. Terminal-specific adaptations

1. **No `IObservable`/Rx** (per assignment latitude): Avalonia's `GetObservable` allocates a subscription + closure per use and drags an Rx-shaped contract through the codebase. Replaced by `IValueObserver<T>` arrays — same power for the two real consumers (binding, selectors), zero ceremony.
2. **Boxing eliminated where Avalonia still boxes:** Avalonia's `AvaloniaPropertyChangedEventArgs` is a class allocation per change; ours is a `ref struct` over the in-place entry with typed accessors. With `Color`/`Pen`/`Style`/`Rect` dominating the vocabulary and 50 fps animation writes, this is the difference between steady-state-zero and a constant gen0 drizzle.
3. **`Inherits` is fixed at registration, not per-type metadata.** WPF's per-type-flippable inheritance forces an inheritance-cache invalidation protocol. Terminal UIs don't need `Foreground` to inherit on one control class and not another; freezing it keeps §3.5 simple and the inheriting-property set globally enumerable (which the reparent diff relies on).
4. **Lazy-read inheritance** suits shallow, narrow terminal trees: zero cache memory, walk depth ~8; eager-notify preserves correctness for invalidation/selectors. (WPF caches because its trees are 10× deeper and reads 100× hotter; we shouldn't pay its complexity.)
5. **Sorted arrays over hashtables everywhere** (effective table, frames, observer table): at n ≤ 32 a binary search beats hashing on both time and memory, and iteration order is deterministic — which matters for reproducible test assertions, a repo norm (oracle-pinned tables, deterministic parsers).
6. **Coercion as the cell-grid guardrail:** registration-site coercers clamp to the grid's hard constraints (`Rect` is ushort-backed and throws on negatives; sizes ≥ 0; `Grid.Row ≥ 0`) so an overshooting `BackOut` easing or a bad binding can never detonate a `Rect` constructor three layers down. This mirrors the clamping the cell-quantized interpolators already do, now enforced at the property boundary.
7. **Single-thread by contract, not by locks:** the whole stack below (CellBuffer, Scene, compositor) is single-render-thread; the store inherits that assumption and spends zero cycles on synchronization. DEBUG-only thread-affinity assert, matching the lower layers' stance.
8. **No data-validation plumbing in v1** (no `INotifyDataErrorInfo` channel in the value path): terminal forms are a later concern; the `Validate` + diagnostics hook covers integrity without Avalonia's `BindingNotification` weight.

---

## 7. Costs, risks, phasing

**Effort.** ~3–4k LOC plus tests. The `ValueStore` (~900 LOC) is the risk concentration; everything else is mechanical. Phases, following the repo's playbook (design doc first, numbered phases, adversarial review before frames land):

- **P0 — spine:** registry, `UIProperty`/`StyledProperty<T>`/`AttachedProperty<T>`/`DirectProperty`, `UIObject`, local + default tiers, metadata + overrides, all three notification channels, `ClearValue`. Fully usable for hand-built UIs.
- **P1 — frames:** `ValueFrame`, Style/StyleTrigger/Template priorities, `BindingEntry<T>`, `DeferNotifications`. Unblocks styling + binding forks.
- **P2 — inheritance:** walk-up reads, eager subtree notify, reparent diff. Unblocks `DataContext`.
- **P3 — animation:** `AnimatedValueHandle<T>`, `GetBaseValue`, effective/base split exercised under load.
- **Punted:** `SetCurrentValue` (last-writer-wins at LocalValue covers the main scenario; add if TextBox-style controls need binding-preserving internal writes), property-change tracing/dev-tools hooks, weak observers (observers are `IDisposable`-disciplined; leak risk documented), per-type `Inherits`, data validation.

**Perf characteristics.** `GetValue` hot path: null-check + ≤5-compare binary search + one virtual-free field read; default-valued reads skip the store entirely. `SetValue` local fast path: no frame scan. Animation write: in-place mutate + typed callbacks, zero alloc. Memory: ~0 B for default elements; one `EffectiveValue<T>` (~40–250 B depending on `T`, four `T` slots) per touched property per element — at 500 elements × ~8 touched properties, low single-digit KB×10s. Generic instantiation bloat is bounded by distinct property *types* (~20–30 in a real app), AOT-friendly (no reflection outside the XAML fork's seams).

**Risks & mitigations.** (a) *Recompute storms* on theme swap/template apply → `DeferNotifications` coalescing; (b) *observer mutation during raise* → copy-on-write arrays; (c) *metadata-after-use* → hard throw at `OverrideMetadata`; (d) *coercion/changed reentrancy cycles* → equality short-circuit + DEBUG depth assert; (e) *four `T` slots per entry inflate for fat structs* (`Style`) → acceptable at terminal scale; if profiling disagrees, split the animated/local slots into a side allocation for fat `T` (internal change, no API impact).

---

## 8. Steelman & rebuttal

**Steelman 1 — WPF-style `DependencyProperty` (non-generic, boxed `EffectiveValueEntry[]`).** Strongest case: one non-generic storage representation means *no generic instantiation bloat at all*, a single code path to debug, and 20 years of proven semantics; the full priority ladder (incl. `TemplatedParent` trigger tiers) is richer than my five; `DependencyPropertyKey` gives true read-only enforcement, which `DirectProperty` only approximates; boxed storage makes the untyped XAML/tooling path the *fast* path rather than a side door; and WPF's expression-in-slot model means triggers/bindings/animations were co-designed rather than bolted on.
**Rebuttal:** every one of those strengths prices in `object` slots — and this codebase's styling vocabulary is `readonly record struct` all the way down by deliberate convention. WPF boxes each `Color`, `Pen`, and `Rect` on every effective-value store *and* every changed-callback, then asks consumers to cast; at 50 fps animation on value-typed properties that is per-frame garbage by construction, in a project whose stated bar is "per-frame allocs add up." The richer priority ladder is real but unused at terminal scale (Avalonia itself shipped a decade of real apps on five tiers), and read-only enforcement via `DirectProperty` is the same compromise Avalonia made knowingly — `Bounds`, `IsPressed` et al. don't need styling, so they don't need the store. Generic bloat is bounded and measurable (~30 closed types); boxing is unbounded and per-operation. WPF is the design Avalonia's authors refined *after* living inside it; adopting the predecessor because the successor is newer would be the actual risk.

**Steelman 2 — POCO + `INotifyPropertyChanged` (no property system at all).** Strongest case: radically simpler — plain C# properties, plain fields, perfect debuggability, no learning curve, no 900-line ValueStore to harden; terminal apps are small, so "styles" could be imperative apply-on-attach functions and "animation" could write fields directly; `Cursorial.Drawing`'s consumers already work this way happily.
**Rebuttal:** the requirements list, not aesthetics, kills this. Triggers/selectors (R8) need *value restoration* — when `:focus` deactivates, what comes back? The locally-set value? The style value? The inherited one? That question *is* a priority store; INPC designs answer it with per-control ad-hoc "saved previous value" fields that break the moment two sources stack (style + trigger + animation — R1/R8/R10 simultaneously). Animation hold-and-restore (R10) and inheritance with shadowing (R3) re-derive the same machinery twice more. You don't avoid building the value store; you build three bad ones, scattered. Memory also inverts at scale: plain fields cost every-property × every-element always (~80 styleable properties × 500 elements, mostly default), while the sparse store costs only what's touched. And XAML (R7) needs name→property metadata, type info, and untyped set — reflection over POCOs reinvents the registry, minus attached properties, which have no POCO encoding at all. The honest version of this argument is "cut requirements 1, 3, 7, 8, and 10" — which isn't this assignment.

**Residual honesty:** the genuinely weakest points of my design are (a) the `ValueStore`'s effective/base split is subtle and must be hardened with an adversarial-review pass before the styling fork builds on it (the repo's design-panel convention exists for exactly this), and (b) `Animation`-above-`LocalValue` makes "user writes during animation" invisible until completion — inherited from WPF/Avalonia, mitigated by storyboard ownership, but a real teaching cost. I'd take both over boxing the entire styling vocabulary or hand-rolling value restoration per control.