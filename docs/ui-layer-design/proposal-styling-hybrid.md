# Cursorial.UI Styling — Fork B Proposal: **Selectors for the tree, `When` for the data**

*A principled hybrid: a deliberately small CSS-style selector grammar as the single structural activation mechanism, plus `When` data-conditions (DataTrigger equivalents) as the single non-structural activation mechanism — both feeding one activation predicate, one priority slot, one retraction path.*

---

## 1. Executive summary & philosophy

WPF and Avalonia each got half of styling right. WPF's `Trigger` model is a state machine bolted onto each style — it is local and explicit, but it forces every styleable state to exist as a property on the control (`IsMouseOver`, `IsPressed`), multiplies value-priority slots (style setters vs. style-trigger setters vs. template-trigger setters), and has no vocabulary for *reuse across elements* short of keyed styles applied by hand. Avalonia's selectors got reuse and interactive state right — classes and pseudo-classes are the correct model for "this element is in this state" — but viewmodel-driven styling in a pure-selector world degenerates into code-behind class toggling or property-value selector hacks, because *selectors can only see the tree, and viewmodel state isn't in the tree*.

The hybrid keeps exactly one thing from each system and discards the rest:

> **A style is active on an element iff: (structural selector matches) ∧ (all required pseudo-classes set) ∧ (all `When` data-conditions true).**

That is the entire activation model. There are no `Trigger`s, no `MultiTrigger`s, no trigger-specific priority slots, no property-value selectors, no sibling/positional selectors. Selectors answer "*which elements, in which interactive state*"; `When` answers "*under which application state*". Each condition kind is a conjunct in the same predicate, evaluated by the same engine, injecting values into the same property-system slot through the same cookie-based retraction path. The combination is **smaller than either parent system alone** (§8 has the inventory), and every cut is justified by a terminal-scale cost argument: hundreds of elements, 20–60 fps, allocation discipline on the flip path.

Philosophy in three rules:

1. **One predicate, one slot, one sort key.** All style-sourced values land in `BindingPriority.Style` with a packed `StyleSortKey` (layer, specificity, order). "Trigger beats style" is not a slot — it is specificity, the way CSS solved it 28 years ago.
2. **Structural matching is rare; state flipping is hot.** Match once at attach/class-change; precompute *armed frames*; a pseudo-class or `When` flip is a bitmask test plus counter decrement — zero allocation, locality of one element.
3. **Keep the WPF/Avalonia naming kinship** the codebase already cultivates (`Style`, `Setter`, `Styles`, `ResourceDictionary`, `TemplateBinding`, `BasedOn`) so the API reads as family to the existing `RelativePoint`/`Brushes`/`Push*` vocabulary.

---

## 2. Public API sketch

All types in namespace **`Cursorial.UI`** (single namespace regardless of folder, per project convention). `Element`, `StyledProperty<T>`, `BindingBase`, `BindingPriority` are Fork A types (assumed shapes stated in §5); `IBrush`, `Pen` from `Cursorial.Drawing`; `Color`, `Style` (the SGR record — disambiguated below) from `Cursorial.Output`.

> **Name collision, resolved up front:** `Cursorial.Output.Style` is the cell-level SGR record. The UI styling object is `Cursorial.UI.Style`. Inside Cursorial.UI source, the Output record is referred to via `using CellStyle = Cursorial.Output.Style;`. This mirrors how WPF lives with `System.Drawing.Color` vs `System.Windows.Media.Color`.

### 2.1 The style object model

```csharp
namespace Cursorial.UI;

/// <summary>A reusable bundle of setters activated by a selector and optional data conditions.</summary>
public sealed class Style
{
    public Style();                                  // selector-less: only valid as explicit Element.Style / keyed theme
    public Style(Selector selector);
    public Style(string selector, ISelectorTypeResolver? resolver = null);  // parsed; default resolver = registered control types

    public Selector? Selector { get; init; }
    public Style? BasedOn { get; init; }             // setter inheritance chain; flattened at Seal; cycle => InvalidOperationException
    public SetterCollection Setters { get; }         // supports collection initializer
    public WhenCollection When { get; }              // conjunction of data conditions; empty = always
    public StyleCollection Children { get; }         // nested styles; child Selector must start with '^' (Nesting)
    public string? Key { get; init; }                // optional resource key (explicit attachment / BasedOn lookup)

    public bool IsSealed { get; }
    public void Seal();                              // validates, flattens BasedOn, compiles selector, freezes. Idempotent.
                                                     // Called automatically when added to an attached Styles collection.
}

public sealed class Setter
{
    public Setter(StyledProperty property, object? value);
    public StyledProperty Property { get; }
    public object? Value { get; }                    // constant | ResourceReference (DynamicResource) | BindingBase | UnsetValue
    // Constants are validated/converted against Property at Seal time, once, shared by all consumers.
}

public sealed class SetterCollection : Collection<Setter> { /* freezes with owner */ }
public sealed class StyleCollection  : Collection<Style>  { /* freezes with owner */ }
```

### 2.2 `When` — the DataTrigger equivalent

```csharp
/// <summary>A data condition: the style is active only while the bound value satisfies the test.
/// This is the WPF DataTrigger, reduced to a conjunct in the unified activation predicate.</summary>
public sealed class DataCondition
{
    public DataCondition(BindingBase binding, object? value);                       // Equals(value) test
    public DataCondition(BindingBase binding, Func<object?, bool> predicate);       // arbitrary test

    public BindingBase Binding { get; }            // evaluated against the *target element's* DataContext / source
    public object? Value { get; }
    public Func<object?, bool>? Predicate { get; }
    public bool Negate { get; init; }
}

public sealed class WhenCollection : Collection<DataCondition> { }   // ALL must hold (WPF MultiDataTrigger semantics)
```

There is deliberately no `Or` — disjunction is two styles sharing setters via `BasedOn`, exactly as in CSS. Each `DataCondition` adds one unit to specificity in the class column (§3.4), so a `When`-guarded style beats its unguarded base the same way `:pointerover` does.

### 2.3 Selector grammar — the exact supported subset

