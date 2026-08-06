using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Fonts;

namespace Cursorial.Tests.Rendering;

public class MonospaceFontTests
{
    [Fact]
    public void Measure_EmptyText_ReturnsEmpty()
    {
        Assert.Equal(Size.Empty, MonospaceFont.Default.Measure(""));
    }

    [Fact]
    public void Measure_NarrowAscii_ReturnsCharCount()
    {
        Assert.Equal(new Size(5, 1), MonospaceFont.Default.Measure("hello"));
    }

    [Fact]
    public void Measure_WideClusters_CountsTwoColumnsEach()
    {
        Assert.Equal(new Size(4, 1), MonospaceFont.Default.Measure("中国"));
    }

    [Fact]
    public void Measure_EmojiZwjFamily_TreatedAsSingleCluster()
    {
        // 👨‍👩‍👧 — man + zwj + woman + zwj + girl, all emoji presentation. One grapheme, two cells.
        Assert.Equal(new Size(2, 1), MonospaceFont.Default.Measure("\U0001F468‍\U0001F469‍\U0001F467"));
    }

    [Fact]
    public void Paint_AsciiAtAnchor_LaysDownCells()
    {
        var buffer = new CellBuffer(10, 1);
        var painted = MonospaceFont.Default.Paint(buffer, 2, 0, "hi", CellStyle.Default);

        Assert.Equal(new Size(2, 1), painted);
        Assert.Equal("h", buffer[2, 0].Grapheme);
        Assert.Equal("i", buffer[3, 0].Grapheme);
    }

    [Fact]
    public void Paint_AtRightEdge_ClipsRatherThanThrows()
    {
        var buffer = new CellBuffer(5, 1);
        var painted = MonospaceFont.Default.Paint(buffer, 3, 0, "abcdef", CellStyle.Default);

        // Only 2 cells fit at column 3 in a 5-wide buffer (cols 3, 4).
        Assert.Equal(new Size(2, 1), painted);
    }

    [Fact]
    public void Paint_OutOfBoundsAnchor_PaintsNothing()
    {
        var buffer = new CellBuffer(5, 1);
        Assert.Equal(Size.Empty, MonospaceFont.Default.Paint(buffer, 0, 5, "x", CellStyle.Default));
        Assert.Equal(Size.Empty, MonospaceFont.Default.Paint(buffer, 5, 0, "x", CellStyle.Default));
    }

    [Fact]
    public void Paint_RespectsBlendingMode()
    {
        // Pushing Multiply against a red backdrop with green source should produce yellow.
        // (Both treated as opaque; alpha=255 short-circuits to the mode's blend.)
        var buffer = new CellBuffer(2, 1);
        buffer.Set(0, 0, " ", CellStyle.Default.WithBackground(Color.FromRgb(255, 0, 0)));

        buffer.PushBlendingMode(BlendingModes.Plus);
        try
        {
            MonospaceFont.Default.Paint(buffer, 0, 0,
                "x", CellStyle.Default.WithBackground(Color.FromRgb(0, 255, 0)));
        }
        finally { buffer.PopBlendingMode(); }

        // Plus sums channels and clamps — red + green = yellow.
        Assert.Equal(Color.FromRgb(255, 255, 0), buffer[0, 0].Style.Background);
    }
}
