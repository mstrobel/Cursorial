using Cursorial.UI.Xaml;
using UIControls = Cursorial.UI.Controls;
using UIData = Cursorial.UI.Data;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>§2 — xmlns → CLR resolution, intrinsics, directives (X13–X30). Frontend.</summary>
public sealed class Section02_Resolution : XamlTestBase
{
    [Fact] // X13
    public void X013_DefaultXmlns_ResolvesControls()
    {
        var doc = Parse("<Button/>");
        Assert.Equal(typeof(UIControls.Button), doc.RootType);
    }

    [Fact] // X14
    public void X014_DefaultXmlns_ResolvesData_Binding()
    {
        var doc = Parse("<Binding/>");
        Assert.Equal(typeof(UIData.Binding), doc.RootType);
    }

    [Fact] // X15
    public void X015_StyleResolvesToUiStyle()
    {
        var doc = Parse("<Style/>");
        Assert.Equal(typeof(global::Cursorial.UI.Style), doc.RootType);
    }

    [Fact] // X16
    public void X016_BorderStackPanelTextBlock_ResolveThroughDefaultMap()
    {
        Assert.Equal(typeof(UIControls.Border), Parse("<Border/>").RootType);
        Assert.Equal(typeof(UIControls.StackPanel), Parse("<StackPanel/>").RootType);
        Assert.Equal(typeof(UIControls.TextBlock), Parse("<TextBlock/>").RootType);
    }

    [Fact] // X17
    public void X017_UsingScheme_MapsToClrNamespace()
    {
        string xml = "<local:MyControl xmlns=\"https://cursorial.dev/ui\" " +
                     "xmlns:x=\"https://cursorial.dev/xaml\" xmlns:local=\"using:DemoApp\"/>";
        var doc = ParseRaw(xml);
        Assert.Equal(typeof(TestMetadata.MyControl), doc.RootType);
    }

    [Fact] // X18
    public void X018_ClrNamespaceScheme_MapsIdentically()
    {
        string xml = "<legacy:MyControl xmlns=\"https://cursorial.dev/ui\" " +
                     "xmlns:x=\"https://cursorial.dev/xaml\" xmlns:legacy=\"clr-namespace:DemoApp;assembly=Demo\"/>";
        var doc = ParseRaw(xml);
        Assert.Equal(typeof(TestMetadata.MyControl), doc.RootType);
    }

    [Fact] // X19
    public void X019_UnknownType_CUR2002_DidYouMean()
    {
        var ex = Throws(XamlDiagnosticCodes.TypeNotFound, () => Parse("<Bordr/>"));
        Assert.Contains("Bordr", ex.Message);
        Assert.Contains("Border", ex.Message); // the did-you-mean suggestion
    }

    [Fact] // X20
    public void X020_AmbiguousType_CUR2001()
    {
        var ex = Throws(XamlDiagnosticCodes.AmbiguousType, () => Parse("<AmbiguousType/>"));
        Assert.Contains("Cursorial.UI.AmbiguousType", ex.Message);
        Assert.Contains("Cursorial.UI.Controls.AmbiguousType", ex.Message);
    }

    [Fact] // X21
    public void X021_XName_IsDirective_NotMember()
    {
        var doc = Parse("<Button x:Name=\"run\"/>");
        Assert.True(doc.Root().HasFlag(ObjectFlags.HasName));
        var members = doc.MembersOf(0);
        Assert.Single(members);
        Assert.Equal(XamlValueKind.Directive, members[0].Kind);
        Assert.Equal((int)XamlDirectiveKindMirror.Name, members[0].DirectiveKind);
        Assert.Equal("run", doc.StringValue(members[0]));
    }

    [Fact] // X22
    public void X022_XClass_SetsRootClassName()
    {
        var doc = Parse("<Button x:Class=\"DemoApp.MainWindow\"/>");
        Assert.Equal("DemoApp.MainWindow", doc.RootClassName);
    }

