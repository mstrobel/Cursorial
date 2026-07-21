namespace Cursorial.UI;

/// <summary>
/// The runtime counterpart of X4's generated <c>x:Name</c> fields (design doc §12.1): resolve a
/// named element from a name scope, throwing a descriptive error when absent.
/// </summary>
public static class NameScopeExtensions
{
    /// <summary>
    /// Resolves <paramref name="name"/> from <paramref name="scope"/> as a <typeparamref name="T"/>;
    /// throws naming the scope + name when absent or the wrong type (doc §12.1).
    /// </summary>
    public static T RequireControl<T>(this INameScope scope, string name) where T : UIElement
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(name);

        return scope.Find(name) switch
        {
            T element => element,
            null => throw new InvalidOperationException(
                $"No element named '{name}' was found in the name scope ({scope.GetType().Name})."),
            { } other => throw new InvalidOperationException(
                $"The element named '{name}' is a '{other.GetType().Name}', not the required '{typeof(T).Name}'.")
        };
    }

    /// <summary>
    /// Resolves <paramref name="name"/> from <paramref name="scope"/> as an <see cref="object"/>,
    /// throwing when absent — the runtime counterpart of the loader's <c>x:Reference</c>
    /// <c>ReferenceNotFound</c> Fatal. Full-lowering resolves every <c>{x:Reference}</c> and
    /// <c>{Binding Source={x:Reference}}</c> through this so a missing name fails loudly instead of
    /// resolving to <see langword="null"/> (which would silently rebind against the DataContext).
    /// </summary>
    public static object Require(this INameScope scope, string name)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(name);

        return scope.Find(name)
            ?? throw new InvalidOperationException(
                $"Could not resolve the x:Name reference '{name}' — no element with that name exists in this scope.");
    }
}
