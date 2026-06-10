namespace Cursorial.Drawing;

/// <summary>How a <see cref="TileBrush"/> fills cells beyond its first tile.</summary>
public enum TileMode
{
    /// <summary>Draw the image once; cells outside the first tile sample fully transparent.</summary>
    None,

    /// <summary>Repeat the image; the tile coordinate wraps.</summary>
    Tile,

    /// <summary>Repeat, mirroring every other tile horizontally (seamless on X).</summary>
    FlipX,

    /// <summary>Repeat, mirroring every other tile vertically (seamless on Y).</summary>
    FlipY,

    /// <summary>Repeat, mirroring every other tile on both axes.</summary>
    FlipXY,
}
