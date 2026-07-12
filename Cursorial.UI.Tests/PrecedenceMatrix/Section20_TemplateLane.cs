using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>
/// Matrix §20 — the Template lane (M270–M301, PD24 as amended 2026-07-12). Under the completed
/// Avalonia lattice the Template lane sits one rung below <see cref="BindingPriority.StyleTrigger"/>
/// (the conditional style slot — pseudo-class/.class/When-gated rules pierce template-authored part
/// values while active) and one ABOVE resting <see cref="BindingPriority.Style"/> (a template
/// author's literals and TemplateBinding plumbing are the part's resting truth — a broad structural
/// rule cannot wreck a template's internal wiring). It is reached only through the ambient
/// template-instantiation scope, which reroutes a literal <c>SetValue</c> (<c>T(v)</c> here) and a
/// free-standing template binding entry (<c>Bind(P, Template)</c>) to the lane. The 2026-06-16
/// half-adoption (ALL styles above Template) is recorded in the §20 history. M296 (ValueSourceKind)
/// lives with the Phase-2 provenance work.
/// </summary>
public class Section20_TemplateLane
{
    private const ulong K1 = 10;

    /// <summary>`T(v)` — a literal <c>SetValue</c> issued inside an open template-instantiation scope.</summary>
    private static void T(UIObject host, StyledProperty<int> p, int value)
    {
        using (TemplateInstantiationScope.Enter())
            host.SetValue(p, value);
    }

    // ───────────── 20.1 Template over the resolution tiers ─────────────

