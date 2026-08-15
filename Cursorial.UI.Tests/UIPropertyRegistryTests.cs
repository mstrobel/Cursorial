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
    public void RegisterAttached_TargetsChildrenMarker_RoundTrips()
    {
        var childOnly = UIProperty.RegisterAttached<RegistryHost, RegistryHost, int>("RgAttachedChildOnly", targetsChildren: true);
        var selfUsable = UIProperty.RegisterAttached<RegistryHost, RegistryHost, int>("RgAttachedSelfUsable");
        var plainStyled = UIProperty.Register<RegistryHost, int>("RgPlainNotChildTargeted");

        Assert.True(childOnly.TargetsChildElements);
        Assert.False(selfUsable.TargetsChildElements);   // default polarity: UNMARKED = self-usable
        Assert.False(plainStyled.TargetsChildElements);  // a non-attached property is never child-targeted
    }

    [Fact]
    public void OwnMembersOf_ExcludesChildTargeted_IncludesSelfUsable_ButBothStayAttachable()
    {
        var childOnly = UIProperty.RegisterAttached<RegistryHost, RegistryHost, int>("RgOwnChildOnly", targetsChildren: true);
        var selfUsable = UIProperty.RegisterAttached<RegistryHost, RegistryHost, int>("RgOwnSelfUsable");

        var own = UIPropertyRegistry.OwnMembersOf(typeof(RegistryHost));
        Assert.Contains(selfUsable, own);       // unmarked self-usable attached declaration IS an own member
        Assert.DoesNotContain(childOnly, own);  // marked child-only attached declaration is NOT

        // The marker governs ONLY own-membership — BOTH remain attachable on any assignable host (band 1/3).
        var attachable = UIPropertyRegistry.AttachableOnType(typeof(RegistryHost));
        Assert.Contains(childOnly, attachable);
        Assert.Contains(selfUsable, attachable);
    }

    [Fact]
    public void Grid_Row_IsAttachable_ButNotAnOwnMember_TheNestedGridDivergence()
    {
        _ = Cursorial.UI.Controls.Grid.RowProperty; // force Grid's static ctor so its attached declarations register

        var own = UIPropertyRegistry.OwnMembersOf(typeof(Cursorial.UI.Controls.Grid)).Select(p => p.Name).ToList();
        var attachable = UIPropertyRegistry.AttachableOnType(typeof(Cursorial.UI.Controls.Grid)).Select(p => p.Name).ToList();

        // A Grid nested inside another Grid is STILL offered Grid.Row (attachable / band 1)...
        Assert.Contains("Row", attachable);
        Assert.Contains("Column", attachable);
        // ...yet Grid.Row is not a Grid's own intrinsic member (band 2). The two queries diverge — the whole point.
        Assert.DoesNotContain("Row", own);
        Assert.DoesNotContain("Column", own);
    }

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
