using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Imaging;

namespace Cursorial.Tests.Drawing;

// ImageBrush / TileBrush sample a decoded RGBA image per cell. Most assertions hit ColorAt directly (the
// brush's contract), since DrawHarness.Render composites over an opaque base and would consume the alpha
// and turn letterbox-transparent into the base color before read-back.
public class ImageBrushTests
{
    // 2×2: TL red, TR green, BL blue, BR white (all opaque).
    private static DecodedImage Img2x2() => new(2, 2,
    [
        255, 0, 0, 255,   0, 255, 0, 255,
        0, 0, 255, 255,   255, 255, 255, 255,
    ]);

    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Green = Color.FromRgb(0, 255, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);
    private static readonly Color White = Color.FromRgb(255, 255, 255);

    [Fact]
    public void Fill_NearestNeighbor_MapsCellsToTexels()
    {
        var brush = new ImageBrush(Img2x2(), Stretch.Fill, BrushInterpolation.NearestNeighbor);
        var bounds = new Rect(0, 0, 2, 2);
        Assert.Equal(Red, brush.ColorAt(0, 0, bounds));
        Assert.Equal(Green, brush.ColorAt(1, 0, bounds));
        Assert.Equal(Blue, brush.ColorAt(0, 1, bounds));
        Assert.Equal(White, brush.ColorAt(1, 1, bounds));
    }

    [Fact]
    public void Fill_Bilinear_BlendsAdjacentTexels()
    {
        // A 2×1 red|blue image filling a 1-wide box: the single cell center sits between the two texels.
        var brush = new ImageBrush(new DecodedImage(2, 1, [255, 0, 0, 255, 0, 0, 255, 255]),
                                   Stretch.Fill, BrushInterpolation.Bilinear);
        // red→blue at 0.5 in premultiplied sRGB is (128, 0, 128) — same as the gradient midpoint.
        Assert.Equal(Color.FromRgb(128, 0, 128), brush.ColorAt(0, 0, new Rect(0, 0, 1, 1)));
    }

    [Fact]
    public void None_OnePixelPerCell_ClipsPastImageEdge()
    {
        var brush = new ImageBrush(Img2x2(), Stretch.None, BrushInterpolation.NearestNeighbor);
        var bounds = new Rect(0, 0, 4, 4);
        Assert.Equal(Red, brush.ColorAt(0, 0, bounds));      // texel (0,0)
        Assert.Equal(White, brush.ColorAt(1, 1, bounds));    // texel (1,1)
        Assert.Equal(Colors.Transparent, brush.ColorAt(2, 0, bounds));   // past image width → clipped
        Assert.Equal(Colors.Transparent, brush.ColorAt(0, 2, bounds));   // past image height → clipped
    }

    [Fact]
    public void Uniform_Letterboxes_OutsideTransparent()
    {
        // Square image into a 4×2 box: pillarboxed to the centre 2 columns; the outer columns sample nothing.
        var brush = new ImageBrush(Img2x2(), Stretch.Uniform, BrushInterpolation.NearestNeighbor);
        var bounds = new Rect(0, 0, 4, 2);
        Assert.Equal(Colors.Transparent, brush.ColorAt(0, 0, bounds));   // left pillarbox
        Assert.Equal(Colors.Transparent, brush.ColorAt(3, 0, bounds));   // right pillarbox
        Assert.NotEqual(Colors.Transparent, brush.ColorAt(1, 0, bounds));
        Assert.NotEqual(Colors.Transparent, brush.ColorAt(2, 0, bounds));
    }

    [Fact]
    public void Opacity_ScalesSampledAlpha()
    {
        var brush = new ImageBrush(new DecodedImage(1, 1, [255, 0, 0, 255]), Stretch.Fill, opacity: 0.5);
        var c = brush.ColorAt(0, 0, new Rect(0, 0, 1, 1));
        Assert.Equal(ColorKind.Rgb, c.Kind);
        Assert.Equal((byte) 128, c.Alpha);   // 255 × 0.5 → 128
    }