```
selector        := compound ( combinator compound )*
combinator      := ' '            descendant (logical tree)
                 | '>'            child
                 | '/template/'   crosses exactly one template boundary (left side must end in a templated-control compound)
compound        := [ '^' ] [ type | ':is(' type ')' ] simple*
simple          := '.' class-name
                 | '#' element-name
                 | ':' pseudo-class
type            := XAML-resolved CLR type; bare type = EXACT type match; ':is(T)' = T or derived
pseudo-class    := focus | focus-within | focus-visible | pointerover | pressed | disabled
                 | checked | indeterminate | selected | active-window | access-keys
                 | any control-registered custom pseudo-class
'^'             := nesting placeholder, leftmost only, valid only in Style.Children / explicit styles
```

**Explicitly absent, by decision (not omission):** `:not()`, `:nth-child()` / positional selectors, sibling combinators (`+`, `~`), Avalonia property-value selectors (`[IsDefault=true]`), attribute selectors, `,` selector lists (use two styles or `BasedOn`). Rationale in §6/§8 — the one-line version: positional/sibling selectors make invalidation a function of *sibling list mutation*, which destroys the precise-invalidation model for negligible expressive gain on a terminal UI; property-value selectors are subsumed by pseudo-class mappings (control properties) and `When` (everything else).

```csharp
public abstract class Selector
{
    public static Selector Parse(string text, ISelectorTypeResolver? resolver = null);
    public override string ToString();               // round-trips
    // Internals: compiles to CompiledRule at Style.Seal (see §3.2). No public matching API; the engine owns matching.
}

/// <summary>Fluent builders for code-first styling (mirrors Avalonia's Selectors class).</summary>
public static class Selectors
{
    public static Selector OfType<T>() where T : Element;          // exact type
    public static Selector Is<T>() where T : Element;              // type or derived
    public static Selector Class(this Selector? s, string name);
    public static Selector Name(this Selector? s, string name);
    public static Selector PseudoClass(this Selector? s, string name);
    public static Selector Child(this Selector s);                 // s > ...
    public static Selector Descendant(this Selector s);            // s   ...
    public static Selector Template(this Selector s);              // s /template/ ...
    public static Selector Nesting();                              // '^'
}

public interface ISelectorTypeResolver { Type? Resolve(string typeToken); }
```

### 2.4 Element-side surface (classes, pseudo-classes, attachment)

```csharp
public partial class Element   // Fork A's base; styling adds these members (stated here as the contract)
{
    public ClassSet Classes { get; }                       // user classes; mutable, change-notifying
    protected PseudoClassSet PseudoClasses { get; }        // control-author surface; ':' names rejected from Classes
    public Style? Style { get; set; }                      // EXPLICIT attachment (layer = Explicit); selector-less or '^'-rooted
    public Styles Styles { get; }                          // scoped styles: apply to this element's subtree (lazy-alloc)
    public ResourceDictionary Resources { get; }           // lazy-alloc; see §2.6
}

/// <summary>Interned-string small set; add/remove notify the style engine.</summary>
public sealed class ClassSet : IReadOnlyCollection<string>
{
    public bool Add(string name);
    public bool Remove(string name);
    public bool Contains(string name);
    public void Replace(ReadOnlySpan<string> names);       // single restyle pass for bulk swap
}

public sealed class PseudoClassSet
{
    public bool Set(string pseudoClass, bool active);      // returns true if changed; O(1) for well-known (bitmask)
    public bool Contains(string pseudoClass);
}

/// <summary>Declares "this bool property mirrors this pseudo-class" — the property→selector bridge.
/// Registered once per control type, in a static ctor (e.g. ToggleButton maps IsCheckedProperty → ":checked").</summary>
public static class PseudoClassMapping
{
    public static void Register<TOwner>(StyledProperty<bool> property, string pseudoClass) where TOwner : Element;
    public static void Register<TOwner, TValue>(StyledProperty<TValue> property,
        Func<TValue, string?> classify, ReadOnlySpan<string> pseudoClasses) where TOwner : Element;
        // e.g. IsChecked (bool?) → ":checked" / ":indeterminate" / null
}

/// <summary>A prioritized, ordered collection of styles; attaches to Application, Window, Element, or ControlTemplate.</summary>
public sealed class Styles : Collection<Style>
{
    // On attach to a host, builds/updates the StyleIndex for that scope (§3.2). Mutation after attach
    // triggers a scope re-match (legal, intended for hot-reload; not a per-frame operation).
}
```

### 2.5 Interactive-state intake (the Fork C seam)

```csharp
[Flags]
public enum InteractionState : uint
{
    None = 0,
    PointerOver  = 1 << 0,   // ":pointerover"  — set on the full hit chain (element + ancestors)
    Pressed      = 1 << 1,   // ":pressed"
    Focused      = 1 << 2,   // ":focus"        — physical (keyboard) focus
    FocusWithin  = 1 << 3,   // ":focus-within" — element or descendant has focus (set on ancestor chain)
    FocusVisible = 1 << 4,   // ":focus-visible" — focus arrived via keyboard, not mouse
    ActiveWindow = 1 << 5,   // ":active-window" — element's window is the active one
    AccessKeyCue = 1 << 6,   // ":access-keys"  — Alt held / cue mode (window-scoped; access-key fork toggles)
    Disabled     = 1 << 7,   // ":disabled"     — EFFECTIVE IsEnabled (self ∧ ancestors), pushed by property system
}

/// <summary>Implemented by Element; called only by the input/focus/window forks. Each call is a
/// synchronous local restyle of one element (§3.5); callers batch chain updates with BeginInteractionUpdate.</summary>
public interface IInteractionStateSink
{
    void SetInteractionState(InteractionState state, bool active);
    InteractionUpdateScope BeginInteractionUpdate();   // coalesces N flips into one activation pass per element
}
```

### 2.6 Resources & themes

