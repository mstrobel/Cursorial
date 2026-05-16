namespace Cursorial.Rendering.Content;

/// <summary>
/// Resolves a <see cref="Uri"/> to its byte stream, hiding whether the resource lives in an
/// embedded assembly resource, a file on disk, a network endpoint, an encrypted bundle, etc.
/// Used by <see cref="Icon"/> (and any future content type that loads bytes from a URI) so the
/// "where does this come from" decision is one place to extend rather than scattered across
/// content implementations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default implementation.</b> <see cref="ResourceLoader.Default"/> handles three schemes:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>embedded://&lt;assembly&gt;/&lt;resource&gt;</c> — manifest resource lookup. The
/// authority is the assembly's simple name (e.g., <c>Cursorial.Rendering</c>); the path is
/// the manifest resource name as embedded. The resource name can be the default dotted form
/// (<c>MyApp.Resources.Icons.gear.png</c>) or whatever was set via <c>&lt;LogicalName&gt;</c>
/// in the consuming <c>.csproj</c>.
/// </description></item>
/// <item><description>
/// <c>file:///&lt;absolute-path&gt;</c> — absolute path on the local filesystem. Wraps
/// <see cref="System.IO.File.OpenRead(string)"/>.
/// </description></item>
/// <item><description>
/// Relative <see cref="Uri"/> (no scheme) — interpreted as a path relative to
/// <see cref="AppContext.BaseDirectory"/>. Useful for shipping assets alongside the
/// application binary.
/// </description></item>
/// </list>
/// <para>
/// <b>Custom loaders.</b> Implementations are free to support whatever schemes they want.
/// <see cref="Icon"/> accepts an optional <see cref="IResourceLoader"/> so apps can plug in
/// loaders for network resources, hot-reload-from-disk dev tooling, or encrypted bundles
/// without modifying the content type.
/// </para>
/// </remarks>
public interface IResourceLoader
{
    /// <summary>
    /// Open a readable stream for the resource at <paramref name="uri"/>, or return
    /// <see langword="null"/> when the URI can't be resolved (unsupported scheme, missing
    /// resource, I/O error). Callers must dispose the returned stream.
    /// </summary>
    /// <remarks>
    /// Implementations should not throw for "not found" or "scheme unsupported" — those
    /// are expected, gracefully handleable outcomes that callers want to detect via a null
    /// return. Throw only for genuinely unexpected failures (corrupted assembly, permission
    /// violation we couldn't have predicted, etc.).
    /// </remarks>
    Stream? TryOpen(Uri uri);

    /// <summary>
    /// Load the entire resource into a byte array. Returns <see langword="null"/> when the
    /// URI can't be resolved. Convenience over <see cref="TryOpen"/>.
    /// </summary>
    byte[]? TryLoadBytes(Uri uri)
    {
        using var stream = TryOpen(uri);

        if (stream is null)
            return null;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
