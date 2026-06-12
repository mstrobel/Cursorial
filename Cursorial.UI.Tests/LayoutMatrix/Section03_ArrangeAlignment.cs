using Cursorial.Rendering;
using Cursorial.UI;

namespace Cursorial.Tests.UI.LayoutMatrix;

// ReSharper disable InconsistentNaming

/// <summary>
/// Layout matrix §3 — core Arrange &amp; alignment (L38–L60). Alignment rows: <c>Probe(4×2)</c> in
/// <c>slot((0,0,10×6))</c> unless noted; <c>V = Top</c> for horizontal rows, <c>H = Left</c> for
/// vertical rows.
/// </summary>
public class Section03_ArrangeAlignment
{
    private static Probe MeasuredProbe(
        int columns = 4, int rows = 2,
        HorizontalAlignment h = HorizontalAlignment.Left,
        VerticalAlignment v = VerticalAlignment.Top)
    {
        var probe = new Probe(columns, rows) { HorizontalAlignment = h, VerticalAlignment = v };
        probe.Measure(new Size(100, 100));
        return probe;
    }

    [Fact]
    public void L38_Left()
    {
        var probe = MeasuredProbe(h: HorizontalAlignment.Left);
        probe.Arrange(new Rect(0, 0, 10, 6));
        Assert.Equal(new Rect(0, 0, 4, 2), probe.Bounds);
    }

    [Fact]
    public void L39_Center()
    {
        var probe = MeasuredProbe(h: HorizontalAlignment.Center);
        probe.Arrange(new Rect(0, 0, 10, 6));
        Assert.Equal(new Rect(3, 0, 4, 2), probe.Bounds);
    }

    [Fact]
    public void L40_Center_Floor_SpareCellGoesRight()
    {
        var probe = MeasuredProbe(h: HorizontalAlignment.Center);
        probe.Arrange(new Rect(0, 0, 9, 6));
        Assert.Equal(new Rect(2, 0, 4, 2), probe.Bounds);
    }

    [Fact]
    public void L41_Right()
    {
        var probe = MeasuredProbe(h: HorizontalAlignment.Right);
        probe.Arrange(new Rect(0, 0, 10, 6));
        Assert.Equal(new Rect(6, 0, 4, 2), probe.Bounds);
    }

    [Fact]
    public void L42_Stretch_FillsTheSlot()
    {
        var probe = MeasuredProbe(h: HorizontalAlignment.Stretch);
        probe.Arrange(new Rect(0, 0, 10, 6));
        Assert.Equal(new Rect(0, 0, 10, 2), probe.Bounds);
    }

    [Theory]
    [InlineData(VerticalAlignment.Top, 0, 2)]
    [InlineData(VerticalAlignment.Center, 2, 2)]
    [InlineData(VerticalAlignment.Bottom, 4, 2)]
    [InlineData(VerticalAlignment.Stretch, 0, 6)]
    public void L43_VerticalAlignment_Family(VerticalAlignment v, int expectedRow, int expectedRows)
    {
        var probe = MeasuredProbe(v: v);
        probe.Arrange(new Rect(0, 0, 10, 6));
        Assert.Equal(new Rect(0, expectedRow, 4, expectedRows), probe.Bounds);
    }

    [Fact]
    public void L44_StretchThatCannotFill_Centers()
    {
        var probe = MeasuredProbe(h: HorizontalAlignment.Stretch);
        probe.MaxWidth = 6;
        probe.Measure(new Size(100, 100));
        probe.Arrange(new Rect(0, 0, 10, 6));
        Assert.Equal(new Rect(2, 0, 6, 2), probe.Bounds); // LD3
    }

    [Theory]
    [InlineData(HorizontalAlignment.Left)]
    [InlineData(HorizontalAlignment.Center)]
    [InlineData(HorizontalAlignment.Right)]
    public void L45_NonStretch_ArrangedSize_IsMinOfDesiredAndSlot(HorizontalAlignment h)
    {
        var probe = MeasuredProbe(columns: 12, rows: 2, h: h);
        probe.Arrange(new Rect(0, 0, 10, 6));
        Assert.Equal(new Rect(0, 0, 10, 2), probe.Bounds);
    }

    [Fact]
    public void L46_MinForcesOverflow_Center_OffsetClampsToZero_OverflowsRight()
    {
        var probe = MeasuredProbe(h: HorizontalAlignment.Center);
        probe.MinWidth = 12;
        probe.Measure(new Size(100, 100));
        probe.Arrange(new Rect(0, 0, 10, 6));
        Assert.Equal(new Rect(0, 0, 12, 2), probe.Bounds); // LD3 — DEV from WPF's negative offset
    }

    [Fact]
    public void L47_MinForcesOverflow_Right_OffsetClampsToZero()
    {
        var probe = MeasuredProbe(h: HorizontalAlignment.Right);
        probe.MinWidth = 12;
        probe.Measure(new Size(100, 100));
        probe.Arrange(new Rect(0, 0, 10, 6));
        Assert.Equal(new Rect(0, 0, 12, 2), probe.Bounds);
    }

    [Fact]
    public void L48_Margin_OffsetsThePosition()
    {
        var probe = MeasuredProbe();
        probe.Margin = new Margins(1, 1, 0, 0);
        probe.Measure(new Size(100, 100));
        probe.Arrange(new Rect(0, 0, 10, 6));
        Assert.Equal(new Rect(1, 1, 4, 2), probe.Bounds);
    }

    [Fact]
    public void L49_Margin_WithRightAlignment()
    {
        var probe = MeasuredProbe(h: HorizontalAlignment.Right);
        probe.Margin = new Margins(1, 0, 1, 0);
        probe.Measure(new Size(100, 100));
        probe.Arrange(new Rect(0, 0, 10, 6));
        Assert.Equal(5, probe.Bounds.Column); // inner slot 8; offset 8−4 = 4; column = 1+4
    }

