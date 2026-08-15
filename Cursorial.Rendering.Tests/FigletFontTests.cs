using System.Text;

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;

namespace Cursorial.Tests.Rendering;

public class FigletFontTests
{
    // ---- Behavior tests: hand-built FigletFont, bypass the parser --------------------------

    private static FigletFont BuildBehaviorFont(FigletLayoutMode mode = FigletLayoutMode.None)
    {
        // Hand-built 3-row font with hardblank '$'. Each glyph's lines are the raw glyph cells
        // (no end-markers, no wrapping). Hardblanks are not used in the visible body — we test
        // those explicitly in dedicated tests below.
        var glyphs = new Dictionary<uint, FigletGlyph>
                     {
                         [' '] = new(' ', ["   ", "   ", "   "], '$'),
                         ['A'] = new('A', [" A ", "/_\\", "A A"], '$'),
                         ['B'] = new('B', ["BB ", "BB ", "BB "], '$'),
                         ['C'] = new('C', [" C ", "C  ", " C "], '$'),
                     };

        return new FigletFont("behavior", '$', 3, mode, glyphs);
    }

    [Fact]
    public void Measure_AsciiString_WithoutKerning_SumsGlyphWidths()
    {
        var font = BuildBehaviorFont(FigletLayoutMode.None);
        Assert.Equal(new Size(6, 3), font.Measure("AB"));
    }

    [Fact]
    public void Measure_KernedBoundary_BlankGlyphIsAWordGap()
    {
        var font = BuildBehaviorFont(FigletLayoutMode.Kern);
        // "A " — the space glyph is entirely ink-free, which makes it a deliberate WORD GAP:
        // kerning never carries across it (maintainer decision 2026-08-02 — the old contract
        // collapsed the full blank width, visually joining words whenever a run painted as one
        // piece). Fonts that want smush-resistant interior blanks use HARDBLANKS, which count
        // as ink; a plain-blank space glyph keeps its full width. Result = 3 + 3 = 6.
        Assert.Equal(new Size(6, 3), font.Measure("A "));
    }

    [Fact]
    public void Measure_KernedBackToBackGlyphs_NoSlackAtBoundary()
    {
        var font = BuildBehaviorFont(FigletLayoutMode.Kern);
        // 'A' middle row "/_\\" ends at col 2 (no slack). 'B' is "BB " starting at col 0 (no
        // slack). One row has zero slack so kerning produces no movement. Width = 6.
        Assert.Equal(new Size(6, 3), font.Measure("AB"));
    }

    [Fact]
    public void Smush_EqualRule_DoesNotWidenOutput()
    {
        var withEqual = BuildBehaviorFont(
                FigletLayoutMode.Kern | FigletLayoutMode.Smush | FigletLayoutMode.Equal)
            .Measure("AA");

        var withoutEqual = BuildBehaviorFont(
                FigletLayoutMode.Kern | FigletLayoutMode.Smush)
            .Measure("AA");

        Assert.True(withEqual.Columns <= withoutEqual.Columns,
                    $"Equal smushing must not widen output ({withEqual.Columns} vs {withoutEqual.Columns}).");
    }

    [Fact]
    public void Smush_BigXRule_CombinesSlashAndBackslash()
    {
        // Build two glyphs: left ends with '/', right starts with '\'. BigX rule should let
        // them merge — overlap should be at least 1.
        var left = new FigletGlyph('L', ["L  ", "L/ ", "L  "], '$');
        var right = new FigletGlyph('R', ["  R", "\\ R", "  R"], '$');

        var font = new FigletFont("test", '$', 3,
                                  FigletLayoutMode.Kern | FigletLayoutMode.Smush | FigletLayoutMode.BigX,
                                  new Dictionary<uint, FigletGlyph> { ['L'] = left, ['R'] = right });

        // Without smushing, kerning alone gives some overlap. With BigX, the / + \ boundary
        // adds one more column of overlap on the middle row.
        int sizeWithBigX = font.Measure("LR").Columns;

        var noBigX = new FigletFont("test", '$', 3,
                                    FigletLayoutMode.Kern | FigletLayoutMode.Smush,
                                    new Dictionary<uint, FigletGlyph> { ['L'] = left, ['R'] = right });

        int sizeWithoutBigX = noBigX.Measure("LR").Columns;

        Assert.True(sizeWithBigX < sizeWithoutBigX,
                    $"BigX rule should tighten the boundary; got {sizeWithBigX} vs {sizeWithoutBigX}.");
    }

