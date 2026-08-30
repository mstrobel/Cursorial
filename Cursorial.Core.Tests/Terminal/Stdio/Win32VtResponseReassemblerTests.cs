using System.Buffers;
using System.Text;

using Cursorial.Input.Events;
using Cursorial.Input.Parsing;
using Cursorial.Terminal.Stdio;
using Cursorial.Tests.Input.Parsing;

namespace Cursorial.Tests.Terminal.Stdio;

/// <summary>
/// <see cref="Win32VtResponseReassembler"/> — the native-console remedy for the DSR-CPR leak
/// (a terminal's <c>ESC[row;colR</c> reply arriving as per-character vk=0 records, which the Win32
/// Input Mode envelope would otherwise turn into literal <c>[3;1R</c> key text). Feeding those
/// characters one at a time must re-emit the exact ESC-introduced sequence raw, so the downstream
/// classifier + interpreter frame it as a <see cref="DeviceResponseKind.CursorPositionReport"/>.
/// </summary>
public class Win32VtResponseReassemblerTests
{
    /// <summary>Drive one char at a time, mirroring the per-record delivery, and return the raw bytes
    /// the reassembler chose to divert. Asserts every char inside the sequence was consumed.</summary>
    private static byte[] Feed(string sequence, bool expectAllConsumed = true)
    {
        var reassembler = new Win32VtResponseReassembler();
        var output = new ArrayBufferWriter<byte>();
        foreach (char c in sequence)
        {
            bool consumed = reassembler.TryConsume((byte) c, output);
            if (expectAllConsumed)
                Assert.True(consumed, $"expected '{c}' (0x{(int) c:X2}) to be consumed");
        }
        return output.WrittenSpan.ToArray();
    }

    [Fact]
    public void CprReply_ReassembledVerbatim_AndReassemblerReturnsToIdle()
    {
        var reassembler = new Win32VtResponseReassembler();
        var output = new ArrayBufferWriter<byte>();
        const string cpr = "\x1b[3;1R";

        foreach (char c in cpr)
            Assert.True(reassembler.TryConsume((byte) c, output));

        Assert.Equal(cpr, Encoding.ASCII.GetString(output.WrittenSpan));
        Assert.False(reassembler.InSequence); // the CSI final 'R' closed it
    }

    [Fact]
    public void StandaloneNonEscChar_IsNotConsumed()
    {
        // A genuine vk=0 ASCII character (not ESC-introduced) must fall through to the envelope path.
        var reassembler = new Win32VtResponseReassembler();
        var output = new ArrayBufferWriter<byte>();

        Assert.False(reassembler.TryConsume((byte) 'a', output));
        Assert.Equal(0, output.WrittenCount);
        Assert.False(reassembler.InSequence);
    }

    [Fact]
    public void Da1Reply_WithPrivatePrefix_Reassembled() =>
        Assert.Equal("\x1b[?65;1;9c", Encoding.ASCII.GetString(Feed("\x1b[?65;1;9c")));

    [Fact]
    public void Ss3FunctionKey_ThreeBytes_Reassembled()
    {
        var bytes = Feed("\x1bOP"); // SS3 F1
        Assert.Equal("\x1bOP", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void OscReply_BelTerminated_Reassembled()
    {
        var bytes = Feed("\x1b]11;rgb:1a1a/1b1b/2626\x07");
        Assert.Equal("\x1b]11;rgb:1a1a/1b1b/2626\x07", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void OscReply_StTerminated_Reassembled()
    {
        var bytes = Feed("\x1b]11;rgb:1a1a/1b1b/2626\x1b\\");
        Assert.Equal("\x1b]11;rgb:1a1a/1b1b/2626\x1b\\", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void BackToBackReplies_EachReassembledIndependently()
    {
        var reassembler = new Win32VtResponseReassembler();
        var output = new ArrayBufferWriter<byte>();
        const string two = "\x1b[3;1R\x1b[?64c";

        foreach (char c in two)
            Assert.True(reassembler.TryConsume((byte) c, output));

        Assert.Equal(two, Encoding.ASCII.GetString(output.WrittenSpan));
        Assert.False(reassembler.InSequence);
    }

    [Fact]
    public void Reset_AbandonsPartialSequence()
    {
        var reassembler = new Win32VtResponseReassembler();
        var output = new ArrayBufferWriter<byte>();

        Assert.True(reassembler.TryConsume(0x1B, output)); // ESC
        Assert.True(reassembler.TryConsume((byte) '[', output)); // into CSI
        Assert.True(reassembler.InSequence);

        reassembler.Reset();

        Assert.False(reassembler.InSequence);
        // After reset, a plain char is a standalone again (not swallowed as CSI content).
        Assert.False(reassembler.TryConsume((byte) 'x', output));
    }

    [Fact]
    public void ReassembledCpr_DecodesToDeviceResponse_NotKeyText()
    {
        // The end-to-end proof of the fix: characters delivered one-per-record on the native-console
        // path, reassembled, then run through the real classifier + interpreter, produce a CPR device
        // response — never the literal `[12;34R` key text that leaked before.
        byte[] raw = Feed("\x1b[12;34R");

        var sink = new RecordingInputEventSink();
        var classifier = new VtSequenceClassifier();
        var interpreter = new VtInputInterpreter(new VtInputMode(), sink, TimeProvider.System);
        classifier.Process(raw, interpreter);

        var response = sink.Single<DeviceResponseEvent>();
        Assert.Equal(DeviceResponseKind.CursorPositionReport, response.Kind);
        Assert.Equal("12;34", Encoding.ASCII.GetString(response.Payload.Span));
    }
}
