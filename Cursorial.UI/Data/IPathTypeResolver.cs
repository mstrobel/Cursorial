namespace Cursorial.UI.Data;

/// <summary>
/// Resolves a type token in an attached-property path segment (<c>(Grid.Row)</c>) to a CLR type
/// (design doc §6.3). The XAML loader supplies an xmlns-aware resolver; the code-first default
/// (<see cref="DefaultPathTypeResolver"/>) resolves registered <c>UIProperty</c> owner short names
/// via the Fork A registry, surfacing ambiguity.
/// </summary>
public interface IPathTypeResolver
{
    /// <summary>The CLR type for <paramref name="typeToken"/>, or <see langword="null"/> if unresolvable.</summary>
    Type? Resolve(string typeToken);
}

/// <summary>
/// The code-first default <see cref="IPathTypeResolver"/> (design doc §6.3): resolves a type token
/// to the single registered <c>UIProperty</c> owner short name (ledger A15). An ambiguous short
/// name (more than one owner) throws <see cref="FormatException"/> listing the candidates; an
/// unknown token returns <see langword="null"/>.
/// </summary>
public sealed class DefaultPathTypeResolver : IPathTypeResolver
{
    /// <summary>The shared instance (stateless).</summary>
    public static readonly DefaultPathTypeResolver Instance = new();

    private DefaultPathTypeResolver() { }

    /// <inheritdoc/>
    public Type? Resolve(string typeToken)
    {
        ArgumentNullException.ThrowIfNull(typeToken);
        var owners = UIPropertyRegistry.FindOwnerTypesBySimpleName(typeToken);
        return owners switch
        {
            { Count: 0 } => null,
            { Count: 1 } => owners[0],
            _ => throw new FormatException(
                $"Type token '{typeToken}' is ambiguous: {string.Join(", ", owners.Select(static t => t.FullName))}. " +
                "Supply an IPathTypeResolver (or the XAML xmlns context) to disambiguate."),
        };
    }
}
