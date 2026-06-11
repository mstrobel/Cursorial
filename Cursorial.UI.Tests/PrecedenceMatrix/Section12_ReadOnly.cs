using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>Matrix §12 — read-only properties (M205–M212).</summary>
public class Section12_ReadOnly
{
    [Fact]
    public void M209_BindAndBeginAnimation_Throw()
    {
        var host = new Host();

        Assert.Throws<InvalidOperationException>(() => host.Bind(Pro));            // PD14
        Assert.Throws<InvalidOperationException>(() => host.BeginAnimation(Pro));  // PD14
    }

    [Fact]
    public void M210_FrameCarryingReadOnlyEntry_RejectedAtAddFrame()
    {
        var host = new Host();
        var frame = new TestValueFrame(10).With(P, 1).WithUntyped(Pro);

        Assert.Throws<InvalidOperationException>(() => host.AddFrame(frame)); // PD14: rejected at install
        Assert.Equal(0, host.GetValue(P)); // nothing applied — validation precedes installation
    }

    [Fact]
    public void M205_KeyWrite_LandsInLocalLane()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, Pro);

        host.SetValue(Kro, 5);

        Assert.Equal(5, host.GetValue(Pro));
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource(Pro));
        probe.AssertSingleNotify(0, 5, BindingPriority.LocalValue);
    }

    [Fact]
    public void M206_KeylessSetValue_Throws()
    {
        var host = new Host();

        Assert.Throws<InvalidOperationException>(() => host.SetValue(Pro, 5)); // PD14
    }

    [Fact]
    public void M207_SetCurrentValue_Throws()
    {
        var host = new Host();

        Assert.Throws<InvalidOperationException>(() => host.SetCurrentValue(Pro, 5)); // PD14
    }

    [Fact]
    public void M208_ClearValue_Throws()
    {
        var host = new Host();
        host.SetValue(Kro, 5);

        Assert.Throws<InvalidOperationException>(() => host.ClearValue(Pro)); // clearing requires the key surface too
        Assert.Equal(5, host.GetValue(Pro));
    }

    [Fact]
    public void M211_Reads_AreUnrestricted()
    {
        var host = new Host();
        host.SetValue(Kro, 5);

        Assert.Equal(5, host.GetValue(Pro));
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource(Pro));
    }

    [Fact]
    public void M212_Observation_IsUnrestricted()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, Pro);

        host.SetValue(Kro, 5);

        probe.AssertSingleNotify(0, 5, BindingPriority.LocalValue); // selector engines watch read-only state
    }
}