    [Fact]
    public void L50_Margin_WithCenterAlignment()
    {
        var probe = MeasuredProbe(h: HorizontalAlignment.Center);
        probe.Margin = new Margins(1, 0, 1, 0);
        probe.Measure(new Size(100, 100));
        probe.Arrange(new Rect(0, 0, 10, 6));
        Assert.Equal(3, probe.Bounds.Column); // offset (8−4)/2 = 2; column = 1+2
    }

    [Fact]
    public void L51_MarginExceedsSlot_ZeroWidthContent_AtTheMarginOffset()
    {
        var fit = new Fit(4, 2) { Margin = new Margins(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Top };
        fit.Measure(new Size(4, 6));
        fit.Arrange(new Rect(0, 0, 4, 6));
        Assert.Equal(new Rect(3, 0, 0, 2), fit.Bounds);
    }

    [Fact]
    public void L52_SlotOrigin_Honored_ParentRelative()
    {
        var probe = MeasuredProbe();
        probe.Arrange(new Rect(5, 3, 10, 6));
        Assert.Equal(new Rect(5, 3, 4, 2), probe.Bounds);
    }

    [Fact]
    public void L53_ArrangeCacheHit_KeysFinalRect()
    {
        var probe = MeasuredProbe();
        probe.Arrange(new Rect(0, 0, 10, 6));
        probe.Arrange(new Rect(0, 0, 10, 6));
        Assert.Equal(1, probe.ArrangeOverrideCount);
    }

    [Fact]
    public void L54_DifferentRect_RerunsArrange()
    {
        var probe = MeasuredProbe();
        probe.Arrange(new Rect(0, 0, 10, 6));
        probe.Arrange(new Rect(1, 0, 10, 6));
        Assert.Equal(2, probe.ArrangeOverrideCount);
    }

    [Fact]
    public void L55_Arrange_SelfHealsInvalidMeasure_WithLastConstraint()
    {
        var probe = MeasuredProbe();
        probe.Arrange(new Rect(0, 0, 10, 6));
        var measureCount = probe.MeasureOverrideCount;

        probe.InvalidateMeasure();
        probe.Arrange(new Rect(0, 0, 10, 6)); // no intervening measure

        Assert.Equal(measureCount + 1, probe.MeasureOverrideCount); // Measure(_lastMeasureConstraint) ran first
        Assert.Equal(new Size(100, 100), probe.MeasureConstraints[^1]);
        Assert.True(probe.IsMeasureValid);
    }

    [Fact]
    public void L56_NeverMeasured_Arrange_MeasuresWithSlotSize_NoThrow()
    {
        var probe = new Probe(4, 2);
        probe.Arrange(new Rect(0, 0, 10, 6)); // LD4: never-measured → Measure(finalRect.Size)
        Assert.Equal(new Size(10, 6), Assert.Single(probe.MeasureConstraints));
        Assert.Equal(1, probe.MeasureOverrideCount);
    }

    [Fact]
    public void L57_ArrangeOverrideResult_ClampedToMaxExtent_WithDiagnostic()
    {
        var probe = new Probe(4, 2) { ArrangeResult = static _ => new Size(70_000, 2) };
        probe.Measure(new Size(100, 100));

        var diagnostics = LayoutFixture.CaptureDiagnostics(
            () => probe.Arrange(new Rect(0, 0, 10, 5)));

        Assert.Equal(LayoutMath.MaxExtent, probe.Bounds.Columns);
#if DEBUG
        Assert.Contains(diagnostics, d => d.Kind == LayoutDiagnosticKind.ArrangeRectClamped && ReferenceEquals(d.Element, probe));
#endif
    }

    [Fact]
    public void L58_BoundsProperty_DirectProperty_EqualityGatedNotification()
    {
        var probe = MeasuredProbe();
        var observer = new LayoutFixture.RecordingObserver();
        probe.AddObserver(UIElement.BoundsProperty, observer);

        probe.Arrange(new Rect(0, 0, 10, 6));
        Assert.Single(observer.Changes);
        Assert.Equal(LayoutRect.Empty, observer.Changes[0].OldValue); // Bounds is the signed LayoutRect (LD19)
        Assert.Equal(new LayoutRect(0, 0, 4, 2), observer.Changes[0].NewValue);

        probe.InvalidateArrange();
        probe.Arrange(new Rect(0, 0, 10, 6)); // same resulting rect — silent
        Assert.Single(observer.Changes);
    }

    [Fact]
    public void L59_ExplicitWidth_BindsUnderStretch_ThenCenters()
    {
        var probe = new Probe(2, 2) { Width = 6, VerticalAlignment = VerticalAlignment.Top };
        probe.Measure(new Size(100, 100));
        probe.Arrange(new Rect(0, 0, 10, 6));
        Assert.Equal(new Rect(2, 0, 6, 2), probe.Bounds); // LD1 + LD3
    }

    [Fact]
    public void L60_ArrangeOverride_ReceivesAlignedSize_NotTheSlot()
    {
        var left = MeasuredProbe(h: HorizontalAlignment.Left);
        left.Arrange(new Rect(0, 0, 10, 6));
        Assert.Equal(new Size(4, 2), left.ArrangeSizes[^1]);

        var stretch = MeasuredProbe(h: HorizontalAlignment.Stretch);
        stretch.Arrange(new Rect(0, 0, 10, 6));
        Assert.Equal(new Size(10, 2), stretch.ArrangeSizes[^1]);
    }
}
