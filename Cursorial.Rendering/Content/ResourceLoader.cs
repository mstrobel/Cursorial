using System.Reflection;

namespace Cursorial.Rendering.Content;

/// <summary>
/// Default <see cref="IResourceLoader"/> implementation — handles the <c>embedded://</c>,
/// <c>file://</c>, and bare relative-path URI forms described on the interface.
/// </summary>
/// <remarks>
/// <para>
/// <b>URI scheme grammar.</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>embedded://&lt;AssemblyName&gt;/&lt;ResourceName&gt;</c> — manifest resource lookup. The
/// loader first searches assemblies already loaded in the current <see cref="AppDomain"/> by
/// simple name (case-insensitive); when no match is found it falls back to
/// <see cref="Assembly.Load(AssemblyName)"/>. The resource name is the path component with
/// any leading <c>'/'</c> stripped — pass either the default dotted form (the C# compiler
/// builds <c>{RootNamespace}.{relative.path.with.dots}</c> by default) or whatever
/// <c>&lt;LogicalName&gt;</c> was set in the consuming <c>.csproj</c>.
/// </description></item>
/// <item><description>
/// <c>file:///&lt;absolute-path&gt;</c> — absolute filesystem path. The <see cref="Uri.LocalPath"/>
/// is opened via <see cref="File.OpenRead(string)"/>.
/// </description></item>
/// <item><description>
/// Relative <see cref="Uri"/> (no scheme) — interpreted as a path relative to
/// <see cref="AppContext.BaseDirectory"/>. The combined path is then normalized via
/// <see cref="Path.GetFullPath(string)"/>.
/// </description></item>
/// </list>
/// <para>
/// <b>What it deliberately doesn't do.</b> No HTTP / HTTPS / data-URI support — those would
/// introduce real dependencies (network code, content-type negotiation, base64 parsing) for
/// not much gain in the typical "ship an icon with the app" use case. Apps that need them can
/// implement <see cref="IResourceLoader"/> themselves and pass it to <see cref="Icon"/>.
/// </para>
/// </remarks>
public sealed class ResourceLoader : IResourceLoader
{
    /// <summary>The URI scheme reserved for embedded-assembly-resource lookups.</summary>
    public const string EmbeddedScheme = "embedded";

    /// <summary>Process-wide default loader. Stateless and safe to share across threads.</summary>
    public static IResourceLoader Default { get; } = new ResourceLoader();

    /// <inheritdoc/>
    public Stream? TryOpen(Uri uri) => TryOpen(uri, out _);

    /// <inheritdoc/>
    public Stream? TryOpen(Uri? uri, out Exception? error)
    {
        if (uri is null)
            throw new ArgumentNullException(nameof(uri));

        error = null;

        if (!uri.IsAbsoluteUri)
            return TryOpenRelative(uri.OriginalString, out error);

        return uri.Scheme switch
               {
                   EmbeddedScheme => TryOpenEmbedded(uri, out error),
                   "file"         => TryOpenFile(uri, out error),
                   _              => null
               };
    }

    /// <inheritdoc/>
    public Stream Open(Uri uri) => IResourceLoader.Open(this, uri);

    /// <inheritdoc/>
    public byte[]? TryLoadBytes(Uri uri) => IResourceLoader.TryLoadBytes(this, uri, out _);

    /// <inheritdoc/>
    public byte[]? TryLoadBytes(Uri uri, out Exception? error) => IResourceLoader.TryLoadBytes(this, uri, out error);

    /// <summary>
    /// Build an <c>embedded://</c> URI for the given assembly + resource name. Convenience
    /// helper for callers that want the URI form without manually concatenating strings —
    /// resource names containing characters that need URI escaping (rare, but possible with
    /// custom <c>&lt;LogicalName&gt;</c> entries) are escaped here.
    /// </summary>
    public static Uri Embedded(string assemblyName, string resourceName)
    {
        ArgumentException.ThrowIfNullOrEmpty(assemblyName);
        ArgumentException.ThrowIfNullOrEmpty(resourceName);

        return new Uri($"{EmbeddedScheme}://{assemblyName}/{Uri.EscapeDataString(resourceName)}");
    }

    /// <summary>
    /// Build an <c>embedded://</c> URI for the given assembly + resource name. Convenience
    /// helper for callers that want the URI form without manually concatenating strings —
    /// resource names containing characters that need URI escaping (rare, but possible with
    /// custom <c>&lt;LogicalName&gt;</c> entries) are escaped here.
    /// </summary>
    public static Uri Embedded(Assembly assembly, string resourceName)
    {
        if (assembly is not {} a || a.GetName() is not { Name: { Length: > 0 } assemblyName })
            throw new ArgumentException("Assembly must be non-null and have a non-empty name.", nameof(assembly));

        ArgumentException.ThrowIfNullOrEmpty(resourceName);

        return new Uri($"{EmbeddedScheme}://{assemblyName}/{Uri.EscapeDataString(resourceName)}");
    }

    /// <summary>Convenience: build a <c>file://</c> URI for an absolute filesystem path.</summary>
    public static Uri File(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(absolutePath);
        return new Uri(Path.GetFullPath(absolutePath));
    }

    private static Stream? TryOpenEmbedded(Uri uri, out Exception? error)
    {
        error = null;

        // For an embedded:// URI, the authority is the assembly simple name, and the path is
        // the resource name. Uri's Authority can contain dots ("Cursorial.Rendering") and is
        // returned via uri.Host; AbsolutePath has a leading '/' that we strip.
        var assemblyName = uri.Host;
        var resourceName = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));

        if (string.IsNullOrEmpty(assemblyName) || string.IsNullOrEmpty(resourceName))
            return null;

        var assembly = FindAssemblyByName(assemblyName, out error);
        var stream = assembly?.GetManifestResourceStream(resourceName);
        
        if (stream is null) error = new ResourceLoadException(uri, error);

        return stream;
    }

    private static Stream? TryOpenFile(Uri uri, out Exception? error)
    {
        // Honor the IResourceLoader.TryLoadBytes contract ("returns null when the URI can't be resolved"): a malformed
        // path (NUL char ⇒ ArgumentException, over-long ⇒ PathTooLongException : IOException) must return null, not throw.
        error = null;

        try
        {
            return System.IO.File.OpenRead(uri.LocalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                         NotSupportedException)
        {
            error = ex;
            return null;
        }
    }

    private static Stream? TryOpenRelative(string relativePath, out Exception? error)
    {
        error = null;
        if (string.IsNullOrEmpty(relativePath)) return null;

        // Path.GetFullPath/Combine themselves throw on a malformed path — keep them inside the guard so a bad relative
        // URI resolves to null rather than throwing (the same contract as OpenFile).
        try
        {
            var resolved = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativePath));
            return System.IO.File.OpenRead(resolved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                         NotSupportedException)
        {
            error = ex;
            return null;
        }
    }

    private static Assembly? FindAssemblyByName(string assemblyName, out Exception? error)
    {
        error = null;

        // Already-loaded assemblies first — most consumers ship their icons in the same
        // assembly that's running, which is already in AppDomain.CurrentDomain.Assemblies.
        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(loaded.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                return loaded;
        }

        // Try a fresh load. Mostly useful when the resource lives in a plugin assembly that
        // hasn't been touched yet (which is rare in TUI contexts but doesn't hurt to support).
    
        // @formatter:off
        try { return Assembly.Load(new AssemblyName(assemblyName)); } 
        catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException or FileLoadException)
        {
            error = ex;
            return null;
        }

        // @formatter:on
    }
}