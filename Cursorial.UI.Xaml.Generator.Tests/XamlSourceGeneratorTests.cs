using Microsoft.CodeAnalysis;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// WS-X4.1/X4.4 — the generator runs the source-linked frontend parser over each <c>CursorialXaml</c>
/// file and (a) round-trips a marker proving analyzer-load + the item-metadata filter, and (b) surfaces
/// the parser's <c>CUR1xxx</c> syntax diagnostics as Roslyn build diagnostics at the <c>.xaml</c>
/// location. The semantic (<c>CUR2xxx</c>) band + the codegen outputs join once WS-X4.3's symbol-backed
/// metadata provider lands.
/// </summary>
public class XamlSourceGeneratorTests
{
    [Fact]
    public void Generator_EmitsMarker_ForCursorialXamlFile()
    {
        var result = GeneratorHarness.Run(("View.xaml",
            "<DockPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" x:Class=\"App.View\"/>"));

        var generated = Assert.Single(result.Results);
        // The generator also emits the per-compilation metadata provider (WS-X4.5); pick the View output.
        var tree = generated.GeneratedSources.Single(s => s.HintName.Contains("View"));
        Assert.Contains("View", tree.HintName);
        Assert.Contains("source:", tree.SourceText.ToString());
        Assert.Contains("App.View", tree.SourceText.ToString()); // x:Class captured

        // Valid syntax ⇒ no CUR1xxx (parse-band) errors. (Type-resolution CUR2xxx is deferred to X4.3,
        // so it must not leak as a spurious error here.)
        Assert.DoesNotContain(result.Diagnostics, d => d.Id.StartsWith("CUR1"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Id.StartsWith("CUR2"));
    }

    [Fact]
    public void Generator_IgnoresUnflaggedFiles()
    {
        var result = GeneratorHarness.Run(); // no CursorialXaml files
        Assert.Empty(result.Results.SelectMany(r => r.GeneratedSources));
    }

    [Fact]
    public void Generator_EmitsOnePerFile()
    {
        var result = GeneratorHarness.Run(
            ("Alpha.xaml", "<Border/>"),
            ("Beta.xaml", "<StackPanel/>"));

        var sources = result.Results.SelectMany(r => r.GeneratedSources).ToList();
        Assert.Equal(2, sources.Count);
        Assert.Contains(sources, s => s.HintName.Contains("Alpha"));
        Assert.Contains(sources, s => s.HintName.Contains("Beta"));
    }

    [Fact] // WS-X4.5 — the generator emits ONE metadata provider per compilation, over the union closed set
    public void Generator_EmitsMetadataProvider_ForCompilation()
    {
        var result = GeneratorHarness.Run(
            ("A.xaml", "<StackPanel xmlns=\"https://cursorial.dev/ui\"><Button/></StackPanel>"),
            ("B.xaml", "<Border xmlns=\"https://cursorial.dev/ui\"/>"));

        var providers = result.Results
            .SelectMany(r => r.GeneratedSources)
            .Where(s => s.HintName.Contains("__GeneratedXamlMetadata"))
            .ToList();

        var provider = Assert.Single(providers); // exactly one per compilation (not per file)
        var src = provider.SourceText.ToString();
        Assert.Contains("__GeneratedXamlMetadata", src);
        Assert.DoesNotContain("ModuleInitializer", src); // pull-discovered via the attribute, never auto-installed
        Assert.Contains("[assembly:", src);               // ...which the loader's entry-assembly discovery consults
        // The union of both files' types is baked.
        Assert.Contains("typeof(global::Cursorial.UI.Controls.StackPanel)", src);
        Assert.Contains("typeof(global::Cursorial.UI.Controls.Button)", src);
        Assert.Contains("typeof(global::Cursorial.UI.Controls.Border)", src);
    }

    [Fact] // WS-X4.4 semantic band — an unknown member surfaces CUR2102 (member-not-found) at the .xaml location
    public void Generator_ReportsSemanticError_AsRoslynDiagnostic()
    {
        var result = GeneratorHarness.Run(("Bad.xaml",
            "<Button xmlns=\"https://cursorial.dev/ui\" Frobnicate=\"nope\"/>"));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "CUR2102");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        var lineSpan = diagnostic.Location.GetLineSpan();
        Assert.Contains("Bad.xaml", lineSpan.Path);
    }

