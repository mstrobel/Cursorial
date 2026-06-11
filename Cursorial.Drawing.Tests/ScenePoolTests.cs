using Cursorial.Drawing;
using Cursorial.Drawing.Media;
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
        // ReSharper disable once AccessToDisposedClosure
        first.Draw(ctx => ctx.FillRectangle(first.Bounds, new SolidColorBrush(Color.FromRgb(255, 0, 0))));
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

    [Fact]
    public void DoubleDispose_DoesNotAliasBufferAcrossRents()
    {
        // P1-3: a double dispose must not push the buffer twice, or two later rents would hand the
        // same backing buffer to two callers (silent aliasing).
        var pool = new ScenePool();

        var a = pool.Rent(1, 1);
        a.Dispose();
        a.Dispose();   // idempotent

        var b = pool.Rent(1, 1);
        b.Draw(ctx => ctx.FillRectangle(b.Bounds, new SolidColorBrush(Color.FromRgb(255, 0, 0))));
        var c = pool.Rent(1, 1);
        c.Draw(ctx => ctx.FillRectangle(c.Bounds, new SolidColorBrush(Color.FromRgb(0, 0, 255))));

        // If b and c aliased one buffer, c's blue would have overwritten b. Composite b → still red.
        var buffer = new CellBuffer(1, 1);
        new SceneCompositor(Style.Default.WithBackground(Color.FromRgb(0, 0, 0)))
            .Composite(new[] { new SceneLayer(b) }, buffer.AsView());
        Assert.Equal(Color.FromRgb(255, 0, 0), buffer[0, 0].Style.Background);
    }

    [Fact]
    public void Draw_AfterDispose_Throws()
    {
        var pool = new ScenePool();
        var scene = pool.Rent(1, 1);
        scene.Dispose();

        Assert.Throws<ObjectDisposedException>(() => scene.Draw(_ => { }));
    }
}
