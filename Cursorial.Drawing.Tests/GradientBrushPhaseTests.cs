using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;

namespace Cursorial.Tests.Drawing;

/// <summary>
/// The phase-shifted sampling overload (<see cref="IBrush.ColorAt(int, int, Rect, double)"/>):
/// the phase is added to the <em>raw</em> gradient parameter before the spread applies, so the
/// brush's own <see cref="GradientSpread"/> decides wrap (Repeat — the marquee), clamp (Pad),
/// or fold (Reflect). Assertions are sample-equivalences against the unshifted brush wherever
/// possible — the ramp arithmetic itself is pinned by <see cref="GradientBrushTests"/>.
/// </summary>
public class GradientBrushPhaseTests
{
    private static readonly Color Black = Color.FromRgb(0, 0, 0);
    private static readonly Color White = Color.FromRgb(255, 255, 255);

    private static GradientStop[] BlackToWhite => [new(0.0, Black), new(1.0, White)];

    private static readonly Rect FourCells = new(0, 0, 4, 1);

    [Fact]
    public void ZeroPhase_SamplesIdenticallyToTheUnshiftedForm()
    {
        var brush = new LinearGradientBrush(BlackToWhite, spread: GradientSpread.Repeat);

        for (var col = 0; col < 4; col++)
            Assert.Equal(brush.ColorAt(col, 0, FourCells), brush.ColorAt(col, 0, FourCells, 0.0));
    }

    [Fact]
    public void RepeatSpread_QuarterPhase_IsAOneColumnMarqueeShift()
    {
        // Four columns, one period: t(col) = (col + 0.5) / 4, so a phase of 0.25 lands each cell
        // exactly on its right neighbor's parameter — the marquee shift, cell for cell.
        var brush = new LinearGradientBrush(BlackToWhite, spread: GradientSpread.Repeat);

        for (var col = 0; col < 3; col++)
            Assert.Equal(brush.ColorAt(col + 1, 0, FourCells), brush.ColorAt(col, 0, FourCells, 0.25));

        // The last cell wraps: t = 0.875 + 0.25 = 1.125 → Repeat → 0.125, cell 0's parameter.
        Assert.Equal(brush.ColorAt(0, 0, FourCells), brush.ColorAt(3, 0, FourCells, 0.25));
    }

    [Fact]
    public void RepeatSpread_WholePeriodPhases_AreTheIdentity()
    {
        var brush = new LinearGradientBrush(BlackToWhite, spread: GradientSpread.Repeat);

        for (var col = 0; col < 4; col++)
        {
            var unshifted = brush.ColorAt(col, 0, FourCells);
            Assert.Equal(unshifted, brush.ColorAt(col, 0, FourCells, 1.0));
            Assert.Equal(unshifted, brush.ColorAt(col, 0, FourCells, -1.0));
        }
    }

    [Fact]
    public void PadSpread_PhaseClampsAtTheEndStops()
    {
        // Pad is the default spread: the phase-advanced parameter slides off and saturates at the
        // nearer stop — no wrap. t(3) = 0.875 + 0.5 = 1.375 → 1.0; t(0) = 0.125 − 0.5 → 0.0.
        var brush = new LinearGradientBrush(BlackToWhite);

        Assert.Equal(White, brush.ColorAt(3, 0, FourCells, 0.5));
        Assert.Equal(Black, brush.ColorAt(0, 0, FourCells, -0.5));
    }

    [Fact]
    public void ReflectSpread_PhaseFoldsBackThroughTheMirror()
    {
        // t(3) = 0.875 + 0.5 = 1.375 → Reflect → 0.625 — exactly cell 2's unshifted parameter.
        var brush = new LinearGradientBrush(BlackToWhite, spread: GradientSpread.Reflect);

        Assert.Equal(brush.ColorAt(2, 0, FourCells), brush.ColorAt(3, 0, FourCells, 0.5));
    }

    [Fact]
    public void SolidBrush_IgnoresThePhase()
    {
        // A solid has no parameter axis: the interface DEFAULT forwards to the unshifted sample.
        IBrush brush = new SolidColorBrush(Color.FromRgb(10, 20, 30));

        Assert.Equal(brush.ColorAt(1, 0, FourCells), brush.ColorAt(1, 0, FourCells, 0.7));
    }

    [Fact]
    public void CustomBrushWithoutTheOverload_FallsBackToTheUnshiftedSample()
    {
        // The overload is a default interface member — pre-existing implementations keep compiling
        // and sample unshifted (the additive-change contract).
        IBrush brush = new ThreeArgumentOnlyBrush();

        Assert.Equal(Color.FromRgb(0, 64, 0), brush.ColorAt(0, 0, FourCells, 0.9));
    }

    private sealed class ThreeArgumentOnlyBrush : IBrush
    {
        public Color ColorAt(int column, int row, Rect bounds) => Color.FromRgb(0, 64, 0);
    }
}
