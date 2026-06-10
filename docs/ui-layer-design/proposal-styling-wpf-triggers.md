# Cursorial.UI Styling System — Fork B Proposal: Styles, Setters, and WPF-Style Triggers

---

## 1. Executive summary & philosophy

This fork proposes a **WPF-faithful styling model**: `Style` objects carrying `Setters` and a full trigger taxonomy (`Trigger`, `MultiTrigger`, `DataTrigger`, `MultiDataTrigger`, `EventTrigger`), with `BasedOn` inheritance, implicit/explicit attachment, tree-scoped `ResourceDictionary` lookup with theme variants, and template-internal styling via `ControlTemplate.Triggers` + `TemplateBinding`.

**Philosophy in three sentences.** A trigger is an *object*, not a string: it is validated when the style seals, navigable in a debugger, and its activation state is a bit you can inspect — on a platform debugged over SSH with no devtools, that is the difference between "read the provenance report in a debug overlay" and "stare at a selector string wondering why it didn't match." Interactive state (`:pointerover`, `:focus`, `:pressed`) is not a closed pseudo-class grammar but ordinary read-only styled properties, so *any* property — including terminal-specific ones like color depth, access-key visibility, or a view-model flag via `DataTrigger` — participates in styling with one uniform mechanism. Everything a style or trigger injects enters the property system through a tagged, prioritized value slot and is retractable to byte-identical prior state — the system's named invariant.

**The spine (§0 invariant, following project convention):** *No styling mechanism ever writes a local value. Every value injected by a setter, trigger, template, or storyboard carries a `(ValuePriority, sub-priority)` provenance tag; removal of the contributor restores the exact prior effective value with no residue.* Every later section is checked against this.

A second structural rule: **seal-once, share-everywhere.** All per-style data (flattened setter tables, trigger watch maps, boxed values, converted comparands) is computed once at `Seal()` and shared immutably by every element using the style. Per-element state is bits and tokens only. This is what makes a 200-element restyle cheap and the steady state allocation-free.

---

## 2. Public API sketch

All types in namespace `Cursorial.UI.Styling` unless noted. `StyledProperty` / `StyledProperty<T>`, `UIElement`, `RoutedEvent`, `IBinding` come from Fork A's property/binding system (contract in §5); `Storyboard` from the animation-orchestration module.

### 2.1 Value priority slots (shared vocabulary with Fork A)

```csharp
public enum ValuePriority : byte
{
    Default         = 0,   // property metadata default
    Inherited       = 1,   // property-inheritance (Fork A)
    ThemeSetter     = 2,   // theme (default) style setters
    ThemeTrigger    = 3,   // theme style triggers
    StyleSetter     = 4,   // element's Style setters
    StyleTrigger    = 5,   // element's Style triggers
    TemplateSetter  = 6,   // values the owning ControlTemplate sets on its parts (incl. TemplateBinding)
    TemplateTrigger = 7,   // ControlTemplate.Triggers setters on parts
    LocalValue      = 8,   // element.SetValue / XAML attribute
    Animation       = 9,   // storyboard holders
}

/// SubPriority orders contributors within a slot: flattened setter/trigger index.
/// Higher (Priority, SubPriority) wins; within a trigger slot, the later trigger
/// in the (flattened) collection wins — the WPF rule.
public readonly record struct ValueSlot(ValuePriority Priority, ushort SubPriority);
```

**Deliberate deviation from WPF:** WPF interleaves style triggers above template triggers *on the templated parent* and has a separate "ParentTemplate" slot for children. Here, `TemplateSetter/TemplateTrigger` slots are used **only on template part elements** (the template owns its parts; the control author's template triggers beat the part's own style), and the templated parent itself only ever receives Theme/Style/Local/Animation values. One table, no asterisks. `StyleDiagnostics` (§2.8) makes the table observable, which is the real fix for precedence confusion.

### 2.2 Style and setters

```csharp
public sealed class Style
{
    public Style();
    public Style(Type targetType, Style? basedOn = null);

    public Type TargetType { get; set; }              // mutation after Seal() throws
    public Style? BasedOn { get; set; }               // must target same type or base
    public SetterCollection Setters { get; }          // collection-initializer friendly
    public TriggerCollection Triggers { get; }
    public ResourceDictionary? Resources { get; set; } // style-scoped resources (lookup §2.5)
    public bool IsSealed { get; }
    public void Seal();   // idempotent; validates, flattens BasedOn chain, compiles (§3.1).
                          // Called automatically on first attachment to an element.
}

public abstract class SetterBase
{
    public bool IsSealed { get; }
    internal abstract void Seal(StyleSealContext context);
}

public sealed class Setter : SetterBase
{
    public Setter();
    public Setter(StyledProperty property, object? value);
    public Setter(StyledProperty property, object? value, string? targetName);

    public StyledProperty Property { get; set; }
    public object? Value { get; set; }      // literal | DynamicResourceValue | IBinding
    public string? TargetName { get; set; } // valid only inside ControlTemplate triggers
}

public sealed class EventSetter : SetterBase   // Phase 3
{
    public RoutedEvent Event { get; set; }
    public Delegate Handler { get; set; }
    public bool HandledEventsToo { get; set; }
}
```

Setter values may be:
- a **literal**, type-converted and boxed once at seal (a `Pen`, an `IBrush`, `TextAttributes`, `Margins` — all the terminal-native immutable value vocabulary);
- a **`DynamicResourceValue`** — a deferred, change-tracked resource reference;
- an **`IBinding`** — instantiated per element, value flows into the setter's slot (Phase 2; requires Fork A binding).

