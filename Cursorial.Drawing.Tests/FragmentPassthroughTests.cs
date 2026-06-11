using System.Buffers;
using System.Text;

using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fragments;
using Cursorial.Rendering.Imaging;

namespace Cursorial.Tests.Drawing;

// Phase 6b.1: DrawContent registers an out-of-band fragment on the scene buffer, and SceneCompositor carries
// it onto the composite target (offset-translated) so the frame renderer can emit it.
public class FragmentPassthroughTests
{
    private static SceneLayer Layer(Scene scene, int offsetColumn = 0, int offsetRow = 0) =>
        new(scene, new CompositeParameters(offsetColumn, offsetRow));

    [Fact]
    public void DrawContent_RegistersFragment_ThenCompositorCarriesItToTheTarget()
    {
        IBufferFragment frag = new FakeFragment(2, 1);
        using var scene = Scene.Create(10, 4);
        scene.Draw(ctx => ctx.DrawContent(new Rect(3, 1, 2, 1), new FragmentContent(frag), OutputCapabilities.None));
        Assert.True(scene.Buffer.AsView().ContainsFragment(frag.Key));   // landed on the scene buffer

        var target = new CellBuffer(10, 4);
        new SceneCompositor(Style.Default).Composite([Layer(scene)], target.AsView());

        Assert.True(target.AsView().TryGetFragmentAnchor(frag.Key, out var anchor));
        Assert.Equal((3, 1), anchor);   // no offset → same anchor on the target
    }

    [Fact]
    public void Composite_TranslatesFragmentByLayerOffset()
    {
        IBufferFragment frag = new FakeFragment(2, 1);
        using var scene = Scene.Create(10, 4);
        scene.Draw(ctx => ctx.DrawContent(new Rect(0, 0, 2, 1), new FragmentContent(frag), OutputCapabilities.None));

        var target = new CellBuffer(12, 6);
        new SceneCompositor(Style.Default).Composite([Layer(scene, offsetColumn: 5, offsetRow: 2)], target.AsView());

        Assert.True(target.AsView().TryGetFragmentAnchor(frag.Key, out var anchor));
        Assert.Equal((5, 2), anchor);   // (0,0) + offset(5,2)
    }

    [Fact]
    public void Composite_MovesFragment_OnOffsetChange_NoStaleDuplicate()
    {
        IBufferFragment frag = new FakeFragment(2, 1);
        using var scene = Scene.Create(10, 4);
        scene.Draw(ctx => ctx.DrawContent(new Rect(0, 0, 2, 1), new FragmentContent(frag), OutputCapabilities.None));

        var target = new CellBuffer(12, 4);
        var view = target.AsView();
        var compositor = new SceneCompositor(Style.Default);

        compositor.Composite([Layer(scene, offsetColumn: 1)], view);
        compositor.Composite([Layer(scene, offsetColumn: 4)], view);   // slide right

        int fragmentCount = target.AsView().Fragments.Count;          // ref-struct dict — can't use Assert.Single
        Assert.Equal(1, fragmentCount);                               // no stale fragment left behind
        Assert.True(target.AsView().TryGetFragmentAnchor(frag.Key, out var anchor));
        Assert.Equal((4, 0), anchor);
    }

    [Fact]
    public void Composite_DropsFragmentsOutsideTheClip()
    {
        IBufferFragment inFrag = new FakeFragment(1, 1);
        IBufferFragment outFrag = new FakeFragment(1, 1);
        using var scene = Scene.Create(10, 4);
        scene.Draw(ctx =>
        {
            ctx.DrawContent(new Rect(1, 1, 1, 1), new FragmentContent(inFrag), OutputCapabilities.None);
            ctx.DrawContent(new Rect(8, 1, 1, 1), new FragmentContent(outFrag), OutputCapabilities.None);
        });

        var target = new CellBuffer(10, 4);
        var clip = new Rect(0, 0, 5, 4);   // only the left half
        new SceneCompositor(Style.Default).Composite([new SceneLayer(scene, new CompositeParameters(clip: clip))], target.AsView());

        Assert.True(target.AsView().ContainsFragment(inFrag.Key));    // anchor (1,1) inside the clip
        Assert.False(target.AsView().ContainsFragment(outFrag.Key));  // anchor (8,1) outside → dropped
    }

