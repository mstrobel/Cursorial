using System.Buffers;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fragments;

namespace Cursorial.Tests.Rendering;

public class ICellSurfaceTests
{
    // The supported way to write drawing code once against both surfaces: a generic method
    // constrained to ICellSurface. The JIT specializes the method per value type, so a
    // CellBufferView (readonly struct) flows through with NO boxing — unlike taking an
    // ICellSurface-typed parameter, which would box the struct.
    private static int DrawHeader<TSurface>(TSurface surface) where TSurface : ICellSurface
    {
        surface.Clear();
        surface.CursorVisible = false;
        return surface.Write(0, 0, "hi 中", Style.Default);   // h i space (3) + wide 中 (2) = 5
    }

    [Fact]
    public void BothSurfaces_ImplementTheContract()
    {
        Assert.True(typeof(ICellSurface).IsAssignableFrom(typeof(CellBuffer)));
        Assert.True(typeof(ICellSurface).IsAssignableFrom(typeof(CellBufferView)));
    }

    [Fact]
    public void CellBuffer_DrawsThroughGenericConstraint()
    {
        var buffer = new CellBuffer(10, 2);

        int advanced = DrawHeader(buffer);

        Assert.Equal(5, advanced);
        Assert.Equal("h", buffer[0, 0].Grapheme);
        Assert.Equal(CellKind.WideLeft, buffer[3, 0].Kind);
        Assert.Equal("中", buffer[3, 0].Grapheme);
        Assert.False(buffer.CursorVisible);
    }

    [Fact]
    public void CellBufferView_DrawsThroughGenericConstraint()
    {
        var buffer = new CellBuffer(10, 2);
        var view = buffer.View(2, 0, 6, 2);

        int advanced = DrawHeader(view);   // readonly struct via the constraint — no boxing

        Assert.Equal(5, advanced);
        Assert.Equal("h", buffer[2, 0].Grapheme);          // translated by the view origin
        Assert.Equal(CellKind.WideLeft, buffer[5, 0].Kind); // 2 + 3 (the wide glyph's left half)
        Assert.False(buffer.CursorVisible);
    }

    // The fragment methods are part of the contract too — register + look up through the same
    // generic constraint, for both the class and the readonly struct.
    private static bool RegisterAndFind<TSurface>(TSurface surface, IBufferFragment fragment)
        where TSurface : ICellSurface
        => surface.AddFragment(0, 0, fragment) && surface.ContainsFragment(fragment.Key);

    [Fact]
    public void FragmentMethods_WorkThroughGenericConstraint_ForBothSurfaces()
    {
        var buffer = new CellBuffer(10, 2);
        Assert.True(RegisterAndFind(buffer, new MarkerFragment()));

        var view = new CellBuffer(10, 2).View(0, 0, 6, 2);
        Assert.True(RegisterAndFind(view, new MarkerFragment()));
    }

    // View() is part of the contract too — a sub-view obtained through the constraint draws onto
    // the underlying buffer at the offset.
    private static void WriteToSubView<TSurface>(TSurface surface) where TSurface : ICellSurface
        => surface.View(2, 1, 4, 2).Write(0, 0, "x", Style.Default);

    [Fact]
    public void View_WorksThroughGenericConstraint_ForBothSurfaces()
    {
        var buffer = new CellBuffer(10, 4);
        WriteToSubView(buffer);
        Assert.Equal("x", buffer[2, 1].Grapheme);   // buffer.View(2,1,..) → local (0,0) = backing (2,1)

        var outer = new CellBuffer(10, 4);
        WriteToSubView(outer.View(1, 1, 8, 3));
        Assert.Equal("x", outer[3, 2].Grapheme);    // nested: (1,1) + (2,1) = (3,2)
    }

    private sealed class MarkerFragment : IBufferFragment
    {
        public Size GetSize() => new(1, 1);
        public bool IsSupported(OutputCapabilities capabilities) => true;
        public void Emit(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities) { }
    }
}
