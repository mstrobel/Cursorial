I have everything I need — all binding decisions, the maps, the fork proposals' load-bearing details (Fork C's converter/builder seams, `UIProperty.UnsetValue`, the styling layer model and `IStyleValueSink` shape). Writing the final spec now.

# S7 — Resources, Themes, and Dynamic Resolution — Subsystem Spec (FINAL)

Status: design for `Cursorial.UI` (namespace `Cursorial.UI`; theme assets in `Cursorial.UI.Themes`; XAML-facing media builders in `Cursorial.UI.Media`). Conforms to DECISIONS.md (Forks A/B/C as amended) and the named invariants. Vocabulary per the shared table: `UIObject`/`UIElement`/`Control`/`Window`, `BindingPriority`, `ValueFrame`, `StyleSortKey`, `ITemplateContent`. Incorporates the adversarial-critique fixes; disposition appended.

---

## 1. Scope

**This subsystem owns:**

- `ResourceDictionary` — keyed storage, `MergedDictionaries`, `ThemeDictionaries`, the typed `Styles` slot (the theme-styles channel for Fork B), the **deferred-entry runtime contract** consumed by Fork C's lazy node-graph slices, sealing, and the `Changed` pulse.
- The **lookup chain**: element → logical ancestors (crossing template barriers via the owning template's resources) → `Window` → `Application.Resources` → `Application.Theme` (active theme) → `CursorialTheme.BuiltIn` (built-in control-theme dictionary). Precise ordering, variant-aware probing, allocation-free walk, and the `ResourceParent` contract it requires from the element tree.
- **StaticResource vs DynamicResource runtime semantics**: the `IResourceScope` implementations the XAML loader's ambient stack consumes; `ResourceReference` as the DynamicResource currency; the per-root **subscription registry** with pause-not-destroy lifecycle and **snapshot-iterated sweeps**; re-resolution on pulses; the root-level `ResourcesChanged` notification and per-root monotonic **resource version** (the S8 cache-key contract).
- **`ThemeVariant`** = (Dark/Light base × `ColorDepth` tier), derivation from negotiated capabilities, explicit override, `RenegotiateAsync` re-derivation, and the resource-event-only flip.
- The **built-in default theme**: control templates/colors, the `ThemeKeys` palette vocabulary, the tier-key layout rules (§3.7), per-control and wholesale override paths, control-theme resolution service (feeding the styling engine's `ControlTheme` layer).
- **XAML-facing media builders** (`Cursorial.UI.Media`): parameterless-ctor, settable-member brush/pen shapes realized to immutable `Cursorial.Drawing` values via the `IResourceValueBuilder` seam — Drawing stays untouched (invariant 7).
- `FindResource` / `TryFindResource` / `SetResourceReference` APIs; key-type policy (strings + `Type` + `DataTemplateKey`; **no ComponentResourceKey type — stance in §6.7**).
- **Brush-name registry integration** — one namespace shared between `{StaticResource}`/`{DynamicResource}` brushes and `[brush=name]` text markup, via a resolver factory over the lookup chain.
- **Diagnostics**: `ResourceDiagnostics.Trace/Explain`, deferred-entry inspection (including realize-time variant), subscription accounting. Serialization stance: **none in v1** — XAML is the authoring format; inspection is the diagnostic surface; round-trip serialization is deferred (no scenario; hot reload re-parses source).

**Explicitly not owned:**

- Selector matching, frame arming, `StyleSortKey` computation (Fork B / styling subsystem — I hand it resolved values and change callbacks). Styling also owns stamping capability classes; §3.6 pins which tier value it stamps from.
- XAML parsing, slice construction, the ambient-stack *push discipline* during instantiation (Fork C — I define what it pushes, what `Realize` receives, and the `IResourceValueBuilder` replacement rule it must honor).
- The `ValueStore` (Fork A — I am a well-behaved value *producer*; retraction stays store-owned).
- DataContext/binding (resources are not bindings; `When` watchers are styling's).
- Which elements re-template / re-measure on a resource change (routed entirely by `PropertyEffects` metadata; invariant 2).
- DataTemplate *probing policy* (exact type → base chain) — the items/presenter subsystem owns it; I ship the collision-free `DataTemplateKey` and the chain it probes through.

---

## 2. Public API sketch

### 2.1 ResourceDictionary and deferred entries

```csharp
namespace Cursorial.UI;

/// <summary>Lazily-realizing keyed resource storage with merged and theme-variant sub-dictionaries.</summary>
public sealed class ResourceDictionary : IEnumerable<KeyValuePair<object, object?>>
{
    public ResourceDictionary();

    // Keyed storage. Keys: string (ordinal), Type (control themes), DataTemplateKey. Any object legal.
    // Values: any object EXCEPT UIProperty.UnsetValue (rejected at Add/set — it is the miss sentinel, §3.4).
    public object? this[object key] { get; set; }            // get realizes deferred; set pulses (keyed)
    public void Add(object key, object? value);              // duplicate key ⇒ ArgumentException
    public bool Remove(object key);
    public bool ContainsKey(object key);                     // does NOT realize
    public bool TryGetValue(object key, out object? value);  // this dictionary only; realizes deferred
    public int Count { get; }
    public IEnumerable<object> Keys { get; }                 // no realization

    // Fork C deferred-entry contract (lazy node-graph slices). Realize-once, cached in the slot.
    public void SetDeferred(object key, IDeferredResourceEntry entry);

    public IList<ResourceDictionary> MergedDictionaries { get; }        // lazy-alloc; later wins
    public ThemeDictionaryCollection ThemeDictionaries { get; }         // lazy-alloc; keyed by ThemeVariantKey

    /// <summary>Selector styles shipped by a theme dictionary, armed by the styling engine at
    /// StyleSortKey layer Theme(2). Consumed ONLY from Application.Theme (own + merged, §3.7).
    /// Variant-agnostic by design — variant flips are resource-event-only and never re-match.</summary>
    public Styles? Styles { get; set; }                      // Fork B's Styles collection type

    public Uri? Source { get; set; }   // setter invokes ResourceDictionaryLoader.LoadCallback and adopts the
                                       // result; a null callback ⇒ InvalidOperationException naming the fix
                                       // ("reference Cursorial.UI.Xaml or set LoadCallback") — never an NRE.

    public bool IsSealed { get; }
    public void Seal();                // deep-freeze (entries/merged/theme/Styles); mutation throws; sealed
                                       // dicts never pulse. Deferred realization remains legal (cache fill is
                                       // logically immutable; no Version bump, no pulse). Sealing inside an
                                       // open BeginUpdate scope throws InvalidOperationException.

    /// <summary>Single-hop lookup: ThemeDictionaries (variant probe per §3.2, recursive) → own entries →
    /// MergedDictionaries last-to-first (recursive). Does NOT walk parents — chain walking is
    /// ResourceExtensions' job.</summary>
    public bool TryGetResource(object key, ThemeVariant variant, out object? value);

    public ResourceUpdateScope BeginUpdate();  // coalesces mutations into one catch-all pulse at Dispose.
                                               // Nestable (refcounted; outermost Dispose pulses). Debug-asserts
                                               // disposal within the same dispatcher turn (invariant 1).
    public int Version { get; }                // bumped on any mutation incl. merged/theme/Styles/Source changes
    public event EventHandler<ResourcesChangedEventArgs>? Changed;   // the per-dictionary pulse
}

/// <summary>The runtime contract for Fork C's lazy dictionary entries (parse-time-checked node-graph slices).
/// Realize is called at most once per entry per dictionary instance ON SUCCESS, on the UI thread; the result
/// replaces the slot payload. A throwing Realize resets the entry to Deferred (retried on next lookup; the
/// exception propagates to the lookup caller). lexicalScope is the resource chain captured at the entry's
/// *definition* site.</summary>
public interface IDeferredResourceEntry
{
    object? Realize(IResourceScope lexicalScope);
}

/// <summary>One lookup level for the XAML loader's ambient stack (Fork C contract shape, owned here).</summary>
public interface IResourceScope
{
    bool TryGetResource(object key, out object? value);   // variant applied internally (current effective variant)
    IResourceScope? Parent { get; }
}

public static class ResourceScopes
{
    public static IResourceScope ForElement(UIElement element);                       // live chain from element upward
    public static IResourceScope ForDictionary(ResourceDictionary d, IResourceScope? parent); // lexical capture links
    public static IResourceScope ForApplication();                                    // app → theme → built-in
}

public interface IResourceHost
{
    ResourceDictionary Resources { get; }      // lazy-alloc on first get
    bool HasResources { get; }                 // false ⇒ chain walk skips without allocating
}

public static class ResourceDictionaryLoader   // set once by Cursorial.UI.Xaml's module initializer.
{                                              // Process-global mutable static: test-isolation hazard —
    public static Func<Uri, ResourceDictionary>? LoadCallback { get; set; }   // tests must save/restore.
}

public readonly record struct ResourcesChangedEventArgs(ResourceChangeKind Kind, object? Key);
public enum ResourceChangeKind : byte { Keyed, CatchAll }   // CatchAll: merged/theme/Styles/Source/variant/bulk
```

### 2.2 Theme variants

```csharp
public enum ThemeBase : byte { Dark = 0, Light = 1 }

/// <summary>The terminal-native theme axis: light/dark × capability tier. No tier-baked Dark/Light statics —
/// equality against a baked tier silently fails on non-truecolor terminals; compare bases via IsDark/IsLight.</summary>
public readonly record struct ThemeVariant(ThemeBase Base, ColorDepth Tier)
{
    public bool IsDark  => Base == ThemeBase.Dark;
    public bool IsLight => Base == ThemeBase.Light;

    /// <summary>Base from DefaultBackground relative luminance (OSC 11 readback; >0.5 ⇒ Light; null or
    /// non-RGB ⇒ Dark — the terminal-world default). Tier = negotiated ColorCapabilities.Depth.</summary>
    public static ThemeVariant FromCapabilities(TerminalCapabilities capabilities);
}

/// <summary>ThemeDictionaries key: either axis may be a wildcard. "Dark", "Light", "Ansi16", "Dark+Ansi16".
/// A tier key declares a MINIMUM capability (served at that tier and above via descent, §3.2); a base-only
/// key (B,·) is the last-probed catch-all and its values MUST be renderable at every tier incl. NoColor.</summary>
public readonly record struct ThemeVariantKey(ThemeBase? Base, ColorDepth? Tier)
{
    public static ThemeVariantKey Parse(ReadOnlySpan<char> text);     // XAML converter target
    public static implicit operator ThemeVariantKey(ThemeBase b);
    public static implicit operator ThemeVariantKey(ColorDepth t);
    // (null, null) is rejected at collection insert — that's the dictionary's own entries.
}

public sealed class ThemeDictionaryCollection   // IDictionary<ThemeVariantKey, ResourceDictionary>-shaped
{
    public ResourceDictionary this[ThemeVariantKey key] { get; set; }
    public bool Remove(ThemeVariantKey key);
    public int Count { get; }
}
```

### 2.3 Application / Window / UIElement / Control surface (members this subsystem contributes)

```csharp
public partial class Application : UIObject, IResourceHost
{
    public ResourceDictionary Resources { get; set; }          // replace ⇒ catch-all pulse, all roots
    public ResourceDictionary? Theme { get; set; }             // active theme; null ⇒ BuiltIn only; swap ⇒ catch-all pulse
                                                               // + styling re-reads Theme.Styles (§3.7)

    public ThemeBase?  RequestedThemeBase { get; set; }        // explicit Dark/Light override; null = derive
    public ColorDepth? RequestedColorTier { get; set; }        // testing/preview override; null = negotiated.
                                                               // Styling stamps caps-color classes from the
                                                               // EFFECTIVE tier (§3.6) so previews stay coherent.
    public ThemeVariant ActualThemeVariant { get; }            // (override ?? derived) per axis
    public event EventHandler? ActualThemeVariantChanged;

    /// <summary>App-level resource notification: raised for every registry pulse that fans app-wide —
    /// variant flips (Kind=CatchAll, Key=null), Resources/Theme replacement, and keyed/catch-all pulses
    /// originating at Application scope. THIS, not ResourceDictionary.Changed, is the external signal for
    /// variant changes (sealed app dictionaries never pulse; this event is independent of dictionary
    /// sealed-ness). DECISIONS amendment: Fork C's "pulsing ResourceDictionary.Changed" on variant
    /// re-resolution is redirected here.</summary>
    public event EventHandler<ResourcesChangedEventArgs>? ResourcesChanged;

    /// <summary>Host subsystem calls this after TerminalSession.RenegotiateAsync completes (and once at
    /// startup) with the fresh snapshot. MUST be called on the UI thread (async continuations are not —
    /// the host marshals; VerifyAccess debug-asserts). Re-derives ActualThemeVariant; pulses CatchAll on
    /// change.</summary>
    public void OnCapabilitiesChanged(TerminalCapabilities capabilities);
}

public partial class UIElement : UIObject, IResourceHost
{
    public ResourceDictionary Resources { get; set; }          // lazy-alloc; replace ⇒ catch-all pulse for subtree
    public bool HasResources { get; }
}

public partial class Control
{
    /// <summary>Per-instance control-theme override (a selector-less Style with ^-rooted Children).
    /// When null, the theme is resolved by ControlThemeKey through the lookup chain.</summary>
    public static readonly StyledProperty<Style?> ThemeProperty;
    public Style? Theme { get; set; }

    /// <summary>Resource key for control-theme lookup. Default GetType(); override to inherit a base
    /// control's theme (Avalonia StyleKeyOverride semantics — exact key only, no base-chain probing:
    /// MyButton : Button resolves NOTHING anywhere, incl. BuiltIn, unless it overrides this to
    /// typeof(Button) or ships its own theme; resolution miss fires a one-time debug diagnostic).</summary>
    protected virtual object ControlThemeKey => GetType();
}
```

### 2.4 Lookup, references, subscriptions

```csharp
public static class ResourceExtensions
{
    /// <summary>Walks the full chain at ActualThemeVariant. Throws ResourceNotFoundException
    /// (message lists every scope searched, hop by hop — diagnostics-first).</summary>
    public static object? FindResource(this UIElement element, object key);
    public static bool TryFindResource(this UIElement element, object key, out object? value);
    public static bool TryFindResource(this UIElement element, object key, ThemeVariant variant, out object? value);

    /// <summary>DynamicResource on a direct element property: installs a resource-fed value producer at
    /// BindingPriority.LocalValue. Live: re-resolves on pulses. Evicted (subscription disposed) when a later
    /// SetValue/Bind replaces the local slot — via Fork A's IValueEvictionListener. Cleared by ClearValue.</summary>
    public static void SetResourceReference<T>(this UIElement element, StyledProperty<T> property, object key);
}

/// <summary>The DynamicResource currency. Held by Setter.Value (Fork B) and produced by the {DynamicResource}
/// markup node (Fork C). Never passed through SetValue (DECISIONS: no sentinels through SetValue).</summary>
public readonly record struct ResourceReference(object Key);

/// <summary>Subscription handle used by the styling engine (ResourceReference setters), SetResourceReference,
/// and control-theme tracking. SEMANTICS (pinned): the struct wraps a single registry-node reference; copies
/// share the node; default(ResourceSubscription) is a safe no-op for all three methods; Dispose is idempotent
/// across copies (node-level dead flag). Safe to embed in styling's ActivationFrame structs.</summary>
public struct ResourceSubscription : IDisposable
{
    public void Pause();      // O(1): sets the node's paused flag + stamps the root version. No allocation,
                              // no list movement — this runs on styling's frame-deactivation edge, which is
                              // on the any-event mouse-motion hot path (§3.5).
    public void Resume();     // O(1) flag clear; re-resolves once iff the root version moved while paused.
                              // CONTRACT (Fork B): call Resume BEFORE the frame's entries are read/activated,
                              // so a stale value is never briefly effective.
    public void Dispose();    // unregisters (tombstone if a sweep is in flight, §3.5); idempotent.
}

public interface IResourceChangeListener
{
    /// <summary>UI thread, synchronous within the pulse. newValue is UIProperty.UnsetValue when the key no
    /// longer resolves (distinct from a resource whose value is null) — the listener must surface "unset"
    /// (entry HasValue = false) so lower priority sources promote, per Fork A.</summary>
    void OnResourceChanged(object key, object? newValue);
}

public static class ResourceServices   // the styling engine's hookup (PROVIDES, see §4)
{
    /// <summary>Resolve now + subscribe. scope = the consuming element (lookup walks from it).
    /// initialValue is UIProperty.UnsetValue on miss (never conflated with a null-valued resource).
    /// Unattached elements resolve to UnsetValue and force one re-resolve on tree attach (§3.8) —
    /// the forced re-resolve also covers cross-root moves, whose version counters are independent.</summary>
    public static ResourceSubscription Subscribe(UIElement scope, object key,
        IResourceChangeListener listener, out object? initialValue);

    /// <summary>Control-theme resolution: control.Theme ?? chain lookup by ControlThemeKey. ONE handle
    /// covering BOTH watches: a property observer on ThemeProperty (Fork A change channel) and a registry
    /// node for the chain lookup — the listener fires on either. Styling arms the result at StyleSortKey
    /// layer ControlTheme(0); identity change ⇒ listener fires and styling re-arms (frame removal + add —
    /// store-owned retraction).</summary>
    public static ResourceSubscription SubscribeControlTheme(Control control,
        IResourceChangeListener listener, out Style? theme);

    /// <summary>The visual root's monotonic resource version for scope's window (0 when detached).
    /// Bumps on every pulse reaching that root (keyed or catch-all, incl. variant flips). S8 CONTRACT:
    /// text-bearing controls include (GetResourceVersion(this), ActualThemeVariant) in their FormattedText
    /// cache keys so the next render after any pulse re-parses with fresh resolver output. The version is
    /// root-GLOBAL by design: any resource mutation invalidates every formatted-text cache in the window —
    /// a stated property, acceptable at rare-pulse cadence (§8 Q3).</summary>
    public static int GetResourceVersion(UIElement scope);
}

public sealed class ResourceNotFoundException : KeyNotFoundException
{
    public object Key { get; }
    public string SearchedScopes { get; }   // rendered chain, one hop per line
}

/// <summary>Collision-free key for data templates stored as resources (typeof(VM) must not collide with a
/// control-theme Type key). Probing policy (exact → base chain) is the presenter subsystem's.</summary>
public readonly record struct DataTemplateKey(Type DataType);
```

### 2.5 Built-in theme and brush registry

```csharp
namespace Cursorial.UI.Themes;

public static class CursorialTheme
{
    /// <summary>The sealed, process-shared built-in dictionary: control themes (Type keys) + the ThemeKeys
    /// palette, with ThemeDictionaries laid out per the tier-key rules (§3.7: color-bearing values live at
    /// exact (B,T) keys, never in (B,·)). Always the final lookup hop. Code-first (no XAML — Cursorial.UI
    /// cannot reference Cursorial.UI.Xaml).</summary>
    public static ResourceDictionary BuiltIn { get; }

    /// <summary>An unsealed structural copy for apps that want to start from the default and mutate
    /// (assign to Application.Theme). Control themes / palette values are shared instances; the
    /// dictionary shells are fresh.</summary>
    public static ResourceDictionary CreateDefault();
}

/// <summary>The palette vocabulary — string constants, not a key type (§6.7). Referenceable from XAML
/// verbatim ({DynamicResource Theme.AccentBrush}) and from C# typo-proof.</summary>
public static class ThemeKeys
{
    public const string SurfaceBrush       = "Theme.SurfaceBrush";
    public const string SurfaceHoverBrush  = "Theme.SurfaceHoverBrush";
    public const string TextBrush          = "Theme.TextBrush";
    public const string DisabledTextBrush  = "Theme.DisabledTextBrush";
    public const string AccentBrush        = "Theme.AccentBrush";
    public const string AccentHoverBrush   = "Theme.AccentHoverBrush";
    public const string FocusPen           = "Theme.FocusPen";
    public const string BorderPen          = "Theme.BorderPen";
    public const string ObscuredOverlayBrush = "Theme.ObscuredOverlayBrush";  // modal-dim scrim (window manager)
    public const string AccessKeyUnderlineBrush = "Theme.AccessKeyUnderlineBrush";
    // … grows with the control set; additions are non-breaking.
}

namespace Cursorial.UI;

public static class ResourceBrushResolver
{
    /// <summary>A TextMarkupOptions.BrushResolver over the element's resource chain: tries BrushMarkup's
    /// inline grammar first ("linear:#f92672,#66d9ef"), then TryFindResource(name) accepting IBrush
    /// (wrapped as BrushedStyle, DeclarationScope.Inline) or BrushedStyle directly; else null (parser
    /// raises "Unrecognized brush"). [brush=Theme.AccentBrush] and {StaticResource Theme.AccentBrush}
    /// therefore share one namespace. Resolution is static-per-parse; refresh rides the S8 cache-key
    /// contract on ResourceServices.GetResourceVersion (§2.4).</summary>
    public static Func<string, object?> Create(UIElement scope);
}
```

### 2.6 XAML-facing media builders (`Cursorial.UI.Media`)

> **RETIRED (#8) — superseded by element-authorable Drawing types.** This section's `Cursorial.UI.Media` builder
> twins (`SolidColorBrush`/`LinearGradientBrush`/`Pen` + the `IResourceValueBuilder.Build()` loader seam) were
> never wired into the loader. The simpler resolution was to make the real `Cursorial.Drawing.Media` types directly
> XAML-element-authorable — parameterless ctors + `init` members + `[ContentProperty]` on the gradient `Stops`
> (#5), and `Activator.CreateInstance` + `init`-member reflection for the `Pen`/`GradientStop` record structs (#20)
> — so element syntax (path 2) uses the Drawing types themselves. The twins and the `IResourceValueBuilder` seam
> are removed; the text below is retained for historical context only. See canonical design §11.9.

The `Cursorial.Drawing` brush/pen types are deliberately XAML-hostile: no parameterless ctors, get-only members, ctor-supplied stop lists, `Pen` a `readonly record struct` — Fork C's instantiator (parameterless-ctor `Activate` + settable members) cannot build them. Two complementary paths, both keeping Drawing untouched (invariant 7):

1. **Attribute text** — Fork C's registered converters already cover it: the `IBrush` converter reuses `BrushMarkup`'s grammar (`"linear:#f92672,#66d9ef"`), the `Pen` converter parses preset+composition text (`"Dashed #888"`). Used for setter values and brush-typed attributes.
2. **Element syntax** (required for keyed dictionary entries and multi-stop gradients) — mutable builder types in `Cursorial.UI.Media`, mapped into the default UI xmlns. Named identically to their Drawing counterparts (WPF/Avalonia `*.Media` kinship; deliberate shadowing — C# consumers use Drawing's types directly and rarely touch builders):

```csharp
namespace Cursorial.UI.Media;

/// <summary>Loader replacement seam (owned by Cursorial.UI; honored by Fork C): when a constructed object
/// implements this, the instantiator calls Build() at end-of-object (after ISupportInitialize.EndInit when
/// present) and uses the RESULT everywhere the object would have been used — dictionary insert, member
/// assignment, collection add. Builders are one-shot, single-threaded, never stored.</summary>
public interface IResourceValueBuilder { object Build(); }

public sealed class SolidColorBrush : IResourceValueBuilder   // Build() → Drawing.SolidColorBrush
{ public Color Color { get; set; } public double Opacity { get; set; } = 1.0; … }

public sealed class GradientStop { public double Offset { get; set; } public Color Color { get; set; } }

public sealed class LinearGradientBrush : IResourceValueBuilder   // Build() → Drawing.LinearGradientBrush
{
    public List<GradientStop> GradientStops { get; }   // content property
    public RelativePoint StartPoint { get; set; }      // "0,0" via converter
    public RelativePoint EndPoint { get; set; }
    public GradientSpread Spread { get; set; }
    public double Opacity { get; set; } = 1.0;
}
// RadialGradientBrush, ConicGradientBrush: same shape over their Drawing ctors (incl. CellAspectRatio).

public sealed class Pen : IResourceValueBuilder   // Build() → boxed Cursorial.Drawing.Pen
{
    public Color Color { get; set; }              // or Brush (IBrush); Color wins when both set? — no: setting
    public IBrush? Brush { get; set; }            // both is a builder-time InvalidOperationException.
    public StrokeWeight Weight { get; set; }      public CornerStyle Corners { get; set; }
    public LineDash Dash { get; set; }            public EndCap EndCap { get; set; }
    public JunctionMode Junction { get; set; }    public GlyphSet GlyphSet { get; set; }
    public TextAttributes Attributes { get; set; }
}
```

`ImageBrush`/`TileBrush` builders (URI-sourced decode) are deferred with the resource-loader story (§7). Built values are the immutable Drawing instances, boxed once and shared — sealed-dictionary-safe, folded-constant-friendly.

### 2.7 Diagnostics

```csharp
public static class ResourceDiagnostics
{
    /// <summary>Hop-by-hop record of a lookup: every dictionary probed (incl. theme-variant probe keys tried,
    /// merged recursion), hit/miss per hop, whether the hit was deferred-then-realized.</summary>
    public static ResourceLookupTrace Trace(UIElement element, object key);
    public static string Explain(UIElement element, object key);      // acceptance test: one line per hop

    public static IReadOnlyList<ResourceSubscriptionInfo> Subscriptions(Window root);  // leak hunting
    public static IReadOnlyList<DeferredEntryInfo> DeferredEntries(ResourceDictionary dictionary);
    // DeferredEntryInfo: (object Key, bool Realized, ThemeVariant? RealizedAtVariant, Uri? Source,
    // int Line, int Column) — RealizedAtVariant records WHEN a lazy entry froze its StaticResource captures
    // (§3.1 nondeterminism note); line info supplied by Fork C's IDeferredResourceEntry implementation via
    // an optional IXamlLineInfo-shaped interface probe.
}
```

### 2.8 Consumer example

```xml
<!-- App.xaml. Tier keys declare MINIMUM capability; descent never ascends (§3.2). -->
<Application xmlns="https://cursorial.dev/ui" xmlns:x="https://cursorial.dev/xaml">
  <Application.Resources>
    <ResourceDictionary>
      <ResourceDictionary.ThemeDictionaries>
        <ResourceDictionary x:Key="Dark+Ansi256">  <!-- serves Truecolor AND Ansi256 (quantizer-safe RGB) -->
          <LinearGradientBrush x:Key="Theme.AccentBrush" StartPoint="0,0" EndPoint="1,0">
            <GradientStop Offset="0" Color="#f92672"/><GradientStop Offset="1" Color="#66d9ef"/>
          </LinearGradientBrush>
        </ResourceDictionary>
        <ResourceDictionary x:Key="Dark+Ansi16">   <!-- tier-specialized: hand-picked palette beats the quantizer -->
          <SolidColorBrush x:Key="Theme.AccentBrush" Color="LightCyan"/>
          <Pen x:Key="Theme.BorderPen" GlyphSet="Ascii"/>
        </ResourceDictionary>
        <ResourceDictionary x:Key="Light+Ansi256">
          <SolidColorBrush x:Key="Theme.AccentBrush" Color="#a6004e"/>
        </ResourceDictionary>
        <!-- A base-only "Dark" / "Light" dictionary is the LAST-probed catch-all: legal only for values
             renderable at every tier incl. NoColor (attributes, pens) — never raw truecolor brushes. -->
      </ResourceDictionary.ThemeDictionaries>
      <ResourceDictionary.MergedDictionaries>
        <ResourceDictionary Source="cursorial://DemoApp/Themes/Widgets.xaml"/>  <!-- deferred entries -->
      </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
  </Application.Resources>
</Application>
```

```csharp
// Program.cs — host wiring
var session = await TerminalSession.OpenAsync();
app.OnCapabilitiesChanged(session.Capabilities);      // UI thread; derives ActualThemeVariant = e.g. (Dark, Ansi256)
app.RequestedThemeBase = ThemeBase.Light;             // user toggle later: ONE catch-all pulse,
                                                      // DynamicResource re-resolution only — no style re-match.

// Per-control theme override, app-wide: nearer scope shadows CursorialTheme.BuiltIn's typeof(Button) entry.
app.Resources[typeof(Button)] = MyButtonTheme.Create();

// Code-side dynamic reference on a direct property (LocalValue producer; live across theme flips):
statusBar.SetResourceReference(Panel.BackgroundProperty, ThemeKeys.SurfaceBrush);

// One-shot lookup + diagnostics:
if (!toolbar.TryFindResource(ThemeKeys.FocusPen, out var pen))
    Console.Error.WriteLine(ResourceDiagnostics.Explain(toolbar, ThemeKeys.FocusPen));

// Brush namespace shared with text markup:
var options = BrushMarkup.Options(registry: null) with { BrushResolver = ResourceBrushResolver.Create(banner) };
var rich = TextMarkup.Parse("[brush=Theme.AccentBrush]Cursorial[/brush]", options);
```

---

## 3. Mechanics

### 3.1 Storage

`ResourceDictionary` backs onto `Dictionary<object, object?>` with a private comparer: ordinal for `string`, default otherwise (`Type` hashes by reference; `DataTemplateKey`/`ThemeVariantKey` by value). Deferred entries are stored as an internal `sealed class DeferredSlot` holding a tri-state payload (`Deferred(entry)` → `Realizing` → realized value). **Realization mutates the payload *inside* the slot object — the `Dictionary` slot itself is never replaced**, so lazy realization during enumeration cannot trip the backing dictionary's version check, and `Version` does not bump (cache fill is logically immutable; sealed dictionaries realize freely). `MergedDictionaries` and `ThemeDictionaries` are lazy-allocated owner-notifying collections; `Styles` assignment/mutation routes the same owner notification (CatchAll). Each dictionary holds an internal `Owner` back-link (an `IResourceHost` or a parent `ResourceDictionary`) for pulse routing; a dictionary added to two owners throws (single-parent rule — keeps pulse routing unambiguous). **Sealed dictionaries are exempt** — they never pulse, so they are freely shared: that exemption is exactly what makes both `MergedDictionaries`-sharing and the **template-resources multi-instance slot-in (§3.3)** legal.

**Realization.** `Realize(lexicalScope)` runs on the UI thread; the lexical scope is whatever Fork C captured at parse/load (`ResourceScopes.ForDictionary(definingDict, enclosingChain)`). A realization re-entering the same entry (cycle through StaticResource) throws with both keys named; forward references are already a Fork C parse error, so only genuine cycles reach this guard (`Realizing` is observed only within the synchronous realization stack). **A throwing `Realize` resets the slot to `Deferred`** — the entry is retried on the next lookup (transient failures, e.g. a not-yet-loadable merged `Source`, don't poison the entry) and the exception propagates to the lookup caller; pinned by test. Enumeration of values realizes lazily per entry; `Keys`, `Count`, `ContainsKey` never realize. **StaticResource captures inside a deferred entry freeze at first-realization time, under whatever variant is then current** — time-nondeterministic by construction; theme files should use DynamicResource for variant-sensitive references, and `DeferredEntryInfo.RealizedAtVariant` makes the freeze observable. `Seal()` freezes structure recursively (entries/merged/theme/Styles); realization cache-fill remains legal on sealed dictionaries. Single-UI-thread invariant makes this safe without locks, including for the process-shared `BuiltIn`.

### 3.2 Per-dictionary lookup and the variant probe

`TryGetResource(key, variant)` probes, in order:

1. **ThemeDictionaries** — for each probe key (below), if present, recurse into that sub-dictionary (which may itself merge/realize). Variant-specific beats generic *within a dictionary*.
2. **Own entries** (realize if deferred).
3. **MergedDictionaries, last to first** (recursive) — WPF rule: later merged wins; own entries beat merged.

**Probe order** for effective variant `(B, T)` — precomputed static tables, one per the **8 possible variants** (2 bases × 4 tiers), built once at type-init, so a probe is a `ReadOnlySpan<ThemeVariantKey>` walk with zero per-lookup work:

```
(B, T) → (B, T−1) → … → (B, NoColor)        // 1. exact-base tier descent
(·, T) → (·, T−1) → … → (·, NoColor)        // 2. wildcard-base tier descent
(B, ·)                                       // 3. base-only catch-all — LAST
```

Tier descent **never ascends** — a tier key declares a *minimum* capability: an `(B, Ansi16)` entry serves Ansi16 and every tier above it (unless a higher-tier entry shadows); Truecolor entries are served only at Truecolor. `(B,·)` is probed **last** and is by contract **tier-agnostic: its values must be renderable at every tier, NoColor included** (it will be served at NoColor whenever no tiered entry matches) — color-bearing values belong at exact `(B,T)` keys. Tier specialization deliberately beats base specialization between the wildcard forms (a hand-picked `(·,Ansi16)` palette entry beats a `(B,·)` catch-all — tier entries exist to beat the quantizer). A key present in **both** a `(B,·)` dictionary and any `(·,T)` dictionary is ambiguous-by-construction: flagged by a **seal-time/load-time lint** telling the author to use exact `(B,T)` keys.

**Worked truth table** (pinned in the T0 oracle matrix; key K present in the listed sub-dictionaries):

| K present in | (D,True) | (D,256) | (D,16) | (D,NoColor) |
|---|---|---|---|---|
| (D,256), (D,16), (·,NoColor) | (D,256) | (D,256) | (D,16) | (·,NoColor) |
| (D,·) only | (D,·) | (D,·) | (D,·) | (D,·) — hence the NoColor-safety contract |
| (D,·), (·,16) | (D,·)* | (D,·)* | (·,16) | (D,·) |
| (D,True) only | (D,True) | miss | miss | miss |

\* at tiers ≥ Ansi256 descent reaches `(·,16)` first → `(·,16)` wins there too — corrected: row 3 at (D,True)/(D,256) is **(·,16)** (descent passes through Ansi16 before the catch-all). This pair is exactly what the lint flags. "Gradient at high tiers + palette at Ansi16, per base" is therefore authored with exact keys: `(B,Ansi256)` + `(B,Ansi16)` (as in §2.8).

`ColorDepth`'s meaningful numeric ordering (`NoColor=0 … Truecolor=3`, per rendering-session.md) is load-bearing here. Max 9 probe keys at Truecolor; dictionaries without `ThemeDictionaries` (the common case) skip the whole step on a null check.

### 3.3 The chain walk

The walk follows **`ResourceParent`**, defined over the S1 contract (a named REQUIRES, §4):

```
ResourceParent(node) = node.LogicalParent ?? node.TemplatedParent   // null only at a true root

TryFindResource(element e, key, variant v):
  node = e
  while node != null:
      if node.HasResources && node.Resources.TryGetResource(key, v, out value): return true
      if node.LogicalParent == null && node.TemplatedParent is { } tp:
          // template barrier crossing: node is the TEMPLATE ROOT; the owning template's resources slot in.
          // TemplateInstance lives on the TEMPLATED PARENT, not the part.
          t = tp.TemplateInstance?.Template
          if t is { HasResources: true } && t.Resources.TryGetResource(key, v, out value): return true
          node = tp
      else:
          node = node.LogicalParent                 // Window is the last logical ancestor
  app = Application.Current
  if app.HasResources && app.Resources.TryGetResource(key, v, out value): return true
  if app.Theme is { } th && th.TryGetResource(key, v, out value): return true
  return CursorialTheme.BuiltIn.TryGetResource(key, v, out value)
```

**REQUIRES from S1 (load-bearing, named):** template-generated elements form a logical chain **part → … → template root**; the template root has `LogicalParent == null` and `TemplatedParent == the templated control`; interior parts carry `TemplatedParent` for the styling barrier but their `LogicalParent` chain stays within the template. Without this, every `FindResource` from a template part dead-ends. **DataTemplate-generated content** has a normal `LogicalParent` (its presenter) and `TemplatedParent == null` ⇒ no template-resource hop; **DataTemplate-own `Resources` do not participate in the chain walk in v1** (StaticResource inside a DataTemplate body still resolves against Fork C's captured lexical scope; chain participation is a recorded additive deferral, §7).

**Template resources are sealed** — a `ControlTemplate`'s `Resources` is sealed as part of Fork B's template seal-on-attach (seal-time check; arming an unsealed template throws naming the template). The hop is therefore static and pulse-free, and the seal exemption from the single-parent rule (§3.1) is what makes one dictionary slotting into *every instance* of the template across windows legal. A template-shipped brush that must respond to theme flips is authored as `{DynamicResource}` inside the template body, not as a mutable template resource.

Allocation-free: no enumerators (indexed loops over merged lists), the probe span is static, `HasResources` avoids lazy-alloc on read. Depth is the logical-tree depth (~8); cost is a handful of dictionary probes. Chain *walks* happen at load, activation, attach, and pulse time — but note honestly: this subsystem touches the per-frame path via `Pause`/`Resume` on styling's activation edges (§3.5 costs it). No lookup cache in v1 (recorded: a per-root memo table is an API-compatible upgrade if profiling ever demands it; at 10² elements it won't).

**Decision — child windows do not chain to their owner**: a child `Window`'s walk goes window → Application, WPF-parity (see §8 Q1). **Decision — `StaticResource` never uses this walk**: it resolves at instantiation against Fork C's lexical/ambient stack (`XamlLoadContext.AmbientResources` defaults to `ResourceScopes.ForApplication()`), making load-order explicit and forward-reference-free.

### 3.4 DynamicResource — producers, priorities, lifecycle

**A resource reference is a value *producer*, not a priority** — exactly the DECISIONS stance for bindings.

- **In a `Setter`** (`Setter.Value is ResourceReference`): the styling engine, at frame-arm/first-activation (`ApplyRule`), calls `ResourceServices.Subscribe(element, key, entryListener, out initial)`. The resolved value lives inside the frame's `IValueEntry<T>`; it therefore rides **`BindingPriority.Style` at the owning frame's `StyleSortKey`** — control-theme setters at layer `ControlTheme(0)`, template setters at `Template(1)`, theme styles at `Theme(2)`, app/scoped/explicit per the layer model. A pulse mutates the entry in place and raises Fork A's `OnEntryChanged`; the store recomputes — **the frame is never removed/re-added for a value change**, so no re-match, no sort churn. Frame deactivation calls `subscription.Pause()`; activation calls `Resume()` **before** the frame's entries are read (pinned ordering, §2.4) — both O(1) flag operations because they ride the `:pointerover` hot path. Frame *disarm* (detach, re-match removal) disposes the subscription. Retraction of the value itself is store-owned promotion (invariant 4) — this subsystem never "sets back".
- **On a direct property** (`SetResourceReference`, or `{DynamicResource}` on an element attribute via Fork C's `IDeferredValue.AttachTo` seam routing here): installs a producer at **`BindingPriority.LocalValue`** (a `BindingEntry<T>`-shaped store entry). A later `SetValue`/`Bind` at LocalValue replaces it; Fork A's mandated `IValueEvictionListener` notifies the producer, which disposes its subscription — no leak, no zombie clobbering on the next pulse. `ClearValue` likewise detaches it.
- **Type conversion**: the resolved object is assigned through the same path XAML values take (untyped `SetValue`/entry write with the property's converter via the `XamlConverters` registry when types mismatch); a type-incompatible resource is discarded with a `UIDiagnostics.OnRejectedValue` diagnostic naming the key, per Fork A's live-data rule.
- **Missing key — the `UnsetValue` contract**: a miss resolves to **`UIProperty.UnsetValue`** (Fork A's mandated sentinel; never conflated with a null-valued resource) — `Subscribe`'s `initialValue`, and `OnResourceChanged`'s `newValue` on a transition *to* missing (key removed, theme swapped away), both carry it. The consuming entry reports `HasValue = false` so lower sources surface (not default-clobbering, not null-clobbering). Symmetrically, `UnsetValue` is rejected as a stored dictionary value (§2.1). A one-time debug diagnostic fires with the rendered search chain on first miss.

### 3.5 Pulse routing and the subscription registry

One `ResourceSubscriptionRegistry` per visual root (`Window`); app-level state (Application.Resources/Theme/variant) fans to all roots. Registry layout: `Dictionary<object, InlineList<Node>>` keyed by resource key for keyed pulses, plus one flat node list for catch-all sweeps; each `Node` = `(UIElement scope, IResourceChangeListener listener, object? lastValue, int resolvedVersion, NodeFlags flags)` with `flags ∈ {Paused, Dead}`. **One list — no segregated active list**: `Pause`/`Resume` are flag writes (the hot path, fired per hover-cell-crossing under any-event motion); sweeps — the rare path — test the flag per node. Secondary index: each element holds an inline list of its node handles so detach is O(own subscriptions).

**Mutation → pulse → sweep**, all synchronous on the UI thread (invariant 1 — a change during frame N's input drain is visible to frame N's layout/render; invariant 6 — `VerifyAccess` debug-asserted on every mutation and lookup):

1. Dictionary mutates → `Version++` → raise `Changed(Keyed key | CatchAll)` → walk `Owner` links to the host element (or Application).
2. Host element → its visual root's registry; Application → every root's registry **and** `Application.ResourcesChanged`. Root's monotonic resource version increments.
3. Sweep: keyed pulse visits only nodes under that key; catch-all visits the flat list. **Sweep iteration uses snapshot semantics** (the mid-sweep mutation flow is *designed*, not exceptional — a CatchAll theme swap fires `SubscribeControlTheme` listeners, styling re-arms, frames disarm/`Dispose` and new `ApplyRule`s `Subscribe`, all while the sweep runs): the sweep iterates a per-bucket copy-on-write snapshot (the bucket is marked in-sweep; a mutating `Subscribe`/`Dispose` against an in-sweep bucket clones the backing array first; sweeps are rare, so the clone cost is paid approximately never). Nodes **subscribed during a sweep are not visited** in that sweep (they resolved fresh at `Subscribe`); nodes **disposed during a sweep are tombstoned** (`Dead` flag, skipped on visit) and compacted after the sweep completes. Regression case pinned in T1: theme swap with re-templating controls.
4. Each *visited, non-paused, non-dead* candidate is filtered by **scope containment** — the node's element must be the pulsing host or its logical descendant (parent-chain walk, ~8 hops) — which is precisely what makes **shadowing** correct: a key added at a *nearer* scope re-resolves consumers below it even though their previously-hit dictionary never changed. Each survivor re-resolves via the full chain walk; `Equals(lastValue, newValue)` short-circuits (theme dictionaries that share an instance across variants no-op; misses compare as `UnsetValue`); changed values invoke the listener → entry mutation → Fork A change notification → invalidation routed **only** by `PropertyEffects` (`AffectsRender` ⇒ `Scene.Invalidate()` re-raster; `AffectsComposite` ⇒ CompositeParameters refresh; invariants 2 & 3 — this subsystem never touches Scene/CellBuffer).
5. Paused nodes cost one flag test per sweep visit; they catch up on `Resume()` via the version compare (at most one re-resolve regardless of pulse count while paused).

**Cost envelope, honestly stated.** Node count ≈ elements × armed `ResourceReference` setters: ~300 elements × 4–6 themed setters in built-in control themes, plus persisting paused hover/focus child-rule subscriptions (pause-not-destroy keeps them registered after first activation) ⇒ **low thousands of nodes** (~1.5–3k), ~48–64 B each ⇒ 100–200 KB per root — fine. A catch-all sweep = N × (flag test + containment walk + chain re-resolve) ≈ low single-digit ms worst case, at rare, user-initiated cadence, followed by a full repaint anyway. The *hot* path (pause/resume per pointer cell-crossing) is two flag writes and an int stamp — allocation-free by contract.

`BeginUpdate()` defers steps 1–5 to scope-dispose as one catch-all pulse (theme-file loads, bulk merges); nested scopes refcount, the outermost pulses; `Seal()` inside an open scope throws; debug-assert that a scope is disposed within the dispatcher turn that opened it (invariant 1). Re-entrancy: a listener mutating *resources* during a sweep queues a follow-up pulse (drained to a fixpoint, generation-capped at 16 with a cycle diagnostic — mirroring styling's loop guard); *registry* mutation during a sweep is the snapshot/tombstone path above.

### 3.6 Theme variant lifecycle

Effective variant = `(RequestedThemeBase ?? derivedBase, RequestedColorTier ?? negotiatedDepth)`. Derivation: `ColorCapabilities.DefaultBackground` (the OSC 11 readback the capability record explicitly designates for light/dark detection) → relative luminance `0.2126R + 0.7152G + 0.0722B` over sRGB-normalized channels; `> 0.5` ⇒ Light; null or non-RGB ⇒ Dark. Tier = `ColorCapabilities.Depth`.

- **`OnCapabilitiesChanged`** (host calls after `RenegotiateAsync` and at startup, **marshaled to the UI thread** — async continuations are not on it; `VerifyAccess` debug-asserts but the release-build contract is the host's): recompute; if `ActualThemeVariant` changed → raise `ActualThemeVariantChanged`, raise `Application.ResourcesChanged(CatchAll, null)`, and pulse every root's registry. No dictionary mutates and no dictionary `Changed` fires — the app-level event is the external variant signal (DECISIONS amendment recorded in §4; sealed-ness of app dictionaries never suppresses it).
- **Capability-class coherence (cross-subsystem decision):** styling re-stamps the color-tier classes (`caps-truecolor|ansi256|ansi16|nocolor`) from **`ActualThemeVariant.Tier`** (i.e. the *effective* tier, honoring `RequestedColorTier`) off `ActualThemeVariantChanged`; the non-color capability classes (`caps-motion`, `caps-kitty-keyboard`, `caps-unicode|ascii`) stamp from negotiated capabilities as DECISIONS states. A "preview Ansi16" app therefore gets Ansi16 resources *and* Ansi16-gated styles — no desync. Note the asymmetry: a renegotiation may change classes and resources; a `RequestedThemeBase` flip changes **only** resources.
- **Variant flip = resource-event-only** (DECISIONS, Fork B amendment): no selector re-match, no frame re-arm. Every themed value reaches elements through a DynamicResource subscription (built-in control themes use `ResourceReference` setters for all colors — §3.7), so the catch-all sweep + entry mutation is the entire mechanism. Control-theme subscriptions re-resolve to the *same* `Style` instance (themes are keyed per Type, not per variant) → identity short-circuit → no re-templating.
- Cost: the §3.5 envelope (low-thousands sweep + changed-value notifications); a rare, user-initiated event followed by a full-screen repaint whose bytes the two cache tiers below bound to the changed cells.

### 3.7 Built-in theme architecture

`CursorialTheme.BuiltIn` is constructed **code-first in `Cursorial.UI`** (the XAML loader lives in `Cursorial.UI.Xaml`, which depends on `Cursorial.UI` — the dependency cannot point back), sealed at construction (lint-clean by definition), shared process-wide, and is always the final lookup hop. Contents:

1. **The palette**: `ThemeKeys.*` string keys, populated through `ThemeDictionaries` **per the §3.2 tier-key rules — no color-bearing value lives in a `(B,·)` dictionary**: `(Dark,Ansi256)`/`(Light,Ansi256)` carry the RGB brushes (served at Truecolor and Ansi256 via descent; distinct truecolor-only values would go at `(B,Truecolor)` keys additively); `(Dark,Ansi16)`/`(Light,Ansi16)` carry hand-picked `Colors.*` palette brushes and `Pens.Ascii`-family pens; `(·,NoColor)` carries attribute-only styling values (which now actually win at NoColor, instead of being shadowed). `(B,·)` is reserved for genuinely tier-agnostic, NoColor-safe values. The quantizer downstream makes RGB *safe*; tier dictionaries make every tier *good*.
2. **Control themes**: one `Style` per control `Type` key — selector-less, `Children` rooted at `^` (incl. `^:access-keys` rules driving requirement 6's underscore visibility and `^.obscured` modal dimming via `ThemeKeys.ObscuredOverlayBrush`), with a `TemplateProperty` setter holding an `ITemplateContent` (code-first `FuncTemplateContent`). **Every color-bearing setter is a `ResourceReference` into the palette** — this is the inheritance spine: overriding `ThemeKeys.AccentBrush` at any nearer scope re-skins every control with zero template work.
3. **Layer assignment + the theme-styles channel (pinned):** Type-keyed control themes arm at `StyleSortKey` layer **`ControlTheme(0)`** wherever in the chain they were found. Selector styles a theme ships ride the typed **`ResourceDictionary.Styles`** slot (§2.1); the styling engine consumes **only `Application.Theme`'s** `Styles` — flattened depth-first as: each merged dictionary's `Styles` in `MergedDictionaries` order, then the theme's own `Styles` last (own beats merged via `StyleSortKey` order, consistent with lookup precedence) — armed at layer **`Theme(2)`**. Styling reads the slot when `Application.Theme` is set and re-reads on any CatchAll pulse whose origin is the theme dictionary (version compare makes the re-read cheap when `Styles` didn't change); a theme swap is the scope-wide re-match path styling already costs ("restyle the world"). Element/window dictionaries' `Styles` slots are ignored in v1 (scoped styles already exist on elements via Fork B); a debug diagnostic flags a populated non-theme `Styles` slot. Layer beats specificity (DECISIONS), so app styles (3+) always win over theme styles without ceremony.

**Override paths**: per-control = shadow the `Type` key at window/app scope, or set `Control.Theme` per instance; wholesale = `Application.Theme = myDict`; per-value = shadow a `ThemeKeys` string anywhere. Resolution order falls out of the chain — no special casing. **Backstop scope (precise claim):** BuiltIn backstops *partial app themes* — a control whose `ControlThemeKey` has a BuiltIn entry (the shipped control set) can't be stranded template-less by an incomplete `Application.Theme`. It does **not** cover novel keys: `MyButton : Button` resolves nothing anywhere under the default `ControlThemeKey => GetType()` (exact-key, no base-chain probing) — the author overrides `ControlThemeKey => typeof(Button)` or ships a theme; a control-theme resolution miss fires a one-time debug diagnostic naming the key and the chain searched.

### 3.8 Lifecycles

- **Element attach**: the element's nodes register with the new root's registry and **always force one re-resolve, regardless of stored version** — version counters are per-root and independent, so a cross-root move could otherwise spuriously satisfy the "no pulse occurred" compare (attach is rare; the forced walk is cheap). Pending references made while detached resolve the same way. **Detach**: the element's inline handle list unregisters its nodes; styling disposes frame subscriptions as frames disarm. Debug builds assert zero live nodes for a root at window teardown (subscription-leak tracker, mirroring the styling fork's).
- **`Resources` property replace / `Source` set / merged or `Styles` mutation**: catch-all pulse scoped to the host's subtree.
- **Dictionary moved between owners**: throws (single-parent); sealed dictionaries (incl. template resources and `BuiltIn`) are freely shared.

---

## 4. Cross-subsystem contracts

**REQUIRES from Property system (Fork A):**
- `BindingPriority` ladder as decided; producer entries (`BindingEntry<T>`-shaped) installable at `LocalValue` with in-place `SetValue`/unset and equality short-circuit; entries must represent "unset" (`HasValue = false`) when fed `UIProperty.UnsetValue`.
- `UIProperty.UnsetValue` as the shared miss sentinel (already mandated vocabulary).
- `IValueEvictionListener` notification when a LocalValue producer is displaced (drives `SetResourceReference` cleanup).
- Untyped `SetValue(UIProperty, object?, BindingPriority)` + box-interning cache (resource values arrive boxed).
- `PropertyEffects` routing of all change notifications (I never invalidate anything myself).

**REQUIRES from Styling (Fork B):**
- Frame entries that can hold a resolved-resource value (or unset) and re-emit via `OnEntryChanged` on mutation; **`Resume()` called before frame entries are read at activation**, `Pause()` at deactivation, `Dispose` at disarm; `UnsetValue` from a pulse handled as entry-unset (promotion), not a value write.
- Arming of control themes I resolve, at layer `ControlTheme(0)`; re-arm on identity change from `SubscribeControlTheme` (which owns *both* the `ThemeProperty` watch and the chain-lookup node — styling does not watch `ThemeProperty` separately).
- Consumption of `Application.Theme.Styles` per §3.7(3) at layer `Theme(2)`: read at Theme set, re-read on theme-origin CatchAll pulses; scope-wide re-match on swap.
- Capability classes: color-tier classes stamped from `ActualThemeVariant.Tier` off `ActualThemeVariantChanged` (§3.6); other classes from negotiated caps per DECISIONS.
- Template seal includes sealing the template's `Resources` (§3.3).
- `StyleDiagnostics.Explain` surfaces the originating `ResourceReference.Key` for resource-fed setter values (I expose it on the entry's debug surface).
- **Supersession note:** the hybrid proposal's `IStyleValueSink.OnResourcesChanged(subtreeRoot, change)` sweep entry-point is replaced by my per-node registry callbacks — styling must *not* build a parallel sweep.

**REQUIRES from XAML loader (Fork C):**
- Calls `ResourceDictionary.SetDeferred(key, IDeferredResourceEntry)` for lazy dictionary slices, capturing the lexical scope via my `ResourceScopes.ForDictionary` (name harmonization in §8 Q2).
- Pushes `IResourceHost`s on its ambient stack during instantiation; `StaticResource` resolves through `IResourceScope` chains I provide; `XamlLoadContext.AmbientResources` defaults to `ResourceScopes.ForApplication()`.
- `{DynamicResource key}` attaches via the `IDeferredValue.AttachTo` seam → routes to `SetResourceReference` (direct properties) or constructs `ResourceReference` (setter values). No sentinels through `SetValue`.
- **Honors `IResourceValueBuilder`** (§2.6): at end-of-object (after `ISupportInitialize.EndInit` when present), an instance implementing it is replaced by `Build()`'s result for dictionary insert / member assignment / collection add.
- **Converts `x:Key` by the target collection's key type** — a `ThemeDictionaryCollection` item's `x:Key` goes through `ThemeVariantKey.Parse`; registering the converter alone is insufficient without this loader semantic.
- Registers `ResourceDictionaryLoader.LoadCallback` at module init (for `Source=`); ships the `ThemeVariantKey.Parse` converter, the `IBrush`/`Pen`/`RelativePoint` text converters, and the `Cursorial.UI.Media` builder types in its default-xmlns map.

**REQUIRES from Element tree / lifecycle (S1):**
- `UIElement.LogicalParent`, `TemplatedParent`, `TemplateInstance` (on the templated parent; template + its resources), visual-root accessor; attach/detach hooks invoked on tree changes; `Application.Current` + window enumeration.
- **The template logical-chain guarantee (§3.3, named):** parts chain part → … → template root; template root has `LogicalParent == null`, `TemplatedParent == templated control`; DataTemplate content has a normal logical parent and null `TemplatedParent`.

**REQUIRES from App host / session subsystem:**
- Call `Application.OnCapabilitiesChanged(session.Capabilities)` at startup and after every successful `RenegotiateAsync`, **marshaled to the UI thread** (the host owns *when* to renegotiate; rendering-session.md: don't call mid-interaction).

**PROVIDES:**
- `ResourceDictionary` (+ deferred contract, `Styles` slot, pulse), the chain walk (`FindResource`/`TryFindResource`), `IResourceScope` + `ResourceScopes` (Fork C's ambient currency), `ResourceServices.Subscribe`/`SubscribeControlTheme`/**`GetResourceVersion`** (Fork B's hookup + **S8's formatted-text cache-key contract**, §2.4), `SetResourceReference`, `ResourceReference`, `ThemeVariant`/`ThemeVariantKey`/`ActualThemeVariant` + `ActualThemeVariantChanged` + **`Application.ResourcesChanged`**, `CursorialTheme.BuiltIn`/`CreateDefault`, `ThemeKeys`, `DataTemplateKey`, `ResourceBrushResolver.Create`, `IResourceValueBuilder` + the `Cursorial.UI.Media` builders, `ResourceDiagnostics`. Single implementation of resource lookup lives here (hybrid P3 honored).
- **DECISIONS amendment (recorded):** Fork C's line "re-resolved on `RenegotiateAsync`, pulsing `ResourceDictionary.Changed`" is satisfied by `Application.ResourcesChanged` + registry sweeps; per-dictionary `Changed` does not fire for variant flips (no dictionary mutates, and sealed dictionaries never pulse by design).

---

## 5. Requirement mapping

- **R3 (resource/style inheritance) — primary.** Three-axis inheritance: positional (chain walk, nearer shadows farther), thematic (variant probe within every hop, minimum-capability tier descent), and palette-indirection (built-in themes consume `ThemeKeys` via DynamicResource, so one key override re-skins the tree). Value inheritance proper stays Fork A's `Inherited` tier.
- **R1 (styling/templating).** Built-in control themes deliver default templates; deferred dictionary entries make 300-resource theme files cost inserts, not instantiations; the (sealed) template-resource hop supports template-shipped values.
- **R7 (XAML).** The deferred-entry runtime contract, ambient-scope implementations, `Source` loading, `ThemeVariantKey` parsing + the x:Key conversion semantic, and the `IResourceValueBuilder` media builders are exactly the loader's required seams.
- **R8 (setters + hybrid model).** `ResourceReference` as a first-class `Setter.Value`, riding frames at the Style slot with `StyleSortKey` — resource changes mutate entries; activation edges pause/resume (O(1), hot-path-safe).
- **R9 (prioritized sources).** Resource feeds are producers at `LocalValue` (direct) or `Style`-slot frames (setters); miss = `UnsetValue` ⇒ promotion, never clobbering; eviction via `IValueEvictionListener`; retraction is store-owned (**invariant 4**).
- **R5 (child windows).** Per-root registries; windows resolve through Application (decision §3.3); the modal-dim scrim brush (`ObscuredOverlayBrush`) is themed, consumed by the window manager's `obscured` class styles.
- **R6 (access keys).** The `:access-keys` underline appearance (underline brush/attributes) lives in built-in control themes per variant/tier — the toggle is styling's pseudo-class; the *look* is mine.
- **R10 (animation).** Animation rides above everything; a pulse during an animation updates the *base* silently (Fork A semantics), so storyboard handoff (`SnapshotAndReplace` via `GetBaseValue`) lands on fresh theme values with no special handling here.
- **Invariant compliance.** (1) Pulses are synchronous UI-thread events; `BeginUpdate` scopes are turn-bounded (debug-asserted). (2) No Scene/CellBuffer access anywhere; consequences flow only through `PropertyEffects`. (3) A resource brush change is content-shaped → `AffectsRender` re-raster; resource-fed opacity/offset properties route `AffectsComposite` — the metadata, not the resource system, decides. (4) Store-owned retraction throughout. (5) The chain walk respects the template barrier (template resources slot in only at the barrier crossing; no name/scope leakage). (6) All APIs UI-thread-affine, `VerifyAccess` debug-asserted; `Seal()`d dictionaries are the only cross-context-shareable form. (7) Drawing untouched — builders realize to existing Drawing types via a UI-owned seam.

---

## 6. Terminal-specific design

1. **The theme axis is 2-D: light/dark × `ColorDepth` tier.** Desktop frameworks key themes on Light/Dark/HighContrast; here the second axis is negotiated color capability. Tier probing **never ascends** — tier entries declare a minimum capability, so hand-picked `(Dark, Ansi16)` palette brushes beat the 6×6×6-cube quantizer instead of leaking truecolor intent; `(B,·)` is the last-probed, NoColor-safe catch-all (§3.2), so attribute-only `(·,NoColor)` entries actually win on monochrome terminals.
2. **Variant derives from the terminal, not the OS.** There is no OS dark-mode API for a TTY; the OSC 11 readback is ground truth, with Dark as the null-response default (terminal convention). `RenegotiateAsync` is the only re-derivation trigger besides the explicit overrides, and the host owns its timing (it parks the input pump ~500 ms — rendering-session.md).
3. **Resources hold terminal-native values.** `IBrush`/`Pen`/`Color`/`TextAttributes`/`UnderlineStyle`/`TextSizing` — no fonts, no DPI, no geometry transforms. Pens vary by tier (`Pens.Ascii` at low tiers — the drawing layer's "GlyphSet is a consumer policy" knob surfaces as theming; the color tier is a deliberate proxy for terminal age/Unicode coverage). Most are `readonly record struct`s, boxed once at builder realization / dictionary insert and shared thereafter (folded-constant discipline from Fork C; Fork A's box-interning covers the set path).
4. **The flip is cheap because the repaint is the cost.** A variant/theme change re-resolves a few thousand subscriptions (§3.5's honest envelope) and then the screen re-rasters once — two cache tiers below (scene raster cache, `FrameRenderer` front-buffer diff) mean the bytes emitted are exactly the changed cells. That is why "resource-event-only, no re-match" is affordable and correct. The *steady-state* cost this subsystem adds to the mouse-motion hot path is two flag writes per frame edge — by contract, not by accident.
5. **Brush names are one namespace across XAML, code, and text markup.** `[brush=Theme.AccentBrush]` (via `TextMarkupOptions.BrushResolver`, opaque-`object` tags keeping Rendering brush-blind) resolves through the same chain as `{DynamicResource Theme.AccentBrush}` — a terminal app's rich text and its widget chrome share a palette by construction; freshness rides the `GetResourceVersion` cache-key contract.
6. **The built-in theme adapts to the terminal; it does not reprogram it.** No OSC 4/10/11 palette writes from the theme in v1 (apps may use `TerminalPalette` themselves and rewrite the capability snapshot per the sanctioned demo pattern); palette-sync deferral recorded in §7.
7. **No ComponentResourceKey type (decided).** It solves cross-library key collisions at desktop-ecosystem scale; a terminal app has one or two resource-producing libraries. Stance: `Type` keys for control themes, `DataTemplateKey` for data templates, and **static classes of string constants** (`ThemeKeys` as the pattern) for everything else — discoverable, typo-proof, `{x:Static}`-referenceable, zero new key machinery. Re-addable additively if a library ecosystem materializes.

---

## 7. Phasing

**v1 spine:**
- **T0 — dictionary + chain**: `ResourceDictionary` (entries, merged, theme collections, `Styles` slot, deferred contract incl. throw-resets-to-Deferred and in-slot realization, pulse, seal + the `(B,·)`/`(·,T)` lint, `BeginUpdate` nesting/turn rules), chain walk + `ResourceParent` + template-barrier hop (against S1 stubs pinning the logical-chain contract), `FindResource`/`TryFindResource`, `IResourceScope`/`ResourceScopes`, `ResourceNotFoundException` with rendered chain. **Oracle-pinned lookup-order test matrix** authored before the engine, per the Fork A convention: shadowing, merged precedence, and the §3.2 **variant-probe truth table verbatim** (including the two regression rows: `(·,NoColor)` wins at NoColor over `(B,·)`; `(B,Ansi16)` does not shadow `(B,Ansi256)` at Ansi256).
- **T1 — variants + subscriptions**: `ThemeVariant`/`ThemeVariantKey` + the 8 precomputed probe tables, `Application` theme surface + `OnCapabilitiesChanged` (thread-affinity assert) + `ResourcesChanged`, the per-root registry with **snapshot/tombstone sweep semantics** (regression: theme swap with re-templating controls mutating the registry mid-sweep), `ResourceServices.Subscribe` (UnsetValue miss currency), `SetResourceReference` (+ eviction wiring), pause/resume (O(1), Resume-before-activate ordering test), forced re-resolve on attach (cross-root move test), pulse sweeps + fixpoint guard, `GetResourceVersion`.
- **T2 — built-in theme + builders**: `ThemeKeys`, `CursorialTheme.BuiltIn`/`CreateDefault` laid out per §3.7 tier rules (Dark/Light × Ansi256/Ansi16 + `(·,NoColor)`) for the v1 control set, `SubscribeControlTheme` (dual watch) + `Control.Theme`/`ControlThemeKey` + miss diagnostic, `ResourceBrushResolver`, `IResourceValueBuilder` + the `Cursorial.UI.Media` builder set (loader-integration tests live in Fork C's corpus).
- **T3 — diagnostics**: `Trace`/`Explain` (acceptance: one line per hop incl. probe keys tried), `Subscriptions`, `DeferredEntries` (incl. `RealizedAtVariant`), debug leak assert, in-demo resource-inspector hook alongside the styling overlay.

**Deferred (recorded with reasons, §11 convention):**
- **ComponentResourceKey-equivalent** — no collision pressure at terminal scale (§6.7); additive.
- **Per-window / per-scope `ThemeVariantScope`** — app-level override covers v1; `effective = nearest override ?? app` is additive; deferred until a dialog needs cross-variant chrome.
- **Resource lookup memo cache** — chain walk is not per-frame; add behind the same API only if profiling demands (benchmark-gated).
- **Owner-window resource chaining** — WPF-parity "no" for predictability (§8 Q1); additive.
- **`x:Shared="False"`** — Fork C punts it; dictionary side is trivially additive once the loader supports it.
- **DataTemplate-own `Resources` in the chain walk** — excluded in v1 (§3.3); additive once a scenario demands it.
- **`ImageBrush`/`TileBrush` media builders** — needs the URI/resource-loader story; additive (§2.6).
- **Dynamic `[brush=name]` re-resolution** — static-per-parse in v1; freshness via the now-contractual `GetResourceVersion` cache key (§2.4); true per-run subscriptions would cross the brush-blind boundary for no observed need.
- **Theme-driven terminal palette sync** (`ThemeOptions.SyncTerminalPalette` writing OSC 10/11) — interacts with capability rewriting and renegotiation; needs its own design pass.
- **Resource serialization / round-trip** — no scenario (XAML is the source of truth; hot reload re-parses); inspection APIs cover tooling.

---

## 8. Open questions

1. **Should a child `Window` chain resource lookup through its owner window before Application?** Owner-chaining makes a modal dialog inherit its opener's overrides; WPF does not chain, and implicit cross-window inheritance makes `Explain` output depend on runtime ownership wiring. **Recommendation: no (window → Application), matching WPF; owner-chaining recorded as an additive deferral** — apps that want sharing put resources at Application scope or merge the owner's dictionary explicitly.
2. **Naming collision on `IDeferredValue` (Fork C).** The runtime-loader proposal uses `IDeferredValue { Realize(IResourceScope) }` for lazy dictionary entries, but DECISIONS reassigns that name to the markup-extension `AttachTo` seam. **Recommendation: the dictionary contract is renamed `IDeferredResourceEntry` (signature unchanged, §2.1); the `AttachTo` seam keeps `IDeferredValue`.** Needs Fork C sign-off; zero semantic change.
3. **Do `[brush=name]` markup references update on theme flips?** Resolved into contract: static-per-parse, with the refresh recipe now implementable and pinned — S8 includes `(ResourceServices.GetResourceVersion(this), ActualThemeVariant)` in `FormattedText` cache keys (§2.4), so the next render after any pulse re-parses with fresh resolver output. Root-global granularity (any pulse invalidates all text caches in the window) is a stated, accepted property at rare-pulse cadence. Remaining open: nothing — kept here as the record of the decision.
4. **Relative priority of `(·,T)` vs `(B,·)` (§3.2).** Tier-specialization-first is pinned (tier entries exist to beat the quantizer), and the seal-time lint makes the ambiguous both-forms case loud. If real themes surface a need for base-specialization-first in some pocket, the answer is exact `(B,T)` keys, not a probe-order change — recorded so the order is never silently relitigated.

---

## Critique disposition

**P0-1 (variant probe order contradicts built-in + example) — ACCEPTED.** Probe order reordered to exact-base descent → wildcard-base descent → `(B,·)` last (§3.2); `(B,·)` contractually NoColor-safe; built-in restructured (RGB at `(B,Ansi256)`, nothing color-bearing in `(B,·)`, §3.7); seal/load-time lint for the `(B,·)`+`(·,T)` ambiguity; worked truth table pinned in §3.2 and the T0 oracle matrix; §2.8 example rewritten (`Dark+Ansi256` gradient so Ansi256 gets the quantized gradient, not the Ansi16 fallback) with the minimum-capability semantics documented inline.

**P0-2 (registry mutation mid-sweep unspecified) — ACCEPTED.** Snapshot semantics pinned (§3.5): copy-on-write per in-sweep bucket; subscribe-during-sweep not visited (fresh at Subscribe); dispose-during-sweep tombstoned and compacted after; theme-swap-with-re-templating regression added to T1.

**P1-3 (template logical-chain assumption) — ACCEPTED.** Named S1 REQUIRES (part→…→root chain; root `LogicalParent == null`, `TemplatedParent` = templated control); `ResourceParent` defined; pseudocode corrected (`tp.TemplateInstance`); DataTemplate content stance stated (no hop; DataTemplate-own Resources excluded in v1, recorded deferral).

**P1-4 (template resources vs single-parent/pulse) — ACCEPTED.** Template `Resources` sealed at template seal (Fork B contract line); hop static and pulse-free; sealed-share exemption named as what legalizes multi-instance slot-in; seal-time check added.

**P1-5 (variant pulse vs sealed dictionaries / DECISIONS letter) — ACCEPTED.** `Application.ResourcesChanged` added (§2.3); variant flips raise it independent of dictionary sealed-ness; DECISIONS amendment recorded in §4 PROVIDES.

**P1-6 (cache recipe references nonexistent API) — ACCEPTED.** `ResourceServices.GetResourceVersion(UIElement)` added to the API and PROVIDES; the S8 recipe moved into the §2.4 contract; root-global invalidation granularity stated as a property, not an accident.

**P1-7 (miss inexpressible in Subscribe/listener signatures) — ACCEPTED.** `UIProperty.UnsetValue` (mandated Fork A vocabulary) is the miss currency on `initialValue` and `OnResourceChanged` (including transition-to-missing); `UnsetValue` rejected as a stored value; pinned in the Fork A/B contract lines. (Chose the sentinel over `out bool` — one currency end-to-end, no signature churn.)

**P1-8 (theme-styles channel hand-waved) — ACCEPTED.** Typed `ResourceDictionary.Styles` slot (§2.1); consumption contract pinned (§3.7(3)): Application.Theme only, merge-order flattening, read-at-set + re-read on theme-origin CatchAll, layer `Theme(2)`; added to the Fork B REQUIRES table.

**P1-9 (cross-root version compare) — ACCEPTED.** Attach always forces one re-resolve regardless of stored version (§3.8, §2.4); test in T1.

**P1-10 (segregated active list taxes the hot path) — ACCEPTED.** One list + `Paused` flag tested during rare sweeps; `Pause`/`Resume` are O(1) allocation-free flag writes (§2.4, §3.5); the "nothing here is per-frame" self-image corrected in §3.3/§6.4 — this subsystem *is* on the any-event motion hot path via styling's edges, and is costed for it.

**P1-11 (flagship XAML unimplementable against Drawing types) — ACCEPTED** (critique's option b, grounded in Fork C's existing converter plan). New §2.6: `Cursorial.UI.Media` builder types + the `IResourceValueBuilder` end-of-object replacement seam (Fork C REQUIRES); attribute-text path already covered by Fork C's `IBrush`/`Pen` converters; Drawing untouched (invariant 7). Example fixed (`GlyphSet="Ascii"`, builder-shaped gradient).

**P2-12 (ThemeProperty changes vs SubscribeControlTheme) — ACCEPTED.** `SubscribeControlTheme` owns both watches under one handle (§2.4); Fork B told not to watch separately.

**P2-13 (backstop claim over-broad) — ACCEPTED.** Claim scoped to partial app themes over the shipped key set; `MyButton : Button` behavior documented at `ControlThemeKey` (§2.3, §3.7); one-time miss diagnostic added.

**P2-14 (Realize throw leaves Realizing) — ACCEPTED.** Throw resets to `Deferred` (retry); exception propagates; pinned by test (§3.1, T0).

**P2-15 (realize-in-place vs enumeration) — ACCEPTED.** Payload swaps inside the `DeferredSlot`; the backing `Dictionary` slot is never replaced during enumeration (§3.1).

**P2-16 (RequestedColorTier vs caps-* classes) — ACCEPTED** as a cross-subsystem decision: styling stamps color-tier classes from the *effective* tier (`ActualThemeVariant.Tier`); non-color classes stay negotiated (§3.6, Fork B REQUIRES).

**P2-17 (tier-baked Dark/Light statics) — ACCEPTED.** Statics removed; `IsDark`/`IsLight` helpers added (§2.2).

**P2-18 (subscription envelope ~10× low) — ACCEPTED.** Arithmetic redone honestly: low thousands of nodes incl. paused child-rule subscriptions, ~100–200 KB per root, low-single-digit-ms catch-all worst case (§3.5, §6.4).

**P2-19 (Resume-vs-activate ordering) — ACCEPTED.** Resume-before-activate pinned on `Resume()` and in the Fork B contract; tested in T1.

**P2-20 (mutable struct handle semantics) — ACCEPTED.** Pinned on the type: node-reference wrapper, copies share, `default` safe, Dispose idempotent across copies (§2.4).

**P2-21 (OnCapabilitiesChanged thread affinity) — ACCEPTED.** Host must marshal to the UI thread; stated on the API and in the host REQUIRES (§2.3, §4).

**P2-22 (BeginUpdate edges) — ACCEPTED.** Refcounted nesting; `Seal()` inside an open scope throws; same-dispatcher-turn disposal debug-asserted (§2.1, §3.5).

**P2-23 (x:Key conversion needs a loader semantic) — ACCEPTED.** Explicit Fork C REQUIRES: x:Key converted by the target collection's key type (§4).

**P2-24 (StaticResource freeze nondeterminism in deferred entries) — ACCEPTED.** Documented (§3.1); DynamicResource recommended for variant-sensitive theme-file references; `RealizedAtVariant` added to `DeferredEntryInfo` (§2.7).

**P2-25 (LoadCallback global) — ACCEPTED.** Null-callback `Source` set throws an informative `InvalidOperationException`; test-isolation hazard noted on the type (§2.1).

**REBUTTED: none.** Every finding was concretely grounded; where the critique offered alternatives, the chosen option is named above (P1-7 sentinel over out-param; P1-8 typed slot over key constant; P1-10 single-list flag; P1-11 builders + the already-planned Fork C converters).