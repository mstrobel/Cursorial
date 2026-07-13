using System.Collections.Generic;
using System.Text.RegularExpressions;

using Cursorial.UI.Xaml; // source-linked frontend: XmlnsNamespaces

namespace Cursorial.UI.Xaml.Generator;

/// <summary>
/// A cheap, parse-free scan of a XAML document's ROOT TAG: its <c>x:Class</c> plus the root element's
/// <c>(xmlns, localName)</c>. The generator uses this to build the cross-document x:Class → base map for
/// <c>AugmentWithXClassBases</c> — a full frontend parse of every sibling document inside each per-document
/// emit would be quadratic, and the map only needs the root tag. Over-collection is impossible (one root per
/// document); a scan miss (no x:Class, unresolvable prefix) just skips augmentation for that document, which
/// degrades to the pre-existing behavior for its class.
/// </summary>
internal static class XClassBaseScanner
{
    /// <summary>The root's (x:Class, root xmlns URI, root local name), or null for class-less documents.</summary>
    public static (string ClassName, string RootNamespace, string RootLocalName)? Scan(string xaml)
    {
        var text = Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

        // The first real element tag — skip the XML declaration / doctype / processing instructions.
        var root = Regex.Match(text, @"<(?![?!])\s*([A-Za-z_][\w.-]*(?::[A-Za-z_][\w.-]*)?)");
        if (!root.Success)
            return null;

        // The tag's full text up to its quote-aware closing '>' (attribute values may contain '>').
        var end = root.Index;
        var inQuote = false;
        while (end < text.Length && (inQuote || text[end] != '>'))
        {
            if (text[end] == '"')
                inQuote = !inQuote;
            end++;
        }

        var tag = text.Substring(root.Index, end - root.Index);

        string? defaultNamespace = null, className = null;
        var prefixes = new Dictionary<string, string>(System.StringComparer.Ordinal);
        var attributes = new List<(string Name, string Value)>();

        foreach (Match m in Regex.Matches(tag, "([A-Za-z_][\\w:.-]*)\\s*=\\s*\"([^\"]*)\""))
            attributes.Add((m.Groups[1].Value, m.Groups[2].Value));

        foreach (var (name, value) in attributes)
        {
            if (name == "xmlns")
                defaultNamespace = value;
            else if (name.StartsWith("xmlns:", System.StringComparison.Ordinal))
                prefixes[name.Substring("xmlns:".Length)] = value;
        }

        foreach (var (name, value) in attributes)
        {
            var colon = name.IndexOf(':');
            if (colon > 0 &&
                name.Substring(colon + 1) == "Class" &&
                prefixes.TryGetValue(name.Substring(0, colon), out var uri) &&
                uri == XmlnsNamespaces.Intrinsics)
            {
                className = value;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(className))
            return null;

        var element = root.Groups[1].Value;
        var elementColon = element.IndexOf(':');
        var rootNamespace = elementColon < 0
            ? defaultNamespace
            : prefixes.TryGetValue(element.Substring(0, elementColon), out var prefixed) ? prefixed : null;

        return rootNamespace is null
            ? null
            : (className!, rootNamespace, elementColon < 0 ? element : element.Substring(elementColon + 1));
    }
}
