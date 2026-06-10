# Fork C — The Cursorial.UI XAML Pipeline: Compile-Time XAML via a Roslyn Source Generator

---

## 1. Executive summary & philosophy

XAML in Cursorial.UI is **a build-time language, not a runtime format**. Every `.xaml` file in a project is parsed, type-checked, and lowered to plain C# by an incremental Roslyn source generator (`Cursorial.UI.Xaml.Generator`). What ships in the app is ordinary, debuggable, AOT-compatible C#: `InitializeComponent()` bodies, typed `x:Name` fields, template factory lambdas, and compiled binding accessors. There is no parser, no reflection-driven object graph walker, and no markup file in the published binary.

Why this is the right call *for this project specifically*:

1. **Terminal apps want to be small static binaries.** The TUI ecosystem's distribution norm is a single self-contained executable (`lazygit`, `btop`, `gh`). NativeAOT + full trimming is not a nice-to-have here; it's table stakes. A reflection XAML loader is structurally at war with that. A generator is structurally aligned with it: the generated code references every type it constructs *statically*, so the trimmer keeps exactly what's used and nothing else.
2. **The whole Cursorial stack is allocation- and startup-disciplined.** Per the design doc, frames run at 20–60 fps and per-frame allocations matter. The same discipline at startup means "parse XML, resolve types by string, box converter inputs" is the wrong shape. Generated `InitializeComponent` is straight-line construction code — the cost of XAML at runtime is the cost of the equivalent hand-written C#.
3. **Errors belong at build time.** A typo'd property, a missing event handler, a binding path that doesn't exist on the `x:DataType`, a `StaticResource` key that no reachable dictionary defines — all of these become Roslyn diagnostics with `.xaml` file/line/column locations, surfaced in the IDE error list before the app ever runs. This matches the project's existing culture (oracle-pinned tables, realized-not-advertised capabilities, "don't silently swallow protocol surface").
4. **Templates become trivially correct.** "Deferred content" — the historically hairy part of every XAML system — collapses into the most boring possible mechanism: a generated `static` factory method per template body, invoked per instantiation with a fresh namescope. No stored node graphs, no re-parse, no expression-tree compilation at runtime.

The honest cost is generator complexity and the loss of "edit markup, see it instantly without compiling." Section 7 prices the first; the design-time story in Section 3.7 (a shared front end powering an optional dev-only runtime interpreter + terminal previewer) answers the second without compromising the production path.

**Processing stance, stated explicitly:** XAML is processed **at build time** for all production scenarios. A **runtime interpreter exists as a separate, optional, dev-only package** (`Cursorial.UI.Xaml.Interactive`) that shares the same parser front end — used by the live previewer, by `dotnet watch`-style hot markup reload during development, and by the conformance test suite. It is never a dependency of generated code and is never needed in a published app.

---

## 2. Public API sketch

All types live in `Cursorial.UI.Markup` unless noted. The generator package is `Cursorial.UI.Xaml.Generator` (netstandard2.0 analyzer, packaged inside the `Cursorial.UI` NuGet so library and generator can never version-skew); the shared parser is `Cursorial.UI.Xaml.Frontend` (netstandard2.0, consumed by the generator, the interpreter, and tests).

### 2.1 Assembly/type metadata attributes

```csharp
namespace Cursorial.UI.Markup;

/// Maps a XAML namespace URI to a CLR namespace in this assembly. Read from
/// *referenced assembly symbols* by the generator — no assembly loading.
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class XmlnsDefinitionAttribute(string xmlNamespace, string clrNamespace) : Attribute
{
    public string XmlNamespace { get; } = xmlNamespace;
    public string ClrNamespace { get; } = clrNamespace;
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class XmlnsPrefixAttribute(string xmlNamespace, string preferredPrefix) : Attribute;

/// Which property absorbs element content. Required on container widgets (Fork B annotates).
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class ContentPropertyAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// Opt-in string→value converter for a type or property the generator can't fold itself.
/// The converter type is statically referenced by generated code (AOT-safe).
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Struct)]
public sealed class XamlConverterAttribute(Type converterType) : Attribute
{
    public Type ConverterType { get; } = converterType;
}

public interface IXamlValueConverter<out T>
{
    T ConvertFromText(string text);   // throws FormatException with a precise message; the
}                                     // generator wraps the call site with file/line context
```

Literal conversion precedence (build time): **generator intrinsic folding** (enums, numerics, `bool`, `string`, `Thickness`, `Color`, brush grammar, `GridLength`, `Rect`, `Size`, `TimeSpan`, `Easing` names, `Pen` shorthand, `CursorShape`, key gestures, access-key text) → **`IParsable<TSelf>`** (emit `T.Parse(text, CultureInfo.InvariantCulture)`) → **`[XamlConverter]`** → diagnostic `CXAML0103`.

### 2.2 Namescopes

```csharp
public interface INameScope
{
    void Register(string name, object element);          // throws on duplicate
    object? Find(string name);
}

public sealed class NameScope : INameScope
{
    public static void SetNameScope(Widget root, INameScope scope);   // attached, Fork-B-stored slot
    public static INameScope? GetNameScope(Widget element);           // walks to owning root
    public void Register(string name, object element);
    public object? Find(string name);
}

public static class NameScopeExtensions
{
    public static T? FindControl<T>(this Widget anchor, string name) where T : class;
    // resolves against the *nearest enclosing* scope: a widget inside a template
    // instance resolves template names; a document widget resolves document names.
}
```

### 2.3 Templates (deferred content) — joint contract with Fork A, factory shape owned here

```csharp
/// Everything a template body needs at instantiation time.
public readonly struct TemplateBuildContext
{
    public TemplateBuildContext(Widget templatedParent, INameScope nameScope, IResourceHost resources);
    public Widget TemplatedParent { get; }       // {TemplateBinding} source
    public INameScope NameScope { get; }         // fresh per instantiation
    public IResourceHost Resources { get; }      // lexical resource chain at instantiation site
}

public readonly record struct TemplateInstance(Widget Root, INameScope NameScope);

public sealed class ControlTemplate
{
    public ControlTemplate(Func<TemplateBuildContext, Widget> build);
    public Type? TargetType { get; init; }
    public TemplateInstance Instantiate(Widget templatedParent, IResourceHost resources);
}

public sealed class DataTemplate
{
    public DataTemplate(Func<TemplateBuildContext, Widget> build);
    public Type? DataType { get; init; }         // also default x:DataType for bindings inside
    public TemplateInstance Instantiate(Widget host, object? dataContext, IResourceHost resources);
}
```

