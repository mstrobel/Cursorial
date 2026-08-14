using System.Reflection;

using Cursorial.UI;
using Cursorial.UI.Xaml;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// Task #31 — XAML completion metadata: (1) <see cref="ReflectionXamlMetadata.GetKnownMemberNames"/> enumerates
/// only XAML-SETTABLE members (read-only computed properties like <c>Bounds</c> / <c>IsFocused</c> no longer leak),
/// and (2) the optional <see cref="IXamlOwnMemberProvider"/> seam exposes a type's OWN (declared / <c>AddOwner</c>'d)
/// members for band-2 completion ranking. The settable test is Cursorial's property read-only model
/// (<see cref="UIProperty.IsReadOnly"/>), NOT reflection <c>CanWrite</c>.
/// </summary>
public sealed class SettableMemberMetadataTests
{
    private static readonly ReflectionXamlMetadata Provider = ReflectionXamlMetadata.Instance;

    private static string[] KnownMembers(Type t) => Provider.GetKnownMemberNames(Provider.GetXamlType(t).ClrType);

    private static string[] OwnMembers(Type t) => Provider.GetOwnMemberNames(Provider.GetXamlType(t).ClrType);

    // ── Change 1: settable-only member enumeration (the read-only leak) ─────────────────────────────

    [Fact]
    public void KnownMembers_ExcludeReadOnlyMembers_TheLeakAtHead()
    {
        // At HEAD, GetKnownMemberNames added EVERY public instance property unconditionally, so all of these
        // get-only / read-only members leaked into completion. The settable filter removes them.
        var members = KnownMembers(typeof(UIControls.TextBlock));

        // read-only DirectProperty (setter-less RegisterDirect), get-only wrapper
        Assert.DoesNotContain("Bounds", members);
        Assert.DoesNotContain("DesiredSize", members);
        // read-only StyledProperty (RegisterReadOnly), get-only wrapper
        Assert.DoesNotContain("IsFocused", members);
        Assert.DoesNotContain("IsKeyboardFocusWithin", members);
        // plain get-only computed CLR property (no UIProperty, no setter)
        Assert.DoesNotContain("IsPointerOver", members);
        Assert.DoesNotContain("LogicalParent", members);
        // plain auto-property with a PRIVATE setter — reflection CanWrite is TRUE, but it is not XAML-settable
        Assert.DoesNotContain("IsArrangeValid", members);
        // an OWN read-only styled property on TextBlock (protected-set wrapper over a RegisterReadOnly key)
        Assert.DoesNotContain("IsTrimmed", members);
    }

    [Fact]
    public void KnownMembers_IncludeReadWriteMembers()
    {
        var members = KnownMembers(typeof(UIControls.TextBlock));

        Assert.Contains("Width", members);      // read-write StyledProperty (get/set wrapper), inherited
        Assert.Contains("IsEnabled", members);  // read-write StyledProperty (get/set wrapper), inherited
        Assert.Contains("Text", members);       // read-write StyledProperty declared on TextBlock
        Assert.Contains("Foreground", members); // read-write attached property AddOwner'd onto TextBlock (has a wrapper)
    }

    [Fact]
    public void KnownMembers_IncludeDirectReadWrite_ExcludeDirectReadOnly()
    {
        var combo = KnownMembers(typeof(UIControls.ComboBox));
        Assert.Contains("IsDropDownOpen", combo); // read-write DirectProperty (registered with a setter)

        var crumb = KnownMembers(typeof(UIControls.BreadcrumbBar));
        Assert.Contains("Text", crumb);            // read-write DirectProperty
        Assert.DoesNotContain("IsEditing", crumb); // read-only DirectProperty (no setter registered)
        Assert.DoesNotContain("HasOverflow", crumb);
    }

    [Fact]
    public void KnownMembers_KeepEvents()
    {
        var members = KnownMembers(typeof(UIControls.TextBlock));
        Assert.Contains("AttachedToLogicalTree", members); // inherited public event — an event-handler member
        Assert.Contains("KeyDown", members);
    }

