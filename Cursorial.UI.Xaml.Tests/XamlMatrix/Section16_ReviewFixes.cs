using System.Globalization;

using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Xaml;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// Regression coverage for the P6 API/correctness review fixes (dispositions in the handoff). One test
/// per fix; these are not matrix rows — they pin the review-driven behavior so it cannot silently
/// regress.
/// </summary>
public sealed class Section16_ReviewFixes : LoaderTestBase
{
    // ── P0-2: LoadComponent threads XamlLoadContext.AmbientResources ──────────────────────────────

    [Fact]
    public void LoadComponent_ThreadsAmbientResources()
    {
        // A {StaticResource} with no document-local dictionary resolves against the ambient scope supplied
        // through the LoadComponent context (previously discarded).
        var ambient = ResourceScopes.ForApplication();
        var dict = new ResourceDictionary { ["AmbientBrush"] = new TestBrush { Color = Colors.Red } };
        var scope = ResourceScopes.ForDictionary(dict, ambient);

        var doc = Parse("<Button Background=\"{StaticResource AmbientBrush}\"/>");
        var component = new Button();
        Loader.LoadComponent(component, doc, new XamlLoadContext { AmbientResources = scope });

        Assert.IsType<TestBrush>(component.Background);
    }

    [Fact]
    public void LoadComponent_WithoutAmbient_StaticResourceMisses()
    {
        // Sanity: without the ambient scope the same document's StaticResource is a miss (proving the
        // ambient wiring above is what resolved it).
        var doc = Parse("<Button Background=\"{StaticResource AmbientBrush}\"/>");
        var component = new Button();
        Assert.Throws<XamlParseException>(() => Loader.LoadComponent(component, doc));
    }

    // ── P1-3: ConstructorArgumentAttribute honored for positional mapping ─────────────────────────

    [Fact]
    public void ConstructorArgument_OrdersPositionalsByAttribute()
    {
        // ReversedExtension declares its positionals via [ConstructorArgument] mapped to a (first, second)
        // ctor whose parameter order is the authoritative positional order — NOT reflection property order.
        var button = Load<Button>("<Button Content=\"{Reversed head, tail}\"/>");
        Assert.Equal("head|tail", button.Content);
    }

    // ── P1-4: unrecognized RelativeSource mode is a hard error, not a silent Self ─────────────────

    [Fact]
    public void RelativeSource_UnrecognizedMode_Throws()
    {
        var ex = Assert.Throws<XamlParseException>(() => Load<Button>(
            "<Button Content=\"{Binding Name, RelativeSource={RelativeSource FindAncestor}}\"/>"));
        Assert.Equal(XamlDiagnosticCodes.ConversionFailed, ex.Code);
    }

    [Fact]
    public void RelativeSource_ModeNamedArg_TemplatedParent_Resolves()
    {
        // {RelativeSource Mode=TemplatedParent} (the named-arg form) resolves the same as the positional.
        var template = Load<ControlTemplate>(
            "<ControlTemplate><Border Background=\"{Binding Background, RelativeSource={RelativeSource Mode=TemplatedParent}}\"/></ControlTemplate>");
        var owner = new Button { Background = new SolidColorBrush(Color.FromRgb(1, 2, 3)) };
        template.TargetType = typeof(Button);
        var instance = template.Instantiate(owner);
        var part = (Border)instance.Root;
        Assert.Equal(owner.Background, part.Background);
    }

    // ── P1-5: XamlParseException exposes SourceUri (does not shadow Exception.Source) ─────────────

    [Fact]
    public void Exception_SourceUri_Distinct_From_ExceptionSource()
    {
        var ex = Assert.Throws<XamlParseException>(() => Load<Button>("<Bogus/>"));
        Assert.Equal(TestSource, ex.SourceUri);
        // Exception.Source (the assembly-name slot, a string?) is NOT clobbered into a Uri — assigning it
        // a string round-trips, which a `new Uri? Source` shadow would have broken.
        Exception baseEx = ex;
        baseEx.Source = "some-assembly";
        Assert.Equal("some-assembly", baseEx.Source);
    }

