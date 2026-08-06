using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Drawing;

// DrawingContext.Blit must honor the ambient translate + clip like every other draw primitive.
// It originally wrote straight to the scene surface, so any caller that was not a render-boundary
// element (which renders at a zero translate) silently blitted onto the zone's origin.
public class DrawingContextBlitTests
{
    private static CellBuffer Filled(int columns, int rows, string glyph)
    {
        var buffer = new CellBuffer(columns, rows);
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
            buffer.Set(column, row, glyph, Style.Default);
        return buffer;
    }

    private static string Map(CellBuffer buffer)
    {
        var map = new System.Text.StringBuilder();
        for (int row = 0; row < buffer.Rows; row++)
        {
            for (int column = 0; column < buffer.Columns; column++)
                map.Append(buffer[column, row].Grapheme is { Length: > 0 } g ? g[0] : '.');
            if (row + 1 < buffer.Rows) map.Append('/');
        }
        return map.ToString();
    }

    [Fact]
    public void Blit_HonorsTheAmbientTranslate()
    {
        var source = Filled(2, 2, "X");
        var buffer = DrawHarness.Render(6, 4, context =>
        {
            using var _ = context.PushTranslate(3, 1);
            context.Blit(source.View(new Rect(0, 0, 2, 2)), new Rect(0, 0, 2, 2));
        });

        Assert.Equal("....../...XX./...XX./......", Map(buffer));
    }

    [Fact]
    public void Blit_HonorsTheAmbientClip()
    {
        var source = Filled(4, 4, "X");
        var buffer = DrawHarness.Render(6, 4, context =>
        {
            using var _ = context.PushClip(new Rect(1, 1, 2, 2));
            context.Blit(source.View(new Rect(0, 0, 4, 4)), new Rect(0, 0, 4, 4));
        });

        Assert.Equal("....../.XX.../.XX.../......", Map(buffer));
    }

    [Fact]
    public void Blit_TranslateAndClipCompose()
    {
        var source = Filled(3, 3, "X");
        var buffer = DrawHarness.Render(8, 5, context =>
        {
            using var _ = context.Push(new Rect(2, 1, 3, 2), translateColumns: 2, translateRows: 1);
            context.Blit(source.View(new Rect(0, 0, 3, 3)), new Rect(0, 0, 3, 3));
        });

        // The clip maps to scene (2,1)-(5,3); the translate puts the source at scene (2,1), so the
        // visible result is exactly the clip rectangle.
        Assert.Equal("......../..XXX.../..XXX.../......../........", Map(buffer));
    }

    [Fact]
    public void Blit_AtTheOrigin_IsUnchanged()
    {
        // The path ChartPresenter uses (boundary element, zero translate, origin destination).
        var source = Filled(2, 2, "X");
        var buffer = DrawHarness.Render(4, 3, context =>
            context.Blit(source.View(new Rect(0, 0, 2, 2)), new Rect(0, 0, 2, 2)));

        Assert.Equal("XX../XX../....", Map(buffer));
    }
}