    [Fact]
    public void KnownMembers_SettablePredicate_FollowsPropertyModel_NotCanWrite()
    {
        // The controlled fixture pins every branch of the settable rule (no real framework type combines a
        // read-write styled property with a get-only wrapper — see the report).
        var members = KnownMembers(typeof(MetadataFixture));

        // INCLUDE — read-write StyledProperty exposed through a GET-ONLY CLR wrapper: settable via the property
        // system though reflection CanWrite is FALSE. The load-bearing case Change 1 must get right.
        Assert.Contains(nameof(MetadataFixture.SettableStyledGetOnly), members);
        // INCLUDE — read-write DirectProperty; plain CLR property with a public setter; public event
        Assert.Contains(nameof(MetadataFixture.SettableDirect), members);
        Assert.Contains(nameof(MetadataFixture.PlainSettable), members);
        Assert.Contains(nameof(MetadataFixture.SomethingHappened), members);

        // EXCLUDE — read-only StyledProperty; read-only DirectProperty; get-only computed CLR property
        Assert.DoesNotContain(nameof(MetadataFixture.ReadOnlyStyled), members);
        Assert.DoesNotContain(nameof(MetadataFixture.ReadOnlyDirect), members);
        Assert.DoesNotContain(nameof(MetadataFixture.PlainComputed), members);
        // EXCLUDE — plain CLR property with a PRIVATE setter (reflection CanWrite is TRUE; no PUBLIC setter)
        Assert.DoesNotContain(nameof(MetadataFixture.PlainPrivateSetter), members);
    }

    [Fact]
    public void KnownMembers_IncludeReadOnlyContentCollections_Bug1()
    {
        // Bug 1: read-only COLLECTION/content properties are populated IN PLACE via property-element /
        // content syntax (<Panel.Children>…</Panel.Children>, <Style.Setters>…), so they are content-settable
        // and MUST be enumerable — unlike read-only SCALARS, which are settable in no XAML form (stay excluded).
        var style = KnownMembers(typeof(Style));
        Assert.Contains("Setters", style);   // SetterCollection — get-only, the [ContentProperty], filled as elements
        Assert.Contains("Children", style);  // StyleCollection — get-only nested styles
        Assert.Contains("Selector", style);  // a settable SCALAR (init setter) stays present — the union includes both

        var stack = KnownMembers(typeof(UIControls.StackPanel));
        Assert.Contains("Children", stack);  // UIElementCollection — get-only, the panel's content collection

        // Read-only SCALARS remain excluded (the #31 invariant the attribute path depends on).
        Assert.DoesNotContain("Bounds", stack);
        Assert.DoesNotContain("DesiredSize", stack);
    }

    [Fact]
    public void KnownMembers_IncludeReadOnlyResourceDictionary_Bug1Dictionaries()
    {
        // Bug 1, dictionary arm: a read-only ResourceDictionary property (UIElement.Resources) is populated in
        // place via <Owner.Resources>…</Owner.Resources>, so it is content-settable and MUST be enumerable. The
        // loader fills it through the dictionary add-item path, which IsCollectionType (list-only, and it
        // reports ResourceDictionary as false) does not cover — so content-settability names it explicitly.
        var members = KnownMembers(typeof(UIControls.StackPanel));
        Assert.Contains("Resources", members);
    }

    // ── Change 2: own-member provenance for band-2 ranking ──────────────────────────────────────────

    [Fact]
    public void ReflectionProvider_ImplementsTheOwnMemberSeam()
        => Assert.IsAssignableFrom<IXamlOwnMemberProvider>(ReflectionXamlMetadata.Instance);

