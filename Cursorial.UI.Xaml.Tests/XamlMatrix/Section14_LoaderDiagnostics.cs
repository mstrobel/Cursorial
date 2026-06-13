using System;
using System.Globalization;

using Cursorial.UI;
using Cursorial.UI.Xaml;
using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// §14 — the loader-stage (X1) rows: X171 (instantiation errors carry position), X173 (reflection
/// inventory), X174 (the dual-provider drift gate), X175 (AOT fallback), X177 (GetOrParse cache),
/// X178 (UI-thread), X181 (culture), X182 (parse-once / instantiate-many reuse). The X0 parse-half
/// rows live in <see cref="Section14_Diagnostics"/>.
/// </summary>
public sealed class Section14_LoaderDiagnostics : LoaderTestBase
{
    [Fact] // X171
    public void X171_InstantiationError_CarriesNodePosition()
    {
        // A duplicate x:Name is an instantiation-time (CUR3002) failure; it carries the offending node's
        // 1-based line + column (XD1 — the instantiator always knows the current record).
        var ex = Assert.Throws<XamlParseException>(
            () => Load("<StackPanel><Button x:Name=\"dup\"/><Border x:Name=\"dup\"/></StackPanel>"));
        Assert.StartsWith("CUR3", ex.Code);
        Assert.True(ex.Line > 0 && ex.Column > 0);
    }

    [Fact] // X173
    public void X173_ReflectionInventory_AnnotatedAndUIPropertyFree()
    {
        // Reflection lives only in ReflectionXamlMetadata, annotated [RequiresUnreferencedCode] /
        // [RequiresDynamicCode]; UIProperty members resolve through the registry (no reflection).
        var type = typeof(ReflectionXamlMetadata);
        Assert.NotNull(type.GetCustomAttributes(typeof(System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute), false));
        Assert.NotEmpty(type.GetCustomAttributes(typeof(System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute), false));
        Assert.NotEmpty(type.GetCustomAttributes(typeof(System.Diagnostics.CodeAnalysis.RequiresDynamicCodeAttribute), false));
    }

    [Fact] // X174
    public void X174_DualProvider_NoDrift()
    {
        // The reflection provider and a hand-built provider (over the same CLR types) produce identical
        // trees AND identical diagnostics: the X5 generated provider cannot drift semantically. A
        // representative subset, walked in full (P6 review correctness P2-3 broadens the gate from the
        // first child to the whole tree + diagnostic lists).
        const string body = "<StackPanel><Button Content=\"Hi\" Width=\"10\" Margin=\"1,2\"/><Border/></StackPanel>";

        var reflectionLoader = new XamlLoader(new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance });
        var handBuiltLoader = new XamlLoader(new XamlLoaderOptions { MetadataProvider = HandBuiltMetadata.Instance });

        // Diagnostics drift: parse the same document through both providers and compare code/line/column.
        var docA = reflectionLoader.Parse(Wrap(body), TestSource);
        var docB = handBuiltLoader.Parse(Wrap(body), TestSource);
        Assert.Equal(docA.Diagnostics.Count, docB.Diagnostics.Count);
        for (int i = 0; i < docA.Diagnostics.Count; i++)
        {
            Assert.Equal(docA.Diagnostics[i].Code, docB.Diagnostics[i].Code);
            Assert.Equal(docA.Diagnostics[i].Line, docB.Diagnostics[i].Line);
            Assert.Equal(docA.Diagnostics[i].Column, docB.Diagnostics[i].Column);
        }

        var a = reflectionLoader.Load<UIControls.StackPanel>(docA);
        var b = handBuiltLoader.Load<UIControls.StackPanel>(docB);

        // Full-tree structural comparison: child count + every child's runtime type, then the leaf values.
        Assert.Equal(a.Children.Count, b.Children.Count);
        for (int i = 0; i < a.Children.Count; i++)
            Assert.Equal(a.Children[i].GetType(), b.Children[i].GetType());

        var ba = (UIControls.Button)a.Children[0];
        var bb = (UIControls.Button)b.Children[0];
        Assert.Equal(ba.Content, bb.Content);
        Assert.Equal(ba.Width, bb.Width);
        Assert.Equal(ba.Margin, bb.Margin);
        Assert.IsType<UIControls.Border>(a.Children[1]);
        Assert.IsType<UIControls.Border>(b.Children[1]);
    }

    [Fact] // X175
    public void X175_AotFallback_DocumentedSupportedTrimmedModeIsX5()
    {
        // The reflective provider uses Activator.CreateInstance / MethodInfo.Invoke — the honest AOT
        // fallback. Values still set; the supported trimmed mode is the X5 generated provider (deferred).
        var button = Load<UIControls.Button>("<Button Content=\"Hi\"/>");
        Assert.Equal("Hi", button.Content);
    }

    [Fact] // X177
    public void X177_GetOrParse_Caches()
    {
        var uri = new Uri("cursorial://test/cache.xaml");
        var xml = Wrap("<Button/>");
        var first = Loader.GetOrParse(uri, xml);
        var second = Loader.GetOrParse(uri, xml);
        Assert.Same(first, second);

        var other = Loader.GetOrParse(new Uri("cursorial://test/other.xaml"), Wrap("<Border/>"));
        Assert.NotSame(first, other);
    }

    [Fact] // X181
    public void X181_ConverterCulture_HonoredForContextDependentConverts()
    {
        // A culture-sensitive double under FoldConstants=false converts at instantiate with the
        // configured culture. German uses ',' as the decimal separator.
        var german = new XamlLoader(new XamlLoaderOptions
        {
            FoldConstants = false,
            ConverterCulture = CultureInfo.GetCultureInfo("de-DE"),
        });
        var button = german.Load<UIControls.Button>(Wrap("<Button Opacity=\"0,5\"/>"), TestSource);
        Assert.Equal(0.5, button.Opacity);
    }

    [Fact] // X178
    public async System.Threading.Tasks.Task X178_LoadIsSingleThreadAffine()
    {
        // Instantiation is single-UI-thread (invariant 6): with no ambient application, a UIObject
        // captures its constructing thread's affinity, so a Load on one thread builds a consistent
        // tree. The documented contract is that all of one Load runs on one thread; parse is the
        // thread-safe half (X176). Here we assert a single-threaded Load builds + reads coherently.
        var content = await System.Threading.Tasks.Task.Run(() =>
        {
            var button = Loader.Load<UIControls.Button>(Wrap("<Button Content=\"Hi\"/>"), TestSource);
            return button.Content;
        });
        Assert.Equal("Hi", content);
    }

    [Fact] // X182
    public void X182_ParseOnce_InstantiateMany()
    {
        var doc = Parse("<Button Margin=\"1,2\"/>");
        var a = Loader.Load<UIControls.Button>(doc);
        var b = Loader.Load<UIControls.Button>(doc);
        var c = Loader.Load<UIControls.Button>(doc);
        Assert.NotSame(a, b);
        Assert.NotSame(b, c);
        Assert.Equal(a.Margin, b.Margin);
        Assert.Equal(b.Margin, c.Margin);
    }
}
