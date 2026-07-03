# S2 — Data Binding Engine: Subsystem Specification (FINAL)

**Subsystem:** `Cursorial.UI` binding engine · **Folder:** `Cursorial.UI/Data/`, namespace `Cursorial.UI` (repo single-namespace convention) · **Conforms to:** DECISIONS.md Forks A/B/C + named invariants. Vocabulary per DECISIONS §Shared-vocabulary (`UIObject`/`UIElement`/`Control`/`Window`, `BindingEntry<T>`, `BindingPriority`, `ValueFrame`, `IValueEvictionListener`, `ITemplateContent`/`TemplateInstance`).

---

## 1. Scope

**Owns:**
- `BindingBase` / `AnchoredBinding` / `Binding` / `TemplateBinding` / `CompiledBinding<TSource,TValue>` — the immutable binding *descriptor* object model. Descriptors are construction-immutable (`init`-only) and instance-shareable: one `Binding` inside a `Setter` or `DataCondition` serves every element it is armed on; **all per-target state lives in expressions**.
- The path parser (`BindingPath`): property chains, indexers, attached/styled-property segments.
- `BindingExpressionBase` and its two lanes (`ReflectionBindingExpression`, `CompiledBindingExpression<TSource,TValue>`) — the runtime machinery: source resolution (DataContext / `Source` / `ElementName` / `RelativeSource`), INPC + `UIObject`-observer + `INotifyCollectionChanged` subscription wiring, value pipeline (converter → `TargetNullValue` → `StringFormat` → `FallbackValue` → type conversion), production into the property store via free-standing or **frame-hosted** `BindingEntry<T>`, two-way/one-way-to-source write-back, `UpdateSourceTrigger`.
- The **per-target expression registry** (release builds, not just DEBUG): one small list per `UIObject` that has live expressions, stored in an S1-reserved opaque slot. It backs replace-and-dispose at LocalValue, `GetBindingExpression`, `BindingDiagnostics.Explain`, the teardown sweep (`BindingOperations.TearDown`), and the DEBUG leak tracker's reporting.
- `DataContextProperty` registration (inherited styled property on `UIElement`), the "DataContext is the default binding source" rule, and the **DataContext-as-target special case** (§3.4).
- The **watch-only surface** (`BindingOperations.Watch` → `IBindingWatch`) consumed by the styling engine's `When`/`DataCondition` arming — the self-source and ancestor-source numbered requirement from Fork B.
- The `{TemplateBinding}` fast path.
- The compiled-binding descriptor contract (`Binding.Compiled<TSource,TValue>`) as a second producer of the same engine contract (Fork C: "designed NOW, implemented later" — descriptor shape is v1, generator production is X4+).
- Binding diagnostics (`BindingDiagnostics`) including the terminal-appropriate sink design.
- The runtime `INameScope` *consumption* contract (interface + attachment points + the guarded lookup walk). The XAML fork and template engine populate scopes; this subsystem defines how `ElementName` finds them.

**Explicitly not owned:**
- The `ValueStore`, `BindingPriority` arbitration, eviction mechanics, frame ordering, `SetCurrentValue` implementation — S1 (property system). This engine is a *client* of `BindingEntry<T>` / `BindInFrame` / `IValueEvictionListener`.
- Resource lookup, `StaticResource`/`DynamicResource` (S7/styling). A `DynamicResource` setter value is **not** a binding here.
- `When`-condition arming lifecycle, specificity, retraction cookies — styling engine; it consumes `IBindingWatch`.
- Focus determination — S3. We consume a `LostFocus` notification only.
- XAML parsing of `{Binding …}` text — XAML fork; it constructs `Binding` descriptors and attaches them through the `IDeferredValue.AttachTo` seam, which this engine implements.
- Collection views (sorting/filtering/current-item), `ICollectionView` — items-control subsystem, later. The engine delivers the collection object; what a list control does with it is not binding.

---

## 2. Public API sketch

### 2.1 Descriptors

```csharp
namespace Cursorial.UI;

public enum BindingMode : byte { Default, OneWay, TwoWay, OneTime, OneWayToSource }

public enum UpdateSourceTrigger : byte { Default, PropertyChanged, LostFocus, Explicit }
// Default resolves to PropertyChanged (deliberate divergence from WPF's per-property LostFocus default — §6.3).

public abstract class BindingBase
{
    public BindingMode Mode { get; init; } = BindingMode.Default;
    public UpdateSourceTrigger UpdateSourceTrigger { get; init; } = UpdateSourceTrigger.Default;
    public object? FallbackValue { get; init; } = UIProperty.UnsetValue;   // UnsetValue sentinel = "no fallback"
    public object? TargetNullValue { get; init; } = UIProperty.UnsetValue; // UnsetValue sentinel = "not specified"
    public string? StringFormat { get; init; }
    public CultureInfo? ConverterCulture { get; init; }                    // null ⇒ CultureInfo.CurrentCulture (§6.4)
    public bool Trace { get; init; }                                       // per-binding verbose diagnostics

    // THE engine contract. All descriptor lanes produce the same expression shape.
    internal abstract BindingExpressionBase CreateExpression(in BindingActivationContext context);
    // BindingActivationContext (internal readonly struct): target, target property, anchor element,
    // ValueFrame? HostFrame (frame-hosted installs), templated parent + namescope ambience.
}

/// Anchor surface shared by BOTH path-bearing lanes (reflection and compiled). TemplateBinding
/// deliberately does NOT inherit it — its anchor is fixed (the templated parent).
public abstract class AnchoredBinding : BindingBase
{
    public object? Source { get; init; }                 // explicit root; mutually exclusive w/ ElementName/RelativeSource
    public string? ElementName { get; init; }            //   (validated at CreateExpression; violation throws)
    public RelativeSource? RelativeSource { get; init; }
}

public sealed class Binding : AnchoredBinding
{
    public Binding();
    public Binding(string path);

    public string Path { get; init; } = "";              // "" or "." = the source object itself
    public IValueConverter? Converter { get; init; }
    public object? ConverterParameter { get; init; }
    public IPathTypeResolver? TypeResolver { get; init; }  // required only for attached segments "(Grid.Row)"

    /// Compiled-binding factory — lambda is the SOLE path source (Fork C contract).
    /// Cache the result in a static readonly field; each call re-analyzes the tree.
    public static CompiledBinding<TSource, TValue> Compiled<TSource, TValue>(
        Expression<Func<TSource, TValue>> path);
}

public sealed class RelativeSource
{
    public static RelativeSource Self { get; }
    public static RelativeSource TemplatedParent { get; }
    public static RelativeSource Ancestor<T>(int level = 1) where T : UIElement;

    public RelativeSourceMode Mode { get; init; }         // Self | TemplatedParent | FindAncestor
    public Type? AncestorType { get; init; }
    public int AncestorLevel { get; init; } = 1;
}

/// One-way fast path to the templated parent. Parse-time restricted to template bodies (Fork C).
/// Two-way reach-in = new Binding { RelativeSource = RelativeSource.TemplatedParent, Mode = TwoWay }.
/// CreateExpression VALIDATES inherited descriptor members: Mode other than Default/OneWay or a
/// non-default UpdateSourceTrigger throws InvalidOperationException naming the property. Converter,
/// FallbackValue, TargetNullValue, StringFormat are honored — but any of them forfeits the typed
/// fast path (§3.7) and routes through the boxed pipeline.
public sealed class TemplateBinding : BindingBase
{
    public TemplateBinding(UIProperty property);
    public UIProperty Property { get; }
    public IValueConverter? Converter { get; init; }
    public object? ConverterParameter { get; init; }
}

/// Second producer of the engine contract: typed end-to-end, zero reflection, AOT-clean when
/// generator-produced. Constructible three ways: Binding.Compiled (runtime expression analysis),
/// the X4 generator (emits this ctor), or by hand. Inherits the full anchor surface — a compiled
/// binding can be Self-, TemplatedParent-, ElementName-, or FindAncestor-anchored; the typed root
/// check (`root is TSource`) covers anchor/type mismatch (§3.7).
public sealed class CompiledBinding<TSource, TValue> : AnchoredBinding
{
    public CompiledBinding(Func<TSource, TValue> getter,
                           Action<TSource, TValue>? setter,
                           ReadOnlyMemory<CompiledPathStep> steps,
                           string pathText);

    public Func<TSource, TValue> Getter { get; }          // whole-chain typed read (the hot path)
    public Action<TSource, TValue>? Setter { get; }       // null ⇒ one-way only
    public ReadOnlyMemory<CompiledPathStep> Steps { get; } // per-hop wiring info for INPC subscription
    public string PathText { get; }                       // diagnostics ("Customer.Address.City")
    public IValueConverter? Converter { get; init; }
    public object? ConverterParameter { get; init; }
}

/// One hop of a compiled chain: the member name to match against PropertyChangedEventArgs,
/// and an object-typed step getter used only for subscription rewiring (intermediates are refs;
/// the typed whole-chain Getter does the actual value reads). For a constant-index indexer hop,
/// MemberName is "Item[]" (the INPC convention) and GetStep applies the captured index; indexer
/// hops over INotifyCollectionChanged sources additionally subscribe CollectionChanged, exactly
/// like the reflection lane.
public readonly record struct CompiledPathStep(string MemberName, Func<object?, object?> GetStep);
```

