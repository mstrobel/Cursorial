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
        Assert.Equal("\x1b]4;255;rgb:abab/cdcd/efef\x1b\a", AsAscii(bytes));
    }

    [Fact]
    public void WriteSet_DuplicatesEachNibbleIntoXOneOneChannel()
    {
        // Channel 0x0F → "0f0f" (high nibble 0, low nibble F, repeated).
        var bytes = CaptureBytes(w => PaletteWriter.WriteSet(w, 1, 0x0F, 0xF0, 0x80));
        Assert.Equal("\x1b]4;1;rgb:0f0f/f0f0/8080\x1b\a", AsAscii(bytes));
    }

    [Fact]
    public void WriteSet_IndexZero()
    {
        var bytes = CaptureBytes(w => PaletteWriter.WriteSet(w, 0, 0x00, 0x00, 0x00));
        Assert.Equal("\x1b]4;0;rgb:0000/0000/0000\x1b\a", AsAscii(bytes));
    }

    [Fact]
    public void WriteQuery_EmitsOscFourQuerySequence()
    {
        var bytes = CaptureBytes(w => PaletteWriter.WriteQuery(w, 42));
        Assert.Equal("\x1b]4;42;?\x1b\a", AsAscii(bytes));
    }

    [Fact]
    public void WriteReset_EmitsOscOneZeroFourWithIndex()
    {
        var bytes = CaptureBytes(w => PaletteWriter.WriteReset(w, 7));
        Assert.Equal("\x1b]104;7\x1b\a", AsAscii(bytes));
    }

    [Fact]
    public void WriteResetAll_EmitsOsc_104_110_111_112_WithNoIndex()
    {
        var bytes = CaptureBytes(w => PaletteWriter.WriteResetAll(w));
        Assert.Equal("\x1b]104\x1b\a\x1b]110\x1b\a\x1b]111\x1b\a\x1b]112\x1b\a", AsAscii(bytes));
    }

    [Fact]
    public void WriteResetMany_EmitsMultipleIndicesInOneSequence()
    {
        var bytes = CaptureBytes(w => PaletteWriter.WriteResetMany(w, [1, 2, 3, 100]));
        Assert.Equal("\x1b]104;1;2;3;100\x1b\a", AsAscii(bytes));
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
        Assert.EndsWith("\x1b\a", ascii);
        Assert.Contains(";255\x1b\a", ascii);
        Assert.Contains(";128;129;", ascii);
    }

    [Fact]
    public void WriteSet_NullWriter_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PaletteWriter.WriteSet(null!, 0, 0, 0, 0));
    }

    // ---- Default foreground / background / cursor (OSC 10 / 11 / 12 set, OSC 110 / 111 / 112 reset) ----

    [Fact]
    public void WriteSetForeground_EmitsOsc10WithRgbPayload()
    {
        var bytes = CaptureBytes(w => PaletteWriter.WriteSetForeground(w, 0xAB, 0xCD, 0xEF));
        Assert.Equal("\x1b]10;rgb:abab/cdcd/efef\x1b\a", AsAscii(bytes));
    }

    [Fact]
    public void WriteSetForeground_DuplicatesNibbles()
    {
        var bytes = CaptureBytes(w => PaletteWriter.WriteSetForeground(w, 0x0F, 0xF0, 0x80));
        Assert.Equal("\x1b]10;rgb:0f0f/f0f0/8080\x1b\a", AsAscii(bytes));
    }

    [Fact]
    public void WriteSetBackground_EmitsOsc11WithRgbPayload()
    {
        var bytes = CaptureBytes(w => PaletteWriter.WriteSetBackground(w, 0x12, 0x34, 0x56));
        Assert.Equal("\x1b]11;rgb:1212/3434/5656\x1b\a", AsAscii(bytes));
    }

    [Fact]
    public void WriteSetCursor_EmitsOsc12WithRgbPayload()
    {
        var bytes = CaptureBytes(w => PaletteWriter.WriteSetCursor(w, 0xDE, 0xAD, 0xBE));
        Assert.Equal("\x1b]12;rgb:dede/adad/bebe\x1b\a", AsAscii(bytes));
    }

    [Fact]
    public void WriteResetForeground_EmitsOsc110Bare()
    {
        var bytes = CaptureBytes(PaletteWriter.WriteResetForeground);
        Assert.Equal("\x1b]110\x1b\a", AsAscii(bytes));
    }

    [Fact]
    public void WriteResetBackground_EmitsOsc111Bare()
    {
        var bytes = CaptureBytes(PaletteWriter.WriteResetBackground);
        Assert.Equal("\x1b]111\x1b\a", AsAscii(bytes));
    }

    [Fact]
    public void WriteResetCursor_EmitsOsc112Bare()
    {
        var bytes = CaptureBytes(PaletteWriter.WriteResetCursor);
        Assert.Equal("\x1b]112\x1b\a", AsAscii(bytes));
    }

    [Fact]
    public void DefaultColorWriters_NullWriter_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PaletteWriter.WriteSetForeground(null!, 0, 0, 0));
        Assert.Throws<ArgumentNullException>(() => PaletteWriter.WriteSetBackground(null!, 0, 0, 0));
        Assert.Throws<ArgumentNullException>(() => PaletteWriter.WriteSetCursor(null!, 0, 0, 0));
        Assert.Throws<ArgumentNullException>(() => PaletteWriter.WriteResetForeground(null!));
        Assert.Throws<ArgumentNullException>(() => PaletteWriter.WriteResetBackground(null!));
        Assert.Throws<ArgumentNullException>(() => PaletteWriter.WriteResetCursor(null!));
    }
}