```csharp
public sealed class ResourceDictionary : IDictionary<object, object?>
{
    public IList<ResourceDictionary> MergedDictionaries { get; }
    public IDictionary<ThemeVariant, ResourceDictionary> ThemeDictionaries { get; }
    public int Version { get; }                        // bumped on any mutation incl. merged/theme swap
    public bool TryGetResource(object key, ThemeVariant variant, out object? value);
}

/// <summary>The terminal-native theme axis: light/dark × color tier (§6.1).</summary>
public readonly record struct ThemeVariant(ThemeBase Base, ColorDepth Tier)
{
    public static ThemeVariant FromCapabilities(TerminalCapabilities caps);  // luminance of DefaultBackground + Color.Depth
    public static readonly ThemeVariant Dark, Light;                          // tier = Truecolor
    // Lookup order: exact (Base,Tier) → (Base, next-lower tier…) → Base-only → unkeyed.
}

public readonly struct ResourceReference(object key) { public object Key { get; } }  // DynamicResource payload

public static class ResourceExtensions   // on Element
{
    public static object? FindResource(this Element e, object key);          // throws if missing
    public static bool TryFindResource(this Element e, object key, out object? value);
    // Walk: self → logical ancestors → Window → Application → built-in theme. Each hop checks ThemeDictionaries first.
}
```

