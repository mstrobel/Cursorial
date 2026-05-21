using System.Buffers;

namespace Cursorial.Rendering.Fragments;

/// <summary>
/// An entry in a Sixel color palette. Components are 0..255 in linear sRGB; the encoder scales
/// to the protocol's 0..100 register range internally.
/// </summary>
public readonly record struct SixelColor(byte R, byte G, byte B)
{
    /// <summary>Construct from a <see cref="Cursorial.Output.Color"/>. Default and palette colors collapse to black.</summary>
    public static SixelColor From(Cursorial.Output.Color color)
    {
        if (color.Kind != Cursorial.Output.ColorKind.Rgb)
            return new SixelColor(0, 0, 0);
        return new SixelColor(color.Red, color.Green, color.Blue);
    }
}

/// <summary>
/// Encodes raw indexed pixels into a Sixel DCS envelope. Stateless and allocation-free apart
/// from the output buffer the caller supplies.
/// </summary>
/// <remarks>
/// <para>
/// Wire format: <c>ESC P 0 ; 1 ; 0 q " Pan ; Pad ; Pw ; Ph # color-defs &lt;data&gt; ESC \</c>.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>Pa = 0</c> — square pixel aspect (1:1). Modern terminals treat the default as 1:1; we
/// emit explicitly so behavior is identical across xterm, foot, mlterm, mintty, Kitty,
/// Konsole, and WezTerm.
/// </description></item>
/// <item><description>
/// <c>Pb = 1</c> — leave the current background color in unused pixels. The alternative
/// (<c>0</c> = use default bg) often clears scrollback content underneath the image.
/// </description></item>
/// <item><description>
/// Raster attributes <c>"Pan;Pad;Pw;Ph</c> are emitted before any pixel data — without them
/// xterm renders at unpredictable sizes.
/// </description></item>
/// <item><description>
/// Color registers are declared up front with RGB color space (<c>;2;</c>) and components
/// scaled to 0..100. Sixel's per-pixel color switches reference these registers by index.
/// </description></item>
/// <item><description>
/// Pixel data is encoded in 6-row bands, column-major. For each color used in a band the
/// encoder emits <c>#&lt;index&gt;</c> followed by the band's column patterns, RLE-collapsing
/// runs of identical 6-bit patterns. Bands are separated by <c>-</c>; within a band,
/// <c>$</c> returns the cursor to the band start for the next color.
/// </description></item>
/// </list>
/// </remarks>
public static class SixelEncoder
{
    /// <summary>Maximum palette size supported by the encoder. Sixel's legacy ceiling.</summary>
    public const int MaxPaletteSize = 256;

    /// <summary>
    /// Encode <paramref name="indexedPixels"/> as a complete Sixel DCS envelope and write the
    /// bytes to <paramref name="output"/>. The pixel buffer is row-major:
    /// <c>indexedPixels[y * width + x]</c> is the palette index at <c>(x, y)</c>.
    /// </summary>
    public static void Encode(IBufferWriter<byte> output,
                              ReadOnlySpan<byte> indexedPixels,
                              int width, int height,
                              ReadOnlySpan<SixelColor> palette)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
        if (palette.IsEmpty) throw new ArgumentException("Palette must contain at least one color.", nameof(palette));
        if (palette.Length > MaxPaletteSize)
            throw new ArgumentException($"Palette size {palette.Length} exceeds the encoder's limit of {MaxPaletteSize}.", nameof(palette));
        if (indexedPixels.Length != width * height)
            throw new ArgumentException($"indexedPixels length {indexedPixels.Length} doesn't match width × height = {width * height}.", nameof(indexedPixels));

        // DCS introducer + raster attributes. Pa=0 (1:1 aspect), Pb=1 (keep bg), Pc=0 (default grid).
        WriteAscii(output, "\x1bP0;1;0q\"1;1;");
        WriteInt(output, width);
        WriteAscii(output, ";");
        WriteInt(output, height);

        // Color register definitions.
        for (int i = 0; i < palette.Length; i++)
        {
            var c = palette[i];
            int r = ScaleTo100(c.R);
            int g = ScaleTo100(c.G);
            int b = ScaleTo100(c.B);

            WriteAscii(output, "#");
            WriteInt(output, i);
            WriteAscii(output, ";2;");
            WriteInt(output, r);
            WriteAscii(output, ";");
            WriteInt(output, g);
            WriteAscii(output, ";");
            WriteInt(output, b);
        }

        // Per-band emission.
        int bandCount = (height + 5) / 6;
        Span<bool> colorsInBand = stackalloc bool[palette.Length];