    [Fact]
    public void OwnMembers_IncludeDeclaredAndAddOwnered_ExcludeInheritedAndReadOnly()
    {
        var own = OwnMembers(typeof(UIControls.TextBlock)).ToHashSet(StringComparer.Ordinal);

        // declared on TextBlock
        Assert.Contains("Text", own);
        Assert.Contains("TextWrapping", own);
        // AddOwner'd onto TextBlock: TextElement.ForegroundProperty.AddOwner<TextBlock>() — the band-2 provenance
        Assert.Contains("Foreground", own);

        // purely inherited from UIElement — never redeclared / AddOwner'd on TextBlock
        Assert.DoesNotContain("Width", own);
        Assert.DoesNotContain("IsEnabled", own);
        // own but READ-ONLY — must not appear in band 2 (or anywhere)
        Assert.DoesNotContain("IsTrimmed", own);

        // Alignment: every own member of TextBlock is also a completable (known) member — the host tags band 2
        // by set-membership, so own must be a subset of the known-member set.
        var known = KnownMembers(typeof(UIControls.TextBlock)).ToHashSet(StringComparer.Ordinal);
        Assert.Subset(known, own);
    }

    [Fact]
    public void OwnMembers_Fixture_Declared_AddOwnered_Inherited_ReadOnly_AttachedDeclaration()
    {
        var own = OwnMembers(typeof(MetadataFixture)).ToHashSet(StringComparer.Ordinal);

        // declared-on-the-fixture settable members (reflection DeclaredOnly)
        Assert.Contains(nameof(MetadataFixture.SettableStyledGetOnly), own);
        Assert.Contains(nameof(MetadataFixture.SettableDirect), own);
        Assert.Contains(nameof(MetadataFixture.PlainSettable), own);
        Assert.Contains(nameof(MetadataFixture.SomethingHappened), own);

        // AddOwner'd onto the fixture with NO CLR wrapper — reflection's DeclaringType cannot see it, so the
        // registry-exact-owner union is LOAD-BEARING here (unlike TextBlock/Control, which redeclare a wrapper).
        Assert.Contains("Foreground", own);
        var declaredWrappers = typeof(MetadataFixture)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("Foreground", declaredWrappers); // proves the union recovered it, not reflection

        // purely inherited from UIElement — excluded
        Assert.DoesNotContain("Width", own);
        Assert.DoesNotContain("IsEnabled", own);
        // own but read-only — excluded (same settable rule as Change 1)
        Assert.DoesNotContain(nameof(MetadataFixture.ReadOnlyStyled), own);
        Assert.DoesNotContain(nameof(MetadataFixture.ReadOnlyDirect), own);
        // the fixture's CHILD-ONLY attached-property DECLARATION (marked) is not an own member of the fixture
        Assert.DoesNotContain("Slot", own);
        // the fixture's SELF-USABLE attached-property DECLARATION (unmarked) IS an own member (marker polarity)
        Assert.Contains(nameof(MetadataFixture.SelfAttached), own);
        // plain read-only / non-public-setter members — excluded
        Assert.DoesNotContain(nameof(MetadataFixture.PlainComputed), own);
        Assert.DoesNotContain(nameof(MetadataFixture.PlainPrivateSetter), own);
    }

    [Fact]
    public void OwnMembers_SelfUsableAttached_Included_ChildOnlyAttached_Excluded_Bug2()
    {
        // Bug 2: a SELF-USABLE attached property (unmarked — set on the declaring type's own instance) IS an
        // own member. ScrollViewer.[H|V]ScrollBarVisibility configure the ScrollViewer itself.
        var sv = OwnMembers(typeof(UIControls.ScrollViewer)).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("HorizontalScrollBarVisibility", sv);
        Assert.Contains("VerticalScrollBarVisibility", sv);

        // ...while a CHILD-ONLY attached property (marked — set on the container's children) is NOT an own
        // member of the container. Grid.Row/Column are set on a Grid's children, never on a Grid as its surface.
        var grid = OwnMembers(typeof(UIControls.Grid)).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("Row", grid);
        Assert.DoesNotContain("Column", grid);
    }

