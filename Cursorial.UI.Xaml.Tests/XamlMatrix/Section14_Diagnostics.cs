using System.Text;

using Cursorial.UI.Xaml;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// §14 — Diagnostics, threading, parse-cost (the X0 parse-half rows: X169, X170, X172, X176, X180,
/// X182). The loader-stage rows (X171 instantiation position, X173 reflection inventory, X174
/// dual-provider, X175 AOT fallback, X177 GetOrParse cache, X178 UI-thread, X181 culture) land at
/// X1/X2 and are absent (not red) until then per the stage map.
/// </summary>
public sealed class Section14_Diagnostics : XamlTestBase
{
    [Fact] // X169
    public void X169_ThrowOnFirstError_ThrowsOnFirst()
    {
        // Two errors: an unknown type and an unknown intrinsic. ThrowOnFirstError throws on the first.
        var ex = Assert.Throws<XamlParseException>(() =>
            ParseRaw("<Bogus xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" x:Zonk=\"a\"/>"));
        Assert.True(ex.Diagnostics.Count >= 1);
        Assert.Equal(ex.Code, ex.Diagnostics[0].Code);
        Assert.True(ex.Line > 0 && ex.Column > 0);
    }

    [Fact] // X170
    public void X170_CollectAll_CollectsBoth()
    {
        // CollectAll never throws; both errors are recorded with their own code + line + col.
        var doc = ParseRaw(
            "<Button xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" " +
            "x:Zonk=\"a\" x:TypeArguments=\"b\"/>",
            mode: XamlDiagnosticMode.CollectAll);
        Assert.True(doc.Diagnostics.Count >= 2);
        Assert.Contains(doc.Diagnostics, d => d.Code == XamlDiagnosticCodes.UnknownIntrinsic);
        Assert.Contains(doc.Diagnostics, d => d.Code == XamlDiagnosticCodes.UnsupportedTypeArguments);
        foreach (var d in doc.Diagnostics)
        {
            Assert.True(d.Line > 0, "every diagnostic carries a 1-based line");
            Assert.True(d.Column > 0, "every diagnostic carries a 1-based column");
        }
    }

    [Fact] // X172
    public void X172_DiagnosticBanding_ParseVsResolve()
    {
        // A parse-grammar error is CUR1xxx; a resolution error is CUR2xxx.
        var parseEx = Assert.Throws<XamlParseException>(() => Parse("<Button x:Bogus=\"y\"/>"));
        Assert.StartsWith("CUR1", parseEx.Code);

        var resolveEx = Assert.Throws<XamlParseException>(() => Parse("<Nope/>"));
        Assert.StartsWith("CUR2", resolveEx.Code);
    }

    [Fact] // X176
    public async Task X176_ParseIsThreadSafe()
    {
        const string body = "<StackPanel><Button Content=\"A\"/><Border/></StackPanel>";
        var t1 = Task.Run(() => Parse(body));
        var t2 = Task.Run(() => Parse(body));
        var d1 = await t1;
        var d2 = await t2;
        Assert.Equal(d1.ObjectCount(), d2.ObjectCount());
        Assert.Equal(3, d1.ObjectCount());
        // distinct immutable documents, no shared mutable state
        Assert.NotSame(d1, d2);
    }

    [Fact, Trait("Category", "Benchmark")] // X180
    public void X180_FiftyElementDocument_ParsesQuickly()
    {
        var sb = new StringBuilder();
        sb.Append("<StackPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\">");
        for (int i = 0; i < 50; i++)
            sb.Append("<Button Content=\"item\" Width=\"10\" Margin=\"1,2\"/>");
        sb.Append("</StackPanel>");
        string xml = sb.ToString();

        // warm up
        _ = ParseRaw(xml);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var doc = ParseRaw(xml);
        sw.Stop();
        Assert.Equal(51, doc.ObjectCount());
        // single-digit-ms parse (generous CI ceiling)
        Assert.True(sw.ElapsedMilliseconds < 100, $"parse took {sw.ElapsedMilliseconds} ms");
    }

    [Fact] // X182
    public void X182_ImmutableArtifact_ParseOnce()
    {
        // Parsing the same body twice yields two distinct immutable documents (the loader reuses one
        // parse across many Loads at X1; here we assert parse purity + folded-constant presence).
        var doc = Parse("<Button Margin=\"1,2\"/>");
        Assert.True(doc.TryFindMember(0, "Margin", out var m));
        Assert.Equal(XamlValueKind.Folded, m.Kind);
        // the folded constant lives in the shared Constants array (one box per document)
        Assert.Single(doc.Constants);
    }
}
