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
        probeCurrent.AssertSingleNotify(0, 100, BindingPriority.LocalValue);
        probeSet.AssertSingleNotify(0, 100, BindingPriority.LocalValue);
    }
}
