# Cursorial.UI — design document

Status: **living design doc** — the canonical reference for the `Cursorial.UI` layer. Authored 2026-06-10 from a multi-agent design program (advocate/judge panels for the three foundational forks, eight adversarially-critiqued subsystem specs, cross-subsystem coherence + completeness passes); the full design-phase artifacts are archived under `docs/ui-layer-design/` (§16). Update this document as decisions change or follow-up work lands.

Cursorial.UI is a WPF/Avalonia-inspired retained-mode UI framework for terminal applications: styled, templated, data-bound widget trees rendered through `Cursorial.Drawing` scenes onto `Cursorial.Rendering` cell buffers, with input, focus, windowing, and animation built on `Cursorial.Core`'s negotiated terminal capabilities.

**The ten requirements** this layer exists to satisfy:

1. Rich styling and templating (similar to WPF and Avalonia)
2. Powerful data binding
3. Resource/style inheritance
4. Logical and physical focus (similar to WPF)
5. Modal and modeless child windows
6. Access keys with underscore indicators that toggle while Alt is held when the Kitty keyboard flags `ReportAllKeysAsEscapeCodes` + `ReportEventTypes` are negotiated (or Win32 input mode is active), and are permanently visible otherwise
7. XAML markup for declarative UI, including the extra plumbing templates need
8. Setters paired with a trigger/selector mechanism (resolved: the §3 hybrid — selector subset + `When` data-conditions)
9. DependencyProperty/AvaloniaProperty-style properties with multiple prioritized value sources and efficient storage (resolved: the §2 Avalonia-style typed chassis)
10. Rich animation support

---

## §0 Invariants (the spine)

Every section below is checked against these. Each names its enforcing mechanism — an invariant that is merely asserted is a bug in this document.

1. **Frame coherence.** A property set during frame N's input drain is visible to frame N's layout and render. No dispatcher priority tiers exist. *Enforced by:* S6's normative frame-phase order (§10.5), the same-tick layout fixpoint (§5), the frozen per-frame `FrameTime` (§9), synchronous binding/styling flushes (§6, §3).
2. **The property and styling engines never touch `Scene`/`CellBuffer`.** They only raise typed change notifications; `PropertyEffects` metadata drives invalidation, routed exclusively by the element tree. *Enforced by:* S1's two-lane effects dispatch as the only bridge (§5.5), plus a DEBUG render-pass read-only guard.
3. **Re-composite vs re-raster.** Offset/opacity/clip-shaped changes carry `AffectsComposite` and refresh `CompositeParameters` on a cached raster; only content/brush-shaped changes carry `AffectsRender` and call `Scene.Invalidate()`. Animated slides, fades, and reveals must never re-raster. *Enforced by:* the metadata flag (§2), S1's boundary-layer parameter refresh (§5.6), S5's routing table + DEBUG perpetual-on-`AffectsMeasure` diagnostic (§9.9).
4. **Retraction is store-owned.** Deactivating a style rule, killing a binding, or completing an animation removes a frame/entry and the store promotes the next value. Nothing ever "sets the old value back." *Enforced by:* Fork A's `ValueStore` frames (§2), Fork B's cookie batch retraction (§3), `TemplateInstance.Detach()` (§12.2), the S2 teardown sweep (§6.5).
5. **Template barrier.** Style rules never match elements with `TemplatedParent != null` except through the `/template/` combinator; the engine skips such elements before candidate scanning. *Enforced by:* S8's stamp walk + foreign-parent throw (§12.2), Fork B's matcher skip (§3), the namescope guard (§6.3).
6. **Single UI thread.** One dedicated thread owns dispatch, layout, and render; the input pump and background work marshal onto it via `UIDispatcher`; `UIObject` has thread affinity, debug-asserted. *Enforced by:* S6's thread ownership hand-off and `VerifyAccess` (§10.3).
7. **`Cursorial.Core` accepts only additive changes; the unshipped layers accept first-class improvements.** (Amended 2026-06-11: only Core has shipped. Rendering, Drawing, and Animation are cleared for non-additive improvements when this layer wants them — prefer fixing the lower layer over a UI-side workaround; record each change in the owning layer's design doc.) Core changes still land as additive members or opaque seams, recorded per case. *Current ledger:* one Core seam (`EmergencyRestoreBytes`, §10.7), additive `Cursorial.Animation` combinators (§9.11), one additive Drawing interpolator (`MarginsInterpolator`, §9.11). *Cleared lower-layer improvements:* **landed (P2.5 ①–③):** Drawing push-stack full coverage — clip/translate honored by `DrawFormattedText`/`DrawContent`/deferred strokes/braille/shadows/titled boxes — and the `RenderContext` self-translation deleted in its favor (§5); public read-only `Scene.RasterVersion` (the test `InternalsVisibleTo` into Drawing is dropped); `ScenePool` size-bucketing (exact-dimension buckets, LRU retention cap). **Landed (P8):** `Cursorial.Animation` combinators + Elastic/Bounce/`CubicBezier` easings + `Easings.TryParse`; the signed `MarginsInterpolator` (+ Point/Size/Rect/RelativePoint/CompositeParameters/Brush/Pen interpolators) registered with `Interpolator` via a `[ModuleInitializer]`.

---

## §1 Architecture overview

### §1.1 Layering

```
Cursorial.UI            widget tree, styling, binding, XAML, focus, windows, animation orchestration   ← this layer
        │   (+ Cursorial.UI.Xaml — runtime loader;  Cursorial.UI.Xaml.Generator — build-time tooling)
Cursorial.Drawing       Scene, SceneCompositor, DrawingContext, IBrush, Pen        (cached rasters, compositing)
        │                        Cursorial.Animation (pure elapsed→value; UI owns the clock)
Cursorial.Rendering     CellBuffer, FrameRenderer, rich text, fragments            (diff rendering to bytes)
        │
Cursorial.Core          TerminalSession, input events, capabilities, output writers
```

The render pipeline per frame: retained widget tree → per-zone cached `Scene`s (§5.5) → window-manager layer assembly (§8) → `SceneCompositor` onto the retained screen `CellBuffer` → `FrameRenderer` diff → one buffered write + flush (§10.5). The input pipeline: `TerminalSession.Input` (single-shot) → S6's pump → UI-thread queue → S3 routed events → controls. Idle frames cost nothing at every tier (clean scenes, compositor change detection, renderer diff).

### §1.2 Canonical subsystem map (reading guide)

| § | Label | Owns |
|---|---|---|
| §2 | Fork A | Property system: `UIProperty`/`StyledProperty<T>`, `UIObject`, `ValueStore`, priorities, metadata |
| §3 | Fork B | Styling: `Style`/`Selector`/`When`, sort keys, pseudo-classes, activation engine |
| §4 | Fork C | XAML: runtime loader, markup extensions, deferred templates, generator roadmap |
| §5 | S1 | Element tree, layout, render zones, hit testing, caret service |
| §6 | S2 | Data binding engine, namescopes/`FindName`, teardown sweep |
| §7 | S3 | Input routing, focus (physical + logical scopes), access keys, commands |
| §8 | S4 | Windows, modality, popups, the window manager, chrome behavior |
| §9 | S5 | Animation orchestration: frame clock, storyboards, transitions, `UITimer` |
| §10 | S6 | `UIApplication`, dispatcher, the frame loop, headless test host |
| §11 | S7 | Resources, themes, variants, dynamic resolution |
| §12 | S8 | Control infrastructure (templates, presenters, items) + the v1 catalog |

Cross-references throughout use these labels. §13 records resolved decisions, §14 the phase plan, §15 global deferrals, §16 the archived design artifacts.

### §1.3 Vocabulary and naming

- Class hierarchy: `UIObject` (property host) → `UIElement` (tree/layout/render/input node) → `Control` (templated) → `ContentControl` → `Window`. The acronym **UI is fully capitalized in type names** (`UIElement`, `UIProperty`, `UIApplication`, `NotUIInput`).
- **Namespace scheme (pinned 2026-06-11, WPF/Avalonia kinship):** `Cursorial.UI` — the core: `UIObject`/`UIElement`, the property system, element-level layout primitives/enums (`Visibility`, the alignments — WPF's `System.Windows` set), render integration, hosting (the `System.Windows` analog). **`Cursorial.UI.Controls`** — `Panel` and the panels, presenters (`ScrollContentPresenter`, later `ContentPresenter`/`ItemsPresenter`), every control from `Control` down, **and the panel/control-facing enums `Orientation` + `Dock`** (WPF kinship: both live in `System.Windows.Controls`; relocated with the P2.5 ⑤ move) — the `System.Windows.Controls` analog; P1 shipped all of these in the root namespace — the move is a P2.5 mechanical refactor. `UIElementCollection` stays in `Cursorial.UI` beside `UIElement` (deliberate deviation from WPF's placement). `Cursorial.UI.Input` — S3's routed events, dispatcher, focus, gestures (§7). `Cursorial.UI.Xaml` — the Fork C loader assembly. Styling/binding/animation namespaces are pinned by their phases against this scheme.
- **Name collision, resolved up front:** `Cursorial.Output.Style` is the cell-level SGR record; the UI styling object is `Cursorial.UI.Style`. Framework source disambiguates with `using CellStyle = Cursorial.Output.Style;` — mirroring how WPF lives with `System.Drawing.Color` vs `System.Windows.Media.Color`.
- Drawing-layer terminology (scene, composite vs re-raster, brush, evict/occlude, `Column`/`Row` for integer cell addresses) carries over unchanged from `docs/drawing-layer-design.md` §-vocabulary; this layer adds: **zone / render boundary** (the element subtree sharing one `Scene`, §5.5), **frame** (a `ValueFrame` of property values at a priority, §2 — disambiguate from render frames by context), **armed / active** (a style rule structurally matched / with all conditions met, §3), **surface** (a top-level compositor participant: window or popup, §8).
- Layout is **integer cell coordinates** end to end. `Size`/`Rect`/`Margins` come from `Cursorial.Rendering` (`Rect` is ushort-backed and non-negative — signed math uses dedicated carriers; arranged `Bounds` is the signed `LayoutRect` since P2.6 because signed margins may pull an origin negative; *animated* placement still rides composite offsets, §5.2).

---

## §2 Fork A — the property system

The property engine is an Avalonia-shaped typed chassis: `StyledProperty<T>` / `AttachedProperty<T>` / `DirectProperty<TOwner,T>` over a per-instance `ValueStore` of priority frames, hosted on `UIObject`. Three commitments drive everything: **typed end-to-end, zero boxing on hot paths** (the styling vocabulary — `Color`, `Pen`, `Rect`, `Margins` — is `readonly record struct` all the way down); **priority is a property of the write, not the writer** (bindings, styles, templates, and animations are all value producers converging on one arbitration algorithm); **restoration is store-owned** (invariant 4: deactivation = frame/entry removal + promotion of the next source, never "set the old value back"). Namespace `Cursorial.UI`. Scale target: hundreds of elements, 20–60 fps, allocation-free steady state.

### §2.1 Object model & public API (condensed)

```csharp
public abstract class UIProperty
{
    public static readonly object UnsetValue;          // "this source contributes nothing"
    public int Id { get; }                             // dense, process-global; −1 = unregistered sentinel (A14:
                                                       //   internal ctor backs static UnsetTargetProperty for S2 watch-only expressions)
    public string Name { get; }  public Type PropertyType { get; }  public Type OwnerType { get; }
    public bool Inherits { get; }                      // fixed at registration — not per-type-overridable (§2.6)
    public bool IsAttached { get; }  public bool IsDirect { get; }
    internal PropertyEffects GlobalEffects;            // A1: global lane, writable only during the registration window
    public PropertyEffects GetEffects(Type forType);   // A1: perTypeTable[denseId] | GlobalEffects
    internal virtual BindingEntryBase CreateEntry(UIObject target, /*…*/);                // A16: untyped→typed bridge,
    internal virtual BindingEntryBase CreateTemplateTransfer(                             //   overridden by StyledProperty<T>;
        UIObject templatedParent, UIObject target, IValueEvictionListener? listener);     //   TemplateBinding fast path, no reflection

    public static StyledProperty<T> Register<TOwner, T>(string name, T defaultValue = default!,
        bool inherits = false, Func<UIObject, T, T>? coerce = null, Func<T, bool>? validate = null,
        PropertyChangedCallback<T>? changed = null) where TOwner : UIObject;   // NO effects parameter (A2)
    public static AttachedProperty<T> RegisterAttached<TOwner, THost, T>(/* same shape */) where THost : UIObject;
    public static DirectProperty<TOwner, T> RegisterDirect<TOwner, T>(string name, Func<TOwner, T> getter,
        Action<TOwner, T>? setter = null, T unsetValue = default!) where TOwner : UIObject;
}

public class StyledProperty<T> : UIProperty
{
    public PropertyMetadata<T> GetMetadata(Type forType);                // merged + cached per concrete type
    public void OverrideMetadata<TOwner>(PropertyMetadata<T> m);         // THROWS once any TOwner instance touched the property
    public void OverrideDefaultValue<TOwner>(T defaultValue);            // sugar (S3's `OverrideDefault<T>` = this, name drift only)
    public StyledProperty<T> AddOwner<TOwner>();                         // registers (TOwner, Name) for XAML lookup
}
public sealed class AttachedProperty<T> : StyledProperty<T> { public Type HostType { get; } }
public sealed class UIPropertyKey<T>          // structural read-only: registration returns the key; only key-holders write
{ public StyledProperty<T> Property { get; } }
public sealed record PropertyMetadata<T>(T DefaultValue = default!, Func<UIObject, T, T>? Coerce = null,
    Func<T, bool>? Validate = null, PropertyChangedCallback<T>? Changed = null, IEqualityComparer<T>? Comparer = null);

[Flags] public enum PropertyEffects { AffectsMeasure, AffectsArrange, AffectsRender, AffectsComposite,
    AffectsParentMeasure, AffectsParentArrange, Inherits, BindsTwoWayByDefault, NotDataBindable }
```

`AffectsComposite` is mandatory: offset/opacity/clip-shaped properties route to CompositeParameters refresh (cached raster reused); only content/brush-shaped changes route to re-raster (invariant 3). The engine never references scenes or rendering — S1 builds the `Affects*<TOwner>(params UIProperty[])` sugar statics on the metadata `Changed` channel, writing **both** effects lanes pre-freeze (invariant 2). The global lane is what makes attached properties work: host types' frozen per-type tables structurally never see the declaring panel's registration, so without it `Grid.SetRow(button, 2)` would invalidate nothing.

Per-type metadata flag `ParsesAccessKeyLiterals` (A21) is set via metadata override on exactly `ButtonBase.ContentProperty`, `MenuItem.HeaderProperty`, `TabItem.HeaderProperty`, `Label.ContentProperty`; resolved against the instance's **runtime type**; consumed by Fork C's parse-time AccessText folding and runtime `GetAccessText()`.

```csharp
public abstract class UIObject
{
    protected UIObject();                       // A25: captures UIApplication.Current!.Dispatcher (thread-local; non-null
    public UIDispatcher Dispatcher { get; }     //   wherever construction is legal); debug affinity = Dispatcher.VerifyAccess()

    public T GetValue<T>(StyledProperty<T> p);                              // hot path; never boxes
    public T GetValue<T>(StyledProperty<T> p, BindingPriority maxPriority);
    public T GetBaseValue<T>(StyledProperty<T> p);                          // effective value IGNORING Animation (handoff snapshot)
    public object? GetValue(UIProperty p);                                  // untyped lane; box-interning cache
    public bool IsSet(UIProperty p);                                        // A23: guards S8 auto-aliasing
    public ValueSource GetValueSource(UIProperty p);                        // diagnostics; + frame/local enumeration for tooling
    public void SetValue<T>(StyledProperty<T> p, T value, BindingPriority priority = BindingPriority.LocalValue);
    public void SetValue(UIProperty p, object? value, BindingPriority priority = BindingPriority.LocalValue);
    public void SetValue<T>(UIPropertyKey<T> key, T value);
    public void SetCurrentValue<T>(StyledProperty<T> p, T value);           // §2.2
    public void ClearValue(UIProperty p);                                   // A9: removes local value AND evicts local bindings
    public void CoerceValue(UIProperty p);
    public IDisposable DeferNotifications();                                // A23: template apply / container prepare / DataContext swap

    public IDisposable AddObserver<T>(StyledProperty<T> p, IValueObserver<T> o);
    public IDisposable AddObserver<T>(StyledProperty<T> p, IValueObserver<T> o, ObserverOptions options);  // A20
    public IDisposable AddObserver(UIProperty p, IUntypedValueObserver o);                                 // A10
    public BindingEntry<T>  Bind<T>(StyledProperty<T> p, BindingPriority pr, IValueEvictionListener? l);   // A6: LocalValue ONLY
    public BindingEntryBase BindUntyped(UIProperty p, BindingPriority pr, IValueEvictionListener? l);      //   (Style/Default throw)
    public BindingEntry<T>  BindInFrame<T>(StyledProperty<T> p, ValueFrame hostFrame, IValueEvictionListener? l);  // A5
    public BindingEntryBase BindInFrameUntyped(UIProperty p, ValueFrame hostFrame, IValueEvictionListener? l);
    public AnimatedValueHandle<T> BeginAnimation<T>(StyledProperty<T> p);
    public void AddFrame(ValueFrame f);  public void RemoveFrame(ValueFrame f);
    public void SetInheritanceParent(UIObject? parent);                     // S1 calls on attach/detach/reparent
    internal object? BindingHostState;                                      // A17: reserved slot for S2's expression registry

    protected bool SetAndRaise<TOwner, T>(DirectProperty<TOwner, T> p, ref T field, T value);
    protected virtual void OnPropertyChanged(in UIPropertyChangedEventArgs args);
    protected internal virtual void OnInheritedPropertyChanged(in InheritedPropertyChangedEventArgs args);  // A3
}
```

Change carriers are **copied-value `readonly struct`s passed by `in`** — values are snapshotted out of the store entry before dispatch (a ref struct over live entries corrupts under reentrancy; rejected). `UIPropertyChangedEventArgs { UIProperty Property; BindingPriority Priority; T GetOldValue<T>(); T GetNewValue<T>() }`; `InheritedPropertyChangedEventArgs` is the second small carrier for entry-less descendants — `UIElement` overrides `OnInheritedPropertyChanged` to run the same effects dispatch there. Zero-box CLR wrappers (`get => GetValue(FooProperty); set => SetValue(FooProperty, value);`) are the consumer idiom.

### §2.2 Priority, sort keys, precedence

```csharp
public enum BindingPriority
{
    Animation  = -100,  // above local: trigger-driven pulses must beat the value they animate; restoration falls out free
    LocalValue =    0,  // SetValue / local {Binding}
    Style      =  100,  // SINGLE slot; within-slot order = Fork B's packed StyleSortKey carried by frames
                        //   (layer field encodes ControlTheme/Theme/Template/app provenance — template-local included)
    Template   =  150,  // §20/PD24: values a control/data template AUTHORS on its parts (literal SetValue,
                        //   {TemplateBinding}/{Binding}, SetResourceReference) — below Style so a page/theme
                        //   Style overrides a template default; reached only via the template-instantiation scope
    Inherited  =  200,  // resolution-only: walk-up result; never assignable
    Default    =  300,  // resolution-only: per-type metadata default
    Unset      = int.MaxValue,
}
```

- **Binding is not a priority.** A `BindingEntry<T>` contributes at the priority it was installed at; within one priority, last writer wins and a binding's push counts as a write.
- **One Style slot.** All style/trigger/template contributions are frames in the single `Style` slot, ordered by `StyleSortKey` (Fork B owns key construction; layer beats specificity per DECISIONS). The store sorts frames by the key and arbitrates; it never evaluates selectors.
- **The Template lane** (`BindingPriority.Template`, wire 150 — added 2026-06-16, precedence matrix §20/PD24) is the engine-internal lane for everything a control/data template *authors on its parts*: a literal `SetValue`, a `{TemplateBinding}`/`{Binding}`, a `SetResourceReference`. It sits one rung **below** `Style` (so a page/theme Style overrides a template default — the deliberate inverse of WPF, where template-property values outrank Style) and **above** `Inherited`. It is **not** a `SetValue` priority argument (PD1 stands); a thread-static **template-instantiation scope** open while a template's content tree is built (`ControlTemplate.Instantiate` / `DataTemplate.Build`) reroutes the ordinary local-lane producers to it. **Do not confuse it with Fork B's `StyleLayer.Template`** (§3.4): that is a *sort-key layer within the Style slot* for style rules declared in a control template's `Styles` — those are still `Style`-priority and therefore *beat* a Template-lane part value. (A `BindingEntry<T>` installed inside the scope captures the lane at install — A6 now accepts `LocalValue` or `Template`.)
- **`SetCurrentValue<T>`** (verbatim P3 graft): replace the effective value in place without changing its source; no entry ⇒ behaves as Local. Observer args carry the priority of the lane whose value was replaced — `Animation` while an animation holds the property, else the base lane's priority (A11); provenance unchanged. Non-echo `SetValue` **and** `SetCurrentValue` writes feed TwoWay write-back (A12 — `Popup.IsOpen` dismissal depends on it); jointly: a mid-animation `SetCurrentValue` on a two-way-bound property never reaches the source, because S2 filters Animation-priority args (pinned as one oracle row, §2.5).
- **`ClearValue` is the binding kill** (A9): removes the local value and evicts local-priority binding entries; `SetValue` is a transient override that never kills a binding (coexistence model).
- **`GetBaseValue<T>`** is the storyboard handoff snapshot; `Animation`-above-local means the base keeps living underneath and disposing the handle resurfaces it with one notification.

### §2.3 Storage, resolution, loading

`UIObject` holds one nullable `ValueStore?` field, allocated on first write/observe/frame — a default-valued element costs zero property-system bytes. The store: a **sparse effective-value table** (sorted `int[]` ids + parallel entries, binary search, n ≤ ~32), a frame list sorted by priority/`StyleSortKey`, copy-on-write observer arrays, the inheritance parent, and a defer-depth + pending-change list. The per-property `EffectiveValue<T>` entry carries `Value`/`LocalValue`/`AnimatedValue` slots plus `EffectivePriority`/`BasePriority` flags — the effective/base two-slot split (no per-priority array); entries mutate in place: one allocation per `(instance, property)` that ever leaves default, ever.

- **Resolution:** local → active frames (StyleSortKey order, later/stronger first) → inherited walk (if `Inherits`) → per-runtime-type metadata default. Frame entries with `HasValue == false` are skipped (unset promotion).
- **Write fast paths:** local `SetValue` with no animation = slot write + `SetEffective`, no frame scan; `SetValue` under animation updates base only, no notification; `AnimatedValueHandle.SetValue` = in-place mutate + equality short-circuit (cell-quantized no-op frames produce zero downstream work); `ClearValue`/deactivation/`SetUnset` = rescan of affected ids only.
- **Notification order**, synchronous: metadata `Changed` (invalidation channel) → typed observers → untyped observers → virtual `OnPropertyChanged`. All observer change-args carry `BindingPriority` (A10). Reentrancy is legal; equality short-circuit is the cycle breaker (+ debug depth assert). `DeferNotifications()` coalesces per property (first old, last new).
- **Inheritance:** lazy-read (walk `_inheritanceParent` to nearest contributing ancestor; ancestor's *effective* — including animated — value; misses skip coercion), eager-notify (recurse inheritance children, stopping at shadowing subtrees; entry-less descendants get `OnInheritedPropertyChanged` (A3) **and** observer notification (A4 — DataContext rebind depends on it); inherited changes also ride the A20 winning-base seam). Push-down shared-box O(1) reads are the documented API-compatible upgrade, benchmark-gated.
- **Metadata:** per-type frozen tables; `GetMetadata` merges down the runtime-type chain (`Changed` chains base-first; others nearest-wins) with a monomorphic inline cache; `OverrideMetadata` throws after first touch — no cache invalidation problem class exists.
- **Validation at the mouth:** local `SetValue` throws on invalid; binding/frame-produced invalid values are discarded with `UIDiagnostics.OnRejectedValue`, keeping the previous value.
- **Registry:** append-only `UIProperty[]` by dense `Id`; `(Type, string)` lookup for XAML; `FindOwnersByShortName(string) → IReadOnlyList<Type>` with ambiguity detection (A15 — backs `(Grid.Row)` path resolution); cached `InheritingPropertyIds`; the XAML loader forces static ctors before name lookup.
- **Direct properties:** plain field + `SetAndRaise`; no store, no coercion, no styling, no inheritance, no `PropertyEffects` routing (consumers hand-route), no animation lane (A24). Composite-routed animatable state must be styled properties — ScrollViewer offsets are two-way mirrors of `ScrollContentPresenter`'s styled `AffectsComposite` properties. Effective-`IsEnabled` is S1's, on its inheritance wiring (`IsEnabledProperty` + `InvalidateIsEnabledCore()`); no store work.
- **Teardown:** `ValueStore.TearDown()` (A13) evicts every entry on the instance — free-standing and frame-hosted — firing `OnEvicted` per entry; S1 calls it bottom-up on permanent detach, then `BindingOperations.TearDown(element)`. Load-bearing for the strong-INPC no-leak claim.

### §2.4 Cross-engine seams

```csharp
public abstract class ValueFrame                       // Fork B / S8 subclass; per-element shim over shared immutable setters
{
    public StyleSortKey SortKey { get; }  public bool IsActive { get; }
    protected void SetActive(bool active);             // store recomputes affected ids; retraction = removal + promotion
    protected void OnEntryChanged(IValueEntry entry);  // in-place value re-emit (resource pulses) — never remove/re-add
    public abstract int EntryCount { get; }  public abstract IValueEntry GetEntry(int index);
}
public interface IValueEntry { UIProperty Property { get; } bool HasValue { get; } }   // !HasValue ⇒ skip to next source
public interface IValueEntry<T> : IValueEntry { T GetValue(); }

public abstract class BindingEntryBase : IDisposable   // A7
{
    public UIProperty Property { get; }  public BindingPriority Priority { get; }  public bool HasValue { get; }
    public void SetValue(object? value);  public void SetUnset();
    public void Dispose();                             // idempotent; legal re-entrantly from within OnEvicted
}
public sealed class BindingEntry<T> : BindingEntryBase { public void SetValue(T value); }
public interface IValueEvictionListener { void OnEvicted(BindingEntryBase entry); }    // taken at install

public sealed class AnimatedValueHandle<T> : IDisposable
{
    public bool IsDetached { get; }       // A19: SetValue after detach = silent no-op; Dispose idempotent
    public bool SetValue(T value);        // A18: returns "effective value actually changed" — feeds S6's dirty signal
    public void Dispose();                // base resurfaces with one notification; last-started handle wins
}
```

- **Frame-hosted producers (A5):** `BindInFrame` entries live in the host frame, participate in within-slot `StyleSortKey`/template provenance, and are evicted (firing `OnEvicted`) on frame removal, cookie retraction, or `TemplateInstance.Detach()` — Fork B passes real `ValueFrame`s for binding-valued setters. Free-standing `Bind` is LocalValue-only (A6); Animation is `AnimatedValueHandle<T>` territory.
- **Entry-holds-unset (A8):** new entries are valueless until first `SetValue` (OneWayToSource relies on it); an entry fed `UIProperty.UnsetValue` reports `HasValue = false` and the store promotes — never null/default-clobber. LocalValue resource producers additionally get in-place set/unset, equality short-circuit, and eviction-listener displacement notification (drives S7's `SetResourceReference` cleanup).
- **Winning-base observer (A20):** `AddObserver<T>(p, o, new ObserverOptions { IncludeBaseChanges = true })` fires **only** when the effective base — the winner among sub-Animation priorities — changes, delivering `(oldEffectiveBase, newEffectiveBase, bool isAnimated)`. The store detects winning-base changes *under an active Animation entry* (the one sanctioned exception to "no notification on base write under animation"), and inherited changes ride the same seam with the same shape. S5's transition retargeting is blind without it; gates S5 Phase A3.
- **Auto-alias observer channel (A22):** the alias channel is plain `AddObserver` — no new type — with two pinned guarantees: subscribing on another `UIObject` requires no binding or frame, and creates **no store entry** on the observing side (ContentPresenter's read-through fallback likewise creates none on the presenter). Lifetime = template instance, torn down in `Detach()`.
- **Template seam:** `ITemplateContent`/`TemplateInstance` are Fork B/S8 types; the engine's legs are `CreateTemplateTransfer` (A16), frame-hosted-entry eviction on `Detach()`, and `DeferNotifications` around template apply. `IDeferredResourceEntry` is Fork C/S7's seam; the engine sees only the resulting entries/frames.

### §2.5 Mandated test program

- **Oracle-pinned precedence matrix, authored before the engine.** Rows = source combinations (local / frames at varying sort keys / animation / inherited / default) × operations (set, clear, frame add/remove/activate, `SetUnset`, `SetCurrentValue`, handle dispose) → expected effective value, notification priority, and observer deliveries. Includes the **joint A11×A12 row**: mid-animation `SetCurrentValue` on a TwoWay-bound property ⇒ args carry `Animation` ⇒ source untouched; same operation un-animated ⇒ writes through (gates S4's W3 and S2's recorded divergence; pinned fallback if the store cannot honor write-through: Popup writes `SetValue(LocalValue)` through the binding).
- **`ValueFrame` conformance kit** consumed by Fork B: activation/retraction promotion, `OnEntryChanged` in-place re-emit, within-slot `StyleSortKey` ordering, entry-unset promotion, retraction-is-not-set-back.
- **Eviction/death-edge rows** (S2's table): frame removal, cookie retraction, `TemplateInstance.Detach()`, `ClearValue`, `TearDown` — each fires `OnEvicted` exactly once; re-entrant `Dispose` from `OnEvicted` legal; `TearDown` sweeps free-standing and frame-hosted alike.
- **A20 matrix:** winning-base change detection under an active Animation entry; inherited base changes through the same seam.
- **A22 countersignature rows:** observer-on-another-object creates no store entry; presenter read-through creates none.
- **Inheritance matrix:** shadowing stops, reparent diff over `InheritingPropertyIds`, entry-less descendant delivery (A3/A4) including DataContext rebind.
- **Allocation assertions:** steady-state animation write and `GetValue` hot path allocate nothing (repo norm: deterministic, oracle-pinned tables).

### §2.6 Terminal-specific adaptations

1. **No `IObservable`/Rx** — typed `IValueObserver<T>` arrays serve the two real consumers (binding, selectors) with zero subscription ceremony.
2. **Copied-value `in`-struct change args** where Avalonia allocates an args class per change — steady-state zero versus constant gen0 drizzle at 50 fps on value-typed properties.
3. **`Inherits` fixed at registration** — keeps the inheriting set globally enumerable (reparent diff) and kills the inheritance-cache-invalidation protocol.
4. **Lazy-read inheritance** suits shallow (~8–12 deep), wide terminal trees: zero cache memory; eager-notify preserves selector/invalidation correctness.
5. **Sorted arrays over hashtables** (effective table, frames, observers): at n ≤ 32, faster and deterministic — reproducible test assertions.
6. **Coercion as the cell-grid guardrail:** registration-site coercers clamp to grid constraints (`Rect` is ushort-backed and throws on negatives; sizes ≥ 0; `Grid.Row ≥ 0`) so an overshooting easing or bad binding can't detonate a constructor three layers down.
7. **Single thread by contract:** zero synchronization; dispatcher captured at construction (A25), `Dispatcher.VerifyAccess()` debug assert (invariant 6).
8. **No data-validation plumbing in v1:** `Validate` + `UIDiagnostics.OnRejectedValue` cover integrity without `BindingNotification` weight.

### §2.7 Rejected alternatives

- **Boxed-`object` storage (WPF `DependencyProperty` chassis)** — boxes the record-struct styling vocabulary on every store/notify; per-frame garbage by construction.
- **One-winner-per-priority slots** — the single Style slot needs coexisting frame contributions ordered by `StyleSortKey`.
- **Coercion as a priority slot** — coercion happens inside effective-value computation, not on the ladder.
- **Any `IObservable` surface** — subscription + closure allocation per use; drags an Rx contract through the codebase.
- **WPF's 10-bucket priority ladder** — unused at terminal scale; cut rungs recorded as re-addable. The first re-add landed 2026-06-16: the **Template lane** (`BindingPriority.Template`, §2.2/§20) — but **weaker** than Style, not stronger (WPF puts template values above style); see §20's header for why.
- **Ref-struct change args over live store entries** — reentrancy corrupts the entry mid-dispatch; replaced by copied-value carriers.
- **Effects parameter on `Register`** — only the `Affects*<TOwner>` sugar + `GlobalEffects` lane handles attached properties; the parameter shape cannot.
- **POCO + INPC (no property system)** — value restoration, priority arbitration, and inheritance get re-derived ad hoc per control, three bad stores instead of one.

### §2.8 Engine amendment ledger

Deltas the subsystem chapters impose beyond the canonical proposal as amended by DECISIONS; *confirmation* rows add no delta but are countersigned for the oracle matrix.

| # | Amendment | By | Shape (exact member/behavior) |
|---|---|---|---|
| A1 | Two-lane effects metadata | S1 | `internal PropertyEffects UIProperty.GlobalEffects` + `GetEffects(Type) ⇒ perType \| Global`; global lane mandatory for attached properties |
| A2 | Effects authoring surface | S1 | S1's `Affects*<TOwner>` sugar statics write both lanes pre-freeze; no `Register` signature change |
| A3 | Inherited-change virtual | S1 | `UIObject.OnInheritedPropertyChanged(in InheritedPropertyChangedEventArgs)`; `UIElement` overrides for entry-less-descendant effects dispatch |
| A4 | Inherited delivery on observers | S2 | `AddObserver` fires on inherited changes on entry-less descendants (DataContext rebind) |
| A5 | Frame-hosted producer entries | S2 | `BindInFrame<T>(StyledProperty<T>, ValueFrame, IValueEvictionListener?)` + untyped; evicted on frame removal / cookie retraction / `TemplateInstance.Detach()` |
| A6 | Free-standing `Bind` restricted | S2 | `Bind`/`BindUntyped` accept LocalValue or **Template** (the §20/PD24 in-template install path, added 2026-06-16); Style-slot contributions must be frame-hosted |
| A7 | Entry base + re-entrant dispose | S2 | `BindingEntryBase { Property; Priority; SetValue; SetUnset; Dispose }`; `Dispose` idempotent, legal from `OnEvicted` |
| A8 | Entry-holds-unset | S2, S7 | Entries valueless until first `SetValue`; `UnsetValue` ⇒ `HasValue=false` ⇒ promotion; resource producers get in-place set/unset + displacement notification |
| A9 | `ClearValue` is the binding kill | S2 | Removes local value **and** evicts local-priority entries; `SetValue` never kills |
| A10 | Untyped observer lane | S2 | `AddObserver(UIProperty, IUntypedValueObserver)`; all observer args carry `BindingPriority` |
| A11 | `SetCurrentValue` observer-args priority | S2 | Args carry the replaced lane's priority — `Animation` under animation, else base lane |
| A12 | `SetCurrentValue` two-way write-through | S4, S2 | Non-echo `SetValue` and `SetCurrentValue` feed TwoWay write-back; joint A11×A12 oracle row; fallback: Popup writes `SetValue(LocalValue)` through the binding |
| A13 | Teardown sweep | S2 (S1 calls) | `ValueStore.TearDown()` evicts every entry, firing `OnEvicted`; bottom-up on permanent detach, then `BindingOperations.TearDown` |
| A14 | Sentinel ctor | S2 | Internal `UIProperty` ctor (Id −1) backing `UnsetTargetProperty` |
| A15 | Short-name registry query | S2 | `FindOwnersByShortName(string) → IReadOnlyList<Type>` with ambiguity detection |
| A16 | Untyped→typed bridge | S2 | Internal virtuals `CreateEntry` / `CreateTemplateTransfer` overridden by `StyledProperty<T>`; no reflection |
| A17 | Opaque host slot | S2 | `internal object? UIObject.BindingHostState` |
| A18 | `AnimatedValueHandle<T>.SetValue → bool` | S5 | Returns "effective value actually changed"; feeds S6's dirty signal |
| A19 | Handle detach surface | S5 | `IsDetached`; post-detach `SetValue` no-op; `Dispose` idempotent |
| A20 | Winning-base observer | S5 | `AddObserver<T>(p, o, ObserverOptions{IncludeBaseChanges})` → `(oldBase, newBase, isAnimated)`; detected under active Animation entries; inherited rides the same seam. **Not small**; gates S5 Phase A3 |
| A21 | `ParsesAccessKeyLiterals` flag | S8 (Fork C consumes) | Metadata override on exactly the four named properties; runtime-type-resolved |
| A22 | Auto-alias observer channel | S8 | *Confirmation* + naming settled: plain `AddObserver`, pinned no-store-entry guarantee on the observing side; lifetime = template instance |
| A23 | `IsSet` / `DeferNotifications` | S8, S2 | *Confirmations*; pinned usage: `IsSet` guards auto-aliasing; `DeferNotifications` wraps template apply / container prepare / DataContext swap |
| A24 | Direct-property confirmations | S8 | *Confirmation*: no `PropertyEffects` routing, no animation lane; composite-routed animatable state must be styled (ScrollViewer offsets = two-way mirrors of SCP's styled properties) |
| A25 | Dispatcher capture at construction | S6 | `UIObject` captures `UIApplication.Current!.Dispatcher`; debug assert = `VerifyAccess()` |
| A26 | SCV graft yields to an arriving style producer | Fork B (P3) | The M118 no-contribution graft stores `LocalIsCurrentValueOnly`; `Reevaluate` lets a style contribution replace it (one `notify(old→style, Style)`, the graft evaporates) instead of holding the local lane — A11's "a producer change replaces the overlay" extended to producers that *arrive* (style matrix S100). A real `SetValue` (or SCV over a real local) clears the flag; no prior M-row pinned the SCV-then-style-activation case |

Recorded ownership notes: effective-`IsEnabled` is S1's (no store work); `OverrideDefault<T>` in older text = `OverrideDefaultValue<TOwner>`.

### §2.9 Deferred (carry forward)

- **Push-down shared-box O(1) inherited reads** — API-compatible upgrade to lazy-read v1; benchmark-gated.
- **Per-type `Inherits` override** — frozen-at-registration keeps the reparent diff enumerable; no consumer yet.
- **Weak observers** — `IDisposable` discipline + the A13 teardown sweep cover v1; revisit if leak reports surface.
- **Data validation (`INotifyDataErrorInfo` channel)** — terminal forms are a later concern; `Validate` + diagnostics hook suffice.
- **Property-change tracing / dev-tools hooks** beyond `GetValueSource` + frame/local enumeration — DevTools-era work.
- **Fat-struct slot split** (side allocation for fat `T` like `Style`) — internal, profiling-gated; no API impact.
- **Cut WPF ladder rungs** — recorded as re-addable if a consumer materializes. The **Template lane** materialized 2026-06-16 (§2.2/§20); the remaining WPF rungs (e.g. a separate trigger lane) stay cut — `Style.When` covers triggers within the Style slot.

---

## §3 Fork B — the styling system

> Decision: **hybrid** — a deliberately small CSS-style selector subset as the single *structural* activation mechanism, plus `When` data-conditions (the DataTrigger equivalent) as the single *non-structural* one. A style is active on an element iff **(structural selector matches) ∧ (all required pseudo-classes set) ∧ (all `When` conditions true)**. No `Trigger`s, no trigger priority slots, no property-value selectors. One predicate, one `BindingPriority.Style` slot, one packed sort key, one cookie-based retraction path. Canonical proposal: `proposal-styling-hybrid.md`, amended per the ledger in §3.11.

Name collision resolved up front: the UI styling object is `Cursorial.UI.Style`; framework source refers to the SGR record via `using CellStyle = Cursorial.Output.Style;`.

### §3.1 Object model and grammar (condensed)

```csharp
public sealed class Style
{
    public Style(); public Style(Selector selector); public Style(string selector, ISelectorTypeResolver? resolver = null);
    public Selector? Selector { get; init; }
    public Style? BasedOn { get; init; }            // flattened at Seal; cycle ⇒ InvalidOperationException
    public SetterCollection Setters { get; }
    public WhenCollection When { get; }             // conjunction of DataConditions; empty = always
    public StyleCollection Children { get; }        // nested; child selector starts with '^'
    public EdgeActionCollection Enter { get; }      // IStyleEdgeAction, run on activation edge   (B5; name pinned)
    public EdgeActionCollection Exit { get; }       // IStyleEdgeAction, run on retraction edge
    public string? Key { get; init; }
    public bool IsSealed { get; } public void Seal();   // auto-called on attach; idempotent
}

public sealed class Setter
{
    public Setter(UIProperty property, object? value);
    public UIProperty Property { get; }
    public object? Value { get; }   // constant | ResourceReference | BindingBase | UIProperty.UnsetValue
}

public sealed class DataCondition
{
    public DataCondition(BindingBase binding, object? value);                 // Equals test
    public DataCondition(BindingBase binding, Func<object?, bool> predicate);
    public bool Negate { get; init; }
    // Pinned: unknown/UnsetValue binding value ⇒ unmet; watcher lifetime = armed lifetime; DataContext change ⇒ rebind.
}
```

```
selector-list := selector ( ',' selector )*            // supported; each member compiles to its own rule
selector      := compound ( (' ' | '>' | '/template/') compound )*
compound      := [ '^' ] [ type | ':is(' type ')' ] ( '.' class | '#' name | ':' pseudo )*
type          := name | prefix '|' name                 // 'prefix|Name' = CSS/Avalonia namespace form ('|', not ':')
pseudo        := focus | focus-within | focus-visible | pointerover | pressed | disabled | checked
               | indeterminate | selected | active-window | access-keys | modal-attention
               | any control-registered custom pseudo-class
```

Bare type = exact match; `:is(T)` = T or derived. A type token may be **namespace-qualified** as `prefix|Name` (the CSS/Avalonia form — `:` is taken by pseudo-classes), recognized only in type-token position (bare or inside `:is(...)`), never on a `.class`/`#name`/`:pseudo`. The qualifier is resolved by a namespace-aware `ISelectorTypeResolver`: a XAML-loaded selector binds `prefix` against the document's **root** xmlns declarations (the top-level-only policy — a non-root xmlns is `CUR2004`; see Fork C §4) and resolves `Name` through the schema context; the default code-first resolver matches simple names only and rejects a qualified token. `Selector.Parse`/`ToString` round-trip (the qualified token is preserved verbatim); fluent builders in `Selectors` (`OfType<T>`, `Is<T>`, `.Class/.Name/.PseudoClass/.Child/.Descendant/.Template/.Nesting`) already carry the exact CLR type, so they need no qualifier. Each member of a selector list shares the style's setters/`When` but carries its own specificity (CSS semantics). **Explicitly absent by decision:** `:not()`, `:nth-child()`/positional, sibling combinators, attribute and property-value selectors (§3.10). There is no `Or` in `When` — disjunction is two styles via `BasedOn` or a selector list.

### §3.2 Element surface and interaction state

```csharp
public partial class UIElement
{
    public ClassSet Classes { get; }                 // interned strings; Add/Remove/Replace notify the engine
    protected PseudoClassSet PseudoClasses { get; }  // control-author surface; ':'-names rejected from Classes
    public Style? Style { get; set; }                // explicit attachment, layer Explicit(5)
    public Styles? Styles { get; set; }              // scoped: this element's subtree (lazy-alloc; surface owned by S1 §5.1)
}

[Flags] public enum InteractionState : uint
{
    None = 0, PointerOver = 1 << 0, Pressed = 1 << 1, Focused = 1 << 2, FocusWithin = 1 << 3,
    FocusVisible = 1 << 4, ActiveWindow = 1 << 5, AccessKeyCue = 1 << 6, Disabled = 1 << 7,
    ModalAttention = 1 << 8,   // ":modal-attention" — S4 sets on blocked-window press; cleared ~600 ms later by an S5 UITimer
}

public interface IInteractionStateSink   // implemented by UIElement; callers: S3, S4, S1's enabled plumbing
{
    void SetInteractionState(InteractionState state, bool active);
    InteractionUpdateScope BeginInteractionUpdate();   // coalesces N flips into one activation pass per element
}
```

`PseudoClassMapping.Register<TOwner>(StyledProperty<bool>, string)` plus the multi-class classify overload (`Func<TValue,string?>` over a fixed class set — canonical, e.g. `bool?` → `:checked`/`:indeterminate`/null) bridge control properties to pseudo-classes via Fork A's `IPropertyChangeObserver`. Interaction pseudo-classes are writable only by framework services and control authors. **`PseudoClassSet.Set` is sanctioned only for DirectProperty-backed control-semantic classes with no `InteractionState` bit** (`:open`, `:highlighted`); `:pressed` MUST flow through `SetInteractionState` so S3's dispatcher-held pressed-holder set keeps the C8 (terminal focus-out clears Pressed window-wide) and W-DC (window-deactivation clearing — a distinct named contract) guarantees; `IsPressed` is a read-only mirror. Effective-`IsEnabled` is computed by **S1's** inheritance plumbing (recompute via `InvalidateIsEnabledCore()`), pushing `InteractionState.Disabled` flips into this sink.

### §3.3 Matching and storage mechanics

**Seal** (auto on attach to an attached `Styles`): flatten `BasedOn` (derived setters append after base), AND-compose `Children` nesting (`Button.primary` + `^:pointerover` ⇒ one rule), validate/convert setter constants once, compile to `CompiledRule` (right-to-left `CompoundMatcher[]`, flattened setters/`When`, subject pseudo mask, ancestor-state requirements, base sort key). Seal-time errors name (style, rule index, property). Seal cascades: every `Storyboard` referenced by a `BeginStoryboard` in `Enter`/`Exit` is sealed too, surfacing track-type errors at attach with (storyboard, track index, property). Template seal deep-seals `ControlTemplate.Resources`; **arming an unsealed template throws, naming the template**.

Each attached `Styles` owns a **`StyleIndex`** rule-hash keyed by the subject's most selective discriminator (name → class → exact type → `:is` base → diagnostics-warned universal bucket), plus `AncestorInterestingClasses` for bounded subtree re-match. Candidate sets are typically <10 rules.

**Phase 1 — structural match** (attach / class / name change / `Styles` mutation): walk the scope chain, gather candidates, evaluate structure only. A match becomes an **armed `ActivationFrame`** (struct: rule, `UnmetCount` over pseudo bits + custom pseudos + `When` + ancestor requirements, flags, Fork A cookie, watcher array) in the element's `ElementStyleState`, frames sorted by `StyleSortKey`, with a `PseudoInterestMask` union for O(1) early-out. **Template barrier (§0-grade):** rules never match elements with `TemplatedParent != null` except via `/template/`; the engine skips such elements before the candidate scan. Lifecycle pins: `OnElementAttached` fires **before the element's first measure** (styles affect layout in the same frame); `OnElementDetached` runs bottom-up, batched (cookie batch retraction). Permanent detach retracts frames and **disposes** `When` watchers; reattach rebuilds.

**Phase 2 — the hot path** (pseudo/`When` flips): one routine — interest-mask test (most elements exit in one AND), then per interested frame an `UnmetCount` increment/decrement; transition to 0 ⇒ apply (setters → frame, capture cookie), from 0 ⇒ retract (cookie removal). Zero allocation on the steady path. Ancestor pseudo requirements (`Pane:focus-within Button`) register explicit `AncestorDependency` nodes — a flip walks only its dependency list; diagnostics flag rules with >1 ancestor-state compound. Flips raised during application queue and drain to fixpoint (generation cap 16, then throw with cycle trace; A→B→A in one drain trips the **style-loop diagnostic** naming the rule pair). `BeginInteractionUpdate` batches chain crossings so activate-then-deactivate within one input event does neither.

**Class change** re-runs Phase 1 for the element (plus bounded subtree re-match when the class is ancestor-interesting); frames diff by rule identity so survivors keep cookies and watcher instances. `Styles` mutation / theme swap = scope-wide re-match — deliberately the unoptimized startup/theme-switch tier.

### §3.4 Precedence: one slot, one sort key

All style values enter Fork A at **`BindingPriority.Style`**: `Animation > LocalValue > Style (within-slot ordered by StyleSortKey) > Template > Inherited > Default`. Values a template *authors on its parts* (`{TemplateBinding}`, literals, `SetResourceReference`) enter at the separate **`BindingPriority.Template`** lane *below* the whole Style slot (§2.2/§20, added 2026-06-16) — so any style rule, including one armed at the `StyleLayer.Template` *sort-key layer* (a rule from a control template's own `Styles`, still `Style`-priority), overrides a templated part's authored value. The `StyleLayer.Template` layer below and the `BindingPriority.Template` lane are **different mechanisms that share a word** — the former orders rules *within* the Style slot, the latter is a weaker lane *outside* it.

```csharp
public readonly struct StyleSortKey : IComparable<StyleSortKey>   // packed ulong
{
    // [layer:3][names:8][classLike:10][types:8][scopeDepth:8][order:27]
    // layer: ControlTheme(0) < Template(1) < Theme(2) < App(3) < Scoped(4, deeper wins) < Explicit(5)
    // classLike: classes + pseudo-classes + When-conditions each count 1 — DataConditions ARE specificity
}
```

**Layer beats specificity** — a documented deliberate divergence from WPF/Avalonia. Counting each `DataCondition` as class-equivalent makes `When`-guarded styles beat their unguarded bases with no extra mechanism.

### §3.5 Style input channels (per layer)

- **ControlTheme(0)** — a control theme is a type-keyed, selector-less `Style` (with `^`-rooted `Children`) in a resource dictionary; armed wherever found in the chain via `ResourceServices.SubscribeControlTheme`, which owns **both** the `Control.ThemeProperty` watch and the chain-lookup node under one handle (styling never watches `ThemeProperty` separately). Identity change ⇒ listener fires ⇒ re-arm (frame remove + add). Lookup key is `Control.ControlThemeKey` (exact-key, no base probing — S7's semantics).
- **Template(1)** — armed inside `ControlTemplate.Instantiate` (step 3 of S8's apply sequence) against the freshly stamped subtree. `TemplateInstance.Detach()` retracts by cookie: template-scoped style frames + `TemplateBinding`s + ContentPresenter auto-alias observers. Store-owned promotion, never set-back.
- **Theme(2)** — the engine consumes **only `UIApplication.Theme`'s** `ResourceDictionary.Styles` slot, flattened depth-first (each merged dictionary's `Styles` in `MergedDictionaries` order, then the theme's own last). Read when `Theme` is set; re-read on theme-origin CatchAll pulses (version compare makes no-change re-reads cheap); theme swap = scope-wide re-match. `Styles` slots on element/window-level dictionaries are ignored in v1 (debug diagnostic when populated).
- **App(3) / Scoped(4)** — `Styles` collections on `UIApplication` and on elements (nearer scope wins via `scopeDepth`). **Explicit(5)** — `UIElement.Style` (selector-less or `^`-rooted), arms without the index.

### §3.6 Value injection — the Fork A seam

Each armed rule activation arms **one `ValueFrame`** at the rule's `StyleSortKey`; retraction removes it by cookie and the store promotes (invariant 4: never set-back). The handshake:

```csharp
public interface IStyleValueSink   // implemented by Fork A's ValueStore
{
    StyleValueCookie ApplyRule(UIElement target, CompiledRule rule, StyleSortKey key);
    void RemoveRule(UIElement target, StyleValueCookie cookie);   // MUST be O(entries), allocation-free; promotion store-owned
    // ClearValue does not disturb Style entries. There is NO resource-sweep entry point here:
    // resource change delivery is S7's per-node registry (ResourceServices.Subscribe) — styling builds no parallel sweep.
}
```

- **Resource-valued setters (`ResourceReference`)** live inside the armed frame's `IValueEntry<T>` at the frame's sort key; a pulse **mutates the entry in place** and raises Fork A's `OnEntryChanged` — the frame is never removed/re-added for a value change (no re-match, no sort churn). A pulse resolving to `UIProperty.UnsetValue` ⇒ entry-unset ⇒ store promotion, never a value write.
- **Subscription discipline (hot path):** frame deactivation calls `ResourceSubscription.Pause()`; activation calls `Resume()` **before** the frame's entries are read; frame disarm calls `Dispose()`. Pause/Resume are O(1) flag writes — they ride the `:pointerover` edge under any-event motion. Pinned by S7 test R1.
- **Binding-valued setters are frame-hosted:** the engine calls `BindingOperations.Install(element, property, bindingBase, ValueFrame hostFrame)` passing its own frame for the armed rule; cookie retraction evicts the expression with zero styling-side bookkeeping. Free-standing `Bind` is LocalValue-only — Style-slot contributions MUST be frame-hosted.
- **`When` watchers** connect at arm time (one `ConditionWatcher` per condition; expression instances cached). Watchers stay live across *deactivation* edges — they are the re-activation predicate — and are disposed only at disarm/detach. Consequently `IBindingWatch` carries **no** Pause/Resume members (pause semantics exist only on S7's `ResourceSubscription`, which has the genuine caller above). `When` requires self-source and ancestor-source bindings from S2 (numbered requirement).

Styling and the property system never touch `Scene`/`CellBuffer` (invariant 2); restyle reaches pixels only through `PropertyEffects` metadata routed by S1.

### §3.7 Frame-loop and cross-engine seams

```csharp
public interface IStyleFrameHooks   // consumed by S6's frame loop
{
    void FlushPendingActivations();   // drain the queued flip fixpoint; called at Phase 3 AND after the animation tick — cheap when empty
    bool HasPendingActivations { get; }   // O(1); feeds the Phase-7 idle guard and UITestHost.RunUntilIdle
    void OnCapabilitiesChanged(TerminalCapabilities capabilities);   // records the snapshot only
}
```

Pseudo-flips raised during layout/render (e.g. scrollbar-visibility mappings) queue, surface via `HasPendingActivations`, and trigger another frame — never sitting until the next input event. Capability-class **stamping happens at visual-root attachment** for new roots and immediately for attached roots on renegotiate; the startup call (pre-Show) stamps nothing by design. Color-tier classes (`caps-truecolor|ansi256|ansi16|nocolor`) stamp from **`ActualThemeVariant.Tier`** (honoring `RequestedColorTier`) off `UIApplication.ActualThemeVariantChanged`; non-color classes (`caps-motion`, `caps-kitty-keyboard`, `caps-unicode|ascii`) stamp from negotiated capabilities. On `RenegotiateAsync` the re-stamp executes in the **same tick** as S1's `RenderTree.Capabilities` re-stamp + `InvalidateAll` + fresh `FrameRenderer`/`SceneCompositor`, so visuals and rasters change in one coherent frame.

**Edge actions:** the engine invokes `OnActivated(scope)`/`OnRetracted(scope)` on each rule's `Enter`/`Exit` `IStyleEdgeAction`s **in rule-document order** on activation/retraction edges. Actions are shared per style; S5 owns per-element instancing (the `(igniter, scope)` registry) and the no-throw contract. Ignition vocabulary: `BeginStoryboard`/`StopStoryboard` with `HandoffBehavior.SnapshotAndReplace`; storyboards ignited by the post-tick flush are covered by S5's `TickNewlyStarted`. The `:modal-attention` flash is pure theme content on this path (S4 sets the bit, an S5 `UITimer` clears it).

**Template seam:** `ITemplateContent` (Fork C's deferred node-graph content) + `TemplateInstance { Root, NameScope, Template, Detach() }` with the cookie-scoped retraction above, plus a debug subscription-leak tracker. **Observers:** `IPropertyChangeObserver` feeds `PseudoClassMapping`; `BindingEntry<T>`/`AnimatedValueHandle<T>` are Fork A producers styling never touches (Animation outranks the Style slot wholesale); `IDeferredResourceEntry` realization is S7's — styling sees only resolved values through its frames.

### §3.8 Terminal-specific adaptations

- **Theme variants are capability-shaped** (`ThemeVariant = (ThemeBase, ColorDepth)`, owned by S7). A theme-variant flip is **DynamicResource re-resolution only — no re-match**; renegotiation may additionally change capability classes.
- **`:pointerover` is capability-honest:** with `MouseCapabilities.Motion` false the bit never sets — no polyfill (any-event motion is on by default, so this is the rare path). Lint: hover-only affordances without `:focus` parity get a one-time debug warning. `:access-keys` follows S3's gate (`(DistinguishesKeyUpDown && ReportsRepeats) || Win32InputMode`, undecorated snapshot); where unsupported the root bit is permanently on, making requirement-6 underscore visibility pure styling: `:access-keys AccessTextPresenter { ShowUnderline: true }`.
- **State styling favors attributes/fill over geometry:** `:disabled` → `Faint` + `DisabledBackgroundBrush`, `:focus` → reverse-video (pickable controls) / well-fill + caret (text controls), `:pressed` → reverse-video in accent; underline shape/color are first-class setter targets. No transform/sub-cell-opacity setters, and **no sub-cell focus ring or border-weight escalation** — the default look is fill-bounded (§11.8a, §12.7).
- **`Background` is not property-inherited** (defaults `Brushes.Transparent`); visual continuity is the compositor's job. Modal dimming = S4 sets the `obscured` class on background windows.
- **Scale honesty:** ~10² elements means scope-wide re-match is genuinely cheap, flips are zero-allocation and element-local, and the grammar is curated to keep the invalidation graph exactly *element-local bits + explicit ancestor edges + explicit binding watchers*. Glyph degradation: glyph resources live at color-tier keys, with `caps-ascii`-class-selected style overrides for genuine mismatches.

### §3.9 Diagnostics and the mandated test program

`StyleDiagnostics.Explain(element, property)` renders every contributing setter — active and shadowed — with its full sort-key derivation in **one line** (acceptance test); `MatchedRules(element)` dumps armed frames + activation state; an in-terminal style-inspector overlay demo ships with the engine. Mandated tests, authored **before** the engine where marked: ① oracle-pinned specificity/precedence matrix (hand-computed CSS-equivalent cases; before); ② Fork A's `ValueFrame` conformance kit run against the styling engine's arming/retraction/promotion behavior (before); ③ selector `Parse`/`ToString` round-trip corpus, shared with Fork C's loader for `Selector=` attributes; ④ flip-path zero-allocation assertions; ⑤ style-loop diagnostic + generation-cap tests; ⑥ resume-before-activate ordering (S7 R1); ⑦ `TemplateInstance.Detach()` leak-tracker run; ⑧ `UITestHost.RunUntilIdle` convergence via `HasPendingActivations`; ⑨ theme-flip-with-open-popup regression (joint with S4/S7).

### §3.10 Rejected alternatives

- **WPF `Trigger`/`MultiTrigger` + trigger priority slots** — forces interaction state to exist as properties and multiplies value slots; specificity inside one slot gives the same override behavior.
- **Property-value selectors (`[IsDefault=true]`)** — subsumed by `PseudoClassMapping` + `When`; reintroduce per-value-change selector re-evaluation, the most invalidation-hostile feature in Avalonia's grammar.
- **`:not()` / `:nth-child` / sibling combinators** — invalidation becomes a function of sibling-list mutation, destroying the element-local flip model (the invalidation-graph razor); classes are the escape hatch.
- **`ControlTheme` as a distinct type** — a keyed selector-less `Style` with `^` children covers it; one mechanism.
- **`EventTrigger`** — ceded to S5's edge-action/storyboard surface; routed-event form is a recorded deferral.
- **Styling-owned resource sweep (`OnResourcesChanged`)** — superseded by S7's per-node registry; one lookup implementation lives in S7.

### §3.11 Engine amendment ledger

Baseline = `proposal-styling-hybrid.md` as amended by DECISIONS. Each row is a countersigned delta.

| # | Amendment | By | Shape |
|---|---|---|---|
| B1 | `IStyleFrameHooks` seam | S6 | §3.7 interface; flush at Phase 3 **and** post-animation-tick; `HasPendingActivations` O(1) feeds Phase-7 guard + `UITestHost.RunUntilIdle`; layout/render-raised flips trigger another frame. |
| B2 | Stamp at root attach | S6 | `OnCapabilitiesChanged` records only; stamping at visual-root attachment (immediate for attached roots on renegotiate); startup pre-Show call stamps nothing. |
| B3 | Effective-tier class sourcing | S7 | Color-tier classes from `ActualThemeVariant.Tier` off `ActualThemeVariantChanged`; non-color classes from negotiated caps. |
| B4 | Re-stamp rides renegotiation transaction | S1 | Same tick as `RenderTree.Capabilities` re-stamp + `InvalidateAll` + fresh `FrameRenderer`/`SceneCompositor`. |
| B5 | Edge-action collections + order | S5 | `Style.Enter`/`Style.Exit` of `IStyleEdgeAction` (names pinned); `OnActivated`/`OnRetracted` in rule-document order; S5 owns `(igniter, scope)` instancing + no-throw. |
| B6 | Seal cascade into storyboards | S5 | Style seal seals every `BeginStoryboard`-referenced `Storyboard`; errors at attach with (storyboard, track index, property). |
| B7 | Template seal seals `ControlTemplate.Resources` | S7, S8 | Deep-seal at template seal; arming an unsealed template throws naming the template. |
| B8 | Template-layer arming + `Detach()` scope | S8 | Arm `template.Styles` at `Template(1)` inside `Instantiate` step 3; `Detach()` retracts by cookie: frames + `TemplateBinding`s + auto-alias observers. |
| B9 | `PseudoClassSet.Set` sanction narrowed | S8, S3 | Direct `Set` only for DirectProperty-backed control-semantic classes without an `InteractionState` bit; `:pressed` via `SetInteractionState`; `IsPressed` read-only mirror. Multi-class `PseudoClassMapping` overload is canonical, no delta. |
| B10 | Frame entries hold resolved value or unset | S7 | `ResourceReference` values live in the frame's `IValueEntry<T>`; pulses mutate in place + `OnEntryChanged`; `UnsetValue` ⇒ entry-unset ⇒ promotion. Joint with Fork A. |
| B11 | Resume-before-activate ordering | S7 | Deactivate ⇒ `Pause()`; activate ⇒ `Resume()` before entry reads; disarm ⇒ `Dispose()`. O(1) flag writes; pinned by S7 T1. |
| B12 | Control-theme arming via `SubscribeControlTheme` | S7 | One handle owns the `ThemeProperty` watch + chain node; styling never watches `ThemeProperty` separately; identity change ⇒ re-arm. |
| B13 | `Theme.Styles` consumption channel | S7 | Only `UIApplication.Theme`'s dictionary `Styles` slot, flattened depth-first, armed at `Theme(2)`; re-read on theme-origin CatchAll pulses; element/window dictionary `Styles` slots ignored in v1 + debug diagnostic. **Landed (R2/B13):** the theme's *own* top-level `Styles` slot is consumed at `Theme(2)` and re-matched on theme reassignment + theme.Styles mutation (`StyleEngine.OnThemeStylesInvalidated`); a variant flip stays resource-only (CD15). Residual follow-ups: flattening `Styles` nested in the theme's `MergedDictionaries`, and the version-compare re-read short-circuit (the re-read is currently unconditional). |
| B14 | `IStyleValueSink.OnResourcesChanged` superseded | S7 | Sweep entry point deleted; delivery via S7's per-node registry; proposal §2.6 resource/theme sketches superseded wholesale by S7's types. |
| B15 | Binding-valued setters frame-hosted | S2 | `BindingOperations.Install(element, property, binding, hostFrame)` with the engine's own `ValueFrame`; cookie retraction evicts; free-standing `Bind` is LocalValue-only. |
| B16 | `When`-watcher lifecycle arbitration | S2, S7, S8 | Permanent detach retracts frames + **disposes** watchers; watchers stay live across deactivation (they are the re-activation predicate); `UnsetValue` ⇒ unmet; `IBindingWatch` carries no Pause/Resume (those live on `ResourceSubscription` only). |
| B17 | `:modal-attention` transient pulse | S4, S5 | `InteractionState.ModalAttention = 1 << 8` + `:modal-attention`; S4 sets via `IInteractionStateSink`, S5 `UITimer` clears ~600 ms; B5 edge actions animate. Routed-event `EventTrigger` stays deferred. |
| B18 | Contract re-points C5/C8/W-DC | S1, S3, S4 | C5: effective-IsEnabled computed by S1 (`InvalidateIsEnabledCore()`), pushing `Disabled`; C8 implemented by S3's pressed-holder set behind `SetInteractionState`; W-DC is a distinct named contract. |
| B19 | Lifecycle ordering pins | S1 | `OnElementAttached` before first measure; `OnElementDetached` bottom-up, batched cookie retraction. |

### §3.12 Deferred (carry forward)

- **`:not()`** — re-addable additively; needs negative-dependency invalidation.
- **Style-driven implicit transitions (`Style.Transitions`)** — `Enter`/`Exit` edges are the hooks; jointly owned with S5 when it lands.
- **`OrConditionGroup`** — disjunction via `BasedOn`/selector lists suffices for v1.
- **Property-value selectors** — probably never; `When` + `PseudoClassMapping` cover the use cases.
- **Routed-event `EventTrigger`** — `:modal-attention` pulse is the v1 answer; code-behind fallback otherwise.
- **Hot-reload diffing beyond scope re-match** — scope re-match is the v1 reload path.
- **`x:Shared`-style setter-value cloning** — setter values are shared singletons in v1.
- **Lazy `When`-watcher connection (viewport-gated)** — only if N-armed-subscription cost bites on long lists.
- **Element/window dictionary `Styles` slots** — ignored in v1 (debug diagnostic); promotion is additive.
- **`:alternate` / `AlternationIndex` alternating-row styling** — designed mechanism (generator-stamped, element-local, does not reopen the `:nth-child` fence); cheap early add after control milestone C2 (needs the container generator).
- **Boundary-promotion demotion valve interplay** — styling-triggered layer minting follows S1's sticky rule; revisit only with S1's demotion design.

---

## §4 Fork C — the XAML pipeline

> Canonical proposal: `proposal-xaml-runtime-loader.md`, amended by DECISIONS and the ledger in §4.12. XAML is **processed at runtime, validated at build time**. There is exactly one execution path — the runtime loader; the X4+ generator runs *the same parser* for build-time diagnostics and typed-field generation and never generates object-construction code in v1. One semantic implementation, zero drift.

### §4.1 Assemblies and pipeline shape

- `Cursorial.UI` — framework types the loader populates (also home of `AccessText`).
- `Cursorial.UI.Xaml` — the loader. Its module initializer registers `ResourceDictionaryLoader.LoadCallback : Func<Uri, ResourceDictionary>?` (powers `ResourceDictionary.Source=`; process-global static — tests save/restore it).
- `Cursorial.UI.Xaml.Generator` — X4+: build-time validation, typed `x:Name` fields + `InitializeComponent`, generated `IXamlTypeMetadataProvider`, `CursorialXamlStrictAot` auto-set by `PublishAot`.
- A **netstandard2.0 parser-frontend assembly ships at X0**, shared by loader and generator.

Two stages, one immutable artifact between them:

```
.xaml bytes ──XmlReader──▶ XamlDocument (immutable, resolved, folded node graph; cached per URI, thread-safe)
                                 ▼
                       XamlInstantiator walk ──▶ live element tree
            templates / resource entries = slices of the same node graph, re-walked per Build/Realize
```

Everything expensive and fallible — XML parse, xmlns/type/member resolution, markup-extension parsing, converter selection, constant folding — happens once in stage 1 with line/column on every node. Stage 2 is a tight array walk over cached delegates. Deferred content is a contiguous **slice of the resolved node graph** — parse-time-checked, near-zero storage, instantiation cost proportional to objects created. Deferral is **type-contract-driven**: a member typed `ITemplateContent` defers (`ControlTemplate.Content`, `DataTemplate.Content`, `ItemsPanelTemplate.Content`); there is no `[DeferredContent]` attribute. `Storyboard`/`TransitionCollection` have no `ITemplateContent`-typed members and are deliberately *not* deferred — they instantiate as ordinary resource objects.

### §4.2 Public API (condensed)

```csharp
public sealed class XamlLoader
{
    public XamlLoader(XamlLoaderOptions? options = null);
    public static XamlLoader Shared { get; }
    public XamlDocument Parse(Stream xml, Uri? sourceUri = null);      // stage 1: pure, thread-safe
    public XamlDocument Parse(string xml, Uri? sourceUri = null);
    public XamlDocument GetOrParse(Uri sourceUri);                     // per-loader cache
    public object Load(XamlDocument doc, XamlLoadContext? ctx = null); // stage 2: UI thread only
    public T Load<T>(XamlDocument doc, XamlLoadContext? ctx = null) where T : class;
    public object Load(Uri sourceUri, XamlLoadContext? ctx = null);
    public void LoadComponent(object component);                       // x:Class ⇒ embedded-resource convention
    public static void LoadComponent(object component, Uri sourceUri); // uses Shared
    public static void LoadComponent(object component);                // uses Shared + x:Class convention
}

public sealed class XamlLoaderOptions
{
    public IXamlTypeMetadataProvider MetadataProvider { get; init; }   // default: ReflectionXamlMetadata.Instance
    public IXamlResourceProvider ResourceProvider { get; init; }       // default: embedded-resource resolver
    public XamlDiagnosticMode DiagnosticMode { get; init; }            // ThrowOnFirstError | CollectAll
    public bool FoldConstants { get; init; } = true;
    public CultureInfo ConverterCulture { get; init; }                 // default: invariant
}

public sealed class XamlLoadContext
{
    public object? RootInstance { get; init; }            // LoadComponent population
    public IResourceScope? AmbientResources { get; init; }// default: ResourceScopes.ForApplication()
    public IServiceProvider? Services { get; init; }
    public INameScope? NameScope { get; init; }
}

public sealed class XamlDocument
{ public Uri? SourceUri { get; } public Type RootType { get; } public string? RootClassName { get; }
  public IReadOnlyList<XamlDiagnostic> Diagnostics { get; } }

public readonly record struct XamlDiagnostic(string Code, string Message,
    XamlDiagnosticSeverity Severity, Uri? Source, int Line, int Column); // CUR1xxx parse / 2xxx resolve / 3xxx instantiate
public sealed class XamlParseException : FormatException
{ public Uri? Source { get; } public int Line { get; } public int Column { get; }
  public IReadOnlyList<XamlDiagnostic> Diagnostics { get; } }            // runtime failures carry line/column too

public interface IXamlTypeMetadataProvider
{ XamlType? TryGetType(string xmlNamespace, string localName); void RegisterXmlnsDefinitions(IXmlnsRegistry registry); }

public sealed class XamlType    // built once per CLR type, cached
{ public Type ClrType { get; } public Func<object>? Activate { get; } public string? ContentProperty { get; }
  public bool IsCollection { get; } public Action<object, object?>? AddItem { get; }
  public Action<object, object, object?>? AddDictionaryItem { get; }
  public Type? DictionaryKeyType { get; }                  // drives x:Key conversion (C-8)
  public XamlMember? TryGetMember(string name); public bool RequiresInitialize { get; } }

public sealed class XamlMember
{ public string Name { get; } public Type ValueType { get; } public UIProperty? Property { get; }
  public Action<object, object?>? SetClr { get; } public Func<object, object?>? Get { get; }
  public ITypeConverter? Converter { get; } public bool IsEvent { get; }
  public bool IsDeferredContent { get; } }                 // derived: ValueType == typeof(ITemplateContent)

public interface ITypeConverter
{ object? ConvertFromString(string text, in XamlValueContext context); bool IsContextFree { get; } }
public static class XamlConverters                         // process-wide; public runtime seam (C-15)
{ public static void Register(Type targetType, ITypeConverter converter); public static ITypeConverter? For(Type targetType); }

public abstract class MarkupExtension { public abstract object? ProvideValue(IServiceProvider services); }
// services during ProvideValue: IProvideValueTarget, IRootObjectProvider, IXamlLineInfo,
// IAmbientResources (S7's IResourceScope), ITemplateHost, INameScopeProvider

public interface INameScope { void Register(string name, object element); object? Find(string name); }
public interface ITemplateContent { object Build(in TemplateBuildContext context); }
public readonly struct TemplateBuildContext
{ public object? TemplatedParent { get; init; } public INameScope NameScope { get; init; }
  public IResourceScope? InstantiationScope { get; init; } public IServiceProvider? Services { get; init; } }
```

Authoring attributes: `XmlnsDefinitionAttribute(string xmlNs, string clrNs)` (assembly, multiple), `ContentPropertyAttribute(string)`, `XamlMetadataProviderAttribute(Type)` (AOT registration). `ReflectionXamlMetadata` is annotated `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]` honestly; the X5 generated provider replaces it for trimmed/AOT builds.

### §4.3 Storage and loading mechanics

`XamlDocument` owns flat struct arrays (`ObjectRecord`/`MemberRecord`/`ExtensionRecord` + `Constants`/`Strings`/`ResolvedTypes`/`ResolvedMembers`), children contiguous depth-first — any subtree is `(startIndex, SubtreeLength)`, which *is* a deferred slice. Packed line info on every record. Immutable after parse; shared across threads and template builds.

**Stage 1** — single `XmlReader` pass (`DtdProcessing = Prohibit`; XML external entities are never processed). Per element: resolve type through the xmlns stack (`https://cursorial.dev/ui` via `XmlnsDefinitionAttribute`; `using:` and `clr-namespace:` both accepted; misses get did-you-mean suggestions); classify directives / attached members (resolved via Fork A's registry against the owner type) / events / members; resolve members once (registered `UIProperty` first — reflection-free — then CLR via the metadata provider, else CUR2102 with the member list); parse markup extensions (hand-rolled recursive-descent grammar, positional-argument convention pinned at X0; `{x:Static}`/`{x:Type}`/`{x:Null}` folded to constants immediately); **fold literals** whose converter `IsContextFree` — `Margin="1,2"` costs one parse + one box per *document*, shared by every template build. Whitespace: trim ends, collapse interior newline+indent to one space, honor `xml:space="preserve"` (simpler than WPF; documented + oracle-tested). `Setter` values fold through the *property's* converter against the lexically known `TargetType` at end-of-object; an unresolvable Setter owner is CUR2110 at parse time.

**Stage 2** — index walk per `ObjectRecord`: activate (or populate `RootInstance`), `BeginInit` when `ISupportInitialize`, register `x:Name`, push `IResourceHost`s onto the lexical scope stack, evaluate members (`Folded` → shared constant; `Text` → context-dependent convert; `Object`/`Items` → recurse/Add; `Extension` → §4.4; `Deferred` → one `XamlTemplateContent` allocation; `Event` → bind to the root instance's handler — events inside deferred content are CUR2301), assign via Fork A `SetValue` when a `UIProperty` exists, else the CLR setter, then `EndInit`, pop scope. **End-of-object rules:** (a) ~~`IResourceValueBuilder.Build()` substitution~~ — **retired (#8, never wired); see §11.9** (the Drawing media types are directly element-authorable, so there are no builder shadows to substitute); (b) `x:Key` converts through the **target collection's `DictionaryKeyType`** — a `ThemeDictionaryCollection` item's key goes through `ThemeVariantKey.Parse` (C-8); plain dictionaries keep literal strings. `ISupportInitialize` bracketing plus Fork A's `DeferNotifications` means loading never fires per-property invalidation storms into layout.

### §4.4 Extensions, resources, precedence

Built-in extensions (`{x:Null}`, `{x:Static}`, `{x:Type}`, `{StaticResource}`, `{DynamicResource}`, `{Binding}`, `{TemplateBinding}` — the last parse-time restricted to template bodies) are typed records, zero allocation for the extension object. Results attach through the **`IDeferredValue.AttachTo` seam — never sentinel objects through `SetValue`**; value stores only ever see values.

- `StaticResource` walks the lexical `IResourceHost` stack innermost-first, then `XamlLoadContext.AmbientResources`. Forward references within a dictionary are errors with both positions. The scope currency is **S7-owned**: `IResourceScope { bool TryGetResource(object, out object?); IResourceScope? Parent; }` + the `ResourceScopes` factories (C-4).
- `DynamicResource` (C-5): direct property → `SetResourceReference` (producer at `BindingPriority.LocalValue`); setter value → construct `ResourceReference`. Type mismatches at resolve time go through `XamlConverters.For` (C-15).
- `Binding` constructs the parsed `BindingRecord` into S2's `Binding` and applies via `BindingOperations.Apply` → a `BindingEntry<T>` producer at the attach site's priority. Binding to a non-`UIProperty` member is CUR2210 at parse time. Paths parse with the loader's xmlns-aware `IPathTypeResolver { Type? Resolve(string typeToken); }` so `(Type.Property)` segments resolve against xmlns context (C-14); code-first falls back to Fork A's short-name registry.
- `TemplateBinding` applies S2's optimized one-way-to-templated-parent entry at build time.

**Precedence:** the loader is a well-behaved value source, never a precedence authority. Document-level sets carry `BindingPriority.LocalValue`; values authored inside a template build land on the **`BindingPriority.Template`** lane (§2.2/§20, added 2026-06-16) — below Style, so both a document-level `LocalValue` set and any page/theme Style override a template-shipped value. Within-`Style` ordering is Fork B's `StyleSortKey`; restoration is store-owned (invariant 4) — the loader retracts nothing.

### §4.5 Deferred content: templates and resource entries

A deferred member costs one allocation at load: `XamlTemplateContent(document, sliceStart, capturedLexicalScope)` — the captured scope is the resource chain enclosing the template *definition*. Each `Build(in TemplateBuildContext)` creates a fresh template `NameScope`, walks the slice with template-local provenance and `TemplateBinding` enabled, and returns the root. Namescope attachment points are **S2-owned** (C-16): document roots get `NameScope.SetNameScope(root, scope)`; template `x:Name` registers *only* in `TemplateInstance.NameScope`; the template-scope carrier is the **templated parent** (`TemplateNameScopeProperty`, set by `ApplyTemplate` — the template root is NOT the carrier). Fork B's `TemplateInstance.Detach()` is the retraction contract. Because S1 does not call `ApplyTemplate()` while an element is Collapsed, template parts and the template namescope materialize at first non-collapsed measure — generated `x:Name` field access, `FindName` tooling, and hot reload must tolerate null/absent parts (C-21).

Resource dictionaries get the same slices: the loader fills keyed entries via `ResourceDictionary.SetDeferred(object key, IDeferredResourceEntry)` where `interface IDeferredResourceEntry { object? Realize(IResourceScope lexicalScope); }` (C-1 — the markup-extension `AttachTo` seam keeps the `IDeferredValue` name). Contract (C-2): `lexicalScope` = definition-site chain via `ResourceScopes.ForDictionary(definingDict, enclosingChain)`; `Realize` runs once-on-success on the UI thread; **a throwing `Realize` resets the slot to `Deferred`** — entries must be retry-safe and consume no slice state on failure. Entries expose source/line/column via an optional `IXamlLineInfo`-shaped probe feeding `DeferredEntryInfo` (incl. `RealizedAtVariant`) (C-3). A 300-resource theme costs parse + 300 inserts, not 300 instantiations. Theme-variant flips re-resolve through `Application.ResourcesChanged` + S7 registry sweeps; **per-dictionary `Changed` does not fire on variant flips** — sealed dictionaries never pulse (C-10).

### §4.6 Converters

Shipped registry (beyond the geometry/color set in §4.8): `IBrush` via the `BrushMarkup` grammar (`"linear:#f92672,#66d9ef"`), `Pen` text (`"Dashed #888"`), `RelativePoint` (`"0,0"`), `ThemeVariantKey.Parse` — with the real `Cursorial.Drawing.Media` brush/gradient types directly element-authorable in the default xmlns (C-7; the `Cursorial.UI.Media` builder twins are retired, §11.9/#8). Animation set (C-11): `UIProperty` (`"Control.Background"` — registry + Fork A `FindOwnersByShortName`), `Easing` (delegates to `Easings.TryParse` — catalog names **and** `cubic-bezier(x1,y1,x2,y2)`), `RepeatBehavior` (`"1x"`/`"Forever"`), `TimeSpan` (BCL). `KeyGesture` is pinned to S3's `KeyGesture.Parse` (`"Ctrl+S"`, `"F5"`) (C-13). **`Optional<T>` dispatch rule (pinned):** the registry probes for `Optional<T>` shapes, unwraps to the inner type's converter, and wraps the result; empty string ⇒ `Optional<T>.Unset`. The X4 generator folds by the identical rule. `XamlConverters.For(Type)` is a **public, load-independent runtime seam** consumed by S2's value pipeline (target-type fallback, `ConvertBack` reverse parse for `StringFormat == "{0}"` only, OneWayToSource string→leaf) and S7's DynamicResource conversion (C-15).

### §4.7 Access-key literal folding

One data model: `Cursorial.UI.AccessText` — `readonly record struct AccessText(string Text, char Key, int KeyIndex)`; single `Parse` (`"_File"` → `("File",'F',0)`; `"Save __As"` escapes); **explicit** string conversion operator (parsing is lossy); key must be a BMP letter/digit (else literal underscore, no key); simple-case-folded matching; `AccessText.Literal(text)` factory (C-18). Three producers fold identically: loader parse, X4 generator fold, and S8's runtime `GetAccessText()`. For `AccessText`-typed properties the fold is type-driven. For **object-typed** slots the fold engages **iff** the resolved per-type metadata of the instance's runtime type carries Fork A's `ParsesAccessKeyLiterals` flag — exactly `ButtonBase.Content`, `MenuItem.Header`, `TabItem.Header`, `Label.Content`; `TextBlock.Text` and unflagged slots never fold (C-19).

### §4.8 Terminal-specific adaptations

1. **Integer cell geometry.** `Thickness`/`Margins` parse as integer cells; fractional values are CUR2401 with a "cells are atomic" message; **`Margins` components may be negative** (P2.6/LD19 — `Margin="0,-1,0,0"` is legal markup). `GridLength`: `Auto`/`*`/`2*`/`12`. `Rect`/`Size` map to the ushort-backed Rendering types; their converters validate ≥ 0 at parse time (the `Rect` ctor throws on negatives).
2. **Color mini-language.** `#RGB`/`#RRGGBB`(+`AA`), named **ANSI palette** colors (`"Red"` → `Colors.*` palette entries, deliberately not web RGB), `"Palette(123)"`, `"Default"`, `"Transparent"`; plain color text yields cached `Brushes.*` singletons.
3. **Pens, not pixels.** `"Heavy"`, `"Double Rounded"` → `Pens` presets; `BorderStyle` selects a glyph family — weight is a glyph family, never thickness.
4. **No font converters** — `TextAttributes` flags (`"Bold,Italic"`) and FIGlet/`TextSizing` properties instead.
5. **Scale kills compile-first.** Terminal documents are 1–50 KB; a large app's entire markup parses in single-digit milliseconds — under one frame at 50 fps.
6. **Hot reload is the designer** (X5): re-parse under a version stamp, rebuild registered live roots; no previewer can exist for a cell grid.
7. **Threading fit.** Parse is thread-safe (preload off-thread); instantiation and template builds run on the single UI thread. The loader never touches `TerminalSession`, scenes, or buffers (invariants 2/6/7).
8. **URIs:** `cursorial://<assembly>/<path>` over embedded resources (declarative `.targets` item-group wiring; no MSBuild task in v1); no `pack://`, no runtime file probing outside hot-reload dev mode.

### §4.9 Generator phase (X4/X5) — contracts pinned now

- X4 emits S2's `CompiledBinding<TSource,TValue>(Func<TSource,TValue> getter, …, ReadOnlyMemory<CompiledPathStep> steps, …)` ctor directly with real delegates — a second producer of the same type, zero engine change; the descriptor contract (`Binding.Compiled<TSource,TValue>(static vm => vm.X)`, `x:DataType` diagnostics) is v1 (C-17).
- X4 cross-checks `x:Name` sets in `ControlTemplate` slices against the `TargetType`'s `[TemplatePart]` attributes as Roslyn diagnostics — parse-time assist, **not a gate** (C-20).
- X5: generated `IXamlTypeMetadataProvider` (trim/AOT-clean), hot reload dev mode, `PreloadAsync`. The compiled-XAML producer remains an additive plug-in behind `ITemplateContent`/`LoadComponent`/the metadata provider.

### §4.10 Mandated test program

- **Parser fuzzing** over the markup-extension grammar; **pinned oracle table** of WPF's documented escape/quoting cases.
- **Windows-only CI leg** pinning escape/whitespace behavior against real System.Xaml as oracle.
- **Diagnostic golden files** (code + line + column + message) for every CUR1xxx/2xxx/3xxx.
- **Conformance corpus**: one document set drives loader tests and X4 generator diagnostics — the drift gate between the two front ends; fold-equivalence checks (constants, `AccessText`, `Optional<T>`) assert generator folds match loader parse results exactly.
- **Dual-provider run**: full suite twice — `ReflectionXamlMetadata` vs a hand-built provider — so the X5 generated provider cannot drift semantically.
- **Template double-build isolation**: distinct instances, shared folded boxes, separate namescopes; `TemplateInstance.Detach()` leaves no subscriptions (Fork B's leak tracker).
- **Deferred-entry retry-safety**: a throwing `Realize` resets to `Deferred`; the retried `Realize` succeeds.
- The namescope guarded-walk conformance test is S2-owned; this corpus supplies its producing documents.

### §4.11 Rejected alternatives

- **Vendor System.Xaml / Portable.Xaml** — System.Xaml is Windows-Desktop-only; Portable.Xaml is a minimally maintained fork at the UI layer's front door; both forfeit diagnostics/AOT ownership and violate the zero-dependency stance.
- **Source-gen-first** — several times the loader's size, the runtime loader ships anyway (hot reload, dynamic markup), and the desktop payoff doesn't exist at terminal scale; it is the X4/X5 endgame with one semantic implementation in the library.
- **Fluent C# DSL instead of XAML** — no data-shipped themes, no lexical resource scoping, no deferred content without capturing lambdas; `FuncTemplateContent` keeps code-first templates on the same contract regardless.
- **Sentinel objects through `SetValue`** — replaced by the `IDeferredValue.AttachTo` seam; value stores only see values.
- **`[DeferredContent]` attribute** — deferral is type-contract-driven via `ITemplateContent`-typed members.

### §4.12 Engine amendment ledger

Baseline = the canonical proposal as amended by DECISIONS Fork C. Every row is an addition/correction beyond that baseline; X4 rows bind the generator phase but are contract-pinned now.

| # | Amendment | For | Binding shape |
|---|---|---|---|
| C-1 | `IDeferredValue` → `IDeferredResourceEntry` (lazy-dictionary contract) | S7 | `interface IDeferredResourceEntry { object? Realize(IResourceScope lexicalScope); }`; the extension `AttachTo` seam keeps `IDeferredValue`. Fork C sign-off recorded here |
| C-2 | Deferred-entry runtime contract | S7 | `SetDeferred(object, IDeferredResourceEntry)`; scope = `ResourceScopes.ForDictionary(definingDict, enclosingChain)`; once-on-success on the UI thread; throwing `Realize` resets to `Deferred` — entries retry-safe |
| C-3 | Line info on deferred entries | S7 | Optional `IXamlLineInfo`-shaped probe → `DeferredEntryInfo` (incl. `RealizedAtVariant`) |
| C-4 | Ambient-stack currency is S7-owned | S7 | Loader consumes S7's `IResourceScope` + `ResourceScopes`; `AmbientResources` defaults to `ResourceScopes.ForApplication()` |
| C-5 | `{DynamicResource}` attach routing | S7 | Direct property → `SetResourceReference` (producer at `BindingPriority.LocalValue`); setter value → `ResourceReference`; never a sentinel through `SetValue` |
| C-6 | ~~`IResourceValueBuilder` replacement seam~~ | S7 | **Retired (#8)** — never wired; the Drawing media types are directly XAML-element-authorable (§11.9), so there is no builder to substitute |
| C-7 | Default-xmlns map + resource converters | S7 | real `Cursorial.Drawing.Media` brush/gradient types element-authorable in default xmlns (builder twins retired, #8); converters for `IBrush` (BrushMarkup grammar), `Pen` text, `RelativePoint`, `ThemeVariantKey.Parse` |
| C-8 | `x:Key` converted by the collection's key type | S7 | `ThemeDictionaryCollection` keys via `ThemeVariantKey.Parse`; loader semantic keyed on `XamlType.DictionaryKeyType` |
| C-9 | `ResourceDictionaryLoader.LoadCallback` at module init | S7 | Set once by `Cursorial.UI.Xaml`'s module initializer; process-global static — tests save/restore |
| C-10 | Variant-flip pulse correction | S7 | Re-resolution via `Application.ResourcesChanged` + registry sweeps; per-dictionary `Changed` never fires on variant flips (sealed dictionaries never pulse) |
| C-11 | Animation converter set | S5 | `UIProperty` (registry + `FindOwnersByShortName`), `Easing` (`Easings.TryParse`: catalog + `cubic-bezier(…)`), `Optional<T>` (inner-type unwrap; empty ⇒ `Unset`), `RepeatBehavior`, `TimeSpan` |
| C-12 | No deferral for animation objects | S5 | `Storyboard`/`TransitionCollection` are ordinary resource objects — no `ITemplateContent`-typed members, so the type-contract deferral never engages |
| C-13 | `KeyGesture` converter pinned | S3 | `KeyGesture.Parse(string)` (`"Ctrl+S"`, `"Alt+Enter"`, `"F5"`) is the registered converter for `KeyBinding.Gesture` |
| C-14 | Xmlns-aware `IPathTypeResolver` | S2 | Loader implements `IPathTypeResolver { Type? Resolve(string typeToken); }`, passed to `BindingPath.Parse(text, resolver)`; code-first falls back to the Fork A short-name registry |
| C-15 | `XamlConverters.For(Type)` public runtime seam | S2, S7 | Load-independent availability: S2 target-type fallback, `ConvertBack` reverse parse (`StringFormat == "{0}"` only), OneWayToSource string→leaf; S7 DynamicResource conversion |
| C-16 | Namescope population via S2 attachment points | S2, S8 | Document roots: `NameScope.SetNameScope(root, scope)`; template `x:Name` only in `TemplateInstance.NameScope`; carrier = templated parent (`TemplateNameScopeProperty`, set by `ApplyTemplate`; template root is NOT the carrier) |
| C-17 | Compiled-binding emission pinned to S2's type | S2 | X4 emits the `CompiledBinding<TSource,TValue>(getter, …, ReadOnlyMemory<CompiledPathStep>, …)` ctor with real delegates — second producer, zero engine change |
| C-18 | `AccessText` fold target shape | S3, S8 | `Cursorial.UI.AccessText` — `readonly record struct (string Text, char Key, int KeyIndex)`; single `Parse`; explicit string operator; BMP letter/digit rule; case-folded matching; `Literal` factory |
| C-19 | Metadata-flag folding for object-typed mnemonic slots | S8 | Fold string literals to `AccessText` iff resolved per-type metadata carries Fork A's `ParsesAccessKeyLiterals` (exactly `ButtonBase.Content`, `MenuItem.Header`, `TabItem.Header`, `Label.Content`); `TextBlock.Text`/unflagged never fold; three producers fold identically |
| C-20 | X4 `[TemplatePart]` cross-check diagnostics | S8 | Generator validates template `x:Name` sets against `TargetType`'s `[TemplatePart]`s as Roslyn diagnostics — assist, not a gate |
| C-21 | Tolerate unmaterialized template parts while Collapsed | S1 | `ApplyTemplate()` not called while Collapsed; generated fields, `FindName` tooling, and hot reload must tolerate null/absent parts until first non-collapsed measure |
| C-22 | *(Mis-binned)* `TerminalSessionOptions.EmergencyRestoreBytes` | S6 | A Cursorial.Core seam (`ReadOnlyMemory<byte>` via `IStdioTransports.WriteBytesSync` at the top of the signal path) — carried on the Core-seam ledger, not Fork C's |

Closed during consolidation: the static one-arg `XamlLoader.LoadComponent(object)` overload is added (x:Class → conventional source URI — S8's and S6's code-behind examples assume it); the `Optional<T>` converter dispatch rule is pinned as written in §4.6.

### §4.13 Deferred (carry forward)

- **Compiled-XAML producer** — additive plug-in behind `ITemplateContent`/`LoadComponent`/metadata-provider seams; adopt when profiling, not ideology, demands it.
- **X5 deliverables** (generated metadata provider, hot reload, `PreloadAsync`) — phased; trimmed/AOT publish is explicitly unsupported-with-diagnostics until X5.
- **Events inside deferred content** (CUR2301) — templates use commands/`TemplateBinding`; revisit if a real consumer appears.
- `x:TypeArguments`, `x:Shared="False"`, `x:Array`, `x:Reference`, attached events, `x:FieldModifier` — no v1 consumer; each re-addable without re-architecture.
- **Localization / `x:Uid`** — out of scope for v1 (recorded stance).
- **East-Asian-aware whitespace model** — v1 ships the simpler documented model, oracle-tested; revisit only on demonstrated need.
- **Per-instance designer metadata** — no designer exists; hot reload is the dev loop.

---

## §5 S1 — Element tree, layout, and render integration

All types in `Cursorial.UI`, except the panels/presenters and the `Orientation`/`Dock` enums, which live in `Cursorial.UI.Controls` since the P2.5 ⑤ namespace move (§1.3 scheme). `Size`/`Rect`/`Margins` come from `Cursorial.Rendering` (`Rect` is ushort-backed, non-negative; arranged `Bounds` is S1's signed `LayoutRect` since P2.6 — see §5.2/§5.3); `Scene`/`ScenePool`/`SceneLayer`/`CompositeParameters`/`DrawingContext`/`IBrush`/`Pen` from `Cursorial.Drawing`; `using CellStyle = Cursorial.Output.Style;` per Fork B. S1 owns: `UIElement` and tree plumbing, two-pass integer-cell layout (`LayoutManager`, core panels, `Orientation`), `PropertyEffects` invalidation routing, the render-zone engine (`RenderTree`, `RenderContext`, hit testing — **the** scene-granularity model; S4's `TopLevelSurface` wraps a `RenderTree`, S8 layer needs are expressed as boundary promotion), `IsEnabled`/effective-enabled, and `ITerminalCaretService`.

### §5.1 Tree model: one hierarchy, two relationships

One node class in **one visual tree** (layout, render, hit-test, composite order) plus a separate **logical-parent** pointer (Fork B descendant combinators, S7 resource scope, S2 DataContext inheritance). `InheritanceParent = LogicalParent ?? VisualParent` — content inherits through its `ContentControl`, template parts through chrome to the templated control. WPF's Visual/UIElement/FrameworkElement stratification is rejected at hundreds-of-elements scale. A popup surface root's `LogicalParent` is the `Popup` element in the host window (S4): resource scope (S7 pulse registration tops out at the host window's root), inheritance, and S3's cross-tree route continuation all ride this link.

```csharp
public abstract class UIElement : UIObject, IInheritanceNode
{
    public UIElement? VisualParent { get; }     public UIElement? LogicalParent { get; }
    public UIElement? VisualRoot { get; }       public bool IsAttachedToTree { get; }
    public UIElement? TemplatedParent { get; }  // template-barrier datum; set only via SetTemplatedParent (S8)
    protected IReadOnlyList<UIElement> VisualChildren { get; }   // physical order = base paint order
    protected void AddVisualChild(UIElement child, int index = -1);  protected void RemoveVisualChild(UIElement child);
    protected void AddLogicalChild(UIElement child);                 protected void RemoveLogicalChild(UIElement child);
    internal void AddVisualChildOnly(UIElement child);  // visual adoption WITHOUT logical reparent — ItemsPresenter
                                                        // keeps generated containers logical children of the ItemsControl (S8)
    protected virtual void OnAttachedToTree(in TreeAttachmentEventArgs e);   // + OnDetachedFromTree, OnVisualParentChanged
    public event EventHandler<LogicalTreeAttachmentEventArgs>? AttachedToLogicalTree, DetachedFromLogicalTree;  // S2 consumes

    // styling host surface (Fork B types; storage hosted here, lazily allocated)
    public string? Name { get; set; }  public ClassSet Classes { get; }  protected PseudoClassSet PseudoClasses { get; }
    public Styles? Styles { get; set; }  public Style? Style { get; set; }

    // layout + render properties (StyledProperty<T>; effects in brackets)
    public int? Width/Height { get; set; }                       // null = Auto                  [AffectsMeasure]
    public int MinWidth/MinHeight/MaxWidth/MaxHeight { get; set; }                            // [AffectsMeasure]
    public Margins Margin { get; set; }                          // SIGNED since P2.6 (LD19)    [AffectsMeasure]
    public HorizontalAlignment HorizontalAlignment { get; set; } // default Stretch             [AffectsArrange]
    public VerticalAlignment VerticalAlignment { get; set; }     // default Stretch             [AffectsArrange]
    public Visibility Visibility { get; set; }                   // custom routing, §5.6
    public bool IsHitTestVisible { get; set; }                   // default true
    public bool IsEnabled { get; set; }                          // default true; effective-enabled below
    public int ZIndex { get; set; }                              // [AffectsRender + z-order recollect]
    public double Opacity { get; set; }      // default 1.0  [AffectsComposite; <1 promotes boundary] (Window.Opacity = AddOwner, S4)
    public bool ClipToBounds { get; set; }   // default false [AffectsComposite; true promotes]
    public Rect? CompositeClip { get; set; } // composite-time clip (reveal/wipe lane, S5) [AffectsComposite; non-null promotes]
    public int RenderOffsetColumn/RenderOffsetRow { get; set; }  // composite slide, may be negative [AffectsComposite; ≠0 promotes]
    public bool IsRenderBoundary { get; set; }                   // explicit cache hint [promotion is sticky until detach, §5.5]

    public Size DesiredSize { get; }  public LayoutRect Bounds { get; }  // DirectProperty; DesiredSize INCLUDES Margin (WPF
                                           // rule, clamped ≥ 0); Bounds origin is SIGNED (P2.6/LD19 — negative margins)
    public bool IsMeasureValid { get; }  public bool IsArrangeValid { get; }
    public void Measure(Size availableSize);  public void Arrange(in Rect finalRect);
    protected virtual Size MeasureOverride(Size availableSize);  protected virtual Size ArrangeOverride(Size finalSize);
    protected virtual void OnChildDesiredSizeChanged(UIElement child);
    public virtual bool ApplyTemplate();   // S8 seam; called by Measure before MeasureOverride. Collapsed elements
                                           // early-out BEFORE this — no template/name scope until first non-collapsed measure.
    public void InvalidateMeasure();  public void InvalidateArrange();   // self+ancestor walk / self-only
    public void InvalidateVisual();   public void InvalidateComposite(); // zone re-raster / parameter refresh, never re-raster
    public (int Column, int Row) TranslateToWindow(int column, int row); // live O(depth) chain walks, allocation-free,
    public (int Column, int Row) TranslateToLocal(int column, int row);  //   never stale (no cached absolute bounds exist)
    protected virtual void Render(RenderContext context);   // element-local coords; DEBUG read-only guard (§5.5)
    protected virtual bool HitTestCore(int column, int row) => true;
    public bool IsEffectivelyVisible { get; }      // self + ancestors Visible (consumed by S3 hit/focus checks)
    protected virtual bool IsEnabledCore => true;  protected void InvalidateIsEnabledCore();
    protected static void AffectsMeasure<TOwner>(params ReadOnlySpan<UIProperty> ps);  // + Arrange/Render/Composite/
                                                                                       //   ParentMeasure/ParentArrange
}
public enum Visibility : byte { Visible, Hidden, Collapsed }
public enum HorizontalAlignment : byte { Stretch, Left, Center, Right }   // VerticalAlignment mirrors
public enum Orientation : byte { Horizontal, Vertical }                   // S1-owned; S8 cites. In
                                                                           //   Cursorial.UI.Controls
                                                                           //   (with Dock) since P2.5 ⑤
```

**Lifecycle.** Attach walk is pre-order parent-first (set `VisualRoot`, `OnAttachedToTree`, styling `OnElementAttached` — before first measure so styles affect same-frame layout, mark layout invalid, assign zone pointer). Detach walk is bottom-up (Fork B batch retraction): styling `OnElementDetached` retracts and **disposes** armed frames/`When` watchers, boundary scene returns to pool, zone pointer + sticky promotion cleared, then logical/visual detach notifications. Elements are reusable — detach+reattach rebuilds all single-shot state. No `IDisposable` on elements.

**Permanent detach + teardown sweep (CONTRACT).** Pooled scenes and style cookies release on detach, but S2's strong INPC subscriptions to long-lived viewmodels do **not** — a dropped subtree is only leak-free if the sweep runs. *Permanent detach* = window close (S4 invokes it) or the explicit `UIElement.TearDown()` API for app-discarded subtrees; both run, per element bottom-up: `ValueStore.TearDown()` → `BindingOperations.TearDown(element)`.

**Effective IsEnabled (S1-owned).** `effective = IsEnabled && IsEnabledCore && parentEffective`, computed on S1's inheritance wiring; a change pushes `InteractionState.Disabled` to styling (S3's seam) and re-evaluates descendants. Controls call `InvalidateIsEnabledCore()` when their `IsEnabledCore` input changes.

### §5.2 Integer-cell layout math

```csharp
public static class LayoutMath
{
    public const int Unbounded = int.MaxValue;   // measure "infinity"; the only encoding
    public const int MaxExtent = 65535;          // ushort Rect cap — hard ceiling for any arrange rect
    public static int Add(int a, int b);  public static int Sub(int a, int b);   // saturating; Unbounded absorbs
    public static Size Add(Size s, Margins m);  public static Size Sub(Size s, Margins m);
    public static int CenterOffset(int slot, int size);   // floor — spare cell goes right/bottom
}
public static class LayoutLimits { public const int MaxScrollExtent = 32_000; }  // scroll extent cap, one named constant
```

Normative: all layout arithmetic goes through `LayoutMath` (never raw `+`); `DesiredSize` may exceed the constraint but is never `Unbounded` (diagnostic clamp to `MaxExtent`); arrange positions clamp to `[−MaxExtent, MaxExtent]` and extents to `[0, MaxExtent]` before `LayoutRect` construction (DEBUG diagnostic). **Signed margins (P2.6, matrix LD19 — reverses the v1 cut):** `Margin` components may be negative with WPF semantics — the measure/arrange margin-deflate *enlarges* the inner constraint/slot, `DesiredSize = clamp(natural + margin, ≥ 0)` per axis, and the arrange position fold (`slot origin + signed margin + alignment offset`) may produce a **negative origin**, carried by the signed `LayoutRect` (`Bounds`; Rendering's ushort `Rect` is unchanged — implicit `Rect → LayoutRect` widening; `ToRect()` is the explicit narrowing affordance, currently uncalled). Alignment offsets and Canvas offsets still clamp ≥ 0 — margins are the only layout-side source of negative placement; *animated* placement still belongs to composite offsets (`RenderOffset*`, scroll), which never re-raster. Negative-origin content clips per cell at the zone edge (the P2.5 push-stack). No layout rounding exists; fractional shares get pinned per-site policies (floor centering; Grid largest-remainder).

### §5.3 Measure / Arrange / LayoutManager

Measure: Collapsed early-out (precedes `ApplyTemplate`) → constraint cache hit → `ApplyTemplate()` → margin-deflate, min/max clamp (explicit Width/Height fold into both min and max) → `MeasureOverride` → clamp → cache the **natural** (post-MinMax, pre-margin) size — WPF's `_unclippedDesiredSize` → `DesiredSize = natural + Margin` (saturating) → `OnChildDesiredSizeChanged` on change. Arrange: self-heals an invalid measure; non-Stretch content size = `min(natural, slot)` (the cached natural, never `DesiredSize − margin` — the reconstruction inflates past natural when the DesiredSize floor engages under signed margins; matrix L225); alignment offset via floor-centering; `SetBoundsAndRoute` (a `DirectProperty` `SetAndRaise`): **size change** → `InvalidateVisual` + boundary scene recreate (scenes don't resize); **position-only** → boundary: `InvalidateComposite` (cheap layer move); non-boundary: zone `InvalidateVisual`. Loudly documented: *animate position via `RenderOffset*` (composite path), never `Margin`/`Canvas.Left` (re-raster path)* — invariant 3.

```csharp
public sealed class LayoutManager       // one per visual root; owned by the Window
{
    public bool HasPendingWork { get; }
    public void RunLayoutPass(Size rootConstraint);  // measure+arrange to fixpoint: 16-pass cap + cycle diagnostic,
                                                     // ONE bounded re-run if LayoutUpdated handlers dirty layout
    public void AbandonPendingLayout();              // drop queued work (stays invalid; re-runs next tick) — S6 give-up path
    public event Action? LayoutUpdated;              // post-pass; caret/overlay reposition hooks
}
```

**Convergence is owned here** (DECISION): depth-keyed min-heap queues, shallowest-first; invalidations raised during the pass loop within the same frame (invariant 1 — S6 calls `RunLayoutPass` once per frame per window between input drain and render, consulting only `HasPendingWork` for its idle guard). Residual work slips to the next tick with a DEBUG diagnostic. Tree mutation during the pass is legal only from the element currently being measured/arranged (the `ApplyTemplate`/items-expansion path).

### §5.4 Panels

`Panel.Children` is a `UIElementCollection` — owner-wired, sets visual **and** logical parent (index = paint order; throws on null/duplicate/attached-elsewhere; mutation invalidates measure + the cached z-order array); `ItemsPresenter` uses `AddVisualChildOnly`. `Panel.Background (IBrush?) [AffectsRender]` paints via **`FillOpaque`, always** (§5.5). Catalog: `StackPanel` (`Orientation` default Vertical, `Spacing`), `DockPanel` (`LastChildFill`, attached `Dock [AffectsParentMeasure]`), `WrapPanel` (greedy line packing on `ItemWidth/ItemHeight ?? desired`), `Canvas` (attached `Left/Top/Right/Bottom (int?) [AffectsParentArrange]`, children measured `Unbounded`, offsets clamped ≥ 0 — negatives via `RenderOffset*`), `Grid` (`GridLength` Cell/Auto/Star with implicit `int` conversion; attached `Row/Column` coerce ≥0, spans ≥1, all `[AffectsParentMeasure]`; definitions owner-wired so post-attach mutation is live). **Pinned Grid star policy:** largest-remainder (Hamilton) distribution — floor ideal shares, leftover cells to largest fractional parts, ties to lowest index; clamp surplus re-runs over unclamped stars; star under `Unbounded` behaves as Auto. Spanning-Auto deficit spreads evenly, remainder rightmost (refinement deferred).

### §5.5 PropertyEffects routing and render zones

`UIElement.OnPropertyChanged` does one `GetEffects(GetType())` lookup and dispatches: `AffectsMeasure→InvalidateMeasure`, `…Arrange→InvalidateArrange`, `…Render→InvalidateVisual`, `…Composite→InvalidateComposite`, `…ParentMeasure/Arrange→VisualParent` equivalents. **Two storage lanes (Fork A contract amendment):** per-type frozen tables for styled/direct properties, plus a property-global `UIProperty.GlobalEffects` slot for **attached properties** — a host type's table can freeze before the declaring panel's static ctor runs, so `GetEffects` returns `perTypeTable[id] | GlobalEffects`; without this lane `Grid.SetRow(button, 2)` would invalidate nothing. **Inherited-change routing:** Fork A's second carrier flows through an overridable `UIObject.OnInheritedPropertyChanged(in …)` virtual; `UIElement` runs the same dispatch — one root write fans out to inheriting descendants with re-raster bounded to zones actually containing affected elements. This routing is the *only* bridge from property changes to invalidation (invariant 2); cost per animated `RenderOffsetColumn` write at 60 fps is a flag-set.

**Zone model (canonical scene granularity, all subsystems).** One `Scene` per **render boundary**, not per element (per-element scenes make every list mutation a full-screen recomposite). Boundary predicates: ① window root, ② `Opacity < 1`, ③ `RenderOffset* ≠ 0` or animated, ④ `ClipToBounds`, ⑤ `CompositeClip != null`, ⑥ `ScrollContentPresenter` (always), ⑦ `IsRenderBoundary`. **Promotion is sticky until detach** — layer count never oscillates (the compositor full-recomposites on count change); ProgressBar's indeterminate layer persists once minted; no demotion valve in v1. Normative S8 guidance: animate the *container's* opacity, never template per-item opacity/offset animations. Mid-life promotion is four steps (re-raster old zone excluding the subtree; rent+raster new zone; rebuild `_zoneRoot` pointers; full recomposite) — two re-rasters + a recomposite, once per element lifetime. Zero-sized/Collapsed boundaries retain their scene (else rent 1×1) and publish `Clip = Rect.Empty` — the layer slot survives.

**Zone raster.** One reusable `RenderContext` per zone raster, origin re-pointed per element (no per-element allocation; do not capture). `RenderContext` re-exposes the Drawing surface in **element-local** coordinates as a thin veneer: each element render runs under **one pushed translate scope** (its zone-local origin) on Drawing's clip/translate stack, composing with the banded zone's ambient `PushTranslate` — no UI-side coordinate arithmetic (P2.5 ①/②; the stack covers every Drawing path, including formatted text, content, pen strokes, shadows, and titled boxes). Surface: `Set`, `FillRectangle`/`FillOpaque`, `DrawText`, `DrawLine/Box/Rectangle/TitledBox/Panel`, `DrawDropShadow/InnerShadow`, `DrawFormattedText`, `DrawContent` (capabilities auto-supplied), `BeginFigure` (closes the painter's ambient per-element figure, reopens it on dispose — junctions never bleed across sibling controls; scopes don't nest). No `PushClip`/`PushTranslate` surface — per-element clipping is a boundary concern. The paint walk skips non-Visible subtrees and boundary children (they raster in their own zones), ordered by the cached `(ZIndex, index)` array shared with hit testing. DEBUG guard: `SetValue`/tree mutation/`Invalidate*` inside `Render` throw. **Line breaks:** `RenderContext.DrawText` inherits Drawing's multi-line contract (P2.6 fixes batch — `\r\n`|`\n`|`\r` continue at the original start column one row down; returns the bounding `Size`; `\t` → one space + DEBUG diagnostic, other C0/C1 skipped; drawing design doc §13).

**Pinned surface rules:** `Panel.Background` paints `FillOpaque` (glyph-occluding; translucent brushes still frost — `FillRectangle` would let lower layers' glyphs show through); borders over opaque fills need `overwrite: true` (S8 recipe). Zone content hard-clips at the scene extent, so a boundary cannot paint its own drop shadow — **boundary-level shadows are the parent zone's job** (S8 decorator), window shadows are S4 chrome. In-zone overlap follows the painter's algorithm with Drawing's deferred-stroke semantics; elements that genuinely float over siblings must be boundaries (the one-property escape hatch).

### §5.6 Composite refresh, z-order, visibility

`RenderTree` (one per window; S4's `TopLevelSurface` wraps it) — `RunRenderPass()`: ① pending promotions, ② re-raster dirty zones (`Scene.RasterVersion` bumps only on actual re-raster), ③ walk the **boundary tree unconditionally every pass** (tens of boundaries, integer math — eliminates stale-accumulation bugs), accumulating absolute origin (+`RenderOffset*`, −ancestor scroll), opacity product, clip intersection (∩ `CompositeClip`), and effective visibility; publish `CompositeParameters` **only when different** (equality is the change detector — idle boundary = one compare). Effective visibility forces `Clip = Rect.Empty` for boundary layers under Hidden/Collapsed ancestors (layer retained, count stable). Hidden/Visible flips on boundaries are parameters-only; on non-boundaries they re-raster the zone; Collapsed additionally releases layout space. `CollectLayers(list, windowCol, windowRow, windowOpacity)` appends layers bottom-up in screen coordinates; `LayerCount` signals churn; `InvalidateAll()` for resize/renegotiation; `Detach()` returns scenes to the pool. **Z-order (normative, fed to S4):** within a zone, pre-order DFS with `(ZIndex, index)` sibling sort; layer order = boundary-tree DFS with the same key — a zone's own scene is always the lowest layer of its subtree (**zone-base rule**); across windows S4 concatenates `CollectLayers` output in window z-order into **one** flat `SceneCompositor.Composite` (window movement = parameters-only). Opacity groups multiply ancestor boundary opacities into descendants (approximation of true group opacity; scene nesting deferred).

### §5.7 Scrolling: banded scenes

`ScrollContentPresenter` (always a boundary; S8's ScrollViewer templates around it): `Content`, `CanScrollHorizontally/Vertically [AffectsMeasure]`, **styled** `ScrollOffsetColumn/Row [AffectsComposite]` — coerced into `[0, Extent − Viewport]` at set time AND re-coerced at end of arrange (content shrinking while scrolled snaps back same-frame; fires only on actual movement), storyboard-animatable (smooth scrolling works); ScrollViewer's `DirectProperty` offsets are two-way mirrors. `Extent`/`Viewport` are `DirectProperty` readbacks; extent is capped at `LayoutLimits.MaxScrollExtent` per axis. **Banded scene policy (DECISION):** the zone scene covers `[anchor − K, anchor + viewport + K)` per scrollable axis (clamped to extent), not the full extent — memory bounded by construction, no budget knob, no degraded mode. Scroll within the band is a composite slide (offset folds `anchor − ScrollOffset`; zero re-raster); the `ScrollOffset*` metadata handler runs the **re-anchor check** — an offset nearing a band edge re-centers the band and re-rasters once. Every draw path rides the band's `PushTranslate` and clips per cell at a band edge (P2.5 ① closed the former push-stack coverage gap — straddling draws used to be dropped/imperfect); `K` is sized so those edges sit outside the visible viewport clip anyway. Nested boundaries inherit scroll through the boundary walk (offsets subtract, clips intersect the viewport). Fragment caveat: images straddling a layer clip are pixel-cropped on Sixel but **suppressed** on Kitty/iTerm2 (pop-in/out) — S8 should snap images fully in/out of view. Virtualization remains the real answer for huge extents (deferred, with S8 items controls).

### §5.8 Hit testing

`RenderTree.HitTest(column, row)` is **the** hit test (S3's dispatcher does the surface-level scan via S4's `FilterMouseEvent`, then delegates intra-surface testing here; no per-element absolute-bounds cache exists anywhere). It walks the flat boundary-layer list **reversed** (topmost-first, exact reverse of `CollectLayers`), clip-rejects on the cached boundary clips the §5.6 walk just refreshed (`Rect.Empty` skips hidden-ancestor layers), transforms by the layer's effective offset (scroll handled for free), then descends the zone over the cached `(ZIndex, index)` array descending, reading live `Bounds`; `IsHitTestVisible`/`Visibility`/`HitTestCore` gate the leaf. Hit order is provably identical to composite order (zone-base rule included). Allocation-free integer-rect arithmetic — cheap enough for default-on any-event motion; S3's per-Move "element under pointer changed?" is a `HitTest` re-run.

### §5.9 Terminal caret service

S1 owns `ITerminalCaretService`: a publication registry — the focused editing control publishes an element-local caret position + `CursorShape`; S1 transforms to window coordinates during frame assembly via `TranslateToWindow` (post-layout, post-boundary-walk so render/scroll offsets are folded) and drops stale owners on detach; S4 folds the surface offset when assembling; S6 writes the back-buffer cursor state in `RenderFrame`. Positioning the *real* terminal cursor is also the one v1 accessibility affordance.

### §5.10 Cross-subsystem contracts (condensed)

- **Fork A:** REQUIRES the `UIObject` surface, two-lane `GetEffects` + `GlobalEffects` (amendment), the inherited-change virtual, `DirectProperty`/`SetAndRaise` for `Bounds`/`DesiredSize`/`Extent`/`Viewport`. PROVIDES inheritance-parent wiring on every attach/detach/reparent, inheritance-children spans, debug `VerifyAccess` on all tree/layout/render mutation (invariant 6).
- **Fork B:** REQUIRES `ClassSet`/`PseudoClassSet`/`Styles`/`Style`, `OnElementAttached/Detached` (detach bottom-up, batched, disposes watchers). PROVIDES the logical walk, `TemplatedParent` (barrier datum), attach-before-first-measure ordering.
- **S2:** PROVIDES logical attach/detach events, the permanent-detach teardown sweep (§5.1), DataContext inheritance riding the wiring.
- **S3:** PROVIDES `HitTest`, `VisualParent` routing topology + the popup logical-route continuation, `TranslateToWindow/ToLocal`, `LayoutUpdated`, `IsHitTestVisible`/`Visibility` semantics, `InteractionState.Disabled` push.
- **S4:** PROVIDES per-window `RenderTree` (`CollectLayers`/`LayerCount`/`HitTest`/`InvalidateAll`/`Detach`) + `LayoutManager` + the z-order rules; REQUIRES window z-order/positions/opacity at collect time, `Detach()` on close, the caret surface-offset fold. **The render system (S4) owns the single `SceneCompositor` and shared `ScenePool`.**
- **S5:** all animated writes via `AnimatedValueHandle<T>` on styled properties; slide/fade/reveal storyboards target `RenderOffsetColumn/Row`/`Opacity`/`CompositeClip`/`ScrollOffset*` — the `AffectsComposite` lane guarantees they never re-raster (invariant 3).
- **S6:** owns the screen `CellBuffer` + `FrameRenderer`; per-tick sequence = drain input → per window `RunLayoutPass` (once; `HasPendingWork`/`AbandonPendingLayout` for the idle/give-up paths) + `RunRenderPass` → one concatenated layer span → ONE `Composite` → `FrameRenderer.Render`. **Renegotiation transaction (pinned):** in one tick, re-stamp `RenderTree.Capabilities` + `InvalidateAll()` per window, replacement `FrameRenderer`, fresh `SceneCompositor` (built by the render system), styling re-stamp riding the same tick. **Resize transaction:** fresh `SceneCompositor` + per-window re-layout + full redraw. PROVIDES idle detection (no pending layout/dirty zones/composite refresh ⇒ zero bytes end-to-end).
- **S8:** `ApplyTemplate` seam (+ Collapsed caveat), `SetTemplatedParent`, visual-only adoption, banded SCP, the boundary-shadow pattern, the per-item-animation prohibition, the `FillOpaque`/`overwrite: true` recipe.
- **Lower layers (invariant 7):** consumes only existing public Drawing/Rendering surface; no additive changes required for v1.

### §5.11 Terminal-specific deviations (from WPF/Avalonia)

① Integer cells end-to-end — `LayoutMath.Unbounded` replaces `double.PositiveInfinity`; remainder policies replace `UseLayoutRounding`. ② Scene-per-boundary, not retained per-visual drawing — sticky promotion, layer-slot retention, the empty-clip hidden-layer trick. ③ `RenderContext` rides the per-element origin on one pushed Drawing translate scope per element render (P2.5 ②; the coverage gap that once forced UI-side self-translation closed at P2.5 ①); viewports/clips via sub-scene composition. ④ Per-element ambient figures discharge Drawing's no-nesting junction contract for every control author. ⑤ `RenderTransform` generality collapses to integer `RenderOffset*` + `Opacity` + `CompositeClip` — exactly the compositor's cheap path. ⑥ Flat compositor topology, tuned for tens of layers. ⑦ Hit testing is integer-rect descent in exact composite order (`HitTestCore` is the shaped-control escape hatch). ⑧ Hidden non-boundary content requires a zone repaint (no per-pixel compositing of retained visuals). ⑨ Opaque surfaces are a glyph-grid hazard — hence `Panel.Background` = `FillOpaque`. ⑩ No `IDisposable` element trees; pooled scenes release on detach, viewmodel subscriptions release via the teardown sweep.

### §5.12 Phasing

T0 tree + effects routing (unblocks Forks A/B and S2) — includes **de-risking probe P1** (zone raster benchmark, Phase-0 deliverable); T1 `LayoutMath` + Measure/Arrange + `LayoutManager` + Stack/Dock/Canvas (oracle-pinned WPF-derived layout matrix authored with T1); T2 Grid + WrapPanel; T3 zones/`RenderTree`/`RenderContext`/composite walk/hit testing; T4 banded `ScrollContentPresenter` + opacity groups + caret service; demo in `Cursorial.Demo` per repo convention (band padding K measured against a 10k-line log view before T4 exit).

### §5.13 Deferred (carry forward)

- **Grid `SharedSizeGroup`/`IsSharedSizeScope`** — cross-Grid column sharing for form layouts; additive to Grid measurement, no v1 consumer.
- **Layer splitting** (ancestor-zone content above a boundary descendant): multiplies layer count and destabilizes the z-stack; `IsRenderBoundary = true` is the overlap escape hatch.
- **True group opacity via scene nesting:** v1 ships the multiplicative approximation; nesting is recorded as cheap to add later.
- **Boundary demotion-on-idle:** escape valve for sticky-promotion pathologies; needs layer-churn hysteresis — container-opacity guidance covers v1.
- **Virtualizing layout / items panels:** the real answer to huge scroll extents; lands with S8 items controls.
- **Negative Canvas coordinates:** still clamped ≥ 0 (matrix L126); `RenderOffset*` covers the motivating cases. ~~Signed margins / signed arrange carriers~~ — **landed at P2.6** (matrix LD19): `Margin` is signed with WPF semantics and `Bounds` is the signed `LayoutRect`; the S5 `MarginsInterpolator` (P8) must interpolate signed (per-side linear, rounded, **no** zero-clamp).
- **Zone scene inflation for boundary self-shadows:** parent-zone/S4-chrome shadows cover v1; inflation touches clip math, hit testing, and offsets.
- **Grid spanning refinement + star-distribution hysteresis:** one-cell visual artifacts only.
- **Partial intra-zone re-raster:** scene granularity is the lower layers' invalidation unit; more boundaries is the existing knob.
- **Per-element composite blend modes:** `CompositeParameters.Mode` exists; a styled property awaits a use case (mode instances must be reused or parameter-equality caching breaks).

---

## §6 S2 — Data binding

`Cursorial.UI/Data/`, namespace `Cursorial.UI`. The binding engine is a *client* of Fork A's store — it produces values into `BindingEntry<T>`s, never arbitrates priority, never restores values (invariant 4) — and it is the entire data half of Fork B's `When` conditions. **Owns:** descriptors + expressions, the path parser, `DataContextProperty`, the watch-only surface, `INameScope` consumption + `UIElement.FindName`, binding diagnostics. **Not owned:** the `ValueStore`/eviction mechanics (Fork A), resource lookup — a `DynamicResource` setter value is not a binding (S7), `When` arming lifecycle (Fork B), focus determination (S3), `{Binding}` parsing (Fork C), collection views (§6.14).

### §6.1 Descriptors

Descriptors are construction-immutable (`init`-only) and instance-shareable — one `Binding` in a `Setter` serves every element it is armed on; all per-target state lives in expressions. The single engine contract: `internal abstract BindingExpressionBase CreateExpression(in BindingActivationContext)` (target, property, anchor, optional host `ValueFrame`, templated-parent + namescope ambience). All lanes produce the same expression shape.

```csharp
public enum BindingMode : byte { Default, OneWay, TwoWay, OneTime, OneWayToSource }
public enum UpdateSourceTrigger : byte { Default, PropertyChanged, LostFocus, Explicit } // Default ⇒ PropertyChanged (§6.11)

public abstract class BindingBase
{
    public BindingMode Mode { get; init; }
    public UpdateSourceTrigger UpdateSourceTrigger { get; init; }
    public object? FallbackValue { get; init; }         // UIProperty.UnsetValue sentinel = "none"
    public object? TargetNullValue { get; init; }       // ditto
    public string? StringFormat { get; init; }
    public CultureInfo? ConverterCulture { get; init; } // null ⇒ CurrentCulture (§6.11)
    public bool Trace { get; init; }                    // per-binding verbose diagnostics
}
public abstract class AnchoredBinding : BindingBase     // shared by BOTH path lanes; TemplateBinding excluded (fixed anchor)
{
    public object? Source { get; init; }                // the three anchors are mutually exclusive (validated; throws)
    public string? ElementName { get; init; }
    public RelativeSource? RelativeSource { get; init; }
}
public sealed class Binding : AnchoredBinding
{
    public Binding(); public Binding(string path);
    public string Path { get; init; }                   // "" or "." = the source itself
    public IValueConverter? Converter { get; init; }    public object? ConverterParameter { get; init; }
    public IPathTypeResolver? TypeResolver { get; init; }   // required only for "(Grid.Row)" segments
    public static CompiledBinding<TSource,TValue> Compiled<TSource,TValue>(Expression<Func<TSource,TValue>> path);
}
public sealed class RelativeSource      // Self | TemplatedParent | FindAncestor(type, level) — logical tree only in v1
{
    public static RelativeSource Self { get; }
    public static RelativeSource TemplatedParent { get; }
    public static RelativeSource Ancestor<T>(int level = 1) where T : UIElement;
}
public sealed class TemplateBinding : BindingBase       // one-way fast path to the templated parent;
{                                                       //   parse-time restricted to template bodies (Fork C)
    public TemplateBinding(UIProperty property);
    public UIProperty Property { get; }
    public IValueConverter? Converter { get; init; }    public object? ConverterParameter { get; init; }
}
public sealed class CompiledBinding<TSource,TValue> : AnchoredBinding   // typed end-to-end, zero reflection, AOT-clean
{
    public CompiledBinding(Func<TSource,TValue> getter, Action<TSource,TValue>? setter,
                           ReadOnlyMemory<CompiledPathStep> steps, string pathText);
    public IValueConverter? Converter { get; init; }    public object? ConverterParameter { get; init; }
}
public readonly record struct CompiledPathStep(string MemberName, Func<object?,object?> GetStep);
// MemberName "Item[]" for constant-index hops (INPC convention); INCC subscribed when the hop implements it.
public interface IValueConverter
{
    object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture);
    object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture);
    // UIProperty.UnsetValue = "no value"; NotSupportedException from ConvertBack = binding error, not crash
}
```

`TemplateBinding.CreateExpression` validates: a `Mode` other than Default/OneWay or a non-default trigger **throws**; Converter/FallbackValue/TargetNullValue/StringFormat are honored but forfeit the typed fast path (§6.7). Two-way reach-in = `new Binding { RelativeSource = RelativeSource.TemplatedParent, Mode = TwoWay }`.

`CompiledBinding` has three producers: `Binding.Compiled` (runtime lambda analysis — member + constant-index hops only, method calls/operators throw `FormatException`; cache the result in a `static readonly` field), the Fork C X4 generator (emits the ctor; `x:DataType` enables build-time path diagnostics), or by hand. Descriptor shape is v1, generator production is X4+ — second producer, no engine change.

### §6.2 Installation, expressions, watches

```csharp
public static class BindingOperations
{
    // LocalValue install. REPLACE-AND-DISPOSE: an existing LocalValue-lane expression for
    // (target, property) is disposed before the new install — one live expression per pair.
    public static BindingExpressionBase Install(UIObject target, UIProperty property, BindingBase binding);
    // FRAME-HOSTED install (binding-valued style setters; template content incl. TemplateBinding):
    // the entry lives IN hostFrame — within-slot ordering via StyleSortKey / template provenance;
    // evicted (→ expression disposal) on frame removal/retraction. Exempt from replace-and-dispose.
    public static BindingExpressionBase Install(UIObject target, UIProperty property, BindingBase binding, ValueFrame hostFrame);
    public static BindingExpressionBase? GetBindingExpression(UIObject target, UIProperty property); // LocalValue lane only
    // Watch-only arming (Fork B When seam): no store entry; values → callback. Unresolved ⇒ UnsetValue (= unmet).
    public static IBindingWatch Watch(UIElement anchor, BindingBase binding, Action<object?> onValueChanged);
    public static void TearDown(UIObject target);   // teardown-sweep half: disposes remaining registry-tracked expressions
}
public static class BindingExtensions
{ public static BindingExpressionBase SetBinding(this UIObject target, UIProperty property, BindingBase binding); }

public enum BindingStatus : byte { Inactive, Active, PathError, SourceMissing, Detached }
public abstract class BindingExpressionBase : IDisposable
{
    public BindingBase ParentBinding { get; }  public UIObject Target { get; }
    public UIProperty TargetProperty { get; }  // internal unregistered sentinel (Id −1) for watch-only
    public BindingStatus Status { get; }
    public BindingMode EffectiveMode { get; }  // Default resolved via BindsTwoWayByDefault (§6.6)
    public void UpdateTarget(); public void UpdateSource(); public void Dispose(); // idempotent, reentrancy-safe
}
public interface IBindingWatch : IDisposable
{
    object? Value { get; }    // UIProperty.UnsetValue while unresolved
    // No Pause/Resume: watchers stay live across styling DEACTIVATION edges — they are the
    // re-activation predicate (Fork B arbitration, ledger B16). Pause semantics exist only on
    // S7's ResourceSubscription. Lifetime ends at disarm/element detach via Dispose.
}
```

### §6.3 Paths, name scopes, FindName

`BindingPath.Parse(text, resolver?)` — grammar v1: property steps; `(Type.Property)` attached/styled segments (resolver required; the code-first default resolves owner short names via Fork A's `FindOwnersByShortName`, ambiguity ⇒ `FormatException` listing candidates); single-argument int/string indexers. Out (recorded): multi-arg indexers, source casts, XPath/slash, `Path=/` current-item.

Per-node accessor resolution, cached in a copy-on-write `AccessorCache`: (1) registered `UIProperty` on a `UIObject` hop → `UIPropertyAccessor` (untyped store lane + `AddObserver` — no reflection, no INPC needed); (2) CLR property — compiled delegate when dynamic code is supported, else raw `PropertyInfo` (honest AOT fallback; the compiled lane is the real AOT answer); (3) indexers — `IList` int fast path, reflection otherwise, plus `INotifyCollectionChanged` subscription and the INPC `"Item[]"` convention.

**Source change-notification ladder (pinned 2026-06-11 — MVVM sources are plain CLR objects):** per CLR-property hop, (1) the object implements `INotifyPropertyChanged` → subscribe (strong, explicit-lifetime, the death-edge table applies); (2) else a convention-matched **`[PropertyName]Changed` CLR event** (`EventHandler` or `EventHandler<EventArgs>`-compatible, discovered once per (type, property) and cached beside the accessor) → subscribe with the same death edges — this is WPF's `PropertyDescriptor.AddValueChanged` fallback without TypeDescriptor machinery or its global-table leak (our subscriptions die with the expression, by contract); (3) else the hop is observed-on-parent-change only (re-read when an upstream hop notifies) with a one-time `BindingDiagnostics` Info — the WPF one-time-read degradation. INPC wins when both exist (one subscription, not two). The compiled-binding lane changes only value ACCESS — notification rides this same ladder.

```csharp
public interface INameScope { void Register(string name, object element); object? Find(string name); }
public static class NameScope
{
    public static readonly AttachedProperty<INameScope?> NameScopeProperty;            // DOCUMENT scope
    internal static readonly AttachedProperty<INameScope?> TemplateNameScopeProperty;  // TEMPLATE scope — on the TEMPLATED PARENT
    public static INameScope? FindEnclosing(UIElement element);                        // guarded nearest-scope walk
}
```

Guarded walk: at each logical ancestor A, the template scope is consulted only when `element.TemplatedParent == A` — template parts see their template's names first, while a document content *child* of a templated control fails the guard and resolves document names, never part names (pinned conformance test). Scope producers: the Fork C loader sets the document scope on document roots; S8's `ApplyTemplate` sets `TemplateNameScopeProperty` on the control from `TemplateInstance.NameScope` (cleared in `Detach()`); `DataTemplate.Build` attaches its fresh scope to the item root's document slot via `SetNameScope`.

**S2 owns `UIElement.FindName(string)`** = `NameScope.FindEnclosing(this)?.Find(name)` — the template-aware lookup consumed by S5 storyboard targeting and app code (`window.FindName("toast")`).

### §6.4 DataContext and anchoring

```csharp
public static readonly StyledProperty<object?> DataContextProperty =
    UIProperty.Register<UIElement, object?>(nameof(DataContext), defaultValue: null, inherits: true);
```

An ordinary inherited styled property — Fork A's lazy-read/eager-notify inheritance is binding's backbone. It flows *through* template instances (the template barrier is a style-matching concept, not a property-inheritance one); `DataTemplate` realization sets `DataContext = item` on the instantiated root.

**DataContext-as-target special case (pinned):** a default-source binding whose target property *is* `DataContextProperty` (`DataContext="{Binding Sub}"`) anchors to the **logical parent's** DataContext — anchoring on the value it produces oscillates. Observer on the parent, re-anchored on `AttachedToLogicalTree`/`DetachedFromLogicalTree`; no parent yet ⇒ park `SourceMissing`, retry on attach. In the B0 oracle matrix.

Anchoring otherwise: **default** = target's DataContext (one `AddObserver(DataContextProperty)`; change ⇒ full rebind, including `OneTime` — WPF-consistent); **`Source`** = fixed, never re-resolved; **`ElementName`** = `FindEnclosing` walk — parks until attach/registration, `NameNotFound` traced only after attach; **Self / TemplatedParent / FindAncestor** — FindAncestor walks `LogicalParent` (crossing part → templated parent), resolved at attach, re-resolved on reparent; logical tree only in v1. Default-source bindings on non-`UIElement` targets are an install-time error — use `Source`. Whole-window DataContext swaps SHOULD be wrapped in Fork A's `DeferNotifications`.

### §6.5 Lifecycle: strong subscriptions, death edges, registry

**Decision: explicit-lifetime, strong INPC subscriptions; no weak-event manager.** Every install path has a contractual death edge:

- Local install → `ClearValue` / replace-and-dispose / teardown sweep (`IValueEvictionListener.OnEvicted`).
- Binding-valued `Setter` (frame-hosted) → style-frame cookie retraction evicts the entry.
- Template content (incl. `TemplateBinding`) → `TemplateInstance.Detach()` removes the instance's frames; **every expression created inside template content dies there** (feeds the leak tracker).
- `When` watcher → styling disarm, **including element detach** — detach retracts armed frames and disposes watchers (§6.8).
- Permanent detach (window close via S4; explicit teardown for app-discarded subtrees) → the **teardown sweep** (pinned S1 REQUIRES): bottom-up per element, `ValueStore.TearDown()` (evicts every entry, firing `OnEvicted`) then `BindingOperations.TearDown(element)` (remaining registry-tracked expressions: DirectProperty targets, watches anchored here).
- `DirectProperty` target on a non-element `UIObject` → caller-owned (documented loudly; DEBUG leak tracker flags specially).

Chain: eviction/sweep → `Dispose` → unsubscribe INPC/INCC/observers/triggers. Retraction is store-owned (invariant 4), so the store already enumerates entries on every frame/clear edge — eviction notification rides along free; "strong handlers cannot leak" is a contract, not a hope. The **per-target expression registry** (release builds: one inline list in a Fork-A-reserved opaque `UIObject` slot) backs replace-and-dispose, `GetBindingExpression`, `Explain`, and the sweep; DEBUG adds install-site capture + a weak-target sweep on window close reporting undisposed expressions by path and install site.

Reentrancy (pinned — push → observer → `When` flip → cookie retraction can evict the pushing expression's own frame mid-stack): `Dispose` idempotent via `Disposing`/`Disposed` flags; eviction-initiated dispose skips `entry.Dispose()` (the store is mid-eviction; `BindingEntryBase.Dispose` is additionally pinned idempotent and legal from within `OnEvicted`); every handler entry point checks `Disposed`; after any `entry.SetValue` returns, re-check `Disposed` before touching wiring state.

### §6.6 Value pipeline and write-back

Source → target: `ReadLeaf()` (UnsetValue on any unresolved hop) → Converter (exception ⇒ `ConversionFailed`, UnsetValue) → `TargetNullValue` → `StringFormat` (string/object targets only) → type conversion (assignable → IConvertible/enum fast paths → `XamlConverters.For`) → typed or boxed `entry.SetValue`. Any dead end ⇒ `FallbackValue` if specified, else **`entry.SetUnset()`** — never "push a default": the store promotes the next frame/priority. Steady-state leaf change: one ≤4-node scan, one pipeline pass, one store write; zero allocations except the boxed leaf in the reflection lane (box-interning covers common values).

Target → source (TwoWay/OneWayToSource), on a target effective-value change:

1. Skip if `IsPushingToTarget` (synchronous echo of our own push).
2. Skip if the new value equals `_lastPushedValue` per the property's `Comparer` — the *asynchronous*-echo discriminator (covers animation-handle disposal resurfacing our pushed base value); cannot lose a genuine edit — writing back the round-tripped value is definitionally a no-op.
3. Skip if the observer args' `BindingPriority == Animation` — animated values never round-trip (§6.11). Pinned with Fork A: `SetCurrentValue` observer args carry the priority of the replaced lane (`Animation` while animated — so mid-animation `SetCurrentValue` is also filtered — else the base lane's).
4. Genuine write → per trigger: `PropertyChanged` ⇒ write now. `LostFocus` ⇒ set `SourceDirty`, flush on S3's routed `LostFocusEvent` (physical focus moving off the element) **or** on S3's edit-commit notification for terminal focus-out — terminal focus-out retains keyboard focus and raises no LostFocus (S3 model; refocus restores state); the non-focus-moving notification exists precisely so pending edits still flush. Trigger unavailable ⇒ one-time Warning, fall back to PropertyChanged. `Explicit` ⇒ dirty until `UpdateSource()`.

**Value coexistence (pinned, Fork A canonical):** within one priority, last writer wins and a binding's push counts as a write. `SetValue` at LocalValue does **not** kill a local binding — transient override; **`ClearValue` is the documented kill** (removes the value *and* detaches local-priority bindings). Control-author contract (PROVIDES to S8): control-internal writes (TextBox keystrokes, slider drags) use `SetCurrentValue` — the consumer's binding may be frame-hosted at Style provenance, and a LocalValue write would permanently shadow it; `SetCurrentValue` replaces the effective value in place. Both write APIs feed write-back; steps 1–4 discriminate echoes, not APIs.

`WriteToSource()`: ConvertBack (UnsetValue/exception ⇒ `ConvertBackFailed`, no write); `TargetNullValue` reverse mapping (target equals it ⇒ write null); StringFormat reverse parse **only** when the format is exactly `"{0}"` — composite formats ⇒ `ConvertBackFailed` (parsing a formatted prefix back is corruption, not conversion); no-converter type gaps via the same conversion ladder; leaf write through the node accessor or `CompiledBinding.Setter`. `OneWayToSource` re-resolves the chain from the anchor on every write (its nodes are unsubscribed and go stale; ≤4 cheap reads); TwoWay keeps live nodes. Guarded by `IsWritingToSource`; source INPC raised during the write (VM normalization/clamping) coalesces into one post-write re-read — the WPF round-trip, kept.

`EffectiveMode`: `Default` resolves at install from `BindsTwoWayByDefault`; a read-only leaf at wiring degrades to OneWay with a one-time Warning, re-evaluated per rewire. `OneWayToSource` keeps the anchor observer, creates no path subscriptions, installs a never-producing entry (lifetime/discoverability only; the store treats never-set entries as contributing nothing), and pushes target → source at activation.

### §6.7 Compiled lane

`CompiledBindingExpression<TSource,TValue>` shares anchoring (full `AnchoredBinding` surface), triggers, and lifecycle with the reflection lane; replaces path machinery: read = `_anchor.Root is TSource s ? Getter(s) : …` (root type mismatch, incl. ElementName/FindAncestor anchors, ⇒ `SourceTypeMismatch` + UnsetValue — styling: unmet); `Steps` drive INPC re-wiring but values come from one whole-chain `Getter` call (struct intermediates just work). When the target is `StyledProperty<TValue>` with no converter/StringFormat, push via `BindingEntry<TValue>.SetValue(v)` — **zero boxing, zero steady-state allocation**, the binding analog of `AnimatedValueHandle<T>`; sized for bound `AffectsComposite` properties changing every frame under VM-driven animation (re-composite, never re-raster — invariant 3 with zero engine awareness). `TemplateBinding`'s untyped→typed bridge: internal virtuals `UIProperty.CreateEntry`/`CreateTemplateTransfer` overridden by `StyledProperty<T>` — double dispatch on property identity, no reflection, no `MakeGenericType`.

### §6.8 Watch-only surface (Fork B seam)

`Watch` builds the same expression with no store entry and a callback sink. Pinned semantics: unresolved path/source ⇒ callback receives `UIProperty.UnsetValue` (styling pins that as "unmet"); anchor DataContext change ⇒ automatic rebind + re-deliver; delivery is synchronous on the UI thread, so a VM-driven `When` flip participates in the same frame. Self-source (`RelativeSource.Self`) and ancestor-source (`FindAncestor`) ship in B0 — Fork B's numbered requirement, whole at spine.

**Watcher lifetime = armed lifetime, and element detach ends it:** detach retracts armed style frames (cookie batch retraction) and **disposes** the rules' watchers; reattach rebuilds arming from scratch. Across *deactivation* edges (a rule dropping out of contention while still structurally matched) watchers stay live — they are the re-activation predicate, so there is nothing to park (ledger B16; pause semantics exist only on S7's `ResourceSubscription`, which has a genuine caller). Watches are registry-tracked under their anchor; the teardown sweep is the backstop. Binding-valued setters: the styling engine calls `Install(element, property, binding, frame)` passing its own `ValueFrame` per armed rule; cookie retraction kills the expression with zero styling-side bookkeeping.

### §6.9 Threading and frame coherence

All entry points are UI-thread-only (`VerifyAccess` debug-asserted — invariant 6). Same-thread INPC applies synchronously — a VM change during frame N's input drain reaches the store before layout (invariant 1, no machinery). Foreign-thread INPC sets a per-node dirty bitmask via `Interlocked.Or` and posts one coalesced drain via `IUIDispatcher.Post`; the drain rewires from the lowest set bit; N changes between frames coalesce into one rewire+push. S6 owns the concrete `UIDispatcher`; this engine consumes the two-method seam (`CheckAccess`/`Post`) with the pin: posted work runs in the next frame's dispatch drain **before layout**, and **`Post` MUST wake the event-driven frame loop** when no drain is pending (the `InteractiveDemo.Invalidate()` Interlocked-flag pattern) — a background VM update must not wait for unrelated input. `PauseIOAsync`/`RenegotiateAsync` need nothing: bindings touch only the store, never the terminal (invariant 2).

### §6.10 Diagnostics

Never write to the terminal — stdout *is* the screen, and a stray write desyncs `FrameRenderer` (sole owner of SGR/cursor state). `BindingDiagnostics`: `Level` (default Error); a 256-entry always-on ring (`RecentEvents`, ~30 KB); `TraceEmitted` + `AddSink(IBindingTraceSink)`; env-gated file sink (`CURSORIAL_BINDING_TRACE`, mirroring `CURSORIAL_TRACE_OUTPUT`); `DumpTo(TextWriter)` for S6 to call **after** session disposal; and **`Explain(target, property)`** — every expression across all lanes (LocalValue / frame-hosted / watch-only / DirectProperty): status, resolved source chain, last value, last failure. Release-build, registry-backed; the binding half of the F12 panel next to `StyleDiagnostics.Explain`. Pinned ring policy: Warning/Error events are always constructed and ring-recorded (failure paths only — the happy path allocates nothing for diagnostics); Verbose gated on `Level`/`Trace`; sinks receive severity ≥ `Level`. `BindingTraceEvent` carries level, `BindingFailureKind`, path, target description, message, `Environment.TickCount64`.

### §6.11 Terminal-specific divergences (recorded)

1. **Strong subscriptions** over WeakEventManager — every death is contractual (§6.5); at 10² elements / 20–60 fps, weak indirection is pure cost; the DEBUG leak tracker replaces the safety weak events would buy.
2. **`UpdateSourceTrigger.Default` = `PropertyChanged`** (WPF defaults TextBox.Text to LostFocus) — a keystroke source update is a few struct copies + one store write, and live `When` conditions (Save enabling as you type) are the showcase. LostFocus remains the documented choice for parse-on-commit fields.
3. **Culture = `CurrentCulture`** (WPF's hardcoded en-US is a recorded wart); per-binding `ConverterCulture` overrides. Terminal apps are frequently ssh'd, locale-sensitive tools.
4. **Animated values never round-trip to the source** (§6.6 steps 2–3): `Animation` priority sits above LocalValue and writes at frame rate; WPF-faithful TwoWay would spam the VM at 50 fps with quantized intermediates.
5. **No `IsAsync`/async bindings** — one UI thread, one frame loop; the cross-thread marshal (§6.9) is the supported pattern; slow reads belong in the VM.
6. **`LostFocus` rides physical focus moves plus the terminal-focus-out edit-commit pulse.** Keyboard focus is *retained* on terminal focus-out — no LostFocus is raised (S3 model; refocus restores state) — but S3's edit-commit notification flushes pending edits anyway. Rationale: a terminal app can be killed from outside at any moment (SIGHUP, ssh drop) — commit on departure is the safe default; and "menu takes focus" genuinely ends the edit gesture in a cell-grid UI, so cross-scope physical moves flush where WPF's logical-focus trigger would not.

### §6.12 Cross-subsystem contracts (condensed)

**REQUIRES Fork A (store):** `BindingEntryBase { SetValue(object?); SetUnset(); Dispose() /* idempotent, legal from OnEvicted */ }`, `BindingEntry<T>.SetValue(T)`, `IValueEvictionListener.OnEvicted`; on `UIObject`: free-standing `Bind<T>`/`BindUntyped` (**LocalValue only** — Style-slot contributions MUST be frame-hosted; Animation is `AnimatedValueHandle<T>` territory), frame-hosted `BindInFrame<T>`/`BindInFrameUntyped(property, ValueFrame, listener)`, typed + untyped `AddObserver` (untyped change args MUST carry `BindingPriority`), untyped `GetValue`, one reserved opaque registry slot. Behavioral pins (countersigned): `SetCurrentValue` args priority = replaced lane; observer delivery on inherited changes on entry-less descendants (DataContext depends on it); `ClearValue` evicts local-priority entries; frame removal/retraction evicts frame-hosted entries; `ValueStore.TearDown()` evicts everything, firing `OnEvicted` per entry; metadata `BindsTwoWayByDefault`/`NotDataBindable`/`Comparer`; `UIProperty.UnsetValue` + the internal sentinel ctor; `(Type,string) → UIProperty` lookup + `FindOwnersByShortName`; entries valueless until first `SetValue`; the `CreateEntry`/`CreateTemplateTransfer` virtuals; DirectProperty bindings via getter/setter delegates + observer — no entry, no priority arbitration, no restoration.

**REQUIRES S1 (tree):** `LogicalParent`, `TemplatedParent`, `AttachedToLogicalTree`/`DetachedFromLogicalTree`; the pinned teardown contract — on permanent detach, walk bottom-up and per element call `ValueStore.TearDown()` then `BindingOperations.TearDown(element)`; no entry or expression survives.

**REQUIRES S3 (input/focus):** the routed `LostFocusEvent(FocusChangedEventArgs)`, raised on the UI thread when physical focus moves off an element; and the non-focus-moving **edit-commit notification** (`InputDispatcher.EditCommitRequested`) raised on terminal focus-out (`FocusEvent { HasFocus: false }`) — terminal focus-out never moves keyboard focus or raises LostFocus; the notification is the engine's flush trigger (§6.6 step 4, §6.11.6).

**REQUIRES S6 (app model):** `IUIDispatcher { bool CheckAccess(); void Post(Action); }` with the loop-wake pin (§6.9); S6 owns the concrete dispatcher and frame-drain ordering; no priority tiers (invariant 1).

**REQUIRES Fork C (XAML):** the `IDeferredValue.AttachTo` seam (extension results never flow through `SetValue` as sentinels), attach context carrying the host frame for template content; xmlns-aware `IPathTypeResolver`; `XamlConverters.For(Type)`; namescope population per §6.3's pinned attachment points; later, `x:DataType` → generator-produced `CompiledBinding`s.

**PROVIDES:** to Fork B — `Watch` (§6.8) + frame-hosted `Install`; to Fork C — `BindingBase.AttachTo` (LocalValue or `ctx.HostFrame`), `TemplateBinding` as the parse-restricted node, `BindingPath.Parse` with position info; to the template engine — the `TemplateBinding` fast path + the every-template-expression-dies-on-`Detach` guarantee; to S5 — `UIElement.FindName` for storyboard targeting; to S8 — the `SetCurrentValue` contract and `GetBindingExpression(...).UpdateSource()` for commit gestures (Enter in a TextBox).

### §6.13 Phasing

- **B0 — spine** *(unblocks Fork B `When` + element bring-up)*: descriptors; path parser; reflection expression + accessor cache; strong INPC/INCC/observer wiring; `DataContextProperty` + the as-target special case; five modes + read-only-leaf degradation; PropertyChanged/Explicit triggers; Source/Self/TemplatedParent/**FindAncestor**; full pipeline + reverse lane; free-standing + frame-hosted entries + eviction lifecycle; expression registry + replace-and-dispose + teardown integration + leak tracker; **`Watch`** incl. ancestor-source; diagnostics ring. Oracle-pinned test matrix (fallback/null/format permutations, DataContext-self, echo suppression incl. animation-handle disposal) authored before the engine.
- **B1:** `ElementName` + `FindEnclosing` + `FindName`; the `LostFocus` trigger (gated on S3's events landing); `Explain`; namescope conformance tests (content-child-vs-part-names).
- **B2** *(with the template engine)*: `TemplateBinding` fast path + typed bridge; `Detach`-eviction conformance against the `ValueFrame` kit; `Binding.Compiled` + typed push lane.
- **B3** *(with X4)*: generator-emitted descriptors; no engine change — second producer only.

### §6.14 Deferred (carry forward)

- **Collection views (sort/filter/current-item)** — jointly owned by S2+S8 when it lands; v1 binds pre-shaped collections; the engine stays collection-blind.
- **MultiBinding / PriorityBinding** — no v1 consumer in the control set; `CreateExpression` is the seam (N watch-legs + an `IMultiValueConverter`); re-addable additively.
- **INotifyDataErrorInfo validation** — terminal forms are a later concern; seam reserved: `BindingStatus` + a future `IBindingValidationSink` at write-back, `:data-error` pseudo-class registered by controls.
- **`Delay` (debounce)** — needs a clock; revisit against S5's `UITimer`.
- **Typed `IValueConverter<TIn,TOut>`** — boxed converters acceptable at install-grade frequency.
- **Weak-subscription backstop mode** — only if real consumer lifecycles demand it (§6.5).
- **Multi-argument indexers / path source casts** — no demonstrated need.
- **`RelativeSourceMode.FindVisualAncestor`** — logical-only v1; re-add if panel-generated intermediates break expectations.

---

## §7 S3 — Input routing, focus, and access keys

Namespace `Cursorial.UI.Input` (geometry + `AccessText` in `Cursorial.UI`). S3 owns the dispatch pipeline from the moment S6's pump hands an `InputEvent` to the UI thread during frame N's input drain: classification, route building, tunneling/bubbling, `Handled`; the UI event vocabulary (`Preview*`/main pairs for Key/TextInput/Mouse, `MouseEnter/Leave`, `GotFocus/LostFocus`, `LostMouseCapture`); surface-level hit-test orchestration, mouse capture, hover-chain maintenance; `InteractionState` writes for `PointerOver/Focused/FocusWithin/FocusVisible/AccessKeyCue` plus the `Pressed` seam; keyboard focus (physical + logical scopes, Tab/directional navigation); access keys; commands/`KeyBinding`s. S3 does **not** own: the pump and frame loop (S6); window activation/z-order/light-dismiss *policy* (S4 — S3 consumes `FilterMouseEvent` and owns the consequences); intra-surface hit-test z-order (S1's `RenderTree.HitTest`); `:active-window` (S4 writes it) and `:disabled` (S1's effective-IsEnabled pipeline); styling reaction to state (Fork B); `ResizeEvent`/`DeviceResponseEvent`/`UnknownEvent` (classified `NotUIInput`, returned to S6 — device responses must reach whatever issued the query).

### §7.1 Geometry: the signed carrier

Composite-inclusive positions can be negative (window mid-slide, scrolled content); Rendering's `Rect` is ushort-backed/non-negative and reserved for arranged pre-composite geometry. Everything hit-testing-facing uses:

```csharp
public readonly record struct CellRect(int Column, int Row, int Columns, int Rows)   // Cursorial.UI
{
    public bool Contains(CellPosition p);
    public CellRect Translate(int columns, int rows);                  // plain int math; never throws
    public static CellRect FromRect(in Cursorial.Rendering.Rect r);    // widening, always safe
}
```

### §7.2 Routed events

`RoutedEvent` registry: `RoutingStrategy { Direct, Bubble, Tunnel }`; `RoutedEvent<TArgs>.Register(name, strategy, ownerType)`; dense `GlobalIndex` into per-element handler stores. `RoutedEventArgs` carries `RoutedEvent`, `OriginalSource`, `Source` (v1: always `== OriginalSource`; template source adjustment deferred), `Handled`.

**Args ownership (pooling contract):** framework dispatch rents from a per-concrete-type free-list (nested `RaiseEvent` rents a *distinct* instance; depth debug-capped 16); rented args are valid only during their dispatch and debug-stamped (stale access throws). Caller-`new` args are caller-owned, never pooled. Wrapped device records (`KeyEvent`, `MouseEvent`) are immutable and always retainable — handlers copy the record, never the pooled args. `protected TArgs RentEvent<TArgs>(...)` for control authors; one rented args → exactly one `RaiseEvent`.

Args types (each wraps the device record): `KeyEventArgs` (`Key`, `Modifiers` — lock-free, match shortcuts on this, never `ExtendedModifiers`; `Text`, `IsRepeat`, `RepeatCount`); `TextInputEventArgs` (`Text`, `FromPaste`); `MouseEventArgs` (`Surface` — S4's `TopLevelSurface`; `SurfacePosition`; **`ScreenPosition`** — terminal coords, S4's screen-anchored drag math depends on it; `GetPosition(UIElement relativeTo)` — element-local via terminal coords + S1's `TranslateToLocal`, well-defined cross-surface, may be negative under capture); `MouseButtonEventArgs` (`Button`, `ClickCount` — reads >1 **only on MouseDown** under the mandated pipeline; double-click logic belongs in `OnMouseDown`); `MouseWheelEventArgs` (1/120-notch deltas, `LinesPerNotch` hint); `FocusChangedEventArgs` (`OldFocus`, `NewFocus`, `FocusNavigationMethod { Programmatic, Pointer, Tab, Directional, AccessKey, Restore }`).

### §7.3 UIElement input surface (S3's slice)

```csharp
public partial class UIElement : UIObject, IInteractionStateSink
{
    // Routed-event fields (Preview/main Key, TextInput, MouseDown/Up/Move/Wheel; Enter/Leave + LostMouseCapture Direct;
    // GotFocus/LostFocus Bubble) + AddHandler<TArgs>(evt, handler, bool handledEventsToo = false) / RemoveHandler / RaiseEvent.
    // The On* virtuals ARE the class-handler stage: invoked at every route node before instance handlers, skipped once Handled.
    public static readonly StyledProperty<bool> FocusableProperty;        // default false; controls OverrideDefault
    public static readonly StyledProperty<bool> IsTabStopProperty;        // default true
    public static readonly StyledProperty<int>  TabIndexProperty;         // default int.MaxValue; ties → document order
    public static readonly StyledProperty<bool> IsHitTestVisibleProperty; // default true
    public static StyledProperty<bool> IsFocusedProperty { get; }             // read-only; UIPropertyKey<bool> INTERNAL —
    public static StyledProperty<bool> IsKeyboardFocusWithinProperty { get; } // the key IS the write right (Fork A)
    public InputBindingCollection InputBindings { get; }                  // lazy-alloc; swept during bubble
    public bool Focus(FocusNavigationMethod method = Programmatic);
    public bool CaptureMouse(); public void ReleaseMouseCapture();        // THE capture surface (no separate interface)
    protected void SetInteractionState(InteractionState state, bool active);  // framework/control authors; Pressed flips
        // additionally fan into the dispatcher's pressed-holder set (styling contract C8)
    protected virtual bool IsEnabledCore => true;                         // command CanExecute coupling (declared §5.1)
    protected void InvalidateIsEnabledCore();                             // S1 recomputes effective-enabled + :disabled
    protected virtual bool HitTestCore(int column, int row) => true;      // consulted by S1's RenderTree.HitTest (declared §5.1)
}
```

### §7.4 The dispatcher

```csharp
public enum InputDispatchResult : byte { DispatchedHandled, DispatchedUnhandled, NotUIInput }

public sealed class InputDispatcher : IWindowFocusHooks                   // S4's hooks: OnWindowBlocked /
{                                                                         // OnSurfaceClosed / deactivation clears
    public void OnCapabilitiesChanged(TerminalCapabilities caps);  // startup + after every RenegotiateAsync; also
        // unconditionally clears Alt/sticky-cue state (renegotiation parks the pump; an Alt Up can vanish)
    public InputDispatchResult ProcessEvent(InputEvent e);         // THE entry point; UI thread, frame N's drain.
        // DispatchedHandled iff a handler/tail consumed it — S6's default gestures (Ctrl+C exit) key on Unhandled.
    public void UpdateHover();              // S6 calls once per rendered frame, after layout AND composite finalize:
        // hover under layout moves / composite slides / scrolls / surface changes + detach-deferred hover work.
        // No-op until the first real mouse event (LastPointerPosition == null).
    public void OnSurfacesChanged();        // S4 MUST call on every surface open/close/modal/z change, even mid-drain:
        // synchronously re-validates capture (force-release if surface closed/blocked) + marks hover dirty.
    public UIElement? MouseCaptureTarget { get; }
    public bool CaptureMouse(UIElement element); public void ReleaseMouseCapture(UIElement element);
    public CellPosition? LastPointerPosition { get; }  public MouseButtons ButtonsHeld { get; }
    public InputModality LastModality { get; }                            // feeds :focus-visible
    public UIElement? HitTest(CellPosition terminal, out TopLevelSurface surface);  // S4 cursor-shape policy; tooltips
    public event Action<bool>? TerminalFocusChanged;                      // S4 listens
    public event HoverChangedHandler? HoverChanged;    // (removedChain, addedChain) pooled snapshots, raised in hover
        // phase 2 — ToolTipService's (S8) observation hook; consumers must not retain the spans
    public event Action<UIElement>? EditCommitRequested;  // terminal focus-out, focus RETAINED: S2 flushes
        // UpdateSourceTrigger.LostFocus edits on this — no LostFocus is raised (recorded divergence)
}
```

### §7.5 Classification and key dispatch

`ProcessEvent` switches on record type: `KeyEvent` → key dispatch; `MouseEvent` → mouse dispatch (`Kind == Click` ignored — pipeline contract forbids synthesized Clicks; defensive no-op); `FocusEvent{HasFocus:false}` → `AccessKeyManager.OnTerminalFocusLost()`, capture force-release, hover-chain clear, **every pressed-holder cleared** (C8 — covers keyboard-held press visuals that never took capture), `EditCommitRequested(focused)`, then `TerminalFocusChanged(false)`; **keyboard focus is retained** (refocus restores state intact). `PasteEvent` → `TextInput { FromPaste = true }` at the focused element (no `OnPaste` event exists; TextBox keys newline-flattening on the flag). `ResizeEvent`/`DeviceResponseEvent`/`UnknownEvent`/`PointerEvent` → `NotUIInput`.

KeyDown order: **(1) pre-stage** (skips `Synthesized: true` events — a stray `KeyReleaseSynthesizer` must not corrupt the Alt bracket): Alt-side tracking; stale-Alt inference (non-Alt-key Down lacking the Alt bit while a side bit is set ⇒ lost Up ⇒ clear bracket); sticky-cue Esc **consumed** (menu mode is modal to Esc). **(2) Target** = `FocusedElement ?? ActiveRoot`; both null ⇒ dropped (`DispatchedUnhandled`, never routed to "topmost" — topmost ≠ active). **(3) Tunnel** `PreviewKeyDown`. **(4) Bubble** `KeyDown`: virtual → instance handlers → `InputBindings` sweep (first matching gesture whose command `CanExecute` executes, `Handled = true`). **(5) Unhandled tail — access keys** (`ProcessKeyDown`, incl. F10 menu-mode entry). **(6) Unhandled tail — navigation**: Tab/Shift+Tab → `MoveFocus`; plain arrows → directional nav; marks handled only when focus actually moved. **(7) TextInput synthesis**: unhandled `Key.Character` with `(Modifiers & (Control|Alt|Super|Hyper|Meta)) == 0` (Shift allowed) → `PreviewTextInput`/`TextInput`. Alt-modified keys **never** produce TextInput (reserved for access keys/bindings; AltGr text arrives without the Alt bit — control-author contract: text widgets must not handle Alt-modified keys). KeyUp: pre-stage + steps 2–4 only; never drives framework activation.

**Route building:** visual-parent walk target→surface root into pooled scratch (free-list for nested dispatch); **at a surface root with `LogicalParent != null` the route continues via the logical parent** (popup root → `Popup` element → host chain) — Esc inside a menu closes the popup, not the window (pinned test). Handler exceptions propagate to S6's drain (fail fast); pooled resources/interaction scopes are `using`-protected along the unwind. Nested `RaiseEvent` legal; `ProcessEvent` re-entry is a debug assert.

### §7.6 Mouse dispatch, hit testing, hover, capture

**Gating (S4's protocol, consumed):** capture-first bypass — with capture held, events route to the capture target (S4 still gets `ObservePointerPosition` for pointer-cell freshness). Otherwise `windowTopology.FilterMouseEvent(e)` returns `Swallowed` (the WM performed light dismiss / blocked-press attention / activation-on-press internally) or `Route` with the target surface. S3 owns hover/enter-leave and all intra-surface routing.

**Hit testing:** the dispatcher does the *surface-level scan only* — S4's surfaces topmost-first, bounds are signed `CellRect` in terminal coords **reflecting composite offsets** (a window mid-slide hit-tests where it is drawn); blocked surfaces **occlude** (no hover/wheel bleeds below; no hit element); surfaces are **hit-opaque within bounds** (a point no descendant claims hits the surface root, never a surface below — terminal windows are opaque rectangles). Intra-surface descent delegates to **S1's `RenderTree.HitTest(surfaceLocal)`**, which provably mirrors composite order (ZIndex, boundary clips, zone-base rule) and consults `IsHitTestVisible`/`HitTestCore`. S3 keeps no per-element bounds cache and imposes no clipping defaults — pruning comes from `RenderTree`'s boundary clips (`ClipToBounds` stays S1's, default false). Cost: O(surfaces + descent) integer tests, zero steady-state allocation — the budget for any-event motion (Move per cell crossed, on by default).

**Hover chain:** retained pooled chain root→leaf. On Move/Drag and per-frame `UpdateHover`: hit test → common prefix → **phase 1 (state)**: one `using`-protected `BeginInteractionUpdate` batch clears/sets `PointerOver` on the removed/added suffixes, snapshots both, disposes the scope; **phase 2 (events)**: `MouseLeave` deepest-first, `MouseEnter` outermost-first over the snapshots, then `HoverChanged`, then `PreviewMouseMove`/`MouseMove` to the dispatch target. Handlers observe post-restyle state; detach during a raise only marks the deferred refresh. `MouseCapabilities.Motion == false` ⇒ the bits never set (capability-honest, no polyfill).

**Capture:** routing policy, not OS capture — terminals keep reporting drags regardless. Granted only to attached, effectively-visible elements, in one of two modes (`CaptureMode { Element, SubTree }`; "None" is just the absence of a holder): **`Element`** (default) routes every uncaptured-position event to the holder; **`SubTree`** (the Menu/ComboBox stance) routes normally while the pointer is over the holder or an element in its visual-then-logical subtree — descendants stay interactive — and redirects to the holder only for positions outside it (a miss redirects too). The subtree test walks the same `VisualParent ?? LogicalParent` hop the route uses, so a captured menu's popup items count as inside. A re-capture by the current holder just swaps the mode (no transfer, no `LostMouseCapture`). Force-released (Direct `LostMouseCapture`) on explicit release, target detach, terminal focus-out, and — via `OnSurfacesChanged` — the target's surface closing or becoming modal-blocked (a modal opened by keyboard mid-drag releases capture in that same call). Hit testing still runs under capture so `:pointerover` stays honest; wheel always targets the *hit* element regardless of mode. Under `Element` capture a button press short-circuits the hit test entirely, so a captured gesture never triggers the window manager's press-time light-dismiss / activation.

**ClickCount contract:** counts arrive baked in by `MouseClickSynthesizer` (deterministic, timestamp-based — no `GetDoubleClickTime()` on a terminal); the dispatcher adds no timing. S6's pipeline default **is** the contract: `WithClickSynthesis(new MouseClickOptions { ClickCount = ClickCountTarget.ButtonDown, SynthesizeClickEvents = false })`; no `KeyReleaseSynthesizer` in the default pipeline (synthetic Ups would lie to the access-key gate).

**Mouse cursor (native pointer shape) — added 2026-06-11; landed in the P2.5 batch (stage ④).** Terminals reporting `OutputProtocolCapabilities.MouseCursorShape` support setting the host OS pointer shape (OSC 22, Kitty pointer-shape protocol; Core ships `MouseCursorWriter` + the `MouseCursorShape` enum). Element-level overrides: a `Cursor` styled property on `UIElement` typed `MouseCursorShape?` (Core's enum directly — no UI wrapper type; null = unset, default null, `[no invalidation]` — the cursor is not cell content; a change takes effect at the next re-resolution, i.e. the next rendered frame's `UpdateHover` at the latest). Resolution rides the existing hover machinery: whenever the hover chain or capture target changes (Move, `UpdateHover`, capture grant/release), the effective shape = the capture target's resolved cursor while capture is held (its self→root walk — capture redirects the pointer's meaning, so it owns the shape), else the first non-null `Cursor` walking the hover chain leaf→root, else the terminal default. The resolution + equality gate live in S3 (`UpdateEffectiveCursorShape`, allocation-free, behind the capability gate — no resolution, no tracking when unsupported); capture transitions resolve **once, after the full transition settles** — `ForceReleaseCapture` itself never re-resolves; each of its callers owns exactly one re-resolution (after the new holder installs / the hover chain clears or truncates), so a capture transfer or focus-out cannot emit an intermediate shape's redundant bytes; S6 owns emission through an internal change seam: each effective-shape change queues one OSC 22 sequence through `QueueControlSequence` (`MouseCursorWriter.WriteSet` — "back to the default" is an explicit `WriteSet(Default)`, never the empty-payload `WriteReset`: kitty honors empty-as-reset, **Ghostty ignores it** and strands the previous shape — observed live 2026-06-11). Capability-gated on `MouseCursorShape` (silently inert otherwise — no polyfill); `WriteSet(Default)` in the canonical teardown (capability-gated) and on `RenegotiateAsync` re-entry (under the OLD gate, in the same flush as the old renderer's close; the dispatcher's capability fan-out then forgets its tracked shape and re-emits an active shape under the NEW gate — at most one set per renegotiation). Controls set their conventional shapes in templates at P5+ (TextBox → text/I-beam, hyperlink-likes → pointer); window resize grips override during drag at P7.

### §7.7 Focus

`FocusManager`: `FocusedElement` (one per app); `ActiveRoot` (recorded from `OnWindowActivated/Deactivated` — the load-bearing target for key/paste fallback and the access-key fallback scope; null + null focus ⇒ keys dropped); `SetFocus(target, method)`; `ClearFocus()`; attached `IsFocusScopeProperty` (window/popup/menu/toolbar roots true) + `FocusedElementProperty` scope memory; `GetFocusScope`/`GetFocusedElement`; `MoveFocus(direction)` (null focus starts from `ActiveRoot`'s first/last tab-ordered focusable); **`FindNext(UIElement from)`** — pure query over the tab-order collection (Label targeting, S8). `KeyboardNavigation` attached properties: `TabNavigationProperty` (`KeyboardNavigationMode { Continue, Cycle, None, Once }` — **`Once` ships in v1**: container is a single tab stop, ListBox items hosts; `Local` deferred), `DirectionalNavigationProperty` (`{ None, Contained, Cycle }`).

`SetFocus`: validate (attached/`Focusable`/enabled/visible, no ancestor fallback) → one `BeginInteractionUpdate` batch flips `Focused/FocusVisible/FocusWithin` along the diverging chains (`FocusVisible` when method ∈ {Tab, Directional, AccessKey, Restore} or Programmatic ∧ keyboard modality — **`Restore` always sets it**, recorded divergence from Chrome heuristics) and mirrors `IsFocused`/`IsKeyboardFocusWithin` via the internal `UIPropertyKey`s (store-owned, never a style) → nearest scope records memory → `AccessKeyManager.OnFocusChanged(method)` (Pointer clears sticky cue) → `LostFocus` then `GotFocus` bubble, state committed before events; re-entrant `SetFocus` last-wins, depth-capped 8.

**Activation/restore:** `OnWindowActivated` restores scope memory (validated), else first tab-ordered focusable, else none; `method = Restore`. Menus: S4 pushes the popup as a focus scope; closing re-activates the owner → memory restore. Terminal-level `FocusEvent` never moves keyboard focus. Detach hygiene eagerly clears scope memories pointing at detached elements (virtualized churn must not pin subtrees).

**Tab:** container = nearest self-or-ancestor `Cycle` (window/popup roots default `Cycle` — there is no OS to Tab out to; modal trapping is the zero-cost default), eligibility = `Focusable && IsTabStop && enabled && visible`, stable-sorted by `TabIndex` then document order; recomputed per keypress, one pooled list. **Directional:** opt-in per container; direction filter + `facing-edge distance + 2 × orthogonal-range gap` scoring on window-translated arranged rects (via `TranslateToWindow`), ties → tab order.

**Focus visuals are styling:** no adorner layer; `:focus`/`:focus-within`/`:focus-visible` pseudo-classes only, styled in templates.

### §7.8 Access keys

```csharp
public readonly record struct AccessText(string Text, char Key, int KeyIndex)   // Cursorial.UI
{
    public bool HasKey => KeyIndex >= 0;
    public static AccessText Parse(string raw);      // "__" escapes; key must be a BMP letter/digit, else no key
    public static AccessText Literal(string text);   // (text, '\0', -1)
    public static explicit operator AccessText(string raw);   // explicit: parsing is lossy
}
```

Matching is simple-case-folded (lower-invariant); the registry keys likewise. **Three producers, one model:** (1) XAML fold at parse time — for `AccessText`-typed properties *and* object-typed slots whose property metadata carries the **`ParsesAccessKeyLiterals`** flag (`ButtonBase.Content`, `MenuItem.Header`, `TabItem.Header`, `Label.Content`; Fork A ledger entry; loader and generator fold identically); (2) runtime `GetAccessText()` parsing under the same flag — `button.Content = "_Save"` works code-first, bound strings included; (3) explicit `Parse`/`Literal` by app code. **Registration is control-side:** controls register/unregister on attach, content change, and detach via `AccessKeyManager.Register(char, UIElement)` / `Unregister` — a **flat** `Dictionary<char, List<UIElement>>` with **no scope captured at registration** (scope membership resolves at activation time by walking the candidate's ancestor chain against the live scope stack / active window root — attach-vs-`PushScope` ordering and reparenting are non-issues; manager-side `OnElementDetached` backstop). Presenters only render: controls realize an **`AccessTextPresenter`** (S8) for AccessText content; the default theme rule `:access-keys AccessTextPresenter { AccessKeyManager.ShowUnderline: true }` makes requirement-6 cue visibility pure styling downstream of the cue bit. S3 never touches a glyph.

**Capability gate** (in `OnCapabilitiesChanged`, evaluated against the **undecorated** negotiated snapshot — a synthesizer-decorated `DistinguishesKeyUpDown` must not qualify, since no Alt Down ever arrives to synthesize from):

```csharp
Mode = (k.DistinguishesKeyUpDown && k.ReportsRepeats) || p.Win32InputMode
     ? AccessKeyMode.AltHeld : AccessKeyMode.AlwaysVisible;
```

(`ReportsRepeats` is the runtime-testable proxy for the full Kitty flag set; the formula is equivalent to the conjunct form `DistinguishesKeyUpDown && (ReportsRepeats || Win32InputMode)` because Win32 input mode implies DistinguishesKeyUpDown.) `OnCapabilitiesChanged` also unconditionally clears side bits, sticky flag, chord-flash latch, and cue.

**AltHeld cue machine:** per-side Alt Down → cue ON (`AccessKeyCue` on the active scope root *and* window root, one batch); Alt tap (down+up, chordless) → sticky cue + `EnterMenuMode`; sticky clears on activation, consumed Esc, second tap, pointer-driven focus change, terminal focus-out (unconditional — Alt+Tab swallows the Up). **Chord-flash self-correction:** the Kitty push is family-gated but unverified (no DECRQM) — an Alt-chord arriving with no observed bracket flips the cue ON (sticky) before processing and latches; a later real Alt Down clears the latch. Recovers cue discoverability on terminals that claim but don't deliver the bracket (the input map's prescribed degradation). **AlwaysVisible:** cue set on **every** surface root (windows at attach/activation, popup roots at `PushScope`) and never cleared — the same theme rule renders underscores permanently. **Menu-mode entry = Alt tap or F10** (unhandled tail); S8's `IMainMenu` registers with `AccessKeyManager` as the `EnterMenuMode` subscriber.

**Activation** (unhandled tail; `Modifiers == Alt` chord works everywhere, Alt-held/sticky unmodified keys on capable terminals; exact-Alt match keeps AltGr out): 0 matches with sticky cue ⇒ swallowed (WPF bonk — typing must not leak into TextInput while cues are up); 1 ⇒ the manager **moves focus to the target when it is `Focusable`** (method `AccessKey`, before invoking so the invoked action can redirect — last-wins) then `OnAccessKey(IsMultiMatch: false)` invokes; n ⇒ cycle focus through matches via `OnAccessKey(IsMultiMatch: true)` — **multi-match focuses, never invokes**. The manager owns the focus move on **both** the single- and multi-match paths (parity with WPF/Avalonia and with the plain-element fallback); a non-focusable target — e.g. a `Label`, which forwards focus to its own `Target` inside `OnAccessKey` — is left untouched so its `OnAccessKey` decides. S3 owns `AccessKeyEventArgs { Key, IsMultiMatch, Target }` and `IAccessKeyTarget { IsAccessKeyEligible, OnAccessKey }`. Because this is the unhandled tail, a focused TextBox keeps plain `F`; Alt+F still reaches the manager.

### §7.9 Commands, key bindings, control-author contracts

`ICommand` = BCL `System.Windows.Input.ICommand`. Lifecycle is control-side: hook `CanExecuteChanged` on attach/command-change, unhook on detach (strong handlers, leak bounded by tree membership — recorded trade-off vs WPF weak events); `IsEnabledCore` ANDs into S1's effective-enabled pipeline; `InvalidateIsEnabledCore()` triggers recompute → S1 pushes the `Disabled` flip (`:disabled` styles identically for command- and property-disabled). Cross-thread `CanExecuteChanged` must be marshaled via `UIDispatcher.Post`.

`KeyGesture(Key, KeyModifiers, string? Character)` with `Parse("Ctrl+S")`; matching on lock-free `Modifiers` exactly, named keys by `Key`, character keys by ordinal case-insensitive `Character` vs `e.Text` (`(Key, Text)` is the printable-key identity). `KeyBinding : InputBinding`; `InputBindingCollection` is ordered — **ordering is the priority mechanism** where it matters. **Default/cancel buttons:** no window-key registry exists; `IsDefault`/`IsCancel` buttons (S8) install/remove `KeyBinding`s on their window root on attach/detach — focused-element-wins falls out of bubble order.

Control-author contracts (normative, `ButtonBase` is the reference consumer): activate on **KeyDown** (Enter/Space, `!IsRepeat`, no modifiers) — KeyUp exists only on Kitty/Win32; never gate core activation on it; the pressed-latch visual is a capability-gated nicety. `Pressed` is set **only** via `SetInteractionState(InteractionState.Pressed, …)` (fans into the pressed-holder set — C8's clearing guarantees depend on it); S8's `IsPressed` DirectProperty mirrors it; raw `PseudoClassSet.Set` is sanctioned only for control-semantic classes with no `InteractionState` bit (`:open`, `:highlighted`). `Click` is a **routed** `ClickEvent` (Bubble, `ClickEventArgs : RoutedEventArgs`, S8-owned) raised via `RentEvent`, plus CLR sugar. Mouse press: capture + pressed + `Focus(Pointer)` + handle; release on the capture-held MouseUp; pressed tracking under capture via `GetPosition(this)` against the arranged size.

### §7.10 Tree hygiene, threading, frame placement

Element detach fans into all three services: hover-chain truncation (+ deferred refresh at `UpdateHover`), capture force-release, pressed-holder removal, focus repair (nearest focusable ancestor → scope-root first tab stop → clear), eager scope-memory clear, access-key backstop unregister. Everything runs synchronously on the single UI thread inside the input drain — handler writes, `InteractionState` flips, focus changes are visible to frame N (invariant 1). One recorded exception: `UpdateHover` runs after layout/composite finalize, so a hover-restyle carrying `AffectsMeasure/Arrange` renders frame **N+1** (deliberate; no bounded re-layout loop). `VerifyAccess` (debug) on every entry point; S3 holds no locks and spawns no timers.

### §7.11 Cross-subsystem contracts

- **S6:** assembles the pinned device pipeline (§7.6); calls `ProcessEvent` per drained event (routing `NotUIInput` onward), `UpdateHover` once per rendered frame after layout+composite finalize, and `dispatcher.OnCapabilitiesChanged` + `accessKeys.OnCapabilitiesChanged` explicitly in startup/renegotiate sequences; keys default gestures on `DispatchedUnhandled`; documents `UIDispatcher.Post`.
- **S4:** provides the surface stack (topmost-first, signed `CellRect` terminal bounds incl. composite offsets) + `FilterMouseEvent` + `ObservePointerPosition`; calls `OnSurfacesChanged` on **every** stack mutation, `OnWindowActivated/Deactivated` + `accessKeys.OnWindowActivated`, `PushScope/PopScope` on popup open/close; subscribes `TerminalFocusChanged`; writes `:active-window` itself; window roots get `IsFocusScope = true` + `TabNavigation = Cycle`. S3 implements S4's `IWindowFocusHooks` (blocked/closed/deactivation clears map onto the existing hygiene paths).
- **S1:** `RenderTree.HitTest(surfaceLocal)`, `TranslateToWindow/TranslateToLocal`, `IsEffectivelyVisible`, `VisualChildren`/`VisualParent`/`LogicalParent` (surface-root logical hop), attach/detach hooks with detach fan-in, and the effective-IsEnabled pipeline (S1 declares `IsEnabledProperty`, owns the computation, pushes `Disabled`).
- **Fork A:** internal `UIPropertyKey<bool>` registrations, attached properties, `OverrideDefault`, the `ParsesAccessKeyLiterals` metadata flag (engine ledger).
- **Fork B:** `IInteractionStateSink` + `BeginInteractionUpdate` batching; C8 cites the pressed-holder set; the `:access-keys AccessTextPresenter` theme rule (S8-authored content in S7's theme).
- **Fork C:** AccessText fold per §7.8; `KeyGesture.Parse` as the `KeyBinding.Gesture` type converter.
- **Provides to S8:** `FindNext`, `HoverChanged` (ToolTipService, with S5's `UITimer`), `RentEvent`/args rules, `CaptureMouse/ReleaseMouseCapture` (no separate capture interface), `IAccessKeyTarget`/`AccessKeyEventArgs`, `EnterMenuMode`/F10, the `IsEnabledCore` + window-root `KeyBinding` patterns.

### §7.12 Deferred (carry forward)

- **Template source adjustment** (`Source` ≠ `OriginalSource` at template boundaries) — Avalonia ships without it; `Source` is already a separate slot, re-addable.
- **Static class-handler registry** — `On*` virtuals cover control authoring; an open registry invites ordering ambiguity with no current consumer.
- **Cancelable Preview focus events** — gnarly re-entrancy; needs a real consumer first.
- **RoutedCommand / CommandManager.RequerySuggested** — BCL `ICommand` + element `KeyBinding`s cover stated requirements; recorded as re-addable.
- **MouseBinding / non-key gesture vocabulary** — trivially additive to `InputBinding`.
- **Subtree mouse capture** — element capture suffices for buttons/drags/menus.
- **`KeyboardNavigationMode.Local`** and the full WPF scope/Tab matrix — ship the four modes that map to real terminal layouts (`Once` promoted to v1 for ListBox).
- **Mouse cursor shape on hover** (OSC 22) — needs S4 policy for which surface owns the pointer shape.
- **`PointerEvent` (pen/touch) routing** — no source emits it today; vocabulary reserved.
- **Drag-and-drop** — out of scope until a consumer exists.
- **Cached subtree hit bounds** — hit-test pruning now rides S1's `RenderTree` boundary clips; revisit only if profiling shows scans.

---

## §8 S4 — Windows, popups, and the window manager

S4 turns one terminal screen into a desktop: it owns `Window`, `WindowManager`, modality, `Popup`, drop shadows, and the whole-screen composite assembly. There are no OS HWNDs — every top-level surface is a stack of `Scene` layers in one `SceneCompositor` over a desktop base, and the WM **is** the window system. Root namespace `Cursorial.UI`.

### §8.1 Scope and ownership

**Owns:** `Window` (templated top-level control: title/chrome, sizing models, move/resize interactions, Closing/Closed, `DialogResult`); `WindowManager` (window list, owner-banded z order, activation, screen-resize policy, shutdown close-all, deferred-topology queue); modal stack + `ShowDialogAsync`; modeless `Show`/`Activate`/`Close`; `Popup` (light-dismiss primitive S8's Menu/ComboBox/ToolTip/ContextMenu consume); window/popup drop shadows + the `FillOpaque` occluding-chrome stance; `TopLevelSurface`; **the `SceneCompositor` and `ScenePool`** (S6 owns only the screen `CellBuffer` + `FrameRenderer` and passes the target view per frame); top-level mouse gating for uncaptured events.

**Does NOT own:** frame loop / `FrameRenderer` / screen buffer (S6); focus mechanics, mouse capture, key routing, access keys, intra-surface hit testing (S3 — which delegates the latter to S1's `RenderTree.HitTest`); element tree/layout/rasterization (S1 — each surface wraps an S1 `RenderTree`); styling internals (the WM only flips `InteractionState.ActiveWindow`, the `obscured` class, and the `:modal-attention` pulse through Fork B's published sinks); menu/tooltip *behavior* (S8 — S4 owns surface, placement, dismissal mechanics).

### §8.2 Window

`Window : ContentControl` (hierarchy `UIElement → Control → ContentControl → Window`). `Content` is **not** re-registered — it is `ContentControl.Content`, so the chrome template's `ContentPresenter` auto-alias engages naturally. Likewise `Width/Height/Min*/Max*` and `Opacity` are `AddOwner`s of the S1 registrations with overridden metadata (window defaults + WM change handlers) — never duplicate registrations (shadowing hazard). Element opacity exists on `UIElement` (S1, `AffectsComposite`, boundary-promoting); `Window.Opacity` is its `AddOwner` with [0,1] metadata coercion.

```csharp
public enum WindowState : byte { Normal, Maximized }            // Minimized: deferred
public enum SizeToContent : byte { Manual, Width, Height, WidthAndHeight }
public enum SizeToContentMode : byte { Once, Always }           // when SizeToContent drives: first show only, or live
public enum WindowStyle : byte { TitleBar, None }
public enum WindowStartupLocation : byte { Manual, CenterScreen, CenterOwner }
public enum WindowCloseReason : byte { Programmatic, ChromeAction, OwnerClosed, ManagerShutdown }

public readonly record struct WindowShadow(ShadowGeometry Geometry, Color Color)
{
    public static WindowShadow None => default;
    public static WindowShadow Default { get; }   // Drop(radius:1, offset:1, strength:0.5), opaque black.
                                                  // CANONICAL — S8's chrome cites this, never inlines geometry.
    public bool IsNone { get; }
    public Margins GetMargins();                  // per-edge cells the surface grows beyond content
}

public class Window : ContentControl
{
    public static readonly StyledProperty<string?>      TitleProperty;        // AffectsRender; main window → OSC 2 (§8.8)
    public static readonly StyledProperty<WindowStyle>  WindowStyleProperty;
    public static readonly StyledProperty<WindowShadow> ShadowProperty;       // surface-geometry handler
    public static readonly StyledProperty<int>  LeftProperty, TopProperty;    // SIGNED cells; AffectsComposite
    public static readonly StyledProperty<SizeToContent> SizeToContentProperty;  // default WidthAndHeight
    public static readonly StyledProperty<SizeToContentMode> SizeToContentModeProperty; // default Once; Always = live re-fit,
                                                  // converged INSIDE the layout phase (measure-only probe of the window at the
                                                  // open constraint → surface resize → same-frame final pass — never a
                                                  // post-layout resize, so no transitional flicker). The unclamped fit is
                                                  // remembered per axis; a transient viewport shrink caps the surface without
                                                  // losing it. Window.FitToContent() is the Once-mode on-demand re-fit lever
                                                  // (restore-from-maximize requests one implicitly).
    public static readonly StyledProperty<bool> AutoFitToViewportProperty;    // default false (§8.7 badge policy); opt-in:
                                                  // a content-driven grow past the viewport shifts the window back into view
                                                  // (dialogs with expandable regions — never drags/terminal resizes)
    public static readonly StyledProperty<WindowState>   WindowStateProperty;
    public static readonly StyledProperty<WindowStartupLocation> WindowStartupLocationProperty;
    public static readonly StyledProperty<bool> CanMoveProperty, CanResizeProperty, CanCloseProperty; // default true
    public static readonly StyledProperty<double> OpacityProperty;            // AddOwner of UIElement.OpacityProperty
    public static readonly DirectProperty<Window, bool> IsActiveProperty;     // read-only

    public Window? Owner { get; set; }            // settable until first Show*; immutable thereafter (throws)
    public WindowManager? Manager { get; }        // non-null while shown
    public bool IsShown { get; }   public bool IsModal { get; }   public bool IsActive { get; }
    public Size ActualSize { get; }               // realized content size (excludes shadow)
    public object? DialogResult { get; set; }     // non-null while shown modal requests Close()

    public void Show();                           // register + Activate (WPF semantics; blocked ⇒ silent redirect)
    public void Show(WindowManager manager);      // default: Owner?.Manager ?? UIApplication.Current.WindowManager
    public Task<object?> ShowDialogAsync(CancellationToken ct = default);
    public Task<TResult?> ShowDialogAsync<TResult>(CancellationToken ct = default);
    public bool Activate();                       // false when modal-blocked: silently redirects to the gate
    public void Close();                          // Closing (cancelable) → Closed; reentrancy-guarded
    public void Close(object? dialogResult);

    public event EventHandler<WindowClosingEventArgs>? Closing;   // Reason + CanCancel + Cancel
    public event EventHandler? Closed, Activated, Deactivated;
    public static readonly RoutedEvent ModalAttentionEvent;       // code-behind seam; theme flash rides §8.6's pulse
}
```

`ShowDialogAsync` is `Task`-based — **the frame loop is the pump**; `await` is the modal pump (invariant 1; no `DispatcherFrame`, no priorities). Cancellation registrations always `Post` the forced close to the UI `SynchronizationContext` (S6) — `Close` mutates WM/styling state and never runs on the canceling thread (invariant 6); pre-canceled tokens return a canceled task with no side effects; races tolerated via `IsShown` + `TrySetCanceled(ct)`. User-gesture writes (move/resize/re-clamp) use `SetCurrentValue` so bindings survive; **`SetCurrentValue` propagating through two-way bindings to the source is a pinned Fork A conformance-matrix row** (the `Popup.IsOpen` write-back depends on it).

### §8.3 Chrome contract (template-flexible, role-attached)

```csharp
[Flags] public enum WindowHitTestRole : byte { None = 0, Drag = 1, Close = 2, Maximize = 4, ResizeSE = 8 }
public static class WindowChrome
{
    public static readonly AttachedProperty<WindowHitTestRole> HitTestRoleProperty;  // Window interprets
}                                                                                    // bubbling ButtonDown/Drag per role
```

Behavior keys on **roles, not part names** — `PART_*` names are S8-internal (`GetTemplatePart` lookups only), never a cross-subsystem contract; there are no window command objects. S8's default theme template sets `HitTestRole` on its parts. **S4 ships the interim default chrome template** (placeholder until S8's themed one lands at C4): an occluding root — `FillOpaque` background + `DrawTitledBox(overwrite: true)` per the drawing-core occluding-panel idiom (windows must occlude, not tint) — title-bar row (`Drag`, double-click toggles `WindowState`, `TemplateBinding Title`, ✕ = `Close`), `ContentPresenter`, `◢` grip (`ResizeSE`) when `CanResize`. `WindowStyle.None` selects a chrome-less, still-opaque template.

### §8.4 Popup

```csharp
public enum PlacementMode : byte { Bottom, Top, Right, Left, Center, Pointer }
public enum PopupCloseReason : byte { Programmatic, LightDismiss, EscapeKey, HostDeactivated,
                                      HostBlocked, HostClosed, ScreenResized }
public class Popup : UIElement      // lives in the host's LOGICAL tree (DataContext/resources/styles inherit);
{                                   // when open, Child roots a separate TopLevelSurface placed by the WM
    public static readonly StyledProperty<UIElement?>    ChildProperty;          // swappable while open (below)
    public static readonly StyledProperty<bool>          IsOpenProperty;         // BindsTwoWayByDefault: every close
                                                                                 // reason writes back via SetCurrentValue
    public static readonly StyledProperty<PlacementMode> PlacementProperty;      // default Bottom
    public static readonly StyledProperty<UIElement?>    PlacementTargetProperty;// default: logical parent
    public static readonly StyledProperty<Rect?>         PlacementRectProperty;  // target-local anchor override
    public static readonly StyledProperty<int>           HorizontalOffsetProperty, VerticalOffsetProperty;
    public static readonly StyledProperty<bool>          StaysOpenProperty;      // default false ⇒ light dismiss
    public static readonly StyledProperty<bool>          CloseOnEscapeProperty;  // default true
    public static readonly StyledProperty<WindowShadow>  ShadowProperty;         // default WindowShadow.Default
    public void Open();  public void Close();   // Close ⇒ reason Programmatic
    public event EventHandler? Opened;  public event EventHandler<PopupClosedEventArgs>? Closed;
}
```

**Guarantees (the S8 contract):** (1) flip-then-clamp placement against the target's screen rect; popups never overhang the screen, size clamps to it. (2) Opening never changes window activation; a child with `IsHitTestVisible=false` yields a **hit-test-transparent surface** (tooltips never steal hover/clicks; skipped by `SurfaceFromPoint` and light-dismiss "outside" tests). (3) Light-dismiss chains close innermost→outermost on any uncaptured outside `ButtonDown`; the dismissing press is swallowed; a press inside chain A dismisses every *other* chain but still routes into A; host deactivation/blocking/close and screen resize close with the matching reason; on close the WM calls `OnSurfaceClosed` and S3 restores the host's remembered logical focus (the menu round-trip). (4) A popup whose target lives in another popup's child joins that chain; chains dismiss together. (5) Anchor tracking repositions in the **same frame** during `OnLayoutCompleted` — composite-offset change only. (6) Popups occupy a global band above all windows, ordered by (chain-root open order, depth); `StaysOpen` popups of blocked hosts close (`HostBlocked`) so nothing input-swallowing sits above the modal gate. (7) Popups inherit the host window's composite opacity. (8) **Content swap without close:** setting `Child` (or re-placing) on an open Popup retains the surface and reuses its scenes where sizes allow — no surface-count change, no full-target recomposite. This is the menu-session contract: hover-switching top-level menus reuses one popup surface instead of paying two full recomposites per switch.

`CloseOnEscape` is an ordinary routed-key handler on `Popup`: S3's route builder, on reaching a surface root with `LogicalParent != null`, continues the route via the logical parent (popup root → `Popup` → host chain) — load-bearing for the entire S8 menu contract.

### §8.5 TopLevelSurface, WindowManager, composite pipeline

A `TopLevelSurface` (a shown `Window` or an open `Popup`'s child) **wraps an S1 `RenderTree`** — one `Scene` per render boundary, not one scene per surface; there is no whole-surface rasterizer. Raster scheduling, zone scenes, and intra-surface hit testing are S1's.

```csharp
public sealed class TopLevelSurface
{
    public UIElement Root { get; }   public Window HostWindow { get; }   // self for windows
    public bool IsPopup { get; }     public bool IsHitTestTransparent { get; }
    public int Left { get; }  public int Top { get; }    // SIGNED screen cells (content origin, shadow excluded)
    public Size Size { get; }
    public RenderTree RenderTree { get; }                // S1 zone engine; S6's raster pass drives its dirty zones
    public bool Contains(int column, int row);           // content rect only — shadow cells are not hit-testable
    public void CollectLayers(List<SceneLayer> target);  // RenderTree.CollectLayers at screen offset
}                                                        // (Left−margins, Top−margins) with host opacity

public sealed class WindowManager : IWindowTopology, IWindowSystem, IRenderSystem   // S6 seams declared in §10.9
{
    public WindowManager(TerminalCapabilities capabilities); // S6 supplies the negotiated snapshot; constructs the
                                                             //   SceneCompositor + ScenePool it owns (§13.2)
    public void AttachFocusHooks(IWindowFocusHooks hooks);   // WM first, S3 second, before any Show*; throws otherwise
    public IReadOnlyList<Window> Windows { get; }            // z order bottom→top
    public Window? ActiveWindow { get; }   public Window? TopmostModal { get; }
    public Size ScreenSize { get; }
    public IBrush DesktopBackground { get; set; }            // solid ⇒ uniform compositor base; else backdrop buffer
    public event EventHandler? ActiveWindowChanged;
    // S6 contract (IWindowSystem + IRenderSystem, §10.9 — RenderFrame = raster dirty scenes z-order, then:):
    public IReadOnlyList<TopLevelSurface> Surfaces { get; }  // z order; stable within a frame
    public void DrainDeferredTopology();                     // end of S6 Phase 1 (input drain)
    public void OnLayoutCompleted();                         // SizeToContent resolution; popup anchor compare/reposition
    public bool CompositeFrame(in CellBufferView target);    // inside RenderFrame; false ⇒ idle (renderer emits nothing).
                                                             //   Folds each surface's screen offset into S1's published
                                                             //   caret position while assembling; S6 writes the buffer
                                                             //   cursor state in RenderFrame (§5.9 caret contract)
    public void OnViewportResized(Size newSize);             // after CellBuffer.Resize, before layout (fresh compositor
                                                             //   + InvalidateAllSurfaces inside)
    public void InvalidateAllSurfaces();                     // renegotiate / theme-variant flip
    public Task CloseAllAsync();                             // shutdown: top-down forced sweeps
    // S3 contract (IWindowTopology):
    public TopLevelSurface? SurfaceFromPoint(int column, int row);  // topmost non-transparent; O(surfaces), no alloc
    public bool IsInputEnabled(Window window);                      // modal gate
    public MouseRoutingDecision FilterMouseEvent(in MouseEvent e);  // UNCAPTURED events only
    public void ObservePointerPosition(in CellPosition screen);     // captured events: one field store
    public void OnTerminalFocusChanged(bool hasFocus);
}
public readonly record struct MouseRoutingDecision(MouseRoutingKind Kind,       // Route | Swallowed
    TopLevelSurface? Surface, CellPosition SurfaceLocalPosition);
```

`CompositeFrame` fills a cached scratch list by concatenating each surface's `CollectLayers` output in z order (popups' parameters carry the host's opacity), then `SceneCompositor.Composite(layers, target)`. **The WM owns the `SceneCompositor` and `ScenePool`** (compositor identity is coupled to the layer list, base, and resize policy); it is recreated on screen resize and `DesktopBackground` change. Pending scene disposals drain **after** `CompositeFrame` (the compositor's per-slot diff may hold last frame's references; pooled buffers must not alias a frame's diff inputs). `InvalidateAllSurfaces` + per-window `RenderTree.Capabilities` re-stamp ride S6's renegotiation transaction (fresh compositor + fresh `FrameRenderer`, one coherent frame).

**Layer-count stability over boundary counts.** Total layer count = Σ `RenderTree.LayerCount` over shown surfaces. S1's sticky promotion keeps each surface's count stable after warm-up, so the count changes only at surface open/close and first-time boundary promotion — the compositor's rare, accepted full-target recomposites. Within a surface's lifetime, move/fade/restack are `CompositeParameters` diffs (footprint recomposite, zero re-raster).

Frame ordering (S6-decomposed, normative): input/work drain (ends with `DrainDeferredTopology`) → layout → `OnLayoutCompleted` → raster pass (dirty zone scenes, z order) → `CompositeFrame` → `FrameRenderer.Render`.

### §8.6 Z order, activation, modality, input gating

**Z policy (stability-first):** owner-forest groups bottom→top by group activation stamp (max over the owned closure), owner-DFS within a group (owned windows always above their owner), then the global popup band. Modal-on-top is **emergent**: the modal's activation stamp is newest and blocked windows can never re-activate (activation silently redirects to the gate; `Activate` returns false; no attention pulse from programmatic redirects). Z is recomputed only on show/close/activate/popup open/close — never per frame (`Owner` is immutable after first `Show*`).

**Modal stack:** `ShowDialogAsync` pushes, shows, activates; the **enabled set** = topmost modal + its transitively owned windows (+ their popups). Every blocked⇄enabled flip sets/clears the **`obscured` class** on the window root (the DECISIONS Fork B dimming mechanism — the blessed truecolor recipe is `Window.obscured { Opacity: 0.7 }`, a composite-only dim; Ansi16/Ansi256 theme variants ship `Faint` + darker-background setters instead), closes its popups (`HostBlocked`), and calls `OnWindowBlocked` (S3 releases pointer capture held inside — mid-gesture modal engagement). Out-of-order modal closes recompute gate/classes but **never** transfer activation: handoff runs only when the closing window `wasActive` (owner → gate → topmost group → `null`; the null-active state routes keys to Application-scope bindings only).

**Attention pulse:** a user press on a blocked window (the *one* source) makes the WM pulse the transient **`:modal-attention` pseudo-class** on the gate's root via the S3 `InteractionState` plumbing, cleared ~600 ms later by an S5 `UITimer` — the styling edge-action path (`BeginStoryboard`, `HandoffBehavior.SnapshotAndReplace`) animates the flash with no new trigger vocabulary. The routed `ModalAttentionEvent` is raised alongside for code-behind.

**Input gating:** S3 checks pointer capture **first**; captured events bypass the WM entirely (one `ObservePointerPosition` field store keeps `PlacementMode.Pointer` fresh). `FilterMouseEvent` handles uncaptured events: update last-pointer-cell → `SurfaceFromPoint` → light-dismiss sweep on `ButtonDown` (outside all chains ⇒ close all + swallow; inside chain A ⇒ close other chains, route into A) → desktop ⇒ swallow → blocked host ⇒ pulse `:modal-attention` + `Activate(gate)` on press, swallow all motion (no hover) → press on inactive enabled window ⇒ activate → `Route(surface, screen − origin)`. S3 then delegates intra-surface hit testing to `RenderTree.HitTest` (composite-order-faithful; S1 owns hit-test z order). Must stay O(surfaces), allocation-free — any-event motion fires per cell crossed. **Normative:** the WM calls `dispatcher.OnSurfacesChanged()` on every show/close/popup open/close/modal flip/z reorder so S3 re-evaluates hover against the new stack. Terminal `FocusEvent` toggles only the `:active-window` *visual* bit (re-applied on focus return; never re-lit while unfocused) — logical activation, focus memory, and the gate are unaffected.

### §8.7 Scenes, shadows, resize, drags

The surface's **root zone scene** grows by `Shadow.GetMargins()` (≥ 1×1; an empty-measuring popup child defers surface creation); the shadow is drawn into it before content — S1's zone-base rule makes it the lowest layer of the surface's subtree, so it darkens lower layers' backgrounds at composite time and rides the layer offset for free during drags. Shadows require RGB and no-op on palette themes — themes gate `Window.Shadow` via `caps-truecolor`. Modal dimming is *not* a shadow trick (shadows cannot dim a lower glyph's foreground).

- **Screen resize** (S6 calls `OnViewportResized` after the destructive `CellBuffer.Resize`, before layout): compositor recreated; backdrop re-sampled if brushed; `Maximized` windows re-size; `Normal` windows **re-clamp** (`Top ∈ [0, rows−1]`, `Left ∈ [−(width−MinVisible), cols−MinVisible]`, `MinVisible = 4` — the title bar must stay grabbable; no OS rescues an off-screen window); oversized resizable windows get `SetCurrentValue(Width/Height, clamped)`; light-dismiss popups close (`ScreenResized`), `StaysOpen` popups re-place.
- **Drag math (normative):** screen-space, anchored at press — routed mouse args carry both surface-local `Position` and `ScreenPosition` (S3); `delta = screenNow − screenAtPress` applied to the press-time snapshot. Surface-local deltas are forbidden for drags (the origin moves under the gesture). Move drags `SetCurrentValue(Left/Top, …)` with the live re-clamp formula — pure composite churn, never a re-raster, even at 60 fps motion rates.
- **Resize drags:** gesture-scoped over-allocation — during capture, the root zone scene rounds up to a 16-col × 8-row quantum (clamped to screen + margins) so per-frame sizes reuse one scene; a `GestureClip` intersected into the surface's collected layer parameters bounds the composite to the actual footprint; exact-size recreate at release. Without this an SE-grip drag reallocates O(cols×rows) `Cell[]` per frame.
- Image caveat carried from Drawing: dragging a window containing Sixel images re-anchors and re-encodes fragments per frame — image-heavy windows should prefer Kitty-class terminals or stay put.

### §8.8 Reentrancy, shutdown, window title

Topology mutations (`Show`/`Close`/popup open/close) requested **during layout/OnLayoutCompleted/raster/composite** are queued and drained at the next frame's `DrainDeferredTopology` — `Surfaces` and the layer scratch stay stable while S6 iterates them; frame coherence holds as worded (these sets were never part of frame N's drain). Mutations during the input drain or app code apply immediately. `Close()` is reentrancy-guarded (`IsClosing`); owned windows close first (`OwnerClosed`, `CanCancel=false`), then hosted popups; detach runs the S1/S2 teardown sweeps (retraction is store-owned, invariant 4); a closed window cannot be re-shown.

`CloseAllAsync` (invoked by S6's teardown **before** `AnimationScheduler.Shutdown`): snapshot the shown list, close top-down with `ManagerShutdown`/`CanCancel=false`, open dialogs complete `null`; windows shown during the sweep are swept again until empty — a hostile handler can delay shutdown, never veto it.

**`Window.Title` → OSC 2 (wired in v1):** the main window's effective `Title` flows through S6's `QueueControlSequence` channel via `WindowWriter.WriteTitle` on change; S6's session teardown restores the prior title.

### §8.9 Terminal-specific deviations (from WPF/Avalonia)

1. **No nested message pump** — `await ShowDialogAsync()` over the running frame loop; cancellation marshals to the UI context.
2. **We are the WM** — flip/clamp placement is mandatory (nothing can overhang the screen).
3. **Layer-count stability is a first-class design force** (full-target recomposite on count change) — analyzed over per-surface boundary counts (§8.5).
4. **Windows occlude, not tint** (`FillOpaque` + overwrite box; background-only fills bleed lower windows' glyphs).
5. **Integer cells, signed position** — `Left`/`Top` are ints expressed via `CompositeParameters` offsets (`Rect` is ushort-backed); window motion steps whole cells.
6. **Drag-move never re-rasters; drag-resize never reallocates mid-gesture** (§8.7).
7. **Resize is destructive end-to-end** — buffer + compositor + scenes rebuild in one coordinated frame; live re-clamp keeps title bars reachable.
8. **Hover gating is O(surfaces)** with DECSET-1003 motion on by default; blocked/desktop motion swallowed before any element hit test.
9. **Terminal focus ≠ window activation** — `FocusEvent` flips a visual bit; S3 handles cue/hover clearing (styling C8) distinctly from window-deactivation clearing (contract W-DC).

### §8.10 Deferred (carry forward)

- **Minimized state / taskbar** — no shell surface to minimize to; additive `WindowState` value.
- **`ShowActivated=false`** — no driving consumer; additive property on the existing Show path.
- **Topmost band** — no driving consumer; additive `WindowBand` slot in the z key.
- **Routed-event `EventTrigger` (storyboards)** — the `:modal-attention` pulse covers the only v1 consumer; code-behind covers the rest.
- **Window open/close transition storyboards** — needs a close-deferral protocol with S5 (hold `Closing` until the storyboard completes).
- **Edge resize on all borders + keyboard move/resize** — SE grip covers v1; edge bands need hit-test margins that interact fiddly with shadows.
- **Light-dismiss pass-through option** — WPF parity (swallow) is the safe default; additive `Popup` flag.
- **Independent popup surface opacity** — v1 inherits the host's; additive parameter if a consumer appears.
- **Modal scrim layer slot** — `.obscured` + composite opacity satisfies DECISIONS; a scrim scene would toggle the layer count.
- **Live desktop backdrop (scene-based base)** — the compositor exposes no base-invalidation plumbing; uniform/brushed base covers v1.
- **Window snapping/tiling presets, placement persistence** — app-level conveniences, not spine.
- **Desktop context menu** — desktop `ButtonDown` is swallowed in v1; additive hook later.

---

## §9 S5 — Animation orchestration

The storyboard layer over `Cursorial.Animation`'s time-free mechanism (drawing-doc §9 split: mechanism = pure `elapsed → value`; orchestration — clock, scheduler, triggers, lifecycle — lives here). All UI types in `Cursorial.UI`, folder `Cursorial.UI/Animation/`; everything added to lower layers is additive (invariant 7).

**Owns:** `FrameClock` (frame-frozen time), `AnimationScheduler` (active-instance registry, per-frame sampler, idle signal, detach stop pass, `Shutdown`), the `IAnimationFrameDriver` implementation S6 drives, `UITimer`, imperative `BeginAnimation`, declarative `Storyboard` + Fork B ignition actions, Transitions (phase A3), `Interpolator.For<T>` registry, and additive `Cursorial.Animation` combinators/easings.
**Does not own:** the render loop / pacing / idle decision (S6), styling edges and selector evaluation (Fork B — we implement the edge-action interface it invokes), `ValueStore`/`AnimatedValueHandle<T>` internals (Fork A — we are a pure client), the element tree / namescopes / `PropertyEffects` registration (S1; `FindName` is S2's).

### §9.1 Clock, scheduler, frame driver (the S6 contract)

S6 computes one `FrameTime(FrameNumber, Elapsed, Delta)` per frame and passes it down — **the single time source**. The scheduler freezes it; between `BeginFrame` calls, `Clock.Now` never moves, so every animation sampled in a frame sees one timestamp (invariant 1). `TimeProvider`-derived ⇒ wall-clock monotonic (slow terminals drop frames, never stretch time) and `FakeTimeProvider`-deterministic in tests.

```csharp
public interface IAnimationFrameDriver               // declared by S6 (§10.9); AnimationScheduler implements it
{
    void BeginFrame(in FrameTime time);              // Phase 0, FIRST statement of the frame: freeze the clock —
                                                     //   the frame's ONLY time carrier (resolution 8)
    void Tick();                                     // Phase 4: sample all active instances once at the frozen time;
                                                     //   fire due UITimers; completions raise inline AFTER sampling
    void TickNewlyStarted();                         // after the post-Tick styling flush: completes registration +
                                                     //   completion processing for storyboards ignited on that edge
                                                     //   (they already self-sampled at Begin) — no one-frame From-snap;
                                                     //   MUST be a cheap no-op when nothing started.
    bool HasActiveAnimations { get; }                // idle gate: Delayed + Running instances + running UITimers
}

public sealed class FrameClock                       // TimeSpan Now, frozen at last BeginFrame
{
    public TimeSpan Now { get; }
}

public sealed class AnimationScheduler : IAnimationFrameDriver
{
    public static AnimationScheduler Current { get; }     // thread-ambient (decided over per-Window service:
    public static void Install(AnimationScheduler s);     //   invariant 6 = one UI thread; one clock + one idle
    public FrameClock Clock { get; }                       //   signal serves all windows incl. modal children)
    public bool AnimationsEnabled { get; set; } = true;   // reduced-motion switch, §9.7
    public void Shutdown();                                // session teardown, §9.6; idempotent, then inert
}
```

Frame protocol (S6's loop, normative ordering): `BeginFrame` → input + dispatcher drains (a `Begin` during the drain stamps `StartTime = T_N` and **writes its first sample synchronously** — frame coherence with no tiers) → styling flush → `Tick` → styling flush #2 (animation-driven pseudo flips) → `TickNewlyStarted` → layout → render → idle decision. `Tick` iterates with a count snapshot (instances begun inside callbacks append, skip this frame — they self-sampled); completions raise after all sampling so frame N's values are coherent before user code observes them. Render dirtiness comes from store-routed invalidations (`HasDirtyVisuals`), not from a sampler return value.

### §9.2 Imperative surface

```csharp
public enum FillBehavior : byte { HoldEnd = 0, Stop = 1 }
public enum HandoffBehavior : byte { SnapshotAndReplace = 0 }      // Compose deferred
public enum AnimationState : byte { Delayed, Running, Paused, Holding, Completed, Stopped }

public readonly record struct AnimationStartOptions(
    TimeSpan BeginTime = default, FillBehavior Fill = FillBehavior.HoldEnd,
    HandoffBehavior Handoff = HandoffBehavior.SnapshotAndReplace);

public static class ElementAnimationExtensions
{
    public static AnimationHandle BeginAnimation<T>(this UIObject target,
        StyledProperty<T> property, IAnimation<T> animation, AnimationStartOptions options = default);
    public static void StopAnimation(this UIObject target, UIProperty property);
}

public sealed class AnimationHandle
{
    public AnimationState State { get; }  public UIObject Target { get; }  public UIProperty Property { get; }
    public void Pause();                  // legal from Delayed or Running; StateBeforePause captured
    public void Resume();                 // restores captured state; StartTime += pause span
    public void Seek(TimeSpan offset);    // animation's own timeline (post-BeginTime), clamped; works paused;
                                          //   Delayed seek ≥ 0 starts it; Holding seek < Duration re-enters
                                          //   Running WITHOUT re-raising Completed (at-most-once)
    public void Stop();                   // dispose store handle ⇒ retraction ⇒ base resurfaces (invariant 4); no Completed
    public void SkipToEnd();              // finite only (perpetual ⇒ InvalidOperationException); Delayed: attach +
                                          //   From snapshot + end value + complete; Holding/Completed/Stopped: no-op
    public event Action<AnimationHandle>? Completed;   // UI thread, after the sampling pass; at most ONCE per
                                          //   lifetime; never on Stop / detach-stop / Shutdown
}
```

No collision with Fork A's raw seam `UIObject.BeginAnimation<T>(StyledProperty<T>) → AnimatedValueHandle<T>` (different arity); that overload is the engine-level handle factory used only by this subsystem.

Sampling pins: track start keys on `_handle is null` (survives Pause-while-Delayed); the **perpetual guard** (`Duration == TimeSpan.MaxValue`, captured once) keeps `MaxValue` out of all arithmetic; the final write is always `ValueAt(Duration)` — for PingPong that is the *start* value (documented); zero-duration reports `To` at elapsed 0 (one-frame set + completion); `Sample` re-reads `State` after every store write (a reentrant `Stop` from the write's own change notification skips the completion branch); `Retract()` is idempotent. Reentrancy contract: state mutations apply immediately, `_running` membership changes are flag-then-sweep, the completed-scratch list drains index-over-live-Count (same-frame delivery of callback-enqueued completions). Allocation accounting: a bounded handful at `Begin` (instance, handle, factory closure, built timeline); **per-frame steady state: zero** (`ValueAt` allocation-free for value types, in-place `SetValue`, reused scratch; `BrushTrack` is the documented exception — one brush per sample). Under any-event mouse motion, edge-ignition churn is bounded by enter/leave *edges*, never per-cell `Move` events.

### §9.3 Storyboards and styling ignition

```csharp
public readonly struct Optional<T> { public bool HasValue { get; } public T Value { get; }
    public static Optional<T> Unset => default; public static implicit operator Optional<T>(T value); }
public readonly record struct RepeatBehavior { /* Once, Forever, Count(int ≥ 1); XAML "1x"/"3x"/"Forever" */ }

public abstract class AnimationTrack
{
    public string? TargetName { get; set; }            // null ⇒ Begin scope; resolved via S2's template-aware FindName
    public UIProperty? TargetProperty { get; set; }    // required; Fork C converter resolves "Control.Background"
    public TimeSpan BeginTime { get; set; }            // stagger; property UNTOUCHED until then (no handle)
    public RepeatBehavior Repeat { get; set; }  public bool AutoReverse { get; set; }
    public FillBehavior Fill { get; set; } = FillBehavior.HoldEnd;
}
public class AnimationTrack<T> : AnimationTrack
{
    public Optional<T> From { get; set; }              // unset ⇒ snapshot GetValue(property) at TRACK START (§9.4)
    public Optional<T> To { get; set; }  public TimeSpan Duration { get; set; }
    public Easing? Easing { get; set; }  public IInterpolator<T>? Interpolator { get; set; }   // null ⇒ For<T>()
    public IList<Keyframe<T>>? Keyframes { get; set; }
    public IAnimation<T>? Source { get; set; }         // code-built escape hatch; Repeat/AutoReverse wrap it uniformly
}
// Sealed XAML-friendly tracks: DoubleTrack, Int32Track, ColorTrack (Cursorial.Output.Color),
// BrushTrack (allocates per sample), RectTrack, SizeTrack, MarginsTrack.

public sealed class Storyboard
{
    public IList<AnimationTrack> Children { get; }     // seals on first Begin OR on seal of a Style holding a
                                                       //   BeginStoryboard referencing it; mutation after ⇒ throws
    public StoryboardHandle Begin(UIElement scope, HandoffBehavior handoff = HandoffBehavior.SnapshotAndReplace);
    public void Stop(UIElement scope);                 // stops the imperatively-keyed instance only
}
public sealed class StoryboardHandle                   // ops on the STORYBOARD timeline (per-child t − BeginTime;
{                                                      //   backward seek past BeginTime retracts the handle and returns
    public bool IsCompleted { get; }                   //   the child to Delayed, retaining its built timeline — the
    public void Pause(); public void Resume();         //   From factory runs at most once per instance lifetime)
    public void Seek(TimeSpan offset); public void Stop();
    public void SkipToEnd();                           // validates all-finite UP FRONT; any perpetual track ⇒ throw
    public event Action<StoryboardHandle>? Completed;  // at most once; perpetual child ⇒ never
}

public interface IStyleEdgeAction                      // invoked by Fork B on rule activation/retraction edges,
{ void OnActivated(UIElement scope); void OnRetracted(UIElement scope); }   // rule-document order; NO-THROW (owned here)
public sealed class BeginStoryboard : IStyleEdgeAction
{ public Storyboard? Storyboard { get; set; } public HandoffBehavior Handoff { get; set; }
  public bool StopOnRetraction { get; set; } = true; }
public sealed class StopStoryboard : IStyleEdgeAction  // by OBJECT reference — no name registry (deliberate WPF
{ public Storyboard? Storyboard { get; set; } }        //   divergence; resources give identity for free)
public static class AnimationDiagnostics { public static event Action<StoryboardTrackError>? TrackError; }
```

A shared `Storyboard` is a description; `Begin` creates a per-scope instance keyed **`(igniter, scope)`** — the `BeginStoryboard` action instance for edge ignitions, the `Storyboard` itself for imperative `Begin` — so two rules sharing one storyboard resource never fight; `StopStoryboard` stops every live instance on the scope across igniters. Element-independent validation (track `T` vs property type, `Source` perpetuity vs `Repeat`, `RepeatAnimation` overflow) runs at seal; a `Style` sealing on attach also seals referenced storyboards, surfacing type errors with (storyboard, track index, property) at attach, not first hover. **Error-policy split:** imperative `Begin` *throws* on an unresolvable `TargetName`; edge-ignited begins **never throw** (runtime, driven by pseudo-class flips under any-event motion) — failures route to `AnimationDiagnostics.TrackError`, the failing track is skipped, siblings proceed. Template-scoped ignition resolves names in the template namescope, so the template barrier holds by construction.

**No routed `EventTrigger` in v1.** The one motivating consumer — S4's modal-attention flash — is a transient pseudo-class pulse: S4 sets the interaction state through S3's plumbing and clears it ~600 ms later with a `UITimer`, so the existing edge-action path animates it. Routed-event triggering is a recorded deferral (§9.12).

### §9.4 Handoff — `SnapshotAndReplace`

**Single From-snapshot rule: the only `From` snapshot is the factory invocation at track start** (synchronous inside `Begin` for `BeginTime == 0`). On retarget of a live (element, property): retire the old instance (`Stopped`, evicted, no `Completed`); **immediate replacement** builds the new timeline while the old handle is still attached (the snapshot reads the presented animated value — no visual jump), attaches the new handle (store's last-started-wins detaches the old), then defensively retracts the old (idempotent no-op); **delayed replacement** retracts the old handle at `Begin` — base shows during the delay window, consistent with "Delayed = property untouched". Retraction promotion is *not* a base change, so a delayed handoff cannot spuriously ignite a transition. `GetBaseValue<T>` is never used for handoff.

### §9.5 Transitions (implicit animations — phase A3)

`Transition` family (`DoubleTransition`, `ColorTransition`, `BrushTransition`, `Int32Transition`, `MarginsTransition`) in a `TransitionCollection` set via the attached `Transition.TransitionsProperty` (style-settable; themes declare hover fades). The collection **seals on arm** (attached ∧ non-null; replacing the collection property re-arms). Semantics (pinned): a change to the **effective base** — the winner among sub-Animation priorities; a Style flip shadowed by a LocalValue does not transition — starts an Animation-priority run with `From = isAnimated ? GetValue(property) : oldEffectiveBase` (*not* `GetValue` in the non-animated case: Fork A mutates the effective value before notification, so `GetValue` already equals the new base at delivery), `To = newEffectiveBase`, `FillBehavior.Stop` (zero steady-state animation entries). Animation-priority writes never re-trigger the observer (structural loop safety). Equal From/To skips; initial style application and attach do not transition; `AnimationsEnabled == false` ⇒ no transition starts. Gated on the Fork A winning-base observer seam (§9.10 item 5). Oracle test pinned for A3: *a hover flip with no prior animation in flight transitions from the old value*.

### §9.6 Lifetime, teardown, idle

- **Detach stops animations — production behavior from A1.** On S1's detach notification the scheduler retracts + evicts every instance targeting the detached subtree and every storyboard scoped in it; **no `Completed` raise**. Consequences: re-attach does not restore HoldEnd values; a detached element's perpetual animation cannot pin the idle gate. Idempotent against Fork B's own retraction edges on the same detach.
- **`Shutdown()`** retracts every live handle (bases resurface so S6 can render one final base-value restore frame), evicts everything, raises no `Completed`, leaves the scheduler inert (subsequent `Begin` throws). **S6's canonical teardown order: `CloseAllAsync()` → `Shutdown()` → optional final restore frame → pump cancel → terminal-mode restore.**
- **Idle:** `HasActiveAnimations` ≡ Delayed + Running instances + running `UITimer`s (excludes Paused/Holding/Completed — Holding costs nothing per frame; the Animation slot holds the value statically). Delayed instances keep the flag true so S6 can't sleep through a `BeginTime` (frames during delay are cheap end-to-end; wake-at-time deferred). DEBUG leak tracker (A2) covers the residual shape: instances whose target was never attached for > N frames.

### §9.7 `AnimationsEnabled` (reduced motion)

`Begin` while `false`: finite ⇒ attach, snapshot, write `ValueAt(Duration)` synchronously, apply `FillBehavior`, enqueue `Completed` for the next completion pass (never raised synchronously from `Begin`); perpetual ⇒ **no handle, handle born `Stopped`, base shows** — base is the reduced-motion rendition of a pulse; pinning `ValueAt(0)` at Animation priority forever would block base styling. Flip `true → false` applies at the next `Tick`: finite instances (incl. Delayed/Paused, in-flight transitions) snap to `ValueAt(Duration)` through the normal completion branch; perpetual instances retract; Holding unaffected. `false → true` is prospective. Meaningful over SSH/slow links and for accessibility.

### §9.8 `UITimer` (S5-owned; frame-aligned)

```csharp
public sealed class UITimer : IDisposable
{
    public static UITimer Start(TimeSpan dueTime, Action callback);                     // one-shot
    public static UITimer Start(TimeSpan dueTime, TimeSpan interval, Action callback);  // repeating after dueTime
    public bool IsRunning { get; }
    public void Restart();                       // re-arm dueTime from the current frame clock
    public void Stop();                          // = Dispose(); idempotent
}
```

Registered with the thread-ambient scheduler; due timers fire during `Tick` at the frozen frame time (latency ≤ one paced frame period), sharing `FrameClock` ⇒ `FakeTimeProvider`-deterministic. Running timers count in `HasActiveAnimations`, so S6's idle guard covers them. Callback exceptions surface through S6's guarded `Tick`; scheduler state stays consistent (flag-then-sweep). Timers are not element-scoped — owners stop them on detach (S8's unhook convention). Consumers: S8 RepeatButton / menu hover-open / ToolTipService; S4's modal-attention pulse clear (§9.3).

### §9.9 Property targeting (invariant 3 as authoring guidance)

**Animated slides/fades/clips write only composite-shaped properties and re-composite a cached raster; only brush/content-shaped targets re-raster, at the store's equality-gated cadence.**

| Intent | Target (S1) | Effects | Cost |
|---|---|---|---|
| Move/slide panel, toast, drawer | `RenderOffsetColumn`/`RenderOffsetRow` (signed ints; negative placement lives here) | `AffectsComposite` | Recomposite cached raster; `Int32Interpolator` + store equality gate ⇒ writes only on cell crossings. Never re-rasters. |
| Fade, modal dim | `Opacity` (double 0–1, coerce-clamped; S1 maps to the `CompositeParameters` byte) | `AffectsComposite` | Recomposite; byte quantization ⇒ ≤256 distinct updates per fade, identical bytes ⇒ compositor no-op. |
| Reveal/wipe/collapse-without-layout | `CompositeClip` (`Rect?`; S1, boundary-promoting) | `AffectsComposite` | Recomposite; `RectInterpolator` rounds+clamps (ushort `Rect` safety under `Back*` overshoot). Animate the `Rect` directly — clip inside `CompositeParametersInterpolator` snaps at 0.5. |
| Color/brush pulse | `Background`/`BorderBrush`/`Foreground` | `AffectsRender` | Re-raster per changed frame — keep pulsing scenes small or pulse `Opacity` instead. |
| Layout size/position | `Width`/`Height`/`Margin` (`Margins`-typed; **signed** since P2.6 — tracks may legitimately interpolate through negative side values) | `AffectsMeasure` | Full measure/arrange; cell-quantized interpolators gate to actual cell changes (sizes clamp ≥ 0; margins do **not**). DEBUG warns on *perpetual* `AffectsMeasure` animations. |
| Typewriter/counters | `Text`, content props | `AffectsRender` (+Measure) | Re-raster; `Int32Animation` over an index gives per-character cadence. |

This subsystem never touches `Scene`/`CellBuffer`/compositor (invariant 2) — every effect flows through the store and S1's `PropertyEffects` routing. Terminal-specific notes carried from the maps: no transform animations (`CompositeParameters` is integer translate + opacity + clip — no sub-cell rotate/scale; the equality gate is the terminal's natural frame limiter); on `ansi256`/`ansi16` an animated truecolor pulse quantizes at emit, so cost stays in re-raster, not bytes — themes should branch on capability classes to substitute opacity fades; ordered dither disables scroll detection (don't mix with animated full-screen scrolls); sliding Sixel-bearing scenes re-encodes fragments per cell crossing — animate image panels on Kitty-class terminals or keep anchors static.

### §9.10 Cross-subsystem contracts

**Fork A (property system; engine-ledger items):** (1) `AnimatedValueHandle<T> UIObject.BeginAnimation<T>(StyledProperty<T>)` — last-started-wins, dispose ⇒ retraction + promotion; (2) `GetValue`/`GetBaseValue`; (3) `bool AnimatedValueHandle<T>.SetValue(T)` returning "effective value actually changed" (the store's equality gate is the animation cadence valve); (4) `bool IsDetached`, post-detach `SetValue` a silent no-op, **idempotent `Dispose`**; (5) **winning-base change observer** (gates A3; *not* small): fires only when the effective base — the winner among sub-Animation priorities — changes, delivering `(oldEffectiveBase, newEffectiveBase, isAnimated)`, with inherited changes routed through the same seam via Fork A's second carrier.
**S1:** attach/detach lifecycle notification (production-critical from A1); registrations per the §9.9 table (`RenderOffsetColumn`/`RenderOffsetRow`/`Opacity`/`CompositeClip` = `AffectsComposite`).
**S2:** template-aware `UIElement.FindName(string)` (over `NameScope.FindEnclosing`) for `TargetName` resolution.
**Fork B (styling):** invoke `IStyleEdgeAction` on activation/retraction edges in rule-document order; style seal-on-attach seals referenced storyboards. The no-throw contract is ours.
**Fork C (XAML):** converters for `UIProperty`, `Easing` (via `Easings.TryParse`), `Optional<T>` (inner-type), `RepeatBehavior`, `TimeSpan`; `Storyboard`/`TransitionCollection` are ordinary resource-dictionary objects — no deferral contract.
**S6:** construct + `Install` once per UI thread with the session's `TimeProvider`; drive `IAnimationFrameDriver` per §9.1; idle gate reads `HasActiveAnimations`; call `Shutdown()` in teardown per §9.6.

### §9.11 Additions to `Cursorial.Animation` and the interpolator registry

`DelayAnimation<T>` (holds `inner.ValueAt(0)` during the delay; `Duration = checked(delay + inner)`, perpetual inner guarded — no arithmetic) and `SequenceAnimation<T>` (children own half-open intervals `[start_k, start_k+1)` — next child wins at boundaries, non-final zero-duration children never sampled, clamp-to-last at/after total; perpetual legal only in last position; `checked` sum) as `IAnimation<T>` decorators, plus `.Delay`/`.Then` extensions and `Easings.TryParse` (catalog names + `cubic-bezier(x1,y1,x2,y2)`), Elastic/Bounce/`CubicBezier` easings (A2). Pure timeline arithmetic — mechanism by the §9 definition, usable by non-UI consumers. **`Parallel` is deliberately absent**: one `IAnimation<T>` yields one value; parallelism across properties is what a `Storyboard` is; staggering is per-track `BeginTime`.

`Interpolator.For<T>()` / `Register<T>()` — pre-seeded: double, int, `Color` (`Cursorial.Animation`); `PointD`, `Size`, `Rect`, `RelativePoint`, `IBrush`, `CompositeParameters`, `Margins` (`Cursorial.Drawing`); throws with a "register or specify" message for unknown `T`. Threading pinned: process-global; registration at startup on the UI thread (DEBUG-asserted); lock-free immutable-snapshot reads (keeps the multi-session door open). `MarginsInterpolator` **landed (P8)** in `Cursorial.Drawing` beside the `Size`/`Rect` family: per-side linear, rounded, **signed** (amended P2.6 — S1's margins are signed per matrix LD19, so tracks legitimately interpolate through negative values; registered via the Drawing `[ModuleInitializer]`). Decided: `Opacity` is `double` 0–1 with coerce-clamp (WPF-familiar, easing-friendly; byte quantization happens in S1's composite mapping, where record equality makes the compositor the final no-op gate).

```csharp
// Toast: slide in from off-screen right + fade, auto-dismiss after 4 s. Both targets AffectsComposite ⇒ recomposite only.
toast.BeginAnimation(UIElement.RenderOffsetColumnProperty,
    new Int32Animation(from: 30, to: 0, TimeSpan.FromMilliseconds(250), Easings.CubicOut));
var fade = toast.BeginAnimation(UIElement.OpacityProperty, new SequenceAnimation<double>(
    new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(250)),
    new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(400), Easings.QuadIn).Delay(TimeSpan.FromSeconds(4))));
fade.Completed += _ => window.CloseToast(toast);   // detach evicts both Holding instances + this closure — no leak
```

**Phasing:** **A0** spine — `FrameClock`, scheduler + driver, `AnimationInstance<T>`, `BeginAnimation`/`AnimationHandle`, perpetual/overflow guards, reentrancy contract, interpolator registry, mechanism combinators; oracle-pinned completion/handoff/PingPong/reentrancy matrices authored *before* the scheduler. **A1** — storyboards, tracks, `(igniter, scope)` instancing, edge actions + `AnimationDiagnostics`, the production detach stop pass, `UITimer`; toast/focus-pulse demo. **A2** — Pause/Resume/Seek/`SkipToEnd`, full `AnimationsEnabled` flip semantics, Elastic/Bounce/CubicBezier, DEBUG leak tracker + perpetual-on-`AffectsMeasure` diagnostic. **A3** — Transitions (gated on the Fork A winning-base observer seam; budget the seam here).

### §9.12 Deferred (carry forward)

- **Routed-event `EventTrigger`** — the only v1 consumer (modal attention) is served by the pseudo-class pulse + `UITimer` path; add when a second consumer appears.
- **`SpeedRatio`** — multiplies into pause/seek bookkeeping; add when a consumer exists.
- **`HandoffBehavior.Compose` + additive/`By` animations** — needs value composition in the Animation slot (store change); recorded re-addable.
- **Signed-margin (`Thickness`) animation vocabulary** — ~~deferred~~ resolved by P2.6 (matrix LD19): `Margins` itself is signed, so no separate `Thickness` type is needed; the P8 `MarginsInterpolator` interpolates signed (no zero-clamp).
- **Wake-at-time scheduling** (for `BeginTime` delays and `UITimer` due times) — frames during delay are cheap; optimize only if idle-power profiling demands.
- **Keyframe binary search** — O(n) scan is fine at typical keyframe counts.
- **Weak-target instances** — the production detach pass + `IDisposable` discipline covers it; DEBUG tracker watches the never-attached residue.
- **`Parallel` as `IAnimation<T>`** — no single-value semantics without additive composition; `Storyboard` is the cross-property parallel.

---

## §10 S6 — Application model, dispatcher, and the frame loop

Namespace `Cursorial.UI`; test host in `Cursorial.UI.Testing`. S6 is the composition root and the only owner of "time, thread, and the byte pipe": it acquires the terminal host, runs the one dedicated UI thread, drives the single authoritative frame loop, owns the screen `CellBuffer` + `FrameRenderer`, and tears the terminal down on every exit path. It orchestrates against `rendering-session.md` §7 (the validated demo loop) and `input.md` (single-shot device, pump threading).

### §10.1 Scope and composition

**Owns:** `UIApplication` (one merged application object — host/loop half here, theme surface per S7), the frame loop and pacing policy, `ITerminalHost`, the input pump, `UIDispatcher` + `UISynchronizationContext`, the out-of-band control-sequence channel, `IClipboardService`, exception policy + `IUserCodeGuard`, shutdown/teardown, `RenegotiateAsync` orchestration, and `UITestHost`. **Does not own:** input semantics (S3), scene assembly/z-order/compositing (S1/S4 — including the `SceneCompositor` and `ScenePool`, which the render system owns; S6 owns only the target `CellBuffer` and `FrameRenderer`), styling activation (Fork B), animation values (S5), the property system (Fork A).

**Normative, loop-forced:** there are **no nested dispatcher loops** (no WPF `PushFrame`). Modal results are async-only — S4's shape is `Task<TResult> ShowDialogAsync(...)`, frame-coherent via `UISynchronizationContext`. A blocking `ShowDialog` would deadlock by construction and is not provided anywhere in the stack.

`UIApplication` news up the default subsystems in fixed order inside `RunAsync` (no DI container in v1): at `Build()` the `UIDispatcher` (owner = building thread), sync context, app `Resources`/`Styles`, thread-local `Current`; in `RunAsync` the host/capabilities/palette/`ActualThemeVariant`/buffer/renderer/UI-mode bytes; on the UI thread, in dependency order: styling engine (Fork B) → window/render system (S4 over S1; one object typically implements `IWindowSystem` + `IRenderSystem`; receives the `IUserCodeGuard`) → layout system (S1 facade) → input router (S3) → animation driver (S5). The seam references feed an internal `FrameLoop` core (`RunFrameOnce(in FrameTime)`) shared by the production loop and `UITestHost` stepping.

### §10.2 `UIApplication` and builder

One application type (S6 + S7 surfaces merged; S4's window-manager ambient joins it):

```csharp
public sealed partial class UIApplication : UIObject, IResourceHost, IAsyncDisposable
{
    public static UIApplicationBuilder CreateBuilder();
    public static UIApplication? Current { get; }       // [ThreadStatic]: Build thread pre-run, UI thread after;
                                                         // null elsewhere — parallel UITestHosts never cross-wire
    public UIDispatcher Dispatcher { get; }
    public TerminalCapabilities Capabilities { get; }    // undecorated snapshot; replaced on renegotiate
    public InputCapabilities EffectiveInputCapabilities { get; }  // post-decoration; what S3 introspects
    /// Access-key gate (pinned formula, computed from the UNDECORATED snapshot so KeyReleaseSynthesizer
    /// can never make it lie): (Keyboard.DistinguishesKeyUpDown && Keyboard.ReportsRepeats)
    /// || Protocol.Win32InputMode. (Equivalent to the input map's conjunct form — Win32 input mode
    /// implies DistinguishesKeyUpDown.)
    public bool SupportsAltKeyTracking { get; }
    public FrameTime CurrentFrameTime { get; }
    public event EventHandler<CapabilitiesChangedEventArgs>? CapabilitiesChanged;       // UI thread
    public event EventHandler<DispatcherUnhandledExceptionEventArgs>? DispatcherUnhandledException;

    // S7 surface (one object — semantics specified in the S7 section):
    public ResourceDictionary Resources { get; }         // lookup-walk terminus
    public Styles Styles { get; }
    public CursorialTheme Theme { get; set; }
    public ThemeBase? RequestedThemeBase { get; set; }
    public ColorTier? RequestedColorTier { get; set; }
    public ThemeVariant ActualThemeVariant { get; }      // recomputed on renegotiate
    public event EventHandler? ResourcesChanged;
    internal void OnCapabilitiesChanged(TerminalCapabilities caps);   // S7's leg of the four-call fan-out

    public IWindowSystem WindowManager { get; }          // S4 ambient (Application.Current.WindowManager)
    public IClipboardService Clipboard { get; }

    public ShutdownMode ShutdownMode { get; set; }       // default OnMainWindowClose
    public Task<int> RunAsync(Func<Window> factory, CancellationToken ct = default);  // PREFERRED: runs on UI thread
    public Task<int> RunAsync(Window mainWindow, CancellationToken ct = default);     // Build-thread hand-off
    public void Shutdown(int exitCode = 0);              // thread-safe; idempotent (first code wins)
    public void RequestRender();                         // thread-safe (Interlocked flag + wake)
    /// Thread-safe out-of-band byte channel, drained in Phase 6 AFTER the renderer's delta (forces a
    /// flush even on an empty delta). OSC-class only (title, clipboard, pointer shape, palette);
    /// SGR/CUP/ED/scroll FORBIDDEN — FrameRenderer is the sole owner of that state.
    public void QueueControlSequence(Action<IBufferWriter<byte>> writePayload);
    public void NotifyResized(int columns, int rows);    // BYO embedders (no ResizeEvents arrive otherwise)
    public ValueTask RenegotiateAsync(CancellationToken ct = default);  // UI thread only (VerifyAccess)
    public IDisposable RegisterDeviceResponseSink(Action<DeviceResponseEvent> sink);
    public ValueTask DisposeAsync();
}

public enum ShutdownMode { OnMainWindowClose, OnLastWindowClose, OnExplicitShutdown }
/// One timestamp per frame — the single time truth. Mid-frame code never re-samples the clock.
public readonly record struct FrameTime(long FrameNumber, TimeSpan Elapsed, TimeSpan Delta);

public interface IClipboardService     // S6-provided; S8 consumes
{
    void SetText(string text);                              // OSC 52 write via QueueControlSequence (ClipboardWriter)
    Task<string?> GetTextAsync(TimeSpan? timeout = null);   // OSC 52 query via response sink; null on timeout/unsupported
}
```

Builder (all no-I/O; the host opens inside `RunAsync`; `Build()` and `RunAsync` are single-use): `WithSessionOptions`, `WithSession(session, disposeWithApp = false)`, `WithTerminalHost(host, …)`, `WithFrameRate(int)` (clamped [1,120], default 30), `WithTimeProvider`, `WithPalette(Action<TerminalThemeBuilder>)` (OSC 4/10/11/12), `WithClickOptions` — **default `ClickCountTarget.ButtonDown`, `SynthesizeClickEvents = false`** (the S3 router contract; enabling Click synthesis violates it and is documented as such), `WithKeyReleaseSynthesis` (opt-in), `UseAlternateScreen(true)`, `WithRendererOptions(orderedDither: false)`, `ExitOnUnhandledCtrlC(true)`.

**`Window.Title` → OSC 2 is wired in v1:** the main window's `Title` flows through `QueueControlSequence` via `WindowWriter.WriteTitle` (re-queued on change); the title is restored at teardown.

```csharp
var app = UIApplication.CreateBuilder()
    .WithFrameRate(30)
    .WithPalette(t => { t.Background = Color.FromHex("#1e1e2e"); })   // OSC 11 + capability rewrite
    .Build();                                    // Current + Dispatcher exist from here
return await app.RunAsync(() => new MainWindow());   // terminal restored before this returns
```

### §10.3 `UIDispatcher` and threading

`RunAsync` does async startup on the caller, then spawns **one dedicated foreground thread** ("Cursorial UI") that runs the loop synchronously; `RunAsync` completes with the exit code. The dispatcher's owner is the `Build()` thread until the loop thread's first act, `TransferOwnershipToCurrentThread()` (one-shot; debug-observable — `VerifyAccess` from the old thread throws after it). Pre-run `UIObject` construction is legal on the Build thread; the `Func<Window>` overload sidesteps the hand-off entirely. Fork A's per-element affinity assert is `Dispatcher.VerifyAccess()` via thread-local `UIApplication.Current` (invariant 6).

`UIDispatcher`: `CheckAccess`/`VerifyAccess`, `Post(Action)` + allocation-light `Post<TState>`, `InvokeAsync` (Action/Func/async variants; **always queues, never runs inline even from the UI thread** — inline would break frame-phase ordering; callers wanting inline call the delegate directly), `ShutdownToken`. **No priority tiers exist** (invariant 1). After shutdown: `InvokeAsync` returns a canceled `Task`, `Post` is dropped. `UISynchronizationContext`: `Post` → dispatcher; `Send` inline when `CheckAccess`, else blocking invoke (documented deadlock hazard). Async event-handler continuations land in the dispatcher queue and run in a later frame's Phase 2 — frame-coherent by construction. The loop's only async touchpoint is one bounded blocking flush per frame (pipe drain is thread-pool-side; cannot deadlock; internals `ConfigureAwait(false)`).

### §10.4 Input pump and capability provenance

Exactly one `ReadAllAsync` enumeration per session (single-shot contract). Assembly: `host.Input` → optional `KeyReleaseSynthesizer` (opt-in, innermost) → `WithClickSynthesis` (S3-contract defaults, outermost). The pump `Task.Run`s the pull enumeration into a `ConcurrentQueue<InputEvent>` + `Wake()`; EOF ⇒ shutdown; faults land in an `Interlocked` slot surfaced **once** on the UI thread. **The pull surface is used, never `EventInputDevice`** (it swallows handler exceptions; S6 owns exception visibility). S6 never disposes the decorated device — `host.DisposeAsync()` owns transport lifecycle (the session drains trailing reports through it).

Provenance split: `EffectiveInputCapabilities` = post-decoration (recomputed on renegotiate by re-applying recorded decoration projections) — S3 reads this for clicks/gestures; `SupportsAltKeyTracking` = always the undecorated snapshot (§10.2 formula), so the opt-in synthesizer's timer-derived claims cannot affect the access-key gate. `KeyReleaseSynthesizer` is **off by default** (flickery held-view, best-effort claims, no modifier events). BYO hosts get no `ResizeEvent`s; `NotifyResized` injects a synthesized one into the same queue.

### §10.5 The frame loop (normative phase order)

```
RunLoop():                                    // dedicated UI thread; FrameLoop core shared with UITestHost
  dispatcher.TransferOwnershipToCurrentThread(); SetSynchronizationContext(uiSyncContext)
  // capability fan-out — the four calls, explicit and ordered (no event indirection):
  styling.OnCapabilitiesChanged(caps); inputDispatcher.OnCapabilitiesChanged(caps)
  accessKeys.OnCapabilitiesChanged(caps); application.OnCapabilitiesChanged(caps)   // S7 leg
  windowSystem.Show(ResolveMainWindow())      // factory overload invoked HERE, on the UI thread
  while (!_shutdownRequested):
    // PHASE 0 — one timestamp; freeze the animation clock FIRST, so BeginAnimation during the
    // input drain stamps StartTime = T_N:
    time = new FrameTime(_frame, elapsed, delta);  animation.BeginFrame(in time)
    // PHASE 1 — input drain to empty (inline try/catch; no per-event closures at motion rates):
    //   ResizeEvent → coalesce (last wins);  DeviceResponseEvent → snapshot-iterate sinks;
    //   other → if (inputTarget.Dispatch(e) != DispatchedHandled) ApplyDefaultGestures(e)
    if (resize): ApplyResize(resize); resizedThisFrame = true            // §10.6
    surface _pumpFault once;  if (_streamEnded) Shutdown(0)
    windowSystem.DrainDeferredTopology()      // S4's deferred popup/z work lands at this boundary
    // PHASE 2 — dispatcher jobs, SNAPSHOT count (jobs posted during the drain run next frame)
    // PHASE 3 — styling.FlushPendingActivations()  (all phase-1/2 pseudo/class flips reach fixpoint
    //           before animation/layout/render — invariant 1)
    // PHASE 4 — animation.Tick() at the frozen clock; styling.FlushPendingActivations() again
    //           (animation-driven flips); animation.TickNewlyStarted() — storyboards ignited this
    //           frame (incl. by that second flush) sample at elapsed-zero: no one-frame From-snap
    // PHASE 5 — layout.RunLayoutPass() — ONE call per frame: each window's S1 LayoutManager runs its
    //           internal fixpoint (16 passes + one LayoutUpdated retry; non-convergence ⇒ S1's
    //           AbandonPendingLayout, rate-limited diagnostic — never pins HasPendingLayout).
    //           Ends: windowSystem.OnLayoutCompleted()
    // PHASE 6 — render, GATED on !_renegotiating (the negotiator owns the pipe during its window):
    //   renderNeeded = HasDirtyVisuals || layoutRan || resizedThisFrame || _renderRequested
    //   renderSystem.RenderFrame(_buffer, in time)   // defined as: raster invalidated scenes in z
    //     order → CompositeFrame onto the RETAINED target → set buffer cursor state (S1's caret
    //     service; S4 folds surface offsets). Handled draw exception ⇒ changed = true (conservative
    //     emit; the compositing invariant keeps the buffer safe). guard.IsFatal ⇒ unwind to teardown.
    //   inputDispatcher.UpdateHover()                // once per rendered frame, after layout AND
    //     composite parameters are final — hover under scroll/animation, detach-deferred hover work;
    //     flips it queues are caught by the Phase-7 guard
    //   _renderer.Render(_buffer, _scratch)          // diff; sync-output brackets emitted by renderer
    //   drain _controlSequences into _scratch AFTER the delta (forces flush even on an empty delta)
    //   ONE Write + ONE blocking flush per frame (pooled ArrayBufferWriter<byte>, reset per frame)
    // PHASE 7 — idle (every path is paced; no unpaced re-entry):
    //   workPending = HasDirtyVisuals || HasPendingLayout || styling.HasPendingActivations
    //   workPending or animation.HasActiveAnimations (incl. pending UITimers) ⇒ wait out the
    //   remaining frame budget; else event-driven with rate clamp: if this frame rendered, wait the
    //   remaining budget first (any-event mouse-motion storms coalesce into ONE next frame — a pointer
    //   sweep renders ≤ 1/FrameInterval), then park on _wake for free until input/Post/RequestRender/
    //   QueueControlSequence/Shutdown. Single advancement point: _frame++; _lastElapsed = elapsed.
  RunTeardown()                                // §10.7 — also runs from finally on fatal exception
```

**Wake protocol (normative; ordering load-bearing):** `SemaphoreSlim(0,1)` + `Interlocked` flag. Producer: enqueue first, CAS flag 0→1, `Release()` only on CAS success. Consumer: on `Wait` return, **clear the flag before draining**. The wrong order loses the race where a post-drain producer skips `Release` and the loop parks on a non-empty queue — a frozen app. `FrameInterval = 1000/fps` ms, default 30 fps; waits measure from frame start (heavy frames self-throttle); idle costs zero CPU and zero bytes.

### §10.6 Startup, resize, `RenegotiateAsync`

**Startup (in `RunAsync`, before the UI thread):** host open (`TerminalSessionHost` over either `TerminalSession.OpenAsync` factory, with `EmergencyRestoreBytes`) → initial size query (`host.QuerySizeAsync`, fallback `Console.WindowWidth/Height`, then 80×24; the session's startup `ResizeEvent` corrects it in frame 0/1) → palette theming + **capability rewrite** (apply OSC theme, then rewrite `DefaultForeground/Background` into the capability snapshot so `CellBuffer` alpha-composites against the real themed RGB; register the palette's response sink) → `ActualThemeVariant` computed post-rewrite → `CellBuffer` + `FrameRenderer` from the **negotiated** capabilities (quantization protection) → alt-screen entry (or clear-screen fallback) + SGR reset → start pump → start UI thread (composition §10.1, the four capability calls, `Show`).

**Resize (`ApplyResize`, coalesced last-wins per frame):** `_buffer.Resize` (contents discarded; renderer full-redraws on dimension change) → `renderSystem.OnViewportResized(size)` — inside which the render system, as compositor owner, **constructs a fresh `SceneCompositor` and runs `InvalidateAllSurfaces`**, re-arranges windows, and invalidates window roots so full relayout lands in Phase 5 of the same frame. Coalescing reorders the in-band stream (documented); S3 clamps out-of-viewport positions, never throws.

**`RenegotiateAsync` (UI thread, rare):** set `_renegotiating` **before** the await — Phase 6 (delta emission *and* control-sequence drain) is gated, because the negotiator writes probes to the same non-thread-safe `PipeWriter` during the ~500 ms window. Then `await host.RenegotiateAsync()`; on success: re-apply the palette capability rewrite; `_renderer.Close` + flush; rebuild `FrameRenderer` + `CellBuffer`; recompute `EffectiveInputCapabilities`; the render system constructs a **fresh `SceneCompositor`** (and resets the `ScenePool`), runs `InvalidateAllSurfaces`/per-window `InvalidateAll`, and **re-stamps `RenderTree.Capabilities`** on every window; then the **four capability calls in order** — `styling.OnCapabilitiesChanged` → `inputDispatcher.OnCapabilitiesChanged` → `accessKeys.OnCapabilitiesChanged` → `application.OnCapabilitiesChanged` (the S7 leg recomputes `ActualThemeVariant` and pulses `ResourcesChanged`; **color-tier `caps-*` classes are stamped from the effective tier** — `ActualThemeVariant.Tier`, honoring `RequestedColorTier` — while `caps-motion`/`caps-kitty-keyboard`/`caps-unicode|ascii` stamp from the negotiated snapshot); raise `CapabilitiesChanged`; clear the gate; full relayout + redraw (`RequestRender` persists across the window). On failure the session keeps the old negotiator — clear the flag, change nothing.

### §10.7 Shutdown, teardown, signal net

`Shutdown(int)`: CAS exit code, set flag, cancel `ShutdownToken`, wake. `ShutdownMode` applied on S4's `WindowClosed`. Default gesture: an **unhandled** Ctrl+C `KeyEvent` (raw mode — Ctrl+C is input, not SIGINT) triggers `Shutdown(0)` when `ExitOnUnhandledCtrlC`; it routes through S3 first (`DispatchedHandled` suppresses it — a text box can claim it for copy).

**Canonical teardown (runs in `finally` — crash paths restore the terminal too; every step best-effort/idempotent):** (0) `SetSynchronizationContext(null)` first — all subsequent awaits are blocking `GetResult()`; then drain `_jobs`, completing every `InvokeAsync` as canceled (actions not run); (1) `windowSystem.CloseAllAsync()` — pending `ShowDialogAsync` complete null; close-path detach retracts styles/bindings; (2) `animation.Shutdown()` — handles released, values revert to base — then one final restore frame (optional render so base values reach the screen); (3) cancel pump, blocking-wait; (4) `_renderer.Close` (fragment erases + re-enable autowrap); (5) show cursor; (6) SGR reset; (7) leave alt screen (or clear); plus title restore (OSC 2) when `Window.Title` was set; (8) one write + flush; (9) `palette?.Dispose()` (OSC resets); (10) `host.DisposeAsync()` (skipped for BYO unless `disposeWithApp`); (11) only now is `Console.WriteLine` safe; `RunAsync` completes (or rethrows the fatal exception). Thread-local `Current` cleared.

**Signal net:** the happy-path session restores opt-ins/termios on signals but knows nothing of alt screen / hidden cursor. S6 depends on the **additive Core seam `TerminalSessionOptions.EmergencyRestoreBytes`** (opaque bytes written via `IStdioTransports.WriteBytesSync` at the top of the session's signal path — invariant-7-clean): a conservative, unconditional string cached at startup (show cursor + SGR reset + leave-alt + autowrap; each an idempotent no-op when inapplicable). A PipeWriter fallback is **not viable** (not signal-safe; races mid-frame writes); the documented fallback is S6-owned `PosixSignalRegistration` + direct `write(2)` to fd 1. Scope: owned happy-path sessions only — `ITerminalHost.OwnsSignalHandling == false` (BYO) means S6 registers nothing; embedders own their signal strategy.

### §10.8 Exception policy

Every user-code entry point (dispatch, jobs, styling flush, animation tick + completions, measure/arrange, draw delegates) runs through the **funnel**: catch → raise `DispatcherUnhandledException` → `Handled` ⇒ continue the frame; else record fatal, run full canonical teardown, rethrow from `RunAsync`. The terminal is *always* restored before an exception escapes. The funnel is a **pattern** (inline try/catch per phase; Phase 1 is closure-free), passed down to S1/S4 as `IUserCodeGuard` for draw delegates. Handler-thrown exceptions are fatal immediately (no re-raise). `OperationCanceledException` on `ShutdownToken` bypasses the funnel. `InvokeAsync` exceptions go to the returned `Task` only; pump faults surface exactly once — `Handled` on a pump fault means the app runs on with **input permanently dead** (single-shot device; no restart exists).

### §10.9 Cross-subsystem seams

All calls on the UI thread unless noted. Core REQUIRES (invariant 7, additive): `TerminalSessionOptions.EmergencyRestoreBytes`.

```csharp
public interface ITerminalHost : IAsyncDisposable     // S6-owned; no Core change
{
    TerminalCapabilities Capabilities { get; }        // replaced by RenegotiateAsync
    IAsyncInputDevice Input { get; }                  // single-shot per host lifetime
    IOutputByteSink Output { get; }
    bool OwnsSignalHandling { get; }
    ValueTask<(int Columns, int Rows)?> QuerySizeAsync(CancellationToken ct = default);
    ValueTask RenegotiateAsync(CancellationToken ct = default);   // hosts that can't: no-op
}

public interface IUserCodeGuard                       // S6-owned; handed to S1/S4 at composition
{
    bool Run<TState>(TState state, Action<TState> userCode);  // false ⇒ fatal recorded — unwind promptly
    bool IsFatal { get; }
}

// InputDispatchResult declared in §7.4 (DispatchedHandled / DispatchedUnhandled / NotUIInput)

public interface IInputDispatchTarget                 // S3
{
    // Phase 1, one event at a time, arrival order, never re-entrant. Positions may lie outside the
    // viewport under resize coalescing — clamp, never throw. Default gestures key on != DispatchedHandled.
    InputDispatchResult Dispatch(InputEvent inputEvent);  // implemented by InputDispatcher.ProcessEvent (§7.4)
    void UpdateHover();                               // Phase 6, once per rendered frame
    void OnCapabilitiesChanged(TerminalCapabilities capabilities);
}

public interface IStyleFrameHooks                     // Fork B
{
    void FlushPendingActivations();                   // queued pseudo-flip fixpoint; MUST be cheap when empty
    bool HasPendingActivations { get; }               // O(1); Phase-7 guard + UITestHost.RunUntilIdle
    void OnCapabilitiesChanged(TerminalCapabilities capabilities);  // records; stamping at root attachment
}

public interface IAnimationFrameDriver                // S5's AnimationScheduler implements
{
    void BeginFrame(in FrameTime time);   // Phase 0, first statement — the single time source; frozen for the frame
    void Tick();                          // Phase 4: sample every active storyboard once; apply via AnimatedValueHandle
    void TickNewlyStarted();              // post-flush ignitions sample at elapsed-zero; cheap no-op when none
    bool HasActiveAnimations { get; }     // incl. pending UITimers; perpetual repeats count
    void Shutdown();                      // teardown step 2: release handles, values revert to base
}

public interface ILayoutSystem                        // S1 facade
{
    bool HasPendingLayout { get; }        // consulted ONLY by the Phase-7 idle guard
    void RunLayoutPass();                 // once per frame: each window's LayoutManager internal fixpoint
}                                         // (convergence cap + AbandonPendingLayout are S1-owned)

public interface IRenderSystem                        // S4 over S1; owns SceneCompositor + ScenePool
{
    bool HasDirtyVisuals { get; }
    bool RenderFrame(CellBuffer target, in FrameTime time);   // raster dirty scenes (z order) → CompositeFrame
                                                              // onto RETAINED target → caret cursor state;
                                                              // draw delegates via IUserCodeGuard
    void OnViewportResized(Size newSize);                     // fresh compositor + InvalidateAllSurfaces inside
}

public interface IWindowSystem                        // typically the same S4 object as IRenderSystem
{
    void Show(Window window);
    Task CloseAllAsync();                             // teardown: pending ShowDialogAsync complete null
    void DrainDeferredTopology();                     // end of Phase 1
    void OnLayoutCompleted();                         // end of Phase 5
    event Action<Window> WindowClosed;                // S6 applies ShutdownMode
    int OpenWindowCount { get; }
    Window? MainWindow { get; }
    // Modal is async-only: Task<TResult> ShowDialogAsync(...); no nested dispatcher loop exists.
}
```

**Provides to everyone:** `UIDispatcher` (Fork A's marshal point; `VerifyAccess` backs invariant 6), `FrameTime`/`CurrentFrameTime`, `Capabilities`/`EffectiveInputCapabilities`/`SupportsAltKeyTracking`/`CapabilitiesChanged`, `RegisterDeviceResponseSink`, `RequestRender`, `QueueControlSequence` (the only sanctioned out-of-band byte path — S4 titles, clipboard, pointer shape, palette), `IClipboardService`, `NotifyResized`, app `Resources`/`Styles` instances, `IUserCodeGuard`, `UITestHost`.

### §10.10 Headless testing — `UITestHost`

Every subsystem's integration harness, and the reason no test ever needs a TTY:

```csharp
public sealed class UITestHost : IAsyncDisposable     // Cursorial.UI.Testing
{
    public static UITestHost Create(UITestHostOptions? options = null);
    // Synchronous: SyntheticTerminalHost (an ITerminalHost) with scripted capabilities over an
    // in-memory sink — no probes, no negotiation. The CALLING thread becomes the UI thread (no
    // ownership transfer); frames run only when stepped. Single-thread-affine; parallel hosts
    // never cross-wire (Current is thread-local).
    public UIApplication Application { get; }
    public UIDispatcher Dispatcher { get; }
    public FakeTimeProvider Time { get; }             // manual clock; animations/gestures sample it
    public CellBuffer FrameBuffer { get; }            // LIVE accessor (replaced by resize/renegotiate)
    public ReadOnlyMemory<byte> LastFrameBytes { get; }   // wire bytes when CaptureFrameBytes

    public void ShowWindow(Window window);            // through S4 exactly as production
    public void RunFrame();                           // ONE full frame, synchronously
    public int  RunFrames(int count);                 // early-exits on shutdown
    public bool RunUntilIdle(int maxFrames = 100);    // idle = input ∧ jobs ∧ layout ∧ dirty-visuals ∧
                                                      //        styling activations ∧ animations all empty
    public void AdvanceTime(TimeSpan delta);          // FrameInterval steps, one RunFrame each

    public void SendInput(InputEvent inputEvent);     // straight into the loop's queue — no pump hop
    public void SendKey(Key key, KeyModifiers modifiers = default, string? text = null, bool withRelease = false);
    public void SendText(string text);  public void SendMouseMove(int column, int row);
    public void SendClick(int column, int row, MouseButton button = MouseButton.Left, int clickCount = 1);
    public void SendResize(int columns, int rows);
    public void SendBytes(ReadOnlySpan<byte> rawBytes);   // parser-inclusive: a REAL VtInputDevice
                                                          // constructed on Time — bare-ESC ambiguity and
                                                          // multi-click thresholds live on the fake clock
    public Task DrainParsedInputAsync(TimeSpan? timeout = null);
    public string GetRowText(int row);  public Cell GetCell(int column, int row);
    public ValueTask DisposeAsync();                  // full canonical teardown into the captured sink
}
// Options: InitialSize (80×24), Capabilities (presets: KittyTruecolor, Ansi16Legacy, NoMotion,
// NoMouseCursorShape), FrameInterval (33 ms), CaptureFrameBytes (false).
```

A trailing lone ESC commits only after `AdvanceTime` crosses the ambiguity window — never on the wall clock. Deterministic by construction: one fake clock domain for parser timestamps, gesture thresholds, animations, and `FrameTime`.

### §10.11 Terminal-specific deviations (vs WPF/Avalonia)

1. **No vsync / `CompositionTarget.Rendering`** — fixed `FrameInterval`, paced-while-animating, event-driven idle with a rate clamp; idle frames cost zero CPU and zero bytes.
2. **One render target, one byte stream** — windows are composited layers in one `CellBuffer`; `FrameRenderer` solely owns terminal SGR/cursor state; "nobody writes raw bytes" is enforced structurally (sink unexposed; `QueueControlSequence` is the lone OSC-class escape hatch).
3. **Resize is input data** — in-band, coalesced, contents discarded ⇒ full relayout + redraw (no incremental `SizeChanged`).
4. **Teardown is a correctness feature** — the terminal is the user's shell; the close order runs on crash paths, and the signal net needs UI-mode bytes only S6 knows (`EmergencyRestoreBytes`).
5. **`DeviceResponseEvent`s interleave with input** — S6's response router keeps protocol traffic out of S3.
6. **Capabilities are runtime-mutable** (`RenegotiateAsync`) — renderer/buffer/compositor rebuild + four-way re-stamp has no desktop analog.
7. **Raw mode inverts signals** — Ctrl+C is routed input policy; **single-shot input device** — one pump, faults unrecoverable; **no dispatcher priorities, no nested pumps** — modal is async-only; **blocking flush on a dedicated thread** — one write + flush per frame.

### §10.12 Deferred (carry forward)

- **`RestrictToDirtyRegions` adoption** — needs an airtight mark-every-cell contract from S1/S4; full-buffer diff is fine at terminal scale.
- **`PauseIOAsync` integration** (host `$EDITOR`/child processes) — needs a loop quiesce + `FrameRenderer.Reset()` dance; no v1 requirement.
- **Renegotiation-triggered live theme-morph animation** — v1 re-stamps and redraws atomically; transitions need S5 cooperation.
- **Frame-skip/catch-up policy** — wall-clock `FrameTime.Elapsed` already makes animations drop-frame-tolerant; catch-up only matters for game-style simulation.
- **Windows console-buffer resize events** — Core TODO; the loop consumes `ResizeEvent` source-agnostically, zero S6 change.
- **Multiple sessions / UI threads per process** — thread-local `Current` removes the static hazard; full multi-app support unvalidated.
- **`EmergencyRestoreBytes` final seam shape** (options property vs post-open setter) — tracked with the Core-side change; the direct-`write(2)` fallback stands if it cannot land.
- **Dispatcher priority tiers** — rejected permanently (invariant 1); recorded so it isn't re-proposed.
- **Nested dispatcher loops / blocking `ShowDialog`** — rejected permanently (deadlock by construction); async-only modal is the contract.

---

## §11 S7 — Resources and theming

S7 owns keyed resource storage (`ResourceDictionary`), the lookup chain, StaticResource/DynamicResource runtime semantics, theme variants, the built-in theme **infrastructure**, and resource diagnostics. Namespaces: `Cursorial.UI` (core), `Cursorial.UI.Themes` (theme assets). (The `Cursorial.UI.Media` XAML-facing builders are retired — §11.9/#8; the real `Cursorial.Drawing.Media` types are element-authorable.) Not owned: selector matching / frame arming / `StyleSortKey` (Fork B), XAML parsing and the ambient-stack push discipline (Fork C), the `ValueStore` (Fork A — S7 is a well-behaved value *producer*; retraction is store-owned, invariant 4), invalidation routing (`PropertyEffects` metadata only — invariant 2), DataTemplate probing policy (S8; S7 ships the collision-free `DataTemplateKey`). No serialization in v1 — XAML is the authoring format; inspection is the diagnostic surface.

### §11.1 ResourceDictionary and deferred entries

```csharp
public sealed class ResourceDictionary : IEnumerable<KeyValuePair<object, object?>>
{
    public object? this[object key] { get; set; }            // get realizes deferred; set pulses (Keyed)
    public void Add(object key, object? value);              // duplicate key ⇒ ArgumentException
    public bool Remove(object key);
    public bool ContainsKey(object key);                     // never realizes (nor Keys / Count)
    public bool TryGetValue(object key, out object? value);  // this dictionary only; realizes
    public void SetDeferred(object key, IDeferredResourceEntry entry);  // Fork C lazy node-graph slices
    public IList<ResourceDictionary> MergedDictionaries { get; }        // later wins; own beats merged
    public ThemeDictionaryCollection ThemeDictionaries { get; }         // keyed by ThemeVariantKey
    public Styles? Styles { get; set; }   // theme-styles channel; consumed ONLY from UIApplication.Theme (§11.8)
    public Uri? Source { get; set; }      // via ResourceDictionaryLoader.LoadCallback; null callback ⇒ informative IOE
    public bool IsSealed { get; }  public void Seal();   // deep-freeze; sealed dictionaries NEVER pulse
    public bool TryGetResource(object key, ThemeVariant variant, out object? value);  // single hop (§11.2)
    public ResourceUpdateScope BeginUpdate();            // coalesces into one CatchAll; refcounted nesting
    public int Version { get; }
    public event EventHandler<ResourcesChangedEventArgs>? Changed;
}
public interface IDeferredResourceEntry { object? Realize(IResourceScope lexicalScope); }
public interface IResourceScope { bool TryGetResource(object key, out object? value); IResourceScope? Parent { get; } }
public static class ResourceScopes   // ForElement(UIElement) / ForDictionary(dict, parent) / ForApplication()
public interface IResourceHost { ResourceDictionary Resources { get; } bool HasResources { get; } }
public readonly record struct ResourcesChangedEventArgs(ResourceChangeKind Kind, object? Key);
public enum ResourceChangeKind : byte { Keyed, CatchAll }
```

- **Keys:** `string` (ordinal), `Type` (control themes), `DataTemplateKey`; any object legal. **Values:** anything except `UIProperty.UnsetValue` — that is the miss sentinel, rejected at insert. No ComponentResourceKey type (decided): static classes of string constants (`ThemeKeys` pattern) cover terminal-scale ecosystems; re-addable additively.
- **Deferred entries** (the runtime contract for Fork C's parse-time-checked slices; named `IDeferredResourceEntry` — Fork C's `IDeferredValue` name stays with the markup-extension `AttachTo` seam) realize at most once on success, UI thread, **in place inside the slot object** — the backing `Dictionary` slot is never replaced (enumeration-safe), `Version` doesn't bump (cache fill is logically immutable; sealed dictionaries realize freely). A throwing `Realize` resets to Deferred (retried next lookup) and propagates. Cycles throw naming both keys. **StaticResource captures inside a deferred entry freeze at first realization under the then-current variant** — variant-sensitive theme references must use DynamicResource; `DeferredEntryInfo.RealizedAtVariant` makes the freeze observable.
- **Single-parent rule:** a dictionary added to two owners throws — *sealed dictionaries exempt* (they never pulse, so they're freely shared; the exemption is what legalizes template-resource multi-instance slot-in and the process-shared `BuiltIn`).
- `BeginUpdate` scopes are nestable (outermost pulses), must dispose within the dispatcher turn (debug-asserted, invariant 1); `Seal()` inside an open scope throws. `LoadCallback` is set by `Cursorial.UI.Xaml`'s module initializer; process-global static — tests save/restore.

### §11.2 Theme variants and the probe order

```csharp
public enum ThemeBase : byte { Dark = 0, Light = 1 }
public readonly record struct ThemeVariant(ThemeBase Base, ColorDepth Tier)   // IsDark/IsLight helpers;
{ public static ThemeVariant FromCapabilities(TerminalCapabilities caps); }   // NO tier-baked Dark/Light statics
public readonly record struct ThemeVariantKey(ThemeBase? Base, ColorDepth? Tier)  // wildcards; (null,null) rejected
{ public static ThemeVariantKey Parse(ReadOnlySpan<char> text); }             // "Dark", "Ansi16", "Dark+Ansi16"
```

The theme axis is 2-D and terminal-native: **light/dark × negotiated `ColorDepth` tier**. Base derives from `ColorCapabilities.DefaultBackground` (OSC 11 readback) relative luminance `> 0.5` ⇒ Light; null/non-RGB ⇒ Dark. Tier = negotiated depth. Probe order for effective `(B,T)` — precomputed static tables (8 variants, zero per-lookup work):

1. `(B,T) → (B,T−1) → … → (B,NoColor)` — exact-base tier descent;
2. `(·,T) → … → (·,NoColor)` — wildcard-base tier descent;
3. `(B,·)` — base-only catch-all, probed **last**; contractually renderable at every tier incl. NoColor.

A tier key declares a **minimum capability**; descent never ascends (a `(B,Ansi16)` entry serves Ansi16 and above unless shadowed; Truecolor entries serve only Truecolor). Tier specialization deliberately beats base specialization — tier entries exist to beat the quantizer. A key present in both `(B,·)` and any `(·,T)` is ambiguous-by-construction and flagged by a seal/load-time lint (use exact `(B,T)` keys). The variant-probe truth table is pinned verbatim in the R0 oracle matrix. Per-dictionary `TryGetResource`: ThemeDictionaries (variant probe, recursive) → own entries → MergedDictionaries last-to-first.

```xml
<ResourceDictionary.ThemeDictionaries>
  <ResourceDictionary x:Key="Dark+Ansi256">  <!-- serves Truecolor AND Ansi256 via descent -->
    <LinearGradientBrush x:Key="Theme.AccentBrush" StartPoint="0,0" EndPoint="1,0">…</LinearGradientBrush>
  </ResourceDictionary>
  <ResourceDictionary x:Key="Dark+Ansi16">   <!-- hand-picked palette beats the quantizer -->
    <SolidColorBrush x:Key="Theme.AccentBrush" Color="LightCyan"/><SolidColorBrush x:Key="Theme.WellBrush" Color="Black"/>
  </ResourceDictionary>
</ResourceDictionary.ThemeDictionaries>
```

### §11.3 UIApplication theme surface and contributed members

The theme surface lives on the single merged `UIApplication` (S6 owns the host/loop half):

```csharp
public sealed partial class UIApplication       // theme-surface half; base list declared in §10.2
{
    public ResourceDictionary Resources { get; set; }      // replace ⇒ CatchAll pulse, all roots
    public ResourceDictionary? Theme { get; set; }         // active theme; null ⇒ BuiltIn only
    public ThemeBase?  RequestedThemeBase { get; set; }    // explicit override; null = derive from terminal
    public ColorDepth? RequestedColorTier { get; set; }    // preview/testing override; null = negotiated
    public ThemeVariant ActualThemeVariant { get; }        // (override ?? derived) per axis
    public event EventHandler? ActualThemeVariantChanged;
    public event EventHandler<ResourcesChangedEventArgs>? ResourcesChanged;  // THE external variant signal
    public void OnCapabilitiesChanged(TerminalCapabilities capabilities);    // UI thread; S6 calls (§11.7)
}
public partial class UIElement : UIObject, IResourceHost  // Resources lazy-alloc; HasResources skips the hop
public partial class Control
{
    public static readonly StyledProperty<Style?> ThemeProperty;   // per-instance control-theme override
    protected virtual object ControlThemeKey => GetType();        // exact-key; NO base-chain probing
}
```

`ControlThemeKey : object` is the one control-theme key member (theme keys are dictionary keys). Exact-key semantics: `MyButton : Button` resolves *nothing* anywhere, including `BuiltIn`, unless it overrides to `typeof(Button)` or ships its own theme; a resolution miss fires a one-time debug diagnostic naming the key and chain.

### §11.4 The lookup chain

`ResourceParent(node) = node.LogicalParent ?? node.TemplatedParent` (null only at a true root). Walk from the element: at each node with `HasResources`, probe at `ActualThemeVariant`; at a template root (`LogicalParent == null`, `TemplatedParent != null`) the **owning template's resources slot in** — read `tp.TemplateInstance?.Template` then `Template.Resources` (`ControlTemplate.Resources` and `TemplateInstance.Template` are S8 members, sealed/populated at template seal) — then continue at the templated parent. `Window` is the last logical ancestor; then `UIApplication.Resources` → `UIApplication.Theme` → `ThemeContributions` → `CursorialTheme.BuiltIn` (always the final hop).

#### §11.3a Assembly theme-contribution tier

`ThemeContributions` (static, `Cursorial.UI.Themes`) is a process-shared, ordered set of **sealed** `ResourceDictionary` instances a control library registers — from a `[ModuleInitializer]` — to ship its default control themes **and the brushes/resources those themes reference** through `{DynamicResource}`/`SetResource`. It sits in the chain **between `UIApplication.Theme` and `CursorialTheme.BuiltIn`**, so an app overrides any contributed key, a contribution overrides the BuiltIn default, and a contribution's control theme may reference core `ThemeKeys` (resolved onward in BuiltIn) as well as its own keys (resolved in the same contributed dictionary). Resolution is **last-registered-wins** and **exact-key** (the §11.3 contract — a subclass opts into a base library control's theme by overriding `ControlThemeKey`, WPF `DefaultStyleKey` parity). A contribution may carry `ThemeDictionaries` for per-(base × tier) variants. Its `Styles` selector channel is **not** consumed (only `UIApplication.Theme.Styles` is, §11.8) — a library ships `Type`-keyed control themes + resources, not app-level selector styles. Registration is idempotent (by reference) and lock-free to read (COW snapshot); a late registration (after an app is live — unusual) re-pulses the current thread's app. **Why this and not a per-type `Control.Theme` default:** `OverrideDefaultValue<T>(style)` delivers the theme `Style` but is not a chain node, so a contributed control template's DynamicResource references have nowhere to resolve — the tier fixes exactly that. `Cursorial.UI.Bars` is the reference consumer (`BarsThemeModule` + `CursorialBarsTheme.BuildContribution`).

- **Named S1 REQUIRES:** template parts chain part → … → template root; the root has `LogicalParent == null`, `TemplatedParent ==` the templated control; DataTemplate-generated content has a normal logical parent and null `TemplatedParent` (no template hop; DataTemplate-own `Resources` excluded from the chain in v1).
- **Template resources are sealed** at Fork B's template seal (arming an unsealed template throws) — the hop is static and pulse-free; theme-reactive template brushes are authored `{DynamicResource}` in the body.
- **Child windows do not chain to their owner** — window → UIApplication, WPF parity.
- **StaticResource never uses this walk**: it resolves at instantiation against Fork C's lexical/ambient stack (`XamlLoadContext.AmbientResources` defaults to `ResourceScopes.ForApplication()`) — load-order explicit, forward-reference-free.
- Allocation-free: static probe spans, indexed merged loops, `HasResources` short-circuit; depth ≈ logical depth (~8). No lookup memo cache in v1 (API-compatible upgrade, benchmark-gated).

`FindResource` throws `ResourceNotFoundException` whose message renders the searched chain hop by hop; `TryFindResource` overloads take an optional explicit variant.

### §11.5 DynamicResource: producers, priorities, lifecycle

A resource reference is a value **producer, not a priority** (the Fork A stance for bindings).

```csharp
public readonly record struct ResourceReference(object Key);   // Setter.Value / {DynamicResource} currency;
                                                               // never passed through SetValue (no sentinels)
public static void SetResourceReference<T>(this UIElement element, StyledProperty<T> property, object key);
public struct ResourceSubscription : IDisposable   // wraps one registry node; copies share; default is no-op
{ public void Pause(); public void Resume(); public void Dispose(); }   // all O(1); Dispose idempotent
public interface IResourceChangeListener { void OnResourceChanged(object key, object? newValue); }
public static class ResourceServices
{
    public static ResourceSubscription Subscribe(UIElement scope, object key,
        IResourceChangeListener listener, out object? initialValue);          // UnsetValue on miss
    public static ResourceSubscription SubscribeControlTheme(Control control,
        IResourceChangeListener listener, out Style? theme);  // ONE handle: ThemeProperty observer + chain node
    public static int GetResourceVersion(UIElement scope);    // root-global monotonic version (0 when detached)
}
```

- **In a `Setter`:** the styling engine subscribes at frame-arm/first-activation; the resolved value lives inside the frame's entry and rides `BindingPriority.Style` at the owning frame's `StyleSortKey` (ControlTheme(0) / Template(1) / Theme(2) / app layers). A pulse **mutates the entry in place** (`OnEntryChanged`) — never frame removal/re-add, so no re-match, no sort churn. Frame deactivation calls `Pause()`; activation calls `Resume()` **before the frame's entries are read** (pinned Fork B ordering); frame disarm — including element detach — disposes. Both Pause/Resume are O(1) allocation-free flag writes because they ride the `:pointerover` hot path under any-event motion.
- **On a direct property** (`SetResourceReference`, or `{DynamicResource}` via Fork C's `IDeferredValue.AttachTo` seam): a producer at `BindingPriority.LocalValue`; displaced by later `SetValue`/`Bind` via Fork A's `IValueEvictionListener` (the producer disposes its subscription — no zombie clobbering); `ClearValue` detaches it.
- **Miss = `UIProperty.UnsetValue`** end-to-end (`initialValue`, and `OnResourceChanged` on transition-to-missing) — never conflated with a null-valued resource; the consuming entry reports `HasValue = false` so lower-priority sources promote. One-time debug diagnostic with the rendered chain on first miss. Type-incompatible resources are discarded with a `UIDiagnostics.OnRejectedValue` diagnostic; conversion rides the XAML converter registry.
- `SubscribeControlTheme` resolves `control.Theme ?? chain lookup by ControlThemeKey` and owns **both** watches under one handle; styling arms the result at ControlTheme(0) and re-arms on identity change (frame removal + add — store-owned retraction). Styling must not watch `ThemeProperty` separately.

### §11.6 Pulse routing, the subscription registry, and the staleness contract

One `ResourceSubscriptionRegistry` per visual root; UIApplication-level changes fan to all roots. **Cross-surface routing (popups):** a node registers under the registry of the root its **logical chain** tops out at — popup-surface elements register under the *host window's* registry, so window-scoped mutations sweep open popups; no surface→host fan map. (Regression test: menu open during theme flip.)

Registry layout: keyed buckets + one flat list; node = `(scope, listener, lastValue, resolvedVersion, flags{Paused,Dead})`. **One list, no segregated active list** — Pause/Resume are flag writes; sweeps (rare) test flags per node. Each element holds an inline handle list so detach is O(own subscriptions). Mutation → `Version++` → `Changed` → owner-link walk to the host → root registry sweep (+ `UIApplication.ResourcesChanged` for app-scope pulses), all synchronous on the UI thread (invariants 1, 6):

- **Snapshot/tombstone sweep semantics** (mid-sweep mutation is *designed*: a CatchAll theme swap re-arms styling while sweeping): per-bucket copy-on-write snapshot; nodes subscribed during a sweep aren't visited (fresh at Subscribe); nodes disposed during a sweep are tombstoned (`Dead`, skipped) and compacted after.
- Visited candidates are filtered by **scope containment** (pulsing host or its logical descendant) — which is what makes nearer-scope **shadowing** re-resolve correctly. Survivors re-resolve via the full chain; `Equals(lastValue, newValue)` short-circuits; changed values invoke the listener → entry mutation → Fork A notification → invalidation routed **only** by `PropertyEffects` (brush change ⇒ `AffectsRender` re-raster; resource-fed offset/opacity ⇒ `AffectsComposite`; invariants 2–3).
- Paused nodes catch up on `Resume()` via version compare — at most one re-resolve regardless of pulse count. **Element attach always forces one re-resolve regardless of stored version** (version counters are per-root and independent; covers cross-root moves). Re-entrant resource mutation during a sweep queues a follow-up pulse, drained to a fixpoint (generation cap 16 + cycle diagnostic).
- Cost envelope: ~1.5–3k nodes/root (~100–200 KB); catch-all sweep low-single-digit ms at rare, user-initiated cadence followed by a repaint anyway; steady-state hot-path cost is two flag writes per frame edge — by contract.

**The staleness contract (S8, normative):** sealed dictionaries never pulse and variant flips fire only `UIApplication.ResourcesChanged` — so text-bearing controls **must not** subscribe to `ResourceDictionary.Changed`. Instead they include `(ResourceServices.GetResourceVersion(this), ActualThemeVariant)` in their `FormattedText` cache keys; the next render after any pulse re-parses with fresh resolver output. The version is root-global by design (any pulse invalidates every text cache in the window) — a stated property, acceptable at rare-pulse cadence.

### §11.7 Variant lifecycle and capability coherence

Effective variant = `(RequestedThemeBase ?? derivedBase, RequestedColorTier ?? negotiatedDepth)`. S6 calls `UIApplication.OnCapabilitiesChanged(caps)` — one of the four explicitly enumerated fan-out calls in its startup and renegotiate sequences — **marshaled to the UI thread**. On change: raise `ActualThemeVariantChanged`, raise `ResourcesChanged(CatchAll, null)`, pulse every root's registry. No dictionary mutates and no dictionary `Changed` fires (Fork C's "pulse `ResourceDictionary.Changed` on re-resolution" is satisfied by this app-level event — recorded amendment).

- **Variant flip = resource-event-only** (Fork B amendment): no selector re-match, no frame re-arm. Every themed value reaches elements through a DynamicResource subscription, so the catch-all sweep is the entire mechanism; control-theme subscriptions resolve to the *same* `Style` instance (themes are keyed per Type, not per variant) → identity short-circuit → no re-templating.
- **Capability-class coherence:** styling stamps the color-tier classes (`caps-truecolor|ansi256|ansi16|nocolor`) from **`ActualThemeVariant.Tier`** (the *effective* tier, honoring `RequestedColorTier`) off `ActualThemeVariantChanged`; non-color classes (`caps-motion`, `caps-kitty-keyboard`, `caps-unicode|ascii`) stamp from negotiated capabilities. A "preview Ansi16" app gets Ansi16 resources *and* Ansi16-gated styles. A `RequestedThemeBase` flip changes only resources.
- **Glyph resources** live at color-tier keys — the color tier is the deliberate proxy for glyph capability/terminal age (`Pens.Ascii` at low tiers); genuine proxy mismatches escape via capability-class-selected styles (a `caps-ascii`-classed rule reassigning the glyph resource reference). There is no Unicode variant axis.

### §11.8 Built-in theme architecture

`CursorialTheme.BuiltIn` (in `Cursorial.UI.Themes`): the sealed, process-shared, **code-first** default dictionary (the loader lives in `Cursorial.UI.Xaml`, which depends on `Cursorial.UI` — no back edge), always the final lookup hop; `CreateDefault()` returns an unsealed structural copy for mutation. **Ownership split:** S7 owns the theme *infrastructure* — the BuiltIn dictionary, variant layout, tier-key rules, the `ThemeKeys` naming convention (`"Theme.*"` string constants — the cell-faithful **fill/foreground role tokens** `Theme.WindowBackground`, `Theme.SurfaceBrush`, `Theme.PanelBrush`, `Theme.WellBrush`, `Theme.SelectionBrush`, `Theme.HoverBrush`, `Theme.TextBrush`, `Theme.MutedBrush`, `Theme.AccentBrush`, `Theme.OnAccentBrush`, … (full table in §11.8a), plus `Theme.ObscuredOverlayBrush`/`Theme.AccessKeyIndicatorBrush` and the glyph carriers; `Theme.BorderPen`/`Theme.FocusPen` survive only as **opt-in chrome keys**, not spine members — the default look is fill-bounded, not line-bounded) and the palette spine; **S8 authors the content** — per-control templates/styles and control-specific keys — into S7's structure under `Theme.*` names.

1. **Palette:** populated per the §11.2 tier rules — no color-bearing value in `(B,·)`: RGB brushes at `(B,Ansi256)` (served at Truecolor via descent), hand-picked `Colors.*` + ASCII-glyph pens at `(B,Ansi16)`, attribute-only values at `(·,NoColor)` (which actually win on monochrome). The quantizer makes RGB *safe*; tier dictionaries make every tier *good*.
2. **Control themes:** one selector-less `Style` per `Type` key, `Children` rooted at `^` (incl. `^:access-keys` underscore rules and `^.obscured` modal dimming via `Theme.ObscuredOverlayBrush`), with a `Template` setter holding a `ControlTemplate` (whose `Content` is the `ITemplateContent`). **Every fill- and foreground-bearing setter is a `ResourceReference` into the palette** — the inheritance spine: overriding one fill/foreground `ThemeKeys` entry at a nearer scope re-skins every control with zero template work. *The re-skin proof is a fill/foreground token, not border ink:* re-pointing `Theme.AccentBrush` (or `Theme.SurfaceBrush`) at a nearer scope restyles every pressed/default state and focus accent live, no template rebuild. **Implementation status:** the **R2** palette-spine wiring (`ResourceReference` fill/foreground setters → re-skin-via-one-key) is **landed** — control themes `SetResource` into the spine and a shadowed `ThemeKeys` entry re-skins the built-in controls live (matrix row C99). The remaining default-theme work (the cell-faithful adoption, `docs/ui-layer-design/default-theme-adoption-spec.md`) sits *on top* of the wired spine: repopulate the palette with the §11.8a role tokens and add the per-control reverse-video / well-fill state rules. The `GlyphSet` keys (`CheckBoxGlyphs`/`RadioGlyphs`/`ScrollArrowGlyphs`) are live resource reads.
3. **Theme-styles channel:** type-keyed control themes arm at `ControlTheme(0)` wherever found. Selector styles a theme ships ride `ResourceDictionary.Styles`; the styling engine consumes **only `UIApplication.Theme`'s** slot — flattened merged-order-then-own-last — armed at layer `Theme(2)`, re-read on theme-origin CatchAll pulses (version-compared). Element/window `Styles` slots are ignored in v1 (debug-flagged). Layer beats specificity, so app styles always win over theme styles. *(Landed R2/B13: the theme's own top-level `Styles` slot is consumed at `Theme(2)` and re-matched on theme reassignment + theme.Styles mutation via `StyleEngine.OnThemeStylesInvalidated`. The app-theme leg is gathered with an order-base above the BuiltIn framework leg, so an app-theme rule **categorically overrides** an identical BuiltIn rule — the resource-model "app.Theme layers over BuiltIn" applied to styles — while a BuiltIn rule the theme does not redefine is unaffected. A variant flip stays resource-only per CD15. The caps-unicode/caps-nocolor styles are authored in `Cursorial.UI.Themes.Xaml/Themes/Styles.xaml` (the data twin of `CursorialThemeStyles`), via the `<ResourceDictionary.Styles>` loader path + the `GlyphSetCarrier` string converter (`"unchecked|checked|indeterminate"`); `AccessKeyCue` stays BuiltIn-only (its `AccessKeyManager` owner is outside the default xmlns map and it is always supplied by the framework leg). Residuals: flattening `Styles` nested in the theme's `MergedDictionaries`, and the version-compare re-read short-circuit — the re-read is currently unconditional.)*

**Override paths** fall out of the chain: per-control = shadow the `Type` key at window/app scope or set `Control.Theme`; wholesale = `UIApplication.Theme = dict`; per-value = shadow a `ThemeKeys` string anywhere. **Backstop scope (precise):** BuiltIn backstops *partial app themes* over the shipped control set; it does not cover novel `ControlThemeKey`s (§11.3).

### §11.8a Cell-faithful theme conventions (the default-theme contract)

The built-in theme is **cell-faithful and fill-bounded**. Its governing artifact is the default-theme gallery
(`docs/ui-layer-design/default-theme-gallery-final.html`) — the source of truth for the role-token set, the
dark/light hex per token, the part·state→token mapping, and the per-control resource-key taxonomy; the
reconciliation, tier values, NoColor model, and rollout are pinned in `default-theme-adoption-spec.md`. The
conventions below are normative; the gallery is the visual oracle.

- **Fill, not line, bounds a control.** Buttons, list/menu/tab items, and pickable rows are *fill-bounded*: a
  button is a single row, content at row 0, drawn entirely by its background/foreground fill with **no
  surrounding box**. Panels, popups, lists, and grids are bounded by a solid fill (`Theme.PanelBrush`), not a
  stroked frame.
- **Focus has two looks, split by control family.** *Pick* controls (Button, CheckBox, RadioButton, list/menu/
  tab items, links, slider, calendar day, tree node) show focus as **reverse-video** (`Theme.TextBrush` fill +
  `Theme.WindowBackground` text). *Text* controls (TextBox, editable ComboBox, Spinner, cell-edit) show focus as an
  **intensified well fill** (`Theme.WellBrush`) plus a caret. Both are render-only paint flips (§12.4) and both
  survive `NoColor` (degrade to `Inverse` for pick, `Underline`+caret for text).
- **Pressed/default = accent reverse-video** (`Theme.AccentBrush` fill + `Theme.OnAccentBrush` text). **Hover =
  a fill swap** (`Theme.HoverBrush`). **Selected = `Theme.SelectionBrush` fill.** **Disabled =
  `Theme.DisabledBackgroundBrush` fill + `Theme.DisabledForegroundBrush`/Faint.**
- **The only line accents are genuine whole-cell decorations:** whole-cell underlines (links, access-key
  mnemonics, the active-tab accent row), the bracket cells that *are* a check/radio box (`[ ]`/`[x]`/`[-]`,
  `( )`/`(*)`), and the gutter `▸` focus/expansion marker. No sub-cell strokes, rings, or halos.
- **Line chrome is opt-in, not a control default.** `DrawTitledBox`/`DrawBox`, `Theme.BorderPen`, and
  `Theme.FocusPen` survive only for surfaces that genuinely want a drawn frame — `Border`, GroupBox, Expander,
  Window chrome — and for apps that re-introduce a bordered look. No common control reads them by default.

**Role tokens (the palette spine).** Every part draws from a small set of semantic fill/foreground tokens,
mirrored 1:1 onto `ThemeKeys`; dark/light hex and swatches are pinned by the gallery's "Role tokens" table
(tier values + the hand-picked Ansi16 floor + the NoColor attribute model in `default-theme-adoption-spec.md`):

| `ThemeKeys` | role |
|---|---|
| `Theme.WindowBackground` | page background; the reverse-video text color |
| `Theme.SurfaceBrush` | control fill (button, field, header) |
| `Theme.PanelBrush` | popup / list / grid / menu surface |
| `Theme.WellBrush` | focused text-field fill |
| `Theme.SelectionBrush` | selection fill |
| `Theme.HoverBrush` | shared pointer-over fill |
| `Theme.TextBrush` | primary text / reverse-video fill |
| `Theme.TextDimBrush` | secondary text |
| `Theme.MutedBrush` | tertiary text, glyphs, disabled text |
| `Theme.FaintBrush` | inactive track / faint glyph |
| `Theme.DisabledBackgroundBrush` / `Theme.DisabledForegroundBrush` | disabled fill / text |
| `Theme.AccentBrush` | focus accent, links, pressed/default fill, today |
| `Theme.Accent2Brush` | hover-link, folder glyph |
| `Theme.OnAccentBrush` | text on accent/colored fill |
| `Theme.GreenBrush` / `Theme.AmberBrush` / `Theme.RedBrush` / `Theme.PurpleBrush` | success·on / warning·paused·indeterminate / error·danger / pressed-slider·visited-link·file-glyph |

These are the inheritance spine: overriding one token at a nearer scope re-skins every control with zero
template work (§11.8 bullet 2; the R2 wiring is landed). `Theme.BorderPen`/`Theme.FocusPen` are **not** in the
spine — they are opt-in chrome. Per-control keys (`Theme.<Control><Slot><State>`, e.g.
`ButtonForegroundFocus`/`ButtonBackgroundFocus` = the reverse-video pair, `InputBackgroundFocus` = `WellBrush`,
`InputCaretBrush` = `AccentBrush`) follow the gallery's key table (base/shared first, then per-control in
selector-precedence order, states Normal → Hover → Focus → Pressed/Active → Disabled). S8 authors them into
`CursorialTheme.BuiltIn`; the gallery's "copy as XAML" output is the authoring template.

### §11.9 XAML-facing media values

`Cursorial.Drawing`'s brush/pen types were originally judged XAML-hostile (no parameterless ctors, get-only members, `Pen` a readonly record struct), so this section proposed a `Cursorial.UI.Media` set of mutable, parameterless-ctor **builder twins** (`SolidColorBrush`/`LinearGradientBrush`/`Pen` + an `IResourceValueBuilder { object Build(); }` loader seam substituting `Build()`'s result at end-of-object).

**Retired (2026-06; was never wired).** The Drawing types themselves were made directly XAML-element-authorable instead — the gradient brushes gained parameterless ctors + `init` members + a `[ContentProperty]` on `Stops` (#5), and `Pen` / `GradientStop` (record structs) activate via `Activator.CreateInstance` with `init`-member reflection in the loader (#20). The loader never called `Build()`, so the `Cursorial.UI.Media` twins were dead weight and were removed (#8) along with their isolated unit tests (former control-matrix C104–C107). The `IResourceValueBuilder` end-of-object substitution rule (former C-6 / §4 stage-2 rule a) is likewise retired. Two authoring paths remain, Drawing untouched (invariant 7): **attribute text** via Fork C's registered converters (`IBrush` reuses `BrushMarkup`'s grammar, `Pen` parses preset+composition text), and **element syntax** via the real `Cursorial.Drawing.Media` types directly.

**Brush names are one namespace:** `ResourceBrushResolver.Create(scope)` produces a `TextMarkupOptions.BrushResolver` over the element's chain, so `[brush=Theme.AccentBrush]` text markup and `{DynamicResource Theme.AccentBrush}` resolve identically; markup resolution is static-per-parse with freshness riding the §11.6 cache-key contract.

### §11.10 Diagnostics

`ResourceDiagnostics.Trace/Explain(element, key)` — hop-by-hop lookup record incl. variant probe keys tried, merged recursion, hit/miss, deferred-then-realized (acceptance test: one line per hop); `Subscriptions(Window)` for leak hunting (debug builds assert zero live nodes at window teardown); `DeferredEntries(dict)` incl. `RealizedAtVariant` and Fork C line info. `StyleDiagnostics.Explain` surfaces the originating `ResourceReference.Key` for resource-fed setter values.

### §11.11 Cross-subsystem contracts (condensed)

- **Fork A:** producer entries at LocalValue with in-place set/unset; entries represent unset (`HasValue = false`) when fed `UIProperty.UnsetValue`; `IValueEvictionListener`; untyped `SetValue` + box-interning; all invalidation via `PropertyEffects`.
- **Fork B:** frame entries holding resolved values with `OnEntryChanged`; `Resume`-before-activate / `Pause`-on-deactivate / `Dispose`-on-disarm; control themes armed at ControlTheme(0), re-armed on identity change; `UIApplication.Theme.Styles` consumed at Theme(2); color-tier capability classes from `ActualThemeVariant.Tier`; template seal includes template `Resources`. The hybrid proposal's `IStyleValueSink.OnResourcesChanged` sweep entry-point is **superseded** by S7's per-node registry — styling builds no parallel sweep.
- **Fork C:** `SetDeferred` + lexical capture via `ResourceScopes.ForDictionary`; ambient `IResourceScope` stack; `{DynamicResource}` via `IDeferredValue.AttachTo` → `SetResourceReference`/`ResourceReference`; **converts `x:Key` by the target collection's key type** (`ThemeVariantKey.Parse` for theme dictionaries); registers `LoadCallback` at module init. (The `IResourceValueBuilder` hook is retired — §11.9/#8.)
- **S1:** `LogicalParent`/`TemplatedParent`/`TemplateInstance`, the template logical-chain guarantee (§11.4), attach/detach hooks, visual-root accessor, window enumeration.
- **S6:** calls `OnCapabilitiesChanged` at startup + after every `RenegotiateAsync`, marshaled to the UI thread.
- **S8:** `ControlTemplate.Resources` + `TemplateInstance.Template`; the `GetResourceVersion`/`ActualThemeVariant` cache-key contract; theme content authored under `Theme.*`.

Phasing: **R0** dictionary + chain walk + oracle-pinned lookup/variant-probe test matrix (authored before the engine); **R1** variants + registry (snapshot/tombstone regression, Resume-before-activate, cross-root attach) + `GetResourceVersion`; **R2** BuiltIn + `ThemeKeys` + `SubscribeControlTheme` + builders + `ResourceBrushResolver`; **R3** diagnostics + in-demo resource inspector. (Labels `R*` — renamed from the spec's `T*` to avoid colliding with S1's sub-phases.)

### §11.12 Deferred (carry forward)

- **ComponentResourceKey-equivalent** — no cross-library collision pressure at terminal scale; additive.
- **Per-window/per-scope `ThemeVariantScope`** — app-level override covers v1; `nearest ?? app` is additive.
- **Resource lookup memo cache** — chain walk is not per-frame; add behind the same API only if profiling demands.
- **Owner-window resource chaining** — WPF-parity "no" for predictability; additive.
- **`x:Shared="False"`** — Fork C punts it; dictionary side trivially additive once the loader supports it.
- **DataTemplate-own `Resources` in the chain walk** — excluded in v1; additive when a scenario demands it.
- **`ImageBrush`/`TileBrush` media builders** — blocked on the URI/resource-loader story.
- **Dynamic `[brush=name]` re-resolution** — static-per-parse; freshness already contractual via the cache key; live subscriptions would cross the brush-blind boundary for no observed need.
- **Theme-driven terminal palette sync** (OSC 10/11 writes) — interacts with capability rewriting and renegotiation; needs its own design pass.
- **Resource serialization/round-trip** — no scenario; XAML is the source of truth, inspection APIs cover tooling.

---

## §12 S8 — Control infrastructure and the v1 catalog

Namespace `Cursorial.UI` (framework source uses `using CellStyle = Cursorial.Output.Style;`). S8 owns: `Control` (the templated base — there is no separate `TemplatedControl`), the template/content/items pipelines, the access-key **production** pipeline, the v1 catalog, and the default theme's **content** — per-control templates/styles and control keys authored into S7's `CursorialTheme.BuiltIn` under `Theme.*` names (`Theme.AccentBrush`, `Theme.SelectionBrush`, `Theme.WindowBackground`, `Theme.MenuBackground`, glyph resources). S8 consumes, never owns: windowing/popups/chrome behavior (S4), focus/routing/the access-key **manager** (S3), layout/render boundaries/caret/effective-IsEnabled (S1), `UITimer` (S5), clipboard (S6), binding (S2), resource infrastructure (S7), the three engines. Controls never touch Scene/CellBuffer (invariant 2). Elements draw **element-local** through S1's `Render(RenderContext)` — all translation happens behind the context (Drawing's push stack), including formatted text, strokes, and shadows.

### §12.1 Control base + template machinery

```csharp
public static class TextElement      // inherited attached text properties; AddOwner'd by Control/TextBlock
{
    public static readonly AttachedProperty<IBrush?> ForegroundProperty;            // Inherits | AffectsRender
    public static readonly AttachedProperty<TextAttributes> TextAttributesProperty; // Inherits | AffectsRender
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class TemplatePartAttribute(string name, Type type) : Attribute
{ public bool IsRequired { get; init; } }     // names are "PART_*" by convention; default optional — degrade gracefully

public class Control : UIElement
{
    public static readonly StyledProperty<ControlTemplate?> TemplateProperty;   // AffectsMeasure
    public static readonly StyledProperty<IBrush?> BackgroundProperty;          // AffectsRender; NOT inherited
    public static readonly StyledProperty<IBrush?> ForegroundProperty;          // TextElement AddOwner (inherits)
    public static readonly StyledProperty<Pen?>    BorderPenProperty;           // AffectsRender + nullity escalation (§12.4)
    public static readonly StyledProperty<Margins> PaddingProperty;             // AffectsMeasure
    protected virtual object ControlThemeKey => GetType();  // S7 control-theme lookup: exact key, no base probing
    public bool ApplyTemplate();                             // expands NOW; S1 calls it at the head of Measure
    protected virtual void OnApplyTemplate() { }
    protected virtual void OnTemplateDetaching(TemplateInstance old) { }  // "unhook before rewire" (§12.2)
    protected T? GetTemplatePart<T>(string name) where T : UIElement;     // template namescope only
    protected internal TemplateInstance? TemplateInstance { get; }
}

public sealed class ControlTemplate
{
    public Type? TargetType { get; set; }
    public ITemplateContent? Content { get; set; }   // typed ITemplateContent ⇒ XAML defers automatically (Fork C)
    public Styles Styles { get; }                    // armed at the Template layer (Fork B)
    public ResourceDictionary Resources { get; }     // sealed in Seal(); target of S7's template-resource hop
    public void Seal();
    public TemplateInstance Instantiate(Control owner);   // the ONLY entry point
}
// TemplateInstance { UIElement Root; INameScope NameScope; ControlTemplate Template; void Detach(); }
// Detach() = store-owned cookie/frame retraction (never set-back) + TemplateBinding teardown
//          + presenter auto-alias observer teardown + TemplateNameScopeProperty clear.

public class DataTemplate
{
    public Type? DataType { get; set; }              // implicit-template key
    public ITemplateContent? Content { get; set; }
    public UIElement Build(object? data);            // fresh namescope attached via NameScope.SetNameScope(root, scope);
}                                                    // DataContext = data on root; TemplatedParent stays null
```

Theme styles resolve via `ControlThemeKey` (exact-key semantics, S7); the theme's template setter holds a `ControlTemplate` whose `Content` is the `ITemplateContent`. `NameScopeExtensions.RequireControl<T>(root, name)` is the runtime counterpart of X4's generated `x:Name` fields (throws naming scope + name). `FindName` itself is S2-owned (over `NameScope.FindEnclosing`).

### §12.2 Template application lifecycle (measure-time expansion)

Templates expand **lazily at measure time**: S1 calls `ApplyTemplate()` before `MeasureOverride`. Sequence when dirty:

1. **Detach old:** `OnTemplateDetaching(old)` — control unhooks part handlers/timers/`CanExecuteChanged` (*unhook before rewire*, normative; ScrollViewer is the reference implementation) → `old.Detach()` → remove `old.Root` as visual child (subtree detach retracts DynamicResource subscriptions, `When` watchers, armed style frames — S7/Fork B detach contracts).
2. Resolve `Template` (theme control-style supplies it at the Style slot; `null` ⇒ no child + one-time diagnostic).
3. `Instantiate(this)`: build → stamp `TemplatedParent = this` on every null-stamped element (**foreign non-null `TemplatedParent` throws** — shared-subtree misuse; nested controls' parts exempt automatically) → arm `Styles` at the Template layer → set `NameScope.TemplateNameScopeProperty` (S2) on the control from `TemplateInstance.NameScope`.
4. Validate `[TemplatePart]` **immediately after `Instantiate`, before visual attach** (seal-time validation is impossible — `ITemplateContent` is opaque until `Build`): declared part of wrong type ⇒ throw always; `IsRequired` missing ⇒ throw always; optional missing ⇒ null-check and degrade.
5. Attach `Root` as **visual child only** — parts are never logical children; they see the templated parent's DataContext via inheritance through the visual link.
6. `OnApplyTemplate()` — re-entrant `Template` sets defer to the next measure behind a guard.
7. `MeasureOverride` — parts measurable the same pass (frame coherence, invariant 1).

**Template barrier:** `ControlTemplate`-built elements carry `TemplatedParent != null` ⇒ stylable only via `/template/`. **`DataTemplate`-built elements get `TemplatedParent = null`** — a deliberate WPF deviation: data-template content is app content and must be app-styleable (the hybrid styling model has no `DataTemplate.Triggers`). Part names live only in `TemplateInstance.NameScope`; document and template namescopes never see each other.

### §12.3 Content pipeline

```csharp
public class ContentControl : Control
{   public static readonly StyledProperty<object?> ContentProperty;                 // AffectsMeasure
    public static readonly StyledProperty<DataTemplate?> ContentTemplateProperty;   // AffectsMeasure
    protected virtual AccessText? GetAccessText(); }                                // §12.5 producer ③
public class HeaderedContentControl : ContentControl { /* Header, HeaderTemplate */ }
public class HeaderedItemsControl  : ItemsControl    { /* Header, HeaderTemplate */ }   // MenuItem's base
public sealed class ContentPresenter : UIElement
{   public static readonly StyledProperty<object?> ContentProperty;
    public static readonly StyledProperty<DataTemplate?> ContentTemplateProperty;
    public static readonly StyledProperty<bool> RecognizesAccessKeyProperty;        // default false
    public UIElement? Child { get; } }                                              // realized visual (diagnostic)
```

**Auto-aliasing (normative).** Inside a template, when the presenter's `Content`/`ContentTemplate` have no frame or local entry (Fork A `IsSet == false`), it behaves as if `TemplateBinding`'d to `TemplatedParent.Content`/`.ContentTemplate` — a **read-through fallback, never an installed binding** (a binding would create a frame, flip `IsSet`, and destroy its own condition). While active, a typed property-changed observer on the templated parent (Fork A `IPropertyObserver`; no presenter store entry) re-realizes on notification, re-checking `IsSet` so a later explicit value wins; lifetime = template instance, torn down in `Detach()`.

**DataTemplate lookup chain** (pinned jointly with S7): ① explicit `ContentTemplate` → ② implicit walk — presenter → **templated-parent hop** (when `LogicalParent == null && TemplatedParent != null`, hop to the templated parent and continue up *its* logical chain) → logical ancestors → `Window` → `UIApplication` → built-in theme, probing `DataTemplateKey(t)` for the runtime type then each base class (interfaces deferred) → ③ `UIElement` passthrough (logical child of the templated parent, visual child of the presenter) → ④ `AccessText` content ⇒ `AccessTextPresenter` (extended to plain strings when `RecognizesAccessKey`) → ⑤ fallback `TextBlock(Convert.ToString(content))`. Content change with the same template identity reuses the subtree (DataContext update only).

### §12.4 Render integration, invalidation, scrolling

S1's render-boundary **zone** engine is the substrate: one `Scene` per zone, few zones per default window. S8 mints no layer API — a control needing a dedicated zone uses **S1 boundary promotion** (`IsRenderBoundary`; sticky once minted). Effects routing: `AffectsMeasure` → layout; `AffectsRender` → owning-zone scene re-raster (`Scene.Invalidate()` is whole-scene; bounded because pseudo-class flips occur per hit-chain change, never per cell crossed, text layouts are cached, and the FrameRenderer diff keeps wire cost at changed-cells-only); `AffectsComposite` → `CompositeParameters` refresh on the cached raster, never re-raster (invariant 3). Per-control rule: geometry-bearing properties are `AffectsMeasure`; paint-only are `AffectsRender`; offset/opacity-shaped state is `AffectsComposite`.

**Nullity-escalation pattern.** `PropertyEffects` is frozen metadata and cannot express "measure on nullity flip, render on restyle". The default theme's focus look needs none of this machinery: focus is a **pure paint-only flip** — pickable controls reverse foreground/background, text controls swap to the well fill — so the `:focus` rule changes only `Background`/`Foreground` brushes (`AffectsRender`), re-rasters only the owning zone (invariant 3), and adds zero geometry. It is the *common* hot path and it is render-only by construction, not by escalation. (Migration note: today's built-in `:focus` rule still escalates `BorderPen → FocusPen` (a Heavy pen) on Button/ToggleButton; the cell-faithful adoption replaces those child rules with the reverse-video setters — `default-theme-adoption-spec.md` §7.) Nullity-escalation is reserved for the surviving **conditional-geometry** properties — `BorderPen`, `Border.Title`, which `Border` keeps as opt-in line chrome (§11.8a) — which register `AffectsRender` (a restyle that merely recolors the stroke stays render-only) while the owner's change handler imperatively calls `InvalidateMeasure()` iff the geometry facet flipped (pen nullity ±1 cell/edge; title presence forcing the top border row). Focus never touches this path; only border/title presence does.

**Scrolling.** `ScrollContentPresenter` is **S1-owned**; S8 contributed its scene policy: a banded boundary scene covering content rows `[anchor − K, anchor + viewport + K)`, `K = max(viewportRows, 8)`. In-band scroll = pure re-composite (offset + viewport clip clips *everything* incl. formatted text/fragments/strokes — drawing-core "robust route (a)"); past-slack = one band re-raster (re-anchor); allocation changes only on viewport resize; an `AffectsRender` inside content re-rasters the band, never the extent (a `:selected` flip in a 1,000-item list costs ≤ ~3× viewport rows). SCP's `ScrollOffsetColumn/Row` are **styled** properties (`AffectsComposite`, re-anchor check in the metadata handler — therefore storyboard-animatable; smooth scroll works in v1). `ScrollViewer.HorizontalOffset/VerticalOffset` (`DirectProperty`, two-way bindable) are two-way mirrors of SCP's styled offsets. Extent measured with the scrollable-axis constraint capped at `LayoutLimits.MaxScrollExtent` (32,000; S1-owned constant; clamp + one-time diagnostic). v1 bands the vertical axis only. SU/SD honesty: `TryDetectAndApplyScroll` needs the entire back buffer shifted, no Overlay fragment anywhere, dither off — a templated ScrollViewer practically never qualifies; the savings are the diff's.

### §12.5 Access keys (the production pipeline)

```csharp
public readonly record struct AccessText(string Text, char Key, int KeyIndex)
{
    public bool HasKey => KeyIndex >= 0;
    public static AccessText Parse(string s);     // "_File"→("File",'F',0); "__" = literal '_'. Mnemonic must be a BMP
                                                  // letter/digit, else the underscore stays literal with NO key (never
    public static AccessText Literal(string s);   // throws). Matching is simple-case-folded (char.ToLowerInvariant).
    public static explicit operator AccessText(string s);   // EXPLICIT — parsing is lossy
}
public sealed class AccessTextPresenter : UIElement   // leaf renderer; never templated
{   public static readonly StyledProperty<AccessText> TextProperty;                 // AffectsMeasure
    public static readonly StyledProperty<TextAttributes> KeyAttributesProperty; }  // default Underline; AffectsRender
    // Underlines the KeyIndex grapheme (GraphemeWidth column math) when S3's attached
    // AccessKeyManager.ShowUnderlineProperty (default false) is true on it.
public class Label : ContentControl   // never focusable; ContentProperty metadata: ParsesAccessKeyLiterals
{   public static readonly StyledProperty<UIElement?> TargetProperty; }  // null ⇒ S3 FocusManager.FindNext(this)
```

**Three producers, one model** (all call the single `Parse`): ① type-driven parse-time folding — string literals assigned to `AccessText`-typed properties (loader + X4 generator); ② metadata-flag folding — Fork A per-type flag **`ParsesAccessKeyLiterals`**, set on exactly `ButtonBase.Content`, `MenuItem.Header`, `TabItem.Header`, `Label.Content` and resolved against the instance's **runtime type**; never set on `ContentControl`/`ListBoxItem`/`TextBlock`, so data strings (`snake_case_file.txt`) are safe by construction; ③ runtime `GetAccessText()` parsing under the same flag — `button.Content = "_Save"` works code-first and for bound strings (`MenuItem`/`TabItem` override to read `Header`).

**Registration is control-side:** controls call `GetAccessText()` and `AccessKeyManager.Register(char, UIElement)` / `Unregister` (S3's flat registry; menus ride S3's activation-time `PushScope`/`PopScope`) on attach / content change / detach. **Rendering:** presenters/controls realize an `AccessTextPresenter` for `AccessText` content (`RecognizesAccessKey` extends this to plain strings; the default templates of every flagged control set it true, keeping registration and rendering in lock-step); the cue rides the theme rule `:access-keys AccessTextPresenter { ShowUnderline: true }` — pseudo-class-driven, themeable, no inherited fan-out. **Invocation:** S3 calls `IAccessKeyTarget.OnAccessKey(AccessKeyEventArgs)` (both types S3-owned); per-control defaults — Button: click; ToggleButton: toggle; MenuItem: open submenu/invoke; TabItem: select; Label: focus `Target ?? FindNext`. **`IsMultiMatch` ⇒ focus only, never invoke.**

**Capability gate (normative sourcing):** `(Keyboard.DistinguishesKeyUpDown && Keyboard.ReportsRepeats) || Protocol.Win32InputMode`, evaluated against the **undecorated negotiated snapshot** — never the decorated pipeline (`KeyReleaseSynthesizer` claims both flags unconditionally but never covers modifiers; pipeline sourcing would leave legacy terminals' cues permanently invisible). Equivalent to the input map's conjunct form since Win32 input mode implies key-up/down. Re-evaluated on `RenegotiateAsync`; the same sourcing rule gates TabControl's Ctrl+Tab (§12.7).

### §12.6 Items pipeline + selection

```csharp
public class ItemsControl : Control
{   public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty;        // throws if Items also used (WPF rule)
    public static readonly StyledProperty<DataTemplate?> ItemTemplateProperty;
    public static readonly StyledProperty<ITemplateContent?> ItemsPanelProperty;    // default: vertical StackPanel
    public static readonly StyledProperty<Style?> ItemContainerStyleProperty;       // assigned as container.Style (Explicit layer)
    public ItemCollection Items { get; }  public ItemContainerGenerator ContainerGenerator { get; }
    protected virtual UIElement GetContainerForItemOverride() => new ContentPresenter();
    protected virtual bool IsItemItsOwnContainer(object? item) => item is UIElement;
    protected virtual void PrepareContainerForItemOverride(UIElement c, object? item, int index);
    protected virtual void ClearContainerForItemOverride(UIElement c, object? item); }   // mandatory unhook duty
public sealed class ItemContainerGenerator   // index↔container map; range-based = the virtualization seam
{   public UIElement Realize(int index);  public void Unrealize(int index);
    public UIElement? ContainerFromIndex(int i);  public int IndexFromContainer(UIElement c);
    public event EventHandler<ContainersChangedEventArgs>? ContainersChanged; }
public sealed class SelectionModel   // pure index-based model, no element references; reused by ListBox/TabControl
{   public SelectionMode Mode { get; set; }  public int SelectedIndex { get; set; }   // -1 = none
    public IReadOnlyList<int> SelectedIndexes { get; }  public int AnchorIndex { get; set; }
    public void Select(int i); public void Toggle(int i); public void SelectRangeFromAnchor(int i);
    public void ItemsInserted(int i, int n); public void ItemsRemoved(int i, int n); public void Reset();
    public event EventHandler<SelectionChangedEventArgs>? SelectionChanged; }
```

- **Normalization:** `ItemsSource` wraps in an internal `ItemsSourceView` (indexable snapshot; subscribes `INotifyCollectionChanged`); direct `Items` mode uses the same view. One code path downstream.
- **Generation (v1 eager):** `Realize` = own-container check → `GetContainerForItemOverride` → `Prepare` (DataContext = item, `ItemContainerStyle` as `container.Style`, content preset via the §12.3 chain with `ItemTemplate` explicit). Containers are **logical children of the ItemsControl** and **visual children of the items panel** — `ItemsPresenter` adopts them through S1's visual-only adoption path (`AddVisualChildOnly`), so panel adoption never steals logical parentage.
- **`Unrealize` — normative retraction sequence:** ① `ClearContainerForItemOverride` ② detach from logical + visual tree — subtree detach is the retraction trigger (DynamicResource unsubscription, `When` watcher disposal, style-frame retraction; S7/Fork B contracts) ③ clear local `DataContext` ④ if templated, `TemplateInstance.Detach()`. A future recycle pool re-enters at `Prepare`; this sequence is the seam.
- **Updates:** Add/Remove/Move/Replace map to Realize/Unrealize + index fixups (+ `SelectionModel.ItemsInserted/Removed`); runtime change to `ItemTemplate`/`ItemsPanel`/`ItemContainerStyle` ⇒ **Reset** (v1 policy). `ItemsPresenter` subscribes `ContainersChanged` on attach, unsubscribes on detach (a re-templated-away presenter must not survive on the control-lifetime generator); reparenting is one-directional — old panel releases at its detach, new presenter adopts in index order at first measure.
- **Virtualization seam (designed, not built):** only the panel and the element-free `SelectionModel` consume realization state; a future `VirtualizingStackPanel` drives `Realize`/`Unrealize` per viewport with no API reshaping. Cost honesty: with banded scenes, eager v1 pays raster O(band), but **layout O(n) + container memory** — the trigger arrives at ~10³+ items.
- **Selection:** `ListBoxItem.IsSelected` two-way mirrors `SelectionModel` via `SetCurrentValue` (preserves bindings; `:selected` flips via `PseudoClassMapping`; re-entrancy guarded). **Styling `IsSelected` via style setters is unsupported** (documented stance — `SetCurrentValue` replacement vs frame re-promotion races); selectors react to `:selected`, never set it. Removing selected items moves selection to the nearest surviving index. `SelectedIndex`/`SelectedItem` are `DirectProperty`, two-way bindable.

### §12.7 The v1 catalog

Default-theme vocabulary (**cell-faithful, fill-bounded — no sub-cell borders or focus rings on the common controls**; §11.8a): control identity is whole-cell **fill** + **foreground** role tokens, never stroked outlines; **focus = reverse-video (pickable controls) / intensified well-fill + caret (text controls)** — both work at `NoColor` (degrade to `Inverse`) and are render-only (§12.4); pressed/default = reverse-video in accent (`Theme.AccentBrush` fill + `Theme.OnAccentBrush` text); hover = a fill swap (`Theme.HoverBrush`); selected = `Theme.SelectionBrush`; disabled = `Theme.DisabledBackgroundBrush` fill + Faint/muted. Buttons, list/menu items, and tabs are **fill-bounded — a button is one row, content at row 0, no surrounding box** (the fill *is* the button). The only **line** accents are genuine whole-cell decorations — underlines (links, access-key mnemonics, the active-tab accent row), the bracket cells that *are* a check/radio box (`[ ]`/`( )`), and the gutter `▸` marker. **Line chrome survives as opt-in primitives, not control defaults:** `DrawTitledBox`/`DrawBox` (and `Theme.BorderPen`/`Theme.FocusPen`, weight = glyph family, never a width) remain for `Border`/GroupBox/Expander and Window chrome — titled frames = `DrawTitledBox` (degrades to plain box when narrow); floating surfaces = `FillOpaque` + `DrawBox(overwrite: true)` + drop shadow drawn before the element. Glyph resources live at S7's **color-tier** variant keys (the color tier proxies glyph capability), with `caps-ascii`-class-selected style overrides for genuine mismatches; base defaults are **true ASCII** (`[ ] [x] [-]`, `( ) (*)`, `^v`, `#`), richer tiers opt up to defense-covered Unicode (`(•)`, `☐☑`, `▲▼`, eighth-ramps) per the ambiguous-width memory.

| Control | Pinned surface (beyond base) | Pseudo-classes | Behavior pins |
|---|---|---|---|
| `TextBlock : UIElement` | `Text` (never folded), `Markup` (BBCode incl. `[brush=…]` via S7 lookup; wins over Text), `TextWrapping/Alignment/Trimming`, `TextElement` AddOwners | — | Format cache keyed `(text/markup identity, width, caps, ResourceServices.GetResourceVersion(this), ActualThemeVariant)` — variant flips and renegotiates invalidate via the key; **no dictionary subscription** (sealed dictionaries never pulse). Draws element-local via `RenderContext`. |
| `Label : ContentControl` | `Target` | — | Folds `Content`; `OnAccessKey` → `(Target ?? FindNext).Focus()`; never focusable/tab-stop. |
| `Decorator`/`Border` | `Child`; `Background`, `BorderPen`, `Padding`, `Title`+`TitlePosition`, `Occludes` | — | `Title` **is** the GroupBox story (`DrawTitledBox`); nullity/presence escalation (§12.4); `Occludes` ⇒ `FillOpaque` + overwrite box; adjacent Borders junction-merge inside a shared zone scene (`JunctionMode.Merge`). |
| `ButtonBase : ContentControl` | `ClickMode`, `Command`/`CommandParameter` (BCL `ICommand`), `IsPressed` (read-only `DirectProperty`) | `:pressed` | Routed `ClickEvent` (`RoutedEvent<ClickEventArgs>`, Bubble; `ClickEventArgs : RoutedEventArgs`, S8-owned) + CLR `Click` sugar; `OnClick` = raise, then `Execute` if `CanExecute`. Mouse: down → `CaptureMouse()` (both ClickModes), pressed tracks pointer-over while captured; up over + `Release` ⇒ click. **`Pressed` is an `InteractionState` flag set via S3's `SetInteractionState`** (keeps focus-out/deactivation window-wide clears); `IsPressed` mirrors it for binding/`When`. Cleanup: `OnLostMouseCapture` ⇒ unpressed, no click; Space latch cleared on `OnLostFocus`. **Space clicks on Down** (`IsRepeat`-guarded) — KeyUp exists only on Kitty/Win32; the pressed-latch visual is a capability-gated nicety where Up is reported. Enter = immediate click. `IsEnabledCore` includes `CanExecute`; `CanExecuteChanged` subscribed on attach, unsubscribed on detach **and** Command change. **Focus look = reverse-video** (`Theme.TextBrush` fill + `Theme.WindowBackground` text via `:focus`), a paint-only flip (§12.4); **pressed/default = accent reverse-video** (`Theme.AccentBrush` + `Theme.OnAccentBrush`); the button is **fill-bounded — one row, content at row 0, no surrounding box**; `IsDefault` adds the `▸ … ◂` gutter brackets, not a heavier border (§11.8a). |
| `Button` | `IsDefault`, `IsCancel` | `:default` | Installs/removes `KeyBinding`s (Enter/Esc) on the window root on attach/detach — focused-element-wins falls out of bubble order; no window-key registry exists. |
| `RepeatButton` | `Delay` (400 ms), `Interval` (60 ms) | | `ClickMode.Press` default; repeats via S5 `UITimer` while pressed + over; timer canceled on release/capture loss and unhooked per §12.2. |
| `ToggleButton`/`CheckBox` | `IsChecked` (`bool?`, two-way), `IsThreeState` | `:checked`/`:indeterminate` (multi-class projection mapping) | Cycle unchecked→checked→indeterminate (WPF order); Space/click/access key toggle. Glyphs are theme resources. |
| `RadioButton` | `GroupName` | `:checked` | Group uncheck via `SetCurrentValue` (peers' bindings survive); group = logical parent or same-name within Window; arrows move + check. |
| `TextBox : Control` + `TextPresenter` | `Text` (two-way, **per-change source push** — pinned S2 contract), `IsReadOnly`, `MaxLength`, `Placeholder`, `CaretIndex`/selection API | `:readonly`, `:empty` | See bullet below. **Focus look = intensified well fill** (`Theme.WellBrush` via `:focus`) + the real terminal caret — a **blinking i-beam** (`CursorShape.BlinkingBar`, §12.9), not a drawn adorner; no border, no ring. Part: `PART_TextPresenter` (required). |
| `ScrollViewer : ContentControl`, `ScrollBar : Control` | Visibilities (V `Auto`, H `Disabled`), offset mirrors, `Extent`/`Viewport`, `ScrollBy`, `EnsureVisible` | `:horizontal`/`:vertical` (ScrollBar) | Scene policy §12.4. Wheel: `WheelDeltaY/120 × 3` lines, Shift/`WheelDeltaX` horizontal, unconsumed bubbles. ScrollBar = 1 cell wide; rail Pen + `█` thumb (min 1); track paging, thumb drag (capture, cell-quantized), arrow RepeatButtons (`Orientation` is S1-owned). ScrollViewer wires bars in `OnApplyTemplate`, unhooks in `OnTemplateDetaching` (reference impl). `Auto` re-measure loop broken by remember-last-verdict. **Horizontal `Auto` (v1):** since v1 bands the vertical axis only, horizontal `ScrollBarVisibility.Auto` ("scroll on overflow, hide bar otherwise") cannot be honored and degrades to `Disabled` (a DEBUG `ControlDiagnosticKind.HorizontalAutoUnsupported` is emitted); use `Hidden` to allow horizontal wheel/key scrolling today. `Auto` gains overflow semantics when v2 bands the horizontal axis. |
| `ItemsControl`/`ListBox`/`ListBoxItem` | §12.6; `SelectionMode` (Single/Extended), `SelectedIndex/Item`, `ItemActivated` | `:selected` | List `IsTabStop = false`, items host `TabNavigation.Once` (S3 v1 — promoted with this consumer). Keys: arrows move focus + select per mode (Shift range, Ctrl focus-only), Space/Ctrl+Space, Home/End/Page, Ctrl+A. Enter or double-click (`ClickCount == 2`) ⇒ `ItemActivated`. Navigation cost = one band re-raster + re-composite, item-count-independent. |
| `Menu`/`MenuItem`/`ContextMenu`/`Separator` | `Command`, `InputGestureText` (display-only), `IsCheckable`/`IsChecked`, `IsSubmenuOpen`, `IsHighlighted` | `:open`, `:highlighted` (DirectProperty-backed, via `PseudoClassSet.Set`), `:checked` | See bullet below. |
| `TabControl`/`TabItem` | `SelectedIndex/Item`, `ContentTemplate`; TabItem `IsSelected`; Header folds | `:selected` | Header arrows = focus + select (selection-follows-focus); Ctrl+PageUp/PageDown = universal cycle chord (window-root `KeyBinding`s; xterm-standard encodings); **Ctrl+Tab registers additionally only when wire-distinguishable** (Kitty/modifyOtherKeys ≥ 2/Win32; §12.5 sourcing rule). Headers junction-merge into the content frame — requires the shared zone scene. `TabStripPlacement`: Top only. |
| `ProgressBar : Control` | `Minimum/Maximum/Value`, `IsIndeterminate`, `Fill`, `IndeterminateOffset` (`StyledProperty<int>`, `AffectsComposite`) | `:indeterminate` | See bullet below. |
| `ToolTip : ContentControl`, `ToolTipService` (static) | Attached `Tip`, `InitialDelay` (500 ms), `ShowOnFocus` (`bool?`; null = auto `!MouseCapabilities.Motion`) | — | See bullet below. |
| Window chrome template | (template only — `Window` is S4's, `Window : ContentControl`) | | See bullet below. |

**TextBox.** Caret = **the real terminal cursor**: `TextPresenter` publishes element-local `(column, row, CursorShape.BlinkingBar)` through S1's `ITerminalCaretService` while focused + window-active; S1 transforms at frame assembly (S4 folds surface offsets; the write lands in S6's `RenderFrame`); native blink = zero re-raster per phase, DECSCUSR shapes, terminal-level semantics for assistive tech. `Clear(this)` on detach **and** S1 drops detached owners (stale-cursor double guarantee). Caret pinned to grapheme-cluster boundaries; all column math via `GraphemeWidth`; presenter-owned horizontal scroll with 2-column slack. Editing: cluster/word movement, Shift-extension, cluster/word deletion, Ctrl+A; typed input inserts `KeyEvent.Text` (respects `MaxLength`, rejects controls); paste arrives as **`TextInput` with `FromPaste = true`** — newline flattening keys on the flag. Clipboard via **S6's `IClipboardService`**: Copy/Cut → OSC 52 write when negotiated, silent no-op otherwise; Ctrl+V/Shift+Insert attempt an OSC 52 read only when negotiated (250 ms timeout); Ctrl+C with no selection is not consumed. Undo/redo deferred.

**Menus.** Submenus/ContextMenu open through **S4's `Popup` element** (placement below/right, flip-to-fit; light-dismiss, focus behavior = S4). One **reusable Popup per menu session**: top-level switches are content-swap + `Move` on the open Popup (surface + scene retained ⇒ no layer-count change, no full recomposite, no Sixel re-emission). The bar is a logical focus scope (S3): opening pushes a scope, closing pops and restores physical focus; while open, S3 activates the menu's access-key scope. `Menu` registers as the window main menu **with S3** (`IMainMenu`; Alt tap / F10 enter menu mode via `AccessKeyManager.EnterMenuMode`). Keys: Left/Right cycle (wrap), Down/Enter open, Esc/Alt exit level/mode; hover-open after 250 ms via S5 `UITimer`. Theme: `FillOpaque(Theme.MenuBackground)` + light box (overwrite) + shadow; columns `[check][gap][header][fill][gesture Faint][gap]`; Separator junction-merges into the side borders.

**ProgressBar.** Never throws (`Max == Min` ⇒ 0%; clamp; NaN ⇒ 0%). Determinate: brushes `ColorAt`-sampled per cell against the whole track rect; fill = full `█` cells + one left-eighth-ramp partial as **foreground glyphs** via `Set`; ASCII tier `#`, no partials. Indeterminate: the highlight element is **S1 boundary-promoted; the zone persists once minted** (sticky rule — no demotion valve in v1); the theme's `:indeterminate` style ignites `BeginStoryboard` (`SnapshotAndReplace`) targeting `IndeterminateOffsetProperty` — animated at `BindingPriority.Animation` through the store, `AffectsComposite` routes a `CompositeParameters` offset `PingPong`: **re-composite only, zero re-raster**, no raw layer pokes (invariants 2/3 end-to-end); retraction = `StopStoryboard`.

**ToolTipService.** One process-wide service (no per-element timers; S5 `UITimer`); consumes S3's `InputDispatcher.HoverChanged(removedChain, addedChain)` hook. Under any-event motion: the open timer starts on entering a `Tip`-bearing element and is **not** reset by intra-element cell moves; quick-show when the last tip closed < 100 ms ago. Close on leave, any ButtonDown, any **non-modifier** KeyDown (bare Shift under Kitty must not dismiss), focus loss, owner detach. Display = S4 `Popup` (child hit-test-transparent, never focused), below-right of pointer, flip-to-fit; max width 40 cells. `ShowOnFocus` auto-mode shows on `:focus-visible` when hover can't exist.

**Window chrome template.** `Window : ContentControl` (`UIElement → Control → ContentControl → Window`), so the chrome's `ContentPresenter` auto-alias engages naturally. Behavior rides **S4's `WindowChrome.HitTestRoleProperty`**: the template tags its title-bar strip (drag-move), close button (a real hit-testable `Button`, rendered `[x]`), and bottom-right resize grip; the Window interprets bubbling events per role — no command objects. PART names (`PART_TitleBar/Title/CloseButton/ContentHost`) are **S8-internal** for `GetTemplatePart`, not a cross-subsystem contract. Visual: body Border with `Occludes = true` (`FillOpaque(Theme.WindowBackground)`) + `DrawTitledBox` title-in-edge; `Pens.Double` when `:active-window`, else `Pens.Light` + Faint title; drop shadow cites S4's `WindowShadow.Default` (S4 sizes the surface margin and sequences the shadow painter); modal dimming = S4's `obscured` class. S4 ships its interim chrome painter until this template lands at C4. Window roots get S3's defaults (`IsFocusScope = true`, `TabNavigation = Cycle`) — cited, not restated.

### §12.8 Cross-subsystem contracts (condensed)

- **S1:** calls `ApplyTemplate()` at the head of Measure (load-bearing); `Render(RenderContext)` element-local; zone/boundary promotion + banded SCP + styled `ScrollOffset*` (§12.4); `ITerminalCaretService` (publish/clear; drops detached owners); `IsEnabledProperty` + `IsEnabledCore` virtual + `InvalidateIsEnabledCore()` — effective-enabled computed on S1's inheritance plumbing, pushed as `InteractionState.Disabled`; visual-only adoption (`AddVisualChildOnly`) for `ItemsPresenter`; owns `Orientation` and `ScrollContentPresenter`.
- **S2:** per-change push for `TextBox.Text`; self-/ancestor-source bindings for `When`; ElementName (`Label.Target`); `NameScope`/`FindName`/`TemplateNameScopeProperty`.
- **S3:** routed virtuals with S3-owned arg shapes (`GetPosition(this)`, `ClickCount`, `FromPaste`, `Handled`); element `CaptureMouse()`/`ReleaseMouseCapture()` (no separate capture interface); pipeline guarantee `MouseClickSynthesizer` (`ClickCount` on ButtonDown, no synthesized Click events); focus surface incl. `TabNavigation.Once` and `FocusManager.FindNext`; `KeyBinding`s/`InputBindingCollection` on window roots; `AccessKeyManager` (`Register`/`Unregister`, scopes, `ShowUnderlineProperty`, `EnterMenuMode` ← `IMainMenu`, F10); `SetInteractionState` for `Pressed`; `HoverChanged` router hook.
- **S4:** `Popup` element (placement, light-dismiss, focus options, **content-swap-without-close**); `WindowChrome.HitTestRole`; `WindowShadow.Default`; `obscured`/`:active-window`.
- **S5:** `UITimer` (RepeatButton, ToolTipService, menu hover-open); `BeginStoryboard`/`StopStoryboard` on style edges.
- **S6:** `IClipboardService` (OSC 52 via `QueueControlSequence` write / device-response-sink read).
- **S7:** resource walk with the templated-parent hop; `DataTemplateKey` probing; `ControlThemeKey` exact-key lookup; `ControlTemplate.Resources` hop; resource-version cache-key contract (TextBlock); hosts S8's theme content in `CursorialTheme.BuiltIn`.
- **Fork A:** `ParsesAccessKeyLiterals` metadata flag; `IsSet`; `SetCurrentValue`; `IPropertyObserver`; `DeferNotifications`; confirmation: direct properties skip `PropertyEffects` routing and are not storyboard-animatable — composite-animatable state must be styled (`IndeterminateOffset`, `ScrollOffset*`).
- **Fork B:** `PseudoClassMapping` (incl. multi-class projection); raw `PseudoClassSet.Set` sanctioned **only** for control-semantic classes with no `InteractionState` bit (`:open`, `:highlighted`); Template-layer arming; `TemplateInstance.Detach()` cookie retraction; subtree-detach disposal of `When` watchers.
- **Fork C:** `ITemplateContent`/`TemplateBuildContext`; `TemplateBinding` (template bodies only; one-way by design — bar wiring is code-behind); AccessText folding per §12.5 rules ① /②.
- **Provides:** chrome template + `Border`/`ContentPresenter`/`ButtonBase` for S4 composition; `IMainMenu` → S3; theme content under `Theme.*` → S7; `AccessText`/`AccessTextPresenter`/`Label`, `SelectionModel`, presenters, `TemplatePartAttribute`, `RequireControl<T>` → everyone; the generator range API + retraction sequence → future virtualization. Arg types (`ClickEventArgs`, `ScrollEventArgs`, `SelectionChangedEventArgs`, `ItemActivatedEventArgs`, …) are S8-owned, shaped in their C-phases.

### §12.9 Terminal-specific deviations (recorded)

Caret = terminal cursor, not a drawn adorner; text-input fields publish a **blinking i-beam** (`CursorShape.BlinkingBar`) — a pinned deviation from the gallery's illustrative block caret, chosen for editable-field affordance. Focus visuals = **reverse-video (pickable controls) / intensified well-fill + caret (text controls)** — paint-only Style flips (`AffectsRender`), render-only on the hottest path, surviving `NoColor` via `Inverse`. **No focus rectangles, no focus rings, no border-weight escalation on focus** — the common controls are fill-bounded and read no pen; the bordered look survives only as opt-in `Border`/GroupBox/Expander/Window chrome (`DrawTitledBox`/`DrawBox`, `Theme.BorderPen`/`Theme.FocusPen`, where pen weight is a glyph family, never a width). `Border.Title` replaces GroupBox. Three explicit surface semantics (tint `FillRectangle` vs occluding `FillOpaque` + overwrite box) because the compositor distinguishes them. Viewport clipping at composite, banded (Drawing v1's clip stack misses formatted text/fragments). Junction-merging chrome (tabs, separators) — impossible in pixel frameworks, free here; co-rationale for shared zone scenes. Block-element eighth-ramp fills instead of sub-pixel widths. Capability-honest interaction with honest sourcing (hover/tooltips gated on motion; access-key cues and Ctrl+Tab gated on the undecorated snapshot; clipboard OSC 52 write-mostly). Integer-cell ergonomics: `Margins`/`Padding` in whole cells, 1-cell scrollbars, pen weight = glyph family, ASCII-default glyph resources. No hover/press geometry animation in the default theme — motion is reserved for composite-parameter paths.

### §12.10 Phasing

**C0** template spine (`Control`, templates, parts, `ContentPresenter` + aliasing, `ContentControl` shells, `Decorator`/`Border`, `TextBlock`, full AccessText pipeline, `Button`). **C1** interactive leaves (`ButtonBase` completion, `RepeatButton`, toggles, `TextBox`/`TextPresenter`). **C2** items (`ItemsControl`, generator + retraction sequence, `ItemsPresenter`, `SelectionModel`, `ListBox`). **C3** scrolling (`ScrollViewer`/`ScrollBar` over S1's banded SCP; band-slack profiling). **C4** popup tier (menus, tooltips, chrome template — replaces S4's interim painter). **C5** completion (`TabControl`, `ProgressBar`, theme hardening across variant tiers, adversarial review of template + items lifecycles).

### §12.11 Deferred (carry forward)

- **ComboBox** — composes selection + popup + editable text across three subsystems; ListBox-in-Popup recipe covers v1.
- **Slider** — continuous drag has poor cell-grid affordance; ProgressBar covers display.
- **TreeView** — hierarchical generation/indent/expansion is a sizable design; nested-ItemsControls prototype path recorded.
- **DataGrid** — requires virtualization + column layout; out of v1 scope by design.
- **StatusBar** — a `DockPanel`+`Border`+`Separator` recipe, ships as a gallery sample, not a control.
- **Virtualization** — seam designed (§12.6); banding caps raster cost, so the trigger is layout time + container memory at ~10³+ items.
- **Alternating-row styling** — deferred with a designed mechanism: the container generator stamps an `:alternate` pseudo-class (or `AlternationIndex` attached int) on generated containers — generator-owned, element-local invalidation, does not reopen the `:nth-child` fence; cheap early add after C2.
- **Collection views (sort/filter/current-item)** — jointly owned by S2+S8 when it lands; v1 binds pre-shaped collections.
- **Boundary demotion valve** — promoted zones (ProgressBar) persist once minted; a demotion API is S1's call if layer counts ever hurt.
- **TextBox**: multi-line + undo/redo stack, PasswordBox/mask, `UpdateSourceTrigger` knob, drawn-caret fallback — none load-bearing for v1.
- **ListBox**: drag-selection (capture + edge auto-scroll machinery), type-ahead.
- **Templates/items**: `DataTemplateSelector`/`ItemContainerStyleSelector` (design once, with S7), interface-based implicit templates (needs deterministic ordering).
- **TabControl** `TabStripPlacement` Left/Right (vertical headers are weak on a cell grid); vertical ProgressBar.
- **MenuItem gesture execution** — `InputGestureText` is display-only; a command/gesture map belongs with S3.
- **`Thumb`/`Track` as public primitives** — internal to ScrollBar until a second consumer appears.
- **S1 region invalidation within a zone** — recorded future seam; every S8 behavior is correct without it (profiling-gated).

---

## §13 Resolved decisions

The record of contested calls and why they fell the way they did. Process: each fork ran three advocate proposals through a three-lens judge panel (consumer ergonomics / engineering cost / requirements coherence); the eight subsystem specs then went through adversarial critique + revision and two cross-subsystem coherence passes whose 60-item punch list is folded into the sections above. Full materials in `docs/ui-layer-design/` (§16).

### §13.1 The three foundational forks

- **Property system: Avalonia-style typed chassis** (§2) — typed `StyledProperty<T>` end-to-end over a `ValueStore` with priority frames. Decisive: the mechanism has a shipping oracle (Avalonia 11's converged store); typed/zero-box hot paths match the stack's `readonly record struct` character; restoration-on-deactivation lives *inside* the store, which is what styling and animation need guaranteed (invariant 4). **Rejected:** WPF-faithful boxed-`object` storage (an irreversible public-API decision against the stack's grain — though its `AffectsComposite` flag, oracle-first test matrix, and diagnostics were grafted in); a terminal-optimized one-winner-per-priority store (exports the engine's defining correctness obligation — within-priority restoration — to the styling layer); coercion-as-priority-slot; any `IObservable` surface.
- **Styling: hybrid selector subset + `When` data-conditions** (§3) — unanimous across all three judges. Selector reach for theming and cascade *plus* per-rule DataTrigger power, in one activation predicate with one priority slot, one sort key, and one cookie-based retraction path. **Rejected:** pure WPF triggers (most machinery for permanently less reach — no structural styling); pure Avalonia selectors (data-driven styling structurally second-class; per-element `Classes.Bind` is a per-instance mechanism standing in for a per-rule one); sibling/positional combinators (`:nth-child`) — fenced out because their invalidation graph entangles layout.
- **XAML: custom runtime loader, generator endgame** (§4) — unanimous. One semantic implementation in a library; node-graph slices give parse-time-checked deferred templates; hot reload is "the terminal's designer." The source generator is the *planned* X4/X5 end state on seams that exist from day one (netstandard2.0 parser front-end, compiled-binding descriptor contract). **Rejected:** source-gen-first ("the right end state and the wrong first move" — dual implementations forever, IDE-perf machinery hand-waved); vendoring Portable.Xaml/System.Xaml (40 kLOC of unowned engine vs the repo's zero-dependency value; its conformance-corpus and System.Xaml-as-CI-oracle process ideas were kept).

### §13.2 Cross-subsystem resolutions (the coherence-pass verdicts)

- **One scene-granularity model:** S1's render-boundary **zones** are canonical (§5.7). S4's surfaces wrap an S1 `RenderTree`; S8's per-control layer wishes are expressed as boundary promotion. Promotion is sticky until detach (layer-count stability beats memory in v1); the Phase-0 raster benchmark (§14) validates the cost model.
- **One `ScrollContentPresenter`:** S1 owns the type; the banded-scene policy (viewport ± slack, re-anchor on exit) replaces extent-sized scenes; scroll offsets are styled properties at `AffectsComposite` — which is also what makes smooth scrolling storyboard-able.
- **One hit test:** `RenderTree.HitTest` (provably mirrors composite order, §5.8). S3 routes; it does not re-derive geometry.
- **Compositor ownership:** the window manager owns the `SceneCompositor` + `ScenePool`; S6 owns the screen `CellBuffer` + `FrameRenderer`; renegotiation/resize rebuild both sides transactionally (§10.5).
- **Windowing input protocol:** S4's `FilterMouseEvent` + `IWindowFocusHooks` wins over S3's surface-snapshot sweeps; light dismiss, modal attention, and activation-on-press live in the window manager. Chrome behavior is **role-attached** (`WindowChrome.HitTestRole`), not PART-name-coupled; PART names stay S8-internal.
- **`Window : ContentControl`** — the chrome's `PART_ContentHost` presenter auto-alias engages naturally.
- **Popups:** S4's `Popup` element is the contract; it supports **content-swap-without-close** so menu navigation reuses one surface (no layer-count churn, no Sixel re-emission storms).
- **Terminal focus-out:** keyboard focus is **retained** (refocus restores state); S3 raises `EditCommitRequested` so `UpdateSourceTrigger.LostFocus` edits still flush on Alt-Tab. A terminal focus event is not an element focus change.
- **Access-key pipeline (one design):** S8's `AccessText` struct (explicit conversion — parsing is lossy), three producers (XAML fold / runtime parse under the `ParsesAccessKeyLiterals` metadata flag / explicit construction), control-side registration against S3's flat `AccessKeyManager` registry with activation-time scoping, cue rendering via the `:access-keys` theme rule targeting `AccessTextPresenter.ShowUnderline`. Pinned gate: `(Keyboard.DistinguishesKeyUpDown && Keyboard.ReportsRepeats) || Protocol.Win32InputMode`, evaluated against the **undecorated** negotiated capabilities (a `KeyReleaseSynthesizer` upstream must not spoof it), re-evaluated on renegotiation; Alt-held state cleared unconditionally on terminal focus loss.
- **One application object:** `UIApplication` merges the host/loop surface (S6) and the theme surface (S7); capability classes are stamped from the **effective** color tier, honoring `RequestedColorTier`.
- **Theme split:** S7 owns theme *infrastructure* (the built-in dictionary, variant axes `ThemeBase × ColorDepth`, `Theme.*` key naming, `ControlThemeKey`); S8 *authors the content* (per-control templates/styles) into it. Glyph fallbacks ride color-tier keys plus `caps-ascii`-class style overrides.
- **Effective-`IsEnabled`** is owned by S1's inheritance plumbing (`IsEnabledCore` virtual, ancestor AND, pushed as `InteractionState.Disabled`).
- **Activation semantics that survive legacy terminals:** Space/Enter activate on key **Down** (`IsRepeat`-guarded) — key-up exists only on Kitty/Win32 terminals; pressed visuals are the capability-gated nicety, never the activation gate.
- **`ModalAttention` is a transient pseudo-class pulse** animated by the existing style-edge path; routed-event `EventTrigger`s are deferred (§15).
- **`Margins`, not `Thickness`:** v1 reuses the Rendering type everywhere; since P2.6 (matrix LD19) margins are **signed** with WPF semantics (animation tracks interpolate signed — sizes still clamp ≥ 0).
- **`Window.Title` → OSC 2** is wired in v1 through S6's control-sequence channel.
- **Frame-coherent synchronous dispatch** (invariant 1) is itself a resolved decision — no WPF-style dispatcher priority tiers; the terminal's frame loop makes them unnecessary, and their absence is a real simplification every spec leans on.

---

## §14 Phase plan & status

### §14.1 Workstream map

Eleven workstreams feed this plan: the eight subsystems (S1 tree/layout, S2 binding, S3 input/focus, S4 windowing, S5 animation, S6 app model, S7 resources, S8 controls) plus the three engines — **Fork A (property system) and Fork B (styling) are their own workstream rows** (punch 32: no subsystem owns them; their consolidated amendment ledgers (§2.x/§3.x) land incrementally with the consuming phase), and **Fork C (XAML)** owns the `Cursorial.UI.Xaml` / `Cursorial.UI.Xaml.Generator` assemblies. Sub-phase labels used below come from the subsystem specs: S1 `T0–T4`, S2 `B0–B3`, S4 `W0–W5`, S5 `A0–A3`, S7 `R0–R3` (**renamed** from that spec's `T0–T3` — collides with S1's `T` labels; punch 57 normalization), S8 `C0–C5`, Fork C `X0–X5`. S3 and S6 specify ordered spines without letters; they are sliced across P1–P9 as noted. The five de-risking probes are from the completeness report §4 (numbered 1–5 below).

### §14.2 Phases

| Phase | Scope | Exit criteria | Status |
|---|---|---|---|
| **P0** | Scaffolding: `Cursorial.UI` + `Cursorial.UI.Tests` in the solution. **Fork A engine core**: `UIProperty`/`StyledProperty<T>`/`AttachedProperty<T>`/`DirectProperty<TOwner,T>`, `UIObject`, `ValueStore` + `ValueFrame` priority frames, `BindingPriority` ladder, `PropertyEffects` (both lanes + inherited carrier), `SetCurrentValue`, untyped lane + `GetValueSource`, box-interning, `IValueEvictionListener`, `ValueFrame` conformance kit. **Oracle-pinned precedence matrix authored before any engine code** (probe 2), including the three cross-cutting rows other phases gate on: `SetCurrentValue` two-way write-through (S4 W3 gate), winning-base observer under animation incl. inherited routing (S5 A3 gate), frame-hosted eviction order (S2/Fork B); plus a throwaway store spike benchmarked at 300 elements × hover-flip churn. **Probe 1** (scene-raster benchmark, Drawing-only demo command): 200×60 scene, ~300 draw ops, 60 fps + `FrameRenderer` diff + banded re-anchor — its numbers size how much of S1 T3's zone machinery P1 builds. **Probe 3** (`accesskeys` demo command): Alt Down/Up + FocusEvent + gate inputs logged across kitty/WezTerm/Windows Terminal/xterm/tmux/Alacritty — validates the punch-21 gate truth table on live wires. | Precedence matrix green against the store; spike allocation numbers recorded in this doc; probe 1/3 findings folded into S1 T3 scoping and S3's gate table. | Pending |
| **P1** | **S1 T0–T4**: tree plumbing + lifecycle walks (incl. logical attach/detach events + the permanent-detach two-sweep hook, punch 39), inheritance wiring, `TemplatedParent`, effects routing; `LayoutMath` + Measure/Arrange + `LayoutManager` (internal fixpoint — sole convergence owner, punch 13) + StackPanel/DockPanel/Canvas with the WPF-derived oracle layout matrix; Grid + WrapPanel; render zones + `RenderTree` + `RenderContext` + composite walk + z-order + `RenderTree.HitTest` + scene pooling (scoped by probe 1); `ScrollContentPresenter` with S8's banded policy + styled `ScrollOffset*` (resolution 2), opacity groups, sticky promotion, `CompositeClip` (resolution 51); `ITerminalCaretService` registry + transform legs (punch 29). **S6 spine (minimal-complete)**: `UIApplication` + builder, `ITerminalHost`/`TerminalSessionHost`/`SyntheticTerminalHost`, `UIDispatcher` + sync context, the 7-phase frame loop + normative wake protocol, resize pipeline, device-response router, `QueueControlSequence`, teardown skeleton, **`UITestHost` (headless)**. | UITestHost renders a static panel tree headlessly with cell/byte assertions green; layout oracle matrix green; a scrolled band re-anchors without full re-raster. | Pending |
| **P2** | **S3 spine**: `RoutedEvent` registry + route walker + pooled args → event vocabulary + `ProcessEvent` (3-state result, punch 15) → hit testing **delegated to `RenderTree.HitTest`** (punch 5) + two-phase hover diff + capture → `FocusManager` + scopes + tab navigation (incl. `Once`, punch 24) → `InteractionState` plumbing + pressed-holder set (punch 54) → commands + `KeyGesture`/`KeyBinding` → directional navigation. `AccessKeyManager` core (flat registry, modes, gate per punch 21) — end-to-end UX at P9. **S6 amendments**: `UpdateHover()` per rendered frame (punch 10), click-synthesis defaults flipped to S3's contract (punch 14), capability fan-out call #2 (resolution 11). **Early S5 slice**: `FrameClock` + `UITimer` pulled forward (inversion 1, §14.3). **Probe 4 as a CI gate**: UITestHost motion-storm benchmark (200-column sweep over ~300 elements), zero steady-state allocation, ≤ 33 ms/frame; re-asserted at P3 and P5 as hover styles and templated controls join the hot path. | Motion-storm gate green and wired into CI; focus-restore and modal-occlusion-hover oracle tests green; gesture matching green across legacy-C0 and Kitty encodings. | Pending |
| **P2.5** | **Post-P2 batch** (invariant-7 amendment of 2026-06-11 — only Core has shipped; lower layers cleared for first-class improvements): ① Drawing push clip/translate stack extended to cover `DrawFormattedText`/`DrawContent`/deferred pen strokes/chart braille/shadows/titled boxes, then `RenderContext` self-translation deleted in favor of the stack (drawing design doc updated); ② public read-only `Scene.RasterVersion` (drop the test `InternalsVisibleTo` into Drawing); ③ `ScenePool` size-bucketing; ④ element-level **mouse cursor** support per §7.6 (`UIElement.Cursor : MouseCursorShape?`, hover-chain/capture resolution, equality-gated OSC 22 emission via `QueueControlSequence`, teardown/renegotiate reset, `uipanels` demo line + UITestHost byte assertions); ⑤ the **`Cursorial.UI.Controls` namespace move** (§1.3 scheme): `Panel` + panels + `ScrollContentPresenter` relocate from the root namespace (mechanical; usings updated repo-wide; `UIElementCollection` stays in `Cursorial.UI`). | Push-stack coverage proven by Drawing tests + RenderContext simplification lands with zero UI test regressions; cursor bytes asserted under TestCapabilities presets; full suite green after the namespace move. | **①–④ done** (push-stack coverage + 29 Drawing tests; `RenderContext` reduced to one pushed translate scope per element render, zero UI regressions; `RasterVersion` public + `InternalsVisibleTo` dropped; `ScenePool` bucketed — exact-dimension buckets, LRU cap — with churn/allocation tests; `UIElement.Cursor` + S3 hover/capture resolution behind the `MouseCursorShape` gate, S6 OSC 22 emission with teardown/renegotiate resets, `KittyTruecolor` preset gains `MouseCursorShape=true` + the `NoMouseCursorShape` preset, `SyntheticTerminalHost.ScriptRenegotiatedCapabilities` for gate-flip tests, 17 byte-asserted tests, `uipanels` cursors); **⑤ done** (`Panel`/`StackPanel`/`DockPanel`/`Canvas`/`Grid`+`GridLength`/definitions/`WrapPanel`/`ScrollContentPresenter` **plus the `Orientation` and `Dock` enums** (WPF kinship — `System.Windows.Controls` types; `Visibility` + alignments stay in the root) → `Cursorial.UI.Controls` under `Cursorial.UI/Controls/`, usings updated repo-wide, `UIElementCollection` stays in `Cursorial.UI` with the deviation noted in its doc comment; zero behavior change. Fork C note: the XAML loader's default xmlns map must include `Cursorial.UI.Controls`) — **P2.5 complete** |
| **P3** | **Fork B styling engine**: selector grammar + lists, packed `StyleSortKey`, style frames via the Fork A conformance kit, cookie batch retraction, pseudo-classes + `PseudoInterestMask` + `PseudoClassMapping`, **template barrier**, seal-on-attach + seal-time errors, capability classes (stamped from *negotiated* caps until P5 re-points to the effective tier — inversion 6), `IStyleFrameHooks` (S6), `StyleDiagnostics.Explain`. `IStyleEdgeAction` declared as a seam only (inversion 3). **`When` is deliberately absent** — it needs B0's `BindingOperations.Watch` (inversion 2). | `Explain` acceptance test (full sort-key derivation, one line per winning value); pseudo-class flip restyles without re-raster of unaffected zones; motion-storm gate still green with hover restyles. Recorded informationally (style matrix S177/S178, Debug build, dev machine, 2026-06): cold attach of 300 elements under a 20-rule `S_app` ≈ 17 ms one-time; the probe-4 storm with a real 2-setter `:pointerover` rule armed on all 300 leaves drains 200 moves + restyles + render in ≈ 6.3 ms/frame (budget 33 ms), 0 B steady-state per Move. Release re-assert at the loaded probe-4 weight recorded in the dated "P3 motion-storm re-assert" blockquote below (2026-06-12: 2.9 µs/Move incl. restyles, 0 B worst-rep, 3.66 ms/frame). | Pending |
| **P4** | **S2 B0** (full spine: paths, INPC/INCC, DataContext inheritance + as-target anchor, five modes, `FindAncestor`, frame-hosted `BindingEntry` + eviction + teardown-sweep integration, **`BindingOperations.Watch`**, diagnostics ring; B0 oracle matrix authored first) + **B1** (`ElementName`/`NameScope.FindEnclosing`, `UpdateSourceTrigger.LostFocus` via S3's terminal-focus-out edit-commit notification — punch 16, `BindingDiagnostics.Explain`). **`When`/`DataCondition` wiring into styling** (the Fork B numbered requirement, closed here). | B0 oracle matrix green; `When`-driven style flips under UITestHost; binding leak tracker clean across detach/teardown sweeps. | Pending |
| **P5** | **S7 R0–R3**: dictionary + chain + variant-probe oracle matrix; variants + subscriptions + **`UIApplication` merge** (punch 44 — S7's theme surface joins S6's type; capability-class stamping re-pointed to `ActualThemeVariant.Tier`); `CursorialTheme.BuiltIn` + `ThemeKeys` + builders (S8 authors content into S7's structure under `Theme.*` names, punch 45); resource diagnostics. **S8 C0** (Control, `ControlTemplate`/`TemplateInstance` + `ITemplateContent` runtime, part validation, ContentPresenter + aliasing, ContentControl/Headered shells, Decorator/Border, TextBlock, AccessText data model + `Label`, Button) + **C1 minus TextBox** (ButtonBase completion, RepeatButton on the P2 `UITimer` slice, ToggleButton, CheckBox, RadioButton) + **C3 split**: ScrollViewer + ScrollBar now (SCP landed P1; ListBox integration at P9 — inversion 5). **S2 B2** (TemplateBinding fast path, `Detach`-eviction conformance). | Themed Button/CheckBox/ScrollViewer demo across `ThemeVariant` tiers; template subscription-leak tracker clean; motion-storm gate green over templated controls. | Pending |
| **P6** | **Fork C X0–X3**: netstandard2.0 parser frontend (shared with the future generator), node model + diagnostics (line/column everywhere) + fuzzing + golden tests; instantiator + converter registry + `x:Name` + `LoadComponent`; markup extensions end-to-end (`IDeferredValue.AttachTo` seam) + resource dictionaries + deferred entries; deferred content + template namescopes + `TemplateBinding` + lexical scope capture. Access-key literal folding at parse time (second producer of `AccessText`). | Conformance corpus green; Windows-only CI leg pinning escape/whitespace cases against System.Xaml as oracle; demo window loads from XAML with templates + resources. | Done (X0–X3 + the P6 integration pass; the System.Xaml oracle leg is reflection-only and Windows-gated — skips elsewhere with a documented reason; live `uixaml` demo) |
| **P7** | **S4 W0–W5**: `TopLevelSurface` wrapping an S1 `RenderTree` (resolution 1); compositor + `ScenePool` ownership on the WM with S6's renegotiate/resize transactions amended (punch 4); `Window : ContentControl` (resolution 42); modal stack + `ShowDialogAsync`; `Popup` incl. **content-swap-without-close** (resolution 37); chrome via `HitTestRole` + S4's interim default template (punch 36); hardening + `WindowDiagnostics.DumpZOrder`. S3 consumes `FilterMouseEvent` (punch 33), adds `ScreenPosition` (34), cross-tree popup route continuation (35). `ModalAttention` as transient pseudo-class pulse via `UITimer` (resolution 38). `Window.Title` → OSC 2 via `QueueControlSequence` (RESOLUTIONS stance). S6 teardown gains `CloseAllAsync` (punch 9, first half). **Probe 5**: the scenario matrix written as UITestHost scripts **before W2/W3 code** (modal-from-timer-mid-drag, out-of-order modal close, cancel-token races, menu→submenu→switch-top-level) + menu hover-switch stress measuring the recomposite cost the content-swap decision claims to avoid. | Scenario matrix green asserting `DumpZOrder` + focus/capture invariants; Esc-inside-popup closes the popup, not the window; layer count stable across a menu session. | **Complete** (W0–W5b + the `windows` demo; subtree mouse capture pulled into v1; no-auto-shrink resize policy + WM fit badge adopted over auto-shrink). Tests in `Cursorial.UI.Tests/Windowing/`. An adversarial review (multi-agent) confirmed + fixed 3 findings — the P3 StyleEngine assumed a single root, so window/popup content was unstyled/untemplated (now every live surface root is stylable + caps-stamped + re-matched), and maximize moved into `OnWindowStateChanged` so a direct `WindowState` assignment matches the gesture. **W4-b deferrals → S8 menu work**: content-swap scene-reuse (W4-a re-hosts), hit-test-transparent tooltip surfaces, multi-popup chain semantics + the menu→submenu→switch / hover-switch layer-stability stress landed with P9.4 menus; **on-close focus restore landed at P9-W4** (`Popup.CloseCore` returns focus to the open-time trigger when the popup held keyboard focus — guarded on `IsKeyboardFocusWithin`, distinct from detach focus-repair; tests in `WindowPopupTests`). The `Popup.PlacementRect` anchor-tracking property stays **reserved/unimplemented** (no consumer needs it yet). |
| **P8** | **S5 A0–A3**: clock + scheduler (absorbing the P2 `UITimer` slice — same types, no API change), `AnimationInstance`/handles, `Begin`/`Stop`, interpolator registry; storyboards + `BeginStoryboard`/`StopStoryboard` edge actions (`HandoffBehavior.SnapshotAndReplace`) + production detach stop pass + toast demo; pause/resume/seek, `SkipToEnd`, `AnimationsEnabled`, easings; **Transitions** gated on the Fork A winning-base-observer seam (ledger A20) (budgeted here — "NOT small"). **S6**: `IAnimationFrameDriver` implementation, `BeginFrame(in FrameTime)` as the frame's first statement, `TickNewlyStarted` after the post-tick styling flush (resolution 8); teardown adds `Shutdown()` before terminal restore (punch 9, second half). `Margins`-typed thickness animation with **signed** interpolation (resolution 52 as amended by P2.6/LD19 — margins are signed; sizes still clamp ≥ 0). | Invariant-3 test: an animated slide/fade emits zero `Scene.Invalidate()` calls; loop idles when no animations/timers pending; handoff/PingPong/reentrancy oracle matrices green. | **Complete** (A0–A3 + the `motion` demo). `animation-matrix.md` N1–N153 + the AD ledger; tests in `Cursorial.UI.Tests/AnimationMatrix/Section01…16`. Edge actions reconciled as **do/undo** against the pinned SD16 seam (`BeginStoryboard.OnActivated` begins / `OnRetracted` stops; `StopOnRetraction` dropped — a `StopStoryboard` in `Style.Exit` stops on the exit edge; nested-rule edge actions fire on the nested rule's edge). Transitions go live via a per-element latch keyed to the first **non-collapsed** arrange, flipped at the post-layout boundary (`CompletePendingTransitionGoLive`, the `CompletePendingActivationFocus` mirror) — the only siting after both initial base-write points. `MarginsInterpolator` shipped **signed** (LD19). Adversarial audits per sub-phase found + fixed **7 real bugs** the green tests missed. UI 1945 / Animation 113 green. |
| **P9** | **Access keys end-to-end**: control-side registration against S3's manager (punch 19), `ShowUnderline` pseudo-class route → `AccessTextPresenter` (20), `IsMultiMatch` cycling (22), `FindNext` Label targeting (23), F10/`IMainMenu` re-point (56). **S8 C4** (Menu/MenuItem/ContextMenu/Separator on `Popup`, ToolTip/ToolTipService on S3's `HoverChanged` hook (26) + `UITimer`; themed chrome template replaces S4's interim, 36). **TextBox + TextPresenter** (caret service from P1, **`IClipboardService` lands in S6 here** — punch 30, per-change push from P4; inversion 4). **C2** (ItemsControl, `ItemContainerGenerator` + Unrealize sequence, `ItemsPresenter` visual-only adoption (punch 43), `SelectionModel`, ListBox + scroll integration + `TabNavigation.Once`). **C5** (TabControl/TabItem, ProgressBar with storyboard-ignited indeterminate + persistent layer per resolution 1, theme hardening across tiers). `:alternate` row-striping pseudo-class (with the container generator). **Style-inspector overlay demo** + resource-inspector hook. | SaveDialog-grade demo drivable keyboard-only on a legacy terminal and access-keyed on kitty; adversarial review of template lifecycle + items pipeline (repo design-panel convention). | Pending |
| **P10** | **Fork C X4** (generator package: build-time validation via the shared parser surfaced as Roslyn diagnostics, typed `x:Name` fields + `InitializeComponent`, generated `IXamlTypeMetadataProvider`, `CursorialXamlStrictAot` auto-set by `PublishAot`) + **S2 B3** (generator-emitted `CompiledBinding` descriptors, `x:DataType` build-time path diagnostics — second producer, no engine change). **X5 recorded as the endgame, not scheduled**: compiled bindings at scale, hot reload ("the terminal's designer"), `PreloadAsync`. | Full suite passes twice — reflection metadata vs generated provider — with no semantic drift; AOT publish of the demo app green. | Pending |

> **Probe 1 results (2026-06-10; macOS 15.6.1, Apple Silicon Arm64, 10 cores, .NET 10.0.8, Release, headless `rasterbench` — 400 iterations + warmup per scenario).** Whole-zone re-raster of the 200×60 / 314-draw-op dashboard costs ~1.0 ms raster + ~0.55 ms composite + ~0.52 ms diff+emit ≈ **2.0–2.2 ms/frame mean, p95 ≤ 2.8 ms** — roughly 13 % of a 16 ms frame budget, so **whole-zone re-raster comfortably fits 60 fps at this size** (≈ 5–7 zones of this weight could coexist per frame). Steady-state diff emits 9 B/frame; a small per-frame mutation emits ~1.2 KB. Banded scroll (band = viewport + 2K rows, K = 15, 438 ops/band-raster): offset-only frames ≈ **1.66 ms** (0.48 composite + 1.18 diff; SU scroll-detection caps emission at ~455 B/frame, 272 B allocated), re-anchor frames ≈ **2.2 ms** (band re-raster adds only ~0.53 ms, every K frames). Interpretation for S1 T3 scoping: zone re-raster cost is not the bottleneck at realistic sizes — zones can re-raster whole on invalidation without partial-raster machinery; banding's payoff is *bounding* raster cost on tall scrollable content (O(band), not O(document)) and cutting steady scroll to composite+diff, not rescuing the frame budget. Raster allocates ~178 KB/frame (text formatting churn in the drawing layer), which reinforces invariant 3 (animate by re-composite, not re-raster) and the retained-raster zone model for idle frames.

> **P0 store spike results (2026-06-10; macOS 15.6.1, Apple Silicon Arm64, .NET 10.0.8, Release, best-of-5 in-process repetitions after tiered-JIT settle; `Cursorial.UI.Tests/Benchmarks/StoreSpikeBenchmark.cs`, `[Trait("Category","Benchmark")]` — runs in every suite invocation, allocation contract asserted, timings informational).** Hover-flip churn (300 `UIObject`s with the virtual-channel override, a 2-setter `ValueFrame` removed from 30 and applied to 30 objects per frame, 1000 frames/rep): **4.4–5.0 µs/frame** (~75 ns per Add/RemoveFrame op incl. 2 change notifications each), **4,320 B/frame** steady-state — entirely `ReevaluateFrameProperties`' per-op dedupe `List<UIProperty>` for multi-entry frames (~72 B × 60 ops; gen0, trivially poolable if a profile ever asks — withdrawn entries are retained at `Unset` base, so churn pays no entry re-creation). Animated write path (one `AnimatedValueHandle<double>`, 10,000 distinct-value writes/rep, observer subscribed): **88–92 ns/write, 0 bytes** across 50,000 steady-state writes — the §2 bar ("allocation-free steady state") met exactly, asserted on the worst repetition. Micro-numbers: cold `SetValue` (first write on a fresh object) **~90 ns / 280 B** — the one-time `ValueStore` + sparse table + `EffectiveValue<T>` materialization §2.3 promises ("one allocation per (instance, property) that ever leaves default, ever"); cold `GetValue` on a storeless object **~12 ns / 0 B**; warm changing-value `SetValue` **~16 ns / 0 B**; warm `GetValue` **~11 ns / 0 B**. Interpretation against the allocation-discipline bar: every hot path is zero-allocation and the only steady-state allocation anywhere is the bounded churn-path dedupe list (4.3 KB/frame for a 30-element hover storm — noise against the drawing layer's ~178 KB/frame raster numbers above); a full 30-element hover flip costs ~5 µs ≈ 0.03 % of a 16 ms frame budget, so store arbitration is decisively not the frame-budget constraint at P0 scale. Measurement note: timings are best-of-5 because single-shot numbers on this hardware swing >5x with asynchronous tier-1/PGO promotion (instrumented tier-0 code measures *slower than Debug MinOpts*) and P/E-core scheduling; the benchmark warms the exact measured delegate ~200 ms before sampling.

> **Probe 4 / motion-storm results (2026-06-11; macOS 15.6.1, Apple Silicon Arm64, .NET 10.0.8 (SDK 10.0.300), Release, best-of-5 in-process repetitions after tiered-JIT settle; `Cursorial.UI.Tests/Benchmarks/MotionStormBenchmark.cs` + the always-on matrix gate `Section14_Perf.N200`, both `[Trait("Category","Benchmark")]` — run in every suite invocation, allocation contract asserted on the worst repetition, timing budget-asserted).** The loaded storm: a 200-position pointer sweep (row crossing a leaf boundary every other cell) over a 300-element hover-reactive tree — every leaf arms `InteractionState.Pressed` on enter and clears on leave through the sanctioned protected setter (state commit + `InteractionStateService` routing + pressed-holder fan-in), with an installed `IInteractionStateObserver` (the P3 styling-engine slot) and a `HoverChanged` subscriber riding every flip. Per-`Move` dispatch path (hit-test descent via `RenderTree.HitTest`, two-phase hover diff, enter/leave pair raises, state writes, route + raise of the Preview/main move pair): **~2.7 µs/Move (≈ 540 µs per 200-move sweep), 0 bytes steady-state across 50,000 measured dispatches** — the §2.3 zero-allocation bar met exactly, asserted on the worst repetition. Frame-loop leg (the whole 200-event storm enqueued and drained in ONE frame: Phase-1 dispatch + render + the Phase-6 `UpdateHover` re-diff): **0.55–0.58 ms/frame against the 33 ms budget (~1.7 %)**. Interpretation: input dispatch is decisively not the frame-budget constraint at P2 scale — a full-viewport hover storm costs less than a tenth of one 60 fps frame period, leaving the entire budget for the P3 hover-restyle and P5 templated-control weight this gate re-asserts later (§14.2 P3/P5 rows). Methodology notes as for the P0 spike: timings are best-of-5 after warming the exact measured delegate and busy-spin settling tiered promotion; allocation counts are exact and deterministic.

> **P3 motion-storm re-assert (2026-06-12; macOS 15.6.1, Apple Silicon Arm64, .NET 10.0.8 (SDK 10.0.300), Release, same methodology as probe 4 above; `MotionStormBenchmark.Probe4_MotionStormWithHoverRestyles` — the loaded probe-4 storm with every leaf armed by a real 2-setter `:is(RestyledLeaf):pointerover` rule whose properties are both `AffectsRender` (the background-setter shape), the styling engine in its production `InteractionStateObserver` slot; the matrix's always-on twin is `Section13_Perf.S177`).** Per-`Move` dispatch path *including* the restyles (hover diff + interaction-state commit + engine reconcile + armed-frame `SetActive` + store arbitration + render invalidation): **~2.9 µs/Move (≈ 578 µs per 200-move sweep), 0 bytes steady-state across 50,000 measured dispatches, asserted on the worst repetition** — the pseudo-class flip fast path (armed frame + one-AND interest-mask hit) adds ≈ 140 ns/Move (+5 %) over the P2 baseline re-measured the same session (2.75 µs/Move) and stays exactly allocation-free. Frame-loop leg (the 200-event storm enqueued and drained in ONE frame: dispatch + restyles + zone re-raster + render + Phase-6 `UpdateHover`): **3.66 ms best-of-5 (worst 10.99 ms) against the 33 ms budget (~11 %)** vs 0.56 ms without the restyle render weight — the delta is dominated by the `AffectsRender` re-rasters the rule legitimately buys, not by engine bookkeeping. Same-session matrix-gate numbers: S177 (5,000-dispatch leg) 0 B worst-rep + 3.74 ms best frame; S178 cold attach of 300 elements under a 20-rule `S_app` **16.2 ms** one-time (startup tier 250 ms). The §14 P3 exit criterion — motion-storm gate still green with hover restyles, flip path zero-allocation — holds with ~3× frame-budget headroom even against the loaded leg's worst repetition.

> **P5 motion-storm re-assert (2026-06-13; macOS 15.6.1, Apple Silicon Arm64, .NET 10.0.8 (SDK 10.0.300), Release, same methodology as probe 4 above; `MotionStormBenchmark.Probe4_MotionStormOverTemplatedButtons` — the loaded probe-4 storm now over real *templated controls*: 160 default-template `Button`s (each a `ContentPresenter` over presented content, natively `:pointerover`-aware via S3 hover → `InteractionState`) on a `Canvas`, every button armed with a real `Button:pointerover` background-setter rule, the styling engine in its production `InteractionStateObserver` slot).** Every hover flip drives the full P5 hot path: the hit-test descends *through* each button's template subtree (`RenderTree.HitTest` over template parts), the `:pointerover` pseudo-class flips, the styling engine reconciles the armed rule on the *templated* control, and the `AffectsRender` setter re-rasters the affected zone. Per-`Move` dispatch path *including* the templated restyles: **~12.2 µs/Move (≈ 2.45 ms per 200-move sweep), 0 bytes steady-state across 50,000 measured dispatches, asserted on the worst repetition** — the per-Move cost is ~4× the P3 bare-leaf leg (2.9 µs), the delta being the hit-test descent through the template subtree plus the heavier styling reconcile on a templated `Control`, and it stays *exactly* allocation-free. Frame-loop leg (the 200-event storm enqueued and drained in ONE frame: dispatch + templated restyles + zone re-raster + render + Phase-6 `UpdateHover`): **15.42 ms best-of-5 (worst 16.06 ms) against the 33 ms budget (~47 %)** — roughly 4× the P3 bare-leaf frame leg (3.66 ms), again the template-subtree hit-test + reconcile + re-raster weight the controls legitimately buy. (Debug build, same machine/session: per-Move 59.7 µs, frame leg 28.95 ms best — still inside budget; the always-on Release gate is the asserting one.) The §14 P5 exit criterion — motion-storm gate still green over templated controls with `:pointerover` theme rules, flip path zero-allocation — holds with ~2× frame-budget headroom even against the worst repetition.

### §14.3 Dependency inversions (found while merging; resolutions pinned)

1. **`UITimer` (S5-owned, punch 28) is consumed before S5's phase**: P5 RepeatButton→ScrollBar, P7 `ModalAttention` pulse, P9 ToolTip/menu hover-open — all precede P8. *Resolution:* a minimal `FrameClock` + `UITimer` slice ships at P2; S5 A0 absorbs it unchanged at P8.
2. **Styling `When` (Fork B) needs `BindingOperations.Watch` (S2 B0)**, but styling phases before binding. *Resolution:* P3 is selector-only; `When` is a named P4 deliverable. Recorded so Fork B isn't marked complete at P3.
3. **Style edge actions name S5 A1 types from P3.** *Resolution:* `IStyleEdgeAction` is declared at P3 as a seam; `BeginStoryboard`/`StopStoryboard` land at P8. ProgressBar's indeterminate (storyboard-ignited) correctly sits at P9.
4. **TextBox is S8's C1** but needs `ITerminalCaretService` (S1), `IClipboardService` (S6, no earlier consumer), and per-change two-way push (S2 B0). *Resolution:* moved to P9; pullable into P5 if those seams are green early.
5. **S8 orders C2 (items) before C3 (scrolling); this plan inverts** — ScrollViewer/ScrollBar have no items dependency and unblock P5 demos; ListBox scroll integration rejoins C2 at P9.
6. **Capability-class stamping** is interim-sourced from negotiated caps at P3; the pinned contract (punch 44) is the *effective* tier, which exists only after S7 R1. P5 re-points it; the P3 rule is scaffolding, not the contract.
7. **Label collision**: S1 and S7 both used `T`-prefixed sub-phases; S7's are `R0–R3` everywhere in this doc (punch 57 normalization).

---

## §15 Known deferrals (carry forward)

Each section above ends with its own deferred list; this is the global registry of cross-cutting stances, recorded per the project's convention that a deliberate cut needs a written reason. (The five de-risking probes for the riskiest bets are scheduled inside §14's phases.)

- **Accessibility / automation.** No v1 story. The one v1 affordance is deliberate: the caret service (§5.9) positions the *real terminal cursor*, which is what terminal screen readers track. An `AutomationPeer`-like seam is future, additive work.
- **Alternating-row styling.** Deferred with a designed mechanism: the items container generator stamps an `:alternate` pseudo-class (generator-owned state, element-local invalidation — does not reopen the `:nth-child` fence). Cheap early add once the ItemsControl pipeline lands.
- **Collection views (sort/filter/group/current-item).** Deferred, jointly owned by S2+S8 when it lands; v1 binds pre-shaped collections.
- **Virtualization.** Deferred; the container-generator seam (§12.6) is shaped for it, and the banded scroll-scene design (§5.7) removes raster cost from the equation — layout cost is what will eventually force it.
- **Routed-event `EventTrigger` / event-ignited storyboards.** Deferred; style-edge actions (§3, §9) plus pseudo-class pulses cover the v1 cases (`ModalAttention`).
- **MultiBinding / PriorityBinding / `INotifyDataErrorInfo` validation / `Binding.Delay`.** Deferred (§6); the `:data-error` pseudo-class name is reserved.
- **RoutedCommand/CommandManager, drag-and-drop, subtree mouse capture.** Deferred (§7); `ICommand` + `KeyBinding`s cover v1.
- **ComboBox, Slider, TreeView, DataGrid, PasswordBox, multi-line TextBox, undo.** Deferred catalog items (§12) — each a composition of shipped primitives.
- **Grid `SharedSizeGroup`, `DataTemplateSelector`, `x:Shared`, `x:TypeArguments`, minimized/topmost windows, `SpeedRatio`/`HandoffBehavior.Compose`/additive animation.** Deferred, recorded in their owning sections. (Signed margins, formerly on this list, landed at P2.6 — matrix LD19.)
- **Localization / `x:Uid`.** Out of scope for v1.
- **Push-down (shared-box) property inheritance.** The lazy-read/eager-notify v1 ships first; the O(1)-read upgrade is API-compatible and benchmark-gated (§2).
- **XAML X5 (compiled bindings, full strict-AOT).** The designed endgame on day-one seams (§4); X4 (build-time validation, typed fields, metadata provider) is in-plan at §14 P10.

## §16 Design-phase artifacts

`docs/ui-layer-design/` archives the materials this document was synthesized from — point-in-time artifacts, **not** living references (this document is canonical; where they disagree, this document wins):

- `decisions.md` — the fork-decision memo the subsystem designs were built against; `resolutions.md` — the punch-list resolution record.
- `spec-*.md` — the eight full subsystem specs (S1–S8), post-critique finals (~70–110k chars each; the implementer's deep reference per subsystem).
- `punch-list.md` — the 60-item cross-subsystem coherence punch list; `completeness-report.md` — requirement coverage, invariant enforcement audit, and the five riskiest bets with probes.
- `proposal-*.md` / `judgment-*.md` — the nine fork proposals and nine judge verdicts.

The specs predate the final naming/resolution pass; they are mechanically updated for `UI*` casing, but the punch-list resolutions are applied only *here* — read specs through this document's §13.
