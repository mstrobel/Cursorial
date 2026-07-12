using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Cursorial.UI.Xaml;

/// <summary>
/// The public XAML runtime loader (matrix §0.2 / doc §4.2). <see cref="Parse(string, Uri?)"/> is stage 1 —
/// pure, thread-safe, host-free (the frontend parser). <see cref="Load(XamlDocument, XamlLoadContext?)"/>
/// is stage 2 — instantiation on the single UI thread. <see cref="Load(string, Uri?)"/> and the typed
/// overloads parse + instantiate in one call. <see cref="LoadComponent(object)"/> populates an existing
/// instance via the <c>x:Class</c> convention (deferred resolution of the embedded document is X2+; the
/// explicit-URI / instance overloads are present now).
/// </summary>
// The loader is provider-agnostic — it calls the metadata provider through the IXamlTypeMetadataProvider
// interface, so its reflection-ness lives in the PROVIDER, not here. With a generated (trim/AOT-clean)
// provider and standard markup, the load path is reflection-free; the reflective surfaces are
// ReflectionXamlMetadata ([Requires*]), the embedded-resource LoadComponent(object), and custom
// markup-extension instantiation (a documented v1 AOT limitation). The reflection DEFAULT is feature-switched
// in XamlLoaderOptions so NativeAOT can drop it.
public sealed class XamlLoader
{
    private readonly XamlLoaderOptions _options;
    private readonly ConcurrentDictionary<Uri, XamlDocument> _documentCache = new();

    /// <summary>The process-wide default-options loader.</summary>
    public static XamlLoader Shared { get; } = new();

    /// <summary>Creates a loader with default options (<see cref="ReflectionXamlMetadata.Instance"/>).</summary>
    public XamlLoader() : this(new XamlLoaderOptions()) { }

    /// <summary>Creates a loader with explicit options.</summary>
    public XamlLoader(XamlLoaderOptions options)
        => _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>The loader's options.</summary>
    public XamlLoaderOptions Options => _options;

    // ── Stage 1: parse (pure, thread-safe) ─────────────────────────────────────────────────────────

    /// <summary>Parses XAML text into an immutable document (stage 1; no host, thread-safe — matrix XD20).</summary>
    public XamlDocument Parse(string xml, Uri? source = null)
    {
        ArgumentNullException.ThrowIfNull(xml);
        return XamlFrontend.Parse(xml, _options.ToParseOptions(), source);
    }

    /// <summary>Parses XAML from a stream into an immutable document.</summary>
    public XamlDocument Parse(Stream xml, Uri? source = null)
    {
        ArgumentNullException.ThrowIfNull(xml);
        return XamlFrontend.Parse(xml, _options.ToParseOptions(), source);
    }

    /// <summary>Parses XAML from a text reader into an immutable document (the reader is streamed, not materialized).</summary>
    public XamlDocument Parse(TextReader xml, Uri? source = null)
    {
        ArgumentNullException.ThrowIfNull(xml);
        return XamlFrontend.Parse(xml, _options.ToParseOptions(), source);
    }

