using Cursorial.UI;
using Cursorial.UI.Data;
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
}
