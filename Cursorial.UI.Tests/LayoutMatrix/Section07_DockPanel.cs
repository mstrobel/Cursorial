using Cursorial.Rendering;
using Cursorial.UI;

namespace Cursorial.Tests.UI.LayoutMatrix;

// ReSharper disable InconsistentNaming

/// <summary>
/// Layout matrix §7 — DockPanel (L107–L118). Panel arranged 20×10 via <c>Root</c>;
/// <c>LastChildFill</c> default true; child sizes are desired sizes (<c>Probe</c>).
/// </summary>
public class Section07_DockPanel
{
    private static LayoutManager Rooted(DockPanel panel, int columns = 20, int rows = 10)
    {
        var manager = LayoutFixture.CreateRoot(panel);
        manager.Layout(columns, rows);
        return manager;
    }

    [Fact]
    public void L107_DockLeft_FillsCrossAxis_LastChildFills()
    {
        var panel = new DockPanel();
        var a = new Probe(5, 3);
        var b = new Probe(2, 2);
        DockPanel.SetDock(a, Dock.Left);
        panel.Children.Add(a);
        panel.Children.Add(b);
        Rooted(panel);

        Assert.Equal(new Rect(0, 0, 5, 10), a.Bounds);
        Assert.Equal(new Rect(5, 0, 15, 10), b.Bounds);
    }

    [Fact]
    public void L108_DockTop()
    {
        var panel = new DockPanel();
        var a = new Probe(5, 3);
        var b = new Probe(2, 2);
        DockPanel.SetDock(a, Dock.Top);
        panel.Children.Add(a);
        panel.Children.Add(b);
        Rooted(panel);

        Assert.Equal(new Rect(0, 0, 20, 3), a.Bounds);
        Assert.Equal(new Rect(0, 3, 20, 7), b.Bounds);
    }

    [Fact]
    public void L109_DockOrder_IsChildOrder()
    {
        var panel = new DockPanel();
        var a = new Probe(5, 3);
        var b = new Probe(3, 2);
        var c = new Probe(2, 2);
        DockPanel.SetDock(a, Dock.Left);
        DockPanel.SetDock(b, Dock.Top);
        panel.Children.Add(a);
        panel.Children.Add(b);
        panel.Children.Add(c);
        Rooted(panel);

        Assert.Equal(new Rect(0, 0, 5, 10), a.Bounds);
        Assert.Equal(new Rect(5, 0, 15, 2), b.Bounds);
        Assert.Equal(new Rect(5, 2, 15, 8), c.Bounds);
    }

    [Fact]
    public void L110_DockRight()
    {
        var panel = new DockPanel();
        var a = new Probe(4, 2);
        var b = new Probe(2, 2);
        DockPanel.SetDock(a, Dock.Right);
        panel.Children.Add(a);
        panel.Children.Add(b);
        Rooted(panel);

        Assert.Equal(new Rect(16, 0, 4, 10), a.Bounds);
        Assert.Equal(new Rect(0, 0, 16, 10), b.Bounds);
    }

    [Fact]
    public void L111_DockBottom()
    {
        var panel = new DockPanel();
        var a = new Probe(5, 3);
        var b = new Probe(2, 2);
        DockPanel.SetDock(a, Dock.Bottom);
        panel.Children.Add(a);
        panel.Children.Add(b);
        Rooted(panel);

        Assert.Equal(new Rect(0, 7, 20, 3), a.Bounds);
        Assert.Equal(new Rect(0, 0, 20, 7), b.Bounds);
    }

    [Fact]
    public void L112_DockDefault_IsLeft()
    {
        var panel = new DockPanel();
        var a = new Probe(5, 3); // Dock never set
        var b = new Probe(2, 2);
        panel.Children.Add(a);
        panel.Children.Add(b);
        Rooted(panel);

        Assert.Equal(Dock.Left, DockPanel.GetDock(a));
        Assert.Equal(new Rect(0, 0, 5, 10), a.Bounds);
    }

    [Fact]
    public void L113_LastChildFillFalse_SingleChild_DocksPerItsOwnDock()
    {
        var panel = new DockPanel { LastChildFill = false };
        var a = new Probe(5, 3);
        panel.Children.Add(a);
        Rooted(panel);

        Assert.Equal(new Rect(0, 0, 5, 10), a.Bounds); // LD15 — docks Left, no fill
    }

    [Fact]
    public void L114_Exhaustion_DockedSlotIsDesired_RemainderClampsToZero()
    {
        var panel = new DockPanel();
        var a = new Probe(6, 2);
        var b = new Probe(6, 2);
        var c = new Probe(2, 2);
        DockPanel.SetDock(a, Dock.Left);
        DockPanel.SetDock(b, Dock.Left);
        panel.Children.Add(a);
        panel.Children.Add(b);
        panel.Children.Add(c);
        Rooted(panel, 10, 10);

        Assert.Equal(new Rect(0, 0, 6, 10), a.Bounds);
        Assert.Equal(new Rect(6, 0, 6, 10), b.Bounds);  // slot = desired even past exhaustion (LD15)
        Assert.Equal(new Rect(12, 0, 0, 10), c.Bounds); // remainder clamps to 0 — no negative rects
    }

    [Fact]
    public void L115_Constraint_MinusAccumulatedDockUsage()
    {
        var panel = new DockPanel();
        var a = new Probe(5, 3);
        var b = new Probe(2, 2);
        DockPanel.SetDock(a, Dock.Left);
        panel.Children.Add(a);
        panel.Children.Add(b);
        Rooted(panel);

        Assert.Equal(new Size(15, 10), b.MeasureConstraints[^1]);
    }

    [Fact]
    public void L116_Desired_AccumulatesDockAxis_MaxesPerpendicular()
    {
        var panel = new DockPanel();
        var a = new Probe(5, 3);
        var b = new Probe(7, 4);
        DockPanel.SetDock(a, Dock.Left);
        panel.Children.Add(a);
        panel.Children.Add(b); // last child; default dock Left
        Rooted(panel);

        Assert.Equal(new Size(12, 4), panel.DesiredSize); // 5+7 wide; max(3, 4) tall
    }

    [Fact]
    public void L117_CollapsedDockedChild_ConsumesNoEdge()
    {
        var panel = new DockPanel();
        var a = new Probe(5, 3) { Visibility = Visibility.Collapsed };
        var b = new Probe(2, 2);
        DockPanel.SetDock(a, Dock.Left);
        panel.Children.Add(a);
        panel.Children.Add(b);
        Rooted(panel);

        Assert.Equal(new Rect(0, 0, 20, 10), b.Bounds);
    }

    [Fact]
    public void L118_SetDock_RedocksEndToEnd()
    {
        var panel = new DockPanel();
        var a = new Probe(4, 2);
        var b = new Probe(2, 2);
        DockPanel.SetDock(a, Dock.Left);
        panel.Children.Add(a);
        panel.Children.Add(b);
        var manager = Rooted(panel);
        Assert.Equal(0, a.Bounds.Column);

        DockPanel.SetDock(a, Dock.Right);
        manager.Layout(20, 10);

        Assert.Equal(new Rect(16, 0, 4, 10), a.Bounds); // the AffectsParentMeasure route, end-to-end
    }
}
