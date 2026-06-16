namespace Cursorial.UI.Xaml;

/// <summary>
/// The namespace-aware <see cref="ISelectorTypeResolver"/> for XAML-loaded selectors (and prefixed Style
/// <c>TargetType</c>s). A <c>prefix|Local</c> token resolves <c>Local</c> in the namespace the document's
/// ROOT bound to <c>prefix</c> — the document-level xmlns table (the top-level-only policy, CUR2004, makes
/// it unambiguous) — via the loader's <see cref="IXamlTypeMetadataProvider"/>, the same resolution element
/// types use. A bare, unqualified token falls back to <see cref="Selector.DefaultTypeResolver"/> (SD1:
/// registry-known + exported element types by simple name) so existing simple-name selectors are unchanged.
/// Ambiguity never arises on the qualified path — the namespace pins the type.
/// </summary>
internal sealed class XamlSelectorTypeResolver : ISelectorTypeResolver
{
    private readonly IReadOnlyDictionary<string, string> _namespaces;
    private readonly IXamlTypeMetadataProvider _metadata;

    internal XamlSelectorTypeResolver(IReadOnlyDictionary<string, string> namespaces, IXamlTypeMetadataProvider metadata)
    {
        _namespaces = namespaces;
        _metadata = metadata;
    }

    public Type? Resolve(string typeName, out IReadOnlyList<Type>? ambiguousCandidates)
    {
        int pipe = typeName.IndexOf('|');
        if (pipe < 0)
            return Selector.DefaultTypeResolver.Resolve(typeName, out ambiguousCandidates);

        ambiguousCandidates = null;
        string prefix = typeName.Substring(0, pipe);
        string local = typeName.Substring(pipe + 1);

        if (!_namespaces.TryGetValue(prefix, out var ns))
            return null; // an unbound prefix — the parser reports a qualified-type error at the token

        var resolution = _metadata.TryGetType(ns, local);
        return resolution.IsResolved ? resolution.Type!.ClrType : null;
    }
}
