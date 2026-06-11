using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>
/// Matrix §9 — the winning-base observer, <c>ObserverOptions.IncludeBaseChanges</c> (ledger A20),
/// M167–M178 including the inherited-routing rows.
/// </summary>
public class Section09_WinningBase
{
    private const ulong K1 = 10;

    private static readonly ObserverOptions BaseChanges = new() { IncludeBaseChanges = true };

    [Fact]
    public void M167_MaskedBaseWrite_BaseChannelFires_PlainSilent()
    {
        var host = new RecordingHost();
        var baseObserver = new BaseRecorder<int>();
        host.AddObserver(P, baseObserver, BaseChanges);
        var probe = Probe<int>.Attach(host, P);
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        probe.Clear();
        baseObserver.BaseDeliveries.Clear();

        host.SetValue(P, 4);

        probe.AssertSilent(); // M61
        Assert.Equal([(3, 4, true)], baseObserver.BaseDeliveries); // exactly once
        Assert.Empty(baseObserver.OrdinaryDeliveries); // the options subscription is base-channel only
    }

    [Fact]
    public void M168_AnimatedWrite_PlainFires_BaseSilent()
    {
        var host = new RecordingHost();
        var baseObserver = new BaseRecorder<int>();
        host.AddObserver(P, baseObserver, BaseChanges);
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        var probe = Probe<int>.Attach(host, P);
        baseObserver.BaseDeliveries.Clear();

        handle.SetValue(10);

        probe.AssertSingleNotify(9, 10, BindingPriority.Animation);
        Assert.Empty(baseObserver.BaseDeliveries); // base unchanged
    }

    [Fact]
    public void M169_UnAnimatedChange_BothChannelsFire()
    {
        var host = new RecordingHost();
        var baseObserver = new BaseRecorder<int>();
        host.AddObserver(P, baseObserver, BaseChanges);
        var probe = Probe<int>.Attach(host, P);

        host.SetValue(P, 4);

        probe.AssertSingleNotify(0, 4, BindingPriority.LocalValue);
        Assert.Equal([(0, 4, false)], baseObserver.BaseDeliveries);
    }

    [Fact]
    public void M170_FrameRemovalUnderAnimation_BaseChannelFires()
    {
        var host = new RecordingHost();
        var baseObserver = new BaseRecorder<int>();
        host.AddObserver(P, baseObserver, BaseChanges);
        var frame = new TestValueFrame(K1).With(P, 5);
        host.AddFrame(frame);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        var probe = Probe<int>.Attach(host, P);
        baseObserver.BaseDeliveries.Clear();

        host.RemoveFrame(frame);

        probe.AssertSilent();
        Assert.Equal([(5, 0, true)], baseObserver.BaseDeliveries);
    }

    [Fact]
    public void M171_ClearValueUnderAnimation_BaseChannelFires_EvictionOrdered()
    {
        var host = new RecordingHost();
        var sequence = new List<string>();
        var baseObserver = new BaseRecorder<int>();
        host.AddObserver(P, baseObserver, BaseChanges);
        var listener = new EvictionRecorder { SequenceLog = sequence };
        var entry = host.Bind(P, listener: listener);
        entry.SetValue(3);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        var probe = Probe<int>.Attach(host, P);
        baseObserver.BaseDeliveries.Clear();

        host.ClearValue(P);

        probe.AssertSilent();
        Assert.Equal([(3, 0, true)], baseObserver.BaseDeliveries);
        Assert.Equal([entry], listener.Evictions); // A9 eviction still fires, ordered per PD2
        Assert.Equal(["evict(P)"], sequence);
    }

    [Fact]
    public void M172_AnimationDetach_NotABaseChange()
    {
        var host = new RecordingHost();
        var baseObserver = new BaseRecorder<int>();
        host.AddObserver(P, baseObserver, BaseChanges);
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        var probe = Probe<int>.Attach(host, P);
        baseObserver.BaseDeliveries.Clear();

        handle.Dispose();

        Assert.Empty(baseObserver.BaseDeliveries); // base still 3
        probe.AssertSingleNotify(9, 3, BindingPriority.LocalValue);
    }

