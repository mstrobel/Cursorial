using System.IO.Compression;
using System.Buffers.Binary;

using Cursorial.Rendering.Fragments;

namespace Cursorial.Tests.Rendering;

public class PngDecoderTests
{
    [Fact]
    public void Decode_BadSignature_Throws()
    {
        Assert.Throws<InvalidDataException>(() => PngDecoder.Decode([0, 0, 0, 0, 0, 0, 0, 0]));
    }

    [Fact]
    public void Decode_SinglePixelRgb_RoundTrips()
    {
        var png = BuildPng(width: 1, height: 1, channels: 3, filterType: 0, rawPixels: new byte[] { 200, 100, 50 });
        var decoded = PngDecoder.Decode(png);

        Assert.Equal(1, decoded.Width);
        Assert.Equal(1, decoded.Height);
        Assert.Equal(new byte[] { 200, 100, 50, 255 }, decoded.Rgba);
    }

    [Fact]
    public void Decode_SinglePixelRgba_RoundTrips()
    {
        var png = BuildPng(width: 1, height: 1, channels: 4, filterType: 0, rawPixels: new byte[] { 10, 20, 30, 200 });
        var decoded = PngDecoder.Decode(png);

        Assert.Equal(new byte[] { 10, 20, 30, 200 }, decoded.Rgba);
    }

