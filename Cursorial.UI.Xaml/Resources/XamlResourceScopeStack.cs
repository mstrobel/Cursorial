using System.Text;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// The lexical ambient resource stack the loader maintains while instantiating (matrix XD9): a
/// push/pop stack of <see cref="ResourceDictionary"/>s (each <c>x:Resources</c> hop, innermost on top)
/// terminated by the optional <see cref="XamlLoadContext"/> ambient scope. <c>{StaticResource}</c>
/// resolves eagerly innermost-first against this stack (load-order explicit, forward-reference-free,
/// per XD9 — a key defined later in the same dictionary is NOT yet present, so it is a miss).
/// </summary>
internal sealed class XamlResourceScopeStack
{
    private readonly List<ResourceDictionary> _stack = new();
    private readonly IResourceScope? _ambient;

    internal XamlResourceScopeStack(IResourceScope? ambient) => _ambient = ambient;

    /// <summary>Pushes a dictionary scope (a resources hop); returns the new depth.</summary>
    internal int Push(ResourceDictionary dictionary)
    {
        _stack.Add(dictionary);
        return _stack.Count;
    }

    /// <summary>
    /// Pops scopes until the stack is back to <paramref name="targetDepth"/> entries (the depth read
    /// before the matching pushes — the desired final depth, no off-by-one to reason about).
    /// </summary>
    internal void PopDownTo(int targetDepth)
    {
        while (_stack.Count > targetDepth && _stack.Count > 0)
            _stack.RemoveAt(_stack.Count - 1);
    }

    /// <summary>The current scope depth (for a save/restore around a subtree).</summary>
    internal int Depth => _stack.Count;

    /// <summary>Resolves a key innermost-first against the dictionary stack, then the ambient scope.</summary>
    internal bool TryResolve(object key, out object? value)
    {
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            if (_stack[i].TryGetValue(key, out value))
                return true;
        }

        for (var scope = _ambient; scope is not null; scope = scope.Parent)
        {
            if (scope.TryGetResource(key, out value))
                return true;
        }

        value = null;
        return false;
    }

    /// <summary>Renders the searched chain hop-by-hop for a not-found diagnostic (matrix X114).</summary>
    internal string DescribeSearchedChain()
    {
        var sb = new StringBuilder("Searched scopes:");
        for (int i = _stack.Count - 1; i >= 0; i--)
            sb.Append(Environment.NewLine).Append("  resource dictionary (hop ").Append(_stack.Count - i).Append(')');
        if (_ambient is not null)
            sb.Append(Environment.NewLine).Append("  ambient (XamlLoadContext.AmbientResources)");
        return sb.ToString();
    }

    /// <summary>
    /// Captures the current dictionary stack as an immutable snapshot (a deferred template / dictionary
    /// entry's lexical resource capture, matrix X162/X143). The snapshot resolves innermost-first when
    /// re-pushed via <see cref="PushChain"/>; the ambient root is shared (it is immutable).
    /// </summary>
    internal CapturedScopeChain CaptureChain() => new(_stack.ToArray());

    /// <summary>Re-pushes a captured chain's dictionaries (outermost-first, so the deepest resolves first).</summary>
    internal void PushChain(CapturedScopeChain? chain)
    {
        if (chain is not { } c)
            return;
        foreach (var dict in c.Dictionaries)
            Push(dict);
    }
}

/// <summary>An immutable snapshot of a lexical dictionary chain (the definition-site capture, matrix X162/X143).</summary>
internal readonly struct CapturedScopeChain(ResourceDictionary[] dictionaries)
{
    /// <summary>The dictionaries, outermost-first.</summary>
    public ResourceDictionary[] Dictionaries { get; } = dictionaries;
}
