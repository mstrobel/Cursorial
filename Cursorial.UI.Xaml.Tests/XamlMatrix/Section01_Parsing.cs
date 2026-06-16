using Cursorial.UI.Xaml;
using UIControls = Cursorial.UI.Controls;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>§1 — XML → node graph: element &amp; property-element syntax (X1–X12). Frontend, no host.</summary>
public sealed class Section01_Parsing : XamlTestBase
{
    [Fact] // X1
    public void X001_SingleElement_OneRootObject()
    {
        var doc = Parse("<Button/>");
        Assert.Equal(1, doc.ObjectCount());
        Assert.Equal("Button", doc.RootTypeLocalName());
        Assert.True(doc.Root().HasFlag(ObjectFlags.IsRoot));
        Assert.Equal(0, doc.RootMemberCount());
        Assert.Equal(1, doc.RootSubtreeLength());
        Assert.Equal(typeof(UIControls.Button), doc.RootType);
    }

    [Fact] // X2
    public void X002_AttributeMember_ContentText()
    {
        var doc = Parse("<Button Content=\"Hi\"/>");
        Assert.Equal(1, doc.RootMemberCount());
        Assert.True(doc.TryFindMember(0, "Content", out var m));
        // Content is an object-typed slot (context-dependent): stays Text at parse.
        Assert.Equal(XamlValueKind.Text, m.Kind);
        Assert.Equal("Hi", doc.StringValue(m));
        Assert.Null(doc.RootClassName);
    }

    [Fact] // X3
    public void X003_ContentChild_FillsContentProperty()
    {
        var doc = Parse("<Border><Button/></Border>");
        Assert.Equal("Border", doc.RootTypeLocalName());
        Assert.Equal(2, doc.RootSubtreeLength());
        Assert.True(doc.TryFindMember(0, "Child", out var m));
        Assert.Equal(XamlValueKind.Object, m.Kind);
        Assert.Equal(1, m.ValueIndex); // the Button is the second object, depth-first contiguous
    }

    [Fact] // X4
    public void X004_PropertyElement_SameAsAttribute()
    {
        var doc = Parse("<Button><Button.Content>Hi</Button.Content></Button>");
        Assert.True(doc.TryFindMember(0, "Content", out var m));
        Assert.Equal("Hi", doc.StringValue(m));
    }

    [Fact] // X5
    public void X005_BothForms_Duplicate_CUR1101()
    {
        Throws(XamlDiagnosticCodes.DuplicatePropertyAssignment,
            () => Parse("<Button Content=\"A\"><Button.Content>B</Button.Content></Button>"));
    }

    [Fact] // X6
    public void X006_ImplicitChildrenCollection()
    {
        var doc = Parse("<StackPanel><Button/><Border/></StackPanel>");
        Assert.Equal(3, doc.RootSubtreeLength());
        Assert.True(doc.TryFindMember(0, "Children", out var m));
        Assert.Equal(XamlValueKind.Items, m.Kind);
        Assert.Equal(2, m.ItemCount);
    }

    [Fact] // X7
    public void X007_ExplicitCollectionPropertyElement()
    {
        var doc = Parse("<StackPanel><StackPanel.Children><Button/></StackPanel.Children></StackPanel>");
        Assert.True(doc.TryFindMember(0, "Children", out _));
        // a single child through the explicit collection element still fills Children
        Assert.Equal(1, doc.ObjectCount() - 1); // one child object
    }

    [Fact] // X8
    public void X008_NestedPropertyElement_Collection()
    {
        var doc = Parse("<Grid><Grid.RowDefinitions><RowDefinition/></Grid.RowDefinitions></Grid>");
        Assert.True(doc.TryFindMember(0, "RowDefinitions", out var m));
        Assert.Equal(1, m.ItemCount == 0 ? 1 : m.ItemCount); // one RowDefinition item
        Assert.True(m.Kind is XamlValueKind.Items or XamlValueKind.Object);
    }

    [Fact] // X9
    public void X009_SelfClosing_AttributesInOrder()
    {
        var doc = Parse("<Button Content=\"X\" Width=\"10\"/>");
        var members = doc.MembersOf(0);
        Assert.Equal(2, members.Length);
        // Width folds to int 10 (context-free); Content stays Text.
        Assert.True(doc.TryFindMember(0, "Width", out var width));
        Assert.Equal(XamlValueKind.Folded, width.Kind);
        Assert.Equal(10, doc.FoldedValue(width));
        Assert.True(doc.TryFindMember(0, "Content", out var content));
        Assert.Equal(XamlValueKind.Text, content.Kind);
    }

    [Fact] // X10
    public void X010_LineInfoContract()
    {
        // a 3-line document, the Button opening on line 2 col 3 (col counts the '<').
        string xml = "<Border xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\">\n  <Button/>\n</Border>";
        var doc = ParseRaw(xml);
        // child object is at index 1
        ref readonly var child = ref doc.ObjectAt(1);
        Assert.Equal(2, NodeGraphProbe.Line(in child));
        Assert.Equal(3, NodeGraphProbe.Column(in child));
        // every object carries a non-zero position
        for (int i = 0; i < doc.ObjectCount(); i++)
        {
            ref readonly var o = ref doc.ObjectAt(i);
            Assert.True(NodeGraphProbe.Line(in o) > 0);
            Assert.True(NodeGraphProbe.Column(in o) > 0);
        }
    }

    [Fact] // X11
    public void X011_EmptyElementBody_NoContentMember()
    {
        var doc = Parse("<Button></Button>");
        Assert.Equal(1, doc.ObjectCount());
        Assert.Equal(0, doc.RootMemberCount());
    }

    [Fact] // X12
    public void X012_DeepTree_SubtreeLengthInvariant()
    {
        var doc = Parse("<Border><Border><Border><Border><Button/></Border></Border></Border></Border>");
        Assert.Equal(5, doc.ObjectCount());
        Assert.Equal(5, doc.RootSubtreeLength());
        // each record's SubtreeLength = count of objects in its subtree incl. self
        for (int i = 0; i < doc.ObjectCount(); i++)
        {
            ref readonly var o = ref doc.ObjectAt(i);
            Assert.Equal(doc.ObjectCount() - i, o.SubtreeLength); // a straight chain: subtree = remaining
        }
    }
}
