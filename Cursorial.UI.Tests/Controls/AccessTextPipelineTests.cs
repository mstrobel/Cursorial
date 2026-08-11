// xUnit1031 disabled: UITestHost is single-thread-affine (see FrameLoopTests).
#pragma warning disable xUnit1031

using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Controls;

/// <summary>
/// UNIFIED-B2: AccessTextPresenter joins the shared text pipeline, and the access-key indicator is
/// PIPELINE data — a <c>TextIndicator(Cluster, Delta)</c> declaration the rich path realizes as a
/// delta-wearing mnemonic run (<see cref="FormattedTextCache.BuildIndicatorText"/>) and the fast
/// path composes directly. The fast≡slow equivalence property therefore EXTENDS to
/// indicator-bearing inputs: indicator at the start / middle / end cluster, a wide-glyph mnemonic,
/// and gradient-bearing carriers and deltas, proven cell-for-cell through both paint lanes so the
/// two renderings cannot drift.
/// </summary>
public sealed class AccessTextPipelineTests
{
    // The TextBlock-ish plain carrier and a gradient + attribute-bearing carrier (the ATP carrier
    // is BrushedStyle.FromElement, which can state brushes AND attributes — the delta must compose
    // over both identically in both lanes).
    private static readonly BrushedStyle PlainCarrier =
        BrushedStyle.FromStated(CellStyle.Transparent.WithForeground(Color.Default));

