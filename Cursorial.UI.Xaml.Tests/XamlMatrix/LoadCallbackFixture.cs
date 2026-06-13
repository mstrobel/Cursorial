using System;
using System.Collections.Generic;

using Cursorial.UI;
using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// The xUnit collection that serializes tests touching the process-global
/// <see cref="ResourceDictionary.LoadCallback"/> / <see cref="XamlModule.ResourceProvider"/> (matrix
/// §16: the Source= rows save/restore the static).
/// </summary>
[CollectionDefinition(nameof(LoadCallbackCollection))]
public sealed class LoadCallbackCollection : ICollectionFixture<LoadCallbackFixture>;

/// <summary>Ensures the module initializer ran so <see cref="ResourceDictionary.LoadCallback"/> is installed.</summary>
public sealed class LoadCallbackFixture
{
    public LoadCallbackFixture() => XamlModule.EnsureInitialized();
}

/// <summary>A <see langword="using"/>-scoped swap of <see cref="XamlModule.ResourceProvider"/> (restored on dispose).</summary>
internal readonly struct LoadCallbackScope : IDisposable
{
    private readonly IXamlResourceProvider _saved;

    private LoadCallbackScope(IXamlResourceProvider saved) => _saved = saved;

    public static LoadCallbackScope WithProvider(IXamlResourceProvider provider)
    {
        XamlModule.EnsureInitialized();
        var saved = XamlModule.ResourceProvider;
        XamlModule.ResourceProvider = provider;
        return new LoadCallbackScope(saved);
    }

    public void Dispose() => XamlModule.ResourceProvider = _saved;
}

/// <summary>An in-memory URI → XAML-text provider for the Source= rows (matrix X131).</summary>
internal sealed class InMemoryProvider : IXamlResourceProvider
{
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryProvider(params (string Uri, string Xaml)[] entries)
    {
        foreach (var (uri, xaml) in entries)
            _map[uri] = xaml;
    }

    public bool TryGetXaml(Uri uri, out string? xaml)
    {
        if (_map.TryGetValue(uri.ToString(), out var found))
        {
            xaml = found;
            return true;
        }
        xaml = null;
        return false;
    }
}
