using Cursorial.UI;
using Cursorial.UI.Xaml;
using Cursorial.Rendering;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>§5 — Constant folding &amp; Setter folding (X59–X66). XD3 + the Setter special fold. Frontend.</summary>
public sealed class Section05_Folding : XamlTestBase
{
    [Fact] // X59
    public void X059_ContextFreeInt_Folds()
    {
        var doc = Parse("<Button Width=\"10\"/>");
        Assert.True(doc.TryFindMember(0, "Width", out var m));
        Assert.Equal(XamlValueKind.Folded, m.Kind);
        Assert.Equal(10, doc.FoldedValue(m));
    }

    [Fact] // X60
    public void X060_ContextFreeMargins_FoldsAndShares()
    {
        var doc = Parse("<Button Margin=\"1,2\"/>");
        Assert.True(doc.TryFindMember(0, "Margin", out var m));
        Assert.Equal(XamlValueKind.Folded, m.Kind);
        Assert.Equal(new Margins(1, 2), doc.FoldedValue(m));
        // The boxed-constant-sharing-across-Loads aspect (XD20) is asserted in the X1 stage; here we
        // assert the fold happened once into the Constants array.
        Assert.Single(doc.Constants);
    }

    [Fact] // X61
    public void X061_ObjectTypedContent_StaysText()
    {
        var doc = Parse("<Button Content=\"Hi\"/>");
        Assert.True(doc.TryFindMember(0, "Content", out var m));
        Assert.Equal(XamlValueKind.Text, m.Kind); // the access-key / AccessText decision is stage-2 (XD11)
    }

    [Fact] // X62
    public void X062_Enum_Folds()
    {
        var doc = Parse("<Button Visibility=\"Collapsed\"/>");
        Assert.True(doc.TryFindMember(0, "Visibility", out var m));
        Assert.Equal(XamlValueKind.Folded, m.Kind);
        Assert.Equal(Visibility.Collapsed, doc.FoldedValue(m));
    }

    [Fact] // X63
    public void X063_FoldConstantsFalse_StaysText()
    {
        var doc = Parse("<Button Width=\"10\"/>", fold: false);
        Assert.True(doc.TryFindMember(0, "Width", out var m));
        Assert.Equal(XamlValueKind.Text, m.Kind);
        Assert.Equal("10", doc.StringValue(m)); // the convert is deferred to stage 2
    }

    [Fact] // X64
    public void X064_SetterValue_FoldsAgainstLexicalTargetType()
    {
        var doc = Parse("<Style TargetType=\"Button\"><Setter Property=\"Width\" Value=\"10\"/></Style>");
        // the Setter is the second object; its Value folds through Width's converter to int 10
        Assert.True(doc.TryFindMember(1, "Value", out var value));
        Assert.Equal(XamlValueKind.Folded, value.Kind);
        Assert.Equal(10, doc.FoldedValue(value));
    }

    [Fact] // X65
    public void X065_SetterNoResolvableOwner_CUR2110()
    {
        Throws(XamlDiagnosticCodes.SetterNoTarget,
            () => Parse("<Setter Property=\"Width\" Value=\"10\"/>"));
    }

    [Fact] // X66
    public void X066_SetterUnknownProperty_CUR2102()
    {
        Throws(XamlDiagnosticCodes.MemberNotFound,
            () => Parse("<Style TargetType=\"Button\"><Setter Property=\"Nope\" Value=\"x\"/></Style>"));
    }
}
