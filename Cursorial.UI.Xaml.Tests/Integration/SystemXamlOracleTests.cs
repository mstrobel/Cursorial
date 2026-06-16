using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

using Cursorial.Tests.UI.Xaml.XamlMatrix; // NodeGraphProbe: TryFindMember / StringValue over the node graph
using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.Integration;

/// <summary>
/// The doc §4.10 mandated <b>Windows-only System.Xaml oracle leg</b>: the corpus rows flagged
/// <c>oracle: System.Xaml</c> in the matrix (whitespace collapse — XD19/X31–X36 — and the markup-extension
/// <c>{}</c> brace-escape — XD ledger / X47) are pinned against <b>real System.Xaml</b>, the WPF-faithful
/// reference parser. This class parses each row through both System.Xaml (via reflection, so the test
/// project carries no Windows-only assembly reference and the cross-platform build stays clean) and
/// Cursorial's loader, then asserts the documented-shared text value agrees.
/// <para>
/// Off Windows — or anywhere <c>System.Xaml</c> is not loadable — every fact <b>skips</b> with a
/// documented reason (System.Xaml ships only with the Windows Desktop runtime; vendoring it was rejected
/// in doc §4's resolved decision). On a Windows CI agent the leg runs and enforces parity. The same rows
/// are additionally asserted against Cursorial's <em>documented</em> behavior unconditionally in
/// <c>XamlMatrix/Section03_Whitespace</c> + <c>Section04_MarkupExtensions</c> (the off-Windows leg), so a
/// regression is caught on every platform; this class is the extra "we match the oracle" guarantee.
/// </para>
/// </summary>
[Trait("Oracle", "SystemXaml")]
public sealed class SystemXamlOracleTests
{
    // The shared-semantics whitespace rows (matrix X32/X33/X34): element text trimmed at both ends, an
    // interior newline+indent run collapsed to one space, xml:space="preserve" verbatim. The expected text
    // is the documented XD19 value; the oracle leg additionally asserts System.Xaml produces the same.
    [WindowsTheory]
    [InlineData("  hello  ", "hello")]                       // X32 — trim both ends
    [InlineData("a\n   b", "a b")]                           // X33 — interior newline+indent collapses
    [InlineData("one   two", "one two")]                     // interior run of spaces collapses to one
    [InlineData("  lead\nmid\ntrail  ", "lead mid trail")]   // multi-line collapse + outer trim
    public void Whitespace_MatchesSystemXaml(string body, string expected)
    {
        // Cursorial's parse of the element-text content member.
        var cursorial = CursorialElementText(body);
        Assert.Equal(expected, cursorial);

        // System.Xaml's parse of the same content (the oracle); both agree on the documented value.
        var oracle = SystemXamlContent(body, preserveSpace: false);
        Assert.Equal(expected, oracle);
        Assert.Equal(cursorial, oracle);
    }

    [WindowsFact] // X34 — xml:space="preserve" is verbatim in both engines
    public void XmlSpacePreserve_MatchesSystemXaml()
    {
        const string body = "  a\n  b  ";
        var cursorial = CursorialElementText(body, preserveSpace: true);
        Assert.Equal(body, cursorial);

        var oracle = SystemXamlContent(body, preserveSpace: true);
        Assert.Equal(body, oracle);
        Assert.Equal(cursorial, oracle);
    }

    // The markup-extension {} brace-escape (matrix X47): a value beginning with the {} prefix is a LITERAL
    // (not an extension); the literal value is the rest verbatim minus the prefix. System.Xaml pins this.
    [WindowsTheory]
    [InlineData("{}{not an extension}", "{not an extension}")]
    [InlineData("{}plain", "plain")]
    [InlineData("{}{Binding Foo}", "{Binding Foo}")]
    public void BraceEscape_MatchesSystemXaml(string attributeValue, string expected)
    {
        var cursorial = CursorialAttributeText(attributeValue);
        Assert.Equal(expected, cursorial);

        var oracle = SystemXamlAttribute(attributeValue);
        Assert.Equal(expected, oracle);
        Assert.Equal(cursorial, oracle);
    }

    // ───────────────────────────── Cursorial parse helpers ─────────────────────────────

    private static string CursorialElementText(string body, bool preserveSpace = false)
    {
        var space = preserveSpace ? " xml:space=\"preserve\"" : "";
        var doc = XamlFrontend.Parse(
            $"<TextBlock xmlns=\"https://cursorial.dev/ui\"{space}>{body}</TextBlock>",
            new XamlParseOptions());
        AssertNoErrors(doc);
        Assert.True(doc.TryFindMember(0, "Text", out var member));
        return doc.StringValue(member);
    }

    private static string CursorialAttributeText(string attributeValue)
    {
        // The {} escape applies to attribute values that would otherwise begin an extension; Content is
        // object-typed (no access-key fold), so the literal is surfaced verbatim minus the {} prefix.
        var doc = XamlFrontend.Parse(
            $"<Button xmlns=\"https://cursorial.dev/ui\" Content=\"{attributeValue}\"/>",
            new XamlParseOptions());
        AssertNoErrors(doc);
        Assert.True(doc.TryFindMember(0, "Content", out var member));
        Assert.NotEqual(XamlValueKind.Extension, member.Kind);
        return doc.StringValue(member);
    }

