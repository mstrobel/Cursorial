using Cursorial.UI;
using Cursorial.UI.Xaml;
using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// §6 (X67–X82): type activation + content/collection/attached-property syntax, the X1 loader.
/// </summary>
public sealed class Section06_Activation : LoaderTestBase
{
    [Fact]
    public void X067_Activate_TypedRoot_AndMismatchThrows()
    {
        Assert.IsType<UIControls.Button>(Load("<Button/>"));
        var button = Load<UIControls.Button>("<Button/>");
        Assert.NotNull(button);
        Assert.ThrowsAny<System.Exception>(() => Load<UIControls.TextBlock>("<Button/>"));
    }

    [Fact]
    public void X068_ContentSet_ThroughUIPropertyAtLocalValue()
    {
        var button = Load<UIControls.Button>("<Button Content=\"Hi\"/>");
        Assert.Equal("Hi", button.Content);
        Assert.Equal(BindingPriority.LocalValue, button.GetValueSource(UIControls.ContentControl.ContentProperty).Priority);
    }

    [Fact]
    public void X069_ContentProperty_FillsChild()
    {
        var border = Load<UIControls.Border>("<Border><Button/></Border>");
        Assert.IsType<UIControls.Button>(border.Child);
    }

    [Fact] // Selector string->Selector converter (Task #10 prerequisite): <Style Selector="^:focus"> + nested <Style.Children>
    public void StyleSelectorAttribute_AndNestedChildren_AuthorStateSubRules()
    {
        var style = Load<Style>(
            "<Style TargetType=\"Button\">" +
              "<Setter Property=\"Background\" Value=\"#111111\"/>" +
              "<Style.Children>" +
                "<Style Selector=\"^:focus\" TargetType=\"Button\"><Setter Property=\"Background\" Value=\"#222222\"/></Style>" +
              "</Style.Children>" +
            "</Style>");

        // The parent resolved its setter against its TargetType (a type selector). The nested sub-rule carries
        // a ^:focus Selector (via the new string->Selector converter) plus its own TargetType for setter
        // resolution — the shape a XAML-authored control theme uses for :focus/:pressed/... state rules.
        Assert.Single(style.Setters);
        Assert.Single(style.Children);
        Assert.NotNull(style.Children[0].Selector);
        Assert.Single(style.Children[0].Setters);
    }

    [Fact]
    public void X070_ContentProperty_Resolved_AndAbsentRejectsContent()
    {
        // ContentControl.Content / Border.Child / Panel.Children resolve as content properties.
        var schema = ReflectionXamlMetadata.Instance;
        Assert.Equal("Content", schema.GetXamlType(typeof(UIControls.Button)).ContentProperty);
        Assert.Equal("Child", schema.GetXamlType(typeof(UIControls.Border)).ContentProperty);
        Assert.Equal("Children", schema.GetXamlType(typeof(UIControls.StackPanel)).ContentProperty);

        // A type with no content property rejects implicit content (CUR2104). RowDefinition has none.
        Assert.Null(schema.GetXamlType(typeof(UIControls.RowDefinition)).ContentProperty);
        ThrowsLoad(XamlDiagnosticCodes.NoContentProperty,
            () => LoadRaw("<RowDefinition xmlns=\"https://cursorial.dev/ui\">text</RowDefinition>"));
    }

    [Fact]
    public void X071_ImplicitChildrenFill_InDocumentOrder()
    {
        var panel = Load<UIControls.StackPanel>("<StackPanel><Button/><Border/></StackPanel>");
        Assert.Equal(2, panel.Children.Count);
        Assert.IsType<UIControls.Button>(panel.Children[0]);
        Assert.IsType<UIControls.Border>(panel.Children[1]);
    }

    [Fact]
    public void X072_StylesCollectionFill()
    {
        var panel = Load<UIControls.StackPanel>(
            "<StackPanel><StackPanel.Styles><Style TargetType=\"Button\"/></StackPanel.Styles></StackPanel>");
        Assert.Single(panel.Styles);
    }

