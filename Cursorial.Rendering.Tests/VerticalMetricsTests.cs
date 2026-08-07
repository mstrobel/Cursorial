using System.Text;

using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Text;

namespace Cursorial.Tests.Rendering;

/// <summary>
/// Vertical metrics: <see cref="IGlyphFont.Baseline"/> / <see cref="IGlyphFont.Ascender"/> /
/// <see cref="IGlyphFont.Descender"/>, parsed from the FLF header's second field and forwarded
/// (NOT blindly) through the decorators.
/// </summary>
/// <remarks>
/// <b>The trap these tests exist to catch.</b> FLF <c>Baseline</c> is a COUNT of rows from the top
/// of the glyph box THROUGH the baseline row — not a 0-based row index. Treating the 5 in
/// <c>standard.flf</c>'s <c>flf2a$ 6 5 …</c> header as an index is an off-by-one that renders
/// perfectly on <c>ansi-shadow</c> (<c>7 7</c>, where the two readings coincide) and one row wrong
/// on every other bundled face — so every assertion here that could tell the readings apart is
/// made on a face where <c>Height != Baseline</c>.
/// </remarks>
public class VerticalMetricsTests
{
    // ---- The FLF header field ----

    /// <summary>(face, height, baseline) straight from each bundled <c>.flf</c>'s header line.</summary>
    public static TheoryData<string, int, int> BundledGeometry => new()
    {
        { "Standard", 6, 5 },        // flf2a$ 6 5 16 15 13 0 24463 230
        { "Slant", 6, 5 },           // flf2a$ 6 5 16 15 10 0 18319
        { "Small", 5, 4 },           // flf2a$ 5 4 13 15 10 0 22415 97
        { "SmallSlant", 5, 4 },      // flf2a$ 5 4 14 15 10 0 22415
        { "Big", 8, 6 },             // flf2a$ 8 6 59 15 10 0 24463 154
        { "Mini", 4, 3 },            // flf2a$ 4 3 10 0 10 0 1920 97
        { "AnsiShadow", 7, 7 },      // flf2a$ 7 7 13 0 7 0 64 1  — the ONLY zero-descent face
        { "MiniWi", 4, 3 },          // flf2a$ 4 3 3 -1 5 0 1 0  — header corrected: it shipped
                                     // claiming 4 (no descent), but its glyph bodies bottom out on
                                     // row index 2 with row 3 empty, i.e. a baseline COUNT of 3.
        { "CGA", 2, 2 },             // flf2a$ 2 2 7 -1 6 0 0 163
        { "LCDMatrix", 2, 2 },       // flf2a$ 2 2 7 -1 7 0 0 3
        { "Hp2640LargeType", 3, 3 }, // flf2a$ 3 3 20 -1 24 0 0 2
        { "Roman", 10, 10 },         // flf2a$ 10 10 30 -1 7
    };

    private static FigletFont Face(string name)
        => (FigletFont) typeof(FigletFonts).GetProperty(name)!.GetValue(null)!;

    [Theory]
    [MemberData(nameof(BundledGeometry))]
    public void BundledFace_ParsesItsHeaderBaseline(string name, int height, int baseline)
    {
        var face = Face(name);

        Assert.Equal(height, face.Height);
        Assert.Equal(baseline, face.Baseline);
        Assert.Equal(baseline, face.Ascender);
        Assert.Equal(height - baseline, face.Descender);
        Assert.Equal(height, face.Ascender + face.Descender); // the invariant the three names share
    }

    [Fact]
    public void Baseline_IsARowCount_NotARowIndex()
    {
        // THE OFF-BY-ONE, pinned on a face where the two readings differ. standard.flf is 6 rows
        // with 5 rows of body: the baseline is the FIFTH row, whose 0-based index is 4, and one
        // descender row (index 5) hangs beneath it.
        var standard = Face("Standard");

        Assert.Equal(6, standard.Height);
        Assert.Equal(5, standard.Baseline);              // a COUNT …
        Assert.Equal(4, standard.Baseline - 1);          // … whose row INDEX is one less
        Assert.NotEqual(standard.Height, standard.Baseline);
        Assert.Equal(1, standard.Descender);

        // And the count really does describe the ink: 'g' (a descender letter) puts ink on the
        // row below the baseline, while 'A' (no descender) stops at the baseline row.
        Assert.True(HasInk(standard, 'g', standard.Baseline), "'g' should reach into the descender row");
        Assert.False(HasInk(standard, 'A', standard.Baseline), "'A' should not reach into the descender row");
        Assert.True(HasInk(standard, 'A', standard.Baseline - 1), "'A' should rest ON the baseline row");
    }

