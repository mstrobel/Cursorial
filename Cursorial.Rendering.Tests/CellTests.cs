using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Text;

namespace Cursorial.Tests.Rendering;

public class CellTests
{
    [Fact]
    public void DefaultCell_IsSingleWidthBlankWithDefaultStyle()
    {
        Cell c = default;
        Assert.Equal(CellKind.Single, c.Kind);
        Assert.Null(c.Grapheme);
        Assert.True(c.Style.IsDefault);
        Assert.True(c.IsBlank);
        Assert.Equal(1, c.Width);
    }

    [Fact]
    public void Blank_EqualsDefault()
    {
        Assert.Equal(Cell.Blank, default);
    }

    [Fact]
    public void WideContinuation_IsZeroWidth()
    {
        Assert.Equal(0, Cell.WideContinuation.Width);
        Assert.Equal(CellKind.WideContinuation, Cell.WideContinuation.Kind);
        Assert.False(Cell.WideContinuation.IsBlank);
    }

    [Fact]
    public void WideLeft_IsTwoCellsWide()
    {
        var cell = new Cell("中", CellKind.WideLeft, CellStyle.Default);
        Assert.Equal(2, cell.Width);
    }

    [Fact]
    public void Equality_IsByValue()
    {
        var a = new Cell("a", CellKind.Single, CellStyle.Default.WithAttributes(TextAttributes.Bold));
        var b = new Cell("a", CellKind.Single, CellStyle.Default.WithAttributes(TextAttributes.Bold));
        Assert.Equal(a, b);
        Assert.NotEqual(a, b with { Style = CellStyle.Default });
    }
}