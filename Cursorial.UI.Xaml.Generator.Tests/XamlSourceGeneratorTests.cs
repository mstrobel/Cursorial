using System.Linq;

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
        Assert.Contains("ModuleInitializer", src); // installs itself as the loader default (AOT-clean)
        // The union of both files' types is baked.
        Assert.Contains("typeof(global::Cursorial.UI.Controls.StackPanel)", src);
        Assert.Contains("typeof(global::Cursorial.UI.Controls.Button)", src);
        Assert.Contains("typeof(global::Cursorial.UI.Controls.Border)", src);
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
