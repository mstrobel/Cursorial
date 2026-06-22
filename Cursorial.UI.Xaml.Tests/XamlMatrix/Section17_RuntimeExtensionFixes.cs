using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Data;
using Cursorial.UI.Testing;
using Cursorial.UI.Xaml;

using UIControls = Cursorial.UI.Controls;
using Style = Cursorial.UI.Style;
using Binding = Cursorial.UI.Data.Binding;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// Runtime-loader support for previously-deferred XAML constructs (the loader-side twins of the generator
/// lowering-parity work): an inline OBJECT <c>Setter.Value</c> (was silently <c>UnsetValue</c>), a nested
/// <c>{x:Type}</c> resource key, and a <c>{StaticResource}</c> (incl. <c>{x:Type}</c>-keyed) <c>Style.BasedOn</c>.
/// </summary>
public sealed class Section17_RuntimeExtensionFixes : LoaderTestBase
{
    private const string Pre = " xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    [Fact] // An inline OBJECT Setter.Value is built + assigned (previously dropped to UIProperty.UnsetValue).
    public void InlineObjectSetterValue_IsBuiltAndAssigned()
    {
        var style = (Style)LoadRaw(
            $"<Style{Pre} TargetType=\"ItemsControl\">" +
            "<Setter Property=\"ItemsPanel\"><ItemsPanelTemplate><StackPanel/></ItemsPanelTemplate></Setter>" +
            "</Style>");

        var setter = Assert.Single(style.Setters);
        Assert.IsType<UIControls.ItemsPanelTemplate>(setter.Value); // not UIProperty.UnsetValue — the inline object stuck
    }

