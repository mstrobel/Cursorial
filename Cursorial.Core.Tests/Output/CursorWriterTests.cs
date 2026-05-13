using System.Buffers;
using System.Text;

using Cursorial.Output;

namespace Cursorial.Tests.Output;

public class CursorWriterTests
{
    private static string Encode(Action<IBufferWriter<byte>> action)
    {
        var w = new ArrayBufferWriter<byte>();
        action(w);
        return Encoding.ASCII.GetString(w.WrittenSpan);
    }

    [Fact]
    public void WriteMoveTo_ZeroBasedRowColTranslateToOneBasedOnWire()
    {
        Assert.Equal("\x1b[1;1H", Encode(w => CursorWriter.WriteMoveTo(w, 0, 0)));
        Assert.Equal("\x1b[5;10H", Encode(w => CursorWriter.WriteMoveTo(w, 4, 9)));
    }

    [Fact]
    public void WriteMoveTo_NegativeArgsClampToZero()
    {
        Assert.Equal("\x1b[1;1H", Encode(w => CursorWriter.WriteMoveTo(w, -5, -3)));
    }

    [Fact]
    public void WriteColumnAbsolute_TranslatesAndEmitsCha()
    {
        Assert.Equal("\x1b[12G", Encode(w => CursorWriter.WriteColumnAbsolute(w, 11)));
    }

    [Fact]
    public void WriteRowAbsolute_TranslatesAndEmitsVpa()
    {
        Assert.Equal("\x1b[7d", Encode(w => CursorWriter.WriteRowAbsolute(w, 6)));
    }

    [Theory]
    [InlineData(3, "\x1b[3A")]
    [InlineData(1, "\x1b[1A")]
    [InlineData(0, "")]
    [InlineData(-2, "")]
    public void WriteMoveUp_RelativeWithZeroSuppressedAndNegativeSuppressed(int n, string expected)
    {
        Assert.Equal(expected, Encode(w => CursorWriter.WriteMoveUp(w, n)));
    }

    [Fact]
    public void WriteMoveDown_LeftRight_EmitCorrectFinals()
    {
        Assert.Equal("\x1b[4B", Encode(w => CursorWriter.WriteMoveDown(w, 4)));
        Assert.Equal("\x1b[2C", Encode(w => CursorWriter.WriteMoveRight(w, 2)));
        Assert.Equal("\x1b[7D", Encode(w => CursorWriter.WriteMoveLeft(w, 7)));
    }

    [Fact]
    public void WriteSaveRestorePosition_EmitsEsc7AndEsc8()
    {
        // C# \x is greedy across hex digits; use \u for the ESC + ASCII-digit case to avoid
        // \x1b7 parsing as the single code point U+01B7.
        Assert.Equal("7", Encode(CursorWriter.WriteSavePosition));
        Assert.Equal("8", Encode(CursorWriter.WriteRestorePosition));
    }

    [Fact]
    public void WriteHideShow_EmitsDecPrivateMode25()
    {
        Assert.Equal("\x1b[?25l", Encode(CursorWriter.WriteHide));
        Assert.Equal("\x1b[?25h", Encode(CursorWriter.WriteShow));
    }

    [Theory]
    [InlineData(CursorShape.Default, "\x1b[0 q")]
    [InlineData(CursorShape.BlinkingBlock, "\x1b[1 q")]
    [InlineData(CursorShape.SteadyBlock, "\x1b[2 q")]
    [InlineData(CursorShape.SteadyBar, "\x1b[6 q")]
    public void WriteShape_EmitsDecScusr(CursorShape shape, string expected)
    {
        Assert.Equal(expected, Encode(w => CursorWriter.WriteShape(w, shape)));
    }
}
