using System.Buffers;
using System.Text;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fragments;

namespace Cursorial.Tests.Rendering;

public class FrameRendererFragmentDiffTests
{
    private static string Render(FrameRenderer renderer, CellBuffer back)
    {
        var w = new ArrayBufferWriter<byte>();
        renderer.Render(back, w);
        return Encoding.UTF8.GetString(w.WrittenSpan);
    }

    [Fact]
    public void StableFragment_SkippedOnSecondRender()
    {
        // Reference equality on the IBufferFragment instance + value equality on the anchor
        // style is the diff key. Reusing the same instance across frames lets the renderer
        // skip re-emission of an unchanged fragment.
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 1);
        var fragment = new SentinelFragment(new Size(2, 1), "STABLE");
        buffer.AddFragment(0, 0, fragment);

        Render(r, buffer);                  // first render — emits the fragment
        var output = Render(r, buffer);     // second render with the same instance

        // The second render should not contain the fragment payload again.
        Assert.DoesNotContain("STABLE", output);
    }

    [Fact]
    public void DifferentFragmentInstance_RegEmitsEvenIfPayloadIdentical()
    {
        // Reference equality is the contract — a new instance, even with identical content,
        // triggers re-emission. (Callers concerned with avoiding this should hoist fragment
        // construction out of per-frame paint loops.)
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 1);

        buffer.AddFragment(0, 0, new SentinelFragment(new Size(2, 1), "TEXT"));
        Render(r, buffer);

        buffer.AddFragment(0, 0, new SentinelFragment(new Size(2, 1), "TEXT")); // new instance
        var output = Render(r, buffer);

        Assert.Contains("TEXT", output);
    }

    [Fact]
    public void RemovedFragment_TriggersCellsToRepaint()
    {
        // While the fragment was registered, cells in its footprint emitted bg-only-spaces.
        // When the fragment is removed, the cell pass sees back glyphs != front (which is
        // still the space-with-bg form), and re-emits the actual glyphs.
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 1);
        buffer.Set(0, 0, "Q", Style.Default);
        buffer.Set(1, 0, "Z", Style.Default);
        // Sentinel chosen to share no letters with the cell glyphs so contains-checks are
        // unambiguous.
        buffer.AddFragment(0, 0, new SentinelFragment(new Size(2, 1), "[OVL]"));

        var firstOutput = Render(r, buffer);
        Assert.DoesNotContain("Q", firstOutput); // covered → glyph dropped
        Assert.DoesNotContain("Z", firstOutput);
        Assert.Contains("[OVL]", firstOutput);

        buffer.RemoveFragment(0, 0);
        var secondOutput = Render(r, buffer);

        // Cells now emit their actual glyphs.
        Assert.Contains("Q", secondOutput);
        Assert.Contains("Z", secondOutput);
        // No fragment payload — it was removed.
        Assert.DoesNotContain("[OVL]", secondOutput);
    }

    [Fact]
    public void OverlayFragmentRemoved_CallsEmitErase()
    {
        // Overlay-layer fragments must explicitly erase themselves on removal; cell repainting
        // doesn't reach into their separate display plane.
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 1);
        var overlay = new EraseTrackingFragment(new Size(2, 1));
        buffer.AddFragment(0, 0, overlay);

        Render(r, buffer);
        Assert.False(overlay.EraseEmitted);

        buffer.RemoveFragment(0, 0);
        var output = Render(r, buffer);

        Assert.True(overlay.EraseEmitted);
        Assert.Contains("ERASE", output);
    }

    [Fact]
    public void CellsUnderOverlayFragment_RenderNormally()
    {
        // Overlay-layer fragments don't trigger the covered-cell treatment — cells render
        // their actual glyphs because the fragment composites on a separate plane.
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 1);
        buffer.Set(0, 0, "X", Style.Default);
        buffer.Set(1, 0, "Y", Style.Default);
        buffer.AddFragment(0, 0, new EraseTrackingFragment(new Size(2, 1)));

        var output = Render(r, buffer);

        Assert.Contains("X", output);
        Assert.Contains("Y", output);
    }

    [Fact]
    public void StableCellsUnderCellLayerFragment_StaySkipped()
    {
        // Across multiple frames where the back cells under the fragment haven't changed and
        // the fragment is stable, the renderer shouldn't re-emit the bg-only space for those
        // cells.
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 1);
        buffer.Set(0, 0, "X", Style.Default.WithBackground(Color.FromRgb(30, 30, 30)));
        var fragment = new SentinelFragment(new Size(1, 1), "F");
        buffer.AddFragment(0, 0, fragment);

        Render(r, buffer);
        var output = Render(r, buffer);

        // Second render of identical state: no bg-paint re-emission, no fragment re-emission.
        Assert.DoesNotContain("48;2;30;30;30", output);
        Assert.DoesNotContain("F", output);
    }

    [Fact]
    public void ReplacedFragment_ErasesOldThenEmitsNew()
    {
        // When a different fragment instance takes over the same anchor, the old one's erase
        // runs (no-op for cell-layer, real for overlay) before the new one's emit.
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 1);
        var first = new EraseTrackingFragment(new Size(1, 1));
        buffer.AddFragment(0, 0, first);
        Render(r, buffer);

        buffer.AddFragment(0, 0, new EraseTrackingFragment(new Size(1, 1)));
        Render(r, buffer);

        Assert.True(first.EraseEmitted);
    }

    [Fact]
    public void WideCellUnderFragment_EmitsTwoSpacesWithBackground()
    {
        // A wide cell under a Cell-layer fragment shouldn't try to emit the wide glyph — that
        // would corrupt the fragment. The covered-cell path emits a bg-only space at both the
        // wide-left position and the continuation position, totaling two cells of bg paint.
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 1);
        buffer.Set(0, 0, "中", Style.Default.WithBackground(Color.FromRgb(80, 80, 80)));
        buffer.AddFragment(0, 0, new SentinelFragment(new Size(2, 1), "F"));

        var output = Render(r, buffer);

        // No wide glyph in the output — it was suppressed by the cover.
        Assert.DoesNotContain('中', output);
        // The cell's background still paints.
        Assert.Contains("48;2;80;80;80", output);
        // Fragment payload appears.
        Assert.Contains("F", output);
    }

    // ---- Test helpers ------------------------------------------------------------

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

    private sealed class EraseTrackingFragment(Size size) : IBufferFragment
    {
        public bool EraseEmitted { get; private set; }
        public FragmentLayer Layer => FragmentLayer.Overlay;
        public Size GetSize() => size;
        public bool IsSupported(OutputCapabilities capabilities) => true;
        public void Emit(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities)
        {
            var bytes = "EMIT"u8;
            var dest = output.GetSpan(bytes.Length);
            bytes.CopyTo(dest);
            output.Advance(bytes.Length);
        }
        public void EmitErase(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities)
        {
            EraseEmitted = true;
            var bytes = "ERASE"u8;
            var dest = output.GetSpan(bytes.Length);
            bytes.CopyTo(dest);
            output.Advance(bytes.Length);
        }
    }
}