### 2.3 Triggers — the full taxonomy

```csharp
public abstract class TriggerBase
{
    public TriggerActionCollection EnterActions { get; }  // run on false→true (after setters apply)
    public TriggerActionCollection ExitActions { get; }   // run on true→false (after setters retract)
    public bool IsSealed { get; }
    internal abstract void Seal(StyleSealContext context);
}

/// Property trigger: condition is (source.Property == Value).
public sealed class Trigger : TriggerBase
{
    public Trigger();
    public Trigger(StyledProperty property, object? value);

    public StyledProperty Property { get; set; }
    public object? Value { get; set; }        // converted to Property.PropertyType at seal
    public string? SourceName { get; set; }   // template scope only: watch a named part instead of self
    public SetterCollection Setters { get; }
}

public sealed class Condition
{
    public Condition();
    public Condition(StyledProperty property, object? value, string? sourceName = null);
    public Condition(IBinding binding, object? value);

    public StyledProperty? Property { get; set; }  // exactly one of Property / Binding
    public IBinding? Binding { get; set; }
    public object? Value { get; set; }
    public string? SourceName { get; set; }
}

public sealed class MultiTrigger : TriggerBase        // AND of property conditions
{
    public ConditionCollection Conditions { get; }
    public SetterCollection Setters { get; }
}

public sealed class DataTrigger : TriggerBase         // condition on a binding (view-model state)
{
    public required IBinding Binding { get; set; }
    public object? Value { get; set; }
    public SetterCollection Setters { get; }
}

public sealed class MultiDataTrigger : TriggerBase    // AND of binding conditions
{
    public ConditionCollection Conditions { get; }
    public SetterCollection Setters { get; }
}

/// Fire-and-forget: runs Actions on a routed event; owns no setters, retracts nothing.
public sealed class EventTrigger : TriggerBase
{
    public EventTrigger();
    public EventTrigger(RoutedEvent routedEvent);

    public RoutedEvent RoutedEvent { get; set; }
    public string? SourceName { get; set; }
    public TriggerActionCollection Actions { get; }
}
```

Semantics, pinned:
- Condition test is `Equals(currentValue, comparand)` with the comparand pre-converted to the property type at seal (binding comparands convert lazily on first compare, then cache). All Cursorial style primitives (`Color`, `Pen`, `Style`, `Margins`) are records — value equality works naturally.
- Activation order: **EnterActions → setters apply**; deactivation: **setters retract → ExitActions**. Initial evaluation at attach applies setters and runs EnterActions if the condition already holds (so a `Loaded`-time true condition behaves like WPF).
- When two active triggers in the same scope set the same property, the **later trigger in the flattened collection wins** (sub-priority = flattened trigger index). Derived-style triggers flatten after base-style triggers, so derived wins — consistent with setter flattening.
- Trigger setters enter at the scope's trigger slot (`StyleTrigger` / `ThemeTrigger` / `TemplateTrigger`); retract = holder removal; the property system recomputes the effective value from remaining entries (§0 invariant — no "restore saved old value" bugs, ever).

```csharp
public abstract class TriggerAction
{
    public abstract void Invoke(in TriggerActionContext context);
}

public readonly struct TriggerActionContext
{
    public UIElement Element { get; }      // the styled element / templated parent
    public UIElement? Source { get; }      // SourceName-resolved part, when applicable
    public INameScope? NameScope { get; }  // template namescope, when in a template
}

public sealed class BeginStoryboard : TriggerAction
{
    public string? Name { get; set; }                 // handle for StopStoryboard
    public required Storyboard Storyboard { get; set; }
    public HandoffBehavior Handoff { get; set; } = HandoffBehavior.SnapshotAndReplace;
}

public sealed class StopStoryboard : TriggerAction
{
    public required string BeginStoryboardName { get; set; }
}
```

`Storyboard` values are applied at `ValuePriority.Animation` through the same holder seam, so a stopped storyboard retracts exactly like a deactivated trigger. (Storyboard internals belong to the animation-orchestration module; the contract is in §5.)

### 2.4 Interactive-state properties (the pseudo-class surface)

There is no pseudo-class grammar. Interactive state is **read-only styled properties** maintained by the input/focus fork (contract §5):

| WPF/Avalonia state | Property (owner) | Source of truth |
|---|---|---|
| `:pointerover` | `UIElement.IsPointerOverProperty` | hit-test enter/leave from `MouseEventKind.Move` (requires `MouseCapabilities.Motion`) |
| `:pressed` | `ButtonBase.IsPressedProperty` | ButtonDown + implicit capture; cleared on release/`FocusEvent{HasFocus:false}` |
| `:focus` (physical) | `UIElement.IsFocusedProperty` | focus manager (keyboard focus) |
| focus-within (logical) | `UIElement.IsKeyboardFocusWithinProperty` | focus manager, inherited-ish maintained up the chain |
| `:disabled` | `UIElement.IsEnabledProperty` (false) | coerced AND of self + ancestors (Fork A coercion) |
| `:checked` | `ToggleButton.IsCheckedProperty` | control logic |
| window active | `Window.IsActiveProperty` | `FocusEvent` (DECSET 1004) |
| access keys visible | `AccessKeyManager.ShowAccessKeysProperty` (attached, inherited) | Alt down/up on Kitty/Win32 paths; pinned `true` otherwise (req. 6) |

Because these are ordinary properties, the trigger mechanism needs **no special cases** — and the state vocabulary is open: a control or app can add `IsDropTarget`, `ValidationState`, `Density.Compact` and style against them identically.

