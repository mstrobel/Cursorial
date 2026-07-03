using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Drawing;

// DrawText line-break contract (drawing design doc §13.1): \r\n | \n | \r break to the original
// start column one row down; empty lines consume a row; the brush samples the full multi-line
// extent; tab → one space + DEBUG diagnostic; other C0/C1 controls skipped + DEBUG diagnostic;
// returns the bounding Size (widest line's advance × line count).
public class DrawTextLineBreakTests
{
    private static readonly Color White = Color.FromRgb(255, 255, 255);
    private static readonly Color Black = Color.FromRgb(0, 0, 0);

    [Fact]
    public void MultiLine_ContinuesAtOriginalStartColumn()
    {
        Size size = default;
        var b = DrawHarness.Render(8, 4, ctx => size = ctx.DrawText(2, 1, "ab\ncd", White));

        Assert.Equal(new Size(2, 2), size);
        Assert.Equal("a", b[2, 1].Grapheme);
        Assert.Equal("b", b[3, 1].Grapheme);
        Assert.Equal("c", b[2, 2].Grapheme);   // continuation at the original start column
        Assert.Equal("d", b[3, 2].Grapheme);
    }

    [Theory]
    [InlineData("ab\ncd")]
    [InlineData("ab\r\ncd")]
    [InlineData("ab\rcd")]
    public void LineBreakForms_AreEquivalent(string text)
    {
        Size size = default;
        var b = DrawHarness.Render(8, 3, ctx => size = ctx.DrawText(0, 0, text, White));

        Assert.Equal(new Size(2, 2), size);
        Assert.Equal("a", b[0, 0].Grapheme);
        Assert.Equal("b", b[1, 0].Grapheme);
        Assert.Equal("c", b[0, 1].Grapheme);
        Assert.Equal("d", b[1, 1].Grapheme);
    }

    [Fact]
    public void TrailingNewline_YieldsAFinalEmptyLineThatCounts()
    {
        Size size = default;
        var b = DrawHarness.Render(6, 3, ctx => size = ctx.DrawText(0, 0, "ab\n", White));

        Assert.Equal(new Size(2, 2), size);
        Assert.Equal("a", b[0, 0].Grapheme);
        Assert.DoesNotContain("a", Enumerable.Range(0, 6).Select(c => b[c, 1].Grapheme));
        Assert.DoesNotContain("b", Enumerable.Range(0, 6).Select(c => b[c, 1].Grapheme));
    }

    [Fact]
    public void EmptyLines_ConsumeARow()
    {
        Size size = default;
        var b = DrawHarness.Render(6, 4, ctx => size = ctx.DrawText(0, 0, "a\n\nb", White));

        Assert.Equal(new Size(1, 3), size);
        Assert.Equal("a", b[0, 0].Grapheme);
        Assert.DoesNotContain("b", Enumerable.Range(0, 6).Select(c => b[c, 1].Grapheme));   // blank middle row
        Assert.Equal("b", b[0, 2].Grapheme);
    }

    [Fact]
    public void ReturnSize_IsWidestLineByLineCount()
    {
        Size size = default;
        DrawHarness.Render(10, 4, ctx => size = ctx.DrawText(0, 0, "x\nabcd\nyz", White));

        Assert.Equal(new Size(4, 3), size);
    }

    [Fact]
    public void EmptyText_ReturnsEmptySize()
    {
        Size size = new(9, 9);
        DrawHarness.Render(4, 2, ctx => size = ctx.DrawText(0, 0, "", White));

        Assert.Equal(Size.Empty, size);
    }

    [Fact]
    public void Brush_SamplesTheFullMultiLineExtent()
    {
        // Vertical black→white gradient over a 1×2 extent samples 0.25 / 0.75 per row (64 / 191) —
        // if each line restarted its own bounds, both rows would sample 0.5 (127).
        var brush = new LinearGradientBrush([new(0.0, Black), new(1.0, White)],
                                            RelativePoint.TopLeft, RelativePoint.BottomLeft);

        var b = DrawHarness.Render(4, 3, ctx => ctx.DrawText(0, 0, "A\nB", brush));

        Assert.Equal("A", b[0, 0].Grapheme);
        Assert.Equal("B", b[0, 1].Grapheme);
        Assert.Equal(64, b[0, 0].Style.Foreground.Red);
        Assert.Equal(191, b[0, 1].Style.Foreground.Red);
    }