The generator emits each template body as a `static Widget BuildTemplate_N(in TemplateBuildContext ctx)` method and wires `new ControlTemplate(BuildTemplate_N) { TargetType = typeof(...) }`. **Per-target, late instantiation is a delegate invoke** — no node graph retained, no re-parse, no runtime codegen.

### 2.4 Resources

```csharp
public interface IResourceHost
{
    bool TryGetResource(object key, ThemeVariant? variant, out object? value);
    IResourceHost? ResourceParent { get; }       // logical-tree / app chain (Fork B wires)
}

public class ResourceDictionary : IResourceHost
{
    public void Add(object key, object? value);
    public void AddDeferred(object key, Func<object?> factory);       // memoized on first hit
    public void AddDeferred(object key, ThemeVariant variant, Func<object?> factory);
    public IList<ResourceDictionary> MergedDictionaries { get; }      // later wins (WPF order)
    public bool TryGetResource(object key, ThemeVariant? variant, out object? value);
    public event Action<object?>? Changed;        // key, or null = bulk; feeds DynamicResource
}

/// Terminal-native theme axis (replaces WPF's implicit-OS theme): variants keyed by what
/// the *negotiated terminal* can actually do plus light/dark detection.
public readonly record struct ThemeVariant(ColorDepth MinimumDepth, bool? Dark = null)
{
    public static ThemeVariant Truecolor { get; }
    public static ThemeVariant Ansi256 { get; }
    public static ThemeVariant Ansi16 { get; }
    public static ThemeVariant Dark { get; }
    public static ThemeVariant Light { get; }
}
```

Cross-file/cross-assembly dictionary references are **by type, resolved at build time** — no pack/avares URIs:

```xml
<ResourceDictionary.MergedDictionaries>
  <ResourceDictionary Source="Themes/Base.xaml"/>        <!-- same project: generator resolves the
                                                              file, emits `new Themes_Base_Xaml()`;
                                                              missing file = compile error CXAML0205 -->
  <ResourceInclude Type="theme:CursorialDarkTheme"/>     <!-- other assembly: a generated public
                                                              dictionary class, referenced statically -->
</ResourceDictionary.MergedDictionaries>
```

### 2.5 Markup extensions

```csharp
public interface IMarkupExtension<out T>
{
    T ProvideValue(in XamlServiceContext context);
}

public readonly struct XamlServiceContext
{
    public object TargetObject { get; }
    public UIProperty? TargetProperty { get; }            // Fork A's property identity (may be null for plain CLR props)
    public INameScope? NameScope { get; }
    public IResourceHost? Resources { get; }
    public Widget? RootObject { get; }
}
```

The seven intrinsics — `{Binding}`, `{StaticResource}`, `{DynamicResource}`, `{TemplateBinding}`, `{x:Static}`, `{x:Null}`, `{x:Type}` — are **not** dispatched through this interface; the generator lowers them directly (Section 3.4). User-defined extensions implement `IMarkupExtension<T>` and are instantiated + invoked inline in generated code, so they compose without the generator knowing about them.

### 2.6 Generated-code runtime helpers

```csharp
public static class XamlRuntime
{
    // StaticResource fallback when not lexically resolvable at build time:
    public static T FindResource<T>(IResourceHost host, object key, string file, int line);
    // throws XamlResourceNotFoundException("Resource 'AccentBrush' not found (MainView.xaml:23)")

    public static void WireAccessKey(Widget target, char key, int underscoreIndex); // req. 6 plumbing
}

public sealed class XamlResourceNotFoundException : Exception;
```

### 2.7 The `.csproj` experience

One package reference; zero ceremony. `Cursorial.UI`'s `buildTransitive/Cursorial.UI.props`:

```xml
<ItemGroup>
  <CursorialXaml Include="**/*.xaml" Exclude="bin/**;obj/**" />
  <AdditionalFiles Include="@(CursorialXaml)" SourceItemGroup="CursorialXaml" />
  <CompilerVisibleItemMetadata Include="AdditionalFiles" MetadataName="SourceItemGroup" />
</ItemGroup>
<PropertyGroup>
  <!-- visible to the generator via build_property.* -->
  <CursorialXamlStrictAot Condition="'$(PublishAot)' == 'true'">true</CursorialXamlStrictAot>
</PropertyGroup>
```

No embedded resources, no MSBuild task, no intermediate `.g.cs` files on disk (unless `EmitCompilerGeneratedFiles=true` for inspection). `dotnet build` is the whole pipeline.

### 2.8 Consumer example

`Views/MainView.xaml`:

```xml
<Window x:Class="FileBrowser.MainView"
        xmlns="https://cursorial.dev/ui"
        xmlns:x="https://cursorial.dev/xaml"
        xmlns:vm="clr-namespace:FileBrowser.ViewModels"
        x:DataType="vm:MainViewModel"
        Title="{Binding WorkingDirectory}">
  <Window.Resources>
    <SolidColorBrush x:Key="AccentBrush" Color="#66d9ef"/>
    <Style x:Key="HotItem" TargetType="ListViewItem">
      <Setter Property="Background" Value="{DynamicResource AccentBrush}"/>
      <Style.Triggers>
        <Trigger Property="IsPointerOver" Value="True">
          <Setter Property="Foreground" Value="palette:0"/>
        </Trigger>
      </Style.Triggers>
    </Style>
  </Window.Resources>

  <DockPanel>
    <Menu DockPanel.Dock="Top">
      <MenuItem Header="_File" Click="OnFileMenu"/>   <!-- access key folded at build -->
    </Menu>
    <StatusBar DockPanel.Dock="Bottom">
      <TextBlock x:Name="StatusText" Text="{Binding Status}"/>
    </StatusBar>
    <ListView x:Name="FileList" ItemsSource="{Binding Files}" ItemContainerStyle="{StaticResource HotItem}">
      <ListView.ItemTemplate>
        <DataTemplate x:DataType="vm:FileItem">
          <DockPanel>
            <TextBlock Text="{Binding Size, StringFormat='{}{0:n0} B'}" DockPanel.Dock="Right"/>
            <TextBlock Text="{Binding Name}" Foreground="{TemplateBinding Foreground}"/>
          </DockPanel>
        </DataTemplate>
      </ListView.ItemTemplate>
    </ListView>
  </DockPanel>
</Window>
```

Code-behind `Views/MainView.xaml.cs`:

```csharp
namespace FileBrowser;

public partial class MainView : Window
{
    public MainView() => InitializeComponent();
    private void OnFileMenu(object sender, RoutedEventArgs e) => StatusText.Text = "menu";
}
```