### 2.5 Resources, lookup, themes

```csharp
public class ResourceDictionary : IDictionary<object, object?>
{
    public IList<ResourceDictionary> MergedDictionaries { get; }
    public IDictionary<ThemeVariant, ResourceDictionary> ThemeDictionaries { get; }

    public bool TryGetResource(object key, in ThemeVariant variant, out object? value);
    public int Version { get; }              // bumped on any mutation, incl. merged/theme children
    public event EventHandler? Invalidated;  // coalesced; subscribers re-resolve their keys
}

public sealed class DynamicResourceValue          // what {DynamicResource K} parses to; usable in code
{
    public DynamicResourceValue(object key);
    public object Key { get; }
}

public readonly record struct ThemeVariant(ThemeBase Base, ColorDepth Depth)
{
    // Base from OSC-11 DefaultBackground luminance; Depth from ColorCapabilities.Depth.
    public static ThemeVariant FromCapabilities(TerminalCapabilities capabilities);
    public static ThemeVariant Dark { get; }      // (Dark, Truecolor)
    public static ThemeVariant Light { get; }
}
public enum ThemeBase : byte { Dark = 0, Light = 1 }

public sealed class ThemeManager                  // app-level singleton, owned by Application
{
    public ThemeVariant Variant { get; set; }     // initialized from negotiated capabilities
    public event EventHandler? VariantChanged;    // drives a tree-wide resource invalidation
}
```

**Lookup order** for `FindResource` / `StaticResource` / `DynamicResource`, from an element:
1. `element.Resources` (theme-variant-resolved: exact `(Base, Depth)` → `(Base, Truecolor)` → `(Base, any)` → plain dictionary);
2. the element's `Style.Resources` (then its `BasedOn` chain's);
3. if inside a template instance: the template's `Resources` (consulted at the templated-parent boundary);
4. logical parent chain, repeating 1–3 per ancestor;
5. `Window.Resources` → `Application.Resources` (incl. theme dictionaries) → built-in default theme.

`StaticResource` resolves once at parse/construction time. `DynamicResource` installs a subscription (§3.4): when any dictionary in the element's scope chain changes (or the theme variant flips), only the subscribed keys re-resolve and only changed values propagate.

**Attachment** (requirement 1/3 surface on the element — these members are this fork's contribution to Fork A's `UIElement` partial):

```csharp
public partial class UIElement
{
    public static readonly StyledProperty<Style?> StyleProperty;     // explicit style
    public Style? Style { get; set; }
    public ResourceDictionary Resources { get; set; }               // lazily allocated
    protected internal object DefaultStyleKey { get; set; }         // defaults to GetType(); theme + implicit lookup key

    public object? FindResource(object key);                        // throws ResourceNotFoundException
    public bool TryFindResource(object key, out object? value);
    public void InvalidateStyles();   // re-resolve implicit style + dynamic resources for the subtree
}
```

- **Explicit:** `element.Style = ...` or `Style="{StaticResource DangerButton}"`.
- **Implicit by type:** on logical-tree attach, look up `DefaultStyleKey` (exact type, WPF-faithful) through the resource chain; first hit wins and applies at the Style slots.
- **Theme style:** same key resolved in `Application` theme dictionaries; applies at the Theme slots, always *under* an implicit/explicit style. Both can be active simultaneously — slots keep them ordered.

### 2.6 Templates

```csharp
public class FrameworkTemplate
{
    public ITemplateContent? Content { get; set; }
    public TriggerCollection Triggers { get; }        // ControlTemplate.Triggers — same TriggerBase types,
                                                      //   plus TargetName/SourceName resolution against the namescope
    public ResourceDictionary? Resources { get; set; }
    public bool IsSealed { get; }
    public void Seal();
    public TemplateInstance Instantiate(UIElement owner);
}

public sealed class ControlTemplate : FrameworkTemplate
{
    public ControlTemplate();
    public ControlTemplate(Type targetType);
    public Type TargetType { get; set; }
}

public sealed class DataTemplate : FrameworkTemplate
{
    public Type? DataType { get; set; }   // implicit data-template key (ContentPresenter lookup)
}

public interface ITemplateContent { UIElement Build(in TemplateFactoryContext context); }

public static class TemplateContent
{
    public static ITemplateContent FromFactory(Func<TemplateFactoryContext, UIElement> factory); // code-behind
    // The XAML fork supplies the deferred-node-tree implementation for markup templates.
}

public readonly struct TemplateFactoryContext
{
    public UIElement Owner { get; }
    public INameScope NameScope { get; }
    public void Register(string name, UIElement element);
}

public sealed class TemplateInstance
{
    public UIElement Root { get; }
    public INameScope NameScope { get; }
    public void Detach();   // retracts all TemplateSetter/TemplateTrigger values, disposes bindings/subscriptions
}

public interface INameScope { UIElement? Find(string name); }

public static class TemplateBindingExtensions
{
    // One-way templated-parent binding; value lands at ValuePriority.TemplateSetter on the part.
    public static void SetTemplateBinding(this UIElement part, StyledProperty target, StyledProperty source);
}
```

Template triggers watch the templated parent by default; `Trigger.SourceName` watches a named part; `Setter.TargetName` sets on a named part. Both resolve through the instance's `INameScope` at attach. Template-internal styling = template `Resources` (step 3 of lookup) + template triggers; parts may also carry their own styles, which template triggers out-prioritize by slot design.

### 2.7 Consumer example

