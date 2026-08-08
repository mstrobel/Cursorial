using System.Buffers;
using System.Text;

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Terminal;

namespace Cursorial.Tests.Rendering;

/// <summary>
/// The front buffer stores the form the renderer <em>emits</em>, not the form the caller asked for —
/// so an unpainted cell, whose colours are the terminal's own default promoted to RGB by
/// <see cref="CellBuffer.DefaultStyle"/>, normalises to <c>default(Cell)</c> and drops out of the frame
/// diff entirely. On a full redraw of a mostly-empty screen that is most of the frame: no cursor move,
/// no space, nothing.
/// </summary>
/// <remarks>
/// <para>
/// What licenses that is the full redraw's emission order. <c>ED</c> (<c>CSI 2 J</c>) erases to the
/// terminal's <em>current</em> background — the same rule the renderer's end-of-frame reset exists to
/// contain — so the <c>SGR 0</c> has to go out FIRST. Clear before reset and the screen is filled with
/// whatever SGR state was inherited, while a freshly-zeroed front buffer claims it holds the terminal's
/// default; those cells would then never be repainted. <see cref="ClearedUnderAnInheritedBackground_LeavesTheScreenAtTheTerminalDefault"/>
/// is that hazard, run through a screen model rather than asserted as a byte order.
/// </para>
/// </remarks>
public class FrameRendererBlankDiffTests
{
    private static readonly Color DefaultFg = Color.FromRgb(205, 214, 244);
    private static readonly Color DefaultBg = Color.FromRgb(30, 30, 46);

    private static TerminalCapabilities Caps(ColorDepth depth = ColorDepth.Truecolor,
                                             bool reportDefaults = true) =>
        TerminalCapabilities.None with
        {
            Output = OutputCapabilities.None with
                     {
                         Color = ColorCapabilities.None with
                                 {
                                     Depth = depth,
                                     DefaultColorReset = true,
                                     DefaultForeground = reportDefaults ? DefaultFg : null,
                                     DefaultBackground = reportDefaults ? DefaultBg : null
                                 },
                         Styling = new TextStylingCapabilities(
                             Italic: true, Underline: true, ExtendedUnderline: true,
                             ColoredUnderline: true, Strikethrough: true, Overline: true, Hyperlinks: false)
                     }
        };

