using Cursorial.UI;

// ReSharper disable EqualExpressionComparison

namespace Cursorial.Tests.UI;

/// <summary>
/// <see cref="StyleSortKey"/> is an opaque comparable for the store — these tests pin the ordering
/// contract Fork B's packing relies on (larger packed value sorts stronger).
/// </summary>
public class StyleSortKeyTests
{
    [Fact]
    public void Ordering_IsUnsignedNumericOnPackedBits()
    {
        var weak = new StyleSortKey(1);
        var strong = new StyleSortKey(ulong.MaxValue); // top bit set — unsigned comparison required

        Assert.True(weak < strong);
        Assert.True(strong > weak);
        Assert.True(weak <= strong);
        Assert.True(strong >= weak);
        Assert.True(weak.CompareTo(strong) < 0);
        Assert.True(strong.CompareTo(weak) > 0);
    }

    [Fact]
    public void Equality_IsStructural()
    {
        Assert.Equal(new StyleSortKey(42), new StyleSortKey(42));
        Assert.NotEqual(new StyleSortKey(42), new StyleSortKey(43));
        Assert.Equal(0, new StyleSortKey(42).CompareTo(new StyleSortKey(42)));
        Assert.True(new StyleSortKey(42) <= new StyleSortKey(42));
        Assert.True(new StyleSortKey(42) >= new StyleSortKey(42));
    }

    [Fact]
    public void DefaultKey_IsTheWeakest()
    {
        Assert.True(default(StyleSortKey) <= new StyleSortKey(0));
        Assert.True(default(StyleSortKey) < new StyleSortKey(1));
    }
}
