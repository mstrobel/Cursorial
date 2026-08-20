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

    [Fact] // a type-qualified {TemplateBinding Owner.Prop} / {TemplateBinding ns:Owner.Prop} joins the
    // closed set: the loader resolves the OWNER type (via the metadata provider) to find the source
    // UIProperty, so the generated provider must bake it. A bare {TemplateBinding Prop} has no owner.
    public void Collects_TemplateBindingOwner_FromQualifiedSource()
    {
        const string xaml =
            "<UserControl xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" " +
            "xmlns:vm=\"clr-namespace:GenApp.ViewModels;assembly=GeneratorTestAssembly\">" +
            "<Border Background=\"{TemplateBinding Control.Background}\"/>" +
            "<Border Tag=\"{TemplateBinding vm:ProbeModel.Value}\"/>" +
            "<Border Padding=\"{TemplateBinding (Control.Padding)}\"/>" +
            "<Border Width=\"{TemplateBinding Width}\"/>" +
            "</UserControl>";

        var locals = ClosedTypeSet.CollectTypeReferenceNames(xaml).Select(n => n.LocalName).ToHashSet();

        Assert.Contains("Control", locals);      // Owner.Prop
        Assert.Contains("ProbeModel", locals);   // ns:Owner.Prop (prefix carried through)
        Assert.Contains(ClosedTypeSet.CollectTypeReferenceNames(xaml),
            n => n is { LocalName: "ProbeModel", Namespace: "clr-namespace:GenApp.ViewModels;assembly=GeneratorTestAssembly" });
        Assert.DoesNotContain("Width", locals);  // bare {TemplateBinding Prop} has no owner token
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

    [Fact] // {x:Static} resolves a CHAIN — a static member then INSTANCE member accesses (A.B.C.D); it bakes the full path
    public void ResolveStaticExpr_MemberChain_BakesFullPath()
    {
        var resolver = new XamlSymbolResolver(GeneratorHarness.ReferencedCompilation());

        Assert.NotNull(ResolveStaticOf(resolver, "Brushes.Red")); // 2-part baseline — Brushes resolves in the generator

        // Brushes.Red (static field) → .Color (instance property): the instance hop bakes as one member-access expr.
        var expr = ResolveStaticOf(resolver, "Brushes.Red.Color");
        Assert.NotNull(expr);
        Assert.EndsWith(".Brushes.Red.Color", expr);

        // A broken hop ANYWHERE in the chain resolves to null — no partial bake (would drift from the loader).
        Assert.Null(ResolveStaticOf(resolver, "Brushes.Red.Nonexistent"));
        Assert.Null(ResolveStaticOf(resolver, "Brushes.Nonexistent.Color"));
    }

    [Fact] // the shared XamlPathParser ladder: int / enum / string indexer segments in an x:Static chain
    public void ResolveStaticExpr_IndexerSegments_BakeTypedKeys()
    {
        const string statics = @"
using System.Collections.Generic;
namespace TestStatics
{
    public enum Kind { First, Second }
    public sealed class KindMap { public string this[Kind kind] => kind.ToString(); }
    public static class Host
    {
        public static List<string> Items { get; } = new() { ""zero"", ""one"" };
        public static Dictionary<string, int> Map { get; } = new() { [""a""] = 1 };
        public static KindMap Kinds { get; } = new();
    }
}";
        var compilation = GeneratorHarness.ReferencedCompilation()
            .AddSyntaxTrees(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(statics));
        var resolver = new XamlSymbolResolver(compilation);
        const string ns = "using:TestStatics";

        // int key -> Item[int]; the baked expression indexes with the literal.
        Assert.EndsWith("Host.Items[1]", ClosedTypeSet.ResolveStaticExpr(resolver, ns, "Host.Items[1]"));

        // string key (unquoted, non-int) -> Item[string]; baked quoted.
        Assert.EndsWith("Host.Map[\"a\"]", ClosedTypeSet.ResolveStaticExpr(resolver, ns, "Host.Map[a]"));

        // enum key -> Item[Kind], case-insensitive last-segment constant match, baked as the enum member.
        var enumExpr = ClosedTypeSet.ResolveStaticExpr(resolver, ns, "Host.Kinds[second]");
        Assert.NotNull(enumExpr);
        Assert.EndsWith("Host.Kinds[global::TestStatics.Kind.Second]", enumExpr);

        // an indexer chain continues with instance members; a key with no matching indexer is a clean null.
        Assert.EndsWith("Host.Items[0].Length", ClosedTypeSet.ResolveStaticExpr(resolver, ns, "Host.Items[0].Length"));
        Assert.Null(ClosedTypeSet.ResolveStaticExpr(resolver, ns, "Host.Items[not-an-int]")); // List<string> has no string indexer
    }

    private static string? ResolveStaticOf(XamlSymbolResolver resolver, string path)
        => ClosedTypeSet.ResolveStaticExpr(resolver, XamlSymbolResolver.CursorialUiNamespace, path);
}
