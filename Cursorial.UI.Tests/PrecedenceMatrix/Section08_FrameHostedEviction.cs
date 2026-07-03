using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>
/// Matrix §8 — frame-hosted entries and the pinned eviction order (M153–M166): A5/A7/A8, PD2/PD3.
/// Cookie retraction and <c>TemplateInstance.Detach()</c> reduce to <c>RemoveFrame</c> at engine
/// level.
/// </summary>
public class Section08_FrameHostedEviction
{
    private const ulong K1 = 10;
    private const ulong K2 = 20;

    [Fact]
    public void M153_BindInFrame_InstallsValueless()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1);
        host.AddFrame(frame);
        var probe = Probe<int>.Attach(host, P);

        var entry = host.BindInFrame(P, frame);

        Assert.False(entry.HasValue); // A8
        Assert.Equal(BindingPriority.Style, entry.Priority); // A5
        probe.AssertSilent();
        Assert.Equal(0, host.GetValue(P));
    }

    [Fact]
    public void M154_HostedPush_ContributesAtFrameSortKey()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1);
        host.AddFrame(frame);
        var entry = host.BindInFrame(P, frame);
        var probe = Probe<int>.Attach(host, P);

        entry.SetValue(5);

        Assert.Equal(5, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Style, false), host.GetValueSource(P));
        probe.AssertSingleNotify(0, 5, BindingPriority.Style);
    }

    [Fact]
    public void M155_HostedEntry_FullSortKeyCitizenship()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1);
        host.AddFrame(frame);
        var entry = host.BindInFrame(P, frame);
        entry.SetValue(5);
        var probe = Probe<int>.Attach(host, P);

        var stronger = new TestValueFrame(K2).With(P, 8);
        host.AddFrame(stronger);
        Assert.Equal(8, host.GetValue(P)); // k2 stronger
        probe.Clear();

        entry.SetValue(6); // masked
        probe.AssertSilent();

        host.RemoveFrame(stronger);
        probe.AssertSingleNotify(8, 6, BindingPriority.Style);
    }

    [Fact]
    public void M156_HostedUnset_PromotesEntryRemainsHosted()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1);
        host.AddFrame(frame);
        var entry = host.BindInFrame(P, frame);
        entry.SetValue(5);
        var probe = Probe<int>.Attach(host, P);

        entry.SetUnset();

        Assert.False(entry.HasValue);
        probe.AssertSingleNotify(5, 0, BindingPriority.Default);

        entry.SetValue(6); // still hosted (A8)
        Assert.Equal(6, host.GetValue(P));
        probe.AssertSingleNotify(0, 6, BindingPriority.Style);
    }

    [Fact]
    public void M157_RemoveFrame_EvictRecomputeNotify_InThatOrder()
    {
        var host = new RecordingHost();
        var sequence = new List<string>();
        var frame = new TestValueFrame(K1);
        host.AddFrame(frame);
        var listener = new EvictionRecorder { SequenceLog = sequence };
        var entry = host.BindInFrame(P, frame, listener);
        entry.SetValue(5);
        var typed = new TypedRecorder<int> { SequenceLog = sequence };
        host.AddObserver(P, typed);

        host.RemoveFrame(frame);

        Assert.Equal([entry], listener.Evictions);
        Assert.Equal(["evict(P)", "typed(5->0,Default)"], sequence); // PD2: ① evict ② recompute ③ notify
        Assert.Equal(0, host.GetValue(P));
    }

    [Fact]
    public void M158_TwoHostedEntries_EvictionsInIndexOrder_ThenOneNotifyEach()
    {
        var host = new RecordingHost();
        var sequence = new List<string>();
        var frame = new TestValueFrame(K1);
        host.AddFrame(frame);
        var listener = new EvictionRecorder { SequenceLog = sequence };
        var entry1 = host.BindInFrame(P, frame, listener);
        var entry2 = host.BindInFrame(Pc, frame, listener);
        entry1.SetValue(5);
        entry2.SetValue(60);
        host.AddObserver(P, new TypedRecorder<int> { SequenceLog = sequence, Label = "P" });
        host.AddObserver(Pc, new TypedRecorder<int> { SequenceLog = sequence, Label = "Pc" });

        host.RemoveFrame(frame);

        Assert.Equal([entry1, entry2], listener.Evictions); // frame-entry index order, each once
        Assert.Equal(["evict(P)", "evict(Pc)", "P(5->0,Default)", "Pc(60->0,Default)"], sequence);
    }

    [Fact]
    public void M159_Deactivation_PromotesWithoutEviction_ReactivationReapplies()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1);
        host.AddFrame(frame);
        var listener = new EvictionRecorder();
        var entry = host.BindInFrame(P, frame, listener);
        entry.SetValue(5);
        var probe = Probe<int>.Attach(host, P);

        frame.Deactivate();
        probe.AssertSingleNotify(5, 0, BindingPriority.Default);
        Assert.Empty(listener.Evictions); // PD3

        frame.Activate();
        probe.AssertSingleNotify(0, 5, BindingPriority.Style); // the held value re-applies
    }

    [Fact]
    public void M160_ReentrantDisposeFromOnEvicted_DuringRemoveFrame()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1);
        host.AddFrame(frame);
        var listener = new EvictionRecorder { Callback = static e => e.Dispose() }; // A7
        var entry = host.BindInFrame(P, frame, listener);
        entry.SetValue(5);
        var probe = Probe<int>.Attach(host, P);

        host.RemoveFrame(frame);

        Assert.Equal([entry], listener.Evictions); // once
        probe.AssertSingleNotify(5, 0, BindingPriority.Default); // no double promotion
    }

    [Fact]
    public void M161_HostedSelfDispose_WithdrawsNoEviction_FrameStaysFunctional()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1).With(Pc, 70);
        host.AddFrame(frame);
        var listener = new EvictionRecorder();
        var entry = host.BindInFrame(P, frame, listener);
        entry.SetValue(5);
        var probe = Probe<int>.Attach(host, P);

        entry.Dispose();

        probe.AssertSingleNotify(5, 0, BindingPriority.Default);
        Assert.Empty(listener.Evictions); // PD12
        Assert.Equal(70, host.GetValue(Pc)); // the frame remains installed and functional
        frame.SetEntryValue(Pc, 80);
        Assert.Equal(80, host.GetValue(Pc));
    }

    [Fact]
    public void M162_ClearValue_DoesNotTouchFrameHostedEntries()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1);
        host.AddFrame(frame);
        var listener = new EvictionRecorder();
        var entry = host.BindInFrame(P, frame, listener);
        entry.SetValue(5);
        var probe = Probe<int>.Attach(host, P);

        host.ClearValue(P); // A9 kills local-priority entries only

        probe.AssertSilent();
        Assert.Empty(listener.Evictions);
        Assert.Equal(5, host.GetValue(P));
    }

    [Fact]
    public void M163_TearDown_SweepsFrameHostedEntries()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1);
        host.AddFrame(frame);
        var listener = new EvictionRecorder();
        var entry = host.BindInFrame(P, frame, listener);
        entry.SetValue(5);
        var probe = Probe<int>.Attach(host, P);

        host.TearDownValueStore();

        Assert.Equal([entry], listener.Evictions); // exactly once (A13)
        probe.AssertSilent(); // PD13
        Assert.Equal(0, host.GetValue(P));
    }

    [Fact]
    public void M164_MaskedHostedEntry_StillEvictedOnRemoval()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1);
        host.AddFrame(frame);
        var listener = new EvictionRecorder();
        var entry = host.BindInFrame(P, frame, listener);
        entry.SetValue(5);
        host.SetValue(P, 9); // masks the frame's value
        var probe = Probe<int>.Attach(host, P);

        host.RemoveFrame(frame);

        Assert.Equal([entry], listener.Evictions); // eviction is lifecycle, not value-driven
        probe.AssertSilent(); // effective unchanged
        Assert.Equal(9, host.GetValue(P));
    }

    [Fact]
    public void M165_PushAfterFrameRemoval_DeadEntrySilent()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1);
        host.AddFrame(frame);
        var entry = host.BindInFrame(P, frame);
        entry.SetValue(5);
        host.RemoveFrame(frame);
        var probe = Probe<int>.Attach(host, P);

        entry.SetValue(7);

        probe.AssertSilent();
        Assert.Equal(0, host.GetValue(P));
    }

    [Fact]
    public void M166_BindInFrame_FrameNotInstalledHere_Throws()
    {
        var host = new Host();
        var frame = new TestValueFrame(K1); // never AddFrame'd to host

        Assert.Throws<ArgumentException>(() => host.BindInFrame(P, frame));

        // Installed on a DIFFERENT object is equally rejected.
        var other = new Host();
        other.AddFrame(frame);
        Assert.Throws<ArgumentException>(() => host.BindInFrame(P, frame));
    }
}
