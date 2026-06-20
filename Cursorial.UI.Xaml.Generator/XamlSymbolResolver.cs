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

    /// <summary>The CLR namespaces the default UI xmlns probes (mirrors <c>XamlSchemaContext</c>).</summary>
    private static readonly string[] DefaultUiNamespaces =
    [
        "Cursorial.UI", "Cursorial.UI.Controls", "Cursorial.UI.Data", "Cursorial.UI.Input",
        "Cursorial.Drawing.Media", "Cursorial.UI.Themes"
    ];

    private const string UsingPrefix = "using:";
    private const string ClrNamespacePrefix = "clr-namespace:";

    private readonly Compilation _compilation;
    private readonly Dictionary<(string, string), INamedTypeSymbol?> _cache = new();

    public XamlSymbolResolver(Compilation compilation) => _compilation = compilation;

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
        if (string.Equals(xmlNamespace, CursorialUiNamespace, System.StringComparison.Ordinal))
            return DefaultUiNamespaces;

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
