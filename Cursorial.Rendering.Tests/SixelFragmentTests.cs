using System.Buffers;
using System.Text;

using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fragments;

namespace Cursorial.Tests.Rendering;

public class SixelFragmentTests
{
    // A trivial pre-encoded Sixel envelope: DCS q ESC \. Real Sixel payloads also include
    // raster attributes and pixel data, but the fragment treats the bytes opaquely — only the
    // framing needs to round-trip correctly through Emit + multiplexer wrap.
    private static readonly byte[] SamplePayload =
        [0x1B, (byte) 'P', (byte) 'q', (byte) '?', (byte) '?', 0x1B, (byte) '\\'];

    private static OutputCapabilities CapsWithSixel(bool sixel, bool multiplexer = false) =>
        OutputCapabilities.None with
        {
            Graphics = new GraphicsCapabilities(Sixel: sixel, KittyGraphics: false, ITerm2InlineImages: false),
            Protocol = OutputCapabilities.None.Protocol with { MultiplexerPassthrough = multiplexer },
        };

    [Fact]
    public void Constructor_EmptyPayload_Throws()
    {
        Assert.Throws<ArgumentException>(() => new SixelFragment(ReadOnlyMemory<byte>.Empty, new Size(4, 2)));
    }

    [Fact]
    public void Constructor_ZeroCellSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SixelFragment(SamplePayload, default));
    }

    [Fact]
    public void GetSize_ReturnsConstructorValue()
    {
        var fragment = new SixelFragment(SamplePayload, new Size(8, 4));
        Assert.Equal(new Size(8, 4), fragment.GetSize());
    }

    [Fact]
    public void IsSupported_GatedOnGraphicsSixel()
    {
        var fragment = new SixelFragment(SamplePayload, new Size(4, 2));

        Assert.True(fragment.IsSupported(CapsWithSixel(sixel: true)));
        Assert.False(fragment.IsSupported(CapsWithSixel(sixel: false)));
    }

    [Fact]
    public void Emit_WithoutMultiplexer_WritesPayloadVerbatim()
    {
        var fragment = new SixelFragment(SamplePayload, new Size(4, 2));
        var writer = new ArrayBufferWriter<byte>();

        fragment.Emit(0, 0, writer, CapsWithSixel(sixel: true));

        Assert.Equal(SamplePayload, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void Emit_InsideMultiplexer_WrapsInTmuxPassthroughWithDoubledEscapes()
    {
        var fragment = new SixelFragment(SamplePayload, new Size(4, 2));
        var writer = new ArrayBufferWriter<byte>();

        fragment.Emit(0, 0, writer, CapsWithSixel(sixel: true, multiplexer: true));

        var output = Encoding.ASCII.GetString(writer.WrittenSpan);

        // The tmux passthrough envelope: ESC P tmux ; <doubled ESCs> ESC \.
        Assert.Contains("Ptmux;", output);
        // The inner DCS opener (ESC P) gets its ESC doubled by the wrapper.
        Assert.Contains("\x1b\x1bP", output);
    }
}
