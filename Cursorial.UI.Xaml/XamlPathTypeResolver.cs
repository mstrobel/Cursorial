using Cursorial.UI.Data;

namespace Cursorial.UI.Xaml;

/// <summary>
/// The xmlns-aware <see cref="IPathTypeResolver"/> for XAML-loaded binding paths — resolves the owner type of an
/// attached-property path segment, <c>(prefix:Type.Member)</c>, where <c>prefix</c> is a document xmlns prefix.
/// Mirrors <see cref="XamlSelectorTypeResolver"/>: a <c>prefix:Local</c> token resolves <c>Local</c> in the namespace
/// the document's ROOT bound to <c>prefix</c> — the document-level xmlns table (the top-level-only policy, CUR2004,
/// makes it unambiguous) — via the loader's <see cref="IXamlTypeMetadataProvider"/>, the same resolution element
/// types use. A bare, UNprefixed token resolves in two steps: first the code-first
/// <see cref="DefaultPathTypeResolver"/> (registered <c>UIProperty</c> owner short names — so existing unprefixed
/// attached paths (<c>(Grid.Row)</c>) and user-registered owners are unchanged), then, when the registry has no
/// owner for that short name, the Cursorial UI xmlns so unregistered framework types still resolve.
/// </summary>
internal sealed class XamlPathTypeResolver(IReadOnlyDictionary<string, string> namespaces, IXamlTypeMetadataProvider metadata)
    : IPathTypeResolver
{
    public Type? Resolve(string typeToken)
    {
        var colon = typeToken.IndexOf(':');
        if (colon < 0)
        {
            // Unprefixed: registry short-name owners first (the code-first contract — a user-registered owner
            // must not be shadowed by a same-named framework type), then the Cursorial UI xmlns as a fallback.
            if (DefaultPathTypeResolver.Instance.Resolve(typeToken) is { } registered)
                return registered;

            var metadataResult = metadata.TryGetType(XmlnsNamespaces.CursorialUi, typeToken);

            return metadataResult is { IsResolved: true } r
                       ? r.Type!.ClrType.UnderlyingSystemType
                       : null;
        }

        var prefix = typeToken[..colon];
        var local = typeToken[(colon + 1)..];

        // The prefix must be bound at the document root (an empty prefix = the default xmlns, keyed under "").
        if (!namespaces.TryGetValue(prefix, out var ns))
            return null; // an unbound prefix — resolution fails and the path parser reports the offending token

        var resolution = metadata.TryGetType(ns, local);
        return resolution.IsResolved ? resolution.Type!.SystemType() : null;
    }
}
