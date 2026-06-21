using System.Text;

using Cursorial.Gallery;
using Cursorial.Gallery.Pages;
using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

using Xunit.Abstractions;

namespace Cursorial.Tests.UI.Gallery;

// The standalone gallery (#107) is a real TTY app, so it can't be run in CI — this headless canary builds its shell +
// pages through UITestHost and asserts they render without error (the manual harness still gets exercised on every run).
public sealed class GallerySmokeTests(ITestOutputHelper output)
{
    private static string Screen(UITestHost host, int rows)
    {
        var sb = new StringBuilder();
        for (var r = 0; r < rows; r++)
            sb.AppendLine(host.GetRowText(r));
        return sb.ToString();
    }

    [Fact]
    public void Shell_WithScrollViewerPage_RendersWithoutError()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(80, 24) });
        var shell = new GalleryShell([new ScrollViewerPage()]);

        var ex = Record.Exception(() =>
        {
            host.ShowRoot(shell.Build());
            host.RunUntilIdle();
        });
        Assert.Null(ex);

        var screen = Screen(host, 24);
        output.WriteLine(screen);
        Assert.Contains("Gallery", screen);      // the title bar
        Assert.Contains("ScrollViewer", screen); // the nav entry
        Assert.Contains("V-bar", screen);        // the ScrollViewer page's toggle bar
        Assert.Contains("row 0", screen);        // the scrollable content
    }

    [Fact] // The chessboard page (#107): content-assisted scrolling snaps the offset to whole tiles via IScrollContentHost.
    public void ChessboardPage_SnapsScrollToWholeTiles()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(80, 24) });
        var board = new Chessboard();
        var sv = new ScrollViewer
        {
            Content = board,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, // v1 horizontal Auto degrades; Hidden scrolls without a bar
            Focusable = true,
        };
        host.ShowRoot(sv);
        host.RunUntilIdle();
        sv.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal(4, sv.VerticalOffset);   // snapped a whole 4-row tile, not 1 cell

        host.SendKey(Key.RightArrow);
        host.RunUntilIdle();
        Assert.Equal(8, sv.HorizontalOffset); // snapped a whole 8-column tile
    }
}
