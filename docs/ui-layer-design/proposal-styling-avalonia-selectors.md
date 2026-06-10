# Cursorial.UI Styling — Fork B Proposal: Selector-Based Styling (Avalonia Model)

**Scope:** requirements 1 (rich styling & templating), 3 (resource/style inheritance), 8 (setters + selectors). Namespace: everything public lives in `Cursorial.UI` regardless of folder (matching `Cursorial.Core.Input` / `Cursorial.Drawing` convention). All code targets net10.0, nullable, latest C#.

---

## 1. Executive summary & philosophy

I propose CSS-style selectors with pseudo-classes, Avalonia-shaped: `Style` = `Selector` + `Setters` (+ nested children styles), `Classes` on every element, `ControlTheme` as the template+style bundle, and a **match-once / activate-forever** engine that compiles each conditional selector into a tiny subscription object (an *activator*) at tree-attach time, so interactive state changes (`:focus`, `:pointerover`, `:pressed`) never re-run selector matching — they flip a pre-built value frame in the property store.

Why selectors and not WPF triggers, in one paragraph: a trigger is a *private* conditional attached to one style instance that one element must explicitly adopt; a selector is a *published* rule the tree adopts by shape. For theming — the actual hard problem at requirement-1/3 scale — the publication model wins: a theme is a flat list of rules you can drop in, override by appending, and scope by tree position, instead of a web of `BasedOn` chains where every customization re-keys and re-bases a style and every state×state combination becomes a `MultiTrigger`. Selectors also *subsume* WPF's implicit-style mechanism (an implicit style is just the degenerate selector `Button`), so we ship one matching engine instead of two attachment systems. Crucially, the runtime cost objection to CSS does not apply here: matching happens once at attach; **state flips are O(subscribed activators on that element)**, not O(rules × elements) — exactly the discipline a 50 fps terminal loop with allocation budgets needs.

Philosophy constraints inherited from the stack: the styling layer is the *owner-driven invalidation authority* the Drawing layer demands (`Scene.Invalidate()` is coarse; styling tells it when), values flow into Fork A's property store through declared priority slots and retract without residue, and everything is single-render-thread with immutable, shareable style objects (seal-on-attach, like WPF freezables but without the ceremony).

---

## 2. Public API sketch

### 2.1 Selectors

```csharp
namespace Cursorial.UI;

/// <summary>An immutable, parsed selector. Built fluently or via Parse.</summary>
public abstract class Selector
{
    /// <summary>Parse "Button.primary:pointerover > TextBlock#label". Throws SelectorParseException
    /// with position info. typeResolver maps bare type names (and xmlns-prefixed names from XAML)
    /// to CLR types; null = the engine's default registry (Cursorial.UI controls + registered assemblies).</summary>
    public static Selector Parse(string selector, ISelectorTypeResolver? typeResolver = null);

    public abstract override string ToString();   // round-trips to canonical grammar

    // internal surface (engine-only):
    internal abstract SelectorMatch Match(StyledElement target, MatchContext context);
    internal abstract Type? TargetTypeFilter { get; }      // rightmost type constraint, for indexing
    internal abstract bool   InTemplateScope { get; }      // contains a /template/ combinator
}

/// <summary>Resolves selector type names. Supplied by the XAML fork per-document; a default
/// reflection-backed registry exists for code-first use.</summary>
public interface ISelectorTypeResolver
{
    Type? ResolveType(ReadOnlySpan<char> name);
}

/// <summary>Fluent, refactor-safe construction — the primary API for code-first consumers;
/// the string grammar is sugar over this.</summary>
public static class Selectors
{
    public static Selector OfType<T>() where T : StyledElement;          // exact StyleKey match
    public static Selector OfType(Type type);
    public static Selector Is<T>() where T : StyledElement;              // assignable match
    public static Selector Is(Type type);
    public static Selector Nesting();                                    // '^' — parent style's selector

    public static Selector Class(this Selector? previous, string name);  // ".primary"
    public static Selector Name(this Selector? previous, string name);   // "#label"
    public static Selector PseudoClass(this Selector? previous, string name); // ":pointerover" (with colon)
    public static Selector PropertyEquals(this Selector? previous, StyleProperty property, object? value); // "[IsDefault=true]"
    public static Selector Not(this Selector? previous, Selector argument);   // ":not(.primary)" — compound arg only
    public static Selector Child(this Selector previous);                // " > "
    public static Selector Descendant(this Selector previous);           // " " (whitespace)
    public static Selector Template(this Selector previous);             // " /template/ "
    public static Selector Or(params ReadOnlySpan<Selector> selectors);  // "A, B"

    // Sugar for the standard pseudo-classes (refactor-safe, no string literals at call sites):
    public static Selector PointerOver(this Selector s) => s.PseudoClass(StdPseudoClasses.PointerOver);
    public static Selector Pressed(this Selector s)     => s.PseudoClass(StdPseudoClasses.Pressed);
    public static Selector Focus(this Selector s)       => s.PseudoClass(StdPseudoClasses.Focus);
    public static Selector FocusWithin(this Selector s) => s.PseudoClass(StdPseudoClasses.FocusWithin);
    public static Selector Disabled(this Selector s)    => s.PseudoClass(StdPseudoClasses.Disabled);
    public static Selector Checked(this Selector s)     => s.PseudoClass(StdPseudoClasses.Checked);
}
```

**String grammar** (the parser is span-based, allocation only for the node objects, parsed once at theme load):

```
selector-list   := selector (',' selector)*
selector        := compound ( combinator compound )*
combinator      := '>'                  // child
                 | WS                   // descendant
                 | '/template/'         // cross into target's expanded template
compound        := [ type ] simple*     // at least one of type or simple required
type            := IDENT                // exact StyleKey match: "Button"
                 | ':is(' IDENT ')'     // assignable match
                 | '^'                  // nesting (only inside Style.Children)
                 | ':root'              // the visual root (Window content host)
                 | '*'
simple          := '.' IDENT            // class
                 | '#' IDENT            // Name
                 | ':' IDENT            // pseudo-class
                 | ':not(' compound ')'
                 | '[' IDENT '=' VALUE ']'   // property selector (StyleProperty equality)
```

