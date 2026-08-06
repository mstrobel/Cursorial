using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Text;

namespace Cursorial.Tests.Rendering;

/// <summary>
/// Authored U+2026 art in the bundled <c>.flf</c> faces. This is an OPTIMIZATION over the derived
/// "..." the formatter falls back to (see <see cref="Text.TrimIndicatorTests"/>) — tighter, and
/// drawn in each face's own idiom — never the fix: user-supplied fonts we will never see still
/// have to work, and several bundled faces are deliberately left without art.
/// </summary>
public class BundledFigletEllipsisTests
{
    private const uint HorizontalEllipsis = 0x2026;

    /// <summary>Faces that carry authored ellipsis art.</summary>
    public static TheoryData<string> Authored =>
    [
        "Standard", "Small", "Big", "Mini", "AnsiShadow", "Hp2640LargeType", "CGA", "LCDMatrix"
    ];

    /// <summary>
    /// Faces deliberately left to the derived "...". Slant / SmallSlant / AnsiShadow-shaped
    /// reasoning: their '.' already smushes into the exact three-dot mark authored art would
    /// draw. Roman's dots carry a hardblank pad that only a muddier, denser glyph could beat.
    /// MiniWi and LED ship truncated (95 of the 102 required codepoints), and the FLF required
    /// block is POSITIONAL — an appended codetag is swallowed as the missing Ä…ß bodies. Padding
    /// the block with empty bodies does reach the codetag, but leaves those seven reporting
    /// HasGlyph == true while painting nothing and advancing zero cells, which is the very bug
    /// HasGlyph exists to prevent. Finishing the faces properly is the only real fix.
    /// </summary>
    public static TheoryData<string> Derived => ["Slant", "SmallSlant", "Roman", "MiniWi", "LED"];

    public static TheoryData<string> AllFaces =>
    [
        "Standard", "Slant", "Small", "Big", "Mini", "AnsiShadow", "Hp2640LargeType",
        "MiniWi", "CGA", "LCDMatrix", "LED", "Roman", "SmallSlant"
    ];

    private static FigletFont Face(string name)
        => (FigletFont) typeof(FigletFonts).GetProperty(name)!.GetValue(null)!;

    [Theory]
    [MemberData(nameof(Authored))]
    public void AuthoredFace_DefinesTheHorizontalEllipsis(string name)
    {
        var face = Face(name);

        Assert.True(face.HasGlyph(HorizontalEllipsis));
        Assert.Equal(face.Height, face.GetGlyph(HorizontalEllipsis).Height);
        Assert.Equal(face.Height, face.Measure("…").Rows);
    }

    [Theory]
    [MemberData(nameof(Authored))]
    public void AuthoredEllipsis_PaintsInk(string name)
    {
        // The whole point: the glyph must be VISIBLE. An empty codetag body would parse fine and
        // reintroduce the invisible-indicator bug in the faces we ship.
        //
        // Measured against the face's OWN reported size rather than a fixed cell count: the
        // bundled art spans a 2x2 LED cell (2 cells of ink) to big.flf's 7x8 (10), so any constant
        // floor is either unreachable for the compact faces or vacuous for the large ones. Painting
        // into a buffer with slack turns this into an exact property instead — every inked cell
        // must fall inside the box `Measure` promised, which also catches a glyph that under-reports
        // its width and would then overrun whatever sits beside it in a trimmed line.
        var face = Face(name);
        var size = face.Measure("…");
        var buffer = new CellBuffer(size.Columns + 2, size.Rows + 2);

        face.Paint(buffer, 0, 0, "…", CellStyle.Default);

        int total = Ink(buffer);
        int inside = Ink(buffer, size.Columns, size.Rows);

        Assert.True(total > 0, $"{name}: the authored ellipsis painted no ink");
        Assert.True(inside == total,
                    $"{name}: {total - inside} of {total} inked cells fell outside the reported {size.Columns}x{size.Rows} box");
    }

    [Theory]
    [MemberData(nameof(Authored))]
    public void AuthoredEllipsis_IsTighterThanTheDerivedForm(string name)
    {
        // Authored art earns its keep only by being narrower — otherwise the derived "..." is
        // strictly the better deal (no font bytes, works everywhere).
        var face = Face(name);

        Assert.True(face.Measure("…").Columns < face.Measure(TextFormatter.DerivedEllipsis).Columns,
                    $"{name}: authored {face.Measure("…").Columns} vs derived {face.Measure("...").Columns}");
    }

    [Theory]
    [MemberData(nameof(Derived))]
    public void FaceWithoutArt_StillTrimsThroughTheDerivedForm(string name)
    {
        var face = Face(name);
        Assert.False(face.HasGlyph(HorizontalEllipsis));

        var tf = new TextFormatter { Wrap = WrapMode.NoWrap, Trim = TextTrimming.CharacterEllipsis };
        var rt = new RichTextBuilder().Figlet(new string('M', 80), face).Build();
        var ft = tf.Format(rt, 40, capabilities: Cursorial.Output.Capabilities.OutputCapabilities.None);

        var p = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
        var line = Assert.Single(p.Lines);
        Assert.Equal(TextFormatter.DerivedEllipsis, ((FormattedTextRun) line.Runs[^1]).Text);
    }

    [Theory]
    [MemberData(nameof(AllFaces))]
    public void EveryBundledFace_KeepsItsRequiredAsciiRepertoire(string name)
    {
        // A codetag appended in the wrong place desynchronizes the POSITIONAL required-glyph
        // parse and every glyph after it shifts by one body. This catches that.
        var face = Face(name);

        for (uint cp = 32; cp <= 126; cp++)
        {
            Assert.True(face.HasGlyph(cp), $"{name}: lost U+{cp:X4} '{(char) cp}'");
            Assert.Equal(face.Height, face.GetGlyph(cp).Height);
        }
    }

    [Theory]
    [MemberData(nameof(AllFaces))]
    public void EveryBundledFace_StillRendersItsAlphabet(string name)
    {
        // Shifted glyph bodies would still be `Height` tall — so also check the table means what
        // it says: letters have ink, the space glyph does not.
        var face = Face(name);

        foreach (var ch in "ABZaz09.")
        {
            var size = face.Measure(ch.ToString());
            var buffer = new CellBuffer(Math.Max(1, size.Columns), size.Rows);
            face.Paint(buffer, 0, 0, ch.ToString(), CellStyle.Default);
            Assert.True(Ink(buffer) > 0, $"{name}: '{ch}' painted no ink");
        }

        var space = new CellBuffer(Math.Max(1, face.Measure(" ").Columns), face.Height);
        face.Paint(space, 0, 0, " ", CellStyle.Default);
        Assert.Equal(0, Ink(space));
    }

    private static int Ink(CellBuffer buffer) => Ink(buffer, buffer.Columns, buffer.Rows);

    /// <summary>Inked cells within the top-left <paramref name="columns"/>x<paramref name="rows"/> region.</summary>
    private static int Ink(CellBuffer buffer, int columns, int rows)
    {
        int count = 0;

        for (int r = 0; r < Math.Min(rows, buffer.Rows); r++)
        for (int c = 0; c < Math.Min(columns, buffer.Columns); c++)
            if (buffer[c, r].Grapheme is { Length: > 0 } g && g.Trim().Length > 0)
                count++;

        return count;
    }
}
