// xUnit1031 disabled: UITestHost is single-thread-affine (see FrameLoopTests).
#pragma warning disable xUnit1031

using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Controls;

/// <summary>
/// The M2 plain-text fast path: <see cref="FormattedTextCache.TryFormatPlainTextFast"/> bypasses
/// the RichText model for a single markup-free line that fits its bounds, and the bypass is
/// BYTE-IDENTICAL BY CONSTRUCTION — proven here by painting the fast layout and the full-pipeline
/// layout for the same input into twin buffers and asserting cell-for-cell equality, across a
/// matrix of eligible inputs (ASCII / wide / combining / emoji graphemes, every alignment,
/// varying bounds, two document-default carriers) and through BOTH paint lanes (the raw
/// <see cref="FormattedText.Paint"/> walk, and <see cref="DrawingContext.DrawFormattedText"/>
/// under an element-style preference — the presenters' route, gradient foreground included so the
/// resolver ladder's sampling geometry is exercised). Eligibility ROUTING is observable through
/// the cache's internal counters; the negative pins nail the conservative predicate.
/// </summary>
public sealed class FormattedTextFastPathTests
{
    // TextBlock's plain-lane document default (the carrier its full pipeline hands RichTextBuilder).
    private static readonly BrushedStyle TextBlockCarrier =
        BrushedStyle.FromStated(CellStyle.Transparent.WithForeground(Color.Default));

    // A second, gradient-bearing carrier: DefaultCarrier.IsUniform goes false, so Paint takes the
    // ComputeExtent document-rect route — the fast layout must agree there too.
    private static readonly BrushedStyle GradientCarrier = new()
    {
        Foreground = new LinearGradientBrush(Color.FromRgb(255, 0, 0), Color.FromRgb(0, 0, 255)),
        Background = new SolidColorBrush(Color.FromRgb(10, 10, 40)),
    };

    private sealed class Fixture : IDisposable
    {
        public UIHeadlessHost Host { get; }
        public Border Element { get; }
        public FormattedTextCache Cache { get; }

        public Fixture()
        {
            Host = UIHeadlessHost.Create();
            Element = new Border();
            Host.ShowRoot(Element);
            Assert.True(Host.RunUntilIdle());
            Cache = new FormattedTextCache(Element, () => { });
        }

        public void Dispose() => Host.Dispose();
    }

    private static FormattedTextCache.LayoutRequest Request(
        object? source, bool markupLane = false, int columns = 20, int? maxRows = null,
        WrapMode wrap = WrapMode.WordWrap,
        TextAlignment alignment = TextAlignment.Left,
        TextTrimming trim = TextTrimming.CharacterEllipsis)
        => new(source, markupLane, columns, maxRows, wrap, alignment, trim);

    /// <summary>The full pipeline exactly as TextBlock's plain lane runs it (a single-line text is
    /// one <c>Run</c> in a builder carrying the document default and the paragraph modes).</summary>
    private static FormattedText FullPipeline(FormattedTextCache cache, string text,
                                              in FormattedTextCache.LayoutRequest request,
                                              in BrushedStyle carrier)
    {
        var document = new RichTextBuilder(carrier,
                                           defaultTrimming: request.Trim,
                                           defaultWrap: request.Wrap)
                       .Run(text)
                       .Build();

        return cache.Format(document, request.Columns, request.MaxRows,
                            request.Alignment, request.Trim, request.Wrap);
    }

    // ─────────────────────────────── the equivalence matrix ───────────────────────────────

    // ASCII, interior/double/trailing spaces, wide CJK, combining accents, emoji, single cell,
    // mixed-width — all eligible (single line, ASCII-space-only whitespace, no leading space).
    private static readonly string[] Texts =
    [
        "Hello",
        "Hello world",
        "a  b",
        "trail ",
        "你好",
        "éclair",
        "🙂ok",
        "x",
        "mixed 界 x",
    ];

    private static readonly TextAlignment[] Alignments =
    [
        TextAlignment.Left, TextAlignment.Center, TextAlignment.Right, TextAlignment.Justify,
    ];

