namespace Cursorial.Rendering.Media;

/// <summary>How an <see cref="ImageBrush"/> fits its source image into the paint bounds.</summary>
public enum Stretch
{
    /// <summary>Stretch the image to exactly fill the bounds, ignoring aspect (the default).</summary>
    Fill,

    /// <summary>No scaling — one source pixel per cell from the top-left; the image is clipped (or padded
    /// with transparency) to the bounds.</summary>
    None,

    /// <summary>Scale to fit <em>within</em> the bounds preserving aspect; the unfilled remainder
    /// (letterbox / pillar-box) samples transparent.</summary>
    Uniform,

    /// <summary>Scale to <em>fill</em> the bounds preserving aspect; the overflow is cropped.</summary>
    UniformToFill,
}
