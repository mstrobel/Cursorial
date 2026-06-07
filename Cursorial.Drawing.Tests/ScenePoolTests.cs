using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Drawing;

public class ScenePoolTests
{
    [Fact]
    public void Rent_AfterReturn_ReusesBuffer_ClearedToTransparent()
    {
        var pool = new ScenePool();

        // Rent, paint it red, return it to the pool.
        var first = pool.Rent(2, 1);
        first.Draw(ctx => ctx.FillRectangle(first.Bounds, Brush.Solid(Color.FromRgb(255, 0, 0))));
        first.Dispose();

        // Rent again at the same size → the recycled buffer must come back TRANSPARENT, not red.
        var second = pool.Rent(2, 1);

        var buffer = new CellBuffer(2, 1);
        var view = buffer.AsView();
        new SceneCompositor(Style.Default.WithBackground(Color.FromRgb(0, 0, 255)))
            .Composite(new[] { new SceneLayer(second) }, view);

        // The reused (undrawn) scene is transparent, so the blue base shows — the old red is gone.
        Assert.Equal(Color.FromRgb(0, 0, 255), buffer[0, 0].Style.Background);
    }

    [Fact]
    public void Rent_DifferentSize_ResizesRecycledBuffer()
    {
        var pool = new ScenePool();

        var first = pool.Rent(2, 1);
        first.Dispose();

        var second = pool.Rent(5, 3);
        Assert.Equal(5, second.Columns);
        Assert.Equal(3, second.Rows);
    }
}
