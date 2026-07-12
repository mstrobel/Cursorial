using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>Matrix §18 — read surfaces: <c>GetValue(maxPriority)</c>, <c>GetBaseValue</c>, <c>GetValueSource</c>, diagnostics (M259–M264).</summary>
public class Section18_ReadSurfaces
{
    [Fact]
    public void M259_FullStack_LocalValueProbe_EqualsGetBaseValue()
    {
        var (_, leaf, _, _) = BuildM112Stack();

        Assert.Equal(7, leaf.GetValue(Pi, BindingPriority.LocalValue));
        Assert.Equal(leaf.GetBaseValue(Pi), leaf.GetValue(Pi, BindingPriority.LocalValue)); // PD16
    }

    [Fact]
    public void M260_FullStack_StyleProbe_SkipsAnimationAndLocal()
    {
        var (_, leaf, _, _) = BuildM112Stack();

        Assert.Equal(5, leaf.GetValue(Pi, BindingPriority.Style));
    }

    [Fact]
    public void M261_FullStack_InheritedAndDefaultProbes()
    {
        var (_, leaf, _, _) = BuildM112Stack();

        Assert.Equal(2, leaf.GetValue(Pi, BindingPriority.Inherited));
        Assert.Equal(0, leaf.GetValue(Pi, BindingPriority.Default));
    }

    [Fact]
    public void M262_GetValue_UnsetMaxPriority_Throws()
    {
        var host = new Host();

        Assert.Throws<ArgumentException>(() => host.GetValue(P, BindingPriority.Unset)); // PD16
        Assert.Throws<ArgumentException>(() => host.GetValue((UIProperty)P, BindingPriority.Unset)); // untyped parity
    }

    [Theory]
    [InlineData("fresh", BindingPriority.Default, false)]
    [InlineData("inherited", BindingPriority.Inherited, false)]
    [InlineData("frame", BindingPriority.Style, false)]
    [InlineData("local", BindingPriority.LocalValue, false)]
    [InlineData("animated", BindingPriority.Animation, false)]
    [InlineData("scv-overwritten", BindingPriority.Style, true)]
    public void M263_GetValueSource_FamilyTable(string scenario, BindingPriority expectedPriority, bool expectedCurrent)
    {
        var host = new RecordingHost();
        switch (scenario)
        {
            case "fresh":
                break;
            case "inherited":
                var root = new RecordingHost();
                root.SetValue(Pi, 5);
                host.SetInheritanceParent(root);
                break;
            case "frame":
                host.AddFrame(new TestValueFrame(10).With(Pi, 5));
                break;
            case "local":
                host.SetValue(Pi, 5);
                break;
            case "animated":
                host.BeginAnimation(Pi).SetValue(9);
                break;
            case "scv-overwritten":
                host.AddFrame(new TestValueFrame(10).With(Pi, 5));
                host.SetCurrentValue(Pi, 6);
                break;
        }

        var source = host.GetValueSource(Pi);
        Assert.Equal(expectedPriority, source.Priority); // never Unset
        Assert.Equal(expectedCurrent, source.IsCurrentValue);
    }

    [Fact]
    public void M264_DiagnosticsEnumeration_ListsTheFullStack()
    {
        var (root, leaf, frame, _) = BuildM112Stack();

        var rows = leaf.GetValueDiagnostics(Pi);

        Assert.Equal(4, rows.Count); // strongest-first: animation, local raw, frame entry, inherited provenance
        Assert.Equal(new PropertyValueDiagnostic(BindingPriority.Animation, 9, HasValue: true), rows[0]);
        Assert.Equal(new PropertyValueDiagnostic(BindingPriority.LocalValue, 7, HasValue: true), rows[1]); // the RAW local
        Assert.Equal(new PropertyValueDiagnostic(BindingPriority.Style, 5, HasValue: true, frame.SortKey, IsActive: true), rows[2]);
        Assert.Equal(new PropertyValueDiagnostic(BindingPriority.Inherited, 2, HasValue: true, InheritedFrom: root), rows[3]);
    }

    // ─────────────── M264a–M264e: the raw-local read mouths (ReadLocalValue / TryReadLocalValue) ───────────────

    [Fact] // M264a — the raw (pre-coercion) value surfaces; the effective stays coerced (PD6)
    public void M264a_ReadLocalValue_ReturnsTheRawPreCoercionValue()
    {
        var host = new Host();
        host.SetValue(Pc, 250); // clamp [0,100] ⇒ effective 100

        Assert.Equal(100, host.GetValue(Pc));
        Assert.Equal(250, host.ReadLocalValue(Pc));

        Assert.True(host.TryReadLocalValue(Pc, out var raw));
        Assert.Equal(250, raw);
    }

    [Fact] // M264b — only a LOCAL contribution surfaces; everything else is the sentinel
    public void M264b_ReadLocalValue_NonLocalContributions_ReturnUnsetValue()
    {
        var fresh = new Host();
        Assert.Same(UIProperty.UnsetValue, fresh.ReadLocalValue(P));
        Assert.False(fresh.TryReadLocalValue(P, out _));

        var framed = new Host();
        framed.AddFrame(new TestValueFrame(10).With(P, 5)); // style-only: effective 5, local unset
        Assert.Equal(5, framed.GetValue(P));
        Assert.Same(UIProperty.UnsetValue, framed.ReadLocalValue(P));

        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 2); // inherited-only at the leaf
        Assert.Equal(2, leaf.GetValue(Pi));
        Assert.Same(UIProperty.UnsetValue, leaf.ReadLocalValue(Pi));
        Assert.False(leaf.TryReadLocalValue(Pi, out _));
    }

    [Fact] // M264c amended 2026-07-12 — the SCV graft is local for STORAGE only; ReadLocalValue hides it (WPF parity)
    public void M264c_ReadLocalValue_GraftInvisible_RealLocalStillReports()
    {
        var host = new Host();
        host.SetCurrentValue(P, 4); // the M118 no-contribution graft

        // Invisible: no local AUTHORSHIP exists — consistent with GetValueSource reporting the
        // underlying source (+cur), so "was this set deliberately?" has one answer everywhere.
        Assert.Same(UIProperty.UnsetValue, host.ReadLocalValue(P));
        Assert.False(host.TryReadLocalValue(P, out _));

        // A real SetValue is local authorship; an SCV over it overwrites the raw slot (M119) and
        // ReadLocalValue keeps reporting the latest raw write.
        var authored = new Host();
        authored.SetValue(P, 3);
        authored.SetCurrentValue(P, 4);
        Assert.Equal(4, authored.ReadLocalValue(P));
        Assert.True(authored.TryReadLocalValue(P, out var raw));
        Assert.Equal(4, raw);
    }

    [Fact] // M264d — direct properties report their current value (field semantics, M220 parity)
    public void M264d_ReadLocalValue_DirectProperty_ReportsTheValue()
    {
        var host = new Host();
        host.SetD(5);

        Assert.Equal(5, host.ReadLocalValue(Pd));
    }

    [Fact] // M264e — the raw slot dies with the local contribution
    public void M264e_ReadLocalValue_AfterClearValue_ReturnsUnsetValue()
    {
        var host = new Host();
        host.SetValue(Pc, 250);
        host.ClearValue(Pc);

        Assert.Same(UIProperty.UnsetValue, host.ReadLocalValue(Pc));
        Assert.False(host.TryReadLocalValue(Pc, out _));
    }
}
