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
                  .Split(Path.PathSeparator)
                  .Where(p => p.Length > 0 && p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

        var cursorial = new[]
                        {
                            typeof(Cursorial.UI.UIElement).Assembly.Location,           // Cursorial.UI
                            typeof(Cursorial.UI.Xaml.XamlType).Assembly.Location,       // Cursorial.UI.Xaml.Frontend
                            typeof(Cursorial.UI.Xaml.XamlConverters).Assembly.Location, // Cursorial.UI.Xaml (loader — XamlConverters)
                            typeof(Cursorial.Markup.ContentPropertyAttribute).Assembly.Location, // Cursorial.Shared (markup attributes)
                        };

        var references = tpa.Concat(cursorial)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Select(p => (MetadataReference) MetadataReference.CreateFromFile(p))
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
                              .Select(f => (AdditionalText) new InMemoryAdditionalText(f.FileName, f.Xaml))
                              .ToImmutableArray();

        var optionsProvider = new CursorialXamlOptionsProvider(additionalTexts);

        var driver = CSharpGeneratorDriver.Create(
            generators: [new Cursorial.UI.Xaml.Generator.XamlSourceGenerator().AsSourceGenerator()],
            additionalTexts: additionalTexts,
            optionsProvider: optionsProvider);

        return driver.RunGenerators(compilation).GetRunResult();
    }

    /// <summary>Compiles <paramref name="generatedSource"/> against the framework references and returns the
    /// error-severity diagnostics — empty means the generated provider is valid, symbol-correct C#.</summary>
    public static IReadOnlyList<Diagnostic> CompileErrors(string generatedSource)
    {
        var compilation = ReferencedCompilation("GeneratedProviderCompile").AddSyntaxTrees(CSharpSyntaxTree.ParseText(generatedSource));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        return result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    }

    /// <summary>Compiles <paramref name="generatedSource"/> to an in-memory assembly, loads it, and returns the
    /// generated <c>__GeneratedXamlMetadata.Instance</c> as an <c>IXamlTypeMetadataProvider</c> (for the dual-run).</summary>
    public static Cursorial.UI.Xaml.IXamlTypeMetadataProvider CompileAndLoadProvider(string generatedSource)
    {
        var compilation = ReferencedCompilation("GeneratedProviderAsm").AddSyntaxTrees(CSharpSyntaxTree.ParseText(generatedSource));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (!result.Success)
        {
            var errors = string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            throw new InvalidOperationException("generated provider failed to compile:\n" + errors);
        }

        var assembly = System.Reflection.Assembly.Load(ms.ToArray());

        var type = assembly.GetType("Cursorial.UI.Xaml.Generated.__GeneratedXamlMetadata") ??
                   throw new InvalidOperationException("generated provider type not found");

        var instance = type.GetField("Instance")!.GetValue(null);
        return (Cursorial.UI.Xaml.IXamlTypeMetadataProvider) instance!;
    }

    /// <summary>Runs the generator over <paramref name="codeBehindSource"/> (a hand-written code-behind syntax
    /// tree) + the given CursorialXaml files, returning the updated compilation (with generated sources) and the
    /// generator diagnostics — the substrate for the code-behind end-to-end test.</summary>
    public static (CSharpCompilation Compilation, ImmutableArray<Diagnostic> Diagnostics) RunWithCodeBehind(
        string codeBehindSource, params (string FileName, string Xaml)[] files)
        => RunWithCodeBehind(codeBehindSource, loweringFull: false, files);

    /// <summary>As <see cref="RunWithCodeBehind(string, ValueTuple{string,string}[])"/>, but with the WS-X5.5
    /// full-lowering opt-in (<c>build_property.CursorialXamlLowering=full</c>) set when <paramref name="loweringFull"/>.</summary>
    public static (CSharpCompilation Compilation, ImmutableArray<Diagnostic> Diagnostics) RunWithCodeBehind(
        string codeBehindSource, bool loweringFull, params (string FileName, string Xaml)[] files)
    {
        var compilation = ReferencedCompilation("CodeBehindHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehindSource));

        var additionalTexts = files
                              .Select(f => (AdditionalText) new InMemoryAdditionalText(f.FileName, f.Xaml))
                              .ToImmutableArray();

        var globalOptions = loweringFull
            ? new Dictionary<string, string> { ["build_property.CursorialXamlLowering"] = "full" }
            : new Dictionary<string, string>();

        var driver = CSharpGeneratorDriver.Create(
            generators: [new Cursorial.UI.Xaml.Generator.XamlSourceGenerator().AsSourceGenerator()],
            additionalTexts: additionalTexts,
            optionsProvider: new CursorialXamlOptionsProvider(additionalTexts, globalOptions));

        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics);
        return ((CSharpCompilation) updated, diagnostics);
    }

    /// <summary>Parses <paramref name="xaml"/> with the symbol-backed provider and runs the full-lowering
    /// emitter, returning the generated source (throws if no code-behind class). The standard substrate for the
    /// lowering-emitter unit tests — the view-model/types referenced by the XAML must already be in
    /// <paramref name="compilation"/> (add the code-behind syntax tree first).</summary>
    public static string LowerView(CSharpCompilation compilation, string xaml)
    {
        var document = Cursorial.UI.Xaml.XamlFrontend.Parse(xaml, new Cursorial.UI.Xaml.XamlParseOptions
        {
            MetadataProvider = new Cursorial.UI.Xaml.Generator.RoslynXamlMetadata(compilation),
            DiagnosticMode = Cursorial.UI.Xaml.XamlDiagnosticMode.CollectAll,
            FoldConstants = false,
        });

        return (Cursorial.UI.Xaml.Generator.LoweringEmitter.Emit(
                    document, "MyView.xaml", new Cursorial.UI.Xaml.Generator.XamlSymbolResolver(compilation))
                ?? throw new InvalidOperationException("no lowering emitted")).Source;
    }

    /// <summary>Compiles a compilation to an in-memory assembly and loads it (throws on compile error).</summary>
    public static System.Reflection.Assembly EmitAndLoad(Compilation compilation)
    {
        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (!result.Success)
        {
            var errors = string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            throw new InvalidOperationException("compilation failed:\n" + errors);
        }

        return System.Reflection.Assembly.Load(ms.ToArray());
    }

    private sealed class InMemoryAdditionalText(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default)
            => SourceText.From(text, Encoding.UTF8);
    }

    // Returns SourceItemType=CursorialXaml for every supplied .xaml additional file (mirrors the package targets).
    private sealed class CursorialXamlOptionsProvider(
        ImmutableArray<AdditionalText> xamlFiles, Dictionary<string, string>? globalOptions = null) : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _cursorialXaml = new FixedOptions(
            new Dictionary<string, string> { ["build_metadata.AdditionalFiles.SourceItemType"] = "CursorialXaml" });

        private readonly AnalyzerConfigOptions _empty = new FixedOptions(new Dictionary<string, string>());
        private readonly AnalyzerConfigOptions _global = new FixedOptions(globalOptions ?? new Dictionary<string, string>());

        public override AnalyzerConfigOptions GlobalOptions => _global;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile)
            => xamlFiles.Contains(textFile) ? _cursorialXaml : _empty;

        private sealed class FixedOptions(Dictionary<string, string> values) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
        }
    }
}