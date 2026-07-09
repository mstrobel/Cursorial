using System.Buffers;
using System.Text;

using Cursorial.Output;

namespace Cursorial.Tests.Output;

public class ClipboardWriterTests
{
    private static string Encode(Action<IBufferWriter<byte>> action)
    {
        var w = new ArrayBufferWriter<byte>();
        action(w);
        return Encoding.ASCII.GetString(w.WrittenSpan);
    }

    [Fact]
    public void WriteSet_AsciiText_DefaultsToSystemTarget()
    {
        // "hi" → base64 "aGk=".
        var s = Encode(w => ClipboardWriter.WriteSet(w, "hi".AsSpan()));
        Assert.Equal("\x1b]52;c;aGk=\x1b\\", s);
    }

    [Fact]
    public void WriteSet_PrimaryTarget_UsesPSelectionCode()
    {
        var s = Encode(w => ClipboardWriter.WriteSet(w, "x".AsSpan(), ClipboardTarget.Primary));
        Assert.Equal("\x1b]52;p;eA==\x1b\\", s);
    }

    [Fact]
    public void WriteSet_Utf8Text_EncodesToBase64OfUtf8Bytes()
    {
        // "héllo" → UTF-8 = 68 c3 a9 6c 6c 6f → base64 = aMOpbGxv
        var s = Encode(w => ClipboardWriter.WriteSet(w, "héllo".AsSpan()));
        Assert.Equal("\x1b]52;c;aMOpbGxv\x1b\\", s);
    }

    [Fact]
    public void WriteSet_EmptyText_EmitsEmptyBase64Payload()
    {
        var s = Encode(w => ClipboardWriter.WriteSet(w, ReadOnlySpan<char>.Empty));
        Assert.Equal("\x1b]52;c;\x1b\\", s);
    }

    [Fact]
    public void WriteClear_EmitsEmptyBase64ForGivenTarget()
    {
        var s = Encode(w => ClipboardWriter.WriteClear(w, ClipboardTarget.Primary));
        Assert.Equal("\x1b]52;p;\x1b\\", s);
    }

    [Fact]
    public void WriteSet_LargePayloadBeyondInlineBuffer_StillCorrect()
    {
        // The writer uses a stackalloc for payloads <= 256 bytes and rents for larger ones.
        // Verify both paths agree on the encoding by giving a 1 KB ASCII payload.
        var text = new string('A', 1024); // 1024 A's → base64 of 1024 bytes of 0x41
        var s = Encode(w => ClipboardWriter.WriteSet(w, text.AsSpan()));

        Assert.StartsWith("\x1b]52;c;", s);
        Assert.EndsWith("\x1b\\", s);

        // Base64 of 1024 ASCII 0x41 bytes is "QUFB..." repeated; total length 1368 base64 chars
        // ((1024+2)/3 * 4 = 1368).
        int prefixLen = "\x1b]52;c;".Length;
        int terminatorLen = "\x1b\\".Length;
        int base64Len = s.Length - prefixLen - terminatorLen;
        Assert.Equal(1368, base64Len);
    }

    [Fact]
    public void WriteQuery_DefaultsToSystemTarget()
    {
        var s = Encode(w => ClipboardWriter.WriteQuery(w));
        Assert.Equal("\x1b]52;c;?\x1b\\", s);
    }

    [Fact]
    public void WriteQuery_PrimaryTarget_UsesPSelectionCode()
    {
        var s = Encode(w => ClipboardWriter.WriteQuery(w, ClipboardTarget.Primary));
        Assert.Equal("\x1b]52;p;?\x1b\\", s);
    }
}
