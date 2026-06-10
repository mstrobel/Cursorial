# Fork C Proposal — The Cursorial.UI XAML Pipeline
## Approach: Reuse a spec-compliant XAML object-graph engine (vendored Portable.Xaml), hidden behind Cursorial-owned seams

---

## 1. Executive summary & philosophy

**Verdict up front, with the honesty the assignment demands:**

- **System.Xaml is non-viable.** On modern .NET it ships only in `Microsoft.WindowsDesktop.App` (the WPF desktop pack), is absent from `dotnet/runtime`, and is Windows-only. This is confirmed by Microsoft's own triage of [dotnet/wpf#3187](https://github.com/dotnet/wpf/issues/3187) ("System.Xaml is not available on .NET Core Runtime, only on Desktop Runtime as part of WPF"). A cross-platform terminal library cannot reference it. Eliminated.
- **Portable.Xaml as a NuGet dependency is also non-viable — but for dependency-health reasons, not technical ones.** The package ([Portable.Xaml 0.26.0](https://www.nuget.org/packages/Portable.Xaml/)) last shipped **October 29, 2020** ([releases](https://github.com/cwensley/Portable.Xaml/releases)) — dormant for over five years. Cursorial's production assemblies currently have **zero external NuGet dependencies** (verified: only test projects reference packages); breaking that property for an unmaintained package is indefensible.
- **Portable.Xaml as vendored source is the winning move.** It is [MIT-licensed](https://github.com/cwensley/Portable.Xaml), `netstandard2.0` (compiles cleanly on net10.0), is a mature port of Mono's System.Xaml that the upstream's own benchmarks show **faster than .NET's System.Xaml in most cases**, and Eto.Forms has shipped on it for a decade. We fork it into the repo as internal engine code (`Cursorial.UI.Xaml/Engine/`), re-namespace it, own it, and **never let an engine type leak into the public API** except the deliberately WPF-shaped `MarkupExtension` base.

**Why reuse at all?** Because the cost of XAML is not the XML. The cost is the *semantic long tail*: the markup-extension `{...}` grammar with nesting and escaping; forward references and fix-ups (`x:Reference` to an element declared later); namescope registration rules; **deferred content** (the single hardest feature on the requirements list — templates that capture markup and instantiate per-target, late); ambient property resolution (how `Setter Property="Background"` knows the enclosing `Style.TargetType`); generic instantiation (`x:TypeArguments`); attached-member syntax; type-converter dispatch; and line/column error propagation through all of it. System.Xaml solved each of these with a documented, decade-hardened design — `XamlNodeList`, `XamlDeferringLoader`, `IAmbientProvider`, `INameScope` + `ExternalNameScope`, `IXamlLineInfo` — and Portable.Xaml implements that exact API surface. A hand-rolled "subset" parser re-derives each of these one bug report at a time, and the subset converges on the spec while having none of its tests.

**Philosophy:** the engine is plumbing; *the glue is the product*. Roughly 80% of the work in this proposal (schema context, converters, markup extensions, template plumbing, source generator, MSBuild experience) is identical under any parser. Reusing the engine means that work starts in week 1 instead of month 4 — and the node-stream architecture gives us the build-time and AOT escape hatches for free later.

---

## 2. Public API sketch

All public types live in `Cursorial.UI.Xaml` (new assembly, references `Cursorial.UI` + the existing stack). The vendored engine is internal. XAML is **optional** — `Cursorial.UI` never references `Cursorial.UI.Xaml`; apps can build trees in pure C#.

### 2.1 Loader facade

```csharp
namespace Cursorial.UI.Xaml;

/// <summary>Entry point for loading Cursorial XAML (.cxaml) into widget trees.</summary>
public static class CursorialXaml
{
    public static object Load(Stream stream, XamlLoadOptions? options = null);
    public static T Load<T>(Stream stream, XamlLoadOptions? options = null);
    public static T Parse<T>(string xaml, XamlLoadOptions? options = null);          // tests, tooling
    public static T LoadEmbedded<T>(Assembly assembly, string logicalPath,
                                    XamlLoadOptions? options = null);

    /// <summary>x:Class path: loads markup INTO an existing root instance.
    /// Called by generated InitializeComponent().</summary>
    public static void LoadComponent(object root, Uri componentUri,
                                     XamlLoadOptions? options = null);
}

public sealed record XamlLoadOptions
{
    public XamlSchema Schema { get; init; } = XamlSchema.Shared;
    public Uri? BaseUri { get; init; }                  // resolves ResourceDictionary.Source etc.
    public IServiceProvider? HostServices { get; init; } // app-level services visible to MEs
    public bool AllowReflectionFallback { get; init; } = true;  // clr-namespace: Type.GetType — off under trimming
}

public sealed class XamlLoadException : Exception
{
    public string? SourceUri { get; }
    public int? Line { get; }
    public int? Column { get; }
    // Message format: "MainWindow.cxaml(42,17): Cannot convert 'Centre' to TitlePosition. Did you mean 'Center'?"
}
```

### 2.2 Schema registration (the AOT-facing surface)

```csharp
/// <summary>Type universe for XAML resolution. Thread-safe; one shared instance per app is typical.
/// Wraps the internal engine schema context — engine types never appear here.</summary>
public sealed class XamlSchema
{
    public static XamlSchema Shared { get; }

    /// <summary>Scans the assembly for [XmlnsDefinition]. The source generator emits a
    /// module initializer calling this, so consumers rarely call it by hand.</summary>
    public void RegisterAssembly(Assembly assembly);

    public void RegisterType<[DynamicallyAccessedMembers(
        DynamicallyAccessedMemberTypes.PublicConstructors |
        DynamicallyAccessedMemberTypes.PublicProperties |
        DynamicallyAccessedMemberTypes.PublicMethods)] T>(string xmlNamespace, string? elementName = null);

    public void RegisterTypeConverter(Type targetType, IXamlTypeConverter converter);
    public void RegisterMemberConverter(Type ownerType, string memberName, IXamlTypeConverter converter);
}

/// <summary>String → value conversion. Registry-based (no [TypeConverter] attributes on
/// Core/Drawing types — lower layers stay untouched).</summary>
public interface IXamlTypeConverter
{
    object? ConvertFromString(string value, IXamlServiceContext context);
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class XmlnsDefinitionAttribute(string xmlNamespace, string clrNamespace) : Attribute
{
    public string XmlNamespace { get; } = xmlNamespace;
    public string ClrNamespace { get; } = clrNamespace;
}

[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class ContentPropertyAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
```

### 2.3 Markup extensions

The base class keeps WPF's exact shape (muscle memory; trivially portable docs/snippets). It *is* the vendored engine's `MarkupExtension`, re-namespaced into our public surface — that's the one engine type we expose, deliberately.

```csharp
public abstract class MarkupExtension
{
    public abstract object? ProvideValue(IServiceProvider serviceProvider);
}

/// <summary>Typed view over the engine's service provider. Obtained via extension method.</summary>
public interface IXamlServiceContext
{
    object? TargetObject { get; }            // IProvideValueTarget
    object? TargetMember { get; }            // UIProperty, or PropertyInfo for POCO members
    object? RootObject { get; }              // IRootObjectProvider — the x:Class instance
    Uri? BaseUri { get; }
    Type ResolveType(string qualifiedName);                  // IXamlTypeResolver — "ui:Button" → Type
    object? ResolveName(string name);                        // IXamlNameResolver (forward refs OK)
    IEnumerable<object> GetAmbientValues(Type ownerType, string propertyName);  // IAmbientProvider
    T? GetHostService<T>() where T : class;
}

public static class XamlServiceContextExtensions
{
    public static IXamlServiceContext GetXamlContext(this IServiceProvider provider);
}
```

The seven required extensions (`x:Null`, `x:Static`, `x:Type` — plus `x:Reference`, `x:Array` — **come free from the engine's `XamlLanguage` built-ins**; we write only the framework four):

```csharp
public sealed class StaticResourceExtension(object resourceKey) : MarkupExtension
{
    public object ResourceKey { get; set; } = resourceKey;
    // ProvideValue: walks ambient Resources properties (in-progress tree) via
    // GetAmbientValues(typeof(Widget), "Resources") outermost-in, then app resources
    // via GetHostService<IResourceHost>(). Throws XamlLoadException with line info on miss.
    public override object? ProvideValue(IServiceProvider serviceProvider);
}

public sealed class DynamicResourceExtension(object resourceKey) : MarkupExtension
{
    // Returns a Fork-B IDeferredValue (resource expression). The loader's set-interceptor
    // routes it to expression attachment instead of a literal SetValue. Valid only on
    // UIProperty targets; throws (with line info) on plain CLR members.
    public override object? ProvideValue(IServiceProvider serviceProvider);
}

public sealed class TemplateBindingExtension : MarkupExtension
{
    public TemplateBindingExtension() { }
    public TemplateBindingExtension(string propertyName);
    public string? Property { get; set; }
    // Resolves against ambient ControlTemplate.TargetType; legal only during template
    // instantiation (the TemplateContent replay supplies the templated parent).
    public override object? ProvideValue(IServiceProvider serviceProvider);
}

public sealed class BindingExtension : MarkupExtension
{
    public BindingExtension() { }
    public BindingExtension(string path);
    public string? Path { get; set; }
    public BindingMode Mode { get; set; }
    public object? Converter { get; set; }
    public object? ConverterParameter { get; set; }
    public string? ElementName { get; set; }
    public object? Source { get; set; }
    public object? FallbackValue { get; set; }
    public string? StringFormat { get; set; }
    // Builds a Fork-A BindingBase descriptor. If the target member's type IS BindingBase
    // (e.g. Setter.Value in a style targeting a binding), returns the descriptor itself;
    // otherwise returns it wrapped as IDeferredValue for expression attachment.
    public override object? ProvideValue(IServiceProvider serviceProvider);
}
```

### 2.4 Deferred content (templates)

```csharp
/// <summary>Captured markup: an engine node list + the schema/namespace snapshot needed to
/// replay it. Created by the engine's deferring loader when it hits a TemplateContent-typed
/// member; never parsed from XML again.</summary>
public sealed class TemplateContent
{
    // internal: XamlNodeList Nodes; NamespaceDeclaration[] InScopeNamespaces;
    //           XamlSchema Schema; string? SourceUri; (line info rides the nodes)

    public object Instantiate(in TemplateInstantiationContext context);
}

public readonly record struct TemplateInstantiationContext
{
    public required object TemplatedParent { get; init; }   // TemplateBinding target
    public required INameScope NameScope { get; init; }     // fresh per stamp (Fork B supplies INameScope)
    public IResourceHost? ResourceContext { get; init; }    // StaticResource lookup chain
    public object? DataContext { get; init; }               // DataTemplate stamping
}
```

Fork B's `ControlTemplate`/`DataTemplate` then look like:

```csharp
// Fork B type; shown to pin the contract.
public class ControlTemplate
{
    public Type? TargetType { get; set; }
    [DeferredContent] public TemplateContent? Content { get; set; }   // set by XAML, deferred
}
```

(The `[DeferredContent]` attribute is advisory — deferral is actually keyed on the member type `TemplateContent` in the schema context, so Fork B needs no reference to engine types and no attribute at all if it prefers.)

### 2.5 Names & code-behind

```csharp
public static class NameScopeExtensions
{
    public static T? FindControl<T>(this INameScope scope, string name) where T : class;
    public static T GetRequiredControl<T>(this INameScope scope, string name) where T : class;
}
```

Both stories are supported: **`FindControl`** always works (no generator required); the **source generator** additionally emits typed fields for `x:Name` elements when `x:Class` is present (§3.6).

### 2.6 Consumer experience — a realistic app

`MainWindow.cxaml`:

```xml
<Window xmlns="https://cursorial.dev/ui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        x:Class="DemoApp.MainWindow"
        Title="Files">
  <Window.Resources>
    <ResourceDictionary>
      <ResourceDictionary.MergedDictionaries>
        <ResourceDictionary Source="Themes/Dark.cxaml"/>
      </ResourceDictionary.MergedDictionaries>
      <SolidColorBrush x:Key="AccentBrush" Color="#66d9ef"/>
      <Style x:Key="PrimaryButton" TargetType="Button">
        <Setter Property="Background" Value="{DynamicResource AccentBrush}"/>
        <Setter Property="Padding" Value="2,0"/>
        <Setter Property="Template">
          <Setter.Value>
            <ControlTemplate TargetType="Button">
              <Border BorderPen="Heavy" Background="{TemplateBinding Background}">
                <ContentPresenter Content="{TemplateBinding Content}"/>
              </Border>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Style>
    </ResourceDictionary>
  </Window.Resources>

  <Grid RowDefinitions="Auto,*,Auto" ColumnDefinitions="*,Auto">
    <TextBlock Text="{Binding Title}" Foreground="linear:#f92672,#66d9ef"/>
    <ListBox x:Name="FileList" Grid.Row="1" Grid.ColumnSpan="2"
             ItemsSource="{Binding Files}">
      <ListBox.ItemTemplate>
        <DataTemplate>
          <TextBlock Text="{Binding Name}"/>
        </DataTemplate>
      </ListBox.ItemTemplate>
    </ListBox>
    <Button x:Name="SaveButton" Grid.Row="2" Grid.Column="1"
            Style="{StaticResource PrimaryButton}"
            Content="_Save" Click="OnSaveClicked"/>
  </Grid>
</Window>
```

Code-behind (`MainWindow.cxaml.cs`):

```csharp
namespace DemoApp;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();                 // generated
        DataContext = new MainViewModel();
        FileList.SelectionChanged += OnSelectionChanged;   // generated typed field
    }

    private void OnSaveClicked(object? sender, ClickEventArgs e) { /* … */ }
}
```

Generated partial (by `Cursorial.UI.Xaml.Generators`):

```csharp
partial class MainWindow
{
    internal ListBox FileList = null!;
    internal Button SaveButton = null!;

    public void InitializeComponent()
    {
        CursorialXaml.LoadComponent(this, new Uri("embedded://DemoApp/MainWindow.cxaml"));
        var scope = (INameScope)this;
        FileList   = scope.GetRequiredControl<ListBox>("FileList");
        SaveButton = scope.GetRequiredControl<Button>("SaveButton");
    }
}
```

`.csproj` — one reference; props/targets do the rest:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Cursorial.UI.Xaml\Cursorial.UI.Xaml.csproj" />
  </ItemGroup>
  <!-- Cursorial.UI.Xaml.props auto-includes: <CursorialXaml Include="**\*.cxaml" />
       Targets turn each item into:
         <EmbeddedResource LogicalName="$(RootNamespace)/%(RecursiveDir)%(Filename).cxaml" />
         <AdditionalFiles … />   (feeds the source generator)  -->
</Project>
```

**Processing model, stated explicitly: BOTH build time and runtime.** At **build time**: files are embedded as resources (no semantic compilation in v1), and the incremental source generator emits (a) `x:Class` partials with typed fields + `InitializeComponent`, (b) the trim-safety type manifest (§3.7), (c) compile-time *diagnostics* for malformed XML, unknown `x:` directives, and unresolvable element names. At **runtime**: the vendored engine parses and instantiates. Phase 5 optionally moves *parsing* to build time (binary node-stream pre-bake) and Phase 6 optionally moves *instantiation* to build time (generated factories) — the node-stream seam supports both without API change (§7). File extension is **`.cxaml`** (mirroring Avalonia's `.axaml` rationale: keeps the VS WPF designer from claiming the file) with plain `.xaml` also accepted.

---

## 3. Internal architecture

### 3.1 Assembly layout

```
Cursorial.UI.Xaml/                      net10.0; refs Cursorial.UI (+ transitively Drawing/Rendering/Core/Animation)
  Engine/**                             VENDORED Portable.Xaml (~30–45 kLOC est.), re-namespaced
                                        Cursorial.Xaml.Engine.*, #nullable disable, MIT headers retained,
                                        THIRD-PARTY-NOTICES.md at repo root. Everything internal except
                                        MarkupExtension (re-homed to Cursorial.UI.Xaml, public).
  Schema/                               CursorialSchemaContext, CursorialXamlType, CursorialXamlMember,
                                        XamlSchema facade, xmlns registry
  Converters/                           Thickness/Color/Brush/GridLength/Pen/Easing/… converters
  MarkupExtensions/                     Binding/StaticResource/DynamicResource/TemplateBinding
  Templates/                            TemplateContentLoader, TemplateContent, namescope plumbing
  Loader/                               CursorialXaml facade, transform loop, set-interceptor
Cursorial.UI.Xaml.Generators/           netstandard2.0 Roslyn incremental generator
Cursorial.UI.Xaml.Build/                .props/.targets (packaged with the NuGet, or imported via ProjectReference)
Cursorial.UI.Xaml.Tests/                xUnit; + a Windows-only CI leg running conformance tests against
                                        real System.Xaml as the behavioral oracle (repo convention: oracle-pinned)
```

The vendored engine preserves System.Xaml's public API *shape* (that is its whole value — the API is documented on MSDN and its semantics are testable against the genuine article on Windows CI). We compile it as `internal` via a build-step that strips `public` (one `InternalsVisibleTo` for tests), except `MarkupExtension`.

### 3.2 The load pipeline

```
Stream/string
  → XmlReader (System.Xml, DTD off, async off)
  → Engine.XamlXmlReader(xmlReader, CursorialSchemaContext, settings { ProvideLineInfo = true })
      produces the node stream: NamespaceDeclaration | StartObject | GetObject |
                                StartMember | Value | EndMember | EndObject
  → transform loop (ours, ~40 lines): forwards nodes + IXamlLineInfo → IXamlLineInfoConsumer,
      wraps engine exceptions into XamlLoadException(sourceUri, line, col)
  → Engine.XamlObjectWriter(context, settings {
        RootObjectInstance   = root,            // LoadComponent path only
        ExternalNameScope    = documentScope,   // root widget's INameScope, adapted
        RegisterNamesOnExternalNamescope = true,
        XamlSetValueHandler  = SetValueInterceptor,   // THE property-system seam (§3.4)
    })
  → writer.Result  (the widget tree)
```

No reflection-based assembly scanning happens during load: type resolution hits the xmlns registry (§3.3). The schema context is shared and thread-safe (engine contract); loads may run on any thread — the tree is handed to the render thread on attach, consistent with the one-render-thread rule.

### 3.3 Type resolution

`CursorialSchemaContext : Engine.XamlSchemaContext` overrides `GetXamlType(XamlTypeName)`:

1. **Registry first.** A `ConcurrentDictionary<(string xmlns, string name), Type>` populated from `[XmlnsDefinition]` of explicitly registered assemblies (the generator emits a `[ModuleInitializer]` per consumer assembly that calls `XamlSchema.Shared.RegisterAssembly(...)` — deterministic, no AppDomain sweeps). `https://cursorial.dev/ui` maps to `Cursorial.UI`, `Cursorial.UI.Controls`, `Cursorial.Drawing` (brush/pen types usable as elements), `Cursorial.Animation`.
2. **`clr-namespace:Ns;assembly=Asm`** parsed per spec; resolved through registered assemblies, then `Type.GetType` only when `AllowReflectionFallback` (the one trim-unsafe path, off in trimmed builds).
3. **xmlns:x** stays `http://schemas.microsoft.com/winfx/2006/xaml` — the engine hardwires `XamlLanguage` to it, and every XAML editor on earth expects it. We get `x:Name`, `x:Key`, `x:Class`, `x:TypeArguments`, `x:FactoryMethod`, `x:Arguments`, `x:Shared`, `{x:Null}`, `{x:Static}`, `{x:Type}`, `{x:Reference}`, `{x:Array}` from the engine.

Per-`Type` metadata is a cached `CursorialXamlType : Engine.XamlType` overriding:

- `LookupContentProperty` → our `ContentPropertyAttribute` (cached).
- `LookupTypeConverter` → the **converter registry**, *not* `System.ComponentModel` attributes — Core/Drawing types (`Color`, `Rect`, `Pen`, `Thickness`…) stay attribute-free; the lower layers need zero changes (their "additive changes only via opaque seams" rule is satisfied with *no* changes at all).
- `LookupIsAmbient` → true for members registered ambient (`Widget.Resources`, `Style.TargetType`, `ControlTemplate.TargetType`).
- `LookupDeferringLoader` (type- and member-level) → `TemplateContentLoader` for `TemplateContent`-typed members.
- `LookupAllAttachableMembers` / `GetAttachableMember` → Fork A's `UIPropertyRegistry.Find(ownerType, name)` for attached properties (`Grid.Row="2"` resolves to the `UIProperty`, not to a `GetRow/SetRow` reflection pair — though plain CLR attached patterns also work via the engine default).

### 3.4 Property setting — the interceptor

The engine's `XamlObjectWriterSettings.XamlSetValueHandler` fires for **every** member assignment with `(instance, member, value)` and lets us claim or decline the set. Our interceptor (ordered):

1. **Deferred values.** `value is IDeferredValue dv` (returned by `{Binding}`/`{DynamicResource}`) → `dv.AttachTo(instance, member.UIProperty)` (Fork A/B contract, §5). Claimed.
2. **UIProperty members.** Member maps to a Fork A `UIProperty` → `((IUiObject)instance).SetValue(prop, value, ValueSource.Local)` — or `ValueSource.TemplatedParent` when an *instantiation ambient* says we're inside a template replay (a `[ThreadStatic]` scope pushed by `TemplateContent.Instantiate`; mirrors WPF's template-value priority). Claimed. **This is also the perf fast path: no `PropertyInfo.SetValue` for framework properties.**
3. **Events.** Member is an event and value is a method-name string → `Delegate.CreateDelegate(handlerType, rootObject, name)` against the `x:Class` root (kept alive by the generator's manifest under trimming). Claimed.
4. Everything else (POCO properties on view-models, converter classes, etc.) → decline; engine does standard reflection assignment.

Collections and dictionaries (`Children`, `Resources`, `Setters`, `MergedDictionaries`) are engine-native: `IList`/`IDictionary`/`ICollection<T>` adds, `x:Key` extraction, `GetObject` for read-only collection members — zero code from us.

**Type conversion order** is engine-standard: member converter → type converter → markup extension `ProvideValue` → assignable check, all funneling through the registry. Enum parsing (incl. `[Flags]` comma syntax) is engine-native.

### 3.5 Deferred content — the template mechanism, in full

**Capture.** When the object writer begins `ControlTemplate.Content` (member type `TemplateContent`), it consults `LookupDeferringLoader` and hands the *unparsed-into-objects* node sub-stream to:

```csharp
internal sealed class TemplateContentLoader : Engine.XamlDeferringLoader
{
    public override object Load(Engine.XamlReader deferred, IServiceProvider sp)
    {
        var nodes = new Engine.XamlNodeList(deferred.SchemaContext);
        Engine.XamlServices.Transform(deferred, nodes.Writer, closeWriter: true); // O(nodes) copy, no objects built
        var nsResolver = (Engine.IXamlNamespaceResolver)sp.GetService(typeof(Engine.IXamlNamespaceResolver))!;
        return new TemplateContent(nodes, nsResolver.GetNamespacePrefixes().ToArray(),
                                   schema, baseUri);   // line info rides the node list
    }
    public override Engine.XamlReader Save(object value, IServiceProvider sp)
        => throw new NotSupportedException();          // no save path in v1
}
```

So the answer to "stored node graph? factory delegates? re-parse?" is: **stored node list** — parsed once, replayed per stamp, never re-parsed, with line info preserved (template errors report the original `.cxaml` file and line, something factory-delegate designs lose).

**Instantiation.** `TemplateContent.Instantiate(ctx)`:

1. Push the template ambient scope (`TemplatedParent`, `ResourceContext`, `ValueSource.TemplatedParent` for the interceptor).
2. New `XamlObjectWriter` over the same schema, with `ExternalNameScope = ctx.NameScope`, `RegisterNamesOnExternalNamescope = true` — **template namescope isolation**: `x:Name="PART_Border"` registers into the per-stamp scope, never the document scope. Two stamped buttons each have their own `PART_Border`; `GetTemplateChild` on the templated parent consults the stamp's scope. Document loads, by contrast, pass the root widget's scope — that's the **two-namescope story** in one mechanism, which the engine already has.
3. Replay: prefix declarations first, then `XamlServices.Transform(nodes.GetReader(), writer)`.
4. `{TemplateBinding}` / `{Binding}` / `{StaticResource}` MEs execute during replay against the pushed ambients; `{StaticResource}` resolves template → templated parent → logical chain → app.
5. Return root; Fork B wires it into the visual tree.

**Cost model** (estimates, pinned by a Phase-0 benchmark): node replay is reflection-bound at roughly **1–5 µs/node**; a realistic 30-node control template ≈ 50–150 µs per stamp; stamping 200 controls ≈ 10–30 ms, **one-time at view construction, never per-frame**. At the stated terminal scale (hundreds of elements, not tens of thousands) this is comfortably inside an interactive budget; Portable.Xaml's own benchmarks beating System.Xaml support the order of magnitude. If a profiled hot spot emerges (virtualized lists stamping during scroll), the bounded fix is a per-template factory cache compiled on Nth instantiation — but it is *not* in v1, and virtualized item recycling (Fork B) attacks the same problem at the right layer.

### 3.6 Resource dictionaries, merged dictionaries, themes

- `ResourceDictionary` is Fork B's type; the engine handles `x:Key` insertion and the `MergedDictionaries` collection with no XAML-layer code.
- `Source="Themes/Dark.cxaml"` — a `XamlUriConverter` resolves relative URIs against `BaseUri` (flowed by the loader; `embedded://Assembly/Path` scheme), loads the target dictionary via `CursorialXaml.LoadEmbedded<ResourceDictionary>`, and caches by absolute URI (`x:Shared`-style instance sharing punt: **v1 caches the dictionary object; per-entry `x:Shared="false"` is deferred**).
- Separate-file themes are just dictionary roots; runtime theme swap = replace a merged dictionary and let Fork B's `DynamicResource` invalidation propagate. The XAML layer's only obligation is that `{DynamicResource}` produced an expression, not a value (§2.3).
- **v1 instantiates dictionary entries eagerly.** WPF-style per-key lazy instantiation (BAML key records) is a documented Phase-5+ follow-up: the node-stream architecture supports slicing a dictionary body into per-entry `XamlNodeList`s behind a deferring loader on `ResourceDictionary` itself — same mechanism as templates, just keyed. Punted because terminal-scale theme dictionaries (hundreds of entries, not WPF's tens of thousands) don't justify it yet.

### 3.7 Source generator & trimming/AOT stance

The incremental generator (`.cxaml` as `AdditionalFiles`) does three jobs:

1. **`x:Class` partials** — typed fields per `x:Name`, `InitializeComponent()`. Element-name → CLR-type resolution uses the *Roslyn compilation's* symbol table against the same `[XmlnsDefinition]` metadata (no Portable.Xaml inside the generator; a lightweight `System.Xml` scan suffices because fields need only element name + xmlns).
2. **Trim manifest** — a `[ModuleInitializer]` that registers every element type, attached property owner, markup-extension type, converter, and event-handler method referenced by the assembly's XAML into `XamlSchema.Shared`, through the `[DynamicallyAccessedMembers]`-annotated `RegisterType<T>` API. **This makes the reflective engine trim-safe by construction: the generator computes the closed world; the annotations root it.**
3. **Build diagnostics** — malformed XML, unknown element names, duplicate `x:Name`, `x:Class` mismatch surface as compiler errors with file/line *before* runtime.

**Honest AOT inventory.** Where the engine reflects at runtime: type lookup (registry — safe), instance creation (`Activator.CreateInstance` over rooted ctors — works under trimming *and* NativeAOT when rooted), property sets (mostly bypassed by the interceptor's `UIProperty` fast path; POCO fallback is `PropertyInfo.SetValue` over rooted members — works), converter dispatch (registry — safe), `Delegate.CreateDelegate` for events (rooted — works). **Two genuine risks**, called out as Phase-0 spike gates rather than hand-waved: (a) Portable.Xaml's invoker layer may use `Expression.Compile` for speed — on NativeAOT that silently falls back to the interpreter (functional, slower); if present we replace those paths in our fork with direct invocation; (b) `XamlTypeName`/generic `x:TypeArguments` construction uses `MakeGenericType` — fine when the closed constructed types appear in the manifest, and the generator can enumerate them from markup. **Stance: trimming-compatible in v1 via the manifest; NativeAOT-functional in v1 with a perf tax; NativeAOT-fast via the Phase-6 compiled backend** (generated factories consuming the same node streams at build time — the engine's reader becomes the front end of a compiler, which is exactly how WPF (BAML) and Avalonia (XamlX) evolved).

### 3.8 Error reporting

`XamlXmlReader` implements `IXamlLineInfo`; `XamlObjectWriter` implements `IXamlLineInfoConsumer`; our transform loop wires them, so **every** engine exception — parse error, unknown type, converter failure, ME failure, deferred-template replay failure — carries line/column, which we wrap into `XamlLoadException` with the source URI. Converter misses add did-you-mean suggestions (Levenshtein over enum names/registry keys — cheap, huge DX). Template instantiation errors report the *template's* original file/line (node lists retain line info). The generator catches the static subset at compile time.

### 3.9 Testability

- The loader is a **pure function** `(string, XamlSchema) → object tree` — no terminal, no session, no thread affinity. Tests parse snippets and assert tree shape, property values, namescope contents, and exception line numbers.
- `TemplateContent.Instantiate` is equally pure given a fake `TemplatedParent` — template semantics (namescope isolation, TemplateBinding resolution, per-stamp independence) get direct unit tests.
- **Oracle pinning** (repo convention): a Windows-only CI leg runs the same node-stream conformance corpus against genuine `System.Xaml` and diffs node sequences — the vendored engine's spec fidelity is *measured*, not assumed.
- The generator gets snapshot tests (Roslyn `GeneratorDriver`), and `Parse<T>` keeps app-level UI tests headless.

---

## 4. Requirement satisfaction

| # | Requirement | How this design serves it |
|---|---|---|
| 1 | Styling & templating | `Style`/`Setter`/`ControlTemplate`/`DataTemplate` (Fork B types) are plain XAML elements; `Setter.Property="Background"` resolves via ambient `TargetType` (`IAmbientProvider` — engine-native); templates get true WPF-grade deferral (§3.5). |
| 2 | Data binding | `{Binding}` constructs Fork A descriptors with full ME grammar (nested MEs, `Converter={StaticResource …}`) parsed by the engine; expression attachment via the set-interceptor. |
| 3 | Resource/style inheritance | `StaticResource` walks ambient in-progress `Resources` then the host chain; `DynamicResource` defers to Fork B's invalidation; merged dictionaries + `Source` themes (§3.6). |
| 4 | Logical/physical focus | Not a markup concern; XAML sets `Focusable`, `TabIndex`, `FocusManager.*` attached properties — attached syntax fully supported. |
| 5 | Modal/modeless windows | `Window`/`Dialog` are ordinary roots; `LoadEmbedded<Dialog>(…)` per instance; nothing window-specific needed in the pipeline. |
| 6 | Access keys | `Content="_Save"` flows as a plain string to Fork B's `AccessText`; XAML guarantees no underscore mangling (no converter touches plain strings). The Alt-toggle behavior is Fork B + input-capability logic, not markup. |
| 7 | XAML markup + template plumbing | The whole proposal; deferral mechanism specified to the node level. |
| 8 | Setters + Triggers/selectors | Triggers are element syntax for free; if Fork B chooses Avalonia-style selectors, a `Selector="Button.primary:focus"` string converter is one registry entry — the pipeline is agnostic. |
| 9 | DependencyProperty-style properties | All framework property sets route through `IUiObject.SetValue(prop, value, ValueSource.Local/TemplatedParent)` — correct priority semantics and no reflection on the hot path (§3.4). |
| 10 | Rich animation | Animation/storyboard types (Fork B orchestration over `Cursorial.Animation`) are XAML elements; converters for `Duration` ("0:0:0.3"), `Easing` ("CubicOut" → `Easings` catalog), `Color`/`Brush`/`Thickness` interpolation targets. |

---

## 5. Cross-fork contract

What Fork C **requires**, stated as interfaces (names negotiable, shapes are not):

```csharp
// ── From the property-system fork (Fork A) ─────────────────────────────
public abstract class UIProperty
{
    public string Name { get; }
    public Type OwnerType { get; }
    public Type PropertyType { get; }
    public bool IsAttached { get; }
}
public static class UIPropertyRegistry
{
    // Lookup by (owner CLR type, member name); walks base types; includes attached
    // properties registered against any owner. MUST be O(1)-ish: called per member set.
    public static UIProperty? Find(Type ownerType, string name);
}
public interface IUiObject
{
    void SetValue(UIProperty property, object? value, ValueSource source);
    object? GetValue(UIProperty property);
}
public enum ValueSource { /* must include at least: */ Local, TemplatedParent, Style /* … */ }

// Binding descriptors: plain objects constructible with init-style setters, no services
// needed at construction (BindingExtension builds them parser-side).
public abstract class BindingBase { /* Path, Mode, Converter, Source, ElementName, … */ }

// ── From the styling/visual-tree fork (Fork B) ─────────────────────────
public interface INameScope
{
    void Register(string name, object element);   // duplicate name MUST throw (XAML relies on it)
    object? Find(string name);
}
// Root widgets (Window, UserControl) implement INameScope, or expose one the loader can adopt.

public interface IResourceHost
{
    bool TryFindResource(object key, out object? value);
    IResourceHost? ResourceParent { get; }
}
// Widgets expose: ResourceDictionary Resources { get; }  — registered ambient by Fork C.

public sealed class ResourceDictionary : IDictionary<object, object?>
{
    public IList<ResourceDictionary> MergedDictionaries { get; }
    public Uri? Source { get; set; }     // setter triggers load via a resolver delegate Fork C installs
}

// Deferred-value attachment — the seam {Binding}/{DynamicResource} land on:
public interface IDeferredValue
{
    void AttachTo(IUiObject target, UIProperty property);
}
// Fork A's binding expressions and Fork B's dynamic-resource expressions both implement it.

public class ControlTemplate { public Type? TargetType { get; set; } public TemplateContent? Content { get; set; } }
public class DataTemplate    { public Type? DataType { get; set; }  public TemplateContent? Content { get; set; } }
// Fork B calls TemplateContent.Instantiate(ctx) when applying a template; Fork C never
// decides WHEN to stamp — only HOW. GetTemplateChild = ctx.NameScope.Find.

public class Style { /* Type TargetType (must be settable pre-Setters: registered ambient); IList<Setter> Setters (content property) */ }
public sealed class Setter { public UIProperty? Property { get; set; } public object? Value { get; set; } }
// Fork C supplies the UIPropertyConverter that turns "Background" / "Grid.Row" into a
// UIProperty using ambient TargetType — Fork B just declares the property as UIProperty-typed.
```

What Fork C **provides** to A and B: declarative construction of every public type they ship (provided types have public parameterless ctors or registered factories); deferral for anything `TemplateContent`-typed; line-info-bearing errors; the converter registry (they register converters for their value types at module init); and the guarantee that **no engine type appears in their compile-time surface** — A and B never reference `Cursorial.UI.Xaml`.

Dependency direction: `Cursorial.UI.Xaml → Cursorial.UI → Drawing → {Rendering, Animation} → Core`. XAML stays optional and the lower layers stay untouched — this proposal requires **zero changes** to Core/Rendering/Drawing/Animation.

---

## 6. Terminal-specific adaptations

Where we deliberately deviate from WPF/Avalonia because this is a cell grid:

1. **Integer geometry converters.** `Thickness`, `Margins`-style values, `Size`, and `GridLength` parse **integers** (cells), not DIPs — `Padding="2,0"`, never `"2.5"`. `GridLength`: `Auto`, `*`, `2*`, `12`. Fractional input is a converter *error with line info*, not a silent round — sub-cell layout is inexpressible and pretending otherwise hides bugs (`Rect` is ushort-backed and throws on negatives; converters validate ≥ 0 at parse time so load-time errors beat render-time throws).
2. **`Color` converter speaks the terminal color model**, not sRGB-only: `Default` (the terminal's own default — `ColorKind.Default`, a concept WPF lacks), named ANSI palette (`Red`, `LightCyan` = `Colors.*`), `Palette123`, `#RGB`/`#RRGGBB`, and `#RRGGBBAA` — alpha is meaningful because the Drawing compositor consumes it (scrims, shadows), unlike terminal SGR.
3. **One brush mini-language across the stack.** `BrushConverter` reuses the exact `linear:|radial:|conic:` grammar of `BrushMarkup.Resolver` (`Foreground="linear:#f92672,#66d9ef"`), so text markup `[brush=…]` and XAML attributes are the same dialect — one thing to learn, one parser to maintain (we call into Drawing's resolver).
4. **`Pen` converter** for the stroke vocabulary: `BorderPen="Heavy Rounded #f92672"` → `Pens.Heavy.WithCorners(Rounded).WithColor(…)` — weight is a glyph family, never thickness, so there is deliberately **no** numeric stroke-width syntax.
5. **No render transforms in markup.** `RenderTransform`/`LayoutTransform` don't exist; the only composite-time degrees of freedom are integer offset, opacity, clip, blend mode (`CompositeParameters`) — exposed as plain widget properties, not a transform object model.
6. **`GlyphSet`/ASCII degradation as a theme resource**, not per-element hardcoding: themes set `{DynamicResource ChromeGlyphSet}` so an app can swap Unicode → ASCII chrome wholesale (the Drawing layer is capability-blind by design; the theme layer owns that policy).
7. **No URI pack scheme, no DPI, no Freezables, no x:Uid** in v1. `embedded://` + relative paths only; localization extraction is a later concern.
8. **Capability-conditional markup is punted** (e.g. `cap:If="Sixel"`). Capability adaptation belongs in Fork B triggers/selectors bound to `TerminalCapabilities` — markup stays declarative and capability-blind, mirroring Drawing's stance.

---

## 7. Costs, risks, phasing

### Effort (one experienced engineer; estimates, not promises)

| Phase | Scope | Est. |
|---|---|---|
| **0 — Spike (go/no-go)** | Vendor + compile engine on net10.0; round-trip: load → deferring loader → node-list replay; ambient lookup; event wiring; `ExternalNameScope`; line-info fidelity; NativeAOT smoke test; micro-bench node replay. **Kill criteria:** deferral or ambient materially broken in Mono lineage *and* unfixable in < 2 wks; replay > 50 µs/node. | 1–2 wk |
| 1 | Schema context, xmlns registry, converters, `CursorialXaml` facade, x: intrinsics, namescopes, `FindControl`, `XamlLoadException` | 2–3 wk |
| 2 | Four framework MEs, set-interceptor → Fork A, resource dictionaries + merged/`Source` | 2 wk |
| 3 | `TemplateContent` + loader + template namescopes + `Setter.Property` ambient converter | 2–3 wk |
| 4 | Source generator (partials, manifest, diagnostics) + MSBuild props/targets | 2 wk |
| 5+ (deferred) | Binary node-stream pre-bake (startup), per-entry lazy dictionaries, hot-reload file watcher (dev loop: the interpretive engine makes this nearly free), compiled-factory backend for NativeAOT-fast | — |

**Total to feature-complete v1: ~9–12 weeks.** A hand-rolled spec-meaningful equivalent (XML→nodes, MEL grammar with nesting/escapes, object writer with forward refs + fix-ups, ambient, two-tier namescopes, deferral, line-info plumbing, `x:` directives, generics) is realistically 15–25 kLOC and 4–6 months *to reach the same semantics with none of the accumulated test surface* — and Avalonia's history is the cautionary tale in both directions: it **shipped on Portable.Xaml for years** before outgrowing it, at desktop scale (orders of magnitude more elements and style rules than a terminal app).

### Performance characteristics

- Load: parse + instantiate a ~300-element view in single-digit ms (engine benchmarks beat System.Xaml; validated in Phase 0). One-time per view; **nothing in this pipeline runs per frame** — allocation discipline at 50 fps is untouched because XAML's output is the retained tree, which then drives the existing Scene/compositor path.
- Template stamping: §3.5 numbers; the only recurring cost, paid at view/item construction.
- Memory: node lists retained only for deferred members (~100 B/node ⇒ KBs per template); documents stream without retention.

### Risks, honestly weighted

1. **We own a 30–45 kLOC fork.** Real cost. Mitigations: the code is mature and behaviorally pinned by a Windows System.Xaml oracle CI leg; it's frozen plumbing, not a moving target; MIT obligations are a notices file. This is the *honest* version of the dependency — a dormant NuGet binary is the same fork with worse tools.
2. **Mono-lineage gaps in deferral/ambient.** Genuine unknown; that's why Phase 0 exists with kill criteria. If it kills, the salvage path is explicit: keep `XamlXmlReader` + MEL parser + node model (the proven parts), hand-write a smaller object writer against our property system (~4–6 kLOC) — still far ahead of from-scratch.
3. **AOT tax** until Phase 6: NativeAOT works (manifest-rooted reflection, no hard Emit dependency after spike fixes) but instantiation is interpreter-speed. Acceptable at terminal scale; the compiled backend is the designed-for endgame, not a rewrite.
4. **Editor tooling.** No XSD/completion in v1; `.cxaml` is well-formed XML so editors behave; generator diagnostics catch the dangerous class of typos at build time. Schema generation for completion is a follow-up.
5. **Engine type leak surface = exactly one type** (`MarkupExtension`), by construction; a future engine swap (compiled backend) preserves it.

---

## 8. Steelman & rebuttal

**Steelman A — "Hand-roll a minimal XAML dialect; you control everything, AOT-first, no 40 kLOC fork."**
Strongest form: Cursorial doesn't need `x:Arguments`, generics, or `x:Reference` fix-ups on day one; a tight recursive-descent parser writing straight into the property system could be ~5 kLOC, trivially AOT-safe, and wholly ours.
**Rebuttal:** the features you'd cut are not the expensive ones — *deferred templates, ambient resolution, namescope duality, MEL nesting, and line-info propagation* are on the must-have list in this very assignment, and they are precisely where the spec's hard-won design lives. A dialect that "looks like XAML" but diverges in `{}` escaping or attached-property resolution breaks the muscle memory that is the whole reason to choose XAML (requirement 7 says XAML, not "an XML UI format"). And the 5 kLOC estimate is the parser only; the glue (converters, MEs, generator, MSBuild) is identical in both designs. You don't save the glue; you only forfeit the engine and its decade of tests. Where the steelman is right: full control of error messages and zero vendored code. We buy ~90% of that back by owning the fork and wrapping every error.

**Steelman B — "Use XamlX (Avalonia's compiler): actively maintained, AOT-native, production-proven at far larger scale."**
Strongest form: XamlX compiles markup to IL/C#, eliminating runtime reflection entirely — the best startup time and the only first-class NativeAOT story; Avalonia bet its framework on it after abandoning runtime loading.
**Rebuttal, honest:** XamlX is the strongest *endgame* and the weakest *foundation*. It is not a stable library — it's consumed as a git submodule, near-undocumented, with an API shaped by Avalonia's internal needs; adopting it means absorbing a compiler framework (type-system abstraction, IL emission, diagnostics infra) as the *first* artifact of Cursorial.UI, before a single widget exists. It also makes the dev loop worse: compile-only XAML means no runtime theme files, and hot reload requires the full build pipeline. Avalonia needed XamlX because desktop apps load megabytes of styles and tens of thousands of nodes at startup; a terminal app's markup is two orders of magnitude smaller — the problem XamlX solves is mostly absent here, while the problems it creates (adoption complexity, bus factor outside Avalonia's use case, no interpretive mode) arrive immediately. Crucially, **this proposal keeps the XamlX-shaped door open**: a build-time backend consuming our node streams is the planned Phase 6, with the interpretive engine remaining the semantics oracle and dev-mode loader — which is exactly the System.Xaml→BAML and Avalonia 0.7→0.9 trajectory, walked deliberately instead of paid for up front.

**Steelman C — "Source-generator-only: compile XAML straight to C# at build time; no runtime engine at all."**
Strongest form: perfect AOT, perfect trimming, zero runtime parse cost, errors at compile time.
**Rebuttal:** the generator *becomes* the hand-rolled engine (steelman A) hosted in the worst debugging environment .NET offers, and you lose runtime loading entirely — third-party theme files, downloadable layouts, REPL-style tooling, and cheap hot reload all die. Templates still need a deferral representation at runtime (generated factories — fine, but now the *only* representation). It is the right Phase 6 and the wrong v1: sequencing it first means no working pipeline until the compiler is done; sequencing it last means it compiles a semantics that already exists and is already tested.

---

### Bottom line

System.Xaml: **disqualified by verified fact** (Windows-only on .NET 10). Portable.Xaml-as-package: **disqualified by dependency health** (dormant since Oct 2020, zero-dep repo culture). Portable.Xaml-as-vendored-engine: **the highest-leverage path** — spec-true semantics, deferral/ambient/namescopes/line-info already built, MIT, faster than the reference implementation, hidden behind Cursorial-owned seams with a one-type public leak, trim-safe via a generator manifest, NativeAOT-functional now and NativeAOT-fast by a planned compiled backend that the node-stream architecture was chosen specifically to enable. The fork is the honest cost; the spec long tail is the dishonest cost everyone else hides.

**Sources:** [dotnet/wpf#3187 — System.Xaml not available on .NET runtime](https://github.com/dotnet/wpf/issues/3187) · [NuGet: Portable.Xaml 0.26.0](https://www.nuget.org/packages/Portable.Xaml/) · [Portable.Xaml releases (last: Oct 29, 2020)](https://github.com/cwensley/Portable.Xaml/releases) · [Portable.Xaml repo (MIT, netstandard2.0, benchmarks)](https://github.com/cwensley/Portable.Xaml) · [.NET Core 3 desktop packs announcement](https://devblogs.microsoft.com/dotnet/net-core-3-and-support-for-windows-desktop-applications/)