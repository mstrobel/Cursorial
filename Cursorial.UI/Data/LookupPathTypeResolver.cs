namespace Cursorial.UI.Data;

/// <summary>
/// An <see cref="IPathTypeResolver"/> over an explicit set of <c>type-token → owner <see cref="Type"/></c> mappings,
/// falling back to the registry default (<see cref="DefaultPathTypeResolver"/>) for any token not in the map.
/// <para>
/// The X4 XAML generator emits this for a binding whose path carries a namespace-PREFIXED type-qualified segment
/// (<c>(prefix:Type.Member)</c>): the prefixed owner types are resolved against the document xmlns at BUILD time and
/// baked in as <c>typeof(...)</c>, so the reflective binding resolves the owners identically to the loader's
/// xmlns-aware resolver — WITHOUT needing the runtime type registry (the owner need not have registered a
/// <c>UIProperty</c>: a plain CLR property qualified for clarity resolves too) or the document's prefix table. The
/// unprefixed / fallback tokens still resolve through the registry default, matching the loader's own fallback.
/// </para>
/// </summary>
public sealed class LookupPathTypeResolver : IPathTypeResolver
{
    private readonly (string Token, Type Owner)[] _map;

    /// <summary>Creates a resolver over the given <c>token → owner</c> mappings (the exact type token the path
    /// parser passes to <see cref="Resolve"/> — e.g. <c>"prefix:Grid"</c> — mapped to its resolved owner type).</summary>
    public LookupPathTypeResolver(params (string Token, Type Owner)[] map)
        => _map = map ?? throw new ArgumentNullException(nameof(map));

    /// <inheritdoc/>
    public Type? Resolve(string typeToken)
    {
        foreach (var (token, owner) in _map)
        {
            if (string.Equals(token, typeToken, StringComparison.Ordinal))
                return owner;
        }

        // Not a baked prefixed token (an unprefixed owner, or a token from elsewhere in the path) — resolve through
        // the registry default by simple name, exactly as the loader's resolver falls back for an unprefixed token.
        return DefaultPathTypeResolver.Instance.Resolve(typeToken);
    }
}
