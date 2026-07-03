using Cursorial.UI.Xaml;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// §4 — Markup-extension grammar (X41–X58). The hand-rolled recursive-descent grammar + the {}
/// escape (the System.Xaml-pinned leg) + the fuzz gate. Frontend.
/// </summary>
public sealed class Section04_MarkupExtensions : XamlTestBase
{
    // Extension parsing is exercised through a Content="{…}" member on a Button. The probe reads the
    // parsed node (a Binding/StaticResource/custom node).
    private static MarkupExtensionNode ParseExt(string extension)
    {
        var doc = Parse($"<Button Content=\"{extension}\"/>");
        Assert.True(doc.TryFindMember(0, "Content", out var m));
        Assert.Equal(XamlValueKind.Extension, m.Kind);
        ref readonly var ext = ref doc.ExtensionOf(m);
        var node = doc.ParsedExtension(in ext);
        Assert.NotNull(node);
        return node;
    }

    [Fact] // X41
    public void X041_Binding_PositionalPath()
    {
        var node = ParseExt("{Binding Name}");
        Assert.Equal("Binding", node.Name);
        Assert.Single(node.PositionalArguments);
        Assert.Equal("Name", node.PositionalArguments[0].Text);
    }

    [Fact] // X42
    public void X042_Binding_NamedArgs_OrderIndependent()
    {
        var node = ParseExt("{Binding Path=Name, Mode=TwoWay}");
        Assert.Equal("Name", node.FindNamed("Path")!.Value.Text);
        Assert.Equal("TwoWay", node.FindNamed("Mode")!.Value.Text);
    }

    [Fact] // X43
    public void X043_Binding_MixedPositionalAndNamed()
    {
        var node = ParseExt("{Binding Name, Mode=TwoWay}");
        Assert.Single(node.PositionalArguments);
        Assert.Equal("Name", node.PositionalArguments[0].Text);
        Assert.Equal("TwoWay", node.FindNamed("Mode")!.Value.Text);
    }

    [Fact] // X44
    public void X044_StaticResource_KeyOnExtensionRecord()
    {
        var doc = Parse("<Button Content=\"{StaticResource AccentBrush}\"/>");
        Assert.True(doc.TryFindMember(0, "Content", out var m));
        Assert.Equal(XamlValueKind.Extension, m.Kind);
        ref readonly var ext = ref doc.ExtensionOf(m);
        Assert.Equal(ExtensionKind.StaticResource, ext.Kind);
        Assert.False(ext.PayloadIsParsedExtension); // literal key ⇒ Payload indexes Strings (XD7a guard)
        Assert.Equal("AccentBrush", doc.Strings[ext.Payload]);
    }

    [Fact] // X45
    public void X045_NestedExtension_ConverterIsExtension()
    {
        var node = ParseExt("{Binding Status, Converter={StaticResource StatusToBrush}}");
        var converter = node.FindNamed("Converter")!.Value;
        Assert.True(converter.IsNested);
        Assert.Equal("StaticResource", converter.Nested!.Name);
        Assert.Equal("StatusToBrush", converter.Nested!.PositionalArguments[0].Text);
    }

    [Fact] // X44a — a StaticResource KEY that is itself an {x:Static} (XD7a)
    public void X044a_StaticResource_NestedStaticKey_RecordsParsedKeyNode()
    {
        var doc = Parse("<Button Content=\"{StaticResource {x:Static ThemeKeys.SurfaceBrush}}\"/>");
        Assert.True(doc.TryFindMember(0, "Content", out var m));
        Assert.Equal(XamlValueKind.Extension, m.Kind);
        ref readonly var ext = ref doc.ExtensionOf(m);
        Assert.Equal(ExtensionKind.StaticResource, ext.Kind);
        Assert.True(ext.PayloadIsParsedExtension);          // nested key ⇒ Payload indexes ParsedExtensions
        var keyNode = doc.ParsedExtension(in ext);          // the INNER key node, not the outer *Resource
        Assert.NotNull(keyNode);
        Assert.Equal("x:Static", keyNode.Name);
        Assert.Equal("ThemeKeys.SurfaceBrush", keyNode.PositionalArguments[0].Text);
    }

    [Fact] // X57a — the DynamicResource analog
    public void X057a_DynamicResource_NestedStaticKey_RecordsParsedKeyNode()
    {
        var doc = Parse("<Button Content=\"{DynamicResource {x:Static ThemeKeys.SurfaceBrush}}\"/>");
        Assert.True(doc.TryFindMember(0, "Content", out var m));
        ref readonly var ext = ref doc.ExtensionOf(m);
        Assert.Equal(ExtensionKind.DynamicResource, ext.Kind);
        Assert.True(ext.PayloadIsParsedExtension);
        Assert.Equal("x:Static", doc.ParsedExtension(in ext)!.Name);
    }

    [Fact] // X44a generality — the nested key is any markup extension, not x:Static-only
    public void X044a_StaticResource_NestedResourceKey_NotStaticOnly()
    {
        var doc = Parse("<Button Content=\"{StaticResource {StaticResource KeyHolder}}\"/>");
        Assert.True(doc.TryFindMember(0, "Content", out var m));
        ref readonly var ext = ref doc.ExtensionOf(m);
        Assert.True(ext.PayloadIsParsedExtension);
        Assert.Equal("StaticResource", doc.ParsedExtension(in ext)!.Name);
    }

