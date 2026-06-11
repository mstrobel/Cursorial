using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.LayoutMatrix;

// ReSharper disable InconsistentNaming

/// <summary>
/// Layout matrix §10 — WrapPanel (L160–L172). Horizontal orientation (the default) unless noted;
/// panel arranged via <c>Root</c> at the stated size; items are <c>Probe</c>s; line constraint =
/// the panel's main-axis constraint.
/// </summary>
public class Section10_WrapPanel
{
    private static LayoutManager Layout(WrapPanel panel, int columns, int rows)
    {
        var manager = LayoutFixture.CreateRoot(panel);
        manager.Layout(columns, rows);
        return manager;
    }

    [Fact]
    public void L160_GreedyPacking_WrapsAtLineConstraint()
    {
        var panel = new WrapPanel();
        var a = new Probe(4, 1);
        var b = new Probe(4, 1);
        var c = new Probe(4, 1);
        panel.Children.Add(a);
        panel.Children.Add(b);
        panel.Children.Add(c);
        Layout(panel, 10, 5);

        Assert.Equal(new Rect(0, 0, 4, 1), a.Bounds);
        Assert.Equal(new Rect(4, 0, 4, 1), b.Bounds);
        Assert.Equal(new Rect(0, 1, 4, 1), c.Bounds);
        Assert.Equal(new Size(8, 2), panel.DesiredSize);
    }

    [Fact]
    public void L161_ExactFit_StaysOnTheLine()
    {
        var panel = new WrapPanel();
        var a = new Probe(5, 1);
        var b = new Probe(5, 1);
        panel.Children.Add(a);
        panel.Children.Add(b);
        Layout(panel, 10, 5);

        Assert.Equal(new Rect(0, 0, 5, 1), a.Bounds);
        Assert.Equal(new Rect(5, 0, 5, 1), b.Bounds); // 5+5 is not > 10 — LD14
        Assert.Equal(new Size(10, 1), panel.DesiredSize);
    }

    [Fact]
    public void L162_EverySlotInALine_GetsTheLinesMaxCrossExtent()
    {
        var panel = new WrapPanel();
        var a = new Probe(4, 1);
        var b = new Probe(4, 3);
        panel.Children.Add(a);
        panel.Children.Add(b);
        Layout(panel, 10, 5);

        Assert.Equal(new Rect(0, 0, 4, 3), a.Bounds);
        Assert.Equal(new Rect(4, 0, 4, 3), b.Bounds);
    }

    [Fact]
    public void L163_Desired_MainIsMaxLine_CrossIsSumOfLines()
    {
        var panel = new WrapPanel();
        panel.Children.Add(new Probe(4, 1));
        panel.Children.Add(new Probe(4, 1));
        panel.Children.Add(new Probe(4, 1));
        panel.Measure(new Size(10, 5));

        Assert.Equal(new Size(8, 2), panel.DesiredSize);
    }

    [Fact]
    public void L164_ItemWidth_UniformSlots_RegardlessOfItemDesired()
    {
        var panel = new WrapPanel { ItemWidth = 6 };
        var a = new Probe(4, 1);
        var b = new Probe(9, 1);
        panel.Children.Add(a);
        panel.Children.Add(b);
        Layout(panel, 12, 5);

        Assert.Equal(new Rect(0, 0, 6, 1), a.Bounds);
        Assert.Equal(new Rect(6, 0, 6, 1), b.Bounds); // 6+6 exact fit — one line
    }

    [Fact]
    public void L165_ItemWidth_ReplacesTheConstraintPerAxis()
    {
        var panel = new WrapPanel { ItemWidth = 6 };
        var item = new Probe(4, 1);
        panel.Children.Add(item);
        Layout(panel, 12, 5);

        Assert.Equal(new Size(6, 5), item.MeasureConstraints[^1]);
    }

    [Fact]
    public void L166_ItemHeight_ClampsTheTallerItemsSlot()
    {
        var panel = new WrapPanel { ItemHeight = 2 };
        var a = new Probe(4, 1);
        var b = new Probe(4, 3);
        panel.Children.Add(a);
        panel.Children.Add(b);
        Layout(panel, 10, 6);

        Assert.Equal(new Rect(0, 0, 4, 2), a.Bounds);
        Assert.Equal(new Rect(4, 0, 4, 2), b.Bounds); // the 3-row desired arranges inside a 2-row slot per §3 rules
    }

