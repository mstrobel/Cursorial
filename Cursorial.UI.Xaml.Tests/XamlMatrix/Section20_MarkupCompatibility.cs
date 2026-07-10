using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// Section 20 — markup compatibility (<c>mc:Ignorable</c>) and design-time (<c>d:</c>) metadata.
/// The parser natively knows the design namespaces (Blend URI + the Cursorial alias) and skips
/// them without requiring <c>mc:Ignorable</c>; other tool namespaces are skipped only when the
/// root marks their prefix ignorable. The root's <c>d:DesignWidth</c> / <c>d:DesignHeight</c> /
/// <c>d:DataContext</c> surface on <see cref="XamlDocument.DesignInfo"/> for designer hosts;
/// runtime loading never reads them.
/// </summary>
public sealed class Section20_MarkupCompatibility : XamlTestBase
{
    private const string Mc = "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"";
    private const string D = "xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\"";
    private const string DCursorial = "xmlns:d=\"https://cursorial.dev/xaml/design\"";

    private static string Root(string rootExtras, string body = "")
        => "<StackPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" " +
           rootExtras + ">" + body + "</StackPanel>";

    [Fact] // d:DesignWidth/Height on the root are captured into DesignInfo, diagnostics-free
    public void X200_DesignWidthHeight_CapturedIntoDesignInfo()
    {
        var doc = ParseRaw(Root($"{Mc} {D} mc:Ignorable=\"d\" d:DesignWidth=\"100\" d:DesignHeight=\"30\""));

        Assert.Empty(doc.Diagnostics);
        Assert.NotNull(doc.DesignInfo);
        Assert.Equal(100, doc.DesignInfo!.DesignWidth);
        Assert.Equal(30, doc.DesignInfo.DesignHeight);
        Assert.Null(doc.DesignInfo.DataContextType);
    }

    [Fact] // d:DataContext resolves a (prefix-qualified) type through the metadata provider
    public void X201_DesignDataContext_ResolvesThroughProvider()
    {
        var doc = ParseRaw(Root($"{Mc} {D} xmlns:vm=\"using:DemoApp\" mc:Ignorable=\"d\" d:DataContext=\"vm:MyControl\""));

        Assert.Empty(doc.Diagnostics);
        Assert.Equal("MyControl", doc.DesignInfo!.DataContextType!.ClrType.Name);
    }

    [Fact] // no design attributes → no DesignInfo allocation
    public void X202_NoDesignAttributes_DesignInfoNull()
    {
        var doc = ParseRaw(Root(""));
        Assert.Null(doc.DesignInfo);
    }

    [Fact] // unknown d:* names are forward-compatible: skipped silently, never CUR2102
    public void X203_UnknownDesignAttribute_SkippedSilently()
    {
        var doc = ParseRaw(Root($"{Mc} {D} mc:Ignorable=\"d\" d:SomeFutureThing=\"whatever\""));

        Assert.Empty(doc.Diagnostics);
        Assert.Null(doc.DesignInfo);
    }

    [Fact] // a d: child ELEMENT'S whole subtree is skipped without dropping the following sibling
    public void X204_DesignElementSubtree_SkippedWithoutDroppingSiblings()
    {
        var doc = ParseRaw(Root($"{Mc} {D} mc:Ignorable=\"d\"",
                                "<d:DesignData><Border><Border/></Border></d:DesignData><Button/>"));

        Assert.Empty(doc.Diagnostics);
        Assert.Equal(2, doc.ObjectCount()); // StackPanel + Button; the design subtree left no records
    }

    [Fact] // CUR2005 — mc:Ignorable naming an undeclared prefix warns and the parse continues
    public void X205_IgnorableUndeclaredPrefix_CUR2005Warning()
    {
        var doc = ParseRaw(Root($"{Mc} mc:Ignorable=\"nope\""), XamlDiagnosticMode.CollectAll);

        var diagnostic = Assert.Single(doc.Diagnostics);
        Assert.Equal(XamlDiagnosticCodes.IgnorablePrefixNotDeclared, diagnostic.Code);
        Assert.Equal(XamlDiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact] // CUR2006 — a non-numeric d:DesignWidth warns, is ignored, and the parse continues
    public void X206_InvalidDesignWidth_CUR2006Warning()
    {
        var doc = ParseRaw(Root($"{Mc} {D} mc:Ignorable=\"d\" d:DesignWidth=\"wide\""), XamlDiagnosticMode.CollectAll);

        var diagnostic = Assert.Single(doc.Diagnostics);
        Assert.Equal(XamlDiagnosticCodes.DesignValueInvalid, diagnostic.Code);
        Assert.Equal(XamlDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Null(doc.DesignInfo);
    }

    [Fact] // CUR2006 — an unresolvable d:DataContext type warns instead of erroring
    public void X207_UnresolvableDesignDataContext_CUR2006Warning()
    {
        var doc = ParseRaw(Root($"{Mc} {D} mc:Ignorable=\"d\" d:DataContext=\"NoSuchViewModel\""), XamlDiagnosticMode.CollectAll);

        var diagnostic = Assert.Single(doc.Diagnostics);
        Assert.Equal(XamlDiagnosticCodes.DesignValueInvalid, diagnostic.Code);
        Assert.Equal(XamlDiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact] // a generic tool namespace marked Ignorable is skipped — attributes AND elements,
           // order-independently (the tool attribute precedes mc:Ignorable in the tag)
    public void X208_GenericIgnorableNamespace_AttributesAndElementsSkipped()
    {
        var doc = ParseRaw(Root($"{Mc} xmlns:tool=\"urn:some-tool\" tool:Marker=\"1\" mc:Ignorable=\"tool\"",
                                "<tool:Extra><Border/></tool:Extra><Button/>"));

        Assert.Empty(doc.Diagnostics);
        Assert.Equal(2, doc.ObjectCount()); // StackPanel + Button
    }

    [Fact] // CUR2102 — a non-design tool namespace WITHOUT mc:Ignorable still errors (opt-in only)
    public void X209_ToolNamespaceWithoutIgnorable_StillErrors()
    {
        Throws(XamlDiagnosticCodes.MemberNotFound,
               () => ParseRaw(Root("xmlns:tool=\"urn:some-tool\" tool:Marker=\"1\"")));
    }

    [Fact] // the design namespaces are natively known: d: works WITHOUT mc:Ignorable
    public void X210_DesignNamespace_WorksWithoutIgnorable()
    {
        var doc = ParseRaw(Root($"{D} d:DesignWidth=\"42\""));

        Assert.Empty(doc.Diagnostics);
        Assert.Equal(42, doc.DesignInfo!.DesignWidth);
    }

    [Fact] // the Cursorial-native design URI is an accepted alias for the Blend URI
    public void X211_CursorialDesignAlias_Works()
    {
        var doc = ParseRaw(Root($"{DCursorial} d:DesignWidth=\"24\" d:DesignHeight=\"8\""));

        Assert.Empty(doc.Diagnostics);
        Assert.Equal(24, doc.DesignInfo!.DesignWidth);
        Assert.Equal(8, doc.DesignInfo.DesignHeight);
    }

    [Fact] // design attributes on NON-root elements are skipped without capture or diagnostics
    public void X212_DesignAttributeOnNonRoot_IgnoredSilently()
    {
        var doc = ParseRaw(Root($"{Mc} {D} mc:Ignorable=\"d\"", "<Button d:DesignWidth=\"9\"/>"));

        Assert.Empty(doc.Diagnostics);
        Assert.Null(doc.DesignInfo);
    }
}