    // ---- 6b.2: per-protocol clipping ------------------------------------------------------------

    [Fact]
    public void Composite_CropsAPartiallyClippedFragment_WhenItCanCrop()
    {
        var frag = new CroppableFragment(4, 2);
        using var scene = Scene.Create(10, 4);
        scene.Draw(ctx => ctx.DrawContent(new Rect(0, 0, 4, 2), new FragmentContent(frag), OutputCapabilities.None));

        var target = new CellBuffer(10, 4);
        var clip = new Rect(0, 0, 2, 4);   // keep only the left 2 columns
        new SceneCompositor(Style.Default).Composite([new SceneLayer(scene, new CompositeParameters(clip: clip))], target.AsView());

        int fragmentCount = target.AsView().Fragments.Count;
        Assert.Equal(1, fragmentCount);
        foreach (var (anchor, entry) in target.AsView().Fragments)
        {
            Assert.Equal((0, 0), anchor);
            Assert.Equal(new Size(2, 2), entry.Fragment.GetSize());   // cropped to the visible 2×2
        }
    }

    [Fact]
    public void Composite_SuppressesAPartiallyClippedFragment_WhenItCannotCrop()
    {
        IBufferFragment frag = new FakeFragment(4, 2);   // default Clip → null
        using var scene = Scene.Create(10, 4);
        scene.Draw(ctx => ctx.DrawContent(new Rect(0, 0, 4, 2), new FragmentContent(frag), OutputCapabilities.None));

        var target = new CellBuffer(10, 4);
        new SceneCompositor(Style.Default)
            .Composite([new SceneLayer(scene, new CompositeParameters(clip: new Rect(0, 0, 2, 4)))], target.AsView());

        int fragmentCount = target.AsView().Fragments.Count;
        Assert.Equal(0, fragmentCount);   // straddles the clip + can't crop → suppressed
    }

    [Fact]
    public void Composite_PassesAFullyVisibleFragmentUnchanged()
    {
        IBufferFragment frag = new FakeFragment(2, 2);
        using var scene = Scene.Create(10, 4);
        scene.Draw(ctx => ctx.DrawContent(new Rect(1, 1, 2, 2), new FragmentContent(frag), OutputCapabilities.None));

        var target = new CellBuffer(10, 4);
        new SceneCompositor(Style.Default)
            .Composite([new SceneLayer(scene, new CompositeParameters(clip: new Rect(0, 0, 8, 4)))], target.AsView());

        Assert.True(target.AsView().TryGetFragmentAnchor(frag.Key, out var anchor));   // same fragment, not cropped
        Assert.Equal((1, 1), anchor);
    }

    [Fact]
    public void SixelFragment_Clip_ReCropsToTheVisibleCells()
    {
        // 20×10 px over 2×1 cells (10 px/cell). Crop to the left cell → a 1×1-cell fragment.
        var frag = new SixelFragment(new byte[20 * 10 * 4], pixelWidth: 20, pixelHeight: 10, cellSize: new Size(2, 1));
        var clipped = frag.Clip(new Rect(0, 0, 1, 1));
        Assert.NotNull(clipped);
        Assert.Equal(new Size(1, 1), clipped.GetSize());
    }

    [Fact]
    public void SixelFragment_PreEncoded_CannotClip()
    {
        var payload = new byte[] { 0x1b, (byte) 'P', (byte) 'q', 0x1b, (byte) '\\' };   // a minimal DCS … ST
        var frag = new SixelFragment(payload.AsMemory(), new Size(2, 1));
        Assert.Null(frag.Clip(new Rect(0, 0, 1, 1)));   // no pixels retained → can't crop
    }

