# Fork C — XAML Pipeline for Cursorial.UI: The Custom Runtime Loader

**Author stance:** advocate for a zero-dependency, `XmlReader`-based runtime XAML loader with a parse-once / instantiate-many node graph, full diagnostic ownership, and explicit seams for a later compiled path.

---

## 1. Executive summary & philosophy

XAML in Cursorial.UI is **processed at runtime, validated at build time**. There is exactly **one execution path**: the runtime loader. Build-time tooling (an optional Roslyn source generator + an MSBuild validation hook) runs *the same parser* for diagnostics and code-behind field generation, but never generates object-construction code in v1. One semantic implementation; zero drift.

The core architectural move is splitting the pipeline into two stages with an immutable artifact between them:

```
 .xaml bytes ──XmlReader──▶  XamlDocument (immutable, resolved, folded node graph)
                                   │ cached per URI, shared, thread-safe
                                   ▼
                          XamlInstantiator walk ──▶ live widget tree
                                   ▲
                  templates = slices of the same node graph,
                  re-walked per Build() — no re-parse, no reflection
```

Everything expensive and fallible — XML parsing, xmlns/type resolution, member resolution, markup-extension parsing, type-converter selection, and *constant folding of literal values* — happens once, in stage 1, with line/column info attached to every node. Stage 2 is a tight array walk calling cached delegates. Deferred content (templates, resource-dictionary entries) is not "stored XML to re-parse" and not "compiled factory delegates" — it is a contiguous **slice of the already-resolved node graph**, instantiated on demand. Templates therefore get parse-time error checking, near-zero storage overhead (they share the document's arrays), and instantiation cost proportional only to the objects they create.

Why a custom runtime loader fits *this* project specifically:

1. **Scale.** Terminal apps have hundreds of elements, not tens of thousands. A whole theme file parses in well under a millisecond; the desktop-class argument for ahead-of-time XAML compilation (multi-second WPF startup parse) does not exist here.
2. **Zero external dependencies** is an established repo value (the stack hand-rolls PNG decoding rather than take a dependency). `System.Xaml` is Windows-Desktop-only; `Portable.Xaml`/`XamlX` are third-party dependencies with their own semantics and AOT problems.
3. **Diagnostics are a feature.** A TUI has no visual designer; the dev loop is *edit → run in terminal*. Owning the parser means owning error messages (`MainWindow.xaml(42,17): CUR1203: StaticResource 'AccentBrush' not found; searched: TemplateScope('ButtonChrome') → Window('MainWindow') → Application`) and makes hot reload — the terminal world's substitute for a designer — structurally cheap.
4. **The compiled path stays open.** Every consumer-visible seam (`ITemplateContent`, `IXamlTypeMetadataProvider`, `XamlLoader.LoadComponent`) is producer-agnostic. A future compiler is an alternate producer behind the same interfaces, not a breaking change. Section 7 details the migration.

---

## 2. Public API sketch

All types live in **`Cursorial.UI.Markup`** (assembly `Cursorial.UI`) unless noted. The optional generator ships as a separate analyzer package **`Cursorial.UI.Generators`**.

### 2.1 Loader, document, options

```csharp
public sealed class XamlLoader
{
    public XamlLoader(XamlLoaderOptions? options = null);
    public static XamlLoader Shared { get; }                  // default options, process-wide caches

    // Stage 1 — parse. Pure, thread-safe; results immutable and shareable.
    public XamlDocument Parse(Stream xml, Uri? sourceUri = null);
    public XamlDocument Parse(string xml, Uri? sourceUri = null);
    public XamlDocument GetOrParse(Uri sourceUri);            // per-loader cache keyed by URI

    // Stage 2 — instantiate. Not thread-safe per call; run on the UI/render thread.
    public object Load(XamlDocument document, XamlLoadContext? context = null);
    public T Load<T>(XamlDocument document, XamlLoadContext? context = null) where T : class;
    public object Load(Uri sourceUri, XamlLoadContext? context = null);

    // Code-behind entry point: locates the document for component's type via the
    // x:Class ⇒ embedded-resource convention and populates the existing instance.
    public void LoadComponent(object component);
    public static void LoadComponent(object component, Uri sourceUri);   // uses Shared
}

public sealed class XamlLoaderOptions
{
    public IXamlTypeMetadataProvider MetadataProvider { get; init; }   // default: ReflectionXamlMetadata.Instance
    public IXamlResourceProvider ResourceProvider { get; init; }       // default: embedded-resource resolver
    public XamlDiagnosticMode DiagnosticMode { get; init; }            // ThrowOnFirstError (default) | CollectAll
    public bool FoldConstants { get; init; } = true;                   // eager literal conversion at parse time
    public CultureInfo ConverterCulture { get; init; }                 // default: invariant
}

public sealed class XamlLoadContext
{
    public object? RootInstance { get; init; }            // x:Class population (LoadComponent sets this)
    public IResourceScope? AmbientResources { get; init; }// outermost lookup scope, usually Application.Resources
    public IServiceProvider? Services { get; init; }      // surfaced to markup extensions / converters
    public INameScope? NameScope { get; init; }           // override the document namescope (rare)
}

public sealed class XamlDocument
{
    public Uri? SourceUri { get; }
    public Type RootType { get; }
    public string? RootClassName { get; }                 // x:Class value, if any
    public IReadOnlyList<XamlDiagnostic> Diagnostics { get; }  // warnings always; errors in CollectAll mode
    // internals: the flat node arrays of §3 — immutable, shared by every Load and every template Build
}
```

### 2.2 Diagnostics

```csharp
public enum XamlDiagnosticSeverity { Warning, Error }

public readonly record struct XamlDiagnostic(
    string Code,                    // "CUR1xxx" parse, "CUR2xxx" resolution, "CUR3xxx" instantiation
    string Message,
    XamlDiagnosticSeverity Severity,
    Uri? Source, int Line, int Column);

public sealed class XamlParseException : FormatException
{
    public Uri? Source { get; }
    public int Line { get; }
    public int Column { get; }
    public IReadOnlyList<XamlDiagnostic> Diagnostics { get; }   // ≥ 1; first is the thrown one
}
```

Instantiation-time failures (missing StaticResource, converter failure on a context-dependent value, event handler not found on the code-behind class) throw `XamlParseException` too — the instantiator always knows the current node, so **runtime errors carry source line/column as well**.

### 2.3 Type metadata & converters (the AOT seam)

```csharp
public interface IXamlTypeMetadataProvider
{
    XamlType? TryGetType(string xmlNamespace, string localName);
    void RegisterXmlnsDefinitions(IXmlnsRegistry registry);     // assembly-level URI→CLR-namespace maps
}

public sealed class XamlType        // built once per CLR type, cached
{
    public Type ClrType { get; }
    public Func<object>? Activate { get; }                      // parameterless-ctor thunk
    public string? ContentProperty { get; }
    public bool IsCollection { get; }
    public Action<object, object?>? AddItem { get; }            // IList.Add / duck-typed Add
    public Action<object, object, object?>? AddDictionaryItem { get; } // (dict, key, value)
    public XamlMember? TryGetMember(string name);
    public bool RequiresInitialize { get; }                     // ISupportInitialize
}

public sealed class XamlMember
{
    public string Name { get; }
    public Type ValueType { get; }
    public UIProperty? Property { get; }              // Fork A handle when registered (incl. attached)
    public Action<object, object?>? SetClr { get; }   // CLR fallback setter
    public Func<object, object?>? Get { get; }        // read-only collection members ("get-object")
    public ITypeConverter? Converter { get; }         // resolved once: member-level → type-level → registry
    public bool IsEvent { get; }
    public bool IsDeferredContent { get; }            // [DeferredContent] — see §3.5
}

public interface ITypeConverter
{
    object? ConvertFromString(string text, in XamlValueContext context);
    bool IsContextFree { get; }    // true ⇒ eligible for parse-time constant folding
}

public readonly struct XamlValueContext
{
    public Uri? BaseUri { get; }
    public IServiceProvider? Services { get; }
    public CultureInfo Culture { get; }
    public Type TargetType { get; }
}

public static class XamlConverters       // process-wide registry; populate at module init
{
    public static void Register(Type targetType, ITypeConverter converter);
    public static ITypeConverter? For(Type targetType);   // also exposed to Fork B (DataTrigger.Value etc.)
}
```

`ReflectionXamlMetadata` (the default provider) is annotated `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]` honestly; a generated provider replaces it for trimmed/AOT builds (§3.8).

### 2.4 Markup extensions

```csharp
public abstract class MarkupExtension
{
    public abstract object? ProvideValue(IServiceProvider services);
}

// Services available from the IServiceProvider during ProvideValue:
public interface IProvideValueTarget   { object TargetObject { get; } object? TargetProperty { get; } } // UIProperty or XamlMember
public interface IRootObjectProvider   { object RootObject { get; } }
public interface IXamlLineInfo         { Uri? Source { get; } int Line { get; } int Column { get; } }
public interface IAmbientResources     { IResourceScope LexicalScope { get; } }     // Fork B type, see §5
public interface ITemplateHost         { object? TemplatedParent { get; } }         // non-null inside template Build
public interface INameScopeProvider    { INameScope NameScope { get; } }
```

Built-in extensions (`x:Null`, `x:Static`, `x:Type`, `StaticResource`, `DynamicResource`, `Binding`, `TemplateBinding`) are **recognized at parse time and represented as typed nodes** — the instantiator handles them inline with zero allocation for the extension object itself. User-defined extensions go through the general `MarkupExtension` path (activate, set members, `ProvideValue`).

### 2.5 Names, templates

```csharp
public interface INameScope
{
    void Register(string name, object element);      // duplicate ⇒ XamlParseException with both positions
    object? Find(string name);
}

public static class NameScopeExtensions
{
    public static T? FindControl<T>(this Control root, string name) where T : class;
    public static T RequireControl<T>(this Control root, string name) where T : class;  // throws with available-name list
}

public interface ITemplateContent                    // THE deferred-content currency (cross-fork, §5)
{
    object Build(in TemplateBuildContext context);
}

public readonly struct TemplateBuildContext
{
    public object? TemplatedParent { get; init; }    // ControlTemplate: the control; DataTemplate: null
    public INameScope NameScope { get; init; }       // fresh per Build — the template namescope
    public IResourceScope? InstantiationScope { get; init; }
    public IServiceProvider? Services { get; init; }
}
// internal sealed class XamlTemplateContent : ITemplateContent
//     — (XamlDocument doc, int sliceStart, IResourceScope? capturedLexicalScope)
```

### 2.6 Authoring attributes

```csharp
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class XmlnsDefinitionAttribute(string xmlNamespace, string clrNamespace) : Attribute;

[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class ContentPropertyAttribute(string name) : Attribute;

[AttributeUsage(AttributeTargets.Property)]
public sealed class DeferredContentAttribute : Attribute;    // value delivered as ITemplateContent

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class XamlMetadataProviderAttribute(Type providerType) : Attribute;  // AOT registration
```

### 2.7 Consumer experience

**MainWindow.xaml**

```xml
<Window xmlns="https://cursorial.dev/ui"
        xmlns:x="https://cursorial.dev/xaml"
        xmlns:local="using:DemoApp"
        x:Class="DemoApp.MainWindow"
        Title="Cursorial Demo">
  <Window.Resources>
    <ResourceDictionary>
      <ResourceDictionary.MergedDictionaries>
        <ResourceDictionary Source="cursorial://DemoApp/Themes/Dark.xaml"/>
      </ResourceDictionary.MergedDictionaries>
      <SolidColorBrush x:Key="AccentBrush" Color="#66d9ef"/>
      <Style x:Key="ToolButton" TargetType="Button">
        <Setter Property="Padding" Value="2,0"/>
        <Setter Property="Background" Value="{DynamicResource AccentBrush}"/>
      </Style>
    </ResourceDictionary>
  </Window.Resources>

  <DockPanel>
    <Menu DockPanel.Dock="Top">
      <MenuItem Header="_File">
        <MenuItem Header="_Save" Command="{Binding SaveCommand}" InputGesture="Ctrl+S"/>
      </MenuItem>
    </Menu>
    <Button x:Name="RunButton" Style="{StaticResource ToolButton}"
            Click="OnRunClicked" Content="_Run"/>
    <ListBox ItemsSource="{Binding Results}">
      <ListBox.ItemTemplate>
        <DataTemplate>
          <TextBlock Text="{Binding Name}" Foreground="{Binding Status, Converter={StaticResource StatusToBrush}}"/>
        </DataTemplate>
      </ListBox.ItemTemplate>
    </ListBox>
  </DockPanel>
</Window>
```

**MainWindow.xaml.cs** (no generator installed):

```csharp
public partial class MainWindow : Window
{
    public MainWindow()
    {
        XamlLoader.LoadComponent(this);                       // parse-cached after first window
        _runButton = this.RequireControl<Button>("RunButton");
    }
    private readonly Button _runButton;
    private void OnRunClicked(object? sender, ClickEventArgs e) { /* … */ }
}
```

With `Cursorial.UI.Generators` referenced, the generated partial supplies `InitializeComponent()` and a typed `RunButton` field (`FindControl` performed once inside `InitializeComponent`), and XAML errors appear in the IDE/build with file/line/column — produced by *this same parser* running as an analyzer.

**.csproj** (the whole experience):

```xml
<ItemGroup>
  <PackageReference Include="Cursorial.UI" Version="…" />
  <PackageReference Include="Cursorial.UI.Generators" Version="…" PrivateAssets="all" /> <!-- optional -->
</ItemGroup>
<!-- Cursorial.UI.targets (shipped in the package) does automatically:
     <EmbeddedResource Include="**\*.xaml" LogicalName="$(RootNamespace)/%(RelativeDir)%(Filename).xaml" />
     <AdditionalFiles Include="**\*.xaml" />   (only when the generator package is present) -->
```

No MSBuild task runs in v1; embedding is declarative item-group wiring. `cursorial://<assembly>/<path>` URIs resolve through `IXamlResourceProvider` to those embedded resources (tests can substitute an in-memory provider).

---

## 3. Internal architecture

### 3.1 Storage layout — the structure-of-arrays node graph

A `XamlDocument` owns six flat arrays. Nodes are structs linked by index; **children are stored contiguous and depth-first**, so any subtree is `(startIndex, SubtreeLength)` — which is exactly what a deferred-content slice is.

```csharp
internal readonly struct ObjectRecord
{
    public readonly int TypeId;            // → ResolvedTypes[] (XamlType)
    public readonly int MemberStart;       // → Members[], contiguous run
    public readonly ushort MemberCount;
    public readonly ObjectFlags Flags;     // IsRoot | HasName | HasKey | NeedsBeginInit | IsGetObject …
    public readonly int SubtreeLength;     // ObjectRecords in this subtree incl. self — O(1) slice/skip
    public readonly int LineInfo;          // packed: line in low 20 bits (clamped), column in high 12
}

internal readonly struct MemberRecord
{
    public readonly int MemberId;          // → ResolvedMembers[] (XamlMember + provenance flags)
    public readonly XamlValueKind Kind;    // Folded | Text | Object | Items | Extension | Deferred | Event | Directive
    public readonly int ValueIndex;        // Folded→Constants[]; Text→Strings[]; Object→Objects[];
                                           // Items→ItemRuns[]; Extension→Extensions[]; Deferred→Objects[] (slice head)
    public readonly int LineInfo;
}

internal readonly struct ExtensionRecord   // built-ins as a closed enum; user extensions as Object reference
{
    public readonly ExtensionKind Kind;    // Null | Static | Type | StaticResource | DynamicResource
                                           // | Binding | TemplateBinding | Custom
    public readonly int Payload;           // Static/Type → Constants[] (folded at parse);
                                           // *Resource → Strings[] (key); Binding → BindingRecords[];
                                           // Custom → Objects[] (an ordinary object subtree)
}
```

Document-level: `ObjectRecord[] Objects; MemberRecord[] Members; object?[] Constants; string[] Strings; ExtensionRecord[] Extensions; XamlType[] ResolvedTypes; XamlMember[] ResolvedMembers;`. Everything is immutable after parse; documents are freely shared across threads and across template builds. Parse uses growable pooled builders, then trims to exact arrays — one allocation per array at the end.

### 3.2 Stage 1 — parsing & resolution

Single pass over `XmlReader` (`IgnoreComments`, `IgnoreProcessingInstructions`, `DtdProcessing = Prohibit`; the reader implements `IXmlLineInfo`, which feeds every record's `LineInfo`). Per element:

1. **Resolve the type** through the xmlns scope stack. URI namespaces (`https://cursorial.dev/ui`) map through `XmlnsDefinitionAttribute` tables; `using:Ns.Name` and `clr-namespace:Ns;assembly=Asm` map directly (both spellings accepted — Avalonia and WPF muscle memory both work). Misses are errors with did-you-mean suggestions (Levenshtein over the namespace's known type names — we own the resolver, so we can do this).
2. **Classify attributes**: directives (`x:Name`, `x:Key`, `x:Class`…), attached members (`Grid.Column` — resolved via Fork A's property registry against the *owner* type), events, plain members. Property-element syntax (`<Button.ContentTemplate>`) re-enters the same member path.
3. **Resolve members once** into the shared `ResolvedMembers` table. Member resolution order: registered `UIProperty` on the type (Fork A registry — reflection-free) → CLR property via the metadata provider → error CUR2102 with the type's member list.
4. **Parse markup extensions** with a recursive-descent parser over the attribute text (grammar below). Built-ins become `ExtensionRecord`s; `{x:Static}`, `{x:Type}`, `{x:Null}` are **folded to constants immediately** (the resolver is right there).
5. **Fold literals.** If the member's converter `IsContextFree` (Thickness, Color, enums, GridLength, int, bool, TimeSpan, Easing… — nearly everything), convert *now* and store the boxed result in `Constants`. `Margin="1,2"` costs one parse + one box per *document*, not per instantiation — every template build reuses the same boxed `Thickness`. Context-dependent conversions (relative URIs, anything needing services) stay as `Text` and convert in stage 2 — still with cached converter references.
6. **Whitespace:** element text is trimmed at both ends; interior newline+indent runs collapse to a single space; `xml:space="preserve"` is honored. (Deliberately simpler than WPF's east-asian-aware model; documented.)

Markup-extension grammar (hand-rolled, ~200 lines, fuzz-tested):

```
Extension := '{' Name ( WS Positional (',' Positional)* )? ( ',' Named )* '}'
Named     := Name '=' Value
Value     := Extension | "'" QuotedChars "'" | BareChars      // '\' escapes within both
Literal   := '{}' rest-of-text                                 // brace-escape prefix
```

Positional arguments map to the extension's primary member (`Path` for Binding, `ResourceKey` for the resource extensions, ctor-arity match for custom extensions).

**Setters get special folding.** Inside a `<Style TargetType="Button">`, `<Setter Property="Background" Value="…"/>` resolves `Background` against the lexically known `TargetType` at end-of-object (attribute order independent) and folds `Value` through the *property's* converter. A `Setter` without resolvable owner context is CUR2110 at parse time — WPF defers this to a runtime exception; we don't have to.

### 3.3 Stage 2 — instantiation

An index-walk with an explicit context (recursion over `Objects`, bounded by markup nesting depth). Per `ObjectRecord`:

```
instance = (IsRoot && context.RootInstance != null) ? context.RootInstance
                                                    : type.Activate()           // cached thunk
if NeedsBeginInit: ((ISupportInitialize)instance).BeginInit()
if HasName:        currentNameScope.Register(name, instance)
if instance is IResourceHost h: push h onto the lexical resource stack          // for StaticResource
for each MemberRecord:
    value = Kind switch
        Folded    → Constants[ValueIndex]                                       // zero work
        Text      → member.Converter.ConvertFromString(text, ctx)               // context-dependent only
        Object    → instantiate subtree (recurse)
        Items     → get-or-activate collection, Add per item (dictionary items use x:Key)
        Extension → EvaluateExtension(record)                                   // §3.4
        Deferred  → new XamlTemplateContent(doc, sliceStart, captureLexicalScope())
        Event     → bind to handler on root instance (see below)
    assign:
        member.Property != null → instance.SetValue(member.Property, value, provenance)  // Fork A
        else                    → member.SetClr(instance, value)
if NeedsBeginInit: EndInit()
pop resource host
```

**Provenance** rides on the set call (Fork A contract, §5): document-level XAML sets `Local`; template-build sets `TemplateLocal`. This is what makes template values overridable by document-level values without hacks, matching WPF's precedence ladder.

**Events** resolve `Click="OnRunClicked"` against the root instance's type via the metadata provider (`Delegate.CreateDelegate` on the cached `MethodInfo`; a generated provider supplies a typed thunk instead). Events inside deferred content are CUR2301 at parse time in v1 (WPF allows them with namescope gymnastics; templates should use commands/TemplateBinding).

**Batching:** `ISupportInitialize` bracketing means a control sees all its XAML properties before reacting; combined with Fork A's expected "no change notification storm during init" behavior, loading never causes per-property invalidation cascades into layout.

### 3.4 Extension evaluation & resource scoping

- `StaticResource` walks: current **lexical** scope stack (the `IResourceHost`s pushed by the instantiator, innermost first) → `context.AmbientResources` (Application). Found value is assigned as an ordinary value. Forward references within a dictionary are an error (WPF rule), reported with both line positions.
- `DynamicResource` does *not* resolve; it assigns Fork B's `ResourceReference(key)` through the same provenance-tagged set — Fork A/B own re-evaluation when scopes change. The loader's only job is constructing the reference.
- `Binding` constructs Fork B's `Binding` description (all parse work — path string, mode, converter sub-extension — already done in stage 1's `BindingRecord`) and calls `BindingOperations.Apply(instance, member.Property!, binding, scopeServices)`. Binding to a CLR-only (non-UIProperty) member is CUR2210 at parse time.
- `TemplateBinding` is only legal inside deferred content (parse-time check); at build time it applies Fork B's optimized one-way-to-templated-parent entry using `TemplateBuildContext.TemplatedParent`.
- **Custom extensions**: instantiate the subtree like any object, then `ProvideValue(services)` where `services` is a pooled, stack-allocated-feel provider exposing the §2.4 interfaces (`IProvideValueTarget`, `IXamlLineInfo` from the current record, etc.).

### 3.5 Deferred content — the template mechanism in detail

A member marked `[DeferredContent]` (the `Content`/`Template` properties of `ControlTemplate`, `DataTemplate`, `ItemsPanelTemplate`) changes only what the *parser emits*: the subtree is parsed, resolved, extension-parsed, and folded **exactly like live content** — type errors inside a template body surface at parse time with line info — but the parser emits a `Deferred` member record pointing at the slice head instead of instantiation work.

At document-load time, encountering that member costs **one allocation**: `new XamlTemplateContent(document, sliceStart, capturedLexicalScope)`. The captured lexical scope is the resource chain enclosing the template *definition* (e.g. the `Window.Resources` above it), so `StaticResource` inside a template body resolves: template's own `<…​.Resources>` → captured lexical chain → instantiation-site scope. (This matches WPF's observable behavior for the dominant cases; the precise rule is documented and tested rather than inherited as folklore.)

Each `Build(in TemplateBuildContext)`:

1. Creates a fresh `NameScope` (the **template namescope**).
2. Runs the §3.3 walk over the slice with provenance `TemplateLocal`, `TemplatedParent` in context, `TemplateBinding` enabled.
3. Returns the root; Fork B's `Control.ApplyTemplate` attaches the namescope to the templated parent so `GetTemplateChild`/`FindControl` and `ElementName` bindings inside the template resolve template-locally (and *only* template-locally — no leakage either direction).

Cost per build ≈ live-object allocations + cached-delegate calls. No XML, no reflection lookups, no converter parsing (folded constants are shared boxes — safe because folded values are immutable value-type boxes or immutable objects like brushes). A `ListBox` realizing 500 rows through a `DataTemplate` does zero parsing work.

**Resource dictionaries get the same treatment for free.** Every keyed entry in a `ResourceDictionary` is stored as a deferred slice (keys — `x:Key`, or implicit `Style.TargetType` / `DataTemplate.DataType` — are literal attributes read at parse time without instantiation). The loader fills the dictionary with `SetDeferred(key, IDeferredValue)` entries (Fork B contract); first lookup realizes and caches. A 300-resource theme file at startup costs: parse (cached) + 300 dictionary inserts — not 300 brush/style instantiations. This is WPF's BAML deferred-dictionary optimization, recovered from the same mechanism as templates.

### 3.6 Namescopes

- Document namescope: created per `Load`, attached to the root (`Window`, `UserControl`). `x:Name` registers during instantiation in document order.
- Template namescope: fresh per `Build` (§3.5).
- Merged/source dictionaries and styles do **not** create namescopes; `x:Name` inside a resource dictionary is CUR2304.
- `INameScope` lookups walk: own scope → (for elements inside a template instance) stop. Document elements never see template names; `FindControl` on the templated control itself consults the template scope via `GetTemplateChild` only.

### 3.7 Hot reload (in principle; phased)

Because runtime loading is the only path, hot reload is a dev-mode add-on, not an architecture change: a file watcher re-parses the changed URI (replacing the cache entry under a version stamp), then for each live root the loader recorded (weak registry, opt-in via `XamlHotReload.Enable(loader)`): rebuild content, transplant `DataContext`, clear template caches on affected controls, and let owner-driven invalidation (`Scene.Invalidate` from the widget layer) repaint. v1 semantics are deliberately blunt (rebuild the window's content); field re-binding and state preservation hooks come later. No proposal here requires it — but no competing approach gets it this cheaply.

### 3.8 Reflection inventory & the AOT path

Reflection lives **only** inside `ReflectionXamlMetadata` and is cached per type:

| Use | Mechanism | AOT replacement |
|---|---|---|
| xmlns→type | `Assembly.GetType`, `XmlnsDefinitionAttribute` scan (once per assembly) | generated provider table |
| Activation | compiled `Expression` thunk when `RuntimeFeature.IsDynamicCodeSupported`, else `Activator.CreateInstance` | generated `() => new Button()` |
| CLR setters/getters | `MethodInfo.CreateDelegate` typed-thunk trick | generated lambdas |
| Events | `Delegate.CreateDelegate` to code-behind method | generated `+=` thunk |
| `x:Static` | `FieldInfo/PropertyInfo.GetValue` (folded once at parse) | generated accessor |
| Custom extensions | activation + member sets (same paths as above) | same |

UIProperty members never need reflection at all — Fork A's registry is the lookup. Binding *path* reflection is Fork B's concern (stated in §5; the generator package is the natural shared home for binding-path accessor generation later).

The AOT story is therefore: ship `Cursorial.UI.Generators` "metadata mode" — it reads the project's XAML (already `AdditionalFiles`), computes the closure of referenced types/members/extensions, and emits a `GeneratedXamlMetadata : IXamlTypeMetadataProvider` registered via `[assembly: XamlMetadataProvider(typeof(GeneratedXamlMetadata))]` plus a module initializer. With it present, the reflection provider is never consulted for covered types; apps that also avoid reflection bindings become trim/AOT-clean. The loader core itself (parser, node graph, instantiator) contains no reflection and is annotation-clean today. This is incremental: reflection works everywhere now; AOT-safety arrives by adding a package, not by rewriting the loader or consumer code.

### 3.9 Startup cost, honestly

- Parse throughput: `XmlReader` runs at tens of MB/s; a large terminal-app document (50 KB, ~600 elements) parses + resolves in roughly **1–3 ms** cold (dominated by first-touch member resolution), well under 1 ms warm. Theme dictionaries amortize via deferred entries.
- First-window overhead: xmlns attribute scan per referenced assembly (~tens of µs each), converter registry init (static tables).
- Instantiation: ~600 objects ≈ allocation cost + delegate calls; **tens of µs**, identical to what hand-written C# construction would pay.
- Per-frame cost: **zero**. The loader runs at load/template-build time only; it allocates nothing during the render loop. Template realization during scrolling reuses folded constants and cached thunks.
- Worst plausible case (huge app, hundreds of XAML files): `GetOrParse` is per-URI lazy; nothing parses until referenced. An optional `XamlLoader.PreloadAsync(IEnumerable<Uri>)` warms caches on a background thread (documents are immutable; parse is thread-safe).

---

## 4. Requirement satisfaction

- **R7 (XAML, the assignment):** Full pipeline above — parsing (§3.2), instantiation (§3.3), type resolution (§3.2.1), converters/content/collections/attached properties (§3.2–3.3), markup extensions (§3.4), x:Name/code-behind (§2.7, §3.6), deferred templates (§3.5), namescopes (§3.6), line-accurate errors (§2.2), resource dictionaries incl. merged + themes (§3.5, §2.7), AOT stance (§3.8), testability (§7). Processing: **runtime execution, build-time validation** (§1, §2.7).
- **R1 (styling/templating):** `Style`, `Setter`, `ControlTemplate`, `DataTemplate` are first-class markup citizens; Setter property/value folding (§3.2) gives parse-time type checking WPF lacks; `ITemplateContent` is the loader-agnostic currency Fork B consumes.
- **R2 (binding):** `{Binding}` parsed once into `BindingRecord`s; applied through Fork B's `BindingOperations`. `ElementName` resolves through the correct namescope (document vs template). Parse-time validation that the target is a bindable property.
- **R3 (resource/style inheritance):** lexical + ambient `StaticResource` walk, `DynamicResource` references, merged dictionaries with URI loading and per-document parse caching, deferred dictionary entries, implicit style/template keys.
- **R4 (focus):** orthogonal to markup, but `x:Name`/`FindControl` and attached-property syntax (`FocusManager.IsFocusScope="True"`) come from here.
- **R5 (child windows):** windows are just root types; `XamlLoader.Load<DialogWindow>(uri)` per instance; nothing special needed.
- **R6 (access keys):** the text pipeline preserves `_` verbatim (no underscore interpretation in the loader — header parsing is the control's job); `KeyGesture` converter (`"Ctrl+S"`, `"Alt+F"`) ships in the converter registry; the Alt-toggle behavior is capability-gated UI logic in another fork.
- **R8 (Setters + Triggers/selectors):** Setters per above. If Fork B chooses WPF Triggers, `<Trigger Property="IsPointerOver" Value="True">` resolves/folds the same way as Setter; `DataTrigger.Value` stays a string + the property converter is applied by Fork B at evaluation time via the exposed `XamlConverters` registry (binding result type is unknowable at parse). If Fork B chooses Avalonia selectors, `Selector="Button:pointerover .accent"` is a string handed to Fork B's selector parser **with line info attached** so selector syntax errors still report XAML positions. The loader is deliberately neutral.
- **R9 (property system):** every member set funnels through Fork A's `SetValue(prop, value, provenance)` when a `UIProperty` exists, with `Local` vs `TemplateLocal` provenance — the loader is a well-behaved value source, not a backdoor CLR mutator.
- **R10 (animation):** XAML instantiates the UI layer's mutable storyboard/animation description objects (e.g. `<DoubleAnimation From="0" To="1" Duration="0:0:0.3" Easing="CubicOut"/>`); converters for `TimeSpan`, `Easing` (name → `Easings` catalog member), `RepeatBehavior` ship in the registry. (`Cursorial.Animation`'s immutable ctor-based types are built *by* those descriptions — XAML-friendly parameterless+props shape stays in the UI layer, per the animation doc's mechanism/orchestration split.)

---

## 5. Cross-fork contract

What Fork C **requires**, stated as interfaces (names negotiable; shapes are not):

```csharp
// ── From Fork A (property system, R9) ─────────────────────────────────────────
public enum ValueProvenance { Local, TemplateLocal /*, Style, Trigger, … (Fork A's full ladder) */ }

public interface IUIPropertyRegistry          // reflection-free lookup; the loader's primary member source
{
    UIProperty? Find(Type ownerOrTargetType, string name);          // walks base types
    UIProperty? FindAttached(Type ownerType, string name);          // "Grid.Column"
}
public interface IUIPropertyTarget            // implemented by every styled element
{
    void SetValue(UIProperty property, object? value, ValueProvenance provenance);
    // Must tolerate value == ResourceReference / IBindingExpression sentinels per Fork A/B agreement.
}
// Plus: sets during ISupportInitialize bracketing must not fire per-property invalidation storms.

// ── From Fork B (styling/binding/resources, R1/2/3/8) ────────────────────────
public interface IResourceScope               // one lookup level (a Resources owner)
{
    bool TryGetResource(object key, out object? value);
    IResourceScope? Parent { get; }
}
public interface IResourceHost { ResourceDictionary Resources { get; } }   // detected by the instantiator

public class ResourceDictionary               // must support deferred entries
{
    public void SetDeferred(object key, IDeferredValue value);
    public IList<ResourceDictionary> MergedDictionaries { get; }
    public Uri? Source { get; set; }          // setter triggers loader callback (Fork B holds a loader hook)
}
public interface IDeferredValue { object? Realize(IResourceScope lexicalScope); }   // I provide implementations

public sealed class Binding { /* settable: Path, Mode, Source, ElementName, Converter, … */ }
public static class BindingOperations
{
    public static void Apply(object target, UIProperty property, Binding binding, IServiceProvider services);
    public static void ApplyTemplateBinding(object target, UIProperty property,
                                            object templatedParent, UIProperty sourceProperty);
}
public sealed class ResourceReference(object key);     // DynamicResource currency, consumed by Fork A/B

public class ControlTemplate { public Type? TargetType { get; set; } public ITemplateContent? Content { get; set; } }
public class DataTemplate    { public Type? DataType { get; set; }   public ITemplateContent? Content { get; set; } }
// Fork B calls Content.Build(new TemplateBuildContext { … }) — and is the only caller.
```

What Fork C **provides** to the others: `ITemplateContent` implementations (the only template factory Fork B needs to know); `IDeferredValue` implementations; the `XamlConverters` registry (Fork B uses it for DataTrigger value coercion); line-info-bearing strings for selector parsing; `XamlLoader.LoadComponent` as the code-behind entry; and the generator package as future shared home for binding-path accessor generation.

Assumptions about the widget base (whichever fork owns it): a `Control` hierarchy with parameterless constructors for XAML-creatable types; `ISupportInitialize` opt-in honored; `Resources` exposed via `IResourceHost`; templated controls expose `ApplyTemplate`/`GetTemplateChild` that accept my namescope.

---

## 6. Terminal-specific adaptations

1. **Integer cell geometry converters.** `Thickness`/`Margins` parse as integer cells (`"1"`, `"2,1"`, `"1,0,1,0"`) — no DIPs, no fractional values (CUR2401 on `"0.5"` with a message explaining cells are atomic). `GridLength`: `"Auto"`, `"*"`, `"2*"`, `"12"` (cells). `Rect`/`Size` map to the ushort-backed Rendering types; converters validate ≥ 0 at parse time because the `Rect` ctor throws on negatives.
2. **Color/brush mini-language aligned with the existing stack.** The `Color` converter accepts `#RGB`/`#RRGGBB`(+`AA` via `FromRgba`), named ANSI palette colors (`"Red"`, `"LightCyan"` → `Colors.*` palette entries, *not* RGB web colors — a deliberate terminal-first deviation), `"Palette(123)"`, `"Default"`, `"Transparent"`. The `IBrush` converter reuses **`BrushMarkup`'s existing grammar** (`"linear:#f92672,#66d9ef"`, `"radial:…"`, `"conic:…"`) so rich-text markup, code, and XAML share one brush vocabulary; plain color text yields the cached `Brushes.*` singleton when one exists (allocation discipline).
3. **Pen converter** for borders: `"Heavy"`, `"Double Rounded"`, `"Dashed #888"` → `Pens` presets + `With*` composition. No `BorderThickness` in pixels — `BorderStyle` selects a glyph family, mirroring the Drawing layer's "weight is a glyph family, never thickness" invariant.
4. **No font converters.** There is no FontFamily/FontSize; instead `TextAttributes` flags converter (`"Bold,Italic"`) and FIGlet/`TextSizing` properties on the relevant controls.
5. **Scale flips the build-vs-runtime tradeoff.** Desktop XAML compilers exist because desktop apps ship megabytes of markup. Terminal documents are 1–50 KB; the entire markup of a large app parses in single-digit milliseconds — *under one frame at 50 fps*. This is the quantitative reason the runtime loader is not a compromise here.
6. **Hot reload is the designer.** No XAML previewer can exist meaningfully for a cell grid short of running the app in a terminal; edit-and-reload against a live `TerminalSession` is the best attainable dev loop, and it requires the runtime loader to exist anyway.
7. **Threading fit.** Parse is thread-safe (preload off-thread); instantiation and template builds run on the single render/UI thread, matching the lower stack's "one render thread, nothing thread-safe below" rule. The loader never touches `TerminalSession`, scenes, or buffers — it produces widget trees; the widget layer owns scenes/invalidation.
8. **URIs:** `cursorial://assembly/path` over embedded resources; no `pack://`, no file-probing at runtime (except hot-reload dev mode, which watches the *project* tree, not the deployment).

---

## 7. Costs, risks, phasing

**Effort estimate** (following the repo's phased-design-doc playbook — living design doc, numbered phases, adversarial review on the parser):

| Phase | Scope | Size |
|---|---|---|
| X0 | Node model, parser, xmlns/type/member resolution, diagnostics, markup-extension grammar; no instantiation. Heavy test investment here (parser fuzzing, diagnostic golden tests). | ~2.5 KLOC + tests |
| X1 | Instantiator, converter registry + terminal converters, content/collection/attached syntax, x:Name, `LoadComponent`, `.targets` embedding | ~2 KLOC |
| X2 | Markup extensions end-to-end, resource dictionaries + merged + deferred entries, `StaticResource`/`DynamicResource`/`Binding` application (against Fork A/B stubs) | ~1.5 KLOC |
| X3 | Deferred content, `ITemplateContent`, template namescopes, `TemplateBinding`, lexical scope capture | ~1 KLOC |
| X4 | Generator package: typed fields + `InitializeComponent` + **build-time validation via the same parser** | ~1.5 KLOC |
| X5 | AOT metadata-provider generation; hot reload dev mode; `PreloadAsync` | ~2 KLOC |

**Testability** (a phase-X0 deliverable, not an afterthought): the loader is a pure function from string to object graph — no terminal, no session, no render thread needed. Tests parse strings and assert (a) node-graph shape via an internal test surface (`InternalsVisibleTo`), (b) diagnostics as golden files (code + line + column + message), (c) instantiated trees against fake controls + a fake `IUIPropertyRegistry`, (d) template double-build isolation (distinct instances, shared folded boxes, separate namescopes), (e) the AOT path by running the whole suite twice — once with `ReflectionXamlMetadata`, once with a hand-built `IXamlTypeMetadataProvider` — guaranteeing the generated provider can't drift semantically.

**Perf characteristics:** parse cost per URI once (ms-scale, cacheable, off-thread-able); template build cost ≈ object allocation; zero steady-state/per-frame cost; folded constants shared process-wide. Memory: one node graph per document (~40–60 bytes/element across the arrays — a 600-element document ≈ 30 KB, retained while templates from it are alive).

**Risks & mitigations:**

1. *Reflection under trimming* — the loudest real risk. Mitigation: honest `[RequiresUnreferencedCode]` annotations from day one, a documented feature-switch, and the X5 generated provider as the supported trimmed mode. Until X5, "trimmed/AOT publish" is explicitly unsupported-with-diagnostics, not silently broken.
2. *Semantic drift from WPF/Avalonia expectations* (whitespace, StaticResource-in-template scoping, event restrictions). Mitigation: a "Deviations from WPF" section in the living design doc, each deviation deliberate + tested — the same "resolved decisions" discipline the Drawing doc uses.
3. *Markup-extension parser edge cases* (quoting, escaping, nested braces). Mitigation: grammar is tiny and closed; fuzz + a port of WPF's documented escape cases as a pinned oracle table.
4. *Lexical-scope capture lifetime* — a captured scope chain can pin a window's resources via a long-lived template. Mitigation: capture holds the dictionaries (which the template legitimately depends on), not the controls; weak host references where Fork B's scope type allows.
5. *Cross-fork timing* — X2/X3 need Fork A/B shapes. Mitigation: the §5 contract is small and stub-able; X0/X1 have no dependency at all.

**Punted (recorded, per house style):** `x:TypeArguments` (generic instantiation), `x:Shared="False"`, `x:Array`, `x:Reference`, attached events, `x:FieldModifier`, localization (`x:Uid`), XML external entities (never — security), per-instance designer metadata.

**The compiled path, added later without breaking anyone.** Three pre-built seams make this additive: (1) `ITemplateContent` — a compiler emits classes implementing it directly; Fork B can't tell. (2) `IXamlTypeMetadataProvider` — already the activation/setter indirection. (3) `LoadComponent` — the generator-emitted `InitializeComponent` can switch from "call runtime loader" to "call generated builder" under a project property (`<CursorialXamlCompile>true</CursorialXamlCompile>`) with zero consumer source changes. The compiled producer would be *generated C#* (debuggable, AOT-trivial), built on the X4 generator's existing parse — and the runtime loader remains for hot reload and dynamically loaded markup, exactly as Avalonia ships both. Nothing in v1 must be re-architected; the compiler is an optimization plug-in, not a successor.

---

## 8. Steelman & rebuttal

### Steelman A: compile XAML at build time from day one (source-generated C#/IL, Avalonia-style)

*The case:* Zero runtime parse; errors at build by construction; stepping through generated `Build()` methods in a debugger; trim/AOT-safe with no metadata provider machinery; no reflection anywhere; templates become factory methods — the theoretically optimal end state. Avalonia proved it works and its users love compiled bindings.

*The honest answer:* It is the right *end state* and the wrong *first move*. (1) **Cost:** a XAML-to-C# compiler is several times the loader's size — Avalonia needed XamlX (~30+ KLOC) plus years of hardening; full XAML semantics (ambient resources, namescopes, deferred dictionaries, markup-extension service providers) must be *expressed in generated code*, which is dramatically harder than executing them in a library. (2) **You ship the runtime loader anyway** — hot reload, dynamic markup, and tooling all require it (Avalonia ships both `XamlLoader` and compiled XAML). Two implementations of one semantics is the actual maintenance position of "compile-first," and they *will* drift. (3) **The payoff is missing at terminal scale:** the thing compilation buys — eliminating multi-MB parse on desktop startup — is worth single-digit milliseconds here, under one frame. (4) **Version coupling:** generated construction code bakes the framework's API shape into *consumer* binaries; a Cursorial.UI behavioral fix means consumers must regenerate. Runtime loading keeps semantics in the library. (5) **The DX gap is closable cheaply:** build-time *validation* — the part of compile-first developers actually feel — comes in phase X4 by running my parser in the generator and reporting `XamlDiagnostic`s as Roslyn diagnostics. And my §7 migration path delivers compile-mode later behind existing seams, when profiling — not ideology — demands it. Choosing compile-first now spends the project's largest UI-layer budget item on the layer's least terminal-relevant problem.

### Steelman B: adopt an existing XAML stack (System.Xaml / Portable.Xaml)

*The case:* XAML is a deceptively deep spec — namescopes, ambient properties, deferring, markup-extension services. System.Xaml implements all of it, battle-tested for 15 years; Portable.Xaml runs cross-platform. Hand-rolling risks a decade of rediscovered edge cases. We'd write only a schema context and object writer glue.

*The honest answer:* (1) **Availability:** `System.Xaml` is part of the Windows Desktop runtime — it does not exist on macOS/Linux .NET, which is disqualifying for a cross-platform-first project. (2) **Portable.Xaml** is a community fork with minimal maintenance — adopting it as the foundation of the UI layer's front door violates the repo's zero-dependency stance for the worst possible component (one we cannot fix or evolve). (3) **The "free" part is the cheap part:** with System.Xaml you still write the schema context, type/member adapters into the Fork A property system, deferring implementation (`XamlDeferringLoader`), converters, and the resource/namescope integration — that *is* most of my X0–X3 effort — while inheriting its heavyweight reflection model (hostile to the AOT path), its `XamlObjectWriter` allocation profile (tuned for desktop, indifferent to per-frame discipline), and its diagnostics (no control over messages, frequently missing line info in object-writer errors). (4) The spec-depth fear is managed by scoping: I implement the XAML 2009 subset WPF/Avalonia apps actually use (documented punts in §7), with a fuzzed parser and oracle-pinned escape/whitespace tables — the same "pin against an external oracle" discipline this repo already applies to easings and Unicode tables.

### Steelman C: skip XAML — a C# fluent DSL covers declarative UI

*The case:* C# 12+ collection expressions and object initializers read nearly as well, are type-safe by construction, refactor-safe, AOT-trivial, and need zero loader code.

*The honest answer:* Requirement 7 mandates XAML, so this is a frame challenge — but it fails on its merits too: a DSL cannot express *deferred* content without lambdas (which capture, allocate, and can't be diffed for hot reload), has no story for designer-editable themes shipped as data, and loses the lexical resource scoping that makes styling systems composable. Notably, my design keeps the DSL door open anyway: `ITemplateContent` accepts a `FuncTemplateContent(Func<TemplateBuildContext, object>)` trivially, so code-first and markup-first templates coexist on the same contract.

---

*Design artifacts referenced: `/tmp/cursorial-ui-maps/design-doc.md`, `drawing-core.md`, `rendering-session.md`, `input.md`, `animation.md`; repo conventions from `/Users/mike.strobel/Workspace/Cursorial/CLAUDE.md` and `docs/drawing-layer-design.md`.*