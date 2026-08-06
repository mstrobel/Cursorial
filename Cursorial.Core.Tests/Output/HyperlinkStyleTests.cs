using Cursorial.Output;

namespace Cursorial.Tests.Output;

public class HyperlinkStyleTests
{
    [Fact]
    public void Default_Hyperlink_IsEmpty()
    {
        Assert.True(default(Hyperlink).IsEmpty);
        Assert.True(Hyperlink.None.IsEmpty);
    }

    [Fact]
    public void Hyperlink_WithUri_IsNotEmpty()
    {
        var link = new Hyperlink("https://example.com");
        Assert.False(link.IsEmpty);
        Assert.Equal("https://example.com", link.Uri);
        Assert.Null(link.Id);
    }

    [Fact]
    public void Hyperlink_WithEmptyUri_IsEmpty()
    {
        var link = new Hyperlink("");
        Assert.True(link.IsEmpty);
    }

    [Fact]
    public void Hyperlink_ValueEquality_ComparesUriAndId()
    {
        var a = new Hyperlink("https://example.com", "anchor-1");
        var b = new Hyperlink("https://example.com", "anchor-1");
        var c = new Hyperlink("https://example.com", "anchor-2");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Style_WithHyperlink_PropagatesToTheField()
    {
        var styled = CellStyle.Default.WithHyperlink("https://example.com");
        Assert.False(styled.Hyperlink.IsEmpty);
        Assert.Equal("https://example.com", styled.Hyperlink.Uri);
    }

    [Fact]
    public void Style_WithHyperlinkStringOverload_AcceptsId()
    {
        var styled = CellStyle.Default.WithHyperlink("https://example.com", "id-42");
        Assert.Equal("id-42", styled.Hyperlink.Id);
    }

    [Fact]
    public void Style_WithHyperlinkNone_ClearsHyperlink()
    {
        var styled = CellStyle.Default.WithHyperlink("https://example.com").WithHyperlink(Hyperlink.None);
        Assert.True(styled.Hyperlink.IsEmpty);
    }

    [Fact]
    public void Style_IsDefault_FalseWhenHyperlinkSet()
    {
        var styled = CellStyle.Default.WithHyperlink("https://example.com");
        Assert.False(styled.IsDefault);
    }
}
