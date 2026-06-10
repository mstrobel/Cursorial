# Cursorial.UI Property System — Fork A Proposal: the Terminal-Optimized Hybrid (`UIProperty`)

---

## 1. Executive summary & philosophy

WPF's `DependencyProperty` and Avalonia's `AvaloniaProperty` solve the same four problems: **sparse storage** (most properties on most elements are default), **value composition** (style vs. local vs. animation must layer, not clobber), **metadata-driven behavior** (defaults, callbacks, inheritance declared once per property), and **change notification cheap enough to hang an entire framework off of**. Both solve them for desktop scale — 10⁴–10⁵ elements, multi-window dispatcher trees, third-party theme ecosystems — and both pay for that scale in machinery: WPF with its 11-level precedence ladder, boxed-`object` callbacks, and per-access thread verification; Avalonia with an observable-based value store it has rewritten three times, each revision moving *toward* flatter, more direct structures.

Cursorial's reality is different and better: **hundreds of elements, one render thread by decree, a value vocabulary of small immutable structs (`Color`, `Rect`, `Thickness`), a pure `elapsed → value` animation layer with no observables anywhere in the stack, and a 20–60 fps loop where steady-state allocations are the enemy.** The hybrid takes both parents' *semantics* — typed static property identities, a single linear priority ladder, metadata with defaults/coercion/inheritance, `ClearValue`/`UnsetValue`/`SetCurrentValue` kinship names — and re-founds the *mechanism* on what this codebase already is:

