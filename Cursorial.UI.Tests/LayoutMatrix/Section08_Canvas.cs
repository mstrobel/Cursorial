using Cursorial.Rendering;
using Cursorial.UI;

namespace Cursorial.Tests.UI.LayoutMatrix;

// ReSharper disable InconsistentNaming

/// <summary>Layout matrix §8 — Canvas (L119–L129). Canvas arranged 20×10 via <c>Root</c>.</summary>
public class Section08_Canvas
{
    private const int U = LayoutMath.Unbounded;

    private static LayoutManager Rooted(Canvas canvas)
    {
        var manager = LayoutFixture.CreateRoot(canvas);
        manager.Layout(20, 10);
        return manager;
    }

    [Fact]
    public void L119_Children_MeasuredUnbounded()
    {
        var canvas = new Canvas();
        var child = new Probe(5, 4);
        canvas.Children.Add(child);
        Rooted(canvas);

        Assert.Equal(new Size(U, U), child.MeasureConstraints[^1]);
    }

    [Fact]
    public void L120_ZeroDesired_RegardlessOfChildren()
    {
        var canvas = new Canvas();
        canvas.Children.Add(new Probe(5, 4));
        canvas.Children.Add(new Probe(30, 30));
        Rooted(canvas);

        Assert.Equal(Size.Empty, canvas.DesiredSize);
    }

    [Fact]
    public void L121_LeftTop_Position()
    {
        var canvas = new Canvas();
        var child = new Probe(5, 4);
        Canvas.SetLeft(child, 3);
        Canvas.SetTop(child, 2);
        canvas.Children.Add(child);
        Rooted(canvas);

        Assert.Equal(new Rect(3, 2, 5, 4), child.Bounds);
    }

    [Fact]
    public void L122_NoAttachedValues_Origin()
    {
        var canvas = new Canvas();
        var child = new Probe(5, 4);
        canvas.Children.Add(child);
        Rooted(canvas);

        Assert.Equal(new Rect(0, 0, 5, 4), child.Bounds);
    }

    [Fact]
    public void L123_Right_PositionsFromTheRightEdge()
    {
        var canvas = new Canvas();
        var child = new Probe(5, 4);
        Canvas.SetRight(child, 2);
        canvas.Children.Add(child);
        Rooted(canvas);

        Assert.Equal(13, child.Bounds.Column); // 20 − 2 − 5
    }

    [Fact]
    public void L124_Bottom_PositionsFromTheBottomEdge()
    {
        var canvas = new Canvas();
        var child = new Probe(5, 4);
        Canvas.SetBottom(child, 1);
        canvas.Children.Add(child);
        Rooted(canvas);

        Assert.Equal(5, child.Bounds.Row); // 10 − 1 − 4
    }

    [Theory]
    [InlineData(true)]  // Left = 3 AND Right = 2
    [InlineData(false)] // Top = 2 AND Bottom = 3 (analog)
    public void L125_LeftBeatsRight_TopBeatsBottom(bool horizontal)
    {
        var canvas = new Canvas();
        var child = new Probe(5, 4);
        if (horizontal)
        {
            Canvas.SetLeft(child, 3);
            Canvas.SetRight(child, 2);
        }
        else
        {
            Canvas.SetTop(child, 2);
            Canvas.SetBottom(child, 3);
        }

        canvas.Children.Add(child);
        Rooted(canvas);

        if (horizontal)
            Assert.Equal(3, child.Bounds.Column); // LD16
        else
            Assert.Equal(2, child.Bounds.Row);
    }

    [Fact]
    public void L126_ComputedNegativePosition_ClampsToZero()
    {
        var canvas = new Canvas();
        var child = new Probe(5, 4);
        Canvas.SetRight(child, 18); // x = 20 − 18 − 5 = −3
        canvas.Children.Add(child);
        Rooted(canvas);

        Assert.Equal(0, child.Bounds.Column); // negative placement never enters layout (use RenderOffset*)
    }

    [Fact]
    public void L127_ChildExtendsPastTheCanvas_NoClip()
    {
        var canvas = new Canvas();
        var child = new Probe(5, 4);
        Canvas.SetLeft(child, 18);
        canvas.Children.Add(child);
        Rooted(canvas);

        Assert.Equal(new Rect(18, 0, 5, 4), child.Bounds); // clipping happens only at the zone/scene edge (T3)
    }

    [Fact]
    public void L128_Margin_InsideTheDesiredSizedSlot()
    {
        var canvas = new Canvas();
        var child = new Probe(5, 4) { Margin = new Margins(1, 0, 0, 0) };
        Canvas.SetLeft(child, 3);
        Canvas.SetTop(child, 2);
        canvas.Children.Add(child);
        Rooted(canvas);

        Assert.Equal(new Rect(4, 2, 5, 4), child.Bounds); // slot 6×4 at (3,2); margin offsets the content
    }

    [Fact]
    public void L129_SetLeft_ArrangeOnlyLane_NoRemeasureAnywhere()
    {
        var canvas = new Canvas();
        var child = new Probe(5, 4);
        canvas.Children.Add(child);
        var manager = Rooted(canvas);
        var childMeasures = child.MeasureOverrideCount;

        Canvas.SetLeft(child, 9);
        manager.Layout(20, 10);

        Assert.Equal(9, child.Bounds.Column);
        Assert.Equal(childMeasures, child.MeasureOverrideCount); // child mo unchanged — the cheap lane
        Assert.True(canvas.IsMeasureValid);                      // no measure work anywhere
    }
}