### 2.2 Converters

```csharp
public interface IValueConverter
{
    object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture);
    object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture);
        // return UIProperty.UnsetValue to mean "no value" (one-way converters may throw NotSupportedException
        // from ConvertBack; the engine treats that as a binding error, not a crash)
}
```

### 2.3 Installation, expressions, watches

```csharp
public static class BindingOperations
{
    /// Untyped install at LocalValue (XAML element attributes, code-first, tooling). VerifyAccess in
    /// debug. Throws ArgumentException if property metadata has NotDataBindable.
    /// REPLACE-AND-DISPOSE: if the registry already holds a LocalValue-lane expression for
    /// (target, property), it is disposed (entry disposed, subscriptions dropped) before the new
    /// install — one live LocalValue expression per (target, property), no zombie subscriptions.
    public static BindingExpressionBase Install(UIObject target, UIProperty property, BindingBase binding);

    /// FRAME-HOSTED install (binding-valued style setters; template content incl. TemplateBinding).
    /// The produced entry lives IN hostFrame: it participates in the frame's within-slot ordering
    /// (StyleSortKey / template provenance) and is evicted — firing OnEvicted → expression disposal —
    /// when the frame is removed or retracted (cookie retraction, TemplateInstance.Detach()).
    /// Frame-hosted installs are exempt from replace-and-dispose (frames stack by design).
    public static BindingExpressionBase Install(UIObject target, UIProperty property, BindingBase binding,
                                                ValueFrame hostFrame);

    /// Returns the LocalValue-lane expression tracked by the registry, or null. Frame-hosted and
    /// watch-only expressions are not returned here — BindingDiagnostics.Explain covers all lanes.
    public static BindingExpressionBase? GetBindingExpression(UIObject target, UIProperty property);

    /// Watch-only arming — NO store entry, value delivered to the callback. This is the styling
    /// engine's When-condition seam (self-source + ancestor-source bindings, Fork B numbered req).
    /// Unresolvable path/source ⇒ callback receives UIProperty.UnsetValue (styling pins that as "unmet").
    /// DataContext changes on the anchor re-resolve automatically and re-deliver.
    public static IBindingWatch Watch(UIElement anchor, BindingBase binding, Action<object?> onValueChanged);

    /// Teardown sweep half owned by this engine: disposes every registry-tracked expression on the
    /// element that is NOT already dead via store eviction — DirectProperty-targeted expressions and
    /// parked watches anchored here. Called by the tree/window fork during permanent-detach teardown,
    /// AFTER ValueStore.TearDown() (§3.1, §4).
    public static void TearDown(UIObject target);
}

public static class BindingExtensions      // sugar; named SetBinding to avoid collision-by-confusion with
{                                          //   UIObject.Bind<T> (the S1 producer handle — different semantics)
    public static BindingExpressionBase SetBinding(this UIObject target, UIProperty property, BindingBase binding);
}

public enum BindingStatus : byte { Inactive, Active, PathError, SourceMissing, Detached }

public abstract class BindingExpressionBase : IDisposable
{
    public BindingBase ParentBinding { get; }
    public UIObject Target { get; }
    public UIProperty TargetProperty { get; }     // the UnsetTargetProperty sentinel for watch-only: a static
                                                  //   internal UIProperty built via an internal sentinel ctor
                                                  //   (Id = -1, name "<watch>", never registered)
    public BindingStatus Status { get; }
    public BindingMode EffectiveMode { get; }     // Default resolved via BindsTwoWayByDefault metadata (§3.6)

    public void UpdateTarget();                   // force re-read source → target
    public void UpdateSource();                   // flush target → source (the Explicit trigger; also legal
                                                  //   for LostFocus/PropertyChanged — forces a flush)
    public void Dispose();                        // detach: unsubscribe everything, dispose the entry.
                                                  //   Idempotent; safe re-entrantly (§3.11).
}

public interface IBindingWatch : IDisposable
{
    object? Value { get; }                        // UIProperty.UnsetValue while unresolved
    void Pause();                                 // unhook subscriptions, keep parsed state. Used by styling
    void Resume();                                //   on element DETACH (watchers stay live while armed —
                                                  //   Fork B pin). Resume re-resolves the anchor from
                                                  //   scratch (it may have changed while paused) and
                                                  //   re-delivers the current value.
}
```

### 2.4 Path parsing

```csharp
public sealed class BindingPath
{
    public static BindingPath Parse(string text, IPathTypeResolver? resolver = null); // throws FormatException w/ position
    public static readonly BindingPath Empty;     // "" / "." — the source itself
    public override string ToString();            // round-trips
    // internal: PathSegment[] — see §3.2
}

public interface IPathTypeResolver { Type? Resolve(string typeToken); }
// XAML loader supplies its xmlns-aware resolver; the code-first default resolves registered
// UIProperty owner short names via the S1 registry's short-name → candidate-owner-types query
// (ambiguity ⇒ FormatException listing candidates) — see §4 REQUIRES.
```

**Grammar (v1):**

```
path      := '' | '.' | step ( '.' step | indexer )*
step      := identifier                  // CLR property, or UIProperty of the node's runtime type
           | '(' Type '.' identifier ')' // attached/styled property segment, resolver required
indexer   := '[' ( integer | string ) ']'   // single argument; bare or single-quoted string
```

Explicitly out (recorded): multi-argument indexers, source casts `(local:T)x`, slash/XPath, `Path=/` current-item syntax (no collection views in v1).

### 2.5 DataContext

```csharp
public partial class UIElement     // registration owned by THIS subsystem; declared on the tree fork's UIElement
{
    public static readonly StyledProperty<object?> DataContextProperty =
        UIProperty.Register<UIElement, object?>(nameof(DataContext), defaultValue: null, inherits: true);

    public object? DataContext
    {
        get => GetValue(DataContextProperty);
        set => SetValue(DataContextProperty, value);
    }
}
```

DataContext is an ordinary inherited styled property — Fork A's lazy-read/eager-notify inheritance gives binding its backbone for free. It flows *through* template instances (a template part's inheritance chain passes through the templated parent), so `{Binding}` inside a template body binds against the host's DataContext unless a `ContentPresenter` re-anchored it (standard WPF behavior; the template *barrier* is a style-matching concept and does not block property inheritance). `DataTemplate` realization sets `DataContext = item` on the instantiated root.

**The self-target special case (P0):** a default-source binding whose *target property is* `DataContextProperty` (`<StackPanel DataContext="{Binding Sub}">`) must not anchor on the value it produces — that oscillates (produce `Sub` → observed anchor changes → rebind against `Sub` → path "Sub" missing → `SetUnset` → parent value resurfaces → produce `Sub` → …). Pinned rule: such an expression anchors to the **logical parent's** DataContext — one `AddObserver(DataContextProperty)` on the logical parent, re-anchored on `AttachedToLogicalTree`/`DetachedFromLogicalTree` (reparenting); no logical parent yet ⇒ park as `SourceMissing` and retry on attach. WPF and Avalonia both special-case this identically. Pinned in the oracle test matrix (§7 B0).

### 2.6 Name scopes (runtime contract this engine consumes)

```csharp
public interface INameScope                      // shape shared with Fork C (§2.5 of the XAML proposal)
{
    void Register(string name, object element);
    object? Find(string name);
}

public static class NameScope
{
    /// DOCUMENT scope: set by the XAML loader on document roots; apps doing code-first naming set it
    /// manually. DataTemplate realization also attaches each build's fresh scope HERE on the
    /// instantiated item root (no templated parent exists; item-instance names are subtree-visible
    /// and shadow outer names — no reach-in concept, so no barrier needed).
    public static readonly AttachedProperty<INameScope?> NameScopeProperty;
    public static INameScope? GetNameScope(UIElement element);
    public static void SetNameScope(UIElement element, INameScope? scope);

    /// TEMPLATE scope: set by ApplyTemplate on the TEMPLATED PARENT (the Control) — pinned with
    /// Fork C §3.5 (the proposal's attachment point wins; the template root is NOT the carrier).
    internal static readonly AttachedProperty<INameScope?> TemplateNameScopeProperty;

    /// Guarded nearest-scope walk. For each A in (element, then logical ancestors):
    ///   1. if A carries a template scope AND element.TemplatedParent == A → return it (template parts
    ///      see their template's names first);
    ///   2. if A carries a document scope → return it.
    /// The guard in (1) is what keeps template names sealed: a DOCUMENT CONTENT CHILD of a templated
    /// control (logical parent = the control, TemplatedParent ≠ the control) fails the guard at the
    /// control and walks on to the document scope — it resolves document names, never part names.
    /// That case is a pinned conformance test.
    public static INameScope? FindEnclosing(UIElement element);
}
```