- **One flat sorted entry table per element**, keyed by a packed `(propertyIndex << 4) | priority` integer. Binary search, struct entries, no per-property objects, no expression trees in the store.
- **Typed end-to-end.** `GetValue<T>`/`SetValue<T>` never box on the hot path; change callbacks receive `in UIPropertyChange<T>`; the boxed-`object` surface exists but is the slow lane, not the foundation.
- **Mutable typed cells for high-frequency writers.** The animation clock and binding engine rewrite the same slot every frame/push; their entries hold a reusable `ValueCell<T>` so a 50 fps animation of 20 properties produces **zero steady-state garbage**.
- **No observable framework.** Change notification is direct dispatch: metadata callback → virtual `OnPropertyChanged<T>` → registered watchers → inheritance push → invalidation flags. An `IObservable` adapter is thirty lines on top for anyone who wants it; ripping Rx out of a store (Avalonia's decade) is not.
- **A 7-level priority ladder** (Avalonia's shape, not WPF's): `Default < Inherited < Style < Template < Trigger < Local < Animation`, with one internal slot for coerced results. Bindings are **not a level** — a binding is a value *producer* that enters at the priority of wherever it was declared, exactly as in both parents.

The design goal in one sentence: *an implementer should be able to hold the entire value store in their head, and a profiler should find nothing to report at terminal scale.*

---

## 2. Public API sketch

All types live in namespace `Cursorial.UI` (project `Cursorial.UI`, referencing `Cursorial.Drawing` → `Cursorial.Animation` → `Cursorial.Rendering` → `Cursorial.Core` per the established acyclic stack).

### 2.1 Priorities and effects

```csharp
/// <summary>Where a value entered the composition stack. Higher wins.</summary>
public enum ValuePriority : byte
{
    Default   = 0,  // metadata default — never stored per-instance
    Inherited = 1,  // received from the inheritance parent — cached, never entry-stored
    Style     = 2,  // style setters (theme + app styles; styling fork arbitrates within)
    Template  = 3,  // values applied by template expansion (incl. TemplateBinding)
    Trigger   = 4,  // trigger / selector-activated setters
    Local     = 5,  // SetValue, XAML attributes, locally-declared bindings
    Animation = 6,  // the animation clock
    // 15 (internal) = coerced-result slot; never visible through the public enum
}

[Flags]
public enum PropertyEffects : byte
{
    None          = 0,
    Render        = 1 << 0,  // owner's scene must re-raster (Scene.Invalidate)
    Arrange       = 1 << 1,
    Measure       = 1 << 2,  // implies Arrange
    ParentArrange = 1 << 3,  // attached layout props (Grid.Row) invalidate the parent
    ParentMeasure = 1 << 4,
}
```

### 2.2 Property identity and registration

```csharp
public abstract class UIProperty
{
    public string Name { get; }
    public Type ValueType { get; }
    public Type OwnerType { get; }
    public int GlobalIndex { get; }              // dense, assigned at registration; the store key
    public bool IsAttached { get; }
    public bool IsReadOnly { get; }
    public bool Inherits { get; }                // fixed at registration (uniform across types)
    internal int InheritSlot { get; }            // -1, or dense slot in the inheritance cache

    /// <summary>Sentinel meaning "no value here — fall through to the next source".</summary>
    public static readonly object UnsetValue;

    // ---- registration --------------------------------------------------
    public static UIProperty<T> Register<TOwner, T>(
        string name,
        T defaultValue = default!,
        PropertyEffects affects = PropertyEffects.None,
        UIPropertyChangedCallback<T>? changed = null,
        CoerceValueCallback<T>? coerce = null,
        bool inherits = false,
        ValidateValueCallback<T>? validate = null) where TOwner : UIObject;

    public static UIProperty<T> Register<TOwner, T>(
        string name, UIPropertyMetadata<T> metadata,
        bool inherits = false, ValidateValueCallback<T>? validate = null) where TOwner : UIObject;

    public static UIProperty<T> RegisterAttached<TOwner, T>(
        string name,
        T defaultValue = default!,
        PropertyEffects affects = PropertyEffects.None,
        UIPropertyChangedCallback<T>? changed = null,
        ValidateValueCallback<T>? validate = null,
        bool inherits = false) where TOwner : UIObject;

    public static UIPropertyKey<T> RegisterReadOnly<TOwner, T>(
        string name,
        T defaultValue = default!,
        PropertyEffects affects = PropertyEffects.None,
        UIPropertyChangedCallback<T>? changed = null) where TOwner : UIObject;

    // ---- lookup (XAML / binding-path / DevTools) -----------------------
    /// <summary>Finds a property by name visible on <paramref name="type"/> (declared,
    /// inherited via AddOwner, or attached-and-registered). Runs the declaring type's
    /// static constructor first — the classic "static ctor never ran" pitfall is handled here.</summary>
    public static UIProperty? Find(Type type, string name);
    public static IReadOnlyList<UIProperty> GetRegistered(Type type);
}

public delegate void UIPropertyChangedCallback<T>(UIObject sender, in UIPropertyChange<T> change);
public delegate void UIPropertyChangedCallback(UIObject sender, in UIPropertyChange change);
public delegate T    CoerceValueCallback<T>(UIObject sender, T baseValue);
public delegate bool ValidateValueCallback<T>(T value);
```

```csharp
public sealed class UIProperty<T> : UIProperty
{
    public UIPropertyMetadata<T> DefaultMetadata { get; }
    public ValidateValueCallback<T>? Validate { get; }   // registration-wide, like WPF

    /// <summary>Per-type metadata override. Must run before the first instantiation of
    /// <typeparamref name="TOwner"/> (debug-enforced); unspecified members merge from base.</summary>
    public void OverrideMetadata<TOwner>(UIPropertyMetadata<T> metadata) where TOwner : UIObject;

    /// <summary>WPF-style co-ownership: makes this property resolvable as TOwner.Name.</summary>
    public UIProperty<T> AddOwner<TOwner>(UIPropertyMetadata<T>? metadata = null) where TOwner : UIObject;
}

/// <summary>Write token for a read-only property (IsPressed, IsKeyboardFocused, …).</summary>
public sealed class UIPropertyKey<T>
{
    public UIProperty<T> Property { get; }
}
```

### 2.3 Metadata

```csharp
public class UIPropertyMetadata<T>
{
    public UIPropertyMetadata(T defaultValue = default!) { … }

    /// <summary>Must be immutable / freely shareable (frozen brushes OK, mutable lists not).
    /// Debug builds assert on known-mutable types.</summary>
    public T DefaultValue { get; init; }
    public UIPropertyChangedCallback<T>? PropertyChanged { get; init; }
    public CoerceValueCallback<T>? Coerce { get; init; }
    public PropertyEffects Affects { get; init; }
    /// <summary>null ⇒ EqualityComparer&lt;T&gt;.Default. Change dispatch is gated on inequality.</summary>
    public IEqualityComparer<T>? Comparer { get; init; }
}
```

`Inherits` and `Validate` are deliberately **registration-level, not metadata-level**: inheritance participation determines cache-slot assignment (must be uniform across the type lattice — WPF documents the same restriction and then lets you violate it confusingly), and validation is a type-domain constraint, not a per-type policy.

### 2.4 The host object and value access

```csharp
public abstract class UIObject
{
    // ---- read ------------------------------------------------------------
    public T GetValue<T>(UIProperty<T> property);                       // hot path; never boxes
    public object? GetValue(UIProperty property);                       // boxed convenience
    /// <summary>Effective value considering only sources at or below the given priority.
    /// Animation handoff and trigger-exit use this ("what would I be without the animation?").</summary>
    public T GetValue<T>(UIProperty<T> property, ValuePriority maxPriority);

    // ---- write -----------------------------------------------------------
    public void SetValue<T>(UIProperty<T> property, T value);           // ValuePriority.Local
    public void SetValue<T>(UIProperty<T> property, T value,
                            ValuePriority priority,
                            IValueEvictionListener? owner = null);      // engine surface
    public void SetValue(UIProperty property, object? value,
                         ValuePriority priority = ValuePriority.Local); // boxed; UnsetValue ⇒ clear
    public void SetValue<T>(UIPropertyKey<T> key, T value);             // read-only properties

    /// <summary>Replaces the current effective value in place without changing its source
    /// (a binding-fed entry stays binding-fed and will be overwritten by the next push).
    /// No entry at all ⇒ behaves as a Local set. The two-way-binding target-write primitive.</summary>
    public void SetCurrentValue<T>(UIProperty<T> property, T value);

    public void ClearValue(UIProperty property);                        // clears Local (WPF kinship)
    public void ClearValue(UIProperty property, ValuePriority priority);
    public void CoerceValue(UIProperty property);                       // re-run coercion (Slider Min/Max/Value)

    // ---- introspection ----------------------------------------------------
    public bool IsSet(UIProperty property);                             // any stored entry?
    public ValuePriority GetValueSource(UIProperty property);           // Default/Inherited when nothing stored
    public bool IsAnimated(UIProperty property);

    // ---- change notification ----------------------------------------------
    public IDisposable Watch<T>(UIProperty<T> property, UIPropertyChangedCallback<T> watcher);
    public IDisposable Watch(UIProperty property, UIPropertyChangedCallback watcher);
    protected virtual void OnPropertyChanged<T>(in UIPropertyChange<T> change) { }

    /// <summary>Bridge to the widget fork's dirty system: called once per effective change
    /// whose metadata carries non-None effects. Implementations route to
    /// InvalidateMeasure/InvalidateArrange/Scene.Invalidate; must be idempotent per frame.</summary>
    protected virtual void InvalidateFromProperty(UIProperty property, PropertyEffects effects) { }

    // ---- inheritance plumbing (called by the tree fork) ---------------------
    protected UIObject? InheritanceParent { get; }
    protected void SetInheritanceParent(UIObject? parent);              // pulls + diffs + notifies
    protected internal virtual void VisitInheritanceChildren(Action<UIObject> visit) { }
}

/// <summary>Receives eviction when another writer replaces/clears a cell-backed entry —
/// how a two-way binding learns that SetValue(Local) killed it.</summary>
public interface IValueEvictionListener
{
    void OnValueEvicted(UIObject target, UIProperty property, ValuePriority priority);
}

public readonly struct UIPropertyChange<T>
{
    public UIProperty<T> Property { get; }
    public T OldValue { get; }
    public T NewValue { get; }
    public ValuePriority OldPriority { get; }
    public ValuePriority NewPriority { get; }
    public UIPropertyChange Box();   // untyped view; boxes lazily, only when asked
}

public readonly struct UIPropertyChange
{
    public UIProperty Property { get; }
    public object? OldValue { get; }
    public object? NewValue { get; }
    public ValuePriority OldPriority { get; }
    public ValuePriority NewPriority { get; }
}
```

### 2.5 Consumer usage — a realistic `Button`

```csharp
public class Button : ContentControl
{
    // Plain styled property with render invalidation baked into metadata.
    public static readonly UIProperty<Color> AccentProperty =
        UIProperty.Register<Button, Color>(nameof(Accent),
            defaultValue: Colors.Cyan,
            affects: PropertyEffects.Render);

    // Read-only state property — trigger/selector source; only the control can write it.
    private static readonly UIPropertyKey<bool> IsPressedPropertyKey =
        UIProperty.RegisterReadOnly<Button, bool>(nameof(IsPressed), false,
            affects: PropertyEffects.Render);
    public static readonly UIProperty<bool> IsPressedProperty = IsPressedPropertyKey.Property;

    // Access-key text, with a change callback (re-parse the underscore mnemonic).
    public static readonly UIProperty<string?> TextProperty =
        UIProperty.Register<Button, string?>(nameof(Text), null,
            affects: PropertyEffects.Measure,
            changed: static (sender, in change) => ((Button)sender).ReparseAccessKey(change.NewValue));

    public Color   Accent    { get => GetValue(AccentProperty);    set => SetValue(AccentProperty, value); }
    public bool    IsPressed { get => GetValue(IsPressedProperty); private set => SetValue(IsPressedPropertyKey, value); }
    public string? Text      { get => GetValue(TextProperty);      set => SetValue(TextProperty, value); }
}
```

Attached property (`Grid.Row`) — note `ParentArrange`, which routes invalidation to the panel that consumes the value rather than the child that carries it:

```csharp
public class Grid : Panel
{
    public static readonly UIProperty<int> RowProperty =
        UIProperty.RegisterAttached<Grid, int>("Row", 0,
            affects: PropertyEffects.ParentArrange,
            validate: static v => v >= 0);

    public static int  GetRow(UIObject target)            => target.GetValue(RowProperty);
    public static void SetRow(UIObject target, int value) => target.SetValue(RowProperty, value);
}
```

Inherited property (declared once on the element base by the tree fork):

```csharp
public static readonly UIProperty<object?> DataContextProperty =
    UIProperty.Register<UIElement, object?>(nameof(DataContext), null, inherits: true);

public static readonly UIProperty<Color> ForegroundProperty =
    UIProperty.Register<UIElement, Color>(nameof(Foreground), Colors.Default,
        inherits: true, affects: PropertyEffects.Render);
```

The layering in action (app code):

```csharp
var button = new Button();
// style engine matched a selector:
button.SetValue(Button.AccentProperty, theme.Accent, ValuePriority.Style);
// app code wins over the style:
button.Accent = Colors.LightMagenta;                       // Local
// a hover transition wins over everything, garbage-free per frame:
clock.Animate(button, Button.AccentProperty,
    new ColorAnimation(button.Accent, Colors.TrueWhite, TimeSpan.FromMilliseconds(150), Easings.QuadOut));
// …animation completes with FillBehavior.Stop:
button.ClearValue(Button.AccentProperty, ValuePriority.Animation);  // reverts to LightMagenta
button.ClearValue(Button.AccentProperty);                           // reverts to theme.Accent (Style)
```

### 2.6 Source-generator sugar (Phase 5, optional, emits exactly the manual pattern)

```csharp
public partial class Badge : Control
{
    [UIProp(Affects = PropertyEffects.Render)]
    public partial Color Fill { get; set; }

    private static Color FillDefaultValue => Colors.Red;            // convention-bound default
    partial void OnFillChanged(Color oldValue, Color newValue);     // convention-bound callback
}
```

The generator (C# partial properties + a `[ModuleInitializer]` per assembly so registration precedes any XAML name lookup) produces the static `UIProperty<Color> FillProperty`, the accessor bodies, and the callback wiring. It is *sugar only* — the hand-written form above is the contract, so the generator can ship late without blocking anything.

---

## 3. Internal architecture

### 3.1 Storage layout

Per-element state is one struct embedded **by value** in `UIObject` — no separate store allocation for elements that never deviate from defaults:

```csharp
internal struct ValueStore
{
    private ValueEntry[]? _entries;     // sorted ascending by Key; null until first set
    private int _count;
    private object?[]? _inherited;      // index = UIProperty.InheritSlot; null array until first push
    private WatcherEntry[]? _watchers;  // (int propertyIndex, object callbackDelegate)
    private int _watcherCount;
    private byte _reentrancyDepth;      // coercion/callback cycle guard
}

[StructLayout(LayoutKind.Auto)]
internal struct ValueEntry
{
    internal uint Key;          // (uint)(GlobalIndex << 4) | slot   — slot = ValuePriority, 15 = coerced
    internal ValueFlags Flags;  // Cell | Coerced | BindingFed
    internal object? Storage;   // reference value | (cached) box | ValueCell<T>
}

internal sealed class ValueCell<T>      // reusable typed slot for high-frequency writers
{
    internal T Value;
    internal IValueEvictionListener? Owner;
}
```

Sizing: `ValueEntry` is 16 bytes on x64. A busy element with 12 entries holds one 16-slot array ≈ 280 bytes. A default-valued element holds **nothing**. Growth is 4 → 8 → 16 → … with `Array.Copy` shifting on insert; at entry counts this small (median well under 10), insertion cost is noise and we use a linear scan below 8 entries, binary search above — both branch-predictable.

**Packed key.** Four bits of priority (7 public levels + internal coerced slot 15 + headroom), 28 bits of property index. One `uint` comparison drives all searching; the highest-priority entry for property *p* is the last entry with `Key >> 4 == p.GlobalIndex` — found with a single upper-bound binary search for `(p.GlobalIndex + 1) << 4`.

**Three storage forms behind one `object?` field:**

| Form | Used for | Allocation behavior |
|---|---|---|
| Direct reference | reference-typed values | none beyond the value itself |
| Box | value-typed one-shot writes | `BoxCache` interns `bool`, small ints, enum zeros, and **each property's default box**; cold values box once per write |
| `ValueCell<T>` | Animation priority always; any engine write passing an `owner` | one cell allocation at slot creation; every subsequent write mutates in place — **zero per-frame garbage** |

Boxes are never mutated (they can escape via untyped `GetValue`); cells never escape (typed `GetValue<T>` reads `cell.Value`; untyped `GetValue` boxes a snapshot on demand, which is the rare diagnostics path).

### 3.2 Read path

```csharp
public T GetValue<T>(UIProperty<T> property)
{
    if (_store.TryPeekHighest(property.GlobalIndex, out ref ValueEntry e))
        return ValueStorage.Unwrap<T>(in e);                  // cell read or unbox; no virtual calls

    int slot = property.InheritSlot;
    if (slot >= 0 && _store.TryGetInherited(slot, out object? box))
        return (T)box!;                                       // shared box pushed from the ancestor

    return MetadataTable.For(GetType()).Resolve(property).DefaultValue;   // frozen per-type table
}
```

Three tiers, in order: stored entries (includes the coerced-result slot, which — being 15 — is naturally the "highest entry" whenever coercion changed the value, so reads have **one** code path), inheritance cache, metadata default. No tree walking at read time, ever (see 3.5). The hot render-loop pattern — a widget's `Draw` delegate reading a dozen properties during scene re-raster — costs a dozen short searches and zero allocations.

### 3.3 Write pipeline

```csharp
internal void SetCore<T>(UIObject host, UIProperty<T> p, T value, int slot, IValueEvictionListener? owner)
{
    if (p.Validate is { } v && !v(value)) throw new ArgumentException(...);

    T oldEff = host.GetValue(p);                       // pre-change effective (3-tier read)
    var oldSrc = host.GetValueSource(p);

    WriteEntry(p.GlobalIndex, slot, value, owner);     // insert-or-update; cell reuse; eviction callback
                                                       //   on replacing a foreign-owned cell
    RecomputeAndDispatch(host, p, oldEff, oldSrc);
}

private void RecomputeAndDispatch<T>(UIObject host, UIProperty<T> p, T oldEff, ValuePriority oldSrc)
{
    T newEff = PeekBaseEffective<T>(p);                // highest entry excluding the coerced slot
    var meta = MetadataTable.For(host.GetType()).Resolve(p);

    if (meta.Coerce is { } coerce)
    {
        T coerced = coerce(host, newEff);
        UpdateCoercedSlot(p, in newEff, in coerced);   // write slot 15 iff coerced != base, else remove it
        newEff = coerced;
    }

    if ((meta.Comparer ?? EqualityComparer<T>.Default).Equals(oldEff, newEff))
        return;                                        // composition changed, value didn't: silent

    DispatchChange(host, p, oldEff, newEff, oldSrc, host.GetValueSource(p));
}
```

`ClearValue` and untyped `SetValue` reach the same generic pipeline through a non-generic virtual on `UIProperty` (`internal abstract void ClearCore(UIObject host, int slot)`) implemented by `UIProperty<T>` — virtual dispatch happens **once per operation**, not per callback, and the pipeline stays typed throughout.

**Coercion as a store slot** (the one place we diverge mechanically from both parents, deliberately): WPF stores base + coerced values inside `EffectiveValueEntry` with flag soup; we give the coerced result priority slot 15. Reads see it for free; `CoerceValue()` just re-runs `RecomputeAndDispatch`; `GetValue(p, maxPriority)` skips it by construction (it caps the search at the requested priority, and the base-effective peek always excludes slot 15). Re-entrancy (a coerce callback calling `SetValue`) is legal and converges via the equality gate; `_reentrancyDepth` throws past 32 nested dispatches to turn unbounded oscillation into a diagnosable exception rather than a stack overflow.

### 3.4 Change dispatch

Synchronous, depth-first, in a fixed order; the store is fully committed before the first callback runs, so every callback observing `GetValue` sees the new world:

1. **Metadata `PropertyChanged` chain** — resolved per type, base-most first (WPF convention; `OverrideMetadata` callbacks *append*, never replace).
2. **`OnPropertyChanged<T>(in change)`** — a *generic virtual* method. This is the load-bearing anti-boxing decision: WPF's `DependencyPropertyChangedEventArgs` boxes old and new values on every change; we hand the control its own typed values in a `readonly struct` passed by `in`. Generic-virtual dispatch costs a lookup on first call per (type, T) and is branch-cached thereafter — measured noise at hundreds of elements.
3. **Watchers** — linear scan of the per-element `(propertyIndex, delegate)` list (these lists are tiny: a handful of bindings/trigger-conditions per element). Typed watchers get the typed struct; the untyped boxed view is materialized **only if** an untyped watcher is actually registered on that property.
4. **Inheritance propagation** (if `InheritSlot >= 0`) — see 3.5.
5. **Effects invalidation** — `InvalidateFromProperty(property, meta.Affects)`, exactly once per effective change. Dirty flags in the widget fork are idempotent per frame, so ordering relative to callbacks is immaterial; running last means callbacks that themselves set properties don't double-invalidate.

No queues, no dispatcher priorities, no deferral: a property set during frame N's input drain is visible to frame N's layout and render. That frame-coherence guarantee is something both desktop parents *cannot* give (their dispatchers interleave) and a terminal main loop gets for free.

### 3.5 Inheritance: push-down with shared boxes

Inheritable properties (`Foreground`, `Background`, `TextAttributes`, `DataContext`, `UseAccessKeyIndicators`, …) get a dense `InheritSlot` at registration — realistically fewer than 16 across the whole framework.

- **Reads are O(1):** `_inherited[slot]` holds the effective value **as a box shared by the entire subtree** — one box per *change*, not per element. No read-time tree walk exists anywhere.
- **Writes push down:** when an element's effective value for an inheritable property changes, `PropagateInherited` visits `VisitInheritanceChildren`; at each child it (a) prunes the branch if the child has any stored entry for the property (the child's own value shadows inheritance — matching WPF), otherwise (b) writes the shared box into the child's cache, runs the child's dispatch steps 1–3 + 5 with `OldPriority/NewPriority = Inherited`, and recurses.
- **Reparenting** (`SetInheritanceParent`) diffs every inherit slot between old and new parent chains and dispatches only actual changes; cost is O(inheritable properties × subtree), which at "hundreds of elements" is microseconds and happens at tree-mutation rate, not frame rate.
- **Modal/modeless child windows** choose their inheritance parent explicitly at creation (owner window or null) — the mechanism is parent-pointer-based and policy-free, so the windowing fork decides whether a dialog inherits the owner's theme brushes.

One documented caveat: an *animated* inheritable property on an ancestor produces one fresh box per changed frame for the subtree push (the cell's value must be snapshotted to share). Animating `Foreground` on a container is the only way to hit it; it degrades to one small allocation per frame, not per element.

### 3.6 Registry and metadata resolution

- A global append-only registry assigns `GlobalIndex` (lock-protected during static-construction time; reads are lock-free against an immutable snapshot). Name lookup is per-type: `UIProperty.Find(type, name)` forces the type's static constructor (`RuntimeHelpers.RunClassConstructor`), then consults a lazily built **`FrozenDictionary<string, UIProperty>`** per type covering declared + `AddOwner`ed + attached-visible properties. XAML and binding-path resolution hit this table.
- Effective metadata per (type, property) — the `OverrideMetadata` merge — is resolved once per concrete element type into a **`FrozenDictionary<int, ResolvedMetadata>`** built on first instantiation and cached on a static-per-type holder (`MetadataTable.For(Type)` with a `ConditionalWeakTable` fallback only for dynamically discovered types). `OverrideMetadata` after the table froze throws in debug builds — the WPF "override too late silently ignored" trap becomes a loud error.

### 3.7 Threading

Single UI thread by repo law. There is **no per-access verification** (WPF's `VerifyAccess` on every `GetValue` is a real, measured cost it pays for multi-dispatcher safety we don't need). Debug builds capture the first-touch thread per `UIObject` and assert on cross-thread mutation; release builds trust the architecture, same as `CellBuffer` and `Scene` already do.

---

## 4. Requirement satisfaction

**Req 9 (this fork) — typed declaration/registration:** §2.2; static `UIProperty<T>` identities with dense indices, attached and read-only variants. **Metadata:** §2.3 — defaults, change callbacks, coercion, validation, effects, inheritance flag, per-type `OverrideMetadata`/`AddOwner` with merge. **Inheritance:** §3.5 — push-down, O(1) reads, shadowing, reparent diffing; `DataContext` and `Foreground` are just registrations. **Priority model:** the 7-level ladder of §2.1; bindings enter at their declaration site's priority (the next paragraph); animation on top (§6 justification). **Efficient storage:** §3.1 — flat sorted struct table, packed keys, cells, box interning, zero footprint at defaults. **Change notification:** §3.4 — direct, typed, synchronous, allocation-free in steady state. **Read API:** `GetValue<T>`/boxed/`maxPriority` + CLR wrappers. **Unset/clear:** `UnsetValue` sentinel (untyped `SetValue(UnsetValue)` ≡ targeted clear, the binding fall-through primitive), `ClearValue` (Local) and `ClearValue(priority)`, clearing reveals the next source and dispatches only on real value change.

**Req 2 (binding)** — the binding engine is *a client, not a partner in crime*: it `Watch`es source steps (including inherited `DataContext`, whose changes arrive as ordinary dispatches), pushes target values via `SetValue(prop, value, declarationPriority, owner: expression)` into a reusable cell (garbage-free repeated pushes), yields gracefully via `UnsetValue` when the path breaks, learns of its own death via `IValueEvictionListener` when app code sets a Local value over it, and uses `SetCurrentValue` for two-way target-side writes that must not detach the binding. **Bindings are not a priority level** — the requirement lists "binding" among sources, and the honest answer from both parent frameworks is that a binding is a value *producer* attached at a priority (WPF stores `BindingExpression` as a local value; Avalonia's `Bind` takes a `BindingPriority`). A local binding occupies Local; a style-setter binding occupies Style; a `TemplateBinding` occupies Template. Giving "binding" its own rung would force the unanswerable question "does a binding in a style beat a local literal?" — no parent says yes.

**Reqs 1, 3, 8 (styling/templating/triggers/selectors)** — the ladder gives styling three rungs (Style/Template/Trigger) with WPF-compatible *relative* semantics in Avalonia's simpler shape; the **one-winner-per-priority contract** (§5) keeps specificity arbitration in the styling fork where the selector knowledge lives; read-only property keys give trigger conditions trustworthy sources (`IsPressed` can't be spoofed by a style); `Watch` powers trigger/DataTrigger condition monitoring; resource/style *inheritance* (req 3) composes from property inheritance (theme-level `Foreground`) plus styling-fork resource resolution pushing concrete values (no deferred-expression machinery in the store — §6).

**Req 7 (XAML)** — `UIProperty.Find(Type, string)` with static-ctor forcing, attached-property resolution (`"Grid.Row"`), `ValueType` for converter selection, `UnsetValue` for optional attributes, registration via module initializers from the generator. Template plumbing: template expansion writes at `Template` priority and the property system needs to know nothing else.

**Req 10 (animation)** — `ValuePriority.Animation` + cells = a per-frame `SetValue` that allocates nothing and short-circuits dispatch when the quantized value didn't change (an `Int32Animation` of a cell offset frequently produces identical values across frames — those frames cost one equality check and **no** invalidation, which composes perfectly with `SceneCompositor.Composite` returning `false`). `GetValue(p, maxPriority: Local)` is the handoff snapshot primitive the storyboard layer needs for retargeting (the animation layer is immutable; handoff = construct new animation from the sampled current value). Completion: `ClearValue(Animation)` for `Stop`, or leave the last write in place for hold-end at zero ongoing cost.

**Reqs 4, 5, 6 (focus, windows, access keys)** — consumers, satisfied by primitives: read-only keys for `IsKeyboardFocused`/`IsLogicallyFocused` (trigger sources for focus visuals), inheritance scoping per window root (§3.5), and access-key state as an inheritable `UseAccessKeyIndicators`/`AccessKeyIndicatorsVisible` property toggled at the window root when the Alt-down/up bracketing is available (`Keyboard.ReportsRepeats == true` per the input reference §7) — one property change at the root propagates with pruning and `PropertyEffects.Render` re-rasters exactly the labels that show underscores.

---

## 5. Cross-fork contract

What I **require** and **provide**, stated as the interface the other forks compile against.

### 5.1 From the widget-tree / layout fork (required)

```csharp
// On the element base class (derives from UIObject):
protected void SetInheritanceParent(UIObject? parent);   // call on logical attach/detach, BEFORE
                                                         // child-visible attach events
protected internal override void VisitInheritanceChildren(Action<UIObject> visit); // logical children
protected override void InvalidateFromProperty(UIProperty property, PropertyEffects effects);
// → maps Render → owning Scene.Invalidate(); Measure/Arrange → layout queue;
//   ParentMeasure/ParentArrange → same on InheritanceParent. Must be idempotent per frame.
```

Plus: all property mutation happens on the single UI thread (the main-loop drain pattern from the rendering reference); element types register properties in static constructors and never after instances exist.

### 5.2 To the styling/templating fork (provided + contract)

```csharp
void SetValue<T>(UIProperty<T>, T, ValuePriority.Style | Template | Trigger, IValueEvictionListener? owner);
void SetValue(UIProperty, object? /* may be UnsetValue */, ValuePriority);
void ClearValue(UIProperty, ValuePriority);
IDisposable Watch<T>(UIProperty<T>, UIPropertyChangedCallback<T>);   // trigger conditions
ValuePriority GetValueSource(UIProperty);                            // DevTools / diagnostics
static UIProperty? UIProperty.Find(Type, string);                    // selector property resolution
static IReadOnlyList<UIProperty> UIProperty.GetRegistered(Type);     // style applicability caches
```

**The one-winner-per-priority contract (the load-bearing clause):** the store holds at most one entry per (property, priority). When three styles match an element and two set `Accent`, *the styling fork* computes the winner (specificity, declaration order, theme-vs-app) and writes exactly one `Style`-priority value; when a higher-specificity trigger deactivates, *the styling fork* writes the runner-up or clears. In exchange the store stays a flat table with trivially correct semantics, and styling owns re-evaluation logic it must own anyway (selector systems re-match on state change regardless of where values live). Read-only properties reject styling writes (`IsReadOnly` ⇒ throw), matching WPF.

### 5.3 To the binding fork (provided)

```csharp
IDisposable Watch<T>(UIProperty<T>, ...);                  // source observation, incl. DataContext
void SetValue<T>(prop, value, priority, owner: expr);      // cell-backed, garbage-free pushes
// UnsetValue          → push "no value", fall through to lower sources
// IValueEvictionListener.OnValueEvicted → binding detach on SetValue(Local) overwrite / ClearValue
void SetCurrentValue<T>(prop, value);                      // two-way target write, source-preserving
T GetValue<T>(prop); object? GetValue(prop);               // source reads
```

I assume the binding fork brings its own path/observation machinery for non-`UIObject` sources (INPC POCOs); the property system is deliberately ignorant of it.

### 5.4 To the animation-orchestration fork (provided + assumptions)

```csharp
void SetValue<T>(prop, clockValue, ValuePriority.Animation);   // cell-backed; equality-gated dispatch
void ClearValue(prop, ValuePriority.Animation);                // FillBehavior.Stop
T GetValue<T>(prop, ValuePriority.Local);                      // base value for handoff snapshots
bool IsAnimated(prop);
```

Assumed shape (per the animation reference): the orchestrator owns `(IAnimation<T>, startTime)` registries and a `TimeProvider`-based clock, samples `ValueAt(now - start)` per frame, and writes through the above. The property system imposes exactly one rule: *the orchestrator is the only writer at Animation priority*, so its clear-on-completion is unambiguous.

---

## 6. Terminal-specific adaptations

Where this deliberately is not WPF or Avalonia, because the substrate is a cell grid driven by one thread at 50 fps:

1. **Animation above Local — with terminal-specific justification.** Both parents put animation on top and we keep that (least surprise), but the terminal argument is stronger than kinship: TUI apps are overwhelmingly **code-driven** — `row.Background = …` in an event handler is the dominant idiom, far more than in XAML-first desktop apps — so "animation loses to any local value" (the CSS-ish alternative) would make transitions silently dead in the most common usage. On top, an animation needs no save/restore of what it covered: `ClearValue(Animation)` uncovers the intact composed base, which is also exactly what makes garbage-free per-frame writes safe (the base is never disturbed). The known WPF annoyance ("can't set a value under a running animation and see it") is mitigated structurally: the base value *does* update underneath and becomes visible the instant the orchestrator clears, and `GetValue(p, maxPriority: Local)` lets the storyboard layer implement snapshot-and-replace handoff cleanly.
2. **The priority ladder is 7 levels, not 11.** WPF's extra rungs (implicit-style, style-trigger vs template-trigger vs style-setter splits, theme-style) exist to arbitrate between *independently authored theme assemblies* — an ecosystem terminals don't have. We keep every distinction with a real client and document the collapse. The 4-bit slot leaves 8 spare levels if one of the cut distinctions ever earns its way back — an additive change.
3. **Effects flags are the bridge to owner-driven coarse invalidation.** The drawing layer's contract is explicit: scenes are memoryless; *the UI layer owns "what changed."* `PropertyEffects.Render → Scene.Invalidate()` per owning widget is precisely that seam, declared once in metadata instead of hand-written in every setter. Whole-scene re-raster granularity also means we need no sub-property damage tracking — a deliberate non-feature.
4. **Frame-coherent synchronous dispatch.** No dispatcher queue, no `DispatcherPriority`, no async property invalidation. The main loop is drain-input → update → render; values set during update are seen by render the same frame. This deletes an entire class of desktop-framework bugs (binding updates racing layout) at zero cost because the platform is single-threaded by decree.
5. **Allocation discipline tuned to 50 fps, not 10⁵ elements.** Cells for animation/binding writers, interned boxes, `in`-passed `readonly struct` change args, generic-virtual typed callbacks, lazy untyped boxing — every steady-state frame with running animations allocates nothing in the property system. Conversely, we *skip* WPF-scale machinery (per-type effective-value caching layers, deferred references, thread verification) that only pays off above ~10⁴ elements.
6. **Inheritance push-down with shared boxes** instead of WPF's lazy read-time walks: at hundreds of elements and < 16 inheritable properties, eager propagation is microseconds, gives change notification for free (which read-time walking cannot), and makes the render-path read O(1).
7. **No deferred-expression objects in the store** (no `DynamicResource` analog at this layer). Theme/resource changes are push-re-evaluated by the styling fork; the worst case — a full theme swap — re-rasters every scene exactly once, which on a cell grid is *one frame*, not a perceptible stall. Terminal capability adaptation (e.g., 256-color themes) likewise lives in styling/theming: the property system stores what it's given; quantization stays where it already lives (`StyleQuantizer` at emit time).
8. **Value vocabulary is cheap structs.** `Color` (8 B), `Rect` (8 B, ushort-backed), `Thickness`/`Margins`, enums, flags — typed accessors mean the render path reads them un-boxed; the `Comparer` metadata hook covers the rare tolerance case. There is no `Freezable` analog: the entire styling vocabulary below us is already immutable records and shareable brushes, so "defaults must be immutable" is a debug assert, not a type system.

---

## 7. Costs, risks, phasing

**Effort estimate.** Core store + registry + metadata ≈ 1.2–1.5 kLOC of dense, highly testable code; the test surface is the real cost (the project's oracle-pinning convention applies: a behavioral table of ~80 scenarios — priority shadowing, clear-reveals, coercion re-entrancy, inheritance pruning/reparenting, eviction, equality gating — each pinned against hand-verified WPF/Avalonia behavior where semantics are shared). Comparable in size to the `StrokeAccumulator`+charts effort already absorbed by this repo.

**Phasing (per the repo's numbered-phase playbook, each sub-phase implemented + tested):**

- **P0 — identity & storage:** registry, `GlobalIndex`, `UIProperty<T>`/metadata/`Register*`, `ValueStore` with Get/Set/Clear at Local only, defaults, frozen metadata tables. *Unblocks nothing downstream yet but is the spine.*
- **P1 — composition & notification:** full priority ladder, `UnsetValue`, change pipeline, `OnPropertyChanged<T>`, watchers, `PropertyEffects` dispatch. *Unblocks styling and widget-invalidation work.*
- **P2 — inheritance:** slots, push-down, reparent diffing, `DataContext`/`Foreground` registrations. *Unblocks binding (DataContext) and theming.*
- **P3 — engine seams:** cells + eviction listeners, `SetCurrentValue`, coercion + `CoerceValue`, read-only keys, `GetValue(maxPriority)`, `IsAnimated`. *Unblocks binding two-way and animation orchestration.*
- **P4 — hardening & perf:** box interning, linear/binary search threshold tuning, debug thread asserts, diagnostics (`GetValueSource`, entry enumeration for DevTools), allocation regression tests (the repo's `CURSORIAL_TRACE_OUTPUT`-style empirical discipline, here as GC-count assertions around animated frames).
- **P5 — source generator** (pure sugar; can ship any time after P1).

**Performance characteristics (claimed, and testable):** `GetValue<T>` ≈ a bounds check + ≤ 4-compare search + unwrap; `SetValue` on an existing cell ≈ search + equality + dispatch, zero alloc; element memory = 0 bytes at all-default, ~300 B heavily styled; full-tree theme swap = O(elements × changed props) synchronous dispatches, well under a frame at terminal scale; 20 concurrently animated properties at 50 fps = 1,000 cell writes/sec — unmeasurable CPU, zero steady-state GC.

**Risks, honestly:**

1. **One-winner-per-priority pushes arbitration complexity into the styling fork.** If that fork wanted store-side specificity frames (Avalonia 11 style), my store is too simple for it. Mitigation: the contract is explicit and early (§5.2); and the fallback is additive — within-priority sub-ordering can be grafted by widening the slot bits (4 spare bits in the key today) without touching public API.
2. **Coercion-as-slot is novel.** The semantics are pinned by tests (base preserved, re-coerce on any base change, `maxPriority` reads skip it), but novelty is where bugs live. Bounded by the re-entrancy guard and by coercion being rare (sliders, selection indices).
3. **`SetCurrentValue` interplay with bindings** is the historically subtle corner in both parents. We constrain it hard (defined in two sentences in §2.4) and hand the residual policy to the binding fork.
4. **Registration-time rigidity** (`Inherits` fixed, `OverrideMetadata` before first instantiation). This trades flexibility nobody sane uses for cache correctness; debug-time exceptions make violations loud.
5. **Generic-virtual `OnPropertyChanged<T>`** has a first-call JIT cost per (type, T) pair. At our type×property counts this is start-up noise; if profiling ever disagrees, a non-generic pre-check ("does this type override at all?") is a contained fix.

**Punted:** per-instance metadata (WPF doesn't really have it either), `IObservable` adapters (30-line add-on when/if wanted), property-changed *batching/transactions* (frame coherence makes it unnecessary until proven otherwise), `Freezable`-style value sealing, multi-thread access of any kind.

---

## 8. Steelman & rebuttal

**Steelman 1 — port WPF's DependencyProperty faithfully.** *The case:* twenty years of hardened edge-case semantics; the full 11-level ladder resolves conflicts (template triggers vs style triggers vs implicit styles) that real composite-control libraries genuinely hit; `SetCurrentValue`, coercion, and value-source diagnostics were each added in response to real pain, and a port inherits the fixes along with the features; developer familiarity is maximal.

*Rebuttal:* the ladder's extra rungs arbitrate between independently authored theme/template assemblies — an ecosystem that does not and will not exist for terminal apps; we keep every rung with a client (style/template/trigger/local/animation) and can re-add the rest additively in the spare slot bits if a real conflict ever materializes. More fundamentally, WPF's *mechanism* is pre-generics-era: `object`-typed metadata callbacks box every change, `DependencyObject` verifies thread access on every read, and `EffectiveValueEntry` complexity exists to serve 100k-row virtualized grids. Porting it means importing costs whose justifications are absent here — and we *do* inherit the fixes, as semantics: `SetCurrentValue`, coercion, `ClearValue`-clears-Local, callback ordering are all preserved behaviorally and pinned by tests.

**Steelman 2 — port Avalonia's AvaloniaProperty with its observable value store.** *The case:* it is the modern, actively maintained reimagining; bindings become nearly free because the store *is* an observable graph (`GetObservable`, `Bind(IObservable)` compose with Rx); `StyledProperty`/`DirectProperty` typing is already generic; priority frames handle multi-style arbitration inside the store so the styling layer stays simple; and it is battle-tested across exactly the styling/selector model this project's requirement 8 names.

*Rebuttal:* Avalonia's own trajectory is the strongest evidence for this proposal. Its value store has been substantially rewritten across 0.10 → 11.0, each time *away from* per-binding observable chains and *toward* flat priority frames with direct effective-value computation — because observable-graph allocation and indirection were the measured cost centers. The hybrid starts where Avalonia's optimization converged, minus the Rx surface its public API is contractually stuck with. Consistency cuts the same way: nothing in Cursorial speaks `IObservable` — input is `IAsyncEnumerable` + direct sinks, animation is pure functions, drawing is immediate calls; an observable property store would be the stack's only Rx island, and the binding fork can be served by typed watchers (and an adapter if it insists) at a fraction of the machinery. The in-store multi-frame arbitration is the one genuine loss, and §5.2/§7-risk-1 answers it: the styling fork owns specificity, which it must compute anyway to know *what matched*; the store stores winners.

**Steelman 3 — no property system at all: plain C# properties + `INotifyPropertyChanged`.** *The case:* radically simpler; zero learning curve; the compiler enforces typing; terminal apps are small enough that "styles just assign properties" might suffice; YAGNI.

*Rebuttal:* requirements 1, 3, 8, and 10 are *value-composition* requirements, and INPC cannot express composition — only mutation. Without priorities, a style applying a value destroys the user's local value; a trigger deactivating cannot restore what it covered; an animation completing cannot reveal what it overrode; every control would hand-roll shadow fields for "the value before the hover style," which is this property system rebuilt badly, per control, without tests. Storage also inverts at scale: hundreds of elements × dozens of declared properties as real fields costs more memory than sparse entry tables where 90% of properties sit at metadata defaults costing zero. And XAML (req 7) plus binding (req 2) need name→typed-property metadata regardless — reflection over CLR properties is the slow, trimming-hostile version of the registry this design builds anyway.

**The honest concession** across all three: this is new code, and both parents' value stores are where their subtlest historical bugs lived. The defense is the project's own established discipline — semantics pinned against the parents as oracles (the repo already does this for easings, Unicode tables, and curve math), a named invariant per layer (here: *one entry per (property, priority); effective value is a pure function of entries + inheritance cache + metadata; dispatch fires iff the effective value changed*), adversarial review before the styling and binding forks build on top, and a deliberately small surface — the entire mechanism is ~1.5 kLOC that one person can audit in an afternoon, which is exactly the property a terminal-scale framework can afford and its desktop ancestors cannot.