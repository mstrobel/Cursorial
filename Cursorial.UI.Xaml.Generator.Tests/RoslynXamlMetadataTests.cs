using Cursorial.UI.Xaml;
using Cursorial.UI.Xaml.Generator;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// WS-TSA.2 — the build-time symbol-backed provider. <see cref="RoslynXamlMetadata"/> resolves XAML names
/// to Roslyn symbols and builds <see cref="XamlType"/>/<see cref="XamlMember"/> with
/// <see cref="RoslynXamlType"/> identities, so the frontend parser runs <i>inside</i> the generator — the
/// same parser, yielding semantic <c>CUR2xxx</c> diagnostics + a resolved node graph. Facts come from the
/// shared <see cref="SymbolXamlModel"/>, so it cannot drift from the emitted runtime provider (the X174
/// dual-run gate). These tests pin the provider's resolution facts and the symbol-backed parse diagnostics.
/// </summary>
public class RoslynXamlMetadataTests
{
    private const string Ui = "https://cursorial.dev/ui";

    private static RoslynXamlMetadata Provider() => new(GeneratorHarness.ReferencedCompilation());

    private static XamlDocument ParseWith(RoslynXamlMetadata provider, string xaml)
        => XamlFrontend.Parse(xaml, new XamlParseOptions
        {
            MetadataProvider = provider,
            DiagnosticMode = XamlDiagnosticMode.CollectAll,
            FoldConstants = false, // no runtime values to fold at generator time
        });

    [Fact]
    public void TryGetType_ResolvesSymbol_WithContentProperty()
    {
        var res = Provider().TryGetType(Ui, "Button");
        Assert.True(res.IsResolved);
        Assert.IsType<RoslynXamlType>(res.Type!.ClrType);
        Assert.Equal("Button", res.Type!.ClrType.Name);
        Assert.Equal("Content", res.Type!.ContentProperty);
    }

    [Fact]
    public void TryGetType_UnknownLocalName_NotFound()
        => Assert.False(Provider().TryGetType(Ui, "Buttn").IsResolved);

    [Theory] // XAML2009 built-in (CLR basic) types resolve via the symbol resolver too (x: intrinsics namespace)
    [InlineData("String")]
    [InlineData("Int32")]
    [InlineData("Double")]
    [InlineData("Boolean")]
    [InlineData("TimeSpan")]
    public void TryGetType_BuiltIn_ResolvesInIntrinsicsNamespace(string local)
    {
        var res = Provider().TryGetType("https://cursorial.dev/xaml", local);
        Assert.True(res.IsResolved);
        Assert.Equal(local, res.Type!.ClrType.Name); // System.String → "String", System.Int32 → "Int32", …
    }

    [Fact] // a registered UIProperty surfaces a non-null Property marker (the XD4 rule-1 signal)
    public void RegisteredUIProperty_HasNonNullPropertyMarker()
    {
        var type = Provider().TryGetType(Ui, "Button").Type!;
        var width = type.TryGetMember("Width");
        Assert.NotNull(width);
        Assert.NotNull(width!.Property);
    }

    [Fact] // the type-level fill flag AND the per-member Object-vs-Items signal both read collection-shaped
    public void CollectionContentMember_IsCollection_True()
    {
        var type = Provider().TryGetType(Ui, "StackPanel").Type!;
        Assert.Equal("Children", type.ContentProperty);
        Assert.True(type.IsCollection);

        var children = type.TryGetMember("Children");
        Assert.NotNull(children);
        Assert.True(children!.ValueType.IsCollection);
    }

    [Fact] // Border.Child is a single UIElement — not collection-shaped
    public void NonCollectionMember_IsCollection_False()
    {
        var child = Provider().TryGetType(Ui, "Border").Type!.TryGetMember("Child");
        Assert.NotNull(child);
        Assert.False(child!.ValueType.IsCollection);
    }

