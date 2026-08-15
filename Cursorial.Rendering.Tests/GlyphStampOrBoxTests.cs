using System.Text;

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Text;

namespace Cursorial.Tests.Rendering;

/// <summary>
/// The flat <see cref="IGlyphFont.Paint(in CellBufferView, int, int, ReadOnlySpan{char}, in PartialStyle)"/>
/// overload's stamp-versus-box rule: the background's PRESENCE decides whether the paint owns the
/// cells its face does not ink.
/// </summary>
/// <remarks>
/// Every assertion here reads the rendered cells. "The fixture stated no background" is not evidence
/// that a cell HAS no background — every one of these buffers is pre-painted, so a cell that came out
/// wrong comes out visibly wrong.
/// </remarks>
public class GlyphStampOrBoxTests
{
    private static readonly Color Backdrop    = Color.FromRgb(0, 0, 128);
    private static readonly Color BackdropInk = Color.FromRgb(200, 200, 0);
    private static readonly Color Ink         = Color.FromRgb(255, 0, 0);
    private static readonly Color BoxColor    = Color.FromRgb(0, 128, 0);

    // The 3x3 'A' is mostly holes: the whole point of the distinction is that they can be kept.
    //   " A "   →  (0,0) and (2,0) are gaps
    //   "/_\"   →  fully inked
    //   "A A"   →  (1,2) is a gap
    private static readonly (int Column, int Row)[] Gaps = [(0, 0), (2, 0), (1, 2)];
    private static readonly (int Column, int Row)[] Strokes = [(1, 0), (0, 1), (1, 1), (2, 1), (0, 2), (2, 2)];

    private static FigletFont Sparse() =>
        new("sparse", '$', 3, FigletLayoutMode.None,
            new Dictionary<uint, FigletGlyph> { ['A'] = new('A', [" A ", "/_\\", "A A"], '$') });

    /// <summary>A buffer every cell of which already holds somebody else's content, in somebody
    /// else's style — so "untouched" and "overwritten" are told apart by what is in the cell.</summary>
    private static CellBuffer Filled(int columns = 8, int rows = 6)
    {
        var buffer = new CellBuffer(columns, rows);
        var style = CellStyle.Default.WithBackground(Backdrop)
                                     .WithForeground(BackdropInk)
                                     .WithAttributes(TextAttributes.Bold);

        for (int r = 0; r < rows; r++)
        for (int c = 0; c < columns; c++)
            buffer.Set(c, r, "#", style);

        // The fixture is itself asserted: a backdrop that failed to land would make every "the
        // content survived" assertion below pass for the wrong reason.
        Assert.Equal("#", buffer[0, 0].Grapheme);
        Assert.Equal(Backdrop, buffer[0, 0].Style.Background);

        return buffer;
    }

    // ---- stamp -----------------------------------------------------------------------------

    [Fact]
    public void Stamp_OverExistingContent_LeavesTheGapsUntouched()
    {
        var buffer = Filled();

        Sparse().Paint(buffer, 0, 0, "A", PartialStyle.WithForeground(Ink));

        foreach (var (column, row) in Gaps)
        {
            var cell = buffer[column, row];

            Assert.Equal("#", cell.Grapheme);
            Assert.Equal(Backdrop, cell.Style.Background);
            Assert.Equal(BackdropInk, cell.Style.Foreground);
            Assert.Equal(TextAttributes.Bold, cell.Style.Attributes);
        }

        // ...and the strokes DID land, so the gaps survived a real paint rather than no paint.
        Assert.Equal("A", buffer[1, 0].Grapheme);
        Assert.Equal(Ink, buffer[1, 0].Style.Foreground);
    }

    [Fact]
    public void Stamp_WithAForegroundAndNoBackground_InksAndLeavesTheBackgroundAlone()
    {
        var buffer = Filled();

        Sparse().Paint(buffer, 0, 0, "A", PartialStyle.WithForeground(Ink));

        foreach (var (column, row) in Strokes)
        {
            var cell = buffer[column, row];

            Assert.Equal(Ink, cell.Style.Foreground);

            // The channel the delta declined to state falls through to the cell — which is the
            // whole difference between "no opinion" and "the terminal default".
            Assert.Equal(Backdrop, cell.Style.Background);

            // As does the attribute it declined to state.
            Assert.Equal(TextAttributes.Bold, cell.Style.Attributes);
        }
    }

