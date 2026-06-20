using Cursorial.UI;
using Cursorial.UI.Xaml;

using UIControls = Cursorial.UI.Controls;
using Style = Cursorial.UI.Style;

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
}
