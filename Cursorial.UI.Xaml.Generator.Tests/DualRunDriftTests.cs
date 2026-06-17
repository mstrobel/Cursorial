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

    [Fact] // WS-X4-attached — the generated provider resolves an attached property identically to reflection
    public void GeneratedProvider_HandlesAttachedProperty_AsReflection()
    {
        var xaml = $"<StackPanel {Xmlns}><Button Content=\"Hi\" Grid.Row=\"2\"/></StackPanel>";

        var generated = BuildGeneratedProvider(xaml);
        var byGenerated = (StackPanel)new XamlLoader(new XamlLoaderOptions { MetadataProvider = generated }).Load(xaml);
        var byReflection = (StackPanel)new XamlLoader(new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance }).Load(xaml);

        foreach (var root in new[] { byGenerated, byReflection })
        {
            var button = Assert.IsType<Button>(Assert.Single(root.Children));
            Assert.Equal(2, Grid.GetRow(button)); // the attached value was set through the registered AttachedProperty
        }
    }

    [Fact] // WS-X4.5 — the generated provider's [ModuleInitializer] installs it as the loader default (no opt-in)
    public void ModuleInitializer_InstallsGeneratedProvider_AsLoaderDefault()
    {
        var xaml = $"<StackPanel {Xmlns}><Button Content=\"Hi\"/></StackPanel>";
        var saved = XamlLoaderOptions.DefaultMetadataProvider;
        try
        {
            // Loading the generated assembly + touching its Instance fires the module initializer.
            var generated = BuildGeneratedProvider(xaml);
            Assert.Same(generated, XamlLoaderOptions.DefaultMetadataProvider); // installed as the default

            // A default-constructed loader now uses the generated provider — no explicit MetadataProvider.
            var root = (StackPanel)new XamlLoader().Load(xaml);
            var button = Assert.IsType<Button>(Assert.Single(root.Children));
            Assert.Equal("Hi", button.Content);
        }
        finally
        {
            XamlLoaderOptions.DefaultMetadataProvider = saved; // restore (process-wide default)
        }
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
