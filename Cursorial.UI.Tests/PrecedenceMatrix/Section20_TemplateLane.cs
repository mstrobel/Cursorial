using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>
/// Matrix §20 — the Template lane (M270–M298, PD24/PD25). The Template lane sits one rung below Style
/// and one above Inherited; it is reached only through the ambient template-instantiation scope, which
/// reroutes a literal <c>SetValue</c> (<c>T(v)</c> here) and a free-standing template binding entry
/// (<c>Bind(P, Template)</c>) to the lane. M296 (ValueSourceKind) lives with the Phase-2 provenance work.
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

    // ───────────── 20.2 The stronger lanes mask Template ─────────────

    [Fact]
    public void M274_StyleOverTemplate_TheFix()
    {
        var host = new RecordingHost();
        T(host, P, 5);
        var probe = Probe<int>.Attach(host, P);

        host.AddFrame(new TestValueFrame(K1).With(P, 9)); // a Style overrides the template default

        Assert.Equal(9, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Style, IsCurrentValue: false), host.GetValueSource(P));
        probe.AssertSingleNotify(5, 9, BindingPriority.Style);
    }

    [Fact]
    public void M275_StyleOverTemplate_Withdraw_TemplateResurfaces()
    {
        var host = new RecordingHost();
        T(host, P, 5);
        var frame = new TestValueFrame(K1).With(P, 9);
        host.AddFrame(frame);
        var probe = Probe<int>.Attach(host, P);

        host.RemoveFrame(frame);

        Assert.Equal(5, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Template, IsCurrentValue: false), host.GetValueSource(P));
        probe.AssertSingleNotify(9, 5, BindingPriority.Template);
    }

    [Fact]
    public void M276_StyleOverTemplate_MaskedTemplateWrite_Silent()
    {
        var host = new RecordingHost();
        T(host, P, 5);
        host.AddFrame(new TestValueFrame(K1).With(P, 9));
        var probe = Probe<int>.Attach(host, P);

        T(host, P, 6); // re-emit the template value while masked by Style

        probe.AssertSilent();
        Assert.Equal(9, host.GetValue(P));
        Assert.Equal(6, host.GetValue(P, BindingPriority.Template)); // the masked template value updated underneath
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

    // ───────────── 20.3 Full six-rung ladder ─────────────

    /// <summary>The §20 full-ladder stack on <c>Pi</c>: root.L(2); leaf TE(4), F(k1){5}, L(7), H(9).</summary>
    private static (RecordingHost Root, RecordingHost Leaf, TestValueFrame Frame,
                    BindingEntry<int> Template, AnimatedValueHandle<int> Handle) BuildLadder()
    {
        var root = new RecordingHost();
        var leaf = new RecordingHost();
        leaf.SetInheritanceParent(root);
        root.SetValue(Pi, 2);

        var te = leaf.Bind(Pi, BindingPriority.Template);
        te.SetValue(4);
        var frame = new TestValueFrame(K1).With(Pi, 5);
        leaf.AddFrame(frame);
        leaf.SetValue(Pi, 7);
        var handle = leaf.BeginAnimation(Pi);
        handle.SetValue(9);
        return (root, leaf, frame, te, handle);
    }

    [Fact]
    public void M280_FullLadder_PeelTopDown_FivePromotions()
    {
        var (root, leaf, frame, te, handle) = BuildLadder();
        Assert.Equal(9, leaf.GetValue(Pi));
        var probe = InheritedProbe<int>.Attach(leaf, Pi);

        handle.Dispose();      // → Local(7)
        leaf.ClearValue(Pi);   // → Style(5)
        leaf.RemoveFrame(frame); // → Template(4)
        te.SetUnset();         // → Inherited(2)
        root.ClearValue(Pi);   // → Default(0), propagated

        (int, int, BindingPriority)[] expected =
        [
            (9, 7, BindingPriority.LocalValue),
            (7, 5, BindingPriority.Style),
            (5, 4, BindingPriority.Template),
            (4, 2, BindingPriority.Inherited),
            (2, 0, BindingPriority.Default),
        ];
        Assert.Equal(expected, probe.Typed);
        Assert.Equal(expected.Select(e => ((object?)e.Item1, (object?)e.Item2, e.Item3)), probe.Untyped);
        Assert.Equal(expected[..4], probe.OrdinaryVirtual); // first four are leaf-local (origin-site)
        Assert.Equal(expected[4..], probe.Inherited);        // root.CV is propagated (PD22)
        Assert.Equal(0, leaf.GetValue(Pi));
    }

    [Theory]
    [InlineData(BindingPriority.Animation, 9)]
    [InlineData(BindingPriority.LocalValue, 7)]
    [InlineData(BindingPriority.Style, 5)]
    [InlineData(BindingPriority.Template, 4)]
    [InlineData(BindingPriority.Inherited, 2)]
    [InlineData(BindingPriority.Default, 0)]
    public void M281_FullLadder_MaxPriorityProbes(BindingPriority maxPriority, int expected)
    {
        var (_, leaf, _, _, _) = BuildLadder();
        Assert.Equal(expected, leaf.GetValue(Pi, maxPriority));
    }

    [Fact]
    public void M282_FullLadder_BaseTracksStrongestSubAnimationLane()
    {
        var (root, leaf, frame, te, _) = BuildLadder();

        Assert.Equal(7, leaf.GetBaseValue(Pi));
        leaf.ClearValue(Pi);
        Assert.Equal(5, leaf.GetBaseValue(Pi));
        leaf.RemoveFrame(frame);
        Assert.Equal(4, leaf.GetBaseValue(Pi));
        te.SetUnset();
        Assert.Equal(2, leaf.GetBaseValue(Pi));
        root.ClearValue(Pi);
        Assert.Equal(0, leaf.GetBaseValue(Pi));
    }

    [Fact]
    public void M283_ApplyBelowApply_StyleStillBeatsTemplate()
    {
        var host = new RecordingHost();
        host.SetValue(P, 7);              // Local wins
        T(host, P, 5);                    // Template — masked, silent
        host.AddFrame(new TestValueFrame(K1).With(P, 3)); // Style — masked, silent
        var probe = Probe<int>.Attach(host, P);

        Assert.Equal(7, host.GetValue(P));
        Assert.Equal(3, host.GetValue(P, BindingPriority.Style));
        Assert.Equal(5, host.GetValue(P, BindingPriority.Template));

        host.ClearValue(P); // Local withdraws: Style (3) beats Template (5)
        Assert.Equal(3, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Style, IsCurrentValue: false), host.GetValueSource(P));
        probe.AssertSingleNotify(7, 3, BindingPriority.Style);
    }

    // ───────────── 20.4 Theme-forwarding invariant + the reported repro ─────────────

    [Fact]
    public void M284_ThemeForward_PageStyleOverridesForwardedValue()
    {
        // A part carries a {TemplateBinding} forwarding the control's (style-set) value at Template;
        // a page Style targeting the part overrides it.
        var part = new RecordingHost();
        var forward = part.Bind(P, BindingPriority.Template); // the TemplateBinding's lane
        forward.SetValue(5);                                  // the forwarded theme value
        var probe = Probe<int>.Attach(part, P);

        part.AddFrame(new TestValueFrame(K1).With(P, 9));     // the page Style

        Assert.Equal(9, part.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Style, IsCurrentValue: false), part.GetValueSource(P));
        probe.AssertSingleNotify(5, 9, BindingPriority.Style);
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

    [Fact]
    public void M286_CloseButtonRepro_StyleOverridesTemplateLiteral()
    {
        // The reported bug: a template literal on a part (Background=Transparent) stayed at LocalValue
        // and a Style on the part could not override it. Now the literal lands at Template and the
        // Style wins.
        var btn = new RecordingHost();
        T(btn, P, 5); // the template literal (e.g., Background=Transparent)
        var probe = Probe<int>.Attach(btn, P);

        btn.AddFrame(new TestValueFrame(K1).With(P, 9)); // the window/page Style

        Assert.Equal(9, btn.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Style, IsCurrentValue: false), btn.GetValueSource(P));
        probe.AssertSingleNotify(5, 9, BindingPriority.Style);
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

        host.AddFrame(new TestValueFrame(K1).With(P, 9)); // Style supersedes the Template overwrite

        Assert.Equal(9, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Style, IsCurrentValue: false), host.GetValueSource(P));
        probe.AssertSingleNotify(6, 9, BindingPriority.Style);
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

    [Fact]
    public void M298_Diagnostics_TemplateRow_OrderedBetweenStyleAndInherited()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 2);                                 // inherited provenance
        leaf.AddFrame(new TestValueFrame(K1).With(Pi, 5));    // Style frame (wins)
        T(leaf, Pi, 4);                                       // Template contribution (masked)

        var diagnostics = leaf.GetValueDiagnostics(Pi);
        Assert.Equal(
            [BindingPriority.Style, BindingPriority.Template, BindingPriority.Inherited],
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
    public void M301_Template_CoerceValue_MaskedByStyle_Silent_ThenResurfaces()
    {
        var host = new RecordingHost();
        T(host, Pcd, 250);                                  // Template: ceiling 100 ⇒ 100
        var frame = new TestValueFrame(K1).With(Pcd, 9);    // Style masks the template ⇒ eff=9
        host.AddFrame(frame);
        Assert.Equal(9, host.GetValue(Pcd));
        var probe = Probe<int>.Attach(host, Pcd);

        host.SetValue(Pmax, 300);
        host.CoerceValue(Pcd); // re-coerces the MASKED template (raw 250) silently — Style still wins

        probe.AssertSilent();
        Assert.Equal(9, host.GetValue(Pcd));

        host.RemoveFrame(frame); // the re-coerced template value (250) resurfaces
        Assert.Equal(250, host.GetValue(Pcd));
        Assert.Equal(BindingPriority.Template, host.GetValueSource(Pcd).Priority);
    }
}