    [Fact]
    public void M270_TemplateOverInherited()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 5);
        var probe = Probe<int>.Attach(leaf, Pi);

        T(leaf, Pi, 9);

        Assert.Equal(9, leaf.GetValue(Pi));
        Assert.Equal(9, leaf.GetBaseValue(Pi));
        Assert.Equal(new ValueSource(BindingPriority.Template, IsCurrentValue: false), leaf.GetValueSource(Pi));
        probe.AssertSingleNotify(5, 9, BindingPriority.Template);
    }

    [Fact]
    public void M271_TemplateOverInherited_Withdraw()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 5);
        var te = leaf.Bind(Pi, BindingPriority.Template);
        te.SetValue(9);
        var probe = Probe<int>.Attach(leaf, Pi);

        te.SetUnset();

        Assert.Equal(5, leaf.GetValue(Pi));
        Assert.Equal(new ValueSource(BindingPriority.Inherited, IsCurrentValue: false), leaf.GetValueSource(Pi));
        probe.AssertSingleNotify(9, 5, BindingPriority.Inherited);
    }

    [Fact]
    public void M272_TemplateOverDefault()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, P);

        T(host, P, 9);

        Assert.Equal(9, host.GetValue(P));
        Assert.Equal(9, host.GetBaseValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Template, IsCurrentValue: false), host.GetValueSource(P));
        Assert.True(host.IsSet(P));
        probe.AssertSingleNotify(0, 9, BindingPriority.Template);
    }

    [Fact]
    public void M273_TemplateOverDefault_Equal_SilentLaneFlip()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, P);

        T(host, P, 0); // equals the metadata default

        probe.AssertSilent();
        Assert.Equal(new ValueSource(BindingPriority.Template, IsCurrentValue: false), host.GetValueSource(P)); // PD9 lane flip
        Assert.True(host.IsSet(P));
    }

    // ───────────── 20.2 The stronger lanes mask Template (and Template masks resting Style) ─────────────

    [Fact] // amended 2026-07-12 (the activator split): only a CONDITIONAL rule overrides Template
    public void M274_TriggerOverTemplate_RestingStyleDoesNot()
    {
        var host = new RecordingHost();
        T(host, P, 5);
        var probe = Probe<int>.Attach(host, P);

        host.AddFrame(new TestValueFrame(K1).With(P, 3)); // a RESTING rule cannot wreck template wiring

        probe.AssertSilent();
        Assert.Equal(5, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Template, IsCurrentValue: false), host.GetValueSource(P));

        host.AddFrame(new TestValueFrame(K1, priority: BindingPriority.StyleTrigger).With(P, 9)); // conditional pierces

        Assert.Equal(9, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.StyleTrigger, IsCurrentValue: false), host.GetValueSource(P));
        probe.AssertSingleNotify(5, 9, BindingPriority.StyleTrigger);
    }

    [Fact]
    public void M275_TriggerOverTemplate_Withdraw_TemplateResurfaces()
    {
        var host = new RecordingHost();
        T(host, P, 5);
        var frame = new TestValueFrame(K1, priority: BindingPriority.StyleTrigger).With(P, 9);
        host.AddFrame(frame);
        var probe = Probe<int>.Attach(host, P);

        host.RemoveFrame(frame);

        Assert.Equal(5, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Template, IsCurrentValue: false), host.GetValueSource(P));
        probe.AssertSingleNotify(9, 5, BindingPriority.Template);
    }

    [Fact]
    public void M276_TriggerOverTemplate_MaskedTemplateWrite_Silent()
    {
        var host = new RecordingHost();
        T(host, P, 5);
        host.AddFrame(new TestValueFrame(K1, priority: BindingPriority.StyleTrigger).With(P, 9));
        var probe = Probe<int>.Attach(host, P);

        T(host, P, 6); // re-emit the template value while masked by the conditional rule

        probe.AssertSilent();
        Assert.Equal(9, host.GetValue(P));
        Assert.Equal(6, host.GetValue(P, BindingPriority.Template)); // the masked template value updated underneath
    }

    [Fact] // M276b — the inverse mask: Template over resting Style (§0.3, 2026-07-12)
    public void M276b_TemplateOverRestingStyle_MaskedStyleWrite_Silent()
    {
        var host = new RecordingHost();
        var frame = new TestValueFrame(K1).With(P, 3); // resting
        host.AddFrame(frame);
        T(host, P, 5); // Template arrives OVER the resting rule
        Assert.Equal(5, host.GetValue(P));
        var probe = Probe<int>.Attach(host, P);

        frame.SetEntryValue(P, 4); // re-emit the resting value while masked by Template

        probe.AssertSilent();
        Assert.Equal(5, host.GetValue(P));
        Assert.Equal(4, host.GetValue(P, BindingPriority.Style)); // the masked resting value updated underneath

        host.RemoveFrame(frame); // withdrawing the MASKED rung is silent — Template still wins
        probe.AssertSilent();
        Assert.Equal(new ValueSource(BindingPriority.Template, IsCurrentValue: false), host.GetValueSource(P));
    }

    [Fact]
    public void M277_LocalOverTemplate()
    {
        var host = new RecordingHost();
        T(host, P, 5);
        var probe = Probe<int>.Attach(host, P);

        host.SetValue(P, 9);

        Assert.Equal(9, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, IsCurrentValue: false), host.GetValueSource(P));
        probe.AssertSingleNotify(5, 9, BindingPriority.LocalValue);
    }

    [Fact]
    public void M278_LocalOverTemplate_Withdraw_TemplateResurfaces()
    {
        var host = new RecordingHost();
        T(host, P, 5);
        host.SetValue(P, 9);
        var probe = Probe<int>.Attach(host, P);

        host.ClearValue(P);

        Assert.Equal(5, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Template, IsCurrentValue: false), host.GetValueSource(P));
        probe.AssertSingleNotify(9, 5, BindingPriority.Template);
    }

    [Fact]
    public void M279_AnimationOverTemplate()
    {
        var host = new RecordingHost();
        T(host, P, 5);
        var probe = Probe<int>.Attach(host, P);

        var handle = host.BeginAnimation(P);
        handle.SetValue(9);

        Assert.Equal(9, host.GetValue(P));
        Assert.Equal(5, host.GetBaseValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Animation, IsCurrentValue: false), host.GetValueSource(P));
        probe.AssertSingleNotify(5, 9, BindingPriority.Animation);

        handle.Dispose();
        Assert.Equal(5, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Template, IsCurrentValue: false), host.GetValueSource(P));
        probe.AssertSingleNotify(9, 5, BindingPriority.Template);
    }

    // ───────────── 20.3 Full seven-rung ladder (§0.3, 2026-07-12) ─────────────

    /// <summary>
    /// The §20 full-ladder stack on <c>Pi</c>: root.L(2); leaf resting F(k1){3}, TE(4), trigger
    /// F(k1){6}, L(7), H(9) — one contribution per rung of the completed lattice
    /// (Animation &gt; Local &gt; StyleTrigger &gt; Template &gt; Style &gt; Inherited &gt; Default).
    /// </summary>
    private static (RecordingHost Root, RecordingHost Leaf, TestValueFrame RestingFrame, TestValueFrame TriggerFrame,
                    BindingEntry<int> Template, AnimatedValueHandle<int> Handle) BuildLadder()
    {
        var root = new RecordingHost();
        var leaf = new RecordingHost();
        leaf.SetInheritanceParent(root);
        root.SetValue(Pi, 2);

        var restingFrame = new TestValueFrame(K1).With(Pi, 3);
        leaf.AddFrame(restingFrame);
        var te = leaf.Bind(Pi, BindingPriority.Template);
        te.SetValue(4);
        var triggerFrame = new TestValueFrame(K1, priority: BindingPriority.StyleTrigger).With(Pi, 6);
        leaf.AddFrame(triggerFrame);
        leaf.SetValue(Pi, 7);
        var handle = leaf.BeginAnimation(Pi);
        handle.SetValue(9);
        return (root, leaf, restingFrame, triggerFrame, te, handle);
    }

    [Fact]
    public void M280_FullLadder_PeelTopDown_SixPromotions()
    {
        var (root, leaf, restingFrame, triggerFrame, te, handle) = BuildLadder();
        Assert.Equal(9, leaf.GetValue(Pi));
        var probe = InheritedProbe<int>.Attach(leaf, Pi);

        handle.Dispose();              // → Local(7)
        leaf.ClearValue(Pi);           // → StyleTrigger(6)
        leaf.RemoveFrame(triggerFrame); // → Template(4)
        te.SetUnset();                 // → Style(3)
        leaf.RemoveFrame(restingFrame); // → Inherited(2)
        root.ClearValue(Pi);           // → Default(0), propagated

        (int, int, BindingPriority)[] expected =
        [
            (9, 7, BindingPriority.LocalValue),
            (7, 6, BindingPriority.StyleTrigger),
            (6, 4, BindingPriority.Template),
            (4, 3, BindingPriority.Style),
            (3, 2, BindingPriority.Inherited),
            (2, 0, BindingPriority.Default),
        ];
        Assert.Equal(expected, probe.Typed);
        Assert.Equal(expected.Select(e => ((object?)e.Item1, (object?)e.Item2, e.Item3)), probe.Untyped);
        Assert.Equal(expected[..5], probe.OrdinaryVirtual); // first five are leaf-local (origin-site)
        Assert.Equal(expected[5..], probe.Inherited);        // root.CV is propagated (PD22)
        Assert.Equal(0, leaf.GetValue(Pi));
    }

    [Theory]
    [InlineData(BindingPriority.Animation, 9)]
    [InlineData(BindingPriority.LocalValue, 7)]
    [InlineData(BindingPriority.StyleTrigger, 6)]
    [InlineData(BindingPriority.Template, 4)]
    [InlineData(BindingPriority.Style, 3)] // the Style-capped probe deliberately skips the stronger Template lane (PD16)
    [InlineData(BindingPriority.Inherited, 2)]
    [InlineData(BindingPriority.Default, 0)]
    public void M281_FullLadder_MaxPriorityProbes(BindingPriority maxPriority, int expected)
    {
        var (_, leaf, _, _, _, _) = BuildLadder();
        Assert.Equal(expected, leaf.GetValue(Pi, maxPriority));
    }

    [Fact]
    public void M282_FullLadder_BaseTracksStrongestSubAnimationLane()
    {
        var (root, leaf, restingFrame, triggerFrame, te, _) = BuildLadder();

        Assert.Equal(7, leaf.GetBaseValue(Pi));
        leaf.ClearValue(Pi);
        Assert.Equal(6, leaf.GetBaseValue(Pi));
        leaf.RemoveFrame(triggerFrame);
        Assert.Equal(4, leaf.GetBaseValue(Pi));
        te.SetUnset();
        Assert.Equal(3, leaf.GetBaseValue(Pi));
        leaf.RemoveFrame(restingFrame);
        Assert.Equal(2, leaf.GetBaseValue(Pi));
        root.ClearValue(Pi);
        Assert.Equal(0, leaf.GetBaseValue(Pi));
    }

    [Fact] // amended 2026-07-12: below a Local winner the masked order is Trigger > Template > resting Style
    public void M283_ApplyBelowApply_TriggerBeatsTemplateBeatsRestingStyle()
    {
        var host = new RecordingHost();
        host.SetValue(P, 7);              // Local wins
        T(host, P, 5);                    // Template — masked, silent
        host.AddFrame(new TestValueFrame(K1).With(P, 3)); // resting Style — masked, silent
        var trigger = new TestValueFrame(K1, priority: BindingPriority.StyleTrigger).With(P, 8);
        host.AddFrame(trigger);           // conditional — masked, silent
        var probe = Probe<int>.Attach(host, P);

        Assert.Equal(7, host.GetValue(P));
        Assert.Equal(8, host.GetValue(P, BindingPriority.StyleTrigger));
        Assert.Equal(5, host.GetValue(P, BindingPriority.Template));
        Assert.Equal(3, host.GetValue(P, BindingPriority.Style));

        host.ClearValue(P); // Local withdraws: the trigger (8) beats Template (5) beats resting (3)
        Assert.Equal(8, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.StyleTrigger, IsCurrentValue: false), host.GetValueSource(P));
        probe.AssertSingleNotify(7, 8, BindingPriority.StyleTrigger);

        host.RemoveFrame(trigger); // the trigger withdraws: Template (5) beats the resting rule (3)
        Assert.Equal(5, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Template, IsCurrentValue: false), host.GetValueSource(P));
        probe.AssertSingleNotify(8, 5, BindingPriority.Template);
    }

    // ───────────── 20.4 Theme-forwarding invariant + the reported repro ─────────────

    [Fact] // re-pinned 2026-07-12 (M284): the forwarded value IS the part's resting truth; only activation pierces
    public void M284_ThemeForward_RestingPageStyleDoesNotOverride_ActivatedDoes()
    {
        // A part carries a {TemplateBinding} forwarding the control's (style-set) value at Template.
        // A RESTING page rule targeting the part does NOT override it — re-skinning at rest flows
        // through the CONTROL's own properties (which resting styles CAN set) via the forwarding
        // spine. An ACTIVATED (conditional) page rule still pierces while active.
        var part = new RecordingHost();
        var forward = part.Bind(P, BindingPriority.Template); // the TemplateBinding's lane
        forward.SetValue(5);                                  // the forwarded theme value
        var probe = Probe<int>.Attach(part, P);

        part.AddFrame(new TestValueFrame(K1).With(P, 3));     // the resting page rule — masked, silent

        probe.AssertSilent();
        Assert.Equal(5, part.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Template, IsCurrentValue: false), part.GetValueSource(P));

        part.AddFrame(new TestValueFrame(K1, priority: BindingPriority.StyleTrigger).With(P, 9)); // the activated rule

        Assert.Equal(9, part.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.StyleTrigger, IsCurrentValue: false), part.GetValueSource(P));
        probe.AssertSingleNotify(5, 9, BindingPriority.StyleTrigger);
    }

    [Fact]
    public void M285_ThemeForward_NoPageStyle_ForwardedValueWins()
    {
        var part = new RecordingHost();
        var forward = part.Bind(P, BindingPriority.Template);
        forward.SetValue(5);

        Assert.Equal(5, part.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Template, IsCurrentValue: false), part.GetValueSource(P));
    }

    [Fact] // re-pinned 2026-07-12 (M286): the close-button repro under the completed lattice
    public void M286_CloseButtonRepro_TemplateLiteralResistsRestingStyle_ConditionalPierces()
    {
        // The ORIGINAL repro (a part literal stuck at LocalValue, unstylable) drove the 2026-06-16
        // half-adoption that put ALL styles above Template — which made template literals useless
        // in the other direction (any broad resting rule wrecked template wiring). The completed
        // lattice re-pins the repro: the literal (e.g., Background=Transparent) IS the part's
        // resting truth, so a resting page rule does NOT override it; a state rule
        // (:pointerover/.class/When — conditional ⇒ StyleTrigger) pierces while active and
        // retracts cleanly back to the literal.
        var btn = new RecordingHost();
        T(btn, P, 5); // the template literal (e.g., Background=Transparent)
        var probe = Probe<int>.Attach(btn, P);

        btn.AddFrame(new TestValueFrame(K1).With(P, 3)); // the window/page RESTING rule — masked

        probe.AssertSilent();
        Assert.Equal(5, btn.GetValue(P));

        var hover = new TestValueFrame(K1, isActive: false, priority: BindingPriority.StyleTrigger).With(P, 9);
        btn.AddFrame(hover); // the :pointerover rule, armed but inactive
        probe.AssertSilent();

        hover.Activate(); // hover ON: the conditional rule pierces the literal
        Assert.Equal(9, btn.GetValue(P));
        probe.AssertSingleNotify(5, 9, BindingPriority.StyleTrigger);

        hover.Deactivate(); // hover OFF: clean retraction back to the literal
        Assert.Equal(5, btn.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Template, IsCurrentValue: false), btn.GetValueSource(P));
        probe.AssertSingleNotify(9, 5, BindingPriority.Template);
    }

    // ───────────── 20.5 SetCurrentValue × Template ─────────────

    [Fact]
    public void M287_SetCurrentValueGraft_YieldsToTemplateProducer()
    {
        var host = new RecordingHost();
        host.SetCurrentValue(P, 4); // the M118 as-Local graft
        var probe = Probe<int>.Attach(host, P);

        T(host, P, 8); // a Template producer arrives — the graft evaporates (PD24's extra branch)

        Assert.Equal(8, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Template, IsCurrentValue: false), host.GetValueSource(P));
        probe.AssertSingleNotify(4, 8, BindingPriority.Template);
    }

    [Fact]
    public void M288_SetCurrentValue_OverTemplate_ProvenanceUnchanged()
    {
        var host = new RecordingHost();
        T(host, P, 5);
        var probe = Probe<int>.Attach(host, P);

        host.SetCurrentValue(P, 6);

        Assert.Equal(6, host.GetValue(P));
        Assert.Equal(6, host.GetBaseValue(P)); // the overwrite IS the base while un-animated
        Assert.True(host.IsSet(P));
        Assert.Equal(new ValueSource(BindingPriority.Template, IsCurrentValue: true), host.GetValueSource(P));
        probe.AssertSingleNotify(5, 6, BindingPriority.Template); // A11 replaced lane
    }

    [Fact]
    public void M289_SetCurrentValue_OverTemplate_LaneReEmit_Clobbers()
    {
        var host = new RecordingHost();
        T(host, P, 5);
        host.SetCurrentValue(P, 6);
        var probe = Probe<int>.Attach(host, P);

        T(host, P, 8); // the template re-emits a different value

        Assert.Equal(8, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Template, IsCurrentValue: false), host.GetValueSource(P)); // +cur cleared
        probe.AssertSingleNotify(6, 8, BindingPriority.Template);
    }

    [Fact]
    public void M289b_SetCurrentValue_OverTemplate_SameValueReEmit_StillClobbers()
    {
        var host = new RecordingHost();
        T(host, P, 5);
        host.SetCurrentValue(P, 6);
        var probe = Probe<int>.Attach(host, P);

        T(host, P, 5); // the template re-emits the SAME value — still clobbers (the Style M122 analog)

        Assert.Equal(5, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Template, IsCurrentValue: false), host.GetValueSource(P));
        probe.AssertSingleNotify(6, 5, BindingPriority.Template);
    }

    [Fact]
    public void M290_SetCurrentValue_OverTemplate_LaneWithdrawal_Evaporates()
    {
        var host = new RecordingHost();
        var te = host.Bind(P, BindingPriority.Template);
        te.SetValue(5);
        host.SetCurrentValue(P, 6);
        var probe = Probe<int>.Attach(host, P);

        te.Dispose();

        Assert.Equal(0, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Default, IsCurrentValue: false), host.GetValueSource(P));
        probe.AssertSingleNotify(6, 0, BindingPriority.Default);
    }

    [Fact]
    public void M291_SetCurrentValue_OverTemplate_StrongerLaneWins()
    {
        var host = new RecordingHost();
        T(host, P, 5);
        host.SetCurrentValue(P, 6);
        var probe = Probe<int>.Attach(host, P);

        // The stronger style lane is the CONDITIONAL slot (a resting rule sits BELOW Template
        // under the amended ladder and would leave the overwrite in place).
        host.AddFrame(new TestValueFrame(K1, priority: BindingPriority.StyleTrigger).With(P, 9));

        Assert.Equal(9, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.StyleTrigger, IsCurrentValue: false), host.GetValueSource(P));
        probe.AssertSingleNotify(6, 9, BindingPriority.StyleTrigger);
    }

    [Fact]
    public void M292_ClearValue_DoesNotTouchTemplate()
    {
        var host = new RecordingHost();
        T(host, P, 5);
        var probe = Probe<int>.Attach(host, P);

        host.ClearValue(P); // no local contribution to clear — the template lane is not "local"

        probe.AssertSilent();
        Assert.Equal(5, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Template, IsCurrentValue: false), host.GetValueSource(P));
    }

    // ───────────── 20.6 Install seams, IsSet, diagnostics ─────────────

    [Fact]
    public void M293_ScopeRoutesProducers_RestoresOnDispose()
    {
        // The scope is the only trigger: a literal SetValue inside it lands at Template, outside at Local.
        var inHost = new Host();
        using (TemplateInstantiationScope.Enter())
            inHost.SetValue(P, 5);
        Assert.Equal(BindingPriority.Template, inHost.GetValueSource(P).Priority);

        var outHost = new Host();
        outHost.SetValue(P, 5);
        Assert.Equal(BindingPriority.LocalValue, outHost.GetValueSource(P).Priority);

        // Nesting is re-entrant/last-open-wins and restores on dispose.
        Assert.False(TemplateInstantiationScope.IsActive);
        using (TemplateInstantiationScope.Enter())
        {
            Assert.True(TemplateInstantiationScope.IsActive);
            using (TemplateInstantiationScope.Enter())
                Assert.True(TemplateInstantiationScope.IsActive);
            Assert.True(TemplateInstantiationScope.IsActive); // inner dispose pops one level only
        }
        Assert.False(TemplateInstantiationScope.IsActive);
    }

    [Fact]
    public void M294_SetValueWithTemplatePriority_Rejected()
    {
        var host = new Host();
        Assert.Throws<ArgumentException>(() => host.SetValue(P, 1, BindingPriority.Template)); // PD1/PD24
    }

    [Fact]
    public void M295_Bind_AcceptsTemplate_RejectsOtherStyleAndResolutionLanes()
    {
        var host = new Host();

        var te = host.Bind(P, BindingPriority.Template);
        te.SetValue(7);
        Assert.Equal(7, host.GetValue(P));
        Assert.Equal(BindingPriority.Template, host.GetValueSource(P).Priority);

        Assert.Throws<ArgumentException>(() => new Host().Bind(P, BindingPriority.Style));
        Assert.Throws<ArgumentException>(() => new Host().Bind(P, BindingPriority.Animation));
        Assert.Throws<ArgumentException>(() => new Host().Bind(P, BindingPriority.Inherited));
        Assert.Throws<ArgumentException>(() => new Host().Bind(P, BindingPriority.Default));
    }

    [Fact]
    public void M296_ValueSourceKind_Family()
    {
        // Local
        var local = new Host();
        local.SetValue(P, 1);
        Assert.Equal(ValueSourceKind.Local, local.GetValueSource(P).Kind);

        // TemplateLiteral
        var literal = new Host();
        T(literal, P, 1);
        Assert.Equal(ValueSourceKind.TemplateLiteral, literal.GetValueSource(P).Kind);

        // TemplateBinding (a free-standing Template-lane binding entry — the in-template install path)
        var binding = new Host();
        binding.Bind(P, BindingPriority.Template).SetValue(1);
        Assert.Equal(ValueSourceKind.TemplateBinding, binding.GetValueSource(P).Kind);

        // StyleSetter (a plain, unconditional frame)
        var setter = new Host();
        setter.AddFrame(new TestValueFrame(K1).With(P, 1));
        Assert.Equal(ValueSourceKind.StyleSetter, setter.GetValueSource(P).Kind);

        // StyleWhen (a When-guarded / conditional frame)
        var when = new Host();
        when.AddFrame(new TestValueFrame(K1, isConditional: true).With(P, 1));
        Assert.Equal(ValueSourceKind.StyleWhen, when.GetValueSource(P).Kind);

        // Animation
        var animated = new Host();
        animated.BeginAnimation(P).SetValue(1);
        Assert.Equal(ValueSourceKind.Animation, animated.GetValueSource(P).Kind);

        // Inherited
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 1);
        Assert.Equal(ValueSourceKind.Inherited, leaf.GetValueSource(Pi).Kind);

        // Default
        Assert.Equal(ValueSourceKind.Default, new Host().GetValueSource(P).Kind);

        // Kind is excluded from equality (PD25): two sources differing only in Kind compare equal.
        Assert.Equal(
            new ValueSource(BindingPriority.Template, IsCurrentValue: false),
            new ValueSource(BindingPriority.Template, IsCurrentValue: false) { Kind = ValueSourceKind.TemplateLiteral });
        // (TemplateResource is exercised end-to-end in TemplateLanePrecedenceTests — it needs the resource system.)
    }

    [Fact]
    public void M297_IsSet_CountsTemplate()
    {
        var host = new Host();
        T(host, P, 5);
        Assert.True(host.IsSet(P)); // PD11 extended: auto-aliasing yields to template-provided values
    }

    [Fact] // amended 2026-07-12 (M298): the Template row sits between the trigger and resting style rows
    public void M298_Diagnostics_TemplateRow_OrderedBetweenTriggerAndRestingStyle()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 2);                                 // inherited provenance
        leaf.AddFrame(new TestValueFrame(K1).With(Pi, 3));    // resting frame (masked)
        leaf.AddFrame(new TestValueFrame(K1, priority: BindingPriority.StyleTrigger).With(Pi, 5)); // trigger frame (wins)
        T(leaf, Pi, 4);                                       // Template contribution (masked)

        var diagnostics = leaf.GetValueDiagnostics(Pi);
        Assert.Equal(
            [BindingPriority.StyleTrigger, BindingPriority.Template, BindingPriority.Style, BindingPriority.Inherited],
            diagnostics.Select(d => d.Priority));

        var templateRow = diagnostics.Single(d => d.Priority == BindingPriority.Template);
        Assert.Equal(4, templateRow.Value);
    }

    // ───────────── 20.7 Coercion (PD6 parity — the Template lane is the local lane's twin) ─────────────

    /// <summary>The M300 instance-state ceiling property (read by <see cref="Pcd"/>'s coercer).</summary>
    private static readonly StyledProperty<int> Pmax = UIProperty.Register<Host, int>(UniqueName("M300Max"), defaultValue: 100);

    /// <summary>An instance-state coercer: clamp to the ceiling carried by <see cref="Pmax"/> (the M232 dance, Template lane).</summary>
    private static readonly StyledProperty<int> Pcd = UIProperty.Register<Host, int>(
        UniqueName("M300Pc"), coerce: static (o, v) => Math.Min(v, o.GetValue(Pmax)));

    [Fact]
    public void M299_Template_CoercesAtWrite()
    {
        var host = new RecordingHost();
        T(host, Pc, 250); // clamps to [0,100]

        Assert.Equal(100, host.GetValue(Pc));
        var src = host.GetValueSource(Pc);
        Assert.Equal(BindingPriority.Template, src.Priority);
        Assert.True(src.IsCoerced);

        // The raw template slot holds 250 (PD6 parity — a later CoerceValue coerces against it, M300).
        var entry = Assert.IsType<EffectiveValue<int>>(host.DebugValueStore!.TryGetEntry(Pc.Id));
        Assert.Equal(250, entry.RawTemplateValue);
    }

    [Fact] // M299b — the local M231a twin: a gated template re-emit still updates the raw template slot
    public void M299b_GatedTemplateWrite_UpdatesTheRawSlot_ForTheCoerceValueDance()
    {
        var host = new RecordingHost();
        T(host, Pcd, 250); // ceiling 100 ⇒ eff = 100, raw template = 250

        T(host, Pcd, 120); // gated: coerced 100 == template 100 ⇒ silent — but the raw slot must move
        Assert.Equal(100, host.GetValue(Pcd));

        var entry = Assert.IsType<EffectiveValue<int>>(host.DebugValueStore!.TryGetEntry(Pcd.Id));
        Assert.Equal(120, entry.RawTemplateValue);

        host.SetValue(Pmax, 300); // raise the ceiling
        host.CoerceValue(Pcd);    // re-runs against the LATEST template write (120), never the first (250)

        Assert.Equal(120, host.GetValue(Pcd));
        Assert.Equal(BindingPriority.Template, host.GetValueSource(Pcd).Priority);
    }

    [Fact]
    public void M300_Template_CoerceValue_RerunsAgainstRawValue()
    {
        var host = new RecordingHost();
        T(host, Pcd, 250); // ceiling 100 ⇒ eff=100, raw=250
        Assert.Equal(100, host.GetValue(Pcd));
        Assert.Equal(BindingPriority.Template, host.GetValueSource(Pcd).Priority);
        var probe = Probe<int>.Attach(host, Pcd);

        host.SetValue(Pmax, 300); // raise the ceiling
        host.CoerceValue(Pcd);    // re-runs against the RAW 250 (the Maximum/Value dance, Template lane)

        Assert.Equal(250, host.GetValue(Pcd));
        probe.AssertSingleNotify(100, 250, BindingPriority.Template);
    }

    [Fact]
    public void M301_Template_CoerceValue_MaskedByTrigger_Silent_ThenResurfaces()
    {
        var host = new RecordingHost();
        T(host, Pcd, 250);                                  // Template: ceiling 100 ⇒ 100
        // Only a CONDITIONAL rule masks the template lane under the amended ladder (§0.3).
        var frame = new TestValueFrame(K1, priority: BindingPriority.StyleTrigger).With(Pcd, 9);
        host.AddFrame(frame);
        Assert.Equal(9, host.GetValue(Pcd));
        var probe = Probe<int>.Attach(host, Pcd);

        host.SetValue(Pmax, 300);
        host.CoerceValue(Pcd); // re-coerces the MASKED template (raw 250) silently — the trigger still wins

        probe.AssertSilent();
        Assert.Equal(9, host.GetValue(Pcd));

        host.RemoveFrame(frame); // the re-coerced template value (250) resurfaces
        Assert.Equal(250, host.GetValue(Pcd));
        Assert.Equal(BindingPriority.Template, host.GetValueSource(Pcd).Priority);
    }
}
