using System.Globalization;

namespace Cursorial.UI.Xaml;

/// <summary>
/// The <see cref="IServiceProvider"/> full-lowering hands a custom <see cref="MarkupExtension"/> at its
/// <see cref="MarkupExtension.ProvideValue"/> call — the AOT-clean twin of the loader's
/// <c>XamlServiceProvider</c>. It supplies the provide-value target (<see cref="IProvideValueTarget"/>), the
/// document root (<see cref="IRootObjectProvider"/>), the enclosing name scope
/// (<see cref="INameScopeProvider"/>), the lexical ambient resource chain
/// (<see cref="IAmbientResources"/>), and the author position (<see cref="IXamlLineInfo"/>).
///
/// <para>The ambient chain is the enclosing <c>&lt;X.Resources&gt;</c> dictionaries, innermost-first,
/// terminated by the application tier (<c>App.Resources → App.Theme → ThemeContributions → BuiltIn</c>) —
/// a line-for-line twin of the loader's <c>XamlResourceScopeStack.TryResolve</c>, whose ambient tail
/// defaults to <see cref="ResourceScopes.ForApplication"/> for a plain load
/// (<c>XamlObjectGraphBuilder</c> constructs it as <c>ambient ?? ForApplication()</c>). The document
/// dictionaries are probed variant-agnostically (own entries only, via <c>ResourceDictionary.TryGetValue</c>);
/// the application tail is variant-aware, exactly as the loader.</para>
///
/// <para><see cref="TargetProperty"/> mirrors the loader: a registered <c>UIProperty</c> for a styled target,
/// or the target member's runtime <see cref="Type"/> for a CLR member (where the loader passes a
/// <c>XamlMember</c> carrying that type). A standalone entry (a dictionary/collection item) passes
/// <see langword="null"/> for both target fields, matching the loader's standalone provide-value call.</para>
/// </summary>
public sealed class LoweredExtensionServices :
    IServiceProvider,
    IProvideValueTarget,
    IRootObjectProvider,
    INameScopeProvider,
    IAmbientResources,
    IXamlLineInfo
{
    private readonly IReadOnlyList<ResourceDictionary>? _ambientScopes; // innermost-first

    /// <summary>Creates the service bundle for a lowered <see cref="MarkupExtension.ProvideValue"/> call.</summary>
    public LoweredExtensionServices(
        object? targetObject, object? targetProperty, object? rootObject, INameScope? nameScope,
        IReadOnlyList<ResourceDictionary>? ambientScopes = null, int lineNumber = 0, int linePosition = 0)
    {
        TargetObject = targetObject;
        TargetProperty = targetProperty;
        RootObject = rootObject;
        NameScope = nameScope;
        _ambientScopes = ambientScopes;
        LineNumber = lineNumber;
        LinePosition = linePosition;
    }

    /// <inheritdoc/>
    public object? TargetObject { get; }

    /// <inheritdoc/>
    public object? TargetProperty { get; }

    /// <inheritdoc/>
    public object? RootObject { get; }

    /// <inheritdoc/>
    public INameScope? NameScope { get; }

    /// <inheritdoc/>
    public int LineNumber { get; }

    /// <inheritdoc/>
    public int LinePosition { get; }

    /// <inheritdoc/>
    public bool TryFindResource(object key, out object? value)
    {
        // Mirror XamlResourceScopeStack.TryResolve exactly: the enclosing document dictionaries
        // innermost-first (variant-agnostic own-entry lookup), then the application ambient tail.
        if (_ambientScopes is not null)
            foreach (var dictionary in _ambientScopes)
                if (dictionary.TryGetValue(key, out value))
                    return true;

        for (var scope = ResourceScopes.ForApplication(); scope is not null; scope = scope.Parent)
            if (scope.TryGetResource(key, out value))
                return true;

        value = null;
        return false;
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IProvideValueTarget)) return this;
        if (serviceType == typeof(IRootObjectProvider)) return this;
        if (serviceType == typeof(INameScopeProvider)) return this;
        if (serviceType == typeof(IAmbientResources)) return this;
        if (serviceType == typeof(IXamlLineInfo)) return this;
        return null;
    }

    /// <summary>
    /// Coerces a <see cref="MarkupExtension.ProvideValue"/> result to a target member type — the runtime
    /// counterpart of the loader's <c>AssignResolvedValue</c> (matrix X121): a <see langword="string"/> result
    /// destined for a typed (non-string/object) slot runs the converter ladder, everything else passes
    /// through. Full-lowering wraps a custom extension's result with this before assigning it to a typed target,
    /// so a string-returning extension behaves identically to the loader instead of an unchecked cast.
    /// </summary>
    public static object? Coerce(object? value, Type targetType)
    {
        if (value is string text && targetType != typeof(string) && targetType != typeof(object))
        {
            var converter = XamlConverters.For(targetType) ?? XamlConverters.BclConverterForType(targetType);
            if (converter is not null)
                return converter.ConvertFromString(text, new XamlValueContext(CultureInfo.InvariantCulture, null, targetType, null, 0, 0));
        }
        return value;
    }
}
