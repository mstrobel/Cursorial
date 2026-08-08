using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fragments;
using Cursorial.Terminal;

namespace Cursorial.Tests.Rendering;

/// <summary>
/// A cell whose color still equals the color the terminal reported as its own default is emitted as
/// the default-color SGR (39 / 49) rather than an explicit color.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CellBuffer"/> promotes the terminal's reported default fg/bg to a concrete color so blank
/// cells can participate in alpha blending, and the frame compositor bases itself on that blank — so
/// every <em>unpainted</em> cell now carries a real color rather than <see cref="Color.Default"/>. Emitting
/// it explicitly is both longer and, at a reduced color depth, wrong-ish: the value is quantized to the
/// nearest palette entry, an approximation of a color the terminal would have reproduced <em>exactly</em>
/// given SGR 49.
/// </para>
/// <para>
/// The substitution keys off the color the <em>terminal</em> reported (that is what SGR 39 / 49 resolve
/// against), and is decided on the color <em>before</em> capability quantization — so it fires only for a
/// genuinely-equal color, never for a different color that happens to quantize to the same palette entry.
/// </para>
/// </remarks>
public class FrameRendererDefaultColorTests
{
    private static readonly Color DefaultFg = Color.FromRgb(205, 214, 244);
    private static readonly Color DefaultBg = Color.FromRgb(30, 30, 46);

