using System.Buffers.Binary;
using System.IO.Compression;

namespace Cursorial.Rendering.Fragments;

/// <summary>
/// A decoded raster image — RGBA bytes laid out row-major (4 bytes per pixel).
/// </summary>
public readonly record struct DecodedImage(int Width, int Height, byte[] Rgba);

/// <summary>
/// Minimal PNG decoder scoped to what the Sixel pipeline needs: 8-bit color type 2 (RGB) and
/// type 6 (RGBA), all five filter types, multi-IDAT concatenation, non-interlaced only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Out of scope by design.</b> Paletted (color type 3), grayscale (0), grayscale+alpha (4),
/// non-8-bit-depth, Adam7 interlacing, and ancillary chunks (gAMA, sRGB, iCCP, tRNS, etc.) all
/// throw <see cref="NotSupportedException"/>. The justification: this decoder feeds the Sixel
/// quantizer, which operates on RGBA — the long tail of PNG features doesn't pay back in this
/// path. Callers needing the full PNG surface should preprocess with a third-party decoder.
/// </para>
/// <para>
/// <b>Validation.</b> CRC checksums on chunk data are <em>not</em> verified. The zlib inflate
/// pass catches catastrophic corruption; bit-rot in chunk metadata that the spec would flag is
/// not. We trade fidelity to the spec for ~30 lines of code we'd otherwise need; consumers
/// reading from disk on platforms where bit-rot is a real concern should checksum upstream.
/// </para>
/// </remarks>
public static class PngDecoder
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Decode <paramref name="pngBytes"/> into a row-major RGBA buffer. The 8-byte PNG signature
    /// is verified before any other parsing; the image-header chunk dictates how subsequent
    /// pixel data is interpreted.
    /// </summary>
    public static DecodedImage Decode(ReadOnlySpan<byte> pngBytes)
    {
        if (Decode(pngBytes, out var decodedImage, out _, false))
            return decodedImage;

        throw new InvalidDataException("Input is not a PNG file.");
    }

    /// <summary>
    /// Decode <paramref name="pngBytes"/> only enough to determine the image dimensions,
    /// then return the decoded dimensions in pixels.
    /// </summary>
    public static (int Width, int Height)? DecodeSize(ReadOnlySpan<byte> pngBytes)
    {
        if (Decode(pngBytes, out _, out var size, true))
            return size;
        return null;
    }

    private static bool Decode(ReadOnlySpan<byte> pngBytes, out DecodedImage decodedImage, out (int Width, int Height) size, bool sizeOnly)
    {
        size = default;
        decodedImage = default;

        if (pngBytes.Length < PngSignature.Length || !pngBytes[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            if (sizeOnly) return false;
            throw new InvalidDataException("Input is not a PNG file (bad signature).");
        }

        int offset = PngSignature.Length;
        int width = 0;
        int height = 0;
        int channelsPerPixel = 0;
        bool ihdrSeen = false;
        bool iendSeen = false;

        using var compressedData = sizeOnly ? null : new MemoryStream();

        while (offset < pngBytes.Length)
        {
            if (offset + 8 > pngBytes.Length)
                throw new InvalidDataException("Truncated PNG: incomplete chunk header.");

            int length = BinaryPrimitives.ReadInt32BigEndian(pngBytes.Slice(offset, 4));
            if (length < 0)
                throw new InvalidDataException($"Invalid chunk length {length}.");
            uint type = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.Slice(offset + 4, 4));
            offset += 8;

            if (offset + length + 4 > pngBytes.Length)
                throw new InvalidDataException("Truncated PNG: chunk data + CRC overruns buffer.");

            var data = pngBytes.Slice(offset, length);
            offset += length + 4; // skip data + CRC

            switch (type)
            {
                case 0x49484452: // "IHDR"
                {
                    if (ihdrSeen)
                        throw new InvalidDataException("Multiple IHDR chunks.");
                    if (length != 13)
                        throw new InvalidDataException($"IHDR length must be 13, got {length}.");

                    width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
                    height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(4, 4));
                    size = (width, height);

                    if (sizeOnly)
                        return true;

                    byte bitDepth = data[8];
                    byte colorType = data[9];
                    byte compression = data[10];
                    byte filterMethod = data[11];
                    byte interlace = data[12];

                    if (width <= 0 || height <= 0)
                        throw new InvalidDataException($"Invalid dimensions {width}×{height}.");
                    if (bitDepth != 8)
                        throw new NotSupportedException($"Only 8-bit color depth is supported; got {bitDepth}.");
                    if (compression != 0)
                        throw new InvalidDataException($"Unknown PNG compression method {compression}.");
                    if (filterMethod != 0)
                        throw new InvalidDataException($"Unknown PNG filter method {filterMethod}.");
                    if (interlace == 1)
                        throw new NotSupportedException("Adam7-interlaced PNGs are not supported.");
                    if (interlace != 0)
                        throw new InvalidDataException($"Unknown interlace method {interlace}.");

                    channelsPerPixel = colorType switch
                    {
                        2 => 3, // RGB
                        6 => 4, // RGBA
                        _ => throw new NotSupportedException(
                            $"Color type {colorType} is not supported; only 2 (RGB) and 6 (RGBA) are.")
                    };

                    ihdrSeen = true;
                    break;
                }

                case 0x49444154: // "IDAT"
                {
                    if (!ihdrSeen)
                        throw new InvalidDataException("IDAT before IHDR.");
                    compressedData?.Write(data);
                    break;
                }

                case 0x49454E44: // "IEND"
                {
                    iendSeen = true;
                    break;
                }

                case 0x504C5445: // "PLTE"
                {
                    // Palette is only meaningful for color type 3; we reject 3 in IHDR, so a PLTE
                    // here is either gratuitous (some encoders include it as a "suggested palette"
                    // for truecolor) or junk. Either way, ignore.
                    break;
                }

                // ReSharper disable once RedundantEmptySwitchSection
                default:
                {
                    // Ancillary chunks (lowercase first letter) — safe to ignore. Critical
                    // unknown chunks (uppercase first letter) we don't recognize would technically
                    // be a fatal error per the spec; in practice, IHDR/IDAT/IEND/PLTE are the only
                    // critical chunks defined. Anything else is by definition ancillary.
                    break;
                }
            }

            if (iendSeen) break;
        }

        if (!ihdrSeen || compressedData is null /* we only reach here if sizeOnly = true */)
            throw new InvalidDataException("PNG is missing IHDR.");
        if (!iendSeen)
            throw new InvalidDataException("PNG is missing IEND.");
        if (compressedData.Length == 0)
            throw new InvalidDataException("PNG contains no IDAT data.");

        if (sizeOnly)
        {
            // This should technically be unreachable.
            return false;
        }

        // Inflate the concatenated IDAT payload. The PNG spec wraps the deflate stream in a zlib
        // header (2 bytes) + Adler32 checksum (4 bytes at the end). ZLibStream handles both.
        compressedData.Position = 0;
        using var inflate = new ZLibStream(compressedData, CompressionMode.Decompress);

        int bytesPerPixel = channelsPerPixel;
        int scanlineBytes = width * bytesPerPixel;
        int expected = (scanlineBytes + 1) * height;
        var filtered = new byte[expected];

        int read = 0;
        while (read < expected)
        {
            int n = inflate.Read(filtered, read, expected - read);
            if (n == 0)
                throw new InvalidDataException($"Inflated PNG data is shorter than expected: got {read}, need {expected}.");
            read += n;
        }

        // Unfilter scanlines in-place into a contiguous RGBA buffer. Each row in `filtered` is
        // prefixed with a 1-byte filter type; we strip those during this pass.
        var rgb = new byte[width * height * bytesPerPixel];
        var prevRow = new byte[scanlineBytes];
        var curRow = new byte[scanlineBytes];

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * (scanlineBytes + 1);
            byte filterType = filtered[rowStart];
            Span<byte> rowData = filtered.AsSpan(rowStart + 1, scanlineBytes);

            switch (filterType)
            {
                case 0: // None
                    rowData.CopyTo(curRow);
                    break;
                case 1: // Sub
                    for (int x = 0; x < scanlineBytes; x++)
                    {
                        byte left = x >= bytesPerPixel ? curRow[x - bytesPerPixel] : (byte) 0;
                        curRow[x] = (byte) (rowData[x] + left);
                    }
                    break;
                case 2: // Up
                    for (int x = 0; x < scanlineBytes; x++)
                        curRow[x] = (byte) (rowData[x] + prevRow[x]);
                    break;
                case 3: // Average
                    for (int x = 0; x < scanlineBytes; x++)
                    {
                        int left = x >= bytesPerPixel ? curRow[x - bytesPerPixel] : 0;
                        int up = prevRow[x];
                        curRow[x] = (byte) (rowData[x] + (left + up) / 2);
                    }
                    break;
                case 4: // Paeth
                    for (int x = 0; x < scanlineBytes; x++)
                    {
                        int left = x >= bytesPerPixel ? curRow[x - bytesPerPixel] : 0;
                        int up = prevRow[x];
                        int upLeft = x >= bytesPerPixel ? prevRow[x - bytesPerPixel] : 0;
                        curRow[x] = (byte) (rowData[x] + PaethPredictor(left, up, upLeft));
                    }
                    break;
                default:
                    throw new InvalidDataException($"Unknown PNG filter type {filterType} on row {y}.");
            }

            curRow.AsSpan().CopyTo(rgb.AsSpan(y * scanlineBytes));

            // Swap rows for the next iteration.
            (prevRow, curRow) = (curRow, prevRow);
        }

        // Expand RGB → RGBA if needed.
        if (bytesPerPixel == 4)
        {
            decodedImage = new DecodedImage(width, height, rgb);
            return true;
        }

        var rgba = new byte[width * height * 4];
        for (int p = 0; p < width * height; p++)
        {
            rgba[p * 4 + 0] = rgb[p * 3 + 0];
            rgba[p * 4 + 1] = rgb[p * 3 + 1];
            rgba[p * 4 + 2] = rgb[p * 3 + 2];
            rgba[p * 4 + 3] = 255;
        }

        decodedImage = new DecodedImage(width, height, rgba);
        return true;
    }

    private static int PaethPredictor(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        if (pb <= pc) return b;
        return c;
    }
}