    [Fact]
    public void Decode_TwoByTwoRgb_AllFilterNone_ProducesExpectedPixels()
    {
        // 2x2 RGB image: red, green / blue, white.
        var pixels = new byte[]
        {
            255, 0,   0,    0, 255, 0,
            0,   0,   255,  255, 255, 255
        };
        var png = BuildPng(width: 2, height: 2, channels: 3, filterType: 0, rawPixels: pixels);
        var decoded = PngDecoder.Decode(png);

        Assert.Equal(2, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.Equal(255, decoded.Rgba[0]); // R of pixel (0,0)
        Assert.Equal(255, decoded.Rgba[5]); // G of pixel (1,0) — index 1*4+1
        Assert.Equal(255, decoded.Rgba[8 + 2]); // B of pixel (0,1) — row 1 starts at byte 8, +2 for B
        Assert.Equal(255, decoded.Rgba[12]); // R of pixel (1,1)
        Assert.Equal(255, decoded.Rgba[13]); // G of pixel (1,1)
        Assert.Equal(255, decoded.Rgba[14]); // B of pixel (1,1)
    }

    [Fact]
    public void Decode_SubFilter_UnfiltersCorrectly()
    {
        // 2x1 RGB image: pixel 0 = (10, 20, 30), pixel 1 = (50, 60, 70).
        // Sub filter: each byte is the difference from the byte 3 to the left.
        // Encoded: (10, 20, 30, 50-10, 60-20, 70-30) = (10, 20, 30, 40, 40, 40).
        var encodedRow = new byte[] { 1, 10, 20, 30, 40, 40, 40 }; // filter=1 (Sub) prefix
        var png = BuildPngWithFilteredRows(width: 2, height: 1, channels: 3, filteredRows: encodedRow);
        var decoded = PngDecoder.Decode(png);

        Assert.Equal(new byte[] { 10, 20, 30, 255, 50, 60, 70, 255 }, decoded.Rgba);
    }

    [Fact]
    public void Decode_UpFilter_UnfiltersCorrectly()
    {
        // 1x2 RGB image. Up filter: each byte differs from the byte directly above.
        // Row 0 (filter=0): (100, 100, 100). Row 1 (filter=2 Up): values (5, 5, 5) decode to (105, 105, 105).
        var rows = new byte[]
        {
            0, 100, 100, 100,
            2, 5, 5, 5
        };
        var png = BuildPngWithFilteredRows(width: 1, height: 2, channels: 3, filteredRows: rows);
        var decoded = PngDecoder.Decode(png);

        Assert.Equal(new byte[] { 100, 100, 100, 255, 105, 105, 105, 255 }, decoded.Rgba);
    }

    [Fact]
    public void Decode_AverageFilter_UnfiltersCorrectly()
    {
        // 2x2 RGB. Row 0 (filter=0): all zeros. Row 1 (filter=3 Average) with all zeros encoded →
        // decoded = 0 + (left+up)/2 = 0 throughout, since left and up are both 0.
        var rows = new byte[]
        {
            0, 0, 0, 0, 0, 0, 0,
            3, 0, 0, 0, 0, 0, 0,
        };
        var png = BuildPngWithFilteredRows(width: 2, height: 2, channels: 3, filteredRows: rows);
        var decoded = PngDecoder.Decode(png);

        Assert.All(decoded.Rgba, (b, i) => Assert.Equal((i + 1) % 4 == 0 ? (byte) 255 : (byte) 0, b));
    }

    [Fact]
    public void Decode_PaethFilter_UnfiltersCorrectly()
    {
        // 1x1 RGB. Single row with Paeth filter — neighbors are zero so predictor = 0, decoded = encoded.
        var rows = new byte[] { 4, 77, 88, 99 };
        var png = BuildPngWithFilteredRows(width: 1, height: 1, channels: 3, filteredRows: rows);
        var decoded = PngDecoder.Decode(png);

        Assert.Equal(new byte[] { 77, 88, 99, 255 }, decoded.Rgba);
    }

    [Fact]
    public void Decode_MultipleIdatChunks_ConcatenatesCorrectly()
    {
        // Build a 2x1 RGB PNG but split IDAT across two chunks.
        byte[] filtered = [0, 1, 2, 3, 4, 5, 6]; // filter=0 prefix
        var compressed = Compress(filtered);

        // Split the compressed stream roughly in half.
        int mid = compressed.Length / 2;
        var ihdr = BuildIhdr(width: 2, height: 1, channels: 3);
        using var ms = new MemoryStream();
        ms.Write(PngSignature);
        WriteChunk(ms, 0x49484452u, ihdr);
        WriteChunk(ms, 0x49444154u, compressed.AsSpan(0, mid).ToArray());
        WriteChunk(ms, 0x49444154u, compressed.AsSpan(mid).ToArray());
        WriteChunk(ms, 0x49454E44u, []);

        var decoded = PngDecoder.Decode(ms.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, decoded.Rgba);
    }

    [Fact]
    public void Decode_Adam7Interlaced_Throws()
    {
        var ihdr = BuildIhdr(width: 2, height: 2, channels: 3);
        ihdr[12] = 1; // interlace = Adam7

        using var ms = new MemoryStream();
        ms.Write(PngSignature);
        WriteChunk(ms, 0x49484452u, ihdr);
        WriteChunk(ms, 0x49454E44u, []);

        Assert.Throws<NotSupportedException>(() => PngDecoder.Decode(ms.ToArray()));
    }

    [Fact]
    public void Decode_PaletteColorType_Throws()
    {
        var ihdr = BuildIhdr(width: 1, height: 1, channels: 3);
        ihdr[9] = 3; // color type = palette

        using var ms = new MemoryStream();
        ms.Write(PngSignature);
        WriteChunk(ms, 0x49484452u, ihdr);
        WriteChunk(ms, 0x49454E44u, []);

        Assert.Throws<NotSupportedException>(() => PngDecoder.Decode(ms.ToArray()));
    }

    [Fact]
    public void Decode_AncillaryChunks_AreIgnored()
    {
        // Build a 1x1 RGB PNG with a gAMA chunk between IHDR and IDAT.
        var ihdr = BuildIhdr(width: 1, height: 1, channels: 3);
        byte[] filtered = { 0, 42, 43, 44 };
        var compressed = Compress(filtered);

        using var ms = new MemoryStream();
        ms.Write(PngSignature);
        WriteChunk(ms, 0x49484452u, ihdr);
        WriteChunk(ms, 0x67414D41u, [0, 0, 0xB1, 0x8F]); // gAMA, 0xB18F = sRGB gamma
        WriteChunk(ms, 0x49444154u, compressed);
        WriteChunk(ms, 0x49454E44u, []);

        var decoded = PngDecoder.Decode(ms.ToArray());
        Assert.Equal(new byte[] { 42, 43, 44, 255 }, decoded.Rgba);
    }

    [Fact]
    public void Decode_FeedsIntoSixelPipeline()
    {
        // End-to-end: decode a tiny PNG, quantize, encode. Just verifies the wiring.
        var pixels = new byte[]
        {
            255, 0, 0,   0, 255, 0,   0, 0, 255,
            255, 255, 0, 0, 255, 255, 255, 0, 255,
        };
        var png = BuildPng(width: 3, height: 2, channels: 3, filterType: 0, rawPixels: pixels);
        var decoded = PngDecoder.Decode(png);
        var quantized = MedianCutQuantizer.Quantize(decoded.Rgba, decoded.Width, decoded.Height, maxColors: 16);
        var sixel = SixelEncoder.Encode(quantized.IndexedPixels, decoded.Width, decoded.Height, quantized.Palette);

        // DCS introducer + ST terminator on the wire.
        Assert.Equal(0x1B, sixel[0]);
        Assert.Equal((byte) 'P', sixel[1]);
        Assert.Equal(0x1B, sixel[^2]);
        Assert.Equal((byte) '\\', sixel[^1]);
    }

    // ---- Test helpers ----

    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] BuildIhdr(int width, int height, int channels)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = channels switch { 3 => (byte) 2, 4 => (byte) 6, _ => throw new ArgumentException("channels must be 3 or 4") };
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace
        return ihdr;
    }

    private static byte[] BuildPng(int width, int height, int channels, byte filterType, byte[] rawPixels)
    {
        // Prefix each row with the filter type byte. For filter type 0 (None) the row bytes are the
        // pixel bytes verbatim.
        if (filterType != 0)
            throw new ArgumentException("BuildPng only supports filter type 0 for raw construction.");
        int stride = width * channels;
        var filtered = new byte[height * (stride + 1)];
        for (int y = 0; y < height; y++)
        {
            filtered[y * (stride + 1)] = filterType;
            Buffer.BlockCopy(rawPixels, y * stride, filtered, y * (stride + 1) + 1, stride);
        }

        return BuildPngWithFilteredRows(width, height, channels, filtered);
    }

    private static byte[] BuildPngWithFilteredRows(int width, int height, int channels, byte[] filteredRows)
    {
        var ihdr = BuildIhdr(width, height, channels);
        var compressed = Compress(filteredRows);

        using var ms = new MemoryStream();
        ms.Write(PngSignature);
        WriteChunk(ms, 0x49484452u, ihdr);
        WriteChunk(ms, 0x49444154u, compressed);
        WriteChunk(ms, 0x49454E44u, []);
        return ms.ToArray();
    }

    private static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }
        return ms.ToArray();
    }

    private static void WriteChunk(Stream stream, uint type, byte[] data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(4, 4), type);
        stream.Write(header);
        stream.Write(data);

        // Decoder doesn't verify CRC, so a zero placeholder is fine for tests.
        stream.Write([0, 0, 0, 0]);
    }
}
