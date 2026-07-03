using Cursorial.UI.Xaml;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// §13 — Deferred content, the X0 parse-time half (X147 slice capture, X148 IsDeferredContent
/// derivation, X150 DataTemplate defer, X151 type error in body at parse, X152 event rejection at
/// parse). The X3 Build/namescope/lexical-capture/access-key-fold rows (X153–X168) are the loader
/// stage and absent (not red) until X3 per the stage map.
/// </summary>
public sealed class Section13_DeferredContentParse : XamlTestBase
{
    [Fact] // X147
    public void X147_ControlTemplateContent_CapturedAsDeferredSlice()
    {
        var doc = Parse("<ControlTemplate><Border><Button/></Border></ControlTemplate>");
        // ControlTemplate.Content is ITemplateContent-typed ⇒ the <Border>… subtree is a Deferred slice
        Assert.True(doc.TryFindMember(0, "Content", out var m));
        Assert.Equal(XamlValueKind.Deferred, m.Kind);
        // the slice head is the Border (object index 1); the subtree is contiguous (Border + Button)
        Assert.Equal(1, m.ValueIndex);
        ref readonly var sliceHead = ref doc.ObjectAt(1);
        Assert.Equal(2, sliceHead.SubtreeLength); // Border + Button
        Assert.True(sliceHead.HasFlag(ObjectFlags.InDeferredContent));
    }

    [Fact] // X148
    public void X148_IsDeferredContent_DerivedFromValueType()
    {
        // A type with no ITemplateContent member never defers (Border.Child is object-typed).
        var doc = Parse("<Border><Button/></Border>");
        Assert.True(doc.TryFindMember(0, "Child", out var m));
        Assert.NotEqual(XamlValueKind.Deferred, m.Kind);
        // the child is not marked deferred
        ref readonly var child = ref doc.ObjectAt(1);
        Assert.False(child.HasFlag(ObjectFlags.InDeferredContent));
    }

    [Fact] // X150
    public void X150_DataTemplate_DefersIdentically()
    {
        var doc = Parse("<DataTemplate><TextBlock Text=\"hi\"/></DataTemplate>");
        Assert.True(doc.TryFindMember(0, "Content", out var m));
        Assert.Equal(XamlValueKind.Deferred, m.Kind);
        ref readonly var child = ref doc.ObjectAt(1);
        Assert.True(child.HasFlag(ObjectFlags.InDeferredContent));
    }

    [Fact] // X151
    public void X151_TypeErrorInsideTemplateBody_AtParse()
    {
        // A type error inside a deferred slice surfaces at parse time with position (doc §4.1).
        Throws(XamlDiagnosticCodes.TypeNotFound,
            () => Parse("<ControlTemplate><Bogus/></ControlTemplate>"));
    }

    [Fact] // X152
    public void X152_EventInsideTemplateBody_CUR2301()
    {
        // An event attribute inside a deferred slice is rejected at parse (XD14; templates use commands).
        Throws(XamlDiagnosticCodes.EventInDeferredContent,
            () => Parse("<ControlTemplate><Button Click=\"OnX\"/></ControlTemplate>"));
    }
}
