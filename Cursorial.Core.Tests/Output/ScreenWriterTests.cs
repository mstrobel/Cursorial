using System.Buffers;
using System.Text;

using Cursorial.Output;

namespace Cursorial.Tests.Output;

public class ScreenWriterTests
{
    private static string Encode(Action<IBufferWriter<byte>> action)
    {
        var w = new ArrayBufferWriter<byte>();
        action(w);
        return Encoding.ASCII.GetString(w.WrittenSpan);
    }

    [Theory]
    [InlineData(nameof(ScreenWriter.WriteClearScreen),         "\x1b[2J")]
    [InlineData(nameof(ScreenWriter.WriteClearScreenAfter),    "\x1b[0J")]
    [InlineData(nameof(ScreenWriter.WriteClearScreenBefore),   "\x1b[1J")]
    [InlineData(nameof(ScreenWriter.WriteClearScreenAndScrollback), "\x1b[3J")]
    [InlineData(nameof(ScreenWriter.WriteClearLine),           "\x1b[2K")]
    [InlineData(nameof(ScreenWriter.WriteClearLineAfter),      "\x1b[0K")]
    [InlineData(nameof(ScreenWriter.WriteClearLineBefore),     "\x1b[1K")]
    public void ParameterlessWriters_EmitExpectedSequence(string methodName, string expected)
    {
        var method = typeof(ScreenWriter).GetMethod(methodName)!;
        var s = Encode(w => method.Invoke(null, [w]));
        Assert.Equal(expected, s);
    }

    [Fact]
    public void AlternateScreen_TogglesViaDec1049()
    {
        Assert.Equal("\x1b[?1049h", Encode(ScreenWriter.WriteEnterAlternateScreen));
        Assert.Equal("\x1b[?1049l", Encode(ScreenWriter.WriteLeaveAlternateScreen));
    }

    [Fact]
    public void WriteScrollRegion_TranslatesZeroBasedToOneBased()
    {
        Assert.Equal("\x1b[3;10r", Encode(w => ScreenWriter.WriteScrollRegion(w, 2, 9)));
    }

    [Fact]
    public void WriteResetScrollRegion_EmitsBareCsiR()
    {
        Assert.Equal("\x1b[r", Encode(ScreenWriter.WriteResetScrollRegion));
    }
}