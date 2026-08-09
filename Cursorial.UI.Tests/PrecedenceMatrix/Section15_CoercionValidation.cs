using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

// ReSharper disable RedundantCast
// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>Matrix §15 — coercion and validation (M230–M243).</summary>
public class Section15_CoercionValidation
{
    [Fact]
    public void M242_InheritedReads_NeverReEnterTheCoercer()
    {
        var count = 0;
        var picc = UIProperty.Register<Host, int>(
            UniqueName("Picc"), inherits: true,
            coerce: (_, v) =>
            {
                count++;
                return Math.Clamp(v, 0, 100);
            });
        var (root, mid, leaf) = Chain();

        root.SetValue(picc, 250);
        Assert.Equal(1, count); // the set site

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(100, leaf.GetValue(picc));
            Assert.Equal(100, mid.GetValue(picc));
        }

        Assert.Equal(1, count); // inherited reads never re-enter the coercer
    }

    [Fact]
    public void M235_FrameValues_CoercedAtEffectiveComputation()
    {
        var host = new Host();

        host.AddFrame(new TestValueFrame(10).With(Pc, 250));

        Assert.Equal(100, host.GetValue(Pc));
        Assert.Equal(new ValueSource(BindingPriority.Style, false), host.GetValueSource(Pc));
    }

    [Fact]
    public void M239_ProducerMouth_DiscardsWithDiagnostic_NoThrow()
    {
        var host = new RecordingHost();
        var entry = host.Bind(Pcv);
        entry.SetValue(50);
        var probe = Probe<int>.Attach(host, Pcv);

        var rejections = new List<object?>();
        RejectedValueHandler hook = (_, _, value) => rejections.Add(value);
        UIDiagnostics.RejectedValue += hook;
        try
        {
            entry.SetValue(160); // validate rejects raw 160 — no throw at a producer mouth
        }
        finally
        {
            UIDiagnostics.RejectedValue -= hook;
        }

        Assert.Equal([(object?)160], rejections);
        probe.AssertSilent();
        Assert.Equal(50, host.GetValue(Pcv)); // previous value kept
    }

    /// <summary>The M232/M233 instance-state ceiling property (read by <see cref="PcDyn"/>'s coercer).</summary>
    private static readonly StyledProperty<int> Pmax =
        UIProperty.Register<Host, int>(UniqueName("M232Max"), defaultValue: 100);

    /// <summary>An instance-state coercer: clamp to the ceiling carried by <see cref="Pmax"/>.</summary>
    private static readonly StyledProperty<int> PcDyn = UIProperty.Register<Host, int>(
        UniqueName("M232Pc"), coerce: static (o, v) => Math.Min(v, o.GetValue(Pmax)));

    [Fact]
    public void M230_LocalWrite_CoercedAtEffectiveComputation()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, Pc);

        host.SetValue(Pc, 250);

        Assert.Equal(100, host.GetValue(Pc)); // clamped
        probe.AssertSingleNotify(0, 100, BindingPriority.LocalValue);
        // The raw slot holds 250 (PD6) — behaviorally observable via the M232 dance.
    }

    [Fact]
    public void M231_DifferentRaw_SameCoercedResult_IsSilent()
    {
        var host = new RecordingHost();
        host.SetValue(Pc, 250); // eff = 100
        var probe = Probe<int>.Attach(host, Pc);

        host.SetValue(Pc, 120); // raw differs, coerced result equal

        probe.AssertSilent();
        Assert.Equal(100, host.GetValue(Pc));
        Assert.Equal(120, host.ReadLocalValue(Pc)); // the raw slot is last-writer-wins UNDER the gate (M231a)
    }

    [Fact] // M231a — the gated write's raw survives: unwiring the ceiling reverts to the LATEST write, not the first
    public void M231a_GatedWrite_UpdatesTheRawSlot_ForTheCoerceValueDance()
    {
        var host = new RecordingHost();
        host.SetValue(PcDyn, 250); // ceiling 100 ⇒ eff = 100, raw = 250
        var probe = Probe<int>.Attach(host, PcDyn);

        host.SetValue(PcDyn, 120); // gated: coerced 100 == base 100 ⇒ silent — but the raw slot must move
        probe.AssertSilent();

        host.SetValue(Pmax, 300); // raise the ceiling
        host.CoerceValue(PcDyn);  // re-runs against the raw — the author's LAST write (120), never the first (250)

        Assert.Equal(120, host.GetValue(PcDyn));
        probe.AssertSingleNotify(100, 120, BindingPriority.LocalValue);
    }

    [Fact] // M231b — the gate re-derives the coercion provenance flags from the new raw
    public void M231b_GatedWrite_RederivesIsCoerced()
    {
        var host = new RecordingHost();
        host.SetValue(Pc, 250); // eff = 100, coerced
        Assert.True(host.GetValueSource(Pc).IsCoerced);

        host.SetValue(Pc, 100); // gated (coerced 100 == base 100) — but the raw now equals the stored base

        Assert.Equal(100, host.GetValue(Pc));
        Assert.False(host.GetValueSource(Pc).IsCoerced); // the local contribution is no longer coercer-modified
        Assert.Equal(100, host.ReadLocalValue(Pc));
    }

    [Fact]
    public void M232_CoerceValue_RerunsAgainstRawValue()
    {
        var host = new RecordingHost();
        host.SetValue(PcDyn, 250); // ceiling 100 ⇒ eff = 100, raw = 250
        Assert.Equal(100, host.GetValue(PcDyn));
        var probe = Probe<int>.Attach(host, PcDyn);

        host.SetValue(Pmax, 300); // raise the ceiling
        host.CoerceValue(PcDyn);  // re-runs against the RAW 250 (PD6 — the WPF Maximum/Value dance)

        Assert.Equal(250, host.GetValue(PcDyn));
        probe.AssertSingleNotify(100, 250, BindingPriority.LocalValue);
    }

    [Fact]
    public void M233_CoerceValue_LoweredCeiling_NotifiesAtEffectiveLane()
    {
        var host = new RecordingHost();
        host.SetValue(PcDyn, 250);
        host.SetValue(Pmax, 300);
        host.CoerceValue(PcDyn); // eff = 250 (the M232 state)
        var probe = Probe<int>.Attach(host, PcDyn);

        host.SetValue(Pmax, 50); // lower the ceiling
        host.CoerceValue(PcDyn);

        Assert.Equal(50, host.GetValue(PcDyn));
        probe.AssertSingleNotify(250, 50, BindingPriority.LocalValue); // priority = current effective lane
    }

    // ───────────────────────── the M233 seam family (M233a–M233d) ─────────────────────────
    //
    // `RecoerceLocal` hands the A20 winning-base change to user code (PD18) BEFORE it writes the
    // effective lane, so an observer can recompute the entry end-to-end underneath the suspended
    // operation. Every row below drives that one seam, on a fixture only it can produce, and each
    // kills a mutant of the two-test supersession guard:
    //
    //                                        base compare    no-change gate
    //   M233a  clear, `coerced` == default(T)      ·               PIN        + descendant fan-out
    //   M233b  clear, `coerced` != default(T)     PIN               ·         + read/announce agreement
    //   M233c  re-write to a different value      PIN               ·         + lane agreement
    //   M233d  clear THEN re-write the same        ·               PIN        + raw-slot ownership
    //
    // (There is deliberately no `!HasLocal` test: the store's "un-animated ⇒ `Value == BaseValue`
    // after any completed operation" invariant makes such a disjunct unkillable — M233a is the row
    // that would have pinned it, and the no-change gate catches that case instead.)

    /// <summary>The M233 seam family's ceiling property (instance state read by <see cref="PcInh"/>'s coercer; NOT inheriting).</summary>
    private static readonly StyledProperty<int> PmaxInh =
        UIProperty.Register<Host, int>(UniqueName("M233SeamMax"), defaultValue: 100);

    /// <summary>The M233 seam family's probe: an INHERITING property with an instance-state coercer (the M232 dance, inherited tier).</summary>
    private static readonly StyledProperty<int> PcInh = UIProperty.Register<Host, int>(
        UniqueName("M233SeamPc"), coerce: static (o, v) => Math.Min(v, o.GetValue(PmaxInh)), inherits: true);

    /// <summary>
    /// A winning-base observer (A20) that issues exactly ONE re-entrant <c>ClearValue</c> from its first
    /// delivery — the M307 shape at the <c>CoerceValue</c> mouth (M233a/M233b).
    /// </summary>
    private sealed class ReentrantClearer(UIObject target, StyledProperty<int> property) : IValueObserver<int>
    {
        private bool _fired;

        public List<(int OldBase, int NewBase)> BaseDeliveries { get; } = [];

        public List<(int Old, int New, BindingPriority Priority)> OrdinaryDeliveries { get; } = [];

        public void OnPropertyChanged(UIObject source, UIProperty p, int oldValue, int newValue, BindingPriority priority)
            => OrdinaryDeliveries.Add((oldValue, newValue, priority));

        public void OnBaseValueChanged(UIObject source, UIProperty p, int oldBase, int newBase, bool isAnimated)
        {
            BaseDeliveries.Add((oldBase, newBase));

            if (_fired)
                return;

            _fired = true;
            target.ClearValue(property);
        }
    }

    /// <summary>
    /// The seam's OTHER supersession (M233c): a winning-base observer that REPLACES the local
    /// contribution — one re-entrant <c>SetValue</c> from its first delivery — instead of retracting it.
    /// <c>HasLocal</c> stays true throughout, so only the stored base can tell the suspended re-coercion
    /// that it has been superseded.
    /// </summary>
    private sealed class ReentrantWriter(UIObject target, StyledProperty<int> property, int rawValue) : IValueObserver<int>
    {
        private bool _fired;

        public List<(int OldBase, int NewBase)> BaseDeliveries { get; } = [];

        public List<(int Old, int New, BindingPriority Priority)> OrdinaryDeliveries { get; } = [];

        public void OnPropertyChanged(UIObject source, UIProperty p, int oldValue, int newValue, BindingPriority priority)
            => OrdinaryDeliveries.Add((oldValue, newValue, priority));

        public void OnBaseValueChanged(UIObject source, UIProperty p, int oldBase, int newBase, bool isAnimated)
        {
            BaseDeliveries.Add((oldBase, newBase));

            if (_fired)
                return;

            _fired = true;
            target.SetValue(property, rawValue);
        }
    }

    /// <summary>
    /// The seam's residue shape (M233d): a winning-base observer that retracts the contribution and then
    /// re-establishes it with a raw of its own whose coerced result is the one the suspended re-coercion
    /// computed. Supersession is satisfied on resume — the stored base matches again — yet the value was
    /// already published by the nested writer.
    /// </summary>
    private sealed class ReentrantClearThenWriter(UIObject target, StyledProperty<int> property, int rawValue) : IValueObserver<int>
    {
        private bool _fired;

        public List<(int OldBase, int NewBase)> BaseDeliveries { get; } = [];

        public List<(int Old, int New, BindingPriority Priority)> OrdinaryDeliveries { get; } = [];

        public void OnPropertyChanged(UIObject source, UIProperty p, int oldValue, int newValue, BindingPriority priority)
            => OrdinaryDeliveries.Add((oldValue, newValue, priority));

        public void OnBaseValueChanged(UIObject source, UIProperty p, int oldBase, int newBase, bool isAnimated)
        {
            BaseDeliveries.Add((oldBase, newBase));

            if (_fired)
                return;

            _fired = true;
            target.ClearValue(property);
            target.SetValue(property, rawValue);
        }
    }

    /// <summary>
    /// <b>M233a — the retraction whose base collides with <c>default(T)</c> (added 2026-08-09; scenario
    /// re-cut 2026-08-09 so it stops duplicating
    /// <see cref="M233b_CoerceValue_WhoseBaseObserverClears_DoesNotResurrectTheRetractedContribution"/>).</b>
    /// The ceiling is lowered to <b>0</b>, so the re-coercion's <c>coerced</c> is <c>0</c> — exactly the
    /// <c>default(int)</c> that <c>Reevaluate</c> stores into <c>BaseValue</c> when it retracts the last
    /// contribution. The guard's base compare therefore AGREES on resume — this is the one retraction
    /// shape it cannot see — and the no-change test is what stops the operation; that is what this row
    /// pins. What the stopped operation would otherwise publish is a pure no-op announcement
    /// — <c>(0 → 0)</c> at the storeless tier — which on an inheriting property is not merely noise: it
    /// fans out (A3/A4) and tells every descendant that the property is now <c>0</c>, when the retraction
    /// already told them (truthfully) that it is the ancestor's <c>42</c>. Hence the target is
    /// <c>mid</c>, so the fan-out is observable on <c>leaf</c>.
    /// <para>
    /// The retraction's own delivery carries <see cref="BindingPriority.Inherited"/> by
    /// <see cref="ValueStore.Reevaluate{T}"/>'s Unset-promotion arm — §0.3 rule 2 / PD10, the promoted
    /// lane, the same substitution M20 makes for a plain <c>ClearValue</c>. (Not A11: that rule is
    /// <c>SetCurrentValue</c> reporting the REPLACED lane, and nothing here grafts.)
    /// </para>
    /// </summary>
    [Fact] // M233a
    public void M233a_CoerceValue_WhoseBaseObserverClears_ToADefaultValuedBase_PublishesNothing()
    {
        var (root, mid, leaf) = Chain();
        root.SetValue(PcInh, 42);          // the contributing ancestor (root's own ceiling is the default 100)
        mid.SetValue(PcInh, 250);          // ceiling 100 ⇒ mid eff = 100, raw = 250
        mid.SetValue(PmaxInh, 0);          // LOWER the ceiling to zero: the next Co coerces to `default(int)`
        Assert.Equal(100, mid.GetValue(PcInh));
        Assert.Equal(100, leaf.GetValue(PcInh)); // the descendant reads mid's contribution

        var observer = new ReentrantClearer(mid, PcInh);
        using var baseSubscription = mid.AddObserver(PcInh, observer, new ObserverOptions { IncludeBaseChanges = true });
        using var ordinarySubscription = mid.AddObserver(PcInh, observer); // the base subscription delivers on that channel only (M178)
        var descendant = InheritedProbe<int>.Attach(leaf, PcInh);

        mid.CoerceValue(PcInh);

        // ONE ordinary delivery — the nested retraction's, which completes first (PD18). On resume the
        // stored base (`default(int)` = 0) EQUALS the re-coercion's `coerced` (0), so the base compare
        // waves the operation through and only the no-change test stops it; without that test the
        // operation announces (0 → 0) here and fans that 0 out over the truthful 42 below.
        // (§0.3 rule 1 — no `Unset` on a lane-bearing notification — is subsumed by the exact list:
        // a separate `DoesNotContain(… == Unset)` could never fail while this assertion holds.)
        Assert.Equal([(100, 42, BindingPriority.Inherited)], observer.OrdinaryDeliveries);

        // …and exactly one propagated delivery on the descendant, carrying the same values (PD22).
        descendant.AssertSinglePropagated(100, 42, BindingPriority.Inherited);

        // The A20 channel is laneless (it carries `isAnimated`, never a `BindingPriority`) and sees both
        // base moves: the re-coercion's own move to the colliding `default(int)`, then the retraction's.
        Assert.Equal([(100, 0), (0, 42)], observer.BaseDeliveries);

        // Both nodes agree with what was announced.
        Assert.False(mid.IsSet(PcInh));
        Assert.Equal(42, mid.GetValue(PcInh));
        Assert.Equal(42, leaf.GetValue(PcInh));
    }

    /// <summary>
    /// <b>M233b — the resurrection (added 2026-08-09).</b> <c>RecoerceLocal</c> writes
    /// <c>Value = coerced</c> <em>after</em> the A20 base dispatch it hands to user code (PD18). A base
    /// observer that <c>ClearValue</c>s from inside that dispatch retracts the very local contribution the
    /// re-coercion is publishing — and the suspended operation resumed and stored it anyway, resurrecting
    /// a contribution that no longer exists and announcing (<c>0 → 250</c>) a value the very next
    /// <c>GetValue</c> (42, the inherited tier) disagreed with. The ceiling here is RAISED (300), so
    /// <c>coerced</c> (250) differs from the <c>default(int)</c> a retraction stores and the guard's
    /// base compare sees it — the retraction arm of the same test M233c pins from the replacement side.
    /// <para>
    /// The discriminating assertion is the DELIVERY LIST. <c>IsSet</c>/<c>GetValue</c>/<c>GetBaseValue</c>
    /// /<c>GetValueSource</c> all answered correctly even before the fix — the reads route on
    /// <c>HasLocal</c>/<c>BasePriority</c>/<c>EffectivePriority</c>, none of which the resumed write
    /// touched, which is exactly why the bug was an announcement the reads disagreed with rather than a
    /// wrong read. They are asserted here as the agreement contract, not as the pin.
    /// </para>
    /// </summary>
    [Fact] // M233b
    public void M233b_CoerceValue_WhoseBaseObserverClears_DoesNotResurrectTheRetractedContribution()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(PcInh, 42);          // the contributing ancestor
        leaf.SetValue(PcInh, 250);         // ceiling 100 ⇒ leaf eff = 100, raw = 250
        leaf.SetValue(PmaxInh, 300);       // RAISE the ceiling: the next Co moves the base to 250
        Assert.Equal(100, leaf.GetValue(PcInh));

        var observer = new ReentrantClearer(leaf, PcInh);
        using var baseSubscription = leaf.AddObserver(PcInh, observer, new ObserverOptions { IncludeBaseChanges = true });
        using var ordinarySubscription = leaf.AddObserver(PcInh, observer);

        leaf.CoerceValue(PcInh);

        // The retraction is the ONLY ordinary delivery: once the contribution it was re-coercing is
        // gone, the suspended re-coercion has nothing left to announce. (Before the fix a second
        // delivery followed, announcing new = 250.)
        Assert.Equal([(100, 42, BindingPriority.Inherited)], observer.OrdinaryDeliveries);

        // The read is the post-condition, and it agrees with the announcement: the re-entrant
        // ClearValue retracted the only local contribution, so the leaf answers the inherited tier.
        Assert.False(leaf.IsSet(PcInh));
        Assert.Equal(42, leaf.GetValue(PcInh));

        // No stale residue behind the read either. `GetBaseValue` probes the entry's BASE storage and
        // `GetValueSource` its provenance — both must report the retraction, not the resurrected 250.
        Assert.Equal(42, leaf.GetBaseValue(PcInh));
        Assert.Equal(new ValueSource(BindingPriority.Inherited, false), leaf.GetValueSource(PcInh));

        // The A20 channel sees both base moves in order: the re-coercion's own, then the retraction's.
        Assert.Equal([(100, 250), (250, 42)], observer.BaseDeliveries);
    }

    /// <summary>
    /// <b>M233c — the replacement across the seam (added 2026-08-09).</b> The retraction's twin: the base
    /// observer issues a re-entrant <c>SetValue</c> of a DIFFERENT value instead of clearing, so
    /// <c>HasLocal</c> never goes false and the stored base is the only witness that the suspended
    /// re-coercion has been superseded. Within the local lane, last writer wins (§2.2) — the nested write
    /// is the last writer, and it has already published its own change. Resuming the re-coercion would
    /// clobber the newer value with this operation's stale coercion AND leave an un-animated entry whose
    /// two lanes disagree: <c>GetValue</c> answering 250 while <c>GetBaseValue</c> answers 7.
    /// </summary>
    [Fact] // M233c
    public void M233c_CoerceValue_WhoseBaseObserverRewrites_DoesNotClobberTheNewerWrite()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(PcInh, 42);          // the contributing ancestor
        leaf.SetValue(PcInh, 250);         // ceiling 100 ⇒ leaf eff = 100, raw = 250
        leaf.SetValue(PmaxInh, 300);       // raise the ceiling: the next Co would move the base to 250
        Assert.Equal(100, leaf.GetValue(PcInh));

        var observer = new ReentrantWriter(leaf, PcInh, 7); // 7 coerces to 7 under the raised ceiling
        using var baseSubscription = leaf.AddObserver(PcInh, observer, new ObserverOptions { IncludeBaseChanges = true });
        using var ordinarySubscription = leaf.AddObserver(PcInh, observer);

        leaf.CoerceValue(PcInh);

        // Last writer wins: the nested SetValue owns the lane, and the suspended re-coercion's 250 is
        // simply dropped. Both lanes agree — an un-animated local entry has ONE value.
        Assert.Equal(7, leaf.GetValue(PcInh));
        Assert.Equal(7, leaf.GetBaseValue(PcInh));
        Assert.Equal(7, leaf.ReadLocalValue(PcInh)); // the nested writer's raw, not the re-coercion's 250

        // ONE ordinary delivery — the nested write's, which completes first (PD18).
        Assert.Equal([(100, 7, BindingPriority.LocalValue)], observer.OrdinaryDeliveries);

        // The A20 channel sees both base moves in order: the re-coercion's own, then the write's.
        Assert.Equal([(100, 250), (250, 7)], observer.BaseDeliveries);
    }

    /// <summary>
    /// <b>M233d — the clear-then-rewrite residue (added 2026-08-09).</b> Supersession alone is not
    /// exhaustive: an observer that RETRACTS and then RE-ESTABLISHES the contribution with a raw of its
    /// own whose coerced result is the value the suspended re-coercion computed leaves the stored base
    /// matching, so the base compare waves the operation through — yet the nested writer has already
    /// published that value and the resumed operation has nothing but a no-change
    /// <c>(250 → 250)</c> announcement to add. Every other publisher in the store gates on
    /// old == new (§0.3 rule 3 — one notification per real change); <c>RecoerceLocal</c> is the only one
    /// that cannot reach the case at rest, which is why it lacked the gate. The raw slot is the nested
    /// writer's (300), not the re-coercion's (400): the operation is superseded wholesale, not merged.
    /// </summary>
    [Fact] // M233d
    public void M233d_CoerceValue_WhoseBaseObserverClearsThenRewritesTheSameValue_IsSilentOnResume()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(PcInh, 42);          // the contributing ancestor
        leaf.SetValue(PcInh, 400);         // ceiling 100 ⇒ leaf eff = 100, raw = 400
        leaf.SetValue(PmaxInh, 250);       // raise the ceiling to 250: the next Co coerces 400 ⇒ 250
        Assert.Equal(100, leaf.GetValue(PcInh));

        var observer = new ReentrantClearThenWriter(leaf, PcInh, 300); // 300 coerces to the SAME 250
        using var baseSubscription = leaf.AddObserver(PcInh, observer, new ObserverOptions { IncludeBaseChanges = true });
        using var ordinarySubscription = leaf.AddObserver(PcInh, observer);

        leaf.CoerceValue(PcInh);

        // Exactly TWO ordinary deliveries, both the nested observer's — the retraction, then its own
        // re-establishing write. The resumed re-coercion adds NO third, no-change delivery.
        Assert.Equal(
            [(100, 42, BindingPriority.Inherited), (42, 250, BindingPriority.LocalValue)],
            observer.OrdinaryDeliveries);

        Assert.Equal(250, leaf.GetValue(PcInh));
        Assert.Equal(300, leaf.ReadLocalValue(PcInh)); // the nested writer's raw owns the slot, not 400

        // The A20 channel sees all three base moves: the re-coercion's, the retraction's, the rewrite's.
        Assert.Equal([(100, 250), (250, 42), (42, 250)], observer.BaseDeliveries);
    }

    [Fact]
    public void M234_CoerceValue_Untouched_IsNoOp()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, Pc);

        host.CoerceValue(Pc);

        probe.AssertSilent(); // the default lane is not coerced (PD8)
        Assert.Equal(0, host.GetValue(Pc));
    }

    [Fact]
    public void M236_Validate_Rejection_Throws_StoreUntouched()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, Pcv);

        Assert.Throws<ArgumentException>(() => host.SetValue(Pcv, 13));

        probe.AssertSilent();
        Assert.False(host.IsSet(Pcv));
        Assert.Equal(0, host.GetValue(Pcv));
    }

    [Fact]
    public void M237_ValidateSeesRaw_ThenCoerceClamps()
    {
        var host = new Host();

        host.SetValue(Pcv, 140); // validate sees raw 140 (≤ 150, passes); coerce clamps

        Assert.Equal(100, host.GetValue(Pcv));
    }

    [Fact]
    public void M238_ValidateBeforeCoerce_RejectsRawCoercionWouldHaveFixed()
    {
        var host = new Host();

        // Raw 160 fails validation (> 150) even though coercion would clamp it to a valid 100 (PD7).
        Assert.Throws<ArgumentException>(() => host.SetValue(Pcv, 160));
    }

    [Fact]
    public void M240_UntypedSetValue_SameValidationMouth()
    {
        var host = new Host();

        Assert.Throws<ArgumentException>(() => host.SetValue((UIProperty)Pv, -5));
    }

    [Fact]
    public void M241_ThrowingCoercer_PropagatesAndLeavesStoreUnmodified()
    {
        var property = UIProperty.Register<Host, int>(
            UniqueName("M241"),
            coerce: static (_, v) => v == 13 ? throw new FormatException("coercer boom") : v);
        var host = new Host();

        // Fresh: a throwing first write leaves no trace.
        Assert.Throws<FormatException>(() => host.SetValue(property, 13));
        Assert.False(host.IsSet(property));
        Assert.Equal(0, host.GetValue(property));

        // With a prior value: the strong guarantee on the local mouth.
        host.SetValue(property, 5);
        Assert.Throws<FormatException>(() => host.SetValue(property, 13));
        Assert.Equal(5, host.GetValue(property));
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource(property));
    }

    [Fact]
    public void M243_SetCurrentValue_CoercionParityWithSetValue()
    {
        var viaSetCurrent = new RecordingHost();
        var probeCurrent = Probe<int>.Attach(viaSetCurrent, Pc);
        viaSetCurrent.SetCurrentValue(Pc, 250);

        var viaSet = new RecordingHost();
        var probeSet = Probe<int>.Attach(viaSet, Pc);
        viaSet.SetValue(Pc, 250);

        Assert.Equal(viaSet.GetValue(Pc), viaSetCurrent.GetValue(Pc));
        // Coercion parity holds; the notification LANE differs by design — the graft replaces the
        // Default lane (M118 amended 2026-07-12, A11), the real write IS the local lane.
        probeCurrent.AssertSingleNotify(0, 100, BindingPriority.Default);
        probeSet.AssertSingleNotify(0, 100, BindingPriority.LocalValue);
    }
}