    [Fact]
    public void FastPath_PaintsCellForCellIdenticallyToTheFullPipeline_AcrossTheEligibleMatrix()
    {
        using var f = new Fixture();

        // The presenters' preference build: element foreground + imposed attributes.
        var preference = new BrushedStyle { Foreground = new SolidColorBrush(Color.FromRgb(10, 200, 30)) }
            .Imposing(TextAttributes.Bold);

        var combos = 0;

        foreach (var text in Texts)
        {
            var textWidth = GraphemeWidth.StringWidth(text);

            // Exact fit, small slack, taller slot, wide unbounded-rows slot.
            (int Columns, int? MaxRows)[] bounds =
            [
                (textWidth, 1),
                (textWidth + 3, 1),
                (textWidth + 7, 3),
                (40, null),
            ];

            foreach (var alignment in Alignments)
            foreach (var (columns, maxRows) in bounds)
            foreach (var carrier in new[] { TextBlockCarrier, GradientCarrier })
            {
                combos++;

                var request = Request(text, columns: columns, maxRows: maxRows, alignment: alignment);

                var fast = f.Cache.TryFormatPlainTextFast(in request, in carrier);
                Assert.NotNull(fast); // the whole matrix is eligible by construction

                var full = FullPipeline(f.Cache, text, in request, in carrier);

                Assert.Equal(full.Size, fast!.Size);
                Assert.Equal(full.ProvidedColumns, fast.ProvidedColumns);
                Assert.Equal(full.HasTrimmedLines, fast.HasTrimmedLines);

                var label = $"text=\"{text}\" alignment={alignment} columns={columns} maxRows={maxRows?.ToString() ?? "null"} " +
                            $"carrier={(carrier == TextBlockCarrier ? "textblock" : "gradient")}";

                // Lane 1: the raw paint walk (no resolver).
                AssertTwinBuffers(fast, full, columns, maxRows ?? 3, label + " lane=paint",
                                  (FormattedText ft, in CellBufferView view, in Rect rect)
                                      => ft.Paint(view, rect, OutputCapabilities.None));

                // Lane 2: the presenters' route — DrawingContext.DrawFormattedText under an
                // element-style preference (the resolver ladder samples inline scopes here, so a
                // run-structure divergence between the two layouts would surface in these cells).
                AssertTwinBuffers(fast, full, columns, maxRows ?? 3, label + " lane=preference",
                                  (FormattedText ft, in CellBufferView view, in Rect rect) =>
                                  {
                                      var document = ft;    // copies: `in` parameters cannot be
                                      var paintRect = rect; // captured by the scene-draw closure
                                      using var scene = Scene.Create(view.Columns, view.Rows);
                                      scene.Draw(ctx => ctx.DrawFormattedText(document, paintRect, OutputCapabilities.None, preference));
                                      new SceneCompositor(CellStyle.Default.WithBackground(Color.FromRgb(0, 0, 0)))
                                          .Composite([new SceneLayer(scene)], view);
                                  });
            }
        }

        // The matrix size is part of the contract: 9 texts × 4 alignments × 4 bounds × 2 carriers.
        Assert.Equal(288, combos);
    }

    private delegate void PaintLane(FormattedText ft, in CellBufferView view, in Rect rect);

    private static void AssertTwinBuffers(FormattedText fast, FormattedText full,
                                          int columns, int rows, string label, PaintLane paint)
    {
        // Painted one cell in from every edge so an anchoring error can leak OUT of the rect and
        // still be seen.
        var rect = new Rect(1, 1, columns, rows);

        var fastBuffer = new CellBuffer(columns + 2, rows + 2);
        var fullBuffer = new CellBuffer(columns + 2, rows + 2);

        paint(fast, fastBuffer.AsView(), rect);
        paint(full, fullBuffer.AsView(), rect);

        for (var r = 0; r < fullBuffer.Rows; r++)
            for (var c = 0; c < fullBuffer.Columns; c++)
            {
                var expected = fullBuffer[c, r];
                var actual = fastBuffer[c, r];

                if (expected != actual)
                    Assert.Fail($"Cell ({c},{r}) diverged for {label}: full=[{expected}] fast=[{actual}]");
            }
    }

    // ─────────────────────────────── the eligibility predicate ───────────────────────────────

