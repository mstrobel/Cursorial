using System.Buffers;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fragments;

namespace Cursorial.Tests.Rendering;

public class CellBufferFragmentTests
{
    [Fact]
    public void AddFragment_DoesNotModifyCells()
    {
        // Pure metadata registration — cells under the fragment region are left exactly as
        // the caller painted them so they can render through any portion of the fragment's
        // protocol payload that doesn't fully cover them.
        var buffer = new CellBuffer(10, 3);
        buffer.Set(0, 2, "a", Style.Default);
        buffer.Set(0, 3, "b", Style.Default);

        buffer.AddFragment(0, 2, new StubFragment(new Size(4, 2)));

        Assert.Equal("a", buffer[0, 2].Grapheme);
        Assert.Equal("b", buffer[0, 3].Grapheme);
        Assert.Equal(CellKind.Single, buffer[0, 2].Kind);
        Assert.Equal(CellKind.Single, buffer[0, 3].Kind);
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
    public void RemoveFragment_LeavesCellsUntouched()
    {
        // Removing a fragment is the inverse of adding it — cells remain as they were before
        // (and during) the fragment's registration. Callers who want a clean state should
        // explicitly repaint the region.
        var buffer = new CellBuffer(10, 3);
        buffer.Set(0, 0, "x", Style.Default.WithBackground(Color.FromRgb(40, 40, 40)));

        buffer.AddFragment(0, 0, new StubFragment(new Size(3, 1)));
        Assert.True(buffer.RemoveFragment(0, 0));

        Assert.Equal("x", buffer[0, 0].Grapheme);
        Assert.Equal(Color.FromRgb(40, 40, 40), buffer[0, 0].Style.Background);
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
    }

    [Fact]
    public void AddFragment_LeavesWideCellsAlone()
    {
        // Earlier behavior reset wide-cell halves at the fragment boundary to avoid orphan
        // continuation cells. With the pure-overlay model, the buffer doesn't touch any cells
        // — callers are responsible for not straddling wide cells across fragment boundaries
        // if they want clean visuals. Renderer's wide-glyph defense still keeps the
        // continuation-style consistent on emit.
        var buffer = new CellBuffer(10, 1);
        buffer.Set(0, 0, "中", Style.Default); // wide-left at (0,0), continuation at (0,1)

        buffer.AddFragment(0, 1, new StubFragment(new Size(1, 1)));

        // Cells are unchanged from before the fragment was added.
        Assert.Equal(CellKind.WideLeft, buffer[0, 0].Kind);
        Assert.Equal(CellKind.WideContinuation, buffer[0, 1].Kind);
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
