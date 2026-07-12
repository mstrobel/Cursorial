using System.Collections.Generic;
using System.Text;

using Cursorial.UI.Xaml; // frontend node graph (internals via InternalsVisibleTo)
using Microsoft.CodeAnalysis;

namespace Cursorial.UI.Xaml.Generator;

/// <summary>
/// WS-X4.6 — the code-behind half of the generated partial: typed <c>x:Name</c> fields +
/// <c>InitializeComponent()</c>. Given a symbol-backed parse (so <c>ResolvedTypes</c> carry
/// <see cref="RoslynXamlType"/> identities), it collects the document-scope named elements and their
/// resolved types and emits a <c>partial class</c> matching the <c>x:Class</c> directive.
/// </summary>
/// <remarks>
/// v1 authoring model (design doc P10 / plan): <c>InitializeComponent</c> calls the runtime loader
/// (<c>XamlLoader.Shared.LoadComponent(this, doc)</c>) over a once-parsed cached <see cref="XamlDocument"/>,
/// then assigns each typed field from the document name scope — no object-construction codegen (that's X5).
/// The cached document parses at runtime through the AOT-clean generated provider (installed by the
/// generated module initializer), so the published app stays reflection-free. Names inside resource
/// dictionaries (no name scope) and deferred/template content (a separate template scope) are NOT fields.
/// </remarks>
internal static class CodeBehindEmitter
{
    /// <summary>A document-scope named element: its <c>x:Name</c> and resolved type. For an <c>&lt;x:Array&gt;</c>
    /// (<paramref name="IsArray"/>) <paramref name="Type"/> is the ELEMENT type T and the field is typed <c>T[]</c>.</summary>
    internal readonly record struct NamedElement(string Name, INamedTypeSymbol Type, bool IsArray = false);

    /// <summary>Collects the <c>x:Class</c> name + the document-scope (name → type) pairs from a parsed document.</summary>
    public static (string? RootClass, IReadOnlyList<NamedElement> Named) Collect(XamlDocument document)
    {
        var named = new List<NamedElement>();
        var seen = new HashSet<string>(System.StringComparer.Ordinal);

        var objects = document.Objects;
        var members = document.Members;
        var strings = document.Strings;
        var resolvedTypes = document.ResolvedTypes;

        for (int i = 0; i < objects.Length; i++)
        {
            ref readonly var obj = ref objects[i];

            if (!obj.HasFlag(ObjectFlags.HasName))
                continue;

            // x:Name inside a resource dictionary has no name scope (X104); inside deferred/template content
            // it lives in a per-instantiation template scope — neither becomes a code-behind field.
            if (obj.HasFlag(ObjectFlags.InResourceDictionary) || obj.HasFlag(ObjectFlags.InDeferredContent))
                continue;

            string? name = null;

            for (int m = obj.MemberStart; m < obj.MemberStart + obj.MemberCount; m++)
            {
                ref readonly var member = ref members[m];

                if (member.Kind == XamlValueKind.Directive && member.DirectiveKind == (int) XamlDirectiveKind.Name)
                {
                    name = strings[member.ValueIndex];
                    break;
                }
            }

            if (name is null || !seen.Add(name)) // dedup: a duplicate name is the parser's diagnostic, not a CS0102
                continue;

            // The element's resolved type identity is a RoslynXamlType (symbol-backed parse). Skip if the type
            // did not resolve (can't emit a typed field) — the parser already reported the resolution failure.
            // For an <x:Array> the resolved type is the ELEMENT type; the field is typed T[] (IsArray).
            if (obj.TypeId >= 0 && resolvedTypes[obj.TypeId]?.ClrType is RoslynXamlType { Symbol: INamedTypeSymbol symbol })
                named.Add(new NamedElement(name, symbol, obj.HasFlag(ObjectFlags.IsArray)));
        }

        return (document.RootClassName, named);
    }

    /// <summary>
    /// The machine-independent source URI baked into generated code: <c>cursorial://assembly/path</c>
    /// (the embedded-resource scheme) when the assembly name is known, else the raw path. Keeps
    /// generated output deterministic across machines AND gives relative
    /// <c>ResourceDictionary.Source</c> references a resolvable base.
    /// </summary>
    internal static string SourceUriFor(string? assemblyName, string relativePath, string fallbackPath)
    {
        if (assemblyName is not { Length: > 0 })
            return fallbackPath;
        var path = relativePath.Replace('\\', '/').TrimStart('/');
        return $"cursorial://{assemblyName}/{path}";
    }

    /// <summary>The fully qualified base type from the document's root element, or null when unresolved.</summary>
    internal static string? RootBaseType(XamlDocument document)
    {
        var objects = document.Objects;
        if (objects.Length == 0)
            return null;

        ref readonly var root = ref objects[0];
        return root.TypeId >= 0 && document.ResolvedTypes[root.TypeId]?.ClrType is RoslynXamlType { Symbol: INamedTypeSymbol symbol }
            ? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : null;
    }