    [Fact] // X23
    public void X023_XKey_DirectiveAndFlag()
    {
        var doc = Parse("<Button x:Key=\"AccentBrush\"/>");
        Assert.True(doc.Root().HasFlag(ObjectFlags.HasKey));
        Assert.True(doc.TryFindDirective(0, XamlDirectiveKindMirror.Key, out var value));
        Assert.Equal("AccentBrush", value);
    }

    [Fact] // X24
    public void X024_XType_FoldsToType()
    {
        var doc = Parse("<Button Width=\"{x:Type Button}\"/>");
        Assert.True(doc.TryFindMember(0, "Width", out var m));
        Assert.Equal(XamlValueKind.Folded, m.Kind);
        Assert.Equal(typeof(UIControls.Button), doc.FoldedValue(m));
    }

    [Fact] // X24b — {x:Type prefix:Button} strips + resolves via the captured prefix (the arg previously kept its prefix)
    public void X024b_XType_PrefixedArg_FoldsViaCapturedNamespace()
    {
        // c: is an explicit prefix bound to the default UI namespace; the x:Type argument carries it. Previously
        // the whole arg ("c:Button") was resolved verbatim against the default ns and missed; now the prefix binds
        // from the live reader scope. (A prefix to a NON-default CLR namespace is proven end-to-end by the loader.)
        var doc = Parse("<Button xmlns:c=\"https://cursorial.dev/ui\" Width=\"{x:Type c:Button}\"/>");
        Assert.True(doc.TryFindMember(0, "Width", out var m));
        Assert.Equal(XamlValueKind.Folded, m.Kind);
        Assert.Equal(typeof(UIControls.Button), doc.FoldedValue(m));
    }

    [Fact] // X25
    public void X025_XNull_FoldsToNull()
    {
        var doc = Parse("<Button Content=\"{x:Null}\"/>");
        Assert.True(doc.TryFindMember(0, "Content", out var m));
        Assert.Equal(XamlValueKind.Folded, m.Kind);
        Assert.Null(doc.FoldedValue(m));
    }

    [Fact] // X26
    public void X026_XStatic_FoldsToReference()
    {
        var doc = Parse("<Button Content=\"{x:Static Colors.Red}\"/>");
        Assert.True(doc.TryFindMember(0, "Content", out var m));
        Assert.Equal(XamlValueKind.Folded, m.Kind);
        var folded = doc.FoldedValue(m);
        var sref = Assert.IsType<XamlStaticReference>(folded);
        Assert.Equal("Colors.Red", sref.MemberPath);
    }

    [Fact] // X27
    public void X027_UnknownIntrinsic_CUR1201()
    {
        var ex = Throws(XamlDiagnosticCodes.UnknownIntrinsic, () => Parse("<Button x:Bogus=\"y\"/>"));
        Assert.Contains("Bogus", ex.Message);
    }

    [Fact] // X28
    public void X028_TypeArguments_CUR1202()
    {
        Throws(XamlDiagnosticCodes.UnsupportedTypeArguments, () => Parse("<Button x:TypeArguments=\"Button\"/>"));
    }

    [Theory] // X29
    [InlineData("Reference")]
    [InlineData("Array")]
    [InlineData("FieldModifier")]
    [InlineData("Shared")]
    [InlineData("Uid")]
    public void X029_RecordedOutIntrinsics_CUR1203(string intrinsic)
    {
        Throws(XamlDiagnosticCodes.UnsupportedIntrinsic, () => Parse($"<Button x:{intrinsic}=\"y\"/>"));
    }

    [Fact] // X30
    public void X030_MissingPrefix_CUR2003()
    {
        // foo: is undeclared. The default xmlns + x are declared by the preamble.
        Throws(XamlDiagnosticCodes.UndeclaredPrefix, () => Parse("<Button foo:Bar=\"y\"/>"));
    }
}

/// <summary>Mirror of the internal directive-kind enum for test reads.</summary>
internal enum XamlDirectiveKindMirror
{
    Class = 0,
    Name = 1,
    Key = 2,
    DataType = 3,
}
