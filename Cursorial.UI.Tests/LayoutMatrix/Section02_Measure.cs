using Cursorial.Rendering;
using Cursorial.UI;

namespace Cursorial.Tests.UI.LayoutMatrix;

/// <summary>Layout matrix §2 — core Measure contract (L13–L37).</summary>
public class Section02_Measure
{
    private const int U = LayoutMath.Unbounded;

    [Fact]
    public void L13_DesiredSize_IncludesMargin()
    {
        var probe = new Probe(5, 3) { Margin = new Margins(1, 1, 1, 1) };
        probe.Measure(new Size(20, 20));
        Assert.Equal(new Size(7, 5), probe.DesiredSize);
    }

    [Fact]
    public void L14_Constraint_MarginDeflated()
    {
        var probe = new Probe(5, 5) { Margin = new Margins(1, 2, 3, 4) };
        probe.Measure(new Size(20, 20));
        Assert.Equal(new Size(16, 14), Assert.Single(probe.MeasureConstraints));
        Assert.Equal(new Size(9, 11), probe.DesiredSize);
    }

    [Fact]
    public void L15_UnboundedConstraint_PropagatesThroughMarginDeflate()
    {
        var probe = new Probe(5, 5) { Margin = new Margins(2, 2, 2, 2) };
        probe.Measure(new Size(U, 10));
        Assert.Equal(new Size(U, 6), Assert.Single(probe.MeasureConstraints));
    }

    [Fact]
    public void L16_MaxWidth_CapsTheConstraint()
    {
        var probe = new Probe(5, 5) { MaxWidth = 8 };
        probe.Measure(new Size(20, 20));
        Assert.Equal(8, Assert.Single(probe.MeasureConstraints).Columns);
    }

    [Fact]
    public void L17_MinWidth_RaisesTheConstraint()
    {
        var probe = new Probe(5, 5) { MinWidth = 12 };
        probe.Measure(new Size(8, 20));
        Assert.Equal(12, Assert.Single(probe.MeasureConstraints).Columns);
    }

    [Fact]
    public void L18_ExplicitWidth_BeatsSmallerContent()
    {
        var probe = new Probe(2, 2) { Width = 6 };
        probe.Measure(new Size(20, 20));
        Assert.Equal(6, Assert.Single(probe.MeasureConstraints).Columns);
        Assert.Equal(6, probe.DesiredSize.Columns);
    }

    [Fact]
    public void L19_ExplicitWidth_BeatsLargerContent()
    {
        var probe = new Probe(9, 2) { Width = 6 };
        probe.Measure(new Size(20, 20));
        Assert.Equal(6, probe.DesiredSize.Columns);
    }

    [Fact]
    public void L20_ExplicitWidth_WinsOverAvailability_DesiredNotClipped()
    {
        var probe = new Probe(2, 2) { Width = 30 };
        probe.Measure(new Size(20, 20));
        Assert.Equal(30, Assert.Single(probe.MeasureConstraints).Columns);
        Assert.Equal(30, probe.DesiredSize.Columns);
    }

    [Fact]
    public void L21_MinBeatsMax()
    {
        var probe = new Probe(5, 5) { MinWidth = 20, MaxWidth = 10 };
        probe.Measure(new Size(40, 40));
        Assert.Equal(20, probe.DesiredSize.Columns);
    }

    [Fact]
    public void L22_MaxBeatsExplicit()
    {
        var probe = new Probe(2, 2) { Width = 50, MaxWidth = 30 };
        probe.Measure(new Size(60, 60));
        Assert.Equal(30, probe.DesiredSize.Columns);
    }

    [Fact]
    public void L23_MinBeatsExplicit()
    {
        var probe = new Probe(2, 2) { Width = 5, MinWidth = 10 };
        probe.Measure(new Size(60, 60));
        Assert.Equal(10, probe.DesiredSize.Columns);
    }

    [Fact]
    public void L24_MaxWidth_ClampsNaturalDown()
    {
        var probe = new Probe(9, 2) { MaxWidth = 6 };
        probe.Measure(new Size(20, 20));
        Assert.Equal(6, probe.DesiredSize.Columns);
    }

    [Fact]
    public void L25_MinWidth_RaisesNatural()
    {
        var probe = new Probe(2, 2) { MinWidth = 5 };
        probe.Measure(new Size(20, 20));
        Assert.Equal(5, probe.DesiredSize.Columns);
    }

    [Fact]
    public void L26_Desired_ExceedsConstraint_ByDesign()
    {
        var probe = new Probe(15, 3);
        probe.Measure(new Size(10, 10));
        Assert.Equal(new Size(15, 3), probe.DesiredSize);
    }

    [Fact]
    public void L27_Parent_SeesUnclippedDesired_OverflowIsParentPolicy()
    {
        var probe = new Probe(15, 3);
        var slot = new SlotHost(probe) { SlotRect = new Rect(0, 0, 10, 10) };
        slot.Measure(new Size(10, 10));
        Assert.Equal(new Size(15, 3), probe.DesiredSize); // the parent reads 15×3 (SCP extent depends on this)
    }

