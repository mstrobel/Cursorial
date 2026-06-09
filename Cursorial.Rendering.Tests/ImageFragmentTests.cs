using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fragments;
using Cursorial.Rendering.Imaging;

namespace Cursorial.Tests.Rendering;

public class KittyImageFragmentTests
{
    private static byte[] FakePng(int seed = 1)
    {
        // Doesn't actually need to be a valid PNG for the fragment's wire-format tests — the
        // fragment just base64-encodes whatever bytes we give it. Make the payload large
        // enough that we exercise the chunking boundary (≥ 4096 base64 bytes → ≥ 3072 raw).
        var bytes = new byte[6000];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte) ((i * seed) & 0xFF);
        return bytes;
    }

    [Fact]
    public void GetSize_MatchesImageDataCellSize()
    {
        var data = new ImageData(FakePng(), ImageFormat.Png, new Size(20, 10));
        var fragment = new KittyImageFragment(data);
        Assert.Equal(new Size(20, 10), fragment.GetSize());
    }

    [Fact]
    public void IsSupported_GatedOnKittyGraphics()
    {
        var data = new ImageData([1, 2, 3], ImageFormat.Png, new Size(5, 5));
        var fragment = new KittyImageFragment(data);

        var caps = OutputCapabilities.None with
                   {
                       Graphics = new GraphicsCapabilities(Sixel: false, KittyGraphics: true, ITerm2InlineImages: false),
                   };
        Assert.True(fragment.IsSupported(caps));

        Assert.False(fragment.IsSupported(OutputCapabilities.None));
    }

    [Fact]
    public void Emit_FirstChunkHasFullHeader()
    {
        var data = new ImageData([1, 2, 3], ImageFormat.Png, new Size(20, 10));
        var fragment = new KittyImageFragment(data);
        var w = new ArrayBufferWriter<byte>();
        fragment.Emit(0, 0, w, OutputCapabilities.None);
        var text = Encoding.ASCII.GetString(w.WrittenSpan);

        // APC introducer: ESC _ G ...
        Assert.StartsWith("_G", text);
        // Full transmit header
        Assert.Contains("a=T", text);
        Assert.Contains("f=100", text);
        Assert.Contains("c=20", text);
        Assert.Contains("r=10", text);
        Assert.Contains("q=2", text);
        // String terminator
        Assert.EndsWith("\\", text);
    }

    [Fact]
    public void Emit_LargePayload_ChunksAcross4096ByteBoundary()
    {
        var data = new ImageData(FakePng(), ImageFormat.Png, new Size(20, 10));
        var fragment = new KittyImageFragment(data);
        var w = new ArrayBufferWriter<byte>();
        fragment.Emit(0, 0, w, OutputCapabilities.None);
        var text = Encoding.ASCII.GetString(w.WrittenSpan);

        // Multiple APC packets — each APC starts with ESC _.
        int packetCount = 0;
        int idx = 0;
        while ((idx = text.IndexOf("_", idx, StringComparison.Ordinal)) >= 0)
        {
            packetCount++;
            idx++;
        }
        Assert.True(packetCount >= 2,
            $"Expected the 6000-byte payload to chunk into ≥ 2 APC packets, got {packetCount}.");

        // First chunk: m=1 (more coming). Final chunk: m=0.
        Assert.Contains("m=1", text);
        Assert.Contains("m=0", text);
    }

    [Fact]
    public void Emit_SmallPayload_SinglePacketWithMZero()
    {
        var data = new ImageData([1, 2, 3], ImageFormat.Png, new Size(5, 3));
        var fragment = new KittyImageFragment(data);
        var w = new ArrayBufferWriter<byte>();
        fragment.Emit(0, 0, w, OutputCapabilities.None);
        var text = Encoding.ASCII.GetString(w.WrittenSpan);

        // Single APC means m=0 (final chunk) and no m=1.
        Assert.Contains("m=0", text);
        Assert.DoesNotContain("m=1", text);
    }

    [Fact]
    public void ImageId_IsAssignedAtConstruction_AndUniquePerInstance()
    {
        var data = new ImageData([1, 2, 3], ImageFormat.Png, new Size(5, 3));
        var a = new KittyImageFragment(data);
        var b = new KittyImageFragment(data);

        Assert.True(a.ImageId > 0);
        Assert.NotEqual(a.ImageId, b.ImageId);
    }

    [Fact]
    public void Emit_FirstChunkIncludesImageId()
    {
        var data = new ImageData([1, 2, 3], ImageFormat.Png, new Size(5, 3));
        var fragment = new KittyImageFragment(data);
        var w = new ArrayBufferWriter<byte>();
        fragment.Emit(0, 0, w, OutputCapabilities.None);
        var text = Encoding.ASCII.GetString(w.WrittenSpan);

        Assert.Contains($"i={fragment.ImageId}", text);
    }

    [Fact]
    public void EmitErase_EmitsKittyDeleteByIdSequence()
    {
        var data = new ImageData([1, 2, 3], ImageFormat.Png, new Size(5, 3));
        var fragment = new KittyImageFragment(data);
        var w = new ArrayBufferWriter<byte>();

        fragment.EmitErase(0, 0, w, OutputCapabilities.None);
        var text = Encoding.ASCII.GetString(w.WrittenSpan);

        // ESC _ G a=d,d=I,i=<id>,q=2 ESC \
        Assert.Equal($"\x1b_Ga=d,d=I,i={fragment.ImageId},q=2\x1b\\", text);
    }

    [Fact]
    public void EmitErase_InsideMultiplexer_WrapsInTmuxPassthrough()
    {
        var data = new ImageData([1, 2, 3], ImageFormat.Png, new Size(5, 3));
        var fragment = new KittyImageFragment(data);
        var caps = OutputCapabilities.None with
                   {
                       Protocol = OutputCapabilities.None.Protocol with { MultiplexerPassthrough = true },
                   };
        var w = new ArrayBufferWriter<byte>();

        fragment.EmitErase(0, 0, w, caps);
        var text = Encoding.ASCII.GetString(w.WrittenSpan);

        // Tmux passthrough envelope: ESC P tmux ; <doubled-ESC body> ESC \. The inner ESC of
        // the APC introducer gets doubled.
        Assert.Contains("Ptmux;", text);
        Assert.Contains("\x1b\x1b_G", text);
        Assert.Contains($"i={fragment.ImageId}", text);
    }

    [Fact]
    public void Key_IsContentDerived_SoIdenticalReconstructionsDiffSkip()
    {
        // The renderer uses fragment.Key for its diff. Two fragments wrapping the SAME image
        // (bytes + format + sizes) must compare EQUAL by Key so a per-frame reconstruction is
        // recognized as unchanged and skipped — not deleted and re-transmitted, which would
        // churn Kitty image IDs and exhaust the terminal's image store. Their wire identities
        // (ImageId) remain distinct; the renderer preserves the transmitted id across a skip.
        var data = new ImageData([1, 2, 3], ImageFormat.Png, new Size(5, 3));
        var a = new KittyImageFragment(data);
        var b = new KittyImageFragment(data);

        Assert.Equal(a.Key, b.Key);          // same content → equal diff key (diff-skip)
        Assert.NotEqual(a.ImageId, b.ImageId); // distinct wire identities
    }

    [Fact]
    public void Key_DiffersForDifferentContentOrSize()
    {
        var bytes = new ImageData([1, 2, 3], ImageFormat.Png, new Size(5, 3));
        var differentBytes = new ImageData([9, 9, 9], ImageFormat.Png, new Size(5, 3));
        var differentSize = new ImageData([1, 2, 3], ImageFormat.Png, new Size(6, 3));

        Assert.NotEqual(new KittyImageFragment(bytes).Key, new KittyImageFragment(differentBytes).Key);
        Assert.NotEqual(new KittyImageFragment(bytes).Key, new KittyImageFragment(differentSize).Key);
    }
}