    [Fact]
    public void Paint_AsciiAtAnchor_PaintsGlyphLines()
    {
        var font = BuildBehaviorFont(FigletLayoutMode.None);
        var buffer = new CellBuffer(10, 5);

        var painted = font.Paint(buffer, 0, 0, "A", default(PartialStyle));

        Assert.Equal(new Size(3, 3), painted);
        Assert.Equal("A", buffer[1, 0].Grapheme);
        Assert.Equal("/", buffer[0, 1].Grapheme);
        Assert.Equal("_", buffer[1, 1].Grapheme);
        Assert.Equal("\\", buffer[2, 1].Grapheme);
        Assert.Equal("A", buffer[0, 2].Grapheme);
        Assert.Equal("A", buffer[2, 2].Grapheme);
        // Glyph spaces are transparent — the cells stay blank.
        Assert.True(string.IsNullOrEmpty(buffer[0, 0].Grapheme));
        Assert.True(string.IsNullOrEmpty(buffer[1, 2].Grapheme));
    }

    [Fact]
    public void Paint_HardblanksRenderAsSpace()
    {
        var glyph = new FigletGlyph('X', ["X$X"], '$');

        var font = new FigletFont("test", '$', 1, FigletLayoutMode.None,
                                  new Dictionary<uint, FigletGlyph> { ['X'] = glyph });

        var buffer = new CellBuffer(5, 1);

        font.Paint(buffer, 0, 0, "X", default(PartialStyle));

        Assert.Equal("X", buffer[0, 0].Grapheme);
        Assert.Equal(" ", buffer[1, 0].Grapheme); // hardblank → visible space
        Assert.Equal("X", buffer[2, 0].Grapheme);
    }

    [Fact]
    public void Paint_RespectsBlendingMode()
    {
        var font = BuildBehaviorFont(FigletLayoutMode.None);
        var buffer = new CellBuffer(5, 5);
        buffer.Fill(new Cell(" ", CellKind.Single, CellStyle.Default.WithBackground(Color.FromRgb(255, 0, 0))));

        buffer.PushBlendingMode(BlendingModes.Plus);

        try
        {
            font.Paint(buffer, 0, 0,
                       "A", PartialStyle.WithBackground(Color.FromRgb(0, 255, 0)));
        }
        finally
        {
            buffer.PopBlendingMode();
        }

        Assert.Equal(Color.FromRgb(255, 255, 0), buffer[1, 0].Style.Background);

        // (0,0) is a HOLE in the glyph, and it comes out yellow too — because a stated background
        // BOXES, and the fill blends through the pushed mode exactly as the ink does. This line used
        // to assert "untouched", back when a whole CellStyle could only mean "ink the strokes and
        // leave the gaps". That stamp is now a background the delta simply does not carry, and it is
        // pinned in GlyphStampOrBoxTests rather than riding along inside a blending test.
        Assert.Equal(Color.FromRgb(255, 255, 0), buffer[0, 0].Style.Background);
    }

    [Fact]
    public void Paint_DeltaCarryingMode_BlendsTheInkForegroundOverTheCellBackground()
    {
        var font = BuildBehaviorFont(FigletLayoutMode.None);
        var buffer = new CellBuffer(5, 5);
        buffer.Fill(new Cell(" ", CellKind.Single, CellStyle.Default.WithBackground(Color.FromRgb(0, 0, 255)))); // blue

        // An ink-only delta (no background → no box) carrying its OWN Multiply mode, no ambient push.
        // The glyph's foreground must blend against the cell's BACKGROUND via the scoped mode —
        // Multiply(red, blue) = black — the same fg-over-bg contract the plain arms get from Set.
        font.Paint(buffer, 0, 0, "A",
                   PartialStyle.WithForeground(Color.FromRgb(255, 0, 0)) with { Mode = BlendingModes.Multiply });

        // (1,0) is the 'A' stroke in the top glyph row " A ".
        Assert.Equal("A", buffer[1, 0].Grapheme);
        Assert.Equal(Color.FromRgb(0, 0, 0), buffer[1, 0].Style.Foreground);   // red × blue = black
        Assert.Equal(Color.FromRgb(0, 0, 255), buffer[1, 0].Style.Background); // background left alone
    }

