using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>Matrix §11 — attached properties (M197–M204).</summary>
public class Section11_Attached
{
    [Fact]
    public void M203_InheritingAttachedProperty_WalksTheChain()
    {
        var pai = UIProperty.RegisterAttached<Tab, Host, int>(UniqueName("Pai"), inherits: true);
        var (root, _, leaf) = Chain();

        root.SetValue(pai, 4); // the ShowAccessKeys shape

        Assert.Equal(4, leaf.GetValue(pai));
        Assert.Equal(new ValueSource(BindingPriority.Inherited, false), leaf.GetValueSource(pai));
    }

    [Fact]
    public void M202_AttachedProperties_AreStyleable()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, Pa);
        var frame = new TestValueFrame(10).With(Pa, 3);

        host.AddFrame(frame);
        Assert.Equal(new ValueSource(BindingPriority.Style, false), host.GetValueSource(Pa));
        probe.AssertSingleNotify(0, 3, BindingPriority.Style);

        host.RemoveFrame(frame);
        probe.AssertSingleNotify(3, 0, BindingPriority.Default); // same promotion as M33/M35
    }

    [Fact]
    public void M197_AttachedSetValue_FullLocalLadderSemantics()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, Pa);

        host.SetValue(Pa, 2); // i.e. Tab.SetIndex(host, 2)

        Assert.Equal(2, host.GetValue(Pa));
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource(Pa));
        probe.AssertSingleNotify(0, 2, BindingPriority.LocalValue);
    }

    [Fact]
    public void M198_AttachedGetValue_ReturnsDeclaredDefault()
    {
        var host = new Host();

        Assert.Equal(0, host.GetValue(Pa)); // storage is instance-keyed by dense id — nothing special
    }

    [Fact]
    public void M199_GlobalEffectsLane_DeliversForAttached()
    {
        var attached = UIProperty.RegisterAttached<Tab, Host, int>(UniqueName("M199"));
        attached.GlobalEffects = PropertyEffects.AffectsArrange; // during the registration window

        Assert.True(attached.GetEffects(typeof(Host)).HasFlag(PropertyEffects.AffectsArrange)); // A1: the global lane delivers
    }

    [Fact]
    public void M200_TwoLaneOr_DerivedGetsPerType_BaseGetsGlobalOnly()
    {
        var property = UIProperty.RegisterAttached<Tab, Host, int>(UniqueName("M200"));
        property.GlobalEffects = PropertyEffects.AffectsArrange;
        property.AddPerTypeEffects(typeof(DerivedHost), PropertyEffects.AffectsRender);

        Assert.Equal(
            PropertyEffects.AffectsRender | PropertyEffects.AffectsArrange,
            property.GetEffects(typeof(DerivedHost))); // perType | Global
        Assert.Equal(PropertyEffects.AffectsArrange, property.GetEffects(typeof(Host))); // Global only
    }

    [Fact]
    public void M201_EffectsWrites_AfterTouch_Throw()
    {
        var property = UIProperty.RegisterAttached<Tab, Host, int>(UniqueName("M201"));
        _ = new Host().GetValue(property); // any touch closes the registration window

        Assert.Throws<InvalidOperationException>(() => property.GlobalEffects = PropertyEffects.AffectsRender);
        Assert.Throws<InvalidOperationException>(() => property.AddPerTypeEffects(typeof(Host), PropertyEffects.AffectsRender));
    }

    [Fact]
    public void M204_HostTypeValidation_DebugOnly()
    {
        var other = new OtherHost(); // a UIObject, but not a Host

#if DEBUG
        Assert.Throws<InvalidOperationException>(() => other.SetValue(Pa, 2));
#else
        other.SetValue(Pa, 2); // release: no check, write proceeds
        Assert.Equal(2, other.GetValue(Pa));
#endif
    }
}
