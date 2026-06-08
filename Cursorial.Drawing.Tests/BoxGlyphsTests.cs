using Cursorial.Drawing;

namespace Cursorial.Tests.Drawing;

/// <summary>
/// Pins the box-glyph table's hard cases — the mixed light/heavy/double junctions that can't be
/// eyeballed and the fallback ladder — to their Unicode-oracle values, so a future edit to
/// <see cref="BoxGlyphs"/> that transposes a codepoint is caught (honoring the type's doc-comment).
/// </summary>
public class BoxGlyphsTests
{
    private static string Resolve(int armCode, GlyphSet set = GlyphSet.Unicode) =>
        BoxGlyphs.Resolve((byte) armCode, default, set);

    [Theory]
    // The coin-flip pair (down light/right heavy vs down heavy/right light):
    [InlineData(0x18, "┍")]
    [InlineData(0x24, "┎")]
    // mixed light/heavy tees and crosses:
    [InlineData(0x16, "┞")]
    [InlineData(0x1A, "┡")]
    [InlineData(0x25, "┟")]
    [InlineData(0x29, "┢")]
    [InlineData(0x66, "╂")]
    [InlineData(0x99, "┿")]
    [InlineData(0x5A, "╄")]
    [InlineData(0x96, "╃")]
    // light/double mixes (named "SINGLE … DOUBLE" in Unicode — the parser gotcha):
    [InlineData(0x1C, "╒")]
    [InlineData(0x37, "╟")]
    [InlineData(0xDD, "╪")]
    // pure crosses across the three weights:
    [InlineData(0x55, "┼")]
    [InlineData(0xAA, "╋")]
    [InlineData(0xFF, "╬")]
    public void Resolve_MixedJunctions_MatchOracle(int armCode, string expected) =>
        Assert.Equal(expected, Resolve(armCode));

    [Fact]
    public void Resolve_HeavyPlusDouble_HasNoGlyph_DowngradesToHeavy()
    {
        // left heavy + right heavy + down double → no Unicode glyph → double→heavy → ┳.
        byte code = (byte) (StrokeAccumulator.ArmBits(Arm.Left, StrokeWeight.Heavy)
                          | StrokeAccumulator.ArmBits(Arm.Right, StrokeWeight.Heavy)
                          | StrokeAccumulator.ArmBits(Arm.Down, StrokeWeight.Double));
        Assert.Equal("┳", BoxGlyphs.Resolve(code, default, GlyphSet.Unicode));
    }

    [Theory]
    [InlineData(0x04, "─")]   // lone right light → full horizontal line (no cap)
    [InlineData(0x40, "─")]   // lone left light
    [InlineData(0x01, "│")]   // lone up light → full vertical line
    [InlineData(0x03, "║")]   // lone up double → double vertical
    public void Resolve_LoneArm_NoCap_IsFullLine(int armCode, string expected) =>
        Assert.Equal(expected, Resolve(armCode));

    [Theory]
    [InlineData(0x04, "╶")]   // lone right light + stub
    [InlineData(0x40, "╴")]   // lone left light + stub
    [InlineData(0x02, "╹")]   // lone up heavy + stub
    public void Resolve_LoneArm_WithStub_IsHalfStub(int armCode, string expected) =>
        Assert.Equal(expected, BoxGlyphs.Resolve((byte) armCode,
            new StrokeDecoration(CornerStyle.Sharp, LineDash.None, EndCap.Stub), GlyphSet.Unicode));

    [Fact]
    public void Resolve_RoundedOnlyAppliesToLightCorners()
    {
        var rounded = new StrokeDecoration(CornerStyle.Rounded, LineDash.None, EndCap.None);
        Assert.Equal("╭", BoxGlyphs.Resolve(0x14, rounded, GlyphSet.Unicode));   // light down+right → arc
        Assert.Equal("┏", BoxGlyphs.Resolve(0x28, rounded, GlyphSet.Unicode));   // heavy corner → keeps weight, sharp
    }

    [Fact]
    public void Resolve_DashOnlyOnLightOrHeavyRuns()
    {
        var triple = new StrokeDecoration(CornerStyle.Sharp, LineDash.Triple, EndCap.None);
        Assert.Equal("┄", BoxGlyphs.Resolve(0x44, triple, GlyphSet.Unicode));   // light run → dashed
        Assert.Equal("═", BoxGlyphs.Resolve(0xCC, triple, GlyphSet.Unicode));   // double run → no dash glyph → solid
    }

    [Theory]
    [InlineData(0x55, "+")]   // cross
    [InlineData(0x44, "-")]   // horizontal
    [InlineData(0x11, "|")]   // vertical
    public void Resolve_AsciiGlyphSet(int armCode, string expected) =>
        Assert.Equal(expected, Resolve(armCode, GlyphSet.Ascii));

    [Fact]
    public void MergeArms_IsPerDirectionMax_NotOr()
    {
        // light-right merged with heavy-right → heavy (2), NOT light|heavy = double (3).
        byte merged = StrokeAccumulator.MergeArms(
            StrokeAccumulator.ArmBits(Arm.Right, StrokeWeight.Light),
            StrokeAccumulator.ArmBits(Arm.Right, StrokeWeight.Heavy));
        Assert.Equal(2, (merged >> 2) & 3);
    }

    [Fact]
    public void ArmBits_PacksWeightPlusOneAtTheDirectionField()
    {
        Assert.Equal(0x01, StrokeAccumulator.ArmBits(Arm.Up, StrokeWeight.Light));
        Assert.Equal(0xC0, StrokeAccumulator.ArmBits(Arm.Left, StrokeWeight.Double));
    }
}