    /// <summary>
    /// Emits the code-behind partial for an <c>x:Class</c> document, or <see langword="null"/> when there is
    /// no <c>x:Class</c> (a class-less document has no code-behind to extend).
    /// </summary>
    public static string? Emit(XamlDocument document, string xamlText, string xamlPath, string relativePath, string? assemblyName = null)
    {
        var (rootClass, named) = Collect(document);

        if (rootClass is not { Length: > 0 })
            return null;

        var sourceUri = SourceUriFor(assemblyName, relativePath, xamlPath);

        int dot = rootClass.LastIndexOf('.');
        string? ns = dot > 0 ? rootClass.Substring(0, dot) : null;
        string className = dot > 0 ? rootClass.Substring(dot + 1) : rootClass;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> Cursorial.UI.Xaml.Generator — code-behind (WS-X4.6)");
        sb.AppendLine($"// source: {sourceUri}");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        string indent = ns is null ? string.Empty : "    ";

        if (ns is not null)
        {
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
        }

        // The GENERATED half declares the base type from the document's root element, so the
        // hand-written half needs no base list at all — changing the root element in XAML is a
        // one-place edit. A code-behind that still declares a base is fine while it AGREES
        // (partial base lists must match, CS0263), which turns a root/code-behind mismatch into
        // a compile error instead of a silent split identity.
        var rootBase = RootBaseType(document);
        sb.AppendLine($"{indent}partial class {className}{(rootBase is null ? string.Empty : $" : {rootBase}")}");
        sb.AppendLine($"{indent}{{");

        // Typed x:Name fields. `default!` (the value is always assigned by InitializeComponent before any read) —
        // `default!` (not `null!`) so a value-type built-in field (e.g. `internal int N`) is valid (CS0037 otherwise).
        foreach (var ne in named)
            sb.AppendLine($"{indent}    internal {FieldType(ne)} {ne.Name} = default!;");

        if (named.Count > 0)
            sb.AppendLine();

        // The loader is bound DIRECTLY to this assembly's generated metadata provider (always emitted when a
        // code-behind is — a class-bearing document's types resolve, so the closed set is non-empty). Binding
        // the provider explicitly (rather than reading the global default) makes the load deterministic, AOT-
        // clean, and free of any cross-assembly default-provider coupling. The XAML is parsed ONCE into an
        // immutable, shareable document and instantiated per InitializeComponent call, populating `this`.
        sb.AppendLine($"{indent}    private const string __XamlSource = {Verbatim(xamlText)};");
        sb.AppendLine();
        sb.AppendLine($"{indent}    private static readonly global::Cursorial.UI.Xaml.XamlLoader __XamlLoader =");
        sb.AppendLine($"{indent}        new(new global::Cursorial.UI.Xaml.XamlLoaderOptions {{ MetadataProvider = global::Cursorial.UI.Xaml.Generated.__GeneratedXamlMetadata.Instance }});");
        sb.AppendLine($"{indent}    private static readonly global::Cursorial.UI.Xaml.XamlDocument __XamlDocument =");
        sb.AppendLine($"{indent}        __XamlLoader.Parse(__XamlSource, new global::System.Uri({Verbatim(sourceUri)}, global::System.UriKind.RelativeOrAbsolute));");
        sb.AppendLine();
        sb.AppendLine($"{indent}    private bool __contentLoaded;");
        sb.AppendLine();
        sb.AppendLine($"{indent}    internal void InitializeComponent()");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        if (__contentLoaded) return;");
        sb.AppendLine($"{indent}        __contentLoaded = true;");
        sb.AppendLine($"{indent}        __XamlLoader.LoadComponent(this, __XamlDocument);");

        if (named.Count > 0)
        {
            sb.AppendLine($"{indent}        var __scope = global::Cursorial.UI.NameScope.GetNameScope(this);");

            foreach (var ne in named)
                sb.AppendLine($"{indent}        this.{ne.Name} = ({FieldType(ne)})__scope!.Find(\"{ne.Name}\")!;");
        }

        sb.AppendLine($"{indent}    }}");

        sb.AppendLine($"{indent}}}");

        if (ns is not null)
            sb.AppendLine("}");

        return sb.ToString();
    }

    private static string Global(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    /// <summary>The field/cast type for a named element — <c>T[]</c> for an <c>&lt;x:Array&gt;</c>, else <c>T</c>.
    /// Shared with <c>LoweringEmitter</c>'s full-lowering code-behind so the two pipelines can't drift.</summary>
    internal static string FieldType(NamedElement ne) => ne.IsArray ? Global(ne.Type) + "[]" : Global(ne.Type);

    // A C# verbatim string literal (@"...") with embedded double-quotes doubled. Backslashes are literal.
    private static string Verbatim(string text) => "@\"" + text.Replace("\"", "\"\"") + "\"";
}