using System.Runtime.CompilerServices;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// The XAML source position an object was instantiated from: the document URI and the 1-based
/// line/column of its element tag. Populated only when the loader ran with
/// <see cref="XamlLoaderOptions.TrackSourceInfo"/> (a designer/tooling opt-in — production
/// loads carry no tracking cost).
/// </summary>
public sealed class XamlSourceInfo
{
    internal XamlSourceInfo(Uri? source, int line, int column)
    {
        Source = source;
        Line = line;
        Column = column;
    }

    /// <summary>The document the object came from, when the load supplied one.</summary>
    public Uri? Source { get; }

    /// <summary>1-based line of the object's element tag.</summary>
    public int Line { get; }

    /// <summary>1-based column of the object's element tag.</summary>
    public int Column { get; }
}

/// <summary>
/// Maps XAML-instantiated objects to their <see cref="XamlSourceInfo"/>. Weakly keyed — entries
/// die with their instances, so tracking a designer session leaks nothing. Template-built
/// content registers against the template's own defining document (a user document for
/// in-document templates, the theme's for theme templates), which is exactly the provenance a
/// designer needs to decide between direct caret sync and logical-parent fallback.
/// </summary>
public static class XamlSourceRegistry
{
    private static readonly ConditionalWeakTable<object, XamlSourceInfo> Table = new();

    internal static void Register(object instance, Uri? source, int line, int column)
        => Table.AddOrUpdate(instance, new XamlSourceInfo(source, line, column));

    /// <summary>
    /// The source position <paramref name="instance"/> was instantiated from, or
    /// <see langword="null"/> when it wasn't XAML-created or the load didn't track sources.
    /// </summary>
    public static XamlSourceInfo? TryGetSourceInfo(object instance)
        => Table.TryGetValue(instance, out var info) ? info : null;
}