    [Fact, Trait("Oracle", "SystemXaml")] // X46
    public void X046_LiteralEscape_NotAnExtension()
    {
        var doc = Parse("<Button Content=\"{}{not an extension}\"/>");
        Assert.True(doc.TryFindMember(0, "Content", out var m));
        // a literal — Text (Content is object-typed, no fold), value verbatim minus the {} prefix
        Assert.NotEqual(XamlValueKind.Extension, m.Kind);
        Assert.Equal("{not an extension}", doc.StringValue(m));
    }

    [Fact, Trait("Oracle", "SystemXaml")] // X47
    public void X047_QuotedArg_ProtectsComma()
    {
        var node = ParseExt("{Binding Path='A,B'}");
        Assert.Equal("A,B", node.FindNamed("Path")!.Value.Text);
    }

    [Fact, Trait("Oracle", "SystemXaml")] // X48
    public void X048_EscapedBraceInsideQuotes()
    {
        var node = ParseExt(@"{Binding Path='a\}b'}");
        Assert.Equal("a}b", node.FindNamed("Path")!.Value.Text);
    }

    [Fact, Trait("Oracle", "SystemXaml")] // X49
    public void X049_EscapedBackslash()
    {
        var node = ParseExt(@"{Binding Path='a\\b'}");
        Assert.Equal(@"a\b", node.FindNamed("Path")!.Value.Text);
    }

    [Fact, Trait("Oracle", "SystemXaml")] // X50
    public void X050_BareValue_TrailingSpacesTrimmed()
    {
        var node = ParseExt("{Binding Path=Name }");
        Assert.Equal("Name", node.FindNamed("Path")!.Value.Text);
    }

    [Fact] // X51
    public void X051_EmptyExtensionBody_Valid()
    {
        var node = ParseExt("{Binding}");
        Assert.Equal("Binding", node.Name);
        Assert.Empty(node.PositionalArguments);
        Assert.Empty(node.NamedArguments);
    }

    [Fact] // X52
    public void X052_UnterminatedExtension_CUR1301()
    {
        Throws(XamlDiagnosticCodes.UnterminatedExtension, () => Parse("<Button Content=\"{Binding Name\"/>"));
    }

    [Fact] // X53
    public void X053_UnknownExtensionName_ResolvedAsCustom_CUR2002()
    {
        // {Bogus X} resolves as a custom extension type; the unresolvable type is CUR2002 (did-you-mean).
        Throws(XamlDiagnosticCodes.TypeNotFound, () => Parse("<Button Content=\"{Bogus X}\"/>"));
    }

    [Fact] // X54
    public void X054_MalformedNamedArg_CUR1302()
    {
        Throws(XamlDiagnosticCodes.MalformedExtensionArgument, () => Parse("<Button Content=\"{Binding =Name}\"/>"));
    }

    [Theory] // X55
    [InlineData("{x:Null}")]
    [InlineData("{x:Type Button}")]
    [InlineData("{x:Static Colors.Red}")]
    public void X055_IntrinsicExtensions_FoldToConstants(string extension)
    {
        var doc = Parse($"<Button Content=\"{extension}\"/>");
        Assert.True(doc.TryFindMember(0, "Content", out var m));
        Assert.Equal(XamlValueKind.Folded, m.Kind); // never a live ExtensionRecord
    }

    [Fact] // X56
    public void X056_TemplateBindingOutsideTemplate_CUR2202()
    {
        Throws(XamlDiagnosticCodes.TemplateBindingOutsideTemplate,
            () => Parse("<Button Content=\"{TemplateBinding Background}\"/>"));
    }

    [Fact] // X57
    public void X057_DynamicResource_NotFolded()
    {
        var doc = Parse("<Button Content=\"{DynamicResource AccentBrush}\"/>");
        Assert.True(doc.TryFindMember(0, "Content", out var m));
        Assert.Equal(XamlValueKind.Extension, m.Kind);
        ref readonly var ext = ref doc.ExtensionOf(m);
        Assert.Equal(ExtensionKind.DynamicResource, ext.Kind);
        Assert.False(ext.PayloadIsParsedExtension); // literal key ⇒ Payload indexes Strings (XD7a guard)
        Assert.Equal("AccentBrush", doc.Strings[ext.Payload]);
    }

    [Theory] // X58 — the fuzz gate: no crash/hang/out-of-range, always a CUR13xx (or a clean parse).
    [InlineData("{")]
    [InlineData("{Binding")]
    [InlineData("{Binding ,}")]
    [InlineData("{Binding =}")]
    [InlineData("{Binding Path=}")]
    [InlineData("{{{{")]
    [InlineData("{Binding Path='unterminated}")]
    [InlineData("{Binding Path=a\\}")]
    [InlineData("{ , , , }")]
    [InlineData("{Binding Name, , Mode=}")]
    [InlineData("{x:Static }")]
    [InlineData("{Binding Path={Binding Path={Binding}}}")]
    public void X058_FuzzedExtensions_NeverCrash(string extension)
    {
        // Either a clean parse or a CUR13xx (grammar) / CUR2xxx (resolution) — never an unexpected
        // exception, hang, or out-of-range. We assert no non-XamlParseException escapes.
        try
        {
            var doc = Parse($"<Button Content=\"{extension.Replace("\"", "&quot;")}\"/>");
            // a clean parse is acceptable for the lenient cases
            Assert.NotNull(doc);
        }
        catch (XamlParseException ex)
        {
            Assert.StartsWith("CUR", ex.Code);
            Assert.True(ex.Line > 0 && ex.Column > 0);
        }
    }
}
