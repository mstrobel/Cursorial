using Cursorial.UI.Data;

namespace Cursorial.UI.Xaml;

/// <summary>
/// The xmlns-aware <see cref="IPathTypeResolver"/> for XAML-loaded binding paths — resolves the owner type of an
/// attached-property path segment, <c>(prefix:Type.Member)</c>, where <c>prefix</c> is a document xmlns prefix.
/// Mirrors <see cref="XamlSelectorTypeResolver"/>: a <c>prefix:Local</c> token resolves <c>Local</c> in the namespace
/// the document's ROOT bound to <c>prefix</c> — the document-level xmlns table (the top-level-only policy, CUR2004,
/// makes it unambiguous) — via the loader's <see cref="IXamlTypeMetadataProvider"/>, the same resolution element
/// types use. A bare, UNprefixed token falls back to the code-first <see cref="DefaultPathTypeResolver"/> (registered
/// <c>UIProperty</c> owner short names), so existing unprefixed attached paths (<c>(Grid.Row)</c>) are unchanged.
/// </summary>
internal sealed class XamlPathTypeResolver(IReadOnlyDictionary<string, string> namespaces, IXamlTypeMetadataProvider metadata)
    : IPathTypeResolver
{
    public Type? Resolve(string typeToken)
    {
        var colon = typeToken.IndexOf(':');
        if (colon < 0)
            return DefaultPathTypeResolver.Instance.Resolve(typeToken); // unprefixed: registry short-name owners

        var prefix = typeToken[..colon];
        var local = typeToken[(colon + 1)..];

        // The prefix must be bound at the document root (an empty prefix = the default xmlns, keyed under "").
        if (!namespaces.TryGetValue(prefix, out var ns))
            return null; // an unbound prefix — resolution fails and the path parser reports the offending token

        var resolution = metadata.TryGetType(ns, local);
        return resolution.IsResolved ? resolution.Type!.SystemType() : null;
    }
}
