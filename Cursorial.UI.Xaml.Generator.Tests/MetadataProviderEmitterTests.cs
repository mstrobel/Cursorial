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

    [Fact]
    public void EmptySet_EmitsNothing()
    {
        var compilation = GeneratorHarness.ReferencedCompilation();
        var source = new MetadataProviderEmitter(compilation).Emit(System.Array.Empty<INamedTypeSymbol>());
        Assert.Null(source);
    }
}