### 2.7 Diagnostics

```csharp
public enum BindingTraceLevel : byte { Off, Error, Warning, Verbose }
public enum BindingFailureKind : byte
{ None, PathError, SourceMissing, NameNotFound, AncestorNotFound, ConversionFailed,
  ConvertBackFailed, SourceUpdateFailed, TypeMismatch, SourceTypeMismatch }

public readonly record struct BindingTraceEvent(
    BindingTraceLevel Level, BindingFailureKind Kind, string Path,
    string TargetDescription,        // "Button#save.IsEnabled" — element type, #name, property
    string Message, long Timestamp); // Timestamp = Environment.TickCount64 (monotonic milliseconds)

public interface IBindingTraceSink { void Write(in BindingTraceEvent e); }

public static class BindingDiagnostics
{
    public static BindingTraceLevel Level { get; set; }            // default: Error
    public static int ErrorCount { get; }
    public static IReadOnlyList<BindingTraceEvent> RecentEvents { get; }  // 256-entry ring, always on
    public static event Action<BindingTraceEvent>? TraceEmitted;   // app hook (status bars, test asserts)
    public static void AddSink(IBindingTraceSink sink);

    /// "Why does this property have this value?" — every expression on (target, property) across ALL
    /// lanes (LocalValue, frame-hosted, watch-only, DirectProperty): status, resolved source chain,
    /// last produced value, last failure. Backed by the per-target expression registry (§3.11) —
    /// present in RELEASE builds; cost = one small list per target that has live expressions.
    /// The binding half of the F12 panel next to StyleDiagnostics.Explain.
    public static BindingExplanation Explain(UIObject target, UIProperty property);

    /// Post-session dump: call AFTER TerminalSession disposal (cooked mode restored) — see §6.1.
    public static void DumpTo(TextWriter writer);
}
```

**Ring/level policy (pinned):** Warning- and Error-severity events are always constructed and recorded to the ring regardless of `Level` (failure paths are off the happy path by definition, so this costs the steady state nothing). Verbose events are constructed only when `Level == Verbose` or the binding's `Trace` flag is set — and then also enter the ring. Sinks and `TraceEmitted` receive events with severity ≥ `Level`.

### 2.8 Consumer example

```csharp
public sealed class PersonVm : INotifyPropertyChanged
{
    public string Name { get; set; }      // raises PropertyChanged
    public bool IsDirty { get; }
    public ObservableCollection<string> Tags { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
}

var vm = new PersonVm();
var window = new Window { DataContext = vm };

// Two-way editing; source updated when focus leaves the box. The TextBox's own keystroke writes go
// through SetCurrentValue(TextProperty, …) internally — effective value replaced in place, no
// LocalValue planted, the binding observes the change and flushes on LostFocus (§3.6).
var nameBox = new TextBox();
nameBox.SetBinding(TextBox.TextProperty, new Binding(nameof(PersonVm.Name))
    { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });

// One-way, formatted, with fallback while DataContext is null:
var header = new TextBlock();
header.SetBinding(TextBlock.TextProperty, new Binding("Name")
    { StringFormat = "Editing: {0}", FallbackValue = "Editing: <nobody>" });

// Compiled lane — typed end-to-end, zero boxing on the change path:
static readonly CompiledBinding<PersonVm, bool> DirtyBinding =
    Binding.Compiled(static (PersonVm m) => m.IsDirty);
saveButton.SetBinding(Button.IsEnabledProperty, DirtyBinding);

// Element-name source: the path resolves against the NAMED ELEMENT, so reach its viewmodel through
// the DataContext segment (DataContextProperty is a registered UIProperty — the segment resolves
// through the UIPropertyAccessor lane, no reflection):
status.SetBinding(TextBlock.TextProperty,
    new Binding("DataContext.Tags[0]") { ElementName = "editorPane" });
```

XAML (`{Binding}` recognized at parse time as a typed node — Fork C; same descriptor underneath):

```xml
<TextBox Text="{Binding Name, Mode=TwoWay, UpdateSourceTrigger=LostFocus}"/>
<Button Content="_Save" IsEnabled="{Binding IsDirty}"/>
<!-- The styling engine arms this When via BindingOperations.Watch against each matching Button: -->
<Style Selector="Button#save">
  <Style.When><DataCondition Binding="{Binding IsDirty}" Value="False"/></Style.When>
  <Setter Property="IsEnabled" Value="False"/>
</Style>
```

---

## 3. Mechanics

### 3.1 Lifecycle and the strong-subscription decision

**Decision: explicit-lifetime, strong INPC subscriptions. No weak-event manager.**

Rationale, anchored on named contracts (every row is now a pinned REQUIRES, not an assumption):

