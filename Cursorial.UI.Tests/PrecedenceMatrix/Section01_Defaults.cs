using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>Matrix §1 — defaults, registration, metadata (M1–M14).</summary>
public class Section01_Defaults
{
    [Fact]
    public void M001_FreshHost_GetValue_ReturnsDefault_NoStoreAllocated()
    {
        var host = new Host();

        Assert.Equal(0, host.GetValue(P));
        Assert.Null(host.DebugValueStore);
    }

    [Fact]
    public void M002_FreshHost_GetValueSource_IsDefault_NotCurrent()
    {
        var host = new Host();

        Assert.Equal(new ValueSource(BindingPriority.Default, false), host.GetValueSource(P));
    }

    [Fact]
    public void M003_FreshHost_IsSet_IsFalse()
    {
        var host = new Host();

        Assert.False(host.IsSet(P));
    }

    [Fact]
    public void M004_FreshHost_GetBaseValue_ReturnsDefault()
    {
        var host = new Host();

        Assert.Equal(0, host.GetBaseValue(P));
    }

    [Fact]
    public void M005_OverrideDefaultValue_OnDerived_ReadsPerType()
    {
        // The override itself is applied fixture-time (pre-touch by construction; see MatrixFixture).
        var derived = new DerivedHost();
        var host = new Host();

        Assert.Equal(9, derived.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Default, false), derived.GetValueSource(P));
        Assert.Equal(0, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Default, false), host.GetValueSource(P));
    }

    [Fact]
    public void M006_DefaultResolvesAgainstRuntimeType()
    {
        Host host = new DerivedHost(); // statically typed Host, holding a DerivedHost

        Assert.Equal(9, host.GetValue(P));
    }

    [Fact]
    public void M007_OverrideMetadata_AfterTouch_Throws()
    {
        var property = UIProperty.Register<Host, int>(UniqueName("M7"));
        _ = new DerivedHost().GetValue(property); // a GetValue counts as a touch

        Assert.Throws<InvalidOperationException>(
            () => property.OverrideMetadata<DerivedHost>(new PropertyMetadata<int>(1)));
    }

    [Fact]
    public void M008_ChangedCallbacks_ChainBaseFirst()
    {
        var log = new List<string>();
        var property = UIProperty.Register<Host, int>(
            UniqueName("M8"), changed: (_, _, _) => log.Add("cb1"));
        property.OverrideMetadata<DerivedHost>(new PropertyMetadata<int>(0, Changed: (_, _, _) => log.Add("cb2")));

        new DerivedHost().SetValue(property, 1);

        Assert.Equal(["cb1", "cb2"], log);
    }

    [Fact]
    public void M009_Comparer_NearestWins_DoesNotChain()
    {
        var property = UIProperty.Register<Host, string?>(
            UniqueName("M9"), new PropertyMetadata<string?>(Comparer: StringComparer.OrdinalIgnoreCase));
        property.OverrideMetadata<DerivedHost>(new PropertyMetadata<string?>(Comparer: StringComparer.Ordinal));

        var derived = new DerivedHost();
        var derivedRecorder = new TypedRecorder<string?>();
        derived.AddObserver(property, derivedRecorder);
        derived.SetValue(property, "abc");
        derived.SetValue(property, "ABC");
        Assert.Equal(2, derivedRecorder.Deliveries.Count); // ordinal: second write notifies

        var host = new Host();
        var hostRecorder = new TypedRecorder<string?>();
        host.AddObserver(property, hostRecorder);
        host.SetValue(property, "abc");
        host.SetValue(property, "ABC");
        Assert.Single(hostRecorder.Deliveries); // ignore-case: second write silent
        Assert.Equal("abc", host.GetValue(property));
    }

    [Fact]
    public void M010_DefaultBypassesCoercion()
    {
        var host = new Host();

        Assert.Equal(500, host.GetValue(Pc2)); // default 500 with clamp [0,100] — PD8
    }

    [Fact]
    public void M011_AddOwner_SharedIdentityAndStorage()
    {
        Assert.Same(P, P2);
        Assert.Equal(P.Id, P2.Id);
        Assert.Same(P, UIPropertyRegistry.Find(typeof(OtherHost), "P"));

        var host = new Host();
        host.SetValue(P2, 5);
        Assert.Equal(5, host.GetValue(P));
    }

    [Fact]
    public void M012_AddOwner_PerOwnerDefaultOverride()
    {
        Assert.Equal(7, new OtherHost().GetValue(P));
        Assert.Equal(0, new Host().GetValue(P));
    }

    [Fact]
    public void M013_GetMetadata_ReturnsCachedInstance()
    {
        Assert.Same(P.GetMetadata(typeof(DerivedHost)), P.GetMetadata(typeof(DerivedHost)));
    }

    [Fact]
    public void M014_UntypedGetValue_ReturnsBoxedDefault_NeverUnsetValue()
    {
        var host = new Host();

        var boxed = host.GetValue((UIProperty)P);

        Assert.Equal(0, boxed);
        Assert.NotSame(UIProperty.UnsetValue, boxed);
    }
}