Code-first (the terminal-flavored part is real: focus indication by **stroke weight**, which works at `ColorDepth.NoColor`):

```csharp
var accent = Color.FromHex("#66d9ef");

var buttonStyle = new Style(typeof(Button))
{
    Setters =
    {
        new Setter(Button.BackgroundProperty,     new DynamicResourceValue(ThemeKeys.ControlBackground)),
        new Setter(Button.ForegroundProperty,     Brushes.LightWhite),
        new Setter(Button.BorderPenProperty,      Pens.Light),
        new Setter(Button.PaddingProperty,        new Margins(2, 0)),
    },
    Triggers =
    {
        new Trigger(UIElement.IsPointerOverProperty, true)
        {
            Setters = { new Setter(Button.BackgroundProperty, new SolidColorBrush(accent, opacity: 0.25)) },
        },
        new Trigger(Button.IsPressedProperty, true)
        {
            Setters =
            {
                new Setter(Button.BackgroundProperty,     new SolidColorBrush(accent, opacity: 0.5)),
                new Setter(Button.TextAttributesProperty, TextAttributes.Bold),
            },
        },
        new Trigger(UIElement.IsFocusedProperty, true)
        {
            // Works on 16-color and monochrome terminals: focus = heavy box glyphs, not color.
            Setters = { new Setter(Button.BorderPenProperty, Pens.Heavy) },
        },
        new MultiTrigger
        {
            Conditions =
            {
                new Condition(UIElement.IsFocusedProperty, true),
                new Condition(UIElement.IsPointerOverProperty, true),
            },
            Setters = { new Setter(Button.BorderPenProperty, Pens.Heavy.WithCorners(CornerStyle.Rounded)) },
        },
        new Trigger(UIElement.IsEnabledProperty, false)
        {
            Setters =
            {
                new Setter(Button.ForegroundProperty,     Brushes.LightBlack),
                new Setter(Button.TextAttributesProperty, TextAttributes.Faint),
            },
        },
    },
};

window.Resources[typeof(Button)] = buttonStyle;   // implicit for every Button in the window
```

XAML (parse-time validated — `Property="IsPointerOver"` resolves against `TargetType`, `Value="True"` converts against the property type at seal):

```xml
<Style x:Key="DangerButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
  <Setter Property="Background" Value="{DynamicResource Brush.Danger}" />
  <Style.Triggers>
    <Trigger Property="IsPointerOver" Value="True">
      <Setter Property="Background" Value="#7f1d1d" />
    </Trigger>
    <DataTrigger Binding="{Binding HasPendingChanges}" Value="True">
      <Setter Property="TextAttributes" Value="Bold,Underline" />
    </DataTrigger>
  </Style.Triggers>
</Style>

<ControlTemplate x:Key="ButtonTemplate" TargetType="Button">
  <Border x:Name="PART_Border"
          BorderPen="{TemplateBinding BorderPen}"
          Background="{TemplateBinding Background}"
          Padding="{TemplateBinding Padding}">
    <AccessText Text="{TemplateBinding Content}" />
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsPressed" Value="True">
      <Setter TargetName="PART_Border" Property="Background"
              Value="{DynamicResource Brush.Accent.Pressed}" />
    </Trigger>
    <EventTrigger RoutedEvent="Button.Click">
      <BeginStoryboard>
        <Storyboard>
          <BrushTrack Storyboard.TargetName="PART_Border"
                      Storyboard.TargetProperty="Background"
                      From="{StaticResource Brush.Flash}" To="{StaticResource Brush.Accent}"
                      Duration="0:0:0.15" Easing="QuadOut" />
        </Storyboard>
      </BeginStoryboard>
    </EventTrigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
```

The `IBrush` type converter delegates to the existing `BrushMarkup` inline grammar (`"linear:#f92672,#66d9ef"` works as a setter value); `Pen` gets a small converter (`"Heavy Rounded"`); `Color` uses `Color.FromHex`.

### 2.8 Diagnostics (first-class, not an afterthought)

```csharp
public static class StyleDiagnostics
{
    public static EffectiveValueReport GetValueSource(UIElement element, StyledProperty property);
    public static IReadOnlyList<TriggerState> GetTriggerStates(UIElement element);
}

public readonly record struct EffectiveValueReport(
    ValuePriority Priority, object? Value,
    object? Contributor,        // the Style / FrameworkTemplate / Storyboard that injected it
    int TriggerIndex,           // -1 when not trigger-sourced
    object? ResourceKey);       // non-null when the value came via DynamicResource

public readonly record struct TriggerState(
    object Origin, TriggerBase Trigger, bool IsActive, string Description);
    // Description e.g. "Trigger #2: IsPointerOver == True (active)"
```

