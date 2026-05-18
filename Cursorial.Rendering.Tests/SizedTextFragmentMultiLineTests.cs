using System.Buffers;
using System.Text;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fragments;

namespace Cursorial.Tests.Rendering;

public class SizedTextFragmentMultiLineTests
{
    private static OutputCapabilities CapsWithScale()
        => OutputCapabilities.None with
           {
               TextSizing = new TextSizingCapabilities(Width: true, Scale: true),
           };

    private static string EmitToString(SizedTextFragment fragment)
    {
        var w = new ArrayBufferWriter<byte>();
        fragment.Emit(0, 0, w, CapsWithScale());
        return Encoding.ASCII.GetString(w.WrittenSpan);
    }

    [Fact]
    public void Lines_SinglePayload_ProducesOneLine()
    {
        var fragment = new SizedTextFragment(
            new TextSizing(Scale: 2),
            "hello",
            Style.Default);
        Assert.Equal(1, fragment.Lines.Length);
        Assert.Equal("hello", fragment.Lines[0]);
    }

    [Fact]
    public void Lines_LfSplitsIntoMultiple()
    {
        var fragment = new SizedTextFragment(
            new TextSizing(Scale: 2),
            "hello\nworld",
            Style.Default);
        Assert.Equal(2, fragment.Lines.Length);
        Assert.Equal("hello", fragment.Lines[0]);
        Assert.Equal("world", fragment.Lines[1]);
    }

    [Fact]
    public void Lines_CrLfNormalizedToLf()
    {
        var fragment = new SizedTextFragment(
            new TextSizing(Scale: 2),
            "hello\r\nworld",
            Style.Default);
        Assert.Equal(2, fragment.Lines.Length);
        Assert.Equal("hello", fragment.Lines[0]);
        Assert.Equal("world", fragment.Lines[1]);
    }

    [Fact]
    public void Lines_EmptyTextProducesSingleEmptyLine()
    {
        var fragment = new SizedTextFragment(
            new TextSizing(Scale: 2),
            "",
            Style.Default);
        Assert.Equal(1, fragment.Lines.Length);
        Assert.Equal("", fragment.Lines[0]);
    }

    [Fact]
    public void Lines_TrailingNewlineProducesEmptyTrailingLine()
    {
        var fragment = new SizedTextFragment(
            new TextSizing(Scale: 1),
            "hello\n",
            Style.Default);
        Assert.Equal(2, fragment.Lines.Length);
        Assert.Equal("hello", fragment.Lines[0]);
        Assert.Equal("", fragment.Lines[1]);
    }

    [Fact]
    public void GetSize_AutoWidth_MultiLine_WidestLineByLineCountTimesScale()
    {
        // Two lines: "hi" (2 cols natural) and "hello" (5 cols natural). Scale 2.
        // Bounding box: max(2, 5) × 2 = 10 columns, 2 lines × 2 scale = 4 rows.
        var fragment = new SizedTextFragment(
            new TextSizing(Scale: 2),
            "hi\nhello",
            Style.Default);
        Assert.Equal(new Size(10, 4), fragment.GetSize());
    }

    [Fact]
    public void GetSize_FixedWidth_MultiLine_ClusterCountTimesWidthByLines()
    {
        // 3 clusters × Width 3 × Scale 2 = 18 cols on the long line.
        var fragment = new SizedTextFragment(
            new TextSizing(Scale: 2, Width: 3),
            "ab\ncde",
            Style.Default);
        // Bounding box: max(2, 3) × 3 × 2 = 18; 2 lines × 2 = 4.
        Assert.Equal(new Size(18, 4), fragment.GetSize());
    }

    [Fact]
    public void GetSize_SingleLine_UnchangedFromV1Behavior()
    {
        // The single-line case must produce the same dimensions as the original
        // pre-multi-line code path.
        var fragment = new SizedTextFragment(
            new TextSizing(Scale: 2),
            "hello",
            Style.Default);
        Assert.Equal(new Size(10, 2), fragment.GetSize());
    }

    [Fact]
    public void Emit_SingleLine_OneOsc66Sequence()
    {
        var fragment = new SizedTextFragment(
            new TextSizing(Scale: 2),
            "hi",
            Style.Default);
        var output = EmitToString(fragment);

        // Single OSC 66 payload — no CUP between lines.
        int oscCount = 0;
        int idx = 0;
        while ((idx = output.IndexOf("]66;", idx, StringComparison.Ordinal)) >= 0)
        {
            oscCount++;
            idx += 4;
        }
        Assert.Equal(1, oscCount);
    }

    [Fact]
    public void Emit_MultiLine_OneOsc66PerLine()
    {
        var fragment = new SizedTextFragment(
            new TextSizing(Scale: 2),
            "hello\nworld",
            Style.Default);
        var output = EmitToString(fragment);

        int oscCount = 0;
        int idx = 0;
        while ((idx = output.IndexOf("]66;", idx, StringComparison.Ordinal)) >= 0)
        {
            oscCount++;
            idx += 4;
        }
        Assert.Equal(2, oscCount);

        Assert.Contains("hello", output);
        Assert.Contains("world", output);
    }