    [Fact]
    public void KittyImageFragment_Clip_CropsViaASourceRectangle()
    {
        // 20×10 px over 2×1 cells (10 px/cell). Crop to the left cell → a 1×1-cell placement whose
        // source rectangle is x=0,y=0,w=10,h=10 image pixels. The cropped placement specifies BOTH
        // c and r explicitly (no aspect omission) so the visible region maps exactly.
        var data = new ImageData(new byte[] { 1, 2, 3, 4 }, ImageFormat.Png, new Size(2, 1));
        var frag = new KittyImageFragment(data, displaySize: new Size(2, 1), pixelSize: (20, 10));

        var clipped = frag.Clip(new Rect(0, 0, 1, 1));
        Assert.NotNull(clipped);
        Assert.Equal(new Size(1, 1), clipped.GetSize());

        var output = new ArrayBufferWriter<byte>();
        clipped.Emit(0, 0, output, OutputCapabilities.None);
        string emitted = Encoding.ASCII.GetString(output.WrittenSpan);
        Assert.Contains("c=1", emitted);    // visible cell footprint, both dims explicit
        Assert.Contains("r=1", emitted);
        Assert.Contains("x=0", emitted);    // source rectangle in image pixels
        Assert.Contains("w=10", emitted);
        Assert.Contains("h=10", emitted);
    }

    [Fact]
    public void KittyImageFragment_WithoutPixelSize_CannotClip()
    {
        var data = new ImageData(new byte[] { 1, 2, 3, 4 }, ImageFormat.Png, new Size(2, 1));
        var frag = new KittyImageFragment(data, displaySize: new Size(2, 1));   // no native pixel size
        Assert.Null(frag.Clip(new Rect(0, 0, 1, 1)));   // can't map cells → pixels → suppress
    }

    [Fact]
    public void KittyImageFragment_AspectFreeRows_OmitsRowQualifier_ButReservesWholeCells()
    {
        // Caller sized columns only (rows aspect-derived). The placement pins c= but omits r= so Kitty
        // scales to native aspect; GetSize still reserves the whole-cell footprint for layout.
        var data = new ImageData(new byte[] { 1, 2, 3, 4 }, ImageFormat.Png, new Size(4, 2));
        var frag = new KittyImageFragment(data, displaySize: new Size(4, 2), pixelSize: (40, 20),
                                          aspectFree: AspectFreeDimension.Rows);

        Assert.Equal(new Size(4, 2), frag.GetSize());

        var output = new ArrayBufferWriter<byte>();
        frag.Emit(0, 0, output, OutputCapabilities.None);
        string header = Encoding.ASCII.GetString(output.WrittenSpan);
        header = header[..header.IndexOf(';')];   // the APC control data, before the base64 payload
        Assert.Contains("c=4,", header);          // columns pinned
        Assert.DoesNotContain("r=", header);      // rows left to Kitty's native-aspect scaling
    }

    [Fact]
    public void KittyImageFragment_AspectFreeColumns_OmitsColumnQualifier_ButReservesWholeCells()
    {
        // Mirror of the Rows test (the parallel, previously-untested wire path): caller sized rows only,
        // so columns are aspect-derived — the placement pins r= but omits c=.
        var data = new ImageData(new byte[] { 1, 2, 3, 4 }, ImageFormat.Png, new Size(2, 4));
        var frag = new KittyImageFragment(data, displaySize: new Size(2, 4), pixelSize: (20, 40),
                                          aspectFree: AspectFreeDimension.Columns);

        Assert.Equal(new Size(2, 4), frag.GetSize());

        var output = new ArrayBufferWriter<byte>();
        frag.Emit(0, 0, output, OutputCapabilities.None);
        string header = Encoding.ASCII.GetString(output.WrittenSpan);
        header = header[..header.IndexOf(';')];
        Assert.DoesNotContain("c=", header);   // columns left to Kitty's native-aspect scaling
        Assert.Contains("r=4,", header);       // rows pinned
    }

    [Fact]
    public void KittyImageFragment_Clip_RightEdge_MapsToANonZeroSourceRectangle()
    {
        // 20×10px over 2×1 cells (10px/cell). Crop the RIGHT cell → a non-zero source origin (x=10) — the
        // left-edge crop tests leave the X*multiplier at 0, hiding any offset-mapping bug.
        var data = new ImageData(new byte[] { 1, 2, 3, 4 }, ImageFormat.Png, new Size(2, 1));
        var frag = new KittyImageFragment(data, displaySize: new Size(2, 1), pixelSize: (20, 10));

        var clipped = frag.Clip(new Rect(1, 0, 1, 1));
        Assert.NotNull(clipped);

        var output = new ArrayBufferWriter<byte>();
        clipped.Emit(0, 0, output, OutputCapabilities.None);
        string header = Encoding.ASCII.GetString(output.WrittenSpan);
        header = header[..header.IndexOf(';')];
        Assert.Contains("x=10,", header);   // non-zero source X — the right half of the image
        Assert.Contains("y=0,", header);
        Assert.Contains("w=10,", header);   // x+w = 20 = image width (clamped to the edge, not past it)
        Assert.Contains("h=10,", header);
    }