    [Fact]
    public void Paint_ClipsToBufferRightEdge()
    {
        var font = BuildBehaviorFont(FigletLayoutMode.None);
        var buffer = new CellBuffer(5, 5);

        // 'A' is 3 wide. Anchor at column 3 — only 2 columns fit.
        font.Paint(buffer, 3, 0, "A", default(PartialStyle));

        // Cells inside the buffer at the right side got painted; the third column would have
        // been at col 5, which is past the right edge.
        Assert.Equal("A", buffer[4, 0].Grapheme);
    }

    [Fact]
    public void MissingGlyph_FallsBackToSpace()
    {
        var font = BuildBehaviorFont(FigletLayoutMode.None);

        Assert.False(font.HasGlyph('Z'));
        var size = font.Measure("Z");
        Assert.Equal(3, size.Rows);
        Assert.Equal(3, size.Columns); // width of the space glyph
    }

    [Fact]
    public void Constructor_MissingSpaceGlyph_FabricatesOne()
    {
        // FigletFont must always have a usable space glyph as the missing-glyph fallback.
        var font = new FigletFont("test", '$', 2, FigletLayoutMode.None,
                                  new Dictionary<uint, FigletGlyph>
                                  {
                                      ['A'] = new('A', ["AA", "AA"], '$'),
                                  });

        Assert.True(font.HasGlyph(' '));
    }

    // ---- Parser tests: full synthetic FLF round-trip --------------------------------------

    /// <summary>
    /// Build a complete FLF font source covering every required codepoint. Glyphs not in
    /// <paramref name="overrides"/> render as <c>height</c> rows of blanks. End-marker is
    /// '@' (single on each line, '@@' on the last line to match real FIGlet fonts).
    /// </summary>
    private static string BuildFlfSource(int height, char hardblank, Dictionary<uint, string[]> overrides)
    {
        var sb = new StringBuilder();
        sb.Append($"flf2a{hardblank} {height} {height} 8 0 0\n"); // header — height/baseline/maxLength/oldLayout/comments

        uint[] required =
        [
            32, 33, 34, 35, 36, 37, 38, 39, 40, 41,
            42, 43, 44, 45, 46, 47, 48, 49, 50, 51,
            52, 53, 54, 55, 56, 57, 58, 59, 60, 61,
            62, 63, 64, 65, 66, 67, 68, 69, 70, 71,
            72, 73, 74, 75, 76, 77, 78, 79, 80, 81,
            82, 83, 84, 85, 86, 87, 88, 89, 90, 91,
            92, 93, 94, 95, 96, 97, 98, 99, 100, 101,
            102, 103, 104, 105, 106, 107, 108, 109, 110, 111,
            112, 113, 114, 115, 116, 117, 118, 119, 120, 121,
            122, 123, 124, 125, 126,
            196, 214, 220, 228, 246, 252, 223
        ];

        var blank = new string[height];
        Array.Fill(blank, "    ");

        foreach (var cp in required)
        {
            var lines = overrides.GetValueOrDefault(cp, blank);

            for (int i = 0; i < height; i++)
            {
                var lineBody = i < lines.Length ? lines[i] : "    ";
                sb.Append(lineBody);
                sb.Append(i == height - 1 ? "@@\n" : "@\n");
            }
        }

        return sb.ToString();
    }

