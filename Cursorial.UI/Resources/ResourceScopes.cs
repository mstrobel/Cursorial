using Cursorial.UI.Themes;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// Factory for the lexical <see cref="IResourceScope"/>s Fork C captures for StaticResource /
/// deferred-entry resolution (design doc §11.1). These resolve against an explicit ambient stack,
/// not the runtime chain walk (StaticResource is load-order explicit, forward-reference-free).
/// </summary>
public static class ResourceScopes
{
    /// <summary>A scope over a single dictionary's own entries, with an optional enclosing scope.</summary>
    public static IResourceScope ForDictionary(ResourceDictionary dictionary, IResourceScope? parent)
        => new DictionaryScope(dictionary, parent);

    /// <summary>A scope over an element's logical chain (its <c>Resources</c> and ancestors').</summary>
    public static IResourceScope ForElement(UIElement element)
        => new ElementScope(element);

    /// <summary>A scope over the application's resources (the default ambient root, design doc §11.4).</summary>
    public static IResourceScope ForApplication()
        => new ApplicationScope();

    /// <summary>
    /// Resolves a <c>{StaticResource}</c> key against a lexical ambient chain — the enclosing
    /// <c>&lt;X.Resources&gt;</c> dictionaries <paramref name="ambientInnermostFirst"/> probed
    /// variant-agnostically (own entries only), then the application tail
    /// (<c>App.Resources → App.Theme → ThemeContributions → BuiltIn</c>). This is the exact structural twin of
    /// the loader's <c>XamlResourceScopeStack.TryResolve</c> with its default <see cref="ForApplication"/>
    /// ambient — the resolver full-lowering emits for a <c>{StaticResource}</c> whose key is not a
    /// same-document entry local. Returns <see langword="false"/> on a miss.
    /// </summary>
    public static bool TryResolveStatic(object key, IReadOnlyList<ResourceDictionary>? ambientInnermostFirst, out object? value)
    {
        if (ambientInnermostFirst is not null)
            for (var i = 0; i < ambientInnermostFirst.Count; i++)
                if (ambientInnermostFirst[i].TryGetValue(key, out value))
                    return true;

        for (var scope = (IResourceScope?)ForApplication(); scope is not null; scope = scope.Parent)
            if (scope.TryGetResource(key, out value))
                return true;

        value = null;
        return false;
    }

    /// <summary>
    /// As <see cref="TryResolveStatic"/>, returning the resolved value (which may itself be
    /// <see langword="null"/>, e.g. an <c>{x:Null}</c> resource) and throwing
    /// <see cref="ResourceNotFoundException"/> on a miss — the loader's eager <c>{StaticResource}</c>
    /// resolution. The <c>params</c> array carries the ambient chain innermost-first.
    /// </summary>
    public static object? ResolveStatic(object key, params ResourceDictionary[] ambientInnermostFirst)
        => TryResolveStatic(key, ambientInnermostFirst, out var value)
               ? value
               : throw new ResourceNotFoundException(key, DescribeStaticChain(ambientInnermostFirst));

    /// <summary>
    /// Casts a resolved resource to <see cref="Data.IValueConverter"/> for a <c>Converter={StaticResource}</c>
    /// binding, throwing when it is <see langword="null"/> or the wrong type — the loud twin of the loader's
    /// <c>ResolveConverter</c> type check (so a null/non-converter resource fails identically, never a
    /// silent null converter that would bind unconverted).
    /// </summary>
    public static Data.IValueConverter RequireConverter(object? resolved, object key)
        => resolved as Data.IValueConverter
           ?? throw new InvalidOperationException(
               $"The binding Converter resource '{key}' resolved to '{resolved?.GetType().Name ?? "null"}', not an IValueConverter.");

    private static string[] DescribeStaticChain(ResourceDictionary[] ambient)
    {
        var chain = new string[ambient.Length + 1];
        for (var i = 0; i < ambient.Length; i++)
            chain[i] = $"resource dictionary (hop {i + 1})";
        chain[ambient.Length] = "application (App.Resources → Theme → ThemeContributions → BuiltIn)";
        return chain;
    }

    private sealed class DictionaryScope(ResourceDictionary dictionary, IResourceScope? parent) : IResourceScope
    {
        public IResourceScope? Parent => parent;

        public bool TryGetResource(object key, out object? value)
            => dictionary.TryGetValue(key, out value);
    }

    private sealed class ElementScope(UIElement element) : IResourceScope
    {
        public IResourceScope? Parent => null;

        public bool TryGetResource(object key, out object? value)
            => element.TryFindResource(key, out value);
    }

    private sealed class ApplicationScope : IResourceScope
    {
        public IResourceScope? Parent => null;

        public bool TryGetResource(object key, out object? value)
        {
            var variant = UIApplication.Current?.ActualThemeVariant ?? new ThemeVariant(ThemeBase.Dark, Output.ColorDepth.Truecolor);

            if (UIApplication.Current is { } app)
            {
                if (app.ResourcesOrNull is { } resources && resources.TryGetResource(key, variant, out value))
                    return true;
                if (app.Theme is { } theme && theme.TryGetResource(key, variant, out value))
                    return true;
            }

            // The assembly theme-contribution tier — between App.Theme and BuiltIn, matching the runtime chain
            // (ResourceExtensions.WalkApplicationTailOnce) so {StaticResource} and {DynamicResource} resolve
            // identically (and a contribution overrides BuiltIn for both).
            if (ThemeContributions.HasContributions && ThemeContributions.TryGetResource(key, variant, out value))
                return true;

            return CursorialTheme.BuiltIn.TryGetResource(key, variant, out value);
        }
    }
}
