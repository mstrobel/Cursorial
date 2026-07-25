// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// A code-first <see cref="IDeferredResourceEntry"/>: a delegate that materializes the entry value on first
/// access. The AOT/full-lowering twin of the loader's <c>XamlDeferredResourceEntry</c> — full-lowering emits one
/// per keyed dictionary entry so entries realize lazily (only what is read is built), intra-dictionary FORWARD
/// references resolve (every sibling slot exists before any realizes), and the snapshot / cycle-throw / retry
/// semantics come for free from <see cref="ResourceDictionary"/>'s realization path.
/// </summary>
/// <remarks>
/// The build delegate captures the dictionaries it resolves against (the enclosing <c>&lt;X.Resources&gt;</c>
/// locals) at emit time, so it does NOT consult the <see cref="IResourceScope"/> the dictionary passes to
/// <see cref="Realize"/> — exactly as the loader's entry ignores it in favour of its captured definition-site
/// chain. A <c>{StaticResource}</c> inside the delegate resolves through
/// <see cref="ResourceScopes.ResolveStatic"/> against those captured dictionaries (whose deferred slots realize on
/// contact), which is what makes a forward reference resolve.
/// </remarks>
public sealed class FuncDeferredResourceEntry(System.Func<object?> realize) : IDeferredResourceEntry
{
    private readonly System.Func<object?> _realize =
        realize ?? throw new System.ArgumentNullException(nameof(realize));

    /// <inheritdoc/>
    public object? Realize(IResourceScope lexicalScope) => _realize();
}
