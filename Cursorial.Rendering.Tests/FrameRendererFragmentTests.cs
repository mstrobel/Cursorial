using System.Buffers;
using System.Text;

using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fragments;

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
    public void SizedTextFragment_FixedWidthOverridesNaturalWidth()
    {
        // Width=3 forces 3 cells per cluster regardless of natural width. 5 clusters × 3 = 15.
        var fragment = new SizedTextFragment(
            new TextSizing(Scale: 1, Width: 3),
            "Hello",
            Style.Default);

        Assert.Equal(new Size(15, 1), fragment.GetSize());
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