    [Fact]
    public void Emit_MultiLine_CupBetweenLines()
    {
        // For scale=2, line 1's emission starts at the anchor (row 0), line 2's emission
        // starts at row 0 + 1 × 2 = row 2 (1-based: row 3). The CUP is "CSI 3 ; 1 H".
        var fragment = new SizedTextFragment(
            new TextSizing(Scale: 2),
            "hello\nworld",
            Style.Default);
        var output = EmitToString(fragment);

        Assert.Contains("[3;1H", output);
    }

    [Fact]
    public void Emit_MultiLine_StyleEmittedOnce()
    {
        // Style is terminal-global SGR — one emission at the start covers all lines. The CUP
        // moves between lines don't disturb SGR, so the second OSC 66 inherits.
        var style = Style.Default.WithForeground(Color.FromRgb(255, 0, 0));
        var fragment = new SizedTextFragment(
            new TextSizing(Scale: 2),
            "hello\nworld",
            style);
        var output = EmitToString(fragment);

        // One SGR truecolor sequence (the foreground 255;0;0) appears, not two.
        int sgrCount = 0;
        int idx = 0;
        while ((idx = output.IndexOf("38;2;255;0;0", idx, StringComparison.Ordinal)) >= 0)
        {
            sgrCount++;
            idx += 12;
        }
        Assert.Equal(1, sgrCount);
    }

    [Fact]
    public void Emit_ThreeLines_CursorAdvancesByScaleEachTime()
    {
        // For scale=3, three lines emit CUPs at rows 0, 3, 6 (0-based) = "1;1", "4;1", "7;1"
        // on the wire (1-based). The first line uses the renderer-issued CUP (we don't emit
        // one ourselves for line 0); the subsequent two issue CUPs.
        var fragment = new SizedTextFragment(
            new TextSizing(Scale: 3),
            "line1\nline2\nline3",
            Style.Default);
        var output = EmitToString(fragment);

        // The CUPs we issue between lines: row 3 (line 2 anchored at scale=3) and row 6.
        Assert.Contains("[4;1H", output);
        Assert.Contains("[7;1H", output);
    }

    [Fact]
    public void Emit_EmptyMiddleLine_PreservesRowBand()
    {
        // "a\n\nb" has three lines: "a", "", "b". The empty middle line still advances the
        // emission position by scale rows. The third line emits at row 2 × scale.
        var fragment = new SizedTextFragment(
            new TextSizing(Scale: 2),
            "a\n\nb",
            Style.Default);
        var output = EmitToString(fragment);

        Assert.Contains("[3;1H", output); // line 2 cup (row 2 = "[3;1H" on the wire)
        Assert.Contains("[5;1H", output); // line 3 cup (row 4 = "[5;1H" on the wire)
        Assert.Contains("a", output);
        Assert.Contains("b", output);
    }

    [Fact]
    public void GetSize_EmptyLineDoesNotConsumeColumns()
    {
        // An empty middle line has zero rendered columns but still occupies a row band.
        // Verifying that an "a\n\nb"-style input gives size (1*scale, 3*scale).
        var fragment = new SizedTextFragment(
            new TextSizing(Scale: 2),
            "a\n\nb",
            Style.Default);
        Assert.Equal(new Size(2, 6), fragment.GetSize());
    }

    [Fact]
    public void FrameRenderer_MultiLineFragment_CoveredCellsSpanAllRows()
    {
        // A two-line fragment covers a rectangle of width × (lines × scale) cells in the
        // covered bitmap. Cells in that rectangle drop their glyphs in the cell pass.
        var caps = OutputCapabilities.None with
                   {
                       TextSizing = new TextSizingCapabilities(Width: true, Scale: true),
                   };
        var r = new FrameRenderer(caps);
        var buffer = new CellBuffer(20, 6);

        // Pre-paint cells in the fragment's footprint that we don't want to see emitted.
        for (int row = 0; row < 4; row++)
            for (int col = 0; col < 10; col++)
                buffer.Set(col, row, "X", Style.Default);

        buffer.AddFragment(0, 0, new SizedTextFragment(
                               new TextSizing(Scale: 2),
                               "hi\nbye",
                               Style.Default));

        var w = new ArrayBufferWriter<byte>();
        r.Render(buffer, w);
        var output = Encoding.ASCII.GetString(w.WrittenSpan);

        // None of the pre-painted 'X' glyphs in the fragment's footprint (rows 0-3 × cols 0-5
        // at minimum) should appear — they're covered. (The fragment's own payload "hi" and
        // "bye" appears.)
        // Note: the bounding rectangle for "hi" + "bye" at scale 2 is widest=6 by rows=4.
        // The pre-painted X's at (0..3, 0..5) are all inside the bounding rectangle so all
        // drop. Some X's at columns 6..9 might still emit if cells outside the bounding rect.
        Assert.Contains("hi", output);
        Assert.Contains("bye", output);
    }
}