    // ---- box -------------------------------------------------------------------------------

    [Fact]
    public void Box_FillsTheCellsTheFaceDoesNotInk()
    {
        var buffer = Filled();

        Sparse().Paint(buffer, 0, 0, "A", PartialStyle.WithForeground(Ink) with { Background = BoxColor });

        foreach (var (column, row) in Gaps)
        {
            var cell = buffer[column, row];

            Assert.True(string.IsNullOrWhiteSpace(cell.Grapheme),
                        $"({column},{row}) still holds '{cell.Grapheme}' — the box did not clear it.");
            Assert.Equal(BoxColor, cell.Style.Background);
        }

        foreach (var (column, row) in Strokes)
        {
            var cell = buffer[column, row];

            Assert.False(string.IsNullOrWhiteSpace(cell.Grapheme));
            Assert.Equal(BoxColor, cell.Style.Background);
            Assert.Equal(Ink, cell.Style.Foreground);
        }
    }

    /// <summary>
    /// The case the old spelling could not express AT ALL: a background that IS the terminal's own.
    /// <c>CellStyle.Default.WithForeground(ink)</c> was the only way to write both of these, so
    /// whichever mode a face chose for it, the other was unreachable.
    /// </summary>
    [Fact]
    public void Box_WithTheTerminalDefaultBackground_StillBoxes()
    {
        var stamped = Filled();
        var boxed   = Filled();

        Sparse().Paint(stamped, 0, 0, "A", PartialStyle.WithForeground(Ink));
        Sparse().Paint(boxed,   0, 0, "A", PartialStyle.WithForeground(Ink) with { Background = Color.Default });

        foreach (var (column, row) in Gaps)
        {
            Assert.Equal("#", stamped[column, row].Grapheme);

            Assert.True(string.IsNullOrWhiteSpace(boxed[column, row].Grapheme),
                        $"({column},{row}) still holds '{boxed[column, row].Grapheme}' — a Color.Default " +
                        "background is an OPINION and must own the gaps like any other.");
        }

        // What Color.Default does NOT do is repaint the backdrop's colour: CellBuffer.Set reads it as
        // "the terminal's own background, whatever this cell already had" and keeps the backdrop
        // verbatim rather than compositing. The box still OWNS these cells — it cleared them — it just
        // has no colour of its own to put there. Asserted so the limitation is stated, not discovered.
        Assert.Equal(Backdrop, boxed[0, 0].Style.Background);
    }

    // ---- the four faces --------------------------------------------------------------------
    //
    // Four separate paint paths, one of which (ShadowedFont) paints twice and one of which
    // (DecoratedFont) writes cells of its own after its inner face has finished.

    public static IEnumerable<object[]> AllFaces =>
    [
        ["monospace"], ["figlet"], ["shadowed"], ["decorated"]
    ];

    private static IEnumerable<(int Column, int Row)> Cells(Size box)
    {
        for (int r = 0; r < box.Rows; r++)
        for (int c = 0; c < box.Columns; c++)
            yield return (c, r);
    }

    private static IGlyphFont Face(string name) => name switch
    {
        "monospace" => MonospaceFont.Default,
        "figlet"    => Sparse(),
        "shadowed"  => new ShadowedFont(Sparse(), offset: (1, 1)),
        "decorated" => new DecoratedFont("decorated", Sparse(), new Rune('_')),
        _           => throw new ArgumentOutOfRangeException(nameof(name))
    };

    [Theory]
    [MemberData(nameof(AllFaces))]
    public void EveryFace_Stamping_LeavesEveryBackgroundInItsBoxAlone(string name)
    {
        var face = Face(name);
        var buffer = Filled();

        face.Paint(buffer, 0, 0, "A", PartialStyle.WithForeground(Ink));

        var box = face.Measure("A");

        for (int r = 0; r < box.Rows; r++)
        for (int c = 0; c < box.Columns; c++)
        {
            Assert.Equal(Backdrop, buffer[c, r].Style.Background);
        }

        // And the face really did paint — otherwise "left alone" is what an empty method achieves.
        Assert.True(Cells(box).Any(p => buffer[p.Column, p.Row].Style.Foreground == Ink),
                    $"{name} inked nothing at all");
    }

