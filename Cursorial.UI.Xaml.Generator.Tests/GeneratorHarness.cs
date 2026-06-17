using System.Collections.Immutable;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>Drives <c>XamlSourceGenerator</c> over in-memory <c>CursorialXaml</c> files via a Roslyn
/// <see cref="CSharpGeneratorDriver"/> — the generator-test substrate (no MSBuild needed).</summary>
internal static class GeneratorHarness
{
    /// <summary>A Compilation referencing the runtime BCL + the Cursorial framework assemblies, so a
    /// generator (or a direct <c>XamlSymbolResolver</c>) can resolve <c>Cursorial.UI</c> symbols.</summary>
    public static CSharpCompilation ReferencedCompilation(string assemblyName = "GeneratorTestAssembly")
    {
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(System.IO.Path.PathSeparator)
            .Where(p => p.Length > 0 && p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

        var cursorial = new[]
        {
            typeof(Cursorial.UI.UIElement).Assembly.Location,          // Cursorial.UI
            typeof(Cursorial.UI.Xaml.XamlType).Assembly.Location,      // Cursorial.UI.Xaml.Frontend
        };

        var references = tpa.Concat(cursorial)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToImmutableArray();

        return CSharpCompilation.Create(
            assemblyName,
            syntaxTrees: null,
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>Runs the generator over the given (name → xaml) files, each carrying the
    /// <c>SourceItemType=CursorialXaml</c> AdditionalFiles metadata, and returns the run result.</summary>
    public static GeneratorDriverRunResult Run(params (string FileName, string Xaml)[] files)
    {
        var compilation = ReferencedCompilation();

        var additionalTexts = files
            .Select(f => (AdditionalText)new InMemoryAdditionalText(f.FileName, f.Xaml))
            .ToImmutableArray();

        var optionsProvider = new CursorialXamlOptionsProvider(additionalTexts);

        var driver = CSharpGeneratorDriver.Create(
            generators: [new Cursorial.UI.Xaml.Generator.XamlSourceGenerator().AsSourceGenerator()],
            additionalTexts: additionalTexts,
            optionsProvider: optionsProvider);

        return driver.RunGenerators(compilation).GetRunResult();
    }

    private sealed class InMemoryAdditionalText(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default)
            => SourceText.From(text, Encoding.UTF8);
    }

    // Returns SourceItemType=CursorialXaml for every supplied .xaml additional file (mirrors the package targets).
    private sealed class CursorialXamlOptionsProvider(ImmutableArray<AdditionalText> xamlFiles) : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _cursorialXaml = new FixedOptions(
            new Dictionary<string, string> { ["build_metadata.AdditionalFiles.SourceItemType"] = "CursorialXaml" });

        private readonly AnalyzerConfigOptions _empty = new FixedOptions(new Dictionary<string, string>());

        public override AnalyzerConfigOptions GlobalOptions => _empty;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile)
            => xamlFiles.Contains(textFile) ? _cursorialXaml : _empty;

        private sealed class FixedOptions(Dictionary<string, string> values) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
        }
    }
}