        for (int band = 0; band < bandCount; band++)
        {
            int bandStart = band * 6;
            int bandHeight = Math.Min(6, height - bandStart);

            colorsInBand.Clear();
            for (int col = 0; col < width; col++)
            {
                for (int r = 0; r < bandHeight; r++)
                {
                    byte idx = indexedPixels[(bandStart + r) * width + col];
                    colorsInBand[idx] = true;
                }
            }

            bool firstColorInBand = true;
            for (int colorIdx = 0; colorIdx < palette.Length; colorIdx++)
            {
                if (!colorsInBand[colorIdx]) continue;

                if (!firstColorInBand)
                    WriteAscii(output, "$"); // Carriage return — reset to band start for the next color.
                firstColorInBand = false;

                WriteAscii(output, "#");
                WriteInt(output, colorIdx);

                EmitBandPattern(output, indexedPixels, width, bandStart, bandHeight, (byte) colorIdx);
            }

            if (band < bandCount - 1)
                WriteAscii(output, "-"); // Advance to next 6-row band.
        }

        // ST.
        WriteAscii(output, "\x1b\\");
    }

    /// <summary>
    /// Convenience overload — encodes into a freshly allocated byte array.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> indexedPixels,
                                int width, int height,
                                ReadOnlySpan<SixelColor> palette)
    {
        var writer = new ArrayBufferWriter<byte>();
        Encode(writer, indexedPixels, width, height, palette);
        return writer.WrittenSpan.ToArray();
    }

    private static void EmitBandPattern(IBufferWriter<byte> output, ReadOnlySpan<byte> pixels,
                                        int width, int bandStart, int bandHeight, byte colorIdx)
    {
        // Walk columns, build a 6-bit pattern (bit i = pixel set in row bandStart + i for the
        // current color), RLE-collapse runs of identical patterns. Bit 0 is the top row of the
        // band per the Sixel spec.
        byte prevPattern = 0xFF; // sentinel — first iteration writes regardless
        int runLength = 0;

        for (int col = 0; col < width; col++)
        {
            byte pattern = 0;
            for (int r = 0; r < bandHeight; r++)
            {
                if (pixels[(bandStart + r) * width + col] == colorIdx)
                    pattern |= (byte) (1 << r);
            }

            if (col > 0 && pattern == prevPattern)
            {
                runLength++;
            }
            else
            {
                if (runLength > 0)
                    WriteRunOrSingle(output, prevPattern, runLength);
                prevPattern = pattern;
                runLength = 1;
            }
        }

        if (runLength > 0)
            WriteRunOrSingle(output, prevPattern, runLength);
    }

    private static void WriteRunOrSingle(IBufferWriter<byte> output, byte pattern, int runLength)
    {
        byte sixelByte = (byte) (0x3F + pattern);

        // RLE is worthwhile when it saves bytes: !N<byte> costs 2 + digits(N) bytes vs N
        // characters. For run length 4 it's break-even; we apply RLE for >= 4 to keep wire
        // output small without micro-optimizing.
        if (runLength >= 4)
        {
            WriteAscii(output, "!");
            WriteInt(output, runLength);
            var dest = output.GetSpan(1);
            dest[0] = sixelByte;
            output.Advance(1);
        }
        else
        {
            var dest = output.GetSpan(runLength);
            dest[..runLength].Fill(sixelByte);
            output.Advance(runLength);
        }
    }

    /// <summary>Scale a 0..255 sRGB byte to Sixel's 0..100 register range, rounding to nearest.</summary>
    private static int ScaleTo100(byte value) => (value * 100 + 127) / 255;

    private static void WriteAscii(IBufferWriter<byte> output, string s)
    {
        int len = s.Length;
        var dest = output.GetSpan(len);
        for (int i = 0; i < len; i++) dest[i] = (byte) s[i];
        output.Advance(len);
    }

    private static void WriteInt(IBufferWriter<byte> output, int value)
    {
        // Decimal-format value into the buffer without allocations. Sixel values are always
        // non-negative; the encoder doesn't pass negatives.
        if (value == 0)
        {
            var zero = output.GetSpan(1);
            zero[0] = (byte) '0';
            output.Advance(1);
            return;
        }

        Span<byte> digits = stackalloc byte[10]; // enough for int.MaxValue
        int count = 0;
        int n = value;
        while (n > 0)
        {
            digits[count++] = (byte) ('0' + (n % 10));
            n /= 10;
        }

        var dest = output.GetSpan(count);
        for (int i = 0; i < count; i++) dest[i] = digits[count - 1 - i];
        output.Advance(count);
    }
}
