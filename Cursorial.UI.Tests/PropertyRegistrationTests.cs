using Cursorial.UI;

namespace Cursorial.Tests.UI;

/// <summary>
/// Registration surface: <c>Register</c> / <c>RegisterAttached</c> / <c>RegisterReadOnly</c> /
/// <c>RegisterAttachedReadOnly</c> / <c>RegisterDirect</c>, identity members, dense ids, duplicate
/// detection, name validation, and the A14 sentinel. (The precedence matrix M-rows land with the
/// store; these pin the identity layer beneath them.)
/// </summary>
public class PropertyRegistrationTests
{
    private sealed class RegHost : UIObject;

    private sealed class RegDirectHost : UIObject
    {
        public int Field;
    }

    /// <summary>Attached-property declaring type that is deliberately NOT a UIObject (the fixture's Tab shape).</summary>
    private sealed class TabOwner
    {
        private TabOwner() { }
    }

    [Fact]
    public void Register_PopulatesIdentity()
    {
        var p = UIProperty.Register<RegHost, int>("RegIdentity", defaultValue: 42);

        Assert.Equal("RegIdentity", p.Name);
        Assert.Equal(typeof(int), p.PropertyType);
        Assert.Equal(typeof(RegHost), p.OwnerType);
        Assert.False(p.Inherits);
        Assert.False(p.IsAttached);
        Assert.False(p.IsDirect);
        Assert.False(p.IsReadOnly);
        Assert.True(p.Id >= 0);
        Assert.Equal(42, p.GetMetadata(typeof(RegHost)).DefaultValue);
    }

    [Fact]
    public void Register_DenseIds_AreDistinctAndMonotonic()
    {
        var p1 = UIProperty.Register<RegHost, int>("RegDense1");
        var p2 = UIProperty.Register<RegHost, int>("RegDense2");

        Assert.True(p2.Id > p1.Id);
        Assert.Same(p1, UIPropertyRegistry.FindById(p1.Id));
        Assert.Same(p2, UIPropertyRegistry.FindById(p2.Id));
    }

    [Fact]
    public void Register_DuplicateOwnerAndName_Throws()
    {
        _ = UIProperty.Register<RegHost, int>("RegDuplicate");

        Assert.Throws<ArgumentException>(() => UIProperty.Register<RegHost, int>("RegDuplicate"));
        Assert.Throws<ArgumentException>(() => UIProperty.Register<RegHost, string?>("RegDuplicate"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Grid.Row")]
    public void Register_InvalidName_Throws(string? name)
        => Assert.ThrowsAny<ArgumentException>(() => UIProperty.Register<RegHost, int>(name!));

    [Fact]
    public void Register_Inherits_SetsFlagAndSurfacesInEffects()
    {
        var p = UIProperty.Register<RegHost, int>("RegInherits", inherits: true);

        Assert.True(p.Inherits);
        Assert.True(p.GetEffects(typeof(RegHost)).HasFlag(PropertyEffects.Inherits));
        Assert.True(p.GetEffects(typeof(object)).HasFlag(PropertyEffects.Inherits)); // global by definition
    }

    [Fact]
    public void RegisterAttached_SetsHostAndNonUIObjectOwner()
    {
        var pa = UIProperty.RegisterAttached<TabOwner, RegHost, int>("RegAttachedIndex", defaultValue: 7);

        Assert.True(pa.IsAttached);
        Assert.False(pa.IsDirect);
        Assert.Equal(typeof(TabOwner), pa.OwnerType); // declaring type need not be a UIObject
        Assert.Equal(typeof(RegHost), pa.HostType);
        Assert.Equal(7, pa.GetMetadata(typeof(RegHost)).DefaultValue);
    }

    [Fact]
    public void RegisterReadOnly_ReturnsKey_PropertyOnlyReads()
    {
        var key = UIProperty.RegisterReadOnly<RegHost, int>("RegReadOnly", defaultValue: 3);

        Assert.True(key.Property.IsReadOnly);
        Assert.False(key.Property.IsAttached);
        Assert.Equal("RegReadOnly", key.Property.Name);
        Assert.Same(key.Property, UIPropertyRegistry.Find(typeof(RegHost), "RegReadOnly"));
    }

    [Fact]
    public void RegisterAttachedReadOnly_ReturnsKeyOverAttachedProperty()
    {
        var key = UIProperty.RegisterAttachedReadOnly<TabOwner, RegHost, int>("RegAttachedReadOnly");

        var attached = Assert.IsType<AttachedProperty<int>>(key.Property);
        Assert.True(attached.IsReadOnly);
        Assert.True(attached.IsAttached);
        Assert.Equal(typeof(RegHost), attached.HostType);
    }

    [Fact]
    public void RegisterDirect_WiresDelegatesAndUnsetValue()
    {
        var pd = UIProperty.RegisterDirect<RegDirectHost, int>(
            "RegDirect", static h => h.Field, static (h, v) => h.Field = v, unsetValue: -1);

        Assert.True(pd.IsDirect);
        Assert.False(pd.IsAttached);
        Assert.False(pd.IsReadOnly);
        Assert.False(pd.Inherits);
        Assert.Equal(-1, pd.UnsetValue);

        var host = new RegDirectHost();
        pd.Setter!(host, 9);
        Assert.Equal(9, pd.Getter(host));
        Assert.Equal(9, host.Field);
    }

    [Fact]
    public void RegisterDirect_NoSetter_IsReadOnly()
    {
        var pd = UIProperty.RegisterDirect<RegDirectHost, int>("RegDirectReadOnly", static h => h.Field);

        Assert.True(pd.IsReadOnly);
        Assert.Null(pd.Setter);
    }

    [Fact]
    public void UnsetValue_IsSingletonWithDiagnosticToString()
    {
        Assert.NotNull(UIProperty.UnsetValue);
        Assert.Equal("{UnsetValue}", UIProperty.UnsetValue.ToString());
    }

    [Fact]
    public void UnsetTargetProperty_IsTheUnregisteredSentinel()
    {
        var sentinel = UIProperty.UnsetTargetProperty;

        Assert.Equal(-1, sentinel.Id);
        Assert.Equal("(unset)", sentinel.Name);
        Assert.Null(UIPropertyRegistry.FindById(-1));
        Assert.Equal(PropertyEffects.None, sentinel.GetEffects(typeof(object)));
    }

    [Fact]
    public void ToString_IsOwnerDotName()
    {
        var p = UIProperty.Register<RegHost, int>("RegToString");

        Assert.Equal("RegHost.RegToString", p.ToString());
    }
}
