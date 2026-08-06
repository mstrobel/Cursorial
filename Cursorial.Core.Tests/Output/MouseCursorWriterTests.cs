using System.Buffers;
using System.Text;

using Cursorial.Media;
using Cursorial.Output;

namespace Cursorial.Tests.Output;

public class MouseCursorWriterTests
{
    private static string Encode(Action<IBufferWriter<byte>> action)
    {
        var w = new ArrayBufferWriter<byte>();
        action(w);
        return Encoding.UTF8.GetString(w.WrittenSpan);
    }

    [Fact]
    public void WriteSet_SingleShape_EmitsOscWithCssName()
    {
        var s = Encode(w => MouseCursorWriter.WriteSet(w, MouseCursorShape.Pointer));
        Assert.Equal("\x1b]22;pointer\x1b\\", s);
    }

    [Fact]
    public void WriteSet_TextShape_EmitsIBeamCssName()
    {
        // The CSS name for the I-beam is "text", not "ibeam" — verifying we send the canonical
        // protocol identifier since UI frameworks (e.g. Avalonia) call this Ibeam.
        var s = Encode(w => MouseCursorWriter.WriteSet(w, MouseCursorShape.Text));
        Assert.Equal("\x1b]22;text\x1b\\", s);
    }

    [Fact]
    public void WriteSet_MultiShapeFallbackChain_CommaSeparated()
    {
        var s = Encode(w =>
            MouseCursorWriter.WriteSet(
                w,
                [MouseCursorShape.Progress, MouseCursorShape.Wait, MouseCursorShape.Default]));
        Assert.Equal("\x1b]22;progress,wait,default\x1b\\", s);
    }

    [Fact]
    public void WriteSet_EmptyFallbackChain_Throws()
    {
        var buffer = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentException>(
            () => MouseCursorWriter.WriteSet(buffer, ReadOnlySpan<MouseCursorShape>.Empty));
    }

    [Fact]
    public void WritePush_SingleShape_PrependsGreaterThanOp()
    {
        var s = Encode(w => MouseCursorWriter.WritePush(w, MouseCursorShape.Text));
        Assert.Equal("\x1b]22;>text\x1b\\", s);
    }

    [Fact]
    public void WritePush_MultiShape_OpThenCommaList()
    {
        var s = Encode(w =>
            MouseCursorWriter.WritePush(w, [MouseCursorShape.NotAllowed, MouseCursorShape.NoDrop]));
        Assert.Equal("\x1b]22;>not-allowed,no-drop\x1b\\", s);
    }

    [Fact]
    public void WritePop_EmitsLessThanOp()
    {
        var s = Encode(MouseCursorWriter.WritePop);
        Assert.Equal("\x1b]22;<\x1b\\", s);
    }

    [Fact]
    public void WriteReset_EmitsEmptyShapeList()
    {
        var s = Encode(MouseCursorWriter.WriteReset);
        Assert.Equal("\x1b]22;\x1b\\", s);
    }

    [Theory]
    [InlineData(MouseCursorShape.Default, "default")]
    [InlineData(MouseCursorShape.None, "none")]
    [InlineData(MouseCursorShape.ContextMenu, "context-menu")]
    [InlineData(MouseCursorShape.Help, "help")]
    [InlineData(MouseCursorShape.Pointer, "pointer")]
    [InlineData(MouseCursorShape.Progress, "progress")]
    [InlineData(MouseCursorShape.Wait, "wait")]
    [InlineData(MouseCursorShape.Cell, "cell")]
    [InlineData(MouseCursorShape.Crosshair, "crosshair")]
    [InlineData(MouseCursorShape.Text, "text")]
    [InlineData(MouseCursorShape.VerticalText, "vertical-text")]
    [InlineData(MouseCursorShape.Alias, "alias")]
    [InlineData(MouseCursorShape.Copy, "copy")]
    [InlineData(MouseCursorShape.Move, "move")]
    [InlineData(MouseCursorShape.NoDrop, "no-drop")]
    [InlineData(MouseCursorShape.NotAllowed, "not-allowed")]
    [InlineData(MouseCursorShape.Grab, "grab")]
    [InlineData(MouseCursorShape.Grabbing, "grabbing")]
    [InlineData(MouseCursorShape.EResize, "e-resize")]
    [InlineData(MouseCursorShape.NResize, "n-resize")]
    [InlineData(MouseCursorShape.NeResize, "ne-resize")]
    [InlineData(MouseCursorShape.NwResize, "nw-resize")]
    [InlineData(MouseCursorShape.SResize, "s-resize")]
    [InlineData(MouseCursorShape.SeResize, "se-resize")]
    [InlineData(MouseCursorShape.SwResize, "sw-resize")]
    [InlineData(MouseCursorShape.WResize, "w-resize")]
    [InlineData(MouseCursorShape.EwResize, "ew-resize")]
    [InlineData(MouseCursorShape.NsResize, "ns-resize")]
    [InlineData(MouseCursorShape.NeswResize, "nesw-resize")]
    [InlineData(MouseCursorShape.NwseResize, "nwse-resize")]
    [InlineData(MouseCursorShape.ColResize, "col-resize")]
    [InlineData(MouseCursorShape.RowResize, "row-resize")]
    [InlineData(MouseCursorShape.AllScroll, "all-scroll")]
    [InlineData(MouseCursorShape.ZoomIn, "zoom-in")]
    [InlineData(MouseCursorShape.ZoomOut, "zoom-out")]
    public void WriteSet_CoversEveryShape(MouseCursorShape shape, string expectedCssName)
    {
        var s = Encode(w => MouseCursorWriter.WriteSet(w, shape));
        Assert.Equal($"\x1b]22;{expectedCssName}\x1b\\", s);
    }

    [Fact]
    public void WriteSet_UndefinedEnumValue_Throws()
    {
        var buffer = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MouseCursorWriter.WriteSet(buffer, (MouseCursorShape) 999));
    }
}
