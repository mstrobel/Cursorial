using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

// ReSharper disable InconsistentNaming

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

        leaf.SetCurrentValue(Pi, 6); // lazy-read inheritance holds no leaf entry ⇒ as-Local STORAGE (M118 rule)

        Assert.Equal(6, leaf.GetValue(Pi));
        // Provenance reports the underlying Inherited source (+cur), never LocalValue (M118 amended
        // 2026-07-12); the notification carries the replaced lane (A11).
        Assert.Equal(new ValueSource(BindingPriority.Inherited, IsCurrentValue: true), leaf.GetValueSource(Pi));
        leafProbe.AssertSingleNotify(5, 6, BindingPriority.Inherited);

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

    [Fact] // M118b (added 2026-07-12) — the graft under an animation: provenance still reports the underlying source
    public void M118b_Graft_UnderAnimation_BasePriorityReportsUnderlying()
    {
        var host = new RecordingHost();
        host.SetCurrentValue(P, 4); // the pure graft
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);

        var source = host.GetValueSource(P);
        Assert.Equal(BindingPriority.Animation, source.Priority);   // the animation holds the effective
        Assert.Equal(BindingPriority.Default, source.BasePriority); // the base is the graft ⇒ underlying Default (PD27)
        Assert.Same(UIProperty.UnsetValue, host.ReadLocalValue(P)); // still invisible to local authorship (M264c)
    }

    [Fact] // M125b (added 2026-07-12) — the overlay strip on the StyleTrigger lane
    public void M125b_ClearValue_StripsOverlay_OnStyleTriggerLane()
    {
        var host = new RecordingHost();
        host.AddFrame(new TestValueFrame(K1, priority: BindingPriority.StyleTrigger).With(P, 5));
        host.SetCurrentValue(P, 6);
        Assert.Equal(new ValueSource(BindingPriority.StyleTrigger, IsCurrentValue: true), host.GetValueSource(P));
        var probe = Probe<int>.Attach(host, P);

        host.ClearValue(P);

        probe.AssertSingleNotify(6, 5, BindingPriority.StyleTrigger);
        Assert.Equal(new ValueSource(BindingPriority.StyleTrigger, IsCurrentValue: false), host.GetValueSource(P));
    }

    [Fact] // M125c (added 2026-07-12) — the overlay strip on the Template lane (M292 untouched: CV never removes the template VALUE)
    public void M125c_ClearValue_StripsOverlay_OnTemplateLane_TemplateValueSurvives()
    {
        var host = new RecordingHost();
        using (TemplateInstantiationScope.Enter())
            host.SetValue(P, 5); // the template literal
        host.SetCurrentValue(P, 6); // the M288 overwrite riding the Template lane
        var probe = Probe<int>.Attach(host, P);

        host.ClearValue(P); // strips the overlay; the template's stored source value resurfaces

        probe.AssertSingleNotify(6, 5, BindingPriority.Template);
        Assert.Equal(new ValueSource(BindingPriority.Template, IsCurrentValue: false), host.GetValueSource(P));

        host.ClearValue(P); // and M292 still holds: no overlay, no local ⇒ silent; the template value is NOT local
        probe.AssertSilent();
        Assert.Equal(5, host.GetValue(P));
    }

    [Fact] // M125d (added 2026-07-12; re-pinned same day, audit) — under an active animation ClearValue leaves the overlay to the lane
    public void M125d_ClearValue_UnderAnimation_LeavesOverlayToTheLane()
    {
        // The overwrite rode the ANIMATED effective (M131); only the animation can re-produce its
        // value, and a Holding animation never pushes again — so ClearValue must NOT drop the +cur
        // bit here (that would record an undo that never happened and present a still-effective
        // overwrite as the animation's own output). The overlay dies by the lane's own rules: the
        // next push (M129) or handle disposal (M130).
        var host = new RecordingHost();
        host.AddFrame(new TestValueFrame(K1).With(P, 3));
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);
        host.SetCurrentValue(P, 11); // overwrites the ANIMATED effective (M128)
        var probe = Probe<int>.Attach(host, P);

        host.ClearValue(P);

        probe.AssertSilent();
        Assert.Equal(11, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Animation, IsCurrentValue: true), host.GetValueSource(P)); // truthful +cur

        handle.SetValue(12); // the animation's next push clobbers the overlay (M129) — unchanged
        probe.AssertSingleNotify(11, 12, BindingPriority.Animation);
        Assert.Equal(new ValueSource(BindingPriority.Animation, IsCurrentValue: false), host.GetValueSource(P));
    }

    [Fact] // M125e (added 2026-07-12, audit) — the strip is independent of a co-evicted valueless local entry
    public void M125e_ClearValue_StripsOverlay_WhileEvictingValuelessLocalEntry()
    {
        var host = new RecordingHost();
        host.AddFrame(new TestValueFrame(K1).With(P, 5));
        var listener = new EvictionRecorder();
        host.Bind(P, BindingPriority.LocalValue, listener); // valueless (A8) — LocalEntry set, HasLocal false
        host.SetCurrentValue(P, 6); // rides the producer lane (+cur)
        var probe = Probe<int>.Attach(host, P);

        host.ClearValue(P); // must BOTH evict the entry (A9) and strip the overlay (M125)

        Assert.Single(listener.Evictions);
        probe.AssertSingleNotify(6, 5, BindingPriority.Style);
        Assert.Equal(5, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Style, IsCurrentValue: false), host.GetValueSource(P));
    }

    [Fact] // M118c (added 2026-07-12, audit) — an animation episode over the graft does not erase its +cur provenance
    public void M118c_Graft_SurvivesAnimationEpisode_ProvenanceKeepsCur()
    {
        var host = new RecordingHost();
        host.SetCurrentValue(P, 4); // the pure graft: (Default, +cur)
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);   // M129 clears the shared entry bit...
        handle.Dispose();     // ...and M130 keeps it false as the graft base resurfaces

        // The graft IS the +cur signal (its existence is the SetCurrentValue artifact): the value
        // is still the deliberate overwrite, so provenance must NOT read as an untouched default —
        // "Kind == Default && !IsCurrentValue ⇒ safe to replace" stays sound (the ToggleButton
        // FB-27 gate depends on it).
        Assert.Equal(4, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Default, IsCurrentValue: true), host.GetValueSource(P));
        Assert.Same(UIProperty.UnsetValue, host.ReadLocalValue(P));

        host.ClearValue(P); // and the undo still works on the survived graft
        Assert.Equal(0, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Default, IsCurrentValue: false), host.GetValueSource(P));
    }

    [Fact] // M127b (added 2026-07-12, audit) — the inheriting graft fans out to descendants at Inherited, not Default
    public void M127b_GraftOverDefault_Inheriting_DescendantsReceiveInherited()
    {
        var root = new RecordingHost();
        var leaf = new RecordingHost();
        leaf.SetInheritanceParent(root);
        var probe = InheritedProbe<int>.Attach(leaf, Pi);

        root.SetCurrentValue(Pi, 5); // the graft's ORIGIN notification carries Default (PD27/A11)...

        // ...but the origin's storage CONTRIBUTES (the leaf reads 5 through it), so the descendant
        // fan-out lane is Inherited — matching the leaf's own GetValueSource (M108/M109's premise
        // tested by contribution, not by the origin lane).
        Assert.Equal(5, leaf.GetValue(Pi));
        Assert.Equal(new ValueSource(BindingPriority.Inherited, IsCurrentValue: false), leaf.GetValueSource(Pi));
        Assert.Equal([(0, 5, BindingPriority.Inherited)], probe.Typed);
    }

    [Fact] // M133b (added 2026-07-12, audit) — CoerceValue on a graft notifies at the underlying lane, never LocalValue
    public void M133b_CoerceValue_OnGraft_NotifiesAtUnderlyingLane()
    {
        var host = new RecordingHost();
        host.SetCurrentValue(Pcd2, 250); // ceiling 100 ⇒ eff=100 (the graft holds raw 250)
        Assert.Equal(100, host.GetValue(Pcd2));
        var probe = Probe<int>.Attach(host, Pcd2);

        host.SetValue(Pmax2, 300); // raise the ceiling
        host.CoerceValue(Pcd2);    // re-runs against the grafted raw 250

        probe.AssertSingleNotify(100, 250, BindingPriority.Default); // PD27: a graft never surfaces LocalValue
        Assert.Equal(new ValueSource(BindingPriority.Default, IsCurrentValue: true), host.GetValueSource(Pcd2));
    }

    /// <summary>The M133b ceiling property (the M300 pattern, §6-local registrations).</summary>
    private static readonly StyledProperty<int> Pmax2 = UIProperty.Register<Host, int>(UniqueName("M133bMax"), defaultValue: 100);

    /// <summary>An instance-state coercer clamping to <see cref="Pmax2"/> (the M133b probe).</summary>
    private static readonly StyledProperty<int> Pcd2 = UIProperty.Register<Host, int>(
        UniqueName("M133bPc"), coerce: static (o, v) => Math.Min(v, o.GetValue(Pmax2)));

    [Fact]
    public void M125_ClearValue_StripsCurrentValueOverlay_RestoresProducerValue()
    {
        // Amended 2026-07-12: "ClearValue undoes SetCurrentValue" is UNIVERSAL — CV also strips a
        // +cur overlay riding a producer lane, restoring the lane's stored source value in one
        // notification at that lane (was: pinned no-op, leaving the overlay unremovable).
        var host = new RecordingHost();
        host.AddFrame(new TestValueFrame(K1).With(P, 5));
        host.SetCurrentValue(P, 6);
        var probe = Probe<int>.Attach(host, P);

        host.ClearValue(P);

        probe.AssertSingleNotify(6, 5, BindingPriority.Style);
        Assert.Equal(5, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Style, IsCurrentValue: false), host.GetValueSource(P));

        host.ClearValue(P); // no local, no overlay: still the silent no-op (M21)
        probe.AssertSilent();
        Assert.Equal(5, host.GetValue(P));
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
    public void M118_SetCurrentValue_NoEntry_GraftsAsLocalStorage_ReportsUnderlyingSource()
    {
        // Amended 2026-07-12: "behaves as Local" is STORAGE semantics only (the graft shadows the
        // subtree and evaporates on a producer's arrival, unchanged). PROVENANCE reports the
        // underlying source the overlay rode — WPF parity: SetCurrentValue never changes the
        // source, so Kind == Default stays a sound "not set deliberately" test. The notification
        // carries the replaced (underlying) lane per A11.
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, P);

        host.SetCurrentValue(P, 4);

        Assert.Equal(4, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Default, true), host.GetValueSource(P)); // Default+cur
        probe.AssertSingleNotify(0, 4, BindingPriority.Default);
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
        probe.AssertSingleNotify(0, 100, BindingPriority.Default); // graft notifies at the replaced lane (M118', A11)
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
