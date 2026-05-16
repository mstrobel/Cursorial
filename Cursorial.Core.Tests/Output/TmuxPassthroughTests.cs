using System.Buffers;
using System.Text;
using Cursorial.Output;

namespace Cursorial.Tests.Output;

public class TmuxPassthroughTests
{
    private static string WrapToString(ReadOnlySpan<byte> inner)
    {
        var w = new ArrayBufferWriter<byte>();
        TmuxPassthrough.WriteWrapped(w, inner);
        return Encoding.ASCII.GetString(w.WrittenSpan);
    }

    [Fact]
    public void WriteWrapped_EmptyInput_EmitsBareEnvelope()
    {
        var output = WrapToString(ReadOnlySpan<byte>.Empty);

        // ESC P tmux ; ESC \ — just the framing, no body.
        Assert.Equal("\x1bPtmux;\x1b\\", output);
    }

    [Fact]
    public void WriteWrapped_AsciiOnlyInput_PassesThroughVerbatim()
    {
        // Nothing to escape in pure ASCII.
        var output = WrapToString("hello"u8);
        Assert.Equal("\x1bPtmux;hello\x1b\\", output);
    }

    [Fact]
    public void WriteWrapped_SingleEsc_IsDoubled()
    {
        // One ESC byte in the input → two ESC bytes in the body.
        var inner = new byte[] { 0x1B };
        var output = WrapToString(inner);
        Assert.Equal("\x1bPtmux;\x1b\x1b\x1b\\", output);
    }

    [Fact]
    public void WriteWrapped_TypicalCsi_EscDoubled()
    {
        // CSI 0 m — SGR reset. ESC [ 0 m. The leading ESC gets doubled.
        var output = WrapToString("\x1b[0m"u8);
        Assert.Equal("\x1bPtmux;\x1b\x1b[0m\x1b\\", output);
    }

    [Fact]
    public void WriteWrapped_InnerDcsTerminator_EscDoubledOverTerminator()
    {
        // Inner sequence that ends with ESC \ (e.g. APC or OSC). The inner ESC \ would
        // otherwise terminate the outer DCS prematurely — but ESC doubling moves the inner
        // ESC out of "DCS terminator" parse state.
        var output = WrapToString("\x1b_Gtest\x1b\\"u8);
        // Each ESC doubled: inner ESC at start, inner ESC at end. Then outer terminator.
        Assert.Equal("\x1bPtmux;\x1b\x1b_Gtest\x1b\x1b\\\x1b\\", output);
    }

    [Fact]
    public void WriteWrapped_MultipleConsecutiveEscs_AllDoubled()
    {
        var inner = new byte[] { 0x1B, 0x1B };
        var output = WrapToString(inner);
        // Two ESCs → four ESCs in the body.
        Assert.Equal("\x1bPtmux;\x1b\x1b\x1b\x1b\x1b\\", output);
    }
}
