using System.Buffers;
using System.Text;

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fragments;
using Cursorial.Text;

namespace Cursorial.Tests.Rendering;

public class FrameRendererFragmentTests
{
    private static string Render(FrameRenderer renderer, CellBuffer back)
    {
        var w = new ArrayBufferWriter<byte>();
        renderer.Render(back, w);
        return Encoding.UTF8.GetString(w.WrittenSpan);
    }

    [Fact]
    public void CellsOutsideFragment_RenderTheirGlyphs()
    {
        // Cells outside a Cell-layer fragment's footprint render normally — the fragment's
        // covered-cell skip only applies inside the footprint.
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 1);
        buffer.Set(0, 0, "a", Style.Default);
        buffer.AddFragment(1, 0, new SentinelFragment(new Size(2, 1), "[F]"));
        buffer.Set(3, 0, "z", Style.Default); // outside coverage

        var output = Render(r, buffer);

        Assert.Contains("a", output);
        Assert.Contains("z", output);
        Assert.Contains("[F]", output);

        // The fragment payload comes after the cell pass.
        int aIdx = output.IndexOf('a');
        int fragIdx = output.IndexOf("[F]", StringComparison.Ordinal);
        Assert.True(fragIdx > aIdx, "Fragment emission must follow the cell pass.");
    }

    [Fact]
    public void ForceRepaintRegion_ReEmitsCellsThatMatchTheFrontBuffer()
    {
        // The force-repaint channel (CellBuffer.ForceRepaint) re-emits cells even when they're byte-identical
        // to the front buffer. This is what overwrites a removed Cells-layer image's lingering pixels: the
        // covered cell's front-buffer record is the bg-only placeholder, so after the image is gone the cell
        // content is UNCHANGED and a plain diff would skip it (the tab-switch artifact). The compositor marks
        // the vacated footprint here; the renderer honors it. (See SceneCompositor for who marks it in practice.)
        var r = new FrameRenderer();
        var bg = Color.FromRgb(40, 60, 80);
        var buffer = new CellBuffer(5, 1);
        buffer.Set(1, 0, " ", Style.Default.WithBackground(bg));
        buffer.AddFragment(1, 0, new SentinelFragment(new Size(2, 1), "[F]"));
        Render(r, buffer); // frame 1: covered → front records the bg-only placeholder

        buffer.RemoveFragment(1, 0);        // the image is gone; the cells (" " + bg) are unchanged from the placeholder
        buffer.ForceRepaint(1, 0, 2, 1);    // ...but the compositor force-repaints the vacated footprint
        var output = Render(r, buffer);

        Assert.Contains("48;2;40;60;80", output);   // the footprint cell re-emitted, overwriting the image pixels
        Assert.DoesNotContain("[F]", output);       // the fragment itself is gone
    }

    [Fact]
    public void ForceRepaintRegions_AreClearedAfterEachRender()
    {
        // Force-repaint marks are one-shot: once the cells are emitted the front buffer is correct, so the
        // next frame must not keep re-emitting them. The renderer clears the regions at end-of-render.
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 1);
        buffer.Set(1, 0, "x", Style.Default);
        Render(r, buffer);

        buffer.ForceRepaint(1, 0, 1, 1);
        Render(r, buffer);                  // consumes the mark
        Assert.Empty(buffer.ForceRepaintRegions);

        var output = Render(r, buffer);     // nothing changed and no force mark → empty delta
        Assert.DoesNotContain("x", output);
    }

    [Fact]
    public void CellsUnderCellLayerFragment_DropTheGlyphButKeepTheBackground()
    {
        // For Cell-layer fragments, glyphs under the footprint are skipped — they'd corrupt
        // the fragment's payload — but the cell's background still paints so panels behind
        // fragments show consistent bg colors.
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 1);
        var panelBg = Color.FromRgb(40, 60, 80);
        buffer.Set(1, 0, "U", Style.Default.WithBackground(panelBg));
        buffer.Set(2, 0, "V", Style.Default.WithBackground(panelBg));
        buffer.AddFragment(1, 0, new SentinelFragment(new Size(2, 1), "[F]"));

        var output = Render(r, buffer);

        // Glyphs under the fragment are dropped (the fragment owns the foreground).
        Assert.DoesNotContain("U", output);
        Assert.DoesNotContain("V", output);

        // The panel bg color (rgb 40,60,80) must still appear in the SGR stream for those
        // cells so the panel reads as a single colored block behind the fragment.
        Assert.Contains("48;2;40;60;80", output);

        // The fragment still emits its payload.
        Assert.Contains("[F]", output);
    }

    [Fact]
    public void Fragments_AreBracketedWithSaveRestoreCursor()
    {
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 1);
        buffer.AddFragment(0, 0, new SentinelFragment(new Size(1, 1), "FRAG"));

        var output = Render(r, buffer);

        // Note: \x1b7 is greedy and parses as one char (U+01B7). Use  for explicit ESC.
        int saveIdx = output.IndexOf("7", StringComparison.Ordinal);
        int fragIdx = output.IndexOf("FRAG", StringComparison.Ordinal);
        int restoreIdx = output.IndexOf("8", StringComparison.Ordinal);

        Assert.True(saveIdx >= 0, "DECSC must appear before the fragment payload.");
        Assert.True(fragIdx > saveIdx, "Fragment body must appear between DECSC and DECRC.");
        Assert.True(restoreIdx > fragIdx, "DECRC must appear after the fragment payload.");
    }

    [Fact]
    public void UnsupportedFragment_IsSkipped()
    {
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 1);
        buffer.AddFragment(0, 0, new SentinelFragment(new Size(1, 1), "NOPE", supported: false));

        var output = Render(r, buffer);

        Assert.DoesNotContain("NOPE", output);
    }

    [Fact]
    public void SizedTextFragment_EmitsOsc66()
    {
        var caps = OutputCapabilities.None with
                   {
                       TextSizing = new TextSizingCapabilities(Width: true, Scale: true),
                   };

        var r = new FrameRenderer(caps);
        var buffer = new CellBuffer(20, 2);

        var fragment = new SizedTextFragment(
            new TextSizing(Scale: 2),
            "Hello",
            Style.Default.WithForeground(Color.FromRgb(255, 0, 0)));

        buffer.AddFragment(0, 0, fragment);

        var output = Render(r, buffer);

        Assert.Contains("\x1b]66;", output); // OSC 66 prefix
        Assert.Contains("s=2", output);      // scale metadata
        Assert.Contains("Hello", output);    // payload
        Assert.Contains("\x1b\\", output);   // ST
    }

    [Fact]
    public void SizedTextFragment_WithoutCapability_DoesNotEmit()
    {
        var caps = OutputCapabilities.None; // no TextSizing
        var r = new FrameRenderer(caps);
        var buffer = new CellBuffer(20, 2);

        var fragment = new SizedTextFragment(
            new TextSizing(Scale: 2),
            "Hello",
            Style.Default);

        buffer.AddFragment(0, 0, fragment);

        var output = Render(r, buffer);

        Assert.DoesNotContain("\x1b]66;", output);
        Assert.DoesNotContain("Hello", output);
    }

    [Fact]
    public void SizedTextFragment_ReportsCorrectSize()
    {
        // "Hello" = 5 narrow clusters, scale 2 → width 10, height 2.
        var fragment = new SizedTextFragment(new TextSizing(Scale: 2), "Hello", Style.Default);
        Assert.Equal(new Size(10, 2), fragment.GetSize());
    }

    [Fact]
    public void SizedTextFragment_WidthParameterDoesNotAffectMeasurement()
    {
        // 'w' is unsupported by decision (spec-verified 2026-08-02: it is the fixed width of the
        // ENTIRE sequence, not per-cluster, and sub-cell layouts are unmeasurable in whole
        // cells) — it is never emitted and never measured, so the footprint is natural width.
        var fragment = new SizedTextFragment(
            new TextSizing(Scale: 1, Width: 3),
            "Hello",
            Style.Default);

        Assert.Equal(new Size(5, 1), fragment.GetSize());
    }

    private sealed class SentinelFragment(Size size, string sentinel, bool supported = true) : IBufferFragment
    {
        public Size GetSize() => size;
        public bool IsSupported(OutputCapabilities capabilities) => supported;
        public void Emit(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities)
        {
            var bytes = Encoding.UTF8.GetBytes(sentinel);
            var dest = output.GetSpan(bytes.Length);
            bytes.CopyTo(dest);
            output.Advance(bytes.Length);
        }
    }
}