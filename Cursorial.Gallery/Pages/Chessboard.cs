using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Gallery.Pages;

/// <summary>
/// A 16×16 chessboard that drives <b>content-assisted scrolling</b> — it implements the public
/// <see cref="IScrollContentHost"/> so a hosting <see cref="ScrollViewer"/> sources its line/page step from the
/// board (snapping to whole tiles on both axes) instead of scrolling a fixed cell. The board is small enough to
/// realize fully (no virtualization); the point is the <see cref="LineStep"/>/<see cref="PageStep"/> snapping.
/// </summary>
internal sealed class Chessboard : Panel, IScrollContentHost
{
    private const int Tiles = 16;
    private const int TileWidth = 8;
    private const int TileHeight = 4;

    private Size _viewport;

    public Chessboard()
    {
        // NOT focusable: the hosting ScrollViewer is the focusable scroll surface (a focusable board would make
        // focusing it EnsureVisible its whole extent and jump the offset). Arrow/page keys reach the SV directly.
        for (var r = 0; r < Tiles; r++)
        for (var c = 0; c < Tiles; c++)
        {
            var light = ((r + c) & 1) == 0;
            Children.Add(new Border
            {
                Background = new SolidColorBrush(light ? Color.FromRgb(206, 198, 170) : Color.FromRgb(92, 84, 64)),
                Child = new TextBlock
                {
                    Text = $"{(char) ('a' + c)}{r + 1}",
                    Foreground = new SolidColorBrush(light ? Color.FromRgb(60, 56, 44) : Color.FromRgb(214, 208, 190)),
                },
            });
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children;
        for (var i = 0; i < children.Count; i++)
            children[i].Measure(new Size(TileWidth, TileHeight));
        return new Size(Tiles * TileWidth, Tiles * TileHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children;
        for (var i = 0; i < children.Count; i++)
            children[i].Arrange(new Rect(i % Tiles * TileWidth, i / Tiles * TileHeight, TileWidth, TileHeight));
        return finalSize;
    }

    // ───────────────────────────── IScrollContentHost (whole-tile snapping) ─────────────────────────────

    public bool IsScrollClient => true;
    public bool IsLogicalScroll => true;
    public ScrollContentPresenter? ScrollOwner { get; set; }
    public bool CanScrollHorizontally { get; set; }
    public bool CanScrollVertically { get; set; } = true;

    public Size GetExtent() => new(Tiles * TileWidth, Tiles * TileHeight);

    public void SetViewport(Size viewport) => _viewport = viewport;

    public void InvalidateRealization() { } // the whole board is realized — nothing to re-realize

    // LEADING-EDGE snap (the chosen model): a line scroll snaps the edge you scroll TOWARD onto a tile boundary, so
    // the tile being revealed lands fully in view — scrolling right/down aligns the FAR edge (offset+viewport) to the
    // next tile boundary beyond it; scrolling left/up aligns the NEAR edge (offset) to the previous boundary. The
    // trailing edge may then show a partial tile (unavoidable when the viewport isn't a whole number of tiles). The
    // ScrollViewer applies the sign; this returns the unsigned cell magnitude.
    public int LineStep(int currentOffset, int sign, bool vertical)
    {
        var tileSize = vertical ? TileHeight : TileWidth;
        var viewport = Math.Max(1, vertical ? _viewport.Rows : _viewport.Columns);
        if (sign >= 0)
        {
            // Snap the far edge (offset+viewport) to the next tile boundary STRICTLY beyond it (so an aligned edge
            // still advances a whole tile), then back out the offset that puts it there.
            var farBoundary = (currentOffset + viewport) / tileSize * tileSize + tileSize;
            return Math.Max(1, farBoundary - viewport - currentOffset);
        }

        // Snap the near edge (offset) to the previous tile boundary strictly below it.
        var nearBoundary = (Math.Max(0, currentOffset) - 1) / tileSize * tileSize;
        return Math.Max(1, currentOffset - Math.Max(0, nearBoundary));
    }

    // A page = the whole tiles that fit the viewport (PgUp/PgDn jump by that many tiles). Coarser than the line snap;
    // the user's report was about line scroll, so this stays a simple whole-tile page.
    public int PageStep(int currentOffset, int sign, bool vertical)
    {
        var tileSize = vertical ? TileHeight : TileWidth;
        var viewport = Math.Max(1, vertical ? _viewport.Rows : _viewport.Columns);
        var tilesPerPage = Math.Max(1, viewport / tileSize);
        return tilesPerPage * tileSize;
    }
}
