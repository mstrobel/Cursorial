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
