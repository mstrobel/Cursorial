using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>Matrix §5 — pairwise precedence, every priority pair (M76–M117).</summary>
public class Section05_Pairwise
{
    private const ulong K1 = 10;

    // ───────────── Animation over Local (M76–M79) ─────────────

    [Fact]
    public void M076_AnimOverLocal_Apply()
    {
        var host = new RecordingHost();
        host.SetValue(P, 5);
        var handle = host.BeginAnimation(P);
        var probe = Probe<int>.Attach(host, P);

        handle.SetValue(9);

        Assert.Equal(9, host.GetValue(P));
        Assert.Equal(5, host.GetBaseValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Animation, false), host.GetValueSource(P));
        probe.AssertSingleNotify(5, 9, BindingPriority.Animation);
    }

    [Fact]
    public void M077_AnimOverLocal_Withdraw()
    {
        var host = new RecordingHost();
        host.SetValue(P, 5);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        var probe = Probe<int>.Attach(host, P);

        handle.Dispose();

        Assert.Equal(5, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource(P));
        probe.AssertSingleNotify(9, 5, BindingPriority.LocalValue);
    }

    [Fact]
    public void M078_AnimOverLocal_MaskedWrite()
    {
        var host = new RecordingHost();
        host.SetValue(P, 5);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        var probe = Probe<int>.Attach(host, P);

        host.SetValue(P, 6);

        probe.AssertSilent();
        Assert.Equal(9, host.GetValue(P));
        Assert.Equal(6, host.GetBaseValue(P));
    }

    [Fact]
    public void M079_AnimOverLocal_EqualPromotion()
    {
        var host = new RecordingHost();
        host.SetValue(P, 7);
        var handle = host.BeginAnimation(P);
        handle.SetValue(7);
        var probe = Probe<int>.Attach(host, P);

        handle.Dispose();

        probe.AssertSilent(); // PD9
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource(P));
    }

    // ───────────── Animation over Style (M80–M83) ─────────────

    [Fact]
    public void M080_AnimOverStyle_Apply()
    {
        var host = new RecordingHost();
        host.AddFrame(new TestValueFrame(K1).With(P, 5));
        var handle = host.BeginAnimation(P);
        var probe = Probe<int>.Attach(host, P);

        handle.SetValue(9);

        Assert.Equal(9, host.GetValue(P));
        Assert.Equal(5, host.GetBaseValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Animation, false), host.GetValueSource(P));
        probe.AssertSingleNotify(5, 9, BindingPriority.Animation);
    }

    [Fact]
    public void M081_AnimOverStyle_Withdraw()
    {
        var host = new RecordingHost();
        host.AddFrame(new TestValueFrame(K1).With(P, 5));
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        var probe = Probe<int>.Attach(host, P);

        handle.Dispose();

        Assert.Equal(5, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Style, false), host.GetValueSource(P));
        probe.AssertSingleNotify(9, 5, BindingPriority.Style);
    }

    [Fact]
    public void M082_AnimOverStyle_MaskedReEmit()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1).With(P, 5);
        host.AddFrame(frame);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        var probe = Probe<int>.Attach(host, P);

        frame.SetEntryValue(P, 6); // re-emit under the animation

        probe.AssertSilent();
        Assert.Equal(6, host.GetBaseValue(P));
        Assert.Equal(9, host.GetValue(P));
    }

    [Fact]
    public void M083_AnimOverStyle_EqualPromotion()
    {
        var host = new RecordingHost();
        host.AddFrame(new TestValueFrame(K1).With(P, 7));
        var handle = host.BeginAnimation(P);
        handle.SetValue(7);
        var probe = Probe<int>.Attach(host, P);

        handle.Dispose();

        probe.AssertSilent();
        Assert.Equal(new ValueSource(BindingPriority.Style, false), host.GetValueSource(P));
    }

    // ───────────── Animation over Default (M88–M91) ─────────────

    [Fact]
    public void M088_AnimOverDefault_Apply()
    {
        var host = new RecordingHost();
        var handle = host.BeginAnimation(P);
        var probe = Probe<int>.Attach(host, P);

        handle.SetValue(9);

        Assert.Equal(9, host.GetValue(P));
        Assert.Equal(0, host.GetBaseValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Animation, false), host.GetValueSource(P));
        probe.AssertSingleNotify(0, 9, BindingPriority.Animation);
    }

    [Fact]
    public void M089_AnimOverDefault_Withdraw()
    {
        var host = new RecordingHost();
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        var probe = Probe<int>.Attach(host, P);

        handle.Dispose();

        Assert.Equal(0, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Default, false), host.GetValueSource(P));
        probe.AssertSingleNotify(9, 0, BindingPriority.Default);
    }

    [Fact]
    public void M090_AnimOverDefault_NoWriterForDefault_BaseIsDefault()
    {
        var host = new Host();
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);

        Assert.Equal(0, host.GetBaseValue(P)); // Default has no writer; the base reads the metadata default
    }

    [Fact]
    public void M091_AnimOverDefault_EqualLaneFlips_BothSilent()
    {
        var host = new RecordingHost();
        var handle = host.BeginAnimation(P);
        var probe = Probe<int>.Attach(host, P);

        handle.SetValue(0); // equals the default
        probe.AssertSilent();
        Assert.Equal(new ValueSource(BindingPriority.Animation, false), host.GetValueSource(P));

        handle.Dispose();
        probe.AssertSilent();
        Assert.Equal(new ValueSource(BindingPriority.Default, false), host.GetValueSource(P));
    }

    // ───────────── Local over Style (M92–M95) ─────────────

    [Fact]
    public void M092_LocalOverStyle_Apply()
    {
        var host = new RecordingHost();
        host.AddFrame(new TestValueFrame(K1).With(P, 5));
        var probe = Probe<int>.Attach(host, P);

        host.SetValue(P, 9);

        Assert.Equal(9, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource(P));
        probe.AssertSingleNotify(5, 9, BindingPriority.LocalValue);
    }

    [Fact]
    public void M093_LocalOverStyle_Withdraw()
    {
        var host = new RecordingHost();
        host.AddFrame(new TestValueFrame(K1).With(P, 5));
        host.SetValue(P, 9);
        var probe = Probe<int>.Attach(host, P);

        host.ClearValue(P);

        Assert.Equal(5, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Style, false), host.GetValueSource(P));
        probe.AssertSingleNotify(9, 5, BindingPriority.Style);
    }

    [Fact]
    public void M094_LocalOverStyle_MaskedReEmit()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1).With(P, 5);
        host.AddFrame(frame);
        host.SetValue(P, 9);
        var probe = Probe<int>.Attach(host, P);

        frame.SetEntryValue(P, 6);

        probe.AssertSilent();
        Assert.Equal(6, host.GetValue(P, BindingPriority.Style));
        Assert.Equal(9, host.GetValue(P));
    }

    [Fact]
    public void M095_LocalOverStyle_EqualPromotion()
    {
        var host = new RecordingHost();
        host.AddFrame(new TestValueFrame(K1).With(P, 7));
        host.SetValue(P, 7);
        var probe = Probe<int>.Attach(host, P);

        host.ClearValue(P);

        probe.AssertSilent();
        Assert.Equal(new ValueSource(BindingPriority.Style, false), host.GetValueSource(P));
    }

    // ───────────── Style over Default (M106–M107) ─────────────

    [Fact]
    public void M106_StyleOverDefault_SourceTransitions_EndToEnd()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1).With(P, 9);
        Assert.Equal(new ValueSource(BindingPriority.Default, false), host.GetValueSource(P));
        var probe = Probe<int>.Attach(host, P);

        host.AddFrame(frame);
        Assert.Equal(new ValueSource(BindingPriority.Style, false), host.GetValueSource(P));
        probe.AssertSingleNotify(0, 9, BindingPriority.Style);

        host.RemoveFrame(frame);
        Assert.Equal(new ValueSource(BindingPriority.Default, false), host.GetValueSource(P));
        probe.AssertSingleNotify(9, 0, BindingPriority.Default);
    }

    [Fact]
    public void M107_FrameValueEqualsDefault_SilentLaneFlip()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, P);

        host.AddFrame(new TestValueFrame(K1).With(P, 0)); // equals the default

        probe.AssertSilent();
        Assert.Equal(new ValueSource(BindingPriority.Style, false), host.GetValueSource(P));
        Assert.True(host.IsSet(P));
    }

    // ───────────── apply-below-apply (M116) ─────────────

    [Fact]
    public void M116_ApplyBelowApply_WeakerLanesSilent()
    {
        var host = new RecordingHost();
        var handle = host.BeginAnimation(P);
        var probe = Probe<int>.Attach(host, P);

        handle.SetValue(9);
        probe.AssertSingleNotify(0, 9, BindingPriority.Animation);

        host.SetValue(P, 5); // weaker apply — silent
        probe.AssertSilent();

        host.AddFrame(new TestValueFrame(K1).With(P, 3)); // weaker still — silent
        probe.AssertSilent();

        Assert.Equal(9, host.GetValue(P));
        Assert.Equal(5, host.GetBaseValue(P));
        Assert.Equal(3, host.GetValue(P, BindingPriority.Style));
    }
    [Fact]
    public void M100_LocalOverDefault_SourceTransitions_EndToEnd()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, P);
        Assert.Equal(new ValueSource(BindingPriority.Default, false), host.GetValueSource(P));

        host.SetValue(P, 9);
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource(P));
        probe.AssertSingleNotify(0, 9, BindingPriority.LocalValue);

        host.ClearValue(P);
        Assert.Equal(new ValueSource(BindingPriority.Default, false), host.GetValueSource(P));
        probe.AssertSingleNotify(9, 0, BindingPriority.Default);
    }

    [Fact]
    public void M101_EqualToDefaultWrite_SilentLaneFlip()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, P);

        host.SetValue(P, 0); // equals the default

        probe.AssertSilent(); // PD9: equal-value lane flip is silent…
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource(P)); // …but the source updates
        Assert.True(host.IsSet(P));
    }

    // ───────────── Animation over Inherited (M84–M87) ─────────────

    [Fact]
    public void M084_AnimOverInherited_Apply()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 5);
        var handle = leaf.BeginAnimation(Pi);

        handle.SetValue(9);

        Assert.Equal(9, leaf.GetValue(Pi));
        Assert.Equal(5, leaf.GetBaseValue(Pi));
        Assert.Equal(new ValueSource(BindingPriority.Animation, false), leaf.GetValueSource(Pi));
    }

    [Fact]
    public void M085_AnimOverInherited_Withdraw_PromotesToInherited()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 5);
        var handle = leaf.BeginAnimation(Pi);
        handle.SetValue(9);
        var probe = Probe<int>.Attach(leaf, Pi); // origin-site op on leaf: full four channels

        handle.Dispose();

        Assert.Equal(5, leaf.GetValue(Pi));
        Assert.Equal(new ValueSource(BindingPriority.Inherited, false), leaf.GetValueSource(Pi));
        probe.AssertSingleNotify(9, 5, BindingPriority.Inherited);
    }

    [Fact]
    public void M086_AnimOverInherited_MaskedAncestorWrite_WalkUpBase()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 5);
        var handle = leaf.BeginAnimation(Pi);
        handle.SetValue(9);
        var rootProbe = Probe<int>.Attach(root, Pi);
        var leafProbe = InheritedProbe<int>.Attach(leaf, Pi);

        root.SetValue(Pi, 6);

        rootProbe.AssertSingleNotify(5, 6, BindingPriority.LocalValue); // root notifies normally
        leafProbe.AssertSilent(); // leaf ordinary channels silent under the animation
        Assert.Equal(9, leaf.GetValue(Pi));
        Assert.Equal(6, leaf.GetBaseValue(Pi)); // walk-up base
    }

    [Fact]
    public void M087_AnimOverInherited_EqualPromotion_SilentSourceFlip()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 7);
        var handle = leaf.BeginAnimation(Pi);
        var probe = InheritedProbe<int>.Attach(leaf, Pi);

        handle.SetValue(7); // equals the inherited base
        probe.AssertSilent();
        Assert.Equal(new ValueSource(BindingPriority.Animation, false), leaf.GetValueSource(Pi));

        handle.Dispose();
        probe.AssertSilent();
        Assert.Equal(new ValueSource(BindingPriority.Inherited, false), leaf.GetValueSource(Pi));
    }

    // ───────────── Local over Inherited (M96–M99) ─────────────

    [Fact]
    public void M096_LocalOverInherited_Apply_OldValueIsInherited()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 5);
        var probe = Probe<int>.Attach(leaf, Pi);

        leaf.SetValue(Pi, 9);

        Assert.Equal(9, leaf.GetValue(Pi));
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), leaf.GetValueSource(Pi));
        probe.AssertSingleNotify(5, 9, BindingPriority.LocalValue); // old value is the inherited one
    }

    [Fact]
    public void M097_LocalOverInherited_Withdraw_PromotesToInherited()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 5);
        leaf.SetValue(Pi, 9);
        var probe = Probe<int>.Attach(leaf, Pi);

        leaf.ClearValue(Pi);

        Assert.Equal(5, leaf.GetValue(Pi));
        Assert.Equal(new ValueSource(BindingPriority.Inherited, false), leaf.GetValueSource(Pi));
        probe.AssertSingleNotify(9, 5, BindingPriority.Inherited);
    }

    [Fact]
    public void M098_LocalOverInherited_MaskedAncestorWrite_Shadowed()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 5);
        leaf.SetValue(Pi, 9);
        var probe = InheritedProbe<int>.Attach(leaf, Pi);

        root.SetValue(Pi, 6);

        probe.AssertSilent(); // shadowed — see also M171's analog
        Assert.Equal(9, leaf.GetValue(Pi));
    }

    [Fact]
    public void M099_LocalOverInherited_EqualPromotion_SilentSourceFlip()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 7);
        leaf.SetValue(Pi, 7);
        var probe = InheritedProbe<int>.Attach(leaf, Pi);

        leaf.ClearValue(Pi);

        probe.AssertSilent();
        Assert.Equal(new ValueSource(BindingPriority.Inherited, false), leaf.GetValueSource(Pi));
    }

    // ───────────── Style over Inherited (M102–M105) ─────────────

    [Fact]
    public void M102_StyleOverInherited_Apply()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 5);
        var probe = Probe<int>.Attach(leaf, Pi);

        leaf.AddFrame(new TestValueFrame(K1).With(Pi, 9));

        Assert.Equal(9, leaf.GetValue(Pi));
        Assert.Equal(new ValueSource(BindingPriority.Style, false), leaf.GetValueSource(Pi));
        probe.AssertSingleNotify(5, 9, BindingPriority.Style);
    }

    [Fact]
    public void M103_StyleOverInherited_Withdraw_PromotesToInherited()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 5);
        var frame = new TestValueFrame(K1).With(Pi, 9);
        leaf.AddFrame(frame);
        var probe = Probe<int>.Attach(leaf, Pi);

        leaf.RemoveFrame(frame);

        Assert.Equal(5, leaf.GetValue(Pi));
        Assert.Equal(new ValueSource(BindingPriority.Inherited, false), leaf.GetValueSource(Pi));
        probe.AssertSingleNotify(9, 5, BindingPriority.Inherited);
    }

    [Fact]
    public void M104_StyleOverInherited_MaskedAncestorWrite_FrameShadowsTheWalk()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 5);
        leaf.AddFrame(new TestValueFrame(K1).With(Pi, 9));
        var probe = InheritedProbe<int>.Attach(leaf, Pi);

        root.SetValue(Pi, 6);

        probe.AssertSilent();
        Assert.Equal(9, leaf.GetValue(Pi));
    }

    [Fact]
    public void M105_StyleOverInherited_EqualPromotion_SilentSourceFlip()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 7);
        var frame = new TestValueFrame(K1).With(Pi, 7);
        leaf.AddFrame(frame);
        var probe = InheritedProbe<int>.Attach(leaf, Pi);

        leaf.RemoveFrame(frame);

        probe.AssertSilent();
        Assert.Equal(new ValueSource(BindingPriority.Inherited, false), leaf.GetValueSource(Pi));
    }

    // ───────────── Inherited over Default (M108–M111) ─────────────

    [Fact]
    public void M108_InheritedOverDefault_Apply_PropagatedDelivery()
    {
        var (root, mid, leaf) = Chain();
        var midProbe = InheritedProbe<int>.Attach(mid, Pi);
        var leafProbe = InheritedProbe<int>.Attach(leaf, Pi);

        root.SetValue(Pi, 9);

        Assert.Equal(9, leaf.GetValue(Pi));
        Assert.Equal(new ValueSource(BindingPriority.Inherited, false), leaf.GetValueSource(Pi));
        midProbe.AssertSinglePropagated(0, 9, BindingPriority.Inherited); // A3/A4 delivery (PD22)
        leafProbe.AssertSinglePropagated(0, 9, BindingPriority.Inherited);
    }

    [Fact]
    public void M109_InheritedOverDefault_Withdraw_PromotesToDefault()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 9);
        var probe = InheritedProbe<int>.Attach(leaf, Pi);

        root.ClearValue(Pi);

        Assert.Equal(0, leaf.GetValue(Pi));
        Assert.Equal(new ValueSource(BindingPriority.Default, false), leaf.GetValueSource(Pi));
        probe.AssertSinglePropagated(9, 0, BindingPriority.Default); // PD22 channel set
    }

    [Fact]
    public void M110_EqualToDefaultAncestorWrite_SilentLeafSourceFlip()
    {
        var (root, _, leaf) = Chain();
        var rootProbe = Probe<int>.Attach(root, Pi);
        var leafProbe = InheritedProbe<int>.Attach(leaf, Pi);

        root.SetValue(Pi, 0); // equals the default — silent everywhere (PD9)

        rootProbe.AssertSilent();
        leafProbe.AssertSilent();
        Assert.Equal(new ValueSource(BindingPriority.Inherited, false), leaf.GetValueSource(Pi)); // lane flip
    }

    [Fact]
    public void M111_DetachParent_ReResolvesOverInheritingPropertyIds()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 9);
        var probe = InheritedProbe<int>.Attach(leaf, Pi);

        leaf.SetInheritanceParent(null);

        Assert.Equal(0, leaf.GetValue(Pi));
        probe.AssertSinglePropagated(9, 0, BindingPriority.Default);
    }

    // ───────────── Full ladder (M112–M115, M117) ─────────────

    [Fact]
    public void M112_FullLadder_PeelTopDown_FourPromotions()
    {
        var (root, leaf, frame, handle) = BuildM112Stack();
        Assert.Equal(9, leaf.GetValue(Pi));
        var probe = InheritedProbe<int>.Attach(leaf, Pi);

        handle.Dispose();
        leaf.ClearValue(Pi);
        leaf.RemoveFrame(frame);
        root.ClearValue(Pi);

        // Four notifications, each exactly once, each at the promoted lane: the first three are
        // leaf-local operations (full ordinary channels), the fourth is propagated (PD22 set).
        (int, int, BindingPriority)[] expected =
        [
            (9, 7, BindingPriority.LocalValue),
            (7, 5, BindingPriority.Style),
            (5, 2, BindingPriority.Inherited),
            (2, 0, BindingPriority.Default),
        ];
        Assert.Equal(expected, probe.Typed);
        Assert.Equal(expected.Select(e => ((object?)e.Item1, (object?)e.Item2, e.Item3)), probe.Untyped);
        Assert.Equal(expected[..3], probe.OrdinaryVirtual); // origin-site channel
        Assert.Equal(expected[3..], probe.Inherited);       // propagated channel (A3)
        Assert.Equal(0, leaf.GetValue(Pi));
    }

    [Fact]
    public void M113_FullLadder_BaseTracksStrongestSubAnimationLane()
    {
        var (root, leaf, frame, _) = BuildM112Stack();

        Assert.Equal(7, leaf.GetBaseValue(Pi));
        leaf.ClearValue(Pi);
        Assert.Equal(5, leaf.GetBaseValue(Pi));
        leaf.RemoveFrame(frame);
        Assert.Equal(2, leaf.GetBaseValue(Pi));
        root.ClearValue(Pi);
        Assert.Equal(0, leaf.GetBaseValue(Pi));
    }

    [Theory]
    [InlineData(BindingPriority.Animation, 9)]
    [InlineData(BindingPriority.LocalValue, 7)]
    [InlineData(BindingPriority.Style, 5)]
    [InlineData(BindingPriority.Inherited, 2)]
    [InlineData(BindingPriority.Default, 0)]
    public void M114_FullLadder_MaxPriorityProbes(BindingPriority maxPriority, int expected)
    {
        var (_, leaf, _, _) = BuildM112Stack();

        Assert.Equal(expected, leaf.GetValue(Pi, maxPriority));
    }

    [Fact]
    public void M115_ReApplyAfterFullPeel_NoGhostState()
    {
        var (root, leaf, frame, handle) = BuildM112Stack();
        handle.Dispose();
        leaf.ClearValue(Pi);
        leaf.RemoveFrame(frame);
        root.ClearValue(Pi);
        var probe = Probe<int>.Attach(leaf, Pi);

        leaf.SetValue(Pi, 7);

        Assert.Equal(7, leaf.GetValue(Pi));
        probe.AssertSingleNotify(0, 7, BindingPriority.LocalValue);
    }

    [Fact]
    public void M117_WithdrawOutOfOrder_PromotionSkipsVacatedLanes()
    {
        var (_, leaf, frame, handle) = BuildM112Stack();
        var probe = InheritedProbe<int>.Attach(leaf, Pi);

        leaf.ClearValue(Pi); // middle lanes first, under the animation — masked
        probe.AssertSilent();
        leaf.RemoveFrame(frame);
        probe.AssertSilent();

        handle.Dispose();
        Assert.Equal((9, 2, BindingPriority.Inherited), Assert.Single(probe.Typed)); // skips Local and Style
        Assert.Equal((9, 2, BindingPriority.Inherited), Assert.Single(probe.OrdinaryVirtual));
        Assert.Empty(probe.Inherited); // origin-site promotion, not a propagated delivery
        Assert.Equal(2, leaf.GetValue(Pi));
    }
}
