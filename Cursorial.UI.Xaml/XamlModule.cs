using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

using Cursorial.UI;

namespace Cursorial.UI.Xaml;

/// <summary>
/// The <c>Cursorial.UI.Xaml</c> module initializer (matrix C-9 / X132): installs the
/// <see cref="ResourceDictionary.LoadCallback"/> so a <c>ResourceDictionary.Source="…"</c> routes
/// through the XAML loader (the existing P5 guard throws "no loader installed" until this runs — matrix
/// X133). Source resolution maps a <c>cursorial://&lt;assembly&gt;/&lt;path&gt;</c> URI to an embedded
/// resource via the registered <see cref="ResourceProvider"/> (overridable; the default reads the
/// loader-/app-assembly embedded XAML). The callback is process-global; tests save/restore it.
/// </summary>
public static class XamlModule
{
    /// <summary>
    /// The URI → XAML-text resolver the <see cref="ResourceDictionary.LoadCallback"/> uses (matrix X131).
    /// Defaults to embedded-resource resolution; an app/test may replace it.
    /// </summary>
    public static IXamlResourceProvider ResourceProvider { get; set; } = new EmbeddedXamlResourceProvider();

    // C-9: the library deliberately installs ResourceDictionary.LoadCallback at module load so a
    // ResourceDictionary.Source resolves the instant Cursorial.UI.Xaml is referenced (the P5 guard
    // throws until then). This is the sanctioned advanced use of [ModuleInitializer].
    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "C-9: the loader installs ResourceDictionary.LoadCallback at module load so Source= resolves once the assembly is referenced.")]
    internal static void Initialize()
    {
        // Install the loader-backed Source resolver once (C-9). Idempotent assignment.
        ResourceDictionary.LoadCallback ??= LoadDictionary;
    }

    /// <summary>
    /// Forces the loader callback to be installed (tests / explicit installs); idempotent. The
    /// <c>??=</c> is an intentionally-benign data race when called concurrently from user code: both
    /// writers install the identical <see cref="LoadDictionary"/> delegate (last-writer-wins, same
    /// value), so no observable inconsistency results.
    /// </summary>
    public static void EnsureInitialized() => ResourceDictionary.LoadCallback ??= LoadDictionary;

    /// <summary>
    /// Temporarily swaps <see cref="ResourceProvider"/>, restoring the previous value on dispose — the
    /// ambient-override pattern for tests (and any scoped resolver). Returns an <see cref="IDisposable"/>;
    /// the swap is process-global, so concurrent overrides race (the standard caveat for ambient statics).
    /// </summary>
    public static IDisposable UseResourceProvider(IXamlResourceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var previous = ResourceProvider;
        ResourceProvider = provider;
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope(IXamlResourceProvider previous) : IDisposable
    {
        public void Dispose() => ResourceProvider = previous;
    }

    private static ResourceDictionary LoadDictionary(Uri uri)
    {
        if (!ResourceProvider.TryGetXaml(uri, out string? xaml) || xaml is null)
            throw new InvalidOperationException(
                $"No XAML resource was found for '{uri}' (the registered XamlModule.ResourceProvider returned nothing).");

        var root = XamlLoader.Shared.Load(xaml, uri);
        if (root is ResourceDictionary dictionary)
            return dictionary;

        throw new InvalidOperationException(
            $"The XAML at '{uri}' loaded to a '{root.GetType().Name}', not a ResourceDictionary.");
    }
}

/// <summary>
/// The <c>ResourceDictionary.Source</c> URI → XAML-text resolver seam (matrix X131/C-9). The default
/// <see cref="EmbeddedXamlResourceProvider"/> resolves <c>cursorial://&lt;assembly&gt;/&lt;path&gt;</c>
/// to an embedded resource; a test installs an in-memory map.
/// </summary>
public interface IXamlResourceProvider
{
    /// <summary>Resolves <paramref name="uri"/> to XAML text; returns false on a miss.</summary>
    bool TryGetXaml(Uri uri, out string? xaml);
}

/// <summary>
/// The default <see cref="IXamlResourceProvider"/>: resolves <c>cursorial://&lt;assembly&gt;/&lt;path&gt;</c>
/// to an embedded manifest resource named <c>&lt;assembly&gt;.&lt;dotted-path&gt;</c>.
/// </summary>
public sealed class EmbeddedXamlResourceProvider : IXamlResourceProvider
{
    /// <inheritdoc/>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Resolves an embedded XAML resource by manifest name.")]
    public bool TryGetXaml(Uri uri, out string? xaml)
    {
        xaml = null;
        if (!string.Equals(uri.Scheme, "cursorial", StringComparison.OrdinalIgnoreCase))
            return false;

        string assemblyName = uri.Host;
        string path = uri.AbsolutePath.TrimStart('/');

        var assembly = ResolveAssembly(assemblyName);
        if (assembly is null)
            return false;

        string resourceName = $"{assemblyName}.{path.Replace('/', '.')}";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return false;

        using var reader = new StreamReader(stream);
        xaml = reader.ReadToEnd();
        return true;
    }

    private static Assembly? ResolveAssembly(string simpleName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                return assembly;
        }
        try
        {
            return Assembly.Load(simpleName);
        }
        catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException or FileLoadException)
        {
            return null;
        }
    }
}