    [Fact]
    public void ProviderWithoutTheSeam_ProbeIsFalse_AndConsumerFallsBackToEmpty()
    {
        // A metadata provider that does not implement the optional seam — the netstandard2.0-safe stand-in for a
        // default-interface-method returning empty. The consumer's capability probe fails and it treats every
        // member as inherited (offers no own-member ranking).
        IXamlTypeMetadataProvider bare = HandBuiltMetadata.Instance;
        Assert.False(bare is IXamlOwnMemberProvider);

        var target = ReflectionXamlMetadata.Instance.GetXamlType(typeof(UIControls.TextBlock)).ClrType;
        var offered = bare is IXamlOwnMemberProvider om ? om.GetOwnMemberNames(target) : Array.Empty<string>();
        Assert.Empty(offered);
    }

    [Fact]
    public void GetOwnMemberNames_NullTarget_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => ((IXamlOwnMemberProvider) ReflectionXamlMetadata.Instance).GetOwnMemberNames(null!));

    // A controlled UIElement subclass exercising every branch of the settable rule and the own-member union.
#pragma warning disable CS0067 // the event is asserted by reflection, never raised
    private sealed class MetadataFixture : UIElement
    {
        // AddOwner an attached property with NO CLR wrapper on the fixture — a registry-only own member the
        // reflection (DeclaringType) side cannot see. (Static ctor makes the field initializers below run first.)
        static MetadataFixture() => UIControls.TextElement.ForegroundProperty.AddOwner<MetadataFixture>();

        // read-write StyledProperty exposed through a GET-ONLY wrapper (the CanWrite-diverging INCLUDE case)
        public static readonly StyledProperty<int> SettableStyledGetOnlyProperty =
            UIProperty.Register<MetadataFixture, int>(nameof(SettableStyledGetOnly));
        public int SettableStyledGetOnly => GetValue(SettableStyledGetOnlyProperty);

        // read-only StyledProperty (RegisterReadOnly), get-only wrapper
        private static readonly UIPropertyKey<int> ReadOnlyStyledKey =
            UIProperty.RegisterReadOnly<MetadataFixture, int>(nameof(ReadOnlyStyled));
        public static readonly StyledProperty<int> ReadOnlyStyledProperty = ReadOnlyStyledKey.Property;
        public int ReadOnlyStyled => GetValue(ReadOnlyStyledProperty);

        // read-write DirectProperty
        private int _direct;
        public static readonly DirectProperty<MetadataFixture, int> SettableDirectProperty =
            UIProperty.RegisterDirect<MetadataFixture, int>(nameof(SettableDirect), o => o._direct, (o, v) => o._direct = v);
        public int SettableDirect { get => _direct; set => _direct = value; }

        // read-only DirectProperty (no setter), get-only wrapper
        private readonly int _readOnlyDirect;
        public static readonly DirectProperty<MetadataFixture, int> ReadOnlyDirectProperty =
            UIProperty.RegisterDirect<MetadataFixture, int>(nameof(ReadOnlyDirect), o => o._readOnlyDirect);
        public int ReadOnlyDirect => _readOnlyDirect;

        // a CHILD-ONLY attached property DECLARED by the fixture (marked targetsChildren) — set on OTHER
        // elements, so NOT a settable member OF the fixture itself (the Grid.Row shape).
        public static readonly AttachedProperty<int> SlotProperty =
            UIProperty.RegisterAttached<MetadataFixture, UIElement, int>("Slot", targetsChildren: true);

        // a SELF-USABLE attached property DECLARED by the fixture (UNMARKED) — describes the fixture's own
        // instance, so it IS an own member (the ScrollViewer.HorizontalScrollBarVisibility shape). A get/set
        // wrapper keeps it in the known-member set too (own ⊆ known).
        public static readonly AttachedProperty<int> SelfAttachedProperty =
            UIProperty.RegisterAttached<MetadataFixture, UIElement, int>("SelfAttached");
        public int SelfAttached { get => GetValue(SelfAttachedProperty); set => SetValue(SelfAttachedProperty, value); }

        // plain CLR members
        public int PlainSettable { get; set; }
        public int PlainComputed => 42;
        public int PlainPrivateSetter { get; private set; }

        public event EventHandler? SomethingHappened;
    }
#pragma warning restore CS0067
}
