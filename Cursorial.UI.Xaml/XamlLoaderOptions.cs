using System.Globalization;

namespace Cursorial.UI.Xaml;

/// <summary>
/// The public loader options (matrix §0.2): the metadata provider that drives type/member/converter
/// resolution, the diagnostic mode, the constant-folding flag, and the converter culture. Projected onto
/// the frontend's <see cref="XamlParseOptions"/> for the parse half.
/// </summary>
/// <remarks>
/// This type and <see cref="XamlParseOptions"/> are parallel by design: <see cref="XamlLoaderOptions"/>
/// owns the public loader surface, <see cref="XamlParseOptions"/> is the netstandard2.0 frontend's view
/// (the frontend cannot reference the loader's types). Adding a new option requires updating both types
/// and <see cref="ToParseOptions"/>.
/// </remarks>
public sealed class XamlLoaderOptions
{
    /// <summary>The metadata provider; defaults to <see cref="ReflectionXamlMetadata.Instance"/>.</summary>
    public IXamlTypeMetadataProvider MetadataProvider { get; init; } = ReflectionXamlMetadata.Instance;

    /// <summary>How diagnostics surface. Default <see cref="XamlDiagnosticMode.ThrowOnFirstError"/>.</summary>
    public XamlDiagnosticMode DiagnosticMode { get; init; } = XamlDiagnosticMode.ThrowOnFirstError;

    /// <summary>When true (default), context-free literals fold to constants at parse (matrix XD3).</summary>
    public bool FoldConstants { get; init; } = true;

    /// <summary>The culture for context-sensitive converts. Default <see cref="CultureInfo.InvariantCulture"/>.</summary>
    public CultureInfo ConverterCulture { get; init; } = CultureInfo.InvariantCulture;

    internal XamlParseOptions ToParseOptions() => new()
    {
        MetadataProvider = MetadataProvider,
        DiagnosticMode = DiagnosticMode,
        FoldConstants = FoldConstants,
        ConverterCulture = ConverterCulture,
    };
}

/// <summary>
/// The per-<c>Load</c> context (matrix §0.2 / X105): the optional pre-existing root instance for
/// <c>LoadComponent</c> (the root <see cref="ObjectRecord"/> populates it instead of activating a fresh
/// one) and the optional source URI.
/// </summary>
public sealed class XamlLoadContext
{
    /// <summary>
    /// The code-behind root instance to populate (the <c>LoadComponent</c> path, matrix X105/XD17). When
    /// non-null the root object record sets members on this instance; when null a fresh root activates.
    /// </summary>
    public object? RootInstance { get; init; }

    /// <summary>The document source URI, for diagnostics and relative-resource resolution.</summary>
    public Uri? Source { get; init; }

    /// <summary>
    /// The ambient resource scope <c>{StaticResource}</c> falls back to after the document's lexical
    /// dictionary stack (matrix XD9). Defaults to <c>ResourceScopes.ForApplication()</c> when null.
    /// </summary>
    public IResourceScope? AmbientResources { get; init; }
}