    [Fact]
    public void KittyImageFragment_Clip_ClampsSourceRectangleToTheImageEdge()
    {
        // 7px over 2 cells (3.5 px/cell): the right cell's origin rounds to x=4; without the extent clamp w
        // would round to 4 too (x+w=8, one pixel past the 7px image). The clamp pins w to the 3 remaining px.
        var data = new ImageData(new byte[] { 1, 2, 3, 4 }, ImageFormat.Png, new Size(2, 1));
        var frag = new KittyImageFragment(data, displaySize: new Size(2, 1), pixelSize: (7, 10));

        var clipped = (KittyImageFragment) frag.Clip(new Rect(1, 0, 1, 1))!;
        var output = new ArrayBufferWriter<byte>();
        clipped.Emit(0, 0, output, OutputCapabilities.None);
        string header = Encoding.ASCII.GetString(output.WrittenSpan);
        header = header[..header.IndexOf(';')];
        Assert.Contains("x=4,", header);
        Assert.Contains("w=3,", header);    // 4 + 3 = 7 = image width, not 8
    }

    [Fact]
    public void KittyImageFragment_Clip_DistinctCropsHaveDistinctKeys()
    {
        // Two crops of the same image at the same footprint (1×1) differ only in their source rectangle.
        // The content key must distinguish them, or the renderer would diff-skip the second and leave a
        // stale crop on screen when an image slides under a fixed clip.
        var data = new ImageData(new byte[] { 1, 2, 3, 4 }, ImageFormat.Png, new Size(2, 1));
        var frag = new KittyImageFragment(data, displaySize: new Size(2, 1), pixelSize: (20, 10));

        var left = (KittyImageFragment) frag.Clip(new Rect(0, 0, 1, 1))!;   // source x=0
        var right = (KittyImageFragment) frag.Clip(new Rect(1, 0, 1, 1))!;  // source x=10
        Assert.NotEqual(left.Key, right.Key);
    }

    [Fact]
    public void ITerm2ImageFragment_AspectFreeRows_EmitsHeightAuto()
    {
        // Rows aspect-derived → width pinned, height=auto with preserveAspectRatio=1 (mirrors Kitty).
        var data = new ImageData(new byte[] { 1, 2, 3, 4 }, ImageFormat.Png, new Size(4, 2));
        IBufferFragment frag = new ITerm2ImageFragment(data, displaySize: new Size(4, 2),
                                                       aspectFree: AspectFreeDimension.Rows);
        var output = new ArrayBufferWriter<byte>();
        frag.Emit(0, 0, output, OutputCapabilities.None);
        string emitted = Encoding.ASCII.GetString(output.WrittenSpan);
        Assert.Contains("width=4;", emitted);
        Assert.Contains("height=auto;", emitted);
        Assert.Contains("preserveAspectRatio=1;", emitted);
    }

    [Fact]
    public void ITerm2ImageFragment_AspectFreeColumns_EmitsWidthAuto()
    {
        // Columns aspect-derived → height pinned, width=auto with preserveAspectRatio=1.
        var data = new ImageData(new byte[] { 1, 2, 3, 4 }, ImageFormat.Png, new Size(4, 2));
        IBufferFragment frag = new ITerm2ImageFragment(data, displaySize: new Size(4, 2),
                                                       aspectFree: AspectFreeDimension.Columns);
        var output = new ArrayBufferWriter<byte>();
        frag.Emit(0, 0, output, OutputCapabilities.None);
        string emitted = Encoding.ASCII.GetString(output.WrittenSpan);
        Assert.Contains("width=auto;", emitted);
        Assert.Contains("height=2;", emitted);
        Assert.Contains("preserveAspectRatio=1;", emitted);
    }

