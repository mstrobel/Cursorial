using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

using Cursorial.UI.Xaml; // source-linked frontend: XamlFrontend, XamlDocument, XamlDiagnostic, XamlParseOptions
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Cursorial.UI.Xaml.Generator;

/// <summary>
/// The Fork C X4 incremental XAML generator (design doc §4.9, P10). It discovers the project's
/// <c>CursorialXaml</c> items (surfaced as <see cref="AdditionalText"/> flagged
/// <c>SourceItemType=CursorialXaml</c> by the package targets), runs the SAME netstandard2.0
/// <c>XamlFrontend.Parse</c> the loader runs (referenced) over the symbol-backed
/// <see cref="RoslynXamlMetadata"/> provider, and emits:
/// <list type="bullet">
/// <item>WS-X4.4 — the parser's <see cref="XamlDiagnostic"/>s (the <c>CUR1xxx</c> syntax band AND the
/// <c>CUR2xxx</c> semantic band — type/member-not-found) as Roslyn build diagnostics at the <c>.xaml</c>
/// location.</item>
/// <item>WS-X4.5 — one generated <c>IXamlTypeMetadataProvider</c> per compilation (over the union closed
/// type set) with a <c>[ModuleInitializer]</c> that installs it as the AOT-clean loader default.</item>
/// <item>WS-X4.6 — for each <c>x:Class</c> document, the code-behind partial: typed <c>x:Name</c> fields +
/// <c>InitializeComponent</c>.</item>
/// </list>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class XamlSourceGenerator : IIncrementalGenerator
{
    private const string SourceItemTypeKey = "build_metadata.AdditionalFiles.SourceItemType";
    private const string CursorialXamlItemType = "CursorialXaml";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Each CursorialXaml file → an equatable (path, text) input (filtered to our item type so a stray
        // .xaml added as a plain AdditionalFiles isn't picked up). Equatable strings drive incrementality.
        var xamlFiles = context.AdditionalTextsProvider
                               .Combine(context.AnalyzerConfigOptionsProvider)
                               .Where(static pair =>
                                      {
                                          var (file, options) = pair;

                                          if (!file.Path.EndsWith(".xaml", System.StringComparison.OrdinalIgnoreCase))
                                              return false;

                                          return options.GetOptions(file).TryGetValue(SourceItemTypeKey, out var itemType)
                                                 && string.Equals(itemType, CursorialXamlItemType, System.StringComparison.OrdinalIgnoreCase);
                                      })
                               .Select(static (pair, ct) => new XamlInput(
                                           pair.Left.Path,
                                           pair.Left.GetText(ct)?.ToString() ?? string.Empty));

        // Combine with the compilation so the symbol-backed RoslynXamlMetadata can resolve types (WS-X4.3).
        // This makes the generator compilation-coupled (re-runs as the compilation changes) — the standard
        // tradeoff for a semantic generator; XAML inputs themselves stay equatable for the file half.
        var withCompilation = xamlFiles.Combine(context.CompilationProvider);
        context.RegisterSourceOutput(withCompilation, static (spc, pair) => Emit(spc, pair.Left, pair.Right));

        // WS-X4.5 — one generated metadata provider per compilation, over the UNION of every CursorialXaml
        // file's closed type set. A generated [ModuleInitializer] installs it as the loader default so the
        // app's XAML loads (incl. each code-behind's cached parse) run reflection-free / AOT-clean.
        var allXaml = xamlFiles.Collect().Combine(context.CompilationProvider);
        context.RegisterSourceOutput(allXaml, static (spc, pair) => EmitProvider(spc, pair.Left, pair.Right));
    }

    private static void EmitProvider(SourceProductionContext spc, ImmutableArray<XamlInput> inputs, Compilation compilation)
    {
        if (inputs.IsDefaultOrEmpty)
            return;

        var resolver = new XamlSymbolResolver(compilation);

        // Union of every file's recorded element/attached-owner names → resolved symbols (the closed set).
        var names = new HashSet<(string Namespace, string LocalName)>();
        foreach (var input in inputs)
        {
            foreach (var name in ClosedTypeSet.CollectElementNames(input.Text))
                names.Add(name);
        }

        var types = names
            .Select(n => resolver.Resolve(n.Namespace, n.LocalName, out _))
            .Where(static t => t is not null)
            .Cast<INamedTypeSymbol>()
            .Distinct(SymbolEqualityComparer.Default)
            .Cast<INamedTypeSymbol>()
            .ToList();

        if (new MetadataProviderEmitter(compilation).Emit(types) is { } source)
            spc.AddSource("__GeneratedXamlMetadata.g.cs", SourceText.From(source, Encoding.UTF8));
    }

    private static void Emit(SourceProductionContext spc, XamlInput input, Compilation compilation)
    {
        // Run the SAME parser the loader runs, now over the symbol-backed provider (WS-X4.3) so element types
        // resolve and the node graph carries them. FoldConstants=false — there are no runtime values to fold at
        // generator time. CollectAll so a malformed document yields every diagnostic rather than throwing.
        XamlDocument document;

        try
        {
            document = XamlFrontend.Parse(
                input.Text,
                new XamlParseOptions
                {
                    MetadataProvider = new RoslynXamlMetadata(compilation),
                    DiagnosticMode = XamlDiagnosticMode.CollectAll,
                    FoldConstants = false,
                },
                source: null
            );
        }
        catch (System.Exception ex)
        {
            // The parser should not throw under CollectAll; treat any escape as an internal-error diagnostic
            // rather than crashing the compiler.
            spc.ReportDiagnostic(Diagnostic.Create(InternalError, Location.None, input.Path, ex.Message));
            return;
        }

        bool hasSyntaxError = false;

        foreach (var diagnostic in document.Diagnostics)
        {
            // Surface the CUR1xxx parse (syntax) band AND the CUR2xxx semantic band — the symbol-backed
            // RoslynXamlMetadata mirrors the reflection provider's full member ladder (registered instance +
            // attached properties, events, CLR properties, the synthetic Style.TargetType), so a CUR2 member
            // miss matches the runtime truth and a CUR2002 type miss is a genuine missing-reference error.
            // The CUR3xxx instantiation band cannot arise at parse time (nothing is instantiated).
            bool isParseOrSemantic = diagnostic.Code.StartsWith("CUR1", System.StringComparison.Ordinal) ||
                                     diagnostic.Code.StartsWith("CUR2", System.StringComparison.Ordinal);
            if (!isParseOrSemantic)
                continue;

            // A CUR1xxx syntax error leaves the node graph unreliable → fall back to the marker (no codegen).
            if (diagnostic.Code.StartsWith("CUR1", System.StringComparison.Ordinal) && diagnostic.Severity == XamlDiagnosticSeverity.Error)
                hasSyntaxError = true;

            spc.ReportDiagnostic(ToRoslyn(diagnostic, input));
        }

        var hint = SanitizeHint(System.IO.Path.GetFileNameWithoutExtension(input.Path)) + ".g.cs";

        // WS-X4.6 — a document with an x:Class and valid syntax gets the typed-field + InitializeComponent
        // partial. (A syntax error leaves the node graph unreliable, so fall back to the marker.)
        if (!hasSyntaxError && CodeBehindEmitter.Emit(document, input.Text, input.Path) is {} codeBehind)
        {
            spc.AddSource(hint, SourceText.From(codeBehind, Encoding.UTF8));
            return;
        }

        // Marker for class-less documents (and syntax-error fallback) — proves the file reached the generator.
        var rootClass = document.RootClassName is { Length: > 0 } rc ? rc : "(none)";

        var src =
            "// <auto-generated/> Cursorial.UI.Xaml.Generator\n" +
            $"// source: {input.Path}\n" +
            $"// x:Class: {rootClass}; diagnostics: {document.Diagnostics.Count}\n";

        spc.AddSource(hint, SourceText.From(src, Encoding.UTF8));
    }

    private static Diagnostic ToRoslyn(XamlDiagnostic diagnostic, XamlInput input)
    {
        var severity = diagnostic.Severity switch
                       {
                           XamlDiagnosticSeverity.Error   => DiagnosticSeverity.Error,
                           XamlDiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
                           _                              => DiagnosticSeverity.Info,
                       };

        // One descriptor per CUR code (cached) — the message is the format argument.
        var descriptor = DescriptorFor(diagnostic.Code, severity);
        return Diagnostic.Create(descriptor, LocationFor(input, diagnostic.Line, diagnostic.Column), diagnostic.Message);
    }

    private static readonly Dictionary<string, DiagnosticDescriptor> DescriptorCache = new();
    private static readonly object DescriptorLock = new();

    private static DiagnosticDescriptor DescriptorFor(string code, DiagnosticSeverity severity)
    {
        lock (DescriptorLock)
        {
            var key = code + (char) severity;

            if (DescriptorCache.TryGetValue(key, out var cached))
                return cached;

            var descriptor = new DiagnosticDescriptor(
                id: code,
                title: "XAML " + code,
                messageFormat: "{0}",
                category: "Cursorial.Xaml",
                defaultSeverity: severity,
                isEnabledByDefault: true
            );

            DescriptorCache[key] = descriptor;
            return descriptor;
        }
    }

    private static readonly DiagnosticDescriptor InternalError = new(
        id: "CURG0001",
        title: "XAML generator internal error",
        messageFormat: "The XAML generator failed on '{0}': {1}",
        category: "Cursorial.Xaml",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    // Builds a Roslyn Location on the .xaml file from the frontend's 1-based line/column.
    private static Location LocationFor(XamlInput input, int line, int column)
    {
        if (line <= 0 || column <= 0)
            return Location.None;

        var text = SourceText.From(input.Text);
        var zeroLine = line - 1;

        if (zeroLine >= text.Lines.Count)
            return Location.None;

        var textLine = text.Lines[zeroLine];
        var start = textLine.Start + System.Math.Min(column - 1, textLine.End - textLine.Start);
        var position = new LinePosition(zeroLine, column - 1);
        return Location.Create(input.Path, new TextSpan(start, 0), new LinePositionSpan(position, position));
    }

    private static string SanitizeHint(string name)
    {
        var sb = new StringBuilder(name.Length);

        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');

        return sb.Length == 0 ? "Xaml" : sb.ToString();
    }

    private readonly record struct XamlInput(string Path, string Text);
}