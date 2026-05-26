namespace Cursorial.Rendering.Imaging;

/// <summary>
/// Encoding of an <see cref="ImageData"/> payload. Used by image fragments to fill in the
/// protocol's format header field (Kitty's <c>f=100</c> / <c>f=32</c>, iTerm2's <c>name=…</c>
/// extension hint, etc.).
/// </summary>
public enum ImageFormat
{
    /// <summary>PNG-encoded bytes. Supported natively by Kitty and iTerm2.</summary>
    Png = 0,

    /// <summary>JPEG-encoded bytes. Supported by iTerm2; Kitty accepts only via re-encoding upstream.</summary>
    Jpeg = 1,

    /// <summary>GIF-encoded bytes. Supported by iTerm2.</summary>
    Gif = 2,
}
