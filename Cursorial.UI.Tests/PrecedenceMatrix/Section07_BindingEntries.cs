using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

// ReSharper disable RedundantCast
// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>Matrix §7 — free-standing binding entries (M136–M152): A6/A7/A8/A9.</summary>
public class Section07_BindingEntries
{
    private const ulong K1 = 10;

    [Fact]
    public void M136_Bind_InstallsValueless()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, P);

        var entry = host.Bind(P);

        Assert.False(entry.HasValue); // A8
        Assert.Equal(0, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Default, false), host.GetValueSource(P));
        probe.AssertSilent();
    }

    [Fact]
    public void M137_EntryPush_AppliesAtLocal()
    {
        var host = new RecordingHost();
        var entry = host.Bind(P);
        var probe = Probe<int>.Attach(host, P);

        entry.SetValue(5);

        Assert.Equal(5, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource(P));
        Assert.True(host.IsSet(P));
        probe.AssertSingleNotify(0, 5, BindingPriority.LocalValue);
    }

    [Fact]
    public void M138_EqualPush_Silent()
    {
        var host = new RecordingHost();
        var entry = host.Bind(P);
        entry.SetValue(5);
        var probe = Probe<int>.Attach(host, P);

        entry.SetValue(5);

        probe.AssertSilent();
    }

    [Fact]
    public void M139_Unset_PromotesEntryStaysInstalled()
    {
        var host = new RecordingHost();
        var entry = host.Bind(P);
        entry.SetValue(5);
        var probe = Probe<int>.Attach(host, P);

        entry.SetUnset();
        Assert.False(entry.HasValue);
        Assert.Equal(0, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Default, false), host.GetValueSource(P));
        probe.AssertSingleNotify(5, 0, BindingPriority.Default);

        entry.SetValue(6); // a later push re-applies
        Assert.Equal(6, host.GetValue(P));
        probe.AssertSingleNotify(0, 6, BindingPriority.LocalValue);
    }

    [Fact]
    public void M140_Unset_PromotesToFrame()
    {
        var host = new RecordingHost();
        host.AddFrame(new TestValueFrame(K1).With(P, 3));
        var entry = host.Bind(P);
        entry.SetValue(5);
        var probe = Probe<int>.Attach(host, P);

        entry.SetUnset();

        Assert.Equal(3, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Style, false), host.GetValueSource(P));
        probe.AssertSingleNotify(5, 3, BindingPriority.Style);
    }

    [Fact]
    public void M141_UntypedUnsetValuePush_EquivalentToUnset()
    {
        var host = new RecordingHost();
        var entry = host.Bind(P);
        entry.SetValue(5);
        var probe = Probe<int>.Attach(host, P);

        ((BindingEntryBase)entry).SetValue(UIProperty.UnsetValue); // A8: never a null/default clobber

        Assert.False(entry.HasValue);
        probe.AssertSingleNotify(5, 0, BindingPriority.Default);
    }

    [Fact]
    public void M142_SetValueIsTransientOverride_NeverEvicts()
    {
        var host = new RecordingHost();
        var listener = new EvictionRecorder();
        var entry = host.Bind(P, listener: listener);
        entry.SetValue(5);
        var probe = Probe<int>.Attach(host, P);

        host.SetValue(P, 8); // A9: SetValue never kills
        Assert.Equal(8, host.GetValue(P));
        probe.AssertSingleNotify(5, 8, BindingPriority.LocalValue);
        Assert.Empty(listener.Evictions);

        entry.SetValue(9); // the binding wins again — last writer in the lane
        Assert.Equal(9, host.GetValue(P));
        probe.AssertSingleNotify(8, 9, BindingPriority.LocalValue);
    }

    [Fact]
    public void M143_ClearValue_IsTheBindingKill_EvictBeforeNotify()
    {
        var host = new RecordingHost();
        var sequence = new List<string>();
        var listener = new EvictionRecorder { SequenceLog = sequence };
        var entry = host.Bind(P, listener: listener);
        entry.SetValue(5);
        var typed = new TypedRecorder<int> { SequenceLog = sequence };
        host.AddObserver(P, typed);

        host.ClearValue(P);

        Assert.Equal([entry], listener.Evictions); // exactly once
        Assert.Equal(["evict(P)", "typed(5->0,Default)"], sequence); // PD2 order
        Assert.Equal(0, host.GetValue(P));
    }

    [Fact]
    public void M144_PostEvictionPush_SilentNoOp()
    {
        var host = new RecordingHost();
        var entry = host.Bind(P);
        entry.SetValue(5);
        host.ClearValue(P);
        var probe = Probe<int>.Attach(host, P);

        entry.SetValue(7);

        probe.AssertSilent();
        Assert.Equal(0, host.GetValue(P));
    }

    [Fact]
    public void M145_SelfDispose_WithdrawsValue_NoEviction_Idempotent()
    {
        var host = new RecordingHost();
        var listener = new EvictionRecorder();
        var entry = host.Bind(P, listener: listener);
        entry.SetValue(5);
        var probe = Probe<int>.Attach(host, P);

        entry.Dispose();
        probe.AssertSingleNotify(5, 0, BindingPriority.Default);
        Assert.Empty(listener.Evictions); // PD12: self-initiated — no OnEvicted

        entry.Dispose(); // idempotent
        probe.AssertSilent();
    }

    [Fact]
    public void M146_ReentrantDisposeFromOnEvicted_Legal()
    {
        var host = new RecordingHost();
        var listener = new EvictionRecorder { Callback = static e => e.Dispose() }; // A7
        var entry = host.Bind(P, listener: listener);
        entry.SetValue(5);
        var probe = Probe<int>.Attach(host, P);

        host.ClearValue(P);

        Assert.Equal([entry], listener.Evictions); // once — no double fire
        probe.AssertSingleNotify(5, 0, BindingPriority.Default); // promotion exactly once
    }

    [Fact]
    public void M147_SecondLocalEntry_DisplacesWithEviction()
    {
        var host = new RecordingHost();
        var listener = new EvictionRecorder();
        var first = host.Bind(P, listener: listener);
        first.SetValue(5);
        var probe = Probe<int>.Attach(host, P);

        var second = host.Bind(P); // PD12: displacement

        Assert.Equal([first], listener.Evictions);
        Assert.False(second.HasValue); // installs valueless
        Assert.Equal(0, host.GetValue(P));
        probe.AssertSingleNotify(5, 0, BindingPriority.Default);
    }

    [Fact]
    public void M148_FreeStandingBind_LocalValueOnly()
    {
        var host = new Host();

        Assert.Throws<ArgumentException>(() => host.Bind(P, BindingPriority.Style));     // A6
        Assert.Throws<ArgumentException>(() => host.Bind(P, BindingPriority.Default));
        Assert.Throws<ArgumentException>(() => host.Bind(P, BindingPriority.Animation));
        Assert.Throws<ArgumentException>(() => host.Bind(P, BindingPriority.Inherited));
    }

    [Fact]
    public void M149_RejectedEntryPush_DiagnosticKeepsState()
    {
        var host = new RecordingHost();
        var entry = host.Bind(Pv);
        var probe = Probe<int>.Attach(host, Pv);

        var rejections = new List<object?>();
        RejectedValueHandler hook = (_, _, value) => rejections.Add(value);
        UIDiagnostics.RejectedValue += hook;
        try
        {
            entry.SetValue(-5);
        }
        finally
        {
            UIDiagnostics.RejectedValue -= hook;
        }

        Assert.Equal([(object?)(-5)], rejections);
        probe.AssertSilent();
        Assert.False(entry.HasValue); // a valueless entry stays valueless
        Assert.Equal(0, host.GetValue(Pv));
    }

    [Fact]
    public void M150_EntryValue_CoercedAtEffectiveComputation()
    {
        var host = new RecordingHost();
        var entry = host.Bind(Pc);
        var probe = Probe<int>.Attach(host, Pc);

        entry.SetValue(250);

        Assert.Equal(100, host.GetValue(Pc));
        probe.AssertSingleNotify(0, 100, BindingPriority.LocalValue);
    }

    [Fact]
    public void M151_EntryPushUnderAnimation_MaskedBaseWrite()
    {
        var host = new RecordingHost();
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        var entry = host.Bind(P);
        var probe = Probe<int>.Attach(host, P);

        entry.SetValue(5);
        probe.AssertSilent();
        Assert.Equal(5, host.GetBaseValue(P));

        handle.Dispose();
        probe.AssertSingleNotify(9, 5, BindingPriority.LocalValue);
    }

    [Fact]
    public void M152_TearDown_EvictsOnce_NoChangeNotifications()
    {
        var host = new RecordingHost();
        var listener = new EvictionRecorder();
        var entry = host.Bind(P, listener: listener);
        entry.SetValue(5);
        var probe = Probe<int>.Attach(host, P);

        host.TearDownValueStore();

        Assert.Equal([entry], listener.Evictions); // exactly once
        probe.AssertSilent(); // PD13
        Assert.Equal(0, host.GetValue(P)); // reads return defaults afterwards
        Assert.Equal(new ValueSource(BindingPriority.Default, false), host.GetValueSource(P));
    }
}
