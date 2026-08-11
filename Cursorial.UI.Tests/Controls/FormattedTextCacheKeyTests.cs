// xUnit1031 disabled: UITestHost is single-thread-affine (see FrameLoopTests).
#pragma warning disable xUnit1031

using Cursorial.Media;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Themes;

namespace Cursorial.Tests.UI.Controls;

/// <summary>
/// The shared <see cref="FormattedTextCache"/>'s freshness key, one kill test per TERM in the
/// <see cref="ScaledTextCacheKeyTests"/> style: a dropped term shows up as a missed rebuild (a
/// stale hit where a miss was due), a broken comparison as a spurious rebuild (a miss where a hit
/// was due). This is the mutation census for the key the RTP/Figlet hand copies degraded (doc
/// defect 3 — the photocopied cache lost its freshness terms because nothing observed them).
/// The cache is driven DIRECTLY (no presenter) against an attached host element, and — deliberately —
/// WITHOUT <see cref="FormattedTextCache.Attach"/>: no capability subscription exists here, so the
/// caps test exercises the KEY term, not the push path.
/// </summary>
public sealed class FormattedTextCacheKeyTests
{
    private sealed class Fixture : IDisposable
    {
        public UIHeadlessHost Host { get; }
        public Border Element { get; }
        public FormattedTextCache Cache { get; }

        public Fixture(UIHeadlessHostOptions? options = null)
        {
            Host = options is null ? UIHeadlessHost.Create() : UIHeadlessHost.Create(options);
            Element = new Border();
            Host.ShowRoot(Element);
            Assert.True(Host.RunUntilIdle());
            Cache = new FormattedTextCache(Element, () => { });
        }

        public void Dispose() => Host.Dispose();
    }

    private static RichText Doc(string text) => new RichTextBuilder().Run(text).Build();

    private static FormattedTextCache.LayoutRequest Request(
        object? source = null, bool markupLane = false, int columns = 20, int? maxRows = null,
        WrapMode wrap = WrapMode.WordWrap,
        TextAlignment alignment = TextAlignment.Left,
        TextTrimming trim = TextTrimming.CharacterEllipsis)
        => new(source ?? "hello", markupLane, columns, maxRows, wrap, alignment, trim);

    // ─────────────────────────── the spurious-rebuild direction ───────────────────────────

    [Fact]
    public void UnchangedRequest_ServesTheSameInstance()
    {
        using var f = new Fixture();
        var request = Request();

        var first = f.Cache.FormatAndStore(in request, Doc("hello"));

        Assert.True(f.Cache.TryGetLayout(in request, out var second));
        // Same instance, not merely an equal one: a fresh-but-equal layout would re-run the
        // formatter on every measure/arrange/render pass.
        Assert.Same(first, second);
    }

    [Fact]
    public void StringSource_ComparesByValue_NotReference()
    {
        using var f = new Fixture();
        var request = Request(source: "hello");
        f.Cache.FormatAndStore(in request, Doc("hello"));

        // A fresh-but-equal string (a re-read property, a re-bound cell) must HIT — reference
        // comparison here would spuriously reformat every bound label on every pass.
        var sameChars = Request(source: new string(['h', 'e', 'l', 'l', 'o']));
        Assert.True(f.Cache.TryGetLayout(in sameChars, out _));
    }

    [Fact]
    public void RichTextSource_ComparesByReference()
    {
        using var f = new Fixture();
        var document = Doc("hello");
        var request = Request(source: document);
        f.Cache.FormatAndStore(in request, document);

        Assert.True(f.Cache.TryGetLayout(in request, out _)); // same reference — hit

        // An equal-CONTENT re-parse is a different document: the parse is sticky and
        // identity-cached upstream, so a new reference means the parse genuinely re-ran
        // (new baked brushes) and the layout must follow it.
        var reparsed = Request(source: Doc("hello"));
        Assert.False(f.Cache.TryGetLayout(in reparsed, out _));
    }

    // ─────────────────────────── the missed-rebuild direction, per term ───────────────────────────

    [Fact]
    public void SourceValueChange_Misses()
    {
        using var f = new Fixture();
        var request = Request(source: "hello");
        f.Cache.FormatAndStore(in request, Doc("hello"));

        var changed = Request(source: "world");
        Assert.False(f.Cache.TryGetLayout(in changed, out _));
    }

    [Fact]
    public void MarkupLaneFlip_Misses()
    {
        using var f = new Fixture();

        // The SAME source string in the other lane ("[b]x[/b]" as literal Text vs as Markup)
        // formats to different glyphs — the lane bit is the only term that can tell them apart.
        var literal = Request(source: "[b]x[/b]", markupLane: false);
        f.Cache.FormatAndStore(in literal, Doc("[b]x[/b]"));

        var markup = literal with { MarkupLane = true };
        Assert.False(f.Cache.TryGetLayout(in markup, out _));
    }

    [Fact]
    public void ColumnsChange_Misses()
    {
        using var f = new Fixture();
        var request = Request(columns: 20);
        f.Cache.FormatAndStore(in request, Doc("hello"));

        var narrower = request with { Columns = 12 };
        Assert.False(f.Cache.TryGetLayout(in narrower, out _));
    }