    private static readonly BrushedStyle GradientCarrier = new BrushedStyle
    {
        Foreground = new LinearGradientBrush(Color.FromRgb(255, 0, 0), Color.FromRgb(0, 0, 255)),
        Background = new SolidColorBrush(Color.FromRgb(10, 10, 40)),
    }.Weighing(TextWeight.Faint);

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
        string text, int columns, FormattedTextCache.TextIndicator? indicator,
        TextAlignment alignment = TextAlignment.Left,
        BrushedStyle carrier = default,
        WrapMode wrap = WrapMode.NoWrap,
        TextTrimming trim = TextTrimming.CharacterEllipsis)
        => new(text, MarkupLane: false, columns, MaxRows: 1, wrap, alignment, trim,
               Carrier: carrier, Indicator: indicator);

    /// <summary>The rich path exactly as AccessTextPresenter runs it — through the SHARED
    /// indicator document builder, so a drift (or a mutation) in that builder diverges from the
    /// fast lane and this matrix sees it.</summary>
    private static FormattedText FullPipeline(FormattedTextCache cache, string text,
                                              in FormattedTextCache.LayoutRequest request,
                                              in BrushedStyle carrier)
    {
        var document = FormattedTextCache.BuildIndicatorText(text, in carrier, request.Indicator,
                                                             request.Trim, request.Wrap);
        return cache.Format(document, request.Columns, request.MaxRows,
                            request.Alignment, request.Trim, request.Wrap);
    }

    // ─────────────────────────────── the indicator matrix ───────────────────────────────

    // (text, mnemonic cluster): start, middle and end of a plain label; a mnemonic just past a
    // space; a WIDE-GLYPH mnemonic ('本'); a wide glyph mid-text with an ASCII mnemonic after it.
    private static readonly (string Text, int Cluster)[] IndicatorTexts =
    [
        ("Save", 0),
        ("Save", 1),
        ("Save", 3),
        ("Open File", 5),
        ("日本語x", 1),
        ("mixed 界 x", 8),
    ];

    private static readonly TextAlignment[] Alignments =
    [
        TextAlignment.Left, TextAlignment.Center, TextAlignment.Right, TextAlignment.Justify,
    ];

    // The delta shapes the cue algebra produces (and a foreground-gradient override, which drives
    // the resolver's run-brush leg and its inline-scope geometry).
    private static readonly (string Name, BrushedStyle Delta)[] Deltas =
    [
        ("underline-solid", BrushedStyle.Identity.Underlining(UnderlineStyle.Single,
                                                              new SolidColorBrush(Color.FromRgb(0, 200, 40)))),
        ("underline-gradient", BrushedStyle.Identity.Underlining(UnderlineStyle.Curly,
                                                                 new LinearGradientBrush(Colors.Black, Colors.White))),
        ("weight-and-underline", BrushedStyle.Identity.Weighing(TextWeight.Bold)
                                                      .Underlining(UnderlineStyle.Single,
                                                                   new SolidColorBrush(Color.FromRgb(200, 0, 0)))),
        ("inverse-toggle", BrushedStyle.Identity.Toggling(TextAttributes.Inverse)),
        ("foreground-gradient", BrushedStyle.Identity.WithForeground(
             new LinearGradientBrush(Color.FromRgb(0, 255, 0), Color.FromRgb(255, 0, 255)))),
    ];

    [Fact]
    public void FastPath_WithIndicator_PaintsCellForCellIdenticallyToTheFullPipeline()
    {
        using var f = new Fixture();

        // The presenters' preference build — kept from the B1 matrix so the resolver-ladder lane
        // stays exercised (ATP itself paints with no preference; the property must hold under any).
        var preference = new BrushedStyle { Foreground = new SolidColorBrush(Color.FromRgb(10, 200, 30)) }
            .Imposing(TextAttributes.Bold);

        var combos = 0;

        foreach (var (text, cluster) in IndicatorTexts)
        {
            var textWidth = GraphemeWidth.StringWidth(text);

            foreach (var (deltaName, delta) in Deltas)
            foreach (var alignment in Alignments)
            foreach (var columns in new[] { textWidth, textWidth + 4 })
            foreach (var carrier in new[] { PlainCarrier, GradientCarrier })
            {
                combos++;

                var indicator = new FormattedTextCache.TextIndicator(cluster, delta);
                var request = Request(text, columns, indicator, alignment, carrier);

                var fast = f.Cache.TryFormatPlainTextFast(in request, in carrier);
                Assert.NotNull(fast); // the whole matrix is eligible by construction

                var full = FullPipeline(f.Cache, text, in request, in carrier);

                Assert.Equal(full.Size, fast!.Size);
                Assert.Equal(full.ProvidedColumns, fast.ProvidedColumns);
                Assert.Equal(full.HasTrimmedLines, fast.HasTrimmedLines);

                var label = $"text=\"{text}\" cluster={cluster} delta={deltaName} alignment={alignment} " +
                            $"columns={columns} carrier={(carrier == PlainCarrier ? "plain" : "gradient")}";

                AssertTwinBuffers(fast, full, columns, 1, label + " lane=paint",
                                  (FormattedText ft, in CellBufferView view, in Rect rect)
                                      => ft.Paint(view, rect, OutputCapabilities.None));

                AssertTwinBuffers(fast, full, columns, 1, label + " lane=preference",
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

        // The matrix size is part of the contract: 6 (text, cluster) pairs × 5 deltas ×
        // 4 alignments × 2 bounds × 2 carriers.
        Assert.Equal(480, combos);
    }

    [Fact]
    public void FastPath_OutOfRangeIndicatorCluster_FormatsAsPlainText_InBothLanes()
    {
        using var f = new Fixture();

        var delta = BrushedStyle.Identity.Underlining(UnderlineStyle.Single, new SolidColorBrush(Colors.Black));
        var indicator = new FormattedTextCache.TextIndicator(99, delta);
        var request = Request("Save", columns: 10, indicator);

        var fast = f.Cache.TryFormatPlainTextFast(in request, PlainCarrier);
        Assert.NotNull(fast);

        var full = FullPipeline(f.Cache, "Save", in request, PlainCarrier);

        AssertTwinBuffers(fast!, full, 10, 1, "out-of-range cluster",
                          (FormattedText ft, in CellBufferView view, in Rect rect)
                              => ft.Paint(view, rect, OutputCapabilities.None));

        // ...and both equal the indicator-free layout (the declaration didn't land anywhere).
        var plainRequest = Request("Save", columns: 10, indicator: null);
        var plain = f.Cache.TryFormatPlainTextFast(in plainRequest, PlainCarrier);
        Assert.NotNull(plain);
        AssertTwinBuffers(fast!, plain!, 10, 1, "out-of-range vs plain",
                          (FormattedText ft, in CellBufferView view, in Rect rect)
                              => ft.Paint(view, rect, OutputCapabilities.None));
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

    // ─────────────────────────────── presenter routing wiring ───────────────────────────────

    private static (UIHeadlessHost Host, SlotHost Slot) Show(UIElement child, int columns, int rows)
    {
        var slot = new SlotHost(child) { SlotRect = new Rect(0, 0, columns, rows) };
        var host = UIHeadlessHost.Create();
        host.ShowRoot(slot);
        Assert.True(host.RunUntilIdle());
        return (host, slot);
    }

    private static AccessTextPresenter Presenter(string label, char key, int keyIndex, bool showCue = true)
    {
        var presenter = new AccessTextPresenter
                        {
                            Text = new AccessText(label, key, keyIndex),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Top,
                        };
        AccessKeyManager.SetShowUnderline(presenter, showCue);
        return presenter;
    }

    [Fact]
    public void AccessTextPresenter_FittingLabel_RoutesThroughTheFastPath()
    {
        var presenter = Presenter("Save", 'S', 0);
        var (host, _) = Show(presenter, columns: 20, rows: 1);
        using var _1 = host;

        // The frame is the ordinary cue'd render…
        Assert.Equal("S", host.GetCell(0, 0).Grapheme);
        Assert.True(host.GetCell(0, 0).Style.Attributes.HasFlag(TextAttributes.Underline));
        Assert.False(host.GetCell(1, 0).Style.Attributes.HasFlag(TextAttributes.Underline));

        // …and it was built by the fast path, never the RichText pipeline — the indicator is part
        // of the fast subset now (M2).
        Assert.True(presenter.Cache.FastPathFormatCount >= 1,
                    $"expected the fast path to have formatted (fast={presenter.Cache.FastPathFormatCount})");
        Assert.Equal(0, presenter.Cache.FullFormatCount);
    }

    /// <summary>
    /// The rich path's indicator is OBSERVED at the frame level here: an overflowing label is
    /// ineligible for the fast path (trimming engages), so the cue on its surviving mnemonic can
    /// only come from the delta-wearing run the rich document builder emitted. This is the
    /// designated kill for the "drop the indicator delta from the rich path" mutation.
    /// </summary>
    [Fact]
    public void AccessTextPresenter_OverflowingLabel_RoutesThroughTheFullPipeline_AndStillWearsTheCue()
    {
        // Width pins the measure constraint below the label width, so EVERY pass overflows and the
        // fast path never engages (without it the initial wide measure legitimately routes fast).
        var presenter = Presenter("Documentation", 'D', 0);
        presenter.Width = 8;
        var (host, _) = Show(presenter, columns: 8, rows: 1);
        using var _1 = host;

        Assert.StartsWith("Documen…", host.GetRowText(0));
        Assert.True(presenter.GetValue(TextBlock.IsTrimmedProperty));

        // The mnemonic survives the trim and wears the cue — through the FULL pipeline.
        Assert.True(host.GetCell(0, 0).Style.Attributes.HasFlag(TextAttributes.Underline));
        Assert.False(host.GetCell(1, 0).Style.Attributes.HasFlag(TextAttributes.Underline));

        Assert.Equal(0, presenter.Cache.FastPathFormatCount);
        Assert.True(presenter.Cache.FullFormatCount >= 1,
                    $"expected the full pipeline to have formatted (full={presenter.Cache.FullFormatCount})");
    }

    [Fact]
    public void AccessTextPresenter_WideGlyphMnemonic_CarriesTheCueOnTheWideCluster()
    {
        // '本' is cluster 1 of "日本語" and sits at column 2 ('日' is two cells wide).
        var presenter = Presenter("日本語", '本', 1);
        var (host, _) = Show(presenter, columns: 12, rows: 1);
        using var _1 = host;

        Assert.Equal("日", host.GetCell(0, 0).Grapheme);
        Assert.Equal("本", host.GetCell(2, 0).Grapheme);

        Assert.False(host.GetCell(0, 0).Style.Attributes.HasFlag(TextAttributes.Underline));
        Assert.True(host.GetCell(2, 0).Style.Attributes.HasFlag(TextAttributes.Underline));
        Assert.False(host.GetCell(4, 0).Style.Attributes.HasFlag(TextAttributes.Underline));
    }

    // ─────────────────────────────── cue / carrier freshness ───────────────────────────────

    [Fact]
    public void AccessTextPresenter_ShowUnderlineFlip_TogglesTheCueThroughTheCache()
    {
        var presenter = Presenter("File", 'F', 0, showCue: false);
        var (host, _) = Show(presenter, columns: 12, rows: 1);
        using var _1 = host;

        Assert.False(host.GetCell(0, 0).Style.Attributes.HasFlag(TextAttributes.Underline));

        // The flip is AffectsRender (global effects) and the indicator is a layout-key term, so
        // the repaint re-formats by key miss — no measure pass needed, no stale layout served.
        AccessKeyManager.SetShowUnderline(presenter, true);
        host.RunFrame();
        Assert.True(host.GetCell(0, 0).Style.Attributes.HasFlag(TextAttributes.Underline));

        AccessKeyManager.SetShowUnderline(presenter, false);
        host.RunFrame();
        Assert.False(host.GetCell(0, 0).Style.Attributes.HasFlag(TextAttributes.Underline));
    }

    [Fact]
    public void AccessTextPresenter_IndicatorBrushChange_RepaintsTheFreshCue()
    {
        var red = Color.FromRgb(220, 20, 20);
        var green = Color.FromRgb(20, 220, 20);

        var presenter = Presenter("File", 'F', 0);
        presenter.IndicatorBrush = new SolidColorBrush(red);

        var (host, _) = Show(presenter, columns: 12, rows: 1);
        using var _1 = host;

        Assert.Equal(red, host.GetCell(0, 0).Style.UnderlineColor);

        // The delta is a layout-key term: the brush swap re-formats on the repaint its
        // AffectsRender lane triggers, and the fresh cue ink lands.
        presenter.IndicatorBrush = new SolidColorBrush(green);
        host.RunFrame();
        Assert.Equal(green, host.GetCell(0, 0).Style.UnderlineColor);
    }

    [Fact]
    public void AccessTextPresenter_ForegroundChange_RepaintsFreshInk()
    {
        var red = Color.FromRgb(220, 20, 20);
        var blue = Color.FromRgb(20, 20, 220);

        var presenter = Presenter("File", 'F', 0, showCue: false);
        presenter.Foreground = new SolidColorBrush(red);

        var (host, _) = Show(presenter, columns: 12, rows: 1);
        using var _1 = host;

        Assert.Equal(red, host.GetCell(1, 0).Style.Foreground);

        // The element style is the document-default CARRIER, a layout-key term — the swap cannot
        // serve the layout that carries the old brush.
        presenter.Foreground = new SolidColorBrush(blue);
        host.RunFrame();
        Assert.Equal(blue, host.GetCell(1, 0).Style.Foreground);
    }

    // ─────────────────────────────── the trim-at-the-mnemonic edge ───────────────────────────────

    /// <summary>
    /// CHARACTERIZATION of a new edge the pipeline reroute introduces: when truncation cuts
    /// immediately after a surviving mnemonic, the synthesized ellipsis joins the LAST run's style
    /// — the mnemonic's delta — so the ellipsis wears the cue. The old hand-rolled draw never
    /// underlined its ellipsis; the mnemonic itself keeps its cue either way (an improvement over
    /// the old code's "no cue at all near the cut"). Pinned so a later decision to strip the
    /// delta from the trim indicator is a deliberate one.
    /// </summary>
    [Fact]
    public void AccessTextPresenter_EllipsisCutAtTheMnemonic_TheEllipsisJoinsTheCueRun()
    {
        var presenter = Presenter("Save", 'a', 1);
        var (host, _) = Show(presenter, columns: 3, rows: 1);
        using var _1 = host;

        Assert.Equal("Sa…", host.GetRowText(0).TrimEnd());

        Assert.False(host.GetCell(0, 0).Style.Attributes.HasFlag(TextAttributes.Underline)); // 'S'
        Assert.True(host.GetCell(1, 0).Style.Attributes.HasFlag(TextAttributes.Underline));  // 'a' — the cue
        Assert.True(host.GetCell(2, 0).Style.Attributes.HasFlag(TextAttributes.Underline));  // '…' joins the cue run
    }
}
