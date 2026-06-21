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

    [Fact] // M201 (amended): resolving effects freezes the global lane + the resolved type's own chain (per-type).
    public void M201_EffectsResolution_FreezesGlobalAndResolvedChain()
    {
        var property = UIProperty.RegisterAttached<Tab, Host, int>(UniqueName("M201"));
        _ = property.GetEffects(typeof(Host)); // Host resolves its effects

        Assert.Throws<InvalidOperationException>(() => property.GlobalEffects = PropertyEffects.AffectsRender);
        Assert.Throws<InvalidOperationException>(() => property.AddPerTypeEffects(typeof(Host), PropertyEffects.AffectsRender));
        Assert.Throws<InvalidOperationException>(() => property.AddPerTypeEffects(typeof(UIObject), PropertyEffects.AffectsRender)); // a base
    }

    [Fact] // M201a (amended): the AddOwner cascade fix — a sibling/derived registration after another type resolved succeeds.
    public void M201a_EffectsRegistration_AfterSiblingResolution_Succeeds()
    {
        var property = UIProperty.RegisterAttached<Tab, Host, int>(UniqueName("M201a"));
        _ = property.GetEffects(typeof(Host));

        property.AddPerTypeEffects(typeof(OtherHost), PropertyEffects.AffectsRender); // unrelated sibling
        Assert.Equal(PropertyEffects.AffectsRender, property.GetEffects(typeof(OtherHost)));

        property.AddPerTypeEffects(typeof(DerivedHost), PropertyEffects.AffectsMeasure); // derived
        Assert.Equal(PropertyEffects.AffectsMeasure, property.GetEffects(typeof(DerivedHost)));
    }

    [Fact] // M201b (amended): metadata resolution does NOT freeze the effects lane (the decouple).
    public void M201b_MetadataResolution_DoesNotFreezeEffects()
    {
        var property = UIProperty.RegisterAttached<Tab, Host, int>(UniqueName("M201b"));
        _ = property.GetMetadata(typeof(Host)); // metadata-only resolution

        property.GlobalEffects = PropertyEffects.AffectsRender;                   // allowed — effects not resolved
        property.AddPerTypeEffects(typeof(Host), PropertyEffects.AffectsMeasure); // allowed
        Assert.Equal(PropertyEffects.AffectsRender | PropertyEffects.AffectsMeasure, property.GetEffects(typeof(Host)));
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
