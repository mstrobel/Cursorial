using Cursorial.UI;
using Cursorial.Tests.UI.PrecedenceMatrix;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

namespace Cursorial.Tests.UI.Conformance;

/// <summary>
/// The driver contract a frame implementation hands to the conformance kit: the frame under test
/// plus the three mutations the kit exercises. For a styling-engine frame, <see cref="SetActive"/>
/// is the selector/trigger edge, <see cref="SetEntryValue"/>/<see cref="UnsetEntry"/> the resource
/// pulse re-emitting a setter's <c>IValueEntry</c> in place (design doc §3.6 — a pulse resolving to
/// <c>UnsetValue</c> is an entry-unset, never a value write).
/// </summary>
public abstract class ConformanceFrame
{
    /// <summary>The frame under test.</summary>
    public abstract ValueFrame Frame { get; }

    /// <summary>Toggles the frame's participation (must route through <c>ValueFrame.SetActive</c>).</summary>
    public abstract void SetActive(bool active);

    /// <summary>Mutates the entry for <paramref name="property"/> in place and raises <c>OnEntryChanged</c>.</summary>
    public abstract void SetEntryValue(StyledProperty<int> property, int value);

    /// <summary>Unsets the entry for <paramref name="property"/> and raises <c>OnEntryChanged</c>.</summary>
    public abstract void UnsetEntry(StyledProperty<int> property);
}

/// <summary>
/// The <see cref="ValueFrame"/> conformance kit (design doc §2.5) — the behavioral contract any
/// frame implementation must satisfy against the store: activation/retraction promotion,
/// <c>OnEntryChanged</c> in-place re-emit, within-slot <see cref="StyleSortKey"/> ordering,
/// entry-unset promotion, and retraction-is-not-set-back (invariant 4). Fork B subclasses this with
/// a factory producing its style frames; the engine's reference implementation runs in
/// <see cref="TestValueFrameConformance"/>.
/// </summary>
/// <remarks>
/// An entry value of <see langword="null"/> in <see cref="CreateFrame"/> means a <em>valueless</em>
/// entry (<c>HasValue == false</c>, ledger A8).
/// </remarks>
public abstract class ValueFrameConformanceKit
{
    /// <summary>Creates a frame under test carrying the given entries.</summary>
    protected abstract ConformanceFrame CreateFrame(
        ulong sortKey, bool isActive, params (StyledProperty<int> Property, int? Value)[] entries);

