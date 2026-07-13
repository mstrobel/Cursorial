using System.Linq;

using Cursorial.UI.Xaml.Generator;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// WS-X4.5 — the closed-type-set collector records every element name the parser visits (including
/// nested children, under error recovery), so the generated metadata provider can cover exactly the
/// types a document references.
/// </summary>
public class ClosedTypeSetTests
{
    private const string Ui = "https://cursorial.dev/ui";

    [Fact] // markup-extension names in attribute position never reach the RECORDING parse (an
    // unresolved element type never descends into attribute values), so a text sweep collects them
    // RAW; the provider emission then resolves suffix-first, parser-parity ({Icon} → IconExtension,
    // never the sister Icon control — see Generator_BakesMarkupExtensions_SuffixFirst).
    public void Collects_MarkupExtensionNames_FromAttributeValues()
    {
        const string xaml =
            "<StackPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\">" +
            "  <Border DataContext=\"{Icon Text='01'}\" Width=\"{Binding W}\"/>" +
            "</StackPanel>";

        var names = ClosedTypeSet.CollectMarkupExtensionNames(xaml)
            .Where(n => n.Namespace == Ui)
            .Select(n => n.LocalName)
            .ToHashSet();

        Assert.Contains("Icon", names);    // raw — resolution maps it to IconExtension
        Assert.Contains("Binding", names); // raw — no BindingExtension exists; bare fallback bakes Binding
    }

    [Fact]
    public void Collects_NestedElementNames()
    {
        const string xaml =
            "<StackPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\">" +
            "  <Button/>" +
            "  <Border><TextBlock/></Border>" +
            "</StackPanel>";

        var names = ClosedTypeSet.CollectElementNames(xaml);
        var local = names.Where(n => n.Namespace == Ui).Select(n => n.LocalName).ToHashSet();

        Assert.Contains("StackPanel", local);
        Assert.Contains("Button", local);
        Assert.Contains("Border", local);
        Assert.Contains("TextBlock", local); // the deeply-nested child is captured too
    }

    [Fact]
    public void Deduplicates_RepeatedNames()
    {
        const string xaml =
            "<StackPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\">" +
            "  <Button/><Button/><Button/>" +
            "</StackPanel>";

        var names = ClosedTypeSet.CollectElementNames(xaml);
        Assert.Equal(1, names.Count(n => n.LocalName == "Button"));
    }

    // ── x:Static scan + resolution (P1-REVIEW fixes C/D) ──────────────────────────────────────────────

    [Theory] // Fix D — the scan matches the markup-extension grammar: quotes stripped, '\' unescaped, ',' stops
    [InlineData("{x:Static Brushes.Red}", "Brushes.Red")]
    [InlineData("{x:Static 'Brushes.Red'}", "Brushes.Red")]          // quoted positional → quotes stripped
    [InlineData(@"{x:Static Foo\.Bar}", "Foo.Bar")]                   // '\' escape resolved
    [InlineData("{x:Static Colors.Red, x}", "Colors.Red")]            // stops at the ',' separator (no trailing comma)
    [InlineData("{Binding V, Converter={x:Static C.D}}", "C.D")]      // nested: the inner x:Static's '}' precedes the comma
    public void CollectStaticPaths_MatchesRuntimeGrammar(string xaml, string expected)
    {
        var paths = ClosedTypeSet.CollectStaticPaths(xaml);
        Assert.Contains(expected, paths);
    }

    [Fact] // Fix C — an INHERITED static is NOT baked (reflection uses no FlattenHierarchy → would miss it → drift)
    public void ResolveStaticExpr_InheritedStatic_NotBaked()
    {
        var resolver = new XamlSymbolResolver(GeneratorHarness.ReferencedCompilation());

        // OpacityProperty is declared on UIElement and inherited by Button (no redeclaration).
        Assert.NotNull(ResolveStaticOf(resolver, "UIElement.OpacityProperty"));   // declared-on → baked
        Assert.Null(ResolveStaticOf(resolver, "Button.OpacityProperty"));         // inherited → NOT baked (matches reflection)
    }

    private static string? ResolveStaticOf(XamlSymbolResolver resolver, string path)
        => ClosedTypeSet.ResolveStaticExpr(resolver, path);
}
