using Cursorial.UI;

namespace Cursorial.Tests.UI;

/// <summary>
/// The two-lane effects design (ledger A1): global lane delivery (M199 shape), per-type lane with
/// chain resolution and the two-lane OR (M200 shape), the PER-TYPE effects freeze (M201, amended — a
/// type's resolution freezes only it + its bases, never a sibling; the AddOwner-cascade fix),
/// and the <c>Affects*</c> sugar (ledger A2): per-type for the registering type, plus the global
/// lane for <b>attached</b> properties only (doc §5.5 — ordinary styled properties stay per-type so
/// effects dispatch is bounded; layout-matrix L196).
/// </summary>
public class PropertyEffectsTests
{
    private class FxHost : UIObject
    {
        // Expose the protected UIObject sugar for the tests.
        public static void RegisterAffects<TOwner>(PropertyEffects effects, params UIProperty[] properties)
            where TOwner : UIObject
        {
            switch (effects)
            {
                case PropertyEffects.AffectsMeasure: AffectsMeasure<TOwner>(properties); break;
                case PropertyEffects.AffectsArrange: AffectsArrange<TOwner>(properties); break;
                case PropertyEffects.AffectsRender: AffectsRender<TOwner>(properties); break;
                case PropertyEffects.AffectsComposite: AffectsComposite<TOwner>(properties); break;
                case PropertyEffects.AffectsParentMeasure: AffectsParentMeasure<TOwner>(properties); break;
                case PropertyEffects.AffectsParentArrange: AffectsParentArrange<TOwner>(properties); break;
                default: throw new ArgumentOutOfRangeException(nameof(effects));
            }
        }
    }

    private class FxDerivedHost : FxHost;

    private sealed class FxOtherHost : UIObject;

    [Fact]
    public void GlobalLane_DeliversToEveryType()
    {
        var p = UIProperty.Register<FxHost, int>("FxGlobal");
        p.GlobalEffects = PropertyEffects.AffectsArrange; // written during the registration window

        // M199 shape: types whose per-type tables never saw the registration still get the effects.
        Assert.Equal(PropertyEffects.AffectsArrange, p.GetEffects(typeof(FxHost)));
        Assert.Equal(PropertyEffects.AffectsArrange, p.GetEffects(typeof(FxOtherHost)));
    }

    [Fact]
    public void PerTypeLane_TwoLaneOr_DoesNotLeakToBase()
    {
        var p = UIProperty.Register<FxHost, int>("FxTwoLane");
        p.GlobalEffects = PropertyEffects.AffectsArrange;
        p.AddPerTypeEffects(typeof(FxDerivedHost), PropertyEffects.AffectsMeasure);

        // M200 shape: derived = perType | Global; base = Global only.
        Assert.Equal(
            PropertyEffects.AffectsMeasure | PropertyEffects.AffectsArrange,
            p.GetEffects(typeof(FxDerivedHost)));
        Assert.Equal(PropertyEffects.AffectsArrange, p.GetEffects(typeof(FxHost)));
    }

    [Fact]
    public void PerTypeLane_ResolvesThroughInheritanceChain()
    {
        var p = UIProperty.Register<FxHost, int>("FxChain");
        p.AddPerTypeEffects(typeof(FxHost), PropertyEffects.AffectsRender);

        Assert.Equal(PropertyEffects.AffectsRender, p.GetEffects(typeof(FxDerivedHost)));
        Assert.Equal(PropertyEffects.None, p.GetEffects(typeof(FxOtherHost)));
    }

    [Fact]
    public void PerTypeLane_AccumulatesViaOr()
    {
        var p = UIProperty.Register<FxHost, int>("FxAccumulate");
        p.AddPerTypeEffects(typeof(FxHost), PropertyEffects.AffectsMeasure);
        p.AddPerTypeEffects(typeof(FxHost), PropertyEffects.AffectsRender);

        Assert.Equal(
            PropertyEffects.AffectsMeasure | PropertyEffects.AffectsRender,
            p.GetEffects(typeof(FxHost)));
    }

    [Fact] // M201 (amended): the GLOBAL lane freezes once any type resolves effects; per-type registration is gated
           // PER-TYPE — only the resolved type and its BASES are frozen, never an unrelated sibling or a descendant.
    public void EffectsResolution_FreezesGlobalAndTheResolvedTypesChain_NotSiblings()
    {
        var p = UIProperty.Register<FxHost, int>("FxFreezeOnQuery");
        Assert.False(p.IsRegistrationWindowClosed);

        _ = p.GetEffects(typeof(FxHost)); // FxHost resolves its effects

        // The global lane is frozen (a global write would alter FxHost's already-resolved result).
        Assert.True(p.IsRegistrationWindowClosed);
        Assert.Throws<InvalidOperationException>(() => p.GlobalEffects = PropertyEffects.AffectsRender);

        // Re-registering for the resolved type, or a BASE of it (would invalidate FxHost's cache), throws.
        Assert.Throws<InvalidOperationException>(() => p.AddPerTypeEffects(typeof(FxHost), PropertyEffects.AffectsMeasure));
        Assert.Throws<InvalidOperationException>(() => p.AddPerTypeEffects(typeof(UIObject), PropertyEffects.AffectsMeasure));

        // But a DERIVED type can still register — its resolution hasn't happened and FxHost's cache is unaffected.
        p.AddPerTypeEffects(typeof(FxDerivedHost), PropertyEffects.AffectsMeasure);
        Assert.Equal(PropertyEffects.AffectsMeasure, p.GetEffects(typeof(FxDerivedHost)));
    }

