using Cursorial.UI;
using Cursorial.UI.Data;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.BindingMatrix;

/// <summary>Binding matrix §2 — accessor resolution &amp; the UIObject hop (B17–B24).</summary>
public class Section02_Accessors
{
    public Section02_Accessors()
    {
        BindingMatrixFixture.Ensure();
        AccessorCache.ResetForTests();
    }

    [Fact]
    public void B017_UIObjectHop_ResolvesUIPropertyAccessor_NoReflection()
    {
        var w0 = new BindWidget();
        var segment = ParseSingle("Text");
        var accessor = AccessorCache.ResolveProperty(w0, in segment);

        Assert.IsType<UIPropertyAccessor>(accessor);
        Assert.Equal(AccessorKind.UIProperty, accessor.Kind);

        w0.Text = "hi";
        Assert.Equal("hi", accessor.GetValue(w0));
    }

    [Fact]
    public void B018_AccessorCache_COW_SecondResolveIsCacheHit()
    {
        var w0 = new BindWidget();
        var segment = ParseSingle("Text");

        var before = AccessorCache.ResolveCount;
        var first = AccessorCache.ResolveProperty(w0, in segment);
        var afterFirst = AccessorCache.ResolveCount;
        var second = AccessorCache.ResolveProperty(w0, in segment);
        var afterSecond = AccessorCache.ResolveCount;

        Assert.Same(first, second);
        Assert.Equal(before + 1, afterFirst);
        Assert.Equal(afterFirst, afterSecond); // the second resolve is a cache hit
    }

    [Fact]
    public void B019_ClrProperty_ResolvesClrAccessor()
    {
        var vm = new Vm();
        var segment = ParseSingle("Name");
        var accessor = AccessorCache.ResolveProperty(vm, in segment);

        Assert.True(accessor.Kind is AccessorKind.ClrDelegate or AccessorKind.ClrReflection);
        vm.Name = "x";
        Assert.Equal("x", accessor.GetValue(vm));
    }

    [Fact]
    public void B020_ObservableCollection_IntIndexer_FastPath()
    {
        var vm = new Vm();
        vm.Tags.Add("x");
        var accessor = AccessorCache.ResolveIntIndexer(vm.Tags, 0);

        Assert.IsType<ListIndexerAccessor>(accessor);
        Assert.Equal(AccessorKind.ListIndexer, accessor.Kind);
        Assert.Equal("x", accessor.GetValue(vm.Tags));
    }

    [Fact]
    public void B021_Dictionary_StringIndexer_Reflection()
    {
        var vm = new Vm();
        vm.Map["key"] = "v";
        var accessor = AccessorCache.ResolveStringIndexer(vm.Map, "key");

        Assert.NotNull(accessor);
        Assert.Equal(AccessorKind.ReflectionIndexer, accessor.Kind);
        Assert.Equal("v", accessor.GetValue(vm.Map));
    }

    [Fact]
    public void B022_MixedChain_EachHopResolvesIndependently()
    {
        var vm = new Vm { Sub = new Vm { Name = "inner" } };
        var subSeg = ParseSingle("Sub");
        var nameSeg = ParseSingle("Name");

        var subAccessor = AccessorCache.ResolveProperty(vm, in subSeg);
        var sub = subAccessor.GetValue(vm);
        Assert.NotNull(sub);

        var nameAccessor = AccessorCache.ResolveProperty(sub, in nameSeg);
        Assert.Equal("inner", nameAccessor.GetValue(sub));
    }

    [Fact]
    public void B023_UIObjectWithSameNameClrProperty_PrefersRegisteredUIProperty()
    {
        // Resolution rule 1 is keyed on (runtime type, name) against the registry, not "is a UIObject".
        var w0 = new BindWidget();
        var segment = ParseSingle("Text");
        var accessor = AccessorCache.ResolveProperty(w0, in segment);
        Assert.IsType<UIPropertyAccessor>(accessor);

        // A non-registered name on a UIObject falls through to CLR reflection (rule 2). "Tag" is not
        // a registered UIProperty on BindWidget but UIElement exposes no such CLR member either, so it is
        // unresolvable — proving the registry match is by name, not "is a UIObject".
        var unknownSeg = ParseSingle("NoSuchMember");
        var unknown = AccessorCache.ResolveProperty(w0, in unknownSeg);
        Assert.IsType<UnresolvableAccessor>(unknown);
    }

    [Fact]
    public void B024_ClrHop_ReadsValue()
    {
        // The compiled-delegate vs raw-PropertyInfo choice is a RuntimeFeature.IsDynamicCodeSupported
        // decision; the row documents that values read either way (the compiled lane is the real AOT
        // answer).
        var vm = new Vm { Age = 7 };
        var segment = ParseSingle("Age");
        var accessor = AccessorCache.ResolveProperty(vm, in segment);
        Assert.Equal(7, accessor.GetValue(vm));
    }

    [Fact]
    public void B024a_EnumIndexer_OnGenericDictionary_CoercesTokenToEnumKey()
    {
        // Dictionary<TEnum, T> is IDictionary but exposes only Item[TEnum]; the bare token coerces to
        // the enum key rather than silently missing as a string lookup (design doc §6.3).
        var map = new Dictionary<Status, string>
        {
            [Status.Active] = "on",
            [Status.Closed] = "off"
        };

        var bare = AccessorCache.ResolveStringIndexer(map, BindingPath.Parse("[Active]").Segments[0].Name!);
        Assert.NotNull(bare);
        Assert.Equal(AccessorKind.ReflectionIndexer, bare.Kind);
        Assert.Equal("on", bare.GetValue(map));

        // Qualified form [Status.Closed] strips to the member name.
        var qualified = AccessorCache.ResolveStringIndexer(map, BindingPath.Parse("[Status.Closed]").Segments[0].Name!);
        Assert.Equal("off", qualified!.GetValue(map));

        // An unknown token isn't an enum member, so it degrades to the pre-existing IDictionary
        // string fallback (an accessor that misses at runtime) — not the enum-keyed indexer.
        var bogus = AccessorCache.ResolveStringIndexer(map, "Bogus");
        Assert.Null(bogus!.GetValue(map));
    }

