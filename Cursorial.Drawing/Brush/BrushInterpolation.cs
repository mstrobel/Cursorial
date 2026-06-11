// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>Texture filtering an image-sampling brush uses when a cell falls between source texels.</summary>
public enum BrushInterpolation
{
    /// <summary>Pick the nearest texel — crisp, blocky; best for pixel art and sharp patterns.</summary>
    NearestNeighbor,

    /// <summary>Blend the four surrounding texels (in premultiplied sRGB) — smooth; best for photos.</summary>
    Bilinear,
}