    [Theory]
    [MemberData(nameof(AllFaces))]
    public void EveryFace_Boxing_PaintsTheBackgroundIntoEveryCellOfItsBox(string name)
    {
        var face = Face(name);
        var buffer = Filled();

        face.Paint(buffer, 0, 0, "A", PartialStyle.WithForeground(Ink) with { Background = BoxColor });

        var box = face.Measure("A");

        for (int r = 0; r < box.Rows; r++)
        for (int c = 0; c < box.Columns; c++)
        {
            Assert.Equal(BoxColor, buffer[c, r].Style.Background);
            Assert.NotEqual("#", buffer[c, r].Grapheme);
        }

        // Outside the box, nothing moved.
        Assert.Equal("#", buffer[box.Columns, 0].Grapheme);
        Assert.Equal(Backdrop, buffer[box.Columns, 0].Style.Background);
    }

    /// <summary>
    /// ShadowedFont settles the box ONCE, for the whole decorated footprint, and stamps both passes
    /// inward. Forwarding the background to the inner face instead would have the glyph pass fill the
    /// glyph's box a second time — over the shadow that had just been painted into it.
    /// </summary>
    [Fact]
    public void Box_ThroughAShadowedFace_DoesNotEraseTheShadowInsideTheGlyphBox()
    {
        var buffer = Filled();
        var face = new ShadowedFont(Sparse(), offset: (1, 1));

        face.Paint(buffer, 0, 0, "A", PartialStyle.WithForeground(Ink) with { Background = BoxColor });

        // The shadow of the top row's 'A' (glyph cell (1,0)) lands at (2,1) — which is INSIDE the
        // glyph's own 3x3 box, and is a stroke cell of the glyph too. The shadow of the bottom row's
        // right 'A' (2,2) lands at (3,3): inside the SHADOWED face's box, outside the glyph's.
        Assert.False(string.IsNullOrWhiteSpace(buffer[3, 3].Grapheme),
                     "the shadow row below the glyph was erased");
        Assert.Equal(BoxColor, buffer[3, 3].Style.Background);
    }

    // ---- what the delta carries ------------------------------------------------------------

    [Fact]
    public void Attributes_AndUnderlineShape_ReachTheInkedCells()
    {
        var buffer = Filled();
        var underline = Color.FromRgb(0, 255, 255);

        // MonospaceFont, not the FIGlet face: EnsureCompatibleStyle strips underlines from a FIGlet,
        // which would make this test about the wrong thing.
        MonospaceFont.Default.Paint(buffer, 0, 0, "hi",
                                    PartialStyle.WithUnderline(UnderlineStyle.Curly, underline)
                                                .Applying(TextAttributes.Inverse));

        for (int c = 0; c < 2; c++)
        {
            var cell = buffer[c, 0];

            Assert.Equal(UnderlineStyle.Curly, cell.Style.UnderlineStyle);
            Assert.Equal(underline, cell.Style.UnderlineColor);
            Assert.True(cell.Style.Attributes.HasFlag(TextAttributes.Underline),
                        "a shape implies the flag");
            Assert.True(cell.Style.Attributes.HasFlag(TextAttributes.Inverse));

            // Bold was the CELL's, and the delta said nothing about it.
            Assert.True(cell.Style.Attributes.HasFlag(TextAttributes.Bold));
        }
    }

    [Fact]
    public void Attributes_ReachTheInkedCells_ThroughAFigletFace()
    {
        var buffer = Filled();

        Sparse().Paint(buffer, 0, 0, "A", PartialStyle.WithForeground(Ink).Applying(TextAttributes.Inverse));

        foreach (var (column, row) in Strokes)
            Assert.True(buffer[column, row].Style.Attributes.HasFlag(TextAttributes.Inverse),
                        $"({column},{row}) lost the delta's Inverse");

        // The gaps did not catch it: an attribute is ink, not a box fill.
        foreach (var (column, row) in Gaps)
            Assert.False(buffer[column, row].Style.Attributes.HasFlag(TextAttributes.Inverse));
    }
}