This powers an in-terminal **style inspector overlay** (a debug panel listing, for the element under the cursor, every property whose value isn't default, with its slot, contributor, trigger index, and resource key). Because triggers are objects with indices and bits, this report is exact and cheap — the debuggability claim made concrete.

---

## 3. Internal architecture

### 3.1 Seal-time compilation (per style, shared)

`Style.Seal()` (auto-invoked on first attach) produces an internal `CompiledStyle`:

```csharp
internal sealed class CompiledStyle
{
    public readonly Type TargetType;
    public readonly CompiledSetter[] Setters;     // BasedOn chain flattened base-first; per-property
                                                  // last-wins already resolved (derived overrides base)
    public readonly CompiledTrigger[] Triggers;   // base-first; array index == in-slot sub-priority
    public readonly PropertyWatchMap Watch;       // property GlobalIndex → packed (triggerIdx, conditionIdx)[]
                                                  //   partitioned by source-name index for template scopes
    public readonly string[] SourceNames;         // interned name table (templates only)
    public readonly int MultiConditionSlots;      // sizes the per-element met-count array
    public readonly bool HasDataConditions;       // binding-backed conditions present
}

internal readonly struct CompiledSetter
{
    public readonly StyledProperty Property;
    public readonly object? Value;            // converted + boxed ONCE here; never re-boxed per element
    public readonly SetterValueKind Kind;     // Literal | DynamicResource | Binding
    public readonly short TargetNameIndex;    // -1 = self
}

internal sealed class CompiledTrigger
{
    public readonly CompiledCondition[] Conditions;   // length 1 for Trigger/DataTrigger
    public readonly CompiledSetter[] Setters;
    public readonly TriggerAction[] EnterActions, ExitActions;
}

internal readonly struct CompiledCondition
{
    public readonly StyledProperty? Property;  // null ⇒ binding condition
    public readonly IBinding? Binding;
    public readonly object? Comparand;         // converted to PropertyType at seal when possible
    public readonly short SourceNameIndex;     // -1 = self / templated parent
}
```

Seal validates: `TargetType` compatibility of every property; `BasedOn.TargetType` assignability; value conversion (via the XAML fork's converter service when available, `Convert.ChangeType`/`Enum.Parse` fallback); `TargetName`/`SourceName` only inside templates; `DataTrigger.Binding` non-null. **Errors surface at load with the style, trigger index, and property named** — not at first hover.

`FrameworkTemplate.Seal()` compiles its trigger collection identically, with name indices resolved per-instance against the namescope at attach.

### 3.2 Per-element state: `StyleScope`

An element carries up to three scopes — theme, style, template (template scopes live on the `TemplateInstance` and target parts). Allocated only when the corresponding compiled style exists; trigger arrays allocated only when triggers exist.

```csharp
internal sealed class StyleScope : IPropertyChangeSink
{
    private readonly UIElement _host;
    private readonly CompiledStyle _style;
    private readonly ValuePriority _setterSlot;     // ThemeSetter | StyleSetter | TemplateSetter
    private readonly ValuePriority _triggerSlot;    // the matching trigger slot
    private readonly INameScope? _names;            // template scopes only

    private ulong _activeBits0;                     // 1 bit per trigger; ulong[] overflow for >64 (rare)
    private byte[]? _metCounts;                     // per multi-trigger satisfied-condition counts
    private IBindingExpression[]? _conditionExprs;  // data-trigger conditions, lazily on DataContext
    private IDisposable[]? _subscriptions;          // dynamic-resource + binding setter values

    public void Attach();    // apply setters → register listeners → initial trigger evaluation
    public void Detach();    // clear all values by (slot, this) → dispose subscriptions → unregister
    void IPropertyChangeSink.OnPropertyChanged(UIElement source, StyledProperty p, object? oldV, object? newV);
}
```

Listener registration is **consolidated**: one `IPropertyChangeSink` registration per (scope, watched element) — not one per trigger. The scope dispatches internally through the style's shared `Watch` map (an int-keyed open-addressed map from `StyledProperty.GlobalIndex` to a packed span of `(triggerIdx, conditionIdx)` pairs, built once at seal). For template scopes with `SourceName` conditions, the scope additionally registers on each resolved part — partition tables are precomputed at seal, name resolution happens once at attach.

### 3.3 The change-notification flow, step by step (hover flip)

Mouse moves from Button A to Button B. The input fork's hit test sets two properties:

1. `A.SetValue(IsPointerOverKey, false)` → Fork A store updates the entry, raises consolidated change.
2. A's `StyleScope.OnPropertyChanged` → `Watch.TryGet(IsPointerOver.GlobalIndex)` → one array hit: trigger #0 (and the MultiTrigger #3).
3. Trigger #0: evaluate `Equals(false, true)` → now false, bit was set → **deactivate**: for each compiled setter, `store.ClearValue(Background, slot: (StyleTrigger, 0), source: scope)`. The store removes that holder; effective `Background` falls back to the next-highest entry (the `StyleSetter` dynamic-resource value). MultiTrigger #3: `_metCounts[0]` decrements 2→1 → was inactive only if previously partial; if it was active, same retraction for `BorderPen`.
4. Each effective-value change consults property metadata: `Background` is `AffectsRender` → the element's render closure marks its widget `Scene.Invalidate()` (coarse, per the drawing layer's owner-driven model) and requests a frame.
5. Same sequence on B for `true` (apply instead of retract; setter values are the seal-time boxed objects — **zero allocation**).
6. Next frame: two scenes re-raster, compositor recomposites two footprints, `FrameRenderer` diffs and emits a few hundred bytes.

Steady-state cost: ~2 map lookups, ~4 condition evaluations, ~6 holder insert/removes, 2 scene invalidations. No allocation, no tree walking, no rule matching. This is the §6 cost-model answer in miniature.

**Reentrancy:** trigger setters can change properties other triggers watch. Evaluation is synchronous and recursive with a per-element depth counter; at depth 32 the engine throws `StyleTriggerCycleException` carrying the (style, trigger-index) chain — a debuggable cycle report instead of a silent stack overflow or an unexplained final state.

### 3.4 Dynamic resources

- Every `ResourceDictionary` mutation bumps `Version` and raises `Invalidated` (coalesced per frame).
- A `DynamicResourceValue` setter (or local value) installs a subscription in a **per-window `ResourceSubscriptionRegistry`**: `key → intrusive list of (element, property, ValueSlot)`. Subscribing is O(1); the registry is the only styling structure that outlives a frame besides scopes.
- On invalidation (dictionary change anywhere in the window, or `ThemeManager.VariantChanged`): the registry walks its entries (optionally filtered by changed key when known), re-resolves each through *the subscribing element's own* scope chain (shadowing stays correct), and re-sets the holder only when the resolved value changed. A theme-variant flip touching 60 brush keys across 200 elements is a few thousand dictionary probes plus only-changed holder writes — well under a millisecond; the unavoidable cost is the re-raster, which any styling model pays.
- `Detach`/element removal disposes subscriptions (debug builds keep a leak tracker asserting registry emptiness after tree teardown).

### 3.5 Template instantiation & template triggers

`Control.Template` change → old `TemplateInstance.Detach()` (retract every `TemplateSetter`/`TemplateTrigger` holder on parts — parts are being discarded anyway, but detach also unhooks listeners from the templated parent) → `Instantiate(owner)`: build content via `ITemplateContent`, populate namescope, install `TemplateBinding`s (holders at `TemplateSetter`), attach the template `StyleScope` (parent + named-part listeners), run initial trigger evaluation. `DataTemplate` works identically with `DataContext` as the binding anchor; implicit data templates resolve by `DataType` through the same resource lookup (§2.5).

### 3.6 Memory layout summary

- Per **style**: one `CompiledStyle` (arrays + watch map), shared by all elements, immutable after seal — safe even across windows (everything runs on the single UI/render thread regardless).
- Per **element**: 0–3 `StyleScope` objects; a scope with ≤64 triggers and no data conditions is ~64 bytes. 200 elements ≈ tens of KB total styling overhead.
- Setter values boxed once at seal; trigger comparands likewise; holder entries in Fork A's store are the only per-element value cost (assumed pooled/inline per Fork A's design, see §5).

