using Cursorial.Output;
using Cursorial.UI;
using Cursorial.UI.Xaml;

using UIControls = Cursorial.UI.Controls;
using Style = Cursorial.UI.Style;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// §11 (X129–X140): resource dictionaries — keyed entries, MergedDictionaries, theme sub-dictionaries
/// (ThemeVariantKey.Parse), separate-file Source= via the module-init LoadCallback (C-9), implicit keys.
/// </summary>
[Collection(nameof(LoadCallbackCollection))]
public sealed class Section11_ResourceDictionaries : LoaderTestBase
{
    private const string Pre = " xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    [Fact] // X129
    public void X129_KeyedEntries_LandUnderXKey()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}>" +
            "<TestBrush x:Key=\"A\" Color=\"Red\"/>" +
            "<Style x:Key=\"ToolButton\" TargetType=\"Button\"><Setter Property=\"Width\" Value=\"10\"/></Style>" +
            "</ResourceDictionary>");
        Assert.IsType<TestBrush>(dict["A"]);
        Assert.IsType<Style>(dict["ToolButton"]);
    }

    [Fact] // X130
    public void X130_MergedDictionaries_OwnBeatsMerged()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}>" +
            "<TestBrush x:Key=\"A\" Color=\"Red\"/>" +
            "<ResourceDictionary.MergedDictionaries>" +
            "<ResourceDictionary><TestBrush x:Key=\"B\" Color=\"Blue\"/></ResourceDictionary>" +
            "</ResourceDictionary.MergedDictionaries>" +
            "</ResourceDictionary>");
        Assert.Single(dict.MergedDictionaries);
        // own + merged are both visible (last-to-first walk via TryGetResource).
        var variant = new ThemeVariant(ThemeBase.Dark, ColorDepth.Truecolor);
        Assert.True(dict.TryGetResource("A", variant, out _));
        Assert.True(dict.TryGetResource("B", variant, out _));
    }

    [Fact] // X131 / X132 / X133
    public void X131_Source_RoutesThroughLoadCallback()
    {
        using var _ = LoadCallbackScope.WithProvider(new InMemoryProvider(
            ("cursorial://Cursorial.UI.Xaml.Tests/Themes/Dark.xaml",
             $"<ResourceDictionary{Pre}><TestBrush x:Key=\"Accent\" Color=\"Red\"/></ResourceDictionary>")));

        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre} Source=\"cursorial://Cursorial.UI.Xaml.Tests/Themes/Dark.xaml\"/>");
        Assert.IsType<TestBrush>(dict["Accent"]);
    }

    [Fact] // X131 (amended 2026-07-12) — a nested Source= load binds to the loader ALREADY instantiating
    // on the thread (XamlLoader.Current), not the ambient default: the whole tree resolves through one
    // provider. Under strict trimming that is the generated closed-set provider the code-behind bound
    // explicitly; falling back to Shared would re-read the ambient default mid-tree.
    public void X131_Source_UsesTriggeringLoader_NotSharedDefault()
    {
        using var _ = LoadCallbackScope.WithProvider(new InMemoryProvider(
            ("cursorial://Cursorial.UI.Xaml.Tests/Themes/Inner.xaml",
             $"<ResourceDictionary{Pre}><TestBrush x:Key=\"Accent\" Color=\"Red\"/></ResourceDictionary>")));

        var recording = new RecordingProvider(ReflectionXamlMetadata.Instance);
        var loader = new XamlLoader(new XamlLoaderOptions { MetadataProvider = recording });

        // The OUTER document never mentions TestBrush — only the nested one does. Seeing it on the
        // recording provider proves the nested parse ran through the triggering loader.
        var dict = (ResourceDictionary)loader.Load(
            $"<ResourceDictionary{Pre} Source=\"cursorial://Cursorial.UI.Xaml.Tests/Themes/Inner.xaml\"/>");

        Assert.IsType<TestBrush>(dict["Accent"]);
        Assert.Contains("TestBrush", recording.RequestedTypeNames);
    }

    private sealed class RecordingProvider(IXamlTypeMetadataProvider inner) : IXamlTypeMetadataProvider
    {
        public List<string> RequestedTypeNames { get; } = [];

        public XamlTypeResolution TryGetType(string xmlNamespace, string localName)
        {
            RequestedTypeNames.Add(localName);
            return inner.TryGetType(xmlNamespace, localName);
        }

        public string[] GetClrNamespaces(string xmlNamespace) => inner.GetClrNamespaces(xmlNamespace);

        public string[] GetKnownTypeNames(string xmlNamespace) => inner.GetKnownTypeNames(xmlNamespace);

        public string[] GetKnownMemberNames(IXamlType type) => inner.GetKnownMemberNames(type);
    }

    [Fact] // X132
    public void X132_ModuleInit_InstallsLoadCallback()
    {
        // The module initializer set ResourceDictionary.LoadCallback (C-9); before the module loads it is
        // null and Source= throws. After load (this assembly references the loader) it is non-null.
        XamlModule.EnsureInitialized();
        Assert.NotNull(ResourceDictionary.LoadCallback);
    }

    [Fact] // X133
    public void X133_SourceWithoutLoader_Throws()
    {
        var saved = ResourceDictionary.LoadCallback;
        try
        {
            ResourceDictionary.LoadCallback = null;
            // Setting Source with no loader installed surfaces the documented guard (wrapped CUR2401).
            ThrowsLoad(XamlDiagnosticCodes.ConversionFailed, () => LoadRaw(
                $"<ResourceDictionary{Pre} Source=\"cursorial://x/y.xaml\"/>"));
        }
        finally
        {
            ResourceDictionary.LoadCallback = saved;
        }
    }

    [Fact] // X134
    public void X134_ThemeDictionaries_KeyedByThemeVariantKey()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}>" +
            "<ResourceDictionary.ThemeDictionaries>" +
            "<ResourceDictionary x:Key=\"Dark\"><TestBrush x:Key=\"A\" Color=\"Red\"/></ResourceDictionary>" +
            "<ResourceDictionary x:Key=\"Light\"><TestBrush x:Key=\"A\" Color=\"Blue\"/></ResourceDictionary>" +
            "</ResourceDictionary.ThemeDictionaries>" +
            "</ResourceDictionary>");
        Assert.True(dict.ThemeDictionaries.ContainsKey(new ThemeVariantKey(ThemeBase.Dark, null)));
        Assert.True(dict.ThemeDictionaries.ContainsKey(new ThemeVariantKey(ThemeBase.Light, null)));
    }

    [Fact] // X135
    public void X135_ThemeKey_ConstrainsBothAxes()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}>" +
            "<ResourceDictionary.ThemeDictionaries>" +
            "<ResourceDictionary x:Key=\"Dark+Ansi16\"><TestBrush x:Key=\"A\" Color=\"Red\"/></ResourceDictionary>" +
            "</ResourceDictionary.ThemeDictionaries>" +
            "</ResourceDictionary>");
        Assert.True(dict.ThemeDictionaries.ContainsKey(new ThemeVariantKey(ThemeBase.Dark, ColorDepth.Ansi16)));
    }

    [Fact] // X136
    public void X136_UnparseableThemeKey_CUR2401()
    {
        ThrowsLoad(XamlDiagnosticCodes.ConversionFailed, () => LoadRaw(
            $"<ResourceDictionary{Pre}>" +
            "<ResourceDictionary.ThemeDictionaries>" +
            "<ResourceDictionary x:Key=\"Bogus\"><TestBrush x:Key=\"A\" Color=\"Red\"/></ResourceDictionary>" +
            "</ResourceDictionary.ThemeDictionaries>" +
            "</ResourceDictionary>"));
    }

    [Fact] // X137
    public void X137_ImplicitStyleKey_ByTargetTypeSelector()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}><Style TargetType=\"Button\"/></ResourceDictionary>");
        // The implicit Style key is the type-selector form; the entry is present and is a Style.
        Assert.Equal(1, dict.Count);
        foreach (var pair in dict)
            Assert.IsType<Style>(pair.Value);
    }

    [Fact] // X138
    public void X138_ImplicitDataTemplateKey_ByDataTemplateKey()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}><DataTemplate DataType=\"Button\"><TextBlock/></DataTemplate></ResourceDictionary>");
        Assert.True(dict.ContainsKey(new DataTemplateKey(typeof(UIControls.Button))));
    }

    [Fact] // X139
    public void X139_StyleTargetType_MapsToTypeSelector()
    {
        // Style TargetType="Button" produces a type-selector Style (the styling object uses a Selector).
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}><Style x:Key=\"S\" TargetType=\"Button\"/></ResourceDictionary>");
        var style = (Style)dict["S"]!;
        Assert.NotNull(style.Selector);
    }

    [Fact] // X140
    public void X140_MergedPrecedence_OwnBeatsMerged_LaterBeatsEarlier()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}>" +
            "<TestBrush x:Key=\"A\" Color=\"#ff0000\"/>" +
            "<ResourceDictionary.MergedDictionaries>" +
            "<ResourceDictionary><TestBrush x:Key=\"A\" Color=\"#0000ff\"/></ResourceDictionary>" +
            "</ResourceDictionary.MergedDictionaries>" +
            "</ResourceDictionary>");
        var variant = new ThemeVariant(ThemeBase.Dark, ColorDepth.Truecolor);
        Assert.True(dict.TryGetResource("A", variant, out var value));
        // Own entry beats merged: A is the #ff0000 TestBrush, not the merged #0000ff one.
        Assert.Equal(Color.FromRgb(255, 0, 0), ((TestBrush)value!).Color);
    }

    [Fact] // PRE-TYPEKEY: x:Key="{x:Type Button}" folds to a typeof(Button) dictionary key (Type-keyed ControlThemes)
    public void TypeKey_XKeyTypeExtension_ResolvesToTypeDictionaryKey()
    {
        var dict = (ResourceDictionary)LoadRaw(
            "<ResourceDictionary xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\">" +
            "<TestBrush x:Key=\"{x:Type Button}\" Color=\"Red\"/></ResourceDictionary>");

        Assert.True(dict.ContainsKey(typeof(UIControls.Button)));   // the resolved Type is the key…
        Assert.False(dict.ContainsKey("{x:Type Button}"));          // …NOT the verbatim extension string
        Assert.IsType<TestBrush>(dict[typeof(UIControls.Button)]);
    }

    [Fact] // PRE-TYPEKEY: an unresolvable x:Key type is a positioned diagnostic, not a silent string key
    public void TypeKey_UnresolvableType_IsDiagnosed()
    {
        var ex = Assert.Throws<XamlParseException>(() => LoadRaw(
            "<ResourceDictionary xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\">" +
            "<TestBrush x:Key=\"{x:Type Bogus}\" Color=\"Red\"/></ResourceDictionary>"));
        Assert.Equal(XamlDiagnosticCodes.TypeNotFound, ex.Code);
    }
}
