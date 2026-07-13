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
    /// The process-wide default metadata provider new <see cref="XamlLoaderOptions"/> adopt. Resolved lazily
    /// by PULL: the first read consults the ENTRY assembly's <c>[assembly: XamlMetadataProvider]</c> (which
    /// the X4 generator emits), so an app's generated, trim/AOT-clean provider becomes the default without
    /// any explicit opt-in — while merely LOADING an assembly can never repoint the default (the previous
    /// generated <c>[ModuleInitializer]</c> push-install hijacked any host that loaded a user assembly, e.g.
    /// a designer or test runner). Without an entry-assembly attribute it falls back to
    /// <see cref="ReflectionXamlMetadata.Instance"/> — UNLESS the reflection provider is disabled for
    /// trimming/AOT (the <c>Cursorial.UI.Xaml.ReflectionMetadataProvider.IsSupported</c> feature switch), in
    /// which case a fallback read throws (set the default explicitly, or make the entry assembly's provider
    /// discoverable). Setting <see langword="null"/> restores lazy resolution.
    /// </summary>
    public static IXamlTypeMetadataProvider DefaultMetadataProvider
    {
        get => _default ??= TryDiscoverMetadataProvider(System.Reflection.Assembly.GetEntryAssembly())
                            ?? ReflectionMetadataProviderFallback();
        set => _default = value;
    }

    /// <summary>
    /// Pull-discovers <paramref name="assembly"/>'s XAML metadata provider from its
    /// <c>[assembly: XamlMetadataProvider]</c>: the provider's <c>Instance</c> singleton field when it has
    /// one (the generated shape), else a parameterless construction. Returns null when
    /// <paramref name="assembly"/> is null or carries no attribute; throws when the advertised type is not
    /// an <see cref="IXamlTypeMetadataProvider"/>. <see cref="DefaultMetadataProvider"/> uses this against
    /// the entry assembly; a host loading foreign assemblies (designer, test runner) can call it to adopt a
    /// specific assembly's provider deliberately.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075:DynamicallyAccessedMembers",
        Justification = "The generated provider's Instance field and parameterless ctor are rooted by the same " +
                        "assembly's generated code-behinds (they reference __GeneratedXamlMetadata.Instance); " +
                        "hand-written providers opt into reflection visibility by advertising themselves.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2072:DynamicallyAccessedMembers",
        Justification = "Same rooting argument as IL2075 — discovery only re-finds already-rooted members.")]
    public static IXamlTypeMetadataProvider? TryDiscoverMetadataProvider(System.Reflection.Assembly? assembly)
    {
        var providerType = assembly is null
            ? null
            : System.Reflection.CustomAttributeExtensions.GetCustomAttribute<XamlMetadataProviderAttribute>(assembly)?.ProviderType;
        if (providerType is null)
            return null;

        var instance = providerType
                           .GetField("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                           ?.GetValue(null)
                       ?? Activator.CreateInstance(providerType, nonPublic: true);
        return instance as IXamlTypeMetadataProvider
               ?? throw new InvalidOperationException(
                   $"[assembly: XamlMetadataProvider(typeof({providerType}))] on '{assembly}' does not implement {nameof(IXamlTypeMetadataProvider)}.");
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
                "(Cursorial.UI.Xaml.ReflectionMetadataProvider.IsSupported=false). Set XamlLoaderOptions." +
                "DefaultMetadataProvider explicitly, or ensure the entry assembly carries the X4 generator's " +
                "[assembly: XamlMetadataProvider] (the generator emits it over the assembly's CursorialXaml).");

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
