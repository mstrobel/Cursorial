using System.Reflection;

using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering.Fragments;

namespace Cursorial.Rendering.Content;

/// <summary>
/// A small image with a glyph fallback — typically a 1×1 or 2×1 cell icon (wide-glyph footprint)
/// that renders as a real picture when the terminal supports a graphics protocol, and as a
/// configured glyph (emoji or symbol) when it doesn't or when the image fails to load.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resource resolution.</b> Icons identify their image payload by <see cref="Uri"/>. The
/// default <see cref="IResourceLoader"/> (<see cref="ResourceLoader.Default"/>) understands
/// three URI shapes:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>embedded://&lt;AssemblyName&gt;/&lt;ResourceName&gt;</c> — manifest resource in the named
/// assembly. The resource name matches what the C# compiler produces (or whatever
/// <c>&lt;LogicalName&gt;</c> was set in the <c>.csproj</c>).
/// </description></item>
/// <item><description><c>file:///&lt;absolute-path&gt;</c> — file on the local filesystem.</description></item>
/// <item><description>Relative URI — resolved against <see cref="AppContext.BaseDirectory"/>.</description></item>
/// </list>
/// <para>
/// Apps that need different schemes (network downloads, encrypted bundles, virtual filesystems)
/// implement <see cref="IResourceLoader"/> and pass an instance to the constructor.
/// </para>
/// <para>
/// <b>Fallback chain.</b> Loading happens once, at construction. On success the icon stores
/// the bytes and renders via <see cref="Image"/>'s capability-aware chain — Kitty graphics →
/// iTerm2 inline → glyph placeholder. On load failure or when no graphics protocol is
/// available, the configured <see cref="FallbackGlyph"/> renders in place of the picture; the
/// placeholder block is filled with <see cref="FallbackStyle"/>. <see cref="ImageLoaded"/>
/// exposes which branch the icon took, useful for tests and diagnostics.
/// </para>
/// <para>
/// <b>Format inference.</b> The image's format is inferred from the URI's file extension
/// (<c>.png</c> / <c>.jpg</c> / <c>.jpeg</c> / <c>.gif</c>). Unknown extensions default to
/// <see cref="ImageFormat.Png"/> — fine for most cases, but consumers shipping JPEG / GIF icons
/// with non-standard extensions should pass an explicit <see cref="ImageFormat"/> to the
/// constructor.
/// </para>
/// </remarks>
public sealed class Icon : IContent
{
    private readonly Image? _image;

    /// <summary>Construct an icon from a resource URI.</summary>
    /// <param name="resourceUri">URI identifying the image payload. See class remarks for supported schemes.</param>
    /// <param name="fallbackGlyph">Glyph (emoji / symbol / character) rendered when the image isn't available. Wide glyphs occupy the full <paramref name="renderSize"/>.</param>
    /// <param name="fallbackStyle">Style applied to the glyph-fallback placeholder. <c>default</c> reuses the painter's style at <see cref="Paint"/>.</param>
    /// <param name="renderSize">Cell footprint the icon occupies. <c>default</c> means 2×1 — the natural fit for emoji-shaped fallbacks. A row span of 0 is auto-derived from the column count.</param>
    /// <param name="format">Image format. <see langword="null"/> infers from the URI's file extension.</param>
    /// <param name="loader">Resource loader. <see langword="null"/> uses <see cref="ResourceLoader.Default"/>.</param>
    public Icon(Uri resourceUri,
                string fallbackGlyph,
                in Style fallbackStyle = default,
                Size renderSize = default,
                ImageFormat? format = null,
                IResourceLoader? loader = null)
    {
        ArgumentNullException.ThrowIfNull(resourceUri);
        ArgumentNullException.ThrowIfNull(fallbackGlyph);

        ResourceUri = resourceUri;
        FallbackGlyph = fallbackGlyph;
        FallbackStyle = fallbackStyle;
        RenderSize = renderSize.IsEmpty ? new Size(2, 1) : renderSize;
        Format = format ?? InferFormat(resourceUri);
        Loader = loader ?? ResourceLoader.Default;

        var bytes = Loader.TryLoadBytes(resourceUri);

        if (bytes is not null)
        {
            var data = new ImageData(bytes, Format, RenderSize);
            _image = new Image(data, FallbackStyle, FallbackGlyph);
        }
    }

