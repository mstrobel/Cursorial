using Cursorial.Rendering.Text;

namespace Cursorial.Tests.Rendering.Text;

public class GlyphMapsTests
{
    [Fact]
    public void Identity_ReturnsInputUnchanged()
    {
        Assert.Equal("a", GlyphMaps.Identity.Map("a"));
        Assert.Equal("🚀", GlyphMaps.Identity.Map("🚀"));
        Assert.Equal("中", GlyphMaps.Identity.Map("中"));
    }

    [Fact]
    public void Fullwidth_MapsAsciiPrintables()
    {
        Assert.Equal("Ａ", GlyphMaps.Fullwidth.Map("A"));
        Assert.Equal("ｚ", GlyphMaps.Fullwidth.Map("z"));
        Assert.Equal("０", GlyphMaps.Fullwidth.Map("0"));
        Assert.Equal("！", GlyphMaps.Fullwidth.Map("!"));
        Assert.Equal("～", GlyphMaps.Fullwidth.Map("~"));
    }

    [Fact]
    public void Fullwidth_MapsSpaceToIdeographicSpace()
    {
        Assert.Equal("　", GlyphMaps.Fullwidth.Map(" "));
    }

    [Fact]
    public void Fullwidth_PassesThroughNonAscii()
    {
        Assert.Equal("中", GlyphMaps.Fullwidth.Map("中"));
        Assert.Equal("🚀", GlyphMaps.Fullwidth.Map("🚀"));
    }

    [Fact]
    public void DoubleStruck_MapsLatinAndDigits()
    {
        Assert.Equal("𝔸", GlyphMaps.DoubleStruck.Map("A"));
        Assert.Equal("𝕒", GlyphMaps.DoubleStruck.Map("a"));
        Assert.Equal("𝟘", GlyphMaps.DoubleStruck.Map("0"));
    }

    [Fact]
    public void DoubleStruck_UsesLetterlikeSymbolsForReservedLetters()
    {
        // ℂ ℍ ℕ ℙ ℚ ℝ ℤ live in the Letterlike Symbols block (BMP) rather than Plane 1.
        Assert.Equal("ℂ", GlyphMaps.DoubleStruck.Map("C"));
        Assert.Equal("ℍ", GlyphMaps.DoubleStruck.Map("H"));
        Assert.Equal("ℕ", GlyphMaps.DoubleStruck.Map("N"));
        Assert.Equal("ℙ", GlyphMaps.DoubleStruck.Map("P"));
        Assert.Equal("ℚ", GlyphMaps.DoubleStruck.Map("Q"));
        Assert.Equal("ℝ", GlyphMaps.DoubleStruck.Map("R"));
        Assert.Equal("ℤ", GlyphMaps.DoubleStruck.Map("Z"));
    }

    [Fact]
    public void SmallCaps_MapsLowercaseLatin()
    {
        Assert.Equal("ᴀ", GlyphMaps.SmallCaps.Map("a"));
        Assert.Equal("ʙ", GlyphMaps.SmallCaps.Map("b"));
        Assert.Equal("ᴢ", GlyphMaps.SmallCaps.Map("z"));
    }

    [Fact]
    public void SmallCaps_PassesThroughUppercaseAndOthers()
    {
        Assert.Equal("A", GlyphMaps.SmallCaps.Map("A"));
        Assert.Equal("1", GlyphMaps.SmallCaps.Map("1"));
        Assert.Equal("中", GlyphMaps.SmallCaps.Map("中"));
    }

    [Fact]
    public void Superscript_MapsDigitsAndOperators()
    {
        Assert.Equal("⁰", GlyphMaps.Superscript.Map("0"));
        Assert.Equal("¹", GlyphMaps.Superscript.Map("1"));
        Assert.Equal("⁹", GlyphMaps.Superscript.Map("9"));
        Assert.Equal("⁺", GlyphMaps.Superscript.Map("+"));
        Assert.Equal("⁻", GlyphMaps.Superscript.Map("-"));
        Assert.Equal("⁽", GlyphMaps.Superscript.Map("("));
        Assert.Equal("⁾", GlyphMaps.Superscript.Map(")"));
    }

    [Fact]
    public void Superscript_PassesThroughLetters()
    {
        Assert.Equal("a", GlyphMaps.Superscript.Map("a"));
        Assert.Equal("Z", GlyphMaps.Superscript.Map("Z"));
    }

    [Fact]
    public void Subscript_MapsDigitsAndOperators()
    {
        Assert.Equal("₀", GlyphMaps.Subscript.Map("0"));
        Assert.Equal("₉", GlyphMaps.Subscript.Map("9"));
        Assert.Equal("₊", GlyphMaps.Subscript.Map("+"));
        Assert.Equal("₋", GlyphMaps.Subscript.Map("-"));
    }

    [Fact]
    public void From_LookupTable_AppliesSubstitutions()
    {
        var table = new Dictionary<string, string> { ["🅰"] = "A", ["x"] = "✗" };
        var map = GlyphMaps.From(table);

        Assert.Equal("A", map.Map("🅰"));
        Assert.Equal("✗", map.Map("x"));
        // Unknown grapheme passes through.
        Assert.Equal("y", map.Map("y"));
    }

    [Fact]
    public void From_NullTable_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => GlyphMaps.From(null!));
    }
}
