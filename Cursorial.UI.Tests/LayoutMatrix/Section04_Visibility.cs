using Cursorial.Rendering;
using Cursorial.UI;

namespace Cursorial.Tests.UI.LayoutMatrix;

// ReSharper disable InconsistentNaming

/// <summary>Layout matrix §4 — Visibility (L61–L70).</summary>
public class Section04_Visibility
{
    [Fact]
    public void L61_Collapsed_Measure_ZeroDesired_NoOverride_NoApplyTemplate()
    {
        var probe = new Probe(5, 3) { Visibility = Visibility.Collapsed };
        probe.Measure(new Size(20, 20));

        Assert.Equal(Size.Empty, probe.DesiredSize);
        Assert.Equal(0, probe.MeasureOverrideCount);
        Assert.Equal(0, probe.ApplyTemplateCount); // the early-out precedes ApplyTemplate
    }

    [Fact]
    public void L62_Collapsed_Arrange_EmptyBounds_NoOverride_ArrangeValid()
    {
        var probe = new Probe(5, 3) { Visibility = Visibility.Collapsed };
        probe.Measure(new Size(20, 20));
        probe.Arrange(new Rect(0, 0, 10, 5));

        Assert.Equal(Rect.Empty, probe.Bounds);
        Assert.Equal(0, probe.ArrangeOverrideCount);
        Assert.True(probe.IsArrangeValid);
    }

    [Fact]
    public void L63_CollapsedToVisible_InvalidatesChildAndParentMeasure()
    {
        var panel = new StackPanel();
        var child = new Probe(5, 3) { Visibility = Visibility.Collapsed };
        panel.Children.Add(child);
        var manager = LayoutFixture.CreateRoot(panel);
        manager.Layout(20, 10);
        Assert.True(panel.IsMeasureValid);

        child.Visibility = Visibility.Visible;

        Assert.False(child.IsMeasureValid);
        Assert.False(panel.IsMeasureValid); // LD5: space reclaimed ⇒ parent measure too

        manager.Layout(20, 10);
        Assert.True(child.MeasureOverrideCount > 0);
        Assert.Equal(new Rect(0, 0, 20, 3), child.Bounds); // measured/arranged normally
    }

    [Fact]
    public void L64_CollapsingASibling_ParentReflows()
    {
        var panel = new StackPanel();
        var a = new Probe(5, 3);
        var b = new Probe(5, 2);
        panel.Children.Add(a);
        panel.Children.Add(b);
        var manager = LayoutFixture.CreateRoot(panel);
        manager.Layout(20, 10);
        Assert.Equal(new Size(5, 5), panel.DesiredSize);
        Assert.Equal(3, b.Bounds.Row);

        a.Visibility = Visibility.Collapsed;
        manager.Layout(20, 10);

        Assert.Equal(0, b.Bounds.Row);            // B moves up to row 0
        Assert.Equal(new Size(5, 2), panel.DesiredSize); // SP desired shrinks by A's height
    }

    [Fact]
    public void L65_Hidden_MeasuresAndArrangesExactlyAsVisible()
    {
        var visible = new Probe(5, 3);
        var visibleManager = LayoutFixture.CreateRoot(visible);
        visibleManager.Layout(20, 10);

        var hidden = new Probe(5, 3) { Visibility = Visibility.Hidden };
        var hiddenManager = LayoutFixture.CreateRoot(hidden);
        hiddenManager.Layout(20, 10);

        Assert.Equal(visible.DesiredSize, hidden.DesiredSize);
        Assert.Equal(visible.Bounds, hidden.Bounds); // occupies space
    }

    [Fact]
    public void L66_VisibleHiddenFlips_RenderSideOnly_NoLayoutWork()
    {
        var probe = new Probe(5, 3);
        var manager = LayoutFixture.CreateRoot(probe);
        manager.Layout(20, 10);

        probe.Visibility = Visibility.Hidden;
        Assert.True(probe.IsMeasureValid);
        Assert.True(probe.IsArrangeValid);
        Assert.False(manager.HasPendingWork);

        probe.Visibility = Visibility.Visible;
        Assert.True(probe.IsMeasureValid);
        Assert.True(probe.IsArrangeValid);
        Assert.False(manager.HasPendingWork);
    }

    [Fact]
    public void L67_IsEffectivelyVisible_ReflectsAncestors()
    {
        var parent = new Host();
        var child = new Probe(5, 3);
        parent.Add(child);

        parent.Visibility = Visibility.Hidden;
        Assert.Equal(Visibility.Visible, child.Visibility); // own visibility unchanged
        Assert.False(child.IsEffectivelyVisible);

        parent.Visibility = Visibility.Visible;
        Assert.True(child.IsEffectivelyVisible);
    }

    [Fact]
    public void L68_StackPanel_Spacing_NoGapForCollapsedChild()
    {
        var panel = new StackPanel { Spacing = 2 };
        var a = new Probe(5, 1);
        var b = new Probe(5, 1) { Visibility = Visibility.Collapsed };
        var c = new Probe(5, 1);
        panel.Children.Add(a);
        panel.Children.Add(b);
        panel.Children.Add(c);
        var manager = LayoutFixture.CreateRoot(panel);
        manager.Layout(20, 10);

        Assert.Equal(4, panel.DesiredSize.Rows); // 1 + 2 + 1 (LD7)
        Assert.Equal(0, a.Bounds.Row);
        Assert.Equal(3, c.Bounds.Row);
    }

    [Fact]
    public void L69_StackPanel_Spacing_HiddenKeepsSpaceAndGaps()
    {
        var panel = new StackPanel { Spacing = 2 };
        var a = new Probe(5, 1);
        var b = new Probe(5, 1) { Visibility = Visibility.Hidden };
        var c = new Probe(5, 1);
        panel.Children.Add(a);
        panel.Children.Add(b);
        panel.Children.Add(c);
        var manager = LayoutFixture.CreateRoot(panel);
        manager.Layout(20, 10);

        Assert.Equal(7, panel.DesiredSize.Rows); // 1+2+1+2+1
        Assert.Equal(0, a.Bounds.Row);
        Assert.Equal(3, b.Bounds.Row);
        Assert.Equal(6, c.Bounds.Row);
    }

    [Fact]
    public void L70_ApplyTemplate_FirstCalledOnFirstNonCollapsedMeasure()
    {
        var probe = new Probe(5, 3) { Visibility = Visibility.Collapsed };
        var manager = LayoutFixture.CreateRoot(probe);
        manager.Layout(20, 10);
        manager.Layout(20, 10);
        manager.Layout(20, 10);
        Assert.Equal(0, probe.ApplyTemplateCount);

        probe.Visibility = Visibility.Visible;
        manager.Layout(20, 10);
        Assert.Equal(1, probe.ApplyTemplateCount); // template/name scope materialize late (doc §5.3)
    }
}
