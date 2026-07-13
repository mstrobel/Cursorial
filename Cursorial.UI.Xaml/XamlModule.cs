using System.Reflection;
using System.Runtime.CompilerServices;

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

    /// <summary>
    /// Design-time seam: when set, <see cref="XamlLoader.LoadComponent(object, XamlDocument, XamlLoadContext?)"/>
    /// re-sources a component's BAKED document from this hook (keyed by the document's source URI)
    /// before building — a designer previews the XAML the user is editing, not what the last build
    /// embedded into <c>InitializeComponent</c>. Return <see langword="null"/> for "no override".
    /// The default (<see langword="null"/>) costs a null check at runtime and nothing more.
    /// </summary>
    public static Func<Uri, string?>? LiveXamlSource { get; set; }

    /// <summary>
    /// The loader live-sourced documents parse with (default: the component's own). A designer
    /// sets a reflection-backed loader here: live edits routinely reference types and members the
    /// consuming assembly's GENERATED (closed-set) provider never saw at compile time — exactly
    /// the edits a preview exists to show.
    /// </summary>
    public static XamlLoader? LiveXamlLoader { get; set; }

    // C-9: the library deliberately installs ResourceDictionary.LoadCallback at module load so a
    // ResourceDictionary.Source resolves the instant Cursorial.UI.Xaml is referenced (the P5 guard
    // throws until then). This is the sanctioned advanced use of [ModuleInitializer].
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Install the loader-backed Source resolver once (C-9). Idempotent assignment.
        ResourceDictionary.LoadCallback ??= LoadDictionary;
    }
#pragma warning restore CA2255

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

        // Prefer the loader whose instantiation triggered this Source resolution (the LoadCallback seam
        // erases loader context — Cursorial.UI cannot reference the loader), so a document's merged
        // dictionaries load through the SAME loader/provider as the document itself — under strict
        // trimming that is the generated closed-set provider its InitializeComponent bound explicitly.
        // Shared (the ambient default) serves only loads user code initiates outside any loader.
        var root = (XamlLoader.Current ?? XamlLoader.Shared).Load(xaml, uri);
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
/// (alias: <c>embedded://</c>) to an embedded manifest resource named <c>&lt;dotted-path&gt;</c> —
/// the name the generator's embed target pins via <c>LogicalName</c>.
/// </summary>
public sealed class EmbeddedXamlResourceProvider : IXamlResourceProvider
{
    /// <inheritdoc/>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Resolves an embedded XAML resource by manifest name.")]
    public bool TryGetXaml(Uri uri, out string? xaml)
    {
        xaml = null;
        // Relative URIs have no scheme/host to resolve against (and .Scheme would throw) — the
        // loader resolves relatives against the containing document before this is ever asked.
        if (!uri.IsAbsoluteUri)
            return false;
        if (!string.Equals(uri.Scheme, "cursorial", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "embedded", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // System.Uri canonicalizes the authority to LOWERCASE (RFC host rules) — fine for hosts,
        // wrong for assembly simple names on case-sensitive file systems (Assembly.Load probes
        // by file name on Linux). Read the authority from the original string when possible.
        string assemblyName = XamlUriUtil.OriginalAuthority(uri) ?? uri.Host;
        string path = uri.AbsolutePath.TrimStart('/');

        var assembly = ResolveAssembly(assemblyName);
        if (assembly is null)
            return false;

        // The manifest name is the DOTTED RELATIVE PATH alone (the generator's embed target pins
        // LogicalName to exactly this) — the assembly is already named by the URI host, so
        // prefixing it into every resource name would be redundant and RootNamespace-sensitive.
        string resourceName = path.Replace('/', '.');
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
