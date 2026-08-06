using System.Buffers;
using System.Text;

using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fragments;
using Cursorial.Rendering.Imaging;
using Cursorial.Text;

namespace Cursorial.Tests.Drawing;

// FragmentDictionary exposes Count + a struct enumerator but does not implement IEnumerable, so xUnit's
// Assert.Empty/Assert.Single (which require IEnumerable) do not apply — Assert.Equal(N, …Count) is the only
// idiomatic form here. Silence the xUnit2013 "use Assert.Empty/Single" suggestion (a false positive).
#pragma warning disable xUnit2013

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

    // ---- Orphaned-fragment cleanup across a compositor swap (ResetCompositor path) ----------------

    [Fact] // A fresh compositor's first (full) composite clears fragments a discarded compositor left on the target.
    public void FreshCompositor_FullComposite_ClearsOrphanedFragments()
    {
        IBufferFragment frag = new FakeFragment(2, 1);
        using var scene = Scene.Create(10, 4);
        scene.Draw(ctx => ctx.DrawContent(new Rect(3, 1, 2, 1), new FragmentContent(frag), OutputCapabilities.None));

        var target = new CellBuffer(10, 4);
        // Compositor A places the fragment, then is discarded — exactly the WindowManager.ResetCompositor path
        // (a popup open/close rebuilds the SceneCompositor, losing its _fragmentAnchors).
        new SceneCompositor(Style.Default).Composite([Layer(scene)], target.AsView());
        Assert.True(target.AsView().ContainsFragment(frag.Key));

        // The element is gone (tab switched away): a FRESH compositor composites a scene with NO fragment.
        // Its first composite is a full reset, which must drop A's orphan rather than strand the image on screen.
        using var empty = Scene.Create(10, 4);
        empty.Draw(_ => { });
        new SceneCompositor(Style.Default).Composite([Layer(empty)], target.AsView());

        Assert.Equal(0, target.AsView().Fragments.Count); // orphan cleared (the image-not-removed bug)
    }

    [Fact] // A fresh compositor whose scene STILL has the fragment re-registers it (clear-then-rebuild loses nothing).
    public void FreshCompositor_FullComposite_ReRegistersAStillPresentFragment()
    {
        IBufferFragment frag = new FakeFragment(2, 1);
        using var scene = Scene.Create(10, 4);
        scene.Draw(ctx => ctx.DrawContent(new Rect(3, 1, 2, 1), new FragmentContent(frag), OutputCapabilities.None));

        var target = new CellBuffer(10, 4);
        new SceneCompositor(Style.Default).Composite([Layer(scene)], target.AsView()); // compositor A
        new SceneCompositor(Style.Default).Composite([Layer(scene)], target.AsView()); // fresh B — still present

        Assert.True(target.AsView().ContainsFragment(frag.Key));
        Assert.Equal(1, target.AsView().Fragments.Count);
    }

    // ---- Occlusion of a graphics-protocol fragment by a higher OPAQUE surface (the popup-over-image bug) ----

    private static SceneLayer Occluder(Scene scene, int z, int offsetColumn = 0, int offsetRow = 0) =>
        new(scene, new CompositeParameters(offsetColumn, offsetRow)) { SurfaceZ = z, IsOccluder = true };

    [Fact] // A higher opaque surface (a popup) fully over the image suppresses it — it can't be one source-crop.
    public void Composite_SuppressesAFragmentFullyCoveredByAHigherOccluderSurface()
    {
        var frag = new CroppableFragment(2, 2);
        using var lower = Scene.Create(10, 4);
        lower.Draw(ctx => ctx.DrawContent(new Rect(1, 1, 2, 2), new FragmentContent(frag), OutputCapabilities.None));
        using var occ = Scene.Create(10, 4);
        occ.Draw(_ => { });

        var target = new CellBuffer(10, 4);
        new SceneCompositor(Style.Default).Composite([new SceneLayer(lower) { SurfaceZ = 0 }, Occluder(occ, z: 1)], target.AsView());

        Assert.Equal(0, target.AsView().Fragments.Count); // image hidden under the popup → its cells/placeholder show
    }

    [Fact] // A higher occluder over a clean edge crops the image to the visible band (re-anchored).
    public void Composite_CropsAFragmentUnderAHigherOccluderCleanEdge()
    {
        var frag = new CroppableFragment(4, 2);
        using var lower = Scene.Create(10, 4);
        lower.Draw(ctx => ctx.DrawContent(new Rect(0, 0, 4, 2), new FragmentContent(frag), OutputCapabilities.None));
        using var occ = Scene.Create(4, 1); // covers the top row across the fragment's full width
        occ.Draw(_ => { });

        var target = new CellBuffer(10, 4);
        new SceneCompositor(Style.Default).Composite([new SceneLayer(lower) { SurfaceZ = 0 }, Occluder(occ, z: 1)], target.AsView());

        Assert.Equal(1, target.AsView().Fragments.Count);
        foreach (var (anchor, entry) in target.AsView().Fragments)
        {
            Assert.Equal((0, 1), anchor);                           // re-anchored below the occluder
            Assert.Equal(new Size(4, 1), entry.Fragment.GetSize()); // cropped to the visible bottom band
        }
    }

    [Fact] // A higher zone of the SAME surface never occludes the surface's own image.
    public void Composite_DoesNotOccludeAFragmentByASameSurfaceLayer()
    {
        IBufferFragment frag = new CroppableFragment(2, 2);
        using var lower = Scene.Create(10, 4);
        lower.Draw(ctx => ctx.DrawContent(new Rect(0, 0, 2, 2), new FragmentContent(frag), OutputCapabilities.None));
        using var higherZone = Scene.Create(10, 4);
        higherZone.Draw(_ => { });

        var target = new CellBuffer(10, 4);
        new SceneCompositor(Style.Default).Composite(
            [new SceneLayer(lower) { SurfaceZ = 0 }, new SceneLayer(higherZone) { SurfaceZ = 0, IsOccluder = true }], target.AsView());

        Assert.True(target.AsView().ContainsFragment(frag.Key)); // same SurfaceZ → not occluded
    }

    [Fact] // A higher surface NOT flagged as an occluder (transparent) leaves the image fully visible.
    public void Composite_DoesNotOccludeUnderAHigherNonOccluderSurface()
    {
        IBufferFragment frag = new CroppableFragment(2, 2);
        using var lower = Scene.Create(10, 4);
        lower.Draw(ctx => ctx.DrawContent(new Rect(0, 0, 2, 2), new FragmentContent(frag), OutputCapabilities.None));
        using var higher = Scene.Create(10, 4);
        higher.Draw(_ => { });

        var target = new CellBuffer(10, 4);
        new SceneCompositor(Style.Default).Composite(
            [new SceneLayer(lower) { SurfaceZ = 0 }, new SceneLayer(higher) { SurfaceZ = 1, IsOccluder = false }], target.AsView());

        Assert.True(target.AsView().ContainsFragment(frag.Key));
    }

    // ---- Genuine-removal force-repaint of a Cells-layer image (the lingering-pixels / tab-switch bug) ----
    // A Cells-layer image (iTerm2/Sixel) has NO protocol erase — its pixels linger on the terminal until a cell
    // paints over them. The front buffer records the covered cells as a bg-only placeholder, so once the image is
    // gone an unchanged-content diff re-emits nothing and the pixels persist. The compositor force-repaints the
    // vacated footprint when (and only when) the image's source scene is genuinely gone — never on mere occlusion.

    private static bool IntersectsFootprint(IReadOnlyList<Rect> regions, in Rect footprint)
    {
        foreach (var r in regions)
            if (r.Intersects(footprint)) return true;
        return false;
    }

    [Fact] // An image present last work-frame but gone this one is force-repainted so its lingering pixels are erased.
    public void Composite_ForceRepaintsAGenuinelyRemovedCellsImage()
    {
        IBufferFragment frag = new FakeFragment(2, 2);
        using var withImage = Scene.Create(10, 4);
        withImage.Draw(ctx => ctx.DrawContent(new Rect(1, 1, 2, 2), new FragmentContent(frag), OutputCapabilities.None));
        using var empty = Scene.Create(10, 4);
        empty.Draw(_ => { });

        var target = new CellBuffer(10, 4);
        var compositor = new SceneCompositor(Style.Default);
        compositor.Composite([Layer(withImage)], target.AsView());  // image committed → it joins the ghost set
        target.ClearForceRepaint();                                 // ignore the first-frame full-union marks

        compositor.Composite([Layer(empty)], target.AsView());      // scene unloaded → genuine removal

        Assert.True(IntersectsFootprint(target.ForceRepaintRegions, new Rect(1, 1, 2, 2)));
    }

    [Fact] // An OCCLUDED image is NOT force-repainted — its pixels must linger and re-emit when the popup lifts.
    public void Composite_DoesNotForceRepaintAnOccludedCellsImage()
    {
        IBufferFragment frag = new CroppableFragment(2, 2);
        using var lower = Scene.Create(10, 4);
        lower.Draw(ctx => ctx.DrawContent(new Rect(1, 1, 2, 2), new FragmentContent(frag), OutputCapabilities.None));
        using var occ = Scene.Create(10, 4);
        occ.Draw(_ => { });

        var target = new CellBuffer(10, 4);
        var compositor = new SceneCompositor(Style.Default);
        compositor.Composite([new SceneLayer(lower) { SurfaceZ = 0 }], target.AsView());   // image visible
        target.ClearForceRepaint();

        // The popup opens fully over the image: it's suppressed from the target, but its scene is still alive.
        compositor.Composite([new SceneLayer(lower) { SurfaceZ = 0 }, Occluder(occ, z: 1)], target.AsView());

        Assert.Equal(0, target.AsView().Fragments.Count);                                  // suppressed (occlusion)
        Assert.False(IntersectsFootprint(target.ForceRepaintRegions, new Rect(1, 1, 2, 2)));  // but NOT erased
    }

    [Fact] // The reported bug: an OCCLUDED image whose scene unloads ACROSS a compositor reset is still erased.
    public void AdoptGhostFootprints_ForceRepaintsAnOccludedImageRemovedAcrossAReset()
    {
        IBufferFragment frag = new CroppableFragment(2, 2);
        using var lower = Scene.Create(10, 4);
        lower.Draw(ctx => ctx.DrawContent(new Rect(1, 1, 2, 2), new FragmentContent(frag), OutputCapabilities.None));
        using var occ = Scene.Create(10, 4);
        occ.Draw(_ => { });
        using var empty = Scene.Create(10, 4);
        empty.Draw(_ => { });

        var target = new CellBuffer(10, 4);

        // A — the image is visible (Media tab, no popup). It joins A's ghost set.
        var a = new SceneCompositor(Style.Default);
        a.Composite([new SceneLayer(lower) { SurfaceZ = 0 }], target.AsView());

        // B — a popup opened (WindowManager.ResetCompositor): adopt A's ghost; the image is now occluded
        // (suppressed from the target as a registered fragment, but its scene is still alive, so it stays a ghost).
        var b = new SceneCompositor(Style.Default);
        b.AdoptGhostFootprints(a);
        b.Composite([new SceneLayer(lower) { SurfaceZ = 0 }, Occluder(occ, z: 1)], target.AsView());
        Assert.Equal(0, target.AsView().Fragments.Count);   // confirms the image is NOT a registered target fragment
        target.ClearForceRepaint();

        // C — a tab switch dismissed the popup (another ResetCompositor): adopt B's ghost; the image scene is gone.
        // The ghost hand-off is the only record of the occluded image, so without it this removal would be missed.
        var c = new SceneCompositor(Style.Default);
        c.AdoptGhostFootprints(b);
        c.Composite([Layer(empty)], target.AsView());

        Assert.True(IntersectsFootprint(target.ForceRepaintRegions, new Rect(1, 1, 2, 2)));
    }

    [Fact] // A negative folded offset (scrolled content / a window dragged off the top-left) must not crash the live-footprint build.
    public void Composite_NegativeOffsetCellsImage_DoesNotThrow()
    {
        // anchor (1,1) + offset (-3,-3) = (-2,-2): an unclamped Rect ctor would throw ArgumentOutOfRangeException
        // and take down the frame loop. The footprint must clamp to the on-screen remainder instead.
        IBufferFragment frag = new FakeFragment(2, 2);
        using var scene = Scene.Create(10, 4);
        scene.Draw(ctx => ctx.DrawContent(new Rect(1, 1, 2, 2), new FragmentContent(frag), OutputCapabilities.None));

        var target = new CellBuffer(10, 4);
        var ex = Record.Exception(() =>
            new SceneCompositor(Style.Default).Composite([Layer(scene, offsetColumn: -3, offsetRow: -3)], target.AsView()));

        Assert.Null(ex);
    }

    [Fact] // An image that SHRINKS in place (window resize) must force-repaint the exposed L-remainder.
    public void Composite_ShrinkInPlaceCellsImage_ForceRepaintsTheExposedRemainder()
    {
        var target = new CellBuffer(12, 6);
        var compositor = new SceneCompositor(Style.Default);

        IBufferFragment big = new FakeFragment(6, 4);
        using (var sceneBig = Scene.Create(12, 6))
        {
            sceneBig.Draw(ctx => ctx.DrawContent(new Rect(0, 0, 6, 4), new FragmentContent(big), OutputCapabilities.None));
            compositor.Composite([Layer(sceneBig)], target.AsView());   // 6x4 committed → ghost
            target.ClearForceRepaint();

            // The image shrinks to 2x2 at the same anchor (a different fragment identity). The exposed L-shaped
            // remainder (the old 6x4 minus the new 2x2) must be force-repainted to erase the lingering pixels;
            // the 2x2 the new image still occupies must NOT be (it would partially erase the surviving image).
            IBufferFragment small = new FakeFragment(2, 2);
            using var sceneSmall = Scene.Create(12, 6);
            sceneSmall.Draw(ctx => ctx.DrawContent(new Rect(0, 0, 2, 2), new FragmentContent(small), OutputCapabilities.None));
            compositor.Composite([Layer(sceneSmall)], target.AsView());
        }

        // The bottom band (rows 2-3) and the right band (cols 2-5, rows 0-1) of the old footprint are exposed.
        Assert.True(IntersectsFootprint(target.ForceRepaintRegions, new Rect(0, 2, 6, 2)));  // bottom band exposed
        Assert.True(IntersectsFootprint(target.ForceRepaintRegions, new Rect(4, 0, 2, 2)));  // right band exposed
        // The cell the surviving 2x2 image still covers must not be force-repainted.
        Assert.False(IntersectsFootprint(target.ForceRepaintRegions, new Rect(0, 0, 1, 1))); // under the survivor
    }

    [Fact] // A removed image partially overlapped by a DIFFERENT surviving image: only the disjoint remainder is erased.
    public void Composite_RemovedImageOverlappingASurvivor_ForceRepaintsOnlyTheDisjointPart()
    {
        var target = new CellBuffer(12, 4);
        var compositor = new SceneCompositor(Style.Default);

        // Frame 1: two distinct Cells-layer images, A at cols [0,4) and B at cols [3,7) (they share col 3).
        IBufferFragment a = new FakeFragment(4, 2);
        IBufferFragment b = new FakeFragment(4, 2);
        using (var s1 = Scene.Create(12, 4))
        {
            s1.Draw(ctx =>
            {
                ctx.DrawContent(new Rect(0, 0, 4, 2), new FragmentContent(a), OutputCapabilities.None);
                ctx.DrawContent(new Rect(3, 0, 4, 2), new FragmentContent(b), OutputCapabilities.None);
            });
            compositor.Composite([Layer(s1)], target.AsView());
            target.ClearForceRepaint();

            // Frame 2: A is gone; B survives at [3,7). A's ghost minus B leaves cols [0,3) — only that is erased.
            using var s2 = Scene.Create(12, 4);
            s2.Draw(ctx => ctx.DrawContent(new Rect(3, 0, 4, 2), new FragmentContent(b), OutputCapabilities.None));
            compositor.Composite([Layer(s2)], target.AsView());
        }

        Assert.True(IntersectsFootprint(target.ForceRepaintRegions, new Rect(0, 0, 3, 2)));   // A's disjoint part erased
        Assert.False(IntersectsFootprint(target.ForceRepaintRegions, new Rect(3, 0, 4, 2)));  // B's footprint preserved
    }

    [Fact] // When the surface stack empties (Composite over an empty layer set), a committed image's ghost is erased.
    public void Composite_EmptyLayerSet_ForceRepaintsThePriorGhost()
    {
        IBufferFragment frag = new FakeFragment(2, 2);
        using var scene = Scene.Create(10, 4);
        scene.Draw(ctx => ctx.DrawContent(new Rect(1, 1, 2, 2), new FragmentContent(frag), OutputCapabilities.None));

        var target = new CellBuffer(10, 4);
        var compositor = new SceneCompositor(Style.Default);
        compositor.Composite([Layer(scene)], target.AsView());   // image committed → ghost
        target.ClearForceRepaint();

        compositor.Composite([], target.AsView());               // stack emptied (null root / last surface closed)

        Assert.True(IntersectsFootprint(target.ForceRepaintRegions, new Rect(1, 1, 2, 2)));
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
    [Fact]
    public void SceneReRaster_ReRegistersTheSameFragmentInstance()
    {
        // The re-raster wipe empties the fragment registry, so content re-registers per raster —
        // but a still-fresh fragment must be the SAME instance, not a re-creation: the frame
        // renderer diffs by reference, so a fresh instance re-transmits an unchanged payload
        // (and an image re-creation re-encodes it — Sixel re-quantizes the full raster).
        var caps = OutputCapabilities.None with
                   {
                       Graphics = new GraphicsCapabilities(Sixel: false, KittyGraphics: true, ITerm2InlineImages: false),
                   };
        var content = new Image(new ImageData(MinimalPng(16, 8), ImageFormat.Png, new Size(2, 1)));

        using var scene = Scene.Create(10, 4);
        scene.Draw(ctx => ctx.DrawContent(new Rect(0, 0, 2, 1), content, caps));

        Assert.True(scene.Buffer.AsView().TryGetFragmentAnchor(FirstFragment(scene).Key, out _));
        var first = FirstFragment(scene);

        scene.Invalidate();
        scene.Draw(ctx => ctx.DrawContent(new Rect(0, 0, 2, 1), content, caps));

        Assert.Same(first, FirstFragment(scene));
    }

    [Fact]
    public void SceneReRaster_UnchangedImage_IsNotReTransmitted()
    {
        // End to end on the SIXEL tier — the exposed one: SixelFragment's diff key is reference
        // identity and its construction re-quantizes the raster, so without instance reuse every
        // co-scene cell change re-encoded AND re-transmitted the whole payload. (Kitty dodges the
        // re-transmission via its content-derived key; reuse spares it only the re-encode.)
        var caps = OutputCapabilities.None with
                   {
                       Graphics = new GraphicsCapabilities(Sixel: true, KittyGraphics: false, ITerm2InlineImages: false),
                       Window = OutputCapabilities.None.Window with { CellPixelWidth = 8, CellPixelHeight = 16 },
                   };
        var content = new Image(new ImageData(SolidPng(16, 16), ImageFormat.Png, new Size(2, 1)));
        using var scene = Scene.Create(10, 4);
        var target = new CellBuffer(10, 4);
        var compositor = new SceneCompositor(Style.Default);
        var renderer = new FrameRenderer(caps);

        void Raster(string label)
        {
            scene.Invalidate();
            scene.Draw(ctx =>
            {
                ctx.DrawContent(new Rect(0, 0, 2, 1), content, caps);
                for (int i = 0; i < label.Length; i++) ctx.Set(i, 2, label[i].ToString(), Style.Default);
            });
        }

        Raster("one");
        compositor.Composite([Layer(scene)], target.AsView());
        var w1 = new ArrayBufferWriter<byte>();
        renderer.Render(target, w1);
        Assert.Contains("\x1bP", Encoding.UTF8.GetString(w1.WrittenSpan)); // Sixel DCS payload out

        Raster("two");
        compositor.Composite([Layer(scene)], target.AsView());
        var w2 = new ArrayBufferWriter<byte>();
        renderer.Render(target, w2);
        var second = Encoding.UTF8.GetString(w2.WrittenSpan);

        Assert.Contains("two", second);                 // the cell change went out —
        Assert.DoesNotContain("\x1bP", second);         // — the unchanged image did not
    }

    [Fact]
    public void ScaledTextStyleChange_RebuildsTheFragment()
    {
        // The guard on reuse: ScaledText bakes the style into the emission (the OSC 66 SGR
        // backdrop), so a style change at an unchanged size must produce a NEW fragment.
        var caps = OutputCapabilities.None with
                   {
                       TextSizing = new TextSizingCapabilities(Width: true, Scale: true),
                   };
        var content = new ScaledText("AB", new TextSizing(Scale: 2), Cursorial.Rendering.Fonts.FigletFonts.Standard);

        using var scene = Scene.Create(10, 4);
        scene.Draw(ctx => ctx.DrawContent(new Rect(0, 0, 4, 2), content, caps));
        var first = FirstFragment(scene);

        scene.Invalidate();
        var highlighted = Style.Default.WithBackground(Color.FromRgb(10, 20, 30));
        scene.Draw(ctx => ctx.DrawContent(new Rect(0, 0, 4, 2), content, caps, highlighted));

        var second = FirstFragment(scene);
        Assert.NotSame(first, second);
        Assert.Equal(highlighted, Assert.IsType<SizedTextFragment>(second).Style);
    }

    private static IBufferFragment FirstFragment(Scene scene)
    {
        foreach (var (_, entry) in scene.Buffer.Fragments)
            return entry.Fragment;
        Assert.Fail("no fragment registered on the scene");
        return null!;
    }

    // A real, decodable PNG (solid-color truecolor, filter 0) — the Sixel path decodes pixels,
    // so the header-only MinimalPng is not enough for it.
    private static byte[] SolidPng(int width, int height)
    {
        int stride = width * 3;
        var filtered = new byte[height * (stride + 1)];
        for (int y = 0; y < height; y++)
        {
            var row = filtered.AsSpan(y * (stride + 1));
            row[0] = 0; // filter: None
            for (int x = 0; x < width; x++)
            {
                row[1 + x * 3] = 200; row[2 + x * 3] = 40; row[3 + x * 3] = 90;
            }
        }

        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(filtered, 0, filtered.Length);
            compressed = ms.ToArray();
        }

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit truecolor
        WriteChunk(png, 0x49484452u, ihdr);
        WriteChunk(png, 0x49444154u, compressed);
        WriteChunk(png, 0x49454E44u, []);
        return png.ToArray();

        static void WriteChunk(Stream stream, uint type, byte[] data)
        {
            Span<byte> header = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header[..4], data.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.Slice(4, 4), type);
            stream.Write(header);
            stream.Write(data);
            stream.Write([0, 0, 0, 0]); // decoder skips CRC
        }
    }

    private static byte[] MinimalPng(int width, int height)
    {
        var bytes = new byte[33];
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        signature.CopyTo(bytes);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        bytes[24] = 8;
        bytes[25] = 2;
        return bytes;
    }

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
