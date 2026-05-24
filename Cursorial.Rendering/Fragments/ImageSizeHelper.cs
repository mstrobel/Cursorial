namespace Cursorial.Rendering.Fragments;

internal static class ImageSizeHelper
{
    internal static (int Width, int Height) DecodeSize(ReadOnlySpan<byte> payload, ImageFormat format)
    {
        return format switch
               {
                   ImageFormat.Png  => PngDecoder.DecodeSize(payload) ?? (0, 0),
                   ImageFormat.Jpeg => DecodeJpegSize(payload),
                   ImageFormat.Gif  => DecodeGifSize(payload),
                   _                => (0, 0)
               };
    }

    private static (int Width, int Height) DecodeJpegSize(ReadOnlySpan<byte> payload)
    {
        // JPEG format: FF D8 (SOI) followed by segments
        // We need to find SOF0 (FF C0) or SOF2 (FF C2) markers
        if (payload.Length < 2 || payload[0] != 0xFF || payload[1] != 0xD8)
            return (0, 0);

        int offset = 2;

        while (offset + 8 < payload.Length)
        {
            // Find next marker
            if (payload[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            byte marker = payload[offset + 1];
            offset += 2;

            // SOF0 (Baseline DCT) or SOF2 (Progressive DCT)
            if (marker == 0xC0 || marker == 0xC2)
            {
                if (offset + 5 >= payload.Length)
                    return (0, 0);

                // Skip segment length (2 bytes) and precision (1 byte)
                offset += 3;

                // Height (2 bytes, big-endian)
                int height = (payload[offset] << 8) | payload[offset + 1];
                offset += 2;

                // Width (2 bytes, big-endian)
                int width = (payload[offset] << 8) | payload[offset + 1];

                return (width, height);
            }

            // Skip segment data
            if (offset + 1 >= payload.Length)
                return (0, 0);

            int segmentLength = (payload[offset] << 8) | payload[offset + 1];
            offset += segmentLength;
        }

        return (0, 0);
    }

    private static (int Width, int Height) DecodeGifSize(ReadOnlySpan<byte> payload)
    {
        // GIF format: "GIF87a" or "GIF89a" header (6 bytes)
        // Followed by Logical Screen Width (2 bytes, little-endian)
        // and Logical Screen Height (2 bytes, little-endian)
        if (payload.Length < 10)
            return (0, 0);

        // Check GIF signature
        if (payload[0] != 'G' || payload[1] != 'I' || payload[2] != 'F')
            return (0, 0);

        // Width at offset 6-7 (little-endian)
        int width = payload[6] | (payload[7] << 8);

        // Height at offset 8-9 (little-endian)
        int height = payload[8] | (payload[9] << 8);

        return (width, height);
    }
}