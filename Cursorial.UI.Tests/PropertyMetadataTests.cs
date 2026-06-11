using Cursorial.UI;

namespace Cursorial.Tests.UI;

/// <summary>
/// Metadata resolution and merge: per-type resolved tables (cached — M13 shape), override scoping
/// (M5/M7 shapes at the metadata level), the pinned merge rules (<c>Changed</c> chains base-first —
/// M8 shape; non-<c>Changed</c> members nearest-wins — M9 shape; defaults replace), AddOwner-target
/// overrides (M12 shape), and the A21 <c>ParsesAccessKeyLiterals</c> flag slot.
/// </summary>
public class PropertyMetadataTests
{
    private class MetaHost : UIObject;

    private class MetaDerivedHost : MetaHost;

    private sealed class MetaGrandchildHost : MetaDerivedHost;

    private sealed class MetaOtherHost : UIObject;

    [Fact]
    public void GetMetadata_ReturnsRegistrationMetadata_ForOwnerDerivedAndUnrelatedTypes()
    {
        var metadata = new PropertyMetadata<int>(5);
        var p = UIProperty.Register<MetaHost, int>("MetaRegistration", metadata);

        Assert.Same(metadata, p.GetMetadata(typeof(MetaHost)));
        Assert.Same(metadata, p.GetMetadata(typeof(MetaDerivedHost)));
        Assert.Same(metadata, p.GetMetadata(typeof(MetaOtherHost)));
    }

    [Fact]
    public void GetMetadata_CachesResolvedInstance()
    {
        var p = UIProperty.Register<MetaHost, int>("MetaCache", defaultValue: 1, coerce: static (_, v) => v);
        p.OverrideDefaultValue<MetaDerivedHost>(9);

        var first = p.GetMetadata(typeof(MetaDerivedHost));
        var second = p.GetMetadata(typeof(MetaDerivedHost));

        Assert.Same(first, second); // merged-result cache (M13 shape)
    }

    [Fact]
    public void OverrideDefaultValue_AffectsDerivedOnly()
    {
        var p = UIProperty.Register<MetaHost, int>("MetaDefaultDerived");
        p.OverrideDefaultValue<MetaDerivedHost>(9);

        Assert.Equal(9, p.GetMetadata(typeof(MetaDerivedHost)).DefaultValue); // M5 shape
        Assert.Equal(9, p.GetMetadata(typeof(MetaGrandchildHost)).DefaultValue); // chain resolution
        Assert.Equal(0, p.GetMetadata(typeof(MetaHost)).DefaultValue);
    }

    [Fact]
    public void OverrideMetadata_AfterTouch_Throws()
    {
        var p = UIProperty.Register<MetaHost, int>("MetaTouchFreeze");
        _ = p.GetMetadata(typeof(MetaDerivedHost)); // a GetValue counts as a touch; this is its metadata leg

        // A MetaDerivedHost instance is also a MetaHost instance — both overrides are blocked (M7 shape).
        Assert.Throws<InvalidOperationException>(() => p.OverrideDefaultValue<MetaDerivedHost>(1));
        Assert.Throws<InvalidOperationException>(() => p.OverrideDefaultValue<MetaHost>(1));
    }

    [Fact]
    public void OverrideMetadata_BaseTouch_StillAllowsDerivedOverride()
    {
        var p = UIProperty.Register<MetaHost, int>("MetaBaseTouch");
        _ = p.GetMetadata(typeof(MetaHost)); // touching the base does not freeze untouched derived branches

        p.OverrideDefaultValue<MetaDerivedHost>(9);

        Assert.Equal(9, p.GetMetadata(typeof(MetaDerivedHost)).DefaultValue);
        Assert.Equal(0, p.GetMetadata(typeof(MetaHost)).DefaultValue);
    }

    [Fact]
    public void OverrideMetadata_DuplicateForSameType_Throws()
    {
        var p = UIProperty.Register<MetaHost, int>("MetaDuplicateOverride");
        p.OverrideDefaultValue<MetaDerivedHost>(1);

        Assert.Throws<ArgumentException>(() => p.OverrideDefaultValue<MetaDerivedHost>(2));
    }

    [Fact]
    public void ChangedCallbacks_ChainBaseFirst()
    {
        var order = new List<string>();
        var p = UIProperty.Register<MetaHost, int>(
            "MetaChangedChain", changed: (_, _, _) => order.Add("registration"));
        p.OverrideMetadata<MetaHost>(new PropertyMetadata<int>(Changed: (_, _, _) => order.Add("host")));
        p.OverrideMetadata<MetaDerivedHost>(new PropertyMetadata<int>(Changed: (_, _, _) => order.Add("derived")));

        var resolved = p.GetMetadata(typeof(MetaDerivedHost));
        resolved.Changed!(new MetaDerivedHost(), 0, 1);

        Assert.Equal(["registration", "host", "derived"], order); // base-first (M8 shape)
    }