    [Fact]
    public void FormatterOptionChange_Misses_PerOption()
    {
        using var f = new Fixture();
        var request = Request();
        f.Cache.FormatAndStore(in request, Doc("hello"));

        var wrap = request with { Wrap = WrapMode.NoWrap };
        Assert.False(f.Cache.TryGetLayout(in wrap, out _));

        var alignment = request with { Alignment = TextAlignment.Right };
        Assert.False(f.Cache.TryGetLayout(in alignment, out _));

        var trim = request with { Trim = TextTrimming.ClipFromEnd };
        Assert.False(f.Cache.TryGetLayout(in trim, out _));
    }

    [Fact]
    public void ResourceVersionBump_Misses()
    {
        using var f = new Fixture();
        var request = Request();
        f.Cache.FormatAndStore(in request, Doc("hello"));

        var variantBefore = f.Host.Application.ActualThemeVariant;
        var versionBefore = ResourceServices.GetResourceVersion(f.Element);

        // A dictionary mutation bumps the root-global version (C165's contract) — resource
        // brushes bake into parses/layouts with NO subscription (CD16), so the version term is
        // the ONLY thing standing between a theme-resource edit and permanently stale ink.
        f.Host.Application.Resources["FormattedTextCacheKeyTests.K"] =
            new SolidColorBrush(Color.FromRgb(1, 2, 3));
        Assert.True(f.Host.RunUntilIdle());

        // Term isolation: the variant did NOT move — only the version term can produce this miss.
        Assert.Equal(variantBefore, f.Host.Application.ActualThemeVariant);
        Assert.True(ResourceServices.GetResourceVersion(f.Element) > versionBefore);

        Assert.False(f.Cache.TryGetLayout(in request, out _));
    }

    [Fact]
    public void ThemeVariantFlip_Misses()
    {
        using var f = new Fixture();
        f.Host.Application.RequestedThemeBase = ThemeBase.Dark;
        f.Host.RunFrame();

        var request = Request();
        f.Cache.FormatAndStore(in request, Doc("hello"));

        // NOTE (census honesty): at this HEAD every ActualThemeVariant change ALSO bumps the
        // resource version (probed: a bare base flip moves Version), so this test kills the
        // (Version, Variant) pair rather than the Variant term alone — no individually-killing
        // scenario exists today. The Variant term is retained as CD16's documented contract and
        // as the belt should a future variant change ever stop pulsing the registry.
        f.Host.Application.RequestedThemeBase = ThemeBase.Light;
        f.Host.RunFrame();

        Assert.False(f.Cache.TryGetLayout(in request, out _));
    }

    [Fact]
    public void CapabilitiesChange_Misses_WithoutAnySubscription()
    {
        // KittyGraphics: a NEGOTIATED graphics protocol, so forcing images off genuinely changes
        // EffectiveCapabilities.Output (under the default KittyTruecolor no protocol is negotiated
        // and the override is a no-op the caps term correctly ignores).
        using var f = new Fixture(new UIHeadlessHostOptions { Capabilities = HeadlessCapabilities.KittyGraphics });
        var request = Request();
        f.Cache.FormatAndStore(in request, Doc("hello"));

        var capsBefore = f.Host.Application.EffectiveCapabilities.Output;
        var variantBefore = f.Host.Application.ActualThemeVariant;

        // An FB-5 override flip replaces EffectiveCapabilities. This cache instance holds NO
        // subscription (Attach was never called — the fixture's point), so only the caps KEY term
        // can notice; formatting resolves sized runs and glyph sources against these caps, so a
        // stale hit would paint the OLD terminal's realization.
        f.Host.Application.CapabilityOverrides = new CapabilityOverrides().WithImagesDisabled();
        Assert.True(f.Host.RunUntilIdle());

        Assert.NotEqual(capsBefore, f.Host.Application.EffectiveCapabilities.Output); // the flip took
        Assert.Equal(variantBefore, f.Host.Application.ActualThemeVariant);           // variant held still

        Assert.False(f.Cache.TryGetLayout(in request, out _));
    }

    // ─────────────────────────── the row-budget (grow-back) rule ───────────────────────────

    [Fact]
    public void CappedLayout_IsOnlyValidAtItsExactBudget()
    {
        using var f = new Fixture();

        // Five words at 5 columns word-wrap to 5 rows; a 2-row budget truncates (cap bit set).
        var request = Request(source: "one two three four five", columns: 5, maxRows: 2);
        var capped = f.Cache.FormatAndStore(in request, Doc("one two three four five"));
        Assert.True(capped.Size.Rows >= 2);

        Assert.True(f.Cache.TryGetLayout(in request, out _)); // same budget — hit

        var taller = request with { MaxRows = 6 };
        Assert.False(f.Cache.TryGetLayout(in taller, out _)); // grow-back: MUST reformat

        var unbounded = request with { MaxRows = null };
        Assert.False(f.Cache.TryGetLayout(in unbounded, out _));
    }

    [Fact]
    public void UncappedLayout_IsValidForAnyBudgetItFitsIn()
    {
        using var f = new Fixture();

        var request = Request(source: "hi", columns: 20, maxRows: 5);
        var layout = f.Cache.FormatAndStore(in request, Doc("hi"));
        Assert.Equal(1, layout.Size.Rows); // the cap never bit

        // No spurious rebuild: a 1-row layout is the same layout at ANY budget that fits it.
        var unbounded = request with { MaxRows = null };
        Assert.True(f.Cache.TryGetLayout(in unbounded, out _));

        var exact = request with { MaxRows = 1 };
        Assert.True(f.Cache.TryGetLayout(in exact, out _));
    }
}
