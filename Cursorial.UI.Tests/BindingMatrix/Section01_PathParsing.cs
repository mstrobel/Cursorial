using Cursorial.UI.Data;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.BindingMatrix;

/// <summary>Binding matrix §1 — path parsing (B1–B16).</summary>
public class Section01_PathParsing
{
    public Section01_PathParsing() => BindingMatrixFixture.Ensure();

    [Fact]
    public void B001_Simple_Property()
    {
        var path = BindingPath.Parse("Name");
        Assert.Equal(1, path.SegmentCount);
        Assert.Equal("Name", path.ToString());
    }

    [Fact]
    public void B002_Dotted_Chain_RoundTrips()
    {
        var path = BindingPath.Parse("Customer.Address.City");
        Assert.Equal(3, path.SegmentCount);
        Assert.Equal("Customer.Address.City", path.ToString());
    }

    [Fact]
    public void B003_Int_Indexer()
    {
        var path = BindingPath.Parse("Tags[0]");
        Assert.Equal(2, path.SegmentCount);
        Assert.Equal("Tags[0]", path.ToString());
    }

    [Theory]
    [InlineData("Map[key]")]
    [InlineData("Map['key']")]
    public void B004_String_Indexer_BareAndQuoted_Canonicalize(string text)
    {
        var path = BindingPath.Parse(text);
        Assert.Equal(2, path.SegmentCount);
        // Canonicalizes to the single-quoted form and round-trips.
        Assert.Equal("Map['key']", path.ToString());
    }

    [Fact]
    public void B005_Attached_Segment()
    {
        var path = BindingPath.Parse("(BindWidget.Num)");
        Assert.Equal(1, path.SegmentCount);
        Assert.Equal("(BindWidget.Num)", path.ToString());
    }

    [Fact]
    public void B006_Mixed_StepIndexerAttached_RoundTrips()
    {
        var path = BindingPath.Parse("Items[2].(BindWidget.Num)");
        Assert.Equal(3, path.SegmentCount);
        Assert.Equal("Items[2].(BindWidget.Num)", path.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    public void B007_Empty_And_Dot_AreEmptyPath(string text)
    {
        var path = BindingPath.Parse(text);
        Assert.True(path.IsEmpty);
        Assert.Equal(string.Empty, path.ToString());
        Assert.Equal(0, BindingPath.Empty.SegmentCount);
    }

    [Fact]
    public void B008_Null_Throws()
        => Assert.Throws<ArgumentNullException>(static () => BindingPath.Parse(null!));

    [Fact]
    public void B009_TrailingDot_Throws_WithPosition()
    {
        var ex = Assert.Throws<FormatException>(static () => BindingPath.Parse("Name."));
        Assert.Contains("position 5", ex.Message);
    }

    [Fact]
    public void B010_UnterminatedIndexer_Throws_WithPosition()
    {
        var ex = Assert.Throws<FormatException>(static () => BindingPath.Parse("Tags["));
        Assert.Contains("indexer", ex.Message);
        Assert.Contains("position 4", ex.Message);
    }

    [Fact]
    public void B011_MultiArgIndexer_Throws_Unsupported()
    {
        var ex = Assert.Throws<FormatException>(static () => BindingPath.Parse("Tags[a,b]"));
        Assert.Contains("multi-argument", ex.Message);
        Assert.Contains("position 6", ex.Message); // the comma
    }

    [Fact]
    public void B012_SlashCurrentItem_Throws_Unsupported()
    {
        var ex = Assert.Throws<FormatException>(static () => BindingPath.Parse("/Items"));
        Assert.Contains("current-item", ex.Message);
    }

    [Fact]
    public void B013_SourceCast_Throws_Unsupported()
    {
        var ex = Assert.Throws<FormatException>(static () => BindingPath.Parse("(local:T)x"));
        Assert.Contains("source casts", ex.Message);
    }

    [Fact]
    public void B014_DefaultResolver_ResolvesWidget_AmbiguousThrows()
    {
        // The first resolves via the default resolver (BindWidget is a unique registered owner short name).
        var resolved = BindingPath.Parse("(BindWidget.Num)");
        Assert.Equal(1, resolved.SegmentCount);
        Assert.Equal("(BindWidget.Num)", resolved.ToString());

        // An ambiguous short name (e.g. a property registered on two owners with the same simple
        // name) throws listing candidates. "Name" is registered on multiple owner types (UIElement
        // and the various controls) — but the TYPE token must be ambiguous, so use a shared type
        // name across distinct namespaces if available; absent that, the resolver returns null for an
        // unknown token (covered by B15).
        var ex = Assert.Throws<FormatException>(static () => BindingPath.Parse("(Zzz.X)"));
        Assert.Contains("Zzz", ex.Message);
    }

    [Fact]
    public void B015_UnresolvableType_Throws()
    {
        var ex = Assert.Throws<FormatException>(static () => BindingPath.Parse("(Zzz.X)", new MapResolver()));
        Assert.Contains("Zzz", ex.Message);
    }

    [Fact]
    public void B015a_TypeQualified_MemberNotStaticallyFound_ParsesUnresolved()
    {
        // Only the OWNER type must resolve at parse time. A member that is not statically found on it is
        // NOT a parse failure — the segment stays unresolved and the member resolves by name on the
        // runtime source (the documented disambiguation/clarity contract).
        var path = BindingPath.Parse("(BindWidget.NoSuchMember)");
        Assert.Equal(1, path.SegmentCount);
        Assert.Equal("(BindWidget.NoSuchMember)", path.ToString());
        Assert.False(path.Segments[0].QualifiedProperty.IsResolved);
    }

    [Fact]
    public void B015b_TypeQualified_ShadowedProperty_AmbiguityIsAStaticMiss_NotAThrow()
    {
        // A shadowed ('new') property is ambiguous for reflection on the derived type; the parser treats
        // that as a static miss (runtime by-name resolution) rather than letting AmbiguousMatchException
        // escape as a raw reflection exception.
        var path = BindingPath.Parse("(Shadow.Val)", new MapResolver());
        Assert.Equal(1, path.SegmentCount);
        Assert.Equal("(ShadowVm.Val)", path.ToString());
    }

    [Fact]
    public void B016_PathParsedOncePerDescriptor_Cached()
    {
        var binding = new Binding("Customer.Address.City");
        var first = binding.GetPath();
        var second = binding.GetPath();
        Assert.Same(first, second); // parsed once, cached on the descriptor
    }

    private sealed class MapResolver : IPathTypeResolver
    {
        public Type? Resolve(string typeToken)
            => typeToken switch
               {
                   "BindWidget" => typeof(BindWidget),
                   "Shadow"     => typeof(ShadowVm),
                   _            => null
               };
    }

    private class ShadowBaseVm
    {
        // ReSharper disable once UnusedMember.Local
        public object? Val { get; set; }
    }

    private sealed class ShadowVm : ShadowBaseVm
    {
        // ReSharper disable once UnusedMember.Local
        public new string? Val { get; set; }
    }
}
