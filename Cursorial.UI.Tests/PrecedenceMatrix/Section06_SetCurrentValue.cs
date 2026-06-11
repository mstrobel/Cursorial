using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>Matrix §6 — <c>SetCurrentValue</c> (M118–M135).</summary>
public class Section06_SetCurrentValue
{
    private const ulong K1 = 10;

    [Fact]
    public void M127_SetCurrentValueOnInherited_BehavesAsLocal_ShadowsSubtree()
    {
        var (root, _, leaf) = Chain();
        var subLeaf = new RecordingHost();
        subLeaf.SetInheritanceParent(leaf);
        root.SetValue(Pi, 5);
        var leafProbe = Probe<int>.Attach(leaf, Pi);

        leaf.SetCurrentValue(Pi, 6); // lazy-read inheritance holds no leaf entry ⇒ as-Local (M118 rule)

        Assert.Equal(6, leaf.GetValue(Pi));
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, IsCurrentValue: true), leaf.GetValueSource(Pi));
        leafProbe.AssertSingleNotify(5, 6, BindingPriority.LocalValue);

        // The as-Local overwrite shadows leaf's subtree from further ancestor changes.
        var subProbe = InheritedProbe<int>.Attach(subLeaf, Pi);
        root.SetValue(Pi, 8);
        subProbe.AssertSilent();
        Assert.Equal(6, subLeaf.GetValue(Pi));
    }

    [Fact]
    public void M120_OverwriteOnStyle_ProvenanceUnchanged_ReplacedLanePriority()
    {
        var host = new RecordingHost();
        host.AddFrame(new TestValueFrame(K1).With(P, 5));
        var probe = Probe<int>.Attach(host, P);

        host.SetCurrentValue(P, 6);

        Assert.Equal(6, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Style, IsCurrentValue: true), host.GetValueSource(P));
        probe.AssertSingleNotify(5, 6, BindingPriority.Style); // A11: the replaced lane
    }

    [Fact]
    public void M121_OverwriteIsTheBase_WhileUnAnimated()
    {
        var host = new RecordingHost();
        host.AddFrame(new TestValueFrame(K1).With(P, 5));
        host.SetCurrentValue(P, 6);

        Assert.Equal(6, host.GetBaseValue(P)); // the overwrite IS the base
        Assert.True(host.IsSet(P)); // the style contribution counts (PD11)
    }

    [Fact]
    public void M122_LaneReEmit_ClobbersTheOverwrite()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1).With(P, 5);
        host.AddFrame(frame);
        host.SetCurrentValue(P, 6);
        var probe = Probe<int>.Attach(host, P);

        frame.SetEntryValue(P, 8); // re-evaluation from the replaced lane

        Assert.Equal(8, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Style, IsCurrentValue: false), host.GetValueSource(P)); // +cur cleared
        probe.AssertSingleNotify(6, 8, BindingPriority.Style);
    }

    [Fact]
    public void M123_LaneWithdrawal_OverwriteEvaporates()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1).With(P, 5);
        host.AddFrame(frame);
        host.SetCurrentValue(P, 6);
        var probe = Probe<int>.Attach(host, P);

        host.RemoveFrame(frame);

        Assert.Equal(0, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Default, false), host.GetValueSource(P));
        probe.AssertSingleNotify(6, 0, BindingPriority.Default);
    }

    [Fact]
    public void M124_StrongerLane_WinsNormally()
    {
        var host = new RecordingHost();
        host.AddFrame(new TestValueFrame(K1).With(P, 5));
        host.SetCurrentValue(P, 6);
        var probe = Probe<int>.Attach(host, P);

        host.SetValue(P, 9);

        Assert.Equal(9, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource(P));
        probe.AssertSingleNotify(6, 9, BindingPriority.LocalValue);
    }

    [Fact]
    public void M125_ClearValue_NoLocalContribution_NoOp()
    {
        var host = new RecordingHost();
        host.AddFrame(new TestValueFrame(K1).With(P, 5));
        host.SetCurrentValue(P, 6);
        var probe = Probe<int>.Attach(host, P);

        host.ClearValue(P);

        probe.AssertSilent();
        Assert.Equal(6, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Style, IsCurrentValue: true), host.GetValueSource(P));
    }

    [Fact]
    public void M128_OverwriteUnderAnimation_ReplacesAnimatedEffective()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        var probe = Probe<int>.Attach(host, P);

        host.SetCurrentValue(P, 11);

        Assert.Equal(11, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Animation, IsCurrentValue: true), host.GetValueSource(P));
        Assert.Equal(3, host.GetBaseValue(P)); // base stays
        probe.AssertSingleNotify(9, 11, BindingPriority.Animation); // A11
    }

    [Fact]
    public void M129_AnimationLaneReclaims_CurCleared()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        host.SetCurrentValue(P, 11);
        var probe = Probe<int>.Attach(host, P);

        handle.SetValue(12);

        Assert.Equal(12, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Animation, IsCurrentValue: false), host.GetValueSource(P));
        probe.AssertSingleNotify(11, 12, BindingPriority.Animation);
    }

    [Fact]
    public void M130_HandleDispose_OverwriteDiesWithTheLane()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        host.SetCurrentValue(P, 11);
        var probe = Probe<int>.Attach(host, P);

        handle.Dispose();

        Assert.Equal(3, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource(P));
        probe.AssertSingleNotify(11, 3, BindingPriority.LocalValue);
    }

    [Fact]
    public void M131_OverwriteUnderAnimation_NeverTouchesTheBase()
    {
        var host = new Host();
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);

        host.SetCurrentValue(P, 11);

        Assert.Equal(3, host.GetBaseValue(P)); // A11/A12 joint premise
    }

    [Fact]
    public void M132_JointA11xA12_ArgsPriorityDiscriminatesWriteThrough()
    {
        // Mid-animation SCV ⇒ args carry Animation (S2's TwoWay filter drops it; the source is
        // never written). Un-animated SCV ⇒ args carry the base lane (S2 writes through). P0
        // asserts the two priorities — the observable contract the filter is built on (§2.5).
        var animated = new RecordingHost();
        animated.SetValue(P, 3);
        var handle = animated.BeginAnimation(P);
        handle.SetValue(9);
        var animatedProbe = Probe<int>.Attach(animated, P);
        animated.SetCurrentValue(P, 11);
        animatedProbe.AssertSingleNotify(9, 11, BindingPriority.Animation);

        var plain = new RecordingHost();
        plain.SetValue(P, 3);
        var plainProbe = Probe<int>.Attach(plain, P);
        plain.SetCurrentValue(P, 4);
        plainProbe.AssertSingleNotify(3, 4, BindingPriority.LocalValue);
    }

    [Fact]
    public void M118_SetCurrentValue_NoEntry_BehavesAsLocal()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, P);

        host.SetCurrentValue(P, 4);

        Assert.Equal(4, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, true), host.GetValueSource(P)); // Local+cur
        probe.AssertSingleNotify(0, 4, BindingPriority.LocalValue);
    }

    [Fact]
    public void M119_SetCurrentValue_OverLocal_OverwritesRawSlot()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        var probe = Probe<int>.Attach(host, P);

        host.SetCurrentValue(P, 4);

        Assert.Equal(4, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, true), host.GetValueSource(P));
        probe.AssertSingleNotify(3, 4, BindingPriority.LocalValue);

        // The local raw slot now holds 4 — a later CoerceValue coerces against it (PD6 premise).
        var entry = Assert.IsType<EffectiveValue<int>>(host.DebugValueStore!.TryGetEntry(P.Id));
        Assert.Equal(4, entry.RawLocalValue);
    }

    [Fact]
    public void M126_ClearValue_RemovesAsLocalCurrentValue()
    {
        var host = new RecordingHost();
        host.SetCurrentValue(P, 4);
        var probe = Probe<int>.Attach(host, P);

        host.ClearValue(P);

        Assert.Equal(0, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Default, false), host.GetValueSource(P));
        probe.AssertSingleNotify(4, 0, BindingPriority.Default); // the M118 graft's consequence
    }

    [Fact]
    public void M133_SetCurrentValue_CoercesLikeAnyMouthWrite()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, Pc);

        host.SetCurrentValue(Pc, 250);

        Assert.Equal(100, host.GetValue(Pc));
        probe.AssertSingleNotify(0, 100, BindingPriority.LocalValue);
    }

    [Fact]
    public void M134_SetCurrentValue_ValidateRejection_Throws()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, Pv);

        Assert.Throws<ArgumentException>(() => host.SetCurrentValue(Pv, -5)); // PD17

        probe.AssertSilent();
        Assert.False(host.IsSet(Pv)); // store untouched
        Assert.Equal(new ValueSource(BindingPriority.Default, false), host.GetValueSource(Pv));
    }

    [Fact]
    public void M135_SetCurrentValue_ComparerEqual_Silent_NoCurBit()
    {
        var host = new RecordingHost();
        host.SetValue(Pcmp, "abc");
        var probe = Probe<string?>.Attach(host, Pcmp);

        host.SetCurrentValue(Pcmp, "ABC");

        probe.AssertSilent();
        Assert.Equal("abc", host.GetValue(Pcmp)); // PD20
        Assert.False(host.GetValueSource(Pcmp).IsCurrentValue); // +cur NOT set
    }
}
