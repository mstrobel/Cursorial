using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// XAML2009 built-in primitives, currently usable only in element form (<c>&lt;x:Boolean&gt;True&lt;/x:Boolean&gt;</c>),
/// are now ALSO accepted as single-positional-argument markup extensions (<c>{x:Boolean True}</c>, <c>{x:Int32 4096}</c>).
/// The frontend builds the identical synthetic primitive-object node for both forms, so the loader (and the
/// generator) need no new path — these assert the curly form produces the same value as the element form.
/// </summary>
public sealed class PrimitiveMarkupExtensionTests : LoaderTestBase
{
    [Fact] // {x:Boolean False} (curly) sets the same value as <x:Boolean>False</x:Boolean> (element form).
    public void XBoolean_CurlyExtension_EqualsElementForm()
    {
        var curly = Load<UIControls.Button>("<Button IsEnabled=\"{x:Boolean False}\"/>");
        Assert.False(curly.IsEnabled);

        var element = Load<UIControls.Button>(
            "<Button><Button.IsEnabled><x:Boolean>False</x:Boolean></Button.IsEnabled></Button>");
        Assert.False(element.IsEnabled);

        Assert.Equal(element.IsEnabled, curly.IsEnabled);
    }

    [Fact] // {x:String hello} (curly) yields the same string as the element/plain forms.
    public void XString_CurlyExtension_EqualsElementForm()
    {
        var curly = Load<UIControls.TextBlock>("<TextBlock Text=\"{x:String hello}\"/>");
        Assert.Equal("hello", curly.Text);

        var element = Load<UIControls.TextBlock>(
            "<TextBlock><TextBlock.Text><x:String>hello</x:String></TextBlock.Text></TextBlock>");
        Assert.Equal("hello", element.Text);
    }
}