`:nth-child()` is deliberately deferred (Phase 4, §7); everything else above ships.

### 2.2 Style, Setter, ControlTheme

```csharp
public interface IStyle { }   // marker: Style, ControlTheme, and theme bundles (Styles collections)

public class Style : IStyle
{
    public Style();
    public Style(Func<Selector?, Selector> selector);    // new Style(x => x.OfType<Button>().Class("primary"))

    public Selector? Selector { get; set; }              // null only for ControlTheme / pure-resource styles
    public SetterCollection Setters { get; }             // IList<SetterBase>; last setter per property wins
    public StyleCollection Children { get; }             // nested styles; selectors must start with Nesting()/'^'
    public ResourceDictionary Resources { get; }         // style-scoped resources (DynamicResource inside setters)
    public StyleAnimationCollection Animations { get; }  // storyboards run while the selector is active (Fork D contract)

    /// <summary>Called on first attach; freezes Setters/Children. Mutation after seal throws
    /// InvalidOperationException — styles are immutable shared state once live.</summary>
    public void Seal();
    public bool IsSealed { get; }
}

public abstract class SetterBase
{
    internal abstract void Realize(ValueFrameBuilder frame, StyledElement target);
}

public sealed class Setter : SetterBase
{
    public Setter();
    public Setter(StyleProperty property, object? value);

    public StyleProperty Property { get; set; }  // Fork A's property descriptor (see §5)
    /// <summary>Constant (boxed ONCE at seal time, shared by every applied element),
    /// IBinding (instantiated per element on first activation), DynamicResourceExtension
    /// (subscribed per element on activation), or ITemplate (instantiated by the property's consumer).</summary>
    public object? Value { get; set; }
}

/// <summary>The template + default-look bundle for a control type. Resolved implicitly from
/// resources by StyleKey, or set explicitly via StyledElement.Theme.</summary>
public class ControlTheme : Style
{
    public ControlTheme();
    public ControlTheme(Type targetType);

    public Type TargetType { get; set; }          // implicit selector = Is(TargetType)
    public ControlTheme? BasedOn { get; set; }    // setter-merging inheritance; chain flattened+cached at seal
}

public class StyleCollection : Collection<IStyle>   // element.Styles / app.Styles; owner-notifying
{
    public ResourceDictionary Resources { get; }      // e.g. a theme bundle's palette
}
```

**Attachment model** (requirement: implicit by type, explicit by key/class):