    [Fact]
    public void B024b_EnumIndexer_OnPlainClass_ResolvesRoundTrips_AndUnknownStaysUnresolved()
    {
        var box = new EnumIndexed();
        var accessor = AccessorCache.ResolveStringIndexer(box, BindingPath.Parse("[Active]").Segments[0].Name!);

        Assert.NotNull(accessor);
        Assert.Equal(AccessorKind.ReflectionIndexer, accessor.Kind);
        accessor.SetValue(box, "live");          // two-way write-back through the enum key
        Assert.Equal("live", accessor.GetValue(box));
        Assert.Equal("live", box[Status.Active]);

        // No IDictionary fallback on a plain class: an unknown enum member resolves nothing, so the
        // path stays unresolved rather than binding to a wrong key.
        Assert.Null(AccessorCache.ResolveStringIndexer(box, "Bogus"));
    }

    [Fact]
    public void B024c_TypeQualifiedClr_MismatchedRuntimeInstance_FallsBackToByName()
    {
        // The parse-time PropertyInfo on the qualifier type must not be invoked on an incompatible
        // runtime instance (that would throw TargetException out of the binding rewire — e.g. a
        // transient inherited DataContext of another type). The member re-resolves BY NAME on the
        // runtime type: a same-named property binds, and a missing one degrades to the graceful
        // unresolved path.
        var segment = BindingPath.Parse("(ItemVm.Label)", new TypeMapResolver()).Segments[0];
        Assert.Equal(PathSegmentKind.TypeQualified, segment.Kind);
        Assert.True(segment.QualifiedProperty.IsClrProperty); // statically resolved on the qualifier

        var matched = new ItemVm { Label = "item" };
        Assert.Equal("item", AccessorCache.ResolveProperty(matched, in segment).GetValue(matched));

        var sameName = new OtherVm { Label = "other" };
        Assert.Equal("other", AccessorCache.ResolveProperty(sameName, in segment).GetValue(sameName));

        var noSuchMember = new PlainHolder();
        var unresolved = AccessorCache.ResolveProperty(noSuchMember, in segment);
        Assert.IsType<UnresolvableAccessor>(unresolved);
        Assert.Same(UIProperty.UnsetValue, unresolved.GetValue(noSuchMember));
    }

    [Fact]
    public void B024d_TypeQualified_InterfaceInheritedMember_ResolvesByNameOnRuntimeType()
    {
        // Reflection does not surface base-interface members on a derived interface type, so the parse
        // leaves the member unresolved (not a parse failure); the accessor then resolves it by name on
        // the concrete runtime type.
        var segment = BindingPath.Parse("(IDerived.Tag)", new TypeMapResolver()).Segments[0];
        Assert.False(segment.QualifiedProperty.IsResolved);

        var impl = new Impl { Tag = "t" };
        Assert.Equal("t", AccessorCache.ResolveProperty(impl, in segment).GetValue(impl));
    }

    [Fact]
    public void B024e_QualifierShortNameCollision_DistinctCacheEntries()
    {
        // Two qualifier types sharing a SHORT name (different outer types / namespaces) must not serve
        // each other's cached accessor for the same instance type: the cache key uses the qualifier's
        // full name, so each resolves its own entry.
        var instance = new Vm { Name = "n" };
        var segA = PathSegment.TypeQualified(typeof(OuterA.Q), "Name", ResolvedProperty.Unresolved);
        var segB = PathSegment.TypeQualified(typeof(OuterB.Q), "Name", ResolvedProperty.Unresolved);

        var before = AccessorCache.ResolveCount;
        Assert.Equal("n", AccessorCache.ResolveProperty(instance, in segA).GetValue(instance));
        Assert.Equal("n", AccessorCache.ResolveProperty(instance, in segB).GetValue(instance));
        Assert.Equal(before + 2, AccessorCache.ResolveCount); // two entries, not a short-name cache hit
    }

    private enum Status { Active, Closed }

    private sealed class EnumIndexed
    {
        private readonly Dictionary<Status, string?> _store = new();
        // ReSharper disable once UnusedMember.Local
        public string? this[Status key] { get => _store.GetValueOrDefault(key); set => _store[key] = value; }
    }

    private static PathSegment ParseSingle(string name)
    {
        var path = BindingPath.Parse(name);
        return path.Segments[0];
    }

    private sealed class TypeMapResolver : IPathTypeResolver
    {
        public Type? Resolve(string typeToken)
            => typeToken switch
               {
                   "ItemVm"   => typeof(ItemVm),
                   "IDerived" => typeof(IDerived),
                   _          => null
               };
    }

    private sealed class ItemVm
    {
        public string? Label { get; set; }
    }

    private sealed class OtherVm
    {
        public string? Label { get; set; }
    }

    private interface IBase
    {
        string? Tag { get; }
    }

    private interface IDerived : IBase;

    private sealed class Impl : IDerived
    {
        public string? Tag { get; set; }
    }

    private static class OuterA
    {
        internal sealed class Q;
    }

    private static class OuterB
    {
        internal sealed class Q;
    }
}