    [Fact]
    public void SixelFragment_Clip_SubPixelPerCell_DoesNotThrow()
    {
        // 3px over 8 cells (<0.5 px/cell): the rightmost cell's origin used to reach _pixelWidth, making
        // the extent clamp Math.Clamp(_, 1, 0) throw. Clamping the origin to _pixelWidth-1 keeps it safe.
        var frag = new SixelFragment(new byte[3 * 4 * 4], pixelWidth: 3, pixelHeight: 4, cellSize: new Size(8, 1));
        var clipped = frag.Clip(new Rect(7, 0, 1, 1));   // rightmost cell
        Assert.NotNull(clipped);
        Assert.Equal(new Size(1, 1), clipped.GetSize());
    }

    [Fact]
    public void SixelFragment_Clip_RightCell_CropsADifferentRegionThanTheLeftCell()
    {
        // Left half red, right half blue over 2×1 cells. A left-cell crop and a right-cell crop must encode
        // DIFFERENT payloads — proving the pixel stride/offset is actually applied (all-zero data hides it).
        var rgba = new byte[20 * 10 * 4];
        for (int y = 0; y < 10; y++)
        for (int x = 0; x < 20; x++)
        {
            int i = (y * 20 + x) * 4;
            bool leftHalf = x < 10;
            rgba[i] = (byte) (leftHalf ? 255 : 0);     // R
            rgba[i + 2] = (byte) (leftHalf ? 0 : 255); // B
            rgba[i + 3] = 255;                         // A
        }
        var frag = new SixelFragment(rgba, pixelWidth: 20, pixelHeight: 10, cellSize: new Size(2, 1));

        var left = (SixelFragment) frag.Clip(new Rect(0, 0, 1, 1))!;
        var right = (SixelFragment) frag.Clip(new Rect(1, 0, 1, 1))!;
        Assert.False(left.Payload.Span.SequenceEqual(right.Payload.Span));   // distinct cropped regions
    }

    [Fact]
    public void Composite_ReAnchorsACroppedFragment_ToTheClipOrigin()
    {
        // Fragment at column 0; clip drops the left 2 columns. The cropped fragment must re-anchor at the
        // clip origin (2,0), not stay at (0,0) — the existing left-keep test only validates the no-move case.
        var frag = new CroppableFragment(4, 2);
        using var scene = Scene.Create(12, 4);
        scene.Draw(ctx => ctx.DrawContent(new Rect(0, 0, 4, 2), new FragmentContent(frag), OutputCapabilities.None));

        var target = new CellBuffer(12, 4);
        var clip = new Rect(2, 0, 10, 4);   // keep cols [2,4) of the fragment
        new SceneCompositor(Style.Default).Composite([new SceneLayer(scene, new CompositeParameters(clip: clip))], target.AsView());

        int fragmentCount = target.AsView().Fragments.Count;
        Assert.Equal(1, fragmentCount);
        foreach (var (anchor, entry) in target.AsView().Fragments)
        {
            Assert.Equal((2, 0), anchor);                            // re-anchored to the clip origin
            Assert.Equal(new Size(2, 2), entry.Fragment.GetSize());  // cropped to the visible 2×2
        }
    }

    // A test fragment that CAN crop (returns a smaller fragment), to exercise the compositor's crop path.
    private sealed class CroppableFragment(int width, int height) : IBufferFragment
    {
        private readonly Size _size = new(width, height);
        public Size GetSize() => _size;
        public bool IsSupported(OutputCapabilities capabilities) => true;
        public void Emit(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities) { }
        public IBufferFragment Clip(in Rect visible) => new CroppableFragment(visible.Columns, visible.Rows);
    }

    // A minimal out-of-band fragment for tests — never actually emits.
    private sealed class FakeFragment(int width, int height) : IBufferFragment
    {
        public Size GetSize() => new(width, height);
        public bool IsSupported(OutputCapabilities capabilities) => true;
        public void Emit(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities) { }
    }

    // An IContent that registers a fragment at its paint anchor (stands in for Image/Icon/ScaledText).
    private sealed class FragmentContent(IBufferFragment fragment) : IContent
    {
        public Size Measure(Size availableSpace, OutputCapabilities capabilities) => fragment.GetSize();

        public Rect Paint(in CellBufferView buffer, in Rect bounds, in Style style, OutputCapabilities capabilities)
        {
            buffer.AddFragment(bounds.Column, bounds.Row, fragment, style);
            var size = fragment.GetSize();
            return new Rect(bounds.Column, bounds.Row, size.Columns, size.Rows);
        }
    }
}
