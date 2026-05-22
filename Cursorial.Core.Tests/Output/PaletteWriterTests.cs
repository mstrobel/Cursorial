using System.Buffers;
using System.Text;

using Cursorial.Output;

namespace Cursorial.Tests.Output;

public class PaletteWriterTests
{
    private static string AsAscii(byte[] bytes) => Encoding.ASCII.GetString(bytes);

    private static byte[] CaptureBytes(Action<IBufferWriter<byte>> writeAction)
    {
        var writer = new ArrayBufferWriter<byte>();
        writeAction(writer);
        return writer.WrittenSpan.ToArray();
    }

    [Fact]
    public void WriteSet_EmitsOscFourSequenceWithFourDigitChannels()
    {
        var bytes = CaptureBytes(w => PaletteWriter.WriteSet(w, 255, 0xAB, 0xCD, 0xEF));

        // ESC ] 4 ; 255 ; rgb:abab/cdcd/efef ESC \
        Assert.Equal("\x1b]4;255;rgb:abab/cdcd/efef\x1b\\", AsAscii(bytes));
    }

    [Fact]
    public void WriteSet_DuplicatesEachNibbleIntoXOneOneChannel()
    {
        // Channel 0x0F → "0f0f" (high nibble 0, low nibble F, repeated).
        var bytes = CaptureBytes(w => PaletteWriter.WriteSet(w, 1, 0x0F, 0xF0, 0x80));
        Assert.Equal("\x1b]4;1;rgb:0f0f/f0f0/8080\x1b\\", AsAscii(bytes));
    }

    [Fact]
    public void WriteSet_IndexZero()
    {
        var bytes = CaptureBytes(w => PaletteWriter.WriteSet(w, 0, 0x00, 0x00, 0x00));
        Assert.Equal("\x1b]4;0;rgb:0000/0000/0000\x1b\\", AsAscii(bytes));
    }

    [Fact]
    public void WriteQuery_EmitsOscFourQuerySequence()
    {
        var bytes = CaptureBytes(w => PaletteWriter.WriteQuery(w, 42));
        Assert.Equal("\x1b]4;42;?\x1b\\", AsAscii(bytes));
    }

    [Fact]
    public void WriteReset_EmitsOscOneZeroFourWithIndex()
    {
        var bytes = CaptureBytes(w => PaletteWriter.WriteReset(w, 7));
        Assert.Equal("\x1b]104;7\x1b\\", AsAscii(bytes));
    }

    [Fact]
    public void WriteResetAll_EmitsOscOneZeroFourWithNoIndex()
    {
        var bytes = CaptureBytes(w => PaletteWriter.WriteResetAll(w));
        Assert.Equal("\x1b]104\x1b\\", AsAscii(bytes));
    }

    [Fact]
    public void WriteResetMany_EmitsMultipleIndicesInOneSequence()
    {
        var bytes = CaptureBytes(w => PaletteWriter.WriteResetMany(w, [1, 2, 3, 100]));
        Assert.Equal("\x1b]104;1;2;3;100\x1b\\", AsAscii(bytes));
    }

    [Fact]
    public void WriteResetMany_EmptyIndices_EmitsNothing()
    {
        var bytes = CaptureBytes(w => PaletteWriter.WriteResetMany(w, []));
        Assert.Empty(bytes);
    }

    [Fact]
    public void WriteResetMany_OverScratchBudget_StillEmitsCorrectly()
    {
        // 256 indices forces the heap-fallback path (worst-case > 128-byte stack scratch).
        var indices = new byte[256];
        for (int i = 0; i < 256; i++) indices[i] = (byte) i;

        var bytes = CaptureBytes(w => PaletteWriter.WriteResetMany(w, indices));
        var ascii = AsAscii(bytes);

        Assert.StartsWith("\x1b]104;", ascii);
        Assert.EndsWith("\x1b\\", ascii);
        Assert.Contains(";255\x1b\\", ascii);
        Assert.Contains(";128;129;", ascii);
    }

    [Fact]
    public void WriteSet_NullWriter_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PaletteWriter.WriteSet(null!, 0, 0, 0, 0));
    }
}
