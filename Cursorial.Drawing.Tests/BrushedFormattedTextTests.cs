using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;

namespace Cursorial.Tests.Drawing;

// Phase 6a.1: DrawFormattedText colors a laid-out document's text with a brush sampled per cell against
// each BLOCK's rect (block-scoped 2-D). These pin that the gradient spans the block (across wrapped lines
// and across width), not each line/run independently.
public class BrushedFormattedTextTests
{
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);

    // "aaaa bbbb cccc dddd" word-wrapped at width 9 → two lines: "aaaa bbbb" / "cccc dddd".
    private static FormattedText Wrapped() =>
        new TextFormatter().Format(new RichTextBuilder().Run("aaaa bbbb cccc dddd").Build(), 9);

    [Fact]
    public void WrapsToOneParagraphTwoLines()
    {
        var ft = Wrapped();
        Assert.Single(ft.Blocks);
        var paragraph = Assert.IsType<FormattedParagraph>(ft.Blocks[0]);
        Assert.Equal(2, paragraph.Lines.Length);
    }

    [Fact]
    public void VerticalGradient_SpansTheBlock_AcrossWrappedLines()
    {
        var b = DrawHarness.Render(12, 4, ctx =>
            ctx.DrawFormattedText(Wrapped(), new Rect(0, 0, 12, 4),
                new LinearGradientBrush(Red, Blue, startPoint: RelativePoint.Top, endPoint: RelativePoint.Bottom),
                OutputCapabilities.None));

        // 'a' is the first glyph of line 0 (top of block); 'c' the first glyph of line 1 (bottom).
        Assert.Equal("a", b[0, 0].Grapheme);
        Assert.Equal("c", b[0, 1].Grapheme);

        var top = b[0, 0].Style.Foreground;
        var bottom = b[0, 1].Style.Foreground;
        Assert.True(top.Red > top.Blue, $"top should be red-dominant, was {top}");
        Assert.True(bottom.Blue > bottom.Red, $"bottom should be blue-dominant, was {bottom}");
    }

    [Fact]
    public void HorizontalGradient_SpansTheBlockWidth()
    {
        var b = DrawHarness.Render(12, 4, ctx =>
            ctx.DrawFormattedText(Wrapped(), new Rect(0, 0, 12, 4),
                new LinearGradientBrush(Red, Blue, startPoint: RelativePoint.Left, endPoint: RelativePoint.Right),
                OutputCapabilities.None));

        // Line 0 is "aaaa bbbb": col 0 = first 'a' (left edge), col 8 = last 'b' (right edge).
        Assert.Equal("a", b[0, 0].Grapheme);
        Assert.Equal("b", b[8, 0].Grapheme);

        var left = b[0, 0].Style.Foreground;
        var right = b[8, 0].Style.Foreground;
        Assert.True(left.Red > left.Blue, $"left should be red-dominant, was {left}");
        Assert.True(right.Blue > right.Red, $"right should be blue-dominant, was {right}");
    }

    [Fact]
    public void NullArguments_Throw()
    {
        DrawHarness.Render(4, 2, ctx =>
        {
            Assert.Throws<ArgumentNullException>(() =>
                ctx.DrawFormattedText(null!, new Rect(0, 0, 4, 2), new SolidColorBrush(Red), OutputCapabilities.None));
            Assert.Throws<ArgumentNullException>(() =>
                ctx.DrawFormattedText(Wrapped(), new Rect(0, 0, 4, 2), null!, OutputCapabilities.None));
        });
    }
}