    /// <summary>
    /// Returns a cached document for <paramref name="uri"/>, parsing <paramref name="xml"/> on a miss
    /// (matrix X177). The cache is per-loader and keyed by URI; immutable documents are safe to share.
    /// </summary>
    public XamlDocument GetOrParse(Uri uri, string xml)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(xml);
        return _documentCache.GetOrAdd(uri, u => Parse(xml, u));
    }

    // ── Stage 2: instantiate (UI thread) ──────────────────────────────────────────────────────────

    /// <summary>Instantiates a parsed document into a fresh object graph (stage 2; UI thread).</summary>
    public object Load(XamlDocument document, XamlLoadContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ThrowIfDocumentFailed(document);

        var builder = new XamlObjectGraphBuilder(document, _options, context?.Source ?? document.SourceUri, context?.AmbientResources);
        return builder.Build(context?.RootInstance);
    }

    /// <summary>Instantiates a parsed document, casting the root to <typeparamref name="T"/>.</summary>
    public T Load<T>(XamlDocument document, XamlLoadContext? context = null)
    {
        var root = Load(document, context);
        if (root is T typed)
            return typed;
        throw new InvalidOperationException(
            $"The loaded root is a '{root.GetType().Name}', not the requested '{typeof(T).Name}'.");
    }

    // ── One-shot parse + instantiate ──────────────────────────────────────────────────────────────

    /// <summary>Parses + instantiates XAML text (matrix X67).</summary>
    public object Load(string xml, Uri? source = null) => Load(Parse(xml, source), source is null ? null : new XamlLoadContext { Source = source });

    /// <summary>Parses + instantiates XAML text, casting the root to <typeparamref name="T"/>.</summary>
    public T Load<T>(string xml, Uri? source = null) => Load<T>(Parse(xml, source), source is null ? null : new XamlLoadContext { Source = source });

    /// <summary>Parses + instantiates from a stream.</summary>
    public object Load(Stream xml, Uri? source = null) => Load(Parse(xml, source));

    /// <summary>Parses + instantiates from a text reader.</summary>
    public object Load(TextReader xml, Uri? source = null) => Load(Parse(xml, source));

    // ── LoadComponent (matrix X105–X107 / XD17) ───────────────────────────────────────────────────

    /// <summary>
    /// Populates an existing instance from its <c>x:Class</c>-conventioned document (the embedded-resource
    /// resolution lands at X2 — until then this overload requires the explicit-URI / explicit-XAML forms).
    /// </summary>
    public void LoadComponent(object component)
    {
        ArgumentNullException.ThrowIfNull(component);
        throw new NotSupportedException(
            "LoadComponent(object) requires the x:Class embedded-resource convention (X2). Use " +
            "LoadComponent(component, document) / LoadComponent(component, sourceUri) at X1.");
    }

    /// <summary>
    /// Populates <paramref name="component"/> from a pre-parsed document (the root record sets members
    /// on it). <paramref name="context"/> threads the optional source URI + ambient resource scope
    /// <c>{StaticResource}</c> falls back to (the same seam <see cref="Load(XamlDocument, XamlLoadContext?)"/> exposes).
    /// </summary>
    public void LoadComponent(object component, XamlDocument document, XamlLoadContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(document);

        // Design-time live source (X-designer): a hooked host swaps the baked document for the
        // freshly parsed on-disk text, so edits to an x:Class view's XAML reflect in a preview
        // without a rebuild. Parse errors surface exactly like a failed baked document would.
        if (XamlModule.LiveXamlSource is { } live
            && (context?.Source ?? document.SourceUri) is { } liveUri
            && live(liveUri) is { } liveText)
        {
            // Parse with the designer's loader when one is installed: live edits reference types
            // the consuming assembly's closed-set provider never saw at compile time.
            document = (XamlModule.LiveXamlLoader ?? this).Parse(liveText, liveUri);
        }

        ThrowIfDocumentFailed(document);

        var builder = new XamlObjectGraphBuilder(
            document, _options, context?.Source ?? document.SourceUri, context?.AmbientResources);
        builder.Build(component);
    }

    /// <summary>Populates <paramref name="component"/> from the XAML at <paramref name="sourceXaml"/> + URI (the explicit-URI overload, XD17).</summary>
    public void LoadComponent(object component, string sourceXaml, Uri source, XamlLoadContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(sourceXaml);
        ArgumentNullException.ThrowIfNull(source);
        LoadComponent(component, Parse(sourceXaml, source), context);
    }

    private static void ThrowIfDocumentFailed(XamlDocument document)
    {
        foreach (var diagnostic in document.Diagnostics)
        {
            if (diagnostic.Severity == XamlDiagnosticSeverity.Error)
                throw new XamlParseException(document.Diagnostics);
        }
    }
}
