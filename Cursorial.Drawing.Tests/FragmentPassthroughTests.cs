using System.Buffers;

using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fragments;

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

        Assert.Equal(1, target.AsView().Fragments.Count);              // no stale fragment left behind
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