| Install path | Death edge | Notification |
|---|---|---|
| Local `{Binding}` / `Install(...)` | `ClearValue`, replacement install (replace-and-dispose), teardown sweep | `IValueEvictionListener.OnEvicted` / explicit dispose |
| Binding-valued `Setter` (frame-hosted via `Install(..., hostFrame)`) | Style frame retraction (cookie removal) | eviction of the **frame-hosted** entry (`BindInFrame` contract, §4) |
| Template content (incl. `TemplateBinding`; frame-hosted in the template instance's frames) | `TemplateInstance.Detach()` — removes the template subtree's frames | per-entry eviction |
| `When` watcher | styling disarm (watcher lifetime = armed lifetime, pinned) | `IBindingWatch.Dispose` |
| Window/element detach-for-good | **teardown sweep** (pinned, §4): tree/window fork calls `ValueStore.TearDown()` per element (evicts every entry, firing `OnEvicted`) then `BindingOperations.TearDown(element)` (disposes remaining registry-tracked expressions) | eviction + registry sweep |
| `DirectProperty` target on a tree-attached element | the same teardown sweep, via the registry (no store entry exists) | `BindingOperations.TearDown` |
| `DirectProperty` target on a non-element `UIObject` | **caller-owned** (no tree lifecycle exists) — documented loudly; DEBUG leak tracker flags these specially | explicit `Dispose` |

Since **retraction is store-owned** (invariant 4), the store already must enumerate and remove entries on every frame/clear edge; eviction notification rides along for free. The teardown sweep extends the same guarantee to window close and permanent detach — it is a named cross-subsystem REQUIRES (§4), so "strong handlers cannot leak" is a contract, not a hope. The chain is: *eviction/sweep → expression `Dispose` → unsubscribe INPC/INCC/observers/LostFocus*. Strong handlers therefore cannot leak unless a consumer wires a binding outside the framework's lifecycle and abandons it — which is exactly what the **debug subscription-leak tracker** (Fork B precedent, extended here) catches: in DEBUG, the expression registry is augmented with install-site capture and a weak-target sweep on window close that reports expressions whose targets died without disposal, naming the binding path and install site.

What weak events would cost instead: an allocation + indirection per subscription, nondeterministic handler-list bloat on long-lived viewmodels, and masked lifecycle bugs. At hundreds of elements with allocation discipline as a stated requirement, deterministic strong wins. (Recorded as revisitable: a weak *backstop* mode could be added per-binding later without API change.)

### 3.2 Expression data structures (reflection lane)

```csharp
internal sealed class ReflectionBindingExpression : BindingExpressionBase, IValueEvictionListener
{
    // descriptor (shared, immutable)
    readonly Binding _binding;
    readonly BindingPath _path;                  // PathSegment[] parsed once per descriptor, cached on it

    // per-target state
    BindingEntryBase? _entry;                    // null for watch-only / OneWayToSource-passive / DirectProperty
    SourceAnchorState _anchor;                   // DataContext | Source | ElementName | RelativeSource state
    PathNode[] _nodes;                           // one per segment; _nodes[i].Instance, .Accessor, .Subscription
    PropertyChangedEventHandler? _inpcHandler;   // ONE delegate per expression, shared by all INPC nodes
    IDisposable?[] _tokens;                      // per-node UIObject-observer / INCC tokens
    object? _lastPushedValue;                    // last value produced to the target (post-pipeline) — the
                                                 //   asynchronous-echo discriminator (§3.6); typed slot in
                                                 //   the compiled lane
    Flags _flags;                                // IsPushingToTarget | IsWritingToSource | SourceDirty | Paused
                                                 //   | Disposing | Disposed …
}

internal struct PathNode
{
    public object? Instance;                     // current object at this hop
    public IPropertyAccessor Accessor;           // resolved against Instance's runtime type
    public SubscriptionKind Sub;                 // None | Inpc | UIObserver | Incc
}
```

**`IPropertyAccessor` resolution order per node** (cached globally in `AccessorCache : Dictionary<(Type, string|UIProperty), IPropertyAccessor>`, lock-free read via copy-on-write — populated on the UI thread only):

1. Node instance is `UIObject` and the segment name (or attached segment) matches a registered `UIProperty` for its runtime type → **UIPropertyAccessor** (registry lookup, `GetValue`/`SetValue` untyped lane, observation via `AddObserver` — no reflection, no INPC needed).
2. CLR property via reflection: `PropertyInfo` wrapped in a compiled delegate when `RuntimeFeature.IsDynamicCodeSupported`, else direct `PropertyInfo.GetValue/SetValue` (honest AOT fallback; the compiled lane is the real AOT answer).
3. Indexer segments: `IList`/`IReadOnlyList<T>` int fast path; `IDictionary<string,?>`/general `Item[...]` reflection otherwise. Indexer nodes additionally subscribe `INotifyCollectionChanged` when present, and respect the INPC `"Item[]"` convention.

### 3.3 Activation and re-wiring

```
Activate(expression):
    resolve anchor root (§3.4); if unresolved → Status = SourceMissing, ProduceFallbackOrUnset(), park
    WireFrom(0)

WireFrom(i):
    for k in i..n-1:
        unsubscribe node k (if wired)
        node[k].Instance = (k == 0) ? root : ReadHop(k-1)
        if Instance is null/missing → leaf = UnsetValue; stop wiring (nodes below stay unwired)
        resolve Accessor from cache; subscribe:
            UIObject hop      → AddObserver(property)         (token in _tokens[k])
            INPC hop          → ((INotifyPropertyChanged)inst).PropertyChanged += _inpcHandler
            indexer over INCC → CollectionChanged += handler
        (OneTime / OneWayToSource: skip subscription entirely; OneWayToSource keeps the ANCHOR
         observer — DataContext changes must re-target its writes — but no path-node subscriptions)
    PushToTarget(ReadLeaf())

OnSourcePropertyChanged(sender, e):                  // the single shared INPC handler
    if _flags.Disposed → return                                          (§3.11)
    if not dispatcher.CheckAccess() → CoalescedPost(); return            (§3.8)
    i = index of node with ReferenceEquals(Instance, sender)             (n ≤ ~4; linear scan)
    if e.PropertyName is null/empty or matches node[i] member (ordinal, "Item[]" for indexers):
        if _flags.IsWritingToSource → note echo, re-read once after write completes (§3.6)
        else WireFrom(i + 1)         // re-read hop i, rewire below, push
```

Steady-state cost of a leaf change: one scan over ≤4 nodes, one accessor read per surviving hop, one pipeline pass, one `entry.SetValue` (the store's equality short-circuit absorbs no-ops). Zero allocations except the boxed leaf in the reflection lane (the store's box-interning cache covers common values — bools, small ints).

### 3.4 Source anchoring

- **Default (no Source/ElementName/RelativeSource):** root = target element's `DataContext`. The expression takes one `AddObserver` on `DataContextProperty` of the target (Fork A's eager-notify inheritance guarantees delivery on entry-less descendants). DataContext change ⇒ `WireFrom(0)` — a full rebind, including for `OneTime` (WPF-consistent: OneTime re-evaluates per DataContext). **Exception (the P0 special case, §2.5):** when the *target property* is `DataContextProperty` itself, the anchor is the **logical parent's** DataContext — observer on the parent, re-anchored on reparent, parked until attach when no parent exists. Non-`UIElement` targets have no DataContext; default-source bindings on them are an install-time error (`SourceMissing` + trace) — use `Source`.
- **`Source`:** fixed root; no re-resolution ever.
- **`ElementName`:** `NameScope.FindEnclosing(targetElement)?.Find(name)` — the guarded walk of §2.6. If the target isn't attached to the logical tree yet, or the name isn't registered yet (forward reference during XAML build), the expression parks (`SourceMissing`, no trace yet) and retries on `AttachedToLogicalTree`; failure *after* attach traces `NameNotFound`. Inside a template instance, `FindEnclosing` returns the template namescope (carried by the templated parent, guard-matched) → finds template parts; document names are invisible to parts and part names are invisible to document content (Fork C scoping + the §2.6 guard).
- **`RelativeSource.Self`:** root = target element itself. **`TemplatedParent`:** root = `targetElement.TemplatedParent`; null outside a template ⇒ `SourceMissing` + trace. **`FindAncestor`:** walk `LogicalParent` upward counting assignable matches against `AncestorType` until `AncestorLevel`; resolved at attach, re-resolved on `DetachedFromLogicalTree`/`AttachedToLogicalTree` (reparenting). The walk crosses the template boundary the way the logical tree does (part → templated parent), so a part can find its host control and beyond.
- **Watch-only anchoring** is identical, with `anchor` standing in for the target element. This is what makes `When` conditions get self-source (`RelativeSource.Self`) and ancestor-source (`FindAncestor`) for free — both ship in B0 (§7).

**Bulk-swap note:** a whole-window DataContext swap is ~N synchronous full rebinds, with sub-DataContext bindings re-triggering eager-notify inheritance waves mid-cascade. Fine at 10² elements; the app-model's bulk-swap paths SHOULD wrap the swap in S1's `DeferNotifications`, which coalesces per property (first-old/last-new).

### 3.5 The value pipeline (source → target)

```
raw = ReadLeaf()                                  // UnsetValue on any unresolved hop
if raw is UnsetValue:
    result = FallbackValue (if specified) else → entry.SetUnset(); Trace(if observed); return
else:
    v = raw
    if Converter != null: v = Converter.Convert(v, targetType, ConverterParameter, culture)
        // exception → Trace(ConversionFailed); v = UnsetValue
    if v is UnsetValue: result = FallbackValue ?? → SetUnset; return
    if v is null && TargetNullValue specified: v = TargetNullValue
    if StringFormat != null && targetType ∈ {string, object}: v = string.Format(culture, StringFormat, v)
    result = v
convert result to the property type:
    exact/assignable → done
    else IConvertible/enum-parse fast paths, then XamlConverters.For(targetType)   // Fork C registry
    failure → Trace(TypeMismatch) + SetUnset                                       // lower priorities resurface
push: typed entry when the expression lane is typed; else BindingEntryBase.SetValue(object?)
record _lastPushedValue = result                                                   // §3.6 echo discriminator
```

`SetUnset` — never "push a default" — is what keeps **retraction store-owned**: when a binding can't produce, the store promotes the next frame/priority; the engine never fabricates restoration values. Metadata `Validate` rejection inside the store is likewise the store's diagnostic, not ours.

### 3.6 Target → source (TwoWay / OneWayToSource), value coexistence, `SetCurrentValue`, and `UpdateSourceTrigger`

The expression registers a target observer (`AddObserver` on the target property — typed in the compiled lane, untyped otherwise). On a target effective-value change:

1. **Skip if `_flags.IsPushingToTarget`** (synchronous echo of our own push).
2. **Skip if the new value equals `_lastPushedValue`** per the property's comparer (metadata `Comparer` or `EqualityComparer<T>.Default`; `Equals` in the boxed lane). This is the *asynchronous*-echo discriminator: it filters store-side promotions that resurface a value we produced — most importantly **animation-handle disposal**, where the base (our pushed value) resurfaces at LocalValue priority outside both the `IsPushingToTarget` window and the Animation-priority filter. Suppression-by-value cannot lose a genuine edit: writing a value equal to what the source already round-tripped is definitionally a no-op.
3. **Skip if the change's observer-args `BindingPriority == Animation`** — animated values never round-trip to the source (deliberate divergence from WPF, §6.5). Pinned with S1 (§4): `SetCurrentValue` observer args carry the priority of the lane whose value it replaced — `Animation` while an animation holds the property (so a mid-animation `SetCurrentValue` is also filtered here, consistent with "animated values never round-trip"), else the base lane's priority (so the ordinary TextBox case proceeds).
4. Otherwise the change is a genuine target write → proceed per trigger: `PropertyChanged` → `WriteToSource()` now. `LostFocus` → set `SourceDirty`, flush on the S3 LostFocus notification (subscription created lazily at activation; if S3 reports the trigger unavailable, fall back to `PropertyChanged` with a one-time `Warning` trace). `Explicit` → `SourceDirty` until `UpdateSource()`.

**Value coexistence (pinned — the Fork A model, canonical proposal §2.2, unamended by DECISIONS):** within one priority, *last writer wins, and a binding's push counts as a write*. Therefore `SetValue` at LocalValue does **not** kill a local binding — it is a transient override that loses the next time the binding produces; **`ClearValue` is the documented kill** (it removes the local value *and* detaches local-priority bindings, per the S1 contract). The exported **control-author contract**: control-internal writes (TextBox keystrokes, slider drags) SHOULD use `SetCurrentValue`, not because `SetValue` would destroy a binding (it wouldn't), but because (a) the consumer's binding may be **frame-hosted at Style/template provenance**, and a LocalValue write would *permanently shadow* it (LocalValue > Style), whereas `SetCurrentValue` replaces the effective value in place without changing its source; and (b) `SetCurrentValue` avoids planting a LocalValue that later fights the binding via last-writer-wins races. Both non-echo `SetValue` and `SetCurrentValue` writes feed TwoWay write-back (steps 1–4 discriminate echoes, not write APIs).

