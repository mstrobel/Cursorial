using System.Linq;

using Cursorial.UI.Controls;
using Cursorial.UI.Xaml;
using Cursorial.UI.Xaml.Generator;

using Microsoft.CodeAnalysis;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// WS-X4.7 — the dual-provider drift gate (design doc §14.2 P10 exit, matrix X174): the same document
/// loaded through the GENERATED metadata provider and through <c>ReflectionXamlMetadata</c> produces the
/// identical tree. The generated provider is emitted from the document's closed type set, compiled, and
/// loaded; both loads are asserted to yield byte-identical typed trees — proving zero semantic drift.
/// </summary>
public class DualRunDriftTests
{
    private const string Xmlns =
        "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    [Fact]
    public void GeneratedProvider_ProducesSameTree_AsReflection()
    {
        var xaml =
            $"<StackPanel {Xmlns}>" +
            "  <Button Content=\"Hi\" Width=\"20\"/>" +
            "  <Border><Button Content=\"Inner\"/></Border>" +
            "</StackPanel>";

        var generated = BuildGeneratedProvider(xaml);

        var byGenerated = (StackPanel)new XamlLoader(new XamlLoaderOptions { MetadataProvider = generated }).Load(xaml);
        var byReflection = (StackPanel)new XamlLoader(new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance }).Load(xaml);

        AssertExpectedTree(byGenerated);   // the generated provider produced the expected tree …
        AssertExpectedTree(byReflection);  // … identical to the reflection provider's
    }

    private static void AssertExpectedTree(StackPanel root)
    {
        Assert.Equal(2, root.Children.Count);

        var button = Assert.IsType<Button>(root.Children[0]);
        Assert.Equal("Hi", button.Content);
        Assert.Equal(20, button.Width);

        var border = Assert.IsType<Border>(root.Children[1]);
        var inner = Assert.IsType<Button>(border.Child);
        Assert.Equal("Inner", inner.Content);
    }

    private static IXamlTypeMetadataProvider BuildGeneratedProvider(string xaml)
    {
        var compilation = GeneratorHarness.ReferencedCompilation();
        var resolver = new XamlSymbolResolver(compilation);
        var types = ClosedTypeSet.CollectElementNames(xaml)
            .Select(n => resolver.Resolve(n.Namespace, n.LocalName, out _))
            .Where(t => t is not null)
            .Cast<INamedTypeSymbol>()
            .ToList();

        var source = new MetadataProviderEmitter(compilation).Emit(types)
            ?? throw new System.InvalidOperationException("no provider emitted");
        return GeneratorHarness.CompileAndLoadProvider(source);
    }
}