    private static string Render(FrameRenderer renderer, CellBuffer back)
    {
        var writer = new ArrayBufferWriter<byte>();
        renderer.Render(back, writer);
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    // ---- Part 1: the erase paints the CURRENT background, so the reset has to precede it ---------

    /// <summary>
    /// The screen the user is left looking at, under a shell that handed us a non-default background.
    /// Both variants matter: with reported defaults the blank cells normalise away (part 2), and
    /// WITHOUT them a <see cref="CellBuffer"/>'s blank is already <c>default(Cell)</c> — so the
    /// diff has always skipped them and the mis-ordering was already a live bug.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClearedUnderAnInheritedBackground_LeavesTheScreenAtTheTerminalDefault(bool reportDefaults)
    {
        var caps = Caps(reportDefaults: reportDefaults);
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(10, 3, caps);
        buffer.Set(4, 1, "x", CellStyle.Default.WithBackground(Color.FromRgb(255, 0, 0)));

        // The shell left an explicit background standing. Every cell the frame does not write keeps
        // whatever the erase put there.
        var screen = new ScreenModel(10, 3, inherited: "shell-green");
        screen.Feed(Render(renderer, buffer));

        Assert.Equal("48;2;255;0;0", screen[4, 1]);
        foreach (var (column, row) in screen.Positions)
        {
            if ((column, row) == (4, 1)) continue;
            Assert.Equal(ScreenModel.TerminalDefault, screen[column, row]);
        }
    }

    [Fact]
    public void TheFullRedrawResetPrecedesTheErase()
    {
        // The byte-order statement behind the screen model above, pinned directly so a reorder is
        // impossible to land by accident.
        var caps = Caps();
        var output = Render(new FrameRenderer(caps.Output), new CellBuffer(4, 2, caps));

        int reset = output.IndexOf("\x1b[0m", StringComparison.Ordinal);
        int erase = output.IndexOf("\x1b[2J", StringComparison.Ordinal);

        Assert.True(reset >= 0 && erase >= 0, $"Expected both SGR 0 and CSI 2 J in {Escape(output)}.");
        Assert.True(reset < erase, $"SGR 0 must precede CSI 2 J; got {Escape(output)}.");
    }

    // ---- Part 2: unpainted cells cost nothing ---------------------------------------------------

    [Theory]
    [InlineData(ColorDepth.Truecolor)]
    [InlineData(ColorDepth.Ansi256)]
    public void UnpaintedCells_EmitNothingAtAll(ColorDepth depth)
    {
        // One painted cell on an 80x24 screen. Every other cell is the terminal's own default, which
        // is what the leading SGR 0 + CSI 2 J just put there — so the frame carries exactly one
        // glyph and not one space.
        var caps = Caps(depth);
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(80, 24, caps);
        buffer.Set(5, 3, "x", CellStyle.Default.WithBackground(Color.FromRgb(255, 0, 0)));

        var output = Render(renderer, buffer);

        Assert.DoesNotContain(" ", output);                       // not one blank cell was painted
        Assert.Equal(1, output.Count(c => c == 'x'));
        Assert.True(output.Length < 64, $"Expected a tiny frame, got {output.Length} bytes: {Escape(output)}");
    }

    [Fact]
    public void AWhollyUnpaintedFullRedraw_EmitsNoCellContentWhatsoever()
    {
        var caps = Caps();
        var output = Render(new FrameRenderer(caps.Output), new CellBuffer(80, 24, caps));

        // Autowrap off, reset, erase, home, end-of-frame reset, and the cursor. No cell pass at all.
        Assert.Equal("\x1b[?7l\x1b[0m\x1b[2J\x1b[1;1H\x1b[?25h\x1b[0m", output);
    }

    [Fact]
    public void AnUnchangedBlankRegion_StaysOutOfTheDiffOnLaterFramesToo()
    {
        var caps = Caps();
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(20, 4, caps);
        buffer.Set(2, 1, "x", CellStyle.Default.WithBackground(Color.FromRgb(255, 0, 0)));
        Render(renderer, buffer);

        buffer.Set(2, 1, "y", CellStyle.Default.WithBackground(Color.FromRgb(255, 0, 0)));
        var output = Render(renderer, buffer);

        Assert.DoesNotContain(" ", output);
        Assert.Contains("y", output);
    }

    // ---- The front buffer's new meaning survives every full-repaint trigger ----------------------

    [Fact]
    public void AResize_RepaintsFully()
    {
        var caps = Caps();
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(20, 4, caps);
        var painted = CellStyle.Default.WithBackground(Color.FromRgb(255, 0, 0));
        buffer.Set(2, 1, "x", painted);
        Render(renderer, buffer);

        buffer.Resize(24, 5);
        buffer.Set(2, 1, "x", painted);
        var output = Render(renderer, buffer);

        // A new front buffer, a fresh erase, and the painted cell back on the wire — the blanks stay
        // silent because the erase they are describing has just been re-issued.
        Assert.Contains("\x1b[2J", output);
        Assert.True(output.IndexOf("\x1b[0m", StringComparison.Ordinal) <
                    output.IndexOf("\x1b[2J", StringComparison.Ordinal));
        Assert.Contains("48;2;255;0;0", output);
        Assert.Contains("x", output);
        Assert.DoesNotContain(" ", output);
    }

    [Fact]
    public void ACapabilityRenegotiation_RepaintsFullyAgainstTheNewDefaults()
    {
        // A renegotiation builds a fresh CellBuffer AND a fresh FrameRenderer (UIApplication.FrameLoop),
        // so the front buffer never outlives the reported defaults that gave its zeros their meaning.
        // The colour that WAS the terminal's default has to come back explicitly under the new one.
        var oldCaps = Caps();
        var buffer = new CellBuffer(6, 2, oldCaps);
        buffer.Set(1, 0, "x", CellStyle.Default.WithBackground(DefaultBg));   // the old default, painted

        var before = Render(new FrameRenderer(oldCaps.Output), buffer);
        Assert.DoesNotContain("48;2;30;30;46", before);                       // normalised away

        var newBg = Color.FromRgb(250, 240, 230);
        var newCaps = TerminalCapabilities.None with
                      {
                          Output = oldCaps.Output with
                                   {
                                       Color = oldCaps.Output.Color with { DefaultBackground = newBg }
                                   }
                      };

        var reborn = new CellBuffer(6, 2, newCaps);
        reborn.Set(1, 0, "x", CellStyle.Default.WithBackground(DefaultBg));   // same colour, no longer default

        var after = Render(new FrameRenderer(newCaps.Output), reborn);

        Assert.Contains("\x1b[2J", after);
        Assert.Contains("48;2;30;30;46", after);                              // now an ordinary colour
        Assert.DoesNotContain("48;2;250;240;230", after);                     // and the NEW default is implicit
    }

    [Fact]
    public void AForcedFullRedraw_RepaintsTheScreenItJustCleared()
    {
        // Reset() drops the front buffer, so the next frame re-issues the erase. The blanks it does
        // not repaint are correct precisely because that erase ran under a reset SGR state.
        var caps = Caps();
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(12, 3, caps);
        buffer.Set(3, 1, "x", CellStyle.Default.WithBackground(Color.FromRgb(255, 0, 0)));
        Render(renderer, buffer);

        renderer.Reset();
        var screen = new ScreenModel(12, 3, inherited: "shell-green");
        screen.Feed(Render(renderer, buffer));

        Assert.Equal("48;2;255;0;0", screen[3, 1]);
        Assert.Equal(ScreenModel.TerminalDefault, screen[0, 0]);
        Assert.Equal(ScreenModel.TerminalDefault, screen[11, 2]);
    }

    // ---- The guard: nothing changed for cells that are actually painted -------------------------

    [Theory]
    [InlineData(ColorDepth.Truecolor)]
    [InlineData(ColorDepth.Ansi256)]
    public void ADenselyPaintedFrame_IsUnchangedApartFromTheResetEraseSwap(ColorDepth depth)
    {
        // Every cell explicitly coloured — nothing to normalise, so the whole cell pass must come out
        // byte-for-byte as it did before the front buffer changed meaning. The ONLY licensed
        // difference is the leading "SGR 0 then CSI 2 J" swap, which this test undoes before
        // comparing so a regression in the cell pass cannot hide behind it.
        var caps = Caps(depth);
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(40, 12, caps);

        Color[] palette =
        [
            Color.FromRgb(220, 40, 40), Color.FromRgb(40, 200, 60), Color.FromRgb(40, 80, 220),
            Color.FromRgb(230, 200, 40), Color.FromRgb(190, 60, 200), Color.FromRgb(40, 200, 210),
            Color.FromRgb(240, 130, 30), Color.FromRgb(120, 120, 120)
        ];

        for (int r = 0; r < buffer.Rows; r++)
        for (int c = 0; c < buffer.Columns; c++)
            buffer.Set(c, r, ((char) ('a' + (c + r) % 26)).ToString(),
                       CellStyle.Default
                                .WithForeground(palette[(c + r) % palette.Length])
                                .WithBackground(palette[(c + 3 * r) % palette.Length]));

        var output = Render(renderer, buffer);

        Assert.StartsWith("\x1b[?7l\x1b[0m\x1b[2J", output, StringComparison.Ordinal);
        var asEmitted = "\x1b[?7l\x1b[2J\x1b[0m" + output["\x1b[?7l\x1b[0m\x1b[2J".Length..];

        Assert.Equal(depth == ColorDepth.Truecolor ? 16_664 : 9_824, asEmitted.Length);
        Assert.Equal(depth == ColorDepth.Truecolor ? "541E5917962A9578" : "BED92CEEC523B571", Hash(asEmitted));
    }

    private static string Hash(string output) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(output)))[..16];

    private static string Escape(string output) => output.Replace("\x1b", "ESC");

    /// <summary>
    /// The smallest terminal that can hold the claim under test: which background colour is standing in
    /// each cell. <c>ED</c> and every printed glyph paint the CURRENT background, which is the whole
    /// point — a model that ignored that could not tell the two emission orders apart.
    /// </summary>
    private sealed class ScreenModel
    {
        /// <summary>The colour <c>SGR 0</c> / <c>SGR 49</c> select: whatever the terminal itself uses.</summary>
        public const string TerminalDefault = "<terminal default>";

        private readonly string[] _cells;
        private readonly int _columns;
        private readonly int _rows;
        private string _background;
        private int _column;
        private int _row;

        public ScreenModel(int columns, int rows, string inherited)
        {
            _columns = columns;
            _rows = rows;
            _cells = new string[columns * rows];
            Array.Fill(_cells, inherited);
            _background = inherited;
        }

        public string this[int column, int row] => _cells[row * _columns + column];

        public IEnumerable<(int Column, int Row)> Positions
        {
            get
            {
                for (int r = 0; r < _rows; r++)
                for (int c = 0; c < _columns; c++)
                    yield return (c, r);
            }
        }

        public void Feed(string output)
        {
            for (int i = 0; i < output.Length; i++)
            {
                char ch = output[i];

                if (ch == '\x1b')
                {
                    i = Control(output, i);
                    continue;
                }

                if (ch < ' ') continue;

                if (_row < _rows && _column < _columns)
                    _cells[_row * _columns + _column] = _background;

                _column++;
            }
        }

        /// <summary>Consume the escape sequence starting at <paramref name="start"/>; returns its last index.</summary>
        private int Control(string output, int start)
        {
            if (start + 1 >= output.Length) return start;

            if (output[start + 1] != '[')
                return start + 1;                       // ESC 7 / ESC 8 / … — no cell or colour effect here

            int i = start + 2;
            while (i < output.Length && output[i] is not (>= '@' and <= '~')) i++;
            if (i >= output.Length) return output.Length - 1;

            string parameters = output[(start + 2)..i];
            switch (output[i])
            {
                case 'm':
                    ApplySgr(parameters);
                    break;

                case 'J' when parameters == "2":
                    Array.Fill(_cells, _background);    // ED erases to the CURRENT background
                    break;

                case 'H':
                    var at = parameters.Split(';');
                    _row = at.Length > 0 && int.TryParse(at[0], out int r) ? r - 1 : 0;
                    _column = at.Length > 1 && int.TryParse(at[1], out int c) ? c - 1 : 0;
                    break;
            }

            return i;
        }

        private void ApplySgr(string parameters)
        {
            var fields = parameters.Length == 0 ? ["0"] : parameters.Split(';');

            for (int i = 0; i < fields.Length; i++)
            {
                switch (fields[i])
                {
                    case "0" or "49":
                        _background = TerminalDefault;
                        break;

                    case "48" when i + 4 < fields.Length && fields[i + 1] == "2":
                        _background = $"48;2;{fields[i + 2]};{fields[i + 3]};{fields[i + 4]}";
                        i += 4;
                        break;

                    case "48" when i + 2 < fields.Length && fields[i + 1] == "5":
                        _background = $"48;5;{fields[i + 2]}";
                        i += 2;
                        break;
                }
            }
        }
    }
}
