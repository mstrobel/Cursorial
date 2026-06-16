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
}