    [Fact]
    public void NonChangedMembers_NearestWins()
    {
        var p = UIProperty.Register<MetaHost, string?>(
            "MetaComparer", new PropertyMetadata<string?>(Comparer: StringComparer.OrdinalIgnoreCase));
        p.OverrideMetadata<MetaDerivedHost>(new PropertyMetadata<string?>(Comparer: StringComparer.Ordinal));

        Assert.Same(StringComparer.Ordinal, p.GetMetadata(typeof(MetaDerivedHost)).Comparer); // M9 shape
        Assert.Same(StringComparer.OrdinalIgnoreCase, p.GetMetadata(typeof(MetaHost)).Comparer);
    }

    [Fact]
    public void CoerceAndValidate_FallThroughWhenNull()
    {
        Func<UIObject, int, int> coerce = static (_, v) => Math.Clamp(v, 0, 100);
        Func<int, bool> validate = static v => v >= 0;
        var p = UIProperty.Register<MetaHost, int>("MetaFallThrough", 0, coerce: coerce, validate: validate);
        p.OverrideMetadata<MetaDerivedHost>(new PropertyMetadata<int>(Changed: static (_, _, _) => { }));

        var resolved = p.GetMetadata(typeof(MetaDerivedHost));

        Assert.Same(coerce, resolved.Coerce);
        Assert.Same(validate, resolved.Validate);
    }

    [Fact]
    public void DefaultValue_Replaces_EvenWhenOverrideOmitsIt()
    {
        // Pinned merge rule (design doc §2): defaults REPLACE — DefaultValue is structural, so an
        // override that omits it resets the default to default(T). Overriders wanting to keep the
        // base default must restate it (or use OverrideDefaultValue for the default-only case).
        var p = UIProperty.Register<MetaHost, int>("MetaDefaultReplaces", defaultValue: 5);
        p.OverrideMetadata<MetaDerivedHost>(new PropertyMetadata<int>(Changed: static (_, _, _) => { }));

        Assert.Equal(0, p.GetMetadata(typeof(MetaDerivedHost)).DefaultValue);
        Assert.Equal(5, p.GetMetadata(typeof(MetaHost)).DefaultValue);
    }

    [Fact]
    public void ParsesAccessKeyLiterals_NearestWins_AndFallsThroughWhenNull()
    {
        var p = UIProperty.Register<MetaHost, string?>("MetaAccessKeys");
        Assert.Null(p.GetMetadata(typeof(MetaHost)).ParsesAccessKeyLiterals); // effective default: false

        p.OverrideMetadata<MetaDerivedHost>(new PropertyMetadata<string?>
        {
            ParsesAccessKeyLiterals = true,
        });
        p.OverrideMetadata<MetaGrandchildHost>(new PropertyMetadata<string?>(
            Changed: static (_, _, _) => { })); // flag null ⇒ falls through to the derived override

        Assert.True(p.GetMetadata(typeof(MetaDerivedHost)).ParsesAccessKeyLiterals);
        Assert.True(p.GetMetadata(typeof(MetaGrandchildHost)).ParsesAccessKeyLiterals);
        Assert.Null(p.GetMetadata(typeof(MetaHost)).ParsesAccessKeyLiterals);
    }

    [Fact]
    public void AddOwnerTarget_OverridesResolveForTheNewOwner()
    {
        var p = UIProperty.Register<MetaHost, int>("MetaAddOwnerDefault");
        p.AddOwner<MetaOtherHost>().OverrideDefaultValue<MetaOtherHost>(7);

        Assert.Equal(7, p.GetMetadata(typeof(MetaOtherHost)).DefaultValue); // M12 shape
        Assert.Equal(0, p.GetMetadata(typeof(MetaHost)).DefaultValue);
    }

    [Fact]
    public void EffectiveComparer_DefaultsToEqualityComparer()
    {
        var withComparer = new PropertyMetadata<string?>(Comparer: StringComparer.OrdinalIgnoreCase);
        var without = new PropertyMetadata<string?>();

        Assert.Same(StringComparer.OrdinalIgnoreCase, withComparer.EffectiveComparer);
        Assert.Same(EqualityComparer<string?>.Default, without.EffectiveComparer);
    }
}