    /// <summary>The URI the icon's image bytes were loaded from.</summary>
    public Uri ResourceUri { get; }

    /// <summary>The cell rectangle the icon paints into.</summary>
    public Size RenderSize { get; }

    /// <summary>The glyph rendered when the image is unavailable or no graphics protocol is supported.</summary>
    public string FallbackGlyph { get; }

    /// <summary>Style applied to the glyph-fallback placeholder.</summary>
    public Style FallbackStyle { get; }

    /// <summary>Image format used to decode the loaded bytes.</summary>
    public ImageFormat Format { get; }

    /// <summary>The resource loader that was used to fetch the image bytes.</summary>
    public IResourceLoader Loader { get; }

    /// <summary>True when the image bytes loaded successfully at construction.</summary>
    public bool ImageLoaded => _image is not null;

    /// <inheritdoc/>
    public Size Measure(Size availableSpace, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        // The icon's footprint is fixed at construction. Clamp to availableSpace so the parent
        // sees a consistent "this is the largest box I'll use" answer.
        int cols = Math.Min(RenderSize.Columns, availableSpace.Columns);
        int rows = Math.Min(RenderSize.Rows, availableSpace.Rows);
        return new Size(cols, rows);
    }

    /// <inheritdoc/>
    public Rect Paint(CellBuffer buffer, Rect bounds, in Style style, OutputCapabilities capabilities)
    {
        if (_image is not null)
        {
            // Image owns the capability-aware decision — Kitty graphics if supported, otherwise
            // iTerm2 inline images, otherwise the placeholder rectangle with the FallbackGlyph
            // centered.
            return _image.Paint(buffer, bounds, in style, capabilities);
        }

        // Image bytes never loaded — paint the placeholder rectangle directly via a glyph-only
        // path. Build a minimal ImageData stand-in just to drive the placeholder logic
        // through Image; reuses the same width/height/glyph rendering code, no separate
        // glyph-painting branch.
        var stub = new ImageData([], Format, RenderSize);
        var placeholder = new Image(stub, FallbackStyle, FallbackGlyph);
        return placeholder.Paint(buffer, bounds, in style, capabilities);
    }

    /// <summary>
    /// Convenience constructor — build an icon backed by an embedded assembly resource.
    /// Equivalent to <c>new Icon(ResourceLoader.Embedded(assemblyName, resourceName), …)</c>.
    /// </summary>
    public static Icon FromEmbedded(string assemblyName,
                                    string resourceName,
                                    string fallbackGlyph,
                                    in Style fallbackStyle = default,
                                    Size renderSize = default,
                                    ImageFormat? format = null)
        => new(ResourceLoader.Embedded(assemblyName, resourceName), fallbackGlyph, in fallbackStyle, renderSize, format);

    /// <summary>
    /// Convenience constructor — build an icon backed by an embedded assembly resource.
    /// Equivalent to <c>new Icon(ResourceLoader.Embedded(assemblyName, resourceName), …)</c>.
    /// </summary>
    public static Icon FromEmbedded(Assembly assembly,
                                    string resourceName,
                                    string fallbackGlyph,
                                    in Style fallbackStyle = default,
                                    Size renderSize = default,
                                    ImageFormat? format = null)
        => new(ResourceLoader.Embedded(assembly, resourceName), fallbackGlyph, in fallbackStyle, renderSize, format);

    /// <summary>
    /// Convenience constructor — build an icon backed by a file on disk. The path may be
    /// absolute or relative to <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    public static Icon FromFile(string path,
                                string fallbackGlyph,
                                Size renderSize = default,
                                in Style fallbackStyle = default,
                                ImageFormat? format = null)
    {
        var uri = Path.IsPathRooted(path) ? ResourceLoader.File(path) : new Uri(path, UriKind.Relative);
        return new Icon(uri, fallbackGlyph, in fallbackStyle, renderSize, format);
    }

    private static ImageFormat InferFormat(Uri uri)
    {
        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString;
        var ext = Path.GetExtension(path).ToLowerInvariant();

        return ext switch
               {
                   ".png"            => ImageFormat.Png,
                   ".jpg" or ".jpeg" => ImageFormat.Jpeg,
                   ".gif"            => ImageFormat.Gif,
                   _                 => ImageFormat.Png
               };
    }
}