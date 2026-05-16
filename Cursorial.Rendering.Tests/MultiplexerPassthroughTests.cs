using System.Buffers;
using System.Text;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fragments;

namespace Cursorial.Tests.Rendering;

public class MultiplexerPassthroughTests
{
    private static OutputCapabilities CapsWithPassthrough(bool passthrough)
        => OutputCapabilities.None with
           {
               Graphics = new GraphicsCapabilities(Sixel: false, KittyGraphics: true, ITerm2InlineImages: true),
               Protocol = OutputProtocolCapabilities.None with { MultiplexerPassthrough = passthrough },
           };

    private static string EmitKitty(byte[] bytes, bool passthrough)
    {
        var data = new ImageData(bytes, ImageFormat.Png, new Size(5, 3));
        var fragment = new KittyImageFragment(data);
        var w = new ArrayBufferWriter<byte>();
        fragment.Emit(0, 0, w, CapsWithPassthrough(passthrough));
        return Encoding.ASCII.GetString(w.WrittenSpan);
    }

    private static string EmitIterm2(byte[] bytes, bool passthrough)
    {
        var data = new ImageData(bytes, ImageFormat.Png, new Size(5, 3));
        var fragment = new ITerm2ImageFragment(data);
        var w = new ArrayBufferWriter<byte>();
        fragment.Emit(0, 0, w, CapsWithPassthrough(passthrough));
        return Encoding.ASCII.GetString(w.WrittenSpan);
    }

    [Fact]
    public void KittyFragment_WithoutPassthrough_EmitsRawApc()
    {
        var output = EmitKitty([1, 2, 3], passthrough: false);

        // APC framing: ESC _ G ... ESC \ — no DCS envelope.
        Assert.Contains("_G", output);
        Assert.DoesNotContain("Ptmux;", output);
    }

    [Fact]
    public void KittyFragment_WithPassthrough_WrapsApcInDcs()
    {
        var output = EmitKitty([1, 2, 3], passthrough: true);

        // The wrap helper emits "ESC P tmux ;" as the envelope intro.
        Assert.Contains("Ptmux;", output);
        // Inner APC's ESC bytes get doubled — the _G prefix is preceded by two ESCs total
        // (the leading ESC of the APC, doubled).
        Assert.Contains("\x1b\x1b_G", output);
    }

    [Fact]
    public void KittyFragment_LargePayload_WrapsEachChunk()
    {
        // A payload large enough to chunk (≥ 4096 base64 bytes ≈ 3072 raw bytes) should emit
        // multiple wrapped APCs.
        var bytes = new byte[6000];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte) (i & 0xFF);

        var output = EmitKitty(bytes, passthrough: true);

        // Multiple Ptmux envelopes (one per chunk).
        int envelopeCount = 0;
        int idx = 0;
        while ((idx = output.IndexOf("Ptmux;", idx, StringComparison.Ordinal)) >= 0)
        {
            envelopeCount++;
            idx += 6;
        }
        Assert.True(envelopeCount >= 2, $"Expected ≥ 2 wrapped chunks, got {envelopeCount}.");
    }

    [Fact]
    public void Iterm2Fragment_WithoutPassthrough_EmitsRawOsc()
    {
        var output = EmitIterm2([1, 2, 3], passthrough: false);

        Assert.Contains("]1337;File=", output);
        Assert.DoesNotContain("Ptmux;", output);
    }

    [Fact]
    public void Iterm2Fragment_WithPassthrough_WrapsOscInDcs()
    {
        var output = EmitIterm2([1, 2, 3], passthrough: true);

        Assert.Contains("Ptmux;", output);
        // The OSC's leading ESC is now preceded by an extra ESC (doubled).
        Assert.Contains("\x1b\x1b]1337;File=", output);
    }
}
