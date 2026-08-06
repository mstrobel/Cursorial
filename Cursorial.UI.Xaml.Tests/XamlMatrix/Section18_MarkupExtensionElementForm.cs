using Cursorial.Media;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;

using UIControls = Cursorial.UI.Controls;
using Style = Cursorial.UI.Style;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// §18 (X56a) — markup extensions in ELEMENT form. Every built-in extension (and a custom one) is usable as
/// an element in any value position: a property-element value, a dictionary entry (resource aliasing), and
/// content. Element attributes map to the extension's named arguments, resolving identically to the curly
/// form. The parser translates the element into the same extension value; the loader attaches it to the
/// target member or produces its standalone value.
/// </summary>
public sealed class Section18_MarkupExtensionElementForm : LoaderTestBase
{
    private const string Pre = " xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    [Fact] // X56a — <StaticResource ResourceKey="…"/> as a property-element value resolves eagerly.
    public void X56a_StaticResource_ElementForm_PropertyElementValue()
    {
        var border = (UIControls.Border)LoadRaw(
            $"<Border{Pre}><Border.Resources><TestBrush x:Key=\"A\" Color=\"Red\"/></Border.Resources>" +
            "<Button><Button.Background><StaticResource ResourceKey=\"A\"/></Button.Background></Button></Border>");
        Assert.IsType<TestBrush>(((UIControls.Button)border.Child!).Background);
    }

    [Fact] // X56b — <x:Null/> as a property-element value assigns null.
    public void X56b_XNull_ElementForm()
    {
        var border = Load<UIControls.Border>("<Border><Border.BorderPen><x:Null/></Border.BorderPen></Border>");
        Assert.Null(border.BorderPen);
    }

