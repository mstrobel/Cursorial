using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;

using Cursorial.UI.Xaml; // source-linked frontend: XamlFrontend, XamlDocument, XamlDiagnostic, XamlParseOptions
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
/// type set), advertised via <c>[assembly: XamlMetadataProvider]</c> for the loader's entry-assembly
/// pull discovery (the AOT-clean default; loading an assembly never mutates process state).</item>
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
        // Each CursorialXaml file → an equatable (path, text) input. The SourceItemType metadata
        // discriminates when PRESENT (a stray .xaml added under some other AdditionalFiles role
        // isn't picked up) but its ABSENCE must fail OPEN: per-file CompilerVisibleItemMetadata
        // does not reliably survive IDE design-time models (Rider/R# drops it where global
        // build_property values flow), and failing closed there means the generator silently
        // produces nothing in the IDE — unresolved InitializeComponent with no error anywhere.
        // Equatable strings drive incrementality.
        var xamlFiles = context.AdditionalTextsProvider
                               .Combine(context.AnalyzerConfigOptionsProvider)
                               .Where(static pair =>
                                      {
                                          var (file, options) = pair;

                                          if (!file.Path.EndsWith(".xaml", System.StringComparison.OrdinalIgnoreCase))
                                              return false;

                                          return !options.GetOptions(file).TryGetValue(SourceItemTypeKey, out var itemType)
                                                 || string.IsNullOrEmpty(itemType)
                                                 || string.Equals(itemType, CursorialXamlItemType, System.StringComparison.OrdinalIgnoreCase);
                                      })
                               .Select(static (pair, ct) =>
                                       {
                                           string relativePath = pair.Left.Path;

                                           if (pair.Right.GlobalOptions.TryGetValue("build_property.MSBuildProjectDirectory", out var dir) &&
                                               dir.Length < pair.Left.Path.Length &&
                                               string.Compare(dir, 0, pair.Left.Path, 0, dir.Length, System.StringComparison.OrdinalIgnoreCase) == 0)
                                           {
                                               relativePath = relativePath.Remove(0, dir.Length);

                                               if (relativePath.StartsWith($"{Path.DirectorySeparatorChar}", System.StringComparison.Ordinal) ||
                                                   relativePath.StartsWith($"{Path.AltDirectorySeparatorChar}", System.StringComparison.Ordinal))
                                               {
                                                   relativePath = relativePath.Substring(1);
                                               }
                                           }

                                           return new XamlInput(
                                               pair.Left.Path,
                                               relativePath,
                                               pair.Left.GetText(ct)?.ToString() ?? string.Empty);
                                       });

        // WS-X5.5 — the full-lowering opt-in. `<CursorialXamlLowering>full</CursorialXamlLowering>` (a
        // compiler-visible MSBuild property, declared in the package .props) switches each x:Class document from
        // the X4.6 runtime-loader code-behind to the X5 straight-line lowering (reflection-free). Default
        // (unset / anything but "full") keeps the loader-backed code-behind.
        var loweringFull = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) =>
            provider.GlobalOptions.TryGetValue("build_property.CursorialXamlLowering", out var mode)
            && string.Equals(mode, "full", System.StringComparison.OrdinalIgnoreCase));

        // Every document's (x:Class, root element) — the cross-document base map. A per-document emit
        // analyzing `<v:EditorsPane Width="…">` must know EditorsPane's base even though that base is
        // declared by the GENERATED partial of a SIBLING document (a generator never sees its own output).
        var classRoots = xamlFiles.Select(static (input, _) => XClassBaseScanner.Scan(input.Text)).Collect();

        // Combine with the compilation so the symbol-backed RoslynXamlMetadata can resolve types (WS-X4.3).
        // This makes the generator compilation-coupled (re-runs as the compilation changes) — the standard
        // tradeoff for a semantic generator; XAML inputs themselves stay equatable for the file half.
        var withCompilation = xamlFiles.Combine(context.CompilationProvider).Combine(loweringFull).Combine(classRoots);
        context.RegisterSourceOutput(withCompilation, static (spc, pair) =>
            Emit(spc, pair.Left.Left.Left, pair.Left.Left.Right, pair.Left.Right, pair.Right));

        // WS-X4.5 — one generated metadata provider per compilation, over the UNION of every CursorialXaml
        // file's closed type set, advertised via [assembly: XamlMetadataProvider]. The loader's lazy default
        // pull-discovers the ENTRY assembly's attribute, so an app's XAML loads (incl. each code-behind's
        // cached parse) run reflection-free while a library's attribute stays inert — no [ModuleInitializer],
        // so loading an assembly never hijacks a host's default (the designer/test-host bug).
        var allXaml = xamlFiles.Collect().Combine(context.CompilationProvider);
        context.RegisterSourceOutput(allXaml, static (spc, pair) => EmitProvider(spc, pair.Left, pair.Right));
    }

    private static void EmitProvider(SourceProductionContext spc, ImmutableArray<XamlInput> inputs, Compilation compilation)
    {
        if (inputs.IsDefaultOrEmpty)
            return;

        // The baked member tables must see through generated base declarations too, or an x:Class type
        // referenced by a sibling document bakes an EMPTY member set (no inherited Width/Margin/...).
        compilation = AugmentWithXClassBases(
            compilation, inputs.Select(static i => XClassBaseScanner.Scan(i.Text)).ToImmutableArray());

        var resolver = new XamlSymbolResolver(compilation);

        // Union of every file's recorded element/attached-owner names PLUS its load-time type
        // references (x:Type, TargetType/DataType/AncestorType, selector tokens) → resolved
        // symbols (the closed set). Elements alone are not enough: the loader resolves the
        // reference forms at RUNTIME through this provider.
        var names = new HashSet<(string Namespace, string LocalName)>();
        foreach (var input in inputs)
        {
            foreach (var name in ClosedTypeSet.CollectElementNames(input.Text))
                names.Add(name);
            foreach (var name in ClosedTypeSet.CollectTypeReferenceNames(input.Text))
                names.Add(name);
        }

        var types = names
            .Select(n => resolver.Resolve(n.Namespace, n.LocalName, out _))
            .Where(static t => t is not null)
            .Cast<INamedTypeSymbol>()
            .Distinct(SymbolEqualityComparer.Default)
            .Cast<INamedTypeSymbol>()
            .ToList();

        // WS-X4.5 / P1B — the document set's {x:Static} references, resolved to baked `global::FullType.Member`
        // expressions for the generated provider's TryResolveStatic switch (so x:Static works under the AOT-clean
        // provider, not just reflection). The shared ClosedTypeSet.CollectStatics is the single source the dual-run
        // test also uses, so the two can't drift.
        var statics = ClosedTypeSet.CollectStatics(resolver, inputs.Select(i => i.Text));

        if (new MetadataProviderEmitter(compilation).Emit(types, statics) is { } source)
            spc.AddSource("__GeneratedXamlMetadata.g.cs", SourceText.From(source, Encoding.UTF8));
    }

    private static void Emit(
        SourceProductionContext spc,
        XamlInput input,
        Compilation compilation,
        bool loweringFull,
        ImmutableArray<(string ClassName, string RootNamespace, string RootLocalName)?> classRoots)
    {
        // See AugmentWithXClassBases — without this, inherited members on sibling x:Class types are false CUR2102s.
        compilation = AugmentWithXClassBases(compilation, classRoots);

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

        // WS-B3 — x:DataType binding-path assists (CURG2001), once the node graph is structurally sound.
        if (!hasSyntaxError)
        {
            BindingPathValidator.Validate(document, new XamlSymbolResolver(compilation),
                (message, line, column) => spc.ReportDiagnostic(
                    Diagnostic.Create(BindingPathAssist, LocationFor(input, line, column), message)));
        }

        var hint = SanitizeHint(input.RelativePath) + ".g.cs";

        // WS-X5.5 — full-lowering opt-in: an x:Class document with valid syntax lowers to straight-line,
        // reflection-free C# (no runtime loader). Any member the lowering can't yet emit is surfaced as a
        // CURG3001 build warning at its .xaml position, so an opted-in build never silently drops a member.
        if (loweringFull && !hasSyntaxError &&
            LoweringEmitter.Emit(document, input.Path, input.RelativePath, new XamlSymbolResolver(compilation), compilation.AssemblyName) is { } lowered)
        {
            foreach (var note in lowered.Unlowered)
                spc.ReportDiagnostic(Diagnostic.Create(LoweringGap, LocationFor(input, note.Line, note.Column), note.Message));

            // CURG2002 — info-level "works, but not the AOT-optimal compiled form" notes (reflective-fallback bindings).
            foreach (var note in lowered.Infos)
                spc.ReportDiagnostic(Diagnostic.Create(ReflectiveBindingInfo, LocationFor(input, note.Line, note.Column), note.Message));

            spc.AddSource(hint, SourceText.From(lowered.Source, Encoding.UTF8));
            return;
        }

        // WS-X4.6 — a document with an x:Class and valid syntax gets the typed-field + InitializeComponent
        // partial. (A syntax error leaves the node graph unreliable, so fall back to the marker.)
        if (!hasSyntaxError && CodeBehindEmitter.Emit(document, input.Text, input.Path, input.RelativePath, compilation.AssemblyName) is {} codeBehind)
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

    /// <summary>
    /// The ANALYSIS compilation: the source compilation plus a synthetic <c>partial class C : Base { }</c>
    /// for every x:Class document whose code-behind leaves the base type to the GENERATED partial. A source
    /// generator never sees its own output, so without this the symbol model reads such a class as
    /// <c>: object</c> — every inherited member set on it from a sibling document
    /// (<c>&lt;v:EditorsPane Width="…"&gt;</c>) is a false CUR2102, and the emitted provider bakes an empty
    /// member table for it. The synthetic trees mirror exactly what <see cref="CodeBehindEmitter"/> emits into
    /// the REAL compilation; they exist only for symbol lookup and are never added as sources.
    /// </summary>
    private static Compilation AugmentWithXClassBases(
        Compilation compilation,
        ImmutableArray<(string ClassName, string RootNamespace, string RootLocalName)?> classRoots)
    {
        List<string>? declarations = null;
        XamlSymbolResolver? resolver = null;

        foreach (var entry in classRoots)
        {
            if (entry is not ({ } className, { } rootNamespace, { } rootLocalName) ||
                className.Length == 0 ||
                !className.Split('.').All(SyntaxFacts.IsValidIdentifier))
                continue;

            // Only augment when the base is genuinely missing: a code-behind that declares its own base is
            // left alone (a conflicting synthetic declaration would corrupt symbol lookup; CS0263 in the real
            // compilation already polices root/code-behind mismatches).
            if (compilation.GetTypeByMetadataName(className) is not { BaseType.SpecialType: SpecialType.System_Object } existing)
                continue;

            resolver ??= new XamlSymbolResolver(compilation);
            var baseSymbol = resolver.Resolve(rootNamespace, rootLocalName, out _);
            if (baseSymbol is null || SymbolEqualityComparer.Default.Equals(baseSymbol, existing))
                continue;

            var dot = className.LastIndexOf('.');
            var declaration =
                $"partial class {className.Substring(dot + 1)} : {baseSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {{ }}";
            (declarations ??= new List<string>()).Add(
                dot < 0 ? declaration : $"namespace {className.Substring(0, dot)} {{ {declaration} }}");
        }

        if (declarations is null)
            return compilation;

        // Parse with the compilation's OWN options — AddSyntaxTrees rejects trees whose language
        // version / feature flags differ from the existing ones ("Inconsistent syntax tree features").
        var parseOptions = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions;
        return compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            "// Synthetic analysis-only x:Class base declarations (mirror of the generated partials).\n" +
            string.Join("\n", declarations),
            parseOptions));
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

    // WS-B3 — a generator-only assist (CURGxxxx band): an x:DataType-scoped binding path member that doesn't
    // resolve. Warning, not error — the runtime reflective binding still applies; this just flags a likely typo.
    private static readonly DiagnosticDescriptor BindingPathAssist = new(
        id: "CURG2001",
        title: "Binding path does not resolve on the declared x:DataType",
        messageFormat: "{0}",
        category: "Cursorial.Xaml",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    // WS-B3 — a generator-only assist (CURGxxxx band): under full-lowering, a {Binding} that WAS a compiled-lane
    // candidate (x:DataType in scope) but stayed reflective, naming why. Info severity (quiet by default — it
    // works; this only flags the non-AOT-optimal form). Only emitted in the full-lowering path; a normal build's
    // reflective bindings are expected and get no note.
    private static readonly DiagnosticDescriptor ReflectiveBindingInfo = new(
        id: "CURG2002",
        title: "Binding stays reflective under full-lowering",
        messageFormat: "{0}",
        category: "Cursorial.Xaml",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    // WS-X5.5 — a full-lowering gap: a member the lowering couldn't emit (it left a // TODO X5 marker and the
    // member has no runtime effect). Warning, so an opted-in `full` build sees the dropped member instead of a
    // silently-incomplete view; the fix is to use the supported subset or stay on the code-behind path.
    private static readonly DiagnosticDescriptor LoweringGap = new(
        id: "CURG3001",
        title: "XAML member not lowered (full-lowering)",
        messageFormat: "Full-lowering could not emit this member, so it has no runtime effect: {0}",
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

    private readonly record struct XamlInput(string Path, string RelativePath, string Text);
}