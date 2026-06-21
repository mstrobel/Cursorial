using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Gallery.Pages;

/// <summary>
/// The content-assisted (whole-tile-snapping) scrolling page — a <see cref="Chessboard"/> in a
/// <see cref="ScrollViewer"/>. The board implements the public <see cref="IScrollContentHost"/>, so arrow/page
/// scrolling snaps to whole tiles on both axes rather than scrolling a fixed cell.
/// </summary>
internal sealed class ChessboardPage : IGalleryPage
{
    public string Title => "Chessboard (snap)";

    public UIElement Build()
    {
        var board = new Chessboard();
        var sv = new ScrollViewer
        {
            Content = board,
            // Hidden (not Auto) horizontally: v1 bands only the vertical axis, so horizontal Auto degrades to
            // Disabled; Hidden keeps the axis scrollable (by keys/wheel) without a bar — the snap is the point.
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = true, // the SV is the focusable scroll surface — Tab into it, then arrows snap by tiles
        };

        var info = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(180, 188, 200)),
            Text = " 16×16 board of 8×4 tiles — Tab into it, then ↑/↓/←/→ scroll snapping to WHOLE TILES " +
                   "(content-assisted via IScrollContentHost.LineStep); PgUp/PgDn page by tiles.",
        };
        var infoBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 34, 42)),
            Padding = new Margins(1, 0),
            Child = info,
        };
        DockPanel.SetDock(infoBar, Dock.Top);

        var root = new DockPanel();
        root.Children.Add(infoBar);
        root.Children.Add(sv); // last child fills
        return root;
    }
}
