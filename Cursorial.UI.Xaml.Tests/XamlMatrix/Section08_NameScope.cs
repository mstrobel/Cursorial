using Cursorial.UI;
using Cursorial.UI.Xaml;
using UIControls = Cursorial.UI.Controls;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// §8 (X101–X108): <c>x:Name</c> → the document namescope; <c>FindName</c>; <c>LoadComponent</c>.
/// (The matrix's <c>&lt;Window&gt;</c> is a placeholder — no Window type exists at P6, so these use a
/// real <c>UIElement</c> root.)
/// </summary>
public sealed class Section08_NameScope : LoaderTestBase
{
    [Fact]
    public void X101_DocumentNameScope_Attached_FindNameResolves()
    {
        var root = Load<UIControls.Border>("<Border x:Name=\"root\"><Button x:Name=\"run\"/></Border>");

        Assert.NotNull(NameScope.GetNameScope(root));
        Assert.IsType<UIControls.Button>(root.FindName("run"));
        Assert.Null(root.FindName("nope"));
    }

    [Fact]
    public void X102_RequireControl_TypedAndMissing()
    {
        var root = Load<UIControls.Border>("<Border x:Name=\"root\"><Button x:Name=\"run\"/></Border>");
        var scope = NameScope.GetNameScope(root)!;

        Assert.IsType<UIControls.Button>(scope.RequireControl<UIControls.Button>("run"));
        Assert.Throws<InvalidOperationException>(() => scope.RequireControl<UIControls.Button>("nope"));
    }

    [Fact]
    public void X103_DuplicateName_Throws()
    {
        var ex = Assert.Throws<XamlParseException>(
            () => Load("<StackPanel><Button x:Name=\"dup\"/><Border x:Name=\"dup\"/></StackPanel>"));
        Assert.Equal(XamlDiagnosticCodes.DuplicateName, ex.Code);
        Assert.True(ex.Line > 0 && ex.Column > 0);
    }

    [Fact]
    public void X104_NameInResourceDictionary_Throws()
    {
        var ex = Assert.Throws<XamlParseException>(() => LoadRaw(
            "<ResourceDictionary xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\">" +
            "<Border x:Key=\"A\" x:Name=\"bad\"/></ResourceDictionary>"));
        Assert.Equal(XamlDiagnosticCodes.NameInResourceDictionary, ex.Code);
    }

    [Fact]
    public void X105_LoadComponent_PopulatesExistingInstance()
    {
        var doc = Parse("<Border x:Name=\"root\"><Button x:Name=\"run\"/></Border>");
        var instance = new UIControls.Border();
        Loader.LoadComponent(instance, doc);

        Assert.IsType<UIControls.Button>(instance.Child);
        Assert.IsType<UIControls.Button>(instance.FindName("run"));
    }

    [Fact]
    public void X106_LoadComponent_ExplicitUriOverload()
    {
        var instance = new UIControls.Border();
        Loader.LoadComponent(instance, Wrap("<Border><Button x:Name=\"run\"/></Border>"), TestSource);
        Assert.IsType<UIControls.Button>(instance.FindName("run"));
    }

    [Fact]
    public void X107_StaticLoadComponent_OneArg_Throws_TwoArgRoutes()
    {
        // The one-arg form requires the x:Class embedded-resource convention (X2).
        Assert.Throws<NotSupportedException>(() => Loader.LoadComponent(new UIControls.Border()));

        // The explicit-document form routes through the loader.
        var instance = new UIControls.Border();
        var doc = Parse("<Border><Button x:Name=\"run\"/></Border>");
        Loader.LoadComponent(instance, doc);
        Assert.NotNull(instance.FindName("run"));
    }

    [Fact]
    public void X108_FindName_ResolvesDocumentNames()
    {
        // A document content child resolves document names (the template path is §13).
        var root = Load<UIControls.StackPanel>(
            "<StackPanel x:Name=\"root\"><Border x:Name=\"box\"><Button x:Name=\"run\"/></Border></StackPanel>");
        var button = (UIControls.Button)((UIControls.Border)root.Children[0]).Child!;
        Assert.Same(button, button.FindName("run"));
        Assert.Same(root, button.FindName("root"));
    }
}
