using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;

namespace Cursorial.UI.Xaml.Generator;

/// <summary>
/// Resolves a XAML <c>(xmlns, localName)</c> to an <see cref="INamedTypeSymbol"/> against the Roslyn
/// <see cref="Compilation"/> — the build-time analog of the loader's <c>XamlSchemaContext</c>, reading
/// the same default xmlns→CLR-namespace map (the <c>ui</c> URI → <c>Cursorial.UI</c> /
/// <c>.Controls</c> / <c>.Data</c> / <c>Drawing.Media</c> / <c>.Themes</c>) and decoding the
/// <c>using:</c> / <c>clr-namespace:</c> forms. Purely symbol-based: it never loads
/// <c>Cursorial.UI</c> into the compiler (the generated provider's <c>typeof(...)</c> references
/// resolve at the consumer's compile instead).
/// </summary>
internal sealed class XamlSymbolResolver
{
    /// <summary>The default Cursorial UI xmlns URI (mirrors <c>XmlnsNamespaces.CursorialUi</c>).</summary>
    public const string CursorialUiNamespace = "https://cursorial.dev/ui";
    private const string IntrinsicsNamespace = "https://cursorial.dev/xaml";

    // The XAML2009 built-in (CLR basic) type names, each mapping to System.<name> (System.String, System.Int32, …).
    private static string? BuiltInMetadataName(string localName) => localName switch
    {
        "Object" or "Boolean" or "Byte" or "SByte" or "Char" or "Decimal" or "Single" or "Double"
            or "Int16" or "Int32" or "Int64" or "UInt16" or "UInt32" or "UInt64" or "String"
            or "TimeSpan" or "Uri" => "System." + localName,
        _ => null,
    };

    private const string UsingPrefix = "using:";
    private const string ClrNamespacePrefix = "clr-namespace:";

    private readonly Compilation _compilation;
    private readonly Dictionary<(string, string), INamedTypeSymbol?> _cache = new();
    private Dictionary<string, List<string>>? _namespaceMap;

    public XamlSymbolResolver(Compilation compilation) => _compilation = compilation;

    /// <summary>
    /// The xmlns URI → CLR namespaces map, DISCOVERED from <c>[assembly: XmlnsDefinition]</c> declarations on the
    /// compilation's own + referenced assemblies (the build-time analog of the loader's <c>XamlSchemaContext</c>
    /// discovery; the dual-run gate catches any drift) — keyed by ANY URI, so a library that ships its own xmlns URI
    /// resolves the same as the default one. Matched on the attribute's simple name, purely from symbols —
    /// <c>Cursorial.UI</c> is never loaded into the compiler.
    /// </summary>
    private Dictionary<string, List<string>> NamespaceMap => _namespaceMap ??= DiscoverNamespaceMap();

    private Dictionary<string, List<string>> DiscoverNamespaceMap()
    {
        var map = new Dictionary<string, List<string>>(System.StringComparer.Ordinal);

        void Scan(IAssemblySymbol assembly)
        {
            foreach (var attribute in assembly.GetAttributes())
            {
                if (attribute.AttributeClass?.Name != "XmlnsDefinitionAttribute" || attribute.ConstructorArguments.Length < 2)
                    continue;
                if (attribute.ConstructorArguments[0].Value is not string uri || uri.Length == 0)
                    continue;
                if (attribute.ConstructorArguments[1].Value is string clrNamespace && clrNamespace.Length > 0)
                {
                    if (!map.TryGetValue(uri, out var list))
                        map[uri] = list = new List<string>();
                    if (!list.Contains(clrNamespace))
                        list.Add(clrNamespace);
                }
            }
        }

        Scan(_compilation.Assembly);
        foreach (var referenced in _compilation.SourceModule.ReferencedAssemblySymbols)
            Scan(referenced);

        return map;
    }

    /// <summary>
    /// Resolves a type, or <see langword="null"/> on a miss. <paramref name="ambiguous"/> carries the
    /// candidate full names when the local name matched more than one CLR namespace (the loader's
    /// CUR2001 ambiguity).
    /// </summary>
    public INamedTypeSymbol? Resolve(string xmlNamespace, string localName, out IReadOnlyList<string>? ambiguous)
    {
        ambiguous = null;
        if (_cache.TryGetValue((xmlNamespace, localName), out var cached))
            return cached;

        // XAML2009 built-in (CLR basic) types in the x: (intrinsics) namespace: x:String → System.String, etc.
        // (mirrors the loader's XamlSchemaContext.BuiltInType — the dual-run/twin gates catch any drift).
        if (string.Equals(xmlNamespace, IntrinsicsNamespace, System.StringComparison.Ordinal) && BuiltInMetadataName(localName) is { } metadata)
        {
            var builtin = _compilation.GetTypeByMetadataName(metadata);
            _cache[(xmlNamespace, localName)] = builtin;
            return builtin;
        }

        var matches = new List<INamedTypeSymbol>();
        foreach (var clrNamespace in CandidateNamespaces(xmlNamespace))
        {
            var metadataName = clrNamespace.Length == 0 ? localName : clrNamespace + "." + localName;
            // GetTypeByMetadataName returns null on ambiguity across referenced assemblies; fall back to a
            // namespace-member scan so a type present in multiple assemblies still resolves deterministically.
            var symbol = _compilation.GetTypeByMetadataName(metadataName);
            if (symbol is not null)
            {
                if (!matches.Any(m => SymbolEqualityComparer.Default.Equals(m, symbol)))
                    matches.Add(symbol);
            }
        }

        INamedTypeSymbol? result;
        if (matches.Count == 0)
        {
            result = null;
        }
        else if (matches.Count == 1)
        {
            result = matches[0];
        }
        else
        {
            ambiguous = matches.Select(m => m.ToDisplayString()).ToList();
            result = null;
        }

        _cache[(xmlNamespace, localName)] = result;
        return result;
    }

    /// <summary>The CLR namespaces an xmlns probes, most-specific first.</summary>
    public IEnumerable<string> CandidateNamespaces(string xmlNamespace)
    {
        // Any URI declared via [assembly: XmlnsDefinition] (the default Cursorial URI is just one such key).
        if (NamespaceMap.TryGetValue(xmlNamespace, out var mapped))
            return mapped;

        if (xmlNamespace.StartsWith(UsingPrefix, System.StringComparison.Ordinal))
            return [xmlNamespace.Substring(UsingPrefix.Length).Trim()];

        if (xmlNamespace.StartsWith(ClrNamespacePrefix, System.StringComparison.Ordinal))
        {
            var body = xmlNamespace.Substring(ClrNamespacePrefix.Length);
            var semi = body.IndexOf(';');
            return [(semi < 0 ? body : body.Substring(0, semi)).Trim()];
        }

        return [];
    }
}