    [Fact] // {StaticResource {x:Type T}} — a nested {x:Type} resource key resolves the typeof(T)-keyed entry, AND
           // Style.BasedOn="{StaticResource …}" resolves a same-dictionary base style (both previously unsupported).
    public void StaticResourceBasedOn_WithXTypeKey_ResolvesTypeKeyedBaseStyle()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}>" +
            "<Style x:Key=\"{x:Type Button}\" TargetType=\"Button\"><Setter Property=\"Width\" Value=\"7\"/></Style>" +
            "<Style x:Key=\"Derived\" TargetType=\"Button\" BasedOn=\"{StaticResource {x:Type Button}}\"/>" +
            "</ResourceDictionary>");

        var baseStyle = Assert.IsType<Style>(dict[typeof(UIControls.Button)]); // the {x:Type Button} key IS typeof(Button)
        var derived = Assert.IsType<Style>(dict["Derived"]);
        Assert.Same(baseStyle, derived.BasedOn); // BasedOn={StaticResource {x:Type Button}} resolved the base style
    }

    [Fact] // {Binding RelativeSource={RelativeSource FindAncestor, AncestorType={x:Type T}, AncestorLevel=n}} builds the anchor
    public void RelativeSource_FindAncestor_BuildsAnchor()
    {
        var root = Load<UIControls.StackPanel>(
            "<StackPanel Spacing=\"3\">" +
            "<Border Width=\"{Binding Spacing, RelativeSource={RelativeSource FindAncestor, AncestorType={x:Type StackPanel}, AncestorLevel=2}}\"/>" +
            "</StackPanel>");

        var border = (UIControls.Border) root.Children[0];
        var binding = (Binding) BindingOperations.GetBindingExpression(border, UIElement.WidthProperty)!.ParentBinding!;
        Assert.NotNull(binding.RelativeSource);
        Assert.Equal(RelativeSourceMode.FindAncestor, binding.RelativeSource!.Mode);
        Assert.Equal(typeof(UIControls.StackPanel), binding.RelativeSource.AncestorType);
        Assert.Equal(2, binding.RelativeSource.AncestorLevel);
    }

    [Fact] // {x:Reference Name} resolves both a forward reference (named element appears later) and a backward one
    public void XReference_ResolvesForwardAndBackward()
    {
        var root = Load<UIControls.StackPanel>(
            "<StackPanel>" +
            "<Label x:Name=\"fwd\" Target=\"{x:Reference box}\"/>" + // forward: box appears later in the document
            "<TextBox x:Name=\"box\"/>" +
            "<Label x:Name=\"bwd\" Target=\"{x:Reference box}\"/>" + // backward: box already registered
            "</StackPanel>");

        var box = (UIControls.TextBox) root.Children[1];
        Assert.Same(box, ((UIControls.Label) root.Children[0]).Target); // forward ref resolved at end-of-tree
        Assert.Same(box, ((UIControls.Label) root.Children[2]).Target); // backward ref resolved at encounter
    }

    [Fact] // {x:Reference} naming a nonexistent element is a hard error (CUR2112), never a silent null
    public void XReference_UnknownName_Throws()
    {
        var ex = Assert.Throws<XamlParseException>(() => Load<UIControls.StackPanel>(
            "<StackPanel><Label Target=\"{x:Reference nope}\"/></StackPanel>"));
        Assert.Equal("CUR2112", ex.Code);
    }

    [Theory] // XAML2009 built-in (CLR basic) types resolve in the x: (intrinsics) namespace
    [InlineData("String", typeof(string))]
    [InlineData("Int32", typeof(int))]
    [InlineData("Int64", typeof(long))]
    [InlineData("Double", typeof(double))]
    [InlineData("Single", typeof(float))]
    [InlineData("Boolean", typeof(bool))]
    [InlineData("Byte", typeof(byte))]
    [InlineData("Char", typeof(char))]
    [InlineData("Decimal", typeof(decimal))]
    [InlineData("Object", typeof(object))]
    [InlineData("TimeSpan", typeof(System.TimeSpan))]
    [InlineData("Uri", typeof(System.Uri))]
    public void BuiltInTypes_ResolveInIntrinsicsNamespace(string local, System.Type expected)
        => Assert.Equal(expected, XamlSchemaContext.Default.Resolve(XamlSchemaContext.IntrinsicsNamespace, local, out _));

    [Fact] // {x:Type x:String} folds to typeof(string) end-to-end (a built-in type as a type token)
    public void XType_BuiltIn_FoldsToSystemType()
    {
        var template = Load<UIControls.ControlTemplate>(
            "<ControlTemplate TargetType=\"{x:Type x:String}\"><Border/></ControlTemplate>");
        Assert.Equal(typeof(string), template.TargetType);
    }

    [Fact] // {Binding ElementName=x} resolves the named element's property once the tree is shown
    public void Binding_ElementName_ResolvesNamedElement()
    {
        using var host = UITestHost.Create();
        var root = Load<UIControls.StackPanel>(
            "<StackPanel>" +
            "<TextBox x:Name=\"src\" Text=\"hello\"/>" +
            "<TextBlock x:Name=\"dst\" Text=\"{Binding Text, ElementName=src}\"/>" +
            "</StackPanel>");
        host.ShowRoot(root);
        host.RunUntilIdle();

        Assert.Equal("hello", ((UIControls.TextBlock) root.Children[1]).Text);
    }

    [Fact] // {Binding Source={x:Reference x}} resolves the named element as the binding source
    public void Binding_SourceXReference_ResolvesNamedElement()
    {
        using var host = UITestHost.Create();
        var root = Load<UIControls.StackPanel>(
            "<StackPanel>" +
            "<TextBox x:Name=\"src\" Text=\"world\"/>" +
            "<TextBlock x:Name=\"dst\" Text=\"{Binding Text, Source={x:Reference src}}\"/>" +
            "</StackPanel>");
        host.ShowRoot(root);
        host.RunUntilIdle();

        Assert.Equal("world", ((UIControls.TextBlock) root.Children[1]).Text);
    }

    [Fact] // {Binding ElementName=…} resolves a FORWARD reference (the named element appears after the binding)
    public void Binding_ElementName_ResolvesForwardReference()
    {
        using var host = UITestHost.Create();
        var root = Load<UIControls.StackPanel>(
            "<StackPanel>" +
            "<TextBlock x:Name=\"dst\" Text=\"{Binding Text, ElementName=src}\"/>" + // binding precedes the named element
            "<TextBox x:Name=\"src\" Text=\"forward\"/>" +
            "</StackPanel>");
        host.ShowRoot(root);
        host.RunUntilIdle();

        Assert.Equal("forward", ((UIControls.TextBlock) root.Children[0]).Text);
    }

    [Fact] // ElementName resolves inside a DataTemplate (against that template instance's own name scope)
    public void Binding_ElementName_ResolvesInsideDataTemplate()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(30, 8) });
        var root = Load<UIControls.ContentControl>(
            "<ContentControl Content=\"d\">" +
            "<ContentControl.ContentTemplate><DataTemplate>" +
            "<StackPanel>" +
            "<TextBox x:Name=\"src\" Text=\"tmplname\"/>" +
            "<TextBlock Text=\"{Binding Text, ElementName=src}\"/>" +
            "</StackPanel>" +
            "</DataTemplate></ContentControl.ContentTemplate>" +
            "</ContentControl>");
        host.ShowRoot(root);
        host.RunUntilIdle();

        // Both the TextBox and the bound TextBlock render the template-local name's text — 2 rows, not 1.
        Assert.Equal(2, RowsContaining(host, 8, "tmplname"));
    }

    [Fact] // {x:Reference} inside a DataTemplate resolves against the template instance scope (forward, inside)
    public void Binding_SourceXReference_ResolvesInsideDataTemplate()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(30, 8) });
        var root = Load<UIControls.ContentControl>(
            "<ContentControl Content=\"d\">" +
            "<ContentControl.ContentTemplate><DataTemplate>" +
            "<StackPanel>" +
            "<TextBlock Text=\"{Binding Text, Source={x:Reference src}}\"/>" + // forward, inside the template
            "<TextBox x:Name=\"src\" Text=\"tmplref\"/>" +
            "</StackPanel>" +
            "</DataTemplate></ContentControl.ContentTemplate>" +
            "</ContentControl>");
        host.ShowRoot(root);
        host.RunUntilIdle();

        Assert.Equal(2, RowsContaining(host, 8, "tmplref"));
    }

    private static int RowsContaining(UITestHost host, int rows, string text)
    {
        var count = 0;
        for (var r = 0; r < rows; r++)
            if (host.GetRowText(r).Contains(text, StringComparison.Ordinal))
                count++;
        return count;
    }
}
