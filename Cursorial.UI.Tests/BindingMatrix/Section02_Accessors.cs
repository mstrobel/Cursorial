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
}