    private static void AssertNoErrors(XamlDocument doc)
    {
        foreach (var d in doc.Diagnostics)
            Assert.NotEqual(XamlDiagnosticSeverity.Error, d.Severity);
    }

    // ───────────────────────────── System.Xaml oracle (reflection) ─────────────────────────────

    private const string OracleClr =
        "clr-namespace:Cursorial.Tests.UI.Xaml.Integration;assembly=Cursorial.UI.Xaml.Tests";

    private static string SystemXamlContent(string body, bool preserveSpace)
    {
        // Element text lands on the type's initialization string via its [TypeConverter]: System.Xaml
        // applies its (XD19-shared) whitespace collapse to element text BEFORE invoking the converter's
        // ConvertFrom(string), so the converted node's Content carries the oracle's collapsed value. This
        // is the cross-platform-portable path — [TypeConverter] is System.ComponentModel, never WPF — and
        // exercises exactly the whitespace semantics the leg pins. xml:space="preserve" suppresses collapse.
        var space = preserveSpace ? " xml:space=\"preserve\"" : "";
        var node = ParseWithSystemXaml($"<OracleNode xmlns=\"{OracleClr}\"{space}>{body}</OracleNode>");
        return ((OracleNode)node).Content ?? "";
    }

    private static string SystemXamlAttribute(string attributeValue)
    {
        // The {} brace-escape leg sets the Content member directly (member assignment needs no content
        // property — System.Xaml fills any public settable member by name), so the literal-vs-extension
        // disambiguation is what's under test, not the content-property mechanism.
        var node = ParseWithSystemXaml(
            $"<OracleNode xmlns=\"{OracleClr}\" Content=\"{attributeValue}\"/>");
        return ((OracleNode)node).Content ?? "";
    }

    /// <summary>
    /// Invokes <c>System.Xaml.XamlServices.Parse(string)</c> by reflection. Returns the activated root.
    /// </summary>
    private static object ParseWithSystemXaml(string xaml)
    {
        var services = LoadSystemXamlServicesType()
            ?? throw new InvalidOperationException("System.Xaml is not available on this platform.");
        var parse = services.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)])
            ?? throw new InvalidOperationException("XamlServices.Parse(string) was not found.");
        return parse.Invoke(null, [xaml])
            ?? throw new InvalidOperationException("XamlServices.Parse returned null.");
    }

    /// <summary>True when <c>System.Xaml</c> can be loaded (Windows Desktop runtime only).</summary>
    internal static bool SystemXamlAvailable => LoadSystemXamlServicesType() is not null;

    private static Type? LoadSystemXamlServicesType()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;
        try
        {
            var assembly = Assembly.Load("System.Xaml");
            return assembly.GetType("System.Xaml.XamlServices");
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// The whitespace/escape oracle node System.Xaml activates by reflection over its <c>clr-namespace</c>
/// xmlns. It is decorated with a <see cref="System.ComponentModel.TypeConverterAttribute"/> (a portable
/// <c>System.ComponentModel</c> type, not the Windows-only WPF <c>[ContentProperty]</c>) so System.Xaml
/// fills the node from <em>element text</em> through <see cref="OracleNodeConverter"/> — and applies its
/// XD19-shared whitespace collapse to that text first, which is precisely the semantics under test. The
/// <see cref="Content"/> attribute path needs no content property: System.Xaml sets any public member by
/// name, so the <c>{}</c> brace-escape leg works directly.
/// </summary>
[TypeConverter(typeof(OracleNodeConverter))]
public sealed class OracleNode
{
    /// <summary>The content text (System.Xaml fills this from element text via the converter, or from the
    /// <c>Content</c> attribute by member name).</summary>
    public string? Content { get; set; }
}

/// <summary>
/// The portable element-text converter for <see cref="OracleNode"/>: System.Xaml routes whitespace-collapsed
/// element text through <see cref="ConvertFrom(System.ComponentModel.ITypeDescriptorContext, CultureInfo, object)"/>,
/// yielding a node whose <see cref="OracleNode.Content"/> is the oracle's collapsed value.
/// </summary>
public sealed class OracleNodeConverter : TypeConverter
{
    /// <inheritdoc/>
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    /// <inheritdoc/>
    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        => value is string s ? new OracleNode { Content = s } : base.ConvertFrom(context, culture, value)!;
}

/// <summary>A <see cref="FactAttribute"/> that skips off Windows or when System.Xaml is unavailable.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!SystemXamlOracleTests.SystemXamlAvailable)
            Skip = "System.Xaml oracle leg runs only on Windows (System.Xaml is Windows-Desktop-only; vendoring rejected — doc §4).";
    }
}

/// <summary>A <see cref="TheoryAttribute"/> that skips off Windows or when System.Xaml is unavailable.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!SystemXamlOracleTests.SystemXamlAvailable)
            Skip = "System.Xaml oracle leg runs only on Windows (System.Xaml is Windows-Desktop-only; vendoring rejected — doc §4).";
    }
}