---

## 4. Requirement satisfaction

**Req 1 — Rich styling and templating.** `Style` + `Setter` over the property system; implicit-by-type, explicit-by-key, and theme attachment; lookless controls via `ControlTemplate` with `TemplateBinding` and named parts; `DataTemplate` with implicit `DataType` resolution; template-scoped resources and triggers. Styles set the full terminal styling vocabulary (`IBrush`, `Pen`, `TextAttributes`, `Margins`, `Color`) — all immutable values that are safe to share boxed.

**Req 3 — Resource/style inheritance.** Three orthogonal axes, all covered: (a) **style→style**: `BasedOn` chains, flattened at seal (derived setters/triggers override base by sub-priority); (b) **tree-scoped resources**: element → style → template → ancestors → window → application → theme, with `StaticResource` (resolve-once) and `DynamicResource` (subscribed, invalidation-driven), merged dictionaries, and `ThemeVariant` dictionaries; (c) **property value inheritance** (ambient foreground/text attributes down the tree) via Fork A's `Inherits` metadata at the `Inherited` slot — styling composes with it correctly because slots order it below every styling source.

**Req 8 — Setters paired with WPF Triggers (assigned angle).** The full taxonomy is specified with pinned semantics: property `Trigger` (+ `SourceName` in templates), `MultiTrigger` (AND, met-count evaluation), `DataTrigger`/`MultiDataTrigger` (binding conditions), `EventTrigger` (+ `BeginStoryboard`/`StopStoryboard`), `EnterActions`/`ExitActions` on every trigger, `ControlTemplate.Triggers` with `TargetName` setters, trigger-slot priorities with clean retraction (§0 invariant), in-collection precedence, cycle detection, and load-time validation.

**Supporting roles.** Req 6: access-key underlines are *just a trigger* on `AccessKeyManager.ShowAccessKeysProperty` in the theme's `AccessText` template — capability policy lives in the input fork, presentation in styles. Req 9: this design is the primary consumer of the priority-slot architecture and fixes its slot table. Req 10: `EventTrigger`/Enter/ExitActions are the declarative ignition for storyboards; animation holders share the retract seam. Req 7: triggers are plain object trees — the XAML story is §2.7 with no extra grammar.

---

## 5. Cross-fork contract

### 5.1 Required from Fork A (property system + binding; reqs 2, 9)

```csharp
// Implemented by UIElement's property store.
public interface IPropertyValueStore
{
    object? GetValue(StyledProperty property);

    // Holder model: one entry per (slot, source). Re-setting the same (slot, source, property)
    // replaces in place. Effective value = max (Priority, SubPriority); change notification fires
    // only when the effective value actually changes.
    void SetValue(StyledProperty property, object? value, in ValueSlot slot, object source);
    void ClearValue(StyledProperty property, in ValueSlot slot, object source);

    // Consolidated listener: one registration per sink; sink receives every effective-value change.
    IDisposable RegisterChangeSink(IPropertyChangeSink sink);
}

public interface IPropertyChangeSink
{
    void OnPropertyChanged(UIElement source, StyledProperty property, object? oldValue, object? newValue);
}
```

Also required: `StyledProperty` exposes `int GlobalIndex` (small dense int for watch maps), `Type PropertyType`, metadata (`DefaultValue`, `Inherits`, `AffectsRender`/`AffectsMeasure`, coercion), and **read-only property keys** (pseudo-states must not be settable at `LocalValue` by apps). Storage assumption: per-element sparse map upgraded to a small multi-entry stack only when ≥2 sources exist on a property — styling's holder churn must not allocate in steady state. Binding: `IBinding { IBindingExpression Instantiate(UIElement anchor); }`, `IBindingExpression { object? Value; event Action? ValueChanged; void Dispose(); }`, plus templated-parent binding support for `TemplateBinding`. `DataContext` is an inherited property.

