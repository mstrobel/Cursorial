using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fragments;
using Cursorial.Terminal;
using Cursorial.Text;

namespace Cursorial.Tests.Rendering;

/// <summary>
/// A fragment's anchor style is the only place transparency can be resolved for the fragment path, and
/// the three colour channels do not resolve alike: background inherits the anchor cell's, foreground and
/// underline colour reset to <see cref="Color.Default"/>. See <c>FrameRenderer.EmitFragmentBytes</c>.
/// </summary>
/// <remarks>
/// <para>
/// Cell painting composites transparency away against the back buffer, so by emission time a cell holds
/// resolved colours. A fragment is never painted into cells — it gets an absolute SGR over a region whose
/// cells the renderer does not own — so whatever alpha survives to emission goes on the wire.
/// </para>
/// <para>
/// The anchor cell is <em>painted</em> in every case here, deliberately. Over an unpainted anchor the
/// backdrop is the terminal's own default and a foreground reset is indistinguishable from inheriting it,
/// so the substitution's whole outcome is invisible; the text-pipeline characterisation corpus paints onto
/// a blank surface and cannot express that, which is why these live as targeted tests rather than corpus
/// cases.
/// </para>
/// </remarks>
public class FrameRendererFragmentTransparencyTests
{
    /// <summary>
    /// Capabilities that report <em>no</em> default fg/bg. That keeps <c>CellBuffer.DefaultStyle</c> from
    /// promoting a concrete colour onto unpainted cells, so the only colour in play is the one the test
    /// paints — the default-colour substitution is a separate rule with its own tests
    /// (<see cref="FrameRendererDefaultColorTests"/>) and is deliberately out of the frame here.
    /// </summary>
    private static TerminalCapabilities Caps(ColorDepth depth = ColorDepth.Truecolor) =>
        TerminalCapabilities.None with
        {
            Output = OutputCapabilities.None with
                     {
                         Color = ColorCapabilities.None with
                                 {
                                     Depth = depth,
                                     DefaultColorReset = true,
                                     DefaultForeground = Color.Default,
                                     DefaultBackground = Color.Default
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

    /// <summary>The one bracketed fragment emission — its backdrop SGR, its anchor-style delta, its payload.</summary>
    private static string FragmentEmission(string output) => FragmentEmissionSlicer.Single(output);

    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);

    /// <summary>Red as this depth spells it, for the backdrop's absolute SGR.</summary>
    private static string RedForeground(ColorDepth depth) =>
        depth == ColorDepth.Truecolor ? "38;2;255;0;0" : $"38;5;{StyleQuantizer.NearestPaletteIndex(255, 0, 0)}";

    // ---- Foreground: reset, never inherited ---------------------------------------------------

    [Theory]
    [InlineData(ColorDepth.Truecolor)]
    [InlineData(ColorDepth.Ansi256)]
    public void ATransparentForeground_ResetsTheInkRatherThanInheritingTheAnchorCells(ColorDepth depth)
    {
        // The whole substituted style lands on CellStyle.Default here: the declared background is already
        // Color.Default, so nothing fills it in, and the transparent foreground resolves to Color.Default
        // too. That collision is the case — a guard asking whether the SUBSTITUTED style is Default would
        // skip the delta, leave the terminal in the backdrop the absolute SGR just wrote, and paint the
        // fragment in the anchor cell's RED: the inheritance the foreground rule exists to refuse.
        var caps = Caps(depth);
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(4, 1, caps);
        buffer.Set(1, 0, " ", CellStyle.Default.WithForeground(Red));
        buffer.AddFragment(1, 0, new SentinelFragment(), CellStyle.Default.WithForeground(Color.Transparent));

        var output = Render(renderer, buffer);

        // Backdrop (the anchor cell, absolute), then the anchor style's delta off it: a bare foreground
        // reset. Both tiers say the same thing — below truecolor StyleQuantizer already mapped the
        // transparent colour to Color.Default and this is the rule that makes full depth agree.
        Assert.Equal([$"0;{RedForeground(depth)}", "39"], Sgr(FragmentEmission(output)));
    }

    [Fact]
    public void ATransparentForeground_EmitsNoBlack()
    {
        // Color.Transparent is Rgb(0,0,0,0). SgrEncoder writes the foreground unconditionally — its
        // transparency check covers the background only — so an unresolved transparent foreground escapes
        // as an explicit black, a colour the author never wrote and one that hides the fragment on a dark
        // terminal.
        var caps = Caps();
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(4, 1, caps);
        buffer.Set(1, 0, " ", CellStyle.Default.WithForeground(Red));
        buffer.AddFragment(1, 0, new SentinelFragment(), CellStyle.Default.WithForeground(Color.Transparent));

        Assert.DoesNotContain("38;2;0;0;0", Render(renderer, buffer));
    }

    // ---- Background: inherited, because a fragment HAS a backdrop ------------------------------

    [Fact]
    public void ATransparentBackground_InheritsTheAnchorCellsAndSaysNothingAboutIt()
    {
        // The one channel with something underneath to show through. Taking the anchor cell's background
        // is the nearest thing to compositing this path can do — and once taken it equals the delta's
        // origin, so the delta has nothing left to say and WriteDelta's own `from == to` elides it.
        var caps = Caps();
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(4, 1, caps);
        buffer.Set(1, 0, " ", CellStyle.Default.WithBackground(Red));
        buffer.AddFragment(1, 0, new SentinelFragment(), CellStyle.Default.WithBackground(Color.Transparent));

        Assert.Equal(["0;48;2;255;0;0"], Sgr(FragmentEmission(output: Render(renderer, buffer))));
    }

    [Fact]
    public void AWhollyTransparentAnchorStyle_InheritsTheBackgroundAndResetsTheInk()
    {
        // CellStyle.Transparent — the production default for a document that states no colours — over a
        // painted anchor. The two rules fire together and pull opposite ways, which is the point: the red
        // background survives (inherited, so the delta stays silent about it) while the teal ink does not
        // (reset, so the delta says 39).
        var caps = Caps();
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(4, 1, caps);
        buffer.Set(1, 0, " ", CellStyle.Default.WithForeground(Color.FromRgb(0, 128, 128)).WithBackground(Red));
        buffer.AddFragment(1, 0, new SentinelFragment(), CellStyle.Transparent);

        Assert.Equal(["0;38;2;0;128;128;48;2;255;0;0", "39"], Sgr(FragmentEmission(Render(renderer, buffer))));
    }

    // ---- Underline colour: reset, and 59 means "follow the foreground" -------------------------

    [Fact]
    public void ATransparentUnderlineColour_ResetsToFollowTheForeground()
    {
        // Same reasoning as the foreground — no ink underneath to inherit — plus one of its own: SGR 59
        // means "follow the foreground", which is where a transparent underline belongs once the
        // foreground has stopped naming a colour.
        var caps = Caps();
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(4, 1, caps);
        var underlined = CellStyle.Default.WithAttributes(TextAttributes.Underline);
        buffer.Set(1, 0, " ", underlined.WithUnderlineColor(Blue));
        buffer.AddFragment(1, 0, new SentinelFragment(), underlined.WithUnderlineColor(Color.Transparent));

        Assert.Equal(["0;4;58:2::0:0:255", "59"], Sgr(FragmentEmission(Render(renderer, buffer))));
    }

    // ---- The carve-out the guard exists for, still intact --------------------------------------

    [Fact]
    public void AFragmentThatDeclaresNoStyleAtAll_LeavesTheAnchorCellsBackdropStanding()
    {
        // The counterweight to the first test, and why the guard cannot simply compare the substituted
        // style against `existingStyle`. An untouched AnchorStyle is AddFragment's default argument — an
        // absence, not a request for the terminal's own colours — so the backdrop the absolute SGR wrote
        // is left alone and no delta follows it. Same outcome the sibling background case pins in
        // FrameRendererDefaultColorTests.AFragmentOverAPaintedBackground_StillEmitsThatBackgroundExplicitly.
        var caps = Caps();
        var renderer = new FrameRenderer(caps.Output);
        var buffer = new CellBuffer(4, 1, caps);
        buffer.Set(1, 0, " ", CellStyle.Default.WithForeground(Red).WithBackground(Blue));
        buffer.AddFragment(1, 0, new SentinelFragment());

        Assert.Equal(["0;38;2;255;0;0;48;2;0;0;255"], Sgr(FragmentEmission(Render(renderer, buffer))));
    }

    private sealed class SentinelFragment : IBufferFragment
    {
        public FragmentLayer Layer => FragmentLayer.Cells;
        public Size GetSize() => new(1, 1);
        public bool IsSupported(OutputCapabilities capabilities) => true;

        public void Emit(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities)
        {
            var bytes = "[F]"u8;
            var dest = output.GetSpan(bytes.Length);
            bytes.CopyTo(dest);
            output.Advance(bytes.Length);
        }
    }
}