Generated (`MainView.xaml.g.cs`, abridged but representative):

```csharp
// <auto-generated by Cursorial.UI.Xaml.Generator/0.1.0 — do not edit />
#nullable enable
namespace FileBrowser;

partial class MainView
{
    internal global::Cursorial.UI.Widgets.TextBlock StatusText = null!;
    internal global::Cursorial.UI.Widgets.ListView FileList = null!;
    private bool __contentLoaded;

    internal void InitializeComponent()
    {
        if (__contentLoaded) return;
        __contentLoaded = true;
        var __scope = new global::Cursorial.UI.Markup.NameScope();
        global::Cursorial.UI.Markup.NameScope.SetNameScope(this, __scope);

        // <Window.Resources> — small dictionaries inline eagerly; theme-scale ones defer
        var __res = this.Resources;
#line 8 "Views/MainView.xaml"
        __res.Add("AccentBrush", new global::Cursorial.Drawing.SolidColorBrush(
            global::Cursorial.Output.Color.FromRgb(0x66, 0xd9, 0xef)));
#line 9 "Views/MainView.xaml"
        var __style0 = new global::Cursorial.UI.Style(typeof(global::Cursorial.UI.Widgets.ListViewItem));
        __style0.Setters.Add(new global::Cursorial.UI.Setter(
            global::Cursorial.UI.Widgets.ListViewItem.BackgroundProperty,
            global::Cursorial.UI.DynamicResourceValue.Create("AccentBrush")));
        var __trigger0 = new global::Cursorial.UI.Trigger(
            global::Cursorial.UI.Widgets.ListViewItem.IsPointerOverProperty, true);
        __trigger0.Setters.Add(new global::Cursorial.UI.Setter(
            global::Cursorial.UI.Widgets.ListViewItem.ForegroundProperty,
            global::Cursorial.Output.Color.FromPalette(0)));
        __style0.Triggers.Add(__trigger0);
        __res.Add("HotItem", __style0);

#line 18 "Views/MainView.xaml"
        var __e0 = new global::Cursorial.UI.Widgets.DockPanel();
#line 19 "Views/MainView.xaml"
        var __e1 = new global::Cursorial.UI.Widgets.Menu();
        global::Cursorial.UI.Widgets.DockPanel.SetDock(__e1, global::Cursorial.UI.Widgets.Dock.Top);
#line 20 "Views/MainView.xaml"
        var __e2 = new global::Cursorial.UI.Widgets.MenuItem();
        __e2.Header = new global::Cursorial.UI.AccessText("File", accessKey: 'F', underscoreIndex: 0);
        __e2.Click += this.OnFileMenu;                 // typo here = CS error at build
        __e1.Items.Add(__e2);
        __e0.Children.Add(__e1);
        // … StatusBar elided …
#line 25 "Views/MainView.xaml"
        var __e5 = new global::Cursorial.UI.Widgets.ListView();
        this.FileList = __e5; __scope.Register("FileList", __e5);
        __e5.Bind(global::Cursorial.UI.Widgets.ListView.ItemsSourceProperty,
            global::Cursorial.UI.Binding.Compiled<global::FileBrowser.ViewModels.MainViewModel,
                global::System.Collections.Generic.IReadOnlyList<global::FileBrowser.ViewModels.FileItem>>(
                static vm => vm.Files, path: "Files"));
        __e5.ItemContainerStyle = __style0;            // StaticResource, lexically resolved at build
        __e5.ItemTemplate = new global::Cursorial.UI.Markup.DataTemplate(__BuildTemplate_1)
            { DataType = typeof(global::FileBrowser.ViewModels.FileItem) };
        __e0.Children.Add(__e5);
        this.Content = __e0;

        this.Bind(global::Cursorial.UI.Widgets.Window.TitleProperty,
            global::Cursorial.UI.Binding.Compiled<global::FileBrowser.ViewModels.MainViewModel, string>(
                static vm => vm.WorkingDirectory, path: "WorkingDirectory"));
    }

    private static global::Cursorial.UI.Widget __BuildTemplate_1(
        in global::Cursorial.UI.Markup.TemplateBuildContext __ctx)
    {
#line 28 "Views/MainView.xaml"
        var __t0 = new global::Cursorial.UI.Widgets.DockPanel();
        // … compiled bindings against FileItem; TemplateBinding lowered to:
        var __t2 = new global::Cursorial.UI.Widgets.TextBlock();
        __t2.BindTemplate(global::Cursorial.UI.Widgets.TextBlock.ForegroundProperty,
            global::Cursorial.UI.Widgets.Widget.ForegroundProperty, __ctx.TemplatedParent);
        __t0.Children.Add(__t2);
        return __t0;
    }
}
```

Every `#line` directive means breakpoints set *in the .xaml file* bind, exceptions show `MainView.xaml:25` frames, and stepping through `InitializeComponent` walks the markup.

---

## 3. Internal architecture

### 3.1 Pipeline overview

```
.xaml (AdditionalText)
   │  Stage A — Front end (Cursorial.UI.Xaml.Frontend, shared with interpreter)
   ▼
XamlDocument            immutable, value-equatable node tree + diagnostics + content hash
   │  Stage B — Binder (generator-only; needs Compilation symbols)
   ▼
BoundDocument           every element→INamedTypeSymbol, member→property/event/attached,
   │                    literals classified, markup extensions parsed & typed
   │  Stage C — Lowering
   ▼
CodeModel               flat op list per construction scope (document, each template body,
   │                    each deferred resource factory)
   │  Stage D — Emit
   ▼
*.xaml.g.cs             deterministic C#, #line-mapped, AddSource'd
```

**Stage A — front end.** `XmlReader` over the `AdditionalText` with `IXmlLineInfo` capturing `(line, column, length)` for every element, attribute name, and attribute value. Output node model (all `sealed record`, netstandard2.0):

```csharp
sealed record XamlDocument(string Path, string ContentHash, XamlElement Root,
    ImmutableArray<NamespaceDecl> Namespaces, ImmutableArray<XamlDiagnostic> Diagnostics);
sealed record XamlElement(XamlName Name, ImmutableArray<XamlAttribute> Attributes,
    ImmutableArray<XamlNode> Children, SourceSpan Span);   // XamlNode = element | text
sealed record XamlAttribute(XamlName Name, string RawValue, SourceSpan NameSpan, SourceSpan ValueSpan);
```

