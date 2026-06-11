using System.Diagnostics.CodeAnalysis;

using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.LayoutMatrix;

// ReSharper disable InconsistentNaming

/// <summary>
/// Layout matrix §6 — StackPanel (L95–L106). Vertical orientation (the default) unless noted;
/// panel arranged 20×10 via <c>Root</c>.
/// </summary>
[SuppressMessage("ReSharper", "UnusedTupleComponentInReturnValue")]
public class Section06_StackPanel
{
    private const int U = LayoutMath.Unbounded;

    private static (LayoutManager Manager, StackPanel Panel) Rooted(StackPanel panel, int columns = 20, int rows = 10)
    {
        var manager = LayoutFixture.CreateRoot(panel);
        manager.Layout(columns, rows);
        return (manager, panel);
    }

    [Fact]
    public void L95_Vertical_MainAxisSums_CrossAxisMaxes()
    {
        var panel = new StackPanel();
        panel.Children.Add(new Probe(5, 2));
        panel.Children.Add(new Probe(8, 3));
        Rooted(panel);

        Assert.Equal(new Size(8, 5), panel.DesiredSize);
    }

    [Fact]
    public void L96_Children_MeasuredUnboundedOnStackingAxis()
    {
        var panel = new StackPanel();
        var a = new Probe(5, 2);
        var b = new Probe(8, 3);
        panel.Children.Add(a);
        panel.Children.Add(b);
        Rooted(panel);

        Assert.Equal(new Size(20, U), a.MeasureConstraints[^1]);
        Assert.Equal(new Size(20, U), b.MeasureConstraints[^1]);
    }

    [Fact]
    public void L97_Vertical_SequentialStacking_FullWidthCrossSlots()
    {
        var panel = new StackPanel();
        var a = new Probe(5, 2);
        var b = new Probe(8, 3);
        panel.Children.Add(a);
        panel.Children.Add(b);
        Rooted(panel);

        Assert.Equal(new Rect(0, 0, 20, 2), a.Bounds);
        Assert.Equal(new Rect(0, 2, 20, 3), b.Bounds);
    }

    [Fact]
    public void L98_Horizontal_TransposedStacking()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var a = new Probe(5, 2);
        var b = new Probe(8, 3);
        panel.Children.Add(a);
        panel.Children.Add(b);
        Rooted(panel);

        Assert.Equal(new Size(13, 3), panel.DesiredSize);
        Assert.Equal(new Rect(0, 0, 5, 10), a.Bounds);
        Assert.Equal(new Rect(5, 0, 8, 10), b.Bounds);
    }

    [Fact]
    public void L99_Spacing_BetweenConsecutiveChildren()
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new Probe(5, 1));
        panel.Children.Add(new Probe(5, 1));
        panel.Children.Add(new Probe(5, 1));
        Rooted(panel);

        Assert.Equal(7, panel.DesiredSize.Rows); // 1+2+1+2+1
    }

    [Fact]
    public void L100_Spacing_CollapsedChild_NoGap()
    {
        var panel = new StackPanel { Spacing = 2 };
        var a = new Probe(5, 1);
        var b = new Probe(5, 1) { Visibility = Visibility.Collapsed };
        var c = new Probe(5, 1);
        panel.Children.Add(a);
        panel.Children.Add(b);
        panel.Children.Add(c);
        Rooted(panel);

        Assert.Equal(4, panel.DesiredSize.Rows); // LD7
        Assert.Equal(0, a.Bounds.Row);
        Assert.Equal(3, c.Bounds.Row);
    }

    [Fact]
    public void L101_ChildrenExceedConstraint_DesiredUnclipped_ChildrenArrangedPastExtent()
    {
        var panel = new StackPanel();
        var a = new Probe(5, 6);
        var b = new Probe(5, 6);
        var c = new Probe(5, 3);
        panel.Children.Add(a);
        panel.Children.Add(b);
        panel.Children.Add(c);
        Rooted(panel);

        Assert.Equal(15, panel.DesiredSize.Rows); // exceeds the 10-row constraint (LD2)
        Assert.Equal(12, c.Bounds.Row);           // past the panel's own extent; clip is the zone edge's job
    }

    [Fact]
    public void L102_ChildWiderThanPanel_CrossSlotIsChildDesired_Overflows()
    {
        var panel = new StackPanel();
        var child = new Probe(25, 2);
        panel.Children.Add(child);
        Rooted(panel);

        Assert.Equal(new Rect(0, 0, 25, 2), child.Bounds); // LD17 — max(20, 25)
    }

    [Fact]
    public void L103_ChildAlignsItself_InsideTheFullWidthSlot()
    {
        var panel = new StackPanel();
        var child = new Probe(5, 2) { HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(child);
        Rooted(panel);

        Assert.Equal(new Rect(0, 0, 5, 2), child.Bounds);
    }

    [Fact]
    public void L104_OrientationAndSpacing_AffectMeasure()
    {
        var panel = new StackPanel();
        panel.Children.Add(new Probe(5, 2));
        var (manager, _) = Rooted(panel);

        panel.Orientation = Orientation.Horizontal;
        Assert.False(panel.IsMeasureValid);
        manager.Layout(20, 10);

        panel.Spacing = 3;
        Assert.False(panel.IsMeasureValid);
        manager.Layout(20, 10);
        Assert.True(panel.IsMeasureValid);
    }

    [Fact]
    public void L105_Spacing_SingleChildOrZeroChildren_NoGaps()
    {
        var single = new StackPanel { Spacing = 2 };
        single.Children.Add(new Probe(5, 3));
        Rooted(single);
        Assert.Equal(new Size(5, 3), single.DesiredSize);

        var empty = new StackPanel { Spacing = 2 };
        Rooted(empty);
        Assert.Equal(Size.Empty, empty.DesiredSize);
    }

    [Fact]
    public void L106_EmptyPanel_ZeroDesired()
    {
        var panel = new StackPanel();
        Rooted(panel);
        Assert.Equal(Size.Empty, panel.DesiredSize);
    }

    [Fact]
    public void Regression_NegativeSpacing_CoercesToZero_ArrangeStaysNonNegative()
    {
        // A negative gap larger than a child extent would push the running arrange offset
        // negative, and Rect's ushort backing would silently wrap it to row ~65533. The property
        // coerces (the ItemWidth/Margin precedent) so panel-built slot rects stay non-negative.
        var panel = new StackPanel { Spacing = -5 };
        var a = new Probe(5, 2);
        var b = new Probe(5, 2);
        panel.Children.Add(a);
        panel.Children.Add(b);
        Rooted(panel);

        Assert.Equal(0, panel.Spacing); // coerced at set time
        Assert.Equal(new Size(5, 4), panel.DesiredSize);
        Assert.Equal(0, a.Bounds.Row);
        Assert.Equal(2, b.Bounds.Row); // not 65533
    }
}
