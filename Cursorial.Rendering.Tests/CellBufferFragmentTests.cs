using System.Buffers;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fragments;

namespace Cursorial.Tests.Rendering;

public class CellBufferFragmentTests
{
    [Fact]
    public void AddFragment_MarksCoveredCells()
    {
        var buffer = new CellBuffer(10, 3);
        var fragment = new StubFragment(new Size(4, 2));

        buffer.AddFragment(0, 2, fragment);

        for (int r = 0; r < 2; r++)
            for (int c = 2; c < 6; c++)
                Assert.Equal(CellKind.CoveredByFragment, buffer[r, c].Kind);

        // Cells outside the bounds are still blank.
        Assert.Equal(CellKind.Single, buffer[0, 1].Kind);
        Assert.Equal(CellKind.Single, buffer[0, 6].Kind);
        Assert.Equal(CellKind.Single, buffer[2, 2].Kind);
    }

    [Fact]
    public void AddFragment_RegistersInSidecar()
    {
        var buffer = new CellBuffer(10, 3);
        var fragment = new StubFragment(new Size(2, 1));

        buffer.AddFragment(1, 4, fragment);

        Assert.True(buffer.Fragments.ContainsKey((1, 4)));
        Assert.Same(fragment, buffer.Fragments[(1, 4)].Fragment);
    }

    [Fact]
    public void RemoveFragment_ClearsCoveredCells()
    {
        var buffer = new CellBuffer(10, 3);
        var fragment = new StubFragment(new Size(3, 1));

        buffer.AddFragment(0, 0, fragment);
        Assert.True(buffer.RemoveFragment(0, 0));

        for (int c = 0; c < 3; c++)
            Assert.Equal(CellKind.Single, buffer[0, c].Kind);
        Assert.False(buffer.Fragments.ContainsKey((0, 0)));
    }

    [Fact]
    public void RemoveFragment_NoMatch_ReturnsFalse()
    {
        var buffer = new CellBuffer(10, 3);
        Assert.False(buffer.RemoveFragment(0, 0));
    }

    [Fact]
    public void AddFragment_OverwritesExisting()
    {
        var buffer = new CellBuffer(10, 3);
        var first = new StubFragment(new Size(5, 1));
        var second = new StubFragment(new Size(2, 1));

        buffer.AddFragment(0, 0, first);
        buffer.AddFragment(0, 0, second);

        // Replaced — only one entry, and it's the second.
        Assert.Single(buffer.Fragments);
        Assert.Same(second, buffer.Fragments[(0, 0)].Fragment);

        // Old coverage region (cells 0–4) should reflect the new size: cols 0–1 covered,
        // cols 2–4 reset to blank because the first fragment's coverage was cleared.
        Assert.Equal(CellKind.CoveredByFragment, buffer[0, 0].Kind);
        Assert.Equal(CellKind.CoveredByFragment, buffer[0, 1].Kind);
        Assert.Equal(CellKind.Single, buffer[0, 2].Kind);
    }

    [Fact]
    public void AddFragment_CleansUpWideCells()
    {
        var buffer = new CellBuffer(10, 1);
        buffer.Set(0, 0, "中", Style.Default); // wide cell at (0,0) + continuation at (0,1)

        var fragment = new StubFragment(new Size(1, 1));
        buffer.AddFragment(0, 1, fragment); // anchor on the continuation half

        // (0,1) is now the fragment cover. (0,0) must be reset to blank — leaving it as a
        // wide-left without its right half would be visually corrupting.
        Assert.Equal(CellKind.CoveredByFragment, buffer[0, 1].Kind);
        Assert.Equal(CellKind.Single, buffer[0, 0].Kind);
    }

    [Fact]
    public void Clear_RemovesFragments()
    {
        var buffer = new CellBuffer(5, 1);
        buffer.AddFragment(0, 0, new StubFragment(new Size(2, 1)));
        buffer.Clear();

        Assert.Empty(buffer.Fragments);
    }

    [Fact]
    public void Resize_RemovesFragments()
    {
        var buffer = new CellBuffer(5, 1);
        buffer.AddFragment(0, 0, new StubFragment(new Size(2, 1)));
        buffer.Resize(10, 2);

        Assert.Empty(buffer.Fragments);
    }

    private sealed class StubFragment(Size size) : IBufferFragment
    {
        public Size GetSize() => size;
        public bool IsSupported(OutputCapabilities capabilities) => true;
        public void Emit(int row, int column, IBufferWriter<byte> output, OutputCapabilities capabilities) { }
    }
}