`StaticResource` resolves **once** at XAML load (markup extension, against the loading element's scope). `DynamicResource` stores a `ResourceReference` in the setter/property; resolution + subscription happens per consuming element at activation (§3.6).

### 2.7 Templates

```csharp
public sealed class ControlTemplate
{
    public ControlTemplate(Type targetType, Func<TemplatedControl, INameScope, Element> build);
    public Type TargetType { get; }
    public Styles Styles { get; }                      // template-scoped styles (layer = Template)
    public Element Instantiate(TemplatedControl parent, out INameScope nameScope);
}

public sealed class DataTemplate
{
    public DataTemplate(Type dataType, Func<object?, Element> build);
    public Type DataType { get; }
    public Element Build(object? data);                // result gets DataContext = data; styled normally
}

public static class TemplateBinding
{
    // One-way binding to the templated parent's property, applied at BindingPriority.Template.
    // Allocation-light: the description is static per template; per-instance cost is one subscription node.
    public static BindingBase To(StyledProperty sourceProperty);
    public static BindingBase To(StyledProperty sourceProperty, IValueConverter converter);
}
```

A **control theme** is not a separate type: it is a `Style` (selector-less, with `Children` rooted at `^`) registered in a theme `ResourceDictionary` under the key `typeof(Button)`, typically containing a `Setter` for `TemplateProperty`. The engine resolves it at attach and applies it at layer `ControlTheme` (lowest). One mechanism — keyed style — serves WPF keyed styles, Avalonia `ControlTheme`, and explicit `Element.Style`.

### 2.8 Diagnostics (first-class, not an afterthought)

```csharp
public static class StyleDiagnostics
{
    /// <summary>Why does this property have this value? Returns every contributing setter,
    /// active and shadowed, with its sort key — the F12 "computed styles" panel for the terminal.</summary>
    public static StyleExplanation Explain(Element element, StyledProperty property);
    public static IReadOnlyList<MatchedRuleInfo> MatchedRules(Element element);   // armed frames + activation state
}
```

### 2.9 Consumer example

A `Button` with a theme, classes, pseudo-class styles, a viewmodel-driven `When` style, and template reach-in.

**Control author side (C#):**

```csharp
public class Button : TemplatedControl
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        StyledProperty.Register<Button, IBrush?>(nameof(Background), Brushes.Transparent);
    public static readonly StyledProperty<Pen> BorderPenProperty =
        StyledProperty.Register<Button, Pen>(nameof(BorderPen), Pens.Rounded);
    public static readonly StyledProperty<bool> IsDefaultProperty =
        StyledProperty.Register<Button, bool>(nameof(IsDefault));

    static Button()
    {
        PseudoClassMapping.Register<Button>(IsDefaultProperty, ":default");   // control-defined custom pseudo-class
    }
    // ":pressed"/":pointerover"/":focus" arrive via IInteractionStateSink — no code here.
}
```

**Theme (control theme = keyed style with template), code-first:**

```csharp
var buttonTheme = new Style
{
    Key = typeof(Button).FullName,
    Setters =
    {
        new(Button.TemplateProperty, new ControlTemplate(typeof(Button), static (button, scope) =>
        {
            var chrome = new Border { Name = "chrome" };
            scope.Register("chrome", chrome);
            chrome.Bind(Border.BackgroundProperty, TemplateBinding.To(Button.BackgroundProperty));
            chrome.Bind(Border.BorderPenProperty,  TemplateBinding.To(Button.BorderPenProperty));
            chrome.Child = new ContentPresenter();
            return chrome;
        })),
        new(Button.BackgroundProperty, new ResourceReference("SurfaceBrush")),       // DynamicResource
        new(Button.ForegroundProperty, new ResourceReference("TextBrush")),
    },
    Children =
    {
        new Style("^:pointerover") { Setters = { new(Button.BackgroundProperty, new ResourceReference("SurfaceHoverBrush")) } },
        new Style("^:focus")       { Setters = { new(Button.TextAttributesProperty, TextAttributes.Bold | TextAttributes.Underline) } },
        new Style("^:pressed /template/ #chrome")
                                   { Setters = { new(Border.BorderPenProperty, Pens.Heavy) } },
        new Style("^:disabled")    { Setters = { new(Button.TextAttributesProperty, TextAttributes.Faint) } },
    },
};
themeResources.Add(typeof(Button), buttonTheme);
```

**App XAML:**

```xml
<Window xmlns="https://cursorial.dev/ui" xmlns:x="https://cursorial.dev/xaml">
  <Window.Styles>
    <!-- implicit-by-class -->
    <Style Selector=":is(Button).primary">
      <Setter Property="Background" Value="{DynamicResource AccentBrush}"/>
      <Style Selector="^:pointerover">
        <Setter Property="Background" Value="{DynamicResource AccentHoverBrush}"/>
      </Style>
    </Style>

    <!-- scoped descendant styling -->
    <Style Selector="StackPanel.toolbar > Button">
      <Setter Property="Margin" Value="0,0,1,0"/>
    </Style>

    <!-- the DataTrigger equivalent: viewmodel state, no code-behind, no class toggling -->
    <Style Selector="Button#save">
      <Style.When>
        <DataCondition Binding="{Binding IsDirty}" Value="False"/>
      </Style.When>
      <Setter Property="IsEnabled" Value="False"/>
    </Style>
  </Window.Styles>

  <StackPanel Classes="toolbar">
    <Button x:Name="save" Classes="primary" Content="_Save"/>
    <Button Content="_Cancel"/>
  </StackPanel>
</Window>
```

---

## 3. Internal architecture

### 3.1 The pipeline at a glance

```
Styles collections attach          →  per-scope StyleIndex (rule hash)
Element attaches / classes change  →  PHASE 1: structural match → ActivationFrame[] on element  (rare, "expensive")
Pseudo flip / When flip            →  PHASE 2: mask test + unmet-counter, activate/retract        (hot, O(local), 0 alloc)
Frame activates                    →  setters → IStyleValueSink.SetStyleValue(..., sortKey, cookie)
Frame retracts                     →  IStyleValueSink.RemoveStyleValue(cookie)  (property system promotes next value)
Property change                    →  Fork A change notification → element marks its Scene dirty → owner-driven Invalidate()
```

### 3.2 Compiled rules and the per-scope index

`Style.Seal()` flattens `BasedOn` (derived setters appended after base; later wins at equal sort key) and `Children` (nesting `^` AND-composes compounds: `Button.primary` + `^:pointerover` → one rule `Button.primary:pointerover`; `^ /template/ #chrome` → `Button.primary /template/ #chrome`), then compiles each resulting rule:

```csharp
internal sealed class CompiledRule
{
    public CompoundMatcher[] Compounds;     // right-to-left: [0] = subject
    public Combinator[] Combinators;        // between compounds: Child | Descendant | Template
    public Setter[] Setters;                // flattened, validated, constants pre-converted
    public DataConditionDescriptor[] When;  // flattened (parent style's When AND child's)
    public uint SubjectPseudoMask;          // well-known pseudos required on the subject
    public string[] SubjectCustomPseudos;   // interned; usually empty
    public AncestorStateReq[] AncestorState;// pseudo requirements on non-subject compounds (rare; see §3.5)
    public StyleSortKey BaseSortKey;        // layer+specificity; order filled per scope
}

internal struct CompoundMatcher
{
    public Type? Type; public bool ExactType;
    public int[] ClassIds;                  // interned string handles, sorted
    public int NameId;                      // -1 = none
    public uint PseudoMask; public int[] CustomPseudoIds;
}
```

Each attached `Styles` collection owns a **`StyleIndex`**: a rule-hash keyed by the subject compound's most selective discriminator — name first, else class, else exact type, else `:is` base type, else the (discouraged, diagnostics-warned) universal bucket:

```csharp
internal sealed class StyleIndex
{
    Dictionary<int,  RuleList> byName;      // #save
    Dictionary<int,  RuleList> byClass;     // .primary
    Dictionary<Type, RuleList> byExactType; // Button
    Dictionary<Type, RuleList> byIsType;    // :is(Button) — probed for each type in the element's base chain
    RuleList universal;
    public HashSet<int> AncestorInterestingClasses;  // classes appearing in NON-subject compounds (drives §3.7 subtree re-match)
}
```

This is the browser-engine rule-hash trick at 1/1000 scale; candidate sets per element are typically < 10 rules.

### 3.3 Phase 1 — structural match (attach, class change, name change, Styles mutation)

For an attaching element, the engine walks its scope chain (element → ancestors with `Styles` → Window → App → theme), gathers candidates from each `StyleIndex`, and evaluates **structure only**: subject type/class/name, then ancestor compounds right-to-left via parent walk (logical tree; `/template/` hops exactly one templated-parent edge and requires the left compound to match the templated control). Pseudo-class and `When` requirements are *not* evaluated here — a structurally matching rule becomes an **armed frame**:

```csharp
internal sealed class ElementStyleState        // one per styled element with ≥1 match; ~100–300 B typical
{
    public ActivationFrame[] Frames;            // sorted by StyleSortKey ascending
    public uint PseudoInterestMask;             // union of frames' SubjectPseudoMask — O(1) early-out on flips
    public InlineList<AncestorDependency> AncestorDeps;  // registered on ancestors that carry pseudo requirements
}

internal struct ActivationFrame                 // 32 bytes
{
    public CompiledRule Rule;
    public ushort UnmetCount;                   // (# pseudo bits unmet) + (# custom pseudos unmet) + (# When unmet) + (# ancestor reqs unmet)
    public ushort Flags;                        // Active | HasBindings | HasDynamicResources
    public StyleValueCookie Cookie;             // opaque batch handle from Fork A (int)
    public ConditionWatcher[]? Watchers;        // per-When binding subscriptions; null when no When clauses
}
```

At arm time, `UnmetCount` is initialized from current state; frames arriving at 0 activate immediately. `When` clauses connect their bindings now (one `ConditionWatcher` per condition; the watcher caches the binding expression instance — deactivation pauses, not destroys, so repeated flips don't re-allocate). Sorting happens once; the frames array is immutable until the next Phase 1.

**Explicit styles & control themes** skip the index: `Element.Style` arms as a frame with layer `Explicit`; the type-keyed theme style arms at layer `ControlTheme`. Their `Children` rules arm on the element and (for `/template/` children) on template parts at template application.

### 3.4 Specificity and the sort key

```csharp
public readonly struct StyleSortKey : IComparable<StyleSortKey>   // packed ulong
{
    // [layer:3][names:8][classLike:10][types:8][scopeDepth:8][order:27]
    // layer:     ControlTheme(0) < Template(1) < Theme(2) < App(3) < Scoped(4, deeper wins via scopeDepth) < Explicit(5)
    // classLike: classes + pseudo-classes + When-conditions each count 1   ← DataConditions ARE specificity
    // order:     declaration index within the scope (later wins ties)
}
```

Counting each `DataCondition` as a class-equivalent makes `When`-guarded styles beat their unguarded bases with no extra mechanism — the precise property WPF buys with the separate `StyleTrigger` slot.

### 3.5 Phase 2 — the hot path: pseudo-class and `When` flips

`SetInteractionState` / `PseudoClassSet.Set` / a `PseudoClassMapping` property change / a `ConditionWatcher` callback all converge on one routine:

```
FlipBit(element, bit, on):
    if (element.StyleState is null || (element.StyleState.PseudoInterestMask & bit) == 0
        && element has no ancestor-dependents for bit): return            // O(1) — most elements exit here
    for each frame whose requirement includes bit:                        // typically 1–5 frames
        frame.UnmetCount += on ? -1 : +1
        if transitioned to 0:   Apply(frame)      // setters → sink, capture cookie
        if transitioned from 0: Retract(frame)    // sink.RemoveStyleValue(cookie); pause watchers' subscriptions? no — When watchers stay live
    notify dependent descendants registered for ancestor-state (see below)
```

**Ancestor pseudo-classes** (`Pane:focus-within Button { … }`) are supported but pay their own way: at Phase 1, such a frame registers an `AncestorDependency` node on that specific ancestor. A flip on the ancestor walks only its dependency list — precise, no subtree scan. The grammar's pseudo-class set is curated so this stays rare; diagnostics flag rules with > 1 ancestor-state compound.

**Re-entrancy & loops.** Applying a setter can change a property with a `PseudoClassMapping` (style sets `IsEnabled=false` → `:disabled` flips → more styles). Flips triggered during application are queued and drained to fixpoint with a generation counter; a frame toggled twice in one drain ( A→B→A ) trips the **style-loop diagnostic** (rule pair identified by name), mirroring WPF's trigger-loop failure mode but with a precise error instead of silent oscillation. Depth cap: 16 generations, then throw with the cycle trace.

**Batching.** `BeginInteractionUpdate` (used by Fork C when the pointer crosses N boundary elements, or focus moves and `:focus`/`:focus-within`/`:focus-visible` change together) defers `Apply`/`Retract` until scope dispose, so an element whose frames would activate-then-deactivate within one input event does neither.

### 3.6 Value injection and retraction (the Fork A handshake)

All style values enter the property system at **one** priority: `BindingPriority.Style`, carrying their `StyleSortKey`. The property system keeps, per (element, property), a small sorted list of style entries and exposes the max as the slot's value. The full slot lattice (Fork A's, restated as this fork requires it):

```
Animation  >  Local  >  Style (internally ordered by StyleSortKey)  >  Template (TemplateBinding)  >  Inherited  >  Default
```

```csharp
public interface IStyleValueSink   // implemented by Fork A's value store
{
    StyleValueCookie ApplyRule(Element target, CompiledRule rule, StyleSortKey key);
    //   For each setter: constant → store entry; BindingBase → instantiate (or resume cached) expression at Style priority;
    //   ResourceReference → resolve via target.TryFindResource, store result, subscribe (element-scope, version-keyed).
    void RemoveRule(Element target, StyleValueCookie cookie);
    //   Removes all entries of the batch; per property, promote the next-highest entry (or fall through the lattice).
    //   MUST be O(entries) and allocation-free.
    void OnResourcesChanged(Element subtreeRoot, ResourceScopeChange change);   // DynamicResource re-resolution sweep
}
```

Cookie-based batch retraction is the contract that makes "cleanly retract" honest: no diffing, no "set back to what it was" (which breaks under overlapping styles), just removal + promotion — the same promotion logic the property system already needs for `ClearValue`.

**DynamicResource flow:** setter value `ResourceReference` → resolved at activation against the *consuming element's* scope; the subscription is `(element, key) → dictionary version` and re-resolves on `ResourceDictionary.Version` change anywhere in the element's scope chain (the dictionary change notifies the subtree root; the sweep visits only elements holding subscriptions, tracked in a per-window registry — not a tree walk).

### 3.7 Class / name / tree-shape changes

- **Class change on E:** re-run Phase 1 for E. If the changed class is in any scope's `AncestorInterestingClasses`, also re-match E's subtree (bounded, explicit, and rare — class changes are user actions, not per-frame events). Frames are diffed by rule identity: surviving frames keep cookies and watcher instances; only added/removed frames touch the sink.
- **Attach/detach:** attach = Phase 1; detach = retract active frames, dispose watchers, drop `ElementStyleState`. Detach of a subtree is bottom-up, batch-retracted.
- **`Styles` collection mutation / theme dictionary swap:** scope-wide re-match (Phase 1 over the scope's elements). This is the "restyle the world" path; §7 costs it — it is start-up/theme-switch tier, deliberately not optimized beyond frame-diffing.

### 3.8 Templates

`TemplatedControl` applies its `Template` (from the `Template` property — usually a ControlTheme setter) on first layout-attach: instantiate, register name scope, arm the template's `Styles` (layer `Template`) against the new subtree, arm any outer `/template/` frames that matched the templated parent structurally (their subject compounds are matched against template parts now). Re-templating retracts the whole template subtree's frames (subtree detach) and rebuilds. `TemplateBinding` lives at `Template` priority so both page styles (`Style` slot) and local values on parts beat it — the WPF behavior people actually expect.

`DataTemplate` resolution walks the resource scope for a `DataTemplates` collection keyed by data type (exact, then base chain). Generated elements are ordinary elements: DataContext set, styles match normally — no special styling machinery.

---

## 4. Requirement satisfaction

**Req 1 — Rich styling & templating.** Styles with setters over any `StyledProperty` (brushes, pens, text attributes, templates themselves); `ControlTemplate` with name scopes, template-scoped styles, `TemplateBinding`, external reach-in via `/template/`; `DataTemplate` by data type; control themes as keyed styles. Setter values may be constants, `DynamicResource`, or bindings — the full WPF value vocabulary.

**Req 3 — Resource/style inheritance.** Three orthogonal inheritance axes, each with one mechanism: *resources* inherit down the logical tree via the scope walk (element → ancestors → window → app → theme, theme-variant-aware at every hop); *styles* inherit definitionally via `BasedOn` (flattened at seal, cycle-checked) and positionally via scoped `Styles` collections (a style declared on a `Pane` styles only that subtree; nearer scope = higher layer); *values* inherit via Fork A's `Inherited` slot, which sits below `Style` so any style can override an inherited foreground.

**Req 8 — Setters + Triggers-or-Selectors.** Both, unified: the selector subset covers everything WPF property triggers do (a property trigger *is* a pseudo-class test once `PseudoClassMapping` exists; a `MultiTrigger` *is* a compound selector), and `When` covers everything DataTrigger/MultiDataTrigger do (binding + test, conjunction, full binding-system power including element-name and ancestor sources — whatever Fork A's `BindingBase` supports). What neither needed — `EventTrigger` — is explicitly ceded to the animation/storyboard surface (req 10's fork), which owns event→storyboard wiring; styling exposes activation/deactivation of frames as the natural trigger points for style-driven transitions in a later phase (§7).

---

## 5. Cross-fork contract

Stated as interfaces this fork *consumes* (C) or *provides* (P).

**From Fork A (properties & binding) — consumed:**

```csharp
// C1: property identity & registration
public abstract class StyledProperty { public string Name { get; } public Type PropertyType { get; } public int GlobalIndex { get; } }
public sealed class StyledProperty<T> : StyledProperty { /* Register<TOwner,T>(name, default, inherits, validate, coerce) */ }

// C2: the value store with a Style slot — the load-bearing contract (§3.6)
public interface IStyleValueSink { /* ApplyRule / RemoveRule / OnResourcesChanged, exactly as §3.6 */ }
//   REQUIRED slot order: Animation > Local > Style(sorted by StyleSortKey) > Template > Inherited > Default.
//   REQUIRED: RemoveRule is allocation-free; promotion on removal; ClearValue does NOT disturb Style entries.

// C3: property change notification — PseudoClassMapping subscribes to per-type property-changed
public interface IPropertyChangeObserver { void OnPropertyChanged(Element e, StyledProperty p, object? oldV, object? newV); }

// C4: binding instantiation for When conditions and setter bindings
public interface IBindingService
{
    IConditionSubscription Connect(Element target, BindingBase binding, Action<object?> onValueChanged);
    // subscription is pausable/resumable (When watchers park while structurally matched but never active? No —
    // watchers stay live while ARMED; pausing applies only on element detach). Must tolerate DataContext changes.
}

// C5: effective-IsEnabled — Fork A computes self∧ancestor enabled state and pushes InteractionState.Disabled flips.
```

**From Fork C (input, focus, windows) — consumed:**

```csharp
// C6: interaction state pushed through IInteractionStateSink (§2.5), with BeginInteractionUpdate batching on
//     pointer-chain crossings and focus moves. PointerOver/FocusWithin set along the ancestor chain; Pressed on
//     the captured element; ActiveWindow on a window's subtree root only (styling fans it via ancestor-dependency).
// C7: capability truth: Fork C exposes the negotiated TerminalCapabilities snapshot; styling reads
//     MouseCapabilities.Motion (":pointerover" availability) and ColorCapabilities (ThemeVariant.FromCapabilities).
// C8: FocusEvent { HasFocus:false } clears AccessKeyCue + PointerOver + Pressed window-wide (Fork C's job; styling
//     just receives the flips). Modal scope changes arrive as plain class/pseudo flips (".modal-blocked" or :disabled).
```

**Provided to both (P):**

```csharp
// P1: IInteractionStateSink + PseudoClassSet + ClassSet on Element (Fork C writes; controls write).
// P2: PseudoClassMapping registry (Fork A's property-changed pipeline calls observer C3 → styling flips bits).
// P3: Resource lookup service (TryFindResource + subscription registry) — Fork A's DynamicResource-in-local-value
//     and the markup layer both use it; single implementation lives here.
// P4: Template application service (TemplatedControl calls; Fork A only defines TemplateProperty's type).
// P5: StyleDiagnostics (consumed by dev tooling and by the other forks' own diagnostics).
// P6: Restyle hooks: OnElementAttached/Detached(Element) — the tree/lifecycle owner (Fork A) must call these.
```

**Shared invariant (all forks):** all styling operations are render-thread-only, matching the lower stack (`CellBuffer`/`Scene`/compositor are single-thread). Cross-thread viewmodel changes marshal through Fork A's dispatcher before reaching `ConditionWatcher` callbacks.

---

## 6. Terminal-specific adaptations

1. **Theme variants are capability-shaped, not just light/dark.** `ThemeVariant = (ThemeBase, ColorDepth)` with tiered fallback (§2.6). A theme ships truecolor gradient brushes in the `(Dark, Truecolor)` dictionary and palette-16 `Color` setters in `(Dark, Ansi16)`; `ThemeVariant.FromCapabilities` reads negotiated `ColorCapabilities.Depth` and light/dark from `DefaultBackground` luminance (the capability record explicitly supports this). The quantizer downstream makes truecolor *safe* everywhere; theme tiers make it *good* — hand-picked palette colors beat 6×6×6-cube approximations for brand colors. `RenegotiateAsync` swapping the capability snapshot re-resolves the variant → one DynamicResource sweep.

2. **State styling favors attributes over geometry.** There is no sub-pixel anything: no `RenderTransform` setters, no opacity-per-element below composite granularity, no box-shadow-on-hover (a drop shadow changes the element's *footprint* — a layout-affecting act on a cell grid). The built-in pseudo-class conventions lean on what a terminal does instantly: `:disabled` → `TextAttributes.Faint` (not opacity), `:focus` → `Bold`/`Inverse`/underline, `:pressed` → `Pen` weight swap (`Pens.Heavy`), `:pointerover` → background color. Underline shape (`UnderlineStyle.Curly` etc.) and underline color are first-class setter targets — terminal-native affordances WPF doesn't have.

3. **`:pointerover` is capability-honest.** When `MouseCapabilities.Motion` is false the bit simply never sets — no polyfill, no fake hover. Lint-level diagnostic: a style with `:pointerover` setters and no sibling `:focus` rule for the same properties gets a one-time debug warning ("hover-only affordance; unreachable on N% of terminals"), pushing theme authors toward focus parity. Same honesty for `:access-keys`: per the input reference, Alt-down/up exists only on Kitty-protocol (`ReportEventTypes + ReportAllKeysAsEscapeCodes`) and Win32-input-mode terminals — the access-key fork toggles the window-scoped `AccessKeyCue` bit there, and *sets it permanently on* elsewhere. Requirement 6's "underscores toggle with Alt, else permanently visible" is then **pure styling**: `:access-keys ContentPresenter { ShowAccessKeyUnderlines: true }` plus a window-level permanent bit — no special rendering path.

4. **Restyle → repaint granularity is the scene, and that's the budget that matters.** A pseudo flip's downstream cost is not layout (state styles are encouraged to be non-layout-affecting; a setter on a layout property is legal but diagnostics-visible) — it is `Scene.Invalidate()` on the element's scene, a re-raster of that element's cells (a 12×3 button = 36 cells of brush sampling), a compositor union of that footprint, and a `FrameRenderer` diff emitting only changed cells. Styling deliberately exposes *which properties are paint-only* via Fork A's property metadata so the invalidation is scene-local, never global. Color-only flips on a `CompositeParameters`-expressible property (whole-scene opacity) skip re-raster entirely.

5. **Background ≠ inherited property.** In WPF, `Background` inheritance confusion is endemic. Here, *visual* background continuity is the compositor's job (scenes are transparent; lower layers show through per the §0 compositing invariant), so `Background` defaults to `Brushes.Transparent` and is **not** property-inherited; `Foreground`/`TextAttributes` are. Theme authors style backgrounds positionally (panels, windows), not hereditarily. This keeps the styling model aligned with the transparency model the drawing layer already enforces.

6. **`GlyphSet`/ASCII degradation is a theme decision, surfaced as styling.** The drawing layer is explicit that `GlyphSet.Ascii` is a consumer policy knob. The built-in theme keys its `Pen` resources per variant tier, so a `(Dark, Ansi16)` theme can ship `Pens.Ascii`-based pens wholesale — capability-appropriate chrome with zero control-code awareness.

7. **Scale honesty cuts grammar.** Browsers need `:nth-child` invalidation engineering because pages have 10⁴–10⁵ nodes. We have ~10². That budget argues for *simpler, precisely-invalidating* machinery, not for porting CSS wholesale — hence the subset. It also means the nuclear option (scope-wide re-match on theme swap) is genuinely cheap, so we spend zero complexity making it incremental.

---

## 7. Costs, risks, phasing

**Perf model (magnitudes, with reasoning — not benchmarks):**

| Event | Work | Allocation |
|---|---|---|
| Pseudo/`When` flip, no matching frames | 1 bitmask AND | 0 |
| Pseudo/`When` flip, k frames, s setters | k counter ops + s sink entries + 1 scene invalidate; re-raster = element's cell area | 0 on steady path (cookies/watchers/expressions reused) |
| Pointer crossing m-element chain | m × above, batched in one `InteractionUpdateScope` | 0 |
| Class change on one element | Phase 1: ~c candidate rules × compound check (type ptr cmp + small int-array scans + ancestor walk ≤ tree depth ~8) ≈ c·10²ns | new frames array (small) |
| Attach 200-element screen | 200 × Phase 1 ≈ low single-digit ms ceiling, dominated by setter application | frames + state objects, ~tens of KB |
| Theme swap | DynamicResource sweep over subscriptions + theme-layer re-match; full-screen re-raster follows anyway | bounded by re-resolved values |

The flip path honors the project's allocation discipline: frames are structs in an array, sort keys are packed ulongs, classes/pseudos are interned ints, and the sink contract forbids allocation on `RemoveRule`.

**Implementation effort & phases** (following the repo's phased-design-doc playbook — living doc, numbered phases, adversarial review on the matcher):

- **Phase S0 — spine** *(≈2–3 weeks)*: `Style`/`Setter`/seal/`BasedOn` flatten; selector parser + compiled rules (no `/template/`); `StyleIndex`; Phase-1 matcher; pseudo-class bitset + `PseudoClassMapping`; `IStyleValueSink` integration; implicit-by-type/class; `ClassSet`. Oracle tests: specificity table pinned against hand-computed CSS-equivalent cases.
- **Phase S1 — state** *(≈1–2 weeks)*: `IInteractionStateSink` + batching; ancestor-state dependencies; fixpoint queue + loop diagnostics; `StyleDiagnostics`.
- **Phase S2 — resources** *(≈2 weeks)*: `ResourceDictionary` + merged/theme dictionaries; Static/DynamicResource; `ThemeVariant.FromCapabilities` + tiered lookup; subscription registry.
- **Phase S3 — `When`** *(≈1 week)*: `DataCondition`, watcher lifecycle, specificity integration. Small *because* it reuses Fork A's binding system wholesale — this is the hybrid's structural payoff.
- **Phase S4 — templates** *(≈2–3 weeks)*: `ControlTemplate`/`DataTemplate`, name scopes, `TemplateBinding`, `/template/` combinator, template-scoped styles, control-theme resolution, re-templating.
- **Phase S5 — XAML** *(with the markup fork)*: selector type resolution (`ISelectorTypeResolver` over the XAML schema context), markup extensions, nested-style sugar.

**Punted (recorded as §11-style deferrals):** `:not()` (re-addable additively; needs negative-dependency invalidation), selector lists (`,`), style-driven transitions (`Style.Transitions` — frame activate/retract are the obvious hooks; owned jointly with the animation fork), `OrConditionGroup`, property-value selectors (probably never — `When` + mappings cover it), hot-reload diffing beyond scope re-match, `x:Shared`-style setter-value cloning.

**Risks, honestly:**
- *Specificity is a real cognitive cost.* Mitigation is tooling, not grammar: `StyleDiagnostics.Explain` ships in S1, not as a someday. The layer model (explicit > scoped > app > theme) resolves the cases that actually confuse people; within-layer conflicts at terminal scale are tractable.
- *The Fork A sink contract is load-bearing.* If the property system can't hold multiple sorted style entries per property cheaply, this design degrades. Mitigation: the contract is stated (C2) and small; a fallback (single style value + full re-evaluate on retract) is correct-but-slower and API-compatible.
- *`When` watchers are live while armed, not just while active* — N armed `When` styles on a long list cost N subscriptions. Acceptable at 10² elements; if it bites, the deferral is lazy connection on first structural match within viewport (recorded, not built).
- *Ancestor pseudo-class dependencies* add bookkeeping; capped by diagnostics (warn > 1 ancestor-state compound per rule) and by the curated pseudo set.

---

## 8. Steelman & rebuttal

### Steelman: pure WPF Triggers (no selectors, no parser)

*The strongest case:* Everything about an element's appearance lives in **one object** — base setters and every state response, readable top-to-bottom, no action-at-a-distance. There is **no string grammar**: no parser to write, no type-resolution plumbing for selector tokens, no specificity algebra to teach; conflict resolution is "last writer in the trigger list", which every developer can simulate in their head. Triggers compose conditions explicitly (`MultiTrigger.Conditions`) rather than via invisible compound-selector AND. Tooling is trivial (it's just object graphs). And WPF muscle memory transfers verbatim — `<Trigger Property="IsMouseOver" Value="True">` has 20 years of Stack Overflow behind it. For a *terminal* UI — small trees, few controls, mostly hand-rolled themes — maybe locality beats reuse, and the selector engine is over-engineering.

*Rebuttal:* The trigger model's costs are structural, not cosmetic. (1) **It forces interaction state to be properties.** `IsMouseOver`/`IsPressed`/`IsKeyboardFocusWithin` must exist as dependency properties on every element, with change plumbing through the property system, *purely so triggers can see them* — that's per-element storage and per-flip property-system traffic for state that a pseudo-class bitmask carries in one `uint`. On our stack, Fork C would have to write properties through Fork A to reach Fork B; the pseudo-class sink is a straight line. (2) **It cannot express "all primary buttons" without ceremony.** Implicit styles are type-keyed only; class-like reuse requires keyed styles manually assigned per element — precisely the boilerplate the `Classes="primary"` idiom kills. (3) **It multiplies priority slots.** WPF needs style-setter vs style-trigger vs template-trigger slots *because* triggers are a second mechanism; we get the same override behavior from specificity inside one slot. (4) **Template restyling requires re-templating.** Without `/template/`, changing one border glyph in a themed button means copying the whole template. (5) The genuinely irreplaceable trigger — `DataTrigger` — **we kept**, as `When`, at ~1 week of cost because it reuses the binding fork. The hybrid is not "selectors instead of triggers"; it is "triggers reduced to the one conjunct selectors can't express."

### Steelman: pure Avalonia selectors (no `When`, full grammar)

*The strongest case:* One mechanism, zero exceptions. Avalonia ships today without DataTriggers and real applications get built: viewmodel state flows into styling by (a) controls mapping VM-bound properties to pseudo-classes, (b) property-value selectors (`Button[IsDefault=true]`), or (c) code toggling `Classes` — and Avalonia 11's `Classes.bind` syntax makes (c) declarative. Meanwhile the full grammar (`:not`, `:nth-child`, property selectors) costs little *at terminal scale* — 200 elements means even naive re-match-everything invalidation is affordable, so why curate? Adopting Avalonia's exact grammar also buys ecosystem familiarity and documentation transfer. Adding `When` is a second activation vocabulary: now authors must decide "pseudo-class mapping, class binding, or When?" — a style-guide problem the pure model doesn't have.

*Rebuttal:* Look at what each workaround actually is. (a) requires the *control author* to anticipate every VM state an *app author* might style — `IsDirty`, `IsBusy`, `SyncState == Conflict` are application concepts; baking them into controls as properties-plus-pseudo-classes inverts the dependency. (b) property-value selectors only see *control* properties, so you must first bind the VM value onto some property to select on it — a binding plus a selector to express what `When` says in one clause; and they reintroduce per-value-change selector re-evaluation, the most invalidation-hostile feature in Avalonia's grammar (we'd have to build value-watch invalidation *anyway* — `When` is that same machinery with a clearer name and no grammar surface). (c) class-toggling from code or `Classes.bind` *is* a DataTrigger, just relocated out of the style into per-element markup — N buttons need N bindings, where one `When` style covers the type. On grammar size: the cost of `:nth-child`/sibling selectors isn't match time (agreed, trivial at our scale) — it's that **invalidation becomes a function of sibling-list mutation**, entangling the styling engine with the layout fork's child-collection internals and breaking the "flips are local to one element" property that gives us the zero-allocation hot path. We are not curating to save microseconds; we are curating to keep the invalidation graph exactly: *element-local bits + explicit ancestor edges + explicit binding watchers*. Every grammar feature that respects that graph is in; everything that doesn't is out, with classes as the documented escape hatch (`.first`, `.odd` assigned by the panel that knows the positions). And the "two vocabularies" critique cuts the other way: *tree state* and *application state* are genuinely different things — Avalonia's own ecosystem keeps reinventing DataTriggers (Behaviors' `DataTriggerBehavior`) because the distinction is real. The hybrid names it instead of hiding it.

**The honest accounting** of "smaller than the sum": versus WPF we delete `Trigger`, `MultiTrigger`, `MultiDataTrigger`, `EventTrigger`, `TriggerAction`, Enter/ExitActions, `Setter.TargetName`, three of four style-related priority slots, and keyed-style-per-element ceremony; we add a selector grammar of 3 combinators and 6 simple-selector forms. Versus Avalonia we delete property-value selectors, `:not`, `:nth-*`, sibling combinators, selector lists, and `ControlTheme` as a distinct type; we add `DataCondition` (~200 lines over Fork A's bindings). One predicate, one slot, one sort key, one retraction path — that is the whole system, and every requirement in this fork's brief lands inside it.