    [Fact]
    public void L167_OversizedItem_OwnLine_SlotClampedToLineExtent()
    {
        var panel = new WrapPanel();
        var a = new Probe(4, 1);
        var b = new Probe(12, 1);
        var c = new Probe(4, 1);
        panel.Children.Add(a);
        panel.Children.Add(b);
        panel.Children.Add(c);
        Layout(panel, 10, 5);

        Assert.Equal(new Rect(0, 0, 4, 1), a.Bounds);
        Assert.Equal(new Rect(0, 1, 10, 1), b.Bounds); // clamped to the line extent — LD10
        Assert.Equal(new Rect(0, 2, 4, 1), c.Bounds);
    }

    [Fact]
    public void L168_PartialLastLine_PacksFromLineStart_NoJustification()
    {
        var panel = new WrapPanel();
        var a = new Probe(4, 1);
        var b = new Probe(4, 1);
        var c = new Probe(4, 1);
        panel.Children.Add(a);
        panel.Children.Add(b);
        panel.Children.Add(c);
        Layout(panel, 10, 5);

        Assert.Equal(new Rect(0, 1, 4, 1), c.Bounds);
    }

    [Fact]
    public void L169_CollapsedItem_SkippedEntirely()
    {
        var panel = new WrapPanel();
        var a = new Probe(4, 1);
        var b = new Probe(4, 1) { Visibility = Visibility.Collapsed };
        var c = new Probe(4, 1);
        panel.Children.Add(a);
        panel.Children.Add(b);
        panel.Children.Add(c);
        Layout(panel, 10, 5);

        Assert.Equal(new Rect(0, 0, 4, 1), a.Bounds);
        Assert.Equal(Rect.Empty, b.Bounds);
        Assert.Equal(new Rect(4, 0, 4, 1), c.Bounds); // no slot, no line participation
    }

    [Fact]
    public void L170_Vertical_FlowsDownThenWrapsToANewColumn()
    {
        var panel = new WrapPanel { Orientation = Orientation.Vertical };
        var a = new Probe(1, 3);
        var b = new Probe(1, 3);
        panel.Children.Add(a);
        panel.Children.Add(b);
        Layout(panel, 10, 4);

        Assert.Equal(new Rect(0, 0, 1, 3), a.Bounds);
        Assert.Equal(new Rect(1, 0, 1, 3), b.Bounds);
        Assert.Equal(new Size(2, 3), panel.DesiredSize); // transposed
    }

    [Fact]
    public void L171_UnboundedMainAxis_SingleLine_NoWrapping()
    {
        var panel = new WrapPanel();
        panel.Children.Add(new Probe(4, 1));
        panel.Children.Add(new Probe(4, 1));
        panel.Children.Add(new Probe(4, 1));
        panel.Measure(new Size(LayoutMath.Unbounded, LayoutMath.Unbounded));

        Assert.Equal(new Size(12, 1), panel.DesiredSize);
    }

    [Fact]
    public void L172_ItemWidth_ItemHeight_Orientation_AreAffectsMeasure_RelayoutNextPass()
    {
        var panel = new WrapPanel();
        var a = new Probe(4, 1);
        var b = new Probe(4, 1);
        var c = new Probe(4, 1);
        panel.Children.Add(a);
        panel.Children.Add(b);
        panel.Children.Add(c);
        var manager = Layout(panel, 10, 5);

        panel.ItemWidth = 5;
        Assert.False(panel.IsMeasureValid);
        manager.Layout(10, 5);
        Assert.Equal(new Rect(5, 0, 5, 1), b.Bounds); // 5+5 exact fit

        panel.ItemHeight = 2;
        Assert.False(panel.IsMeasureValid);
        manager.Layout(10, 5);
        Assert.Equal(new Rect(5, 0, 5, 2), b.Bounds);

        panel.Orientation = Orientation.Vertical;
        Assert.False(panel.IsMeasureValid);
        manager.Layout(10, 5);
        Assert.Equal(new Rect(0, 2, 5, 2), b.Bounds); // flows down: a above b, c wraps to column 2
        Assert.Equal(new Rect(5, 0, 5, 2), c.Bounds);
    }
}
