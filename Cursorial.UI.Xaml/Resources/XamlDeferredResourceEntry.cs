using Cursorial.UI;

namespace Cursorial.UI.Xaml;

/// <summary>
/// A Fork C lazy node-graph dictionary entry (matrix X141–X145; XD18): wraps a node-graph slice that
/// realizes on first lookup. The builder + slice head + the captured definition-site lexical scope are
/// retained so realization instantiates the slice with the WPF-faithful lexical resource context
/// (matrix X143). <c>ResourceDictionary</c>'s realize path resets the slot to <c>Deferred</c> on a
/// throwing realize, so this entry is retry-safe (matrix X144/X145) — it holds no consumed slice state.
/// </summary>
internal sealed class XamlDeferredResourceEntry : IDeferredResourceEntry
{
    private readonly XamlObjectGraphBuilder _builder;
    private readonly int _sliceHead;
    private readonly CapturedScopeChain _capturedScope;

    internal XamlDeferredResourceEntry(XamlObjectGraphBuilder builder, int sliceHead, CapturedScopeChain capturedScope)
    {
        _builder = builder;
        _sliceHead = sliceHead;
        _capturedScope = capturedScope;
    }

    /// <inheritdoc/>
    public object? Realize(IResourceScope lexicalScope)
        => _builder.RealizeDeferredEntry(_sliceHead, _capturedScope);
}