    [Fact]
    public void ClipMidBlock_SuppressesClippedRows_SizeStaysFull()
    {
        Size size = default;
        var b = DrawHarness.Render(6, 3, ctx =>
        {
            using (ctx.PushClip(new Rect(0, 0, 6, 1)))   // only row 0 visible
                size = ctx.DrawText(0, 0, "ab\ncd", White);
        });

        Assert.Equal(new Size(2, 2), size);   // local advance ignores the clip (the §11 contract, per line)
        Assert.Equal("a", b[0, 0].Grapheme);
        Assert.Equal("b", b[1, 0].Grapheme);
        Assert.DoesNotContain("c", Enumerable.Range(0, 6).Select(c => b[c, 1].Grapheme));
        Assert.DoesNotContain("d", Enumerable.Range(0, 6).Select(c => b[c, 1].Grapheme));
    }

    [Fact]
    public void Translate_AppliesPerLine()
    {
        var b = DrawHarness.Render(8, 4, ctx =>
        {
            using (ctx.PushTranslate(3, 1))
                ctx.DrawText(0, 0, "a\nb", White);
        });

        Assert.Equal("a", b[3, 1].Grapheme);
        Assert.Equal("b", b[3, 2].Grapheme);
    }

    [Fact]
    public void NoPush_EachLineClampsAtSurfaceRightEdge()
    {
        Size size = default;
        var b = DrawHarness.Render(3, 2, ctx => size = ctx.DrawText(0, 0, "abcd\nxy", White));

        Assert.Equal(new Size(3, 2), size);   // row 0 clamps at the edge: a b c, "d" dropped
        Assert.Equal("c", b[2, 0].Grapheme);
        Assert.Equal("x", b[0, 1].Grapheme);
        Assert.Equal("y", b[1, 1].Grapheme);
    }

    [Fact]
    public void NoPush_OffSurfaceRow_DrawsNothing_ButCountsTheLine()
    {
        Size size = default;
        var b = DrawHarness.Render(4, 1, ctx => size = ctx.DrawText(0, 0, "a\nb", White));

        Assert.Equal(new Size(1, 2), size);   // line count is the text's; the off-surface row advanced 0
        Assert.Equal("a", b[0, 0].Grapheme);
    }

    [Fact]
    public void NoPush_NegativeStartRow_NoThrow_SkipsOffSurfaceLines()
    {
        // Regression (P2.6 review #1): the multi-line rewrite built the brush bounds before the
        // row guard, so a negative start row threw instead of degrading gracefully (design §13.1).
        Size size = default;
        var b = DrawHarness.Render(4, 2, ctx => size = ctx.DrawText(0, -1, "a\nb", White));

        Assert.Equal(new Size(1, 2), size);   // the off-surface line advanced 0; the line count is the text's
        Assert.Equal("b", b[0, 0].Grapheme);  // line 2 lands on surface row 0
        Assert.DoesNotContain("a", Enumerable.Range(0, 4).Select(c => b[c, 0].Grapheme));
    }

    [Fact]
    public void NoPush_AllRowsNegative_NoThrow_DrawsNothing()
    {
        Size size = default;
        var b = DrawHarness.Render(4, 2, ctx => size = ctx.DrawText(0, -3, "ab", White));

        Assert.Equal(new Size(0, 1), size);   // off-surface row advances 0; the line still counts
        Assert.Null(b[0, 0].Grapheme);
    }

    [Fact]
    public void NoPush_NegativeStartColumn_NoThrow_ClipsLeftEdge()
    {
        Size size = default;
        var b = DrawHarness.Render(6, 1, ctx => size = ctx.DrawText(-2, 0, "abcd", White));

        Assert.Equal(new Size(4, 1), size);   // the advance counts from the (negative) start
        Assert.Equal("c", b[0, 0].Grapheme);  // "a"/"b" clipped left of the surface
        Assert.Equal("d", b[1, 0].Grapheme);
        Assert.Null(b[2, 0].Grapheme);
    }

