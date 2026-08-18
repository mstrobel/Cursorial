using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.Integration;

/// <summary>A custom markup extension used as a Setter.Value — provided eagerly and target-less by
/// the RUNTIME loader (BuildSetter's Custom arm), retiring the v1 "not supported" restriction. The
/// lowered lane's twin lives in CustomExtensionLoweringTests.</summary>
public sealed class StampExtension : MarkupExtension
{
    public override object? ProvideValue(IServiceProvider serviceProvider) => "styled!";
}

public sealed class SetterCustomExtensionTests
{
    static SetterCustomExtensionTests() =>
        XamlSchemaContext.Default.RegisterAssembly(typeof(SetterCustomExtensionTests).Assembly);

    [Fact]
    public void RuntimeLoader_SetterValue_CustomExtension_Provides()
    {
        const string xaml =
            "<StackPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" " +
            "xmlns:t=\"clr-namespace:Cursorial.Tests.UI.Xaml.Integration;assembly=Cursorial.UI.Xaml.Tests\">" +
            "<StackPanel.Resources>" +
              "<Style x:Key=\"S\" Selector=\":is(Button)\">" +
                "<Setter Property=\"ContentControl.Content\" Value=\"{t:Stamp}\"/>" +
              "</Style>" +
            "</StackPanel.Resources>" +
            "</StackPanel>";

        var root = (StackPanel)XamlLoader.Shared.Load(xaml, null);
        var style = Assert.IsType<Style>(root.Resources["S"]);
        var setter = Assert.Single(style.Setters);
        Assert.Equal("styled!", setter.Value); // provided, not a silent valueless setter
    }
}