    [Fact] // an x:Class code-behind may leave its base type to the GENERATED partial (the one-place edit);
    // the semantic band must still see INHERITED members on that class when a SIBLING document sets them
    // (<v:EditorsPane Width="…">). The generator can't see its own output, so the analysis compilation is
    // augmented with the would-be generated base declarations — without that, Width is a false CUR2102.
    public void Generator_ResolvesInheritedMembers_OnSiblingXClassTypes()
    {
        var result = GeneratorHarness.Run(
            [
                ("EditorsPane.xaml",
                 "<UserControl xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" " +
                 "x:Class=\"Test.Views.EditorsPane\"><Border/></UserControl>"),
                ("MainView.xaml",
                 "<StackPanel xmlns=\"https://cursorial.dev/ui\" " +
                 "xmlns:v=\"clr-namespace:Test.Views;assembly=GeneratorTestAssembly\">" +
                 "<v:EditorsPane Width=\"30\"/></StackPanel>"),
            ],
            sources: ["namespace Test.Views { public partial class EditorsPane { } }"]);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id.StartsWith("CUR2"));
        // And the emitted provider bakes the inherited member table, not an empty one.
        var provider = result.Results.SelectMany(r => r.GeneratedSources).Single(s => s.HintName.Contains("__GeneratedXamlMetadata"));
        Assert.Contains("Width", provider.SourceText.ToString());
    }

    [Fact] // {Icon Text='…'} bakes IconExtension into the closed set — suffix-first, parser parity —
    // and never the sister Icon CONTROL (the extension-usage must not drag in a same-named type);
    // {Binding} has no BindingExtension, so the bare fallback bakes Binding.
    public void Generator_BakesMarkupExtensions_SuffixFirst()
    {
        var result = GeneratorHarness.Run(("Sink.xaml",
            "<StackPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\">" +
            "<Border DataContext=\"{Icon Text='01'}\" Width=\"{Binding W}\"/></StackPanel>"));

        var provider = result.Results.SelectMany(r => r.GeneratedSources)
            .Single(s => s.HintName.Contains("__GeneratedXamlMetadata"));
        var src = provider.SourceText.ToString();

        Assert.Contains("typeof(global::Cursorial.UI.Xaml.Markup.IconExtension)", src);
        Assert.DoesNotContain("typeof(global::Cursorial.UI.Controls.Icon)", src);
        Assert.Contains("typeof(global::Cursorial.UI.Data.Binding)", src);
    }


    [Fact] // a PROJECT-LOCAL extension ({v:EnumItemsSource EnumType={x:Type JunctionMode}}) must bake
    // BOTH the prefixed extension type and the nested x:Type argument (the Cursorial.Samples shape).
    public void Generator_BakesPrefixedProjectExtensions_AndNestedTypeArguments()
    {
        var result = GeneratorHarness.Run(
            [
                ("Tools.xaml",
                 "<StackPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" " +
                 "xmlns:v=\"clr-namespace:Test.Ext;assembly=GeneratorTestAssembly\">" +
                 "<Border DataContext=\"{v:Probe EnumType={x:Type JunctionMode}}\"/></StackPanel>"),
            ],
            sources:
            [
                """
                namespace Test.Ext
                {
                    public sealed class ProbeExtension : Cursorial.UI.Xaml.MarkupExtension
                    {
                        public System.Type? EnumType { get; set; }
                        public override object ProvideValue(System.IServiceProvider serviceProvider) => EnumType!;
                    }
                }
                """,
            ]);

        var provider = result.Results.SelectMany(r => r.GeneratedSources)
            .Single(s => s.HintName.Contains("__GeneratedXamlMetadata"));
        var src = provider.SourceText.ToString();

        Assert.Contains("typeof(global::Test.Ext.ProbeExtension)", src);
        Assert.Contains("typeof(global::Cursorial.Drawing.Media.JunctionMode)", src);
    }


    [Fact] // the EXACT Cursorial.Samples ToolsPane shape, including its malformed root tag (the root
    // closes at Width="10"> and a stray d:DesignHeight="40" is left as TEXT) — the sweeps and the
    // provider emission must survive a document that is mid-edit broken.
    public void Generator_BakesExtensions_DespiteMalformedSiblingMarkup()
    {
        var result = GeneratorHarness.Run(
            [
                ("ToolsPane.xaml",
                 "<UserControl xmlns=\"https://cursorial.dev/ui\"\n" +
                 "             xmlns:x=\"https://cursorial.dev/xaml\"\n" +
                 "             xmlns:d=\"https://cursorial.dev/xaml/design\"\n" +
                 "             xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"\n" +
                 "             xmlns:v=\"clr-namespace:Test.Views;assembly=GeneratorTestAssembly\"\n" +
                 "             mc:Ignorable=\"d\"\n" +
                 "             x:Class=\"Test.Views.ToolsPane\"\n" +
                 "             Width=\"10\">\n" +
                 "             d:DesignHeight=\"40\"\n" +
                 "  <Grid>\n" +
                 "    <Border DataContext=\"{v:EnumItemsSource EnumType={x:Type JunctionMode}}\"/>\n" +
                 "  </Grid>\n" +
                 "</UserControl>"),
            ],
            sources:
            [
                "namespace Test.Views { public partial class ToolsPane { } }",
                """
                namespace Test.Views
                {
                    public sealed class EnumItemsSourceExtension : Cursorial.UI.Xaml.MarkupExtension
                    {
                        public System.Type? EnumType { get; set; }
                        public override object ProvideValue(System.IServiceProvider serviceProvider) => EnumType!;
                    }
                }
                """,
            ]);

        var provider = result.Results.SelectMany(r => r.GeneratedSources)
            .Single(s => s.HintName.Contains("__GeneratedXamlMetadata"));
        var src = provider.SourceText.ToString();

        Assert.Contains("typeof(global::Test.Views.EnumItemsSourceExtension)", src);
        Assert.Contains("typeof(global::Cursorial.Drawing.Media.JunctionMode)", src);
    }

    [Fact] // a duplicate x:Key in a resource dictionary surfaces CUR2305 as a Roslyn WARNING (not an error,
    // so codegen still proceeds) at the later entry's location.
    public void Generator_ReportsDuplicateResourceKey_AsWarning()
    {
        var result = GeneratorHarness.Run(("Theme.xaml",
            "<ResourceDictionary xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\">" +
            "<Style x:Key=\"Accent\" TargetType=\"Button\"/><Style x:Key=\"Accent\" TargetType=\"Button\"/>" +
            "</ResourceDictionary>"));

        var dup = Assert.Single(result.Diagnostics, d => d.Id == "CUR2305");
        Assert.Equal(DiagnosticSeverity.Warning, dup.Severity);
        Assert.Contains("Theme.xaml", dup.Location.GetLineSpan().Path);
    }

    [Fact] // an unknown element type surfaces CUR2002 (type-not-found)
    public void Generator_ReportsUnknownType_AsRoslynDiagnostic()
    {
        var result = GeneratorHarness.Run(("Bad.xaml", "<Buttn xmlns=\"https://cursorial.dev/ui\"/>"));
        Assert.Contains(result.Diagnostics, d => d.Id == "CUR2002");
    }

    [Fact] // WS-X4.4 — a malformed document surfaces a CUR1xxx parse diagnostic at the .xaml location
    public void Generator_ReportsSyntaxError_AsRoslynDiagnostic()
    {
        // Mismatched end tag — a parse-band (CUR1xxx) error.
        var result = GeneratorHarness.Run(("Bad.xaml", "<Border></Mismatch>"));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id.StartsWith("CUR1"));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        // The location points into the Bad.xaml file (1-based line/col → a real position).
        var lineSpan = diagnostic.Location.GetLineSpan();
        Assert.Contains("Bad.xaml", lineSpan.Path);
        Assert.True(lineSpan.StartLinePosition.Line >= 0);
    }
}