    [Fact] // M201 (amended): the AddOwner sibling-cascade fix — a sibling owner resolving the property must NOT freeze
           // another sibling's effect registration (e.g. a Panel resolving Background locking out
           // AffectsRender<Control>(Background), which surfaced as a Control..cctor TypeInitializationException).
    public void PerTypeEffects_SiblingResolution_DoesNotFreezeOtherTypes()
    {
        var p = UIProperty.Register<FxHost, int>("FxSiblingFreeze");

        _ = p.GetEffects(typeof(FxHost)); // one owner resolves first (previously closed the whole window)

        // An unrelated sibling type can still register its per-type effects — the cascade fix.
        p.AddPerTypeEffects(typeof(FxOtherHost), PropertyEffects.AffectsRender);
        Assert.Equal(PropertyEffects.AffectsRender, p.GetEffects(typeof(FxOtherHost)));
    }

    [Fact] // M201 (amended): metadata resolution seals further metadata OVERRIDES for the resolved type, but does NOT
           // freeze the effects lane (the decouple — a rendered element reading a value can't lock out AffectsX).
    public void MetadataResolution_SealsOverridesButNotEffects()
    {
        var p = UIProperty.Register<FxHost, int>("FxMetaDecouple");
        _ = p.GetMetadata(typeof(FxHost)); // a GetValue's metadata leg

        // The effects lane stays open: global + per-type effects can still be registered.
        Assert.False(p.IsRegistrationWindowClosed);
        p.GlobalEffects = PropertyEffects.AffectsRender;                     // no throw
        p.AddPerTypeEffects(typeof(FxHost), PropertyEffects.AffectsMeasure); // no throw — effects were not resolved

        // But a metadata OVERRIDE for the already-resolved type is sealed (the per-type _resolved gate, unchanged).
        Assert.Throws<InvalidOperationException>(() => p.OverrideMetadata<FxHost>(new PropertyMetadata<int>(7)));
    }

    [Theory]
    [InlineData(PropertyEffects.AffectsMeasure)]
    [InlineData(PropertyEffects.AffectsArrange)]
    [InlineData(PropertyEffects.AffectsRender)]
    [InlineData(PropertyEffects.AffectsComposite)]
    [InlineData(PropertyEffects.AffectsParentMeasure)]
    [InlineData(PropertyEffects.AffectsParentArrange)]
    public void Sugar_StyledProperty_WritesPerTypeLaneOnly(PropertyEffects effects)
    {
        var p = UIProperty.Register<FxHost, int>($"FxSugar{effects}");
        FxHost.RegisterAffects<FxDerivedHost>(effects, p);

        // Per-type lane for the registering type only (doc §5.5): unrelated types see nothing —
        // what bounds inherited-change fan-out to types that opted in (layout-matrix L196).
        Assert.True(p.GetEffects(typeof(FxDerivedHost)).HasFlag(effects));
        Assert.False(p.GetEffects(typeof(FxOtherHost)).HasFlag(effects));
        Assert.False(p.GlobalEffects.HasFlag(effects));
    }

    [Theory]
    [InlineData(PropertyEffects.AffectsRender)]
    [InlineData(PropertyEffects.AffectsParentMeasure)]
    public void Sugar_AttachedProperty_AlsoWritesGlobalLane(PropertyEffects effects)
    {
        var p = UIProperty.RegisterAttached<FxHost, UIObject, int>($"FxSugarAttached{effects}");
        FxHost.RegisterAffects<FxHost>(effects, p);

        // The global lane is mandatory for attached properties (A1): a host type's per-type table
        // can freeze before the declaring type's static ctor runs — without this lane
        // Grid.SetRow(button, 2) would invalidate nothing.
        Assert.True(p.GetEffects(typeof(FxOtherHost)).HasFlag(effects));
        Assert.True(p.GlobalEffects.HasFlag(effects));
    }

    [Fact]
    public void Sugar_AfterFreeze_Throws()
    {
        var p = UIProperty.Register<FxHost, int>("FxSugarFrozen");
        _ = p.GetEffects(typeof(FxHost));

        Assert.Throws<InvalidOperationException>(
            () => FxHost.RegisterAffects<FxHost>(PropertyEffects.AffectsRender, p));
    }

    [Fact]
    public void GetEffects_RepeatedQueriesAreStable()
    {
        var p = UIProperty.Register<FxHost, int>("FxStable");
        p.AddPerTypeEffects(typeof(FxHost), PropertyEffects.AffectsComposite);

        var first = p.GetEffects(typeof(FxDerivedHost));
        var second = p.GetEffects(typeof(FxDerivedHost));

        Assert.Equal(first, second);
        Assert.Equal(PropertyEffects.AffectsComposite, second);
    }
}
