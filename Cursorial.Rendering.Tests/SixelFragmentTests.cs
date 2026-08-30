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

    // ---- Clip identity (the occlusion-flicker fix) -------------------------------
    //
    // A clip re-crops + re-encodes into a brand-new instance every composite pass. The FrameRenderer's
    // fragment diff is Key-equality, so a clipped fragment must key off (source identity, visible rect):
    // otherwise a static image under a static clip re-emits the whole envelope every frame (the banner
    // flickering under a clipping dialog).

    private static byte[] SolidRgba(int width, int height, byte r = 0x40, byte g = 0x80, byte b = 0xC0)
    {
        var px = new byte[width * height * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 0xFF;
        }
        return px;
    }

    private static SixelFragment RgbaSource() => new(SolidRgba(8, 8), 8, 8, new Size(4, 2));

    [Fact]
    public void Clip_SameVisibleRect_FreshInstances_HaveValueEqualKeys()
    {
        var source = RgbaSource();
        var rect = new Rect(0, 0, 2, 1);

        var a = source.Clip(rect)!;
        var b = source.Clip(rect)!;

        Assert.NotSame(a, b);                    // a clip re-encodes into a new instance each call …
        Assert.True(Equals(a.Key, b.Key));       // … but the diff key is value-equal, so FragmentsMatch skips
    }

    [Fact]
    public void Clip_DifferentVisibleRect_HaveDifferentKeys()
    {
        var source = RgbaSource();

        var a = source.Clip(new Rect(0, 0, 2, 1))!;
        var b = source.Clip(new Rect(0, 0, 1, 1))!;

        Assert.False(Equals(a.Key, b.Key)); // a genuinely different crop must re-emit
    }

    [Fact]
    public void Clip_SameContentDifferentSourceInstances_SameRect_HaveEqualKeys()
    {
        // Content-derived: two DISTINCT source instances with identical pixels (e.g. the banner re-rastered)
        // clipped to the same rect produce an equal key, so the diff skips — an upgrade over reference
        // identity, which re-transmitted on every re-raster.
        var rect = new Rect(0, 0, 2, 1);

        var a = RgbaSource().Clip(rect)!;
        var b = RgbaSource().Clip(rect)!;

        Assert.True(Equals(a.Key, b.Key));
    }

    [Fact]
    public void Clip_DifferentContent_SameRect_HaveDifferentKeys()
    {
        // Different source pixels → different source content key → different clip key → re-emit.
        var rect = new Rect(0, 0, 2, 1);

        var a = new SixelFragment(SolidRgba(8, 8), 8, 8, new Size(4, 2)).Clip(rect)!;
        var b = new SixelFragment(SolidRgba(8, 8, 0xFF, 0x00, 0x00), 8, 8, new Size(4, 2)).Clip(rect)!;

        Assert.False(Equals(a.Key, b.Key));
    }

    [Fact]
    public void UnclippedFragment_KeyIsContentDerived()
    {
        // Matches Kitty / iTerm2: identical content compares equal (an identical reconstruction diff-skips);
        // different content or size differs.
        Assert.Equal(RgbaSource().Key, RgbaSource().Key);
        Assert.NotEqual(RgbaSource().Key,
                        new SixelFragment(SolidRgba(8, 8, 0xFF, 0x00, 0x00), 8, 8, new Size(4, 2)).Key);
        Assert.NotEqual(RgbaSource().Key,
                        new SixelFragment(SolidRgba(8, 8), 8, 8, new Size(5, 2)).Key);
    }
}
