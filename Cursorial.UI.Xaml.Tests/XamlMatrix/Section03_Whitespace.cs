using Cursorial.UI.Xaml;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// §3 — Whitespace, comments, PIs, DTD (X31–X40). The whitespace rows (X31–X36) and DTD rows
/// (X39–X40) are the System.Xaml oracle leg (Windows-only CI); off-Windows we assert Cursorial's
/// documented XD19 behavior directly. Frontend.
/// </summary>
public sealed class Section03_Whitespace : XamlTestBase
{
    [Fact, Trait("Oracle", "SystemXaml")] // X31
    public void X031_AttributeValue_Verbatim()
    {
        var doc = Parse("<TextBlock Text=\"  spaced  \"/>");
        Assert.True(doc.TryFindMember(0, "Text", out var m));
        Assert.Equal(XamlValueKind.Text, m.Kind);
        Assert.Equal("  spaced  ", doc.StringValue(m));
    }

    [Fact, Trait("Oracle", "SystemXaml")] // X32
    public void X032_ElementText_TrimmedBothEnds()
    {
        var doc = Parse("<TextBlock>  hello  </TextBlock>");
        Assert.True(doc.TryFindMember(0, "Text", out var m));
        Assert.Equal("hello", doc.StringValue(m));
    }

    [Fact, Trait("Oracle", "SystemXaml")] // X33
    public void X033_ElementText_InteriorNewlineCollapses()
    {
        var doc = Parse("<TextBlock>a\n   b</TextBlock>");
        Assert.True(doc.TryFindMember(0, "Text", out var m));
        Assert.Equal("a b", doc.StringValue(m));
    }

    [Fact, Trait("Oracle", "SystemXaml")] // X34
    public void X034_XmlSpacePreserve_Verbatim()
    {
        var doc = Parse("<TextBlock xml:space=\"preserve\">  a\n  b  </TextBlock>");
        Assert.True(doc.TryFindMember(0, "Text", out var m));
        Assert.Equal("  a\n  b  ", doc.StringValue(m));
    }

    [Fact, Trait("Oracle", "SystemXaml")] // X35
    public void X035_WhitespaceOnlyBody_Dropped()
    {
        var doc = Parse("<StackPanel>\n   \n</StackPanel>");
        // no content member; Children empty
        Assert.Equal(0, doc.RootMemberCount());
        Assert.Equal(1, doc.ObjectCount());
    }

    [Fact, Trait("Oracle", "SystemXaml")] // X36
    public void X036_MixedWhitespaceAndElement_OneChild()
    {
        var doc = Parse("<StackPanel>\n  <Button/>\n</StackPanel>");
        Assert.Equal(2, doc.ObjectCount());
        Assert.True(doc.TryFindMember(0, "Children", out var m));
        // one Button child (the inter-element whitespace is insignificant)
        int count = m.Kind == XamlValueKind.Items ? m.ItemCount : 1;
        Assert.Equal(1, count);
    }

    [Fact] // X37
    public void X037_Comment_Ignored()
    {
        var doc = Parse("<StackPanel><!-- x --><Button/></StackPanel>");
        Assert.Equal(2, doc.ObjectCount()); // StackPanel + Button only
    }

    [Fact] // X38
    public void X038_ProcessingInstruction_Ignored()
    {
        var doc = Parse("<StackPanel><?pi data?><Button/></StackPanel>");
        Assert.Equal(2, doc.ObjectCount());
    }

    [Fact] // X39
    public void X039_Dtd_CUR1001()
    {
        string xml = "<!DOCTYPE Button><Button xmlns=\"https://cursorial.dev/ui\"/>";
        Throws(XamlDiagnosticCodes.DtdProhibited, () => ParseRaw(xml));
    }

    [Fact] // X40
    public void X040_ExternalEntity_CUR1001()
    {
        string xml = "<!DOCTYPE doc [<!ENTITY ext \"x\">]><Button xmlns=\"https://cursorial.dev/ui\">&ext;</Button>";
        Throws(XamlDiagnosticCodes.DtdProhibited, () => ParseRaw(xml));
    }
}