    private static TerminalCapabilities Caps(ColorDepth depth = ColorDepth.Truecolor,
                                             Color? defaultForeground = null,
                                             Color? defaultBackground = null) =>
        TerminalCapabilities.None with
        {
            Output = OutputCapabilities.None with
                     {
                         Color = ColorCapabilities.None with
                                 {
                                     Depth = depth,
                                     DefaultColorReset = true,
                                     DefaultForeground = defaultForeground ?? DefaultFg,
                                     DefaultBackground = defaultBackground ?? DefaultBg
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

    /// <summary>The parameter list of every SGR sequence in <paramref name="output"/>, in emission order.</summary>
    private static string[] Sgr(string output) =>
        Regex.Matches(output, "\x1b\\[([0-9;:]*)m").Select(m => m.Groups[1].Value).ToArray();

    /// <summary>
    /// The bytes of the one fragment emission in <paramref name="output"/> — everything the renderer
    /// brackets between DECSC (<c>ESC 7</c>) and DECRC (<c>ESC 8</c>). Only fragments save the cursor,
    /// so this isolates the fragment's backdrop SGR from the cell pass that precedes it.
    /// </summary>
    private static string FragmentEmission(string output)
    {
        // Composed rather than written as an escape: C#'s \x is variable-length and would swallow the
        // trailing digit of "ESC 7" into the character code.
        string decsc = (char) 0x1B + "7";
        string decrc = (char) 0x1B + "8";

        int start = output.IndexOf(decsc, StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected a bracketed fragment emission (DECSC) in the output.");
        int end = output.IndexOf(decrc, start, StringComparison.Ordinal);
        Assert.True(end > start, "Expected the fragment emission to be closed by DECRC.");
        return output[start..end];
    }

    // ---- Exactness: an unpainted cell emits the default code, not an explicit color ----------

    [Theory]
    [InlineData(ColorDepth.Truecolor)]
    [InlineData(ColorDepth.Ansi256)]
    public void UnpaintedFrame_EmitsNoColorSgrAtAll(ColorDepth depth)
    {
        // Every cell of a freshly-constructed buffer carries the derived default. After the full
        // redraw's SGR 0 the terminal already IS at its default fg/bg, so the correct emission for the
        // whole frame is no color parameter whatsoever.
        var caps = Caps(depth);
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(6, 2, caps);

        var output = Render(renderer, buffer);

        Assert.DoesNotContain("38;", output);
        Assert.DoesNotContain("48;", output);
        Assert.Equal(["0", "0"], Sgr(output));   // the full-redraw reset and the end-of-frame reset
    }

    [Fact]
    public void Truecolor_ReturnToTheBlank_EmitsThe39And49Codes()
    {
        var caps = Caps();
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(3, 1, caps);
        buffer.Set(1, 0, "x", CellStyle.Default
                                       .WithForeground(Color.FromRgb(255, 0, 0))
                                       .WithBackground(Color.FromRgb(0, 0, 255)));

        var output = Render(renderer, buffer);

        Assert.Equal(["0", "38;2;255;0;0;48;2;0;0;255", "39;49", "0"], Sgr(output));
        Assert.DoesNotContain("48;2;30;30;46", output);      // never the derived default, explicitly
        Assert.DoesNotContain("38;2;205;214;244", output);
    }

    [Fact]
    public void Ansi256_ReturnToTheBlank_EmitsThe39And49Codes_NotTheQuantizedApproximation()
    {
        // The accuracy win: at 256 colors the derived default would be emitted as the NEAREST palette
        // entry — an approximation of a color SGR 49 reproduces exactly.
        var caps = Caps(ColorDepth.Ansi256);
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(3, 1, caps);
        buffer.Set(1, 0, "x", CellStyle.Default
                                       .WithForeground(Color.FromRgb(255, 0, 0))
                                       .WithBackground(Color.FromRgb(0, 0, 255)));

        var output = Render(renderer, buffer);

        byte quantizedDefaultBg = StyleQuantizer.NearestPaletteIndex(30, 30, 46);
        byte quantizedDefaultFg = StyleQuantizer.NearestPaletteIndex(205, 214, 244);

        Assert.DoesNotContain($"48;5;{quantizedDefaultBg}", output);
        Assert.DoesNotContain($"38;5;{quantizedDefaultFg}", output);
        Assert.Contains("39;49", output);
    }

    // ---- Per-channel: 39 and 49 clear exactly what they name --------------------------------

    [Fact]
    public void OnlyTheChannelThatMatchesTheTerminalDefault_IsSubstituted()
    {
        var caps = Caps();
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(3, 1, caps);

        buffer.Set(0, 0, "a", CellStyle.Default
                                       .WithForeground(Color.FromRgb(255, 0, 0))
                                       .WithBackground(Color.FromRgb(0, 0, 255)));
        // Foreground is the terminal's default; background is not.
        buffer.Set(1, 0, "b", CellStyle.Default
                                       .WithForeground(DefaultFg)
                                       .WithBackground(Color.FromRgb(0, 128, 0)));

        var output = Render(renderer, buffer);

        Assert.Equal(["0",
                      "38;2;255;0;0;48;2;0;0;255",
                      "39;48;2;0;128;0",   // fg → default code, bg still explicit
                      "49",                // and back to the blank — the fg never left the default
                      "0"],
                     Sgr(output));
    }

    // ---- State tracking: _currentStyle must record what was EMITTED --------------------------

    [Fact]
    public void DefaultThenExplicitThenDefault_EmitsCorrectDeltasThroughout()
    {
        var caps = Caps();
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(4, 1, caps);

        // cell 0: the blank. cell 1: explicit. cell 2: the blank again. cell 3: explicit again.
        buffer.Set(1, 0, "x", CellStyle.Default.WithBackground(Color.FromRgb(0, 0, 255)));
        buffer.Set(3, 0, "y", CellStyle.Default.WithBackground(Color.FromRgb(0, 0, 255)));

        var output = Render(renderer, buffer);

        Assert.Equal(["0",
                      "48;2;0;0;255",   // blank → explicit
                      "49",             // explicit → blank (fg never left the default)
                      "48;2;0;0;255",   // blank → explicit again
                      "0"],
                     Sgr(output));
    }

    [Fact]
    public void AColorThatMerelyQuantizesOntoTheDefault_StillEmitsItsOwnExplicitColor()
    {
        // The _currentStyle trap, made observable. Recording the REQUESTED style rather than the
        // EMITTED one leaves the tracked state holding the quantized default (palette 16) while the
        // terminal is actually sitting on SGR 49 — so this cell, whose own color quantizes onto the
        // same palette entry, compares equal to the tracked state and emits nothing. The terminal
        // then paints its true default here instead of the palette entry the cell asked for.
        var caps = Caps(ColorDepth.Ansi256,
                        defaultForeground: Color.FromRgb(255, 255, 255),
                        defaultBackground: Color.FromRgb(0, 0, 0));
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(3, 1, caps);

        // #010101 is NOT the terminal default (#000000) but quantizes to the same palette entry.
        Assert.Equal(StyleQuantizer.NearestPaletteIndex(0, 0, 0), StyleQuantizer.NearestPaletteIndex(1, 1, 1));

        buffer.Set(1, 0, "x", CellStyle.Default
                                       .WithForeground(Color.FromRgb(255, 255, 255))
                                       .WithBackground(Color.FromRgb(1, 1, 1)));

        var output = Render(renderer, buffer);
        byte index = StyleQuantizer.NearestPaletteIndex(1, 1, 1);

        Assert.Equal(["0", $"48;5;{index}", "49", "0"], Sgr(output));
    }

    // ---- The substitution keys off the TERMINAL's default, not the buffer's declared blank ----

    [Fact]
    public void ABufferWhoseBlankIsNotTheTerminalDefault_StillEmitsItsBlankExplicitly()
    {
        // A buffer may declare any blank it likes (the constructor argument). Only the terminal's own
        // reported default is reproducible by SGR 49 — anything else has to go out explicitly, or the
        // terminal paints the wrong color.
        var caps = Caps();
        var renderer = new FrameRenderer(caps.Output);
        var themed = CellStyle.Default.WithBackground(Color.FromRgb(0, 0, 128));
        var buffer = new CellBuffer(3, 1, caps, defaultStyle: themed);

        buffer.Set(1, 0, "x", CellStyle.Default.WithBackground(Color.FromRgb(255, 0, 0)));

        var output = Render(renderer, buffer);

        Assert.Contains("48;2;0;0;128", output);
        Assert.DoesNotContain("49m", output);
        Assert.DoesNotContain(";49", output);
    }

    [Fact]
    public void NoReportedDefaults_EmissionIsUnchanged()
    {
        var caps = TerminalCapabilities.None with
                   {
                       Output = OutputCapabilities.None with
                                {
                                    Color = ColorCapabilities.None with { Depth = ColorDepth.Truecolor }
                                }
                   };
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(3, 1, caps);
        buffer.Set(1, 0, "x", CellStyle.Default.WithBackground(Color.FromRgb(255, 0, 0)));

        var output = Render(renderer, buffer);

        // The trailing blank is Cell.Blank — identical to the freshly-allocated front buffer, so the
        // full redraw's clear already covers it and the diff emits nothing for it at all.
        Assert.Equal(["0", "48;2;255;0;0", "0"], Sgr(output));
    }

    [Fact]
    public void ATransparentReportedDefault_IsNotSubstituted()
    {
        // SgrEncoder.WriteDelta deliberately emits NOTHING for a transparent background — there is no
        // code for "leave the backdrop alone". Treating a transparent report as a substitutable default
        // would start emitting 49 (paint the terminal's default) exactly where the encoder's contract
        // says to emit nothing.
        var caps = Caps(defaultBackground: Color.Transparent);
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(3, 1, caps);
        buffer.Set(1, 0, "x", CellStyle.Default.WithBackground(Color.FromRgb(255, 0, 0)));

        var output = Render(renderer, buffer);

        Assert.Equal(["0", "48;2;255;0;0", "0"], Sgr(output));   // no 49 anywhere
    }

    // ---- No visual regression: the composited frame is untouched, the diff still converges ----

    [Fact]
    public void TheCompositedFrameIsUnchanged_AndAStableFrameStillEmitsNoCellContent()
    {
        var caps = Caps(ColorDepth.Ansi256);
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(4, 2, caps);
        buffer.Set(1, 0, "x", CellStyle.Default.WithBackground(Color.FromRgb(255, 0, 0)));

        var before = new Cell[buffer.CellCount];
        for (int row = 0; row < buffer.Rows; row++)
        for (int col = 0; col < buffer.Columns; col++)
            before[row * buffer.Columns + col] = buffer[col, row];

        Render(renderer, buffer);

        for (int row = 0; row < buffer.Rows; row++)
        for (int col = 0; col < buffer.Columns; col++)
            Assert.Equal(before[row * buffer.Columns + col], buffer[col, row]);

        // The unpainted cells still carry the derived default — the substitution is an emission-time
        // concern only, and the second frame of an unchanged buffer re-emits nothing.
        Assert.Equal(buffer.DefaultStyle, buffer[0, 0].Style);
        Assert.DoesNotContain("x", Render(renderer, buffer));
    }

    // ---- Fragment backdrops speak the same language as the cells around them -----------------

    [Fact]
    public void Ansi256_FragmentOverUnpaintedBackground_BackdropAgreesWithTheSurroundingCells()
    {
        // The seam. A fragment's backdrop is the anchor cell's style written ABSOLUTELY (SGR 0 plus
        // parameters), because the fragment payload must land in a known state. Handing that write the
        // raw quantized default paints the nearest palette entry behind the fragment while every
        // surrounding cell — which goes through the substitution — shows the terminal's true default.
        var caps = Caps(ColorDepth.Ansi256);
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(4, 1, caps);
        buffer.AddFragment(1, 0, new SentinelFragment(new Size(1, 1), "[F]"));

        var output = Render(renderer, buffer);

        byte quantizedDefaultBg = StyleQuantizer.NearestPaletteIndex(30, 30, 46);
        byte quantizedDefaultFg = StyleQuantizer.NearestPaletteIndex(205, 214, 244);

        // After SGR 0 the terminal already IS at its default fg/bg — the same reason the surrounding
        // cells emit no color parameter at all — so the backdrop is the bare reset and nothing more.
        Assert.Equal(["0"], Sgr(FragmentEmission(output)));
        Assert.DoesNotContain($"48;5;{quantizedDefaultBg}", output);
        Assert.DoesNotContain($"38;5;{quantizedDefaultFg}", output);
    }

    [Fact]
    public void Truecolor_FragmentOverUnpaintedBackground_BackdropAgreesWithTheSurroundingCells()
    {
        // At full depth the derived default was already exact, so this case was never a VISIBLE seam —
        // the explicit rgb(30,30,46) and the terminal's own default paint the same pixels. It is here to
        // pin that the substitution stays a no-op for what already worked: the fragment's backdrop must
        // resolve to the same color as its surroundings, and the surroundings emit no color at all.
        var caps = Caps();
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(4, 1, caps);
        buffer.AddFragment(1, 0, new SentinelFragment(new Size(1, 1), "[F]"));

        var output = Render(renderer, buffer);

        Assert.Equal(["0"], Sgr(FragmentEmission(output)));
        Assert.DoesNotContain("48;2;30;30;46", output);
        Assert.DoesNotContain("38;2;205;214;244", output);
    }

    [Fact]
    public void AFragmentOverAPaintedBackground_StillEmitsThatBackgroundExplicitly()
    {
        // The substitution must fire only for the terminal's own default. A fragment anchored over a
        // genuinely painted cell still needs that color named, or the payload lands on the terminal's
        // default and the panel behind it reads as a hole.
        var caps = Caps(ColorDepth.Ansi256);
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(4, 1, caps);
        buffer.Set(1, 0, " ", CellStyle.Default.WithBackground(Color.FromRgb(255, 0, 0)));
        buffer.AddFragment(1, 0, new SentinelFragment(new Size(1, 1), "[F]"));

        var output = Render(renderer, buffer);

        byte panel = StyleQuantizer.NearestPaletteIndex(255, 0, 0);

        Assert.Equal([$"0;48;5;{panel}"], Sgr(FragmentEmission(output)));
    }

    private sealed class SentinelFragment(Size size, string sentinel) : IBufferFragment
    {
        public FragmentLayer Layer => FragmentLayer.Cells;
        public Size GetSize() => size;
        public bool IsSupported(OutputCapabilities capabilities) => true;

        public void Emit(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities)
        {
            var bytes = Encoding.UTF8.GetBytes(sentinel);
            var dest = output.GetSpan(bytes.Length);
            bytes.CopyTo(dest);
            output.Advance(bytes.Length);
        }
    }
}
