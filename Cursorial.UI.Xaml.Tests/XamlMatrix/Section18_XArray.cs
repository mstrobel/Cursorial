using Cursorial.UI;
using Cursorial.UI.Xaml;

using UIControls = Cursorial.UI.Controls;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// XAML2009 <c>&lt;x:Array Type="T"&gt;</c> (XD27) + element-valued built-in primitives (XD28): the array
/// builds a typed <c>T[]</c> from its item children; a built-in primitive element (<c>&lt;x:String&gt;</c>,
/// <c>&lt;x:Int32&gt;</c>, …) initializes from its content text. These are the runtime-loader twins of the
/// frontend parse + generator lowering for the same constructs.
/// </summary>
public sealed class Section18_XArray : LoaderTestBase
{
    private const string Pre = " xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    [Fact] // XA1 — <x:Array Type="Button"> builds a Button[] from its object item children
    public void XArray_ObjectItems_BuildsTypedArray()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}>" +
            "<x:Array x:Key=\"btns\" Type=\"Button\"><Button Width=\"3\"/><Button Width=\"5\"/></x:Array>" +
            "</ResourceDictionary>");

        var array = Assert.IsType<UIControls.Button[]>(dict["btns"]);
        Assert.Equal(2, array.Length);
        Assert.Equal(3, array[0].Width);
        Assert.Equal(5, array[1].Width);
    }

    [Fact] // XA2 — <x:Array Type="x:String"> with <x:String> items builds a string[] (element-valued primitives)
    public void XArray_StringItems_BuildsStringArray()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}>" +
            "<x:Array x:Key=\"names\" Type=\"x:String\"><x:String>Alice</x:String><x:String>Bob</x:String></x:Array>" +
            "</ResourceDictionary>");

        var array = Assert.IsType<string[]>(dict["names"]);
        Assert.Equal(["Alice", "Bob"], array);
    }

    [Fact] // XA3 — <x:Array Type="x:Int32"> with <x:Int32> items builds an int[] (text converted per element)
    public void XArray_Int32Items_BuildsIntArray()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}>" +
            "<x:Array x:Key=\"nums\" Type=\"x:Int32\"><x:Int32>7</x:Int32><x:Int32>42</x:Int32></x:Array>" +
            "</ResourceDictionary>");

        var array = Assert.IsType<int[]>(dict["nums"]);
        Assert.Equal([7, 42], array);
    }

    // ── Curly {x:Array Type=T, item, …} — the extension twin, building the SAME IsArray node ──────

    [Fact] // XA-C1 — curly {x:Array Type=x:Int32, {x:Int32 …}} builds the SAME int[] as the element form (XA3)
    public void CurlyXArray_Int32PrimitiveItems_BuildsIntArray()
    {
        var button = Load<UIControls.Button>(
            "<Button Content=\"{x:Array Type=x:Int32, {x:Int32 7}, {x:Int32 42}}\"/>");
        Assert.Equal([7, 42], Assert.IsType<int[]>(button.Content));
    }

    [Fact] // XA-C2 — curly with {x:String …} items builds a string[] (XA2 twin)
    public void CurlyXArray_StringPrimitiveItems_BuildsStringArray()
    {
        var button = Load<UIControls.Button>(
            "<Button Content=\"{x:Array Type=x:String, {x:String Alice}, {x:String Bob}}\"/>");
        Assert.Equal(["Alice", "Bob"], Assert.IsType<string[]>(button.Content));
    }

    [Fact] // XA-C3 — BARE value items are converted to T (the curly analog of <T>value</T>)
    public void CurlyXArray_BareValueItems_ConvertToElementType()
    {
        var button = Load<UIControls.Button>(
            "<Button Content=\"{x:Array Type=x:Int32, 7, 42}\"/>");
        Assert.Equal([7, 42], Assert.IsType<int[]>(button.Content));
    }

    [Fact] // XA-C4 — an empty curly array builds a zero-length T[] (XA4 twin)
    public void CurlyXArray_Empty_BuildsEmptyArray()
    {
        var button = Load<UIControls.Button>("<Button Content=\"{x:Array Type=x:String}\"/>");
        Assert.Empty(Assert.IsType<string[]>(button.Content));
    }

    [Fact] // XA-C5 — a curly array with no Type is CUR1204 (XA5 twin)
    public void CurlyXArray_MissingType_Throws()
    {
        var ex = Assert.Throws<XamlParseException>(() => Load<UIControls.Button>(
            "<Button Content=\"{x:Array {x:Int32 1}}\"/>"));
        Assert.Equal("CUR1204", ex.Code);
    }

    [Fact] // XA-C6 — an {x:Null} item is NOT dropped (review finding): {x:Array Type=Button, {x:Null}} builds a
    // length-1 Button[]{ null }, exactly as the element form <x:Array Type="Button"><x:Null/></x:Array> does.
    public void CurlyXArray_XNullItem_IsKept_ParityWithElementForm()
    {
        var curly = Load<UIControls.Button>("<Button Content=\"{x:Array Type=Button, {x:Null}}\"/>");
        var curlyArr = Assert.IsType<UIControls.Button[]>(curly.Content);
        Assert.Single(curlyArr);
        Assert.Null(curlyArr[0]);

        var element = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}><x:Array x:Key=\"a\" Type=\"Button\"><x:Null/></x:Array></ResourceDictionary>");
        Assert.Equal(curlyArr.Length, Assert.IsType<UIControls.Button[]>(element["a"]).Length); // same length — no drop
    }

    [Fact] // XA-C7 — a mix of a primitive and an {x:Null} item keeps BOTH (length stays correct, no silent shortening)
    public void CurlyXArray_MixedPrimitiveAndNull_KeepsAll()
    {
        var button = Load<UIControls.Button>("<Button Content=\"{x:Array Type=x:String, {x:String hi}, {x:Null}}\"/>");
        var arr = Assert.IsType<string[]>(button.Content);
        Assert.Equal(2, arr.Length);
        Assert.Equal("hi", arr[0]);
        Assert.Null(arr[1]);
    }

    [Fact] // XA4 — an empty <x:Array Type="x:String"/> builds a zero-length string[]
    public void XArray_Empty_BuildsEmptyArray()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}><x:Array x:Key=\"empty\" Type=\"x:String\"/></ResourceDictionary>");

        var array = Assert.IsType<string[]>(dict["empty"]);
        Assert.Empty(array);
    }

    [Fact] // XA5 — <x:Array> with no Type attribute is CUR1204
    public void XArray_MissingType_Throws()
    {
        var ex = Assert.Throws<XamlParseException>(() => LoadRaw(
            $"<ResourceDictionary{Pre}><x:Array x:Key=\"x\"><Button/></x:Array></ResourceDictionary>"));
        Assert.Equal("CUR1204", ex.Code);
    }

    [Fact] // XA6 — a standalone built-in primitive element (a resource) initializes from its content text
    public void BuiltInPrimitiveElement_InitializesFromText()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}>" +
            "<x:Double x:Key=\"Pi\">3.5</x:Double>" +
            "<x:Boolean x:Key=\"On\">true</x:Boolean>" +
            "<x:String x:Key=\"Hi\">hello</x:String>" +
            "</ResourceDictionary>");

        Assert.Equal(3.5, Assert.IsType<double>(dict["Pi"]));
        Assert.True(Assert.IsType<bool>(dict["On"]));
        Assert.Equal("hello", Assert.IsType<string>(dict["Hi"]));
    }

    [Fact] // XA7 — an x:Array assigned to an IEnumerable member (ItemsControl.ItemsSource) via a property element
    public void XArray_AssignedToEnumerableMember()
    {
        var list = Load<UIControls.ListBox>(
            "<ListBox><ListBox.ItemsSource>" +
            "<x:Array Type=\"x:String\"><x:String>one</x:String><x:String>two</x:String></x:Array>" +
            "</ListBox.ItemsSource></ListBox>");

        var array = Assert.IsType<string[]>(list.ItemsSource);
        Assert.Equal(["one", "two"], array);
    }

    [Fact] // XA8 — a primitive item not assignable to the array element type is CUR2401 (positioned). The dict
           // entry realizes lazily, so the mismatch surfaces on lookup (where the array is actually built).
    public void XArray_MismatchedItem_Throws()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}>" +
            "<x:Array x:Key=\"x\" Type=\"x:Int32\"><x:String>nope</x:String></x:Array>" +
            "</ResourceDictionary>");

        var ex = Assert.Throws<XamlParseException>(() => _ = dict["x"]);
        Assert.Equal("CUR2401", ex.Code);
    }

    // ── Audit regressions (the adversarial pass found these; the green happy-path tests missed them) ──

    private static XamlDocument ParseCollectAll(string xaml) => XamlFrontend.Parse(xaml,
        new XamlParseOptions { MetadataProvider = ReflectionXamlMetadata.Instance, DiagnosticMode = XamlDiagnosticMode.CollectAll });

    [Fact] // AUDIT: a MID-LIST property element reports CUR2102 but must NOT drop the following sibling item
           // (the Skip()+continue double-advance bug). CollectAll, since the loader throws on the first error.
    public void XArray_MidListPropertyElement_DoesNotDropFollowingSibling()
    {
        var doc = ParseCollectAll(
            $"<x:Array{Pre} Type=\"Button\"><Foo.Bar>x</Foo.Bar><Button Width=\"3\"/><Button Width=\"5\"/><Button Width=\"7\"/></x:Array>");

        Assert.Contains(doc.Diagnostics, d => d.Code == "CUR2102");     // the property element was reported …
        Assert.Equal(4, doc.ObjectCount());                            // … and all 3 Buttons survive (array + 3)
        var items = System.Array.Find(doc.MembersOf(0), m => m.Kind == XamlValueKind.Items);
        Assert.Equal(3, items.ItemCount);
    }

    [Fact] // AUDIT (critical): a TRAILING property element must not overshoot </x:Array> and swallow the array's
           // following siblings into it (the parent's content count + the array's item count must be preserved).
    public void XArray_TrailingPropertyElement_DoesNotSwallowParentSiblings()
    {
        var doc = ParseCollectAll(
            $"<StackPanel{Pre}><x:Array Type=\"Button\"><Foo.Bar>x</Foo.Bar></x:Array><Button Width=\"99\"/></StackPanel>");

        Assert.Contains(doc.Diagnostics, d => d.Code == "CUR2102");
        // StackPanel.Children = { the x:Array, the trailing Button } — the Button is NOT re-parented into the array.
        Assert.True(doc.TryFindMember(0, "Children", out var children));
        Assert.Equal(2, children.ItemCount);
        // The array (object index 1) has zero items (its only child was the rejected property element).
        Assert.DoesNotContain(doc.MembersOf(1), m => m.Kind == XamlValueKind.Items);
    }

    [Fact] // AUDIT: <x:TimeSpan> is a built-in but had no converter — it must now convert to a real TimeSpan
           // (loader returned a raw string before; generator threw InvalidCastException).
    public void BuiltInTimeSpan_ConvertsToTimeSpan()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}>" +
            "<x:TimeSpan x:Key=\"t\">00:00:05</x:TimeSpan>" +
            "<x:Array x:Key=\"spans\" Type=\"x:TimeSpan\"><x:TimeSpan>00:00:01</x:TimeSpan></x:Array>" +
            "</ResourceDictionary>");

        Assert.Equal(System.TimeSpan.FromSeconds(5), Assert.IsType<System.TimeSpan>(dict["t"]));
        var spans = Assert.IsType<System.TimeSpan[]>(dict["spans"]);
        Assert.Equal([System.TimeSpan.FromSeconds(1)], spans);
    }

    [Fact] // AUDIT: an x:Array of a WIDER numeric element type accepts narrower items via Array.SetValue widening
           // (an x:Int32 item into an x:Int64 array) — the strict IsInstanceOfType pre-check rejected this.
    public void XArray_NumericWidening_IsAccepted()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}>" +
            "<x:Array x:Key=\"longs\" Type=\"x:Int64\"><x:Int32>5</x:Int32><x:Int64>9</x:Int64></x:Array>" +
            "</ResourceDictionary>");

        var longs = Assert.IsType<long[]>(dict["longs"]);
        Assert.Equal([5L, 9L], longs);
    }
}