    [Fact]
    public void X073_ResourceDictionaryFill_KeyedEntries()
    {
        var dict = (Cursorial.UI.ResourceDictionary)LoadRaw(
            "<ResourceDictionary xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\">" +
            "<Border x:Key=\"A\"/></ResourceDictionary>");
        Assert.True(dict.TryGetValue("A", out var value));
        Assert.IsType<UIControls.Border>(value);
    }

    [Fact]
    public void X074_GridDefinitionsFill_HeightsConverted()
    {
        var grid = Load<UIControls.Grid>(
            "<Grid><Grid.RowDefinitions><RowDefinition Height=\"*\"/><RowDefinition Height=\"Auto\"/></Grid.RowDefinitions></Grid>");
        Assert.Equal(2, grid.RowDefinitions.Count);
        Assert.True(grid.RowDefinitions[0].Height.IsStar);
        Assert.True(grid.RowDefinitions[1].Height.IsAuto);
    }

    [Fact]
    public void X075_AttachedProperty_GridRow()
    {
        var grid = Load<UIControls.Grid>("<Grid><Button Grid.Row=\"1\"/></Grid>");
        var button = (UIControls.Button)grid.Children[0];
        Assert.Equal(1, UIControls.Grid.GetRow(button));
    }

    [Fact]
    public void X076_AttachedProperty_UnresolvableOwner_Throws()
    {
        ThrowsLoad(XamlDiagnosticCodes.TypeNotFound, () => Load("<Grid><Button Bogus.Row=\"1\"/></Grid>"));
    }

    [Fact]
    public void X078_NoParameterlessConstructor_Throws()
    {
        // ButtonBase is abstract — no usable public parameterless construction.
        ThrowsLoad(XamlDiagnosticCodes.NoParameterlessConstructor, () => Load("<ButtonBase/>"));
    }

    [Fact]
    public void X079_ClrOnlyMember_SetThroughClrSetter()
    {
        // Border.Child is a plain CLR property (no registered UIProperty) — set via the CLR setter.
        var border = Load<UIControls.Border>("<Border><Border.Child><Button/></Border.Child></Border>");
        Assert.IsType<UIControls.Button>(border.Child);
    }

    [Fact]
    public void X080_ReadOnlyCollectionMember_Filled()
    {
        var grid = Load<UIControls.Grid>(
            "<Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition/></Grid.ColumnDefinitions></Grid>");
        Assert.Equal(2, grid.ColumnDefinitions.Count);
    }

    [Fact]
    public void X081_FoldedConstantShared_AcrossLoads()
    {
        var doc = Parse("<Button Margin=\"1,2\"/>");
        var a = Loader.Load<UIControls.Button>(doc);
        var b = Loader.Load<UIControls.Button>(doc);
        Assert.NotSame(a, b);
        Assert.Equal(a.Margin, b.Margin);
        Assert.Equal(new Cursorial.Rendering.Margins(1, 2), a.Margin);

        // XD20: the document holds ONE boxed constant for the folded Margin, and both loads read that
        // same boxed reference out of doc.Constants (the parse-once/instantiate-many box sharing — P6
        // review correctness P2-4 makes the shared-reference probe explicit, not just value-equality).
        Assert.True(doc.TryFindMember(0, "Margin", out var m));
        Assert.Equal(XamlValueKind.Folded, m.Kind);
        object? box1 = doc.FoldedValue(in m);
        object? box2 = doc.FoldedValue(in m);
        Assert.Same(box1, box2);
        Assert.Equal(new Cursorial.Rendering.Margins(1, 2), (Cursorial.Rendering.Margins)box1!);
    }

    [Fact]
    public void X082_FractionalCell_AtInstantiate_Throws()
    {
        // FoldConstants=false defers the convert to stage 2; a fractional cell is CUR2401 there.
        var loader = new XamlLoader(new XamlLoaderOptions { FoldConstants = false });
        var ex = Assert.Throws<XamlParseException>(() => loader.Load<UIControls.Button>(Wrap("<Button Width=\"0.5\"/>"), TestSource));
        Assert.Equal(XamlDiagnosticCodes.ConversionFailed, ex.Code);
        Assert.True(ex.Line > 0 && ex.Column > 0);
    }
}
