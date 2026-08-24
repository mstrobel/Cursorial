using Cursorial.UI.Xaml;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// The W3 <c>x:TypeArguments</c> type-name grammar (<c>XamlTypeName.TryParseList</c>): the XAML 2009 /
/// System.Xaml-compatible core (XG1–XG6 — comma lists, parenthesized nesting, prefixes; oracle-pin
/// candidates for the Windows CI leg) and the separately-marked Cursorial extensions (XG7–XG8 — the
/// <c>[]</c> array and <c>?</c> nullable suffixes, which System.Xaml does NOT accept). Error rows XG9+
/// pin positioned failures — never a silent partial parse.
/// </summary>
public sealed class Section25_TypeNameGrammar
{
    private static IReadOnlyList<XamlTypeName> Parse(string text)
    {
        Assert.True(XamlTypeName.TryParseList(text, out var names, out var error, out _), error);
        return names;
    }

    [Fact] // XG1 (2009 core): a single prefixed name
    public void XG1_SinglePrefixedName()
    {
        var name = Assert.Single(Parse("x:Double"));
        Assert.Equal("x", name.Prefix);
        Assert.Equal("Double", name.Name);
        Assert.Empty(name.TypeArguments);
        Assert.False(name.IsArray);
        Assert.False(name.IsNullable);
    }

    [Fact] // XG2 (2009 core): an unprefixed name binds the in-scope default namespace
    public void XG2_UnprefixedName()
    {
        var name = Assert.Single(Parse("Border"));
        Assert.Null(name.Prefix);
        Assert.Equal("Border", name.Name);
    }

    [Fact] // XG3 (2009 core): a comma list with whitespace tolerance
    public void XG3_CommaList()
    {
        var names = Parse(" x:String , x:Int32 ");
        Assert.Equal(2, names.Count);
        Assert.Equal("String", names[0].Name);
        Assert.Equal("Int32", names[1].Name);
    }

    [Fact] // XG4 (2009 core): parenthesized nesting — scg:List(x:String)
    public void XG4_ParenthesizedNesting()
    {
        var name = Assert.Single(Parse("scg:List(x:String)"));
        Assert.Equal("scg", name.Prefix);
        Assert.Equal("List", name.Name);
        var inner = Assert.Single(name.TypeArguments);
        Assert.Equal("x", inner.Prefix);
        Assert.Equal("String", inner.Name);
    }

    [Fact] // XG5 (2009 core): multi-argument nesting — scg:Dictionary(x:String, x:Int32)
    public void XG5_MultiArgumentNesting()
    {
        var name = Assert.Single(Parse("scg:Dictionary(x:String, x:Int32)"));
        Assert.Equal(2, name.TypeArguments.Count);
        Assert.Equal("String", name.TypeArguments[0].Name);
        Assert.Equal("Int32", name.TypeArguments[1].Name);
    }

    [Fact] // XG6 (2009 core): DEEP nesting — scg:List(scg:List(x:Double))
    public void XG6_DeepNesting()
    {
        var name = Assert.Single(Parse("scg:List(scg:List(x:Double))"));
        var mid = Assert.Single(name.TypeArguments);
        Assert.Equal("List", mid.Name);
        Assert.Equal("Double", Assert.Single(mid.TypeArguments).Name);
    }

    [Fact] // XG7 (Cursorial extension): the [] array suffix — NOT System.Xaml grammar, never oracle-pinned
    public void XG7_ArraySuffix()
    {
        var name = Assert.Single(Parse("x:String[]"));
        Assert.True(name.IsArray);
        Assert.Equal("String", name.Name);
    }

    [Fact] // XG8 (Cursorial extension): the ? nullable suffix, composable with [] (x:Double?[] = double?[])
    public void XG8_NullableSuffix_ComposesWithArray()
    {
        var lone = Assert.Single(Parse("x:Double?"));
        Assert.True(lone.IsNullable);
        Assert.False(lone.IsArray);

        var both = Assert.Single(Parse("x:Double?[]"));
        Assert.True(both.IsNullable);
        Assert.True(both.IsArray);
    }

    [Theory] // XG9: malformed input fails with a POSITIONED message — never a silent partial parse
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("x:")]
    [InlineData(":Double")]
    [InlineData("a:b:c")]
    [InlineData("scg:List(x:String")]
    [InlineData("scg:List()")]
    [InlineData("x:String x:Int32")]
    [InlineData("x:String,,x:Int32")]
    public void XG9_Malformed_IsPositionedError(string text)
    {
        Assert.False(XamlTypeName.TryParseList(text, out _, out var error, out var offset));
        Assert.NotNull(error);
        Assert.InRange(offset, 0, Math.Max(0, text.Length));
    }

    [Fact] // XG10: ToString round-trips the canonical form (the diagnostic surface)
    public void XG10_ToString_Canonical()
    {
        var names = Parse("scg:Dictionary( x:String , scg:List(x:Int32) )");
        Assert.Equal("scg:Dictionary(x:String, scg:List(x:Int32))", names[0].ToString());
    }
}