public class ITerm2ImageFragmentTests
{
    [Fact]
    public void GetSize_MatchesImageDataCellSize()
    {
        var data = new ImageData([1, 2, 3], ImageFormat.Png, new Size(8, 4));
        Assert.Equal(new Size(8, 4), new ITerm2ImageFragment(data).GetSize());
    }

    [Fact]
    public void Layer_IsCells_NotOverlay()
    {
        // iTerm2 inline images live in the cell stream and are erased by repainting their
        // footprint cells — there is no protocol delete command. Marking this Overlay (a refactor
        // once did, accidentally) breaks removal: Overlay cells aren't repainted and EmitErase is
        // a no-op, so a moved/scrolled image leaves a duplicate behind. Guard against regressing.
        var data = new ImageData([1, 2, 3], ImageFormat.Png, new Size(2, 1));
        Assert.Equal(FragmentLayer.Cells, new ITerm2ImageFragment(data).Layer);
    }

    [Fact]
    public void IsSupported_GatedOnITerm2Caps()
    {
        var data = new ImageData([1, 2, 3], ImageFormat.Png, new Size(5, 5));
        var fragment = new ITerm2ImageFragment(data);

        var caps = OutputCapabilities.None with
                   {
                       Graphics = new GraphicsCapabilities(Sixel: false, KittyGraphics: false, ITerm2InlineImages: true),
                   };
        Assert.True(fragment.IsSupported(caps));
        Assert.False(fragment.IsSupported(OutputCapabilities.None));
    }

