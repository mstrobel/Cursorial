using Cursorial.UI;

using UIControls = Cursorial.UI.Controls;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// §9 (X109–X112): the value-vs-markup-extension disambiguation at the instantiation boundary.
/// </summary>
public sealed class Section09_Disambiguation : LoaderTestBase
{
    [Fact]
    public void X109_BindingExtension_NotALiteral()
    {
        // {Binding Name} is recognized as an extension (not the literal string). At X1 the live attach
        // (X2) is not yet wired, so Content stays unset — but it is NOT "{Binding Name}".
        var button = Load<UIControls.Button>("<Button Content=\"{Binding Name}\"/>");
        Assert.NotEqual("{Binding Name}", button.Content);
        Assert.Equal(BindingPriority.Default, button.GetValueSource(UIControls.ContentControl.ContentProperty).Priority);
    }

    [Fact]
    public void X110_EscapedBrace_IsLiteral()
    {
        var button = Load<UIControls.Button>("<Button Content=\"{}{Binding Name}\"/>");
        Assert.Equal("{Binding Name}", button.Content);
    }

    [Fact]
    public void X111_PlainValue_IsLiteral()
    {
        var button = Load<UIControls.Button>("<Button Content=\"plain\"/>");
        Assert.Equal("plain", button.Content);
    }

    [Fact]
    public void X112_LeadingSpaceBeforeBrace_IsLiteral()
    {
        var button = Load<UIControls.Button>("<Button Content=\" {Binding Name}\"/>");
        Assert.Equal(" {Binding Name}", button.Content);
    }
}
