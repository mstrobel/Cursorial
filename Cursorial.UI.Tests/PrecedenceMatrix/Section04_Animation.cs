using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>Matrix §4 — the Animation lane (M54–M75).</summary>
public class Section04_Animation
{
    private const ulong K1 = 10;

    [Fact]
    public void M054_FreshHandle_InertUntilFirstSet()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        var probe = Probe<int>.Attach(host, P);

        _ = host.BeginAnimation(P);

        probe.AssertSilent(); // PD4
        Assert.Equal(3, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource(P));
    }

    [Fact]
    public void M055_FirstPush_AppliesAboveLocal_ReturnsTrue()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        var probe = Probe<int>.Attach(host, P);

        Assert.True(handle.SetValue(9)); // A18

        Assert.Equal(9, host.GetValue(P));
        Assert.Equal(3, host.GetBaseValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Animation, false), host.GetValueSource(P));
        probe.AssertSingleNotify(3, 9, BindingPriority.Animation);
    }

    [Fact]
    public void M056_EqualPush_SilentReturnsFalse()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        var probe = Probe<int>.Attach(host, P);

        Assert.False(handle.SetValue(9)); // A18 equality gate

        probe.AssertSilent();
    }

    [Fact]
    public void M057_ChangedPush_NotifiesReturnsTrue()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        var probe = Probe<int>.Attach(host, P);

        Assert.True(handle.SetValue(10));

        probe.AssertSingleNotify(9, 10, BindingPriority.Animation);
    }

    [Fact]
    public void M058_Dispose_BaseResurfaces_OneNotification()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        var probe = Probe<int>.Attach(host, P);

        handle.Dispose();

        Assert.Equal(3, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource(P));
        probe.AssertSingleNotify(9, 3, BindingPriority.LocalValue); // PD10
    }

    [Fact]
    public void M059_EqualFirstPush_SilentLaneFlip()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        var probe = Probe<int>.Attach(host, P);

        Assert.False(handle.SetValue(3));

        probe.AssertSilent(); // PD9 applied to lane flips
        Assert.Equal(new ValueSource(BindingPriority.Animation, false), host.GetValueSource(P));
    }

    [Fact]
    public void M060_DisposeAfterEqualFlip_SilentFlipBack()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        handle.SetValue(3);
        var probe = Probe<int>.Attach(host, P);

        handle.Dispose();

        probe.AssertSilent();
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource(P));
    }

    [Fact]
    public void M061_MaskedBaseWrite_SilentBaseUpdates()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        var probe = Probe<int>.Attach(host, P);

        host.SetValue(P, 4); // masked base write (§2.3 fast path)

        probe.AssertSilent();
        Assert.Equal(9, host.GetValue(P));
        Assert.Equal(4, host.GetBaseValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Animation, false), host.GetValueSource(P));
    }

    [Fact]
    public void M062_DisposeAfterMaskedWrite_NewBaseResurfaces()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        host.SetValue(P, 4);
        var probe = Probe<int>.Attach(host, P);

        handle.Dispose();

        Assert.Equal(4, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource(P));
        probe.AssertSingleNotify(9, 4, BindingPriority.LocalValue);
    }

    [Fact]
    public void M063_SecondBeginAnimation_DetachesFirst_LastStartedWins()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        var first = host.BeginAnimation(P);
        first.SetValue(9);
        var probe = Probe<int>.Attach(host, P);

        var second = host.BeginAnimation(P);

        Assert.True(first.IsDetached);
        Assert.False(second.IsDetached);
        Assert.Equal(9, host.GetValue(P)); // PD4 inertia: the held value persists until second pushes
        probe.AssertSilent();
    }

    [Fact]
    public void M064_DetachedHandlePush_SilentNoOp()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        var first = host.BeginAnimation(P);
        first.SetValue(9);
        _ = host.BeginAnimation(P);
        var probe = Probe<int>.Attach(host, P);

        Assert.False(first.SetValue(12)); // A19

        probe.AssertSilent();
        Assert.Equal(9, host.GetValue(P));
    }

    [Fact]
    public void M065_DetachedHandleDispose_DoesNothing()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        var first = host.BeginAnimation(P);
        first.SetValue(9);
        var second = host.BeginAnimation(P);
        second.SetValue(11);
        var probe = Probe<int>.Attach(host, P);

        first.Dispose(); // A19: detached — does nothing

        probe.AssertSilent();
        Assert.Equal(11, host.GetValue(P));
        Assert.False(second.IsDetached);
    }

    [Fact]
    public void M066_DoubleDispose_Idempotent()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        handle.Dispose();
        var probe = Probe<int>.Attach(host, P);

        handle.Dispose(); // A19

        probe.AssertSilent();
        Assert.Equal(3, host.GetValue(P));
    }

    [Fact]
    public void M067_NoBase_AnimationOverDefault_RoundTrip()
    {
        var host = new RecordingHost();
        var handle = host.BeginAnimation(P);
        var probe = Probe<int>.Attach(host, P);

        handle.SetValue(9);
        Assert.Equal(9, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Animation, false), host.GetValueSource(P));
        probe.AssertSingleNotify(0, 9, BindingPriority.Animation);

        handle.Dispose();
        Assert.Equal(0, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Default, false), host.GetValueSource(P));
        probe.AssertSingleNotify(9, 0, BindingPriority.Default);
    }

    [Fact]
    public void M068_FrameRemovedUnderAnimation_SilentBaseFlip()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1).With(P, 5);
        host.AddFrame(frame);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        var probe = Probe<int>.Attach(host, P);

        host.RemoveFrame(frame);
        probe.AssertSilent(); // effective unchanged — the animation masks the promotion
        Assert.Equal(9, host.GetValue(P));
        Assert.Equal(0, host.GetBaseValue(P));

        handle.Dispose();
        probe.AssertSingleNotify(9, 0, BindingPriority.Default);
    }

    [Fact]
    public void M069_ClearValueUnderAnimation_LocalRemovedSilently()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        var probe = Probe<int>.Attach(host, P);

        host.ClearValue(P);
        probe.AssertSilent();
        Assert.Equal(9, host.GetValue(P));
        Assert.Equal(0, host.GetBaseValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Animation, false), host.GetValueSource(P));

        handle.Dispose();
        probe.AssertSingleNotify(9, 0, BindingPriority.Default);
    }

    [Fact]
    public void M070_GetBaseValue_EquivalentToMaxPriorityLocal()
    {
        var host = new Host();
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);

        Assert.Equal(3, host.GetBaseValue(P));
        Assert.Equal(3, host.GetValue(P, BindingPriority.LocalValue)); // PD16 equivalence
    }

    [Fact]
    public void M071_AnimatedValue_CoercedAtEffectiveComputation()
    {
        var host = new RecordingHost();
        var handle = host.BeginAnimation(Pc);
        var probe = Probe<int>.Attach(host, Pc);

        Assert.True(handle.SetValue(250));

        Assert.Equal(100, host.GetValue(Pc)); // §2.6-6 guardrail
        probe.AssertSingleNotify(0, 100, BindingPriority.Animation);
    }

    [Fact]
    public void M072_DifferentRaw_SameCoercedResult_SilentFalse()
    {
        var host = new RecordingHost();
        var handle = host.BeginAnimation(Pc);
        handle.SetValue(250);
        var probe = Probe<int>.Attach(host, Pc);

        Assert.False(handle.SetValue(300)); // equality on the coerced value

        probe.AssertSilent();
        Assert.Equal(100, host.GetValue(Pc));
    }

    [Fact]
    public void M073_RejectedAnimatedValue_DiagnosticKeepsPrevious()
    {
        var host = new RecordingHost();
        host.SetValue(Pv, 7);
        var handle = host.BeginAnimation(Pv);
        var probe = Probe<int>.Attach(host, Pv);

        var rejections = new List<(UIObject Target, UIProperty Property, object? Value)>();
        RejectedValueHandler hook = (target, property, value) => rejections.Add((target, property, value));
        UIDiagnostics.RejectedValue += hook;
        try
        {
            Assert.False(handle.SetValue(-5)); // producer-mouth rejection (§2.3)
        }
        finally
        {
            UIDiagnostics.RejectedValue -= hook;
        }

        Assert.Equal([(host, Pv, (object?)(-5))], rejections);
        probe.AssertSilent();
        Assert.Equal(7, host.GetValue(Pv));
    }

    [Fact]
    public void M074_IsSet_CountsLocalContribution_NotAnimation()
    {
        var withBase = new Host();
        withBase.SetValue(P, 3);
        var h1 = withBase.BeginAnimation(P);
        h1.SetValue(9);
        Assert.True(withBase.IsSet(P)); // from the local contribution

        var withoutBase = new Host();
        var h2 = withoutBase.BeginAnimation(P);
        h2.SetValue(9);
        Assert.False(withoutBase.IsSet(P)); // PD11: animation doesn't count
    }

    [Fact]
    public void M075_AnimatedSource_ReportsAnimation()
    {
        var host = new Host();
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);

        Assert.Equal(new ValueSource(BindingPriority.Animation, IsCurrentValue: false), host.GetValueSource(P));
    }
}
