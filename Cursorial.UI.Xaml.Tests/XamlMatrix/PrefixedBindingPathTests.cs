using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

// #3 (stage 1) — a binding path's TYPE-QUALIFIED segment owner may be namespace-prefixed, `(prefix:Type.Member)`.
// Previously the type-qualified parse threw "source casts unsupported" on the ':' (conflating a prefixed owner with a
// (local:T)x cast). Now the ':' is part of the type token, resolved via the xmlns-aware XamlPathTypeResolver (the
// document root's prefix→uri table + the loader metadata); a bare token still resolves via the registry default. The
// qualification is not assumed attached — it resolves the OWNER type only (member stays runtime-resolved).
public sealed class PrefixedBindingPathTests
{
    private const string Ui = "https://cursorial.dev/ui";

    private static XamlPathTypeResolver Resolver(params (string Prefix, string Uri)[] namespaces)
        => new(namespaces.ToDictionary(n => n.Prefix, n => n.Uri), ReflectionXamlMetadata.Instance);

    [Fact] // A prefixed type token resolves via the document xmlns; a bare token via the registry; an unbound prefix misses.
    public void Resolver_ResolvesPrefixedAndBareTokens()
    {
        var resolver = Resolver(("ui", Ui));

        Assert.Equal(typeof(Grid), resolver.Resolve("ui:Grid"));   // prefixed → xmlns lookup
        Assert.Equal(typeof(Grid), resolver.Resolve("Grid"));      // bare → DefaultPathTypeResolver (registry owner)
        Assert.Null(resolver.Resolve("nope:Grid"));                // unbound prefix → miss
    }

    [Fact] // The parser now ACCEPTS the prefixed attached segment and resolves its owner (was a hard throw).
    public void Parse_PrefixedAttachedSegment_ResolvesOwner()
    {
        var resolver = Resolver(("ui", Ui));

        var path = BindingPath.Parse("(ui:Grid.Row)", resolver);
        // ToString() renders the RESOLVED owner's simple name — proof the prefix resolved to the Grid type.
        Assert.Equal("(Grid.Row)", path.ToString());
    }

    [Fact] // An unbound prefix fails at resolution with a position-carrying FormatException (not a silent miss).
    public void Parse_UnboundPrefix_Throws()
    {
        var resolver = Resolver(); // no namespaces bound
        var ex = Assert.Throws<FormatException>(() => BindingPath.Parse("(ui:Grid.Row)", resolver));
        Assert.Contains("ui:Grid", ex.Message);
    }

    [Fact] // A genuine cast form — (Type) with no '.member' — is still rejected as unsupported (regression guard).
    public void Parse_CastForm_StillRejected()
    {
        var resolver = Resolver(("ui", Ui));
        var ex = Assert.Throws<FormatException>(() => BindingPath.Parse("(ui:Grid)x", resolver));
        Assert.Contains("cast", ex.Message);
    }
}
