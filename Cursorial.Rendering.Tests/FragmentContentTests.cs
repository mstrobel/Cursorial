using System.Buffers;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fragments;

namespace Cursorial.Tests.Rendering;

public class FragmentContentTests
{
    // Regression guard for a pair of brain farts in FragmentContent.IsFragmentNeeded that made it
    // re-create its fragment every frame: (1) an inverted `ExistingFragment is not null` check, and
    // (2) a size comparison that recreated whenever the bounds exceeded the rendered fragment. The
    // per-frame RemoveFragment that resulted marked the footprint dirty, silently flipping the
    // renderer into dirty-region-only mode (freezing a ticking clock) and churning a fresh Kitty
    // image id per frame. The contract now: same available space → reuse; changed space → recreate.

    [Fact]
    public void RepaintWithSameBounds_ReusesFragment()
    {
        var buffer = new CellBuffer(10, 4);
        var content = new CountingContent();
        var bounds = new Rect(0, 0, 4, 2);

        content.Paint(buffer, bounds, Style.Default, OutputCapabilities.None);
        content.Paint(buffer, bounds, Style.Default, OutputCapabilities.None);
        content.Paint(buffer, bounds, Style.Default, OutputCapabilities.None);

        // Built once on the first paint; the later two paints reuse the cached fragment.
        Assert.Equal(1, content.CreateCount);
    }

    [Fact]
    public void RepaintWithChangedBounds_RecreatesFragment()
    {
        var buffer = new CellBuffer(10, 4);
        var content = new CountingContent();

        content.Paint(buffer, new Rect(0, 0, 4, 2), Style.Default, OutputCapabilities.None);
        content.Paint(buffer, new Rect(0, 0, 6, 3), Style.Default, OutputCapabilities.None);

        // The available space changed → a re-measure / recreate is required.
        Assert.Equal(2, content.CreateCount);
    }

    private sealed class CountingContent : FragmentContent
    {
        public int CreateCount { get; private set; }

        protected override Size MeasureOverride(Size availableSpace, OutputCapabilities capabilities, out bool canCreateFragment)
        {
            canCreateFragment = true;
            return availableSpace;
        }

        protected override IBufferFragment CreateFragment(in CellBufferView buffer, in Rect bounds, in Style style, OutputCapabilities capabilities)
        {
            CreateCount++;
            return new StubFragment(bounds.Size);
        }

        protected override IContent BuildPlaceholder(Size size, OutputCapabilities capabilities, in Style style)
            => throw new NotSupportedException("Placeholder not exercised by this test.");
    }

    private sealed class StubFragment(Size size) : IBufferFragment
    {
        public FragmentLayer Layer => FragmentLayer.Cells;
        public Size GetSize() => size;
        public bool IsSupported(OutputCapabilities capabilities) => true;
        public void Emit(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities) { }
    }
}