    [Fact]
    public void L28_Min_DoesNotFiniteizeUnboundedConstraint()
    {
        var probe = new Probe(2, 2) { MinWidth = 5 };
        probe.Measure(new Size(U, U));
        Assert.Equal(U, Assert.Single(probe.MeasureConstraints).Columns);
        Assert.Equal(5, probe.DesiredSize.Columns);
    }

    [Fact]
    public void L29_ConstraintCacheHit_MeasureOverrideRunsOnce()
    {
        var probe = new Probe(5, 5);
        probe.Measure(new Size(10, 10));
        probe.Measure(new Size(10, 10));
        Assert.Equal(1, probe.MeasureOverrideCount);
    }

    [Fact]
    public void L30_CacheKeysRawAvailableSize()
    {
        var probe = new Probe(5, 5);
        probe.Measure(new Size(10, 10));
        probe.Measure(new Size(12, 10));
        Assert.Equal(2, probe.MeasureOverrideCount);
    }

    [Fact]
    public void L31_InvalidateMeasure_BustsTheCache()
    {
        var probe = new Probe(5, 5);
        probe.Measure(new Size(10, 10));
        probe.InvalidateMeasure();
        probe.Measure(new Size(10, 10));
        Assert.Equal(2, probe.MeasureOverrideCount);
    }

    [Fact]
    public void L32_ApplyTemplate_BeforeMeasureOverride_SkippedOnCacheHit()
    {
        var probe = new Probe(5, 5);
        var order = new List<string>();
        probe.Log = order;
        probe.LogName = "p";

        probe.Measure(new Size(10, 10));
        Assert.Equal(1, probe.ApplyTemplateCount);
        Assert.Equal(1, probe.MeasureOverrideCount);

        probe.Measure(new Size(10, 10)); // cache hit: neither runs
        Assert.Equal(1, probe.ApplyTemplateCount);
        Assert.Equal(1, probe.MeasureOverrideCount);
    }

    [Fact]
    public void L33_ChildDesiredChange_NotifiesParent_DefaultInvalidatesParentMeasure()
    {
        var parent = new Host();
        var child = new Probe(5, 5);
        parent.Add(child);
        var manager = LayoutFixture.CreateRoot(parent);
        manager.Layout(20, 20);
        var baseline = parent.ChildDesiredSizeChangedCount; // the initial Empty→5×5 measure notified once

        child.Natural = new Size(8, 8); // the test knob
        child.InvalidateMeasure();
        child.Measure(new Size(20, 20)); // re-measure → desired changes

        Assert.Equal(baseline + 1, parent.ChildDesiredSizeChangedCount);
        Assert.Same(child, parent.LastChangedChild);
        Assert.False(parent.IsMeasureValid); // default base implementation invalidates the parent
    }

    [Fact]
    public void L34_ChildRemeasure_SameDesired_NoNotification_ParentStaysValid()
    {
        var parent = new Host();
        var child = new Probe(5, 5);
        parent.Add(child);
        var manager = LayoutFixture.CreateRoot(parent);
        manager.Layout(20, 20);
        var baseline = parent.ChildDesiredSizeChangedCount;

        child.Measure(new Size(21, 20)); // constraint-busting re-measure → same desired

        Assert.Equal(baseline, parent.ChildDesiredSizeChangedCount);
        Assert.True(parent.IsMeasureValid);
    }

    [Fact]
    public void L35_DesiredSizeProperty_DirectProperty_EqualityGatedNotification()
    {
        var probe = new Probe(5, 5);
        var observer = new LayoutFixture.RecordingObserver();
        probe.AddObserver(UIElement.DesiredSizeProperty, observer);

        probe.Measure(new Size(10, 10));
        Assert.Single(observer.Changes);
        Assert.Equal(Size.Empty, observer.Changes[0].OldValue);
        Assert.Equal(new Size(5, 5), observer.Changes[0].NewValue);

        probe.InvalidateMeasure();
        probe.Measure(new Size(10, 10)); // same desired — silent
        Assert.Single(observer.Changes);
    }

    [Fact]
    public void L36_MarginExceedsConstraint_InnerFloorsAtZero()
    {
        var fit = new Fit(3, 3) { Margin = new Margins(6, 0, 6, 0) };
        fit.Measure(new Size(10, 5));
        Assert.Equal(0, Assert.Single(fit.MeasureConstraints).Columns); // max(0, 10 − 12)
        Assert.Equal(new Size(12, 3), fit.DesiredSize);                 // 0 content + 12 margin; 3 rows
    }

    [Fact]
    public void L37_NegativeMargin_CoercedToZero_WithDiagnostic()
    {
        var probe = new Probe(5, 5);
        var diagnostics = LayoutFixture.CaptureDiagnostics(
            () => probe.Margin = new Margins(-2, 1, 1, 1));

        Assert.Equal(new Margins(0, 1, 1, 1), probe.Margin);
#if DEBUG
        Assert.Contains(diagnostics, d => d.Kind == LayoutDiagnosticKind.NegativeMarginCoerced);
#endif
    }
}