    [Fact] // X56c — resource ALIASING: <DynamicResource x:Key=… ResourceKey=…/> as a dictionary entry stores a
    // ResourceReference carrier under the key (the CursorialTheme.Alias shape, now expressible in XAML).
    public void X56c_DynamicResource_ElementForm_DictionaryAlias()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}><TestBrush x:Key=\"OnAccent\" Color=\"Red\"/>" +
            "<DynamicResource x:Key=\"AccentFg\" ResourceKey=\"OnAccent\"/></ResourceDictionary>");
        var alias = Assert.IsType<ResourceReference>(dict["AccentFg"]);
        Assert.Equal("OnAccent", alias.Key);
    }

    [Fact] // X56d — <DynamicResource> element form installs a live producer (resolves after attach), same as curly.
    public void X56d_DynamicResource_ElementForm_LiveOnUIProperty()
    {
        using var host = UIHeadlessHost.Create();
        var border = Load<UIControls.Border>(
            "<Border><Border.Resources><TestBrush x:Key=\"Accent\" Color=\"Red\"/></Border.Resources>" +
            "<Button><Button.Background><DynamicResource ResourceKey=\"Accent\"/></Button.Background></Button></Border>");
        host.ShowRoot(border);
        host.RunFrame();
        Assert.IsType<TestBrush>(((UIControls.Button)border.Child!).Background);
    }

    [Fact] // X56e — <DynamicResource> in a Setter.Value stores the carrier (element-form Setter.Value).
    public void X56e_DynamicResource_ElementForm_SetterValue()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}><Style x:Key=\"S\" TargetType=\"Button\">" +
            "<Setter Property=\"Background\"><DynamicResource ResourceKey=\"Accent\"/></Setter></Style></ResourceDictionary>");
        var setter = ((Style)dict["S"]!).Setters[0];
        Assert.Equal("Accent", Assert.IsType<ResourceReference>(setter.Value).Key);
    }

    [Fact] // X56f — <Binding Path="…"/> element form attaches like the curly form.
    public void X56f_Binding_ElementForm_Attaches()
    {
        var border = Load<UIControls.Border>(
            "<Border><StackPanel><TextBlock><TextBlock.Text><Binding Path=\"Length\"/></TextBlock.Text></TextBlock></StackPanel></Border>");
        border.SetValue(UIElement.DataContextProperty, "hello");
        var tb = (UIControls.TextBlock)((UIControls.StackPanel)border.Child!).Children[0];
        Assert.Equal("5", tb.Text);
    }

    [Fact] // X56g — <TemplateBinding Property="…"/> element form tracks the templated parent.
    public void X56g_TemplateBinding_ElementForm_TracksParent()
    {
        var template = Load<UIControls.ControlTemplate>(
            "<ControlTemplate><Border><Border.Background><TemplateBinding Property=\"Background\"/></Border.Background></Border></ControlTemplate>");
        template.TargetType ??= typeof(UIControls.Button);
        var owner = new UIControls.Button { Background = new TestBrush { Color = Color.FromRgb(1, 2, 3) } };
        var part = (UIControls.Border)template.Instantiate(owner).Root;
        Assert.Same(owner.Background, part.Background);
    }

    [Fact] // X56h — a CUSTOM extension in element form provides its value; the "Extension" suffix is shorthand.
    public void X56h_CustomExtension_ElementForm_ProvidesValue()
    {
        var button = Load<UIControls.Button>(
            "<Button><Button.Content><Repeat Count=\"3\" Text=\"ab\"/></Button.Content></Button>");
        Assert.Equal("ababab", button.Content);
    }

    [Fact] // X56i — a type whose EXACT name is a non-extension type stays a bare object in element form
    // (<Icon/> is the Icon CONTROL, not IconExtension); the extension is <IconExtension/> or {Icon}.
    public void X56i_ExactTypeName_StaysObject_NotExtension()
    {
        var icon = Load<UIControls.Icon>("<Icon Glyph=\"G\" Text=\"T\"/>");
        Assert.Equal("G", icon.Glyph);
        Assert.Equal("T", icon.Text);
    }

    [Fact] // X56j — a document ROOT that names an extension type is the bare TYPE, not element-form extension.
    public void X56j_ExtensionAtRoot_IsBareType()
    {
        var doc = Parse("<Binding/>");
        Assert.Equal(typeof(Cursorial.UI.Data.Binding), doc.RootType);
    }

    [Fact] // X56l — an extension argument may be a PROPERTY ELEMENT (the verbose form): a literal, and a
    // nested extension (<DynamicResource><DynamicResource.ResourceKey><x:Static …/></…></DynamicResource>).
    public void X56l_PropertyElementArgument()
    {
        // Literal property-element argument.
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}><TestBrush x:Key=\"OnAccent\" Color=\"Red\"/>" +
            "<DynamicResource x:Key=\"AccentFg\">" +
            "<DynamicResource.ResourceKey>OnAccent</DynamicResource.ResourceKey>" +
            "</DynamicResource></ResourceDictionary>");
        Assert.Equal("OnAccent", Assert.IsType<ResourceReference>(dict["AccentFg"]).Key);

        // Nested-extension property-element argument: the StaticResource key resolves eagerly.
        var border = (UIControls.Border)LoadRaw(
            $"<Border{Pre}><Border.Resources><TestBrush x:Key=\"A\" Color=\"Red\"/></Border.Resources>" +
            "<Button><Button.Background>" +
            "<StaticResource><StaticResource.ResourceKey>A</StaticResource.ResourceKey></StaticResource>" +
            "</Button.Background></Button></Border>");
        Assert.IsType<TestBrush>(((UIControls.Button)border.Child!).Background);
    }

    [Fact] // X56k — the named-argument curly form matches the positional form (phase 1): both {DynamicResource X}
    // and {DynamicResource ResourceKey=X} resolve identically.
    public void X56k_NamedCurlyArg_MatchesPositional()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}><TestBrush x:Key=\"A\" Color=\"Red\"/>" +
            "<Style x:Key=\"S\" TargetType=\"Button\">" +
            "<Setter Property=\"Background\" Value=\"{DynamicResource ResourceKey=A}\"/></Style></ResourceDictionary>");
        var setter = ((Style)dict["S"]!).Setters[0];
        Assert.Equal("A", Assert.IsType<ResourceReference>(setter.Value).Key);
    }
}