    // ── P1-6: enum values in XAML are case-insensitive ───────────────────────────────────────────

    [Fact]
    public void Enum_BindingMode_CaseInsensitive()
    {
        var vm = new TestVm { Name = "start" };
        var button = Load<Button>("<Button Content=\"{Binding Path=Name, Mode=twoway}\"/>");
        button.DataContext = vm;
        button.Content = "edited";
        Assert.Equal("edited", vm.Name);
    }

    [Fact]
    public void Enum_Converter_CaseInsensitive()
    {
        var ctx = new XamlValueContext(CultureInfo.InvariantCulture, null, typeof(Visibility), null, 1, 1);
        Assert.Equal(Visibility.Collapsed, XamlConverters.For(typeof(Visibility))!.ConvertFromString("collapsed", ctx));
    }

    // ── P1-7: a single child of a read-only collection property fills the collection ──────────────

    [Fact]
    public void SingleCollectionChild_FillsCollection()
    {
        // One <ColumnDefinition> under <Grid.ColumnDefinitions> (a read-only collection) is added to the
        // existing collection, not assigned as a replacement (the IsCollectionMember parse classification).
        var grid = Load<Grid>(
            "<Grid><Grid.ColumnDefinitions><ColumnDefinition/></Grid.ColumnDefinitions></Grid>");
        Assert.Single(grid.ColumnDefinitions);
    }

    // ── P2-1: object-typed access-key slot keeps the raw string (fold equivalence at GetAccessText) ─

    [Fact]
    public void AccessKeyObjectSlot_KeepsRawString_FoldsEquivalently()
    {
        var button = Load<Button>("<Button Content=\"_Run\"/>");
        // The object-typed Content slot keeps the literal string (the runtime ContentControl.GetAccessText
        // is the fold producer — fold-equivalence rather than fold-at-load, P6 review P2-1 / XD11 amendment).
        Assert.Equal("_Run", button.Content);
        Assert.Equal(AccessText.Parse("_Run"), AccessText.Parse((string)button.Content!));
    }

    // ── P2-2: a markup-extension grammar diagnostic lands on the '{' ──────────────────────────────

    [Fact]
    public void ExtensionDiagnostic_ColumnPointsAtBrace()
    {
        // Content="{Binding Name  (no close): the column points at the '{', not the attribute name.
        const string xml =
            "<Button xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" Content=\"{Binding Name\"/>";
        var ex = Assert.Throws<XamlParseException>(() => LoadRaw(xml));
        Assert.Equal(XamlDiagnosticCodes.UnterminatedExtension, ex.Code);
        // The '{' is the first char of the attribute value; find it in the source and assert the column.
        int braceIndex = xml.IndexOf("{Binding", StringComparison.Ordinal);
        int expectedColumn = braceIndex + 1; // 1-based, single line
        Assert.Equal(expectedColumn, ex.Column);
    }

    // ── P2-13: {x:Static} as a nested binding-converter argument ──────────────────────────────────

    [Fact]
    public void Binding_Converter_XStatic_Resolves()
    {
        var vm = new TestVm { Status = "ok" };
        var button = Load<Button>(
            "<Button Foreground=\"{Binding Status, Converter={x:Static StatusConverters.Instance}}\"/>");
        button.DataContext = vm;
        Assert.IsType<SolidColorBrush>(button.Foreground);
    }

    // ── P2-14: XamlFrontend / XamlLoader stream a TextReader ──────────────────────────────────────

    [Fact]
    public void Parse_TextReader_Streams()
    {
        using var reader = new StringReader(Wrap("<Button Content=\"Hi\"/>"));
        var doc = Loader.Parse(reader, TestSource);
        var button = Loader.Load<Button>(doc);
        Assert.Equal("Hi", button.Content);
    }

    [Fact]
    public void Frontend_Parse_TextReader_Overload_Exists()
    {
        using var reader = new StringReader(Wrap("<Border/>"));
        var doc = XamlFrontend.Parse(reader, new XamlParseOptions { MetadataProvider = ReflectionXamlMetadata.Instance }, TestSource);
        Assert.Equal("Border", doc.RootTypeLocalName());
    }