Property-element syntax (`<Button.Content>`) is normalized here; the markup-extension mini-grammar (`{Ext positional, Name=Value, Nested={...}}`, `{}` escape) is parsed here into `MarkupExtensionNode` trees so the interpreter and generator share one grammar. Equality is by `ContentHash` — this is what makes incremental caching exact.

**Stage B — binder.** A `XamlTypeUniverse` wraps the `Compilation`:

- xmlns table: built by walking `compilation.References` assembly symbols for `XmlnsDefinitionAttribute`, plus `clr-namespace:`/`assembly=` URIs, plus the intrinsic `x:` namespace.
- `TryResolveType("Button") → BoundType` memoized per universe. `BoundType` lazily materializes a member table from symbols: settable properties, `Add`-bearing collection properties, events, attached `Get/SetFoo` static pairs, the `FooProperty` static `UIProperty` fields (for setter/trigger/binding lowering), `[ContentProperty]`, `[XamlConverter]`, `IParsable<T>` implementation, accessible constructors.
- Symbols never leave Stage B: the bound tree stores **strings** (fully-qualified metadata names, member names) and small structs, never `ISymbol`, to honor incremental-generator memory rules.

**Stage C — lowering.** Each construction scope becomes a `CodeModel`: an ordered list of ops —

```
CreateLocal(type, ctorArgs) | SetProp(local, member, ValueExpr) | AddToCollection
| SetAttached(ownerType, member) | WireEvent(local, event, handlerName)
| RegisterName(local, name) | AssignField(local, fieldName)
| InstallBinding(local, propField, BindingExpr) | BindResource(local, propField, key)
| EmitTemplateMethod(bodyCodeModel) | AddResource(key, ValueExpr) | AddDeferredResource(key, factoryCodeModel)
```

`ValueExpr` is a closed union: `FoldedCSharp(string expr)` (literal folding), `ParseCall(type)`, `ConverterCall(converterType)`, `NewObject(ref to CreateLocal graph)`, `MarkupExtensionInvoke(...)`, `CompiledBindingFactory(...)`. Template bodies and deferred resource factories recurse into child `CodeModel`s with their own local-variable counters and namescope targets.

**Stage D — emit.** `IndentedTextWriter` over a pooled `StringBuilder`; fully-qualified `global::` names everywhere (no `using` collisions); `#line N "relative/path.xaml"` before each op that has a source span, `#line hidden` around plumbing; deterministic local naming (`__e{n}`, `__t{n}`) so diffs of `EmitCompilerGeneratedFiles` output are stable. SyntaxFactory is deliberately *not* used — string emission is ~10× faster and the output is compiled (and thus validated) by Roslyn anyway.

### 3.2 Incremental-generator wiring and performance

```csharp
public void Initialize(IncrementalGeneratorInitializationContext context)
{
    var parsed = context.AdditionalTextsProvider
        .Where(static t => t.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        .Select(static (t, ct) => XamlFrontEnd.Parse(t, ct));        // cached by ContentHash

    // Tier 1: xmlns/type universe from *metadata references* — changes only when the
    // reference set changes. Equatable by ordered assembly identity + MVID list.
    var referenceUniverse = context.CompilationProvider
        .Select(static (c, ct) => ReferenceFingerprint.Extract(c));

    // Tier 2: in-project types the XAML can see. Keyed per declaration skeleton
    // (type name + member signature hash), so editing a method *body* anywhere,
    // or any type XAML doesn't reference, does not invalidate codegen.
    var localSkeletons = context.SyntaxProvider.CreateSyntaxProvider(
            static (node, _) => node is ClassDeclarationSyntax or StructDeclarationSyntax,
            static (ctx, ct) => TypeSkeleton.Extract(ctx, ct))
        .Collect();

    var perDoc = parsed.Combine(referenceUniverse).Combine(localSkeletons);
    context.RegisterSourceOutput(perDoc, static (spc, input) =>
        XamlCompiler.BindLowerEmit(spc, input /* re-acquires live symbols via a
            thread-local Compilation handle only inside this step */));
}
```

Honest performance accounting:

- **Parse** (Stage A) runs only when a `.xaml` file's content changes. ~0.1–0.3 ms per typical document.
- **Bind+lower+emit** for one document is 1–3 ms (memoized type universe; member tables built once per universe per type). A 200-document app cold-builds the XAML side in ~0.5 s, single-threaded; `RegisterSourceOutput` steps parallelize across documents.
- **The keystroke question** (the classic `CompilationProvider` trap): a C# edit produces a new `Compilation`. Tier 1 fingerprint is unchanged (references didn't change); Tier 2 skeletons are unchanged unless the edit altered a type's *shape*. Both inputs being equal, the per-document output steps are **not** re-run — that is the whole point of the two-tier fingerprint. Editing a class that XAML references (adding a property to a viewmodel) re-runs binding for the documents whose skeleton set changed; worst case (editing `Widget` itself) re-runs everything, costing the ~0.5 s above on a cancellable background thread. IDE typing latency is unaffected; Roslyn cancels stale runs via the `CancellationToken` threaded through every stage.
- Memory: no `ISymbol`/`Compilation` captured in pipeline state; node models are immutable records sized proportional to the markup.

### 3.3 Type resolution

- `xmlns="https://cursorial.dev/ui"` → the set of CLR namespaces declared by `[XmlnsDefinition]` in `Cursorial.UI` (and any assembly claiming the same URI — additive, searched in deterministic assembly-name order, ambiguity = `CXAML0105`).
- `xmlns:vm="clr-namespace:FileBrowser.ViewModels"` (same assembly) and `clr-namespace:X;assembly=Y` (foreign) supported verbatim.
- `xmlns:x="https://cursorial.dev/xaml"` is intrinsic — `x:Class`, `x:ClassModifier`, `x:Name`, `x:FieldModifier`, `x:Key`, `x:DataType`, `x:TypeArguments`, `x:Static`, `x:Null`, `x:Type`. Because we are compile-time, `x:TypeArguments` (generic widget instantiation, e.g. `<ItemsView x:TypeArguments="vm:FileItem">` → `new ItemsView<FileItem>()`) is a near-free differentiator that runtime loaders find painful.
- Unknown element → `CXAML0101` at the element's span; unknown member → `CXAML0102`; both are *errors*, not silent skips — consistent with the input layer's "never silently swallow" doctrine.

### 3.4 Property setting and markup-extension lowering

