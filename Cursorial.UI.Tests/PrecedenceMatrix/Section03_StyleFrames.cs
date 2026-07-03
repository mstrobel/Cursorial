using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>Matrix §3 — the Style slot: frames and within-slot ordering (M33–M53).</summary>
public class Section03_StyleFrames
{
    private const ulong K1 = 10;
    private const ulong K2 = 20;

    [Fact]
    public void M033_AddActiveFrame_AppliesAtStyle()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, P);

        host.AddFrame(new TestValueFrame(K1).With(P, 5));

        Assert.Equal(5, host.GetValue(P));
        Assert.Equal(5, host.GetBaseValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Style, false), host.GetValueSource(P));
        Assert.True(host.IsSet(P));
        probe.AssertSingleNotify(0, 5, BindingPriority.Style);
    }

    [Fact]
    public void M034_ValuelessEntry_ContributesNothing()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, P);

        host.AddFrame(new TestValueFrame(K1).WithUnset(P));

        probe.AssertSilent();
        Assert.Equal(0, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Default, false), host.GetValueSource(P));
        Assert.False(host.IsSet(P)); // PD11: valueless entries do not count
    }

    [Fact]
    public void M035_RemoveFrame_PromotesToDefault()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1).With(P, 5);
        host.AddFrame(frame);
        var probe = Probe<int>.Attach(host, P);

        host.RemoveFrame(frame);

        Assert.Equal(0, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Default, false), host.GetValueSource(P));
        probe.AssertSingleNotify(5, 0, BindingPriority.Default); // PD10: promoted lane
    }

    [Fact]
    public void M036_SetActiveToggle_PromotesAndReclaims_NoEviction()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1).With(P, 5);
        host.AddFrame(frame);
        var probe = Probe<int>.Attach(host, P);

        frame.Deactivate();
        probe.AssertSingleNotify(5, 0, BindingPriority.Default); // PD3: no evict — deactivation only withdraws

        frame.Activate();
        probe.AssertSingleNotify(0, 5, BindingPriority.Style);
    }

    [Fact]
    public void M037_SortKeyOrder_NotAddOrder()
    {
        var host = new RecordingHost();
        host.AddFrame(new TestValueFrame(K2).With(P, 8));
        var probe = Probe<int>.Attach(host, P);

        host.AddFrame(new TestValueFrame(K1).With(P, 5)); // weaker, added later

        probe.AssertSilent();
        Assert.Equal(8, host.GetValue(P));
    }

    [Fact]
    public void M038_EqualKeys_LaterAddedWins()
    {
        var host = new RecordingHost();
        host.AddFrame(new TestValueFrame(K1).With(P, 5));
        var probe = Probe<int>.Attach(host, P);

        host.AddFrame(new TestValueFrame(K1).With(P, 6)); // equal key, added later

        Assert.Equal(6, host.GetValue(P));
        probe.AssertSingleNotify(5, 6, BindingPriority.Style);
    }

    [Fact]
    public void M039_RemoveStrongerFrame_WithinSlotPromotion()
    {
        var host = new RecordingHost();
        var k2 = new TestValueFrame(K2).With(P, 8);
        host.AddFrame(new TestValueFrame(K1).With(P, 5));
        host.AddFrame(k2);
        var probe = Probe<int>.Attach(host, P);

        host.RemoveFrame(k2);

        Assert.Equal(5, host.GetValue(P));
        probe.AssertSingleNotify(8, 5, BindingPriority.Style);
    }

    [Fact]
    public void M040_DeactivateStrongerFrame_SamePromotion_NoEviction()
    {
        var host = new RecordingHost();
        var k2 = new TestValueFrame(K2).With(P, 8);
        host.AddFrame(new TestValueFrame(K1).With(P, 5));
        host.AddFrame(k2);
        var probe = Probe<int>.Attach(host, P);

        k2.Deactivate();
        probe.AssertSingleNotify(8, 5, BindingPriority.Style);

        k2.Activate();
        probe.AssertSingleNotify(5, 8, BindingPriority.Style);
    }

    [Fact]
    public void M041_StrongerFrameValuelessEntry_SkippedWithinSlot()
    {
        var host = new Host();
        host.AddFrame(new TestValueFrame(K1).With(P, 5));
        host.AddFrame(new TestValueFrame(K2).WithUnset(P));

        Assert.Equal(5, host.GetValue(P)); // A8: unset promotion within the slot
    }

    [Fact]
    public void M042_OnEntryChanged_InPlaceReEmit_NotifiesAtStyle()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1).With(P, 5);
        host.AddFrame(frame);
        var probe = Probe<int>.Attach(host, P);

        frame.SetEntryValue(P, 6);

        probe.AssertSingleNotify(5, 6, BindingPriority.Style);
        Assert.Equal(6, host.GetValue(P));
    }

    [Fact]
    public void M043_OnEntryChanged_UnchangedValue_Silent()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1).With(P, 5);
        host.AddFrame(frame);
        var probe = Probe<int>.Attach(host, P);

        frame.SetEntryValue(P, 5);

        probe.AssertSilent();
    }

    [Fact]
    public void M044_MaskedReEmit_SilentUntilPromotion()
    {
        var host = new RecordingHost();
        var k1 = new TestValueFrame(K1).With(P, 5);
        var k2 = new TestValueFrame(K2).With(P, 8);
        host.AddFrame(k1);
        host.AddFrame(k2);
        var probe = Probe<int>.Attach(host, P);

        k1.SetEntryValue(P, 7); // masked by k2
        probe.AssertSilent();

        host.RemoveFrame(k2);
        probe.AssertSingleNotify(8, 7, BindingPriority.Style);
    }

    [Fact]
    public void M045_LocalMasksFrame_AddIsSilent()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        var probe = Probe<int>.Attach(host, P);

        host.AddFrame(new TestValueFrame(K1).With(P, 5));

        probe.AssertSilent();
        Assert.Equal(3, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource(P));
        Assert.Equal(5, host.GetValue(P, BindingPriority.Style));
    }

    [Fact]
    public void M046_ClearLocal_PromotesToFrame()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        host.AddFrame(new TestValueFrame(K1).With(P, 5));
        var probe = Probe<int>.Attach(host, P);

        host.ClearValue(P);

        Assert.Equal(5, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Style, false), host.GetValueSource(P));
        probe.AssertSingleNotify(3, 5, BindingPriority.Style);
    }

    [Fact]
    public void M047_EqualValuePromotion_SilentSourceFlip()
    {
        var host = new RecordingHost();
        host.SetValue(P, 7);
        host.AddFrame(new TestValueFrame(K1).With(P, 7));
        var probe = Probe<int>.Attach(host, P);

        host.ClearValue(P);

        probe.AssertSilent(); // PD9
        Assert.Equal(new ValueSource(BindingPriority.Style, false), host.GetValueSource(P));
    }

    [Fact]
    public void M048_RemoveMaskedFrame_Silent()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        var frame = new TestValueFrame(K1).With(P, 5);
        host.AddFrame(frame);
        var probe = Probe<int>.Attach(host, P);

        host.RemoveFrame(frame);

        probe.AssertSilent();
        Assert.Equal(3, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource(P));
    }

    [Fact]
    public void M049_MultiPropertyFrame_OneNotifyPerProperty_Coerced()
    {
        var host = new RecordingHost();
        var probeP = Probe<int>.Attach(host, P);
        var probePc = Probe<int>.Attach(host, Pc);

        host.AddFrame(new TestValueFrame(K1).With(P, 5).With(Pc, 120));

        probeP.AssertSingleNotify(0, 5, BindingPriority.Style);
        probePc.AssertSingleNotify(0, 100, BindingPriority.Style); // coerced at effective computation
        Assert.Equal(100, host.GetValue(Pc));
    }

    [Fact]
    public void M050_AddFrameTwice_Throws()
    {
        var host = new Host();
        var frame = new TestValueFrame(K1).With(P, 5);
        host.AddFrame(frame);

        Assert.Throws<InvalidOperationException>(() => host.AddFrame(frame)); // PD21
    }

    [Fact]
    public void M051_RemoveNeverAddedFrame_Throws()
    {
        var host = new Host();
        host.SetValue(P, 1); // force a store so the store-level check is exercised

        Assert.Throws<ArgumentException>(() => host.RemoveFrame(new TestValueFrame(K1).With(P, 5))); // PD21
    }

    [Fact]
    public void M052_InactiveFrame_AddSilent_ActivationApplies()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, P);
        var frame = new TestValueFrame(K1, isActive: false).With(P, 5);

        host.AddFrame(frame);
        probe.AssertSilent();
        Assert.Equal(0, host.GetValue(P));

        frame.Activate();
        probe.AssertSingleNotify(0, 5, BindingPriority.Style);
    }

    [Fact]
    public void M053_FrameSourcedValue_ReportsStyle()
    {
        var host = new Host();
        host.AddFrame(new TestValueFrame(K1).With(P, 5));

        Assert.Equal(new ValueSource(BindingPriority.Style, false), host.GetValueSource(P));
    }
}
