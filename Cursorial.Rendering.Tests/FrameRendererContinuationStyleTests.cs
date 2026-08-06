using System.Buffers;
using System.Text;

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fragments;

namespace Cursorial.Tests.Rendering;

/// <summary>
/// A <see cref="CellKind.WideContinuation"/> in a <see cref="CellBuffer"/> carries
/// <see cref="Cell.Kind"/> and nothing else — the wide-left's SGR is what paints both columns.
/// These pin the three renderer paths that touch a continuation column, each of which sources its
/// style from somewhere different and must keep doing so.
/// </summary>
public class FrameRendererContinuationStyleTests
{
    private static string Render(FrameRenderer renderer, CellBuffer back)
    {
        var writer = new ArrayBufferWriter<byte>();
        renderer.Render(back, writer);
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    // Truecolor: the assertions read the RGB backgrounds straight off the wire, and a quantizer
    // would flatten them to nothing before they got there.
    private static OutputCapabilities CapsWithWideGlyphs(bool wide)
        => OutputCapabilities.None with
           {
               Color = OutputCapabilities.None.Color with { Depth = ColorDepth.Truecolor },
               TextSizing = new TextSizingCapabilities(Width: false, Scale: false, ReliableWideGlyphs: wide)
           };

    // ---- 1. The wide-glyph defense pre-paints from the WIDE-LEFT ----

    [Fact]
    public void WideDefense_PrePaintsBothColumnsWithTheWideLeftsStyle_NeverTheContinuations()
    {
        // The defense emits two spaces to claim columns c and c+1 before the glyph, so that a
        // terminal which shrinks the glyph to one cell still leaves c+1 in the pair's colors. One
        // glyph, two cells, ONE style — the wide-left's. A decoy style planted on the continuation
        // (legal through the raw indexer, meaningless to a terminal) must never reach the wire.
        var renderer = new FrameRenderer(CapsWithWideGlyphs(false));
        var buffer = new CellBuffer(4, 1);
        buffer.Set(0, 0, "中", Style.Default.WithBackground(Color.FromRgb(80, 80, 80)));
        buffer[1, 0] = Cell.WideContinuation with { Style = Style.Default.WithBackground(Color.FromRgb(9, 9, 9)) };

        var output = Render(renderer, buffer);

        Assert.DoesNotContain("48;2;9;9;9", output);

        int glyph = output.IndexOf('中');
        Assert.True(glyph > 0);

        // The pre-paint spaces sit between the wide-left's SGR and the CUP-back-to-c, with no
        // further SGR in between — nothing re-syncs the style off the continuation.
        var beforeGlyph = output[..glyph];
        int sgr = beforeGlyph.LastIndexOf("48;2;80;80;80", StringComparison.Ordinal);
        int spaces = beforeGlyph.LastIndexOf("  ", StringComparison.Ordinal);
        Assert.True(sgr >= 0, "the wide-left's background never reached the wire");
        Assert.True(spaces > sgr, "the pre-paint spaces must follow the wide-left's SGR");
        // Ordinal, not xUnit's culture-sensitive default: ESC is a zero-weight character under ICU,
        // so a culture-sensitive search for it matches anywhere.
        Assert.False(beforeGlyph[(sgr + "48;2;80;80;80".Length)..spaces].Contains('\x1b'),
                     "an SGR was re-synced between the wide-left's style and the pre-paint");
    }

    // ---- 2. The ambiguous-width defense pre-paints from the NEIGHBOR ----

    [Fact]
    public void AmbiguousGlyphBeforeAWidePair_EmitsThePairsLeftHalfWithThePairsOwnStyle()
    {
        // Two INDEPENDENT glyphs, not one: the ambiguous glyph at c may render two cells wide, so
        // its neighbor is painted first with the NEIGHBOR's own style and then covered. When that
        // neighbor is the left half of a wide pair, "its own style" is the pair's — which is why
        // this defense must keep sourcing the neighbor's style rather than a sibling's, even though
        // the wide-glyph defense one branch over does the opposite.
        var renderer = new FrameRenderer(CapsWithWideGlyphs(false));
        var buffer = new CellBuffer(4, 1);
        buffer.Set(0, 0, "─", Style.Default.WithBackground(Color.FromRgb(40, 50, 60)));
        buffer.Set(1, 0, "中", Style.Default.WithBackground(Color.FromRgb(10, 20, 30))); // pair at 1-2

        var output = Render(renderer, buffer);

        int pair = output.IndexOf('中');
        int rule = output.IndexOf('─');
        Assert.True(pair >= 0 && rule >= 0);
        Assert.True(pair < rule, "the neighbor must be painted before the ambiguous glyph");
        Assert.Equal(pair, output.LastIndexOf('中'));   // and exactly once — column 2 is skipped

        // The SGR in force when the pair's left half is written is the PAIR's.
        var beforePair = output[..pair];
        Assert.Contains("48;2;10;20;30", beforePair);
        Assert.True(beforePair.LastIndexOf("48;2;10;20;30", StringComparison.Ordinal) >
                    beforePair.LastIndexOf("48;2;40;50;60", StringComparison.Ordinal));
    }

    // ---- 3. The continuation snapshot stays symmetric ----

    [Fact]
    public void StaticWideGlyph_EmitsNothingAtEitherOfItsColumns_OnALaterFrame()
    {
        // EmitDiff copies the back continuation verbatim into the front buffer so later frames
        // diff correctly. A static wide glyph must therefore cost nothing at either of its columns.
        // An unrelated cell changes here so the frame is a real diff frame, not a trivially empty
        // one. (The teeth for the symmetry itself are in the scroll test below — the continuation
        // branch returns before it can emit, so a mismatch is invisible in a plain diff frame.)
        var renderer = new FrameRenderer();
        var buffer = new CellBuffer(6, 1);
        buffer.Set(1, 0, "中", Style.Default.WithBackground(Color.FromRgb(80, 80, 80))); // pair at 1-2
        buffer.Set(4, 0, "x", Style.Default);
        Render(renderer, buffer);

        buffer.Set(4, 0, "y", Style.Default);
        var output = Render(renderer, buffer);

        Assert.Contains("y", output);
        Assert.DoesNotContain('中', output);
        Assert.DoesNotContain("48;2;80;80;80", output);
        Assert.DoesNotContain("\x1b[1;2H", output);   // no CUP to the wide-left (1-based column 2)
        Assert.DoesNotContain("\x1b[1;3H", output);   // no CUP to the continuation
    }

    [Fact]
    public void ScrollDetection_StillMatchesThroughColumnsOfWideGlyphs()
    {
        // Where the snapshot's symmetry is actually load-bearing: scroll detection compares whole
        // cells, front against back-shifted-by-k. If one side carried a style on its continuations
        // and the other did not, every continuation column would read as a difference, no shift
        // would ever match, and a scrolling CJK view would fall back to a full repaint every frame.
        var renderer = new FrameRenderer();
        var buffer = new CellBuffer(4, 4);

        for (int row = 0; row < 4; row++)
        {
            buffer.Set(0, row, "中", Style.Default);              // pair at columns 0-1
            buffer.Set(2, row, ((char) ('A' + row)).ToString(), Style.Default);
        }

        Render(renderer, buffer);

        buffer.Clear();
        for (int row = 0; row < 4; row++)
        {
            buffer.Set(0, row, "中", Style.Default);
            buffer.Set(2, row, ((char) ('B' + row)).ToString(), Style.Default); // shifted up by one
        }

        var output = Render(renderer, buffer);

        Assert.Contains("\x1b[1S", output);   // SU 1 — the shift was recognized
        Assert.Contains("E", output);         // only the newly-uncovered bottom row repaints
    }

    // ---- 4. The covered-cell substitution reads the wide-left's background ----

    [Fact]
    public void WideGlyphUnderACellsFragment_PaintsTheContinuationColumnWithTheWideLeftsBackground()
    {
        // Under a Cells-layer fragment the glyph is dropped and each covered cell emits a bg-only
        // space, so the pair becomes TWO independent single cells — and the right one needs a
        // background of its own. It has none stored, so it has to come off the wide-left; otherwise
        // the fragment sits on one panel-colored column and one terminal-default hole.
        var renderer = new FrameRenderer();
        var buffer = new CellBuffer(5, 1);
        buffer.Set(1, 0, "中", Style.Default.WithBackground(Color.FromRgb(80, 80, 80))); // pair at 1-2
        buffer.AddFragment(1, 0, new CoveringFragment(new Size(2, 1)));

        var output = Render(renderer, buffer);

        Assert.DoesNotContain('中', output);

        // Two bg-only spaces, both in the panel color, and no background reset between them.
        int first = output.IndexOf("48;2;80;80;80", StringComparison.Ordinal);
        Assert.True(first >= 0);
        var afterFirst = output[(first + "48;2;80;80;80".Length)..];
        int payload = afterFirst.IndexOf("COVER", StringComparison.Ordinal);
        Assert.True(payload > 0);
        Assert.DoesNotContain("\x1b[49m", afterFirst[..payload]);  // no default-background hole
        Assert.DoesNotContain("\x1b[0m", afterFirst[..payload]);
    }

    private sealed class CoveringFragment(Size size) : IBufferFragment
    {
        public FragmentLayer Layer => FragmentLayer.Cells;
        public Size GetSize() => size;
        public bool IsSupported(OutputCapabilities capabilities) => true;

        public void Emit(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities)
        {
            var bytes = "COVER"u8;
            bytes.CopyTo(output.GetSpan(bytes.Length));
            output.Advance(bytes.Length);
        }
    }
}