    [Fact]
    public void ZeroDescentFace_IsTheOneWhereBothReadingsAgree()
    {
        // ansi-shadow's `7 7` is why the index reading survived: for THIS face alone, "baseline
        // row index" and "bottom row index" are the same number. Nothing may be pinned on it.
        var ansi = Face("AnsiShadow");

        Assert.Equal(ansi.Height, ansi.Baseline);
        Assert.Equal(0, ansi.Descender);
    }

    private static bool HasInk(FigletFont face, char ch, int row)
    {
        var size = face.Measure(ch.ToString());
        var buffer = new CellBuffer(Math.Max(1, size.Columns), face.Height);
        face.Paint(buffer, 0, 0, ch.ToString(), default(PartialStyle));

        for (int c = 0; c < buffer.Columns; c++)
            if (buffer[c, row].Grapheme is { Length: > 0 } g && g.Trim().Length > 0)
                return true;

        return false;
    }

    // ---- Malformed headers: clamp, don't throw ----

    private static FigletFont ParseFont(string header, params string[] body)
    {
        var text = string.Join("\n", body.Prepend(header));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return FigletFontParser.Load(stream, "malformed");
    }

    // height 3, then two 3-line glyph bodies (space and '!') — enough to construct a font.
    private static readonly string[] TinyBody = ["  @", "  @", "  @@", " a@", " b@", " c@@"];

    [Theory]
    [InlineData("flf2a$ 3 9 5 0 0", 3)]   // baseline past the bottom → the bottom row
    [InlineData("flf2a$ 3 0 5 0 0", 1)]   // baseline above the top → the top row
    [InlineData("flf2a$ 3 -2 5 0 0", 1)]  // negative
    [InlineData("flf2a$ 3 x 5 0 0", 3)]   // unparseable → "no descent", the pre-metrics behavior
    [InlineData("flf2a$ 3", 3)]           // field absent entirely
    [InlineData("flf2a$ 3 2 5 0 0", 2)]   // in range: kept verbatim
    public void MalformedBaseline_IsClampedIntoTheSpecRange_NotThrown(string header, int expected)
    {
        // A baseline outside [1, Height] is a COSMETIC defect — the glyph bodies still paint
        // perfectly — so a third-party .flf with a typo stays loadable. Same judgement the
        // constructor already makes for a font missing its mandatory space glyph.
        var font = ParseFont(header, TinyBody);

        Assert.Equal(3, font.Height);
        Assert.Equal(expected, font.Baseline);
        Assert.InRange(font.Baseline, 1, font.Height);
        Assert.Equal(font.Height - font.Baseline, font.Descender);
    }

    [Fact]
    public void ConstructedFace_WithoutABaseline_HasNoDescent()
    {
        // The hand-built overload's default: glyphs rest on their bottom row, which is what every
        // caller of the pre-metrics constructor implicitly assumed.
        var font = new FigletFont("plain", '$', 4, FigletLayoutMode.None,
                                  new Dictionary<uint, FigletGlyph> { [' '] = new(' ', ["  ", "  ", "  ", "  "], '$') });

        Assert.Equal(4, font.Baseline);
        Assert.Equal(0, font.Descender);
    }

    // ---- The interface's defaults ----

    /// <summary>An external face written before vertical metrics existed.</summary>
    private sealed class LegacyExternalFace : IGlyphFont
    {
        public string DisplayName => "Legacy";
        public CellStyle EnsureCompatibleStyle(in CellStyle style) => style;
        public Size Measure(ReadOnlySpan<char> text) => new(text.Length, 1);

