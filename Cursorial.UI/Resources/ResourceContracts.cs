// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>The granularity of a <see cref="ResourceDictionary.Changed"/> pulse (design doc §11.1).</summary>
public enum ResourceChangeKind : byte
{
    /// <summary>A single key changed (insert / overwrite / remove); <see cref="ResourcesChangedEventArgs.Key"/> is set.</summary>
    Keyed,

    /// <summary>A coarse change — a batched <see cref="ResourceDictionary.BeginUpdate"/>, a Styles mutation, or a variant flip.</summary>
    CatchAll
}

/// <summary>The payload of a <see cref="ResourceDictionary.Changed"/> / <c>UIApplication.ResourcesChanged</c> event.</summary>
/// <param name="Kind">Whether a single key or a catch-all set changed.</param>
/// <param name="Key">The changed key for <see cref="ResourceChangeKind.Keyed"/>; <see langword="null"/> for <see cref="ResourceChangeKind.CatchAll"/>.</param>
public readonly record struct ResourcesChangedEventArgs(ResourceChangeKind Kind, object? Key);

/// <summary>
/// A keyed resource source (design doc §11.1) — implemented by <see cref="UIElement"/> (lazy
/// <c>Resources</c>) and <c>UIApplication</c>. <see cref="HasResources"/> lets the chain walk skip
/// the per-node probe without lazy-allocating an empty dictionary.
/// </summary>
public interface IResourceHost
{
    /// <summary>The host's resource dictionary (lazily allocated).</summary>
    ResourceDictionary Resources { get; }

    /// <summary>Whether a dictionary has been allocated and carries entries — skips the chain hop when false.</summary>
    bool HasResources { get; }
}

/// <summary>
/// The runtime contract for Fork C's parse-time-checked lazy node-graph slices (design doc §11.1).
/// Named <c>IDeferredResourceEntry</c> so Fork C's markup-extension <c>IDeferredValue</c> name stays
/// with the <c>AttachTo</c> seam. Realizes at most once on success.
/// </summary>
public interface IDeferredResourceEntry
{
    /// <summary>Materializes the value against the lexical scope Fork C captured at parse time.</summary>
    object? Realize(IResourceScope lexicalScope);
}

/// <summary>
/// A lexical resource scope (the Fork C ambient stack, design doc §11.1) — the StaticResource /
/// deferred-entry resolution surface, distinct from the runtime chain walk.
/// </summary>
public interface IResourceScope
{
    /// <summary>Resolves a key against this scope only (the parent provides the next hop).</summary>
    bool TryGetResource(object key, out object? value);

    /// <summary>The enclosing scope, or <see langword="null"/> at the ambient root.</summary>
    IResourceScope? Parent { get; }
}

/// <summary>The collision-free implicit-template key (design doc §11.1; the S8 DataTemplate lookup consumes it).</summary>
/// <param name="DataType">The data type a <c>DataTemplate</c> is keyed for.</param>
public readonly record struct DataTemplateKey(Type DataType);

/// <summary>A keyed DynamicResource currency (design doc §11.5) — a <c>Setter.Value</c> / <c>{DynamicResource}</c> producer marker, never passed through <c>SetValue</c>.</summary>
/// <param name="Key">The resource key.</param>
public readonly record struct ResourceReference(object Key);

/// <summary>
/// Thrown by <see cref="ResourceExtensions.FindResource"/> when a key resolves nowhere; the message
/// renders the searched chain hop by hop (design doc §11.4).
/// </summary>
public sealed class ResourceNotFoundException : Exception
{
    internal ResourceNotFoundException(object key, IReadOnlyList<string> searchedScopes)
        : base($"Resource '{key}' was not found. Searched scopes:{Environment.NewLine}{string.Join(Environment.NewLine, searchedScopes)}")
    {
        Key = key;
        SearchedScopes = searchedScopes;
    }

    /// <summary>The key that was not found.</summary>
    public object Key { get; }

    /// <summary>One rendered line per hop searched, in walk order.</summary>
    public IReadOnlyList<string> SearchedScopes { get; }
}
