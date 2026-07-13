using System.Linq;

using Cursorial.UI.Xaml.Generator;

using Microsoft.CodeAnalysis;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// WS-X4.5 — the metadata-provider emitter produces a trim/AOT-clean <c>IXamlTypeMetadataProvider</c>
/// for a closed type set, shaped like <c>HandBuiltMetadata</c>, that <b>compiles</b> against the real
/// framework assemblies (proving the codegen is valid, symbol-correct C# — the right typeof/field/converter
/// references). The runtime dual-run drift gate (vs <c>ReflectionXamlMetadata</c>) is WS-X4.7.
/// </summary>
public class MetadataProviderEmitterTests
{
    private const string Ui = "https://cursorial.dev/ui";

    private static (string Source, IReadOnlyList<Diagnostic> Errors) EmitFor(params string[] localNames)
    {
        var compilation = GeneratorHarness.ReferencedCompilation();
        var resolver = new XamlSymbolResolver(compilation);
        var types = localNames.Select(n => resolver.Resolve(Ui, n, out _)!).Where(t => t is not null).ToList();
        var source = new MetadataProviderEmitter(compilation).Emit(types)!;
        return (source, GeneratorHarness.CompileErrors(source));
    }

    [Fact] // load-time TYPE REFERENCES join the closed set: x:Type, TargetType/DataType, dotted
    public void ClosedSet_CollectsLoadTimeTypeReferences() // Setter owners, selector tokens (the LayerModel/ListBoxItem bug)
    {
        var xaml = """
                   <UserControl xmlns="https://cursorial.dev/ui" xmlns:x="https://cursorial.dev/xaml"
                                xmlns:vm="clr-namespace:GenApp.ViewModels;assembly=GeneratorTestAssembly">
                       <UserControl.Styles>
                           <Style TargetType="ListBoxItem" Selector="ListBoxItem:is(ContentControl)">
                               <Setter Property="Control.Template" Value="{x:Null}"/>
                           </Style>
                       </UserControl.Styles>
                       <ListBox>
                           <ListBox.ItemTemplate>
                               <DataTemplate DataType="{x:Type vm:ProbeModel}"><TextBlock/></DataTemplate>
                           </ListBox.ItemTemplate>
                       </ListBox>
                   </UserControl>
                   """;

        var names = ClosedTypeSet.CollectTypeReferenceNames(xaml);
        var locals = names.Select(n => n.LocalName).ToList();

        Assert.Contains("ListBoxItem", locals);      // TargetType + selector token
        Assert.Contains("ContentControl", locals);   // :is() argument
        Assert.Contains("Control", locals);          // dotted Setter Property owner
        Assert.Contains("ProbeModel", locals);       // {x:Type vm:…} argument
        Assert.Contains(names, n => n is { LocalName: "ProbeModel", Namespace: "clr-namespace:GenApp.ViewModels;assembly=GeneratorTestAssembly" });
        Assert.Contains("ListBox", locals) /* attached/property-element owner */ ;
    }

    [Fact]
    public void Emits_CompilableProvider_ForControlTree()
    {
        var (source, errors) = EmitFor("StackPanel", "Button", "Border");

        Assert.Empty(errors); // the generated provider is valid, symbol-correct C#

        // Structure: the provider, the assembly registration, and the per-type bakes.
        Assert.Contains("__GeneratedXamlMetadata", source);
        Assert.Contains("XamlMetadataProvider(typeof(", source);
        Assert.Contains("typeof(global::Cursorial.UI.Controls.Button)", source);
        Assert.Contains("typeof(global::Cursorial.UI.Controls.StackPanel)", source);
        Assert.Contains("typeof(global::Cursorial.UI.Controls.Border)", source);
    }

    [Fact]
    public void Bakes_RegisteredUIProperty_AsFieldReference()
    {
        var (source, errors) = EmitFor("Button");
        Assert.Empty(errors);

        // Width is a registered StyledProperty<int?> on UIElement → property: <DeclaringType>.WidthProperty
        Assert.Contains("WidthProperty", source);
        // Content is ContentControl.ContentProperty (the content property).
        Assert.Contains("ContentProperty", source);
        Assert.Contains("contentProperty: \"Content\"", source);
        // The converter is a runtime XamlConverters.For(...) call (zero converter-drift vs reflection).
        Assert.Contains("XamlConverters.For(typeof(", source);
    }

    [Fact]
    public void Marks_PanelContentCollection()
    {
        var (source, errors) = EmitFor("StackPanel");
        Assert.Empty(errors);
        Assert.Contains("contentProperty: \"Children\"", source);
        Assert.Contains("isCollection: true", source);
    }

    [Fact] // broader coverage: an ITemplateContent-typed content member bakes IsDeferredContent (templates)
    public void Bakes_DeferredContent_ForTemplateContentMember()
    {
        var (source, errors) = EmitFor("ControlTemplate");
        Assert.Empty(errors);
        Assert.Contains("IsDeferredContent = true", source);
    }

    [Fact] // broader coverage: a ResourceDictionary bakes the keyed dictionary-fill (addDictionaryItem + key type)
    public void Bakes_DictionaryFill_ForResourceDictionary()
    {
        var (source, errors) = EmitFor("ResourceDictionary");
        Assert.Empty(errors);
        Assert.Contains("isCollection: true", source);
        Assert.Contains("addDictionaryItem:", source);
        Assert.Contains("dictionaryKeyType: typeof(object)", source);
        Assert.Contains(".Add(k, v)", source);
    }

    [Fact]
    public void EmptySet_EmitsNothing()
    {
        var compilation = GeneratorHarness.ReferencedCompilation();
        var source = new MetadataProviderEmitter(compilation).Emit(System.Array.Empty<INamedTypeSymbol>());
        Assert.Null(source);
    }