    [Fact]
    public void Emit_OscHeaderWithCellDimensionsAndInlineFlag()
    {
        var data = new ImageData([1, 2, 3, 4, 5, 6, 7, 8], ImageFormat.Png, new Size(12, 6));
        var fragment = new ITerm2ImageFragment(data);
        var w = new ArrayBufferWriter<byte>();
        fragment.Emit(0, 0, w, OutputCapabilities.None);
        var text = Encoding.ASCII.GetString(w.WrittenSpan);

        Assert.StartsWith("]1337;File=", text);
        Assert.Contains("size=8", text);
        Assert.Contains("width=12", text);
        Assert.Contains("height=6", text);
        Assert.Contains("inline=1", text);
        Assert.Contains("preserveAspectRatio=0", text);
        Assert.EndsWith("\\", text);

        // Base64 payload is present.
        Assert.Contains(Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), text);
    }
}

public class ImageContentTests
{
    private static byte[] PngBytes() => [137, 80, 78, 71]; // first four bytes of a PNG signature, enough for tests

    [Fact]
    public void Paint_WhenKittySupported_AttachesKittyFragment()
    {
        var content = new Image(new ImageData(PngBytes(), ImageFormat.Png, new Size(10, 5)));
        var buffer = new CellBuffer(20, 10);

        var caps = OutputCapabilities.None with
                   {
                       Graphics = new GraphicsCapabilities(Sixel: false, KittyGraphics: true, ITerm2InlineImages: false),
                   };
        var painted = content.Paint(buffer, 0, 0, Style.Default, caps);

        Assert.Single(buffer.Fragments);
        Assert.IsType<KittyImageFragment>(buffer.Fragments[(0, 0)].Fragment);
        Assert.Equal(new Size(10, 5), painted.Size);
    }

    [Fact]
    public void Paint_WhenOnlyITerm2Supported_AttachesIterm2Fragment()
    {
        var content = new Image(new ImageData(PngBytes(), ImageFormat.Png, new Size(8, 4)));
        var buffer = new CellBuffer(20, 10);

        var caps = OutputCapabilities.None with
                   {
                       Graphics = new GraphicsCapabilities(Sixel: false, KittyGraphics: false, ITerm2InlineImages: true),
                   };
        content.Paint(buffer, 0, 0, Style.Default, caps);

        Assert.IsType<ITerm2ImageFragment>(buffer.Fragments[(0, 0)].Fragment);
    }

    [Fact]
    public void Paint_KittyWithJpegFormat_FallsThroughToITerm2()
    {
        // Kitty can't accept JPEG via encoded-bytes transmission; should skip and prefer iTerm2.
        var content = new Image(new ImageData(PngBytes(), ImageFormat.Jpeg, new Size(5, 5)));
        var buffer = new CellBuffer(20, 10);

        var caps = OutputCapabilities.None with
                   {
                       Graphics = new GraphicsCapabilities(Sixel: false, KittyGraphics: true, ITerm2InlineImages: true),
                   };
        content.Paint(buffer, 0, 0, Style.Default, caps);

        Assert.IsType<ITerm2ImageFragment>(buffer.Fragments[(0, 0)].Fragment);
    }