    // ── P2-16: member-not-found carries a did-you-mean suggestion ─────────────────────────────────

    [Fact]
    public void MemberNotFound_SuggestsNearestMember()
    {
        var ex = Assert.Throws<XamlParseException>(() => Load<Button>("<Button Withh=\"10\"/>"));
        Assert.Equal(XamlDiagnosticCodes.MemberNotFound, ex.Code);
        Assert.Contains("Did you mean 'Width'", ex.Message);
    }

    // ── P2-17: an empty / root-less document reports an accurate code, never the misleading CUR3001 ─

    [Fact]
    public void EmptyDocument_DoesNotReportConstructorCode()
    {
        // A bare XML declaration has no root element. The XmlReader reports it as malformed XML (CUR1002,
        // accurate — XML requires a root); the important thing is that it is NOT the misleading
        // "type has no parameterless constructor" (CUR3001), which is what fired before (P6 review P2-17).
        var ex = Assert.Throws<XamlParseException>(() => Loader.Load("<?xml version=\"1.0\"?>", TestSource));
        Assert.NotEqual(XamlDiagnosticCodes.NoParameterlessConstructor, ex.Code);
        Assert.Equal(XamlDiagnosticCodes.MalformedXml, ex.Code);
    }

    [Fact]
    public void EmptyDocument_BuildGuard_ReportsEmptyDocumentCode()
    {
        // The Build-time guard (a document that reached instantiation with zero objects) reports the
        // dedicated CUR3003 EmptyDocument code, not CUR3001 (P6 review P2-17). Reached via a directly
        // constructed empty XamlDocument (the InternalsVisibleTo probe surface).
        var empty = new XamlDocument(
            sourceUri: TestSource, rootType: null, rootClassName: null,
            objects: Array.Empty<ObjectRecord>(),
            members: Array.Empty<MemberRecord>(),
            constants: Array.Empty<object?>(),
            strings: Array.Empty<string>(),
            extensions: Array.Empty<ExtensionRecord>(),
            parsedExtensions: Array.Empty<MarkupExtensionNode?>(),
            resolvedTypes: Array.Empty<XamlType?>(),
            resolvedMembers: Array.Empty<XamlMember?>(),
            diagnostics: Array.Empty<XamlDiagnostic>(),
            namespaces: new Dictionary<string, string>());

        var ex = Assert.Throws<XamlParseException>(() => Loader.Load(empty));
        Assert.Equal(XamlDiagnosticCodes.EmptyDocument, ex.Code);
    }

    // ── P1-8: XamlModule.UseResourceProvider restores on dispose ──────────────────────────────────

    [Fact]
    public void UseResourceProvider_RestoresOnDispose()
    {
        var original = XamlModule.ResourceProvider;
        var swapped = new StubResourceProvider();
        using (XamlModule.UseResourceProvider(swapped))
            Assert.Same(swapped, XamlModule.ResourceProvider);
        Assert.Same(original, XamlModule.ResourceProvider);
    }

    private sealed class StubResourceProvider : IXamlResourceProvider
    {
        public bool TryGetXaml(Uri uri, out string? xaml) { xaml = null; return false; }
    }
}

/// <summary>
/// A custom markup extension whose positional arguments are mapped by <see cref="ConstructorArgumentAttribute"/>
/// (P6 review P1-3): the (first, second) ctor's parameter order is authoritative, distinct from the
/// reflection property order (Second declared before First).
/// </summary>
public sealed class ReversedExtension : MarkupExtension
{
    public ReversedExtension() { }

    public ReversedExtension(string first, string second)
    {
        First = first;
        Second = second;
    }

    [ConstructorArgument("second")]
    public string Second { get; set; } = "";

    [ConstructorArgument("first")]
    public string First { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => $"{First}|{Second}";
}

/// <summary>A converter exposed as an {x:Static} singleton (P6 review P2-13).</summary>
public sealed class StatusConverters : IValueConverter
{
    public static readonly StatusConverters Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => new SolidColorBrush(value is "ok" ? Color.FromRgb(0, 255, 0) : Color.FromRgb(255, 0, 0));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