    [Fact]
    public void M173_AnimationAttachAndFirstPush_NotABaseChange()
    {
        var host = new RecordingHost();
        var baseObserver = new BaseRecorder<int>();
        host.AddObserver(P, baseObserver, BaseChanges);
        host.SetValue(P, 3);
        var probe = Probe<int>.Attach(host, P);
        baseObserver.BaseDeliveries.Clear();

        var handle = host.BeginAnimation(P);
        Assert.Empty(baseObserver.BaseDeliveries);
        probe.AssertSilent();

        handle.SetValue(9);
        Assert.Empty(baseObserver.BaseDeliveries); // base still 3
        probe.AssertSingleNotify(3, 9, BindingPriority.Animation);
    }

    [Fact]
    public void M174_InheritedRouting_MaskedAncestorWrite_BaseChannelFires()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 5);
        var baseObserver = new BaseRecorder<int>();
        leaf.AddObserver(Pi, baseObserver, BaseChanges);
        var plainProbe = InheritedProbe<int>.Attach(leaf, Pi);
        var handle = leaf.BeginAnimation(Pi);
        handle.SetValue(9);
        baseObserver.BaseDeliveries.Clear();
        plainProbe.Clear();

        root.SetValue(Pi, 6);

        plainProbe.AssertSilent(); // masked under the leaf's animation
        Assert.Equal([(5, 6, true)], baseObserver.BaseDeliveries); // the A20 inherited seam
    }

    [Fact]
    public void M175_InheritedRouting_UnAnimated_BothChannelsFire()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 5);
        var baseObserver = new BaseRecorder<int>();
        leaf.AddObserver(Pi, baseObserver, BaseChanges);
        var plainProbe = InheritedProbe<int>.Attach(leaf, Pi);
        baseObserver.BaseDeliveries.Clear();

        root.SetValue(Pi, 6);

        Assert.Equal([(5, 6, false)], baseObserver.BaseDeliveries); // exactly once
        plainProbe.AssertSinglePropagated(5, 6, BindingPriority.Inherited); // A4 — exactly once
    }

    [Fact]
    public void M176_SetCurrentValueUnderAnimation_BaseUntouched()
    {
        var host = new RecordingHost();
        var baseObserver = new BaseRecorder<int>();
        host.AddObserver(P, baseObserver, BaseChanges);
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        baseObserver.BaseDeliveries.Clear();

        host.SetCurrentValue(P, 11);

        Assert.Empty(baseObserver.BaseDeliveries); // A11/A12: the overwrite replaces the animated value (M131)
        Assert.Equal(3, host.GetBaseValue(P));
    }

    [Fact]
    public void M177_EqualWrite_BaseChannelGatedToo()
    {
        var host = new RecordingHost();
        var baseObserver = new BaseRecorder<int>();
        host.AddObserver(P, baseObserver, BaseChanges);

        host.SetValue(P, 4);
        Assert.Equal([(0, 4, false)], baseObserver.BaseDeliveries);

        host.SetValue(P, 4); // equality-gated
        Assert.Equal([(0, 4, false)], baseObserver.BaseDeliveries); // no second delivery
    }

    [Fact]
    public void M178_DisposedBaseSubscription_IndependentOfPlain()
    {
        var host = new RecordingHost();
        var observer = new BaseRecorder<int>();
        var baseToken = host.AddObserver(P, observer, BaseChanges);
        var plainToken = host.AddObserver(P, observer); // a plain subscription of the same observer

        baseToken.Dispose();
        host.SetValue(P, 4);

        Assert.Empty(observer.BaseDeliveries); // base channel gone
        Assert.Equal([(0, 4, BindingPriority.LocalValue)], observer.OrdinaryDeliveries); // plain unaffected

        plainToken.Dispose();
        host.SetValue(P, 5);
        Assert.Single(observer.OrdinaryDeliveries); // no further deliveries
    }
}
