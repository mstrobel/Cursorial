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

    [Fact]
    public void CellsFragment_ReEmittedWhenCoveredCellBackgroundChanges()
    {
        // Repro of the sized-text-disappears bug. A Cells-layer fragment (OSC 66 sized text)
        // writes NO cells into the scene, so the fragment diff is Key + AnchorStyle only. When the
        // panel UNDER the fragment repaints — e.g. a base-theme light/dark flip that the fragment's
        // owner never hears about — the covered cells' background changes; the cell pass re-emits
        // them as bg-only spaces, overpainting the fragment's payload on the terminal. The fragment
        // (same instance, unchanged identity) must re-emit on top, or it silently disappears.
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 1);
        var dark = Style.Default.WithBackground(Color.FromRgb(20, 20, 20));
        buffer.Set(0, 0, " ", dark);
        buffer.Set(1, 0, " ", dark);
        var fragment = new SentinelFragment(new Size(2, 1), "SIZED");
        buffer.AddFragment(0, 0, fragment);

        var first = Render(r, buffer);
        Assert.Contains("SIZED", first); // emitted initially

        // The panel underneath flips to a light background — the SAME fragment instance stays put
        // (its owner was never invalidated), so absent the fix the fragment diff-skips.
        var light = Style.Default.WithBackground(Color.FromRgb(230, 230, 230));
        buffer.Set(0, 0, " ", light);
        buffer.Set(1, 0, " ", light);
        var second = Render(r, buffer);

        Assert.Contains("48;2;230;230;230", second); // covered cells repaint the new bg …
        Assert.Contains("SIZED", second);            // … and the fragment re-emits on top (the fix)
    }

    [Fact]
    public void CellsFragment_StillSkippedWhenNothingUnderneathChanges()
    {
        // The re-emit is scoped to an actually-overpainted footprint: a stable frame must NOT
        // re-transmit the fragment (no churn), even though it's a Cells-layer fragment.
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 1);
        buffer.Set(0, 0, " ", Style.Default.WithBackground(Color.FromRgb(20, 20, 20)));
        buffer.Set(1, 0, " ", Style.Default.WithBackground(Color.FromRgb(20, 20, 20)));
        var fragment = new SentinelFragment(new Size(2, 1), "SIZED");
        buffer.AddFragment(0, 0, fragment);

        Render(r, buffer);
        var second = Render(r, buffer); // identical state

        Assert.DoesNotContain("SIZED", second);
    }

    [Fact]
    public void OverlayFragment_NotReEmittedWhenUnderlyingCellChanges()
    {
        // The touched-footprint re-emit is Cells-layer ONLY. An Overlay fragment lives on a
        // separate display plane, isn't clobbered by cell repaints, and must not re-transmit when
        // the cells under it change — that churn is what exhausts a terminal's image store.
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 1);
        buffer.Set(0, 0, "A", Style.Default);
        var overlay = new EraseTrackingFragment(new Size(2, 1)); // Overlay layer, emits "EMIT"
        buffer.AddFragment(0, 0, overlay);
        Render(r, buffer);

        buffer.Set(0, 0, "B", Style.Default); // underlying cell changes (overlay doesn't cover it)
        var output = Render(r, buffer);

        Assert.Contains("B", output);          // the cell repaints normally
        Assert.DoesNotContain("EMIT", output); // overlay NOT re-transmitted
    }

    // ---- Content-keyed diff (per-frame reconstruction without churn) -------------

    [Fact]
    public void ContentKeyedFragment_IdenticalReconstruction_NotReEmittedOrErased()
    {
        // A fragment whose Key is content-derived (like KittyImageFragment): reconstructing an
        // identical one each frame (new instance, same Key) must diff-skip — NOT erase the old
        // and re-emit the new. That churn is what exhausts a terminal's Kitty image store.
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 1);

        buffer.AddFragment(0, 0, new KeyedOverlayFragment("K", wireId: 1));
        Render(r, buffer); // first render emits the fragment

        buffer.AddFragment(0, 0, new KeyedOverlayFragment("K", wireId: 2)); // new instance, same Key
        var output = Render(r, buffer);

        Assert.DoesNotContain("EMIT", output);  // not re-transmitted
        Assert.DoesNotContain("ERASE", output); // not deleted
    }

    [Fact]
    public void ContentKeyedFragment_PreservesTransmittedIdentity_OnLaterErase()
    {
        // When a diff-skip happens, the snapshot must keep the FRONT fragment (the one actually
        // transmitted) so a later erase targets the wire identity really on the terminal — not
        // the never-sent reconstruction. Here the first instance has wireId 1; the second
        // (skipped) has wireId 2; on removal the erase must reference 1, not 2.
        var r = new FrameRenderer();
        var buffer = new CellBuffer(5, 1);

        buffer.AddFragment(0, 0, new KeyedOverlayFragment("K", wireId: 1));
        Render(r, buffer);

        buffer.AddFragment(0, 0, new KeyedOverlayFragment("K", wireId: 2)); // diff-skips
        Render(r, buffer);

        buffer.RemoveFragment(0, 0); // now removed → erase must fire for the transmitted id
        var output = Render(r, buffer);

        Assert.Contains("ERASE1", output);     // erases the id actually transmitted
        Assert.DoesNotContain("ERASE2", output);
    }

    // ---- Test helpers ------------------------------------------------------------

    // Overlay fragment with an explicit content-style Key and a distinct wire id encoded into
    // its emit/erase output, so tests can assert which instance's identity reached the wire.
    private sealed class KeyedOverlayFragment(object key, int wireId) : IBufferFragment
    {
        public object Key => key;
        public FragmentLayer Layer => FragmentLayer.Overlay;
        public Size GetSize() => new(2, 1);
        public bool IsSupported(OutputCapabilities capabilities) => true;

        public void Emit(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities)
            => Write(output, $"EMIT{wireId}");

        public void EmitErase(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities)
            => Write(output, $"ERASE{wireId}");

        private static void Write(IBufferWriter<byte> output, string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            var dest = output.GetSpan(bytes.Length);
            bytes.CopyTo(dest);
            output.Advance(bytes.Length);
        }
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
