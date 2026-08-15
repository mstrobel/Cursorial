using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.UI.Media;

namespace Cursorial.Tests.UI.Media;

/// <summary>
/// <see cref="PhaseShiftedBrush"/>'s brush algebra: the phase delegates into the inner brush's
/// parameter axis (wrap/clamp is the inner <see cref="GradientSpread"/>'s decision), uniformity and
/// opacity delegate, a wrapped solid is phase-inert, and nested wrappers compose phases additively.
/// The repaint mechanism the phase rides is <c>SubObjectObservationTests</c>' territory.
/// </summary>
public class PhaseShiftedBrushTests
{
    private static readonly Color Black = Color.FromRgb(0, 0, 0);
    private static readonly Color White = Color.FromRgb(255, 255, 255);

    private static readonly Rect FourCells = new(0, 0, 4, 1);

    private static LinearGradientBrush Ramp(GradientSpread spread = GradientSpread.Repeat)
        => new([new(0.0, Black), new(1.0, White)], spread: spread);

    [Fact]
    public void SamplesTheInnerBrushAtItsOwnPhase()
    {
        var inner = Ramp();
        var brush = new PhaseShiftedBrush(inner, phase: 0.25);

        // The wrapper's sample IS the inner's phase-shifted sample — for a four-column period a
        // quarter phase is exactly the right neighbor's parameter (the marquee shift).
        for (var col = 0; col < 3; col++)
            Assert.Equal(inner.ColorAt(col + 1, 0, FourCells), brush.ColorAt(col, 0, FourCells));

        Assert.Equal(inner.ColorAt(0, 0, FourCells), brush.ColorAt(3, 0, FourCells));
    }

    [Fact]
    public void MutatingPhase_ChangesTheSampleInPlace()
    {
        var inner = Ramp();
        var brush = new PhaseShiftedBrush(inner);

        var atZero = brush.ColorAt(0, 0, FourCells);
        brush.Phase = 0.5;
        var atHalf = brush.ColorAt(0, 0, FourCells);

        // The same instance answers differently — animating the phase mutates the brush, never
        // replaces it (every reference-keyed cache stays hot; the cache-key census contract).
        Assert.NotEqual(atZero, atHalf);
        Assert.Equal(inner.ColorAt(0, 0, FourCells, 0.5), atHalf);
    }

    [Fact]
    public void PadSpreadInner_ClampsTheShiftedParameter()
    {
        var brush = new PhaseShiftedBrush(Ramp(GradientSpread.Pad), phase: 0.5);

        // The inner spread owns the out-of-range policy: Pad saturates at the end stop.
        Assert.Equal(White, brush.ColorAt(3, 0, FourCells));
    }

    [Fact]
    public void WrappedSolid_IsPhaseInert()
    {
        var solid = new SolidColorBrush(Color.FromRgb(10, 20, 30));
        var brush = new PhaseShiftedBrush(solid);

        var atZero = brush.ColorAt(1, 0, FourCells);
        brush.Phase = 0.7;

        Assert.Equal(atZero, brush.ColorAt(1, 0, FourCells));
        Assert.Equal(solid.ColorAt(1, 0, FourCells), atZero);
    }

    [Fact]
    public void IsUniform_Delegates()
    {
        Assert.False(new PhaseShiftedBrush(Ramp()).IsUniform);              // a phase-shifted gradient is non-uniform
        Assert.True(new PhaseShiftedBrush(new SolidColorBrush(White)).IsUniform); // a wrapped solid stays uniform
        Assert.True(new PhaseShiftedBrush().IsUniform);                     // an empty wrapper samples one value everywhere
    }

    [Fact]
    public void OpacityAndIsOpaque_Delegate()
    {
        var translucent = new SolidColorBrush(White, opacity: 0.5);
        var brush = new PhaseShiftedBrush(translucent);

        Assert.Equal(0.5, brush.Opacity);
        Assert.False(brush.IsOpaque);
        Assert.True(new PhaseShiftedBrush(new SolidColorBrush(White)).IsOpaque);
    }

    [Fact]
    public void EmptyWrapper_SamplesTheTerminalDefault()
    {
        var brush = new PhaseShiftedBrush { Phase = 0.3 };

        Assert.Equal(Colors.Default, brush.ColorAt(0, 0, FourCells));
    }

    [Fact]
    public void NestedWrappers_ComposePhasesAdditively()
    {
        var inner = Ramp();
        var nested = new PhaseShiftedBrush(new PhaseShiftedBrush(inner, phase: 0.25), phase: 0.25);

        for (var col = 0; col < 4; col++)
            Assert.Equal(inner.ColorAt(col, 0, FourCells, 0.5), nested.ColorAt(col, 0, FourCells));
    }

    [Fact]
    public void NonFinitePhase_IsRejectedAtTheMouth()
    {
        var brush = new PhaseShiftedBrush(Ramp());

        Assert.Throws<ArgumentException>(() => brush.Phase = double.NaN);
        Assert.Throws<ArgumentException>(() => brush.Phase = double.PositiveInfinity);
    }
}