    [Fact]
    public void Activation_AppliesValuesAtStyle()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, P);

        host.AddFrame(CreateFrame(10, isActive: true, (P, 5)).Frame);

        Assert.Equal(5, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Style, false), host.GetValueSource(P));
        probe.AssertSingleNotify(0, 5, BindingPriority.Style);
    }

    [Fact]
    public void InactiveFrame_ContributesNothing_UntilActivated()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, P);
        var frame = CreateFrame(10, isActive: false, (P, 5));

        host.AddFrame(frame.Frame);
        probe.AssertSilent();
        Assert.Equal(0, host.GetValue(P));

        frame.SetActive(true);
        Assert.Equal(5, host.GetValue(P));
        probe.AssertSingleNotify(0, 5, BindingPriority.Style);
    }

    [Fact]
    public void Deactivation_PromotesNextSource_ReactivationReapplies()
    {
        var host = new RecordingHost();
        var frame = CreateFrame(10, isActive: true, (P, 5));
        host.AddFrame(frame.Frame);
        var probe = Probe<int>.Attach(host, P);

        frame.SetActive(false);
        Assert.Equal(0, host.GetValue(P));
        probe.AssertSingleNotify(5, 0, BindingPriority.Default); // the promoted lane, not a Style set-back

        frame.SetActive(true);
        probe.AssertSingleNotify(0, 5, BindingPriority.Style);
    }

    [Fact]
    public void Retraction_IsRemovalPlusPromotion_NeverSetBack()
    {
        var host = new RecordingHost();
        var weaker = CreateFrame(10, isActive: true, (P, 5));
        var stronger = CreateFrame(20, isActive: true, (P, 8));
        host.AddFrame(weaker.Frame);
        host.AddFrame(stronger.Frame);
        var probe = Probe<int>.Attach(host, P);

        // Retract the winner: exactly one notification, carrying the promoted (Style) lane and the
        // promoted frame's value — a "set the old value back" implementation would write the local
        // lane instead and IsSet/GetValueSource would betray it.
        host.RemoveFrame(stronger.Frame);
        Assert.Equal(5, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Style, false), host.GetValueSource(P));
        probe.AssertSingleNotify(8, 5, BindingPriority.Style);

        // Retract the last contribution: promotion lands on Default, and no local residue remains.
        host.RemoveFrame(weaker.Frame);
        Assert.Equal(0, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Default, false), host.GetValueSource(P));
        Assert.False(host.IsSet(P));
        probe.AssertSingleNotify(5, 0, BindingPriority.Default);
    }

    [Fact]
    public void OnEntryChanged_ReEmitsInPlace()
    {
        var host = new RecordingHost();
        var frame = CreateFrame(10, isActive: true, (P, 5));
        host.AddFrame(frame.Frame);
        var probe = Probe<int>.Attach(host, P);

        frame.SetEntryValue(P, 6);

        // One Style-priority notification — a remove/re-add would betray itself with an
        // intermediate promotion to Default.
        probe.AssertSingleNotify(5, 6, BindingPriority.Style);
        Assert.Equal(6, host.GetValue(P));

        frame.SetEntryValue(P, 6); // unchanged value: the store's gate owns silence
        probe.AssertSilent();
    }

    [Fact]
    public void WithinSlot_SortKeyOrdering_LayerBeatsAddOrder()
    {
        var host = new RecordingHost();
        host.AddFrame(CreateFrame(20, isActive: true, (P, 8)).Frame);
        var probe = Probe<int>.Attach(host, P);

        host.AddFrame(CreateFrame(10, isActive: true, (P, 5)).Frame); // weaker key, added later

        probe.AssertSilent();
        Assert.Equal(8, host.GetValue(P)); // sort key order, not add order

        host.AddFrame(CreateFrame(20, isActive: true, (P, 9)).Frame); // equal key, added later — wins
        Assert.Equal(9, host.GetValue(P));
        probe.AssertSingleNotify(8, 9, BindingPriority.Style);
    }

    [Fact]
    public void EntryUnset_PromotesToNextSource()
    {
        var host = new RecordingHost();
        var weaker = CreateFrame(10, isActive: true, (P, 5));
        var stronger = CreateFrame(20, isActive: true, (P, 8));
        host.AddFrame(weaker.Frame);
        host.AddFrame(stronger.Frame);
        var probe = Probe<int>.Attach(host, P);

        stronger.UnsetEntry(P); // pulse resolved to UnsetValue ⇒ entry-unset, never a value write

        Assert.Equal(5, host.GetValue(P)); // within-slot promotion
        probe.AssertSingleNotify(8, 5, BindingPriority.Style);

        weaker.UnsetEntry(P);
        Assert.Equal(0, host.GetValue(P));
        probe.AssertSingleNotify(5, 0, BindingPriority.Default);
    }

    [Fact]
    public void ValuelessEntry_SkippedFromTheStart()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, P);
        var frame = CreateFrame(20, isActive: true, (P, null)); // A8: entry present, HasValue false
        host.AddFrame(frame.Frame);
        host.AddFrame(CreateFrame(10, isActive: true, (P, 5)).Frame);

        Assert.Equal(5, host.GetValue(P)); // the stronger frame's valueless entry is skipped
        Assert.False(frame.Frame.GetEntry(0).HasValue);
        probe.AssertSingleNotify(0, 5, BindingPriority.Style);
    }
}

/// <summary>The engine's reference run of the conformance kit, over <see cref="TestValueFrame"/>.</summary>
public sealed class TestValueFrameConformance : ValueFrameConformanceKit
{
    protected override ConformanceFrame CreateFrame(
        ulong sortKey, bool isActive, params (StyledProperty<int> Property, int? Value)[] entries)
    {
        var frame = new TestValueFrame(sortKey, isActive);
        foreach (var (property, value) in entries)
        {
            if (value is { } v)
                frame.With(property, v);
            else
                frame.WithUnset(property);
        }

        return new TestConformanceFrame(frame);
    }

    private sealed class TestConformanceFrame(TestValueFrame frame) : ConformanceFrame
    {
        public override ValueFrame Frame => frame;

        public override void SetActive(bool active)
        {
            if (active)
                frame.Activate();
            else
                frame.Deactivate();
        }

        public override void SetEntryValue(StyledProperty<int> property, int value) => frame.SetEntryValue(property, value);

        public override void UnsetEntry(StyledProperty<int> property) => frame.UnsetEntry(property);
    }
}