    [Fact]
    public void Pushed_NegativeLocalStart_NoThrow_MapsThroughTheTranslate()
    {
        Size size = default;
        var b = DrawHarness.Render(6, 3, ctx =>
        {
            using (ctx.PushTranslate(2, 2))
                size = ctx.DrawText(0, -1, "a\nb", White);
        });

        Assert.Equal(new Size(1, 2), size);
        Assert.Equal("a", b[2, 1].Grapheme);  // local (0,−1) → scene (2,1)
        Assert.Equal("b", b[2, 2].Grapheme);
    }

    [Fact]
    public void Brush_NegativeAnchor_SamplesContractEquivalently()
    {
        // The same text drawn (A) at row 0 un-pushed and (B) at local row −1 under a +1 translate
        // lands on the same scene rows with identical bounds-relative sample positions — the
        // SampleBounds zero-origin shift must color them identically.
        var brush = new LinearGradientBrush([new(0.0, Black), new(1.0, White)],
                                            RelativePoint.TopLeft, RelativePoint.BottomLeft);

        var a = DrawHarness.Render(4, 3, ctx => ctx.DrawText(0, 0, "x\ny\nz", brush));
        var b = DrawHarness.Render(4, 3, ctx =>
        {
            using (ctx.PushTranslate(0, 1))
                ctx.DrawText(0, -1, "x\ny\nz", brush);
        });

        for (var row = 0; row < 3; row++)
        {
            Assert.Equal(a[0, row].Grapheme, b[0, row].Grapheme);
            Assert.Equal(a[0, row].Style.Foreground, b[0, row].Style.Foreground);
        }
    }

    [Fact]
    public void HugeLineCount_PastTheRectCap_NoThrow()
    {
        // 70,001 lines exceeds the ushort Rect cap (65,535); SampleBounds clamps defensively.
        var text = string.Concat(Enumerable.Repeat("x\n", 70_000));
        Size size = default;
        var b = DrawHarness.Render(4, 2, ctx => size = ctx.DrawText(0, 0, text, White));

        Assert.Equal(new Size(1, 70_001), size);   // trailing newline yields a final empty line
        Assert.Equal("x", b[0, 0].Grapheme);
        Assert.Equal("x", b[0, 1].Grapheme);
    }

    [Fact]
    public void Tab_BecomesOneSpace()
    {
        Size size = default;
        var b = DrawHarness.Render(6, 1, ctx => size = ctx.DrawText(0, 0, "a\tb", White));

        Assert.Equal(new Size(3, 1), size);
        Assert.Equal("a", b[0, 0].Grapheme);
        Assert.Equal(" ", b[1, 0].Grapheme);
        Assert.Equal("b", b[2, 0].Grapheme);
    }

    [Fact]
    public void OtherControlCharacters_AreSkipped()
    {
        Size size = default;
        var b = DrawHarness.Render(6, 1, ctx => size = ctx.DrawText(0, 0, "a\u0007b\u009Cc", White));

        Assert.Equal(new Size(3, 1), size);   // BEL (C0) and ST (C1) contribute zero columns
        Assert.Equal("a", b[0, 0].Grapheme);
        Assert.Equal("b", b[1, 0].Grapheme);
        Assert.Equal("c", b[2, 0].Grapheme);
    }

#if DEBUG
    [Fact]
    public void TabAndControls_RaiseDebugDiagnostics()
    {
        var kinds = new List<DrawingDiagnosticKind>();
        Action<DrawingDiagnosticEvent> handler = e => { lock (kinds) kinds.Add(e.Kind); };
        DrawingDiagnostics.DiagnosticRaised += handler;
        try
        {
            DrawHarness.Render(6, 1, ctx => ctx.DrawText(0, 0, "a\tb\u0007", White));
        }
        finally
        {
            DrawingDiagnostics.DiagnosticRaised -= handler;
        }

        // The channel is process-global (parallel collections can cross-wire), so assert presence only.
        lock (kinds)
        {
            Assert.Contains(DrawingDiagnosticKind.TabInText, kinds);
            Assert.Contains(DrawingDiagnosticKind.ControlCharacterInText, kinds);
        }
    }
#endif
}