    [Fact]
    public void Paint_WhenNoGraphicsSupported_PaintsPlaceholderRectangle()
    {
        var expectedSize = new Size(10, 3);
        var content = new Image(new ImageData(PngBytes(), ImageFormat.Png, expectedSize));
        var buffer = new CellBuffer(20, 10);

        content.Measure(expectedSize, OutputCapabilities.None);

        var painted = content.Paint(buffer, 0, 0, Style.Default, OutputCapabilities.None);

        // No fragment registered — placeholder painted as cells.
        Assert.Empty(buffer.Fragments);
        Assert.Equal(expectedSize, painted.Size);

        // Center row should contain the "[image]" label.
        int centerRow = 0 + 3 / 2;
        var rowText = string.Concat(Enumerable.Range(0, 10).Select(c => buffer[c, centerRow].Grapheme ?? " "));
        Assert.Contains("[image]", rowText);
    }

    [Fact]
    public void Paint_PlaceholderStyleOverridesCallerStyle()
    {
        var placeholder = Style.Default.WithBackground(Color.FromRgb(50, 50, 50));
        var content = new Image(new ImageData(PngBytes(), ImageFormat.Png, new Size(4, 2)), placeholder);
        var buffer = new CellBuffer(10, 5);

        content.Paint(buffer, 0, 0, Style.Default.WithBackground(Color.FromRgb(255, 0, 0)), OutputCapabilities.None);

        // Every painted cell should carry the PlaceholderStyle's background, not the caller's.
        Assert.Equal(Color.FromRgb(50, 50, 50), buffer[0, 0].Style.Background);
    }

    // ---- aspect-free derivation (deferred finding 7): CreateFragment maps the SPECIFIED dimension to an
    //      omitted (aspect-free) OTHER dimension on the wire. The fragment-level wire behavior given the
    //      enum is covered elsewhere; this pins the counterintuitive present-dim→omitted-dim mapping. ----

    [Fact]
    public void Paint_ColumnsOnlySize_DerivesRowsAspectFree_OmitsRowQualifier()
    {
        var content = new Image(new ImageData(MinimalPng(40, 20), ImageFormat.Png, new Size(4, 0)));
        var buffer = new CellBuffer(20, 10);
        var caps = OutputCapabilities.None with
                   { Graphics = new GraphicsCapabilities(Sixel: false, KittyGraphics: true, ITerm2InlineImages: false) };
        content.Paint(buffer, 0, 0, Style.Default, caps);

        string header = EmitFragmentHeader(buffer, caps);
        Assert.Contains("c=", header);         // columns pinned (the specified dimension)
        Assert.DoesNotContain("r=", header);   // rows derived from aspect → omitted
    }

    [Fact]
    public void Paint_RowsOnlySize_DerivesColumnsAspectFree_OmitsColumnQualifier()
    {
        var content = new Image(new ImageData(MinimalPng(40, 20), ImageFormat.Png, new Size(0, 3)));
        var buffer = new CellBuffer(20, 10);
        var caps = OutputCapabilities.None with
                   { Graphics = new GraphicsCapabilities(Sixel: false, KittyGraphics: true, ITerm2InlineImages: false) };
        content.Paint(buffer, 0, 0, Style.Default, caps);

        string header = EmitFragmentHeader(buffer, caps);
        Assert.DoesNotContain("c=", header);   // columns derived from aspect → omitted
        Assert.Contains("r=", header);         // rows pinned (the specified dimension)
    }

    // Emit the single registered fragment and return its APC control-data header (before the payload).
    private static string EmitFragmentHeader(CellBuffer buffer, OutputCapabilities caps)
    {
        var fragment = buffer.Fragments[(0, 0)].Fragment;
        var output = new ArrayBufferWriter<byte>();
        fragment.Emit(0, 0, output, caps);
        string emitted = Encoding.ASCII.GetString(output.WrittenSpan);
        return emitted[..emitted.IndexOf(';')];
    }

    // A minimal valid PNG: the 8-byte signature + a 13-byte IHDR chunk. PngDecoder.DecodeSize reads the
    // width/height and returns (sizeOnly) without decoding pixels or validating the CRC — enough for the
    // Kitty path, which transmits the encoded bytes without decoding them.
    private static byte[] MinimalPng(int width, int height)
    {
        var bytes = new byte[33];
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        signature.CopyTo(bytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 13);   // IHDR data length
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        bytes[24] = 8;   // bit depth
        bytes[25] = 2;   // color type (truecolor); DecodeSize(sizeOnly) returns before reading this anyway
        return bytes;
    }
}