| Form | Lowering |
|---|---|
| Literal on CLR property | Folded constant or `Parse`/converter call; `__e0.Title = "Files";` |
| Literal on `UIProperty`-backed property | Same value through the CLR accessor (Fork A guarantees accessor = `SetValue(Property, v, ValueSource.Local)`) |
| Attached `Grid.Row="1"` | `Grid.SetRow(__e0, 1);` |
| Content / collection | `[ContentProperty]` target; `IList<T>`-shaped property → repeated `.Add(child)`; dictionary-shaped + `x:Key` → `.Add(key, child)` |
| `{x:Null}` | `null` |
| `{x:Static local:Theme.Accent}` | `global::…Theme.Accent` — zero runtime cost, member existence checked at build |
| `{x:Type local:FileItem}` | `typeof(global::…FileItem)` |
| `{StaticResource K}` | If `K` is defined in a lexically enclosing `*.Resources` block *in this document* or a build-time-traceable merged dictionary: direct local/field reference (zero lookup). Else: `XamlRuntime.FindResource<T>(this.Resources-chain, "K", "file", line)` executed once at init. Key not found in any build-reachable dictionary → warning `CXAML0201` (suppressible; dictionaries can legally be assembled at runtime). |
| `{DynamicResource K}` | `widget.BindResource(Prop, "K")` (Fork A primitive): subscribes through the `IResourceHost` chain; re-evaluates on `ResourceDictionary.Changed` and on tree reparenting. In `Setter.Value` position it lowers to `DynamicResourceValue.Create("K")`, Fork A's deferred setter value. |
| `{TemplateBinding Prop}` | `target.BindTemplate(TargetProp, SourceProp, __ctx.TemplatedParent)` — one-way, no path walk, property identities resolved at build (`CXAML0304` if the template's `TargetType` lacks the property). |
| `{Binding Path, Mode=…, Converter=…, StringFormat=…}` | See 3.5. |
| User extension `{local:Pluralize Count}` | `new PluralizeExtension(count){ … }.ProvideValue(new XamlServiceContext(__e0, Prop, __scope, __res, this))` — duck-typed against `IMarkupExtension<T>`, `T` checked assignable at build. |

### 3.5 Compiled bindings

When a data type is known — `x:DataType` on the root or on the enclosing `DataTemplate`, or `{TemplateBinding}` — the generator emits a **typed binding descriptor** consumed by Fork A's binding engine:

```csharp
// Contract shape (Fork A owns the engine; the generator emits the descriptors):
public static class Binding
{
    public static CompiledBinding<TSource, TValue> Compiled<TSource, TValue>(
        Func<TSource, TValue> getter, string path,
        Action<TSource, TValue>? setter = null,
        BindingMode mode = BindingMode.OneWay,
        IValueConverter? converter = null, string? stringFormat = null);
}
```

For multi-segment paths (`{Binding Selected.Name}`) the generator emits one getter per segment plus the segment names (`"Selected", "Name"`), so the engine can hook `INotifyPropertyChanged` per hop without reflection. Null-propagation is generated (`vm.Selected?.Name`). A path member missing on the `x:DataType` is **error `CXAML0301` at the binding's span** — the single biggest day-to-day quality win over runtime XAML.

`{Binding}` **without** any inferable data type falls back to Fork A's reflection-path binding (`Binding.Reflection("Selected.Name")`), and the generator emits **warning `CXAML0302`** ("binding cannot be compiled; add x:DataType"). Under `CursorialXamlStrictAot=true` (auto-set by `PublishAot`) that warning becomes an error: a strict-mode app provably contains zero reflection bindings.

### 3.6 Deferred content, templates, namescopes

- **Mechanism: generated static factory methods**, one per `ControlTemplate`/`DataTemplate`/deferred-resource body. Nothing about the template is interpreted at runtime; "deferral" is just "code that hasn't been called yet."
- **Per-instantiation namescope**: `ControlTemplate.Instantiate` allocates a fresh `NameScope`, passes it in the `TemplateBuildContext`; the generated body registers each `x:Name` into *that* scope. `GetTemplateChild(name)` on the templated control = `instance.NameScope.Find(name)`. Template names never collide with document names because the scopes are distinct objects; `FindControl` resolves against the nearest enclosing scope (template instance scope for widgets inside one, document scope above).
- **Document namescope**: `InitializeComponent` creates one `NameScope`, attaches it to the root, registers every `x:Name`, and *also* assigns the generated typed fields. Fields are the fast path (and give IntelliSense + rename refactoring across markup and code-behind, since the generator emits them immediately in the IDE); `FindControl` remains for dynamically loaded/foreign subtrees. Both work; fields are idiomatic.
- **Field access control**: `x:FieldModifier="private|internal|public"`, default `internal`.
- **Style/Setter values are not deferred content** — `Setter` stores the folded value or a `DynamicResourceValue`/binding descriptor (Fork A's object model); only template bodies and large resource values get factory treatment.
- **Deferred resources**: any resource element above a small op-count threshold (or all entries in a document whose root is `ResourceDictionary`, i.e. theme files) lowers to `AddDeferred(key, static () => …)` so a 300-brush theme costs a dictionary of delegates until keys are touched — same shape as Avalonia's deferred XAML resources, but the factory is compiled code.

### 3.7 Design-time experience (no IDE XAML designer exists for terminals anyway)

This is where "terminal" changes the calculus: there is no Blend, no WYSIWYG canvas to lose. The design-time loop is:

1. **In-editor diagnostics.** All `CXAML*` diagnostics carry `Location.Create("Views/MainView.xaml", span, lineSpan)` — VS/Rider surface them in the error list with click-through, and in-file squiggles where the editor supports additional-file locations. `x:Name` fields and `InitializeComponent` appear in IntelliSense immediately because the incremental generator runs in the IDE.
2. **XML completion.** A `cursorial xaml-schema` dotnet tool emits an XSD from the same type universe (refresh on demand or post-build); editors with XML schema association get element/attribute completion. Cheap, dumb, effective.
3. **Live preview without compiling.** `cursorial preview Views/MainView.xaml` — a dotnet tool hosting the **runtime interpreter** (`Cursorial.UI.Xaml.Interactive`, built on the same `Xaml.Frontend` parser + reflection over the project's last-built assemblies). It opens a `TerminalSession`, renders the document through the real Drawing/Rendering stack, file-watches, and re-renders on save (~50 ms loop). Bindings run against an optional design-data object (`d:DesignData` attribute, ignored by the generator). This is the hot-markup-loop answer: instant feedback in development, zero presence in production.
4. **`dotnet watch`** covers the integrated loop (markup edit → incremental build, generator re-emits only the changed document → relaunch).

### 3.8 The runtime-fallback question, answered directly

Should generated apps be able to load XAML at runtime? **No — not in the box, and not implicitly.** The production contract is: markup in, C# out, done. The interpreter package exists for (a) the previewer, (b) the conformance suite, (c) genuinely dynamic scenarios (plugin-supplied markup) where an app *opts in* by referencing `Cursorial.UI.Xaml.Interactive` and accepting reflection, non-AOT-safety, and runtime errors as its own informed tradeoff. The drift risk between the two implementations is contained structurally: **one shared front end** (parser, markup-extension grammar, intrinsic semantics tables) and **one shared conformance corpus** — every `.xaml` test fixture is run through *both* the generator (compile + execute) and the interpreter, asserting identical widget trees (Section 3.9). The interpreter is also deliberately a *subset*: no compiled bindings (always reflection), no `x:TypeArguments`; the corpus marks subset-only fixtures.

### 3.9 Testability

- **Front end**: pure-function golden tests — markup in, S-expression dump of the node tree + diagnostics out. Runs on netstandard2.0, no Roslyn.
- **Binder/emit**: standard `CSharpGeneratorDriver` snapshot tests (Verify): given markup + a stub widget-library compilation, assert generated source and diagnostics. Incrementality is itself tested with `GeneratorDriver.GetRunResult` step-reason assertions ("editing an unrelated method body produces all-`Cached` outputs") — incremental regressions are the most common generator bug class, so they get first-class regression tests.
- **Execution**: compile generated output in-memory against the real `Cursorial.UI`, instantiate, walk the tree with assertions. These are ordinary xUnit tests in `Cursorial.UI.Xaml.Tests`.
- **Conformance corpus**: shared fixtures executed through generator and interpreter, trees compared node-by-node.
- **MSBuild integration**: one sample app project built in CI (`dotnet build` + `dotnet publish /p:PublishAot=true`) proving the props wiring, strict mode, and trim-cleanliness end to end.

---

## 4. Requirement satisfaction

- **Req 7 (XAML, the assignment): covered in full** — parsing/instantiation (3.1, 3.4), type resolution (3.3), converters/content/collections/attached (3.4, 2.1), all seven markup extensions plus user extensions (3.4), `x:Name`/code-behind (3.6), deferred template content with a concrete mechanism (3.6), namescopes (3.6), line/column errors at build *and* `#line`-mapped at runtime (2.8, 3.4), resource dictionaries + merged + theme files (2.4, 3.6), AOT stance (3.5, Section 6), build/runtime stance (Section 1), `.csproj` story (2.7).
- **Req 1 (styling/templating)**: XAML constructs Fork A's `Style`/`Setter`/`Trigger`/`ControlTemplate` object model imperatively in generated code; `TargetType` makes setter property references (`Property="Background"`) resolve to `UIProperty` static fields at build, with type-checked setter values folded against the property's type.
- **Req 2 (binding)**: compiled bindings with build-checked paths when `x:DataType`/`TemplateBinding` context exists; descriptor contract in 3.5; reflection fallback gated by diagnostics and strict mode.
- **Req 3 (resource/style inheritance)**: lexical `StaticResource` resolution at build where provable, runtime chain walk (`IResourceHost.ResourceParent`) otherwise; `DynamicResource` subscriptions through the same chain; merged dictionaries with WPF precedence; `ThemeVariant` keyed entries for depth/light-dark theming.
- **Req 6 (access keys)**: `_File` literals are folded at build into `AccessText("File", 'F', 0)` — no runtime underscore parsing; the toggle-on-Alt vs always-visible behavior is Fork B's, capability-gated per the input reference (§7 of the input map); the markup pipeline guarantees the data (key + underline index) is present and precomputed.
- **Req 8 (setters + triggers/selectors)**: the generator is agnostic between WPF-style `<Style.Triggers>` and Avalonia-style selectors — it constructs whichever object model Fork A ships; if selectors win, the selector string is parsed *at build* into a selector AST constructor chain with class/type names validated (`CXAML0107` on unknown widget class).
- **Req 9 (property system)**: consumed, not owned — generated code goes through CLR accessors backed by `UIProperty` storage; setter/trigger/binding lowering depends on the `FooProperty` field convention (contract below).
- **Req 10 (animation)**: Phase 5 adds `<Storyboard>`/`<DoubleAnimation …>` markup constructing the existing `Cursorial.Animation` value types plus Fork A/B's storyboard orchestration objects; nothing in the pipeline is animation-specific (it's just more object construction), so this rides for free once the orchestration object model exists.
- **Reqs 4, 5 (focus, windows)**: not markup concerns beyond instantiating Fork B's `Window`/`Dialog` types and wiring their events, which the pipeline already does.

---

## 5. Cross-fork contract

What Fork C **requires**, stated as interfaces. Anything here that the other forks shape differently is a renegotiation point, not a blocker — the generator lowers to whatever the object model is; these are the seams it needs to exist.

```csharp
// ---- From Fork A (properties / styling / binding) ----
public abstract class UIProperty { public string Name { get; } public Type OwnerType { get; } }
public sealed class UIProperty<T> : UIProperty { }

// REQUIRED CONVENTION: for every XAML-settable styled property, a public static
// readonly field `<Name>Property` of type UIProperty<T> on the owner, and a CLR
// accessor pair whose setter is equivalent to SetValue(Property, value, Local).
// The generator resolves `Property="Background"` and trigger/setter targets to these fields.

public interface IBindableObject       // implemented by Widget (Fork B) via Fork A storage
{
    void Bind(UIProperty property, BindingBase binding);
    void BindTemplate(UIProperty target, UIProperty source, Widget templatedParent);
    void BindResource(UIProperty target, object resourceKey);          // DynamicResource
}
public abstract class BindingBase { }
public static class Binding            // factory surface per §3.5
{
    public static CompiledBinding<TS, TV> Compiled<TS, TV>(Func<TS, TV> getter, string path,
        Action<TS, TV>? setter = null, BindingMode mode = BindingMode.OneWay,
        IValueConverter? converter = null, string? stringFormat = null);
    [RequiresUnreferencedCode("Reflection binding")]
    public static BindingBase Reflection(string path, BindingMode mode = BindingMode.OneWay, ...);
}

public sealed class Style { public Style(Type targetType); IList<Setter> Setters; IList<TriggerBase> Triggers; }
public sealed class Setter(UIProperty property, object? value);
public static class DynamicResourceValue { public static object Create(object key); } // deferred setter value
// Styles/Setters/Triggers (or selectors) must be constructible imperatively with public ctors/collections.

// ---- From Fork B (widget tree / windows / focus / input) ----
public abstract class Widget : IBindableObject
{
    public string? Name { get; set; }                    // x:Name mirrors here
    public ResourceDictionary Resources { get; }         // per-widget resource host
    public IResourceHost? ResourceParent { get; }        // logical-tree chain, app at root
    public object? Content / Children / Items …          // shaped freely, but every container
}                                                        // carries [ContentProperty]
// REQUIRED: [ContentProperty] on containers; events as ordinary C# events (generator wires +=);
// attached behaviors as static Get/SetX pairs; a Widget slot for NameScope attachment;
// Application.Current.Resources as the chain root; TemplatedParent reachable for template children.

// ---- Provided BY Fork C to both ----
// INameScope/NameScope, ResourceDictionary/IResourceHost/ThemeVariant, ControlTemplate/DataTemplate/
// TemplateBuildContext/TemplateInstance, IMarkupExtension<T>/XamlServiceContext, the attribute set
// (XmlnsDefinition/ContentProperty/XamlConverter), and the guarantee that generated code touches
// widgets only through public surface — no InternalsVisibleTo needed into Fork A/B.
```

Notes: if Fork A prefers Avalonia-style `StyledProperty<T>`/`DirectProperty<T>`, only the field-convention name changes. If Fork B wants `Window`-level namescope storage instead of an attached slot, `NameScope.SetNameScope` changes implementation, not call sites. The single hard requirement I will fight for: **property identity as static fields discoverable by symbol name** — without it, setters/triggers/template bindings can't be resolved at compile time and half the value of this fork evaporates.

---

## 6. Terminal-specific adaptations

Deliberate deviations from WPF/Avalonia, because cells are not pixels:

1. **Integer-cell geometry converters.** `Thickness`/`Margins` fold to integers ("1,0,1,0"); fractional values are a build error (`CXAML0110`) — there is no half-cell. `GridLength` supports `Auto | * | n* | n` where `n` is whole cells. `Rect`/`Size` literals fold to the ushort-backed Rendering types with build-time range validation (negative or >65535 → compile error, honoring the documented `Rect` constraints instead of throwing at runtime).
2. **Color/brush grammar is the Drawing grammar.** Literal colors accept `#RGB/#RRGGBB` (→ `Color.FromRgb`), `palette:N` (→ `FromPalette` — first-class because 16/256-palette fidelity matters on real terminals), named ANSI colors, and `default`. Brush literals reuse the already-shipped `BrushMarkup` inline grammar (`linear:#f92672,#66d9ef`, `radial:…`, `conic:…`) so markup text, rich-text `[brush=…]` tags, and XAML all speak one dialect — folded at build into `LinearGradientBrush(...)` constructor calls with stop lists as collection expressions.
3. **`ThemeVariant` is capability-shaped, not OS-shaped.** Theme selection keys off the negotiated `ColorDepth` and the `DefaultBackground`-luminance dark/light signal (both already in `OutputCapabilities`), chosen at app start from `TerminalCapabilities` — there is no OS theme broker in a terminal. Renegotiation (`RenegotiateAsync`) re-resolves the active variant and pulses `ResourceDictionary.Changed`, which `DynamicResource` subscriptions already handle.
4. **No URI/asset subsystem.** WPF's pack URIs and Avalonia's `avares://` exist to locate markup and assets at runtime. We have no runtime markup, so dictionary `Source` resolves to a *generated class* at build time and cross-assembly themes are plain types (`<ResourceInclude Type="…"/>`). Images referenced from markup (`<Image Source="logo.png"/>`) lower to `Icon.FromEmbedded(...)` against an `EmbeddedResource` the props file wires up — the existing Rendering content/fragment pipeline does the rest.
5. **Access-key literals are folded** (Section 4, req 6) and the `AccessText` data model carries the underline index so Fork B can render the underscore either persistently or only-while-Alt-held per the capability gate (`Keyboard.ReportsRepeats`/Win32 path) — the markup layer guarantees zero runtime string scanning.
6. **`Easing` literals** (`Easing="QuadOut"`) fold to `Easings.QuadOut` delegate references — the catalog is static members, so this is `{x:Static}`-equivalent for free; `cubic-bezier(…)` can join later as an intrinsic fold.
7. **Scale assumptions.** Hundreds of elements, not tens of thousands, means: no BAML-style binary intermediate (generated C# *is* the optimized form), eager inline construction for small resource blocks, deferral only where it pays (themes, templates). The generated `InitializeComponent` for a heavy view is a few hundred straight-line statements — microseconds at startup, zero steady-state cost, perfectly aligned with the "construct once in `Initialize()`, sample per frame" asset-lifetime discipline the demos already canonize.
8. **No `UpdateSourceTrigger=PropertyChanged` ambiguity, no IME/composition special cases in markup** — text input subtleties live in Fork B's `TextBox`; the pipeline stays out.

---

## 7. Costs, risks, phasing

**Effort estimate** (one experienced engineer, with the multi-agent design/critique process this repo already uses):

| Phase | Scope | Estimate |
|---|---|---|
| 0 | `Xaml.Frontend`: XML→node model, markup-extension grammar, spans/diagnostics, golden tests | 1–1.5 wk |
| 1 | Generator MVP: xmlns/type universe, instantiation, literal folding (core converters), content/collections/attached, events, `x:Name` fields + namescope, `x:Class` partials, `#line`, props packaging, snapshot + incrementality tests | 2–3 wk |
| 2 | Markup extensions (`x:Static/Null/Type`, Static/DynamicResource), resource dictionaries, merged/`ResourceInclude`, deferred resources, `ThemeVariant` | 1.5–2 wk |
| 3 | Templates: factory lowering, template namescopes, `TemplateBinding`, Style/Setter/Trigger lowering (Fork A model lands here) | 1.5–2 wk |
| 4 | Compiled bindings (`x:DataType`, multi-segment, two-way, converters/StringFormat), strict-AOT mode, publish-AOT CI proof | 2 wk |
| 5 | `Xaml.Interactive` interpreter + `cursorial preview` + conformance corpus; XSD tool; storyboard markup | 2–3 wk, parallelizable |

Total ≈ 10–13 engineer-weeks to full scope; **Phases 0–2 already ship a usable declarative UI** (views, resources, styles-without-templates).

**Performance characteristics**: build-time cost ~0.5 s cold / ~0 incremental for a 200-file app (3.2); runtime cost identical to hand-written construction; startup contains no parsing; published size carries no parser, no XML stack, no markup files; strict mode is NativeAOT-clean by construction.

**Risks, honestly:**

1. **Generator complexity is real and permanent.** ~12–18k LOC across front end + binder + emit, plus the diagnostics surface. Mitigations: the staged IR keeps concerns separable; the front end is Roslyn-free and trivially testable; the §3-style living design doc + adversarial review process this repo already practices; library + generator ship in one package, eliminating version-skew bug classes.
2. **Incrementality regressions** are the classic failure mode (accidentally capturing a symbol, fingerprint too coarse → keystroke rebinds). Mitigation: step-reason regression tests in CI from day one (3.9) — treat "all-Cached on unrelated edit" as a pinned invariant like the compositing invariant.
3. **Generators can't see other generators' output.** XAML referencing a type *produced by another source generator* in the same project won't resolve. Documented limitation (`CXAML0101` plus a help-link explaining it); workaround is moving such types to a referenced project. Rare in practice; viewmodel generators (e.g. `INotifyPropertyChanged` generators) still work because the *type* is user-declared and only members are generated — the skeleton extractor reads the user partials, and compiled-binding resolution against generator-produced members needs the member declared in user code or the fallback reflection binding (warned, suppressible).
4. **Debugging generated code** could be miserable; it isn't, because of three deliberate choices: `#line`-mapped sequence points (breakpoints in `.xaml` work), `EmitCompilerGeneratedFiles` documented as the inspection path, and readable deterministic output (named locals, one statement per markup construct). `<CursorialXamlSourceMaps>false</…>` flips off `#line` for those who'd rather step the C#.
5. **Design-time gap vs. interpreted XAML**: no instant-apply without a build. Mitigated by the previewer (interpreter) for layout/styling iteration and `dotnet watch` for the integrated loop; accepted as the cost of the AOT/diagnostics win.

**Punted (recorded §11-style):** `x:Shared`, `x:Uid`/localization pipeline, attached-property paths in bindings (`(Grid.Row)`), `RelativeSource AncestorType` bindings (needs Fork B tree-walk support), XAML-declared behaviors/interactions, designer "design-data" beyond the previewer's `d:` ignore-namespace, binary resource compaction. None are architecturally blocked; each is additive lowering.

---

## 8. Steelman & rebuttal

**Steelman A — runtime reflection loader (WPF-spirit, interpret at startup).** *Strongest case:* radically simpler — one implementation, no Roslyn expertise, no incrementality engineering, no generated-code debugging story needed; markup is data, so hot reload is trivial and plugins can ship `.xaml`; build stays fast; the terminal scale (hundreds of elements) means parse cost at startup is single-digit milliseconds, genuinely negligible; XamlX-class maintenance horror stories don't apply to an interpreter.

*Rebuttal:* the startup-cost argument is correct and I won't pretend otherwise — interpretation is fast enough at this scale. The case fails on three other axes. (1) **AOT/trimming is disqualifying, not inconvenient**: an interpreter resolves types and sets properties by name; under full trimming every widget, converter, and viewmodel member reachable only from markup is invisible to the trimmer. The "fix" is rooting annotations or descriptor files — exactly the brittle, whole-app-spanning machinery this stack has refused everywhere else (the lower layers are built on "realized, not advertised; verified, not assumed"). Terminal apps publish as trimmed single files; the flagship deployment mode cannot be the degraded mode. (2) **Error timing**: every misspelled property, dead binding path, and missing handler becomes a runtime fault discoverable only by exercising that screen. The repo's culture is oracle-pinned tables and compile-checked invariants; "your menu crashes when opened" is not in character. (3) **The hot-reload advantage shrinks on inspection**: this proposal ships the same interpreter as a dev tool, so the iteration loop exists — it's just kept out of the product. Choosing interpretation makes the *production* binary carry the dev tool forever.

**Steelman B — XamlX-style post-compile IL emission (Avalonia's actual architecture).** *Strongest case:* it's proven at scale; it runs *after* compilation so it sees the complete assembly (including other generators' output — erasing risk #3); it can emit constructs C# source can't express; it doesn't occupy the source-generator budget or interact with IDE generator runs at all; Avalonia demonstrates compiled bindings, compiled selectors, and trimming compatibility on exactly this design.

*Rebuttal:* everything Avalonia's MSBuild task buys, it pays for in the currency this project can least afford: **maintenance and transparency**. IL emission means hand-rolling or Cecil/SRM-level code emission, a private debug-info writer to get sequence points, bespoke handling for every TFM/runtime change, and a build step invisible to Roslyn — no IDE diagnostics in the error list as you type, no IntelliSense-immediate `x:Name` fields (Avalonia needs a *separate* completion engine and previewer process to recover the experience), broken incremental-build edges, and "what code actually ran?" answerable only with a decompiler. XamlX itself is famously under-documented and has a bus factor the Avalonia team carries with multiple dedicated maintainers; Cursorial is not staffed to adopt that liability. The one concrete capability IL-weaving adds — seeing other generators' output — is bounded (risk #3) and has a documented workaround, while the source-generator route gets sequence points (`#line`), IDE diagnostics, readable output, and incremental builds *from the platform for free*. Scoped sanely (no `XamlIl` compatibility layers, one widget toolkit, one intrinsic set, terminal-scale documents), the generator is the 20%-of-XamlX that yields 95% of the value at a fraction of the carrying cost.

**Steelman C — skip XAML; a typed C# builder DSL.** *Strongest case:* zero pipeline, perfect tooling, refactoring and debugging for free; C# 12 collection expressions and object initializers read almost declaratively.

*Rebuttal:* requirement 7 mandates XAML, and for cause — templates/styles/themes authored as data enable the previewer, theming by non-authors, and the WPF/Avalonia muscle memory this project deliberately courts (`RelativePoint`, `Brushes`, `Push*` naming kinship). Nothing here *blocks* a builder DSL coexisting — generated code proves the object model is fully constructible imperatively, so the DSL is just "what the generator emits, by hand."

The judges should weigh one closing fact: **this approach's worst-case failure mode is graceful.** If the generator proves too costly to maintain, the front end, the object model (`ResourceDictionary`, templates-as-delegates, `INameScope`), and the interpreter all survive — the system degrades to Steelman A with a better-than-WPF object model. If an interpreter-first system later wants compile-time guarantees, it has to retrofit a type-checked binder onto a dynamically-shaped runtime — the expensive direction. Build the checked pipeline first; keep the interpreter as the cheap appendage it naturally is.