    [Fact] // the same parser the loader runs, over the symbol provider, on a valid document: no errors
    public void SymbolBackedParse_CleanDocument_NoErrors()
    {
        var doc = ParseWith(Provider(),
            $"<StackPanel xmlns=\"{Ui}\"><Button Content=\"Hi\" Width=\"20\"/></StackPanel>");
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == XamlDiagnosticSeverity.Error);
    }

    [Fact] // an unknown element name → the loader's CUR2002 type-not-found, emitted at generator time
    public void SymbolBackedParse_UnknownType_EmitsCUR2002()
    {
        var doc = ParseWith(Provider(), $"<Buttn xmlns=\"{Ui}\"/>");
        Assert.Contains(doc.Diagnostics, d => d.Code == "CUR2002");
    }

    [Fact] // an unknown member → the loader's CUR2102 member-not-found, emitted at generator time
    public void SymbolBackedParse_UnknownMember_EmitsCUR2102()
    {
        var doc = ParseWith(Provider(), $"<Button xmlns=\"{Ui}\" Frobnicate=\"x\"/>");
        Assert.Contains(doc.Diagnostics, d => d.Code == "CUR2102");
    }

    [Fact] // an attached property (Grid.Row) resolves as an attachable member from the <Name>Property field
    public void AttachedProperty_ResolvesAsAttachableMember()
    {
        var grid = Provider().TryGetType(Ui, "Grid").Type!;
        var row = grid.TryGetMember("Row");
        Assert.NotNull(row);
        Assert.True(row!.IsAttachable);
        Assert.NotNull(row.Property);                 // registered (XD4 rule 1)
        Assert.Equal("Int32", row.ValueType.Name);    // AttachedProperty<int> -> int
    }

    [Fact] // the formerly-false CUR2102: a symbol-backed parse of an attached property must be CLEAN
    public void SymbolBackedParse_AttachedProperty_NoFalsePositive()
    {
        var doc = ParseWith(Provider(), $"<StackPanel xmlns=\"{Ui}\"><Button Grid.Row=\"0\"/></StackPanel>");
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == "CUR2102");
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == XamlDiagnosticSeverity.Error);
    }

    [Fact] // an ITemplateContent content member is deferred content (its body is a deferred slice)
    public void TemplateContentMember_IsDeferred()
    {
        var template = Provider().TryGetType(Ui, "ControlTemplate").Type!;
        Assert.Equal("Content", template.ContentProperty);
        var content = template.TryGetMember("Content");
        Assert.NotNull(content);
        Assert.True(content!.IsDeferredContent);
    }

    [Fact] // a ResourceDictionary is dictionary-shaped (the type-level fill flag)
    public void ResourceDictionary_IsCollection()
        => Assert.True(Provider().TryGetType(Ui, "ResourceDictionary").Type!.IsCollection);

    [Fact] // coverage audit: a representative theme-style document must parse with NO false CUR2xxx errors,
           // so the CUR2 semantic band can be surfaced as build errors without breaking valid XAML.
    public void SymbolBackedParse_ThemeStyleVocabulary_NoFalsePositives()
    {
        const string xaml = """
            <ResourceDictionary xmlns="https://cursorial.dev/ui" xmlns:x="https://cursorial.dev/xaml">
              <ControlTemplate x:Key="ButtonTemplate">
                <Border Padding="1,0" Background="{TemplateBinding Background}">
                  <ContentPresenter x:Name="PART_ContentPresenter"/>
                </Border>
              </ControlTemplate>
              <Style x:Key="{x:Type Button}" TargetType="Button">
                <Setter Property="Foreground" Value="{DynamicResource Accent}"/>
                <Setter Property="Template" Value="{StaticResource ButtonTemplate}"/>
              </Style>
              <Style Selector=".caps-unicode CheckBox">
                <Setter Property="ToggleGlyph.Glyphs" Value="a|b|c"/>
              </Style>
            </ResourceDictionary>
            """;

        var cur2 = ParseWith(Provider(), xaml).Diagnostics
            .Where(d => d.Code.StartsWith("CUR2"))
            .Select(d => $"{d.Code} @ ({d.Line},{d.Column}): {d.Message}")
            .ToList();

        Assert.True(cur2.Count == 0, "Unexpected CUR2 diagnostics on valid theme XAML:\n" + string.Join("\n", cur2));
    }
}
