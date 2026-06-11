using Cursorial.UI;

namespace Cursorial.Tests.UI;

/// <summary>
/// Registry behavior: <c>(Type, string)</c> lookup with base-chain walk, <c>AddOwner</c> identity
/// and duplicate detection (M11's registry leg), <c>FindOwnersByShortName</c> (ledger A15), and the
/// cached inheriting-property id set.
/// </summary>
public class UIPropertyRegistryTests
{
    private class RegistryHost : UIObject;

    private sealed class RegistryDerivedHost : RegistryHost;

    private sealed class RegistryOtherHost : UIObject;

    [Fact]
    public void Find_ExactOwner()
    {
        var p = UIProperty.Register<RegistryHost, int>("RgFindExact");

        Assert.Same(p, UIPropertyRegistry.Find(typeof(RegistryHost), "RgFindExact"));
    }

    [Fact]
    public void Find_WalksBaseChain()
    {
        var p = UIProperty.Register<RegistryHost, int>("RgFindChain");

        Assert.Same(p, UIPropertyRegistry.Find(typeof(RegistryDerivedHost), "RgFindChain"));
    }

    [Fact]
    public void Find_Unknown_ReturnsNull()
        => Assert.Null(UIPropertyRegistry.Find(typeof(RegistryHost), "RgNoSuchProperty"));

    [Fact]
    public void AddOwner_ReturnsSameInstance_AndRegistersLookup()
    {
        var p = UIProperty.Register<RegistryHost, int>("RgAddOwner");
        var p2 = p.AddOwner<RegistryOtherHost>();

        Assert.Same(p, p2); // shared dense id is the contract (M11 shape)
        Assert.Same(p, UIPropertyRegistry.Find(typeof(RegistryOtherHost), "RgAddOwner"));
    }

    [Fact]
    public void AddOwner_DuplicateName_Throws()
    {
        var p = UIProperty.Register<RegistryHost, int>("RgAddOwnerDup");
        _ = UIProperty.Register<RegistryOtherHost, int>("RgAddOwnerDup");

        Assert.Throws<ArgumentException>(() => p.AddOwner<RegistryOtherHost>());
    }

    [Fact]
    public void FindOwnersByShortName_ListsAllOwnersInRegistrationOrder()
    {
        _ = UIProperty.Register<RegistryHost, int>("RgShortName");
        var second = UIProperty.Register<RegistryOtherHost, string?>("RgShortName");
        second.AddOwner<RegistryDerivedHost>();

        var owners = UIPropertyRegistry.FindOwnersByShortName("RgShortName");

        Assert.Equal([typeof(RegistryHost), typeof(RegistryOtherHost), typeof(RegistryDerivedHost)], owners);
        Assert.True(owners.Count > 1); // the caller's ambiguity signal (A15)
    }

    [Fact]
    public void FindOwnersByShortName_Unknown_ReturnsEmpty()
        => Assert.Empty(UIPropertyRegistry.FindOwnersByShortName("RgNoSuchShortName"));

    [Fact]
    public void InheritingPropertyIds_TracksRegistrations()
    {
        var inheriting = UIProperty.Register<RegistryHost, int>("RgInheriting1", inherits: true);
        var plain = UIProperty.Register<RegistryHost, int>("RgPlain1");

        var ids = UIPropertyRegistry.InheritingPropertyIds;
        Assert.Contains(inheriting.Id, ids);
        Assert.DoesNotContain(plain.Id, ids);

        // The cache is invalidated by later registrations.
        var inheriting2 = UIProperty.Register<RegistryHost, int>("RgInheriting2", inherits: true);
        Assert.Contains(inheriting2.Id, UIPropertyRegistry.InheritingPropertyIds);
    }
}
