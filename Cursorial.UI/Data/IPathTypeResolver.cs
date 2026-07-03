namespace Cursorial.UI.Data;

/// <summary>
/// Resolves the owner type token of a type-qualified path segment (<c>(Owner.Member)</c> — an attached property
/// like <c>(Grid.Row)</c>, OR a regular property qualified by owner type for disambiguation/clarity) to a CLR type
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
                "Supply an IPathTypeResolver (or the XAML xmlns context) to disambiguate.")
        };
    }
}
