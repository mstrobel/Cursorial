using Cursorial.UI.Xaml.Generator;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// WS-X4.3 — the build-time symbol resolver maps a XAML <c>(xmlns, localName)</c> to an
/// <c>INamedTypeSymbol</c> against the Roslyn compilation, mirroring the loader's <c>XamlSchemaContext</c>
/// default map (the <c>ui</c> URI probes Cursorial.UI/.Controls/.Data/Drawing.Media/.Themes) and decoding
/// the <c>using:</c>/<c>clr-namespace:</c> forms — without loading Cursorial.UI into the compiler.
/// </summary>
public class XamlSymbolResolverTests
{
    private const string Ui = "https://cursorial.dev/ui";

    private static XamlSymbolResolver Resolver() => new(GeneratorHarness.ReferencedCompilation());

    [Fact]
    public void Resolves_ControlsType_FromDefaultUiNamespace()
    {
        var resolver = Resolver();

        var button = resolver.Resolve(Ui, "Button", out var ambiguous);
        Assert.Null(ambiguous);
        Assert.NotNull(button);
        Assert.Equal("Cursorial.UI.Controls.Button", button!.ToDisplayString());
    }

    [Fact]
    public void Resolves_TypesAcrossTheProbedNamespaces()
    {
        var resolver = Resolver();

        Assert.Equal("Cursorial.UI.Controls.StackPanel", resolver.Resolve(Ui, "StackPanel", out _)!.ToDisplayString());
        Assert.Equal("Cursorial.UI.Controls.Border", resolver.Resolve(Ui, "Border", out _)!.ToDisplayString());
        // SolidColorBrush lives in Cursorial.Drawing.Media — also probed by the ui xmlns.
        Assert.Equal("Cursorial.Rendering.Media.SolidColorBrush", resolver.Resolve(Ui, "SolidColorBrush", out _)!.ToDisplayString());
        Assert.Equal("Cursorial.Drawing.Media.Pen", resolver.Resolve(Ui, "Pen", out _)!.ToDisplayString());
    }

    [Fact]
    public void Miss_ReturnsNull()
    {
        var resolver = Resolver();
        Assert.Null(resolver.Resolve(Ui, "NoSuchControl", out var ambiguous));
        Assert.Null(ambiguous);
    }

    [Fact]
    public void Decodes_ClrNamespaceForm()
    {
        var resolver = Resolver();
        var button = resolver.Resolve("clr-namespace:Cursorial.UI.Controls;assembly=Cursorial.UI", "Button", out _);
        Assert.NotNull(button);
        Assert.Equal("Cursorial.UI.Controls.Button", button!.ToDisplayString());
    }

    [Fact]
    public void Decodes_UsingForm()
    {
        var resolver = Resolver();
        var button = resolver.Resolve("using:Cursorial.UI.Controls", "Button", out _);
        Assert.NotNull(button);
    }

    [Fact] // #2 — a CUSTOM xmlns URI declared via [assembly: XmlnsDefinition] on a compilation assembly resolves the
           // same as the default one (the URI-keyed map is discovered from any [XmlnsDefinition], not just the ui URI).
    public void Resolves_CustomUri_FromAssemblyDefinition()
    {
        const string source = """
            using Cursorial.Markup;
            [assembly: XmlnsDefinition("https://cursorial.test/gen-widgets", "GenWidgets")]
            namespace GenWidgets { public sealed class GenWidget; }
            """;
        var compilation = GeneratorHarness.ReferencedCompilation().AddSyntaxTrees(CSharpSyntaxTree.ParseText(source));
        var resolver = new XamlSymbolResolver(compilation);

        var widget = resolver.Resolve("https://cursorial.test/gen-widgets", "GenWidget", out var ambiguous);
        Assert.Null(ambiguous);
        Assert.NotNull(widget);
        Assert.Equal("GenWidgets.GenWidget", widget!.ToDisplayString());

        // The default ui URI keeps resolving alongside the custom one.
        Assert.Equal("Cursorial.UI.Controls.Button", resolver.Resolve(Ui, "Button", out _)!.ToDisplayString());
    }
}
