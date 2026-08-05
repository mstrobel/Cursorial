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
        buffer.Set(2, 0, "a", Style.Default);
        buffer.Set(3, 0, "b", Style.Default);

        buffer.AddFragment(2, 0, new StubFragment(new Size(4, 2)));

        Assert.Equal("a", buffer[2, 0].Grapheme);
        Assert.Equal("b", buffer[3, 0].Grapheme);
        Assert.Equal(CellKind.Single, buffer[2, 0].Kind);
        Assert.Equal(CellKind.Single, buffer[3, 0].Kind);
    }

    [Fact]
    public void AddFragment_RegistersInSidecar()
    {
        var buffer = new CellBuffer(10, 3);
        var fragment = new StubFragment(new Size(2, 1));

        buffer.AddFragment(4, 1, fragment);

        Assert.True(buffer.Fragments.ContainsKey((4, 1)));
        Assert.Same(fragment, buffer.Fragments[(4, 1)].Fragment);
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
        // if they want clean visuals.
        var buffer = new CellBuffer(10, 1);
        buffer.Set(0, 0, "中", Style.Default); // wide-left at (0,0), continuation at (0,1)

        buffer.AddFragment(1, 0, new StubFragment(new Size(1, 1)));

        // Cells are unchanged from before the fragment was added.
        Assert.Equal(CellKind.WideLeft, buffer[0, 0].Kind);
        Assert.Equal(CellKind.WideContinuation, buffer[1, 0].Kind);
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

    // ---- Key lookup ----

    [Fact]
    public void ContainsFragment_ReturnsTrueAfterAdd()
    {
        var buffer = new CellBuffer(10, 3);
        var fragment = new StubFragment(new Size(2, 1));
        buffer.AddFragment(4, 1, fragment);

        Assert.True(buffer.ContainsFragment(fragment.Key));
    }

    [Fact]
    public void ContainsFragment_ReturnsFalseForUnknownKey()
    {
        var buffer = new CellBuffer(10, 3);
        buffer.AddFragment(0, 0, new StubFragment(new Size(1, 1)));

        Assert.False(buffer.ContainsFragment(new object()));
    }

    [Fact]
    public void TryGetFragmentAnchor_ReturnsRegisteredAnchor()
    {
        var buffer = new CellBuffer(10, 3);
        var fragment = new StubFragment(new Size(2, 1));
        buffer.AddFragment(4, 1, fragment);

        Assert.True(buffer.TryGetFragmentAnchor(fragment.Key, out var anchor));
        Assert.Equal((4, 1), anchor);
    }

    [Fact]
    public void TryGetFragmentAnchor_ReturnsFalseAfterRemove()
    {
        var buffer = new CellBuffer(10, 3);
        var fragment = new StubFragment(new Size(2, 1));
        buffer.AddFragment(4, 1, fragment);
        buffer.RemoveFragment(4, 1);

        Assert.False(buffer.TryGetFragmentAnchor(fragment.Key, out _));
        Assert.False(buffer.ContainsFragment(fragment.Key));
    }

    [Fact]
    public void AddFragment_ReplacingExistingAnchor_DropsOldKeyFromIndex()
    {
        // Replacing the fragment at an existing anchor must purge the previous fragment's Key
        // from the secondary index — otherwise stale Keys would still resolve and the
        // ContainsFragment / TryGetFragmentAnchor contracts would lie.
        var buffer = new CellBuffer(10, 3);
        var first = new StubFragment(new Size(2, 1));
        var second = new StubFragment(new Size(3, 1));

        buffer.AddFragment(0, 0, first);
        buffer.AddFragment(0, 0, second);

        Assert.False(buffer.ContainsFragment(first.Key));
        Assert.True(buffer.ContainsFragment(second.Key));
        Assert.True(buffer.TryGetFragmentAnchor(second.Key, out var anchor));
        Assert.Equal((0, 0), anchor);
    }

    [Fact]
    public void Clear_AlsoClearsKeyIndex()
    {
        var buffer = new CellBuffer(5, 1);
        var fragment = new StubFragment(new Size(2, 1));
        buffer.AddFragment(0, 0, fragment);
        buffer.Clear();

        Assert.False(buffer.ContainsFragment(fragment.Key));
    }

    [Fact]
    public void Resize_AlsoClearsKeyIndex()
    {
        var buffer = new CellBuffer(5, 1);
        var fragment = new StubFragment(new Size(2, 1));
        buffer.AddFragment(0, 0, fragment);
        buffer.Resize(10, 2);

        Assert.False(buffer.ContainsFragment(fragment.Key));
    }

    private sealed class StubFragment(Size size) : IBufferFragment
    {
        public object Key => this;
        public Size GetSize() => size;
        public bool IsSupported(OutputCapabilities capabilities) => true;
        public void Emit(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities) { }
    }
}