    [Fact]
    public void FastPath_DeclinesMarkupMultilineAndOverflowingInputs()
    {
        using var f = new Fixture();

        // The three mandated pins: a markup-bearing, a multiline, and an overflowing input all
        // take the full pipeline.
        Assert.Null(f.Cache.TryFormatPlainTextFast(Request("[b]x[/b]", markupLane: true), TextBlockCarrier));
        Assert.Null(f.Cache.TryFormatPlainTextFast(Request("a\nb"), TextBlockCarrier));
        Assert.Null(f.Cache.TryFormatPlainTextFast(Request("a\r\nb"), TextBlockCarrier));
        Assert.Null(f.Cache.TryFormatPlainTextFast(Request("abcdefgh", columns: 5), TextBlockCarrier));

        // The conservative fringe: tabs, soft hyphens, non-ASCII whitespace, leading spaces,
        // non-string sources, empty text.
        Assert.Null(f.Cache.TryFormatPlainTextFast(Request("a\tb"), TextBlockCarrier));
        Assert.Null(f.Cache.TryFormatPlainTextFast(Request("co\u00ADoperate"), TextBlockCarrier));
        Assert.Null(f.Cache.TryFormatPlainTextFast(Request("a\u00A0b"), TextBlockCarrier));
        Assert.Null(f.Cache.TryFormatPlainTextFast(Request(" a"), TextBlockCarrier));
        Assert.Null(f.Cache.TryFormatPlainTextFast(Request(new RichTextBuilder().Run("x").Build()), TextBlockCarrier));
        Assert.Null(f.Cache.TryFormatPlainTextFast(Request(""), TextBlockCarrier));

        Assert.Equal(0, f.Cache.FastPathFormatCount); // nothing above may have routed fast
    }

    [Fact]
    public void FastPath_DeclinesGlyphSourceLanes_FontAndSizing()
    {
        using var f = new Fixture();

        TextElement.SetFont(f.Element, FigletFonts.Small);
        Assert.Null(f.Cache.TryFormatPlainTextFast(Request("Hi"), TextBlockCarrier));
        TextElement.SetFont(f.Element, null);

        TextElement.SetSizing(f.Element, TextSizing.Double);
        Assert.Null(f.Cache.TryFormatPlainTextFast(Request("Hi"), TextBlockCarrier));
        TextElement.SetSizing(f.Element, TextSizing.Normal);

        Assert.NotNull(f.Cache.TryFormatPlainTextFast(Request("Hi"), TextBlockCarrier)); // control
    }

    // ─────────────────────────────── TextBlock routing wiring ───────────────────────────────

    private static (UIHeadlessHost Host, SlotHost Slot) Show(UIElement child, int columns, int rows)
    {
        var slot = new SlotHost(child) { SlotRect = new Rect(0, 0, columns, rows) };
        var host = UIHeadlessHost.Create();
        host.ShowRoot(slot);
        Assert.True(host.RunUntilIdle());
        return (host, slot);
    }

    [Fact]
    public void TextBlock_EligiblePlainText_RoutesThroughTheFastPath()
    {
        var tb = new TextBlock("Hi mom") { TextAlignment = TextAlignment.Right };
        var (host, _) = Show(tb, columns: 20, rows: 1);
        using var _1 = host;

        // The frame is the ordinary right-aligned render…
        Assert.Equal("H", host.GetCell(14, 0).Grapheme);
        Assert.Equal("m", host.GetCell(19, 0).Grapheme);

        // …and it was built by the fast path, never the RichText pipeline.
        Assert.True(tb.Cache.FastPathFormatCount >= 1,
                    $"expected the fast path to have formatted (fast={tb.Cache.FastPathFormatCount})");
        Assert.Equal(0, tb.Cache.FullFormatCount);
    }

    [Fact]
    public void TextBlock_MarkupMultilineAndOverflowingText_RouteThroughTheFullPipeline()
    {
        var markup = new TextBlock { Markup = "[b]Hi[/b]" };
        var (host1, _) = Show(markup, columns: 20, rows: 1);
        using var _1 = host1;
        Assert.Equal(0, markup.Cache.FastPathFormatCount);
        Assert.True(markup.Cache.FullFormatCount >= 1);

        var multiline = new TextBlock("a\nb");
        var (host2, _) = Show(multiline, columns: 20, rows: 3);
        using var _2 = host2;
        Assert.Equal(0, multiline.Cache.FastPathFormatCount);
        Assert.True(multiline.Cache.FullFormatCount >= 1);

        // Width pins the measure constraint below the text width, so every pass overflows.
        var overflowing = new TextBlock("abcdefgh")
                          {
                              Width = 5,
                              TextWrapping = WrapMode.NoWrap,
                              TextTrimming = TextTrimming.CharacterEllipsis
                          };
        var (host3, _) = Show(overflowing, columns: 20, rows: 1);
        using var _3 = host3;
        Assert.Equal(0, overflowing.Cache.FastPathFormatCount);
        Assert.True(overflowing.Cache.FullFormatCount >= 1);
    }
}
