using System.Linq;

using Cursorial.UI;
using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// §12 (X141–X146): deferred (lazy) resource entries + retry-safety. XD18 — SetDeferred; once-on-success;
/// a throwing Realize resets to Deferred; the lexical scope = the definition-site chain. Internal-probe
/// rows use the <c>EnumerateDeferredEntries</c>/<c>TryGetDeferredInfo</c> surface (matrix §16).
/// </summary>
public sealed class Section12_DeferredResourceEntries : LoaderTestBase
{
    private const string Pre = " xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    [Fact] // X141
    public void X141_KeyedEntries_LoadAsDeferred_NoInstantiation()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}>" +
            "<TestBrush x:Key=\"A\" Color=\"Red\"/>" +
            "<TestBrush x:Key=\"B\" Color=\"Blue\"/>" +
            "<TestBrush x:Key=\"C\" Color=\"Green\"/>" +
            "</ResourceDictionary>");
        // Count == 3 but none realized — the deferred-dictionary optimization (no instantiation at load).
        Assert.Equal(3, dict.Count);
        var deferred = dict.EnumerateDeferredEntries().ToList();
        Assert.Equal(3, deferred.Count);
        Assert.All(deferred, e => Assert.Equal(ResourceDictionary.DeferredState.Deferred, e.State));
    }

    [Fact] // X142
    public void X142_FirstLookup_RealizesOnce_CachesInPlace()
    {
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}><TestBrush x:Key=\"A\" Color=\"Red\"/></ResourceDictionary>");
        Assert.True(dict.TryGetDeferredInfo("A", out var before));
        Assert.Equal(ResourceDictionary.DeferredState.Deferred, before.State);

        var first = dict["A"];
        var second = dict["A"];
        Assert.Same(first, second); // realized once, cached in place (no re-realize)
        Assert.True(dict.TryGetDeferredInfo("A", out var after));
        Assert.Equal(ResourceDictionary.DeferredState.Realized, after.State); // realized, not re-realized
    }

    [Fact] // X143
    public void X143_LexicalScope_IsDefinitionSiteChain()
    {
        // A StaticResource INSIDE a deferred entry resolves against the defining dictionary (the
        // definition-site chain, XD18): entry "B" references sibling "A" by StaticResource and realizes.
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}>" +
            "<TestBrush x:Key=\"A\" Color=\"Red\"/>" +
            "<Border x:Key=\"B\" Background=\"{StaticResource A}\"/>" +
            "</ResourceDictionary>");
        var border = (Cursorial.UI.Controls.Border)dict["B"]!;
        Assert.IsType<TestBrush>(border.Background);
    }

    [Fact] // X144 / X145
    public void X144_ThrowingRealize_ResetsToDeferred_ThenRetrySucceeds()
    {
        // "B" references a missing key, so its Realize throws; the slot resets to Deferred (retry-safe).
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}>" +
            "<Border x:Key=\"B\" Background=\"{StaticResource Missing}\"/>" +
            "</ResourceDictionary>");

        Assert.ThrowsAny<System.Exception>(() => _ = dict["B"]);
        // The slot reset to Deferred (X144) — retry-safe, consumed no slice state.
        Assert.True(dict.TryGetDeferredInfo("B", out var info));
        Assert.Equal(ResourceDictionary.DeferredState.Deferred, info.State);

        // X145: add the missing dependency and look up again — Realize runs again and succeeds.
        dict["Missing"] = new TestBrush();
        var realized = dict["B"];
        Assert.IsType<Cursorial.UI.Controls.Border>(realized);
    }

    [Fact] // X146
    public void X146_DeferredEntryInfo_AfterRealization()
    {
        using var host = Cursorial.UI.Testing.UITestHost.Create();
        var dict = (ResourceDictionary)LoadRaw(
            $"<ResourceDictionary{Pre}><TestBrush x:Key=\"A\" Color=\"Red\"/></ResourceDictionary>");
        _ = dict["A"]; // realize at the host's variant
        // After realization the entry's State is Realized and it carries the variant it froze at (C-3/C-7).
        Assert.True(dict.TryGetDeferredInfo("A", out var info));
        Assert.Equal(ResourceDictionary.DeferredState.Realized, info.State);
        Assert.NotNull(info.RealizedAtVariant);
    }
}