    [Fact] // regression: an init-only CLR property (SolidColorBrush.Color) bakes a reflection setter, so the
    // provider COMPILES — a compiled `t.Color = v` would be CS8852 (the bug the XAML themes surfaced).
    public void Bakes_InitOnlyClrProperty_ViaReflection_AndCompiles()
    {
        var (source, errors) = EmitFor("SolidColorBrush");
        Assert.Empty(errors);                                  // would be CS8852 if it emitted `((SolidColorBrush)t).Color = ...`
        Assert.Contains("GetProperty(\"Color\")", source);     // the init-only setter goes through reflection
    }

    [Fact] // the provider is advertised via [assembly: XamlMetadataProvider] ONLY — pull metadata for the
    // loader's entry-assembly discovery. Never a [ModuleInitializer]: merely loading an assembly (which
    // designer/test hosts do constantly) must not repoint the process-wide default provider.
    public void Emit_AdvertisesAssemblyAttribute_NeverModuleInitializer()
    {
        var compilation = GeneratorHarness.ReferencedCompilation();
        var resolver = new XamlSymbolResolver(compilation);
        var types = new[] { resolver.Resolve(Ui, "Button", out _)! }.ToList();
        var source = new MetadataProviderEmitter(compilation).Emit(types)!;

        Assert.DoesNotContain("ModuleInitializer", source);
        Assert.Contains("[assembly:", source);
        Assert.Contains("XamlMetadataProvider(typeof(Cursorial.UI.Xaml.Generated.__GeneratedXamlMetadata))", source);
        Assert.Empty(GeneratorHarness.CompileErrors(source));    // and is valid C#
    }

    [Fact] // a member (or its type) carrying [TypeConverter]/[ValueSerializer] emits a runtime ForMember(...) call —
    // the SAME path the reflection provider uses (zero drift, no baking accessibility traps); a plain member bakes
    // the pure, reflection-free For(typeof(T)) ladder (the AOT-clean common path).
    public void Emits_ForMember_ForAttributedMembers_PureFor_ForPlain()
    {
        const string app = @"
using Cursorial.Markup; using Cursorial.UI.Xaml;
namespace App {
  public sealed class Doubler : ITypeConverter { public bool IsContextFree => true; public object? ConvertFromString(string t, in XamlValueContext c) => int.Parse(t) * 2; }
  public sealed class Widget {
    [TypeConverter(typeof(Doubler))] public int Converted { get; set; }
    public int Plain { get; set; }
  }
}";

        var compilation = GeneratorHarness.ReferencedCompilation()
            .AddSyntaxTrees(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(app));
        var widget = compilation.GetTypeByMetadataName("App.Widget")!;
        var source = new MetadataProviderEmitter(compilation).Emit(new[] { widget })!;

        // The attributed member → a runtime ForMember over the reflected property (NOT a baked `new Doubler()`).
        Assert.Contains("XamlConverters.ForMember(typeof(global::App.Widget).GetProperty(\"Converted\"", source);
        Assert.DoesNotContain("new global::App.Doubler()", source);
        // The plain member → the pure ladder (reflection-free, AOT-clean).
        Assert.Contains("XamlConverters.For(typeof(int))", source);

        // And the whole thing compiles.
        using var ms = new System.IO.MemoryStream();
        var result = compilation.AddSyntaxTrees(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source)).Emit(ms);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact] // audit drift: a NULLABLE member whose underlying enum carries a Cursorial [TypeConverter] must be flagged
    // (emit ForMember) — TypeHasMarkupConverterAttribute unwraps Nullable<T>, mirroring the reflection ForMember.
    public void Flags_NullableMemberOfAttributedEnum_ForMember()
    {
        const string app = @"
using Cursorial.Markup; using Cursorial.UI.Xaml;
namespace App {
  public sealed class StarConv : ITypeConverter { public bool IsContextFree => true; public object? ConvertFromString(string t, in XamlValueContext c) => Stars.Two; }
  [TypeConverter(typeof(StarConv))] public enum Stars { Zero, One, Two }
  public sealed class Widget { public Stars? Rating { get; set; } }
}";
        var compilation = GeneratorHarness.ReferencedCompilation()
            .AddSyntaxTrees(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(app));
        var source = new MetadataProviderEmitter(compilation).Emit(new[] { compilation.GetTypeByMetadataName("App.Widget")! })!;

        // Flagged because Stars? unwraps to Stars, which carries the attribute (was For(typeof(Stars?)) → drift).
        Assert.Contains("XamlConverters.ForMember(typeof(global::App.Widget).GetProperty(\"Rating\"", source);
    }

    [Fact] // audit drift: a member OVERRIDING a base virtual that carries a Cursorial [TypeConverter] (not redeclared)
    // must be flagged — MemberHasConverterAttribute walks the OverriddenProperty chain (reflection uses inherit:true).
    public void Flags_OverriddenMember_WithInheritedConverter_ForMember()
    {
        const string app = @"
using Cursorial.Markup; using Cursorial.UI.Xaml;
namespace App {
  public sealed class Doubler : ITypeConverter { public bool IsContextFree => true; public object? ConvertFromString(string t, in XamlValueContext c) => int.Parse(t) * 2; }
  public class BaseW { [TypeConverter(typeof(Doubler))] public virtual int X { get; set; } }
  public sealed class DerivedW : BaseW { public override int X { get; set; } }
}";
        var compilation = GeneratorHarness.ReferencedCompilation()
            .AddSyntaxTrees(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(app));
        var source = new MetadataProviderEmitter(compilation).Emit(new[] { compilation.GetTypeByMetadataName("App.DerivedW")! })!;

        Assert.Contains("XamlConverters.ForMember(typeof(global::App.DerivedW).GetProperty(\"X\"", source);
    }
}
