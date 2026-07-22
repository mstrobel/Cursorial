using Cursorial.UI;

namespace Cursorial.Tests.UI.Resources;

/// <summary>
/// <see cref="ResourceScopes.ResolveStatic"/> / <see cref="ResourceScopes.TryResolveStatic"/> — the framework
/// resolver full-lowering emits for an external <c>{StaticResource}</c>. It is the structural twin of the
/// loader's <c>XamlResourceScopeStack.TryResolve</c> with the default <see cref="ResourceScopes.ForApplication"/>
/// ambient: the given dictionaries innermost-first (variant-agnostic own-entry lookup), then the application
/// tail. (No <c>UIApplication.Current</c> here, so the tail reduces to <c>CursorialTheme.BuiltIn</c> — enough to
/// pin the document-dict + shadowing + miss semantics without a host.)
/// </summary>
public sealed class ResolveStaticTests
{
    private static ResourceDictionary Dict(params (object Key, object? Value)[] entries)
    {
        var dict = new ResourceDictionary();
        foreach (var (key, value) in entries)
            dict[key] = value;
        return dict;
    }

    [Fact] // innermost-first: an inner dictionary shadows an outer one for the same key.
    public void ResolveStatic_InnermostFirst_ShadowsOuter()
    {
        var inner = Dict(("K", "inner"));
        var outer = Dict(("K", "outer"));
        Assert.Equal("inner", ResourceScopes.ResolveStatic("K", inner, outer)); // inner passed first
    }

    [Fact] // a key present only in an outer dictionary resolves through the chain.
    public void ResolveStatic_OuterDictHit()
    {
        var inner = Dict(("A", 1));
        var outer = Dict(("B", 2));
        Assert.Equal(2, ResourceScopes.ResolveStatic("B", inner, outer));
    }

    [Fact] // a genuinely-absent key returns false / throws ResourceNotFoundException (the loader's eager miss).
    public void ResolveStatic_Miss_ThrowsAndTryReturnsFalse()
    {
        var dict = Dict(("Known", 1));
        Assert.False(ResourceScopes.TryResolveStatic("Nope", new[] { dict }, out var value));
        Assert.Null(value);
        Assert.Throws<ResourceNotFoundException>(() => ResourceScopes.ResolveStatic("Nope", dict));
    }

    [Fact] // MergedDictionaries children are INVISIBLE to a direct scope lookup (TryGetValue is own-entries-only),
           // exactly as the loader's {StaticResource} — only Source=-folded entries are visible.
    public void ResolveStatic_MergedDictionaryChild_NotVisible()
    {
        var host = new ResourceDictionary();
        host.MergedDictionaries.Add(Dict(("Merged", "x")));
        Assert.False(ResourceScopes.TryResolveStatic("Merged", new[] { host }, out _));
        Assert.Throws<ResourceNotFoundException>(() => ResourceScopes.ResolveStatic("Merged", host));
    }

    [Fact] // a null ambient list resolves against the application tail alone (no NRE) — a genuinely-absent key misses.
    public void ResolveStatic_NullAmbient_WalksApplicationTailOnly()
    {
        Assert.False(ResourceScopes.TryResolveStatic("DefinitelyAbsentKey", null, out var value));
        Assert.Null(value);
    }
}
