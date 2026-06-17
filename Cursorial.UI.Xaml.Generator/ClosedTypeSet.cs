using System.Collections.Generic;

using Microsoft.CodeAnalysis;

namespace Cursorial.UI.Xaml.Generator;

/// <summary>
/// Discovers the set of CLR types a XAML document references, by parsing with a <em>recording</em>
/// metadata provider: the frontend parser calls <see cref="IXamlTypeMetadataProvider.TryGetType"/> for
/// every element it encounters (children too, under error recovery), so recording each query captures
/// the document's full element-name set without needing positions or runtime <c>System.Type</c>s. The
/// recorded <c>(xmlns, localName)</c> pairs are then resolved to <see cref="INamedTypeSymbol"/>s by
/// <see cref="XamlSymbolResolver"/> — the closed type set the generated metadata provider covers.
/// </summary>
internal static class ClosedTypeSet
{
    /// <summary>The distinct element <c>(xmlns, localName)</c> names a document references (parse order).</summary>
    public static IReadOnlyList<(string Namespace, string LocalName)> CollectElementNames(string xaml)
    {
        var recorder = new RecordingProvider();
        try
        {
            XamlFrontend.Parse(xaml, new XamlParseOptions
            {
                MetadataProvider = recorder,
                DiagnosticMode = XamlDiagnosticMode.CollectAll,
            });
        }
        catch
        {
            // The recording provider returns NotFound for everything, so the parser never instantiates;
            // any escape is ignored — we keep whatever names were recorded before it.
        }

        return recorder.Names;
    }

    /// <summary>
    /// Records every <c>(xmlns, localName)</c> the parser asks about and always reports a miss — so the
    /// parser keeps descending (recording children) without ever building a tree.
    /// </summary>
    private sealed class RecordingProvider : IXamlTypeMetadataProvider
    {
        private readonly HashSet<(string, string)> _seen = new();
        private readonly List<(string, string)> _names = new();

        public IReadOnlyList<(string Namespace, string LocalName)> Names => _names;

        public XamlTypeResolution TryGetType(string xmlNamespace, string localName)
        {
            if (_seen.Add((xmlNamespace, localName)))
                _names.Add((xmlNamespace, localName));
            return XamlTypeResolution.NotFound();
        }

        public string[] GetClrNamespaces(string xmlNamespace) => System.Array.Empty<string>();

        public string[] GetKnownTypeNames(string xmlNamespace) => System.Array.Empty<string>();

        public string[] GetKnownMemberNames(System.Type clrType) => System.Array.Empty<string>();
    }
}