### 5.2 Required from Fork C (input, focus, windows, access keys; reqs 4–6)

- Maintain the pseudo-state property table of §2.4 via read-only keys, with the documented hygiene: `IsPointerOver` from hit-test enter/leave; `IsPressed` with capture; transient states cleared on `FocusEvent { HasFocus: false }` (Alt-Tab swallows releases); `Window.IsActive` from focus reports; `AccessKeyManager.ShowAccessKeys` per the capability matrix (Kitty `ReportEventTypes + ReportAllKeysAsEscapeCodes` or Win32 input mode ⇒ toggles with Alt; otherwise pinned `true`).
- Routed events (`RoutedEvent` registry, bubbling) for `EventTrigger`/`EventSetter`; `Loaded`/`Unloaded`; logical-tree attach/detach notifications (styling hooks implicit-style resolution and scope attach/detach there).
- Windows are resource-scope roots chained to `Application` (modal/modeless children inherit app theme; per-window `ResourceSubscriptionRegistry`).

### 5.3 Required from the XAML fork (req 7)

Markup extensions (`StaticResource`, `DynamicResource`, `TemplateBinding`, `Binding`, `x:Type`); deferred `ITemplateContent` over the parsed node tree; a type-converter service (`IBrush` via `BrushMarkup.Resolver`, `Pen`, `Color.FromHex`, `Margins`, flags enums); property-name resolution against `TargetType` including attached syntax (`theme:Density.Compact`); `x:Name` → namescope registration.

### 5.4 Provided to the other forks

`StyleHelper.ApplyStyle/DetachStyle` invoked from element lifecycle; resource lookup (`FindResource`, used by the XAML fork's `StaticResource`); template instantiation + `INameScope` (the focus fork's part lookup); `ThemeManager`; `StyleDiagnostics`; the `ValueSlot` conventions and retract invariant that the animation orchestration plugs into (`Animation` slot holders behave exactly like trigger holders).

---

## 6. Terminal-specific adaptations

1. **Capability-gated pseudo-states degrade to inert, not broken.** On a terminal without motion tracking, `IsPointerOver` simply never becomes true — hover triggers cost nothing and show nothing. The default theme is therefore **focus-first**: every hover affordance has a focus twin. `ThemeVariant` carries `ColorDepth`, so themes ship per-depth dictionaries (e.g. `Ansi16` variant swaps RGB accent brushes for palette brushes, avoiding quantizer surprises on dithered gradients).

2. **State changes are discrete glyph-vocabulary changes, and that's a feature.** A focus trigger swapping `Pens.Light → Pens.Heavy` re-renders a border as a different box-glyph family — visible at `NoColor` depth, a handful of cells in the frame diff. Triggers naturally express the terminal's discrete styling vocabulary (`TextAttributes.Bold`, `UnderlineStyle`, stroke weight) where sub-pixel CSS-style transitions don't exist anyway.