    [Fact]
    public void EmptyBounds_SampleTransparent()
    {
        var brush = new ImageBrush(Img2x2());
        Assert.Equal(Colors.Transparent, brush.ColorAt(0, 0, Rect.Empty));
    }

    [Fact]
    public void InvalidImage_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ImageBrush(default));                       // Rgba null
        Assert.Throws<ArgumentException>(() => new ImageBrush(new DecodedImage(2, 2, [0, 0]))); // wrong length
    }

    [Fact]
    public void FillRectangle_ImageBrush_ColorsBackgrounds()
    {
        // Integration through the cell path: an opaque texel composited over the black base reads back as itself.
        var brush = new ImageBrush(Img2x2(), Stretch.Fill, BrushInterpolation.NearestNeighbor);
        var b = DrawHarness.Render(2, 2, ctx => ctx.FillRectangle(new Rect(0, 0, 2, 2), brush));
        Assert.Equal(Red, b[0, 0].Style.Background);
        Assert.Equal(White, b[1, 1].Style.Background);
    }

    [Fact]
    public void DrawText_ImageBrush_ColorsGlyphs_IncludingWide()
    {
        // A wide (CJK) cluster: the WideLeft cell samples the brush; the continuation follows it.
        var brush = new ImageBrush(new DecodedImage(1, 1, [10, 200, 30, 255]), Stretch.Fill,
                                   BrushInterpolation.NearestNeighbor);
        var b = DrawHarness.Render(4, 1, ctx => ctx.DrawText(0, 0, "中a", brush));
        Assert.Equal("中", b[0, 0].Grapheme);
        Assert.Equal(CellKind.WideLeft, b[0, 0].Kind);
        Assert.Equal(CellKind.WideContinuation, b[1, 0].Kind);
        Assert.Equal(Color.FromRgb(10, 200, 30), b[0, 0].Style.Foreground);
        Assert.Equal("a", b[2, 0].Grapheme);
    }
}

public class TileBrushTests
{
    private static DecodedImage Img2x2() => new(2, 2,
    [
        255, 0, 0, 255,   0, 255, 0, 255,
        0, 0, 255, 255,   255, 255, 255, 255,
    ]);

    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Green = Color.FromRgb(0, 255, 0);

    [Fact]
    public void Tile_RepeatsAcrossBounds()
    {
        var brush = new TileBrush(Img2x2(), new Size(2, 2), TileMode.Tile, BrushInterpolation.NearestNeighbor);
        var bounds = new Rect(0, 0, 4, 4);
        // Cell (2,0) is the second tile's (0,0) → same texel as (0,0).
        Assert.Equal(brush.ColorAt(0, 0, bounds), brush.ColorAt(2, 0, bounds));
        Assert.Equal(Red, brush.ColorAt(2, 0, bounds));
    }

    [Fact]
    public void None_SecondTile_Transparent()
    {
        var brush = new TileBrush(Img2x2(), new Size(2, 2), TileMode.None, BrushInterpolation.NearestNeighbor);
        var bounds = new Rect(0, 0, 4, 4);
        Assert.Equal(Red, brush.ColorAt(0, 0, bounds));                  // first tile
        Assert.Equal(Colors.Transparent, brush.ColorAt(2, 0, bounds));  // second tile → nothing
    }

    [Fact]
    public void FlipX_MirrorsAlternateTiles()
    {
        var tile = new TileBrush(Img2x2(), new Size(2, 2), TileMode.Tile, BrushInterpolation.NearestNeighbor);
        var flip = new TileBrush(Img2x2(), new Size(2, 2), TileMode.FlipX, BrushInterpolation.NearestNeighbor);
        var bounds = new Rect(0, 0, 4, 4);
        // Plain tile repeats column 0 → red at (2,0); the flipped tile mirrors → samples the right edge → green.
        Assert.Equal(Red, tile.ColorAt(2, 0, bounds));
        Assert.Equal(Green, flip.ColorAt(2, 0, bounds));
    }

    [Fact]
    public void InvalidTileSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TileBrush(Img2x2(), new Size(0, 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TileBrush(Img2x2(), new Size(2, 0)));
    }
}
