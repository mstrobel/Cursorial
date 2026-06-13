using System.Collections.ObjectModel;
using Cursorial.UI;
using Cursorial.UI.Data;
using Xunit;

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
        Assert.Equal(AccessorKind.ReflectionIndexer, accessor!.Kind);
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

        var nameAccessor = AccessorCache.ResolveProperty(sub!, in nameSeg);
        Assert.Equal("inner", nameAccessor.GetValue(sub!));
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

    private static PathSegment ParseSingle(string name)
    {
        var path = BindingPath.Parse(name);
        return path.Segments[0];
    }
}
