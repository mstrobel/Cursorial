using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// Design-fragment re-parsing rides <c>XmlReader.ReadSubtree</c>, which SYNTHESIZES in-scope xmlns
/// declarations lazily — a prefix first USED on a nested element gets its declaration emitted on
/// that element (position 0:0, no source text). Fragment-mode parsing must record those instead of
/// raising CUR2004 (the top-level-only policy is for user-written documents).
/// </summary>
public sealed class DesignFragmentSynthesizedXmlnsTests : XamlTestBase
{
    [Fact]
    public void FragmentWithNestedPrefixFirstUse_ParsesWithoutCur2004()
    {
        var xaml =
            "<StackPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" " +
            "xmlns:d=\"https://cursorial.dev/xaml/design\" xmlns:vm=\"using:DemoApp\">" +
            "<d:DataContext>" +
            "<Border><vm:MyControl/></Border>" +   // vm: first used NESTED → its xmlns is synthesized there
            "</d:DataContext>" +
            "<TextBlock/>" +
            "</StackPanel>";
        var doc = ParseRaw(xaml, XamlDiagnosticMode.CollectAll);

        Assert.Empty(doc.Diagnostics);
        var fragment = doc.DesignInfo?.DataContextContent;
        Assert.NotNull(fragment);
        Assert.DoesNotContain(fragment!.Diagnostics, d => d.Code == XamlDiagnosticCodes.NamespaceNotOnRoot);
        Assert.True(fragment.HasRootType);
    }
}
