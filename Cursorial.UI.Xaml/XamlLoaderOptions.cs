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
    private static IXamlTypeMetadataProvider? _default;

    /// <summary>
    /// The process-wide default metadata provider new <see cref="XamlLoaderOptions"/> adopt. The X4 generated
    /// provider sets this from a <c>[ModuleInitializer]</c> (no reflection) so a consuming assembly's
    /// generated, trim/AOT-clean provider becomes the default without any explicit opt-in. When nothing has
    /// set it, it falls back to <see cref="ReflectionXamlMetadata.Instance"/> — UNLESS the reflection provider
    /// is disabled for trimming/AOT (the <c>Cursorial.UI.Xaml.ReflectionMetadataProvider.IsSupported</c>
    /// feature switch), in which case a fallback read throws (the generated provider must be installed).
    /// Setting <see langword="null"/> restores the fallback.
    /// </summary>
    public static IXamlTypeMetadataProvider DefaultMetadataProvider
    {
        get => _default ??= ReflectionMetadataProviderFallback();
        set => _default = value ?? ReflectionMetadataProviderFallback();
    }

    /// <summary>
    /// The reflection metadata provider feature switch (default <see langword="true"/>). An app targeting
    /// NativeAOT/trimming sets <c>Cursorial.UI.Xaml.ReflectionMetadataProvider.IsSupported=false</c> (a
    /// <c>RuntimeHostConfigurationOption</c> with <c>Trim="true"</c>); the trimmer then constant-folds this
    /// and drops the reflection provider's <c>[RequiresUnreferencedCode]</c>/<c>[RequiresDynamicCode]</c>
    /// branch entirely, so the generated provider is the only metadata source.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.FeatureSwitchDefinition("Cursorial.UI.Xaml.ReflectionMetadataProvider.IsSupported")]
    internal static bool IsReflectionMetadataProviderSupported
        => !AppContext.TryGetSwitch("Cursorial.UI.Xaml.ReflectionMetadataProvider.IsSupported", out var enabled) || enabled;

    private static IXamlTypeMetadataProvider ReflectionMetadataProviderFallback()
        => IsReflectionMetadataProviderSupported
            ? ReflectionXamlMetadata.Instance
            : throw new InvalidOperationException(
                "No XAML metadata provider is installed and the reflection provider is disabled for trimming/AOT " +
                "(Cursorial.UI.Xaml.ReflectionMetadataProvider.IsSupported=false). The X4 generated provider's " +
                "module initializer installs one — ensure the generator ran over this assembly's CursorialXaml.");

    /// <summary>The metadata provider; defaults to <see cref="DefaultMetadataProvider"/>.</summary>
    public IXamlTypeMetadataProvider MetadataProvider { get; init; } = DefaultMetadataProvider;

    /// <summary>How diagnostics surface. Default <see cref="XamlDiagnosticMode.ThrowOnFirstError"/>.</summary>
    public XamlDiagnosticMode DiagnosticMode { get; init; } = XamlDiagnosticMode.ThrowOnFirstError;

    /// <summary>When true (default), context-free literals fold to constants at parse (matrix XD3).</summary>
    public bool FoldConstants { get; init; } = true;

    /// <summary>The culture for context-sensitive converts. Default <see cref="CultureInfo.InvariantCulture"/>.</summary>
    public CultureInfo ConverterCulture { get; init; } = CultureInfo.InvariantCulture;

    /// <summary>
    /// When true, every instantiated object is registered in <see cref="XamlSourceRegistry"/>
    /// with its document URI and 1-based element position — the designer/tooling opt-in behind
    /// element→source navigation. Default false; production loads carry no tracking cost.
    /// Instantiation-stage only (no <see cref="XamlParseOptions"/> counterpart).
    /// </summary>
    public bool TrackSourceInfo { get; init; }

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