`WriteToSource()` — the reverse lane, fully specified:
- **Converter present** → `Converter.ConvertBack(targetValue, leafType, parameter, culture)`; `UnsetValue` or exception ⇒ `Trace(ConvertBackFailed)`, no write.
- **`TargetNullValue` reverse mapping**: if `TargetNullValue` is specified and the target value equals it (property comparer), write `null` to the source.
- **`StringFormat` two-way without a converter**: the reverse parse applies **only when the format is exactly `"{0}"`** (no literal text, no format specifier); then parse via `XamlConverters` against the leaf type. Any composite format (`"Editing: {0}"`) ⇒ `Trace(ConvertBackFailed)`, no write — parsing a formatted prefix back into the source is corruption, not conversion (WPF's de-facto behavior, made explicit).
- **No converter, type gap** (TextBox.Text `string` → VM `int Age`): assignable → direct; else IConvertible/enum-parse fast paths; string values → `XamlConverters.For(leafType)`; failure ⇒ `Trace(SourceUpdateFailed)`, no write.
- **Leaf write** through the node accessor (`UIPropertyAccessor.SetValue`, cached reflection setter, or `CompiledBinding.Setter`). **For `OneWayToSource`** (whose path nodes are unsubscribed and therefore go stale), the chain is **re-resolved from the anchor on every write** (hops 0..n−2 re-read — ≤4 cheap reads) so a replaced intermediate (`vm.Address` swapped) never receives writes through a dead object. TwoWay paths keep live nodes via subscriptions and need no re-resolve. Guarded by `IsWritingToSource`; if the source raises INPC during the write (viewmodel normalization/clamping), one coalesced re-read runs after the write so target and source converge — the WPF round-trip, kept.

`OneWayToSource`: path subscriptions are not created (anchor observer kept); the entry is installed but never produces (it exists purely for lifetime/eviction/discoverability — the store treats a never-set entry as contributing nothing). Initial sync at activation pushes the target's current value to the source (WPF semantics).

**`EffectiveMode` resolution:** `Mode == Default` resolves at install from `BindsTwoWayByDefault` metadata (TwoWay if set, else OneWay). Leaf *writability* cannot be known until wiring (it depends on the runtime type at hop n−1): if the leaf proves read-only at wiring (no setter on the resolved accessor / null `CompiledBinding.Setter`), the expression **degrades to OneWay with a one-time `Warning` trace**, and re-evaluates on every rewire (intermediate instances — and thus the leaf's declaring type — can change).

### 3.7 Compiled lane

`CompiledBindingExpression<TSource,TValue>` shares anchoring (§3.4 — the full `AnchoredBinding` surface: Source/ElementName/Self/TemplatedParent/FindAncestor), triggers, and lifecycle with the reflection lane but replaces path machinery:

- Read: `if (_anchor.Root is TSource s)` → `TValue v = Getter(s)` — no per-hop reads, no boxing. Root type mismatch (including an `ElementName`/`FindAncestor` anchor resolving to a non-`TSource` object) ⇒ `SourceTypeMismatch` trace + UnsetValue (styling: unmet).
- Wiring: `Steps` provide per-hop member names + object-typed step getters; INPC subscription wiring is identical to §3.3 but the *value* read on change is one `Getter(s)` call. Indexer hops carry `MemberName == "Item[]"` and subscribe `INotifyCollectionChanged` when the hop instance implements it (§2.1). A hop returning a struct mid-chain just works (the whole-chain getter copies it; no subscription is attempted on non-INPC hops).
- Push: when the target property is `StyledProperty<TValue>` and no converter/StringFormat is present, push through `BindingEntry<TValue>.SetValue(v)` — **zero boxing, zero allocation steady state**, the binding analog of `AnimatedValueHandle<T>`. `_lastPushedValue` is a typed `TValue` slot here. Otherwise fall through the §3.5 boxed pipeline.
- `Binding.Compiled` (runtime producer): analyzes the lambda body once — member-access chain only (`vm => vm.A.B[0].C` allowed: member hops + constant-index hops; method calls/operators ⇒ `FormatException` naming the offending node). `Getter` = `expr.Compile()` (interpreter on AOT-without-codegen); `Setter` derived when the leaf is a settable member. The X4 generator later emits the `CompiledBinding` ctor call directly with real delegates — same type, second producer, no engine change (the Fork C contract honored).
- **`TemplateBinding` untyped→typed bridge (named mechanism):** the descriptor holds an untyped `UIProperty`, but the fast path needs a typed observer→entry pair. The bridge is double dispatch through the property identity: an internal virtual on `UIProperty` (`CreateEntry(UIObject target, …)` / `CreateTemplateTransfer(templatedParent, target, listener)`), overridden by `StyledProperty<T>` to wire `AddObserver<T>` → `BindingEntry<T>` with `T` closed at registration — no reflection, no `MakeGenericType`. This S1 surface is in the REQUIRES block (§4).

### 3.8 Threading and frame coherence

All engine entry points (`Install`, `Watch`, `UpdateSource/Target`, `Dispose`, `TearDown`) are UI-thread-only (`VerifyAccess` debug-asserted — invariant 6). Source INPC may arrive on any thread:

- Same thread → applied synchronously (a VM change during frame N's input drain reaches the store before layout — invariant 1 holds with no machinery).
- Foreign thread → the expression sets a per-node pending **bitmask** (bit *i* set = node *i* dirty) via `Interlocked.Or` and, if not already queued, posts one drain callback via the consumed `IUiDispatcher.Post`. The drain rewires from the lowest set bit. The dispatcher contract (§4) runs posted work in the next frame's dispatch drain, **before layout**, so a cross-thread change is fully coherent within the frame that first observes it — and **`Post` MUST wake the frame loop** (schedule a dispatch drain if none is pending; the `InteractiveDemo.Invalidate()` Interlocked-flag pattern is the template), because the de-facto main loop has an event-driven mode that otherwise renders only on input/resize — a background VM update must not sit until the user moves the mouse. N changes between frames coalesce into one rewire+push.

`PauseIOAsync`/`RenegotiateAsync` windows need nothing special: bindings only touch the store, never the terminal (invariant 2).

### 3.9 Watch-only mechanics

`BindingOperations.Watch` builds the same expression with `_entry = null` and a callback sink. Pinned semantics for the styling engine: unresolved ⇒ callback receives `UIProperty.UnsetValue` (When treats as unmet); anchor DataContext change ⇒ automatic rebind + re-deliver; **`Pause()` is the element-detach parking mechanism** (per the Fork B pin: watchers stay live while *armed*; activation flips never pause) — it detaches node subscriptions and anchor observers while retaining the parsed path and accessor-cache warmth; **`Resume()` re-resolves the anchor from scratch** (the anchor's DataContext, name resolution, and tree position may all have changed while paused), rewires, and re-delivers the current value. Callback delivery is synchronous on the UI thread (cross-thread INPC goes through §3.8 first), so a `When` flip triggered by a VM change participates in the same frame. Watches are registry-tracked under their anchor; the teardown sweep is their backstop if styling never disarms.

### 3.10 Diagnostics flow

Failure points (§3.5/§3.6 traces) build `BindingTraceEvent`s per the §2.7 pinned policy: Warning/Error always constructed and ring-recorded (failure paths only — the happy path allocates nothing for diagnostics); Verbose gated on `Level`/`Trace`. The ring buffer (256, overwrite-oldest) is ~30 KB and makes post-mortem "why is this blank" answerable. Sink chain: ring (always, ≥ Warning) → `Trace.WriteLine` (DEBUG default) → file sink when `CURSORIAL_BINDING_TRACE=<path>` is set (mirrors the repo's `CURSORIAL_TRACE_OUTPUT` convention) → app sinks via `AddSink`/`TraceEmitted` (≥ `Level`). `Explain` reads the per-target expression registry (§3.11). See §6.1 for why none of these is "write to the terminal."

### 3.11 Expression registry, reentrancy, and disposal discipline

**Registry.** Every live expression registers in a per-target inline list stored in the S1-reserved opaque `UIObject` slot (one pointer per instance; null when unused — no `ConditionalWeakTable` indirection). Tracked: LocalValue installs (keyed by property — backs replace-and-dispose and `GetBindingExpression`), frame-hosted installs, DirectProperty-targeted expressions, and watches (keyed under the anchor). Unregistration happens in `Dispose`. This is a release-build structure; the DEBUG leak tracker adds install-site capture and the weak-target sweep on top of it.

**Reentrancy.** `PushToTarget` → `entry.SetValue` → synchronous observers → styling `When` flip → cookie retraction → eviction of a frame that hosts *this same expression* → `OnEvicted` → disposal — all while the expression's own frames are on the stack. Pinned rules:
1. `Dispose()` is idempotent, gated by `Disposing`/`Disposed` flags.
2. `OnEvicted` invokes `Dispose(fromEviction: true)`, which **skips `entry.Dispose()`** — the store is mid-eviction and the entry is already dead. Belt-and-braces, S1 additionally pins `BindingEntryBase.Dispose()` as idempotent and legal from within `OnEvicted` (§4).
3. Every handler entry point (`OnSourcePropertyChanged`, target observer, collection-changed, LostFocus, dispatcher drain, watch callbacks) checks `Disposed` and returns.
4. After any `entry.SetValue`/`PushToTarget` call returns, the expression re-checks `Disposed` before touching `_nodes`/`_tokens` and unwinds without further work.

---

## 4. Cross-subsystem contracts

### REQUIRES from S1 — property system (the store)

```csharp
// Producer handles: untyped base + typed leaf, eviction listener at install.
public abstract class BindingEntryBase : IDisposable
{
    public UIProperty Property { get; }
    public BindingPriority Priority { get; }
    public void SetValue(object? value);      // boxed lane; store interns common boxes
    public void SetUnset();                   // withdraw contribution; store promotes (invariant 4)
    public void Dispose();                    // PINNED: idempotent; legal re-entrantly from within OnEvicted
}
public sealed class BindingEntry<T> : BindingEntryBase { public void SetValue(T value); }

public interface IValueEvictionListener { void OnEvicted(BindingEntryBase entry); }

// On UIObject — FREE-STANDING entries (LocalValue ONLY; Style/Default throw — style-slot
// contributions MUST be frame-hosted; Animation is AnimatedValueHandle<T> territory):
BindingEntry<T>  Bind<T>(StyledProperty<T> p, BindingPriority pri, IValueEvictionListener? listener);
BindingEntryBase BindUntyped(UIProperty p, BindingPriority pri, IValueEvictionListener? listener);

// FRAME-HOSTED entries (the provenance fix — P0): the entry lives IN hostFrame. It participates in
// the frame's within-slot ordering (StyleSortKey for style frames; template provenance for template
// frames) and is evicted — firing OnEvicted — when the frame is removed or retracted (cookie
// retraction, TemplateInstance.Detach()). This is what makes the §3.1 death-edge table honest.
BindingEntry<T>  BindInFrame<T>(StyledProperty<T> p, ValueFrame hostFrame, IValueEvictionListener? listener);
BindingEntryBase BindInFrameUntyped(UIProperty p, ValueFrame hostFrame, IValueEvictionListener? listener);

IDisposable AddObserver<T>(StyledProperty<T> p, IValueObserver<T> o);     // typed target watch
IDisposable AddObserver(UIProperty p, IUntypedValueObserver o);           // untyped lane (reflection bindings;
                                                                          //   change args MUST carry BindingPriority)
object? GetValue(UIProperty p);                                           // untyped read (OneWayToSource init)
internal object? BindingHostState;   // one opaque per-instance slot reserved for the engine's registry (§3.11)
```

Plus behavioral requirements (each needs S1 countersignature before freeze):
- `SetCurrentValue` raises observer notification with **args priority = the lane whose value it replaced** (`Animation` while animated — so mid-animation `SetCurrentValue` is invisible to write-back, consistent with §6.5 — else the base lane's priority); provenance unchanged either way.
- Observer delivery fires on inherited-value changes on entry-less descendants (the second carrier — DataContext depends on it).
- `ClearValue` evicts local-priority binding entries (the documented binding kill under the coexistence model, §3.6).
- Frame removal/retraction (style cookies, `TemplateInstance.Detach`) evicts frame-hosted entries.
- **Teardown sweep:** `ValueStore.TearDown()` — evicts *every* entry on the instance (free-standing and frame-hosted), firing `OnEvicted` per entry. The tree/window fork MUST call it (with `BindingOperations.TearDown` after — see tree REQUIRES) on permanent detach. This is the load-bearing premise of §3.1's strong-subscription decision.
- Metadata exposes `BindsTwoWayByDefault` / `NotDataBindable` / `Comparer` (the §3.6 step-2 equality source); `UIProperty.UnsetValue` singleton; an internal sentinel `UIProperty` constructor (the `UnsetTargetProperty` story, §2.3).
- Registry lookups: `(Type, string) → UIProperty` for path segments, **plus a short-name → candidate-owner-types query** (`FindOwnersByShortName(string) → IReadOnlyList<Type>`) for the default `IPathTypeResolver`'s `(Grid.Row)` resolution with ambiguity detection.
- Entries newly created are valueless until first `SetValue` (OneWayToSource relies on this).
- Internal virtual `UIProperty.CreateEntry`/`CreateTemplateTransfer` overridden by `StyledProperty<T>` — the untyped→typed bridge of §3.7.
- Bindings to `DirectProperty<TOwner,T>` go through its Getter/Setter delegates + `AddObserver` overload; no entry; lifetime = expression `Dispose`/replacement/**teardown sweep via the registry** (documented: no priority arbitration, no restoration; caller-owned on non-element `UIObject`s).

### REQUIRES from S1/tree — element tree

```csharp
UIElement.LogicalParent { get; }
event AttachedToLogicalTree / DetachedFromLogicalTree     // ElementName retry, FindAncestor + DataContext-target
                                                          //   parent-anchor re-resolution
UIElement.TemplatedParent { get; }                        // RelativeSource.TemplatedParent, TemplateBinding,
                                                          //   the NameScope.FindEnclosing guard
// PINNED teardown contract: on permanent detach (window close, element disposal), the tree/window
// owner walks the subtree bottom-up and per element calls ValueStore.TearDown() THEN
// BindingOperations.TearDown(element). No binding entry or registry-tracked expression survives.
```

### REQUIRES from S3 — input/focus

```csharp
// LostFocus contract; raised on the UI thread when PHYSICAL focus leaves the element. PINNED
// additionally: a terminal-level FocusEvent { HasFocus: false } MUST produce LostFocus on the
// physically focused element (the engine then flushes pending LostFocus-triggered source updates —
// recorded divergence, §6.8). Logical-focus pane switches must eventually produce physical
// LostFocus per S3's model.
UIElement: event Action<UIElement>? LostFocus;
```

### REQUIRES from the app-model subsystem (window/dispatcher owner)

```csharp
public interface IUiDispatcher { bool CheckAccess(); void Post(Action callback); }
// Posted callbacks run in the next frame's dispatch drain BEFORE layout (frame-coherence — invariant 1).
// PINNED: Post MUST schedule a dispatch drain (wake the event-driven frame loop) when none is pending —
// the InteractiveDemo.Invalidate() Interlocked-flag pattern. Without this, cross-thread INPC sits
// until unrelated input arrives. No priority tiers (DECISIONS invariant 1). Exposed to this engine via
// UIDispatcher.Current or ctor injection.
```

### REQUIRES from X — XAML fork

The `IDeferredValue.AttachTo` seam shape (extension results never flow through `SetValue` as sentinels), with the attach context carrying the **host frame** when building template content so installs route to `Install(..., hostFrame)`; `IPathTypeResolver` implementation carrying xmlns context for attached segments; the `XamlConverters.For(Type)` registry (string→T last resort in §3.5 and the §3.6 reverse lane); **namescope population per the pinned attachment points** (§2.6): document scope on document roots, template scope set by `ApplyTemplate` **on the templated parent** (Fork C §3.5 — this spec's earlier "template root" wording is superseded), DataTemplate instance scope on the item root via the document slot; later, `x:DataType` flowing into generator-produced `CompiledBinding` descriptors.

### PROVIDES to styling (Fork B / S4)

`BindingOperations.Watch(anchor, binding, callback) → IBindingWatch` with the pinned semantics (UnsetValue ⇒ unmet; DataContext rebind; Pause/Resume = detach parking with anchor re-resolution on Resume; watcher lifetime = armed lifetime is the *caller's* job — we provide deterministic `Dispose`, and the teardown sweep as backstop). Self-source and ancestor-source via `RelativeSource` (§3.4), **both in B0** — the numbered requirement. Also: binding-valued `Setter` instantiation — the styling engine calls `BindingOperations.Install(element, property, bindingBase, frame)` per armed element, passing **its own `ValueFrame` for that armed rule**; eviction on cookie retraction kills the expression with no styling-side bookkeeping.

### PROVIDES to XAML fork

`BindingBase` implements the `IDeferredValue.AttachTo` seam: `AttachTo(target, property, ctx)` ⇒ `Install` at LocalValue (element attributes) or `Install(..., ctx.HostFrame)` (template content), with `ctx` supplying templated-parent and namescope ambience; `TemplateBinding` as the parse-restricted typed node target; `BindingPath.Parse` reusable for `{Binding Path=…}` validation with position info.

### PROVIDES to template engine

`TemplateBinding` fast-path expression (observer-on-templated-parent → typed entry push via the §3.7 bridge, no path parse, no DataContext dependency); the guarantee that **every expression created inside template content dies on `TemplateInstance.Detach`** via frame-hosted entry eviction — no expression survives the barrier teardown (feeds the debug leak tracker).

### PROVIDES to control authors (S5/S6)

The `SetCurrentValue` contract of §3.6 (use it for control-internal writes; `SetValue` coexists with local bindings but shadows frame-hosted ones; `ClearValue` kills); `BindingOperations.GetBindingExpression(…).UpdateSource()` for commit gestures (e.g. Enter in a TextBox).

---

## 5. Requirement mapping

- **R2 (powerful data binding) — primary.** Paths with indexers and attached segments; five modes incl. `BindsTwoWayByDefault` resolution with read-only-leaf degradation; three triggers + Explicit; Source/ElementName/RelativeSource(Self/TemplatedParent/FindAncestor) on **both** lanes via `AnchoredBinding`; converters/parameter/culture; StringFormat/FallbackValue/TargetNullValue with a fully specified reverse lane; INPC+INCC; compiled typed lane; diagnostics with release-build `Explain`.
- **R1 (styling/templating):** `TemplateBinding` fast path, `RelativeSource.TemplatedParent`, binding-valued setters via frame-hosted `Install`, DataContext realization for `DataTemplate`s.
- **R3 (inheritance):** `DataContext` rides Fork A's `Inherits` machinery; one mechanism, no parallel scope walk; the DataContext-as-target parent-anchor rule keeps the sub-DataContext idiom sound.
- **R7 (XAML):** `{Binding}`/`{TemplateBinding}` attach through `IDeferredValue.AttachTo` (frame-aware); untyped `Install`; parse-time path validation; the compiled-descriptor contract is the X4 generator's landing pad.
- **R8 (hybrid triggers):** `When`/`DataCondition` consume `Watch` — the binding engine is the entire data half of the activation predicate, including ancestor-source conditions from B0.
- **Invariant compliance:** *Frame coherence* — synchronous same-thread application; cross-thread coalesced into pre-layout dispatch with loop wake (§3.8). *Never touch Scene/CellBuffer* — the engine's only output is store writes; invalidation is `PropertyEffects` metadata routing, so a bound `Offset` re-composites and a bound `Background` re-rasters with zero binding-engine awareness (invariants 2, 3). *Retraction is store-owned* — `SetUnset`/eviction/teardown sweep, never set-back (§3.5). *Template barrier* — expressions are template-instance-frame-hosted and die on `Detach` (§3.1, §4); template names sealed by the §2.6 guard. *Single UI thread* — `VerifyAccess` + marshaling. *Lower layers additive-only* — this subsystem references nothing below `Cursorial.UI`.

---

## 6. Terminal-specific design

1. **Where binding errors go.** In WPF they go to the debugger's Output window; in a TUI, stdout/stderr *are the application's screen* — a stray write desyncs the `FrameRenderer` (it is the sole owner of SGR/cursor state; rendering-session map, "Notes for UI-layer designers") and raw-mode LF needs `\r\n`. So: never write to the terminal. The sink design (§3.10): always-on ring buffer (Warning+), `TraceEmitted` for an in-app status-bar consumer, env-gated file sink (`CURSORIAL_BINDING_TRACE`, mirroring the repo's `CURSORIAL_TRACE_OUTPUT` precedent), an in-terminal DevTools overlay (alongside Fork B's style inspector) reading `RecentEvents`/`Explain`, and `DumpTo` for the app-model to call **after** session disposal in the canonical teardown order (renderer Close → … → session dispose → only then console writes).
2. **Strong, deterministic subscriptions** (§3.1) instead of WPF's WeakEventManager: the framework's retraction story (store-owned eviction, frame-hosted entries, `TemplateInstance.Detach`, cookie retraction, the pinned teardown sweep) makes every binding death observable, and at terminal scale (10² elements, 20–60 fps, allocation discipline) weak-event indirection is pure cost. Debug leak tracker replaces the safety weak events would have bought.
3. **`UpdateSourceTrigger.Default` = `PropertyChanged`** (WPF defaults TextBox.Text to LostFocus). A terminal text field's per-keystroke source update is a few struct copies and one store write — there is no expensive validation/visual pipeline to debounce — and live-updating `When` conditions (e.g. Save enabling as you type) are the showcase behavior. LostFocus remains available and is the documented choice for parse-on-commit numeric fields.
4. **Culture: `CultureInfo.CurrentCulture`** for conversion/StringFormat (WPF's hardcoded en-US is a recorded wart); per-binding `ConverterCulture` overrides. Terminal apps are frequently ssh'd system tools where locale-correct number/date display is expected.
5. **Animated values never round-trip to the source** (§3.6 steps 2–3). On this stack the `Animation` priority sits *above* LocalValue and writes at frame rate; a WPF-faithful TwoWay would spam the viewmodel at 50 fps with quantized intermediate cells. The observer args' `BindingPriority` makes the cut exact, the `_lastPushedValue` compare makes handle-disposal resurfacing exact, and the `SetCurrentValue`-while-animated pin closes the last gap. Recorded divergence.
6. **No `IsAsync`/async bindings.** One UI thread, one frame loop; the cross-thread INPC marshal (§3.8) is the supported pattern, and slow source reads belong in the viewmodel (the same stance the lower layers take on blocking work). Rejected, recorded.
7. **Compiled lane sized for the grid's hot properties.** Bound `Offset`/`Opacity`-shaped properties (`AffectsComposite`) can change every frame under VM-driven animation; the typed `BindingEntry<T>` push keeps that allocation-free end-to-end, matching the property system's `AnimatedValueHandle<T>` discipline and the re-composite-not-re-raster invariant.
8. **`UpdateSourceTrigger.LostFocus` rides PHYSICAL focus** (recorded divergence — WPF's rides logical focus). Focusing a menu/toolbar (a separate focus scope, R4) flushes a pending TextBox edit where WPF wouldn't, and a terminal-level `FocusEvent { HasFocus: false }` (Alt-Tab away) also flushes (via S3's pinned LostFocus raise, §4). Rationale: a terminal app can be killed from outside at any moment (SIGHUP, ssh drop) — committing pending edits on focus departure is the safe default, and "menu takes focus" genuinely ends the edit gesture in a cell-grid UI. Recorded like §6.3/§6.5.

---

## 7. Phasing (repo §11 convention: numbered phases, deferrals recorded with reasons)

- **B0 — spine** *(unblocks styling S3 `When` and general element bring-up)*: `BindingBase`/`AnchoredBinding`/`Binding`; `BindingPath` parser (properties, int/string indexers, attached segments); reflection expression + accessor cache; strong INPC/INCC/UIObject-observer wiring; `DataContextProperty` + inheritance hookup + **the DataContext-as-target parent-anchor special case**; all five modes + `BindsTwoWayByDefault` + read-only-leaf degradation; triggers PropertyChanged/Explicit; `Source`/`RelativeSource.Self`/`TemplatedParent`/**`FindAncestor`** (resolution at arm/attach + attach/detach re-resolution — pulled into the spine because Fork B's numbered requirement names *ancestor-source* `When` conditions; it needs only `LogicalParent` + the attach events, which DataContext inheritance wiring already requires); full §3.5 pipeline + the §3.6 reverse lane; free-standing + **frame-hosted** `BindingEntry` production, eviction lifecycle, **expression registry + replace-and-dispose + teardown-sweep integration** + debug leak tracker; **`BindingOperations.Watch`** (the Fork B dependency — deliberately in the spine, ancestor-source included); diagnostics ring + sinks. Oracle-pinned test matrix for the pipeline (fallback/null/format permutations, the DataContext-self case, echo-suppression cases incl. animation-handle disposal) authored before the engine, per the Fork A precedent.
- **B1 — tree-shaped sources & focus**: `ElementName` + `NameScope.FindEnclosing` (guarded walk) + deferred resolution on attach; `UpdateSourceTrigger.LostFocus` (gated on S3's event landing); `BindingDiagnostics.Explain`; namescope conformance tests (content-child-vs-part-names case).
- **B2 — templates & compiled runtime lane** *(with the template engine phase)*: `TemplateBinding` fast path + descriptor validation + the typed bridge; `Detach`-eviction conformance tests against the `ValueFrame` kit; `Binding.Compiled` expression-tree analysis + typed push lane + anchored-compiled cases.
- **B3 — generator handshake** *(with X4)*: generator-emitted `CompiledBinding` descriptors, `x:DataType` build-time path diagnostics. No engine changes — second producer only.
- **Deferred, recorded:** **MultiBinding/PriorityBinding** (no v1 consumer in the control set; `BindingBase.CreateExpression` is the seam — a MultiBinding is N child watch-legs + an `IMultiValueConverter` over the same pipeline; re-addable additively). **INotifyDataErrorInfo validation** (terminal forms are a later concern — same stance as Fork A's "no data-validation plumbing in v1"; seam reserved: `BindingStatus` + a future `IBindingValidationSink` hooked at the §3.6 write-back, plus a `:data-error` pseudo-class registered by controls when it lands). **`Delay`** (debounce needs a clock; clocks are storyboard-subsystem property — revisit with S-animation). **Typed `IValueConverter<TIn,TOut>`** (perf nicety; boxed converters acceptable at install-grade frequency). **Weak-subscription backstop mode** (only if real consumer lifecycles demand it; see §3.1). **Multi-argument indexers / path casts** (no demonstrated need). **Collection views** (items-control subsystem's design space; engine stays collection-blind).

---

## 8. Open questions

1. **`FindAncestor` tree: logical-only, or a visual-tree mode too?** *Recommendation:* logical tree only in v1, with the part→templated-parent hop making template-internal ancestor bindings useful; record a `RelativeSourceMode.FindVisualAncestor` as re-addable if panel-generated intermediate elements (e.g. items hosts) prove to break expectations. Keeps the engine decoupled from S1's visual-tree internals.
2. **Who owns `IUiDispatcher`?** This engine needs only `CheckAccess`/`Post`-before-layout **plus the pinned loop-wake guarantee** (§4). *Recommendation:* the app-model/window subsystem owns the concrete dispatcher and frame-drain ordering; the two-method interface ships in `Cursorial.UI` (declared by this spec if no one else claims it) so S2 is testable with a fake. Needs a one-line confirmation — including the wake clause — in the app-model spec.
3. ~~Install-over-existing-local-binding semantics~~ — **resolved** (§2.3): replace-and-dispose at the LocalValue lane, implemented by the engine via the expression registry (the engine disposes the old expression — and thereby its entry — before installing the new; no S1 store change needed). Frame-hosted installs are exempt (frames stack by design). Consistent with the pinned Avalonia value-coexistence model, which governs *values*, not expression bookkeeping.

---

## Critique disposition

1. **ACCEPTED (P0).** Frame-hosted/template-local entries are now a named contract: `BindInFrame<T>`/`BindInFrameUntyped` on `UIObject` (§4), `Install(target, property, binding, ValueFrame hostFrame)` (§2.3), free-standing `Bind` restricted to LocalValue (Style-slot contributions must be frame-hosted), styling/template PROVIDES rewritten to pass real frames. Flagged for S1 countersignature.
2. **ACCEPTED (P0).** DataContext-as-target special case specified (§2.5, §3.4): default-source bindings targeting `DataContextProperty` anchor to the logical parent's DataContext, re-anchored on reparent, parked until attach; added to the B0 oracle matrix.
3. **ACCEPTED (P0).** Resolved in direction (a) — Avalonia coexistence, grounded in the canonical proposal §2.2 (unamended by DECISIONS): `SetValue` is a transient override, `ClearValue` is the documented kill. §3.6 rewritten accordingly; the `SetCurrentValue` control-author recommendation survives with corrected rationale (frame-hosted bindings would be shadowed by LocalValue writes; in-place replacement composes at any priority). Open Q3 re-derived on the new basis and resolved.
4. **ACCEPTED (P0).** Teardown sweep is now an explicit REQUIRES with named APIs: `ValueStore.TearDown()` (S1) + `BindingOperations.TearDown(element)` (this engine), called by the tree/window fork on permanent detach (§3.1, §4). The strong-subscription premise now rests on contract, not assumption.
5. **ACCEPTED.** New `AnchoredBinding` base carries Source/ElementName/RelativeSource; both `Binding` and `CompiledBinding<,>` inherit it (typed root check covers anchor/type mismatch); `TemplateBinding` deliberately does not (fixed anchor), which also narrows finding 15's surface.
6. **ACCEPTED.** `OneWayToSource` write-back re-resolves the chain from the anchor on every write (≤4 hops); the anchor observer is explicitly retained (§3.3, §3.6).
7. **ACCEPTED.** Reverse lane fully specified (§3.6): (a) general no-converter conversion (assignable → IConvertible/enum → `XamlConverters` against the leaf type); (b) StringFormat reverse parse restricted to exactly `"{0}"`, composite formats trace `ConvertBackFailed`; (c) `TargetNullValue` reverse mapping (target equals it ⇒ write null).
8. **ACCEPTED.** `_lastPushedValue` (per property comparer; typed in the compiled lane) suppresses write-back of resurfaced own values — covers animation-handle disposal and any deferred store-side echo; harmlessness argument included (§3.6 step 2).
9. **ACCEPTED.** `FindAncestor` (anchor resolution + attach/detach re-resolution) pulled into B0; justified by its minimal dependencies (LogicalParent + attach events, already required by inheritance wiring). Fork B's numbered requirement is whole at spine.
10. **ACCEPTED.** §3.11 added: `Disposing`/`Disposed` flags, post-`SetValue` re-checks, handler-entry guards, eviction-initiated dispose skips `entry.Dispose()`; S1 REQUIRES pins `BindingEntryBase.Dispose()` idempotent and legal from `OnEvicted`.
11. **ACCEPTED.** `IUiDispatcher.Post` MUST wake the frame loop (schedule a drain if none pending; `InteractiveDemo.Invalidate()` pattern named) — §3.8 + §4 + Open Q2.
12. **ACCEPTED.** DirectProperty expressions register in the (now release-build) expression registry and die in the teardown sweep on tree-attached elements; non-element `UIObject` targets are documented caller-owned with leak-tracker special-casing (§3.1 table).
13. **ACCEPTED.** Attachment point pinned to Fork C §3.5: template scope on the **templated parent** (internal `TemplateNameScopeProperty`); `FindEnclosing` gains the guard (template scope consumed only when `element.TemplatedParent == A`); the content-child-resolves-document-names case is a pinned conformance test; DataTemplate instance scopes pinned to the item root's document slot (§2.6).
14. **ACCEPTED.** `EffectiveMode`: TwoWay from metadata at install; degrade to OneWay with one-time Warning at wiring if the leaf is read-only; re-evaluated per rewire (§3.6).
15. **ACCEPTED.** `TemplateBinding.CreateExpression` validates: non-OneWay `Mode` / non-default `UpdateSourceTrigger` **throw**; pipeline members are honored but forfeit the typed fast path. The untyped→typed bridge is named: internal virtuals on `UIProperty` overridden by `StyledProperty<T>` (double dispatch, no reflection) — §2.1, §3.7, §4.
16. **ACCEPTED.** `Pause`/`Resume` re-specified: Resume re-resolves the anchor from scratch; rationale corrected to element-detach parking per the Fork B pin (watchers stay live while armed) — §2.3, §3.9.
17. **ACCEPTED.** (a) Ring policy pinned: Warning/Error always constructed + ring-recorded; Verbose gated on `Level`/`Trace`; sinks gated on `Level` (§2.7, §3.10). (b) `Timestamp` = `Environment.TickCount64`, monotonic ms. (c) `Explain` backed by the release-build per-target registry; mechanism and cost named (§3.11), with the registry pulling triple duty (replace-and-dispose, teardown, diagnostics).
18. **ACCEPTED.** Example fixed to `new Binding("DataContext.Tags[0]") { ElementName = "editorPane" }`, with the note that `DataContext` is reachable as a path segment via the UIPropertyAccessor lane (§2.8).
19. **ACCEPTED.** Physical-focus choice recorded as divergence §6.8 with terminal-grounded rationale (commit-on-departure safety; menus end the edit gesture); terminal-level `FocusEvent { HasFocus: false }` pinned to flush, via the S3 REQUIRES.
20. **ACCEPTED.** Compiled indexer hops: `MemberName == "Item[]"` per the INPC convention; INCC subscription identical to the reflection lane (§2.1, §3.7).
21. **ACCEPTED.** S1 REQUIRES gains the short-name → candidate-owner-types registry query with ambiguity surfaced (§2.4, §4).
22. **ACCEPTED.** Pinned with S1 (countersign required): `SetCurrentValue` observer args carry the priority of the replaced lane — `Animation` while animated (write visible until the next animation frame; filtered from write-back, consistent with §6.5), else the base lane's priority (§3.6 step 3, §4).
23. **ACCEPTED.** `GetBindingExpression` returns the LocalValue-lane expression only (null otherwise); `Explain` covers all lanes (§2.3).
24. **ACCEPTED (all four nits).** (a) §3.8 reworded: per-node bitmask, drain from lowest set bit. (b) Extension renamed `SetBinding` (WPF kinship; no overload ambiguity with `UIObject.Bind<T>` producer handles); `Install`'s now-pointless priority parameter dropped in the same stroke (LocalValue implied; frames carry provenance). (c) `UnsetTargetProperty` = static internal sentinel via an internal `UIProperty` ctor (Id −1, unregistered) — §2.3, §4. (d) Whole-window DataContext-swap cost stated with the `DeferNotifications` pointer (§3.4).