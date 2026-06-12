using Cursorial.Rendering;
using Cursorial.UI;

namespace Cursorial.Tests.UI.LayoutMatrix;

// ReSharper disable InconsistentNaming

/// <summary>Layout matrix §1 — <c>LayoutMath</c> &amp; integer saturation (L1–L12).</summary>
public class Section01_LayoutMath
{
    private const int U = LayoutMath.Unbounded;

    [Fact]
    public void L1_Constants_Unbounded_MaxExtent_MaxScrollExtent()
    {
        Assert.Equal(int.MaxValue, LayoutMath.Unbounded);
        Assert.Equal(65535, LayoutMath.MaxExtent);
        Assert.Equal(32_000, LayoutLimits.MaxScrollExtent);
        Assert.True(LayoutMath.IsUnbounded(U));
        Assert.False(LayoutMath.IsUnbounded(LayoutMath.MaxExtent));
    }

    [Fact]
    public void L2_Add_UnboundedAbsorbs()
    {
        Assert.Equal(U, LayoutMath.Add(U, 5));
        Assert.Equal(U, LayoutMath.Add(5, U));
        Assert.Equal(U, LayoutMath.Add(U, U));
    }

    [Fact]
    public void L3_Add_FiniteOverflow_SaturatesToUnbounded()
        => Assert.Equal(U, LayoutMath.Add(2_147_483_640, 100));

    [Fact]
    public void L4_Add_NegativeResult_FloorsAtZero()
        => Assert.Equal(0, LayoutMath.Add(3, -5));

    [Fact]
    public void L5_Sub_UnboundedAbsorbsOnLeft()
    {
        Assert.Equal(U, LayoutMath.Sub(U, 5));
        Assert.Equal(U, LayoutMath.Sub(U, U));
    }

    [Fact]
    public void L6_Sub_FloorsAtZero()
    {
        Assert.Equal(0, LayoutMath.Sub(5, 9));
        Assert.Equal(0, LayoutMath.Sub(5, U));
    }

    [Fact]
    public void L7_SizeMargins_AddAndSub_PerAxis()
    {
        var margins = new Margins(1, 2, 3, 4);
        Assert.Equal(new Size(14, 11), LayoutMath.Add(new Size(10, 5), margins));
        Assert.Equal(new Size(6, 0), LayoutMath.Sub(new Size(10, 5), margins)); // 5 − 6 floors at 0
    }

    [Fact]
    public void L8_SizeSub_UnboundedAxisAbsorbsMargin()
        => Assert.Equal(new Size(U, 6), LayoutMath.Sub(new Size(U, 10), new Margins(2, 2, 2, 2)));

    [Fact]
    public void L9_Clamp_MinAppliedLast_MinWins()
        => Assert.Equal(10, LayoutMath.Clamp(5, 10, 3));

    [Fact]
    public void L10_CenterOffset_Floor_NeverNegative()
    {
        Assert.Equal(3, LayoutMath.CenterOffset(10, 4));
        Assert.Equal(2, LayoutMath.CenterOffset(9, 4)); // floor — the spare cell goes right/bottom
        Assert.Equal(0, LayoutMath.CenterOffset(4, 10));
    }

    [Theory]
    [InlineData(20, 65530, LayoutMath.MaxExtent)]            // positive edge: 65530 + 20 = 65550
    [InlineData(-70_000, 0, -LayoutMath.MaxExtent)]          // negative edge (LD19 symmetric clamp)
    [InlineData(int.MaxValue, 65530, LayoutMath.MaxExtent)]  // int fold would wrap negative — long fold keeps the edge's sign
    public void L11_ArrangePosition_ClampsSymmetricallyToMaxExtent_WithDiagnostic_NoThrow(
        int marginLeft, int slotColumn, int expectedColumn)
    {
        var probe = new Probe(4, 2)
        {
            Margin = new Margins(marginLeft, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var diagnostics = LayoutFixture.CaptureDiagnostics(
            () => probe.Arrange(new Rect(slotColumn, 0, 30, 5)));

        Assert.Equal(expectedColumn, probe.Bounds.Column);
#if DEBUG
        Assert.Contains(diagnostics, d => d.Kind == LayoutDiagnosticKind.ArrangeRectClamped && ReferenceEquals(d.Element, probe));
#endif
    }

    [Fact]
    public void L12_DesiredSize_NeverUnbounded_ClampsToMaxExtent_WithDiagnostic()
    {
        var probe = new Probe { MeasureResult = static _ => new Size(U, 1) };

        var diagnostics = LayoutFixture.CaptureDiagnostics(
            () => probe.Measure(new Size(10, 10)));

        Assert.Equal(new Size(LayoutMath.MaxExtent, 1), probe.DesiredSize);
#if DEBUG
        Assert.Contains(diagnostics, d => d.Kind == LayoutDiagnosticKind.DesiredSizeUnbounded && ReferenceEquals(d.Element, probe));
#endif
    }
}