3. **Trigger → invalidation mapping respects the drawing layer's coarseness.** `AffectsRender` properties call the owning widget's `Scene.Invalidate()` (whole-scene, owner-driven — the only granularity the layer offers); `AffectsMeasure` properties schedule layout. Guidance baked into the default theme: hover/press styling prefers brush *swaps* (one re-raster) over brush *animations* (re-raster per frame); motion/fade storyboards target `CompositeParameters` (cheap re-composite of a cached raster, per the drawing doc's §7 split).

4. **Theme from negotiation.** `ThemeVariant.FromCapabilities` reads dark/light from the OSC-11 `DefaultBackground` luminance readback and depth from `ColorCapabilities`. `RenegotiateAsync` → `ThemeManager.VariantChanged` → one resource-invalidation pass (rare; full re-raster is acceptable there).

5. **Single render thread, no locks.** All styling activity (attach, triggers, resource invalidation) runs on the UI/render thread; input is marshaled there by the dispatcher fork. Debug builds assert thread affinity. Sealed styles and the boxed values inside them are immutable and shareable; brushes are thread-safe by the drawing layer's contract.

6. **SSH-grade debuggability.** The style inspector overlay (§2.8) renders provenance *in the terminal itself* — no external devtools exist or are needed. This is where explicit triggers pay rent daily: "Trigger #2: IsPointerOver == True (active) → Background" is a string the engine can produce exactly, because activation is a bit, not a re-run of a matching algorithm.

7. **Scale honesty.** At hundreds of elements there is no need for selector-style global rule indexes; per-style watch maps and per-element bits are simpler and strictly local. Per-frame styling cost in steady state is zero (no polling — everything is change-driven), which preserves the stack's "fully static frame costs ~nothing" property end-to-end.

---

## 7. Costs, risks, phasing

**Phasing** (per the project's numbered-phase playbook; each phase implemented + tested before the next):

| Phase | Scope | Size |
|---|---|---|
| S1 | `Style`/`Setter`/`BasedOn` flattening, seal pipeline, implicit/explicit/theme attachment, property `Trigger` + `MultiTrigger`, `ResourceDictionary` + `StaticResource`, `StyleDiagnostics` core | L |
| S2 | `DynamicResource` + subscription registry, `ThemeVariant`/`ThemeManager`, `ControlTemplate`/`DataTemplate` instantiation, template triggers (`TargetName`/`SourceName`), `TemplateBinding` | L |
| S3 | `DataTrigger`/`MultiDataTrigger` (needs Fork A binding), `EventTrigger` + `BeginStoryboard`/`StopStoryboard` (needs animation orchestration + routed events), `EnterActions`/`ExitActions`, `EventSetter`, `Style.Resources` | M |
| S4 | Hardening: pooled holder entries, >64-trigger overflow path, leak tracker, style-inspector overlay demo, adversarial review | M |

**Perf characteristics** (claims to pin with benchmarks in S4): hover flip ≈ a dozen map/array ops + 2 scene invalidations, zero alloc; full implicit restyle of 200 elements (theme swap) ≈ 200 × ~10 holder ops + subscription re-resolution, target < 1 ms excluding re-raster; per-element styling memory ≈ 64–150 B.

**Risks, honestly:**
- *Cross-fork sequencing.* `DataTrigger` and `EventTrigger` are hostage to binding/routed-event/storyboard delivery. Mitigated by phasing: S1 ships a fully useful system (property triggers cover the interactive-state bread and butter) with zero dependencies beyond the property store.
- *Precedence-table confusion* is WPF's most-litigated wart. Mitigated by the simplified slot table (template slots only on parts), and by making `GetValueSource` exist from day one.
- *Trigger cascades.* Cycle guard + exception with chain report; documented anti-pattern (triggers setting properties other triggers watch).
- *Seal-time conversion needs converters before the XAML fork lands.* Fallback converter (primitives, enums, `Color.FromHex`, brush markup) keeps code-first usage unblocked.
- *Template detach leaks* (subscriptions/listeners). Single-owner `Detach()` plus a debug leak tracker; this is the adversarial-review focus area.
- *Punted:* `EventSetter` until S3; styling *unnamed* descendants outside templates (see §8 concession); per-key filtered resource invalidation (S2 invalidates per-window, filters later if profiling demands); visual-state-manager-style transition groups (storyboard layer can add later, additively).

---

## 8. Steelman & rebuttal

**The strongest case for Avalonia-style selectors.** (1) *Reach*: `Button.danger:pointerover > TextBlock` styles arbitrary descendants without the control author's cooperation — WPF triggers can only set properties on self or named template parts; cross-element styling outside templates needs auxiliary machinery. (2) *Density*: `Button:focus:pointerover` is one line; the equivalent `MultiTrigger` is eight lines of XML. (3) *Runtime classes*: `element.Classes.Add("compact")` flips whole visual vocabularies with one call, and selectors compose them combinatorially. (4) *Familiarity*: every web developer reads CSS selectors on sight. (5) At terminal scale (hundreds of elements), selector matching cost — the classic objection — is genuinely irrelevant.

**Rebuttal.**

- **Reach — conceded in part, answered with property semantics.** The dominant real-world use of descendant selectors is *inside control templates*, which `TargetName`/`SourceName` covers precisely and with load-time name validation. For app-level cross-element styling, this design offers inherited attached flag properties: `theme:Density.Compact="True"` on a panel inherits down the tree, and any element's style triggers on it — that *is* descendant styling, but carried by the property system (inspectable, type-safe, precedence-governed) instead of a parallel tree-matching engine. What is honestly lost: styling unnamed third-party internals you don't own. At terminal scale, with templates we control and a styling-first default theme, that case is rare enough to trade away for the debugging story.

- **Density — conceded, then priced.** The `MultiTrigger` is verbose. But the verbosity buys parse-time checking: `Property="IsPointerOvr"` fails at load with a property-resolution error naming the style; `Button:pointerovr` matches nothing, silently, forever — Avalonia's own issue tracker documents years of "why doesn't my selector apply" reports. In C#, collection initializers make triggers nearly as dense as fluent selectors, with `nameof`-free static property references the compiler verifies. A selector *syntax* could even be added later as pure sugar that compiles to triggers — the activation/retraction engine and value slots wouldn't change — so choosing triggers now does not foreclose density later. The reverse migration (selectors → triggers) would be a rewrite.

- **Runtime classes — answered.** Attached bool properties are classes with types: `StyleFlags.SetDanger(element, true)` versus `Classes.Add("danger")`. Same gesture count, plus inheritance down the subtree (which CSS classes don't do without `:has()`-grade machinery) and the same single precedence table as everything else.

- **Familiarity — context matters.** This library's declared kinship is WPF/Avalonia *object models* (`RelativePoint`, `Brushes`, `Push*` scopes). `Style.Triggers` is the WPF-natural continuation; XAML consumers of this framework are XAML developers first, CSS developers second. And the selector grammar is the one part of Avalonia that is *not* XAML-natural — it's a string DSL embedded in XAML, invisible to the parser, the type system, and IntelliSense alike.

- **The deciding argument is mechanism, not syntax.** Selectors describe *when* styles apply but still need a mechanism for *what happens* when state flips — Avalonia internally re-evaluates matches and manages applied-setter retraction. Triggers expose that mechanism directly as inspectable objects with bits, indices, and provenance, and the retraction story is one invariant enforced in one place (the holder model). For a terminal framework whose debugging surface is the terminal itself, the system you can *see* is the system you can ship.

**Steelman #2 — "a principled hybrid" (pseudo-class triggers plus a small selector subset).** Tempting, but it doubles the conceptual surface (two attachment grammars, two precedence interactions, two documentation chapters) for reach we showed is mostly recoverable via inherited attached properties. The principled hybrid is sequencing, not synthesis: ship triggers as the one activation mechanism; if selector sugar earns its keep later, compile it onto this engine — additively, in the project's house style.