using System.Collections;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The <see cref="ResourceDictionary.ThemeDictionaries"/> map (design doc §11.2): variant-keyed
/// sub-dictionaries probed in the CD8 order. Keyed by <see cref="ThemeVariantKey"/> (value equality);
/// lazily allocated by its owner.
/// </summary>
public sealed class ThemeDictionaryCollection(ResourceDictionary owner) : IEnumerable<KeyValuePair<ThemeVariantKey, ResourceDictionary>>
{
    private readonly Dictionary<ThemeVariantKey, ResourceDictionary> _map = new();

    /// <summary>The number of variant sub-dictionaries.</summary>
    public int Count => _map.Count;

    /// <summary>Gets or sets the sub-dictionary for a variant key.</summary>
    public ResourceDictionary this[ThemeVariantKey key]
    {
        get => _map.TryGetValue(key, out var d) ? d : throw new KeyNotFoundException($"No theme dictionary for variant key '{key}'.");
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (owner.IsSealed)
                throw new InvalidOperationException("Cannot mutate ThemeDictionaries on a sealed ResourceDictionary (CD4).");

            value.Acquire(owner);
            if (_map.TryGetValue(key, out var old))
                old.Release(owner);
            _map[key] = value;
        }
    }

    /// <summary>Sets a sub-dictionary by its textual variant key (CD10).</summary>
    public ResourceDictionary this[string variantKey]
    {
        get => this[ThemeVariantKey.Parse(variantKey)];
        set => this[ThemeVariantKey.Parse(variantKey)] = value;
    }

    /// <summary>Whether a variant key is present.</summary>
    public bool ContainsKey(ThemeVariantKey key) => _map.ContainsKey(key);

    /// <summary>Resolves a key at an effective variant via the CD8 probe order, recursing into each candidate sub-dictionary's own/merged entries.</summary>
    internal bool TryGetResource(object key, ThemeVariant variant, out object? value)
    {
        foreach (var probeKey in ThemeVariantProbe.Order(variant))
        {
            if (_map.TryGetValue(probeKey, out var sub) && sub.TryGetResource(key, variant, out value))
                return true;
        }

        value = null;
        return false;
    }

    internal void SealAll()
    {
        foreach (var sub in _map.Values)
            sub.Seal();
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<ThemeVariantKey, ResourceDictionary>> GetEnumerator() => _map.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
