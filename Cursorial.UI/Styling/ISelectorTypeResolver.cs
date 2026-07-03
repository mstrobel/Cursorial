// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// Resolves a selector type token (a simple, undotted identifier such as <c>Widget</c>) to a CLR
/// element type (style matrix SD1). <see cref="Selector.Parse"/> uses the framework default when
/// the caller passes <see langword="null"/>: simple names of element types known to the framework —
/// types with <c>UIProperty</c> registrations plus the exported <see cref="UIElement"/> types of
/// <c>Cursorial.UI</c>/<c>Cursorial.UI.Controls</c>. Fork C supplies its own resolver over the XAML
/// schema context at P6 — the parse API shape is shared.
/// </summary>
public interface ISelectorTypeResolver
{
    /// <summary>
    /// Resolves <paramref name="typeName"/> (ordinal comparison — SD2) to an element type.
    /// Returns <see langword="null"/> when the token is unknown (the parser reports an error naming
    /// the token). An <em>ambiguous</em> simple name also returns <see langword="null"/>, with the
    /// candidate types in <paramref name="ambiguousCandidates"/> — the parser reports an error
    /// listing their full names. Resolvers that cannot be ambiguous (a fixed map, a schema context
    /// with unique names) simply set <paramref name="ambiguousCandidates"/> to
    /// <see langword="null"/>; "not found" is then the only failure mode.
    /// </summary>
    /// <param name="typeName">The simple (undotted) type token from the selector text.</param>
    /// <param name="ambiguousCandidates">
    /// The candidate types when <paramref name="typeName"/> is ambiguous; <see langword="null"/>
    /// otherwise (including the not-found case).
    /// </param>
    Type? Resolve(string typeName, out IReadOnlyList<Type>? ambiguousCandidates);
}

/// <summary>
/// The SD1 default resolver: registry-known element types (any <see cref="UIElement"/> subtype
/// that has registered at least one <see cref="UIProperty"/>) plus the exported
/// <see cref="UIElement"/> types of the <c>Cursorial.UI</c> assembly.
/// </summary>
internal sealed class DefaultSelectorTypeResolver : ISelectorTypeResolver
{
    /// <summary>The shared instance (the resolver is stateless).</summary>
    internal static readonly DefaultSelectorTypeResolver Instance = new();

    private static Type[]? _exportedElementTypes;

    private DefaultSelectorTypeResolver()
    {
    }

    /// <inheritdoc/>
    public Type? Resolve(string typeName, out IReadOnlyList<Type>? ambiguousCandidates)
    {
        ArgumentNullException.ThrowIfNull(typeName);

        Type? single = null;
        List<Type>? candidates = null;

        foreach (var type in UIPropertyRegistry.FindOwnerTypesBySimpleName(typeName))
        {
            if (typeof(UIElement).IsAssignableFrom(type))
                Accumulate(type, ref single, ref candidates);
        }

        foreach (var type in ExportedElementTypes)
        {
            if (string.Equals(type.Name, typeName, StringComparison.Ordinal))
                Accumulate(type, ref single, ref candidates);
        }

        if (candidates is not null)
        {
            ambiguousCandidates = candidates;
            return null;
        }

        ambiguousCandidates = null;
        return single;
    }

    private static void Accumulate(Type type, ref Type? single, ref List<Type>? candidates)
    {
        if (single is null)
        {
            single = type;
            return;
        }

        if (ReferenceEquals(single, type) || candidates?.Contains(type) == true)
            return;

        candidates ??= [single];
        candidates.Add(type);
    }

    private static Type[] ExportedElementTypes
        => _exportedElementTypes ??= [.. typeof(UIElement).Assembly.GetExportedTypes()
            .Where(static t => typeof(UIElement).IsAssignableFrom(t))];
}