| Mechanism | How |
|---|---|
| Implicit by type | `ControlTheme` found in tree-scoped resources keyed by `element.StyleKey` (defaults to `GetType()`; a subclass can override `StyleKey => typeof(Button)` to inherit Button's theme). |
| Explicit theme | `element.Theme = (ControlTheme)res;` — overrides implicit lookup. |
| Rule-based | `Style`s in `Application.Styles`, any ancestor's `Styles`, or `element.Styles`; matched by selector. |
| Variants "by key" | **Classes**, not keyed styles: `<Button Classes="primary danger">`. A WPF-style keyed Style + per-element `Style` property is deliberately absent — `Classes` + a `.primary` rule is the same capability with composition (an element can wear several variants at once; keyed styles can't). |

### 2.3 Classes & pseudo-classes

```csharp
/// <summary>An element's style classes. Strings are interned on add. Pseudo-classes (":"-prefixed)
/// live in the same set but are settable only through the protected PseudoClasses accessor —
/// user code cannot fake ":pressed".</summary>
public sealed class Classes : IReadOnlyList<string>
{
    public bool Contains(string name);
    public void Add(string name);                       // throws ArgumentException on ':' prefix
    public void Remove(string name);
    public void Set(string name, bool present);         // idempotent toggle
    public void Replace(IReadOnlyList<string> classes); // batch swap, one notification
    public void Bind(string name, IBinding<bool> binding);  // the DataTrigger replacement (see §4)

    internal event ClassesChangedHandler? Changed;      // (string @class, bool added) — engine-only
}

/// <summary>Protected accessor on StyledElement (Fork A hosts it; we define it):
/// control authors drive control-state pseudo-classes; framework services
/// (focus manager, input router) drive the interaction ones.</summary>
public readonly struct PseudoClassAccessor
{
    public void Set(string name, bool present);         // name must start with ':'
}

public static class StdPseudoClasses
{
    public const string PointerOver = ":pointerover";
    public const string Pressed     = ":pressed";
    public const string Focus       = ":focus";
    public const string FocusWithin = ":focus-within";
    public const string Disabled    = ":disabled";
    public const string Checked     = ":checked";
    public const string Indeterminate = ":indeterminate";
    public const string Selected    = ":selected";       // list/tab items
    public const string Active      = ":active";          // element belongs to the active window
    public const string Open        = ":open";             // dropdowns/expanders
    public const string Empty       = ":empty";            // no logical children
}
```

### 2.4 Resources (requirement 3)

```csharp
public class ResourceDictionary : IResourceProvider, IDictionary<object, object?>
{
    public IList<IResourceProvider> MergedDictionaries { get; }
    public IDictionary<ThemeVariant, IResourceProvider> ThemeDictionaries { get; }
    public bool TryGetResource(object key, ThemeVariant? theme, out object? value);
    public event EventHandler? Invalidated;             // any mutation / merged-dict change
}

public interface IResourceHost           // implemented by StyledElement, Application, Style
{
    ResourceDictionary? Resources { get; }
    IResourceHost? ResourceParent { get; }              // logical parent; Application is the root
    event EventHandler? ResourcesChanged;               // propagated down-tree (see §3.6)
}

public readonly record struct ThemeVariant(string Key)
{
    public static ThemeVariant Default { get; }   // follow detected
    public static ThemeVariant Dark { get; }
    public static ThemeVariant Light { get; }
    /// <summary>Terminal-native detection: luminance of ColorCapabilities.DefaultBackground
    /// (OSC 11 readback); unknown ⇒ Dark (the terminal default).</summary>
    public static ThemeVariant Detect(TerminalCapabilities capabilities);
}

public static class ResourceExtensions
{
    // Lookup order: element.Resources → each Style applied from nearer hosts → logical ancestors → Application.
    public static bool TryFindResource(this StyledElement element, object key, out object? value);
    public static object? FindResource(this StyledElement element, object key); // throws KeyNotFoundException
}

/// <summary>{StaticResource Key}: resolved once at XAML-parse/attach against the lexical resource
/// stack; becomes a plain constant in the Setter. {DynamicResource Key}: a live reference —
/// re-resolves on ResourcesChanged (dictionary mutation, theme-variant flip, tree move).</summary>
public sealed class DynamicResourceExtension(object key) { public object Key { get; } = key; }
```

### 2.5 Template interaction surface (shared contract; full presenter design is Fork A's)

```csharp
public interface ITemplate<out T> { T Build(StyledElement templatedParent); }

public class ControlTemplate : ITemplate<StyledElement>
{
    public Func<StyledElement, StyledElement>? Build { get; set; }   // code-first
    // XAML fork compiles <ControlTemplate> content into Build.
    StyledElement ITemplate<StyledElement>.Build(StyledElement templatedParent);
}
```

- Elements created by a template carry `TemplatedParent`.
- **Template barrier:** a selector never matches an element with non-null `TemplatedParent` *unless* the selector crosses via `/template/`, and the segment left of `/template/` must match that element's `TemplatedParent`. Templates are encapsulated by default; themes opt in to reaching inside their *own* template with `^ /template/ …`.
- `{TemplateBinding Background}` (XAML fork sugar) = one-way binding to `TemplatedParent`'s property, injected at the `Template` priority slot.

### 2.6 Consumer example

Code-first (library-first project; XAML is sugar, never required):

```csharp
// ---- A Button control theme: template + default look + interactive states ----
var buttonTheme = new ControlTheme(typeof(Button))
{
    Setters =
    {
        new Setter(Button.ForegroundProperty,  new DynamicResourceExtension("Text.Primary")),
        new Setter(Button.BackgroundProperty,  new DynamicResourceExtension("Surface.Raised")),
        new Setter(Button.BorderPenProperty,   Pens.Rounded),                  // Pen boxed once, shared
        new Setter(Button.PaddingProperty,     new Margins(2, 0)),
        new Setter(Button.TemplateProperty,    new ControlTemplate
        {
            Build = parent => new Border
            {
                Name = "frame",
                Child = new ContentPresenter { Name = "content" },
            },
        }),
    },
    Children =
    {
        new Style(x => x.Nesting().PointerOver())
            { Setters = { new Setter(Button.BackgroundProperty, new DynamicResourceExtension("Surface.Hover")) } },
        new Style(x => x.Nesting().Pressed())
            { Setters = { new Setter(Button.BackgroundProperty, new DynamicResourceExtension("Surface.Pressed")) } },
        new Style(x => x.Nesting().Focus().Template().OfType<Border>().Name("frame"))
            { Setters = { new Setter(Border.BorderPenProperty, Pens.Heavy.WithColor(Color.FromHex("#66d9ef"))) } },
        new Style(x => x.Nesting().Disabled())
            { Setters = { new Setter(Button.ForegroundProperty, new DynamicResourceExtension("Text.Disabled")) } },
    },
};

// ---- App-level rules: a "primary" variant + capability-adaptive override ----
app.Styles.Add(new Style(x => x.OfType<Button>().Class("primary"))
    { Setters = { new Setter(Button.BackgroundProperty, new DynamicResourceExtension("Accent")) } });

// On 16-color terminals, focus rings can't rely on RGB accents — use Inverse instead.
// ".caps-ansi16" is set on the root by the framework from negotiated capabilities (§6).
app.Styles.Add(new Style(x => Selectors.Parse(":root.caps-ansi16 Button:focus"))
    { Setters = { new Setter(Button.TextAttributesProperty, TextAttributes.Inverse) } });

app.Resources[typeof(Button)] = buttonTheme;          // implicit theme by StyleKey

// ---- Usage ----
var ok = new Button { Content = "_OK", Classes = { "primary" } };
```

Same thing in XAML (requirement 7 touch point — Selector has a TypeConverter calling `Selector.Parse` with the document's namespace resolver):

```xml
<Application.Styles>
  <Style Selector="Button.primary">
    <Setter Property="Background" Value="{DynamicResource Accent}"/>
    <Style Selector="^:pointerover">
      <Setter Property="Background" Value="{DynamicResource AccentHover}"/>
    </Style>
  </Style>
  <Style Selector=":root.caps-ansi16 Button:focus">
    <Setter Property="TextAttributes" Value="Inverse"/>
  </Style>
</Application.Styles>

<ControlTheme x:Key="{x:Type Button}" TargetType="Button">
  <Setter Property="Template">
    <ControlTemplate>
      <Border Name="frame" BorderPen="{TemplateBinding BorderPen}"
              Background="{TemplateBinding Background}">
        <ContentPresenter Name="content"/>
      </Border>
    </ControlTemplate>
  </Setter>
  <Style Selector="^:focus /template/ Border#frame">
    <Setter Property="BorderPen" Value="Heavy #66d9ef"/>
  </Style>
</ControlTheme>
```

---

## 3. Internal architecture

### 3.1 The two-phase model: match once, activate forever

Matching is split into a **static phase** (at logical-tree attach) and a **dynamic phase** (activators):

- Static facts about an element — its `StyleKey`, its `Name`, its tree position, its `TemplatedParent` — are fixed for the element's attached lifetime (cross-fork contract: `Name` is immutable after attach; re-parenting = detach + attach). Selector components over static facts are evaluated exactly once.
- Dynamic facts — classes, pseudo-classes, `[Property=Value]` — compile into **activators**: pre-built subscription nodes that observe exactly the state they test and toggle a pre-built value frame.

```csharp
internal enum SelectorMatchKind : byte { NeverThisType, NeverThisInstance, AlwaysThisInstance, Conditional }

internal readonly struct SelectorMatch
{
    public SelectorMatchKind Kind { get; }
    public IStyleActivator? Activator { get; }      // non-null iff Conditional
}

internal interface IStyleActivator : IDisposable    // disposal = unsubscribe everything
{
    bool IsActive { get; }
    void Initialize(IStyleActivatorSink sink);      // sink = the value frame
}
```

Activator node types: `ClassActivator` (one element, one interned class string), `PropertyActivator` (one element, one `StyleProperty`, one boxed expected value), `AndActivator` (fixed child array, counts active children — O(1) per child flip), `OrActivator` (for descendant combinators over multiple candidate ancestors and for `Or` selector lists), `NotActivator`. All are small sealed classes allocated **once at attach**; steady-state flips allocate nothing.

### 3.2 Data structures

**Per `StyleCollection`: a type-keyed candidate cache.**

```csharp
// Built lazily per concrete StyleKey encountered; invalidated when the collection mutates.
Dictionary<Type, Style[]> _candidatesByType;
```

For each new concrete type, every style in the collection is classified once: `NeverThisType` results are excluded permanently for that type (this is where `Button`-vs-`:is(Button)` filtering, and most of the theme's bulk, drops out). Subsequent attaches of the same type only evaluate the surviving candidates. A 150-rule theme typically leaves 5–15 candidates per concrete type.

**Per element: the applied-styles set.**

```csharp
// On StyledElement (engine-owned field):
internal AppliedStyles? _appliedStyles;

internal sealed class AppliedStyles      // one per styled element; pooled
{
    private InlineList<StyleFrame> _frames;          // typically 2–8 entries
}

internal sealed class StyleFrame : IValueFrame, IStyleActivatorSink
{
    private readonly Style _style;                   // setters NOT copied — shared reference
    private readonly IStyleActivator? _activator;    // null = unconditional
    private readonly StyledElement _owner;
    private object?[]? _realized;                    // per-element lazies: binding instances /
                                                     // dynamic-resource subscriptions, index-aligned
                                                     // with _style.Setters; null until first activation
    public ValuePriority Priority { get; }           // computed at attach (see §3.4)
    public bool IsActive => _activator?.IsActive ?? true;
    void IStyleActivatorSink.OnActivatorChanged() => _owner.PropertyStore.ReevaluateFrame(this);
}
```

Memory at scale: 200 elements × ~5 frames × (frame ≈ 48 B + activator graphs only on conditional frames ≈ 64–160 B) ≈ **50–100 KB total**, allocated at window construction, pooled on teardown. Constant setter values are boxed once per `Setter` at seal time and shared by all elements.

**Per element: class-change dispatch.** `Classes.Changed` notifies a per-element subscriber list; `ClassActivator`s register filtered by interned string, so a flip does one dictionary-free scan over that element's (few) class activators with reference-equality string compares.

### 3.3 Match algorithm

`Match(target, selector)` evaluates **right-to-left**:

1. Rightmost compound runs against `target`: type check (`StyleKey == T` or assignable for `Is`), `#name` string check, then dynamic simples — each dynamic simple contributes an activator node; static failures return `Never*` immediately.
2. `>` (child): continue matching the remaining selector against `LogicalParent` — static redirect, same algorithm.
3. ` ` (descendant): walk ancestors root-ward; for each ancestor where the static parts of the left selector match, collect its conditional residue; result = `AlwaysThisInstance` if any ancestor matches unconditionally, else `OrActivator` over the per-ancestor residues, else `Never`. Tree depth in terminal apps is ~5–12, so this walk is trivial.
4. `/template/`: verify `target.TemplatedParent` is non-null and match the left segment against it; conversely, the engine **skips** any selector lacking `/template/` for elements with a `TemplatedParent` (the barrier from §2.5) — checked before step 1, so template internals don't even scan the app's rule list. Exception: nested styles inside the element's *own* `ControlTheme`, whose `^` binds to the templated parent.
5. `^` (nesting): substitutes the parent style's selector subtree; nested styles therefore match iff parent-and-child conditions hold — implemented by chaining into the parent's compiled nodes, no re-evaluation duplication.
6. Multiple conditions in one compound AND together via `AndActivator`; `Or` lists and multi-ancestor descendant residues use `OrActivator`; `:not` wraps with inversion; `[Prop=Value]` produces a `PropertyActivator` whose expected value was converted and boxed at seal time.

**Attach flow** (per element entering the logical tree, driven by Fork A's `AttachedToLogicalTree`):

```
theme  = element.Theme ?? FindResource(element.StyleKey) as ControlTheme
frames = realize(theme chain, flattened BasedOn → base first)        // ControlTheme(+Trigger) slots
for host in [Application, ...ancestors root→parent, element]:        // outer first = weakest
    for style in host.Styles.CandidatesFor(element.StyleKey):
        match = style.Selector.Match(element, ctx)
        if match is AlwaysThisInstance → frame at Style slot
        if match is Conditional       → frame at StyleTrigger slot, activator initialized
element.PropertyStore.AddFrames(frames)                              // one batched store update
```

Order of insertion within a slot is the application order above, and **within a slot, later-added wins** — giving the cascade: element styles > ancestor styles > app styles > theme; later rules in the same collection > earlier ones. (Specificity is *deliberately* not computed — source order + slot is the whole story. CSS specificity arithmetic is the part of CSS nobody should import.)

### 3.4 Value injection: priority slots (the Fork A contract)

```csharp
public enum ValuePriority : byte        // ascending strength; defined here, implemented by Fork A
{
    Default = 0,            // property metadata default
    Inherited = 1,          // value inherited from logical parent (Foreground etc.)
    ControlTheme = 2,       // unconditional theme setters
    ControlThemeTrigger = 3,// theme setters under an activator (theme's :pointerover etc.)
    Style = 4,              // unconditional selector match
    StyleTrigger = 5,       // selector with activator
    Template = 6,           // TemplateBinding / template-local values
    LocalValue = 7,         // element.Background = …  (and two-way binding targets)
    Animation = 8,          // storyboard-applied values (Fork D)
}
```

A conditional style outranking an unconditional one (`StyleTrigger > Style`) is what makes `Button:pointerover` beat `Button.primary` without specificity math, and matches WPF/Avalonia intuition (triggers beat style setters). The split repeats inside themes so an app-level rule always beats the theme, *including* the theme's hover states — the property a theme author most often gets wrong in WPF.

The store contract:

```csharp
public interface IPropertyStore                       // Fork A
{
    void AddFrames(ReadOnlySpan<IValueFrame> frames); // batched: one effective-value pass
    void RemoveFrames(ReadOnlySpan<IValueFrame> frames);
    void ReevaluateFrame(IValueFrame frame);          // activation flipped: re-resolve only frame.Properties
}

public interface IValueFrame
{
    ValuePriority Priority { get; }
    bool IsActive { get; }
    int  EntryCount { get; }
    StyleProperty PropertyAt(int index);
    object? ValueAt(int index, StyledElement target); // constant box | binding-produced value | resource value
}
```

Effective value per property = highest-priority *active* frame entry, ties broken by insertion recency. Styling guarantees the store sees ≤ ~10 frames per element, each with ≤ ~10 entries — the store can keep frames in a flat priority-sorted array and scan; no per-property dictionaries needed at this scale.

### 3.5 Activation, retraction, lifecycle

- **Activate:** activator flips true → frame realizes lazies on first activation (bindings instantiated, `DynamicResource` subscriptions opened) → `ReevaluateFrame` → only that frame's properties re-resolve → changed properties raise Fork A property-changed → control metadata (`AffectsRender` / `AffectsMeasure` / `AffectsParentArrange`) translates to `Scene.Invalidate()` and/or layout invalidation. Multiple property changes on one element coalesce naturally — `Invalidate()` is idempotent and coarse.
- **Retract:** activator flips false → `ReevaluateFrame` → each of the frame's properties falls back to the next active frame down the priority stack → notify only where the *effective* value actually changed. Binding/resource subscriptions in the frame are **paused** (unsubscribed) on deactivation and resumed on reactivation, reusing instances — hover flicker doesn't churn binding objects.
- **Batching:** activator notifications are queued on the engine (a pooled ring buffer) and drained at a defined point in the UI tick — after input dispatch, before layout/render — so one input event that flips `:pressed` on a button and `:focus-within` on three ancestors produces one coherent re-evaluation pass and at most one re-raster per affected scene per frame. This is the same coalescing discipline the demos' `Invalidate()` flag uses.
- **Detach:** all frames removed in one `RemoveFrames` call, activators disposed (unsubscribing from `Classes.Changed` / property observers), `AppliedStyles` returned to its pool. No residue: the store never sees dangling frames, classes never hold dead subscriber entries.
- **Styles-collection mutation at runtime** (theme swap, plugin styles): coarse by design — the owning host raises `StylesInvalidated`; the engine re-runs attach for the host's subtree. Documented as "window-open cost, not per-frame cost" (§7 numbers). The type-keyed candidate caches are rebuilt lazily.

### 3.6 Resource change propagation

`ResourcesChanged` propagates down the logical tree (an element raises to children when its own dictionary invalidates or its parent re-raises — single event, no per-resource granularity, mirroring Avalonia). Each live `DynamicResource` subscription re-resolves on the event and pushes through its frame entry only if the resolved value differs (`object.Equals`). Theme-variant change (`app.RequestedThemeVariant = ThemeVariant.Light`) is just a `ResourcesChanged` from the root: every dynamic resource re-resolves against `ThemeDictionaries[variant]`; styles and frames are untouched — **no re-matching for a theme-variant flip**, only value pushes. This is the cheap path that makes light/dark runtime switching viable on a 200-element screen (~hundreds of dictionary lookups + the re-rasters of elements whose colors changed).

`StaticResource` resolves during XAML parse against the lexical stack (XAML fork supplies the stack walker) or eagerly at `Seal()` for code-first; it costs nothing at runtime and cannot retract — documented guidance: themes use `DynamicResource` for palette entries, `StaticResource` for structural values (templates, converters).

### 3.7 End-to-end flow: pseudo-class flip on a 200-element screen

Pointer moves onto a button (input fork hit-tests `MouseEventKind.Move`, already cheap per the input layer's any-event tracking):

1. Input router: `button.PseudoClasses.Set(":pointerover", true)` (+ `Set` on old element false). **2 class-flip notifications.**
2. Each notifies only that element's subscribed `ClassActivator`s — say 3 (theme hover, app hover, focus-ring `:not`). Reference-compare interned strings; AND-nodes update counters. **O(activators-on-that-element), zero allocation.**
3. Engine drains the activation queue at tick end: 2 frames re-evaluated, ~3 properties re-resolved, 1 actually changes (`Background`).
4. Property-changed → `AffectsRender` → `button.Scene.Invalidate()`.
5. Next frame: one widget scene re-rasters (the expensive part — brush sampling over the button's ~30 cells), compositor recomposites the button's footprint union, `FrameRenderer` diffs and emits only the cells whose quantized style changed.

The styling layer's share of that frame is **a few microseconds and zero garbage**; raster + diff dominate, exactly as the Drawing layer intends. The other 199 elements are never visited — no selector is re-run anywhere. Contrast both naive CSS (re-match on state change) and WPF (comparable trigger cost per element, but see §8 for where triggers lose anyway).

---

## 4. Requirement satisfaction

**Req 1 — rich styling & templating.** Full setter vocabulary over Fork A properties with terminal-native value types (`IBrush`, `Pen`, `TextAttributes`, `Margins`, `GlyphSet`, `CursorShape`); `ControlTheme` bundles template + default look + state styles per control type; template-internal styling via `^ /template/ Border#frame`; `TemplateBinding` at the `Template` slot; per-element variant composition via `Classes`; style-scoped `Resources`; selector-activated `Animations` (Fork D executes; we provide the rising/falling edge). Templates are encapsulated by the template barrier, so themes can't accidentally restyle another control's internals.

**Req 3 — resource/style inheritance.** Tree-scoped resource lookup (element → applied styles → ancestors → application) with `StaticResource`/`DynamicResource` semantics; `ThemeDictionaries` keyed by `ThemeVariant` with terminal-native variant detection; `ControlTheme.BasedOn` for theme inheritance (flattened at seal: base setters apply first, derived overrides win, nested children concatenate); `StyleKey` overriding for "subclass inherits base look"; *rule* inheritance is composition — classes stack (`Classes="primary compact"`), nested styles refine, and tree position scopes. Property-value inheritance (Foreground flowing to children) is Fork A's `Inherited` slot, deliberately below all styling slots.

**Req 8 — setters + the selector mechanism in full.** Covered: type / `:is()` / `.class` / `#name` / pseudo-classes / `:not()` / `[Property=Value]` / child / descendant / `/template/` / `,` lists / `^` nesting / `:root`. WPF-trigger parity mapping, stated explicitly:

| WPF construct | Selector equivalent |
|---|---|
| `Trigger Property=IsMouseOver Value=True` | `:pointerover` (control-state pseudo-class) or `[Prop=Value]` for arbitrary properties |
| `MultiTrigger` | compound selector: `Button.primary:focus:pointerover` — one token per condition |
| `DataTrigger` | **class binding**: `Classes.Bind("urgent", binding)` / XAML `Classes.urgent="{Binding IsUrgent}"` — the data condition becomes a class, every rule for `.urgent` lights up; or bind data into a property and use `[Prop=Value]` |
| `MultiDataTrigger` | several bound classes in one compound: `.urgent.unread` |
| `EventTrigger` | `Style.Animations` on activator rising edge; discrete event→storyboard wiring is Fork D's, keyed off the same activator signal |
| `Trigger.EnterActions/ExitActions` | activator edges drive storyboard start/stop with handoff (Fork D contract) |

**Touch points.** Req 7: `Selector.Parse` + `ISelectorTypeResolver` + markup extensions are the exact plumbing the XAML fork needs; everything is equally constructible without XAML. Req 9: §3.4 is a precise statement of which slots styling occupies and how values retract — Fork A implements the store once and both forks meet at `IValueFrame`. Req 10: styles host `Animations` and the property system's `Transitions` route style-driven property changes through `IAnimation<T>` instead of snapping (pure mechanism below, orchestration in UI, per the Drawing doc's §7 split).

---

## 5. Cross-fork contract (explicit)

**From Fork A (properties, binding, element base) I require:**

```csharp
public abstract class StyledElement      // Fork A owns; styling needs these members
{
    public virtual Type StyleKey => GetType();
    public string? Name { get; init; }                  // IMMUTABLE after logical attach
    public Classes Classes { get; }                      // type defined by Fork B, hosted here
    protected PseudoClassAccessor PseudoClasses { get; }
    public ControlTheme? Theme { get; set; }
    public StyleCollection Styles { get; }
    public ResourceDictionary? Resources { get; set; }   // lazy-created
    public StyledElement? LogicalParent { get; }
    public StyledElement? TemplatedParent { get; }
    public IPropertyStore PropertyStore { get; }         // §3.4 interface, slots per ValuePriority
    internal event …  AttachedToLogicalTree / DetachedFromLogicalTree;   // styling attach/detach hook
    // Property observation for [Property=Value] activators and class bindings:
    public IDisposable ObserveProperty(StyleProperty property, Action<object?> observer);
}
```

Plus: `StyleProperty` descriptors expose a value-converter hook (for parsing `[IsDefault=true]` and XAML setter values) and metadata flags (`AffectsRender`, `AffectsMeasure`, `Inherits`); `IBinding` exposes `IDisposable Instantiate(StyledElement target, Action<object?> push)` so setter bindings and class bindings can pause/resume; property-changed callbacks fire synchronously on the UI thread; the store tolerates `AddFrames`/`ReevaluateFrame` reentrancy from within change callbacks by deferring (queue), never recursing.

**From Fork C (focus, windows, input routing, access keys) I require calls into `PseudoClasses`/`Classes`, on the UI thread, between frames:**

- FocusManager: `:focus` on the focused element, `:focus-within` on its ancestor chain (set/clear on focus moves; clear all on `FocusEvent { HasFocus: false }`).
- Input router: `:pointerover` on the hit-test chain (enter/leave), `:pressed` between ButtonDown and ButtonUp/capture-loss; **must clear both** on terminal focus loss and on capability absence (no `MouseCapabilities.Motion` ⇒ `:pointerover` simply never sets — styles must already work without it, see §6).
- Window manager: `:active` on the active window's subtree root; a class (e.g. `obscured`) on windows behind a modal — dimming is then a pure theme concern (`Window.obscured` rules), not a compositor hack.
- Access keys (req 6): the Alt-mode tracker sets a class on the window root (e.g. `alt-mode`) when Alt goes down (capability-gated per the input reference §7) — `AccessKeyText.alt-mode-visible` styling falls out as ordinary descendant rules: `:root.alt-mode AccessKeyText { Setter Attributes=Underline }`, with the permanent-underline fallback selected by capability class (§6). No styling-engine special cases.

**From Fork D (animation orchestration):** consume `IStyleActivator` edges via `StyleAnimationCollection`; storyboard-applied values enter at `Animation` priority through their own frames; on falling edge, stop with snapshot-handoff (`old.ValueAt(elapsed)` becomes the transition's `From`). `Transitions` metadata intercepts style-slot value changes.

**From the XAML fork:** per-document `ISelectorTypeResolver`; `Setter.Property` string resolution against the enclosing `Style`'s target-type context; markup-extension protocol producing my `DynamicResourceExtension` and Fork A's `IBinding`; `<ControlTemplate>` compilation into `ControlTemplate.Build`.

**What I guarantee to others:** styles/themes are immutable after seal and safely shareable across windows; all engine work happens on the UI thread; frames are added/removed in balanced batches; pseudo-class names are interned constants; the engine never touches `Scene`/`CellBuffer` directly — it only raises property changes and lets control metadata drive invalidation (the styling layer cannot violate the compositing invariant because it never composites).

---

## 6. Terminal-specific adaptations

1. **Capability classes on the root.** At session open the framework stamps the visual root from `TerminalCapabilities`: `caps-truecolor|ansi256|ansi16|nocolor`, `caps-mouse`, `caps-motion`, `caps-kitty-keyboard`, `caps-images`, `caps-unicode|ascii` (glyph-set policy — a *consumer* knob per the Pen design, so the theme owns it). Themes adapt with ordinary descendant rules (`:root.caps-ansi16 Button:focus { Attributes=Inverse }`, `:root.caps-ascii Border { BorderPen={StaticResource AsciiPen} }`). On `RenegotiateAsync` the classes are re-stamped and exactly the dependent rules re-activate — capability adaptation reuses the activator machinery instead of inventing a parallel system. This is something WPF triggers cannot express without binding every control to a global capabilities object.
2. **Hover is optional; focus is the spine.** `:pointerover`/`:pressed` are progressive enhancement — never the only carrier of state. Shipped themes pair every hover rule with a `:focus` rule; the docs make this a hard theme-authoring rule because `MouseCapabilities.Motion` may be false and many users are keyboard-only. (`:focus-visible` is dropped: terminal focus is always keyboard-meaningful.)
3. **Theme variants from the terminal.** `ThemeVariant.Detect` reads `ColorCapabilities.DefaultBackground` (OSC 11 readback) luminance; unknown ⇒ Dark. Variant swap is a resource event, not a restyle (§3.6).
4. **Quantize at emit, not in styles.** Themes author truecolor once; `StyleQuantizer` + optional ordered dither degrade at the `FrameRenderer`. Capability classes exist for the cases quantization can't fix (state distinctions collapsing at Ansi16 ⇒ switch to `TextAttributes`-based emphasis), not for routine palette work.
5. **Cell-grid value vocabulary.** No `CornerRadius`, no `FontSize`, no sub-cell anything: setters traffic in `Pen` (weight = glyph family), `Margins` (whole cells), `TextAttributes`, `IBrush` (sampled per cell at raster). Geometry stays integer; `Rect`'s ushort/non-negative constraints never reach style authors because styles set sizes via layout properties, not rects.
6. **Restyle granularity matches scene granularity.** `Scene.Invalidate()` is whole-widget; therefore frame entries don't need per-cell dirty info — the engine's batching (§3.5) guarantees ≤ 1 invalidation per element per tick, and the two cache tiers below (scene raster cache, renderer diff) absorb the rest. Style changes that affect only `CompositeParameters`-expressible things (opacity/offset of a floating window) are routed by control implementations to re-composite, not re-raster — styling just changes the property; the control decides which lever.
7. **Attribute-first affordances.** Standard control themes lean on what every terminal has: `Inverse` for selection, `Bold`/`Faint` for emphasis/disabled, underline shapes (capability-gated by the quantizer) for focus — color is layered on top. This keeps the *same* theme acceptable from xterm-16 to Kitty-truecolor.

---

## 7. Costs, risks, phasing

**Effort estimate** (one engineer, with Fork A's store stubbed early): Phase 1 ≈ 3–4 weeks, Phase 2 ≈ 2–3 weeks, Phase 3 ≈ 2 weeks, following the repo's playbook (living design doc, phase table, adversarial review before Phase 2).

- **Phase 1 — core engine (the v1 spine):** `Selector` nodes + fluent API (type/class/name/pseudo, child/descendant, `:not`/`:is`/`Or`), `Style`/`Setter`/seal, `Classes`, activators, frames into the property store, attach/detach lifecycle, type-keyed candidate caches, batching queue. Code-first only. Exit criterion: hover/focus restyle on a synthetic 200-element tree with zero steady-state allocation (assert with `GC.GetAllocatedBytesForCurrentThread` in tests, per the project's oracle-pinning habit).
- **Phase 2 — themes & resources:** `ControlTheme` + `BasedOn` + implicit resolution + `StyleKey`; template barrier + `/template/` + `^` nesting; `ResourceDictionary`, Static/Dynamic resources, `ThemeDictionaries` + variant detection; capability classes.
- **Phase 3 — integration surface:** string parser (+ canonical `ToString` round-trip tests), `[Property=Value]` activators, class bindings, `Style.Animations` edge contract with Fork D, XAML type-converter hookups.
- **Phase 4 (punt list, recorded §11-style):** `:nth-child`/sibling combinators (needs ordered-children change notification from Fork A); selector-level media-query sugar (`@media (depth: ansi16)` — capability classes already cover it); per-resource-key granular invalidation (coarse `ResourcesChanged` is fine at this scale); style hot-reload for a dev inner loop; palette-side theming via `TerminalPalette` OSC 4 (redefine palette indices instead of restyling — cheaper on huge trees, but global).

**Perf characteristics & worst cases.**

| Operation | Cost |
|---|---|
| Pseudo-class flip (steady state) | O(activators on that element) ≈ 2–6 evals; 0 alloc; ~µs |
| Window open, 200 elements | per element: theme chain + ~5–15 candidates × cheap right-to-left match ⇒ ~2–4 k selector evaluations ≈ 1–3 ms one-time + first raster (dominates anyway) |
| Theme-variant flip | resource re-resolution only (no re-match) + re-raster of recolored widgets |
| Runtime `Styles` mutation / theme swap | full subtree re-match (= window-open cost) + full re-raster; documented as an explicit-user-action operation |
| Class added on a container with descendant rules | re-evals only the subscribed descendant activators (those styles' residues), not the subtree |

**Risks, honestly:** (1) *Engine complexity* — activators + frames + batching is the single most intricate piece of Cursorial.UI; mitigated by Phase 1's narrow scope, determinism (single thread, no timers), and the project's adversarial-review convention. (2) *Cascade debuggability* — "why is this cell cyan" needs tooling; mitigation: a diagnostic API from day one (`StyleDiagnostics.GetAppliedFrames(element)` dumping frame/priority/active/value provenance — cheap because frames already know everything) and a demo command rendering it. (3) *Fork A coupling* — the `IValueFrame` contract is the load-bearing seam; if the store's shape shifts, frames are the blast radius; mitigated by agreeing §3.4 before Phase 1 code. (4) *String selectors hide typos until runtime* — mitigated by parse-at-seal (theme load fails fast, with positions), the fluent API for code, and (later) a XAML-compile-time check in the XAML fork.

---

## 8. Steelman & rebuttal

**The strongest case for WPF Triggers/DataTriggers.**

1. *Locality and explicitness.* A WPF style is self-contained: setters, triggers, and their interactions sit in one object you can read top-to-bottom; an element opts in via one property (`Style`). Selectors are spooky action at a distance — any ancestor's `Styles` collection can restyle you, and understanding an element's final look means knowing the whole cascade. For a *library-first* project whose consumers embed widgets into unknown hosts, implicit reach-in is a real hazard.
2. *DataTriggers are a first-class data bridge.* `<DataTrigger Binding="{Binding Severity}" Value="Error">` styles directly off the view-model with full binding power (converters, paths) and no view cooperation. Selector systems make data styling second-class: you must launder data through classes or properties, which pollutes view code and splits the condition from the rule.
3. *Strong typing, no string DSL.* Triggers are object graphs: refactor-safe, analyzable, no parser to write/maintain/version, no type-resolution context needed, no grammar docs. A string selector breaks silently under rename refactoring and demands parser + resolver + tooling — real engineering weight this fork is volunteering to carry.
4. *Less machinery.* Triggers map straight onto the property system (a trigger is "watch property, push values at trigger priority"). No matching engine, no candidate caches, no activator graphs, no template barrier rules. For a terminal framework whose apps are hundreds of elements, the selector engine may be solving a web-scale problem that terminal scale never has — naive trigger evaluation would also be "fast enough."

**Rebuttal.**

1. *Locality:* nested styles give triggers' locality back — `^:pointerover` lives inside the style it modifies, exactly like a `Trigger`, with one concept instead of three (`Trigger`/`MultiTrigger`/`TriggerBase` collections). What selectors *add* is the other direction WPF can't do cleanly: rules that live with the **theme** rather than the element. And the hazard is fenced, not spooky: the template barrier stops cross-control reach-in, `StyleKey` gates implicit themes, and embedded-widget authors ship `ControlTheme`s (closed bundles) rather than open rules. Meanwhile WPF's own answer to theming-at-distance — implicit styles + `BasedOn` — *is* a selector system, just a crippled one (type-only, single-inheritance, famously breaking when a `BasedOn` chain crosses theme dictionaries). We replace it with the general mechanism instead of shipping the crippled one plus triggers.
2. *DataTriggers:* class bindings (`Classes.urgent="{Binding IsUrgent}"`) keep full binding power — converter, path, async — and the laundering is a feature: the bound class becomes a *named, reusable state* every theme can target, instead of a condition copy-pasted into each style that needs it. WPF's DataTrigger couples the style to the view-model's shape; the class decouples them (the theme styles `.urgent`; what *makes* something urgent is the view's business). For property-shaped conditions, `[IsDefault=true]` matches DataTrigger ergonomics one-for-one with the same activator machinery.
3. *Strings:* the string grammar is an optional façade over a typed fluent API — code-first consumers (this is a library, not a designer-tooling ecosystem) never write a string, and XAML consumers get parse-at-load failure with positions plus future compile-time validation. WPF's triggers are not actually refactor-proof either: `Property="IsMouseOver"` and `SourceName="frame"` are strings in XAML too. The honest accounting is "we write one ~600-line span-based parser"; in exchange every rule's condition is one line instead of a 10-line `MultiTrigger` element soup — across a real theme (Avalonia's Fluent: thousands of state rules) that's an order-of-magnitude markup reduction, which is requirement 1's actual day-to-day cost.
4. *Machinery:* the comparison isn't "selector engine vs nothing" — triggers need activator-equivalents too (every `Trigger` subscribes to property changes; `MultiTrigger` needs AND state; `DataTrigger` holds a binding; retraction needs the same frame discipline). The *delta* selectors add is attach-time matching + candidate caches — bounded, one-shot, ~1–3 ms per window at our scale (§7) — and it buys the theming/cascade model triggers structurally lack. On the hybrid option (selectors *and* triggers): two activation systems writing the same priority slots is the worst of both — two precedence stories to document, two retraction paths to test, and every theme author choosing per-rule. One mechanism, made complete (pseudo-classes + property selectors + class bindings + animation edges), covers the entire trigger feature matrix (§4 table); the hybrid's only honest payload is WPF familiarity, which the XAML fork can serve with documentation, not duplicated machinery.

The kernel of truth to keep: triggers' virtue is *explicitness*, and we adopt it where it matters — no specificity arithmetic (slot + source order only), a hard template barrier, sealed immutable styles, and a first-class "why this value" diagnostic. The cascade is powerful exactly once it's predictable; this design spends its complexity budget making it predictable rather than avoiding it.