    [Fact]
    public void Parser_ReadsHeaderFields()
    {
        var source = BuildFlfSource(3, '$', new Dictionary<uint, string[]>());
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));
        var font = FigletFontParser.Load(stream, "synthetic");

        Assert.Equal("synthetic", font.Name);
        Assert.Equal('$', font.HardBlank);
        Assert.Equal(3, font.Height);
    }

    [Fact]
    public void Parser_LoadsRequiredGlyphsInPositionalOrder()
    {
        var overrides = new Dictionary<uint, string[]>
                        {
                            ['A'] = [" A ", "/_\\", "A A"],
                            ['B'] = ["BB ", "BB ", "BB "]
                        };

        var source = BuildFlfSource(3, '$', overrides);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));
        var font = FigletFontParser.Load(stream, "synthetic");

        Assert.True(font.HasGlyph('A'));
        var a = font.GetGlyph('A');
        Assert.Equal(" A ", a.Lines[0]);
        Assert.Equal("/_\\", a.Lines[1]);
        Assert.Equal("A A", a.Lines[2]);

        Assert.True(font.HasGlyph('B'));
        var b = font.GetGlyph('B');
        Assert.Equal("BB ", b.Lines[0]);
    }

    [Fact]
    public void Parser_StripsEndMarkers()
    {
        var overrides = new Dictionary<uint, string[]> { ['A'] = ["A", "B", "C"] };
        var source = BuildFlfSource(3, '$', overrides);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));
        var font = FigletFontParser.Load(stream, "synthetic");

        var a = font.GetGlyph('A');
        Assert.Equal("A", a.Lines[0]);
        Assert.Equal("B", a.Lines[1]);
        Assert.Equal("C", a.Lines[2]);
    }

    [Fact]
    public void Parser_RejectsBadSignature()
    {
        using var stream = new MemoryStream("not a flf file"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => FigletFontParser.Load(stream, "bad"));
    }

    // ---- Embedded fonts: load each bundled resource end-to-end ----------------------------

    [Theory]
    [InlineData("Standard")]
    [InlineData("Slant")]
    [InlineData("Small")]
    [InlineData("Big")]
    [InlineData("Mini")]
    public void EmbeddedFont_LoadsAndRendersAscii(string fontProperty)
    {
        var font = fontProperty switch
                   {
                       "Standard" => FigletFonts.Standard,
                       "Slant"    => FigletFonts.Slant,
                       "Small"    => FigletFonts.Small,
                       "Big"      => FigletFonts.Big,
                       "Mini"     => FigletFonts.Mini,
                       _          => throw new ArgumentException(fontProperty),
                   };

        // Every bundled font defines the ASCII printable range — basic sanity check that the
        // resource was decoded correctly and the parser landed positionally.
        Assert.True(font.HasGlyph('A'));
        Assert.True(font.HasGlyph('Z'));
        Assert.True(font.HasGlyph('0'));
        Assert.True(font.HasGlyph('9'));
        Assert.True(font.HasGlyph('!'));

        // Measure should be non-trivial.
        var size = font.Measure("Hi");
        Assert.True(size.Columns > 0);
        Assert.Equal(font.Height, size.Rows);

        // Paint into a buffer big enough for the measured size and assert at least one cell got
        // ink.
        var buffer = new CellBuffer(size.Columns + 2, size.Rows);
        font.Paint(buffer, 0, 0, "Hi", default(PartialStyle));

        bool anyInk = false;

        for (int r = 0; r < buffer.Rows && !anyInk; r++)
        {
            for (int c = 0; c < buffer.Columns && !anyInk; c++)
            {
                if (!string.IsNullOrEmpty(buffer[c, r].Grapheme))
                    anyInk = true;
            }
        }

        Assert.True(anyInk, $"Painting 'Hi' with the {fontProperty} font produced no visible cells.");
    }

    [Fact]
    public void EmbeddedFont_Standard_CachedAcrossAccesses()
    {
        // Lazy<T> contract — first access loads, subsequent accesses return the same instance.
        Assert.Same(FigletFonts.Standard, FigletFonts.Standard);
    }

    [Fact]
    public void Parser_TruncatedRequiredBlock_StopsGracefully()
    {
        // Header + only one glyph's worth of body — the rest of the required glyphs are missing.
        // The parser should stop at EOF rather than throw.
        var source = "flf2a$ 3 3 4 0 0\n" +
                     " A @\n/_\\@\nA A@@\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));
        var font = FigletFontParser.Load(stream, "truncated");

        Assert.True(font.HasGlyph(' ')); // space was loaded (first required cp)
        // 'A' (cp 65) is way past the single glyph we provided — missing, but no throw.
        Assert.False(font.HasGlyph('A'));
    }
}