        public Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, in PartialStyle style)
            => Measure(text);
    }

    [Fact]
    public void InterfaceDefaults_DescribeASingleRowFace_AndKeepExternalImplementorsCompiling()
    {
        IGlyphFont legacy = new LegacyExternalFace();

        Assert.Equal(1, legacy.Baseline);   // the one row IS the baseline row
        Assert.Equal(1, legacy.Ascender);
        Assert.Equal(0, legacy.Descender);  // nowhere below it to hang a descender
    }

    [Fact]
    public void Monospace_StatesTheMetricsOnTheConcreteType()
    {
        // A default interface member is NOT callable through the concrete type — the same reason
        // MonospaceFont.HasGlyph is explicit. If these were inherited, this wouldn't compile.
        Assert.Equal(1, MonospaceFont.Default.Baseline);
        Assert.Equal(1, MonospaceFont.Default.Ascender);
        Assert.Equal(0, MonospaceFont.Default.Descender);
    }

    // ---- Decorators: NOT a pure forward ----

    /// <summary>A 6-row face with a real descender — standard.flf's geometry, hand-built.</summary>
    private static FigletFont DescenderFace() =>
        new("descender", '$', 6, FigletLayoutMode.None,
            new Dictionary<uint, FigletGlyph>
            {
                [' '] = new(' ', ["  ", "  ", "  ", "  ", "  ", "  "], '$'),
                ['A'] = new('A', ["AA", "AA", "AA", "AA", "AA", "  "], '$'),
            },
            baseline: 5);

    [Fact]
    public void ShadowedFont_KeepsTheInnerBaseline_AndGrowsOnlyTheDescent()
    {
        // Evidence from ShadowedFont.PaintCore: the shadow goes down at `row + Offset.Rows`, the
        // FOREGROUND glyph at `row` — the caller's anchor. The glyph doesn't move, so neither does
        // the baseline; the extra row Measure reports lands underneath, i.e. in the descent.
        var inner = DescenderFace();
        var shadowed = new ShadowedFont(inner);

        Assert.Equal(inner.Height + 1, shadowed.Measure("A").Rows);
        Assert.Equal(inner.Baseline, shadowed.Baseline);            // 5, unchanged
        Assert.Equal(inner.Descender + 1, shadowed.Descender);      // 1 + 1 shadow row
        Assert.Equal(shadowed.Measure("A").Rows, shadowed.Ascender + shadowed.Descender);
    }

    [Fact]
    public void ShadowedFont_WithATallerOffset_GrowsTheDescentByTheWholeOffset()
    {
        var inner = DescenderFace();
        var shadowed = new ShadowedFont(inner, (2, 3));

        Assert.Equal(inner.Baseline, shadowed.Baseline);
        Assert.Equal(inner.Descender + 3, shadowed.Descender);
        Assert.Equal(shadowed.Measure("A").Rows, shadowed.Ascender + shadowed.Descender);
    }

    [Fact]
    public void DecoratedFont_Above_ShiftsTheBaselineDown_Below_DeepensTheDescent()
    {
        // Evidence from DecoratedFont.Paint: `Inner.Paint(buffer, column, above ? row + 1 : row, …)`.
        // Above → the decoration takes the anchor row and every glyph row (baseline included)
        // slides down one. Below → the glyph stays put and the extra row is appended underneath.
        var inner = DescenderFace();

        var above = new DecoratedFont("above", inner, new Rune('▔'), DecorationPosition.Above);
        var below = new DecoratedFont("below", inner, new Rune('▔'), DecorationPosition.Below);

        Assert.Equal(inner.Baseline + 1, above.Baseline);   // 6
        Assert.Equal(inner.Descender, above.Descender);     // 1 — unchanged
        Assert.Equal(above.Measure("A").Rows, above.Ascender + above.Descender);

        Assert.Equal(inner.Baseline, below.Baseline);       // 5 — unchanged
        Assert.Equal(inner.Descender + 1, below.Descender); // 2
        Assert.Equal(below.Measure("A").Rows, below.Ascender + below.Descender);
    }

    [Fact]
    public void DecoratedFont_Above_ShiftsTheBASELINE_VerifiedAgainstInk_NotJustArithmetic()
    {
        // The other decorator assertions are metrics-vs-metrics: they would all still pass if
        // Paint and the metrics agreed on a WRONG anchor. This one paints. Inner 'A' inks rows
        // 0-4 of a 6-row box; Above puts the decoration on the anchor row and the glyph at
        // `row + 1`, so the glyph's last ink row — its baseline row — is index 5, which is
        // exactly `above.Baseline - 1` (6 - 1). A blind `Inner.Baseline` forward reports 5 and
        // claims row index 4, where this face has a glyph row, not its baseline.
        var above = new DecoratedFont("above", DescenderFace(), new Rune('▔'), DecorationPosition.Above);
        var ink = InkRows(above, "A");

        Assert.Equal(6, above.Baseline);
        Assert.Equal(0, ink[0]);                        // the decoration, on the anchor row
        Assert.Equal(above.Baseline - 1, ink[^1]);      // the glyph's baseline row: index 5
        Assert.DoesNotContain(above.Baseline, ink);     // the descender row below it stays blank
    }

    /// <summary>Row indices carrying ink when <paramref name="face"/> paints <paramref name="text"/>
    /// at the buffer's origin.</summary>
    private static int[] InkRows(IGlyphFont face, string text)
    {
        var size = face.Measure(text);
        var buffer = new CellBuffer(Math.Max(1, size.Columns), Math.Max(1, size.Rows));
        face.Paint(buffer, 0, 0, text, default(PartialStyle));

        var rows = new List<int>();

        for (int r = 0; r < buffer.Rows; r++)
        {
            for (int c = 0; c < buffer.Columns; c++)
            {
                if (buffer[c, r].Grapheme is { Length: > 0 } g && g.Trim().Length > 0)
                {
                    rows.Add(r);
                    break;
                }
            }
        }

        return rows.ToArray();
    }

    [Fact]
    public void DecoratorsOverASingleRowFace_HideTheDifference()
    {
        // Why the naive forward would have shipped: over MonospaceFont — which is what every
        // bundled DecoratedFont instance wraps — Above's shift is the only thing distinguishing
        // the two positions, and nobody looks.
        var above = new DecoratedFont("above", MonospaceFont.Default, new Rune('▔'), DecorationPosition.Above);

        Assert.Equal(2, above.Baseline);                    // NOT MonospaceFont's 1
        Assert.Equal(0, above.Descender);
        Assert.Equal(1, DecoratedFont.HalfBlockUnderline.Baseline);
        Assert.Equal(1, DecoratedFont.HalfBlockUnderline.Descender);
    }

    [Fact]
    public void NestedDecorators_CompoundTheirShifts()
    {
        // Shadow over an Above-decorated face: the decoration pushes the baseline down one, the
        // shadow adds a row below it.
        var inner = DescenderFace();
        var stack = new ShadowedFont(new DecoratedFont("d", inner, new Rune('▔'), DecorationPosition.Above));

        Assert.Equal(inner.Baseline + 1, stack.Baseline);
        Assert.Equal(inner.Descender + 1, stack.Descender);
        Assert.Equal(stack.Measure("A").Rows, stack.Ascender + stack.Descender);
    }

    // ---- What layout actually consults ----

    [Fact]
    public void GlyphMetrics_ExposeTheFacesBaseline_ClampedToTheMeasuredBox()
    {
        var face = DescenderFace();
        var metrics = face.GetMetrics();

        Assert.Equal(6, metrics.LineRows);
        Assert.Equal(5, metrics.Baseline);
    }

    [Fact]
    public void IdentityAndScaledMetrics_PutTheBaselineOnTheBottomRow()
    {
        // Neither has a descender concept: a cell is a cell, and an OSC 66 block is s×s cells the
        // TERMINAL fills. Bottom row it is.
        Assert.Equal(1, GlyphMetrics.Monospace.Baseline);
        Assert.Equal(GlyphMetrics.Monospace.LineRows, GlyphMetrics.Monospace.Baseline);

        var scaled = MonospaceFont.Default.GetScaledMetrics(new TextSizing(Scale: 3));
        Assert.Equal(3, scaled.LineRows);
        Assert.Equal(3, scaled.Baseline);
    }
}
